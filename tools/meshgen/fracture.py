"""Fractured + Deformed sets for the control-surface block, per Keen's VRAGE3 guide ("Create New Thruster": boolean cuts
with deformed planes, matching on every LOD; Deformed = Fractured + Displace) and the layout of Keen's own files:

  Fractured_LOD0..2   pieces Fracture_01..06 (the same three cut planes on every LOD): the body (Body_LODn) plus a static
                      stand-in of the flap subpart (Flap_LODn + its hinge head) whose materials are "<name>_Subpart" copies
                      (the game hides those mesh parts while the real flap is spawned: ConditionalSubParts'
                      MeshPartsToHideWhenActive "Fracture_0N-<material>_Subpart"). Cut faces: FracturedMaterial01.
                      Each piece: custom props group = ColliderMeshGroups = its name (Keen's example mod).
  Fractured_LOD3      the last LOD: one mesh (its baked material comes later)
  Deformed_LOD0..3    copies of the Fractured LODs with the guide's Displace (Clouds soft, size 0.25, depth 4, global
                      coordinates, RGB to XYZ, local space, strength 0.075 at 2.5 m)
  Collision_Fractured one convex collider per piece (Fracture_0N_Collision; exported under the piece's name)
The subpart (Flap) stays NonFractured only (Keen's subparts have no fractured sets).

Run: blender -b <ControlSurface_Large_3x3x1.blend> -P fracture.py        (the large file; the small one is a 0.2x copy)
"""
import bpy, bmesh, math
from mathutils import Matrix, Vector

LIB = r"D:\Blender Brojects\SE2\AssetLibrary\SE2Decal&Material.blend"
B = 2.5
# three tilted cut planes (point, normal), world space, large block; never axis-aligned so the breaks read as breaks
PLANES = [
    (Vector((-1.0, -1.30, 0.0)), Vector((0.15, 1.0, 0.10)).normalized()),    # span cut, left third
    (Vector((0.5, 1.20, 0.0)), Vector((-0.12, 1.0, -0.08)).normalized()),     # span cut, right third
    (Vector((-0.6, 0.0, 0.0)), Vector((1.0, 0.25, 0.0)).normalized()),        # chord cut: hinge + wedge root | wedge (vertical: a Z tilt slices the sloped wedge into a sliver)
]
DISPLACE = dict(strength=0.075, size=0.25, depth=4)
CAP_UV_PER_M = 0.35
sc = bpy.context.scene
vl = bpy.context.view_layer
log = []


def region_of(p):
    s = [(p - c).dot(n) for c, n in PLANES]
    span = 0 if s[0] < 0 else (2 if s[1] > 0 else 1)
    return span * 2 + (1 if s[2] > 0 else 0)          # 0..5 -> Fracture_01..06


def ensure_collection(name, parent):
    c = bpy.data.collections.get(name)
    if c is None:
        c = bpy.data.collections.new(name); parent.children.link(c)
    for o in list(c.objects): bpy.data.objects.remove(o, do_unlink=True)
    return c


def lib_material(name):
    m = bpy.data.materials.get(name)
    if m: return m
    with bpy.data.libraries.load(LIB, link=True, assets_only=True) as (src, dst):
        dst.materials = [name]
    return bpy.data.materials[name]


def subpart_material(m):
    name = m.name + "_Subpart"
    return bpy.data.materials.get(name) or _local_copy(m, name)


def _local_copy(m, name):
    c = m.copy(); c.name = name
    return c


def world(o):
    """o's world matrix from its own transforms (objects in excluded collections are not evaluated: matrix_world is stale)."""
    m = o.matrix_basis
    return world(o.parent) @ o.matrix_parent_inverse @ m if o.parent else m.copy()


def world_bmesh(o):
    bm = bmesh.new(); bm.from_mesh(o.data); bm.transform(world(o))
    return bm


