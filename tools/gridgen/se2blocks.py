"""se2blocks - the ONE place that knows Space Engineers 2 block conventions.

Everything else in gridgen works in a continuous "cell" frame (0.25 m units, the grid's frame: +X right,
+Y up, +Z backward/aft, as in VRage) with blocks described by half-open boxes [lo, hi).  This module converts
between that and what the spawner (Orbital Mod GridGen.cs) reads:

  * the catalog  (%TEMP%\\OrbitalMod\\gen\\catalog.txt: GUID mass_kg minX minY minZ maxX maxY maxZ, an INCLUSIVE
    cell range for the block at cell 0 0 0, identity orientation) and blocks.txt (GUID Name);
  * the position convention: a block's spec position P versus the cells it occupies;
  * orientations: the (Forward, Up) pair of a B/F line versus a 3x3 integer rotation;
  * hinge geometry: rotation axis, where the head grid's origin lands, the side the flap mounts on;
  * thruster exhaust direction, slanted-armor face normals, seat facing.

Every convention is in CAL below and marked MEASURED (in game) or ASSUMED (still to be checked).  A file
%TEMP%\\OrbitalMod\\gen\\calibration.txt can override any CAL entry without touching code - see load_calibration.
"""
from __future__ import annotations

import functools
import itertools
import os
import re
import tempfile
from dataclasses import dataclass

import numpy as np

CELL_M = 0.25                                   # one spec cell in metres (GridBuilder.AddBlock: position = 0.25 * cell)
HERE = os.path.dirname(os.path.abspath(__file__))
GEN_DIR = os.path.join(tempfile.gettempdir(), "OrbitalMod", "gen")

# ---------------------------------------------------------------------------------------------------------------
# Calibration.  Keys documented inline; MEASURED = from the in-game catalog/probe, ASSUMED = not yet verified.
# ---------------------------------------------------------------------------------------------------------------
CAL = {
    # MEASURED (catalog): the AABB is an inclusive cell range; with cell i covering the continuous span (i-1, i]
    # every measured full block (cubes, batteries, thrusters, hinge, seats) comes out centred on its position:
    # a large cube at P spans P-5..P+5, a small one P-1..P+1.  continuous box = [min - offset, max].
    "aabb_low_offset": 1,
    # ASSUMED (coordinator, to verify): an identity thruster's exhaust flows along local +Z (Backward), so a thruster
    # with orientation 'Forward Up' pushes the ship toward -Z (Forward).
    "thruster_exhaust_local": (0, 0, 1),
    # MEASURED: the hinge rotates about its block's local Forward axis (local -Z).
    "hinge_axis_local": (0, 0, -1),
    # MEASURED: the head grid's origin is the hinge block's centre (its position), axes equal to the hinge's at 0 deg.
    "hinge_headgrid_origin_local": (0, 0, 0),
    "hinge_headgrid_fwd_up": ("Forward", "Up"),
    # ASSUMED: the rotation axis passes through the hinge block's centre.
    "hinge_pivot_local": (0.0, 0.0, 0.0),
    # MEASURED: flap blocks attach only on the head grid's +Y side (cells (0,10,0), (0,20,0).. for a large hinge;
    # -Y and +X did not attach); a segment's span runs along the head's Z (= the hinge's Forward axis).
    "flap_mount_local": (0, 1, 0),
    # MEASURED: a positive angle is a right-handed rotation about the hinge's local Forward vector (hinge_axis_local).
    "hinge_angle_sign": 1,
    # MEASURED: the vanilla hinge turns 0..180 deg only (0 is an end stop), so a control surface's neutral (flap
    # trailing aft) is at 90 deg: the hinge's Up is chosen so that +90 about Forward turns head +Y to aft
    # (Up = aft x Forward), and limits are written 90 -+ the deflection.
    "hinge_neutral_deg": 90.0,
    "hinge_range_deg": (0.0, 180.0),
    # ASSUMED (unmeasured): outward normal of the slanted face for identity orientation, in local axes.
    #   slope: the cube cut by a plane through two opposite edges; corner: the 1/6 tetrahedron; invcorner: cube minus it.
    # MEASURED (data/mounts_measured.txt, the game's own mount points per face):
    #   slope       full Forward + Down, triangles Left/Right        -> slanted face toward Up+Backward  (0, 1, 1)
    #   corner      triangles Forward/Left/Down                      -> solid at (-,-,-), face toward    (1, 1, 1)
    #   invcorner   full Forward/Right/Down, triangles Back/Left/Up  -> cut corner at Left+Up+Back     (-1, 1, 1)
    #   halfslope   (ArmorHalfLongSlope) full Down, half Backward    -> half wedge, thick at Backward   (0, 2, -1)
    "shape_normal": {"slope": (0, 1, 1), "corner": (1, 1, 1), "invcorner": (-1, 1, 1), "halfslope": (0, 2, -1)},
    # ASSUMED: seats and cockpits face their local Forward with local Up up.
    "seat_fwd_up": ("Forward", "Up"),
    # MEASURED: extra mass per grid (none measured).
    "grid_overhead_kg": 0.0,
    # Engine clearances (generator policy, not game data): free length behind the exhaust face and in front of an
    # atmospheric intake, as multiples of the thruster's own length along its axis.
    "exhaust_clear_lengths": 2.0,
    "intake_clear_lengths": 1.0,
}

