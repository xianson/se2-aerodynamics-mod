"""TURBOPROP rig (2026-10-16): the approved Turboprop_250_v3 kitbash -> the SE2 export layout, one size per run, written to a
NEW file (the source is only read; run it on a COPY):

  250  the approved model itself: A5_* (the stock AtmosphericThruster500 body + its S1 / S2 subparts, turned intake-forward),
       PROP_Spinner + PROP_Blade.0/.1/.2, FAN_* (the stock S1 fan at the intake). World positions are kept exactly.
  150  the same recipe re-sourced from the stock AtmosphericThruster250 (SDK FBX: body, S1 propeller, S2 turbine): body + S2 +
       a static S1 at the thruster's centre (as the 250's A5_ copies), the spinning fan = the stock S1 at its vanilla dummy;
       the spinner + blades = the approved ones x 0.6 about the nacelle's front face.
  050  the same from the stock AtmosphericThruster100 (body, S1; it has no S2); spinner + blades x 0.2.

Block frame (Blender, metres): nose +Y, up +Z, the thrust / spin axis = Y through x = z = 0; origin = the block box's centre.
  size  cell  nacelle (x, z +-cell/2)   prop cell        prop disc (x, z)    blade tip r
  250   2.5   y -3.75 .. 1.25           1.25 .. 3.75     +-3.75 (3 x 3)      3.0
  150   1.5   y -1.75 .. 0.25 (stock 2.0 m)  0.25 .. 1.75  +-2.25 (3 x 3)    1.8
  050   0.5   y -0.75 .. 0.25           0.25 .. 0.75     +-0.75 (3 x 3)      0.6

Output collections (the gear / airbrake / propulsor conventions):
  Body_LOD0..3        Body (all body parts merged), LOD1..3 decimated 0.5 / 0.25 / 0.1
  Prop_LOD0..3        Prop = spinner + 3 blades, origin ON its pivot (on the axis at the blades' station)
  Fan_LOD0..3         Fan = the intake fan (blades, hub, nose cone), origin on its pivot (the stock S1 subpart origin)
  Dummies             Dummy_Prop, Dummy_Fan (empties at the pivots; identity rotation = the frame the meshes are baked in,
                      their local +Y = the spin axis)
  Collision           <N>_Collision (the body's hull, 2 cm inside the nacelle box), occupancy_<N>_0 (nacelle cells) and
                      occupancy_<N>_1 (prop-disc cells: cells only, no physics), mountpoints_<N>_<face> (back, sides)
  Fractured_LOD0..3 / Deformed_LOD0..3 / Collision_Fractured    the body's halves (by x), as propulsor_prep.py
  Ref                 Ref_Cells: wire boxes of the occupied cells
LOD1..3, Fractured, Deformed, Collision_Fractured and Ref are EXCLUDED from the view layer (not hidden).
Preview: drivers turn Prop and Fan about their local Y over frames 1..48 (one prop turn, two fan turns: loops).

  blender -b <copy of Turboprop_250_v3.blend> -P turboprop_rig.py -- <out.blend> <spec.json> 250|150|050
"""
import bpy, bmesh, json, math, sys
from mathutils import Matrix, Vector

OUT, SPEC, SIZE = sys.argv[sys.argv.index("--") + 1:][:3]
S = {"250": 2.5, "150": 1.5, "050": 0.5}[SIZE]
K = S / 2.5
N = f"Turboprop_{SIZE}"
SDK = r"D:/SteamLibrary/steamapps/common/Space Engineers 2 - Mod SDK/GameData/Vanilla/Assets/Blocks/Thrusters/Atmospheric/"
# stock thruster per size: (SDK folder, body fbx, S1 fbx, S2 fbx or None, length m, S1 dummy distance ahead of the model origin
# along the thrust: -(dummy Z - 0.125) from the vanilla BlockModelDefinition)
STOCK = {"250": ("500", "AtmosphericThruster500.fbx", "Subpart01/AtmosphericThruster500_S1_Propeller.fbx", "Subpart02/AtmosphericThruster500_S2_Turbine.fbx", 5.0, 1.8356146 + 0.125, 0.83985245 + 0.125),
         "150": ("250", "AtmosphericThruster250.fbx", "Subpart01/AtmosphericThruster250_S1_Propeller.fbx", "Subpart02/AtmosphericThruster250_S2_Turbine.fbx", 2.0, 0.54616255 + 0.125, 0.35812703 + 0.125),
         "050": ("100", "AtmosphericThruster100.fbx", "Subpart/AtmosphericThruster100_S1_Propeller.fbx", None, 1.0, 0.26664934 + 0.125, None)}
