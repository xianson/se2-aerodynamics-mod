"""bp2boxes.py - a blueprint's blocks (tools\\shared\\VrbReader --json) as an AeroBench / aero_probe .boxes fixture.

    VrbReader <blueprint dir> --json > ship.json
    python bp2boxes.py ship.json Tests/data/NAME.boxes

Each block: its composite GUID -> its name; its position (metres, 0.25 m cells: the GridBuilder's position = 0.25 x cell)
and orientation (quaternion x y z w -> the forward / up the catalogue takes); its occupied cells:
  * blocks of the measured catalogue (se2blocks.Catalog: data/catalog.txt): its box, armour shapes as the staircase of
    0.25 m cells (gridgen.block_cell_lines), its measured mass;
  * every other block - heavy armour, conveyors, tanks, cargo, trusses... (gamedefs.py: the game's own occupancy and mass
    formula, data/gameblocks.json): the game's occupied cell groups, the game's mass;
  * the mod's own blocks (gamedefs.py MOD_SIZES: the generators' block boxes, mass by the game's formula on the box).
    Control surfaces are tagged:  # surf <kind> lox loy loz hix hiy hiz Fx Fy Fz Ux Uy Uz   (a comment line: every reader
    that does not know it skips it; kind cs3s / cs3l / cs3_025 / cs3_100 / cs3_150 / cs1, the box inclusive cells, F / U the block's forward / up in
    grid axes). The 3x3x1 surfaces model all their own air (BlockComponentFactory.IsSelfModelled): their cells are NOT
    hull lines, as in game; the 1x1 surface's cube is (a hull line too).
Air-breathing engines carry 'thr ax ay az' (the bench makes their faces flow-through). The header: the blocks' mass.
Blocks nothing knows are listed and skipped.
"""
import collections
import json
import os
import re
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import se2blocks as sb
import gridgen
import gamedefs

SURF_KIND = {"ControlSurface_Small": "cs3s", "ControlSurface_Large": "cs3l", "ControlSurface_1x1": "cs1",
             "ControlSurface_025": "cs3_025", "ControlSurface_100": "cs3_100", "ControlSurface_150": "cs3_150"}


def quat_matrix(x, y, z, w):
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


GAME = gamedefs.CONTENT


def composite_names():
    """A blueprint stores each block's SERVER COMPOSITE GUID: composite -> block name, the game's blocks and the mod's
    (gamedefs' index; data/composites.json, the older vanilla-only index, for any it lacks)."""
    idx = {e["composite"]: n for n, e in gamedefs.index().items() if "composite" in e}
    cache = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data", "composites.json")
    if os.path.exists(cache):
        for k, v in json.load(open(cache, encoding="utf-8")).items():
            idx.setdefault(k, v)
    return idx


def group_cell_lines(groups, pos, M):
    """Occupied cell groups (inclusive, local frame) of a block at `pos` (cells) turned by M, as .boxes lines."""
    off = sb.CAL["aabb_low_offset"]
    out = []
    for g in groups:
        lo, hi = np.array(g[:3]) - off, np.array(g[3:])           # continuous [lo, hi), as the catalogue's AABB
        c = np.array([M @ np.array(p) for p in ((x, y, z) for x in (lo[0], hi[0]) for y in (lo[1], hi[1]) for z in (lo[2], hi[2]))])
        a, b = np.rint(pos + c.min(0)).astype(int), np.rint(pos + c.max(0)).astype(int)
        out.append(" ".join(str(v) for v in list(a + off) + list(b)))
    return out


def block_geometry(name, cat, gdx, pos, M):
    """(hull lines, the block's whole box as one inclusive line, mass kg, source) - None if nothing knows the block."""
    fwd, up = sb.dname(M @ np.array([0, 0, -1.0])), sb.dname(M @ np.array([0, 1.0, 0]))
    if name in cat and cat[name].measured:
        lines = gridgen.block_cell_lines(cat, name, pos, (fwd, up))
        lo, hi = cat.box(name, pos, (fwd, up))
        box = " ".join(str(int(v)) for v in list(np.asarray(lo) + sb.CAL["aabb_low_offset"]) + list(hi))
        return lines, box, cat[name].mass, "catalogue"
    e = gdx.get(name)
    if e and "bounds" in e:
        lines = group_cell_lines(gamedefs.groups(e), pos, M)
        box = group_cell_lines([e["bounds"]], pos, M)[0]
        return lines, box, e.get("mass", 0.0), "mod" if e.get("mod") else "game"
    return None


