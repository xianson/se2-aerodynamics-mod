"""A pad-foot gear's icon (gen_padgear_defs.py --icons): its housing, leg, rod and foot FBX placed as in game (VRage positions from the
job file; VRage (x, y, z) is Blender (x, -z, y)), a 3/4 view from above, transparent, 200 x 200 (rendered at 400, Cycles - background
Blender has no GPU drawing). Never saves a .blend.

  blender -b --python padgear_icon.py -- <job.json>
"""
import bpy, json, sys
from mathutils import Vector

job = json.load(open(sys.argv[sys.argv.index("--") + 1:][0]))
bpy.ops.wm.read_factory_settings(use_empty=True)
sc = bpy.context.scene


def place(path, at, scale=1.0):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=path)
    new = [o for o in bpy.data.objects if o not in before]
    roots = [o for o in new if o.parent is None or o.parent not in new]
    off = Vector((at[0], -at[2], at[1]))
    for o in roots:
        o.matrix_world = __import__("mathutils").Matrix.Translation(off) @ __import__("mathutils").Matrix.Scale(scale, 4) @ o.matrix_world
    return [o for o in new if o.type == "MESH"]


objs = []
objs += place(job["housing"], (0.125, 0.125, 0.125))
objs += place(job["leg"][0], job["leg"][1])
objs += place(job["rod"][0], job["rod"][1])
objs += place(job["foot"][0], job["foot"][2], job["foot"][1])
bpy.context.view_layer.update()
lo = Vector((1e9,) * 3); hi = Vector((-1e9,) * 3)
for o in objs:
    for c in o.bound_box:
        w = o.matrix_world @ Vector(c); lo = Vector(map(min, lo, w)); hi = Vector(map(max, hi, w))
c = (lo + hi) / 2; r = (hi - lo).length
sc.render.engine = "CYCLES"; sc.cycles.samples = 32; sc.cycles.device = "CPU"
sc.render.resolution_x = sc.render.resolution_y = 400; sc.render.resolution_percentage = 50; sc.render.film_transparent = True
cam = bpy.data.objects.new("IconCam", bpy.data.cameras.new("IconCam")); sc.collection.objects.link(cam); sc.camera = cam
cam.data.type = "ORTHO"; cam.data.ortho_scale = r * 0.95
d = Vector((1.0, -1.3, 0.9)).normalized()
cam.location = c + d * r * 2; cam.rotation_euler = (c - cam.location).to_track_quat("-Z", "Y").to_euler()
for i, e in enumerate([(0.6, 0, 0.8), (1.2, 0, -0.8)]):
    l = bpy.data.objects.new(f"l{i}", bpy.data.lights.new(f"l{i}", "SUN")); l.data.energy = 3.5 - i * 1.5; l.rotation_euler = e; sc.collection.objects.link(l)
w = bpy.data.worlds.new("w"); w.use_nodes = True; w.node_tree.nodes["Background"].inputs[1].default_value = 0.7; sc.world = w
# (the FBX textures are not found in Blender: one neutral painted-metal look for every material)
for m in bpy.data.materials:
    if not m.use_nodes: continue
    bsdf = next((n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
    if bsdf is None: continue
    for l in list(bsdf.inputs["Base Color"].links): m.node_tree.links.remove(l)
    bsdf.inputs["Base Color"].default_value = (0.55, 0.57, 0.6, 1); bsdf.inputs["Metallic"].default_value = 0.6; bsdf.inputs["Roughness"].default_value = 0.45
import os
os.makedirs(os.path.dirname(job["out"]), exist_ok=True)
sc.render.filepath = job["out"]; bpy.ops.render.render(write_still=True)
