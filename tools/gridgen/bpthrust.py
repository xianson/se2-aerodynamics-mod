"""bpthrust.py - how far a blueprint's forward thrust line misses its centre of mass (the 'engine torque').

    python bpthrust.py ship.json NOSE        (NOSE: the cockpit forward, x,y,z - bpinfo.py)

Thrusters: each thruster block's rated ThrustPower (the game's *_ThrusterDefinition.def, read once), at the centre of its
box (as bpinfo), pushing along its axis. The forward group: the thrusters whose axis is the nose axis, the sign with the
most thrust (a plane's main engines; a few reversers point the other way). Mass: the catalogue's, as bpinfo. Up: of the
two axes across the nose, the one the ship is thinnest along. Prints the forward thrust, the thrust line's offset from
the centre of mass (up: + above it; span: across) and the torque at full forward thrust (pitch: + nose up).
"""
import json, sys, os, glob, re
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import se2blocks as sb
from bp2boxes import quat_matrix, composite_names

GAME = r"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers2\GameData\Vanilla\Content"


def ratings():
    out = {}
    for f in glob.glob(os.path.join(GAME, "**", "*ThrusterDefinition.def"), recursive=True):
        name = re.sub(r"_(Hydrogen)?ThrusterDefinition\.def$", "", os.path.basename(f))
        m = re.search(r'"ThrustPower":\s*([0-9.]+)', open(f, encoding="utf-8-sig").read())
        if m and float(m.group(1)) > 0: out[name] = float(m.group(1))
    return out


def main(src, nose_s):
    bp = json.load(open(src, encoding="utf-8"))
    cat = sb.Catalog(); names = composite_names(); rate = ratings()
    off = np.asarray(sb.CAL["aabb_low_offset"], float)
    nose = np.array([float(v) for v in nose_s.split(",")])
    M, MX = 0.0, np.zeros(3); lo_all, hi_all = np.full(3, 1e9), np.full(3, -1e9)
    thr = []
    for b in bp["blocks"]:
        name = names.get(b["def"].lower())
        if name is None or name not in cat: continue
        R = quat_matrix(*b["orient"])
        fwd = sb.dname(R @ np.array([0, 0, -1.0])); up = sb.dname(R @ np.array([0, 1.0, 0]))
        lo, hi = cat.box(name, np.array(b["pos"], float) / sb.CELL_M, (fwd, up))
        lo = np.asarray(lo, float) + off; hi = np.asarray(hi, float)
        c = (lo + hi + 1.0) / 2.0 * 0.25
        lo_all = np.minimum(lo_all, lo * 0.25); hi_all = np.maximum(hi_all, (hi + 1) * 0.25)
        m = float(getattr(cat[name], "mass", 0.0)); M += m; MX += m * c
        if name in rate:
            ax = np.rint(R @ np.array([0, 0, -1.0]))
            thr.append((name, c, ax, rate[name]))
    com = MX / M
    along = [(n, c, ax, t) for n, c, ax, t in thr if abs(ax @ nose) > 0.9]
    plus = sum(t for _, _, ax, t in along if ax @ nose > 0); minus = sum(t for _, _, ax, t in along if ax @ nose < 0)
    sg = 1.0 if plus >= minus else -1.0
    group = [(n, c, t) for n, c, ax, t in along if (ax @ nose) * sg > 0]
    ext = hi_all - lo_all
    across = [i for i in range(3) if abs(nose[i]) < 0.5]
    ui = min(across, key=lambda i: ext[i]); upv = np.zeros(3); upv[ui] = 1.0
    span = np.cross(upv, nose)   # (nose x up the other way: span; pitch axis = nose x up)
    pitch = np.cross(nose, upv)
    F = sum(t for _, _, t in group)
    tau = sum(np.cross(c - com, nose * t) for _, c, t in group) if group else np.zeros(3)
    line = sum((c - com) * t for _, c, t in group) / F if F > 0 else np.zeros(3)
    kinds = {}
    for n, _, t in group: kinds[n] = kinds.get(n, 0) + 1
    length = ext @ np.abs(nose)
    print(f"forward thrust {F / 1e3:,.0f} kN from {len(group)} ({kinds}); weight {M * 9.81 / 1e3:,.0f} kN; length {length:.1f} m")
    print(f"thrust line {line @ upv:+.2f} m up, {line @ span:+.2f} m across (of the centre of mass); "
          f"torque at full thrust: pitch {tau @ pitch / 1e3:+,.0f} kN m (+ nose up), yaw {tau @ upv / 1e3:+,.0f} kN m, roll {tau @ nose / 1e3:+,.0f} kN m")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
