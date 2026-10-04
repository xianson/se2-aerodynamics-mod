"""gridgen - airplane designs and meshes -> Space Engineers 2 block specs for the Orbital Mod 'gen spawn' harness.

  python gridgen.py all                     regenerate the whole library + previews, validate each, run unit tests
  python gridgen.py build designs/x.json    one design (add --cubes for cubes only, --out DIR, --no-preview)
  python gridgen.py mesh plane.obj --grid large --length 40 [--axes "x y z"] [--name N]
                                            any closed (or near-closed) OBJ/STL straight through the same pipeline
  python gridgen.py validate NAME           re-check a written spec (NAME.txt + NAME.json in the output folder)
  python gridgen.py cal                     calibration specs: armor shape orientations (compare with the preview)

Outputs (default %TEMP%\\OrbitalMod\\gen): NAME.txt (spec: in game 'gen spawn NAME.txt'), NAME.json (sidecar:
mass, CoM, neutral point, margin, hinge roles, block counts, validation), preview\\NAME.png.
The library (designs/*.json, sketch_plane first) uses the clean style (clean.py): a constant-section body with
45-degree nose/tail cones, flat constant-thickness wing and tail plates, square-ish flaps, optional nacelle pods.
designs/legacy/ keeps the earlier smooth-style designs (design.py schema with "surfaces"; build them one by one).
Engines: engines.py; control surfaces: controls.py; block conventions and calibration status: se2blocks.py.
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import numpy as np  # noqa: E402
import build as bd  # noqa: E402
import design as dz  # noqa: E402
import preview  # noqa: E402
import se2blocks as sb  # noqa: E402
import spec as specio  # noqa: E402
import validate as val  # noqa: E402


def _build_and_check(d, cat, spath, log):
    m = bd.build(d, cat, log=lambda s: log(s))
    blocks, side = specio.write(spath, m)
    v = val.validate(spath, side, cat)
    # generator-side failures (an engine with no clear spot, no room for the cockpit...) fail the design too
    v["checks"]["generator"] = dict(ok=not side["problems"], detail="; ".join(side["problems"]))
    v["ok"] = v["ok"] and not side["problems"]
    side["validation"] = v
    return m, blocks, side


def probe_available():
    return os.path.exists(os.path.join(PROBE_DIR, "AeroProbe.csproj")) and shutil.which("dotnet") is not None


def run_design(d, cat, out, do_preview=True, log=print, probe_trim=True):
    """Build, write, validate.  Then (when the aero probe can run) trim to the aero mod's OWN neutral point: probe the
    plane without the thruster intakes (a mod-side ram drag, not the airframe), and while the measured NP - CoM misses
    static_margin x MAC by more than a quarter of it, shift the neutral-point estimate by the miss and rebuild (the
    batteries and the wing slide then move the CoM).  The best passing build is kept."""
    t0 = time.time()
    name = d["name"]
    spath = os.path.join(out, name + ".txt")
    jpath = os.path.join(out, name + ".json")
    m, blocks, side = _build_and_check(d, cat, spath, log)
    if probe_trim and probe_available() and d.get("probe_trim", True) and m.np_res is not None:
        want = float(side["static_margin_target"]) * float(m.np_res["mac"])
        shift0 = float(d.get("np_shift", 0.0))
        best = None
        tried = []

        def judge(shift, m, blocks, side):
            nonlocal best
            # judged in true flow (one airflow direction for every grid, flap head grids included; moments about the
            # whole plane's CoM): what the in-game TestForces readout measures since aero 3ea3f9e (sketch_plane: NP
            # 0.74 m in game, 0.70 m here)
            r = probe_spec(spath, cat)
            off = r.get("NP_aft_of_CoM")
            if off is None:
                return None
            side["aero_probe"] = dict(view="true flow, plane CoM", np_aft_of_com_m=round(off, 3),
                                      want_m=round(want, 3), np_shift_m=round(shift, 3), dM_dAoA_kNm=r.get("dMdAoA_kNm"),
                                      CL4=r.get("CL4"), CL8=r.get("CL8"), CD0=r.get("CD0"))
            # passing first; then a real margin (NP at least half the wanted margin behind the CoM); then closest
            key = (not side["validation"]["ok"], off < 0.5 * want, abs(off - want))
            if best is None or key < best[0]:
                best = (key, m, blocks, side, open(spath, encoding="utf-8").read())
            tried.append((shift, off, side["validation"]["ok"]))
            return off

        def build_at(shift):
            d2 = dict(d)
            d2["np_shift"] = shift
            return _build_and_check(d2, cat, spath, log)

        off0 = judge(shift0, m, blocks, side)
        if off0 is not None and abs(off0 - want) > max(0.25 * want, 0.03):
            # the probe's NP moves in steps with each rebuild (the wing slides in block steps, the flaps re-plan):
            # a small scan around the estimated shift rather than an iteration that chases that noise
            s1 = shift0 + (off0 - want)
            log(f"    aero probe: NP {off0:+.2f} m aft of the CoM (want {want:.2f}): scanning NP shifts around {s1:+.2f} m")
            dlt = s1 - shift0
            # the estimate, then partial steps (the CoM / NP response is not 1:1 and moves in steps: the wing slide
            # snaps to blocks and saturates), stopping at the first build within a quarter of the wanted margin
            for f in (1.0, 0.75, 0.5, 0.9, 0.6, 0.4, 1.1, 0.25):
                s = shift0 + f * dlt
                m2, b2, s2 = build_at(s)
                off = judge(s, m2, b2, s2)
                if off is not None and abs(off - want) <= max(0.25 * want, 0.03) and s2["validation"]["ok"]:
                    break
            # then bisect between the nearest passing builds either side of the target (NP - CoM falls as the shift
            # grows), while the best is still outside a quarter of the wanted margin
            for _ in range(4):
                if abs(best[0][2]) <= max(0.25 * want, 0.03) and not best[0][0]:
                    break
                above = [t for t in tried if t[2] and t[1] > want]
                below = [t for t in tried if t[2] and t[1] < want]
                if not above or not below:
                    break
                a = min(above, key=lambda t: t[1] - want)
                b = min(below, key=lambda t: want - t[1])
                s = a[0] + (b[0] - a[0]) * (a[1] - want) / (a[1] - b[1])   # (linear between them)
                if any(abs(s - t[0]) < 0.02 for t in tried):
                    s = 0.5 * (a[0] + b[0])
                    if any(abs(s - t[0]) < 0.02 for t in tried):
                        break
                judge(s, *build_at(s))
        if best is not None:
            _, m, blocks, side, txt = best
            with open(spath, "w", encoding="utf-8") as fh:
                fh.write(txt)
    with open(jpath, "w", encoding="utf-8") as fh:
        json.dump(side, fh, indent=1)
    if do_preview:
        os.makedirs(os.path.join(out, "preview"), exist_ok=True)
        preview.render(m, blocks, side, os.path.join(out, "preview", name + ".png"))
    side["_secs"] = time.time() - t0
    return m, side


def summary_line(side):
    v = side["validation"]
    bad = ",".join(k for k, c in v["checks"].items() if not c["ok"])
    return (f"{side['name']:<16} {side['grid']:<5} {side['mass_kg'] / 1000:8.1f} t  CoM {side['com_m'][0]:7.2f}  "
            f"NP {side['neutral_point_m']:7.2f}  MAC {side['mac_m']:6.2f}  SM {side['static_margin'] * 100:5.1f}%  "
            f"hinges {len(side['hinges']):3d}  eng {len(side['engines']):2d}  bat {len(side['batteries']):2d}  "
            f"{'PASS' if v['ok'] else 'FAIL ' + bad}  ({side['_secs']:.1f}s)")


def cmd_all(args):
    cat = sb.Catalog()
    out = args.out
    os.makedirs(out, exist_ok=True)
    subprocess.run([sys.executable, os.path.join(HERE, "meshes", "make_meshes.py")], check=True)
    files = sorted(glob.glob(os.path.join(HERE, "designs", "*.json")),
                   key=lambda f: (os.path.basename(f) != "sketch_plane.json", f))     # the user's sketch first
    if args.only:
        files = [f for f in files if os.path.splitext(os.path.basename(f))[0] in args.only.split(",")]
    rows, ok = [], True
    print(f"gridgen: {len(files)} designs -> {out}")
    for f in files:
        d = dz.load(f)
        print(f"  {d['name']} ...")
        m, side = run_design(d, cat, out, not args.no_preview, log=lambda s: print(s))
        line = summary_line(side)
        rows.append(line)
        ok &= side["validation"]["ok"]
        for k, c in side["validation"]["checks"].items():
            if not c["ok"]:
                print(f"      FAIL {k}: {c['detail']}")
        for p in side["problems"]:
            print(f"      note: {p}")
    print()
    print("\n".join(rows))
    os.makedirs(os.path.join(out, "preview"), exist_ok=True)
    with open(os.path.join(out, "preview", "summary.txt"), "w", encoding="utf-8") as fh:
        fh.write("\n".join(rows) + "\n")
    cmd_cal(args)
    if not args.no_tests:
        r = subprocess.run([sys.executable, "-m", "unittest", "discover", "-s", os.path.join(HERE, "tests")],
                           cwd=HERE)
        ok &= r.returncode == 0
    print(f"\n== gridgen: {'PASSED' if ok else 'FAILED'}")
    return 0 if ok else 1


def cmd_build(args):
    cat = sb.Catalog()
    d = dz.load(args.file)
    if args.cubes:
        d["skin"] = "cubes"
    os.makedirs(args.out, exist_ok=True)
    m, side = run_design(d, cat, args.out, not args.no_preview)
    print(summary_line(side))
    for k, c in side["validation"]["checks"].items():
        print(f"  {'ok  ' if c['ok'] else 'FAIL'} {k:<11} {c['detail']}")
    for p in side["problems"]:
        print(f"  note: {p}")
    return 0 if side["validation"]["ok"] else 1


def cmd_mesh(args):
    cat = sb.Catalog()
    name = args.name or os.path.splitext(os.path.basename(args.file))[0]
    d = {"name": name, "title": name, "grid": args.grid, "mesh": os.path.abspath(args.file), "mesh_axes": args.axes,
         "_path": os.path.abspath(args.file)}
    if args.length:
        d["mesh_length"] = args.length
    if args.cubes:
        d["skin"] = "cubes"
    os.makedirs(args.out, exist_ok=True)
    m, side = run_design(d, cat, args.out, not args.no_preview)
    print(summary_line(side))
    return 0 if side["validation"]["ok"] else 1


def cmd_validate(args):
    cat = sb.Catalog()
    side = json.load(open(os.path.join(args.out, args.name + ".json"), encoding="utf-8"))
    v = val.validate(os.path.join(args.out, args.name + ".txt"), side, cat)
    for k, c in v["checks"].items():
        print(f"  {'ok  ' if c['ok'] else 'FAIL'} {k:<11} {c['detail']}")
    return 0 if v["ok"] else 1


def block_cell_lines(cat, name, pos, orient):
    """A block's occupied cells as .boxes lines (inclusive, game cell indexing).  Plain blocks: their AABB.  Armor
    shapes: the 0.25 m cells holding a positive volume of the cut cube (solid where n.p <= cut, p in [-0.5, 0.5] of
    the block) - as the game's occupied-cell groups make a slope a staircase (a small slope: 6 of 8 cells, a corner
    4).  The same rule reproduces the measured mount counts (slope sides 3 of 4, corner faces 3 of 4)."""
    lo, hi = cat.box(name, pos, orient)
    lo, hi = np.asarray(lo), np.asarray(hi)
    off = sb.CAL["aabb_low_offset"]
    shape = sb.shape_of(name)
    if shape in (None, "cube") or sb.SHAPE_CUT.get(shape) is None:
        return [" ".join(str(int(v)) for v in list(lo + off) + list(hi))]
    n = sb.orient_matrix(*orient) @ np.array(sb.CAL["shape_normal"][shape], float)
    cut = sb.SHAPE_CUT[shape]
    size = (hi - lo).astype(float)
    out = []
    for i in range(int(size[0])):
        for j in range(int(size[1])):
            for k in range(int(size[2])):
                c = (np.array([i, j, k]) + 0.5) / size - 0.5          # sub-cell centre, block units
                half = 0.5 / size
                if float(n @ c) - float(np.abs(n) @ half) < cut - 1e-9:   # positive volume on the solid side
                    g = lo + np.array([i, j, k]) + off
                    out.append(" ".join(str(int(v)) for v in list(g) + list(g)))
    return out


def export_boxes(spec_path, out_path, cat, flaps=False):
    """A spec as AeroBench/aero_probe .boxes: each main-grid block's occupied cells, inclusive, in the GAME's cell
    indexing (the catalog's AABB: continuous [lo, hi) is cells lo+1..hi), flaps optional (in
    game they are separate head grids, so the main grid's own aero sees the wing without them)."""
    sp = specio.parse(spec_path, cat)
    grid = "small"
    lines = []
    for name, pos, orient in sp["main"]:
        lines += block_cell_lines(cat, name, pos, orient)
        if name.startswith("Armor") and "250" in name:      # (armor names the grid: ...Light50 small, ...Light250
            grid = "large"                                  #  large; Battery250 etc. are small-grid blocks too)
    if flaps:
        for k, (bi, mn, mx) in enumerate(sp["hinges"]):
            hname, hpos, horient = sp["main"][bi]
            origin, Rh = sb.headgrid_frame(hpos, horient)
            for fname, fpos, forient in sp["flaps"].get(k, []):
                p, o = sb.from_headgrid(fpos, forient, origin, Rh)
                lines += block_cell_lines(cat, fname, p, o)
    with open(out_path, "w", encoding="utf-8") as fh:
        fh.write(f"# blockSize {2.5 if grid == 'large' else 0.5}\n" + "\n".join(lines) + "\n")


PROBE_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "aero_probe")


