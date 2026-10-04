"""validate - offline checks of a written spec (re-read from disk, rebuilt through the se2blocks adapter only).

  prefabs      every Guid known; H lines point at hinge blocks; every hinge carries flap blocks
  overlap      no two main-grid blocks share a cell; no flap block overlaps the main grid, a head, or another flap
  attached     each hinge's flap blocks + head are one face-connected piece; flaps on the measured mount side (+Y)
  hinge_line   hinge axis along the segment's span axis; a flap block directly aft of the head at the 90 deg
               neutral (leading edge on the hinge line); limits inside 0..180 around the neutral; the hinge block
               touches the main grid
  sweep        rotated through its limits (about the 90 deg neutral), no flap/head point enters a main-grid block (other than its own hinge)
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
        mounts = [sb.mount_dirs(h["head"], sb.orient_names(h["Rh"]))]
        for fname, fpos, _ in h["flaps"]:
            mounts.append(sb.mount_dirs(fname, sb.orient_names(h["Rh"] @ sb.orient_matrix(*h["forients"][len(mounts) - 1]))))
        n = len(pieces)
        adj = np.zeros((n, n), bool)
        for a, b in itertools.combinations(range(n), 2):
            (l1, h1), (l2, h2) = pieces[a], pieces[b]
            touch = np.minimum(h1, h2) - np.maximum(l1, l2)
            if (touch >= 0).all() and (touch == 0).sum() == 1:
                ax = int(np.nonzero(touch == 0)[0][0])
                d = [0, 0, 0]
                d[ax] = 1 if (np.asarray(l2)[ax] >= np.asarray(h1)[ax]) else -1
                if tuple(d) in mounts[a] and tuple(-x for x in d) in mounts[b]:
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
    for i, (name, pos, orient) in enumerate(main):
        for d in sb.mount_dirs(name, orient):
            mask[i] |= 1 << bits[d]
    rows, cols = [], []
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
        ok = ((mask[pr[:, 0]] >> bits[tuple(up)]) & 1).astype(bool) & ((mask[pr[:, 1]] >> bits[tuple(dn)]) & 1).astype(bool)
        rows.append(pr[ok, 0])
        cols.append(pr[ok, 1])
        plain_r.append(pr[:, 0])
        plain_c.append(pr[:, 1])
    r = np.concatenate(rows) if rows else np.zeros(0, int)
    c = np.concatenate(cols) if cols else np.zeros(0, int)
    A = coo_matrix((np.ones(len(r)), (r, c)), shape=(n, n))
    ncomp, labels = connected_components(A, directed=False)
    big = np.argmax(np.bincount(labels))
    loose = [f"{main[i][0]}@{list(main[i][1])}" for i in np.nonzero(labels != big)[0][:5]]
    rec("connected", ncomp == 1, f"{ncomp} pieces through mount faces; detached e.g. {loose}" if ncomp != 1 else
        "one piece through mount faces")

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
