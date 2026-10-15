"""The small control surface = the large file at 0.2x, everything included (LODs, flap + hinge pivot, Fractured and
Deformed sets, colliders, the Deformed displacement scale). Object scales stay 1 (mesh data is scaled), except the box
collider, which is a unit cube scaled to the envelope.

Run: blender -b <ControlSurface_Large_3x3x1.blend> -P make_small.py -- <ControlSurface_Small_3x3x1.blend>
"""
import bpy, sys
from mathutils import Matrix

OUT = sys.argv[sys.argv.index("--") + 1]
K = 0.2

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
    v.export_directory = v.export_directory.replace("Large", "Small"); v.export_name = v.export_name.replace("Large", "Small")
bpy.ops.wm.save_as_mainfile(filepath=OUT, copy=True)
print("SMALL saved", OUT, "meshes scaled", len(done))