def cut_pieces(o, standin, frac_mat):
    """o's mesh in world space cut by the planes ONE AT A TIME: each cut splits every piece in two and caps each half's
    opening on that plane (flat caps; filling after all cuts bridged two planes' openings with one diagonal face).
    Returns {region: (mesh, materials)}."""
    mats = [s.material for s in o.material_slots]
    if standin: mats = [subpart_material(m) if m else None for m in mats]
    fi = len(mats); mats = mats + [frac_mat]
    pieces = [((), world_bmesh(o))]
    for c, n in PLANES:
        nxt = []
        for sides, bm in pieces:
            bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], plane_co=c, plane_no=n, dist=1e-5)
            for side in (False, True):
                b2 = bm.copy()
                drop = [f for f in b2.faces if ((f.calc_center_median() - c).dot(n) > 0) != side]
                bmesh.ops.delete(b2, geom=drop, context="FACES")
                bmesh.ops.delete(b2, geom=[v for v in b2.verts if not v.link_faces], context="VERTS")
                if not b2.faces: b2.free(); continue
                on = lambda v: abs((v.co - c).dot(n)) < 1e-4
                edges = [e for e in b2.edges if e.is_boundary and on(e.verts[0]) and on(e.verts[1])]
                if edges:
                    caps = bmesh.ops.holes_fill(b2, edges=edges, sides=0)["faces"]
                    for f in caps: f.material_index = fi
                    bmesh.ops.triangulate(b2, faces=caps, quad_method="BEAUTY", ngon_method="BEAUTY")
                    # (triangulate returns only the faces it made: take every cap face on this plane instead)
                    caps = [f for f in b2.faces if f.material_index == fi and all(on(v) for v in f.verts)]
                    # planar UVs on the cut plane at Keen's density for fracture faces (~0.35 UV per metre on the
                    # vanilla hinge and atmospheric thruster); holes_fill leaves them unset
                    u = n.cross(Vector((0, 0, 1)) if abs(n.z) < 0.9 else Vector((1, 0, 0))).normalized(); v = n.cross(u)
                    uvl = b2.loops.layers.uv.active or b2.loops.layers.uv.new("UVMap")
                    for f in caps:
                        f.material_index = fi; f.smooth = False
                        for l in f.loops: l[uvl].uv = (l.vert.co.dot(u) * CAP_UV_PER_M, l.vert.co.dot(v) * CAP_UV_PER_M)
                nxt.append((sides + (side,), b2))
            bm.free()
        pieces = nxt
    out = {}
    for sides, bm in pieces:
        span = 0 if not sides[0] else (2 if sides[1] else 1)
        r = span * 2 + (1 if sides[2] else 0)
        me = bpy.data.meshes.new(f"{o.name}_r{r}"); bm.to_mesh(me); bm.free()
        if r in out:   # (cannot happen with these planes: they do not cross inside the block)
            raise RuntimeError(f"region {r} twice")
        out[r] = (me, mats)
    return out


def mesh_object(name, me, mats, coll):
    # one UV channel, one name: Keen's hinge parts carry "map1" (Maya), the wedge "UVMap"; joined, they made two
    # channels and the wedge's UVs sat in the second (the game reads the first)
    while len(me.uv_layers) > 1: me.uv_layers.remove(me.uv_layers[-1])
    if me.uv_layers: me.uv_layers[0].name = "UVMap"
    for m in mats: me.materials.append(m)
    o = bpy.data.objects.new(name, me); coll.objects.link(o)
    return o


def join_into(objs, name):
    objs = [o for o in objs if o]
    sc.collection.objects.link(objs[0]) if not objs[0].users_collection else None
    if len(objs) > 1:
        with bpy.context.temp_override(active_object=objs[0], selected_editable_objects=objs, selected_objects=objs):
            bpy.ops.object.join()
    objs[0].name = name; objs[0].data.name = name
    return objs[0]


def sources(n):
    base = next(o for o in bpy.data.collections[f"Body_LOD{n}"].objects if o.type == "MESH")
    flap = bpy.data.objects[f"Flap_LOD{n}"]
    head = next(o for o in flap.children if o.type == "MESH")
    return base, flap, head


