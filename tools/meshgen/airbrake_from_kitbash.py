"""The airbrake's EXPORT scene from the user's kitbash (2026-10-14, Airbrake_Large_Kitbash.blend, "Kitbash" collection: the
control surface's hinge base along the front and a TrussFrame_Wide flat in the middle - the body; two halves of the
control surface's flap + hinge head, the upper one a 180 deg turned copy of the lower - the leaves), written to a NEW file
per size (the source is not touched) with a spec for the definitions and the mod:

  Body_LOD0..3        hinge base + truss (LOD1..3 decimated)
  LeafUp_LOD0..2      the upper leaf, its origin ON the hinge: it turns about X, trailing edge up
  LeafDown_LOD0..2    the lower leaf, origin on the hinge
  Fractured_LOD0..3 / Deformed_LOD0..3   the body in two pieces (its halves)
  Collision           <N>_Collision (the body's hull) + mountpoints_<N>_L / _R / _F (slabs on the left, right and front faces)
  Collision_Fractured each half's hull
The kitbash is built in the control surface's axes (chord +X aft of the hinge, span Y): turned so the chord runs aft (-Y:
the block's Forward is Blender +Y) and the span across (X), the 3 x 3 x 3 box centred. open_deg: the most the leaves open
with every vertex inside the box (found from the leaves themselves).

  blender -b Airbrake_Large_Kitbash.blend -P airbrake_from_kitbash.py -- <out.blend> <spec.json> Large|Small
"""
import bpy, bmesh, json, math, sys
from mathutils import Matrix, Vector

OUT, SPEC, SIZE = sys.argv[sys.argv.index("--") + 1:][:3]
s = {"Large": 1.0, "Small": 0.2}[SIZE]
N = f"Airbrake_{SIZE}"; H = 3.75 * s
O = bpy.data.objects; sc = bpy.context.scene
BODY = ["ControlSurface_Hinge_Base", "TrussFrame_Wide"]
UP = ["ControlSurface_Hinge_Head_Lower.001", "ControlSurface_Flap_Lower.001"]
DOWN = ["ControlSurface_Hinge_Head_Lower", "ControlSurface_Flap_Lower.002"]
DISPLACE = dict(strength=0.075, size=0.25, depth=4)
bpy.context.view_layer.update()

hinge_k = O["ControlSurface_Hinge_Head_Lower"].matrix_world.translation.copy()
pts = [O[n].matrix_world @ Vector(c) for n in BODY + UP + DOWN for c in O[n].bound_box]
cx = (min(p.x for p in pts) + max(p.x for p in pts)) / 2
# kitbash -> block: chord +X -> -Y, span +Y -> +X (turned -90 deg about Z), about (box centre along the chord, the hinge)
TURN = Matrix.Scale(s, 4) @ Matrix.Rotation(-math.pi / 2, 4, "Z") @ Matrix.Translation(-Vector((cx, hinge_k.y, hinge_k.z)))
hb = TURN @ hinge_k


def baked(names, M):
    out = []
    for n in names:
        o = O[n]; W = M @ o.matrix_world; me = o.data.copy(); me.transform(W)
        if W.determinant() < 0: me.flip_normals()
        while len(me.uv_layers) > 1: me.uv_layers.remove(me.uv_layers[-1])
        if me.uv_layers: me.uv_layers[0].name = "UVMap"
        out.append(me)
    return out


def join(name, meshes, c):
    objs = []
    for i, me in enumerate(meshes):
        o = bpy.data.objects.new(f"{name}_p{i}", me); c.objects.link(o); objs.append(o)
    if len(objs) > 1:
        with bpy.context.temp_override(active_object=objs[0], selected_editable_objects=objs, selected_objects=objs):
            bpy.ops.object.join()
    o = objs[0]; o.name = name; o.data.name = name; return o


def decimate(o, r):
    if r >= 1: return
    m = o.modifiers.new("Dec", "DECIMATE"); m.ratio = r
    with bpy.context.temp_override(object=o, active_object=o): bpy.ops.object.modifier_apply(modifier=m.name)


def hull_mesh(name, meshes):
    bm = bmesh.new()
    for me in meshes: bm.from_mesh(me)
    h = bmesh.ops.convex_hull(bm, input=bm.verts)
    bmesh.ops.delete(bm, geom=[g for g in h["geom_interior"] + h["geom_unused"] if isinstance(g, bmesh.types.BMVert)], context="VERTS")
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free(); return me


def box_mesh(name, lo, hi):
    bm = bmesh.new(); bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts: v.co = Vector(tuple((lo[i] + hi[i]) / 2 + v.co[i] * (hi[i] - lo[i]) for i in range(3)))
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free(); return me


def halves(me):
    out = []
    for left in (True, False):
        bm = bmesh.new(); bm.from_mesh(me)
        bmesh.ops.delete(bm, geom=[f for f in bm.faces if (f.calc_center_median().x < 0) != left], context="FACES")
        h = me.copy(); bm.to_mesh(h); bm.free(); out.append(h)
    return out


# the source out of the way (dropped from the new file at the end)
src_cols = list(sc.collection.children)
for c in list(bpy.data.collections): c.name = "src_" + c.name   # (all of them: nested ones too - the blockout's Fractured_LOD0 ...)
src_objs = list(sc.objects)

