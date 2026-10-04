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


def coverage_implicit(lat: Lattice, comps, s=3, symmetric=True, chunk=6):
    """Per-component and union coverage fractions from implicit inside(x, y, z) functions.  Evaluated in slabs of
    `chunk` voxels along Z, each component only inside its own bounding box (memory stays bounded at small grid)."""
    X, Y, Z = lat.sample_axes(s)
    nx, ny, nz = lat.shape
    covs = [np.zeros(lat.shape, np.float32) for _ in comps]
    cu = np.zeros(lat.shape, np.float32)
    boxes = []
    for c in comps:
        lo, hi = c.bbox()
        a = lat.index_of_m(lo) - 1
        b = lat.index_of_m(hi) + 2
        a = np.maximum(a, 0)
        b = np.minimum(b, np.array(lat.shape))
        boxes.append((a, b))
    for k0 in range(0, nz, chunk):
        k1 = min(nz, k0 + chunk)
        act = [i for i, (a, b) in enumerate(boxes) if a[2] < k1 and b[2] > k0 and (a < b).all()]
        if not act:
            continue
        ia = min(boxes[i][0][0] for i in act); ib = max(boxes[i][1][0] for i in act)
        ja = min(boxes[i][0][1] for i in act); jb = max(boxes[i][1][1] for i in act)
        union = np.zeros(((ib - ia) * s, (jb - ja) * s, (k1 - k0) * s), bool)
        for i in act:
            a, b = boxes[i]
            i0, i1 = max(a[0], ia), min(b[0], ib)
            j0, j1 = max(a[1], ja), min(b[1], jb)
            kk0, kk1 = max(a[2], k0), min(b[2], k1)
            if i0 >= i1 or j0 >= j1 or kk0 >= kk1:
                continue
            y, z, x = np.meshgrid(X[i0 * s:i1 * s], Y[j0 * s:j1 * s], Z[kk0 * s:kk1 * s], indexing="ij")
            m = comps[i].inside(x, y, z)
            union[(i0 - ia) * s:(i1 - ia) * s, (j0 - ja) * s:(j1 - ja) * s, (kk0 - k0) * s:(kk1 - k0) * s] |= m
            covs[i][i0:i1, j0:j1, kk0:kk1] = m.reshape(i1 - i0, s, j1 - j0, s, kk1 - kk0, s).mean(axis=(1, 3, 5))
        cu[ia:ib, ja:jb, k0:k1] = union.reshape(ib - ia, s, jb - ja, s, k1 - k0, s).mean(axis=(1, 3, 5))
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


def hollow(filled, solid_below=3, only=None):
    """Keep a 1-block shell; parts thinner than solid_below blocks stay solid.  A voxel is interior only when all
    26 neighbours are filled, so the shell stays FACE-connected where the surface steps diagonally (with the
    6-neighbour rule a keel row would hang on edges only).  Returns (shell, cavity)."""
    interior = filled & neighbours26_all(filled)
    remove = interior & (thickness(filled) >= solid_below)
    if only is not None:
        remove &= only                     # e.g. hollow the body and nacelles, keep wings and tails solid
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


def halfslope_fill(solid, cubes, out, cov, forbid, lo=0.18, hi=0.5):
    """Half-height wedges in the inside corners of the outer staircase: an exterior voxel with partial coverage
    that rests on a cube below (direction -a) and leans on a cube riser (direction -b) gets a wedge whose outward
    normal is 2a + b.  The blocks are new (they fill air), so the shell's seal is untouched."""
    g = np.stack(np.gradient(cov.astype(np.float64)), -1)
    res = {}
    cand = out & ~forbid & (cov >= lo) & (cov < hi)
    sup = {d: _shift(cubes, [-x for x in d]) for d in N6}     # sup[d][v] = cubes[v - d]
    air = {d: _shift(out, list(d)) for d in N6}               # air[d][v] = out[v + d]
    for idx in zip(*np.nonzero(cand)):
        best = None
        ge = -g[idx]
        ng = np.linalg.norm(ge)
        if ng < 1e-6:
            continue
        for a in N6:
            if not sup[a][idx] or not air[a][idx]:
                continue
            for b in N6:
                if np.dot(a, b) != 0 or not sup[b][idx] or not air[b][idx]:
                    continue
                n = 2 * np.array(a) + np.array(b)
                cs = float(ge @ n) / (ng * np.linalg.norm(n))
                if cs > 0.6 and (best is None or cs > best[0]):
                    best = (cs, tuple(int(x) for x in n))
        if best:
            res[idx] = ("halfslope", best[1])
    return res


