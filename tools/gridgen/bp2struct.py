"""bp2struct.py - a blueprint's blocks as a STRUCTURE fixture for the offline structure / heat suites (Tests/StructureTests,
AeroBench 'struct'): each block's occupied cells, its mass and its name, one cell box per line.

    VrbReader <blueprint dir> --json > ship.json
    python bp2struct.py ship.json Tests/data/struct/NAME.struct

Line: minX minY minZ maxX maxY maxZ block mass_kg name [thr ax ay az]  (0.25 m cells, inclusive, game cell indexing - the
same boxes bp2boxes writes - bp2boxes.blueprint_blocks: the measured catalogue SNAPSHOT, else the game's own cells and
mass (gamedefs.py: heavy armour, conveyors, tanks...), else the mod's blocks; a block's lines share its index). The mod's
self-modelled blocks (3x3x1 control surfaces) are in: the structure is every block. A block nothing knows: a cube of the
size its name says, volume-scaled mass (se2blocks' placeholder), listed.
"""
import json, sys, os, collections, re
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import se2blocks as sb
import gridgen
from bp2boxes import blueprint_blocks


def main(src, dst):
    bp = json.load(open(src, encoding="utf-8"))
    cat = sb.Catalog(snapshot=True)
    lines, mass, missing, used, nb = [], 0.0, collections.Counter(), set(), 0
    for k in blueprint_blocks(bp):
        name = k.name
        if name is None:
            missing[k.guid] += 1
            continue
        if k.known:
            bl, m = k.lines, float(k.mass)
        else:
            # a block nothing knows: a cube of the size its name says, volume-scaled mass (se2blocks' placeholder) -
            # it still joins its neighbours, as in the game
            cat.by_name[name] = sb._default_def(name, k.guid)
            missing["(placeholder) " + name] += 1
            bl = gridgen.block_cell_lines(cat, name, k.pos, (sb.dname(k.forward), sb.dname(k.M @ np.array([0, 1, 0]))))
            m = float(cat[name].mass)
        tail = ""
        if name.startswith("AtmosphericThruster"):
            ax = k.forward
            tail = f" thr {ax[0]} {ax[1]} {ax[2]}"
        lines += [f"{l} {nb} {m:.1f} {name}{tail}" for l in bl]
        mass += m; used.add(name); nb += 1
    sizes = [int(g.group(1)) for g in (re.search(r"(50|100|150|200|250|500|750|1000)(?:Light|Heavy)?$", n) for n in used) if g]
    bsize = 0.5 if any(x < 125 for x in sizes) else 2.5
    os.makedirs(os.path.dirname(os.path.abspath(dst)), exist_ok=True)
    with open(dst, "w", encoding="utf-8", newline="\n") as f:
        f.write(f"# blockSize {bsize} mass {mass:.0f} blocks {nb}\n# from blueprint '{bp.get('name', '?')}' ({len(bp['blocks'])} blocks, {sum(missing.values())} unknown)\n")
        f.write("\n".join(lines) + "\n")
    print(f"{bp.get('name')}: {nb} blocks -> {len(lines)} boxes, mass {mass / 1000:.1f} t, unknown {sum(missing.values())} of {len(missing)} kinds")
    for g, n in missing.most_common(8): print(f"   unknown {g} x{n}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
