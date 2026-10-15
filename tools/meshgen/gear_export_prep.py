"""The 2.5 m retractable gear's EXPORT scene, from the reorganized kitbash (gear_from_kitbash.py ->
RetractableGear_250_Kitbash.blend), written to a new file (the source is not touched), plus a JSON spec of its layout
(VRage block space) for the definitions (gen_gear_defs.py) and the mod (AeroGearVariants):

  Body_LOD0..3          the mount (LandingGear500) - exposed: no bay walls, no doors (the user's call)
  Leg_LOD0..2           hinge head + piston base, GEAR DOWN, in the pivot's frame (origin = the pivot)
  Rod_LOD0..2           S2 + the fork, gear down, in the deployed hub's frame (origin = the hub)
  Fractured_LOD0..3     the body as two pieces, Fracture_01 / _02 - its halves (LOD3: one mesh); Deformed_LOD0..3 = + the guide's Displace
  Collision             <N>_Collision (the mount's hull, inside the block) + mountpoints_<N> (a slab under the top face: mounts there only)
  Collision_Fractured   Fracture_01 / _02 (each half's hull)

The model is turned 180 deg about Z so the wheel stows toward the block's Forward (Blender +Y = VRage -Z). Subparts
spawn at their dummies with the gear down (the client turns them from there: GearLayout.RakedLeg / RakedRod).

  blender -b RetractableGear_250_Kitbash.blend -P gear_export_prep.py -- <out.blend> <spec.json>
"""
import bpy, bmesh, json, math, sys
from mathutils import Matrix, Vector

OUT, SPEC, SIZE = (sys.argv[sys.argv.index("--") + 1:] + ["250"])[:3]
# per size (2026-10-13, the user: the 1.5 m and 1 m gears are the 2.5 m kitbash scaled): the scale, the vanilla
# suspension's travel (its Wheel Rest Position scale Y), the vanilla wheel's pivot dummy in its own model
# (DrivingWheel<size>_DrivingWheelModelComponentDefinition), the fork widened across for a wider wheel
SIZES = {"250": dict(s=1.0, travel=0.60, wheel_pivot=(0.9375, 0.125, 0.125), fork_x=1.0),
         "150": dict(s=0.6, travel=0.40, wheel_pivot=(0.575, 0.125, 0.125), fork_x=1.06),   # (Wheel150 1.14 wide; 0.6 x 1.80 = 1.08)
         "100": dict(s=0.4, travel=0.25, wheel_pivot=(0.5, 0.0, 0.125), fork_x=1.0),
         # (ours: wheel_half.py's half-size Wheel100, centred on its origin with no model offset; its pivot dummy just inside
         #  its inner face, as the vanilla wheels' - gen_gear_defs.py writes it there)
         "050": dict(s=0.2, travel=0.125, wheel_pivot=(0.14, 0.0, 0.0), fork_x=1.0, model_offset=(0.0, 0.0, 0.0))}
CFG = SIZES[SIZE]
N = f"RetractableGear_{SIZE}"
W, L, H, T = 2.5 * CFG["s"], 7.5 * CFG["s"], 2.5 * CFG["s"], 0.05   # the block, the bay's wall thickness
TRAVEL = CFG["travel"]
WHEEL_MODEL_OFFSET = CFG.get("model_offset", (0.125, 0.125, 0.125))  # (a vanilla model's LOD0 shift: its mesh centre in its own model space)
DISPLACE = dict(strength=0.075, size=0.25, depth=4)
sc = bpy.context.scene; O = bpy.data.objects
R = Matrix.Scale(CFG["s"], 4) @ Matrix.Rotation(math.pi, 4, "Z")

FRAMES = sc.frame_end
sc.frame_set(0); bpy.context.view_layer.update()
hub_s = R @ O["Rod_Hub"].matrix_world.translation
sc.frame_set(FRAMES); bpy.context.view_layer.update()
pivot = R @ O["Leg_Pivot"].matrix_world.translation
hub_d = R @ O["Rod_Hub"].matrix_world.translation
wheel_c = R @ O["Wheel250"].matrix_world.translation
rake = (hub_s - hub_d); retract = rake.length; axis = rake.normalized()


