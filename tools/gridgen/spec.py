"""spec - writing the spawner's text spec and the sidecar JSON, and reading a spec back (for validation)."""
from __future__ import annotations

import json
import math

import numpy as np

import se2blocks as sb


def _v(p):
    return " ".join(str(int(round(float(c)))) for c in p)


def write(path, m, sidecar_path=None):
    """Writes the spec for Model m; returns (main block list, sidecar dict)."""
    cat = m.cat
    d = m.d
    blocks = m.main_blocks()
    lat = m.lat
    ext_m = (np.array(lat.shape) * lat.B).max()
    sp = d.get("spawn", {})
    lines = [f"# gridgen: {d.get('title', d['name'])}  ({d['grid']} grid)",
             "# B prefab X Y Z FWD UP | H index min max | F hinge prefab X Y Z FWD UP  (cells of 0.25 m)",
             f"name {d.get('title', d['name'])}",
             f"ahead {sp.get('ahead', int(round(max(60, ext_m * 0.8 + 30))))}",
             f"up {sp.get('up', 10)}",
             f"static {int(sp.get('static', 1))}"]
    hinge_rows = []
    for i, b in enumerate(blocks):
        lines.append(f"B {cat[b['name']].guid} {_v(b['pos'])} {b['orient'][0]} {b['orient'][1]}")
        if b["kind"] == "hinge":
            hinge_rows.append((i, b["seg"]))
    for k, (i, sg) in enumerate(hinge_rows):
        lo, hi = sg["limits"]
        lines.append(f"H {i} {lo:g} {hi:g}   # {sg['role']} {sg['side']} on {sg['surface']}")
    for k, (i, sg) in enumerate(hinge_rows):
        for f in sg["flaps"]:
            lines.append(f"F {k} {cat[f['name']].guid} {_v(f['hg_pos'])} {f['hg_orient'][0]} {f['hg_orient'][1]}")
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(lines) + "\n")
    side = sidecar(m, blocks, hinge_rows)
    if sidecar_path:
        with open(sidecar_path, "w", encoding="utf-8") as fh:
            json.dump(side, fh, indent=1)
    return blocks, side


def _r(x, n=3):
    return [round(float(v), n) for v in x] if hasattr(x, "__len__") else round(float(x), n)


def sidecar(m, blocks, hinge_rows):
    d = m.d
    M, c = m.com()
    items = m.mass_items()
    kinds = {}
    for mass, _, k in items:
        kinds.setdefault(k, [0, 0.0])
        kinds[k][0] += 1
        kinds[k][1] += mass
    npr = m.np_res or {}
    margin = (npr["x_np"] - c[0]) / npr["mac"] if npr else None
    hinges = []
    for k, (i, sg) in enumerate(hinge_rows):
        hinges.append(dict(hinge=k, block=i, role=sg["role"], side=sg["side"], surface=sg["surface"],
                           limits=list(sg["limits"]), positive_moves_te=sg["positive_moves_te"],
                           axis=list(sg["axis"]), span_axis="XYZ"[sg["span_axis"]],
                           hinge_pos=[int(v) for v in sg["hinge_pos"]], hinge_orient=list(sg["hinge_orient"]),
                           pivot_cells=list(sg["pivot_cells"]), flap_blocks=len(sg["flaps"]),
                           span_m=round(len(sg["columns"]) * m.lat.B, 2),
                           flap_mass_kg=round(sum(m.cat[f["name"]].mass for f in sg["flaps"]), 1)))
    cav = np.argwhere(m.cavity0)
    return dict(
        name=d["name"], title=d.get("title", d["name"]), grid=d["grid"], block_m=m.lat.B,
        source=("mesh " + d["mesh"]) if d.get("mesh") else "parametric",
        mass_kg=round(M, 1), com_m=_r(c), com_cells=_r(np.array([c[1], c[2], c[0]]) / sb.CELL_M, 2),
        neutral_point_m=_r(npr.get("x_np", float("nan"))), mac_m=_r(npr.get("mac", float("nan"))),
        mac_le_m=_r(npr.get("x_mac_le", float("nan"))), wing_ref_area_m2=_r(npr.get("S_ref", float("nan")), 1),
        fuselage_np_shift_m=_r(npr.get("dx_fuselage", 0.0)), downwash=_r(npr.get("downwash", 0.0)),
        np_surfaces=npr.get("surfaces", []),
        static_margin_target=d["static_margin"], static_margin=_r(margin, 4) if margin is not None else None,
        com_target_m=_r(m.target["x"]) if m.target else None, com_tolerance_m=_r(m.target["tol"]) if m.target else None,
        com_ok=bool(m.com_ok), wing_shift_m=round(float(getattr(m, "wing_dx", 0.0)), 3),
        counts={k: v[0] for k, v in sorted(kinds.items())}, mass_by_kind={k: round(v[1], 1) for k, v in sorted(kinds.items())},
        main_blocks=len(blocks), flap_blocks=sum(len(sg["flaps"]) for _, sg in hinge_rows),
        batteries=[dict(name=b["name"], pos=[int(v) for v in b["pos"]]) for b in m.batteries],
        cockpit=m.cockpit,
        engines=[dict(index=e["index"], mount=e["mount"], side=e["side"], prefab=e["name"], type=e["type"],
                      pos=[int(v) for v in e["pos"]], orient=list(e["orient"]), exhaust=list(e["exhaust"]),
                      nudged_blocks=e["nudged"], embedded=e["embedded"], pylon_blocks=e.get("pylon", 0),
                      lanes=[[[int(v) for v in l], [int(v) for v in h], t] for l, h, t in e["lanes"]])
                 for e in m.engines],
        hinges=hinges,
        roles={r: sum(1 for h in hinges if h["role"] == r) for r in sorted({h["role"] for h in hinges})},
        cavity_voxels=int(len(cav)),
        lattice=dict(unit_cells=m.lat.u, i0=[int(v) for v in m.lat.i0], shape=list(m.lat.shape)),
        cavity=[[int(v) for v in row] for row in cav],
        calibration={k: sb.STATUS.get(k, "?") for k in sb.STATUS},
        problems=list(m.problems))


def parse(path, cat):
    """Reads a spec back.  Returns dict(main=[(name, pos, orient)], hinges=[(index, min, max)],
    flaps={hinge: [(name, pos, orient)]}, header)."""
    main, hinges, flaps, hdr = [], [], {}, {}
    for raw in open(path, encoding="utf-8"):
        line = raw.split("#")[0].strip()
        if not line:
            continue
        t = line.split()
        if t[0] == "B":
            main.append((cat.name_of_guid(t[1]), np.array([int(x) for x in t[2:5]]), (t[5], t[6])))
        elif t[0] == "H":
            hinges.append((int(t[1]), float(t[2]), float(t[3])))
        elif t[0] == "F":
            flaps.setdefault(int(t[1]), []).append((cat.name_of_guid(t[2]), np.array([int(x) for x in t[3:6]]),
                                                    (t[6], t[7])))
        else:
            hdr[t[0]] = line[len(t[0]):].strip()
    return dict(main=main, hinges=hinges, flaps=flaps, header=hdr)