STATUS = {   # for the report / sidecar
    "aabb_low_offset": "measured", "thruster_exhaust_local": "ASSUMED", "hinge_axis_local": "measured",
    "hinge_headgrid_origin_local": "measured", "hinge_headgrid_fwd_up": "measured", "hinge_pivot_local": "ASSUMED",
    "flap_mount_local": "measured", "hinge_angle_sign": "measured", "hinge_neutral_deg": "measured",
    "shape_normal": "measured (from the mount table)",
    "seat_fwd_up": "ASSUMED", "grid_overhead_kg": "measured",
}

# ---------------------------------------------------------------------------------------------------------------
# Directions and orientations (VRage Base6Directions; grid frame: Forward = -Z, Up = +Y, Right = +X)
# ---------------------------------------------------------------------------------------------------------------
DIRS = {
    "Forward": (0, 0, -1), "Backward": (0, 0, 1), "Left": (-1, 0, 0),
    "Right": (1, 0, 0), "Up": (0, 1, 0), "Down": (0, -1, 0),
}


def dvec(name: str) -> np.ndarray:
    return np.array(DIRS[name], dtype=int)


def dname(v) -> str:
    v = tuple(int(round(c)) for c in v)
    for k, d in DIRS.items():
        if d == v:
            return k
    raise ValueError(f"not an axis direction: {v}")


def orient_matrix(fwd: str, up: str) -> np.ndarray:
    """Columns: where the block's local +X, +Y, +Z go in the grid frame (local Forward is -Z)."""
    return _orient_matrix(fwd, up).copy()


@functools.lru_cache(maxsize=None)
def _orient_matrix(fwd: str, up: str) -> np.ndarray:
    F, U = dvec(fwd), dvec(up)
    if int(F @ U) != 0:
        raise ValueError(f"forward {fwd} and up {up} not perpendicular")
    R = np.cross(U, -F)
    return np.column_stack([R, U, -F]).astype(int)


def orient_names(M: np.ndarray) -> tuple[str, str]:
    return dname(-M[:, 2]), dname(M[:, 1])


ALL_ORIENTS = [(f, u) for f, u in itertools.product(DIRS, DIRS) if int(dvec(f) @ dvec(u)) == 0]
IDENTITY = ("Forward", "Up")


# ---------------------------------------------------------------------------------------------------------------
# Catalog
# ---------------------------------------------------------------------------------------------------------------
@dataclass
class BlockDef:
    name: str
    guid: str
    mass: float
    lo: np.ndarray       # continuous extent relative to the position, identity orientation (cells)
    hi: np.ndarray
    measured: bool

    @property
    def size(self):
        return self.hi - self.lo


def _size_from_name(name: str) -> int:
    m = re.search(r"(\d+)", name)
    n = int(m.group(1)) if m else 250
    return max(2, int(round(n / 25)))


