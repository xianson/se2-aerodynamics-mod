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
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import build as bd  # noqa: E402
import design as dz  # noqa: E402
import preview  # noqa: E402
import se2blocks as sb  # noqa: E402
import spec as specio  # noqa: E402
import validate as val  # noqa: E402


def run_design(d, cat, out, do_preview=True, log=print):
    t0 = time.time()
    name = d["name"]
    m = bd.build(d, cat, log=lambda s: log(s))
    spath = os.path.join(out, name + ".txt")
    jpath = os.path.join(out, name + ".json")
    blocks, side = specio.write(spath, m)
    v = val.validate(spath, side, cat)
    # generator-side failures (an engine with no clear spot, no room for the cockpit...) fail the design too
    v["checks"]["generator"] = dict(ok=not side["problems"], detail="; ".join(side["problems"]))
    v["ok"] = v["ok"] and not side["problems"]
    side["validation"] = v
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
    args = p.parse_args(argv)
    return {"all": cmd_all, "build": cmd_build, "mesh": cmd_mesh, "validate": cmd_validate, "cal": cmd_cal}[args.cmd](args)


if __name__ == "__main__":
    sys.exit(main())
