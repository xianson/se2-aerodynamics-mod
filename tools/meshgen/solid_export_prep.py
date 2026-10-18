"""A solid rocket rig file (solid_rig.py) -> the propulsor EXPORT scene layout (ramjet_export_prep.py's names, read by
propulsor_export.py), saved as a NEW file; the rig file is only read:
  N|Body_LOD0..3, N|Fractured_LOD0..3, N|Deformed_LOD0..3, N|Collision, N|Collision_Fractured (+ N|Dummies, N|Ref: not exported)
No subparts (no moving parts). Also merges the block's entry into a SOLID spec file (solid_spec.json - NOT propulsor_spec.json,
which gen_propulsor_defs.py reads as its block list); the entry is in VRage block space (x, z, -y), box-centred, without the
+0.125 LOD0 model offset - propulsor_spec.json's conventions.

  blender -b <SolidX_N_rig.blend> -P solid_export_prep.py -- <out.blend> <rig_spec.json> <solid_spec.json>
"""
import bpy, json, os, sys

OUT, RIG_SPEC, SSPEC = sys.argv[sys.argv.index("--") + 1:][:3]
rs = json.load(open(RIG_SPEC)); N = rs["name"]
C = bpy.data.collections

for n in range(4):
    for o in C[f"Body_LOD{n}"].objects: o.name = f"{N}_Body" + (f"_LOD{n}" if n else "")
    C[f"Body_LOD{n}"].name = f"{N}|Body_LOD{n}"
    C[f"Fractured_LOD{n}"].name = f"{N}|Fractured_LOD{n}"
    C[f"Deformed_LOD{n}"].name = f"{N}|Deformed_LOD{n}"
for nm in ("Fractured", "Deformed", "Collision", "Collision_Fractured", "Dummies", "Ref"):
    C[nm].name = f"{N}|{nm}"
bpy.ops.wm.save_as_mainfile(filepath=OUT, copy=False)

Vb = lambda v: [round(v[0], 5), round(v[2], 5), round(-v[1], 5)]
lo, hi = rs["box"]["lo"], rs["box"]["hi"]
size_m = [hi[i] - lo[i] for i in range(3)]
q = [round(x / 0.25) for x in size_m]
nozzle = rs["kind"] == "solid_nozzle"
FACE = {"+Z": "Up", "-Z": "Down"}
entry = {"name": N, "source": f"{N}_rig.blend (Solid_kit.blend {rs['collection']})", "kind": rs["kind"], "size": rs["size"],
         "cell_m": rs["cell_m"], "cells": [rs["cells"]["x"], rs["cells"]["z"], rs["cells"]["y"]],
         "box_m": [size_m[0], size_m[2], size_m[1]], "box_lo": Vb(lo), "box_hi": Vb(hi), "cells_vrage": [q[0], q[2], q[1]],
         "subpart": None, "spin_axis": None,
         "thrust_axis": [0.0, 1.0, 0.0] if nozzle else None, "thrust_direction": "Up" if nozzle else None,
         "exhaust": rs["exhaust"]["vrage"] if nozzle else None,
         "exhaust_exit_radius": rs["exhaust"]["exit_radius"] if nozzle else None,
         "exhaust_dummy": "Dummy_Exhaust (the bell exit on the axis; the exhaust leaves along VRage -Y)" if nozzle else None,
         "materials": rs["materials"],
         "occupancy": [[Vb(a), Vb(b)] for a, b in rs["occupancy"]],
         "mount_faces": [FACE[f] for f in rs["mount_faces"]],
         "mount_faces_vrage": {FACE[f]: [Vb(m["lo"]), Vb(m["hi"])] for f, m in rs["mount_faces"].items()},
         "body_bbox_vrage": [Vb(rs["body_bbox"]["lo"]), Vb(rs["body_bbox"]["hi"])],
         "polycount": rs["polycount"],
         "export_dir": rf"D:\Blender Brojects\SE2\TurbopropBlock\Export\{N}"}
specs = json.load(open(SSPEC)) if os.path.exists(SSPEC) else {}
specs[N] = entry
json.dump(specs, open(SSPEC, "w"), indent=1)
print(f"SREXP {N}: saved {OUT}; cells {entry['cells_vrage']} box {entry['box_lo']} .. {entry['box_hi']} exhaust {entry['exhaust']} mounts {entry['mount_faces']}")