def probe_spec(spec_path, cat, work=None, speed=220.0, intake=True, testforces=False, com_ref="plane"):
    """The aero mod's own model on a spec, offline (aero_probe): the main grid plus every hinge head grid as a
    separate aero grid (as in game), true airflow, torques about the mass CoM.  Returns the probe's SUMMARY as a dict
    (CL4, CL8, CD0 on the frontal area, NP_aft_of_CoM in m, frontal m2, drag0_kN) plus the full text."""
    import subprocess
    import tempfile
    sp = specio.parse(spec_path, cat)
    work = work or tempfile.mkdtemp(prefix="probe_")
    os.makedirs(work, exist_ok=True)
    base = os.path.splitext(os.path.basename(spec_path))[0]
    files = [os.path.join(work, base + "_main.boxes")]
    export_boxes(spec_path, files[0], cat)
    M = np.zeros(3)
    W = 0.0

    def add(name, p, o):
        nonlocal M, W
        lo, hi = cat.box(name, p, o)
        m = float(cat[name].mass)
        M += m * (np.asarray(lo) + np.asarray(hi)) / 2.0
        W += m
        return lo, hi
    thr = []
    for name, pos, orient in sp["main"]:
        lo, hi = add(name, pos, orient)
        if name.startswith("AtmosphericThruster"):          # the mod's intakes: air-breathing thrusters only
            # as BlockComponentFactory.BuildBlockInfo: occupied cells (game indices lo+1..hi), centre in m, min cell,
            # the block's Forward
            gmin = np.asarray(lo) + sb.CAL["aabb_low_offset"]
            gmax = np.asarray(hi)
            c = (gmin + gmax) * 0.5 * sb.CELL_M
            f = sb.orient_matrix(*orient) @ np.array([0, 0, -1])
            width = {"100": 0.5, "250": 1.25, "500": 2.5, "1000": 7.5}.get(name[len("AtmosphericThruster"):], 0.5)
            thr += ["--thruster", ",".join(f"{x:g}" for x in list(c) + list(gmin) + list(f) + [0.25 * width * width])]
    M_main, W_main = M.copy(), W
    for k, (bi, mn, mx) in enumerate(sp["hinges"]):
        hname, hpos, horient = sp["main"][bi]
        origin, Rh = sb.headgrid_frame(hpos, horient)
        lines = []
        for fname, fpos, forient in sp["flaps"].get(k, []):
            p, o = sb.from_headgrid(fpos, forient, origin, Rh)
            add(fname, p, o)
            lines += block_cell_lines(cat, fname, p, o)
        if lines:
            f = os.path.join(work, f"{base}_head{k:02d}.boxes")
            # the head grid's axes in the main frame (row-major): TestForces blows each grid along its OWN -Z
            frame = "# frame " + " ".join(f"{x:g}" for x in np.asarray(Rh, float).ravel())
            open(f, "w", encoding="utf-8").write("# blockSize 0.5" + chr(10) + frame + chr(10) + chr(10).join(lines)
                                                 + chr(10))
            files.append(f)
    # moment reference: the whole plane's CoM, or ("main") the main grid's own - what the game's physics gives the
    # main grid, whose torque the TestForces readout reports (the flap head grids are separate bodies)
    com = (M_main / W_main if com_ref == "main" else M / W) + 0.5   # (continuous cells -> game cell indices)
    exe = [p for p in (os.path.join(PROBE_DIR, "bin", "Release", "net9.0", "AeroProbe.dll"),) if os.path.exists(p)]
    if not exe:
        subprocess.run(["dotnet", "build", "-c", "Release"], cwd=PROBE_DIR, check=True, capture_output=True)
        exe = [os.path.join(PROBE_DIR, "bin", "Release", "net9.0", "AeroProbe.dll")]
    extra = thr + ([] if intake else ["--no-intake"]) + (["--testforces"] if testforces else [])
    out = subprocess.run(["dotnet", exe[0]] + files + ["--speed", str(speed), "--com", ",".join(f"{c:.3f}" for c in com),
                                                      "--quiet"] + extra, capture_output=True, text=True,
                         check=True).stdout
    res = {"text": out}
    for line in out.splitlines():
        if "SUMMARY" in line:
            t = line.split()
            for i in range(t.index("SUMMARY") + 1, len(t) - 1, 2):
                v = t[i + 1].replace("m", "")
                try:
                    res[t[i]] = float(v)
                except ValueError:
                    res[t[i]] = None
    return res


