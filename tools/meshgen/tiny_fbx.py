"""A one-triangle FBX (0.01 mm - 2026-10-23, the user saw the 1 mm one: "make the triangle infinitesimally small" -, at the origin, material PaintedMetalColorable) - the hidden wheel's model needs SOME geometry (the content
builder: "Model asset must have at least one FBX file containing a geometry"); at the wheel's centre, inside the foot, is never seen.
  blender -b --python tiny_fbx.py -- <out.fbx>"""
import bpy, sys
out = sys.argv[sys.argv.index("--") + 1]
bpy.ops.wm.read_factory_settings(use_empty=True)
me = bpy.data.meshes.new("HiddenWheel"); me.from_pydata([(0, 0, 0), (1e-5, 0, 0), (0, 1e-5, 0)], [], [(0, 1, 2)]); me.update()
uv = me.uv_layers.new(name="UVMap")
for i, l in enumerate(me.loops): uv.data[i].uv = ((0, 0), (1, 0), (0, 1))[l.vertex_index]
me.materials.append(bpy.data.materials.new("PaintedMetalColorable"))
ob = bpy.data.objects.new("HiddenWheel", me); bpy.context.scene.collection.objects.link(ob)
bpy.ops.export_scene.fbx(filepath=out, use_selection=False, object_types={"MESH"}, apply_unit_scale=True, bake_space_transform=False)
