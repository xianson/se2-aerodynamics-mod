"""voxel - lattice, coverage voxelisation, connectivity repair, hollowing and skin refinement.

Arrays are indexed [i, j, k] along the grid axes X (design y, right), Y (design z, up), Z (design x, aft).
Voxel (i, j, k) is one block of the design's grid size centred at cells u * (i + i0, j + j0, k + k0),
i.e. at design metres B * (k + k0, i + i0, j + j0) for (x, y, z).
"""
from __future__ import annotations

import itertools
import math

import numpy as np
from scipy import ndimage

import se2blocks as sb

N6 = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)]


class Lattice:
    def __init__(self, grid: str, lo_m, hi_m, pad: int = 3, symmetric: bool = True):
        """lo_m/hi_m: design-frame (x, y, z) bounds in metres to cover."""
        self.grid = grid
        self.u = sb.GRID_UNIT[grid]
        self.B = self.u * sb.CELL_M
        lo = np.array([lo_m[1], lo_m[2], lo_m[0]]) / self.B        # grid order X, Y, Z
        hi = np.array([hi_m[1], hi_m[2], hi_m[0]]) / self.B
        a = np.floor(lo).astype(int) - pad
        b = np.ceil(hi).astype(int) + pad
        if symmetric:
            m = max(abs(a[0]), abs(b[0]))
            a[0], b[0] = -m, m
        self.i0 = a
        self.shape = tuple(int(x) for x in (b - a + 1))

    # index <-> coordinates
    def centre_cells(self, idx):
        return (np.asarray(idx) + self.i0) * self.u

    def centre_m(self, idx):
        c = (np.asarray(idx) + self.i0) * self.B
        return np.stack([c[..., 2], c[..., 0], c[..., 1]], -1)

    def index_of_m(self, p):
        p = np.asarray(p, float)
        g = np.array([p[1], p[2], p[0]]) / self.B
        return np.round(g).astype(int) - self.i0

    def voxel_box(self, idx):
        c = self.centre_cells(idx)
        return c - self.u // 2, c + self.u // 2

    def index_range(self, lo, hi):
        """Voxel index ranges [a, b) touched by the cell box [lo, hi)."""
        h = self.u / 2
        a = np.floor((np.asarray(lo) + h) / self.u).astype(int) - self.i0
        b = np.ceil((np.asarray(hi) + h) / self.u).astype(int) - self.i0
        return a, b

    def covers_exactly(self, lo, hi):
        """True if the cell box is exactly a union of whole voxels."""
        h = self.u // 2
        return all(((np.asarray(v) + h) % self.u == 0).all() for v in (lo, hi))

    def sample_axes(self, s=3):
        """Sub-sample coordinates (metres) along X, Y, Z: s points per voxel."""
        off = (np.arange(s) - (s - 1) / 2) / s * self.B
        axes = []
        for ax in range(3):
            c = (np.arange(self.shape[ax]) + self.i0[ax]) * self.B
            axes.append((c[:, None] + off[None, :]).ravel())
        return axes


def coverage_implicit(lat: Lattice, comps, s=3, symmetric=True):
    """Per-component and union coverage fractions from implicit inside(x, y, z) functions."""
    X, Y, Z = lat.sample_axes(s)
    y, z, x = np.meshgrid(X, Y, Z, indexing="ij")
    nx, ny, nz = lat.shape
    union = np.zeros(y.shape, bool)
    covs = []
    for c in comps:
        m = c.inside(x, y, z)
        union |= m
        covs.append(m.reshape(nx, s, ny, s, nz, s).mean(axis=(1, 3, 5)).astype(np.float32))
    cu = union.reshape(nx, s, ny, s, nz, s).mean(axis=(1, 3, 5)).astype(np.float32)
    if symmetric:
        cu = (cu + cu[::-1]) / 2
        covs = [(c + c[::-1]) / 2 for c in covs]
    return cu, covs


def rescue_thin(cov, threshold=0.5, low=0.12, column=0.4):
    """Plates thinner than a block (mesh wings, fins) straddle voxels with coverage below the threshold.  Along each
    axis, a voxel holding the most coverage of its 3-voxel column is kept when that column adds up to >= `column`
    blocks of material."""
    keep = cov >= threshold
    for ax in range(3):
        d = [0, 0, 0]
        d[ax] = 1
        prev, nxt = _shift(cov, [-x for x in d], 0), _shift(cov, d, 0)
        nxt2 = _shift(cov, [2 * x for x in d], 0)
        # coverage must FALL on both sides along the thin axis (a plate's in-plane axes are flat); an even split
        # over two layers keeps the lower one
        peak = (cov > prev) & ((cov > nxt) | ((cov == nxt) & (nxt2 < nxt)))
        keep |= peak & (cov >= low) & (prev + cov + nxt >= column)
    return keep


def labels_from(covs, filled):
    if not covs:
        return np.full(filled.shape, -1, np.int16)
    st = np.stack(covs, 0)
    lab = st.argmax(0).astype(np.int16)          # ties go to the first component (the fuselage)
    lab[~filled] = -1
    return lab


def _shift(a, d, fill=False):
    """a shifted so that out[v] = a[v + d]."""
    out = np.full_like(a, fill)
    src = [slice(None)] * 3
    dst = [slice(None)] * 3
    for ax, k in enumerate(d):
        if k > 0:
            src[ax], dst[ax] = slice(k, None), slice(None, -k)
        elif k < 0:
            src[ax], dst[ax] = slice(None, k), slice(-k, None)
    out[tuple(dst)] = a[tuple(src)]
    return out