def coll(name, parent=None):
    c = bpy.data.collections.new(name); (parent or sc.collection).children.link(c); return c

made, mats = {}, {}
groups = {"Body": (BODY, TURN, [1.0, 0.5, 0.25, 0.1]),
          "LeafUp": (UP, Matrix.Translation(-hb) @ TURN, [1.0, 0.5, 0.25]),
          "LeafDown": (DOWN, Matrix.Translation(-hb) @ TURN, [1.0, 0.5, 0.25])}
for g, (names, M, ratios) in groups.items():
    for n, r in enumerate(ratios):
        o = join(f"{N}_{g}" + (f"_LOD{n}" if n else ""), baked(names, M), coll(f"{g}_LOD{n}"))
        decimate(o, r); made[(g, n)] = o
        mats.setdefault(g, set()).update(sl.material.name for sl in o.material_slots if sl.material)

tex = bpy.data.textures.get("Deform_Clouds") or bpy.data.textures.new("Deform_Clouds", "CLOUDS")
tex.noise_type = "SOFT_NOISE"; tex.noise_scale = DISPLACE["size"]; tex.noise_depth = DISPLACE["depth"]; tex.cloud_type = "GRAYSCALE"
fr, de = coll("Fractured"), coll("Deformed")
for n in range(4):
    fc, dc = coll(f"Fractured_LOD{n}", fr), coll(f"Deformed_LOD{n}", de)
    src = made[("Body", n)].data; parts = []
    if n < 3:
        for name, me in zip(("Fracture_01", "Fracture_02"), halves(src)):
            p = bpy.data.objects.new(name, me); fc.objects.link(p); p["group"] = p["ColliderMeshGroups"] = name; parts.append(p)
    else:
        p = bpy.data.objects.new(f"{N}_Fractured_LOD3", src.copy()); fc.objects.link(p); parts.append(p)
    for p in parts:
        p["LOD"] = n
        d = p.copy(); d.data = p.data.copy(); dc.objects.link(d)
        m = d.modifiers.new("Deform", "DISPLACE"); m.texture = tex; m.texture_coords = "GLOBAL"
        m.direction = "RGB_TO_XYZ"; m.space = "LOCAL"; m.strength = DISPLACE["strength"] * s

cc, cf = coll("Collision"), coll("Collision_Fractured")
def add(c, name, me):
    o = bpy.data.objects.new(name, me); c.objects.link(o); o["ExportName"] = name; o.display_type = "WIRE"; return o
body0 = made[("Body", 0)].data
add(cc, f"{N}_Collision", hull_mesh(f"{N}_Collision", [body0]))
t = 0.125 * s
add(cc, f"mountpoints_{N}_L", box_mesh("mpL", (-H, -H, -H), (-H + t, H, H)))
add(cc, f"mountpoints_{N}_R", box_mesh("mpR", (H - t, -H, -H), (H, H, H)))
add(cc, f"mountpoints_{N}_F", box_mesh("mpF", (-H, H - t, -H), (H, H, H)))
for name, me in zip(("Fracture_01", "Fracture_02"), halves(body0)):
    add(cf, name + "_Collision", hull_mesh(name + "_Collision", [me]))["ExportName"] = name

# how far the leaves open with every vertex inside the box (up: turned -a about X, trailing edge up; down: +a)
def limit(me, up):
    vs = [v.co.copy() for v in me.vertices]; best = 0.0
    for tenth in range(0, 901, 5):
        R = Matrix.Rotation(math.radians(tenth / 10) * (-1 if up else 1), 3, "X")
        if all(abs((R @ v + hb).z) <= H + 1e-3 and abs((R @ v + hb).y) <= H + 1e-3 for v in vs): best = tenth / 10
        else: break
    return best
open_deg = min(limit(made[("LeafUp", 0)].data, True), limit(made[("LeafDown", 0)].data, False))
lv = [v.co for v in made[("LeafUp", 0)].data.vertices]
chord = max(-v.y for v in lv); span = max(v.x for v in lv) - min(v.x for v in lv)

for o in src_objs: bpy.data.objects.remove(o)
def drop(c):
    for ch in list(c.children): drop(ch)
    bpy.data.collections.remove(c)
for c in src_cols: drop(c)
vl = bpy.context.view_layer.layer_collection
for lc in vl.children:
    if lc.collection.name in ("Fractured", "Deformed", "Collision_Fractured") or lc.collection.name[-5:-1] == "_LOD" and lc.collection.name[-1] != "0":
        lc.exclude = True

spec = {"name": N, "scale": s, "box": 2 * H, "hinge": list(hb), "leaf_up": [hb.x, hb.y, hb.z + 0.005], "leaf_down": [hb.x, hb.y, hb.z - 0.005],
        "chord": chord, "span": span, "open_deg": open_deg, "materials": {g: sorted(m) for g, m in mats.items()}}
json.dump(spec, open(SPEC, "w"), indent=1)
bpy.ops.wm.save_as_mainfile(filepath=OUT, copy=False)
print(f"ABKIT {N}: hinge ({hb.x:.3f}, {hb.y:.3f}, {hb.z:.3f}), leaf chord {chord:.2f} m span {span:.2f} m, opens {open_deg:.1f} deg, materials {spec['materials']}")
