"""The 0.5 m wheel (2026-10-13, the user: "a 0.5 wheel in addition to the 1" - the game has none smaller than 1 m): Keen's
Wheel100 (the 1 m wheel, LOD0, from the Mod SDK's assets) at half size, as its own model for the 0.5 m retractable gear's
wheel prefab (gen_gear_defs.py: a copy of the vanilla DrivingWheel100 with this model). Writes a new .blend:

  Body_LOD0..3   the wheel, its axle along X, centred on the origin (LOD1..3 decimated)
  Collision      its convex hull (the tyre: what meets the ground), exported as <N>_Collision

  blender -b --factory-startup -P wheel_half.py -- <out.blend> [diameter_m]
"""
import bpy, bmesh, math, sys
from mathutils import Matrix, Vector

args = sys.argv[sys.argv.index("--") + 1:]
OUT = args[0]; D = float(args[1]) if len(args) > 1 else 0.495
N = "RetractableGear_050_Wheel"
SRC = r"D:\SteamLibrary\steamapps\common\Space Engineers 2 - Mod SDK\GameData\Vanilla\Assets\Blocks\Wheels\100\Models\NonFractured\Wheel100.fbx"
LIB = r"D:\Blender Brojects\SE2\AssetLibrary\SE2Decal&Material.blend"

bpy.ops.wm.read_factory_settings(use_empty=True)
sc = bpy.context.scene
before = set(bpy.data.objects)
bpy.ops.import_scene.fbx(filepath=SRC, global_scale=100.0)
new = [o for o in bpy.data.objects if o not in before]
bpy.context.view_layer.update()
meshes = []
for o in new:
    if o.type == "MESH":
        W = o.matrix_world.copy(); o.parent = None
        o.data = o.data.copy(); o.data.transform(W); o.matrix_world = Matrix.Identity(4)
        if W.determinant() < 0: o.data.flip_normals()
        meshes.append(o)
for o in new:
    if o.type != "MESH": bpy.data.objects.remove(o)
bpy.ops.object.select_all(action="DESELECT")
for o in meshes: o.select_set(True)
bpy.context.view_layer.objects.active = meshes[0]
if len(meshes) > 1: bpy.ops.object.join()
w = bpy.context.view_layer.objects.active
me = w.data
while len(me.uv_layers) > 1: me.uv_layers.remove(me.uv_layers[-1])
if me.uv_layers: me.uv_layers[0].name = "UVMap"
# its size and place: the diameter (the largest of the two non-axle extents) to D, its centre on the origin
pts = [v.co for v in me.vertices]
lo = Vector([min(p[i] for p in pts) for i in range(3)]); hi = Vector([max(p[i] for p in pts) for i in range(3)])
k = D / max(hi.y - lo.y, hi.z - lo.z)
# centred on the TYRE (its WheelRubber faces): the mesh also has an axle stub on one side, which pulls the bounding box's
# centre off the tyre's (2026-10-13: the 0.5 m wheel sat against one fork arm)
rub = {i for i, sl in enumerate(w.material_slots) if sl.material and sl.material.name.split(".")[0] == "WheelRubber"}
tx = [me.vertices[vi].co.x for p in me.polygons if p.material_index in rub for vi in p.vertices]
c = Vector(((min(tx) + max(tx)) / 2 if tx else (lo.x + hi.x) / 2, (lo.y + hi.y) / 2, (lo.z + hi.z) / 2))
print(f"WHEELHALF tyre x {min(tx):.4f}..{max(tx):.4f} vs mesh {lo.x:.4f}..{hi.x:.4f}" if tx else "WHEELHALF no WheelRubber faces")
me.transform(Matrix.Scale(k, 4) @ Matrix.Translation(-c))
# the library's materials by name (the import's duplicates are ".001")
lib_names = set()
with bpy.data.libraries.load(LIB, link=False) as (src, dst): lib_names = set(src.materials)
for sl in w.material_slots:
    m = sl.material
    if m and "." in m.name and m.name.rsplit(".", 1)[1].isdigit(): m.name = m.name.rsplit(".", 1)[0]

cols = {}
def coll(name):
    c = bpy.data.collections.new(name); sc.collection.children.link(c); return c
for n, r in enumerate((1.0, 0.5, 0.25, 0.1)):
    c = coll(f"Body_LOD{n}")
    o = w if n == 0 else bpy.data.objects.new(f"{N}_LOD{n}", w.data.copy())
    if n == 0:
        for u in list(o.users_collection): u.objects.unlink(o)
        o.name = N
    c.objects.link(o)
    if r < 1:
        m = o.modifiers.new("Dec", "DECIMATE"); m.ratio = r
        with bpy.context.temp_override(object=o, active_object=o):
            bpy.ops.object.modifier_apply(modifier=m.name)
bm = bmesh.new(); bm.from_mesh(w.data)
h = bmesh.ops.convex_hull(bm, input=bm.verts)
bmesh.ops.delete(bm, geom=[g for g in h["geom_interior"] + h["geom_unused"] if isinstance(g, bmesh.types.BMVert)], context="VERTS")
cm = bpy.data.meshes.new(N + "_Collision"); bm.to_mesh(cm); bm.free()
co = bpy.data.objects.new(N + "_Collision", cm); coll("Collision").objects.link(co); co["ExportName"] = N + "_Collision"; co.display_type = "WIRE"
d = w.dimensions
print(f"WHEELHALF {N}: {d.x:.3f} x {d.y:.3f} x {d.z:.3f} m (scale {k:.4f}), materials {[s.material.name for s in w.material_slots if s.material]}")
bpy.ops.wm.save_as_mainfile(filepath=OUT)
