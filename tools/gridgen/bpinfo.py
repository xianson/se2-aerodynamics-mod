"""bpinfo.py - a blueprint's real nose and centre of mass, for AeroBench damping (DAMP_NOSE, DAMP_COM).

    python bpinfo.py ship.json            (VrbReader <blueprint dir> --json > ship.json)

The nose: the forward of its cockpits / seats (the catalogue's names with Cockpit or Seat or Flight; the most common
forward when they differ). The centre of mass: each known block's catalogue mass at the centre of its box (the same
0.25 m cells and offsets bp2boxes writes), in metres in the .boxes frame (a cell i spans i..i+1 x 0.25 m). Prints
'nose x,y,z com x,y,z mass kg known/total' - blocks the catalogue does not know (mods, unknown GUIDs) are left out of
the mass, as bp2boxes leaves them out of the shape.
"""
import json, sys, os, collections
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import se2blocks as sb
from bp2boxes import quat_matrix, composite_names


def main(src):
    bp = json.load(open(src, encoding="utf-8"))
    cat = sb.Catalog(); names = composite_names()
    off = np.asarray(sb.CAL["aabb_low_offset"], float)
    M, MX, known = 0.0, np.zeros(3), 0
    fwds = collections.Counter(); seats = []
    for b in bp["blocks"]:
        name = names.get(b["def"].lower())
        if name is None or name not in cat: continue
        R = quat_matrix(*b["orient"])
        fwd = sb.dname(R @ np.array([0, 0, -1.0])); up = sb.dname(R @ np.array([0, 1.0, 0]))
        pos = np.array(b["pos"], float) / sb.CELL_M
        lo, hi = cat.box(name, pos, (fwd, up))
        lo = np.asarray(lo, float) + off; hi = np.asarray(hi, float)
        c = (lo + hi + 1.0) / 2.0 * 0.25
        m = float(getattr(cat[name], "mass", 0.0))
        M += m; MX += m * c; known += 1
        if any(k in name for k in ("Cockpit", "Seat", "Flight")):
            f = tuple(int(v) for v in np.rint(R @ np.array([0, 0, -1.0])))
            fwds[f] += 1; seats.append(name)
    com = MX / M if M > 0 else np.zeros(3)
    nose = fwds.most_common(1)[0][0] if fwds else None
    ns = f"{nose[0]},{nose[1]},{nose[2]}" if nose else "none"
    print(f"nose {ns} com {com[0]:.3f},{com[1]:.3f},{com[2]:.3f} mass {M:.0f} known {known}/{len(bp['blocks'])} seats {dict(fwds)} {sorted(set(seats))[:3]}")


if __name__ == "__main__":
    main(sys.argv[1])
