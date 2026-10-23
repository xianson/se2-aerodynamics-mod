"""LONGER HELICOPTER ROTOR BLADES (2026-10-23, the user: "longer blades", tip at 7 cells, thrust kept). Run on a main rotor's
export scene (HelicopterRotor_<N>_Export.blend; not the Reversible Rotor), saved in place - back the .blend up first:

  blender -b HelicopterRotor_150_Export.blend -P rotor_lengthen.py -- [tip_cells=7]

The blade's tip moves from 5 cells to tip_cells (the hinge stays at 0.5 cell). Only the blade's constant-chord middle is
stretched (span fraction 1/6.75 .. 6/6.75, d = distance from the hinge: exactly between the blade's two section rings
there, so the twisted surface between them stays ruled - a kink inside would crease it): the root grip and the swept, striped tip keep their
shape and size. The blur disc takes the same radial map (its rings keep their UVs, so the stripe band stays on the tip's)
and its cone height grows with it. The disc's occupancy layer (occupancy_<N>_1, and Ref_DiscLayer) becomes 2 x tip + 1
cells across. propulsor_spec.json (rotor_radius, the box, cells, the disc's occupancy) and rotor_anim_spec.json (tip,
blur radii) follow. Idempotent: the scene remembers the tip it was built to (aero_tip_cells).
"""
import bpy, json, math, os, sys

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
OPT = dict(a.split("=", 1) for a in argv if "=" in a)
TIP_CELLS = float(OPT.get("tip_cells", 7))
HERE = os.path.dirname(bpy.data.filepath)
PROP_SPEC, ANIM_SPEC = os.path.join(HERE, "propulsor_spec.json"), os.path.join(HERE, "rotor_anim_spec.json")
HINGE_CELLS, F0, F1 = 0.5, 1.0 / 6.75, 6.0 / 6.75

sc = bpy.context.scene
N = next(c.name.split("|")[0] for c in bpy.data.collections if c.name.endswith("|Blade_LOD0"))
assert "TailRotor" not in N, "the Reversible Rotor keeps its blades"
prop, anim = json.load(open(PROP_SPEC)), json.load(open(ANIM_SPEC))
cell = prop[N]["cell_m"]
old_cells = sc.get("aero_tip_cells", 5.0)
if abs(old_cells - TIP_CELLS) < 1e-6:
    print(f"LENGTHEN {N}: already at {TIP_CELLS} cells"); sys.exit(0)
ri = HINGE_CELLS * cell
span = (old_cells - HINGE_CELLS) * cell
extra = (TIP_CELLS - old_cells) * cell
d0, d1 = F0 * span, F1 * span
m = (d1 - d0 + extra) / (d1 - d0)
f = lambda d: d if d <= d0 else d0 + (d - d0) * m if d <= d1 else d + extra


def blade(o):
    """Span along Blender -Y from the hinge (the origin)."""
    for v in o.data.vertices:
        d = -v.co.y
        if d > 0: v.co.y = -f(d)
    o.data.update()


def blur(o):
    """Radial from the axis; the hinge ring at ri; heights grow with the ring's distance from the hinge."""
    for v in o.data.vertices:
        r = math.hypot(v.co.x, v.co.y); d = r - ri
        if d <= 1e-6: continue
        k = (ri + f(d)) / r
        v.co.x *= k; v.co.y *= k; v.co.z *= f(d) / d
    o.data.update()


def disc_box(o, cells):
    vs = o.data.vertices; cx = sum(v.co.x for v in vs) / len(vs); cy = sum(v.co.y for v in vs) / len(vs)
    w = max(v.co.x for v in vs) - min(v.co.x for v in vs); k = cells * cell / w
    for v in vs: v.co.x = cx + (v.co.x - cx) * k; v.co.y = cy + (v.co.y - cy) * k
    o.data.update()


for n in range(3):
    sfx = f"_LOD{n}" if n else ""
    blade(bpy.data.objects[f"{N}_Blade{sfx}"]); blur(bpy.data.objects[f"{N}_Blur{sfx}"])
cells = int(round(2 * TIP_CELLS + 1))
for nm in (f"occupancy_{N}_1", "Ref_DiscLayer"):
    if nm in bpy.data.objects: disc_box(bpy.data.objects[nm], cells)
sc["aero_tip_cells"] = TIP_CELLS
bpy.ops.wm.save_mainfile()

# the specs (VRage block space: x, z the disc's plane)
tip, half = TIP_CELLS * cell, cells * cell / 2
s = prop[N]
s["rotor_radius"] = round(tip, 3)
for key in ("box_lo", "box_hi"): s[key] = [math.copysign(half, s[key][0]), s[key][1], math.copysign(half, s[key][2])]
s["box_m"] = [2 * half, s["box_m"][1], -2 * half]
s["cells_vrage"] = [int(round(2 * half / 0.25)), s["cells_vrage"][1], int(round(2 * half / 0.25))]
disc = max(s["occupancy"], key=lambda b: abs(b[1][0] - b[0][0]))
for p in disc: p[0], p[2] = math.copysign(half, p[0]), math.copysign(half, p[2])
a = anim[N]
a["tip_radius"] = round(tip, 4); a["blur_outer_r"] = round(tip, 4)
if "blur_band_r" in a: a["blur_band_r"] = round(ri + f(a["blur_band_r"] - ri), 4)
json.dump(prop, open(PROP_SPEC, "w"), indent=1); json.dump(anim, open(ANIM_SPEC, "w"), indent=1)
print(f"LENGTHEN {N}: tip {old_cells} -> {TIP_CELLS} cells ({tip} m), middle x{m:.3f}, disc layer {cells} cells ({2 * half} m)")
