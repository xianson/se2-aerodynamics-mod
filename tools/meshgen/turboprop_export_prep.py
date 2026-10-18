"""A turboprop rig file (turboprop_rig.py) -> the propulsor EXPORT scene layout (propulsor_prep.py's names, read by
propulsor_export.py), saved as a NEW file; the rig file is only read:
  N|Body_LOD0..3, N|Rotor_LOD0..2 (the Prop), N|Fan_LOD0..2, N|Fractured_LOD0..3, N|Deformed_LOD0..3, N|Collision,
  N|Collision_Fractured
Subparts: the mesh is already baked in its pivot's frame; here the object goes to the origin (identity, no preview driver), as
propulsor_prep.py's Rotor - the exporter writes the subpart about its own origin, the dummy places it.
Also merges the block's entry into propulsor_spec.json (back the spec up before the first run).

  blender -b <Turboprop_N_rig.blend> -P turboprop_export_prep.py -- <out.blend> <rig_spec.json> <propulsor_spec.json>
"""
import bpy, json, os, shutil, sys, time
from mathutils import Matrix

OUT, RIG_SPEC, PSPEC = sys.argv[sys.argv.index("--") + 1:][:3]
rs = json.load(open(RIG_SPEC)); N = rs["name"]
C = bpy.data.collections; O = bpy.data.objects

# (2026-10-17, the rotors' blur treatment: the spinner is the old "Rotor" subpart - Subpart01, the vanilla spin -, the blades
#  their own "Blade" subpart and the blur disc "Blur" - propulsor_export.py puts a turboprop's Blade in Subpart04, its Fan
#  keeping Subpart02)
for g, new in (("Spinner", "Rotor"), ("Blades", "Blade"), ("Blur", "Blur"), ("Fan", "Fan")):
    for n in range(4):
        if g == "Blur" and n == 3: continue
        c = C[f"{g}_LOD{n}"]
        if n == 3:   # (subparts: LOD0..2, as the exporter and gd.model take them; the rig file keeps LOD3)
            for o in list(c.objects): O.remove(o)
            C.remove(c); continue
        for o in c.objects:
            o.animation_data_clear(); o.matrix_world = Matrix.Identity(4)
            o.name = f"{N}_{new}" + (f"_LOD{n}" if n else "")
        c.name = f"{N}|{new}_LOD{n}"
for n in range(4):
    for o in C[f"Body_LOD{n}"].objects: o.name = f"{N}_Body" + (f"_LOD{n}" if n else "")
    C[f"Body_LOD{n}"].name = f"{N}|Body_LOD{n}"
    C[f"Fractured_LOD{n}"].name = f"{N}|Fractured_LOD{n}"
    C[f"Deformed_LOD{n}"].name = f"{N}|Deformed_LOD{n}"
for nm in ("Fractured", "Deformed", "Collision", "Collision_Fractured", "Dummies", "Ref"):
    C[nm].name = f"{N}|{nm}"
bpy.context.scene.frame_set(1)
bpy.ops.wm.save_as_mainfile(filepath=OUT, copy=False)

# the block's propulsor spec entry (propulsor_prep.py's fields + the fan subpart)
Vb = lambda v: [round(v[0], 5), round(v[2], 5), round(-v[1], 5)]
lo, hi = rs["box"]["lo"], rs["box"]["hi"]
size_m = [hi[i] - lo[i] for i in range(3)]
cells = [round(x / 0.25) for x in size_m]
entry = {"name": N, "source": "Turboprop_" + rs["size"] + "_rig.blend", "kind": "turboprop", "size": rs["size"], "cell_m": rs["cell_m"],
         "box_m": Vb(size_m), "box_lo": Vb(lo), "box_hi": Vb(hi), "cells_vrage": [cells[0], cells[2], cells[1]],
         "subpart": rs["pivots"]["Prop"]["vrage"], "fan": rs["pivots"]["Fan"]["vrage"],
         "blade": rs["pivots"]["Blades"]["vrage"], "blur": rs["blur"]["vrage"],
         "blur_disc": {k: rs["blur"][k] for k in ("inner_r", "band_r", "outer_r", "aft_of_prop_pivot")},
         "spin_axis": [0.0, 0.0, -1.0], "thrust_axis": [0.0, 0.0, -1.0],
         "rotor_rotation_axis": rs["spin"]["Prop"]["vrage_rotation_axis"], "fan_rotation_axis": rs["spin"]["Fan"]["vrage_rotation_axis"],
         "rotor_radius": round(rs["spin"]["Prop"]["tip_radius"], 3),
         "materials": {"Body": rs["materials"]["Body"], "Rotor": rs["materials"]["Spinner"], "Blade": rs["materials"]["Blades"], "Fan": rs["materials"]["Fan"]},
         "occupancy": [[Vb(a), Vb(b)] for a, b in rs["occupancy"]]}
specs = json.load(open(PSPEC))
specs[N] = entry
json.dump(specs, open(PSPEC, "w"), indent=1)
print(f"TPEXP {N}: saved {OUT}; spec entry subpart {entry['subpart']} blade {entry['blade']} blur {entry['blur']} fan {entry['fan']} radius {entry['rotor_radius']} cells {entry['cells_vrage']}")
