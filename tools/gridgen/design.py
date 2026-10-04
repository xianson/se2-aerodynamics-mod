"""design - the airplane description (JSON) and its geometry as implicit solids.

Design frame (metres): x aft from the nose tip, y right, z up.  Grid frame (cells of 0.25 m): X = 4y, Y = 4z,
Z = 4x (VRage: +Z is Backward, so the nose points to the grid's Forward).

Schema (all lengths in metres, angles in degrees; every key optional unless noted):
{
  "name": "airliner",                 # required; also the spec file name
  "title": "Twin-jet airliner",
  "grid": "small" | "large",          # 0.5 m (default) or 2.5 m blocks
  "scale": 1.0,                       # multiplies every length (a large-grid variant of a small design)
  "skin": "slopes" | "cubes",         # refine the outer hull with slopes/corners, or cubes only
  "solid_below": 3,                   # parts thinner than this many blocks stay solid; thicker ones keep a 1-block shell
  "static_margin": 0.08,              # CoM target: neutral point - margin * MAC (trimmed on the aero probe's
                                      # TestForces view, ~5% MAC more pessimistic than the game's readout)
  "com_tolerance": 0.02,              # |margin error| allowed, fraction of MAC
  "auto_wing": true,                  # may slide the wing group fore/aft when batteries alone cannot reach the target
  "fuselage": {                       # omit / null for a pure flying wing
     "length": 60, "width": 10, "height": 10,
     "nose": 0.18, "tail": 0.30,      # taper lengths, fractions of length
     "nose_shape": 0.5,               # exponent: 0.5 elliptic/ogive, 1 conical
     "nose_radius": 0.0,              # blunt nose: radius fraction kept at the tip
     "tail_shape": 1.0,
     "tail_width": 0.25, "tail_height": 0.35,   # tail-end size as fraction of the max
     "tail_upsweep": 0.3,             # tail-end centreline raised by this fraction of the half-height
     "section": 2.0,                  # superellipse exponent (2 ellipse, 4+ boxy)
     "z": 0.0,                        # centreline height
     "profile": [[xf, wf, hf, zf], ...]   # optional table (fractions of length/width/height) replacing the formula
  },
  "surfaces": [                       # lifting surfaces; tail presets add to this list
     {"id": "wing", "role": "wing" | "htail" | "vtail" | "canard" | "vee",
      "span": 60,                     # tip to tip (mirrored) or root to tip (single / vertical)
      "root_chord": 12, "tip_chord": 4, "sweep": 25 (leading edge), "dihedral": 5 (90 = vertical fin),
      "x": 22 | "x_frac": 0.38,       # root leading-edge station
      "z": "low" | "mid" | "high" | metres,  "y0": 0 (root offset from the centreline),
      "thickness": 0.12 (root t/c), "thickness_tip": 0.09, "min_root_blocks": 2, "mirror": true,
      "fairing": true (wings: a wing-root fillet into the body),
      "moves_with_wing": true,        # (wings default true) slid together by auto_wing
      "controls": {"chord": 0.3, "aileron_from": 0.55, "per_hinge": 3, "mode": "auto" | "elevon" | "none",
                   "limits": {"aileron": [-25, 25], ...}}}
  ],
  "tail": "conventional" | "t" | "v" | "cruciform" | "twin_boom" | "none"
          or {"type": ..., "size": 1.0, "vh": 0.6, "vv": 0.045 (tail volume coefficients), "ar": 4, "fin_ar": 1.4,
              "sweep": 20, "fin_sweep": 35, "x_end": 55, "boom_y": 6, "htail_at": 0..1 (twin boom: up the fins),
              "dihedral": 40 (V tail)},
  "canard": {"span": .., "root_chord": .., ...}           # shorthand for a canard surface
  "booms": [{"y": 6, "z": 0, "x0": 20 | "wing_te", "x1": 50, "radius": 1, "mirror": true}],
  "struts": [{"from": [x, y, z], "to": [x, y, z], "mirror": true, "radius": 0.3}],
  "engines": [ see engines.py ],
  "cockpit": {"x": 3 | "x_frac": 0.05, "prefab": "ControlSeat250"},
  "batteries": {"min": 2, "max": 200, "prefabs": ["Battery250"]},
  "spawn": {"ahead": 150, "up": 20, "static": 1}
}
Mesh designs replace fuselage/surfaces with  "mesh": "meshes/plane.obj", "mesh_scale": 1, "mesh_axes": "xyz"
(how the file's axes map to design x aft, y right, z up; e.g. "-z x y" for a file with nose toward +z... see meshio).
"""
from __future__ import annotations

