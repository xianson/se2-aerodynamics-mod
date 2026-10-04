"""classify - find the lifting surfaces of a voxelised mesh (no part names in an OBJ/STL).

Thin horizontal parts (at most `thin` blocks tall, wider than that) are wings / tailplanes / canards; thin
vertical parts are fins.  The biggest horizontal component (with its mirror partner) is the wing; horizontal
components aft of it are the tailplane, ahead of it canards.  Each becomes a VoxelSurface carrying the same
attributes as a parametric Surface, so the neutral-point and control-surface code works unchanged.
"""
from __future__ import annotations

import math

import numpy as np
from scipy import ndimage

from voxel import run_lengths


class VoxelSurface:
    def __init__(self, sid, role, mirror, vertical, strips, span, z0, x_le, cr, cols):
        self.id, self.role, self.mirror, self.vertical = sid, role, mirror, vertical
        self.G = math.pi / 2 if vertical else 0.0
        self._strips = strips
        self.span, self.z0, self.x_le, self.cr = span, z0, x_le, cr
        self.spec = {}
        self.moves = False
        self.cols = cols

    def strips(self):
        return self._strips


class VoxelFuselage:
    def __init__(self, L, W):
        self.L, self.W = L, W
        self.id = self.role = "fuselage"


def classify(filled, lat, thin=2, min_vox=6):
    """Returns (label array: 0 fuselage, k>0 surface k-1, -1 empty; list of VoxelSurface; VoxelFuselage)."""
    B = lat.B
    rz = run_lengths(filled, 1)
    rx = run_lengths(filled, 0)
    horiz = filled & (rz <= thin) & (rx > thin)
    vert = filled & (rx <= thin) & (rz > thin) & ~horiz
    # a panel is at least 2 blocks wide along its span; a fuselage's side chine or top ridge is 1
    horiz &= run_lengths(horiz, 0) >= 2
    vert &= run_lengths(vert, 1) >= 2
    label = np.where(filled, 0, -1).astype(np.int16)
    parts = []
    for mask, vertical in ((horiz, False), (vert, True)):
        lab, n = ndimage.label(mask)
        for p in range(1, n + 1):
            idx = np.argwhere(lab == p)
            if len(idx) < min_vox:
                continue
            # a real fin (wing panel) extends at least 3 (2) blocks along its span while staying thin; a fuselage's
            # one-voxel top ridge or chine does not
            span_ax = 1 if vertical else 0
            if idx[:, span_ax].max() - idx[:, span_ax].min() + 1 < (3 if vertical else 2):
                continue
            m = lat.centre_m(idx)
            plan = len({(a, c) for a, b, c in idx}) if not vertical else len({(b, c) for a, b, c in idx})
            parts.append(dict(idx=idx, vertical=vertical, area=plan * B * B, c=m.mean(0)))
    hz = [p for p in parts if not p["vertical"]]
    surfs = []
    if hz:
        main = max(hz, key=lambda p: p["area"])
        used = set()

        def partner(p):
            best = None
            for j, q in enumerate(parts):
                if q is p or id(q) in used or q["vertical"] != p["vertical"]:
                    continue
                if abs(q["c"][1] + p["c"][1]) < 2 * B and abs(q["c"][0] - p["c"][0]) < 2 * B and \
                        abs(q["c"][2] - p["c"][2]) < 2 * B and abs(p["c"][1]) > B:
                    best = q
            return best

        groups = []
        for p in sorted(parts, key=lambda p: -p["area"]):
            if id(p) in used:
                continue
            used.add(id(p))
            q = partner(p)
            if q is not None:
                used.add(id(q))
            groups.append([p] + ([q] if q is not None else []))
        wing_x = main["c"][0]
        counts = {}
        for g in groups:
            p = g[0]
            if p["vertical"]:
                role = "vtail"
            elif any(q is main for q in g):
                role = "wing"
            else:
                role = "htail" if p["c"][0] > wing_x else "canard"
            counts[role] = counts.get(role, 0) + 1
            sid = role if counts[role] == 1 else f"{role}{counts[role]}"
            idx = np.concatenate([q["idx"] for q in g])
            k = len(surfs) + 1
            label[tuple(idx.T)] = k
            surfs.append(_surface(sid, role, g, lat))
    fus = filled & (label == 0)
    if fus.any():
        fi = np.argwhere(fus)
        L = (fi[:, 2].max() - fi[:, 2].min() + 1) * B
        # width: equivalent diameter of the largest cross-section (merged wing roots barely move it)
        area = fus.sum(axis=(0, 1)).max() * B * B
        W = 2 * np.sqrt(area / np.pi)
    else:
        L = W = 0.0
    return label, surfs, VoxelFuselage(L, W)


def _surface(sid, role, group, lat):
    B = lat.B
    vertical = group[0]["vertical"]
    mirror = len(group) == 2
    p = group[0] if mirror and group[0]["c"][1] > 0 else (group[1] if mirror else group[0])
    idx = p["idx"]
    span_ax = 1 if vertical else 0
    cols = {}
    for v in idx:
        cols.setdefault(int(v[span_ax]), []).append(int(v[2]))
    ks = sorted(cols, key=lambda k: abs(k + lat.i0[span_ax]))
    coord = np.array([(k + lat.i0[span_ax]) * B for k in ks])
    chord = np.array([(max(cols[k]) - min(cols[k]) + 1) * B for k in ks])
    xle = np.array([(min(cols[k]) + lat.i0[2]) * B - B / 2 for k in ks])
    if vertical:
        root = coord.min() - B / 2
        s = coord - root
        z0 = root
    else:
        s = np.abs(coord)
        z0 = float(lat.centre_m(idx).mean(0)[2])
        # carry-through over the body: extend the root strip to the centreline
        r = s.min() - B / 2
        if mirror and r > 0:
            n = int(round(r / B))
            s = np.concatenate([(np.arange(n) + 0.5) * B, s])
            chord = np.concatenate([np.full(n, chord[0]), chord])
            xle = np.concatenate([np.full(n, xle[0]), xle])
    span = float((s.max() + B / 2) * (2 if mirror and not vertical else 1))
    return VoxelSurface(sid, role, mirror and not vertical, vertical, (s, B, chord, xle), span, z0, float(xle[0]),
                        float(chord[0]), cols)