def cmd_probe(args):
    """The aero probe over designs in true flow (one airflow direction for every grid, flap head grids included; the
    whole plane's CoM): what the in-game TestForces readout measures.  The mod's intakes (atmospheric thrusters,
    0.25 x width^2) included.  NP + = behind the CoM (stable); dM/dAoA - = stable (+X torque is nose up).  CL / CD on
    the readout's reference area (see aero_probe).  --old-view adds the earlier per-grid-axes view."""
    cat = sb.Catalog()
    names = args.names or [os.path.splitext(f)[0] for f in sorted(os.listdir(os.path.join(HERE, "designs")))
                           if f.endswith(".json")]
    print(f"{'design':16s} {'CL4':>6s} {'CL8':>6s} {'CD0':>6s} {'dM/da':>8s} {'NP m':>6s} {'NP%MAC':>7s} {'target':>7s}  flags")
    for n in names:
        src = n if n.endswith(".txt") else os.path.join(args.out, n + ".txt")
        side = {}
        try:
            side = json.load(open(os.path.splitext(src)[0] + ".json", encoding="utf-8"))
        except (OSError, ValueError):
            pass
        mac = side.get("mac_m") or float("nan")
        tgt = side.get("static_margin_target")
        r = probe_spec(src, cat)
        if args.verbose:
            print(r["text"])
        v = r.get("NP_aft_of_CoM")
        v = float("nan") if v is None else v
        flags = [x for x, bad in (("CD0>0.6", r.get("CD0", 9) > 0.6), ("CL8<0.3", r.get("CL8", 0) < 0.3),
                                  ("UNSTABLE", not v > 0)) if bad]
        print(f"{os.path.basename(n):16s} {r.get('CL4', 0):6.3f} {r.get('CL8', 0):6.3f} {r.get('CD0', 0):6.3f} "
              f"{r.get('dMdAoA_kNm', float('nan')):8.1f} {v:6.2f} {100 * v / mac:6.1f}% "
              f"{(100 * tgt if tgt else float('nan')):6.1f}%  {' '.join(flags)}")
    return 0