import copy
import json
import math
import os

import numpy as np

BLOCK_M = {"large": 2.5, "small": 0.5}


def load(path: str) -> dict:
    with open(path, encoding="utf-8") as f:
        d = json.load(f)
    d["_path"] = os.path.abspath(path)
    return d


LEN_KEYS = {"length", "width", "height", "span", "root_chord", "tip_chord", "x", "y", "z", "y0", "z_off", "clear",
            "x0", "x1", "radius", "x_end", "boom_y", "boom_z"}


def _scale(o, k):
    """Every length in the design multiplied by k (fractions, angles and ratios untouched)."""
    if isinstance(o, dict):
        for key, v in o.items():
            if key in ("from", "to") and isinstance(v, list):
                o[key] = [x * k for x in v]
            elif key in LEN_KEYS and isinstance(v, (int, float)) and not isinstance(v, bool):
                o[key] = v * k
            else:
                _scale(v, k)
    elif isinstance(o, list):
        for v in o:
            _scale(v, k)


def defaults(d: dict) -> dict:
    d = copy.deepcopy(d)
    if d.get("scale") and not d.get("_scaled"):
        k = float(d["scale"])
        for key in ("fuselage", "surfaces", "canard", "tail", "booms", "struts", "engines", "cockpit"):
            if key in d and isinstance(d[key], (dict, list)):
                _scale(d[key], k)
        d["_scaled"] = True
    d.setdefault("grid", "small")
    d.setdefault("skin", "slopes")
    d.setdefault("solid_below", 3)
    d.setdefault("static_margin", 0.08)
    d.setdefault("com_tolerance", 0.02)
    d.setdefault("auto_wing", True)
    d.setdefault("engines", [])
    d.setdefault("surfaces", [])
    d.setdefault("booms", [])
    d.setdefault("struts", [])
    d.setdefault("cockpit", {})
    d.setdefault("batteries", {})
    d.setdefault("spawn", {})
    d.setdefault("title", d["name"])
    return d


# ---------------------------------------------------------------------------------------------------------------
# Fuselage
# ---------------------------------------------------------------------------------------------------------------
class Fuselage:
    kind = "fuselage"

    def __init__(self, f: dict, block: float):
        self.id = "fuselage"
        self.role = "fuselage"
        self.L = float(f["length"])
        self.W = float(f.get("width", self.L * 0.12))
        self.H = float(f.get("height", self.W))
        self.nose = float(f.get("nose", 0.2))
        self.tail = float(f.get("tail", 0.3))
        self.nose_shape = float(f.get("nose_shape", 0.5))
        self.nose_radius = float(f.get("nose_radius", 0.0))
        self.tail_shape = float(f.get("tail_shape", 1.0))
        self.tail_w = float(f.get("tail_width", 0.25))
        self.tail_h = float(f.get("tail_height", 0.35))
        self.upsweep = float(f.get("tail_upsweep", 0.3))
        self.n = float(f.get("section", 2.0))
        self.z0 = float(f.get("z", 0.0))
        self.profile = f.get("profile")
        self.block = block

    def dims(self, x):
        """half-width, half-height, centreline z at stations x (arrays)."""
        x = np.asarray(x, dtype=float)
        L = self.L
        if self.profile:
            p = np.array(self.profile, dtype=float)
            xf = np.clip(x / L, 0, 1)
            w = np.interp(xf, p[:, 0], p[:, 1]) * self.W / 2
            h = np.interp(xf, p[:, 0], p[:, 2]) * self.H / 2
            zc = self.z0 + np.interp(xf, p[:, 0], p[:, 3] if p.shape[1] > 3 else np.zeros(len(p))) * self.H / 2
            return w, h, zc
        rw = np.ones_like(x)
        rh = np.ones_like(x)
        zc = np.full_like(x, self.z0)
        ln, lt = self.nose * L, self.tail * L
        if ln > 0:
            f = np.clip(x / ln, 0, 1)
            r = (1 - (1 - f) ** 2) ** self.nose_shape
            r = self.nose_radius + (1 - self.nose_radius) * r
            m = x < ln
            rw[m] = r[m]
            rh[m] = r[m]
        if lt > 0:
            g = np.clip((x - (L - lt)) / lt, 0, 1) ** self.tail_shape
            m = x > L - lt
            rw[m] = (1 - (1 - self.tail_w) * g)[m]
            rh[m] = (1 - (1 - self.tail_h) * g)[m]
            gz = np.clip((x - (L - lt)) / lt, 0, 1) ** 1.5
            zc = zc + self.upsweep * self.H / 2 * gz
        return rw * self.W / 2, rh * self.H / 2, zc

    def inside(self, x, y, z):
        w, h, zc = self.dims(x)
        w = np.maximum(w, 1e-6)
        h = np.maximum(h, 1e-6)
        v = (np.abs(y) / w) ** self.n + (np.abs(z - zc) / h) ** self.n
        return (x >= 0) & (x <= self.L) & (v <= 1.0)

    def bbox(self):
        xs = np.linspace(0, self.L, 200)
        w, h, zc = self.dims(xs)
        return np.array([0, -w.max(), (zc - h).min()]), np.array([self.L, w.max(), (zc + h).max()])

    def top(self, x):
        w, h, zc = self.dims(np.array([x]))
        return float(zc[0] + h[0])

    def bottom(self, x):
        w, h, zc = self.dims(np.array([x]))
        return float(zc[0] - h[0])

    def half_width(self, x):
        return float(self.dims(np.array([x]))[0][0])

    def centre_z(self, x):
        return float(self.dims(np.array([x]))[2][0])


