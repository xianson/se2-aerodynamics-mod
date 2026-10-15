"""Export one control-surface .blend to Keen's model folder layout with VRAGE-Tools' own export settings
(fn_operators.export_fbx_quick / export_gltf_physics_quick), run in a VISIBLE Blender (the add-on and its Havok
glTF extension need a GPU context):

  <out>/Models/NonFractured/<N>.fbx, <N>_LOD1..3.fbx, <N>_collision.gltf          (Body_LODn, Collision)
  <out>/Models/Fractured/<N>_Fractured.fbx, _LOD1..3, <N>_Fractured_collision.gltf (Fractured_LODn, Collision_Fractured)
  <out>/Models/Deformed/<N>_Deformed.fbx, _LOD1..3, <N>_Deformed_collision.gltf    (Deformed_LODn, Collision_Fractured)
  <out>/Models/NonFractured/Subpart01/<N>_S1_Flap.fbx, _LOD1..2                     (Flap_LODn, pivot moved to the origin)

Objects are exported under their base names (".002" suffixes swapped away for the export; fracture colliders exported
as Fracture_0N). The file is NOT saved. Log: <out>/export_log.txt
Run: blender <file.blend> --python export_models.py -- <N> <out_dir>
"""
import bpy, os, sys, traceback

argv = sys.argv[sys.argv.index("--") + 1:]
NAME, OUT = argv[0], argv[1]
LOG = []


def lc_of(name, layer=None):
    layer = layer or bpy.context.view_layer.layer_collection
    if layer.collection.name == name: return layer
    for ch in layer.children:
        r = lc_of(name, ch)
        if r: return r


def base(n):
    return n[:-4] if len(n) > 4 and n[-4] == "." and n[-3:].isdigit() else n


class BaseNames:
    """Give a collection's objects their base names for the export (another object holding a name is parked)."""
    def __init__(self, objs, rename=None):
        self.objs, self.rename, self.undo = list(objs), rename or {}, []

    def __enter__(self):
        for o in self.objs:
            want = self.rename.get(o.name) or base(o.name)
            if o.name == want: continue
            other = bpy.data.objects.get(want)
            if other is not None:
                self.undo.append((other, other.name)); other.name = "__parked_" + want
            self.undo.append((o, o.name)); o.name = want
        return self

    def __exit__(self, *a):
        for o, n in reversed(self.undo): o.name = n


def export_fbx(coll, path):
    lc = lc_of(coll); was = lc.exclude; lc.exclude = False
    bpy.context.view_layer.active_layer_collection = lc
    objs = list(bpy.data.collections[coll].all_objects)
    with BaseNames(objs):
        bpy.ops.export_scene.fbx(filepath=path, check_existing=False, use_active_collection=True,
                                 object_types={"EMPTY", "MESH", "ARMATURE", "OTHER"}, use_custom_props=True,
                                 apply_scale_options="FBX_SCALE_ALL")
    lc.exclude = was
    tris = sum(len(p.vertices) - 2 for o in objs if o.type == "MESH" for p in o.evaluated_get(bpy.context.evaluated_depsgraph_get()).data.polygons)
    LOG.append(f"fbx  {os.path.relpath(path, OUT)}  objects={[base(o.name) for o in objs]}  tris={tris}")


def export_collision(coll, path, rename):
    lc = lc_of(coll); was = lc.exclude; lc.exclude = False
    vl = bpy.context.view_layer; vl.update()
    objs = [o for o in bpy.data.collections[coll].objects if o.type == "MESH"]
    copies = []
    for o in objs:   # a scale-applied copy with a passive convex rigid body (VRAGE-Tools: Add Rigid Body + apply scale)
        c = o.copy(); c.data = o.data.copy(); bpy.context.scene.collection.objects.link(c)
        c.data.transform(c.matrix_basis); c.matrix_basis.identity()
        with bpy.context.temp_override(object=c, active_object=c, selected_objects=[c], selected_editable_objects=[c]):
            bpy.ops.rigidbody.object_add()
        c.rigid_body.type = "PASSIVE"; c.rigid_body.collision_shape = "CONVEX_HULL"
        copies.append((c, rename(o)))
    for o in bpy.context.selected_objects: o.select_set(False)
    for c, _ in copies: c.select_set(True)
    vl.objects.active = copies[0][0]
    bpy.context.scene.msft_physics_exporter_props.enabled = True
    with BaseNames([c for c, _ in copies], {c.name: n for c, n in copies}):
        bpy.ops.export_scene.gltf(filepath=path, export_format="GLTF_SEPARATE", will_save_settings=False,
                                  use_selection=True, export_yup=True, export_gpu_instances=False, export_apply=False,
                                  export_texcoords=False, export_normals=False, export_materials="NONE",
                                  export_morph=False, export_skins=False, export_animations=False, export_extras=True)
    for c, _ in copies: bpy.data.objects.remove(c, do_unlink=True)
    lc.exclude = was
    LOG.append(f"gltf {os.path.relpath(path, OUT)}  colliders={[n for _, n in copies]}")


def run():
    try:
        m = os.path.join(OUT, "Models")
        for v in ("NonFractured", "Fractured", "Deformed", os.path.join("NonFractured", "Subpart01")):
            os.makedirs(os.path.join(m, v), exist_ok=True)
        sfx = lambda n: f"_LOD{n}" if n else ""
        for n in range(4):
            export_fbx(f"Body_LOD{n}", os.path.join(m, "NonFractured", f"{NAME}{sfx(n)}.fbx"))
            export_fbx(f"Fractured_LOD{n}", os.path.join(m, "Fractured", f"{NAME}_Fractured{sfx(n)}.fbx"))
            export_fbx(f"Deformed_LOD{n}", os.path.join(m, "Deformed", f"{NAME}_Deformed{sfx(n)}.fbx"))
        for n in range(3):   # the subpart: its pivot (the flap's origin) at the model's origin
            f = bpy.data.objects[f"Flap_LOD{n}"]; keep = f.location.copy(); f.location = (0, 0, 0)
            export_fbx(f"Flap_LOD{n}", os.path.join(m, "NonFractured", "Subpart01", f"{NAME}_S1_Flap{sfx(n)}.fbx"))
            f.location = keep
        export_collision("Collision", os.path.join(m, "NonFractured", f"{NAME}_collision.gltf"), lambda o: f"{NAME}_Collision")
        piece = lambda o: o.get("ExportName") or o.name.replace("_Collision", "")
        export_collision("Collision_Fractured", os.path.join(m, "Fractured", f"{NAME}_Fractured_collision.gltf"), piece)
        export_collision("Collision_Fractured", os.path.join(m, "Deformed", f"{NAME}_Deformed_collision.gltf"), piece)
        LOG.append("OK")
    except Exception:
        LOG.append(traceback.format_exc())
    open(os.path.join(OUT, "export_log.txt"), "w").write("\n".join(LOG))
    bpy.ops.wm.quit_blender()


bpy.app.timers.register(run, first_interval=2.0)