# (index 6: the S2 turbine's dummy distance, the same way - vanilla Subpart2 / Subpart02. 2026-10-17, the user: the internal
#  rotor seen through the side windows is the stock subpart; it sat at the thruster's centre, inside solid body, and a static
#  S1 copy with it. Now: S2 at its vanilla dummy, spinning with the fan (merged into the Fan subpart, same shaft); no static S1)
FRONT = {"250": 1.25, "150": 0.25, "050": 0.25}[SIZE]          # nacelle front face (y)
LENGTH = STOCK[SIZE][4]
YC = FRONT - LENGTH / 2                                          # the stock thruster's centre (its model origin)
REAR = FRONT - LENGTH
# the nacelle's CELLS (2026-10-17): whole cells of the block size - the stock Atmo 250 body is 2.0 m, not a whole number of 1.5 m
# cells, so the 150's nacelle box is 2 cells (3.0 m): the front stays, the extra 1.0 m is behind the body (the mesh is unchanged)
NACELLE_LEN = {"150": 3.0}.get(SIZE, LENGTH)
REAR_BOX = FRONT - NACELLE_LEN
FRONT_250 = 1.25
DISPLACE = dict(strength=0.075, size=0.25, depth=4)
RATIOS = [1.0, 0.5, 0.25, 0.1]
FRAMES = 48
O = bpy.data.objects; sc = bpy.context.scene
bpy.context.view_layer.update()
dg = bpy.context.evaluated_depsgraph_get()

# the stock frame -> block: the FBX's +X (exhaust) -> -Y, centred on the nacelle (proven on the 250: every A5_ body part is the
# stock AtmosphericThruster500 part under exactly this transform, within 1 mm)
T_STOCK = Matrix.Translation((0, YC, 0)) @ Matrix.Rotation(-math.pi / 2, 4, "Z")
# the approved spinner + blades, scaled about the nacelle's front face
T_PROP = Matrix.Translation((0, FRONT, 0)) @ Matrix.Scale(K, 4) @ Matrix.Translation((0, -FRONT_250, 0))


def world_mesh(o, M=Matrix.Identity(4)):
    """o's evaluated mesh (modifiers applied: the spinner's Weighted Normal) in M @ world space; materials kept."""
    me = bpy.data.meshes.new_from_object(o.evaluated_get(dg), preserve_all_data_layers=True, depsgraph=dg)
    W = M @ o.matrix_world
    me.transform(W)
    if W.determinant() < 0: me.flip_normals()
    while len(me.uv_layers) > 1: me.uv_layers.remove(me.uv_layers[-1])
    if not me.uv_layers: me.uv_layers.new(name="UVMap")
    me.uv_layers[0].name = "UVMap"
    return me


def stats(meshes):
    pts = [v.co.copy() for me in meshes for v in me.vertices]
    lo = Vector([min(p[i] for p in pts) for i in range(3)]); hi = Vector([max(p[i] for p in pts) for i in range(3)])
    c = sum(pts, Vector()) / len(pts)
    return {"verts": len(pts), "faces": sum(len(me.polygons) for me in meshes), "lo": list(lo), "hi": list(hi), "centroid": list(c)}


# ---- 1. the source meshes, in the block's world frame ----
src_meshes = {"Body": [], "Prop": [], "Fan": [], "Spinner": [], "Blades": []}
# (2026-10-17, the rotors' blur treatment: the prop split - the spinner stays, the blades get their own subpart that fades out at
#  speed, a blur disc fades in; "Prop" = both, kept for the pitch vote)
def add_prop(o, me):
    src_meshes["Prop"].append(me)
    src_meshes["Spinner" if o.name.startswith("PROP_Spinner") else "Blades"].append(me.copy())
