"""A ramjet rig file (ramjet_rig.py) -> the propulsor EXPORT scene layout (turboprop_export_prep.py's names, read by
propulsor_export.py), saved as a NEW file; the rig file is only read:
  N|Body_LOD0..3, N|Spike_LOD0..2 (the translating spike subpart), N|Fractured_LOD0..3, N|Deformed_LOD0..3, N|Collision,
  N|Collision_Fractured (+ N|Dummies, N|Ref: not exported)
The spike: its mesh is already baked about its pivot (Dummy_Spike); here the object goes to the origin (identity, no preview
driver), as turboprop_export_prep.py's subparts - the exporter writes the subpart about its own origin, the dummy places it.
propulsor_export.py writes it to Models/NonFractured/Subpart01/<N>_S1_Spike.fbx (_LOD1..2).
Also merges the block's entry into propulsor_spec.json (back the spec up before the first run).

  blender -b <Ramjet_N_rig.blend> -P ramjet_export_prep.py -- <out.blend> <rig_spec.json> <propulsor_spec.json>
"""
import bpy, json, sys
from mathutils import Matrix

OUT, RIG_SPEC, PSPEC = sys.argv[sys.argv.index("--") + 1:][:3]
rs = json.load(open(RIG_SPEC)); N = rs["name"]
C = bpy.data.collections; O = bpy.data.objects

for n in range(4):
    c = C[f"Spike_LOD{n}"]
    if n == 3:   # (subparts: LOD0..2, as the exporter and gd.model take them; the rig file keeps LOD3)
        for o in list(c.objects): O.remove(o)
        C.remove(c); continue
    for o in c.objects:
        o.animation_data_clear(); o.matrix_world = Matrix.Identity(4)
        o.name = f"{N}_Spike" + (f"_LOD{n}" if n else "")
    c.name = f"{N}|Spike_LOD{n}"
for n in range(4):
    for o in C[f"Body_LOD{n}"].objects: o.name = f"{N}_Body" + (f"_LOD{n}" if n else "")
    C[f"Body_LOD{n}"].name = f"{N}|Body_LOD{n}"
    C[f"Fractured_LOD{n}"].name = f"{N}|Fractured_LOD{n}"
    C[f"Deformed_LOD{n}"].name = f"{N}|Deformed_LOD{n}"
for nm in ("Fractured", "Deformed", "Collision", "Collision_Fractured", "Dummies", "Ref"):
    C[nm].name = f"{N}|{nm}"
bpy.context.scene.frame_set(1)
bpy.ops.wm.save_as_mainfile(filepath=OUT, copy=False)

# the block's propulsor spec entry (turboprop_export_prep.py's fields; the spike instead of the rotor / fan)
Vb = lambda v: [round(v[0], 5), round(v[2], 5), round(-v[1], 5)]
lo, hi = rs["box"]["lo"], rs["box"]["hi"]
size_m = [hi[i] - lo[i] for i in range(3)]
cells = [round(x / 0.25) for x in size_m]
sp = rs["spike"]
entry = {"name": N, "source": f"Ramjet_{rs['size']}_rig.blend (Ramjet_kit.blend KIT_Atmo{rs['kit'][8:]})", "kind": "ramjet", "size": rs["size"],
         "cell_m": rs["cell_m"], "cells": [rs["cells"]["x"], rs["cells"]["z"], rs["cells"]["y"]],
         "box_m": Vb(size_m), "box_lo": Vb(lo), "box_hi": Vb(hi), "cells_vrage": [cells[0], cells[2], cells[1]],
         "subpart": sp["vrage"],
         "spike": {"pivot": sp["vrage"], "dummy": "Dummy_Spike", "subpart_fbx": f"NonFractured/Subpart01/{N}_S1_Spike.fbx",
                   "retract_axis": sp["retract_axis_vrage"], "free_aft_travel_m": sp["free_aft_travel_m"],
                   "forward_room_m": sp["forward_room_m"], "tip": Vb((0.0, sp["tip_y"], 0.0)), "max_radius": sp["max_radius"],
                   "rest": "as modelled = fully forward (tip on the box front face)"},
         "intake": rs["intake"]["vrage"], "exhaust": rs["exhaust"]["vrage"],
         "thrust_axis": [0.0, 0.0, -1.0], "spin_axis": None,
         "materials": {"Body": rs["materials"]["Body"], "Spike": rs["materials"]["Spike"]},
         "occupancy": [[Vb(a), Vb(b)] for a, b in rs["occupancy"]],
         "mount_faces": ["Left", "Right", "Up", "Down"],
         "mount_faces_vrage": {f: [Vb(m["lo"]), Vb(m["hi"])] for f, m in rs["mount_faces"].items()},
         "polycount": rs["polycount"]}
specs = json.load(open(PSPEC))
specs[N] = entry
json.dump(specs, open(PSPEC, "w"), indent=1)
print(f"RJEXP {N}: saved {OUT}; spec entry spike {entry['subpart']} travel {sp['free_aft_travel_m']} cells {entry['cells_vrage']} box {entry['box_lo']} .. {entry['box_hi']}")
