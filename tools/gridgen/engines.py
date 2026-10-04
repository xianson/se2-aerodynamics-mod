"""engines - thrusters anywhere, with a clear exhaust (and intake, for atmospheric) lane.

Engine entry in the design:
  {"mount": "nose" | "tail" | "pod" | "fuselage_side" | "rear_fuselage" | "wingtip" | "wing_root" | "custom",
   "type": "atmospheric" | "hydrogen" | "ion",  "size": 500,  "prefab": "AtmosphericThruster500" (overrides),
   "thrust": "forward" | "up" | "down" | "aft" | "left" | "right"   (direction it pushes the plane; default forward)
   "surface": "wing"            (pod / wingtip / wing_root: which surface)
   "span_frac": [0.3, 0.6]      (pod: stations as fractions of the semi-span; or "count": 2 -> evenly spread)
   "side": "below" | "above"    (pod)
   "x" / "x_frac", "y", "z", "z_off"   (stations in metres / fraction of fuselage length; offsets)
   "mirror": true               (side mounts default true)
   "clear": 6.0                 (exhaust clear length, metres; default CAL exhaust_clear_lengths x engine length)
   "embedded": bool, "nudge": "up" | "down" | "out" | "aft"   (custom mounts)
   "cluster": [2, 1]            (n across x m stacked thrusters as one engine, when one block is too small)
   "nacelle": true, "nacelle_lip": 1   (external engines: a round cowling one block thick, open intake and
                                exhaust ducts reaching nacelle_lip blocks past the thruster)}

Placement: the nominal spot from the mount; then the engine is nudged (one block at a time in the mount's nudge
direction) until neither the engine nor its lanes hit the airframe - except embedded mounts (tail, wing_root),
which cut their own hole and an exhaust/intake channel through the parts they are embedded in.  A pylon of armor
cubes joins a nudged engine back to the structure on the side away from its lanes.
"""
from __future__ import annotations

import math

import numpy as np

import se2blocks as sb

THRUST = {"forward": (0, 0, -1), "aft": (0, 0, 1), "up": (0, 1, 0), "down": (0, -1, 0),
          "left": (-1, 0, 0), "right": (1, 0, 0)}
NUDGE = {"up": (0, 1, 0), "down": (0, -1, 0), "out": (1, 0, 0), "aft": (0, 0, 1), "fwd": (0, 0, -1)}


def snap_lo(lo, ext, u):
    """Align a box's low corner: whole-block extents onto the block lattice, others onto the small-block lattice."""
    out = np.empty(3, int)
    for a in range(3):
        if ext[a] % u == 0:
            out[a] = int(round((lo[a] + u / 2) / u)) * u - u // 2
        else:
            out[a] = int(round((lo[a] + 1) / 2)) * 2 - 1
    return out


def lanes_for(lo, hi, exhaust, ext, kind, clear_m=None):
    """Cell boxes that must stay empty: behind the exhaust face, and in front of an atmospheric intake."""
    a = int(np.nonzero(exhaust)[0][0])
    sgn = int(exhaust[a])
    L = ext[a]
    lx = int(round(clear_m / sb.CELL_M)) if clear_m else int(round(sb.CAL["exhaust_clear_lengths"] * L))
    li = int(round(sb.CAL["intake_clear_lengths"] * L)) if kind == "atmospheric" else 0
    out = []
    for length, s, tag in ((lx, sgn, "exhaust"), (li, -sgn, "intake")):
        if length <= 0:
            continue
        l2, h2 = lo.copy(), hi.copy()
        if s > 0:
            l2[a], h2[a] = hi[a], hi[a] + length
        else:
            l2[a], h2[a] = lo[a] - length, lo[a]
        out.append((l2, h2, tag))
    return out


