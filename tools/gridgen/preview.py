"""preview - a large shaded 3D view plus top / side / front / cutaway of a generated plane.

The 3D view draws every armor block as its real shape (the unit cube clipped by the shape's plane: slopes, corners,
inverted corners, half-slopes) in its orientation, culls faces hidden against neighbouring cubes and back faces,
shades by a fixed light (Lambert) and paints far-to-near.  Other blocks (thrusters, batteries, cockpit, hinges, flaps)
are drawn as their boxes.  The ortho views show block boxes coloured by kind with the CoM (circle), neutral point
(diamond), CoM target (tick) and the engine lanes (dashed).
"""
from __future__ import annotations

import functools

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402
import numpy as np  # noqa: E402
from matplotlib.collections import PolyCollection  # noqa: E402
from matplotlib.patches import Patch, Rectangle  # noqa: E402

import se2blocks as sb  # noqa: E402

# reference categorical palette (dataviz skill); neutral greys for the airframe
COLORS = {
    "shell": "#b9bec7", "slope": "#d9dce1", "surface": "#9aa3b0", "pylon": "#6b7280",
    "engine": "#eb6834", "battery": "#1baf7a", "cockpit": "#2a78d6", "hinge": "#4a3aa7", "flap": "#eda100",
}
LABELS = {"shell": "hull shell", "slope": "shaped skin (slope/corner/half-slope)", "surface": "wing / tail",
          "pylon": "pylon", "engine": "thruster", "battery": "battery", "cockpit": "cockpit", "hinge": "hinge",
          "flap": "flap (head grid)"}
INK, INK2, SURF = "#0b0b0b", "#52514e", "#fcfcfb"
VIEW = np.array([-0.85, -1.0, 0.8])          # toward the viewer, design axes (x aft, y right, z up): front-left-above
VIEW = VIEW / np.linalg.norm(VIEW)
LIGHT = np.array([-0.3, -0.5, 1.0])
LIGHT = LIGHT / np.linalg.norm(LIGHT)


def _g2d(v):
    """grid (X, Y, Z) -> design (x, y, z)."""
    v = np.asarray(v, float)
    return np.stack([v[..., 2], v[..., 0], v[..., 1]], -1)


# ----------------------------------------------------------------------------------------------- shape geometry
CUBE_FACES = []
for ax in range(3):
    for s in (-1, 1):
        o = [i for i in range(3) if i != ax]
        quad = []
        for a, b in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
            p = [0.0, 0.0, 0.0]
            p[ax], p[o[0]], p[o[1]] = 0.5 * s, 0.5 * a, 0.5 * b
            quad.append(p)
        nrm = [0, 0, 0]
        nrm[ax] = s
        CUBE_FACES.append((np.array(quad), tuple(nrm)))


def _clip(poly, n, c):
    """Sutherland-Hodgman: keep the part of a convex polygon with n.p <= c."""
    out = []
    k = len(poly)
    for i in range(k):
        a, b = poly[i], poly[(i + 1) % k]
        da, db = a @ n - c, b @ n - c
        if da <= 1e-9:
            out.append(a)
        if (da < -1e-9 and db > 1e-9) or (da > 1e-9 and db < -1e-9):
            out.append(a + (b - a) * (da / (da - db)))
    return np.array(out)


@functools.lru_cache(maxsize=None)
def shape_faces(shape, orient):
    """[(polygon (k,3) in grid axes of a unit cell centred at 0, outward normal, cube-face direction or None)]"""
    M = sb.orient_matrix(*orient)
    cut = sb.SHAPE_CUT.get(shape)
    faces = []
    if cut is None:
        for q, d in CUBE_FACES:
            faces.append(((M @ q.T).T, M @ np.array(d), tuple(int(x) for x in M @ np.array(d))))
        return faces
    n = np.array(sb.CAL["shape_normal"][shape], float)
    for q, d in CUBE_FACES:
        p = _clip(q, n, cut)
        if len(p) >= 3:
            faces.append(((M @ p.T).T, M @ np.array(d), tuple(int(x) for x in M @ np.array(d))))
    # the slanted cap: the plane's intersection with the cube, ordered around its centre
    pts = []
    for q, d in CUBE_FACES:
        k = len(q)
        for i in range(k):
            a, b = q[i], q[(i + 1) % k]
            da, db = a @ n - cut, b @ n - cut
            if abs(da) < 1e-9:
                pts.append(a)
            elif da * db < 0:
                pts.append(a + (b - a) * (da / (da - db)))
    if len(pts) >= 3:
        pts = np.unique(np.round(np.array(pts), 6), axis=0)
        cen = pts.mean(0)
        nn = n / np.linalg.norm(n)
        u = np.cross(nn, [1, 0, 0] if abs(nn[0]) < 0.9 else [0, 1, 0])
        u /= np.linalg.norm(u)
        w = np.cross(nn, u)
        ang = np.arctan2((pts - cen) @ w, (pts - cen) @ u)
        cap = pts[np.argsort(ang)]
        faces.append(((M @ cap.T).T, M @ nn, None))
    return faces


