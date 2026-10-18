"""PROPULSOR blocks' EXPORT scene (2026-10-16: turboprops, helicopter rotors, ducted fans) from the user's placeholder
blockouts (Rotorcraft_Blockouts.blend, collections Turboprop/Turboprop_050|150|250, Helicopter/Helicopter_150|250,
Fans/FanOct_*|FanSquare_*|FanD_*), written to a NEW file - the source is only read. Each source collection holds:
  <C>_Nacelle | _Mast | _Duct     the block's body (static)
  <C>_Spinner | _Hub + _Blades    the spinning subpart
  <C>_Occupancy                   wire boxes: the cells the block takes (one box per convex piece: the turboprop's nacelle
                                  3x1x1 + disc 5x5x1, the rotor's mast 1x1x2 + disc 9x9x1, a fan's one box)
The models are placeholders: a detailed model with the same object names is a re-run (prep -> export -> defs -> build).

Per block N (Turboprop_250, HelicopterRotor_150, DuctedFanOct_750, ...) the export scene gets collections prefixed "N|":
  N|Body_LOD0..3        the body, in the block frame: origin = the occupancy box's centre (Blender axes; VRage = (x, z, -y))
  N|Rotor_LOD0..2       spinner/hub + blades, origin on the spin axis at the blades' centre (the subpart's dummy)
  N|Fractured_LOD0..3   the body's two halves Fracture_01 / _02 (LOD3 one mesh); N|Deformed_LOD0..3 = + the guide's Displace
  N|Collision           N_Collision (the body's hull, inside the box) + occupancy_N_<k> (each occupancy box: cells only,
                        no physics - VRage.ContentPipeline.ModelsBase "occupancy" prefix) + mountpoints_N_<face> slabs
  N|Collision_Fractured Fracture_01 / _02 (each half's hull)
and a spec (JSON): box, cells, the subpart's dummy and spin axis (VRage block space), materials, thrust axis.

  blender -b Rotorcraft_Blockouts.blend -P propulsor_prep.py -- <out.blend> <spec.json> [only=Turboprop_250,...]
"""
import bpy, bmesh, json, sys
from mathutils import Matrix, Vector

argv = sys.argv[sys.argv.index("--") + 1:]
OUT, SPEC = argv[0], argv[1]
OPT = dict(a.split("=", 1) for a in argv[2:] if "=" in a)
ONLY = set(OPT["only"].split(",")) if "only" in OPT else None
DISPLACE = dict(strength=0.075, size=0.25, depth=4)
O = bpy.data.objects; sc = bpy.context.scene

# source collection prefix -> (block name prefix, kind, thrust axis (Blender), body suffix, rotor suffixes, mount faces of the body box)
# (faces: Blender axis sign letters - "-Y" the nacelle's back; a fan mounts by its rim, a rotor by its mast's foot)
KINDS = [
    ("Turboprop_", "Turboprop_", "turboprop", Vector((0, 1, 0)), "Nacelle", ("Spinner", "Blades"), ("-Y", "+Z", "-Z", "+X", "-X")),
    ("Helicopter_", "HelicopterRotor_", "rotor", Vector((0, 0, 1)), "Mast", ("Hub", "Blades"), ("-Z",)),
    ("FanOct_", "DuctedFanOct_", "fan", Vector((0, 1, 0)), "Duct", ("Hub", "Blades"), ("+X", "-X", "+Z", "-Z")),
    ("FanSquare_", "DuctedFanSquare_", "fan", Vector((0, 1, 0)), "Duct", ("Hub", "Blades"), ("+X", "-X", "+Z", "-Z")),
    ("FanD_", "DuctedFanD_", "fan", Vector((0, 1, 0)), "Duct", ("Hub", "Blades"), ("+X", "-X", "+Z", "-Z")),
]


def walk(c):
    yield c
    for ch in c.children: yield from walk(ch)