def naca_half(u, tc):
    u = np.clip(u, 0, 1)
    return 5 * tc * (0.2969 * np.sqrt(u) - 0.126 * u - 0.3516 * u ** 2 + 0.2843 * u ** 3 - 0.1015 * u ** 4)


NACA_PEAK = float(naca_half(np.linspace(0, 1, 2001), 1.0).max())     # 0.5 at ~30% chord


# ---------------------------------------------------------------------------------------------------------------
# Lifting surface (one panel, optionally mirrored)
# ---------------------------------------------------------------------------------------------------------------
class Surface:
    kind = "surface"

    def __init__(self, s: dict, fus: Fuselage | None, block: float, dx: float = 0.0):
        self.spec = s
        self.id = s.get("id", s.get("role", "wing"))
        self.role = s.get("role", "wing")
        self.mirror = bool(s.get("mirror", self.role not in ("vtail",)))
        self.dihedral = float(s.get("dihedral", 90.0 if self.role == "vtail" else 0.0))
        self.vertical = abs(self.dihedral) > 45
        self.span = float(s["span"])
        self.cr = float(s["root_chord"])
        self.ct = float(s.get("tip_chord", self.cr))
        self.sweep = float(s.get("sweep", 0.0))
        self.tc = float(s.get("thickness", 0.12))
        self.tc_tip = float(s.get("thickness_tip", self.tc * 0.75))
        # a real wing is at least this many blocks thick at the root (peak, ~30% chord), tapering to 1 at the tip
        self.min_root = float(s.get("min_root_blocks", 2.0 if self.role in ("wing", "vtail", "vee") else 1.5))
        self.y0 = float(s.get("y0", 0.0))
        self.block = block
        self.moves = bool(s.get("moves_with_wing", self.role == "wing"))
        if "x" in s:
            x = float(s["x"])
        elif fus is not None:
            x = float(s.get("x_frac", 0.4)) * fus.L
        else:
            x = 0.0
        self.x_le = x + (dx if self.moves else 0.0)
        zz = s.get("z", "mid")
        if isinstance(zz, str):
            if fus is None:
                z0 = 0.0
            else:
                xm = self.x_le + self.cr / 2
                zc, top, bot = fus.centre_z(xm), fus.top(xm), fus.bottom(xm)
                z0 = {"low": bot + 0.75 * block, "mid": zc, "mid+1": zc + block, "high": top - 0.75 * block,
                      "top": top, "bottom": bot}[zz]
        else:
            z0 = float(zz)
        G = math.radians(self.dihedral)
        self.G = G
        if not self.vertical:      # snap a flat panel's plane onto a block layer (clean 1-block slab)
            z0 = round(z0 / block) * block
        else:                      # snap a fin onto a block column
            self.y0 = round(self.y0 / block) * block
        self.z0 = z0
        if self.vertical:
            self.L = self.span
        else:
            half = self.span / 2 if self.mirror else self.span
            self.L = max(half - self.y0, block) / max(math.cos(G), 0.2)
        self.es = np.array([math.cos(G), math.sin(G)])     # spanwise direction in (y, z)
        self.et = np.array([-math.sin(G), math.cos(G)])    # thickness direction
        self.tanL = math.tan(math.radians(self.sweep))

    # geometry along the span
    def chord(self, s):
        return self.cr + (self.ct - self.cr) * np.clip(s / self.L, 0, 1)

    def xle(self, s):
        return self.x_le + s * self.tanL

    def point(self, s):
        """(y, z) of the reference line at span distance s (right/top side)."""
        return self.y0 + s * self.es[0], self.z0 + s * self.es[1]

    def inside(self, x, y, z):
        yy = np.abs(y) if self.mirror else y
        ry, rz = yy - self.y0, z - self.z0
        s = ry * self.es[0] + rz * self.es[1]
        t = ry * self.et[0] + rz * self.et[1]
        c = self.chord(s)
        u = (x - self.xle(s)) / c
        eta = np.clip(s / self.L, 0, 1)
        tc = self.tc + (self.tc_tip - self.tc) * eta
        peak = np.maximum(tc * c / 2, self.block * 0.5 * (self.min_root + (1.0 - self.min_root) * eta))
        half = np.maximum(naca_half(u, 1.0) / NACA_PEAK * peak, self.block * 0.5)
        return (s >= -self.block * 0.25) & (s <= self.L) & (u >= 0) & (u <= 1) & (np.abs(t) <= half)

    def thickness_at(self, s):
        eta = min(max(s / self.L, 0), 1)
        tc = self.tc + (self.tc_tip - self.tc) * eta
        return max(tc * float(self.chord(s)), self.block * (self.min_root + (1.0 - self.min_root) * eta))

    def bbox(self):
        pts = []
        for s in (0.0, self.L):
            y, z = self.point(s)
            for xx in (self.xle(s), self.xle(s) + self.chord(s)):
                for sgn in ((1, -1) if self.mirror else (1,)):
                    pts.append((xx, sgn * y, z))
        p = np.array(pts)
        pad = self.block
        return p.min(0) - pad, p.max(0) + pad

    def strips(self, n=200):
        """Spanwise strips for planform integrals: s, projected width dy (horizontal projection), chord, x_le,
        and the spanwise direction's (y, z) components."""
        s = (np.arange(n) + 0.5) / n * self.L
        ds = self.L / n
        return s, ds, self.chord(s), self.xle(s)


