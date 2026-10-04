"""validate - offline checks of a written spec (re-read from disk, rebuilt through the se2blocks adapter only).

  prefabs      every Guid known; H lines point at hinge blocks; every hinge carries flap blocks
  overlap      no two main-grid blocks share a cell; no flap block overlaps the main grid, a head, or another flap
  attached     each hinge's flap blocks + head are one face-connected piece; flaps on the measured mount side (+Y)
  hinge_line   hinge axis along the segment's span axis; a flap block directly aft of the head at the 90 deg
               neutral (leading edge on the hinge line); limits inside 0..180 around the neutral; the hinge block
               touches the main grid
  sweep        rotated from its spawn pose (hinge angle 0) through the 90 deg neutral to its far limit, no flap point enters a main-grid block (other than its own hinge)
               or another hinge's swept cells
  engines      exhaust lanes (and atmospheric intakes) free of blocks and of flap sweeps
  connected    the main grid is one piece through MOUNT faces (both blocks mount on the shared face); each
               hinge's head grid likewise (in "attached")
  shell        the hollow is sealed: no cavity voxel reachable from outside; no slope/corner exposes an open face
               to the cavity
  com          CoM recomputed from the spec equals the sidecar's, is within tolerance of the target, centred
  power        at least one battery when there are hinges
"""
from __future__ import annotations

import itertools
import math

import numpy as np
from scipy import ndimage
from scipy.sparse import coo_matrix
from scipy.sparse.csgraph import connected_components

import se2blocks as sb
import spec as specio
import voxel as vx


def _rot(axis, ang):
    a = np.asarray(axis, float)
    a = a / np.linalg.norm(a)
    K = np.array([[0, -a[2], a[1]], [a[2], 0, -a[0]], [-a[1], a[0], 0]])
    return np.eye(3) + math.sin(ang) * K + (1 - math.cos(ang)) * K @ K


class Grid:
    """Cell-resolution owner grid."""

    def __init__(self, boxes, pad=2):
        lo = np.min([b[0] for b in boxes], 0) - pad
        hi = np.max([b[1] for b in boxes], 0) + pad
        self.o = lo.astype(int)
        self.own = np.full(tuple((hi - lo).astype(int)), -1, np.int32)

    def sl(self, lo, hi, clip=False):
        a = np.asarray(lo, int) - self.o
        b = np.asarray(hi, int) - self.o
        if clip:
            a = np.maximum(a, 0)
            b = np.minimum(b, self.own.shape)
            if (a >= b).any():
                return None
        return tuple(slice(int(x), int(y)) for x, y in zip(a, b))

    def lookup(self, pts):
        c = np.floor(pts).astype(int) - self.o
        ok = np.all((c >= 0) & (c < np.array(self.own.shape)), 1)
        out = np.full(len(pts), -1, np.int32)
        out[ok] = self.own[tuple(c[ok].T)]
        return out, c


def _samples(lo, hi, n=7, shrink=0.998):
    lo, hi = np.asarray(lo, float), np.asarray(hi, float)
    c, h = (lo + hi) / 2, (hi - lo) / 2 * shrink
    g = (np.arange(n) + 0.5) / n * 2 - 1
    s = np.stack(np.meshgrid(g, g, g, indexing="ij"), -1).reshape(-1, 3)
    return c + s * h


MARGIN_LAST = []


def connection_margin(n, r, c, names, partial_edges=()):
    """Bridges of the mount graph: faces whose loss would split the grid.  Short text: how many pieces of 2+ blocks
    hang by a single mount face (the weakest listed by size and the block on the far side), and how many single blocks
    hang by one face (a lone tip or LE block, a hinge on its one mount)."""
    bridges = vx.bridges(n, r, c)
    pieces = sorted(((min(s_, n - s_), u, v) for u, v, s_ in bridges), reverse=True)
    via = sorted({names[k][0] for p, u, v in pieces if p > 1 and (min(u, v), max(u, v)) in partial_edges
                  for k in (u, v)})
    nvia = sum(1 for p, u, v in pieces if p > 1 and (min(u, v), max(u, v)) in partial_edges)
    big = [p for p in pieces if p[0] > 1]
    single = sum(1 for p in pieces if p[0] == 1)
    ex = "; ".join(f"{p}@{names[v][0]}{list(int(x) for x in names[v][1])}" for p, u, v in big[:3])
    MARGIN_LAST.clear()
    MARGIN_LAST.extend(pieces)
    return (f"margin: min 1 path for {len(big)} pieces of 2+ blocks (largest {big[0][0] if big else 0}"
            f"{': ' + ex if ex else ''}; {nvia} of those joints through a partial face{' ' + str(via) if via else ''}),"
            f" {single} single blocks hanging by one face"
            if pieces else "margin: every piece has 2+ independent mount paths")


