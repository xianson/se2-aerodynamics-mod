"""Render a block's terminal / toolbar icon from its preset .blend: LOD0 body + flap only, a vanilla-style 3/4 view from
above (as the game's own icons, e.g. Hinge_250_Icon.png), transparent background, 200 x 200 RGBA PNG (rendered at 400
and downsampled). Run in the background:

  blender -b <ControlSurface_X_3x3x1.blend> --python render_icon.py -- <out.png>

Gears: blender -b <RetractableGear_250_Kitbash.blend> --python render_icon.py -- <out.png> frame=60 keep=Body,Leg,Rod,Reference show=Wheel250
Never saves the .blend."""
import bpy, sys, os, math
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:]
out = argv[0]
opt = dict(a.split("=", 1) for a in argv[1:] if "=" in a)   # frame=N keep=Coll,Coll show=Object,Object (the gears' kitbash rig)
scene = bpy.context.scene
if "frame" in opt: scene.frame_set(int(opt["frame"]))
for n in opt.get("show", "").split(","):
    if n and n in bpy.data.objects: bpy.data.objects[n].hide_render = False

# only LOD0 renders: body and flap; colliders, dummies, other LODs off
keep = set(opt["keep"].split(",")) if "keep" in opt else {"Body_LOD0", "Flap_LOD0"}
def walk(layer):
    yield layer
    for c in layer.children: yield from walk(c)
for lc in walk(bpy.context.view_layer.layer_collection):
    name = lc.collection.name
    if name in keep: lc.exclude = False; lc.collection.hide_render = False
    elif lc.collection.children and any(k.startswith(name) or name in ("Body", "Flap") for k in keep): pass
    elif name not in ("Scene Collection", "Body", "Flap"): lc.collection.hide_render = True
objs = [o for o in scene.objects if o.type == "MESH" and not o.hide_render
        and any(c.name in keep for c in o.users_collection)]
for o in scene.objects:
    if o.type in ("EMPTY", "LIGHT", "CAMERA") and o.name not in ("IconCam", "IconKey", "IconFill"): o.hide_render = True
    elif o.type == "MESH" and o not in objs: o.hide_render = True
if not objs: raise SystemExit("no LOD0 meshes found")

# frame: every LOD0 vertex in world space
dg = bpy.context.evaluated_depsgraph_get()
pts = []
for o in objs:
    ev = o.evaluated_get(dg); m = ev.to_mesh()
    pts += [o.matrix_world @ v.co for v in m.vertices]; ev.to_mesh_clear()
lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
centre, size = (lo + hi) / 2, (hi - lo).length

cam_data = bpy.data.cameras.new("IconCam"); cam_data.lens = 50
cam = bpy.data.objects.new("IconCam", cam_data); scene.collection.objects.link(cam)
view = Vector(tuple(float(v) for v in opt["view"].split(",")) if "view" in opt else (-0.9, -1.2, 1.3)).normalized()          # (front-left, from above: the vanilla icons' angle)
cam.location = centre + view * size * 3
cam.rotation_euler = (-view).to_track_quat("-Z", "Y").to_euler()
scene.camera = cam
bpy.context.view_layer.update()
corners = [Vector((x, y, z)) for x in (lo.x, hi.x) for y in (lo.y, hi.y) for z in (lo.z, hi.z)]
coords = [c for p in corners for c in p]   # (the box: a vertex sample missed the flap plate's few vertices)
loc, scale = cam.camera_fit_coords(dg, coords)
cam.location = centre + (loc - centre) * float(opt.get("margin", 0.97))              # (a margin, as the vanilla icons)

def light(name, kind, energy, direction):
    d = bpy.data.lights.new(name, kind); d.energy = energy
    o = bpy.data.objects.new(name, d); scene.collection.objects.link(o)
    o.rotation_euler = (-Vector(direction)).to_track_quat("-Z", "Y").to_euler()
light("IconKey", "SUN", 3.0, (-0.6, -1.0, 1.2))
light("IconFill", "SUN", 1.5, (1.0, 0.4, 0.3))
if scene.world is None: scene.world = bpy.data.worlds.new("IconWorld")
scene.world.use_nodes = True
bg = scene.world.node_tree.nodes.get("Background")
if bg: bg.inputs[0].default_value = (0.55, 0.57, 0.6, 1); bg.inputs[1].default_value = 0.8

for eng in ("BLENDER_EEVEE_NEXT", "BLENDER_EEVEE", "CYCLES"):
    try: scene.render.engine = eng; break
    except TypeError: continue
if scene.render.engine == "CYCLES": scene.cycles.samples = 64
scene.render.film_transparent = True
scene.render.resolution_x = scene.render.resolution_y = 400
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = "PNG"; scene.render.image_settings.color_mode = "RGBA"
scene.view_settings.view_transform = "Standard"
big = out + ".400.png"
scene.render.filepath = big
bpy.ops.render.render(write_still=True)

img = bpy.data.images.load(big); img.scale(200, 200)
img.filepath_raw = out; img.file_format = "PNG"; img.save()
print(f"ICON {out} engine {scene.render.engine} meshes {len(objs)}")
