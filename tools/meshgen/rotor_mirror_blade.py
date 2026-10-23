"""THE COUNTER-ROTATING BLADE (2026-10-23, the user: a terminal option to flip a helicopter's rotor torque, mirrored blades).
Run on a main rotor's export scene after rotor_lengthen.py, saved in place:

  blender -b HelicopterRotor_150_Export.blend -P rotor_mirror_blade.py

Writes N|BladeCCW_LOD0..2: N|Blade_LODn mirrored across its chord (Blender X; the span, -Y, and up, Z, kept), so the
leading edge leads in a COUNTER-clockwise spin from above. Same origin (the hinge), so it sits on the same dummies.
propulsor_export.py exports it as Subpart06/<N>_S6_BladeCCW; gen_propulsor_defs.py spawns it on the RotorBladeCCW dummies
(entity "RotorBladeCCW") and AeroRotorAnimComponent shows the set the block's Counter-rotating setting asks for.
Re-run after any blade change: the old CCW collections are replaced.
"""
import bpy

sc = bpy.context.scene
N = next(c.name.split("|")[0] for c in bpy.data.collections if c.name.endswith("|Blade_LOD0"))
for n in range(3):
    sfx = f"_LOD{n}" if n else ""
    old = bpy.data.collections.get(f"{N}|BladeCCW_LOD{n}")
    if old:
        for o in list(old.objects): bpy.data.objects.remove(o, do_unlink=True)
        bpy.data.collections.remove(old)
    src = bpy.data.objects[f"{N}_Blade{sfx}"]
    me = src.data.copy(); me.name = f"{N}_BladeCCW{sfx}"
    c = bpy.data.collections.new(f"{N}|BladeCCW_LOD{n}"); sc.collection.children.link(c)
    o = bpy.data.objects.new(me.name, me); c.objects.link(o)
    o.scale = (-1.0, 1.0, 1.0)   # (Blender's own negative-scale apply flips the winding and any custom normals with it)
    with bpy.context.temp_override(object=o, active_object=o, selected_objects=[o], selected_editable_objects=[o]):
        bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    src_lc = next((lc for lc in bpy.context.view_layer.layer_collection.children if lc.collection.name == f"{N}|Blade_LOD{n}"), None)
    dst_lc = next(lc for lc in bpy.context.view_layer.layer_collection.children if lc.collection.name == c.name)
    if src_lc: dst_lc.exclude = src_lc.exclude
    print(f"MIRROR {N}: {o.name} ({len(me.vertices)} verts, custom normals {me.has_custom_normals})")
bpy.ops.wm.save_mainfile()
