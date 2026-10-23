"""The small control surface = the large file at 0.2x, everything included (LODs, flap + hinge pivot, Fractured and
Deformed sets, colliders, the Deformed displacement scale). Object scales stay 1 (mesh data is scaled), except the box
collider, which is a unit cube scaled to the envelope.

Run: blender -b <ControlSurface_Large_3x3x1.blend> -P make_small.py -- <ControlSurface_Small_3x3x1.blend>
The other sizes (2026-10-23) are the same copy at their own scale and name (gen_cs3_defs.py SIZES: b / 2.5 m):
     blender -b <ControlSurface_Large_3x3x1.blend> -P make_small.py -- <ControlSurface_100_3x3x1.blend> 0.4 100
"""
import bpy, sys
from mathutils import Matrix

ARGS = sys.argv[sys.argv.index("--") + 1:]
OUT = ARGS[0]
K = float(ARGS[1]) if len(ARGS) > 1 else 0.2
NAME = ARGS[2] if len(ARGS) > 2 else "Small"

done = set()
for o in bpy.data.objects:
    if o.type == "MESH" and o.name != "Collision" and o.data.name not in done:
        o.data.transform(Matrix.Scale(K, 4)); done.add(o.data.name)
    if o.name == "Collision": o.scale = o.scale * K
    o.location = o.location * K
    if o.type == "EMPTY": o.empty_display_size *= K
    for m in getattr(o, "modifiers", []):
        if m.type == "DISPLACE": m.strength *= K
for t in bpy.data.textures:
    if t.type == "CLOUDS": t.noise_scale *= K
v = getattr(bpy.context.scene, "vrt", None)
if v is not None:
    v.export_directory = v.export_directory.replace("Large", NAME); v.export_name = v.export_name.replace("Large", NAME)
bpy.ops.wm.save_as_mainfile(filepath=OUT, copy=True)
print(NAME.upper(), "saved", OUT, "x", K, "meshes scaled", len(done))
