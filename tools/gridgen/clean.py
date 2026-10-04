"""clean - the "clean" style: fuselage + flat wings (+ a plain tail), built straight from integer rules so every
shape repeats regularly (no coverage noise, no stair-steps in height).

Fuselage: a chamfered rectangle (octagon) section,  |x| <= a, |y| <= b, |x| + |y| <= a + b - c  (voxels), the same
ring repeated along the body; a 45-degree nose cone (every face shrinks one block per row down to a 3 x 3 tip) and
a 45-degree tail cone down to the end section (sized for the tail engine cluster).  With the slope plane through
each voxel's centre, every chamfer and cone face comes out as one flat, continuous face.

Wings and tail: flat slabs of constant thickness (2-3 blocks) in grid layers, no dihedral or twist.  The leading
and trailing edges step by a fixed rule per span block (Bresenham on the edge's sweep), so sweep and taper repeat
regularly.  The vertical fin is the same slab standing on the body.

Design keys (blocks are the grid's block size; lengths in metres):
  "style": "clean" (default),
  "fuselage": {"length", "width", "height", "chamfer": 0.6 (fraction of the half-size cut at 45 deg),
               "tail_end": [w, h] (blocks; default: fits the engine cluster), "z": 0},
  "wing": {"span", "root_chord", "tip_chord", "sweep" (leading edge, deg), "x" | "x_frac", "z": "low"|"mid"|"high",
           "thickness_blocks": 2|3, "controls": {...}},
  "tail": true | false | "fin"  (plain horizontal + vertical stabiliser, default on; "fin": only the fin),
  "tail_size": 1.0, "tail_taper": 0.6, "fin_taper": 0.6, "tail_sweep", "fin_sweep",
  "htail": {...}, "fin": {...}  (optional overrides of the auto-sized tail surfaces).
"""
from __future__ import annotations

import math

import numpy as np

import design as dz
import se2blocks as sb
import voxel as vx


def _odd(n):
    n = max(1, int(round(n)))
    return n if n % 2 == 1 else n + 1


def wing_spec(d):
    w = dict(d["wing"])
    w.setdefault("id", "wing")
    w["role"] = "wing"
    w["dihedral"] = 0.0
    w["mirror"] = True
    return w


def tail_specs(d, fus_L, B):
    """A plain tail sized by tail-volume coefficients: horizontal stabiliser at mid body height ending at the tail,
    a fin on top.  Returns surface spec dicts."""
    t = d.get("tail", True)
    if t is False or t == "none":
        return []
    w = wing_spec(d)
    b, cr = float(w["span"]), float(w["root_chord"])
    ct = float(w.get("tip_chord", cr))
    S = b * (cr + ct) / 2
    lam = ct / cr
    mac = 2 / 3 * cr * (1 + lam + lam ** 2) / (1 + lam)
    x_w = float(w["x"]) if "x" in w else float(w.get("x_frac", 0.4)) * fus_L
    ybar = b / 6 * (1 + 2 * lam) / (1 + lam)
    xac = x_w + ybar * math.tan(math.radians(float(w.get("sweep", 0)))) + 0.25 * mac
    lh = max(fus_L - 0.4 * mac - xac, 0.25 * fus_L)
    size = float(d.get("tail_size", 1.0))
    out = []
    if t != "fin":
        Sh = 0.6 * size * S * mac / lh
        bh = math.sqrt(4.0 * Sh)
        crh = 2 * Sh / (bh * 1.6)
        tap = float(d.get("tail_taper", 0.6))
        h = dict(id="htail", role="htail", span=bh, root_chord=crh, tip_chord=crh * tap,
                 sweep=float(d.get("tail_sweep", 25.0 if w.get("sweep", 0) else 15.0)), z="mid", thickness_blocks=2)
        h.update(d.get("htail", {}))
        h.setdefault("x", fus_L - h["root_chord"] - B)
        out.append(h)
    Sv = 0.05 * size * S * b / lh
    hv = math.sqrt(1.4 * Sv)
    crv = 2 * Sv / (hv * 1.6)
    f = dict(id="fin", role="vtail", span=hv, root_chord=crv, tip_chord=crv * float(d.get("fin_taper", 0.6)),
             sweep=float(d.get("fin_sweep", 35.0)), thickness_blocks=int(d.get("fin_thickness", 1)), mirror=False,
             dihedral=90.0)
    f.update(d.get("fin", {}))
    f.setdefault("x", fus_L - f["root_chord"] - B)
    out.append(f)
    return out


def _edge(i, tan):
    """Bresenham offset of an edge at span column i (blocks)."""
    return int(math.floor(i * tan + 0.5))


