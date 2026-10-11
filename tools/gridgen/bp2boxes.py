"""bp2boxes.py - a blueprint's blocks (D:\\aero\\tools\\VrbReader --json) as an AeroBench / aero_probe .boxes fixture.

    VrbReader <blueprint dir> --json > ship.json
    python bp2boxes.py ship.json Tests/data/NAME.boxes

Each block: its definition GUID -> the catalogue's block (se2blocks.Catalog), its position (metres, 0.25 m cells: the
GridBuilder's position = 0.25 x cell) and its orientation (quaternion x y z w -> the forward / up the catalogue takes),
its occupied cells by gridgen.block_cell_lines (slopes as the game's staircase of 0.25 m cells). The header carries the
blocks' mass (the catalogue's). Blocks the catalogue does not know are listed and skipped.
"""
import json, sys, os, collections
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import se2blocks as sb
import gridgen

def quat_matrix(x, y, z, w):
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])

GAME = r"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers2\GameData\Vanilla\Content"

def composite_names():
    """A blueprint stores each block's SERVER COMPOSITE GUID; its prefab binding (NAME.def: PrefabBindingDefinition,
    ServerComposite) names it - the catalogue's key. Indexed once from the game's Armors and Blocks, cached beside us."""
    cache = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data", "composites.json")
    if os.path.exists(cache): return json.load(open(cache, encoding="utf-8"))
    idx = {}
    for sub in ("Armors", "Blocks"):
        for root, _, files in os.walk(os.path.join(GAME, sub)):
            for f in files:
                if not f.endswith(".def") or "_" in f: continue
                try: d = json.load(open(os.path.join(root, f), encoding="utf-8-sig"))
                except Exception: continue
                v = d.get("$Value", {})
                if "ServerComposite" in v: idx[v["ServerComposite"].lower()] = f[:-4]
    json.dump(idx, open(cache, "w", encoding="utf-8"), indent=0)
    return idx

def main(src, dst):
    bp = json.load(open(src, encoding="utf-8"))
    cat = sb.Catalog()
    names = composite_names()
    lines, mass, missing, used = [], 0.0, collections.Counter(), set()
    for b in bp["blocks"]:
        name = names.get(b["def"].lower())
        if name is None or name not in cat:
            missing[name or b["def"]] += 1
            continue
        M = quat_matrix(*b["orient"])
        fwd = sb.dname(M @ np.array([0, 0, -1.0])); up = sb.dname(M @ np.array([0, 1.0, 0]))
        pos = np.array(b["pos"], float) / sb.CELL_M
        bl = gridgen.block_cell_lines(cat, name, pos, (fwd, up))
        if name.startswith("AtmosphericThruster"):
            # an air-breathing engine: tagged with its axis (the block's forward, grid axes) - the bench makes its
            # intake and exhaust faces flow-through (DampedShadowedDragModel.SetThroughFaces)
            ax = np.rint(M @ np.array([0, 0, -1.0])).astype(int)
            bl = [l + f" thr {ax[0]} {ax[1]} {ax[2]}" for l in bl]
        lines += bl
        mass += getattr(cat[name], "mass", 0.0); used.add(name)
    # the grid's block size as the game takes it (AeroGridComponent.BlockSize): its SMALLEST block decides - any 0.5 m block, a small grid at 0.25 m cells
    import re
    sizes = [int(m.group(1)) for m in (re.search(r"(50|100|150|200|250|500|750|1000)(?:Light|Heavy)?$", n) for n in used) if m]
    bsize = 0.5 if any(x < 125 for x in sizes) else 2.5
    with open(dst, "w", encoding="utf-8", newline="\n") as f:
        f.write(f"# blockSize {bsize} mass {mass:.0f}\n# from blueprint '{bp.get('name', '?')}' ({len(bp['blocks'])} blocks, {sum(missing.values())} unknown)\n")
        f.write("\n".join(lines) + "\n")
    print(f"{bp.get('name')}: {len(bp['blocks'])} blocks -> {len(lines)} boxes, mass {mass / 1000:.1f} t (catalogue), unknown {sum(missing.values())} of {len(missing)} kinds")
    for g, n in missing.most_common(8): print(f"   unknown {g} x{n}")

if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
