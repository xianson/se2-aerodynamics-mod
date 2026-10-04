"""Writes the test meshes: simple_plane.obj (Y-up file, nose toward -Z, separate overlapping closed shells, written
with quads) and delta_jet.stl (binary, design axes x aft / y right / z up).  Run by 'gridgen.py all'."""
from __future__ import annotations

import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
import meshio  # noqa: E402


def loft(rings, cap=True):
    """Closed shell through polygon rings (each (k, 3)), consecutive rings joined by quads, ends capped."""
    tris = []
    k = len(rings[0])
    for a, b in zip(rings[:-1], rings[1:]):
        for i in range(k):
            j = (i + 1) % k
            tris.append([a[i], b[i], b[j]])
            tris.append([a[i], b[j], a[j]])
    if cap:
        for ring, flip in ((rings[0], True), (rings[-1], False)):
            c = ring.mean(0)
            for i in range(k):
                j = (i + 1) % k
                t = [c, ring[i], ring[j]]
                tris.append(t[::-1] if flip else t)
    t = np.array(tris, float)
    if meshio.signed_volume(t) < 0:
        t = t[:, ::-1]
    return t


def body(L, R, H, n=20, stations=28, nose=0.22, tail=0.35, tail_r=0.25, up=0.4):
    """Fuselage in design axes (x aft)."""
    rings = []
    for x in np.linspace(0.02 * L, L, stations):
        f = 1.0
        if x < nose * L:
            f = np.sqrt(1 - (1 - x / (nose * L)) ** 2)
        zc = 0.0
        if x > (1 - tail) * L:
            g = (x - (1 - tail) * L) / (tail * L)
            f = 1 - (1 - tail_r) * g
            zc = up * H * g ** 1.5
        a = np.linspace(0, 2 * np.pi, n, endpoint=False)
        rings.append(np.stack([np.full(n, x), R * f * np.cos(a), zc + H * f * np.sin(a)], 1))
    return loft(rings)


def section(x_le, chord, y, z, t, vertical=False):
    """A 6-point airfoil section (closed polygon) at span station y (or height z for a fin)."""
    pts = [(0, 0), (0.25, t / 2), (0.7, t * 0.35), (1, 0), (0.7, -t * 0.35), (0.25, -t / 2)]
    out = []
    for u, w in pts:
        if vertical:
            out.append((x_le + u * chord, y + w * chord, z))
        else:
            out.append((x_le + u * chord, y, z + w * chord))
    return np.array(out, float)


def surface(x_le, cr, ct, span, sweep, z, t=0.14, vertical=False, y0=0.0):
    tan = np.tan(np.radians(sweep))
    if vertical:
        rings = [section(x_le, cr, y0, z, t, True), section(x_le + span * tan, ct, y0, z + span, t, True)]
    else:
        h = span / 2
        rings = [section(x_le + h * tan, ct, -h, z, t), section(x_le, cr, 0.0, z, t), section(x_le + h * tan, ct, h, z, t)]
    return loft(rings)


def simple_plane():
    parts = [("fuselage", body(40, 2.6, 2.6)),
             ("wing", surface(16.5, 7, 3, 36, 12, -1.0)),
             ("htail", surface(34.5, 3.6, 2.2, 13, 18, 0.6)),
             ("fin", surface(33.5, 4.8, 2.4, 6.5, 35, 1.8, vertical=True))]
    tris, groups = [], []
    for name, t in parts:
        groups.append((name, sum(len(x) for x in tris), len(t)))
        tris.append(t)
    t = np.concatenate(tris)
    # design (x aft, y right, z up) -> file (X right, Y up, Z forward-is-minus): X = y, Y = z, Z = x
    f = np.stack([t[..., 1], t[..., 2], t[..., 0]], -1)
    return f, groups


def delta_jet():
    parts = [body(16, 1.3, 1.2, nose=0.3, tail=0.15, tail_r=0.7, up=0.0),
             surface(6.0, 8.0, 0.6, 10.0, 50, -0.2, t=0.06),
             surface(12.0, 3.4, 1.0, 3.2, 50, 0.9, t=0.08, vertical=True)]
    return np.concatenate(parts)


def main():
    f, groups = simple_plane()
    meshio.write_obj(os.path.join(HERE, "simple_plane.obj"), f, groups)
    meshio.write_stl(os.path.join(HERE, "delta_jet.stl"), delta_jet())


if __name__ == "__main__":
    main()