class Block:
    """One blueprint block, placed: name (None: an unknown GUID), M (its rotation, integer), pos (cells), and - when
    something knows it (geo) - lines (its occupied cells, .boxes lines), box (its whole box, one inclusive line), mass (kg),
    src (catalogue / game / mod), role (gamedefs MOD_ROLES: 'surface', 'self')."""
    def __init__(self, b, name, M, pos, geo, role):
        self.guid, self.name, self.M, self.pos, self.role = b["def"], name, M, pos, role
        self.known = geo is not None
        self.lines, self.box, self.mass, self.src = geo if geo else ([], None, 0.0, None)

    @property
    def centre_m(self):
        """Its box's centre in metres, the .boxes frame (a cell i spans i..i+1 x 0.25 m)."""
        v = [int(x) for x in self.box.split()]
        return (np.array(v[:3]) + np.array(v[3:]) + 1.0) / 2.0 * sb.CELL_M

    @property
    def forward(self):
        return self.M @ np.array([0, 0, -1])


def blueprint_blocks(bp):
    """Every block of a blueprint (VrbReader --json), placed and resolved as bp2boxes writes it. The catalogue is the
    committed snapshot (data/): a stale %TEMP%/OrbitalMod/gen/catalog.txt shadowed it - every block a default."""
    cat = sb.Catalog(snapshot=True)
    gdx = gamedefs.index()
    names = composite_names()
    for b in bp["blocks"]:
        name = names.get(b["def"].lower())
        M = np.rint(quat_matrix(*b["orient"])).astype(int)
        pos = np.array(b["pos"], float) / sb.CELL_M
        geo = block_geometry(name, cat, gdx, pos, M) if name else None
        yield Block(b, name, M, pos, geo, gdx.get(name, {}).get("role", "") if name else "")


def main(src, dst):
    bp = json.load(open(src, encoding="utf-8"))
    lines, surfs, mass, missing, used = [], [], 0.0, collections.Counter(), set()
    by_src = collections.Counter()
    for k in blueprint_blocks(bp):
        if not k.known:
            missing[k.name or k.guid] += 1
            continue
        name, M, bl = k.name, k.M, k.lines
        if name in SURF_KIND:
            F, U = M @ np.array([0, 0, -1]), M @ np.array([0, 1, 0])
            surfs.append(f"# surf {SURF_KIND[name]} {k.box} {F[0]} {F[1]} {F[2]} {U[0]} {U[1]} {U[2]}")
        if "self" in k.role:
            bl = []          # (its component models all its air: out of the hull, as in game)
        if name.startswith("AtmosphericThruster"):
            # an air-breathing engine: tagged with its axis (the block's forward, grid axes) - the bench makes its
            # intake and exhaust faces flow-through (DampedShadowedDragModel.SetThroughFaces)
            ax = k.forward
            bl = [l + f" thr {ax[0]} {ax[1]} {ax[2]}" for l in bl]
        lines += bl
        mass += k.mass
        used.add(name)
        by_src[k.src] += 1
    # the grid's block size as the game takes it (AeroGridComponent.BlockSize): its SMALLEST block decides - any 0.5 m block, a small grid at 0.25 m cells
    sizes = [int(m.group(1)) for m in (re.search(r"(50|100|150|200|250|500|750|1000)(?:Light|Heavy)?$", n) for n in used) if m]
    bsize = 0.5 if any(x < 125 for x in sizes) else 2.5
    with open(dst, "w", encoding="utf-8", newline="\n") as f:
        f.write(f"# blockSize {bsize} mass {mass:.0f}\n# from blueprint '{bp.get('name', '?')}' ({len(bp['blocks'])} blocks, {sum(missing.values())} unknown)\n")
        if surfs:
            f.write("\n".join(surfs) + "\n")
        f.write("\n".join(lines) + "\n")
    print(f"{bp.get('name')}: {len(bp['blocks'])} blocks -> {len(lines)} boxes, {len(surfs)} control surfaces, mass {mass / 1000:.1f} t "
          f"({', '.join(f'{v} {k}' for k, v in by_src.most_common())}), unknown {sum(missing.values())} of {len(missing)} kinds")
    for g, n in missing.most_common(8):
        print(f"   unknown {g} x{n}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