def refine_clean(shell, cavity, out, allowed, frozen, span_axis, tip=None):
    """Pattern-only skin for the clean style.
    Body: a voxel with two exposed perpendicular faces becomes a slope (normal = their sum, plane through the voxel
    centre: a 45-degree staircase reads as one flat face); in the cones every ring voxel faces forward (or aft) with
    one consistent slope, so each step is a ring of slopes.
    Plates (span_axis >= 0): only the leading edge (exposed forward) and the tips are sloped - the top, bottom and
    trailing edge stay flat."""
    res = {}
    near_frozen = np.zeros_like(shell)
    for d in N6:
        near_frozen |= _shift(frozen, d)
    cand = shell & allowed & ~frozen & ~near_frozen
    ext = {d: _shift(out, d) for d in N6}
    sol = {d: _shift(shell | ~out, d) for d in N6}       # anything but exterior air: shell, equipment, the hollow
    cav = {d: _shift(cavity, d) for d in N6}
    tip = np.zeros_like(shell) if tip is None else tip
    for idx in zip(*np.nonzero(cand)):
        sa = int(span_axis[idx])
        E = [d for d in N6 if ext[d][idx]]
        if sa >= 0:
            es = [d for d in E if d[sa] != 0]
            fwd = (0, 0, -1) in E
            vert = [d for d in E if d[sa] == 0 and d[2] == 0]
            if fwd and len(es) == 1:
                n = tuple(a + b for a, b in zip(es[0], (0, 0, -1)))      # swept edge / tip corner: in the plane
            elif fwd and len(vert) == 1:
                n = tuple(a + b for a, b in zip(vert[0], (0, 0, -1)))    # leading-edge wedge
            elif tip[idx] and len(es) == 1 and len(vert) == 1 and (0, 0, 1) not in E:
                n = tuple(a + b for a, b in zip(es[0], vert[0]))         # rounded tip edge
            else:
                continue
            nb = [tuple(-int(np.sign(v)) if k == j else 0 for k in range(3)) for j, v in enumerate(n) if v]
            if all(sol[d][idx] for d in nb):
                res[idx] = ("slope", tuple(int(v) for v in n))
            continue
        if len(E) not in (2, 3):
            continue
        n = np.sum(np.array(E), 0)
        if np.count_nonzero(n) != len(E):
            continue
        back = [tuple(-x for x in d) for d in E]
        if len(E) == 2:
            if any(not sol[d][idx] for d in back):
                continue
            free_ax = int(np.nonzero(n == 0)[0][0])
            ok = True
            for s_ in (1, -1):
                d = [0, 0, 0]
                d[free_ax] = s_
                if not sol[tuple(d)][idx] or cav[tuple(d)][idx]:
                    ok = False                  # a slope's triangular sides must not open onto the hollow
            if ok:
                res[idx] = ("slope", tuple(int(v) for v in n))
        elif n[2] != 0:
            # a cone ring's chamfer voxel: the same forward/aft-facing slope as its ring neighbours, toward whichever
            # side (X or Y) the ring is wider on, so each step of the cone is one ring of slopes
            for cand_n in ((int(n[0]), 0, int(n[2])), (0, int(n[1]), int(n[2]))):
                nb = [tuple(-int(np.sign(v)) if k == j else 0 for k in range(3)) for j, v in enumerate(cand_n) if v]
                if all(sol[d][idx] and not cav[d][idx] for d in nb):
                    res[idx] = ("slope", cand_n)
                    break
    return res


def bridges(n, rows, cols):
    """Bridges of an undirected graph (n nodes, edge lists): [(u, v, size of v's side)], iterative Tarjan."""
    adj = [[] for _ in range(n)]
    seen = set()
    for a, b in zip(list(rows), list(cols)):
        a, b = int(a), int(b)
        if a == b or (min(a, b), max(a, b)) in seen:
            continue
        seen.add((min(a, b), max(a, b)))
        adj[a].append(b)
        adj[b].append(a)
    disc = [-1] * n
    low = [0] * n
    size = [1] * n
    t = 0
    out = []
    for root in range(n):
        if disc[root] >= 0:
            continue
        disc[root] = low[root] = t
        t += 1
        stack = [(root, -1, iter(adj[root]))]
        while stack:
            u, parent, it = stack[-1]
            w = next(it, None)
            if w is None:
                stack.pop()
                if stack:
                    p = stack[-1][0]
                    low[p] = min(low[p], low[u])
                    size[p] += size[u]
                    if low[u] > disc[p]:
                        out.append((p, u, size[u]))
                continue
            if w == parent:
                continue
            if disc[w] >= 0:
                low[u] = min(low[u], disc[w])
            else:
                disc[w] = low[w] = t
                t += 1
                stack.append((w, u, iter(adj[w])))
    return out