def baked(names, M):
    """Copies of these objects' meshes, world (gear down) -> M, joined into one bmesh-ready mesh list."""
    out = []
    for n in names:
        o = O[n]; me = o.data.copy(); me.transform(M @ R @ o.matrix_world)
        if (M @ R @ o.matrix_world).determinant() < 0: me.flip_normals()
        while len(me.uv_layers) > 1: me.uv_layers.remove(me.uv_layers[-1])
        if me.uv_layers: me.uv_layers[0].name = "UVMap"
        out.append(me)
    return out


def coll(name, parent=None):
    c = bpy.data.collections.new(name); (parent or sc.collection).children.link(c); return c


def obj(name, meshes, c):
    objs = []
    for i, me in enumerate(meshes):
        o = bpy.data.objects.new(f"{name}_part{i}", me); c.objects.link(o); objs.append(o)
    if len(objs) > 1:
        with bpy.context.temp_override(active_object=objs[0], selected_editable_objects=objs, selected_objects=objs):
            bpy.ops.object.join()
    o = objs[0]; o.name = name; o.data.name = name
    return o


def box_mesh(name, size, at, mat=None):
    bm = bmesh.new(); bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts: v.co = Vector((v.co.x * size[0] + at[0], v.co.y * size[1] + at[1], v.co.z * size[2] + at[2]))
    uv = bm.loops.layers.uv.new("UVMap")
    for f in bm.faces:
        n = f.normal
        for l in f.loops:
            p = l.vert.co
            l[uv].uv = (p.y, p.z) if abs(n.x) > 0.5 else ((p.x, p.z) if abs(n.y) > 0.5 else (p.x, p.y))
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    if mat: me.materials.append(mat)
    return me


def hull(name, meshes):
    bm = bmesh.new()
    for me in meshes: bm.from_mesh(me)
    h = bmesh.ops.convex_hull(bm, input=bm.verts)
    bmesh.ops.delete(bm, geom=[g for g in h["geom_interior"] + h["geom_unused"] if isinstance(g, bmesh.types.BMVert)], context="VERTS")
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free(); return me


def decimate(o, ratio):
    if ratio >= 1: return
    m = o.modifiers.new("Dec", "DECIMATE"); m.ratio = ratio
    with bpy.context.temp_override(object=o, active_object=o):
        bpy.ops.object.modifier_apply(modifier=m.name)


# the source's collections renamed out of the way (they and their objects are dropped from the new file at the end)
src_cols = list(sc.collection.children)
for c in src_cols: c.name = "src_" + c.name
src_objs = [o for o in sc.objects]
darker = bpy.data.materials.get("PaintedMetalDarker")
I = Matrix.Identity(4)

bay = [box_mesh("Bay_Ceiling", (W, L, T), (0, 0, H / 2 - T / 2), darker),
       box_mesh("Bay_WallL", (T, L, H - T), (-W / 2 + T / 2, 0, -T / 2), darker),
       box_mesh("Bay_WallR", (T, L, H - T), (W / 2 - T / 2, 0, -T / 2), darker)]
bay = []   # (the user, 2026-10-13: no walls, no doors - the gear stays exposed; mountpoints_ still spans the block)
groups = {"Body": (["LandingGear500"], I, bay, [1.0, 0.5, 0.25, 0.1]),
          "Leg": (["Hinge_250_Head", "Piston500_Base"], Matrix.Translation(-pivot), [], [1.0, 0.5, 0.25]),
          "Rod": (["Piston500_S2", "Hinge_250_Base"], Matrix.Diagonal((CFG["fork_x"], 1, 1, 1)) @ Matrix.Translation(-hub_d), [], [1.0, 0.5, 0.25])}