mat_by_base = {m.name: m for m in bpy.data.materials if "." not in m.name}
imported = []

def import_fbx(rel):
    before = set(O); bpy.ops.import_scene.fbx(filepath=SDK + STOCK[SIZE][0] + "/Models/NonFractured/" + rel, global_scale=1.0)
    bpy.context.view_layer.update()
    new = [o for o in O if o not in before]; imported.extend(new)
    return [o for o in new if o.type == "MESH"]

def remap_materials(me):
    """FBX imports get "<name>.001" copies of the library materials already in the file: use the library ones."""
    for i, m in enumerate(me.materials):
        if m is None: continue
        base = m.name.split(".")[0]
        if base in mat_by_base: me.materials[i] = mat_by_base[base]
        else: print("TPRIG unmatched material", m.name)

def _sig(me):
    pts = [v.co for v in me.vertices]
    return len(pts), sum(pts, Vector()) / max(len(pts), 1)

# the stock S1 / S2 under T_STOCK (the static copies at the thruster's centre): their vertex counts and centroids, to drop the
# 250's A5_ copies of them from the body
STOCK_SUB = []
if SIZE == "250":
    for rel in (STOCK[SIZE][2], STOCK[SIZE][3]):
        for o_ in import_fbx(rel):
            STOCK_SUB.append(_sig(world_mesh(o_, T_STOCK)))
    for o_ in list(imported): O.remove(o_)
    imported.clear()
dropped = []

def stock_copy(o):
    n, c = _sig(world_mesh(o))
    hit = any(n == n2 and (c - c2).length < 2e-3 for n2, c2 in STOCK_SUB)
    if hit: dropped.append(o.name)
    return hit

if SIZE == "250":
    for o in O:
        if o.type != "MESH": continue
        if o.name.startswith("A5_"):
            if not stock_copy(o): src_meshes["Body"].append(world_mesh(o))
        elif o.name.startswith("PROP_"): add_prop(o, world_mesh(o))
        elif o.name.startswith("FAN_"): src_meshes["Fan"].append(world_mesh(o))
    fan_pivot = O["FAN_Cylinder.169"].matrix_world.translation.copy()   # (the fan copy's S1 subpart origin)
    fan_s1 = list(src_meshes["Fan"])
    for o_ in import_fbx(STOCK[SIZE][3]):                                # (S2 at its vanilla dummy, spinning with the fan)
        src_meshes["Fan"].append(world_mesh(o_, Matrix.Translation((0, STOCK[SIZE][6], 0)) @ T_STOCK))
    for me in src_meshes["Fan"]: remap_materials(me)
    print("TPRIG dropped static stock copies:", dropped)
else:
    for o in import_fbx(STOCK[SIZE][1]): src_meshes["Body"].append(world_mesh(o, T_STOCK))
    s1 = import_fbx(STOCK[SIZE][2])
    d = STOCK[SIZE][5]
    fan_pivot = Vector((0.0, YC + d, 0.0))                              # (the vanilla S1 dummy: the spinning fan)
    for o in s1: src_meshes["Fan"].append(world_mesh(o, Matrix.Translation((0, d, 0)) @ T_STOCK))
    fan_s1 = list(src_meshes["Fan"])
    if STOCK[SIZE][3]:                                                  # (S2 at its vanilla dummy, spinning with the fan)
        for o in import_fbx(STOCK[SIZE][3]): src_meshes["Fan"].append(world_mesh(o, Matrix.Translation((0, STOCK[SIZE][6], 0)) @ T_STOCK))
    for o in O:
        if o.type == "MESH" and o.name.startswith("PROP_"): add_prop(o, world_mesh(o, T_PROP))
    for me in src_meshes["Body"] + src_meshes["Fan"]: remap_materials(me)

before_stats = {g: stats(m) for g, m in src_meshes.items()}

# pivots: on the axis (x = z = 0). Prop: at the blades' station (the blade objects' origin, 1.47 on the 250); fan: its S1 origin
blade_y = O["PROP_Blade.0"].matrix_world.translation.y
prop_pivot = Vector((0.0, FRONT + (blade_y - FRONT_250) * K, 0.0))
fan_pivot = Vector((0.0, fan_pivot.y, 0.0))