def neighbours_all(m):
    r = np.ones_like(m)
    for d in N6:
        r &= _shift(m, d)
    return r


def fix_connectivity(filled, cov, max_iter=8):
    """Fill one voxel wherever two filled voxels touch only along an edge (dihedral staircases), so that every
    surface is face-connected (a grid needs face contact)."""
    filled = filled.copy()
    for _ in range(max_iter):
        added = 0
        for a in range(3):
            for b in range(a + 1, 3):
                for sa in (1, -1):
                    for sbb in (1, -1):
                        da = [0, 0, 0]; da[a] = sa
                        db = [0, 0, 0]; db[b] = sbb
                        dab = [da[i] + db[i] for i in range(3)]
                        diag = filled & _shift(filled, dab)
                        A = _shift(filled, da)
                        Bm = _shift(filled, db)
                        need = diag & ~A & ~Bm
                        if not need.any():
                            continue
                        ca, cb = _shift(cov, da, 0), _shift(cov, db, 0)
                        pick_a = need & (ca >= cb)
                        pick_b = need & (ca < cb)
                        # write into the neighbour positions
                        filled |= _shift(pick_a, [-x for x in da])
                        filled |= _shift(pick_b, [-x for x in db])
                        added += int(need.sum())
        if added == 0:
            break
    return filled


def largest_component(filled):
    lab, n = ndimage.label(filled)
    if n <= 1:
        return filled, []
    sizes = ndimage.sum(filled, lab, range(1, n + 1))
    keep = int(np.argmax(sizes)) + 1
    dropped = [int(s) for i, s in enumerate(sizes) if i + 1 != keep]
    return lab == keep, dropped


def run_lengths(m, axis):
    a = np.moveaxis(m, axis, 0).astype(np.int32)
    f = np.zeros_like(a)
    b = np.zeros_like(a)
    for i in range(a.shape[0]):
        f[i] = (f[i - 1] + 1) * a[i] if i else a[i]
    for i in range(a.shape[0] - 1, -1, -1):
        b[i] = (b[i + 1] + 1) * a[i] if i < a.shape[0] - 1 else a[i]
    r = np.where(a > 0, f + b - 1, 0)
    return np.moveaxis(r, 0, axis)


def thickness(filled):
    return np.minimum(np.minimum(run_lengths(filled, 0), run_lengths(filled, 1)), run_lengths(filled, 2))


def neighbours26_all(m):
    r = m.copy()
    for d in itertools.product((-1, 0, 1), repeat=3):
        if d != (0, 0, 0):
            r &= _shift(m, d)
    return r


def hollow(filled, solid_below=3):
    """Keep a 1-block shell; parts thinner than solid_below blocks stay solid.  A voxel is interior only when all
    26 neighbours are filled, so the shell stays FACE-connected where the surface steps diagonally (with the
    6-neighbour rule a keel row would hang on edges only).  Returns (shell, cavity)."""
    interior = filled & neighbours26_all(filled)
    remove = interior & (thickness(filled) >= solid_below)
    return filled & ~remove, remove


def outside(filled_like):
    """Empty voxels face-connected to the array boundary."""
    empty = ~filled_like
    lab, n = ndimage.label(empty)
    border = set(np.unique(np.concatenate([lab[0].ravel(), lab[-1].ravel(), lab[:, 0].ravel(), lab[:, -1].ravel(),
                                           lab[:, :, 0].ravel(), lab[:, :, -1].ravel()])))
    border.discard(0)
    return np.isin(lab, list(border))


def refine_skin(shell, cavity, out, cov, allowed, frozen):
    """Marching-style choice of slope / corner / inverted corner for outer shell voxels.
    shell: solid voxels; cavity: hollow interior; out: exterior empties; allowed: voxels that may change;
    frozen: voxels whose neighbours must stay cubes (next to engines, hinges...).
    Returns dict idx -> (shape, normal(grid int vector))."""
    g = np.stack(np.gradient(cov.astype(np.float64)), -1)
    res = {}
    near_frozen = np.zeros_like(shell)
    for d in N6:
        near_frozen |= _shift(frozen, d)
    cand = shell & allowed & ~frozen & ~near_frozen
    ext = {d: _shift(out, d) for d in N6}
    sol = {d: _shift(shell, d) for d in N6}
    cav = {d: _shift(cavity, d) for d in N6}
    for idx in zip(*np.nonzero(cand)):
        E = [d for d in N6 if ext[d][idx]]
        if len(E) not in (2, 3):
            continue
        n = np.sum(np.array(E), 0)
        if len(E) == 2 and np.count_nonzero(n) != 2:
            continue
        if len(E) == 3 and np.count_nonzero(n) != 3:
            continue
        # the faces opposite the open ones must be solid shell (not cavity)
        if any(not sol[tuple(-x for x in d)][idx] or cav[tuple(-x for x in d)][idx] for d in E):
            continue
        ge = -g[idx]
        ng = np.linalg.norm(ge)
        if ng < 1e-6:
            continue
        cosang = float(ge @ n) / (ng * np.linalg.norm(n))
        c = float(cov[idx])
        if len(E) == 2:
            free_ax = int(np.nonzero(n == 0)[0][0])
            ok = True
            for s_ in (1, -1):
                d = [0, 0, 0]; d[free_ax] = s_
                d = tuple(d)
                if not sol[d][idx] or cav[d][idx]:
                    ok = False
            if ok and cosang > 0.75 and c < 0.9:
                res[idx] = ("slope", tuple(int(v) for v in n))
        else:
            if cosang > 0.75 and c < 0.95:
                res[idx] = ("corner" if c < 0.6 else "invcorner", tuple(int(v) for v in n))
    return res