class Fairing:
    """Wing-root fillet: a mirrored ellipsoid blending the wing into the body (labelled like the fuselage)."""
    kind = "fairing"

    def __init__(self, cid, c, ax, mirror=True):
        self.id, self.role = cid, "fairing"
        self.c, self.ax = np.array(c, float), np.array(ax, float)
        self.mirror = mirror
        self.moves = True

    def inside(self, x, y, z):
        yy = np.abs(y) if self.mirror else y
        return ((x - self.c[0]) / self.ax[0]) ** 2 + ((yy - self.c[1]) / self.ax[1]) ** 2 +             ((z - self.c[2]) / self.ax[2]) ** 2 <= 1.0

    def bbox(self):
        lo, hi = self.c - self.ax, self.c + self.ax
        if self.mirror:
            m = max(abs(lo[1]), abs(hi[1]))
            lo[1], hi[1] = -m, m
        return lo, hi


class Capsule:
    """Boom or strut: a round bar between two points (radius at least half a block)."""
    kind = "capsule"

    def __init__(self, cid, role, a, b, r, mirror, block):
        self.id, self.role = cid, role
        self.a, self.b = np.array(a, float), np.array(b, float)
        # put the bar's axis on voxel centres across its length (a bar between two voxel columns covers neither)
        along = int(np.argmax(np.abs(self.b - self.a)))
        for k in range(3):
            if k != along:
                self.a[k] = round(self.a[k] / block) * block
                self.b[k] = round(self.b[k] / block) * block
        self.r = max(float(r), block * 0.5)
        self.mirror = mirror
        self.moves = False

    def inside(self, x, y, z):
        yy = np.abs(y) if self.mirror else y
        p = np.stack([x, yy, z], -1)
        ab = self.b - self.a
        t = np.clip(((p - self.a) @ ab) / max(ab @ ab, 1e-9), 0, 1)
        d = p - (self.a + t[..., None] * ab)
        return (d ** 2).sum(-1) <= self.r ** 2

    def bbox(self):
        lo = np.minimum(self.a, self.b) - self.r
        hi = np.maximum(self.a, self.b) + self.r
        if self.mirror:
            m = max(abs(lo[1]), abs(hi[1]))
            lo[1], hi[1] = -m, m
        return lo, hi


