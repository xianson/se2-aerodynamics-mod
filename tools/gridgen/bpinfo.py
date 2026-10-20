"""bpinfo.py - a blueprint's real nose and centre of mass, for AeroBench damping (DAMP_NOSE, DAMP_COM).

    python bpinfo.py ship.json            (VrbReader <blueprint dir> --json > ship.json)

The nose: the forward of its cockpits / seats (the catalogue's names with Cockpit or Seat or Flight; the most common
forward when they differ). The centre of mass: each block's mass at the centre of its box (bp2boxes.blueprint_blocks:
the same 0.25 m cells, offsets and masses bp2boxes writes - catalogue, game, mod), in metres in the .boxes frame (a cell i
spans i..i+1 x 0.25 m). Prints 'nose x,y,z com x,y,z mass kg known/total' - blocks nothing knows are left out.
"""
import json, sys, os, collections
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bp2boxes import blueprint_blocks


def main(src):
    bp = json.load(open(src, encoding="utf-8"))
    M, MX, known = 0.0, np.zeros(3), 0
    fwds = collections.Counter(); seats = []
    for k in blueprint_blocks(bp):
        if not k.known: continue
        M += k.mass; MX += k.mass * k.centre_m; known += 1
        if any(w in k.name for w in ("Cockpit", "Seat", "Flight")):
            fwds[tuple(int(v) for v in k.forward)] += 1; seats.append(k.name)
    com = MX / M if M > 0 else np.zeros(3)
    nose = fwds.most_common(1)[0][0] if fwds else None
    ns = f"{nose[0]},{nose[1]},{nose[2]}" if nose else "none"
    print(f"nose {ns} com {com[0]:.3f},{com[1]:.3f},{com[2]:.3f} mass {M:.0f} known {known}/{len(bp['blocks'])} seats {dict(fwds)} {sorted(set(seats))[:3]}")


if __name__ == "__main__":
    main(sys.argv[1])