def main():
    frac_mat = lib_material("FracturedMaterial01")
    root = sc.collection
    fr = bpy.data.collections.get("Fractured") or bpy.data.collections.new("Fractured")
    de = bpy.data.collections.get("Deformed") or bpy.data.collections.new("Deformed")
    for c in (fr, de):
        if c.name not in root.children: root.children.link(c)
    colf = ensure_collection("Collision_Fractured", root)
    tex = bpy.data.textures.get("Deform_Clouds") or bpy.data.textures.new("Deform_Clouds", "CLOUDS")
    tex.noise_type = "SOFT_NOISE"; tex.noise_scale = DISPLACE["size"]; tex.noise_depth = DISPLACE["depth"]; tex.cloud_type = "GRAYSCALE"

    for n in range(4):
        fc = ensure_collection(f"Fractured_LOD{n}", fr)
        dc = ensure_collection(f"Deformed_LOD{n}", de)
        base, flap, head = sources(n)
        pieces = []
        if n < 3:
            per_region = {}
            for o, standin in ((base, False), (head, True), (flap, True)):
                for r, (me, mats) in cut_pieces(o, standin, frac_mat).items():
                    per_region.setdefault(r, []).append(mesh_object(f"tmp_{o.name}_{r}", me, mats, fc))
            for r in sorted(per_region):
                name = f"Fracture_{r + 1:02d}"
                p = join_into(per_region[r], name)
                p["group"] = p["ColliderMeshGroups"] = name; p["LOD"] = n
                pieces.append(p)
                if n == 0:   # collider: the piece's convex hull
                    bm = bmesh.new(); bm.from_mesh(p.data)
                    hull = bmesh.ops.convex_hull(bm, input=bm.verts)
                    bmesh.ops.delete(bm, geom=[g for g in hull["geom_interior"] + hull["geom_unused"] if isinstance(g, bmesh.types.BMVert)], context="VERTS")
                    cm = bpy.data.meshes.new(name + "_Collision"); bm.to_mesh(cm); bm.free()
                    co = bpy.data.objects.new(name + "_Collision", cm); colf.objects.link(co)
                    co.display_type = "WIRE"; co["ExportName"] = name; co["Collider"] = "Convex"
        else:        # last LOD: one mesh, the stand-in's materials as _Subpart
            parts = []
            for o, standin in ((base, False), (head, True), (flap, True)):
                bm = world_bmesh(o); me = bpy.data.meshes.new(o.name + "_w"); bm.to_mesh(me); bm.free()
                mats = [subpart_material(s.material) if standin and s.material else s.material for s in o.material_slots]
                parts.append(mesh_object("tmp_" + o.name, me, mats, fc))
            p = join_into(parts, "ControlSurface_Fractured_LOD3"); p["LOD"] = n
            pieces.append(p)
        # Deformed: copies with the guide's Displace
        for p in pieces:
            d = p.copy(); d.data = p.data.copy(); dc.objects.link(d)
            m = d.modifiers.new("Deform", "DISPLACE"); m.texture = tex; m.texture_coords = "GLOBAL"
            m.direction = "RGB_TO_XYZ"; m.space = "LOCAL"; m.strength = DISPLACE["strength"]
        tri = sum(len(f.vertices) - 2 for p in pieces for f in p.data.polygons)
        log.append(f"LOD{n}: {len(pieces)} pieces, {tri} tris: " + ", ".join(p.name for p in pieces))

    # only LOD0 of the body and flap stay visible; every new collection is excluded (tick it in the outliner to look)
    def lc(name, layer=vl.layer_collection):
        if layer.collection.name == name: return layer
        for ch in layer.children:
            r = lc(name, ch)
            if r: return r
    for name in ["Collision_Fractured"] + [f"{v}_LOD{n}" for v in ("Fractured", "Deformed") for n in range(4)]:
        lc(name).exclude = True
    bpy.ops.wm.save_mainfile()
    print("\n".join("FRACLOG " + l for l in log))


main()