def bounds(objs):
    pts = [o.matrix_world @ Vector(v.co) for o in objs for v in o.data.vertices]
    return Vector([min(p[i] for p in pts) for i in range(3)]), Vector([max(p[i] for p in pts) for i in range(3)])


def baked(objs, M):
    out = []
    for o in objs:
        me = o.data.copy(); W = M @ o.matrix_world; me.transform(W)
        if W.determinant() < 0: me.flip_normals()
        while len(me.uv_layers) > 1: me.uv_layers.remove(me.uv_layers[-1])
        if not me.uv_layers: me.uv_layers.new(name="UVMap")
        me.uv_layers[0].name = "UVMap"
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


def decimate(o, ratio):
    if ratio >= 1: return
    m = o.modifiers.new("Dec", "DECIMATE"); m.ratio = ratio
    with bpy.context.temp_override(object=o, active_object=o):
        bpy.ops.object.modifier_apply(modifier=m.name)


def box_mesh(name, lo, hi):
    bm = bmesh.new(); bmesh.ops.create_cube(bm, size=1.0)
    c, s = (lo + hi) / 2, hi - lo
    for v in bm.verts: v.co = Vector((c.x + v.co.x * s.x, c.y + v.co.y * s.y, c.z + v.co.z * s.z))
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free(); return me


def hull(name, meshes, lo=None, hi=None, m=0.02):
    bm = bmesh.new()
    for me in meshes: bm.from_mesh(me)
    h = bmesh.ops.convex_hull(bm, input=bm.verts)
    bmesh.ops.delete(bm, geom=[g for g in h["geom_interior"] + h["geom_unused"] if isinstance(g, bmesh.types.BMVert)], context="VERTS")
    if lo is not None:   # (kept 2 cm inside the block's box: a hull on a face would take the neighbouring cells)
        for v in bm.verts:
            v.co = Vector([max(lo[i] + m, min(hi[i] - m, v.co[i])) for i in range(3)])
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free(); return me


def loose_boxes(o):
    """The occupancy object's loose parts as (lo, hi) boxes, world space."""
    bm = bmesh.new(); bm.from_mesh(o.data); bm.verts.ensure_lookup_table()
    seen, parts = set(), []
    for v in bm.verts:
        if v.index in seen: continue
        stack, part = [v], []
        while stack:
            x = stack.pop()
            if x.index in seen: continue
            seen.add(x.index); part.append(o.matrix_world @ x.co)
            stack.extend(e.other_vert(x) for e in x.link_edges)
        parts.append((Vector([min(p[i] for p in part) for i in range(3)]), Vector([max(p[i] for p in part) for i in range(3)])))
    bm.free()
    return parts


def halves(me):
    out = []
    for keep_left in (True, False):
        bm = bmesh.new(); bm.from_mesh(me)
        bmesh.ops.delete(bm, geom=[fa for fa in bm.faces if (fa.calc_center_median().x < 0) != keep_left], context="FACES")
        h = me.copy(); bm.to_mesh(h); bm.free(); out.append(h)
    return out


def snap(x, step=0.25): return round(x / step) * step


src_cols = [c for c in walk(sc.collection) if c is not sc.collection]
for c in src_cols: c.name = "src_" + c.name   # (out of the way of the new collections' names)
src_objs = list(sc.objects)
tex = bpy.data.textures.get("Deform_Clouds") or bpy.data.textures.new("Deform_Clouds", "CLOUDS")
tex.noise_type = "SOFT_NOISE"; tex.noise_scale = DISPLACE["size"]; tex.noise_depth = DISPLACE["depth"]; tex.cloud_type = "GRAYSCALE"
Vb = lambda v: [round(v.x, 5), round(v.z, 5), round(-v.y, 5)]   # Blender -> VRage block space
specs, hide = {}, []

