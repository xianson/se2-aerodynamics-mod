"""preview - three views, an isometric and a centreline cutaway of a generated plane, blocks coloured by kind.

Blocks are drawn as their boxes (flaps at 0 degrees), painter-sorted per view.  The CoM (circle), neutral point
(diamond) and CoM target (tick) are marked on the side and top views; exhaust/intake lanes are dashed.
"""
from __future__ import annotations

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402
import numpy as np  # noqa: E402
from matplotlib.collections import PolyCollection  # noqa: E402
from matplotlib.patches import Patch, Rectangle  # noqa: E402

import se2blocks as sb  # noqa: E402

# reference categorical palette (dataviz skill), neutral greys for the airframe
COLORS = {
    "shell": "#b9bec7", "slope": "#d9dce1", "surface": "#9aa3b0", "pylon": "#6b7280",
    "engine": "#eb6834", "battery": "#1baf7a", "cockpit": "#2a78d6", "hinge": "#4a3aa7", "flap": "#eda100",
}
LABELS = {"shell": "hull shell", "slope": "hull slope/corner", "surface": "wing / tail", "pylon": "pylon",
          "engine": "thruster", "battery": "battery", "cockpit": "cockpit", "hinge": "hinge", "flap": "flap (head grid)"}
INK, INK2, SURF = "#0b0b0b", "#52514e", "#fcfcfb"


def gather(m, blocks):
    """(kind, lo(m, design xyz), hi) boxes for every block, flaps included."""
    out = []
    for b in blocks:
        k = b["kind"]
        if k == "shell" and b.get("shape", "cube") != "cube":
            k = "slope"
        out.append((k, np.asarray(b["lo"], float), np.asarray(b["hi"], float)))
    for sg in m.segs:
        out.append(("hinge", np.asarray(sg["head_lo"], float), np.asarray(sg["head_hi"], float)))
        for f in sg["flaps"]:
            out.append(("flap", np.asarray(f["lo"], float), np.asarray(f["hi"], float)))
    res = []
    for k, lo, hi in out:      # cells (X, Y, Z) -> metres (x, y, z)
        lo_m = np.array([lo[2], lo[0], lo[1]]) * sb.CELL_M
        hi_m = np.array([hi[2], hi[0], hi[1]]) * sb.CELL_M
        res.append((k, lo_m, hi_m))
    return res


def _ortho(ax, boxes, u, v, depth, reverse=False, clip=None):
    """Rectangles in axes (u, v), painter-sorted along depth axis (ascending = farther first unless reverse)."""
    items = []
    for k, lo, hi in boxes:
        if clip is not None and not clip(lo, hi):
            continue
        d = (lo[depth] + hi[depth]) / 2
        items.append((-d if reverse else d, k, lo, hi))
    items.sort(key=lambda t: t[0])
    polys, cols = [], []
    for _, k, lo, hi in items:
        polys.append([(lo[u], lo[v]), (hi[u], lo[v]), (hi[u], hi[v]), (lo[u], hi[v])])
        cols.append(COLORS[k])
    ax.add_collection(PolyCollection(polys, facecolors=cols, edgecolors=SURF, linewidths=0.4))