# ---------------------------------------------------------------------------------------------------------------
# Presets: tail configurations sized by tail-volume coefficients
# ---------------------------------------------------------------------------------------------------------------
def _wing_ref(d):
    w = [s for s in d["surfaces"] if s.get("role", "wing") == "wing"]
    if not w:
        return None
    s = w[0]
    b = float(s["span"])
    cr, ct = float(s["root_chord"]), float(s.get("tip_chord", s["root_chord"]))
    S = b * (cr + ct) / 2
    lam = ct / cr
    mac = 2 / 3 * cr * (1 + lam + lam ** 2) / (1 + lam)
    L = float(d["fuselage"]["length"]) if d.get("fuselage") else 0
    x = float(s["x"]) if "x" in s else float(s.get("x_frac", 0.4)) * L
    ybar = b / 6 * (1 + 2 * lam) / (1 + lam)
    xac = x + ybar * math.tan(math.radians(float(s.get("sweep", 0)))) + 0.25 * mac
    return S, mac, b, xac


def expand(d: dict) -> dict:
    """Fill presets (tail type, canard shorthand) into explicit surfaces/booms.  Idempotent."""
    d = defaults(d)
    if d.get("_expanded"):
        return d
    d["_expanded"] = True
    fus = d.get("fuselage")
    block = BLOCK_M[d["grid"]]
    if "canard" in d and d["canard"]:
        c = dict(role="canard", id="canard", **d["canard"])
        d["surfaces"].append(c)
    tail = d.get("tail")
    if not tail or tail == "none":
        return d
    if isinstance(tail, str):
        tail = {"type": tail}
    ttype = tail["type"]
    size = float(tail.get("size", 1.0))
    ref = _wing_ref(d)
    if ref is None or not fus:
        return d
    S, mac, b, xac = ref
    L, H = float(fus["length"]), float(fus.get("height", fus.get("width", 0.12 * fus["length"])))
    F = Fuselage(fus, block)
    x_end = float(tail.get("x_end", L))
    sweep_h = float(tail.get("sweep", 20))
    sweep_v = float(tail.get("fin_sweep", 35))
    # horizontal tail: Vh = 0.6, AR 4, taper 0.6, ac at ~x_end - 0.6 c
    Vh, Vv = float(tail.get("vh", 0.6)) * size, float(tail.get("vv", 0.045)) * size
    ARh, lamh = float(tail.get("ar", 4.0)), 0.6
    ARv, lamv = float(tail.get("fin_ar", 1.4)), 0.6
    lh = max(x_end - 0.7 * mac * 0.5 - xac, 0.25 * L)
    Sh = Vh * S * mac / lh
    Sv = Vv * S * b / lh
    bh = math.sqrt(ARh * Sh)
    crh = 2 * Sh / (bh * (1 + lamh))
    hv = math.sqrt(ARv * Sv)
    crv = 2 * Sv / (hv * (1 + lamv))
    xh = x_end - crh - 0.5 * bh / 2 * math.tan(math.radians(sweep_h)) * 0.5
    xv = x_end - crv
    ztop = F.top(x_end - crv * 0.5) - 0.5 * block
    zc = F.centre_z(x_end - crh * 0.5)
    surfs = []
    if ttype in ("conventional", "cruciform", "t"):
        fin = dict(id="fin", role="vtail", span=hv, root_chord=crv, tip_chord=crv * lamv, sweep=sweep_v,
                   x=xv, z=ztop, mirror=False, thickness=0.12)
        surfs.append(fin)
        if ttype == "conventional":
            hz, hx = zc, xh
        else:
            frac = 1.0 if ttype == "t" else 0.5
            hz = ztop + hv * frac
            hx = xv + hv * frac * math.tan(math.radians(sweep_v)) + (crv - (crv - crv * lamv) * frac) - crh
            if ttype == "t":
                hz -= 0.5 * block
        surfs.append(dict(id="htail", role="htail", span=bh, root_chord=crh, tip_chord=crh * lamh, sweep=sweep_h,
                          x=hx, z=hz, thickness=0.10))
    elif ttype == "v":
        dih = float(tail.get("dihedral", 40))
        Sp = (Sh + Sv) * 1.0
        bv = math.sqrt(ARh * Sp)
        crp = 2 * Sp / (bv * (1 + lamh))
        surfs.append(dict(id="vee", role="vee", span=bv, root_chord=crp, tip_chord=crp * lamh, sweep=sweep_h,
                          dihedral=dih, x=x_end - crp, z=F.centre_z(x_end - crp * 0.5) + 0.25 * H, thickness=0.10))
    elif ttype == "twin_boom":
        by = float(tail.get("boom_y", b * 0.18))
        by = round(by / block) * block
        x_end = float(tail.get("x_end", xac + 3.2 * mac))
        lh = x_end - 0.4 * crh - xac
        Sh = Vh * S * mac / lh
        Sv = Vv * S * b / lh / 2               # two fins
        crh = Sh / (2 * by)
        hv = math.sqrt(ARv * Sv)
        crv = 2 * Sv / (hv * (1 + lamv))
        zb = float(tail.get("boom_z", F.centre_z(xac)))
        zb = round(zb / block) * block
        wing = [s for s in d["surfaces"] if s.get("role", "wing") == "wing"][0]
        x0 = float(wing.get("x", 0.4 * L))
        d["booms"].append(dict(y=by, z=zb, x0=x0, x1=x_end, radius=float(tail.get("boom_radius", block * 0.5)),
                               mirror=True, moves_with_wing=False))
        surfs.append(dict(id="fin", role="vtail", span=hv, root_chord=crv, tip_chord=crv * lamv, sweep=sweep_v,
                          x=x_end - crv, z=zb, y0=by, mirror=True, thickness=0.12))
        surfs.append(dict(id="htail", role="htail", span=2 * by, root_chord=crh, tip_chord=crh, sweep=0,
                          x=x_end - crh, z=zb + hv * float(tail.get("htail_at", 0.0)), thickness=0.10))
    for s in surfs:
        if not any(e.get("id") == s["id"] for e in d["surfaces"]):
            d["surfaces"].append(s)
    return d


