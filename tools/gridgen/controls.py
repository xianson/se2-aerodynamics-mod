"""controls - full trailing-edge coverage with hinged control surfaces.

For every lifting surface panel (left wing, right wing, fin, each canard, ...) the trailing-edge strip is split
into segments along the span.  A segment has ONE straight, axis-aligned hinge line (all its flap cells are aft of
row x_h), so its flap rotates about an axis parallel to the span and stays inside its own span-wise slab -
neighbouring hinges sit along the axis, never across the swing plane.

  main grid : the hinge block at (hinge column, x_h); the other x_h cells of the segment are cut away (the gap
              that lets the flap's leading edge swing); the wing ahead of x_h stays.
  head grid : every surface cell aft of x_h in the segment's columns, as F lines in the head grid's frame
              (head at 0 0 0; chord along head +Y, span along head Z - the measured mount convention).

Each segment's swept volume (over its limits, plus a margin) is checked against everything else: cells of the
same surface ahead of the hinge are cut; anything else (fuselage, booms, engines, exhaust lanes, other segments'
swept volumes) removes the offending column from the segment.

Roles: wing -> flap (inboard) / aileron (outboard), or elevon (no other pitch surface); htail -> elevator;
vtail -> rudder; canard -> canard; vee -> ruddervator.  Limits are given in "positive = trailing edge down
(horizontal) / right (vertical)" and converted to the hinge's own sign.
"""
from __future__ import annotations

import math

import numpy as np
from scipy import ndimage

import se2blocks as sb

LIMITS = {"aileron": (-25, 25), "flap": (0, 35), "elevator": (-25, 25), "rudder": (-25, 25), "elevon": (-25, 25),
          "canard": (-20, 20), "ruddervator": (-25, 25)}
SURFACE_ROLES = ("wing", "htail", "vtail", "canard", "vee")


def _rot(axis, ang):
    a = np.asarray(axis, float)
    a = a / np.linalg.norm(a)
    K = np.array([[0, -a[2], a[1]], [a[2], 0, -a[0]], [-a[1], a[0], 0]])
    return np.eye(3) + math.sin(ang) * K + (1 - math.cos(ang)) * K @ K


_SWEEP = {}


def _cell_sweep(rel_p, rel_q, frac, a, sign, lo_deg, hi_deg, margin, n_sub):
    """2D voxel offsets (from the rounded pivot) touched by one cell at offset (rel_p, rel_q) rotating about axis a."""
    key = (rel_p, rel_q, frac, a, sign, lo_deg, hi_deg, margin, n_sub)
    if key in _SWEEP:
        return _SWEEP[key]
    p, q = [i for i in range(3) if i != a]
    g = np.linspace(-0.4995, 0.4995, n_sub)
    U, V = np.meshgrid(g, g, indexing="ij")
    pts = np.zeros((U.size, 3))
    pts[:, p] = rel_p + U.ravel() - frac[0]
    pts[:, q] = rel_q + V.ravel() - frac[1]
    axis = np.zeros(3)
    axis[a] = sign
    lo = math.radians(lo_deg - (margin if lo_deg < 0 else 0))
    hi = math.radians(hi_deg + (margin if hi_deg > 0 else 0))
    out = set()
    for ang in np.linspace(lo, hi, max(2, int(round(math.degrees(hi - lo))) + 1)):
        r = pts @ _rot(axis, ang).T
        r[:, p] += frac[0]
        r[:, q] += frac[1]
        rr = np.round(r).astype(int)
        out |= set(zip(rr[:, p].tolist(), rr[:, q].tolist()))
    _SWEEP[key] = out
    return out


def swept_voxels(cells, pivot, axis, lo_deg, hi_deg, margin=3.0, n_sub=9):
    """Voxel indices touched by the cells rotated about the (lattice-aligned) axis through pivot, in voxel-index
    coordinates.  Rotation keeps the span coordinate, so each cell is sampled densely over its full cross-section
    in the rotation plane - edges included - at 1 degree steps (corners sweeping through a neighbour are caught);
    the per-offset result is cached."""
    a = int(np.argmax(np.abs(axis)))
    sign = 1 if axis[a] > 0 else -1
    p, q = [i for i in range(3) if i != a]
    pv = np.asarray(pivot, float)
    pr = np.round(pv).astype(int)
    frac = (round(float(pv[p] - pr[p]), 4), round(float(pv[q] - pr[q]), 4))
    out = set()
    for c in cells:
        c = [int(x) for x in c]
        for op, oq in _cell_sweep(c[p] - pr[p], c[q] - pr[q], frac, a, sign, float(lo_deg), float(hi_deg), margin,
                                  n_sub):
            v = [0, 0, 0]
            v[a], v[p], v[q] = c[a], pr[p] + op, pr[q] + oq
            out.add(tuple(v))
    return out