def validate(spec_path, side, cat):
    checks = {}

    def rec(name, ok, detail=""):
        checks[name] = dict(ok=bool(ok), detail=detail)

    try:
        sp = specio.parse(spec_path, cat)
        rec("prefabs", True)
    except KeyError as e:
        rec("prefabs", False, f"unknown prefab {e}")
        return dict(ok=False, checks=checks)
    main = sp["main"]
    boxes = [cat.box(n, p, o) for n, p, o in main]
    hinge_name = {"large": "Hinge250", "small": "Hinge50"}[side["grid"]]
    bad = [i for i, _, _ in sp["hinges"] if main[i][0] not in ("Hinge250", "Hinge50")]
    empty = [k for k in range(len(sp["hinges"])) if not sp["flaps"].get(k)]
    if bad or empty:
        rec("prefabs", False, f"H lines on non-hinges {bad}, hinges without flaps {empty}")

    # ---- flap boxes in the main frame
    hinges = []
    allboxes = list(boxes)
    for k, (bi, mn, mx) in enumerate(sp["hinges"]):
        hname, hpos, horient = main[bi]
        head = "HingeHead" + hname[len("Hinge"):]
        origin, Rh = sb.headgrid_frame(hpos, horient)
        head_box = cat.box(head, origin, sb.orient_names(Rh))
        flaps = []
        forients = []
        for fname, fpos, forient in sp["flaps"].get(k, []):
            p, o = sb.from_headgrid(fpos, forient, origin, Rh)
            flaps.append((fname, fpos, cat.box(fname, p, o)))
            forients.append(forient)
        axis, pivot = sb.hinge_axis_and_pivot(hpos, horient)
        neu = sb.CAL["hinge_neutral_deg"]
        hinges.append(dict(k=k, bi=bi, head=head, head_box=head_box, flaps=flaps, axis=axis, pivot=pivot,
                           lim=(mn - neu, mx - neu), abs_lim=(mn, mx), origin=origin, Rh=Rh, forients=forients))
        allboxes += [head_box] + [f[2] for f in flaps]

    # ---- main-grid overlaps
    G = Grid(allboxes)
    over = []
    for i, (lo, hi) in enumerate(boxes):
        s = G.sl(lo, hi)
        o = G.own[s]
        if (o >= 0).any():
            over.append((int(np.unique(o[o >= 0])[0]), i))
        G.own[s] = i
    rec("overlap", not over, f"{len(over)} main overlaps e.g. {over[:3]}" if over else "")

    # ---- flap overlaps / attachment / mount side / hinge line
    fover, detached, wrongside, notline, loose = [], [], [], [], []
    taken = np.zeros(G.own.shape, bool)
    mount = np.array(sb.CAL["flap_mount_local"])
    u = sb.GRID_UNIT[side["grid"]]
    span_axes = {h["hinge"]: h["span_axis"] for h in side.get("hinges", [])}
    for h in hinges:
        pieces = [h["head_box"]] + [f[2] for f in h["flaps"]]
        for j, (lo, hi) in enumerate(pieces):
            s = G.sl(lo, hi)
            o = G.own[s]
            if j > 0 and ((o >= 0).any() or taken[s].any()):
                fover.append(h["k"])
            if j == 0 and ((o >= 0) & (o != h["bi"])).any():
                fover.append(h["k"])
        for lo, hi in pieces[1:]:
            taken[G.sl(lo, hi)] = True
        # connectivity among the head grid's pieces through mount faces (the head mounts flaps on its +Y only)
        names = [(h["head"], sb.orient_names(h["Rh"]))]
        for fi, (fname, fpos, _) in enumerate(h["flaps"]):
            names.append((fname, sb.orient_names(h["Rh"] @ sb.orient_matrix(*h["forients"][fi]))))
        n = len(pieces)
        adj = np.zeros((n, n), bool)
        for a, b in itertools.combinations(range(n), 2):
            (l1, h1), (l2, h2) = pieces[a], pieces[b]
            touch = np.minimum(h1, h2) - np.maximum(l1, l2)
            if (touch >= 0).all() and (touch == 0).sum() == 1:
                ax = int(np.nonzero(touch == 0)[0][0])
                d = [0, 0, 0]
                d[ax] = 1 if (np.asarray(l2)[ax] >= np.asarray(h1)[ax]) else -1
                if sb.joins(names[a][0], names[a][1], names[b][0], names[b][1], tuple(d)):
                    adj[a, b] = adj[b, a] = True
        ncomp, _ = connected_components(adj, directed=False)
        if ncomp != 1:
            detached.append(h["k"])
        for fname, fpos, _ in h["flaps"]:
            if int(np.asarray(fpos) @ mount) < u:
                wrongside.append(h["k"])
                break
        r0, r1 = sb.CAL["hinge_range_deg"]
        if not (r0 <= h["abs_lim"][0] <= sb.CAL["hinge_neutral_deg"] <= h["abs_lim"][1] <= r1):
            notline.append(h["k"])                  # limits outside the hinge's 0..180, or not around neutral
        want = span_axes.get(h["k"])
        ax = "XYZ"[int(np.argmax(np.abs(h["axis"])))]
        lead = any(np.array_equal(np.asarray(f[1]), mount * u) for f in h["flaps"])
        if (want and ax != want) or not lead:
            notline.append(h["k"])
        lo, hi = boxes[h["bi"]]
        s = G.sl(np.asarray(lo) - 1, np.asarray(hi) + 1, clip=True)
        o = G.own[s]
        if not ((o >= 0) & (o != h["bi"])).any():
            loose.append(h["k"])
    rec("attached", not detached and not wrongside,
        (f"detached flaps on hinges {detached} " if detached else "") +
        (f"flaps off the +Y mount side on {wrongside}" if wrongside else ""))
    rec("hinge_line", not notline and not loose,
        (f"axis/leading edge wrong on {notline} " if notline else "") + (f"hinge not touching main grid {loose}"
                                                                      if loose else ""))
    if fover:
        checks["overlap"] = dict(ok=False, detail=checks["overlap"]["detail"] + f" flap overlaps on hinges {sorted(set(fover))}")

    # ---- sweep clearance
    swept_by = {}
    clash = []
    for h in hinges:
        # flap blocks only: the head turns inside its own hinge block (they overlap by design)
        pts = np.concatenate([_samples(lo, hi) for lo, hi in [f[2] for f in h["flaps"]]])
        cells = set()
        mn, mx = h["lim"]
        # from the spawn pose (hinge angle 0, i.e. -neutral relative) through neutral to the far limit
        mn = min(mn, -sb.CAL["hinge_neutral_deg"])
        for ang in np.radians(np.arange(mn, mx + 1e-6, 1.0) if mx > mn else [mn]):
            q = (pts - h["pivot"]) @ _rot(h["axis"] * sb.CAL["hinge_angle_sign"], ang).T + h["pivot"]
            own, c = G.lookup(q)
            hit = (own >= 0) & (own != h["bi"])
            if hit.any():
                clash.append((h["k"], round(math.degrees(ang)), int(own[hit][0])))
                break
            inb = np.all((c >= 0) & (c < np.array(G.own.shape)), 1)
            cells |= set(map(tuple, c[inb]))
        swept_by[h["k"]] = cells
    pair = []
    ks = list(swept_by)
    for a, b in itertools.combinations(ks, 2):
        if swept_by[a] & swept_by[b]:
            pair.append((a, b))
    rec("sweep", not clash and not pair,
        (f"flap hits main grid {clash[:4]} " if clash else "") + (f"flap sweeps overlap {pair[:4]}" if pair else ""))
    swept_all = np.zeros(G.own.shape, bool)
    for cells in swept_by.values():
        if cells:
            swept_all[tuple(np.array(list(cells)).T)] = True

    # ---- main grid connectivity: blocks join only across faces where BOTH have mount points (the game splits off
    # anything else at spawn - and that split crashed it)
    own = G.own
    n = len(main)
    bits = {d: i for i, d in enumerate(sorted(sb.DIRS.values()))}
    mask = np.zeros(n, np.int32)
    full = np.zeros(n, np.int32)
    for i, (name, pos, orient) in enumerate(main):
        for d in sb.mount_dirs(name, orient):
            mask[i] |= 1 << bits[d]
        for d in sb.mount_full_dirs(name, orient):
            full[i] |= 1 << bits[d]
    full_cache = {}

    def covered(i, d):
        """True if every cell just beyond block i's face d belongs to blocks that mount FULLY on the opposite face."""
        key = (i, d)
        if key in full_cache:
            return full_cache[key]
        lo, hi = np.array(boxes[i][0]), np.array(boxes[i][1])
        ax_ = int(np.nonzero(d)[0][0])
        l2, h2 = lo.copy(), hi.copy()
        if d[ax_] > 0:
            l2[ax_], h2[ax_] = hi[ax_], hi[ax_] + 1
        else:
            l2[ax_], h2[ax_] = lo[ax_] - 1, lo[ax_]
        sl = G.sl(l2, h2, clip=True)
        ok_ = False
        if sl is not None and np.prod([x.stop - x.start for x in sl]) == np.prod(h2 - l2):
            owners = np.unique(G.own[sl])
            nd = tuple(-x for x in d)
            ok_ = bool((owners >= 0).all()) and all((full[o] >> bits[nd]) & 1 for o in owners)
        full_cache[key] = ok_
        return ok_

    rows, cols = [], []
    partial_edges = set()
    plain_r, plain_c = [], []
    for ax in range(3):
        a = np.moveaxis(own, ax, 0)
        x, y = a[:-1], a[1:]
        m = (x >= 0) & (y >= 0) & (x != y)
        pr = np.unique(np.stack([x[m], y[m]], 1), axis=0)
        if len(pr) == 0:
            continue
        up = [0, 0, 0]
        up[ax] = 1
        dn = [0, 0, 0]
        dn[ax] = -1
        ma = ((mask[pr[:, 0]] >> bits[tuple(up)]) & 1).astype(bool)
        mb = ((mask[pr[:, 1]] >> bits[tuple(dn)]) & 1).astype(bool)
        fa = ((full[pr[:, 0]] >> bits[tuple(up)]) & 1).astype(bool)
        fb = ((full[pr[:, 1]] >> bits[tuple(dn)]) & 1).astype(bool)
        # both full; or one partial whose WHOLE face is covered by blocks mounting fully there (wherever its group
        # sits, it meets a full face).  Partial-to-partial never.
        ca = np.array([covered(int(a), tuple(up)) for a in pr[:, 0]]) if len(pr) else np.zeros(0, bool)
        cb = np.array([covered(int(b), tuple(dn)) for b in pr[:, 1]]) if len(pr) else np.zeros(0, bool)
        ok = ma & mb & ((fa & fb) | (fa & ~fb & cb) | (fb & ~fa & ca))
        rows.append(pr[ok, 0])
        cols.append(pr[ok, 1])
        for a_, b_ in pr[ok & ~(fa & fb)]:
            partial_edges.add((min(int(a_), int(b_)), max(int(a_), int(b_))))
        plain_r.append(pr[:, 0])
        plain_c.append(pr[:, 1])
    r = np.concatenate(rows) if rows else np.zeros(0, int)
    c = np.concatenate(cols) if cols else np.zeros(0, int)
    A = coo_matrix((np.ones(len(r)), (r, c)), shape=(n, n))
    ncomp, labels = connected_components(A, directed=False)
    big = np.argmax(np.bincount(labels))
    loose = [f"{main[i][0]}@{list(main[i][1])}" for i in np.nonzero(labels != big)[0][:5]]
    margin = connection_margin(n, r, c, main, partial_edges)
    rec("connected", ncomp == 1, f"{ncomp} pieces through mount faces; detached e.g. {loose}" if ncomp != 1 else
        f"one piece through mount faces; {margin}")

    # ---- shell closure on the voxel lattice
    lat = side["lattice"]
    uu = lat["unit_cells"]
    i0 = np.array(lat["i0"])
    shape = tuple(lat["shape"])
    full = np.zeros(shape, bool)
    shaped = {}
    occ = own >= 0
    for i, (name, pos, orient) in enumerate(main):
        lo, hi = boxes[i]
        a = np.floor((np.asarray(lo) + uu / 2) / uu).astype(int) - i0
        b = np.ceil((np.asarray(hi) + uu / 2) / uu).astype(int) - i0
        for v in itertools.product(*[range(max(x, 0), min(y, s)) for x, y, s in zip(a, b, shape)]):
            c0 = (np.array(v) + i0) * uu - uu // 2
            sl = G.sl(c0, c0 + uu, clip=True)
            if sl is not None and occ[sl].all() and occ[sl].size == uu ** 3:
                full[v] = True
                for shp in ("Slope", "InvCorner", "Corner"):
                    if name.startswith("Armor" + shp):
                        shaped[v] = ({"Slope": "slope", "InvCorner": "invcorner", "Corner": "corner"}[shp], orient)
                        break
    cav = np.zeros(shape, bool)
    for v in side.get("cavity", []):
        cav[tuple(v)] = True
    lab, nl = ndimage.label(~full)
    border = set(np.unique(np.concatenate([lab[0].ravel(), lab[-1].ravel(), lab[:, 0].ravel(), lab[:, -1].ravel(),
                                           lab[:, :, 0].ravel(), lab[:, :, -1].ravel()])))
    border.discard(0)
    outside = np.isin(lab, list(border))
    leaks = int((outside & cav).sum())
    faceleaks = 0
    for v, (shp, orient) in shaped.items():
        ff = set(sb.full_faces(shp, orient))
        for dvec in sb.DIRS.values():
            nb = tuple(np.array(v) + dvec)
            if all(0 <= x < s for x, s in zip(nb, shape)) and cav[nb] and not full[nb] and tuple(dvec) not in ff:
                faceleaks += 1
    enclosed = int((~full & ~outside).sum())
    rec("shell", leaks == 0 and faceleaks == 0,
        f"{leaks} cavity voxels reachable from outside, {faceleaks} open shaped faces" if leaks or faceleaks else
        f"sealed ({enclosed} enclosed voxels)")

    # ---- engines
    blocked = []
    for i, (name, pos, orient) in enumerate(main):
        if not sb.is_thruster(name):
            continue
        ex = sb.thruster_exhaust(name, orient)
        lo, hi = boxes[i]
        a = int(np.nonzero(ex)[0][0])
        s = int(ex[a])
        L = hi[a] - lo[a]
        lanes = [(s, int(round(sb.CAL["exhaust_clear_lengths"] * L)), "exhaust")]
        if sb.thruster_type(name) == "atmospheric":
            lanes.append((-s, int(round(sb.CAL["intake_clear_lengths"] * L)), "intake"))
        for sg, ln, tag in lanes:
            l2, h2 = np.array(lo), np.array(hi)
            if sg > 0:
                l2[a], h2[a] = hi[a], hi[a] + ln
            else:
                l2[a], h2[a] = lo[a] - ln, lo[a]
            sl = G.sl(l2, h2, clip=True)
            if sl is None:
                continue
            if (G.own[sl] >= 0).any() or taken[sl].any() or swept_all[sl].any():
                blocked.append((i, name, tag))
                continue
            # a lane inside the sealed hull is obstructed too
            a2 = np.floor((np.asarray(l2) + uu / 2) / uu).astype(int) - i0
            b2 = np.ceil((np.asarray(h2) + uu / 2) / uu).astype(int) - i0
            a2, b2 = np.maximum(a2, 0), np.minimum(b2, shape)
            if (a2 < b2).all() and (~full & ~outside)[a2[0]:b2[0], a2[1]:b2[1], a2[2]:b2[2]].any():
                blocked.append((i, name, tag + " inside the hull"))
    rec("engines", not blocked, f"obstructed {blocked[:4]}" if blocked else "")

    # ---- centre of mass from the spec
    ms, cs = [], []
    for i, (name, pos, orient) in enumerate(main):
        lo, hi = boxes[i]
        ms.append(cat[name].mass)
        cs.append((np.asarray(lo) + hi) / 2)
    for h in hinges:
        ms.append(cat[h["head"]].mass)
        cs.append((np.asarray(h["head_box"][0]) + h["head_box"][1]) / 2)
        for fname, _, (lo, hi) in h["flaps"]:
            ms.append(cat[fname].mass)
            cs.append((np.asarray(lo) + hi) / 2)
    ms, cs = np.array(ms), np.array(cs)
    cc = (ms[:, None] * cs).sum(0) / ms.sum() * sb.CELL_M
    com = np.array([cc[2], cc[0], cc[1]])
    same = np.allclose(com, side["com_m"], atol=2e-3)
    tgt, tol = side.get("com_target_m"), side.get("com_tolerance_m")
    within = tgt is not None and abs(com[0] - tgt) <= tol + 1e-6
    centred = abs(com[1]) <= 0.05 * side["block_m"]
    rec("com", same and within and centred,
        f"CoM x {com[0]:.3f} m target {tgt} +- {tol}; y {com[1]:+.3f}" + ("" if same else " (sidecar disagrees)"))
    nb = sum(1 for n, _, _ in main if n.startswith("Battery"))
    rec("power", nb > 0 or not hinges, f"{nb} batteries")
    return dict(ok=all(c["ok"] for c in checks.values()), checks=checks)