def build(d, dx, cat):
    """Returns dict(lat, filled, label, comps, surfs, fus) for the clean style."""
    grid = d["grid"]
    B = dz.BLOCK_M[grid]
    f = d["fuselage"]
    L = float(f["length"])
    Wb, Hb = _odd(float(f["width"]) / B), _odd(float(f.get("height", f["width"])) / B)
    a, b = (Wb - 1) // 2, (Hb - 1) // 2
    c = int(round(float(f.get("chamfer", 0.6)) * min(a, b)))
    Lb = int(round(L / B))
    # the tail end section: what the engine cluster needs, else 3 x 3
    te = f.get("tail_end")
    if te is None:
        te = [3, 3]
        for e in d.get("engines", []):
            name = e.get("prefab") or sb.thruster_name(e.get("type", "hydrogen"), e.get("size"), grid)
            ext = cat.extent(name) / sb.GRID_UNIT[grid]
            cl = e.get("cluster", [1, 1])
            te = [max(te[0], _odd(ext[0] * cl[0])), max(te[1], _odd(ext[1] * cl[1]))]
    ae, be = (min(_odd(te[0]), Wb) - 1) // 2, (min(_odd(te[1]), Hb) - 1) // 2
    Nn = int(f.get("nose_rows", max(min(a, b), 0)))      # 45-degree cone down to a 1 x 1 point
    Tn = max(a - ae, b - be)
    z0 = round(float(f.get("z", 0.0)) / B) * B

    w = wing_spec(d)
    tails = tail_specs(d, L, B)
    fus = dz.Fuselage(dict(length=L, width=Wb * B, height=Hb * B, z=z0, nose=Nn / max(Lb, 1), tail=Tn / max(Lb, 1),
                           tail_width=(2 * ae + 1) / Wb, tail_height=(2 * be + 1) / Hb, tail_upsweep=0.0), B)
    surfs = []
    for s in [w] + tails:
        s = dict(s)
        if s.get("role") != "vtail":
            s["dihedral"] = 0.0
        surfs.append(dz.Surface(s, fus, B, dx))

    span_half = max([float(w["span"]) / 2] + [float(t["span"]) / 2 for t in tails if t["role"] != "vtail"])
    fin_h = max([float(t["span"]) for t in tails if t["role"] == "vtail"] or [0.0])
    lo = np.array([-B, -span_half - B, z0 - Hb * B])
    hi = np.array([L + 2 * B, span_half + B, z0 + Hb * B / 2 + fin_h + 2 * B])
    pad = 6
    for e in d.get("engines", []):
        name = e.get("prefab") or sb.thruster_name(e.get("type", "hydrogen"), e.get("size"), grid)
        pad = max(pad, 6 + 2 * int(np.ceil(cat[name].size.max() / sb.GRID_UNIT[grid])))
    lat = vx.Lattice(grid, lo, hi, pad=pad)
    filled = np.zeros(lat.shape, bool)
    label = np.full(lat.shape, -1, np.int16)
    ic = -lat.i0[0]                                  # X index of the centreline
    jc = int(round(z0 / B)) - lat.i0[1]
    k0 = -lat.i0[2]                                  # Z index of the nose (x = 0)

    # --- fuselage
    xs = np.arange(lat.shape[0]) - ic
    ys = np.arange(lat.shape[1]) - jc
    X, Y = np.meshgrid(np.abs(xs), np.abs(ys), indexing="ij")
    for r in range(Lb):
        dn = max(0, Nn - r)
        dt = max(0, r - (Lb - 1 - Tn))
        ar = a - dn
        br = b - dn
        if dt:
            ar, br = max(a - dt, ae), max(b - dt, be)
        diag = a + b - c - max(dn, dt)
        m = (X <= ar) & (Y <= br) & (X + Y <= diag)
        filled[:, :, k0 + r] |= m
        label[:, :, k0 + r][m] = 0

    # --- flat slabs: wing, horizontal tail, fin
    for si, (spec, srf) in enumerate(zip([w] + tails, surfs), start=1):
        vertical = spec.get("role") == "vtail"
        t = int(spec.get("thickness_blocks", 2 if not vertical else 1))
        cr, ct = float(spec["root_chord"]), float(spec.get("tip_chord", spec["root_chord"]))
        k_le0 = int(round(srf.x_le / B)) + k0
        sweep = math.tan(math.radians(float(spec.get("sweep", 0.0))))
        n = max(1, int(round((float(spec["span"]) / 2 if not vertical else float(spec["span"])) / B)))
        te_tan = (srf.x_le + n * B * sweep + ct - (srf.x_le + cr)) / (n * B)
        k_te0 = k_le0 + max(2, int(round(cr / B))) - 1
        if not vertical:
            zz = spec.get("z", "mid")
            if zz == "low":
                jl = jc - b
            elif zz == "high":
                jl = jc + b - t + 1
            elif isinstance(zz, (int, float)):
                jl = int(round(float(zz) / B)) - lat.i0[1] - (t - 1) // 2
            else:
                jl = jc - (t - 1) // 2
            for i in range(0, n + 1):
                le = k_le0 + _edge(i, sweep)
                tr = max(le + 1, k_te0 + _edge(i, te_tan))
                for sgn in ((1, -1) if i else (1,)):
                    ii = ic + sgn * i
                    sl = (ii, slice(jl, jl + t), slice(le, tr + 1))
                    free = label[sl] < 0
                    filled[sl] = True
                    lab = label[sl]
                    lab[free] = si
                    label[sl] = lab
        else:
            j_root = jc + b                                # span counted from the full body's top...
            for jj in range(jc, j_root + n + 1):           # ...but the plate reaches down into the (cone) body
                j = max(0, jj - j_root)
                le = k_le0 + _edge(j, sweep)
                tr = max(le + 1, k_te0 + _edge(j, te_tan))
                sl = (slice(ic - (t - 1) // 2, ic + (t - 1) // 2 + 1), jj, slice(le, tr + 1))
                free = label[sl] < 0
                filled[sl] = True
                lab = label[sl]
                lab[free] = si
                label[sl] = lab
            srf.z0 = (j_root + lat.i0[1]) * B
    return dict(lat=lat, filled=filled, label=label, comps=[fus] + surfs, surfs=surfs, fus=fus)