# spin direction from the blade pitch: phi = the angle about +Y (right-handed: Z -> X); on each blade the edge further along
# +phi sits further forward (+y) when a right-handed turn drives air aft (thrust +Y)
def pitch_sign(meshes, piv):
    """+1 when a right-handed turn about +Y drives air aft. On the outer 40 % of the radius, each face's surface slope along the
    turning direction t (= Y x radial) is dy/dt = -n.t / n.y (sign-free in the normal's orientation): positive = the edge that
    leads a right-handed turn sits further forward. Area-weighted vote; also returns the vote's agreement and the tip radius."""
    rmax = 0.0; votes = []
    for me in meshes:
        for v in me.vertices:
            q = v.co - piv; rmax = max(rmax, math.hypot(q.x, q.z))
    for me in meshes:
        for f in me.polygons:
            c = f.center - piv; r = math.hypot(c.x, c.z)
            if r < 0.6 * rmax: continue
            n = f.normal
            t = Vector((0.0, 1.0, 0.0)).cross(Vector((c.x, 0.0, c.z)).normalized())
            if abs(n.y) < 0.2: continue   # (edges / tip caps)
            votes.append((-n.dot(t) / n.y, f.area))
    tot = sum(a for _, a in votes) or 1.0
    s = sum(a * (1 if k > 0 else -1) for k, a in votes) / tot
    return (1 if s > 0 else -1), round(abs(s), 3), rmax

prop_dir, prop_nb, prop_r = pitch_sign(src_meshes["Prop"], prop_pivot)
fan_dir, fan_nb, fan_r = pitch_sign(fan_s1, fan_pivot)   # (the S1 fan blades vote; the S2 drum spins with them)

# ---- 2. the source out of the way (dropped at the end) ----
src_cols = [c for c in bpy.data.collections]
for c in src_cols: c.name = "src_" + c.name    # (every collection, nested too: the new names must not get .001)
src_objs = sorted({o.name for o in sc.objects} | {o.name for o in imported})


def coll(name, parent=None):
    c = bpy.data.collections.new(name); (parent or sc.collection).children.link(c); return c


def join(name, meshes, c, at=Vector()):
    objs = []
    for i, me in enumerate(meshes):
        o = bpy.data.objects.new(f"{name}_p{i}", me); c.objects.link(o); objs.append(o)
    if len(objs) > 1:
        with bpy.context.temp_override(active_object=objs[0], selected_editable_objects=objs, selected_objects=objs):
            bpy.ops.object.join()
    o = objs[0]; o.name = name; o.data.name = name
    if at.length:   # origin on the pivot, world positions kept
        o.data.transform(Matrix.Translation(-at)); o.location = at
    return o


def decimate(o, r):
    if r >= 1: return
    m = o.modifiers.new("Dec", "DECIMATE"); m.ratio = r
    with bpy.context.temp_override(object=o, active_object=o): bpy.ops.object.modifier_apply(modifier=m.name)


def box_mesh(name, lo, hi, faces=True):
    bm = bmesh.new(); bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts: v.co = Vector(tuple((lo[i] + hi[i]) / 2 + v.co[i] * (hi[i] - lo[i]) for i in range(3)))
    if not faces: bmesh.ops.delete(bm, geom=list(bm.faces), context="FACES_ONLY")
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free(); return me


def hull_mesh(name, meshes, lo=None, hi=None, m=0.02):
    bm = bmesh.new()
    for me in meshes: bm.from_mesh(me)
    h = bmesh.ops.convex_hull(bm, input=bm.verts)
    bmesh.ops.delete(bm, geom=[g for g in h["geom_interior"] + h["geom_unused"] if isinstance(g, bmesh.types.BMVert)], context="VERTS")
    if lo is not None:   # (2 cm inside the nacelle box: a hull on a face would take the neighbouring cells)
        for v in bm.verts: v.co = Vector([max(lo[i] + m, min(hi[i] - m, v.co[i])) for i in range(3)])
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free(); return me