made, mats = {}, {}
for g, (names, M, extra, ratios) in groups.items():
    for n, r in enumerate(ratios):
        c = coll(f"{g}_LOD{n}")
        o = obj(f"{N}_{g}" + (f"_LOD{n}" if n else ""), baked(names, M) + [e.copy() for e in extra], c)
        decimate(o, r); made[(g, n)] = o
        mats.setdefault(g, set()).update(s.material.name for s in o.material_slots if s.material)

# the wheel CENTRED in the fork (2026-10-13, the user: it sat against one arm): the gap between the fork's two hub discs,
# measured on the rod as exported (hub frame: the discs are the vertices 0.5 .. 0.75 m (x scale) from the axle line)
def fork_gap_centre(me, s):
    xs = sorted({v.co.x for v in me.vertices if 0.5 * s < (v.co.y ** 2 + v.co.z ** 2) ** 0.5 < 0.75 * s})
    width, a, b = max((b - a, a, b) for a, b in zip(xs, xs[1:]))
    return (a + b) / 2
gap_c = fork_gap_centre(made[("Rod", 0)].data, CFG["s"])
wheel_c = Vector((hub_d.x + gap_c, wheel_c.y, wheel_c.z))
print(f"GEARGAP fork gap centre {gap_c:+.3f} m from the hub")

# fractured / deformed: the body in TWO pieces, its left and right halves (whole faces by their centre's x - the
# processor wants more than one piece: "a breakable collider cannot be created from a single instance")
PIECES = ("Fracture_01", "Fracture_02")
def halves(me):
    out = []
    for keep_left in (True, False):
        bm = bmesh.new(); bm.from_mesh(me)
        bmesh.ops.delete(bm, geom=[fa for fa in bm.faces if (fa.calc_center_median().x < 0) != keep_left], context="FACES")
        h = me.copy(); bm.to_mesh(h); bm.free(); out.append(h)
    return out
tex = bpy.data.textures.get("Deform_Clouds") or bpy.data.textures.new("Deform_Clouds", "CLOUDS")
tex.noise_type = "SOFT_NOISE"; tex.noise_scale = DISPLACE["size"]; tex.noise_depth = DISPLACE["depth"]; tex.cloud_type = "GRAYSCALE"
fr, de = coll("Fractured"), coll("Deformed")
for n in range(4):
    b = made[("Body", n)]
    fc, dc = coll(f"Fractured_LOD{n}", fr), coll(f"Deformed_LOD{n}", de)
    parts = []
    if n < 3:
        for name, me in zip(PIECES, halves(b.data)):
            p = bpy.data.objects.new(name, me); fc.objects.link(p); p["group"] = p["ColliderMeshGroups"] = name; parts.append(p)
    else:
        p = bpy.data.objects.new(f"{N}_Fractured_LOD3", b.data.copy()); fc.objects.link(p); parts.append(p)
    for p in parts:
        p["LOD"] = n
        d = p.copy(); d.data = p.data.copy(); dc.objects.link(d)
        m = d.modifiers.new("Deform", "DISPLACE"); m.texture = tex; m.texture_coords = "GLOBAL"
        m.direction = "RGB_TO_XYZ"; m.space = "LOCAL"; m.strength = DISPLACE["strength"]

# collision
mount = baked(["LandingGear500"], I)
cc, cf = coll("Collision"), coll("Collision_Fractured")
# (2026-10-13, the user: "the attachment pts are broken" - a block-sized mountpoints_ box made every face mountable, and
#  the mount's hull, reaching the side and end faces, more): the hull kept 2 cm inside the block's faces, and mount
#  points on the TOP face only (a thin slab under it) - a gear bolts on from above
def inset(me, m=0.02):
    for v in me.vertices:
        v.co.x = max(-W / 2 + m, min(W / 2 - m, v.co.x)); v.co.y = max(-L / 2 + m, min(L / 2 - m, v.co.y))
        v.co.z = max(-H / 2 + m, min(H / 2 - m, v.co.z))
    return me
