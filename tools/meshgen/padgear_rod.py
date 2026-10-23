"""The pad gears' fork (gen_padgear_defs.py): the housing's rod with its fork SHORTENED - 2026-10-23, the user: "my only task was to
shrink the height of the fork to better fit where the ankle was ... align it to an axis, grab the group of vertices at the hinge, and
shift them back up". In the fork's own frame (its axis: up the leg, the gear down) every vertex below CUT along the axis - the
hinge end: the bearings, the arms' lower ends - moves up the axis by SHIFT, as one rigid group; nothing else moves. CUT lies in the
fork's empty band (no vertex in 0.250..0.472 x the size, every size and LOD - only the arms' straight plates cross it), and SHIFT
stays inside it, so only those plates get shorter. The whole fork is then scaled across (x only, about its own
centre "xcentre", which lands on the hub) by "xscale", its bearings' inner faces to 1 cm from the pad (2026-10-23, the user: "can
you just horizontally scale it"). The rod's origin (the hub) stays; the bearings - and the pad's ankle on them
(GearLayout.FootDrop) - end SHIFT up the leg from it. Never saves a .blend.

  blender -b --python padgear_rod.py -- <job.json>   (job: {"axis": [x, y, z] up the leg (VRage), "cut": m, "shift": m,
                                                             "xcentre": m, "xscale": f, "lods": [[in.fbx, out.fbx], ...]})
"""
import bpy, json, sys
from mathutils import Vector

job = json.load(open(sys.argv[sys.argv.index("--") + 1:][0]))
up = Vector(job["axis"]).normalized()
for src, out in job["lods"]:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=src)
    moved = 0
    for o in bpy.data.objects:
        if o.type != "MESH": continue
        M = o.matrix_world; Mi = M.inverted()
        for v in o.data.vertices:
            w = M @ v.co; p = Vector((w.x, w.z, -w.y))   # (VRage (x, y, z) is Blender (x, -z, y))
            if p.dot(up) < job["cut"]:
                p += up * job["shift"]; moved += 1
            p.x = (p.x - job["xcentre"]) * job["xscale"]   # (the whole fork, scaled across about its own centre: onto the pad)
            v.co = Mi @ Vector((p.x, -p.z, p.y))
        o.data.update()
    bpy.ops.export_scene.fbx(filepath=out, check_existing=False, object_types={"EMPTY", "MESH"}, use_custom_props=True,
                             apply_scale_options="FBX_SCALE_ALL")
    print(f"padgear_rod: {out}: {moved} vertices moved up {job['shift']:.3f} m")