def cmd_boxes(args):
    cat = sb.Catalog()
    src = args.spec if os.path.isabs(args.spec) or os.path.exists(args.spec) else os.path.join(args.out, args.spec)
    if not src.endswith(".txt"):
        src += ".txt"
    dst = args.to or os.path.splitext(src)[0] + (".flaps" if args.flaps else "") + ".boxes"
    export_boxes(src, dst, cat, args.flaps)
    print("wrote", dst)
    return 0


def cmd_cal(args):
    """A row of large cubes, each with one slope / corner / inverted corner on top, oriented by the CAL
    shape_normal convention.  Each B line's comment says which way the slanted face should look in game; if it
    does not, fix shape_normal (calibration.txt) - nothing else depends on it."""
    import numpy as np
    cat = sb.Catalog()
    lines = ["# gridgen calibration: armor shapes on cubes, 7.5 m apart along +X (Right).  Each comment says where",
             "# the slanted face should point (grid axes: Forward = the camera's forward at spawn, Up = up).",
             "# halfslope = ArmorHalfLongSlope: a half-height wedge lying on the cube, thick end away from the",
             "# direction named second (its slanted face mostly up, tilted toward that direction).",
             "name CalShapes", "ahead 40", "up 5", "static 1"]
    cases = [("slope", (0, 1, -1)), ("slope", (0, 1, 1)), ("slope", (1, 1, 0)), ("corner", (1, 1, -1)),
             ("invcorner", (1, 1, -1)), ("corner", (-1, 1, 1)), ("halfslope", (0, 2, -1)), ("halfslope", (0, 2, 1)),
             ("halfslope", (2, 1, 0))]
    for i, (shape, n) in enumerate(cases):
        x = 30 * i
        o = sb.orient_for_normal(shape, n)
        faces = "+".join(("mostly " if abs(c) == 2 else "") + sb.dname(v)
                         for v, c in zip(np.diag(np.sign(n)), n) if c)
        lines.append(f"B {cat['ArmorCubeLight250'].guid} {x} 0 0 Forward Up")
        lines.append(f"B {cat[sb.armor(shape, 'large')].guid} {x} 10 0 {o[0]} {o[1]}   # {shape}: slanted face "
                     f"toward {faces}")
    path = os.path.join(args.out, "cal_shapes.txt")
    with open(path, "w", encoding="utf-8") as fh:
        fh.write("\n".join(lines) + "\n")
    print("wrote", path)
    return 0


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--out", default=sb.GEN_DIR, help="output folder (default %%TEMP%%\\OrbitalMod\\gen)")
    sub = p.add_subparsers(dest="cmd", required=True)
    a = sub.add_parser("all")
    a.add_argument("--only", help="comma-separated design names")
    a.add_argument("--no-preview", action="store_true")
    a.add_argument("--no-tests", action="store_true")
    b = sub.add_parser("build")
    b.add_argument("file")
    b.add_argument("--cubes", action="store_true")
    b.add_argument("--no-preview", action="store_true")
    m = sub.add_parser("mesh")
    m.add_argument("file")
    m.add_argument("--grid", default="large", choices=["large", "small"])
    m.add_argument("--length", type=float)
    m.add_argument("--axes", default="x y z")
    m.add_argument("--name")
    m.add_argument("--cubes", action="store_true")
    m.add_argument("--no-preview", action="store_true")
    v = sub.add_parser("validate")
    v.add_argument("name")
    sub.add_parser("cal")
    bx = sub.add_parser("boxes")
    bx.add_argument("spec")
    bx.add_argument("--to")
    bx.add_argument("--flaps", action="store_true")
    pr = sub.add_parser("probe")
    pr.add_argument("names", nargs="*", help="design names (default: every design) or spec paths")
    pr.add_argument("-v", "--verbose", action="store_true")
    args = p.parse_args(argv)
    return {"all": cmd_all, "build": cmd_build, "mesh": cmd_mesh, "validate": cmd_validate, "cal": cmd_cal,
            "boxes": cmd_boxes, "probe": cmd_probe}[args.cmd](args)


if __name__ == "__main__":
    sys.exit(main())