def te_motion(hinge_orient):
    """Direction the trailing edge (aft of the hinge at neutral) moves for an increasing hinge angle."""
    return np.cross(sb.hinge_axis_vec(hinge_orient), np.array([0, 0, 1]))


def place_controls(model, d, surfs, cat):
    grid = d["grid"]
    hinge_name, head_name = sb.hinge_names(grid)
    pitch_other = any(s.role in ("htail", "vee", "canard") for s in surfs)
    lat = model.lat
    segs = []
    for ci, comp in enumerate(model.comps):
        if comp.role not in SURFACE_ROLES:
            continue
        ctl = dict(getattr(comp, "spec", {}).get("controls", {}))
        mode = ctl.get("mode", "auto")
        if mode == "none":
            continue
        flap_block = ctl.get("flap_block", d.get("flap_block", "cube"))   # half plates mount on one face only
        vertical = bool(getattr(comp, "vertical", False))
        span_ax = 1 if vertical else 0
        t_ax = 0 if vertical else 1
        mask = model.solid & (model.label == ci)
        lab, n = ndimage.label(mask)
        groups = []
        for p in range(1, n + 1):
            idx = np.argwhere(lab == p)
            X = idx[:, 0] + lat.i0[0]
            if vertical:
                side = "center" if abs(X.mean()) < 0.5 else ("right" if X.mean() > 0 else "left")
                groups.append((side, idx))
            else:
                groups.append(("right", idx[X > 0]))
                groups.append(("left", idx[X < 0]))
        groups = [g for g in groups if len(g[1])]
        # right side first; the left side mirrors its plans exactly (symmetric hinges, symmetric mass).  A right
        # plan whose mirror does not fit is banned and the surface replanned, so the result is always symmetric.
        groups.sort(key=lambda g: {"right": 0, "center": 1, "left": 2}[g[0]])
        args = (span_ax, t_ax, ctl, mode, pitch_other, hinge_name, head_name, flap_block, cat)
        snap = _snapshot(model)
        banned = set()
        for attempt in range(60):
            mine_all, failed = [], []
            for side, cells in groups:
                if side == "left" and any(g[0] == "right" for g in groups):
                    for sg in [x for x in mine_all if x["side"] == "right"]:
                        q = _mirror_plan(model, sg["_plan"], ci)
                        if q is not None:
                            mine_all.append(_commit(model, q))
                        else:
                            failed.append(sg)
                    continue
                mine_all += _panel(model, d, comp, ci, cells, side, *args, banned=banned)
            if not failed:
                break
            for sg in failed:
                banned.add((tuple(sg["columns"]), sg["x_h"], sg["hinge_voxel"]))
            _restore(model, snap)
            mine_all = []
        else:
            model.problems.append(f"{comp.id}: no symmetric control-surface layout found")
        segs += mine_all
    return segs


def _snapshot(model):
    snap = {k: getattr(model, k).copy() for k in ("solid", "engine_vox", "swept_taken", "frozen")}
    snap["hinge_mounts"] = dict(model.hinge_mounts)
    return snap


def _restore(model, snap):
    for k, v in snap.items():
        if k == "hinge_mounts":
            model.hinge_mounts = dict(v)
        else:
            getattr(model, k)[...] = v