def instances(e, d, fus, surfs, grid, model):
    """Expand one engine entry into (side, nominal centre in metres, nudge vector, embedded, carvable ids)."""
    mount = e.get("mount", "custom")
    B = sb.GRID_UNIT[grid] * sb.CELL_M
    out = []
    zoff = float(e.get("z_off", 0.0))

    def surf(sid):
        for s in surfs:
            if s.id == sid or s.role == sid:
                return s
        raise ValueError(f"engine surface {sid} not found")

    def station():
        if "x" in e:
            return float(e["x"])
        return float(e.get("x_frac", 0.5)) * fus.L

    if mount == "nose":
        p = model.nose_point() + [0, 0, zoff]
        out.append((0, p, "front", NUDGE["up"], False, ()))
    elif mount == "tail":
        p = model.tail_point() + [0, 0, zoff]
        # an atmospheric pusher cannot breathe from inside the hull: it climbs over the tail cone instead
        nd = NUDGE[e.get("nudge", "up" if e.get("type", "atmospheric") == "atmospheric" else "aft")]
        out.append((0, p, "back", nd, True, ("fuselage",)))
    elif mount in ("fuselage_side", "rear_fuselage"):
        x = float(e["x"]) if "x" in e else float(e.get("x_frac", 0.35 if mount == "fuselage_side" else 0.72)) * fus.L
        hw = fus.half_width(x)
        zc = fus.centre_z(x) + (0.3 * fus.dims(np.array([x]))[1][0] if mount == "rear_fuselage" else 0) + zoff
        for side in (1, -1):
            out.append((side, np.array([x, side * hw, zc]), "side", (side, 0, 0), False, ()))
    elif mount == "pod":
        s = surf(e.get("surface", "wing"))
        fr = e.get("span_frac")
        if fr is None:
            n = int(e.get("count", 1))
            fr = [0.35] if n == 1 else list(np.linspace(0.25, 0.65, n))
        above = e.get("side", "below") == "above"
        semi = s.span / 2 if s.mirror else s.span
        for f in fr:
            y = f * semi
            ss = (y - s.y0) / max(math.cos(s.G), 0.2)
            c, xle = float(s.chord(ss)), float(s.xle(ss))
            zc = s.z0 + ss * math.sin(s.G)
            for side in (1, -1):
                out.append((side, np.array([xle, side * y, zc + (B if above else -B) + zoff]),
                            "pod_above" if above else "pod", NUDGE["up" if above else "down"], False, ()))
    elif mount == "wingtip":
        s = surf(e.get("surface", "wing"))
        y, z = s.point(s.L)
        x = float(s.xle(s.L) + s.chord(s.L) * 0.5)
        for side in (1, -1):
            out.append((side, np.array([x, side * (y + B * 0.5), z + zoff]), "side", (side, 0, 0), False, ()))
    elif mount == "wing_root":
        s = surf(e.get("surface", "wing"))
        xm = s.x_le + 0.4 * s.cr
        hw = fus.half_width(xm) if fus else B
        ss = (hw - s.y0) / max(math.cos(s.G), 0.2)
        x = float(s.xle(ss) + 0.4 * s.chord(ss))
        z = s.z0 + ss * math.sin(s.G) + zoff
        for side in (1, -1):
            out.append((side, np.array([x, side * hw, z]), "root", (side, 0, 0), True, (s.id,)))
    elif mount == "custom":
        p = np.array([float(e.get("x", 0)), float(e.get("y", 0)), float(e.get("z", 0))])
        mir = e.get("mirror", abs(p[1]) > 1e-6)
        nd = NUDGE.get(e.get("nudge", "up"), (0, 1, 0))
        emb = bool(e.get("embedded", False))
        carve = tuple(e.get("carve", ["fuselage"] if emb else []))
        out.append((1 if mir else 0, p, "centre", nd, emb, carve))
        if mir:
            q = p.copy(); q[1] = -q[1]
            out.append((-1, q, "centre", (-nd[0], nd[1], nd[2]), emb, carve))
    else:
        raise ValueError(f"unknown engine mount {mount}")
    return out