def halves(me):
    out = []
    for left in (True, False):
        bm = bmesh.new(); bm.from_mesh(me)
        bmesh.ops.delete(bm, geom=[f for f in bm.faces if (f.calc_center_median().x < 0) != left], context="FACES")
        h = me.copy(); bm.to_mesh(h); bm.free(); out.append(h)
    return out


# ---- 3. the export layout ----
made, mats = {}, {}
pivots = {"Body": Vector(), "Spinner": prop_pivot, "Blades": prop_pivot, "Fan": fan_pivot}
GROUPS = ("Body", "Spinner", "Blades", "Fan")
for g in GROUPS:
    for n, r in enumerate(RATIOS):
        meshes = src_meshes[g] if n == 0 else [made[(g, 0)].data.copy()]
        c = coll(f"{g}_LOD{n}")
        if n == 0:
            o = join(g, meshes, c, pivots[g])
        else:
            o = bpy.data.objects.new(f"{g}_LOD{n}", meshes[0]); o.data.name = o.name; c.objects.link(o); o.location = pivots[g]
            decimate(o, r)
        made[(g, n)] = o
        mats.setdefault(g, set()).update(s.material.name for s in o.material_slots if s.material)

dc = coll("Dummies")
dummies = {}
# the blur disc's dummy: on the axis, BLUR_AFT behind the prop pivot (as the rotors' - their blur sits 5 mm x cell / 1.5 m under
# the hub, the side away from the thrust - so that a part found by where it spawned is never ambiguous)
BLUR_AFT = 0.005 * S / 1.5
blur_pivot = prop_pivot - Vector((0.0, BLUR_AFT, 0.0))
for nm, at in (("Dummy_Prop", prop_pivot), ("Dummy_Blades", prop_pivot), ("Dummy_Blur", blur_pivot), ("Dummy_Fan", fan_pivot)):
    e = bpy.data.objects.new(nm, None); e.location = at; e.empty_display_type = "ARROWS"; e.empty_display_size = 0.6 * K
    e["spin_axis"] = (0.0, 1.0, 0.0); dc.objects.link(e); dummies[nm] = e

# ---- the blur disc (rotor_rig_v2.py's, for a prop): a thin flat annulus in the prop plane (normal = the spin axis, Blender Y),
# from the spinner's radius at the blades' station to the blade tips; ONE material, AeroRotorBlur (gen_propulsor_defs.py), its
# shared radial texture fitted to the prop by the UVs: root -> the texture's hinge, the blades' yellow tips -> its stripe band,
# tip -> BLUR_TIP_RHO (piecewise linear in radius)
BLUR_MAT, BLUR_TIP_RHO, BLUR_SEGS = "AeroRotorBlur", 0.98, [96, 48, 24]
TEX_HINGE, TEX_BAND = BLUR_TIP_RHO * 0.45 / 4.45, BLUR_TIP_RHO * (0.45 + 0.90 * 4.0) / 4.45   # (blur_material(): its hinge / band)
def _rad(q): return math.hypot(q.x, q.z)
_bl = [(v.co - prop_pivot) for me in src_meshes["Blades"] for v in me.vertices]
_bys = (min(q.y for q in _bl), max(q.y for q in _bl))
_sp = [(v.co - prop_pivot) for me in src_meshes["Spinner"] for v in me.vertices]
blur_ri = max((_rad(q) for q in _sp if _bys[0] <= q.y <= _bys[1]), default=0.1 * prop_r)
_yv = []
for me in src_meshes["Blades"]:
    for f in me.polygons:
        m = me.materials[f.material_index] if f.material_index < len(me.materials) else None
        if m is not None and "Yellow" in m.name and _rad(f.center - prop_pivot) > 0.5 * prop_r:
            _yv += [_rad(me.vertices[i].co - prop_pivot) for i in f.vertices]
blur_band = min(_yv) if _yv else 0.9 * prop_r
blur_ro = prop_r


