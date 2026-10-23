"""A NEW HELICOPTER ROTOR SIZE from an existing one, scaled whole (2026-10-23, the user: "i want a 1m rotor" - the 0.5 m
rotor x 2). Every object of the export scene - body, hub, blades (both sets), blur disc, colliders, occupancy and mount
slabs, fracture pieces, dummies - is scaled about the block origin, and every name carrying the source size takes the new
one. Saved as a NEW file next to the source (the source is not touched); propulsor_spec.json and rotor_anim_spec.json get
the new block's entry (the source's x the scale, the new size / cell).

  blender -b HelicopterRotor_050_Export.blend -P rotor_scale_size.py -- 050 100 2.0

Then: propulsor_export.py on the new file, the icon (rotor_icon_assemble.py + render_icon.py), gen_propulsor_defs.py
(ROTOR_TIP / PCU / SIZE_SID know the size).
"""
import bpy, copy, json, os, sys

SRC, DST, K = sys.argv[sys.argv.index("--") + 1:][:3]
K = float(K)
HERE = os.path.dirname(bpy.data.filepath)
PROP_SPEC, ANIM_SPEC = os.path.join(HERE, "propulsor_spec.json"), os.path.join(HERE, "rotor_anim_spec.json")
NS, ND = f"HelicopterRotor_{SRC}", f"HelicopterRotor_{DST}"
assert NS in os.path.basename(bpy.data.filepath)
out = os.path.join(HERE, f"{ND}_Export.blend")
assert not os.path.exists(out), f"{out} exists - move it away first"

done = set()
for o in bpy.data.objects:
    if o.type == "MESH" and o.data not in done:
        o.data.transform(__import__("mathutils").Matrix.Scale(K, 4)); o.data.update(); done.add(o.data)
    o.location = o.location * K   # (children too: their offsets in the parent's frame)
    o.name = o.name.replace(NS, ND)
    if o.get("ExportName"): o["ExportName"] = o["ExportName"].replace(NS, ND)
    if o.type == "EMPTY": o.empty_display_size *= K
for me in bpy.data.meshes: me.name = me.name.replace(NS, ND)
for c in bpy.data.collections: c.name = c.name.replace(NS, ND)
bpy.ops.wm.save_as_mainfile(filepath=out, copy=True)

# the specs: positions and lengths x K (VRage block space), the new size and cell
LEN = {"rotor_radius", "hinge_radius", "tip_radius", "plane_below_hub", "blur_inner_r", "blur_band_r", "blur_outer_r", "cell", "cell_m"}
def scaled(v, key=None):
    if isinstance(v, dict): return {k: scaled(x, k) for k, x in v.items()}
    if isinstance(v, list): return [scaled(x, key) for x in v]
    if isinstance(v, (int, float)) and not isinstance(v, bool) and key in POS: return round(v * K, 6)
    return v
POS = LEN | {"box_m", "box_lo", "box_hi", "subpart", "occupancy", "hub", "blur", "pos"}
prop, anim = json.load(open(PROP_SPEC)), json.load(open(ANIM_SPEC))
p = scaled(copy.deepcopy(prop[NS]))
p["name"], p["size"], p["source"] = ND, DST, f"{ND}_Export.blend"
p["cells_vrage"] = [int(round(abs(x) / 0.25)) for x in p["box_m"]]
prop[ND] = p
a = scaled(copy.deepcopy(anim[NS]))
a["source"] = f"{ND}_Export.blend"
anim[ND] = a
json.dump(prop, open(PROP_SPEC, "w"), indent=1); json.dump(anim, open(ANIM_SPEC, "w"), indent=1)
print(f"SCALE {NS} x{K} -> {out}; tip {a['tip_radius']} m, box {p['box_m']}, cells {p['cells_vrage']}")