def _iso(ax, boxes):
    # view direction from front-right-above; x aft, y right, z up
    view = np.array([1.0, -1.0, 0.8])
    e1 = np.array([1.0, 1.0, 0.0]) / np.sqrt(2)
    e2 = np.cross(view / np.linalg.norm(view), e1)
    e2 = -e2 if e2[2] < 0 else e2
    faces = []
    shade = {"top": 1.0, "side": 0.78, "front": 0.62}
    for k, lo, hi in boxes:
        c = matplotlib.colors.to_rgb(COLORS[k])
        x0, y0, z0 = lo
        x1, y1, z1 = hi
        quads = {
            "top": [(x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)],
            "side": [(x0, y0, z0), (x1, y0, z0), (x1, y0, z1), (x0, y0, z1)],      # the y-min (left) face
            "front": [(x0, y0, z0), (x0, y1, z0), (x0, y1, z1), (x0, y0, z1)],     # the nose-facing face
        }
        # pick the faces that look toward the viewer
        if view[0] > 0:
            quads["front"] = [(x1, y0, z0), (x1, y1, z0), (x1, y1, z1), (x1, y0, z1)]
        if view[1] > 0:
            quads["side"] = [(x0, y1, z0), (x1, y1, z0), (x1, y1, z1), (x0, y1, z1)]
        for f, q in quads.items():
            q = np.array(q)
            depth = float((q.mean(0) @ view))
            pts = np.stack([q @ e1, q @ e2], 1)
            faces.append((depth, pts, tuple(np.array(c) * shade[f])))
    faces.sort(key=lambda t: t[0])
    ax.add_collection(PolyCollection([f[1] for f in faces], facecolors=[f[2] for f in faces], edgecolors=(1, 1, 1, 0.25),
                                     linewidths=0.2))
    allp = np.concatenate([f[1] for f in faces])
    ax.set_xlim(allp[:, 0].min() - 2, allp[:, 0].max() + 2)
    ax.set_ylim(allp[:, 1].min() - 2, allp[:, 1].max() + 2)