def blur_mesh(name, ri, ro, band, seg):
    t = 0.004 * S
    def rho(r): return TEX_HINGE + (r - ri) / (band - ri) * (TEX_BAND - TEX_HINGE) if r <= band else TEX_BAND + (r - band) / (ro - band) * (BLUR_TIP_RHO - TEX_BAND)
    radii = [ri + (band - ri) * k / 6 for k in range(6)] + [band, ro]
    bm = bmesh.new(); uv = bm.loops.layers.uv.new("UVMap"); rings = []
    for r in radii:
        fr_ = [bm.verts.new(Vector((r * math.cos(2 * math.pi * j / seg), t / 2, r * math.sin(2 * math.pi * j / seg)))) for j in range(seg)]
        bk = [bm.verts.new(Vector((v.co.x, -t / 2, v.co.z))) for v in fr_]
        rings.append((r, fr_, bk))
    def face(vs):
        f = bm.faces.new(vs)
        for l in f.loops:
            r = math.hypot(l.vert.co.x, l.vert.co.z); k = rho(r) / r
            l[uv].uv = (0.5 + 0.5 * k * l.vert.co.x, 0.5 + 0.5 * k * l.vert.co.z)
    for (ra, ta, ba), (rb_, tb, bb) in zip(rings, rings[1:]):
        for j in range(seg):
            k = (j + 1) % seg
            face((ta[j], tb[j], tb[k], ta[k])); face((ba[k], bb[k], bb[j], ba[j]))
    for (r, tp, bt), outer in ((rings[0], False), (rings[-1], True)):
        for j in range(seg):
            k = (j + 1) % seg
            face((tp[j], tp[k], bt[k], bt[j]) if outer else (tp[k], tp[j], bt[j], bt[k]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    m = bpy.data.materials.get(BLUR_MAT) or bpy.data.materials.new(BLUR_MAT); m.diffuse_color = (0.05, 0.05, 0.06, 0.35)
    me.materials.append(m); me.shade_smooth()
    return me


for n, seg in enumerate(BLUR_SEGS):
    c = coll(f"Blur_LOD{n}")
    o = bpy.data.objects.new("Blur" + (f"_LOD{n}" if n else ""), blur_mesh(f"Blur_LOD{n}", blur_ri, blur_ro, blur_band, seg))
    c.objects.link(o); o.location = blur_pivot; made[("Blur", n)] = o

# body box (the nacelle), the prop-disc cells
nlo, nhi = Vector((-S / 2, REAR_BOX, -S / 2)), Vector((S / 2, FRONT, S / 2))
dlo, dhi = Vector((-1.5 * S, FRONT, -1.5 * S)), Vector((1.5 * S, FRONT + S, 1.5 * S))
blo, bhi = Vector((dlo.x, REAR_BOX, dlo.z)), Vector((dhi.x, dhi.y, dhi.z))

tex = bpy.data.textures.get("Deform_Clouds") or bpy.data.textures.new("Deform_Clouds", "CLOUDS")
tex.noise_type = "SOFT_NOISE"; tex.noise_scale = DISPLACE["size"] * K; tex.noise_depth = DISPLACE["depth"]; tex.cloud_type = "GRAYSCALE"
fr, de = coll("Fractured"), coll("Deformed")
PIECES = ("Fracture_01", "Fracture_02")
for n in range(4):
    fc, dc2 = coll(f"Fractured_LOD{n}", fr), coll(f"Deformed_LOD{n}", de)
    src = made[("Body", n)].data; parts = []
    if n < 3:
        for pn, me in zip(PIECES, halves(src)):
            p = bpy.data.objects.new(pn, me); fc.objects.link(p); p["group"] = p["ColliderMeshGroups"] = pn; parts.append(p)
    else:
        p = bpy.data.objects.new(f"{N}_Fractured_LOD3", src.copy()); fc.objects.link(p); parts.append(p)
    for p in parts:
        p["LOD"] = n
        d2 = p.copy(); d2.data = p.data.copy(); dc2.objects.link(d2)
        m = d2.modifiers.new("Deform", "DISPLACE"); m.texture = tex; m.texture_coords = "GLOBAL"
        m.direction = "RGB_TO_XYZ"; m.space = "LOCAL"; m.strength = DISPLACE["strength"] * K

cc, cf = coll("Collision"), coll("Collision_Fractured")
def add(c, name, me, export=None):
    o = bpy.data.objects.new(name, me); c.objects.link(o); o["ExportName"] = export or name; o.display_type = "WIRE"; return o
body0 = made[("Body", 0)].data
add(cc, f"{N}_Collision", hull_mesh(f"{N}_Collision", [body0], nlo, nhi))
add(cc, f"occupancy_{N}_0", box_mesh(f"occupancy_{N}_0", nlo, nhi))
add(cc, f"occupancy_{N}_1", box_mesh(f"occupancy_{N}_1", dlo, dhi))
t = 0.05
for f in ("-Y", "+Z", "-Z", "+X", "-X"):
    i = "XYZ".index(f[1]); a, b = nlo.copy(), nhi.copy()
    if f[0] == "+": a[i] = b[i] - t
    else: b[i] = a[i] + t
    add(cc, f"mountpoints_{N}_{f}", box_mesh(f"mountpoints_{N}_{f}", a, b), f"mountpoints_{N}_{f[0].replace('+', 'P').replace('-', 'M')}{f[1]}")
for pn, me in zip(PIECES, halves(body0)):
    add(cf, pn + "_Collision", hull_mesh(pn + "_Collision", [me]), pn)

rc = coll("Ref")
for nm, (a, b) in (("Ref_Nacelle", (nlo, nhi)), ("Ref_PropCells", (dlo, dhi))):
    o = bpy.data.objects.new(nm, box_mesh(nm, a, b, faces=False)); rc.objects.link(o)
# (the prop-disc cell lines, one cell = S)
bm = bmesh.new()
for k in (-0.5, 0.5):
    for y in (FRONT, FRONT + S):
        a = bm.verts.new((k * S, y, dlo.z)); b = bm.verts.new((k * S, y, dhi.z)); bm.edges.new((a, b))
        a = bm.verts.new((dlo.x, y, k * S)); b = bm.verts.new((dhi.x, y, k * S)); bm.edges.new((a, b))
me = bpy.data.meshes.new("Ref_CellLines"); bm.to_mesh(me); bm.free(); o = bpy.data.objects.new("Ref_CellLines", me); rc.objects.link(o)

# ---- 4. the preview spin (drivers: frame 1 = rest pose, frames 1..48 loop) ----
sc.frame_start, sc.frame_end = 1, FRAMES; sc.render.fps = 24
for o, turns, sign in ((made[("Spinner", 0)], 1, prop_dir), (made[("Blades", 0)], 1, prop_dir), (made[("Fan", 0)], 2, fan_dir)):
    o.rotation_mode = "XYZ"
    fc_ = o.driver_add("rotation_euler", 1); drv = fc_.driver; drv.type = "SCRIPTED"
    drv.expression = f"(frame - 1) / {FRAMES} * {2 * math.pi * turns * sign:.6f}"
sc.frame_set(1)

# ---- 5. the source dropped, extra collections excluded ----
for nm in src_objs:
    if nm in O: bpy.data.objects.remove(O[nm])
def drop(c):
    for ch in list(c.children): drop(ch)
    bpy.data.collections.remove(c)
for c in [c for c in sc.collection.children if c.name.startswith("src_")]: drop(c)
for c in [c for c in bpy.data.collections if c.name.startswith("src_")]: bpy.data.collections.remove(c)
for cam in [c for c in bpy.data.cameras if c.users == 0]: bpy.data.cameras.remove(cam)
bpy.data.orphans_purge(do_local_ids=True, do_linked_ids=True, do_recursive=True)

def lc_of(nm, layer=None):
    layer = layer or bpy.context.view_layer.layer_collection
    if layer.collection.name == nm: return layer
    for ch in layer.children:
        r = lc_of(nm, ch)
        if r: return r
for nm in [f"{g}_LOD{n}" for g in GROUPS for n in (1, 2, 3)] + [f"Blur_LOD{n}" for n in range(3)] + ["Fractured", "Deformed", "Collision_Fractured", "Ref"]:
    lc_of(nm).exclude = True

# ---- 6. the spec ----
Vb = lambda v: [round(v.x, 5), round(v.z, 5), round(-v.y, 5)]   # Blender -> VRage block space
R5 = lambda v: [round(x, 5) for x in v]
after_stats = {}
for g in GROUPS:
    o = made[(g, 0)]
    me = o.data.copy(); me.transform(o.matrix_world); after_stats[g] = stats([me]); bpy.data.meshes.remove(me)
spec = {
    "name": N, "size": SIZE, "cell_m": S, "scale_vs_250": K,
    "source": "Turboprop_250_v3.blend (approved)" if SIZE == "250" else f"stock AtmosphericThruster{STOCK[SIZE][0]} (SDK FBX) + the approved spinner/blades x {K}",
    "frame": "Blender metres; nose +Y, up +Z; VRage = (x, z, -y) (+0.125 LOD0 model offset in the definitions)",
    "nacelle": {"lo": R5(nlo), "hi": R5(nhi)}, "prop_cells": {"lo": R5(dlo), "hi": R5(dhi)}, "box": {"lo": R5(blo), "hi": R5(bhi)},
    "cells_quarter_m": [round((bhi[i] - blo[i]) / 0.25) for i in range(3)],
    "pivots": {"Prop": {"blender": R5(prop_pivot), "vrage": Vb(prop_pivot), "dummy": "Dummy_Prop"},
               "Blades": {"blender": R5(prop_pivot), "vrage": Vb(prop_pivot), "dummy": "Dummy_Blades"},
               "Fan": {"blender": R5(fan_pivot), "vrage": Vb(fan_pivot), "dummy": "Dummy_Fan"}},
    "spin": {"axis_blender": [0, 1, 0], "axis_vrage": [0, 0, -1],
             "Prop": {"sense": "right-handed about +Y (Z towards X, clockwise seen from the front)" if prop_dir > 0 else "left-handed about +Y (X towards Z, counter-clockwise seen from the front)",
                      "sign_about_+Y": prop_dir, "vrage_rotation_axis": [0, 0, -1] if prop_dir > 0 else [0, 0, 1], "agreement": prop_nb, "tip_radius": round(prop_r, 4)},
             "Fan": {"sense": "right-handed about +Y" if fan_dir > 0 else "left-handed about +Y", "sign_about_+Y": fan_dir,
                     "vrage_rotation_axis": [0, 0, -1] if fan_dir > 0 else [0, 0, 1], "agreement": fan_nb, "tip_radius": round(fan_r, 4)},
             "preview": f"drivers, frames 1..{FRAMES}: Prop 1 turn, Fan 2 turns"},
    "occupancy": [[R5(nlo), R5(nhi)], [R5(dlo), R5(dhi)]],
    "polycount": {**{f"{g}_LOD{n}": len(made[(g, n)].data.polygons) for g in GROUPS for n in range(4)}, **{f"Blur_LOD{n}": len(made[("Blur", n)].data.polygons) for n in range(3)}},
    "blur": {"blender": R5(blur_pivot), "vrage": Vb(blur_pivot), "dummy": "Dummy_Blur", "aft_of_prop_pivot": round(BLUR_AFT, 5), "inner_r": round(blur_ri, 4),
             "band_r": round(blur_band, 4), "outer_r": round(blur_ro, 4), "thickness": round(0.004 * S, 5), "segments": BLUR_SEGS, "material": BLUR_MAT,
             "uv": "piecewise in radius: inner -> texture hinge, band -> texture band, tip -> BLUR_TIP_RHO"},
    "materials": {g: sorted(m) for g, m in mats.items()},
    "check_world_before": before_stats, "check_world_after": after_stats,
}
json.dump(spec, open(SPEC, "w"), indent=1)
bpy.ops.wm.save_as_mainfile(filepath=OUT, copy=False)
print(f"TPRIG {N}: prop pivot {R5(prop_pivot)} fan pivot {R5(fan_pivot)} prop dir {prop_dir} (agreement {prop_nb}, tip r {prop_r:.3f}) fan dir {fan_dir} (agreement {fan_nb}, tip r {fan_r:.3f})")
for g in GROUPS:
    b, a = before_stats[g], after_stats[g]
    dmax = max(max(abs(b["lo"][i] - a["lo"][i]), abs(b["hi"][i] - a["hi"][i]), abs(b["centroid"][i] - a["centroid"][i])) for i in range(3))
    print(f"TPRIG check {g}: verts {b['verts']} -> {a['verts']}, max bbox/centroid shift {dmax * 1000:.4f} mm")
