"""A pad-foot gear's icon (gen_padgear_defs.py --icons): its housing, leg, rod and foot FBX placed as in game (VRage positions from the
job file; VRage (x, y, z) is Blender (x, -z, y)), textured from the SDK's BlockMaterials, a low front 3/4 view, transparent, 200 x 200 (rendered at 400, Cycles - background
Blender has no GPU drawing). Never saves a .blend.

  blender -b --python padgear_icon.py -- <job.json>
"""
import bpy, json, os, sys
from mathutils import Matrix, Quaternion, Vector

job = json.load(open(sys.argv[sys.argv.index("--") + 1:][0]))
bpy.ops.wm.read_factory_settings(use_empty=True)
sc = bpy.context.scene


def place(path, at, scale=1.0, local=(0.0, 0.0, 0.0), q=(0.0, 0.0, 0.0, 1.0)):
    """Import an FBX: its vertices scaled, moved by `local`, turned by q (VRage x, y, z, w) and moved to `at` - VRage frames."""
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=path)
    new = [o for o in bpy.data.objects if o not in before]
    roots = [o for o in new if o.parent is None or o.parent not in new]
    B = lambda p: Vector((p[0], -p[2], p[1]))
    rot = Quaternion((q[3], q[0], -q[2], q[1])).to_matrix().to_4x4()   # (VRage (x, y, z) is Blender (x, -z, y): the axis too)
    m = Matrix.Translation(B(at)) @ rot @ Matrix.Translation(B(local)) @ Matrix.Scale(scale, 4)
    for o in roots:
        o.matrix_world = m @ o.matrix_world
    return [o for o in new if o.type == "MESH"]


objs = []
objs += place(job["housing"], (0.125, 0.125, 0.125))
objs += place(job["leg"][0], job["leg"][1])
objs += place(job["rod"][0], job["rod"][1])
objs += place(job["foot"][0], job["foot"][3], job["foot"][1], job["foot"][2], job["foot"][4])
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
d = Vector((1.2, -1.0, 0.35)).normalized()   # (low, front three-quarter: the pad shows, as the wheeled gears' wheel)
cam.location = c + d * r * 2; cam.rotation_euler = (c - cam.location).to_track_quat("-Z", "Y").to_euler()
for i, e in enumerate([(0.6, 0, 0.8), (1.2, 0, -0.8)]):
    l = bpy.data.objects.new(f"l{i}", bpy.data.lights.new(f"l{i}", "SUN")); l.data.energy = 4.5 - i * 2.0; l.rotation_euler = e; sc.collection.objects.link(l)
w = bpy.data.worlds.new("w"); w.use_nodes = True; w.node_tree.nodes["Background"].inputs[1].default_value = 1.2; sc.world = w
# the vanilla textures (2026-10-23, the user: "fix the icons" - they were one flat grey): each material's colour-metal map
# (<name>_cm.dds RGB) from the SDK's BlockMaterials, a little metal; the decals and fonts (overlays) left out
cms = {}
for d, _, fs in os.walk(job["materials"]):
    for f in fs:
        if f.lower().endswith("_cm.dds"): cms.setdefault(f[:-7].lower(), os.path.join(d, f))
for m in bpy.data.materials:
    base = m.name.split(".")[0]
    m.use_nodes = True; nt = m.node_tree; nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    if base.startswith("Atlas"):
        nt.links.new(nt.nodes.new("ShaderNodeBsdfTransparent").outputs[0], out.inputs[0]); continue
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled"); nt.links.new(bsdf.outputs[0], out.inputs[0])
    bsdf.inputs["Roughness"].default_value = 0.45
    cm = cms.get(base.lower())
    if "Colorable" in base:   # (the paint: the game tints it with the player's colour - a plain light grey, as the game's icons)
        bsdf.inputs["Base Color"].default_value = (0.62, 0.64, 0.66, 1); bsdf.inputs["Metallic"].default_value = 0.4
    elif cm:
        tex = nt.nodes.new("ShaderNodeTexImage"); tex.image = bpy.data.images.load(cm, check_existing=True)
        nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"]); bsdf.inputs["Metallic"].default_value = 0.25
    else:
        print("no texture:", base)
        bsdf.inputs["Base Color"].default_value = (0.55, 0.57, 0.6, 1); bsdf.inputs["Metallic"].default_value = 0.6
os.makedirs(os.path.dirname(job["out"]), exist_ok=True)
sc.render.filepath = job["out"]; bpy.ops.render.render(write_still=True)