@functools.lru_cache(maxsize=None)
def _tiled_box_faces(size, tile):
    """A box's faces (cells, centred at 0) split into tile x tile squares; cube-face direction None (never culled)."""
    size = np.array(size, float)
    out = []
    for q, d in CUBE_FACES:
        ax = int(np.nonzero(d)[0][0])
        o = [i for i in range(3) if i != ax]
        n = [max(1, int(round(size[k] / tile))) for k in o]
        for i in range(n[0]):
            for j in range(n[1]):
                quad = []
                for a, b in ((0, 0), (1, 0), (1, 1), (0, 1)):
                    p = np.zeros(3)
                    p[ax] = 0.5 * d[ax] * size[ax]
                    p[o[0]] = -size[o[0]] / 2 + (i + a) * size[o[0]] / n[0]
                    p[o[1]] = -size[o[1]] / 2 + (j + b) * size[o[1]] / n[1]
                    quad.append(p)
                out.append((np.array(quad), np.array(d, float), None))
    return out


# ----------------------------------------------------------------------------------------------- gathering
def gather(m, blocks):
    """(kind, name, orient, lo, hi) of every block (cells, grid frame), flaps at 0 degrees."""
    out = []
    for b in blocks:
        k = b["kind"]
        if k == "shell" and b.get("shape", "cube") != "cube":
            k = "slope"
        out.append((k, b["name"], tuple(b["orient"]), np.asarray(b["lo"], float), np.asarray(b["hi"], float)))
    for sg in m.segs:
        out.append(("hinge", sg["head_name"], sb.orient_names(sg["Rh"]), np.asarray(sg["head_lo"], float),
                    np.asarray(sg["head_hi"], float)))
        for f in sg["flaps"]:
            out.append(("flap", f["name"], tuple(f["orient"]), np.asarray(f["lo"], float),
                        np.asarray(f["hi"], float)))
    return out


def _boxes_m(items):
    res = []
    for k, name, o, lo, hi in items:
        a, b = _g2d(lo) * sb.CELL_M, _g2d(hi) * sb.CELL_M
        res.append((k, np.minimum(a, b), np.maximum(a, b)))
    return res


def _ortho(ax, boxes, u, v, depth, reverse=False, clip=None):
    items = []
    for k, lo, hi in boxes:
        if clip is not None and not clip(lo, hi):
            continue
        d = (lo[depth] + hi[depth]) / 2
        items.append((-d if reverse else d, k, lo, hi))
    items.sort(key=lambda t: t[0])
    polys = [[(lo[u], lo[v]), (hi[u], lo[v]), (hi[u], hi[v]), (lo[u], hi[v])] for _, k, lo, hi in items]
    cols = [COLORS[k] for _, k, lo, hi in items]
    ax.add_collection(PolyCollection(polys, facecolors=cols, edgecolors=SURF, linewidths=0.25))