for src in src_cols:
    name = src.name[4:]
    kind = next((k for k in KINDS if name.startswith(k[0])), None)
    if kind is None or not src.objects: continue
    prefix, nprefix, ktype, axis, body_sfx, rot_sfx, faces = kind
    size = name[len(prefix):]
    N = nprefix + size
    if ONLY and N not in ONLY: continue
    get = lambda s: O.get(f"{name}_{s}")
    body, occ = get(body_sfx), get("Occupancy")
    rot = [get(s) for s in rot_sfx if get(s) is not None]
    if body is None or occ is None or not rot: print(f"PROP SKIP {name}: missing objects"); continue
    boxes = loose_boxes(occ)
    lo = Vector([snap(min(b[0][i] for b in boxes), 0.125) for i in range(3)]); hi = Vector([snap(max(b[1][i] for b in boxes), 0.125) for i in range(3)])
    # the block frame's origin: the box's centre - or, along an axis an odd number of 0.25 m cells long, 0.125 m off it - so
    # that with the model's LOD0 shift (+0.125 m on every VRage axis, colliders too) the box's faces land on cell faces
    # (the occupancy generator's cells are centred on multiples of 0.25 m: BlockOccupancyGenerator.LocalToGridMaxInclusive)
    centre = Vector((0.0, 0.0, 0.0))
    for i in range(3):
        n = round((hi[i] - lo[i]) / 0.25)
        centre[i] = (hi[i] - (n // 2) * 0.25) if i == 1 else (lo[i] + (n // 2) * 0.25)   # (Blender Y is VRage -Z)
    T = Matrix.Translation(-centre)
    blo, bhi = lo - centre, hi - centre
    # the spin axis: through the body's origin, along the thrust axis; the subpart's origin at the blades' centre on it
    rlo, rhi = bounds(rot); a_idx = [i for i in range(3) if abs(axis[i]) > 0.5][0]
    pivot = body.matrix_world.translation.copy(); pivot[a_idx] = (rlo[a_idx] + rhi[a_idx]) / 2
    pivot_b = pivot - centre
    mats = {}
    made = {}
    for g, objs, M, ratios in (("Body", [body], T, [1.0, 0.5, 0.25, 0.1]), ("Rotor", rot, Matrix.Translation(-pivot), [1.0, 0.5, 0.25])):
        for n, r in enumerate(ratios):
            c = coll(f"{N}|{g}_LOD{n}")
            o = obj(f"{N}_{g}" + (f"_LOD{n}" if n else ""), baked(objs, M), c)
            decimate(o, r); made[(g, n)] = o
            mats.setdefault(g, set()).update(s.material.name for s in o.material_slots if s.material)
            if n: hide.append(c.name)
    # fractured / deformed (the body's halves by x)
    fr, de = coll(f"{N}|Fractured"), coll(f"{N}|Deformed")
    PIECES = ("Fracture_01", "Fracture_02")
    for n in range(4):
        b = made[("Body", n)]
        fc, dc = coll(f"{N}|Fractured_LOD{n}", fr), coll(f"{N}|Deformed_LOD{n}", de)
        hide += [fc.name, dc.name]
        parts = []
        if n < 3:
            for pn, me in zip(PIECES, halves(b.data)):
                p = bpy.data.objects.new(pn, me); fc.objects.link(p); p["group"] = p["ColliderMeshGroups"] = pn; parts.append(p)
        else:
            p = bpy.data.objects.new(f"{N}_Fractured_LOD3", b.data.copy()); fc.objects.link(p); parts.append(p)
        for p in parts:
            p["LOD"] = n
            d = p.copy(); d.data = p.data.copy(); dc.objects.link(d)
            m = d.modifiers.new("Deform", "DISPLACE"); m.texture = tex; m.texture_coords = "GLOBAL"
            m.direction = "RGB_TO_XYZ"; m.space = "LOCAL"; m.strength = DISPLACE["strength"]
    # collision: the body's hull (physics), the occupancy boxes (cells only), mount slabs on the body box's chosen faces
    cc, cf = coll(f"{N}|Collision"), coll(f"{N}|Collision_Fractured")
    hide.append(cf.name)
    h = bpy.data.objects.new(f"{N}_Collision", hull(f"{N}_Collision", [made[("Body", 0)].data], blo, bhi)); cc.objects.link(h); h["ExportName"] = f"{N}_Collision"
    occ_local = [(a - centre, b - centre) for a, b in boxes]
    for k, (a, b) in enumerate(occ_local):
        a = Vector([snap(x, 0.125) for x in a]); b = Vector([snap(x, 0.125) for x in b])
        o = bpy.data.objects.new(f"occupancy_{N}_{k}", box_mesh(f"occupancy_{N}_{k}", a, b)); cc.objects.link(o); o["ExportName"] = f"occupancy_{N}_{k}"
    # the body's box: the occupancy box holding the body's origin
    bo = body.matrix_world.translation - centre
    blo2, bhi2 = next(((a, b) for a, b in occ_local if all(a[i] - 1e-3 <= bo[i] <= b[i] + 1e-3 for i in range(3))), occ_local[0])
    blo2 = Vector([snap(x, 0.125) for x in blo2]); bhi2 = Vector([snap(x, 0.125) for x in bhi2])
    t = 0.05
    for f in faces:
        i = "XYZ".index(f[1]); a, b = blo2.copy(), bhi2.copy()
        if f[0] == "+": a[i] = b[i] - t
        else: b[i] = a[i] + t
        o = bpy.data.objects.new(f"mountpoints_{N}_{f}", box_mesh(f"mountpoints_{N}_{f}", a, b)); cc.objects.link(o)
        o["ExportName"] = f"mountpoints_{N}_{f[0].replace('+', 'P').replace('-', 'M')}{f[1]}"
    for pn, me in zip(PIECES, halves(made[("Body", 0)].data)):
        f2 = bpy.data.objects.new(pn + "_Collision", hull(pn + "_Collision", [me])); cf.objects.link(f2); f2["ExportName"] = pn
    for o in list(cc.objects) + list(cf.objects): o.display_type = "WIRE"
    size_m = hi - lo
    cells = [round(size_m[i] / 0.25) for i in range(3)]
    specs[N] = {"name": N, "source": name, "kind": ktype, "size": size, "cell_m": {"050": 0.5, "150": 1.5, "250": 2.5, "750": 7.5}.get(size),
                "box_m": Vb(size_m), "box_lo": Vb(lo - centre), "box_hi": Vb(hi - centre), "cells_vrage": [cells[0], cells[2], cells[1]],
                "subpart": Vb(pivot_b), "spin_axis": Vb(axis), "thrust_axis": Vb(axis),
                "rotor_radius": round(max(rhi[i] - rlo[i] for i in range(3) if i != a_idx) / 2, 3),
                "materials": {g: sorted(m) for g, m in mats.items()}, "occupancy": [[Vb(a), Vb(b)] for a, b in occ_local]}
    print(f"PROP {N}: box {list(size_m)} m, cells {cells}, subpart {list(pivot_b)}, {len(boxes)} occupancy box(es)")

# hide the extra LODs / fractured sets in the view (excluded, as the gear's)
def lc_of(nm, layer=None):
    layer = layer or bpy.context.view_layer.layer_collection
    if layer.collection.name == nm: return layer
    for ch in layer.children:
        r = lc_of(nm, ch)
        if r: return r
for nm in hide:
    l = lc_of(nm)
    if l: l.exclude = True
for o in src_objs: bpy.data.objects.remove(o)
def drop(c):
    for ch in list(c.children): drop(ch)
    bpy.data.collections.remove(c)
for c in [c for c in sc.collection.children if c.name.startswith("src_")]: drop(c)
json.dump(specs, open(SPEC, "w"), indent=1)
bpy.ops.wm.save_as_mainfile(filepath=OUT, copy=False)
print(f"PROPEXP {len(specs)} blocks -> {OUT}")