def place_all(model, d, fus, surfs, cat):
    """Places every engine into model (blocks, lanes, carved voxels).  Returns list of engine records."""
    lat = model.lat
    u = lat.u
    grid = d["grid"]
    recs = []
    for ei, e in enumerate(d["engines"]):
        kind = e.get("type", "atmospheric")
        name = e.get("prefab") or sb.thruster_name(kind, e.get("size"), grid)
        thrust = np.array(THRUST[e.get("thrust", "forward")])
        exhaust = -thrust
        for side, centre_m, anchor, nudge, embedded, carve in instances(e, d, fus, surfs, grid, model):
            ex = exhaust.copy()
            if side < 0:
                ex[0] = -ex[0]
            orient = sb.orient_for_thruster(tuple(ex))
            unit = cat.extent(name, orient)
            # a cluster: n x m thrusters side by side across the thrust axis, placed and cleared as one unit
            cl = list(e.get("cluster", [1, 1]))
            ax_e = int(np.nonzero(ex)[0][0])
            cross = [i for i in range(3) if i != ax_e]
            if ax_e != 1:
                cross = [0, 1] if ax_e == 2 else [2, 1]          # first count across, second stacked vertically
            ext = unit.copy()
            ext[cross[0]] *= int(cl[0])
            ext[cross[1]] *= int(cl[1])
            c = np.array([centre_m[1], centre_m[2], centre_m[0]]) / sb.CELL_M     # grid cells
            # anchor the nominal point: the engine sits beside / behind / below it, not centred on it
            if anchor == "front":
                c[2] += ext[2] / 2
            elif anchor == "back":
                c[2] -= ext[2] / 2
            elif anchor in ("side", "root"):
                c[0] += side * ext[0] / 2
            elif anchor in ("pod", "pod_above"):
                c[1] += (-1 if anchor == "pod" else 1) * ext[1] / 2
                # centred on the leading edge, but the rear at least one block under the wing (a pylon needs wing)
                c[2] += max(0.0, u - ext[2] / 2)
            if side == 0:
                c[0] = 0
            lo = snap_lo(c - ext / 2, ext, u)
            if side == 0 and ext[0] % u == 0 and (ext[0] // u) % 2 == 1:
                lo[0] = -ext[0] // 2                   # keep a centreline engine exactly on the centreline
            res = None
            for step in range(16):
                hi = lo + ext
                lanes = lanes_for(lo, hi, ex, ext, kind, e.get("clear"))
                ok, carve_vox = model.engine_fits(lo, hi, lanes, embedded and step == 0, carve)
                if ok:
                    res = (lo.copy(), hi.copy(), lanes, carve_vox, step)
                    break
                lo = lo + np.array(nudge) * u
            if res is None:
                model.problems.append(f"engine {ei} ({e.get('mount')}) side {side}: no clear spot found")
                continue
            lo, hi, lanes, carve_vox, step = res
            units = []
            for a_ in range(int(cl[0])):
                for b_ in range(int(cl[1])):
                    ulo = lo.copy()
                    ulo[cross[0]] += a_ * unit[cross[0]]
                    ulo[cross[1]] += b_ * unit[cross[1]]
                    units.append((cat.pos_for(name, ulo, orient), ulo, ulo + unit))
            rec = dict(index=ei, mount=e.get("mount"), side=side, name=name, type=kind, pos=units[0][0],
                       orient=orient, lo=lo, hi=hi, units=units, lanes=lanes, exhaust=tuple(int(v) for v in ex),
                       nudged=step, embedded=embedded and step == 0)
            model.add_engine(rec, carve_vox)
            if not rec["embedded"]:
                model.add_pylon(rec, -np.array(nudge))
                if e.get("nacelle", True):
                    model.add_nacelle(rec, float(e.get("nacelle_lip", 1)))
            recs.append(rec)
    return recs