def components(d: dict, dx: float = 0.0):
    """Implicit solids of the design, fuselage first (it wins label ties).  dx slides the wing group aft."""
    d = expand(d)
    block = BLOCK_M[d["grid"]]
    comps = []
    fus = Fuselage(d["fuselage"], block) if d.get("fuselage") else None
    if fus:
        comps.append(fus)
    for s in d["surfaces"]:
        comps.append(Surface(s, fus, block, dx))
    if fus:
        for w in [c for c in comps if isinstance(c, Surface) and c.role == "wing" and not c.vertical]:
            if w.spec.get("fairing", True) is False or w.y0 > fus.W / 2:
                continue
            xm = w.x_le + 0.42 * w.cr
            hw = fus.half_width(xm)
            if hw <= 0:
                continue
            tr = w.thickness_at(hw)
            comps.append(Fairing(f"fairing_{w.id}", (xm, hw * 0.85, w.z0 + 0.15 * tr),
                                 (0.62 * w.cr, max(0.1 * w.cr, 2.2 * block, 0.3 * hw), max(0.9 * tr, 1.6 * block))))
    for i, bm in enumerate(d["booms"]):
        x0 = bm["x0"]
        if x0 == "wing_te" or x0 == "wing":
            w = next(c for c in comps if isinstance(c, Surface) and c.role == "wing")
            s = abs(bm["y"])
            x0 = float(w.xle(s) + (w.chord(s) * 0.5 if x0 == "wing_te" else 0))
        else:
            x0 = float(x0) + (dx if bm.get("moves_with_wing") else 0)
        comps.append(Capsule(f"boom{i}", "boom", (x0, bm["y"], bm["z"]), (bm["x1"], bm["y"], bm["z"]),
                             bm.get("radius", block * 0.5), bm.get("mirror", True), block))
    for i, st in enumerate(d["struts"]):
        sh = dx if st.get("moves_with_wing", True) else 0
        a = np.array(st["from"], float) + [sh, 0, 0]
        b = np.array(st["to"], float) + [sh, 0, 0]
        comps.append(Capsule(f"strut{i}", "strut", a, b, st.get("radius", 0), st.get("mirror", True), block))
    return d, fus, comps
