"""Export every propulsor block of the export scene (propulsor_prep.py -> Propulsors_Export.blend) to Keen's model folder
layout with VRAGE-Tools' own export settings - gear_export.py's layout and settings, for many blocks in one VISIBLE Blender
run (the add-on and its Havok glTF extension need a GPU context). Per block N (collections "N|..."):

  <out>/<N>/Models/NonFractured/<N>.fbx, _LOD1..3, <N>_collision.gltf        (N|Body_LODn, N|Collision)
  <out>/<N>/Models/Fractured/<N>_Fractured.fbx, _LOD1..3, _collision.gltf    (N|Fractured_LODn, N|Collision_Fractured)
  <out>/<N>/Models/Deformed/<N>_Deformed.fbx, _LOD1..3, _collision.gltf      (N|Deformed_LODn, N|Collision_Fractured)
  <out>/<N>/Models/NonFractured/Subpart01/<N>_S1_Rotor.fbx, _LOD1..2        (N|Rotor_LODn: origin on the spin axis)
  <out>/<N>/Models/NonFractured/Subpart02/<N>_S2_Fan.fbx, _LOD1..2          (N|Fan_LODn: a turboprop's intake fan)
  or, for a helicopter rotor split by rotor_split.py: Subpart01/<N>_S1_Hub, Subpart02/<N>_S2_Blade, Subpart03/<N>_S3_Blur
  or, for a ramjet (ramjet_export_prep.py): Subpart01/<N>_S1_Spike, _LOD1..2 (N|Spike_LODn: the translating inlet spike)

Colliders are exported under their "ExportName" (occupancy_* / mountpoints_* keep their prefixes: the content builder
sorts them by it). The file is NOT saved. Log: <out>/export_log.txt
Run: blender <Propulsors_Export.blend> --python propulsor_export.py -- <out_dir> [only=N,N]
"""
import bpy, os, sys, traceback

argv = sys.argv[sys.argv.index("--") + 1:]
OUT = argv[0]
OPT = dict(a.split("=", 1) for a in argv[1:] if "=" in a)
ONLY = set(OPT["only"].split(",")) if "only" in OPT else None
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


def export_fbx(coll, path, root):
    lc = lc_of(coll); was = lc.exclude; lc.exclude = False
    bpy.context.view_layer.active_layer_collection = lc
    objs = list(bpy.data.collections[coll].all_objects)
    with BaseNames(objs):
        bpy.ops.export_scene.fbx(filepath=path, check_existing=False, use_active_collection=True,
                                 object_types={"EMPTY", "MESH", "ARMATURE", "OTHER"}, use_custom_props=True,
                                 apply_scale_options="FBX_SCALE_ALL")
    lc.exclude = was
    LOG.append(f"fbx  {os.path.relpath(path, root)}  objects={[base(o.name) for o in objs]}")


def export_collision(coll, path, rename, root):
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
    LOG.append(f"gltf {os.path.relpath(path, root)}  colliders={[n for _, n in copies]}")


def blocks():
    out = []
    for c in bpy.data.collections:
        if c.name.endswith("|Body_LOD0"):
            n = c.name.split("|")[0]
            if not ONLY or n in ONLY: out.append(n)
    return sorted(out)


def run():
    try:
        sfx = lambda n: f"_LOD{n}" if n else ""
        for N in blocks():
            root = os.path.join(OUT, N); m = os.path.join(root, "Models")
            for v in ("NonFractured", "Fractured", "Deformed", os.path.join("NonFractured", "Subpart01")):
                os.makedirs(os.path.join(m, v), exist_ok=True)
            for n in range(4):
                export_fbx(f"{N}|Body_LOD{n}", os.path.join(m, "NonFractured", f"{N}{sfx(n)}.fbx"), OUT)
                export_fbx(f"{N}|Fractured_LOD{n}", os.path.join(m, "Fractured", f"{N}_Fractured{sfx(n)}.fbx"), OUT)
                export_fbx(f"{N}|Deformed_LOD{n}", os.path.join(m, "Deformed", f"{N}_Deformed{sfx(n)}.fbx"), OUT)
            # the spinning subpart(s): one Rotor (the vanilla spin) - a turboprop also its intake Fan (Subpart02, turboprop_rig.py) -,
            # or a helicopter rotor's Hub / Blade / Blur (rotor_split.py: the mod's AeroRotorAnimComponent drives them)
            # (a turboprop split for the blur treatment, turboprop_export_prep.py: spinner Rotor, Blade, Blur - its Fan keeps Subpart02,
            #  so its Blade goes in Subpart04)
            # (2026-10-17, a ramjet - ramjet_export_prep.py: its one subpart is the translating inlet Spike, Subpart01/<N>_S1_Spike;
            #  blocks without an N|Spike_LOD0 collection export exactly as before)
            fan = f"{N}|Fan_LOD0" in bpy.data.collections
            parts = [(k, g) for k, g in ((1, "Rotor"), (1, "Hub"), (4 if fan else 2, "Blade"), (3, "Blur"), (2, "Fan"), (1, "Spike")) if f"{N}|{g}_LOD0" in bpy.data.collections]
            for k, g in parts:
                os.makedirs(os.path.join(m, "NonFractured", f"Subpart0{k}"), exist_ok=True)
                for n in range(3):
                    export_fbx(f"{N}|{g}_LOD{n}", os.path.join(m, "NonFractured", f"Subpart0{k}", f"{N}_S{k}_{g}{sfx(n)}.fbx"), OUT)
            name = lambda o: o.get("ExportName") or o.name
            export_collision(f"{N}|Collision", os.path.join(m, "NonFractured", f"{N}_collision.gltf"), name, OUT)
            export_collision(f"{N}|Collision_Fractured", os.path.join(m, "Fractured", f"{N}_Fractured_collision.gltf"), name, OUT)
            export_collision(f"{N}|Collision_Fractured", os.path.join(m, "Deformed", f"{N}_Deformed_collision.gltf"), name, OUT)
            LOG.append(f"OK {N}")
        LOG.append("ALL OK")
    except Exception:
        LOG.append(traceback.format_exc())
    os.makedirs(OUT, exist_ok=True)
    open(os.path.join(OUT, "export_log.txt"), "w").write(chr(10).join(LOG))
    bpy.ops.wm.quit_blender()


bpy.app.timers.register(run, first_interval=2.0)