def _mirror_plan(model, p, ci):
    """The left-side twin of a right-side plan (X mirrored), checked against the current model; None if it clashes."""
    lat = model.lat
    nx = lat.shape[0]
    mv = lambda v: (nx - 1 - v[0], v[1], v[2])
    span_ax = p["span_ax"]
    q = dict(p)
    q["side"] = "left"
    q["hidx"] = mv(p["hidx"])
    q["flap"] = [mv(v) for v in p["flap"]]
    q["chunk"] = [nx - 1 - k for k in p["chunk"]] if span_ax == 0 else list(p["chunk"])
    row = {mv(v) for v in p["row"]}
    for v in q["flap"] + [q["hidx"]]:
        if v in row or v == q["hidx"]:
            if not model.solid[v]:
                return None
    for v in q["flap"]:
        own = model.solid[v] and model.label[v] == ci
        if not own and (model.solid[v] or model.cavity[v] or model.blocked[v] or model.swept_taken[v]):
            return None
        if not own and p["extended"] == 0:
            return None
    struct0 = _structure(model)
    n0 = 0
    res = _try(model, ci, q["chunk"], span_ax, p["x_h"], q["hidx"], q["flap"], row, p["orient_h"], p["lim"],
               struct0, n0)
    if res is None:
        return None
    q["swept"], q["carve"] = res
    q["row"] = row
    return q


def _role(comp, ctl, mode, pitch_other, eta):
    r = comp.role
    if r == "wing":
        if mode == "elevon" or (mode == "auto" and not pitch_other):
            return "elevon"
        if ctl.get("flaps", True) is False:
            return "aileron"
        return "flap" if eta < float(ctl.get("aileron_from", 0.55)) else "aileron"
    return {"htail": "elevator", "vtail": "rudder", "canard": "canard", "vee": "ruddervator"}[r]