def _default_def(name: str, guid: str) -> BlockDef:
    """Placeholder for a prefab missing from the catalog: a centred cube sized from the name, volume-scaled mass."""
    s = _size_from_name(name)
    lo = np.array([-s // 2] * 3)
    hi = lo + s
    return BlockDef(name, guid, 1048.6 * (s / 10.0) ** 3, lo, hi, False)


def _find(fname: str, gen_dir: str | None) -> str | None:
    for d in ([gen_dir] if gen_dir else []) + [GEN_DIR, os.path.join(HERE, "data")]:
        p = os.path.join(d, fname)
        if os.path.exists(p):
            return p
    return None


def load_calibration(path: str | None):
    """Optional overrides, one per line:  key value...   e.g.
         thruster_exhaust_local 0 0 -1
         flap_mount_local 0 -1 0
         hinge_pivot_local 0 0 0
         shape_normal slope 0 1 -1
         hinge_angle_sign -1
    """
    if not path or not os.path.exists(path):
        return
    for raw in open(path, encoding="utf-8"):
        line = raw.split("#")[0].split()
        if not line:
            continue
        k, v = line[0], line[1:]
        if k == "shape_normal":
            CAL["shape_normal"][v[0]] = tuple(int(x) for x in v[1:4])
        elif k == "mount_faces":
            MOUNT_OVERRIDE[v[0]] = tuple(v[1:])
        elif k == "hinge_headgrid_fwd_up" or k == "seat_fwd_up":
            CAL[k] = (v[0], v[1])
        elif k in CAL and isinstance(CAL[k], tuple):
            CAL[k] = tuple(float(x) if "." in x else int(x) for x in v)
        elif k in CAL:
            CAL[k] = float(v[0])
        STATUS[k] = "calibration.txt"


class Catalog:
    def __init__(self, gen_dir: str | None = None, snapshot: bool = False):
        """snapshot=True reads only the copy in tools/gridgen/data (deterministic tests)."""
        gd = None if snapshot else gen_dir
        find = (lambda f: os.path.join(HERE, "data", f)) if snapshot else (lambda f: _find(f, gd))
        self.blocks_path, self.catalog_path = find("blocks.txt"), find("catalog.txt")
        self.by_name: dict[str, BlockDef] = {}
        guids = {}
        for raw in open(self.blocks_path, encoding="utf-8"):
            t = raw.split("#")[0].split()
            if len(t) >= 2:
                guids[t[0].lower()] = t[1]
                self.by_name[t[1]] = _default_def(t[1], t[0])
        off = int(CAL["aabb_low_offset"])
        if self.catalog_path and os.path.exists(self.catalog_path):
            for raw in open(self.catalog_path, encoding="utf-8"):
                t = raw.split("#")[0].split()
                if len(t) < 8 or t[0].lower() not in guids or "?" in t:
                    continue
                name = guids[t[0].lower()]
                mn = np.array([int(x) for x in t[2:5]])
                mx = np.array([int(x) for x in t[5:8]])
                self.by_name[name] = BlockDef(name, t[0], float(t[1]), mn - off, mx, True)
        if not snapshot:
            load_calibration(_find("calibration.txt", gd) if gd or os.path.exists(GEN_DIR) else None)
        _CATALOGS.insert(0, self)
        load_mounts(os.path.join(HERE, "data", "mounts_measured.txt") if snapshot else None)

    def __getitem__(self, name: str) -> BlockDef:
        return self.by_name[name]

    def __contains__(self, name):
        return name in self.by_name

    def name_of_guid(self, guid: str) -> str:
        g = guid.lower()
        for n, b in self.by_name.items():
            if b.guid.lower() == g:
                return n
        raise KeyError(guid)

    # --- position convention -----------------------------------------------------------------------------------
    def box(self, name, pos, orient=IDENTITY):
        """Continuous half-open box [lo, hi) occupied by block `name` at spec position `pos` with orientation."""
        b = self[name]
        M = orient_matrix(*orient)
        c = np.array([M @ np.array(p) for p in itertools.product(*zip(b.lo, b.hi))])
        return np.asarray(pos) + c.min(0), np.asarray(pos) + c.max(0)

    def pos_for(self, name, lo, orient=IDENTITY):
        """The spec position putting the block's box low corner at `lo`."""
        b = self[name]
        M = orient_matrix(*orient)
        c = np.array([M @ np.array(p) for p in itertools.product(*zip(b.lo, b.hi))])
        return np.asarray(lo) - c.min(0)

    def extent(self, name, orient=IDENTITY):
        lo, hi = self.box(name, (0, 0, 0), orient)
        return hi - lo

    def centre(self, name, pos, orient=IDENTITY):
        lo, hi = self.box(name, pos, orient)
        return (lo + hi) / 2.0


# ---------------------------------------------------------------------------------------------------------------
# Prefab choices per grid size
# ---------------------------------------------------------------------------------------------------------------
GRID_UNIT = {"large": 10, "small": 2}      # cells per block
SUFFIX = {"large": "250", "small": "50"}
ARMOR = {"cube": "ArmorCubeLight", "slope": "ArmorSlopeLight", "corner": "ArmorCornerLight",
         "invcorner": "ArmorInvCornerLight", "half": "ArmorHalfLight"}
# Shapes as the cube [-0.5, 0.5]^3 cut by the half-space n.p <= c (n = CAL shape_normal, local axes); for drawing,
# volume (mass check) and the shell-closure face rules.
SHAPE_CUT = {"cube": None, "slope": 0.0, "corner": -0.5, "invcorner": 0.5, "halfslope": -0.5}
THRUSTER = {"atmospheric": "AtmosphericThruster", "hydrogen": "HydrogenThruster", "ion": "IonThruster"}
DEFAULT_THRUSTER_SIZE = {"large": {"atmospheric": 500, "hydrogen": 250, "ion": 500},
                         "small": {"atmospheric": 250, "hydrogen": 200, "ion": 150}}
DEFAULT_COCKPIT = {"large": "ControlSeat250", "small": "Cockpit150"}
DEFAULT_BATTERIES = {"large": ["Battery250", "Battery150"], "small": ["Battery250", "Battery150", "Battery50"]}


def armor(shape: str, grid: str) -> str:
    if shape == "halfslope":
        return "ArmorHalfLongSlope" + SUFFIX[grid] + "Light"
    return ARMOR[shape] + SUFFIX[grid]


def shape_of(name: str) -> str:
    for shape in ("halfslope", "invcorner", "corner", "slope", "half", "cube"):
        if armor(shape, "large") == name or armor(shape, "small") == name:
            return shape
    return "block"


def hinge_names(grid: str) -> tuple[str, str]:
    return "Hinge" + SUFFIX[grid], "HingeHead" + SUFFIX[grid]


def thruster_name(kind: str, size, grid: str) -> str:
    size = size or DEFAULT_THRUSTER_SIZE[grid][kind]
    return THRUSTER[kind] + str(size)


def is_thruster(name: str) -> bool:
    return "Thruster" in name


def thruster_type(name: str) -> str:
    for k, v in THRUSTER.items():
        if name.startswith(v):
            return k
    return "?"


# ---------------------------------------------------------------------------------------------------------------
# Orientation solvers for the parts with a direction
# ---------------------------------------------------------------------------------------------------------------
def orient_for_thruster(exhaust_dir, prefer_up=(0, 1, 0)):
    """Orientation whose exhaust (CAL thruster_exhaust_local) points along `exhaust_dir` (a grid axis)."""
    want = np.array(exhaust_dir)
    loc = np.array(CAL["thruster_exhaust_local"])
    best = None
    for o in ALL_ORIENTS:
        M = orient_matrix(*o)
        if np.array_equal(M @ loc, want):
            score = int(M[:, 1] @ np.array(prefer_up))
            if best is None or score > best[0]:
                best = (score, o)
    return best[1]


def thruster_exhaust(name, orient):
    return orient_matrix(*orient) @ np.array(CAL["thruster_exhaust_local"])


def rot(axis, deg):
    """Rotation matrix by deg about axis (right-handed); exact integers for multiples of 90."""
    a = np.asarray(axis, float)
    a = a / np.linalg.norm(a)
    K = np.array([[0, -a[2], a[1]], [a[2], 0, -a[0]], [-a[1], a[0], 0]])
    t = np.radians(deg)
    R = np.eye(3) + np.sin(t) * K + (1 - np.cos(t)) * K @ K
    return np.round(R).astype(int) if abs(deg % 90) < 1e-9 else R


def hinge_axis_vec(hinge_orient):
    """Grid vector of positive rotation (the hinge's Forward times the angle sign)."""
    return orient_matrix(*hinge_orient) @ np.array(CAL["hinge_axis_local"]) * CAL["hinge_angle_sign"]


def orient_for_hinge(span_axis_vec, chord_dir=(0, 0, 1)):
    """Hinge orientation: rotation axis along +span_axis_vec (or its reverse) and, at the neutral angle (90), the
    flap mount side (head +Y) toward chord_dir.  I.e. Forward = span, Up = aft x Forward."""
    ax = np.array(CAL["hinge_axis_local"])
    mount = np.array(CAL["flap_mount_local"])
    s = np.array(span_axis_vec)
    out = None
    for o in ALL_ORIENTS:
        M = orient_matrix(*o)
        a = M @ ax
        if not (np.array_equal(a, s) or np.array_equal(a, -s)):
            continue
        if not np.array_equal(rot(hinge_axis_vec(o), CAL["hinge_neutral_deg"]) @ (M @ mount), np.array(chord_dir)):
            continue
        if np.array_equal(a, s):
            return o
        out = out or o
    return out


def headgrid_frame(hinge_pos, hinge_orient, angle=None):
    """(origin, rotation) of the head grid in the main grid's frame with the hinge at `angle` degrees (default the
    neutral, 90: the frame in which the generator lays the flap out trailing aft)."""
    angle = CAL["hinge_neutral_deg"] if angle is None else angle
    M = orient_matrix(*hinge_orient)
    R = rot(hinge_axis_vec(hinge_orient), angle)
    _, pivot = hinge_axis_and_pivot(hinge_pos, hinge_orient)
    origin0 = np.asarray(hinge_pos) + M @ np.array(CAL["hinge_headgrid_origin_local"])
    origin = pivot + R @ (origin0 - pivot)
    if np.allclose(origin, np.round(origin)):
        origin = np.round(origin).astype(int)
    Rh = R @ M @ orient_matrix(*CAL["hinge_headgrid_fwd_up"])
    return origin, Rh


def hinge_axis_and_pivot(hinge_pos, hinge_orient):
    M = orient_matrix(*hinge_orient)
    return M @ np.array(CAL["hinge_axis_local"]), np.asarray(hinge_pos) + M @ np.array(CAL["hinge_pivot_local"])


def to_headgrid(pos_main, orient_main, origin, Rh):
    """A main-frame block (position, orientation) expressed in the head grid's frame."""
    p = Rh.T @ (np.asarray(pos_main) - origin)
    M = Rh.T @ orient_matrix(*orient_main)
    return p, orient_names(M)


def from_headgrid(pos_hg, orient_hg, origin, Rh):
    p = origin + Rh @ np.asarray(pos_hg)
    return p, orient_names(Rh @ orient_matrix(*orient_hg))


def orient_for_normal(shape: str, normal) -> tuple[str, str] | None:
    """Orientation that turns the shape's slanted-face normal (CAL) into `normal` (integer vector)."""
    return _orient_for_normal(shape, tuple(int(x) for x in normal), tuple(CAL["shape_normal"][shape]))


@functools.lru_cache(maxsize=None)
def _orient_for_normal(shape, normal, n0):
    n0 = np.array(n0)
    want = np.array(normal)
    for o in ALL_ORIENTS:
        if np.array_equal(orient_matrix(*o) @ n0, want):
            return o
    return None


def full_faces(shape: str, orient) -> list[tuple[int, int, int]]:
    """Grid-frame face directions that are completely closed for an armor shape (for the shell-closure check)."""
    faces = [tuple(int(c) for c in v) for v in DIRS.values()]
    if shape == "cube":
        return faces
    n = orient_matrix(*orient) @ np.array(CAL["shape_normal"][shape])
    if shape in ("corner", "halfslope"):
        return [] if shape == "corner" else [f for f in faces if int(np.array(f) @ n) < 0 and abs(int(np.array(f) @ n)) == 2]
    return [f for f in faces if int(np.array(f) @ n) < 0]   # slope / invcorner: the faces facing away from the cut


# ---------------------------------------------------------------------------------------------------------------
# Mount faces.  SE2 generates mount points from each block's collider; two blocks connect across a face only if
# BOTH have a mount group there (CubeBlockComponent.CanConnectBlock, used by BlockOctreeComponent when it splits
# disconnected blocks at spawn - a block held only through non-mount faces is split off, and that split crashed).
# MEASURED: data/mounts_measured.txt (harness 'gen mounts': ComplexTopologyAggregator.GetMountPointsGroupsPerDirection
# per prefab, local axes at identity; counts in 0.25 m cells, a small block's full face is 4, a large one's 100).
# A face is a mount face if it has ANY mount cells: CanConnectBlock tests each mount group's box, pushed one cell
# out, against the neighbour's whole AABB - so a triangle (3 of 4) or half (2 of 4) group still meets a same-size
# neighbour, as long as that neighbour mounts on its facing side too.  Only faces with no group cannot connect.
# Prefabs missing from the table fall back to the shape-derived rule (full faces of the cut cube) / role guesses.
# Override any prefab with calibration.txt:   mount_faces NAME Forward Backward Left Right Up Down
# ---------------------------------------------------------------------------------------------------------------
MOUNT_ASSUMED = {
    "Battery": "all", "Hinge": "all-but-up", "HingeHead": ("Up",),
    "Cockpit": ("Down", "Backward"), "ControlSeat": ("Down",), "Seat": ("Down",),
}
MOUNT_OVERRIDE = {}
MOUNT_MEASURED = {}            # guid -> {direction name: mount cells}
STATUS["mount_faces"] = "MEASURED table (data/mounts_measured.txt); shape/role fallback only for unlisted prefabs"
_LOCAL = {"Forward": (0, 0, -1), "Backward": (0, 0, 1), "Left": (-1, 0, 0), "Right": (1, 0, 0), "Up": (0, 1, 0),
          "Down": (0, -1, 0)}


def load_mounts(path=None):
    """Reads the measured mount table (guid ... : Face=cells ...)."""
    path = path or _find("mounts_measured.txt", None)
    MOUNT_MEASURED.clear()
    if not path or not os.path.exists(path):
        STATUS["mount_faces"] = "NO measured table found: shape/role fallback (unsafe for spawning)"
        return
    for raw in open(path, encoding="utf-8"):
        if raw.startswith("#") or ":" not in raw:
            continue
        guid = raw.split()[0].lower()
        faces = {}
        for tok in raw.rsplit(":", 1)[1].split():
            if "=" in tok:
                k, v = tok.split("=")
                faces[k] = int(v)
        MOUNT_MEASURED[guid] = faces


def _guid_of(name):
    for cat in _CATALOGS:
        if name in cat.by_name:
            return cat.by_name[name].guid.lower()
    return None


_CATALOGS = []


def mount_local(name):
    """Local-frame face directions (identity orientation) on which `name` has mount points."""
    if name in MOUNT_OVERRIDE:
        return {_LOCAL[d] for d in MOUNT_OVERRIDE[name]}
    g = _guid_of(name)
    if g is not None and g in MOUNT_MEASURED:
        return {_LOCAL[k] for k, v in MOUNT_MEASURED[g].items() if v > 0}
    return mount_fallback(name)


def mount_fallback(name):
    """Shape/role rule for prefabs missing from the measured table (full faces of the cut cube)."""
    shape = shape_of(name)
    all6 = set(_LOCAL.values())
    if shape == "cube":
        return all6
    if shape in ("slope", "invcorner", "halfslope", "corner"):
        n = np.array(CAL["shape_normal"][shape])
        if shape == "corner":
            return set()
        if shape == "halfslope":
            return {(0, -1, 0)}
        return {d for d in all6 if int(np.array(d) @ n) < 0}
    if shape == "half":
        return {(0, 0, -1)}
    if is_thruster(name):
        exh = tuple(int(v) for v in CAL["thruster_exhaust_local"])
        out = all6 - {exh}
        if thruster_type(name) == "atmospheric":
            out -= {tuple(-v for v in exh)}
        return out
    for key in sorted(MOUNT_ASSUMED, key=len, reverse=True):
        if name.startswith(key):
            v = MOUNT_ASSUMED[key]
            if v == "all":
                return all6
            if v == "all-but-up":
                return all6 - {(0, 1, 0)}
            return {_LOCAL[d] for d in v}
    return all6


def mount_dirs(name, orient):
    """Grid-frame face directions with mount points for `name` placed with `orient`."""
    M = orient_matrix(*orient)
    return {tuple(int(x) for x in M @ np.array(d)) for d in mount_local(name)}
