"""Reorganize the user's kitbashed 2.5 m retractable gear (2026-10-13, in RetractableGear_250_FoldForward.blend's
"Kitbash" collection, rigged hinge head -> piston base / S2 -> fork + wheel, saved STOWED) into the gear's export layout,
written to a NEW file (the source is not touched):

  Body        LandingGear500 (the mount: static, with the block)
  Leg         Hinge_250_Head + Piston500_Base, under the empty Leg_Pivot (origin ON the pivot; turns about X)
  Rod         Piston500_S2 + Hinge_250_Base (the fork), under the empty Rod_Hub (origin AT the wheel hub; rides the
              hub and turns with the leg - S2 slides in the piston base)
  Reference   Wheel250 (on Rod_Hub - the real wheel is the vanilla one, not exported), RakeLine (the wheel's path)
  Collision   Collision_Box, the block (2.5 x 2.5 x 7.5)
  Dummies     Gear_Leg (pivot), Gear_Rod (deployed hub), Wheel Rest Position (the vanilla suspension's pivot: at the
              wheel's attachment face, its local Y along the rake, scale Y = the vanilla travel)
  Blockout_old  the earlier generated blockout, excluded (kept)

The block is centred at the origin. Motion (keyframed, frames 0 stowed .. 60 deployed): the real wheel moves on the
straight line between the stowed and deployed hubs (the vanilla suspension's axis, moved by its limits -
AeroGearComponent), the leg turns about the pivot to point at it and the rod rides it (GearLayout.RakedLeg/RakedRod).

  blender -b <source.blend> -P gear_from_kitbash.py -- <out.blend>
"""
import bpy, math, sys
from mathutils import Matrix, Vector

OUT = sys.argv[sys.argv.index("--") + 1]
DEPLOY_FROM_VERTICAL = math.radians(30.0)   # the user's deployed pose (screenshot 2026-10-13): leg 30 deg forward of vertical
DEPLOY_LEN = 6.10                           # ... pivot -> hub 6.1 m (S2 out ~2 m)
TRAVEL, WHEEL_W = 0.60, 1.80                # the vanilla 2.5 m suspension's travel, the Wheel250's width
FRAMES = 60

sc = bpy.context.scene
O = bpy.data.objects
LEG, ROD, BODY, WHEEL = ["Hinge_250_Head", "Piston500_Base"], ["Piston500_S2", "Hinge_250_Base"], ["LandingGear500"], "Wheel250"
kit = LEG + ROD + BODY + [WHEEL]
for n in kit:
    if n not in O: raise SystemExit("missing " + n)

# (everything free of the user's rig, world transforms kept)
bpy.context.view_layer.update()
for n in kit:
    o = O[n]; mw = o.matrix_world.copy(); o.parent = None; o.matrix_world = mw

def bbox(objs):
    pts = [o.matrix_world @ Vector(c) for o in objs for c in o.bound_box]
    return Vector([min(p[i] for p in pts) for i in range(3)]), Vector([max(p[i] for p in pts) for i in range(3)])

# the rod collapsed into the piston (the user's first save: the wheel's front edge on the block's end)
lo, hi = bbox([O[n] for n in BODY + LEG])
wlo, whi = bbox([O[WHEEL]])
end_y = -3.75 + 0.0
shift = end_y - wlo.y if wlo.y < end_y else 0.0
for n in ROD + [WHEEL]: O[n].location.y += shift
bpy.context.view_layer.update()

# the block centred at the origin (its 2.5 x 7.5 x 2.5 envelope: across from the mount, along from -3.75 .. 3.75)
blo, bhi = bbox([O[n] for n in kit])
centre = Vector(((blo.x + bhi.x) / 2, 0.0, (blo.z + bhi.z) / 2))
for n in kit: O[n].location -= centre
bpy.context.view_layer.update()

pivot = O["Hinge_250_Head"].matrix_world.translation.copy()
wlo, whi = bbox([O[WHEEL]])
hub_s = Vector((pivot.x, (wlo.y + whi.y) / 2, (wlo.z + whi.z) / 2))   # (in the pivot's plane: the leg turns about X)
wheel_x = (wlo.x + whi.x) / 2
Ls = (hub_s - pivot).length
hub_d = pivot + Vector((0, -math.sin(DEPLOY_FROM_VERTICAL), -math.cos(DEPLOY_FROM_VERTICAL))) * DEPLOY_LEN
rake = hub_s - hub_d; retract = rake.length; axis = rake.normalized()   # (deployed -> stowed: up the rake)
closest = abs((pivot - hub_d).cross(axis).length)

def alpha(hub):   # the leg's turn about +X from stowed (pointing -Y) to point at hub
    d = hub - pivot
    return math.atan2(-d.z, -d.y)

# collections
def coll(name, parent=None):
    c = bpy.data.collections.get(name) or bpy.data.collections.new(name)
    if c.name not in (parent or sc.collection).children: (parent or sc.collection).children.link(c)
    return c