def _panel(model, d, comp, ci, cells, side, span_ax, t_ax, ctl, mode, pitch_other, hinge_name, head_name,
           flap_block, cat, banned=()):
    """Grow segments root to tip: from each free column, the longest run (up to per_hinge, or the whole remainder
    when that is short) that plans cleanly becomes one hinge; a column that fits in no segment is left as wing."""
    lat = model.lat
    B = lat.B
    cols = {}
    for c in cells:
        cols.setdefault(int(c[span_ax]), set()).add((int(c[t_ax]), int(c[2])))
    keys = sorted(cols)
    coord = lambda k: (k + lat.i0[span_ax]) * B
    if span_ax == 0:
        keys.sort(key=lambda k: abs(coord(k)))
        semi = comp.span / 2 if comp.mirror else comp.span
        eta = {k: abs(coord(k)) / max(semi, 1e-6) for k in keys}
    else:
        up = math.sin(comp.G) >= 0
        keys.sort(key=lambda k: coord(k) if up else -coord(k))
        root = comp.z0
        eta = {k: abs(coord(k) - root) / max(comp.span, 1e-6) for k in keys}
    # segment length: about five hinges per wing half (two or three on a tail), at least 2 (large) / 3 (small)
    # blocks each, so a hinge carries a sensible share of the span
    n_target = 5 if comp.role == "wing" else 2
    clean_style = clean_style_of(d)
    cf = float(ctl.get("chord", 0.45 if clean_style else 0.3))
    min_seg = 1
    if ctl.get("square", clean_style):
        # square-ish segments, as wide as the aero detector needs a flap panel to be a wing: span >= 1.5 x chord
        mid = keys[len(keys) // 2]
        zs = [z for t, z in cols[mid]]
        n_rows = max(zs) - min(zs) + 1
        fr = flap_rows(n_rows, cf)
        per = int(ctl.get("per_hinge", max(3, min_panel_span(fr))))
        if fr >= FLAP_LIFT_ROWS:
            min_seg = min_panel_span(fr)
    else:
        per = int(ctl.get("per_hinge", max(2 if d["grid"] == "large" else 3, int(math.ceil(len(keys) / n_target)))))
    coverage = ctl.get("coverage", d.get("coverage", "full"))
    runs = []
    for k in keys:
        zs = [z for t, z in cols[k]]
        if max(zs) - min(zs) < 1:       # a single chord row: nothing to hinge
            role = None
        else:
            role = _role(comp, ctl, mode, pitch_other, eta[k])
        if runs and runs[-1][0] == role and abs(runs[-1][1][-1] - k) == 1:
            runs[-1][1].append(k)
        else:
            runs.append((role, [k]))
    out = []
    for role, ks in runs:
        if role is None:
            continue
        if coverage == "sketch":
            # one square segment per role and side: flaps at the root, ailerons at the tip, the rest at the root
            ks = ks[-per:] if role == "aileron" else ks[:per]
            ks = ks if len(ks) else ks
        i = 0
        while i < len(ks):
            rem = len(ks) - i
            target = rem if rem <= per + per // 2 and coverage != "sketch" else min(per, rem)
            done = False
            for n in range(target, min(min_seg, target) - 1, -1):
                plan = _plan(model, d, comp, ci, cols, ks[i:i + n], span_ax, t_ax, cf, role, side, hinge_name,
                             head_name, flap_block, cat, banned)
                if plan is not None:
                    out.append(_commit(model, plan))
                    i += n
                    done = True
                    break
            if not done:
                i += 1
    return out


def _idx(span_ax, t_ax, k, t, z):
    v = [0, 0, 0]
    v[span_ax], v[t_ax], v[2] = k, t, z
    return tuple(v)


def _structure(model):
    """Main-grid voxels for connectivity: armor, engines/pylons/cockpit, hinges."""
    return model.solid | model.engine_vox


# What the aero mod's wing detector accepts (measured with aero_probe on isolated 1-block-thick plates): a plate is a
# wing only if its shorter side is at least 2 blocks (t/c <= 0.5) and its longer side at least 1.5x the shorter -
# a 1-block chord never, a square panel never.  A flap is its own grid in game (HingeSurfaces: "the flap's real
# aerodynamics are its own grid's"), so:
FIXED_MIN_ROWS = 1      # chord rows ahead of the hinge row that stay on the main grid (with the hinge row: >= 2, t/c <= 0.5)
FLAP_LIFT_ROWS = 2      # a flap of this many rows or more is a wing on its own grid (if its span is >= 1.5x)


def flap_rows(n, cf):
    """Flap rows for a local chord of n rows: at least FLAP_LIFT_ROWS where the chord keeps FIXED_MIN_ROWS + the hinge
    row ahead of it; a 4-row chord gets a 1-row (control-only) flap; 3 rows or fewer none inside the chord (only the
    trailing-edge extension: hinge on the TE row, the flap a new row aft)."""
    room = n - FIXED_MIN_ROWS - 1
    if room <= 0:
        return 0
    if room < FLAP_LIFT_ROWS:
        return room
    return int(min(room, max(FLAP_LIFT_ROWS, round(cf * n), 3 if n >= 12 else 0)))


def min_panel_span(fr):
    """Columns per hinge so a lifting flap panel (fr rows) is a wing on its own grid: span >= 1.5 x chord."""
    return int(math.ceil(1.5 * max(fr, FLAP_LIFT_ROWS)))


def _plan(model, d, comp, ci, cols, chunk, span_ax, t_ax, cf, role, side, hinge_name, head_name, flap_block, cat,
          banned=()):
    """Plan one segment over the given columns without changing the model; None if it does not fit."""
    lat = model.lat
    TE = {k: max(z for t, z in cols[k]) for k in chunk}
    LE = {k: min(z for t, z in cols[k]) for k in chunk}
    # flap chord (flap_rows): a lifting flap where the chord allows, never eating the fixed wing below
    # FIXED_MIN_ROWS rows ahead of the hinge row
    wing = comp.role == "wing"
    if wing:
        target = [TE[k] - max(1, flap_rows(TE[k] - LE[k] + 1, cf)) for k in chunk]
        x_min = max(LE.values()) + FIXED_MIN_ROWS    # hinge row: at least FIXED_MIN_ROWS rows behind every LE
    else:
        # tails keep their control first: the fraction of the chord, at least 1-2 rows, the hinge anywhere
        nf = lambda n: max(1 if n < 5 else (2 if n < 9 else 3), int(round(cf * n)))
        target = [TE[k] - nf(TE[k] - LE[k] + 1) for k in chunk]
        x_min = min(LE.values())
    x_top = min(int(np.median(target)), min(TE.values()) - 1)
    order = sorted(chunk, key=lambda k: abs(k - chunk[len(chunk) // 2]))
    span_vec = [0, 0, 0]
    span_vec[span_ax] = 1
    orient_h = sb.orient_for_hinge(tuple(span_vec), (0, 0, 1))
    mot = te_motion(orient_h)
    pos_sense = ("down" if mot[1] < 0 else "up") if span_ax == 0 else ("right" if mot[0] > 0 else "left")
    lim = tuple(getattr(comp, "spec", {}).get("controls", {}).get("limits", {}).get(role, LIMITS[role]))
    if pos_sense in ("up", "left"):
        lim = (-lim[1], -lim[0])
    struct0 = _structure(model)
    blocked = model.blocked
    n0 = 0
    # candidate hinge lines: inside the chord (flap = the aft rows), then - for chords too short to keep a leading
    # row once the hinge row is cut - the recipe "hinge on the trailing-edge row, flap trailing aft as a new row"
    # the target hinge row first, then smaller flaps (hinge further aft: more fixed wing kept), then larger ones
    aft = list(range(x_top + 1, min(TE.values())))
    fwd = list(range(x_top - 1, x_min - 1, -1))
    cands = [(x_h, []) for x_h in ([x_top] if x_top >= x_min else []) + aft + fwd[:3]]
    if wing and any(flap_rows(TE[k] - LE[k] + 1, cf) == 0 for k in chunk):
        cands = []                     # a short wing chord in the chunk: only the trailing-edge extension
    xe = min(TE.values())
    ext = []
    for k in chunk:
        if TE[k] == xe:
            for t in {t for t, z in cols[k] if z == xe}:
                v = _idx(span_ax, t_ax, k, t, xe + 1)
                if all(0 <= a < n for a, n in zip(v, lat.shape)) and not (model.solid[v] or model.cavity[v] or
                                                                         blocked[v] or model.swept_taken[v]):
                    ext.append(v)
    if ext:
        cands.append((xe, ext))
    for x_h, ext in cands:
        frows = 1 if ext else max(TE[k] - x_h for k in chunk)
        if clean_style_of(d) and frows >= FLAP_LIFT_ROWS and len(chunk) < min_panel_span(frows):
            continue                    # a lifting flap panel must be a wing on its own grid: span >= 1.5 x chord
        flap = [_idx(span_ax, t_ax, k, t, z) for k in chunk for (t, z) in cols[k] if z > x_h] + ext
        if {v[span_ax] for v in flap} != set(chunk):
            continue
        fset = set(flap)
        row = {_idx(span_ax, t_ax, k, t, z) for k in chunk for (t, z) in cols[k] if z == x_h}
        gone = fset | row
        for k in order[:3]:
            ts = sorted({t for t, z in cols[k] if z == x_h and _idx(span_ax, t_ax, k, t, x_h + 1) in fset},
                        key=lambda t: abs(t - np.median([tt for tt, zz in cols[k]])))
            best = None
            for t in sorted(ts, reverse=True)[:3]:
                hidx = _idx(span_ax, t_ax, k, t, x_h)
                attached = False
                for dv in ((0, 0, -1), (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0)):
                    nb = tuple(a + b for a, b in zip(hidx, dv))
                    if all(0 <= a < s for a, s in zip(nb, lat.shape)) and struct0[nb] and nb not in gone:
                        attached = True
                        break
                if not attached or (tuple(chunk), x_h, hidx) in banned:
                    continue
                res = _try(model, ci, chunk, span_ax, x_h, hidx, flap, row, orient_h, lim, struct0, n0, blocked)
                if res is None:
                    continue
                swept, carve = res
                # the fixed wing stays a wing for the aero detector: no column of the chunk may lose chord rows to
                # the flap's swing below min(FIXED_MIN_ROWS + 1, its own chord) (the hinge cell itself excepted)
                if comp.role == "wing" and _thins_fixed_wing(cols, chunk, span_ax, t_ax, x_h, hidx, carve):
                    continue            # (tails: the elevator / rudder matters more than the tail's own lift)
                plan = dict(comp=comp, chunk=chunk, span_ax=span_ax, t_ax=t_ax, x_h=x_h, hidx=hidx, flap=flap,
                            extended=len(ext), row=row, te_slope=clean_style_of(d),
                            carve=carve, swept=swept, orient_h=orient_h, lim=lim, pos_sense=pos_sense, role=role,
                            side=side, hinge_name=hinge_name, head_name=head_name, flap_block=flap_block, cat=cat,
                            grid=d["grid"])
                # least visible first: on a horizontal plate nothing cut from the top layer (the slot stays
                # hidden underneath), then the fewest blocks cut
                top = max(t_ for kk in chunk for t_, z_ in cols[kk]) if span_ax == 0 else None
                plan["_cost"] = (sum(1 for v in carve if top is not None and v[t_ax] == top), len(carve))
                if best is None or plan["_cost"] < best["_cost"]:
                    best = plan
            if best is not None:
                return best
    return None


def _thins_fixed_wing(cols, chunk, span_ax, t_ax, x_h, hidx, carve):
    """True if the flap's swing would leave the fixed strip over this chunk too thin for the aero detector: its mean
    chord (rows at or ahead of the hinge row, less the carved cells and the hinge) below min(FIXED_MIN_ROWS + 1, the
    mean before).  (The detector works on the connected planform: a narrow column next to the hinge is fine, a
    strip that is one row deep on average is not.)"""
    before, after = [], []
    for k in chunk:
        rows = {z for t, z in cols[k] if z <= x_h}
        lost = {v[2] for v in carve if v[span_ax] == k and v[2] <= x_h}
        if hidx[span_ax] == k:
            lost.add(x_h)
        before.append(len(rows))
        after.append(len(rows - lost))
    return float(np.mean(after)) < min(FIXED_MIN_ROWS + 1, float(np.mean(before))) - 1e-9


def _try(model, ci, chunk, span_ax, x_h, hidx, flap, row, orient_h, lim, struct0, n0, blocked=None):
    lat = model.lat
    if blocked is None:
        blocked = model.blocked
    fl = set(flap) | {hidx}
    arr = np.array(sorted(fl))
    lo = arr.min(0)
    g = np.zeros(tuple(arr.max(0) - lo + 1), bool)
    g[tuple((arr - lo).T)] = True
    if ndimage.label(g)[1] != 1:
        return None
    pos_h = lat.centre_cells(np.array(hidx))
    axis, pivot_cells = sb.hinge_axis_and_pivot(pos_h, orient_h)
    pivot = pivot_cells / lat.u - lat.i0
    # the flap blocks swing; the head turns inside its own hinge block (the two overlap by design), so it is not swept
    # the head spawns at hinge angle 0 and the controller swings it to the neutral (90): the flap must clear the
    # whole arc from 0 through neutral to the far deflection, not just the pose around neutral
    neu = sb.CAL["hinge_neutral_deg"]
    # (0 is the hinge's end stop: no safety margin beyond it - swept_voxels widens lo by its 3 deg margin)
    lo_rel = lim[0] if lim[0] < -neu else -neu + 3.0
    swept = swept_voxels(sorted(fl - {hidx}), pivot, axis * sb.CAL["hinge_angle_sign"], min(lim[0], lo_rel), lim[1])
    swept.discard(hidx)
    carve = set()                         # only the hinge-row (and other) cells the swing actually reaches
    for v in swept:
        if v in fl or any(a < 0 or a >= s for a, s in zip(v, lat.shape)):
            continue
        if blocked[v] or model.swept_taken[v]:
            return None
        if model.solid[v]:
            if model.label[v] == ci and v[span_ax] in chunk and v[2] <= x_h:
                carve.add(v)
            else:
                return None
        elif model.cavity[v]:
            return None
    # the hinge must still touch the structure through one of its own mount faces (not the head side)
    hname = sb.hinge_names(model.grid)[0]
    ok = False
    for d in sb.mount_dirs(hname, orient_h):
        nb = tuple(a + b for a, b in zip(hidx, d))
        if all(0 <= a < s for a, s in zip(nb, lat.shape)) and struct0[nb] and nb not in fl and nb not in carve \
                and sb.joins(hname, orient_h, "ArmorCubeLight50", sb.IDENTITY, d):
            ok = True                   # (the hinge's half faces pair with a cube's full face)
            break
    if not ok:
        return None
    # the main grid must stay one piece: compare the pieces in a box around the segment before and after
    allv = np.array(sorted(fl | carve))
    a = np.maximum(allv.min(0) - 3, 0)
    b = np.minimum(allv.max(0) + 4, np.array(lat.shape))
    sl = tuple(slice(int(x), int(y)) for x, y in zip(a, b))
    before = struct0[sl]
    after = before.copy()
    for v in fl | carve:
        after[tuple(np.array(v) - a)] = False
    after[tuple(np.array(hidx) - a)] = True
    hm = dict(model.hinge_mounts)
    hm[tuple(int(x) for x in hidx)] = (hname, tuple(orient_h))
    if _pieces(after, hm, a) > _pieces(before, model.hinge_mounts, a):
        return None
    return swept, carve


def _pieces(mask, hinge_mounts, origin):
    """Connected pieces of a voxel crop where hinge voxels join their neighbours only across their mount faces."""
    from scipy.sparse import coo_matrix
    from scipy.sparse.csgraph import connected_components
    idx = np.argwhere(mask)
    if len(idx) == 0:
        return 0
    index = -np.ones(mask.shape, np.int64)
    index[tuple(idx.T)] = np.arange(len(idx))
    local = {tuple(int(x) for x in np.array(v) - origin): m for v, m in hinge_mounts.items()}
    rows, cols = [], []
    for ax in range(3):
        d = [0, 0, 0]
        d[ax] = 1
        nb = idx + d
        ok = nb[:, ax] < mask.shape[ax]
        j = np.full(len(idx), -1)
        j[ok] = index[tuple(nb[ok].T)]
        for i in np.nonzero(j >= 0)[0]:
            va, vb = tuple(int(x) for x in idx[i]), tuple(int(x) for x in idx[j[i]])
            if va in local or vb in local:
                na = local.get(va, ("ArmorCubeLight50", sb.IDENTITY))
                nb = local.get(vb, ("ArmorCubeLight50", sb.IDENTITY))
                if not sb.joins(na[0], na[1], nb[0], nb[1], tuple(d)):
                    continue
            rows.append(i)
            cols.append(j[i])
    n = len(idx)
    return connected_components(coo_matrix((np.ones(len(rows)), (rows, cols)), shape=(n, n)), directed=False)[0]


def _commit(model, p):
    lat, cat = model.lat, p["cat"]
    hidx = p["hidx"]
    for v in p["flap"]:
        model.solid[v] = False
    for v in p["carve"]:
        model.solid[v] = False
    model.solid[hidx] = False
    model.engine_vox[hidx] = True          # the hinge block is main-grid structure
    for v in p["swept"]:
        if all(0 <= a < s for a, s in zip(v, model.swept_taken.shape)):
            model.swept_taken[v] = True
    model.frozen[hidx] = True
    orient_h = p["orient_h"]
    model.hinge_mounts[tuple(int(x) for x in hidx)] = (p["hinge_name"], tuple(orient_h))
    hpos = cat.pos_for(p["hinge_name"], lat.voxel_box(np.array(hidx))[0], orient_h)
    origin, Rh = sb.headgrid_frame(hpos, orient_h)
    block = p["flap_block"]
    if block == "half" and not _inplane_connected(set(p["flap"]) | {hidx}, p["t_ax"]):
        block = "cube"                     # half blocks only touch within their layer: staircase flaps get cubes
    fname = sb.armor(block, p["grid"])
    t_vec = [0, 0, 0]
    t_vec[p["t_ax"]] = 1
    forient = _orient_thin(t_vec) if block == "half" else sb.IDENTITY
    fblocks = []
    fset = set(p["flap"])
    taper = p.get("te_slope", False)
    for v in sorted(p["flap"]):
        p_main = lat.centre_cells(np.array(v))
        name_v, orient_v = fname, forient
        if taper and block == "cube":
            # a clean, thinner trailing edge: the last row's top and bottom layers are slopes facing aft
            aft = (v[0], v[1], v[2] + 1)
            up = list(v)
            up[p["t_ax"]] += 1
            dn = list(v)
            dn[p["t_ax"]] -= 1
            if aft not in fset and (tuple(up) in fset) != (tuple(dn) in fset):
                n = [0, 0, 1]
                n[p["t_ax"]] = 1 if tuple(dn) in fset else -1
                o = sb.orient_for_normal("slope", n)
                if o is not None:
                    name_v, orient_v = sb.armor("slope", p["grid"]), o
        p_hg, o_hg = sb.to_headgrid(p_main, orient_v, origin, Rh)
        lo, hi = cat.box(name_v, p_main, orient_v)
        fblocks.append(dict(name=name_v, pos=p_main, orient=orient_v, hg_pos=np.round(p_hg).astype(int),
                            hg_orient=o_hg, lo=lo, hi=hi, voxel=v))
    if any(f["name"] != fname for f in fblocks) and not _head_grid_mounted(fblocks, p, Rh, origin, lat):
        # a trailing-edge slope was the only bridge between columns (its triangular sides do not mount): cubes
        for f in fblocks:
            if f["name"] != fname:
                f["name"], f["orient"] = fname, forient
                p_hg, o_hg = sb.to_headgrid(f["pos"], forient, origin, Rh)
                f["hg_pos"], f["hg_orient"] = np.round(p_hg).astype(int), o_hg
                f["lo"], f["hi"] = cat.box(fname, f["pos"], forient)
    axis, pivot_cells = sb.hinge_axis_and_pivot(hpos, orient_h)
    hlo, hhi = cat.box(p["hinge_name"], hpos, orient_h)
    headlo, headhi = cat.box(p["head_name"], origin, sb.orient_names(Rh))
    comp = p["comp"]
    return dict(surface=comp.id, surface_role=comp.role, role=p["role"], side=p["side"], span_axis=p["span_ax"],
                columns=list(p["chunk"]), x_h=p["x_h"], hinge_voxel=hidx, hinge_name=p["hinge_name"],
                head_name=p["head_name"], hinge_pos=np.asarray(hpos, int), hinge_orient=orient_h, hinge_lo=hlo,
                hinge_hi=hhi, head_lo=headlo, head_hi=headhi, head_origin=origin, Rh=Rh,
                axis=tuple(int(a) for a in axis), pivot_cells=tuple(float(a) for a in pivot_cells),
                deflection=p["lim"], neutral_deg=sb.CAL["hinge_neutral_deg"],
                limits=(sb.CAL["hinge_neutral_deg"] + p["lim"][0], sb.CAL["hinge_neutral_deg"] + p["lim"][1]),
                positive_moves_te=p["pos_sense"], flaps=fblocks, carved=sorted(p["carve"]),
                extended_rows=bool(p["extended"]), _plan=p,
                swept=sorted(p["swept"]))


def _inplane_connected(cells, t_ax):
    arr = np.array(sorted(cells))
    lo = arr.min(0)
    g = np.zeros(tuple(arr.max(0) - lo + 1), bool)
    g[tuple((arr - lo).T)] = True
    st = ndimage.generate_binary_structure(3, 1)
    c = [1, 1, 1]
    for dv in (-1, 1):
        c2 = list(c)
        c2[t_ax] += dv
        st[tuple(c2)] = False
    return ndimage.label(g, structure=st)[1] == 1


def _head_grid_mounted(fblocks, p, Rh, origin, lat):
    """True if head + flap blocks form one piece through mount faces (head mounts on its +Y only)."""
    from scipy.sparse.csgraph import connected_components
    cells = [tuple(int(x) for x in p["hidx"])] + [tuple(int(x) for x in f["voxel"]) for f in fblocks]
    names = [(p["head_name"], sb.orient_names(Rh))] + [(f["name"], tuple(f["orient"])) for f in fblocks]
    index = {c: i for i, c in enumerate(cells)}
    n = len(cells)
    A = np.zeros((n, n), bool)
    for i, c in enumerate(cells):
        for d in sb.mount_dirs(*names[i]):
            k = index.get(tuple(a + b for a, b in zip(c, d)))
            if k is not None and sb.joins(names[i][0], names[i][1], names[k][0], names[k][1], tuple(d)):
                A[i, k] = A[k, i] = True
    return connected_components(A, directed=False)[0] == 1


def clean_style_of(d):
    return d.get("style", "clean" if "wing" in d else "smooth") == "clean" and not d.get("mesh")


def _orient_thin(t_vec):
    """Orientation putting a half block's thin local Z along the thickness axis, local Up kept upright if possible."""
    want = np.array(t_vec)
    for o in sb.ALL_ORIENTS:
        M = sb.orient_matrix(*o)
        if np.array_equal(M @ np.array([0, 0, 1]), want) or np.array_equal(M @ np.array([0, 0, 1]), -want):
            if want[1] == 0 and M[1, 1] != 1:
                continue
            return o
    return sb.IDENTITY