def _shaded(ax, items, u_cells):
    """True-shape shaded 3D view."""
    e1 = np.cross([0, 0, 1.0], VIEW)
    e1 /= np.linalg.norm(e1)
    e2 = np.cross(VIEW, e1)
    full = set()
    for k, name, o, lo, hi in items:
        if sb.shape_of(name) == "cube" and (hi - lo).max() == u_cells and k not in ("flap", "hinge"):
            full.add(tuple(((lo + hi) / 2).astype(int)))
    polys, cols, depth = [], [], []
    for k, name, o, lo, hi in items:
        shape = sb.shape_of(name)
        base = np.array(matplotlib.colors.to_rgb(COLORS[k]))
        if shape in ("cube", "slope", "corner", "invcorner", "halfslope") and (hi - lo).max() <= u_cells + 1e-6 \
                and k not in ("flap",):
            c = (lo + hi) / 2
            if shape == "halfslope":
                c = None
            size = u_cells
            faces = shape_faces(shape if shape != "cube" else "cube", tuple(o))
            centre = np.round((lo + hi) / 2) if c is None else c
            if shape == "halfslope":       # its box is half a cell; the shape lives in the full cell around it
                M = sb.orient_matrix(*o)
                down = M @ np.array([0, -1, 0])
                centre = (lo + hi) / 2 - down * (u_cells / 4)
        else:
            faces = _tiled_box_faces(tuple(hi - lo), u_cells)     # block-sized tiles keep the painter's order right
            centre = (lo + hi) / 2
            size = 1.0
        for poly, nrm, cdir in faces:
            if cdir is not None and k not in ("flap", "hinge"):
                nb = tuple((centre + np.array(cdir) * u_cells).astype(int))
                if nb in full:
                    continue
            nd = _g2d(nrm)
            nd = nd / max(np.linalg.norm(nd), 1e-9)
            if nd @ VIEW <= 1e-6:
                continue
            P = _g2d(centre + poly * size) * sb.CELL_M
            shade = 0.42 + 0.58 * max(0.0, float(nd @ LIGHT))
            polys.append(np.stack([P @ e1, P @ e2], 1))
            cols.append(tuple(np.clip(base * shade, 0, 1)))
            depth.append(float(P.mean(0) @ VIEW))
    order = np.argsort(depth)
    ax.add_collection(PolyCollection([polys[i] for i in order], facecolors=[cols[i] for i in order],
                                     edgecolors=[tuple(np.clip(np.array(cols[i]) * 0.85, 0, 1)) for i in order],
                                     linewidths=0.15))
    allp = np.concatenate(polys)
    pad = 0.03 * (allp.max(0) - allp.min(0)).max()
    ax.set_xlim(allp[:, 0].min() - pad, allp[:, 0].max() + pad)
    ax.set_ylim(allp[:, 1].min() - pad, allp[:, 1].max() + pad)


