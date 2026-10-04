"""com - centre of mass: placing batteries inside the hollow to put the CoM on a target station.

solve() is a greedy selection over candidate slots (single batteries on the centreline, or mirrored pairs):
each step adds the slot whose addition brings the CoM closest to the target, until the CoM is within the
tolerance and the minimum battery count is met.  Heavy slots far forward/aft are taken first when the error is
large; slots near the target fine-tune it.  Slots that overlap a taken one drop out.
"""
from __future__ import annotations

import itertools

import numpy as np


def solve(M0, X0, masses, xs, counts, conflicts, x_t, tol, n_min=0, n_max=10 ** 6):
    """M0, X0: fixed mass and its CoM station.  masses/xs/counts: per slot (counts = batteries in the slot).
    conflicts: list of index arrays (slots overlapping slot i).  Returns (chosen indices, final CoM, ok)."""
    masses = np.asarray(masses, float)
    xs = np.asarray(xs, float)
    counts = np.asarray(counts, int)
    valid = np.ones(len(masses), bool)
    M, MX = float(M0), float(M0 * X0)
    chosen, n = [], 0
    for _ in range(len(masses)):
        x = MX / M
        if (abs(x - x_t) <= tol and n >= n_min) or n >= n_max:
            break
        cand = np.nonzero(valid & (n + counts <= n_max))[0]
        if len(cand) == 0:
            break
        newx = (MX + masses[cand] * xs[cand]) / (M + masses[cand])
        err = np.abs(newx - x_t)
        # prefer, among equally good slots, the lighter one (fewer kg for the same correction)
        k = cand[np.lexsort((masses[cand], np.round(err, 6)))[0]]
        if abs(x - x_t) > tol and abs((MX + masses[k] * xs[k]) / (M + masses[k]) - x_t) >= abs(x - x_t) - 1e-9 \
                and n >= n_min:
            break                                    # nothing improves it any more
        chosen.append(int(k))
        M += masses[k]
        MX += masses[k] * xs[k]
        n += counts[k]
        valid[k] = False
        valid[conflicts[k]] = False
    x = MX / M
    return chosen, x, abs(x - x_t) <= tol and n >= n_min


def boxes_overlap(lo1, hi1, lo2, hi2):
    return bool(np.all(lo1 < hi2) and np.all(lo2 < hi1))


def slot_candidates(lat, free, cat, names, orient, taken_boxes):
    """Battery slots inside the free voxels: each prefab tiled on its own lattice (aligned to the small-block
    lattice, centred on the plane's centreline where its width allows), paired left/right.
    Returns list of dict(name, boxes=[(lo, hi, pos)], mass, centre (cells))."""
    slots = []
    idx = np.argwhere(free)
    if len(idx) == 0:
        return slots
    lo_v = lat.centre_cells(idx.min(0)) - lat.u // 2
    hi_v = lat.centre_cells(idx.max(0)) + lat.u // 2
    for name in names:
        ext = cat.extent(name, orient)
        # lattice offsets: X centred if the half width is odd (box edges on odd cells), else edge on the centreline
        off = []
        for a in range(3):
            if a == 0 and (ext[0] // 2) % 2 == 1:
                off.append(-(ext[0] // 2))
            elif a == 0:
                off.append(1)              # edge on the small-block lattice beside the centreline
            else:
                off.append(None)
        ranges = []
        for a in range(3):
            step = int(ext[a])
            if a == 0:
                base = off[0]
            else:
                base = int(lo_v[a])
                if base % 2 == 0:
                    base += 1
            start = base + int(np.floor((lo_v[a] - base) / step)) * step
            ranges.append(range(start, int(hi_v[a]) - step + 1, step))
        pos_by_lo = {}
        for los in itertools.product(*ranges):
            lo = np.array(los)
            hi = lo + ext
            if lo[0] < 0 and hi[0] > 0 and lo[0] != -hi[0]:
                continue
            a, b = lat.index_range(lo, hi)
            if (a < 0).any() or (b > np.array(lat.shape)).any():
                continue
            if not free[a[0]:b[0], a[1]:b[1], a[2]:b[2]].all():
                continue
            if any(boxes_overlap(lo, hi, tl, th) for tl, th in taken_boxes):
                continue
            pos_by_lo[tuple(lo)] = (lo, hi, cat.pos_for(name, lo, orient))
        mass = cat[name].mass
        seen = set()
        for key, (lo, hi, pos) in pos_by_lo.items():
            if key in seen:
                continue
            if lo[0] == -hi[0]:
                slots.append(dict(name=name, boxes=[(lo, hi, pos)], mass=mass, count=1, centre=(lo + hi) / 2))
                seen.add(key)
                continue
            mk = (-hi[0], lo[1], lo[2])
            if mk not in pos_by_lo or lo[0] < 0:
                continue
            ml, mh, mp = pos_by_lo[mk]
            seen.add(key)
            seen.add(mk)
            c = ((lo + hi) / 2 + (ml + mh) / 2) / 2
            slots.append(dict(name=name, boxes=[(lo, hi, pos), (ml, mh, mp)], mass=2 * mass, count=2, centre=c))
    return slots


def conflicts_of(slots):
    """For each slot, the indices of slots whose boxes overlap it."""
    los, his, owner = [], [], []
    for i, s in enumerate(slots):
        for lo, hi, _ in s["boxes"]:
            los.append(lo)
            his.append(hi)
            owner.append(i)
    if not los:
        return []
    L, H, O = np.array(los), np.array(his), np.array(owner)
    out = []
    for i, s in enumerate(slots):
        hit = np.zeros(len(O), bool)
        for lo, hi, _ in s["boxes"]:
            hit |= np.all(L < hi, 1) & np.all(H > lo, 1)
        o = np.unique(O[hit])
        out.append(o[o != i])
    return out