old = bpy.data.collections.new("Blockout_old"); sc.collection.children.link(old)
for c in list(sc.collection.children):
    if c.name in ("Well", "Doors", "Strut", "Collision", "Reference", "Dummies"):
        sc.collection.children.unlink(c); old.children.link(c); c.name = c.name + "_old"
for c in list(old.children): pass
cols = {n: coll(n) for n in ("Body", "Leg", "Rod", "Reference", "Collision", "Dummies")}

def move(o, c):
    for u in list(o.users_collection): u.objects.unlink(o)
    c.objects.link(o)

def empty(name, at, c, size=0.4, kind="ARROWS"):
    e = O.get(name)
    if e is not None: e.name = name + "_old"
    e = bpy.data.objects.new(name, None); e.location = at; e.empty_display_size = size; e.empty_display_type = kind
    c.objects.link(e); return e

leg = empty("Leg_Pivot", pivot, cols["Leg"], 0.6, "CIRCLE"); leg.rotation_mode = "XYZ"
rod = empty("Rod_Hub", hub_s, cols["Rod"], 0.5, "CIRCLE"); rod.rotation_mode = "XYZ"
for e in (leg, rod): e.empty_display_type = "CIRCLE"; e.rotation_euler = (0, 0, 0)
# (circles drawn in the leg's turning plane: the empty's local Y-Z)
bpy.context.view_layer.update()
for n in LEG: move(O[n], cols["Leg"]); O[n].parent = leg; O[n].matrix_parent_inverse = leg.matrix_world.inverted()
for n in ROD: move(O[n], cols["Rod"]); O[n].parent = rod; O[n].matrix_parent_inverse = rod.matrix_world.inverted()
move(O[WHEEL], cols["Reference"]); O[WHEEL].parent = rod; O[WHEEL].matrix_parent_inverse = rod.matrix_world.inverted()
O[WHEEL].hide_render = True
for n in BODY: move(O[n], cols["Body"])

# the motion: frame 0 stowed .. FRAMES deployed, the hub straight down the rake at a steady rate
sc.frame_start, sc.frame_end = 0, FRAMES
bpy.context.preferences.edit.keyframe_new_interpolation_type = "LINEAR"
for f in range(FRAMES + 1):
    hub = hub_s - rake * (f / FRAMES); a = alpha(hub)
    leg.rotation_euler = (a, 0, 0); leg.keyframe_insert("rotation_euler", index=0, frame=f)
    rod.location = hub; rod.rotation_euler = (a, 0, 0)
    rod.keyframe_insert("location", frame=f); rod.keyframe_insert("rotation_euler", index=0, frame=f)
sc.frame_set(FRAMES)   # (the export's rest pose: down - every subpart spawns at its dummy with the gear down)

# reference line, collision, dummies
lm = bpy.data.meshes.new("RakeLine"); lm.from_pydata([tuple(hub_d), tuple(hub_s)], [(0, 1)], [])
cols["Reference"].objects.link(bpy.data.objects.new("RakeLine", lm))
import bmesh
bm = bmesh.new(); bmesh.ops.create_cube(bm, size=1.0)
for v in bm.verts: v.co = Vector((v.co.x * 2.5, v.co.y * 7.5, v.co.z * 2.5))
me = bpy.data.meshes.new("Collision_Box"); bm.to_mesh(me); bm.free()
cb = bpy.data.objects.new("Collision_Box", me); cb.display_type = "WIRE"; cb.hide_render = True; cols["Collision"].objects.link(cb)
empty("Gear_Leg", pivot, cols["Dummies"], 0.3)
empty("Gear_Rod", hub_d, cols["Dummies"], 0.3)
attach = Vector((wheel_x + WHEEL_W / 2, hub_d.y, hub_d.z))
rest = empty("Wheel Rest Position", attach + axis * TRAVEL / 2, cols["Dummies"], 1.0)
rest.scale = (0.1, TRAVEL, 0.1)
rest.rotation_euler = (math.atan2(axis.z, axis.y) - math.pi / 2, 0, 0)   # (its local Y onto the rake)

for o in list(sc.collection.objects):
    if o.type == "LIGHT": move(o, old)
vl = bpy.context.view_layer.layer_collection
for lc in vl.children:
    if lc.collection == old: lc.exclude = True

bpy.ops.wm.save_as_mainfile(filepath=OUT, copy=False)
print(f"GEARKIT pivot ({pivot.x:.3f},{pivot.y:.3f},{pivot.z:.3f}) stowed hub ({hub_s.x:.3f},{hub_s.y:.3f},{hub_s.z:.3f}) "
      f"deployed hub ({hub_d.x:.3f},{hub_d.y:.3f},{hub_d.z:.3f}) wheel x {wheel_x:.3f} | leg {Ls:.2f} stowed -> {DEPLOY_LEN:.2f} m, "
      f"turn {math.degrees(alpha(hub_d)):.1f} deg, rake {math.degrees(math.acos(max(-1, min(1, axis.z)))):.1f} deg from vertical, "
      f"retract {retract:.3f} m, closest {closest:.3f} m (collapsed {Ls:.3f}), shift {shift:.3f}, centre {tuple(round(c, 3) for c in centre)}")