def render(m, blocks, side, path):
    items = gather(m, blocks)
    boxes = _boxes_m(items)
    allb = np.array([[lo, hi] for _, lo, hi in boxes])
    lo, hi = allb[:, 0].min(0) - 1, allb[:, 1].max(0) + 1
    fig = plt.figure(figsize=(26, 20), facecolor=SURF)
    gs = fig.add_gridspec(3, 3, width_ratios=[1, 1, 0.9], height_ratios=[1, 1, 0.9])
    axs = {"iso": fig.add_subplot(gs[0:2, 0:2]), "info": fig.add_subplot(gs[0, 2]), "front": fig.add_subplot(gs[1, 2]),
           "top": fig.add_subplot(gs[2, 0]), "side": fig.add_subplot(gs[2, 1]), "cut": fig.add_subplot(gs[2, 2])}
    for a in axs.values():
        a.set_facecolor(SURF)
        for s in a.spines.values():
            s.set_color("#d0d0cc")
        a.tick_params(colors=INK2, labelsize=8)
    com = side["com_m"]
    npx = side["neutral_point_m"]
    tgt = side["com_target_m"]

    def marks(ax, vaxis):
        cv = com[vaxis]
        ax.plot([com[0]], [cv], "o", ms=11, mfc="white", mec=INK, mew=1.6, zorder=10)
        ax.plot([com[0]], [cv], "+", ms=11, mec=INK, mew=1.4, zorder=11)
        if npx == npx:
            ax.plot([npx], [cv], "D", ms=9, mfc="#e34948", mec="white", mew=1.0, zorder=10)
        if tgt is not None:
            span = 0.04 * (hi[0] - lo[0])
            ax.plot([tgt, tgt], [cv - span, cv + span], "-", color=INK, lw=1.2, zorder=9)

    def lanes(ax, u, v):
        for e in side["engines"]:
            for l, h, t in e["lanes"]:
                l = _g2d(l) * sb.CELL_M
                h = _g2d(h) * sb.CELL_M
                ax.add_patch(Rectangle((min(l[u], h[u]), min(l[v], h[v])), abs(h[u] - l[u]), abs(h[v] - l[v]),
                                       fill=False, ls="--", lw=0.9, ec="#eb6834" if t == "exhaust" else "#2a78d6",
                                       zorder=8))

    a = axs["iso"]
    _shaded(a, items, sb.GRID_UNIT[side["grid"]])
    a.set_aspect("equal")
    a.set_xticks([])
    a.set_yticks([])
    a.set_title("3D (true block shapes, from front-left-above)", color=INK, fontsize=13, loc="left")
    a = axs["top"]
    _ortho(a, boxes, 0, 1, 2)
    lanes(a, 0, 1)
    marks(a, 1)
    a.set_xlim(lo[0], hi[0]); a.set_ylim(lo[1], hi[1]); a.set_aspect("equal"); a.invert_yaxis()
    a.set_title("top (nose left), metres", color=INK, fontsize=11, loc="left")
    a = axs["side"]
    _ortho(a, boxes, 0, 2, 1, reverse=True)
    lanes(a, 0, 2)
    marks(a, 2)
    a.set_xlim(lo[0], hi[0]); a.set_ylim(lo[2], hi[2]); a.set_aspect("equal")
    a.set_title("side (from the left)", color=INK, fontsize=11, loc="left")
    a = axs["front"]
    _ortho(a, boxes, 1, 2, 0, reverse=True)
    a.set_xlim(lo[1], hi[1]); a.set_ylim(lo[2], hi[2]); a.set_aspect("equal")
    a.set_title("front", color=INK, fontsize=11, loc="left")
    a = axs["cut"]
    B = side["block_m"]
    _ortho(a, boxes, 0, 2, 1, reverse=True, clip=lambda l, h: (l[1] + h[1]) / 2 > -B * 0.25)
    marks(a, 2)
    a.set_xlim(lo[0], hi[0]); a.set_ylim(lo[2], hi[2]); a.set_aspect("equal")
    a.set_title("cutaway: right half from the left (hollow, batteries, cockpit)", color=INK, fontsize=11, loc="left")
    a = axs["info"]
    a.axis("off")
    margin = side["static_margin"]
    roles = ", ".join(f"{k} {v}" for k, v in side["roles"].items()) or "none"
    eng = {}
    for e in side["engines"]:
        key = (e["mount"], e["prefab"], e.get("count", 1))
        eng[key] = eng.get(key, 0) + 1
    eng_s = "; ".join(f"{n}x {p}{f' x{c}' if c > 1 else ''} ({mt})" for (mt, p, c), n in eng.items()) or "none"
    counts = side["counts"]
    txt = [
        (side["title"], 18, INK, "bold"),
        (f"{side['grid']} grid ({side['block_m']} m blocks), {side['source']}", 11, INK2, "normal"),
        ("", 6, INK, "normal"),
        (f"mass {side['mass_kg'] / 1000:.1f} t    blocks {side['total_blocks']} "
         f"({side['main_blocks']} main + {side['flap_blocks']} on hinge heads)", 12, INK, "normal"),
        (f"size {side['size_m'][0]:.1f} x {side['size_m'][1]:.1f} x {side['size_m'][2]:.1f} m (length x span x height)",
         12, INK, "normal"),
        (f"CoM x {com[0]:.2f} m   (y {com[1]:+.2f}, z {com[2]:+.2f})", 12, INK, "normal"),
        (f"neutral point x {npx:.2f} m   MAC {side['mac_m']:.2f} m", 12, INK, "normal"),
        (f"static margin {margin * 100:.1f}% MAC  (target {side['static_margin_target'] * 100:.0f}%)  "
         f"{'OK' if side['com_ok'] else 'OFF TARGET'}", 12, INK, "bold"),
        (f"wing group shifted {side['wing_shift_m']:+.2f} m" if side["wing_shift_m"] else "wing at design station",
         11, INK2, "normal"),
        ("", 6, INK, "normal"),
        (f"engines: {eng_s}", 11, INK, "normal"),
        (f"hinges {len(side['hinges'])}: {roles}", 11, INK, "normal"),
        (f"batteries {len(side['batteries'])}   cockpit {side['cockpit']['name'] if side['cockpit'] else 'none'}",
         11, INK, "normal"),
        (f"skin: {counts.get('shell', 0)} shell, {side.get('shaped_blocks', 0)} shaped (slopes/corners/half-slopes)",
         11, INK, "normal"),
    ]
    if side.get("validation"):
        v = side["validation"]
        txt.append(("", 6, INK, "normal"))
        txt.append((f"validation: {'PASS' if v['ok'] else 'FAIL'}  " + ", ".join(
            k for k, r in v["checks"].items() if not r["ok"]), 12, INK, "bold"))
    y = 1.0
    for s, size, col, w in txt:
        a.text(0.0, y, s, fontsize=size, color=col, weight=w, transform=a.transAxes, va="top")
        y -= 0.012 + size * 0.0058
    present = sorted({k for k, _, _ in boxes}, key=list(COLORS).index)
    handles = [Patch(fc=COLORS[k], ec="none", label=LABELS[k]) for k in present]
    handles += [plt.Line2D([], [], marker="o", ls="", mfc="white", mec=INK, label="centre of mass"),
                plt.Line2D([], [], marker="D", ls="", mfc="#e34948", mec="white", label="neutral point"),
                plt.Line2D([], [], ls="--", color="#eb6834", label="exhaust lane"),
                plt.Line2D([], [], ls="--", color="#2a78d6", label="intake lane")]
    axs["iso"].legend(handles=handles, loc="lower right", frameon=False, fontsize=11, ncol=2, labelcolor=INK)
    fig.tight_layout()
    fig.savefig(path, dpi=100, facecolor=SURF)
    plt.close(fig)