h = bpy.data.objects.new(f"{N}_Collision", inset(hull(f"{N}_Collision", mount))); cc.objects.link(h); h["ExportName"] = f"{N}_Collision"
b = bpy.data.objects.new(f"mountpoints_{N}", box_mesh(f"mountpoints_{N}", (W, L, 0.05), (0, 0, H / 2 - 0.025))); cc.objects.link(b)
b["ExportName"] = f"mountpoints_{N}"
for name, me in zip(PIECES, halves(mount[0])):
    f = bpy.data.objects.new(name + "_Collision", hull(name + "_Collision", [me])); cf.objects.link(f); f["ExportName"] = name
for o in list(cc.objects) + list(cf.objects): o.display_type = "WIRE"

# reference (not exported): the wheel down and the rake line
rc = coll("Reference")
wr = bpy.data.objects.new("WheelRef", O["Wheel250"].data); wr.matrix_world = R @ O["Wheel250"].matrix_world; rc.objects.link(wr)
lm = bpy.data.meshes.new("RakeLine"); lm.from_pydata([tuple(hub_d), tuple(hub_s)], [(0, 1)], []); rc.objects.link(bpy.data.objects.new("RakeLine", lm))
for name in ["Reference", "Collision_Fractured"] + [f"{v}_LOD{n}" for v in ("Fractured", "Deformed") for n in range(4)] + \
            [f"{g}_LOD{n}" for g in ("Body", "Leg", "Rod") for n in range(1, 4) if (g, n) in made]:
    def lc(layer):
        if layer.collection.name == name: return layer
        for ch in layer.children:
            r = lc(ch)
            if r: return r
    l = lc(bpy.context.view_layer.layer_collection)
    if l: l.exclude = True

# the spec, VRage block space = Blender (x, z, -y)
V = lambda v: [round(v.x, 5), round(v.z, 5), round(-v.y, 5)]
# the wheel's attachment (the suspension's idle point) such that the wheel's MESH centre lands on wheel_c: the wheel
# entity sits with its pivot dummy on the idle point, in the dummy's frame (X across, Y up the rake), so
# idle = centre - frame x (mesh offset - pivot) - checked in game on the 2.5 m (the wheel landed to the mm)
Vb = lambda v: Vector((v.x, v.z, -v.y))      # Blender -> VRage
ax, ay = Vector((1, 0, 0)), Vb(axis).normalized(); az = ax.cross(ay)
d = Vector(WHEEL_MODEL_OFFSET) - Vector(CFG["wheel_pivot"])
idle_v = Vb(wheel_c) - (ax * d.x + ay * d.y + az * d.z)
rest_v = idle_v + ay * TRAVEL / 2
attach = Vector((idle_v.x, -idle_v.z, idle_v.y)); rest = Vector((rest_v.x, -rest_v.z, rest_v.y))   # (back to Blender for V())
spec = {"name": N, "pivot": V(pivot), "hub_deployed": V(hub_d), "hub_stowed": V(hub_s), "rake_axis": V(axis),
        "retract": round(retract, 4), "wheel_centre_deployed": V(wheel_c), "wheel_attach": V(attach),
        "wheel_rest_dummy": V(rest), "travel": TRAVEL, "materials": {g: sorted(m) for g, m in mats.items()}}
json.dump(spec, open(SPEC, "w"), indent=1)
for o in src_objs:
    if o.name != "Wheel250": bpy.data.objects.remove(o)
bpy.data.objects.remove(O["Wheel250"])
def drop(c):
    for ch in list(c.children): drop(ch)
    bpy.data.collections.remove(c)
for c in src_cols: drop(c)
sc.frame_set(0)
bpy.ops.wm.save_as_mainfile(filepath=OUT, copy=False)
print("GEAREXP " + json.dumps(spec))