def render(m, blocks, side, path):
    boxes = gather(m, blocks)
    allb = np.array([[lo, hi] for _, lo, hi in boxes])
    lo, hi = allb[:, 0].min(0) - 2, allb[:, 1].max(0) + 2
    fig = plt.figure(figsize=(16, 10), facecolor=SURF)
    gs = fig.add_gridspec(2, 3, width_ratios=[1.25, 1.25, 1.0])
    axs = {k: fig.add_subplot(gs[i, j]) for k, i, j in
           (("top", 0, 0), ("side", 0, 1), ("front", 1, 2), ("iso", 1, 0), ("cut", 1, 1), ("info", 0, 2))}
    for a in axs.values():
        a.set_facecolor(SURF)
        for s in a.spines.values():
            s.set_color("#d0d0cc")
        a.tick_params(colors=INK2, labelsize=7)
    com = side["com_m"]
    npx = side["neutral_point_m"]
    tgt = side["com_target_m"]

    def marks(ax, vaxis):
        if com is None:
            return
        cv = com[vaxis]
        ax.plot([com[0]], [cv], "o", ms=10, mfc="white", mec=INK, mew=1.6, zorder=10)
        ax.plot([com[0]], [cv], "+", ms=10, mec=INK, mew=1.4, zorder=11)
        if npx == npx:
            ax.plot([npx], [cv], "D", ms=8, mfc="#e34948", mec="white", mew=1.0, zorder=10)
        if tgt is not None:
            ax.plot([tgt, tgt], [cv - 1.5, cv + 1.5], "-", color=INK, lw=1.2, zorder=9)

    def lanes(ax, u, v):
        for e in side["engines"]:
            for l, h, t in e["lanes"]:
                l = np.array([l[2], l[0], l[1]]) * sb.CELL_M
                h = np.array([h[2], h[0], h[1]]) * sb.CELL_M
                ax.add_patch(Rectangle((l[u], l[v]), h[u] - l[u], h[v] - l[v], fill=False, ls="--", lw=0.8,
                                       ec="#eb6834" if t == "exhaust" else "#2a78d6", zorder=8))

    a = axs["top"]
    _ortho(a, boxes, 0, 1, 2)
    lanes(a, 0, 1)
    marks(a, 1)
    a.set_xlim(lo[0], hi[0]); a.set_ylim(lo[1], hi[1]); a.set_aspect("equal"); a.invert_yaxis()
    a.set_title("top (nose left)", color=INK, fontsize=10, loc="left")
    a = axs["side"]
    _ortho(a, boxes, 0, 2, 1, reverse=True)
    lanes(a, 0, 2)
    marks(a, 2)
    a.set_xlim(lo[0], hi[0]); a.set_ylim(lo[2], hi[2]); a.set_aspect("equal")
    a.set_title("side (from the left)", color=INK, fontsize=10, loc="left")
    a = axs["front"]
    _ortho(a, boxes, 1, 2, 0, reverse=True)
    a.set_xlim(lo[1], hi[1]); a.set_ylim(lo[2], hi[2]); a.set_aspect("equal")
    a.set_title("front", color=INK, fontsize=10, loc="left")
    a = axs["cut"]
    B = side["block_m"]
    _ortho(a, boxes, 0, 2, 1, reverse=True, clip=lambda l, h: (l[1] + h[1]) / 2 > -B * 0.25)
    marks(a, 2)
    a.set_xlim(lo[0], hi[0]); a.set_ylim(lo[2], hi[2]); a.set_aspect("equal")
    a.set_title("cutaway: right half from the left (hollow, batteries, cockpit)", color=INK, fontsize=10, loc="left")
    a = axs["iso"]
    _iso(a, boxes)
    a.set_aspect("equal"); a.set_xticks([]); a.set_yticks([])
    a.set_title("isometric", color=INK, fontsize=10, loc="left")
    a = axs["info"]
    a.axis("off")
    margin = side["static_margin"]
    roles = ", ".join(f"{k} {v}" for k, v in side["roles"].items()) or "none"
    eng = {}
    for e in side["engines"]:
        eng[(e["mount"], e["prefab"])] = eng.get((e["mount"], e["prefab"]), 0) + 1
    eng_s = "; ".join(f"{n}x {p} ({mt})" for (mt, p), n in eng.items()) or "none"
    txt = [
        (side["title"], 13, INK, "bold"),
        (f"{side['grid']} grid, {side['source']}", 9, INK2, "normal"),
        ("", 6, INK, "normal"),
        (f"mass {side['mass_kg'] / 1000:.1f} t   blocks {side['main_blocks']} main + {side['flap_blocks']} flap", 10, INK,
         "normal"),
        (f"CoM x {com[0]:.2f} m   (y {com[1]:+.2f}, z {com[2]:+.2f})", 10, INK, "normal"),
        (f"neutral point x {npx:.2f} m   MAC {side['mac_m']:.2f} m", 10, INK, "normal"),
        (f"static margin {margin * 100:.1f}% MAC  (target {side['static_margin_target'] * 100:.0f}%)  "
         f"{'OK' if side['com_ok'] else 'OFF TARGET'}", 10, INK, "bold"),
        (f"wing group shifted {side['wing_shift_m']:+.2f} m" if side["wing_shift_m"] else "wing at design station",
         9, INK2, "normal"),
        ("", 6, INK, "normal"),
        (f"engines: {eng_s}", 9, INK, "normal"),
        (f"hinges {len(side['hinges'])}: {roles}", 9, INK, "normal"),
        (f"batteries {len(side['batteries'])}   cockpit {side['cockpit']['name'] if side['cockpit'] else 'none'}", 9,
         INK, "normal"),
    ]
    if side.get("validation"):
        v = side["validation"]
        txt.append(("", 6, INK, "normal"))
        txt.append((f"validation: {'PASS' if v['ok'] else 'FAIL'}  " + ", ".join(
            k for k, r in v["checks"].items() if not r["ok"]), 10, INK, "bold"))
    y = 1.0
    for s, size, col, w in txt:
        a.text(0.0, y, s, fontsize=size, color=col, weight=w, transform=a.transAxes, va="top")
        y -= 0.012 + size * 0.0062
    present = sorted({k for k, _, _ in boxes}, key=list(COLORS).index)
    handles = [Patch(fc=COLORS[k], ec="none", label=LABELS[k]) for k in present]
    handles += [plt.Line2D([], [], marker="o", ls="", mfc="white", mec=INK, label="centre of mass"),
                plt.Line2D([], [], marker="D", ls="", mfc="#e34948", mec="white", label="neutral point"),
                plt.Line2D([], [], ls="--", color="#eb6834", label="exhaust lane"),
                plt.Line2D([], [], ls="--", color="#2a78d6", label="intake lane")]
    a.legend(handles=handles, loc="lower left", bbox_to_anchor=(0.0, -0.08), frameon=False, fontsize=8, ncol=2,
             labelcolor=INK)
    fig.tight_layout()
    fig.savefig(path, dpi=110, facecolor=SURF)
    plt.close(fig)
