"""MORTAR rig (2026-10-23): solid_rig.py for the mortar kit (D:/Blender Brojects/SE2/Mortar/Mortar_kit.blend - the approved solid
rocket parts, cut and dropped as rigid groups: Mortar/scripts/build_*.py) -> the SE2 export layout, one block per run, to a NEW file.
The rig only TRANSLATES the block (no rotation, no scale): every part keeps its shape, custom normals and place.

The MORTAR (the user, 2026-10-23: "like a bullet casing ... the casing should kick the original grid back, and the split grid forward.
If a split doesn't happen, then no impulse"): CHARGES (cubes, a Solid Propellant tank each) stacked on a BREECH (a half cube, the
casing's coupling half) on a ROUND (the other half: the cap, the payload's face). Firing breaks the breech's front SEAL (a fracture
piece of its own, the face toward the round) - the grid parts there; the ship is pushed back and the round's grid forward.
  block              collection           centre     cell  cells (x, y, z)  box z (kit)
  MortarCharge_250   MORTAR_Charge_250    (60, 12)   2.5   1 x 1 x 1        0 .. 2.5
  MortarBreech_250   MORTAR_Breech_250    (0, 12)    2.5   1 x 1 x 0.5      1.25 .. 2.5
  MortarRound_250    MORTAR_Round_250     (0, 12)    2.5   1 x 1 x 0.5      0 .. 1.25
  (the 150 and 050: the 250's x0.6 / x0.2, centres y + 8 / + 14; the 050 breech a whole cube: 0 .. 0.5, its segment twice)

Block frame (Blender, metres): up +Z (the stack axis: the charges above, the round below - the mortar fires DOWN, -Z, VRage Down),
"forward" +Y; origin = the box's centre; VRage = (x, z, -y) (+0.125 LOD0 model offset in the definitions). Mount faces: top and
bottom of every block (never the sides), as the solid segments'.

Output collections (solid_rig.py's): Body_LOD0..3, Collision (hull, occupancy, mountpoints), Fractured_LOD0..3 / Deformed_LOD0..3 /
Collision_Fractured - the body's halves by x, except the BREECH's: Fracture_01 the casing above the seal, Fracture_02 the SEAL (the
bottom cell and 2 cm, or the 0.5 m cube's lower half: the face toward the round), Ref.

  blender -b <copy of Mortar_kit.blend> -P mortar_rig.py -- <out.blend> <spec.json> <BlockName>
"""
import bpy, bmesh, json, math, sys
from mathutils import Matrix, Vector

OUT, SPEC, N = sys.argv[sys.argv.index("--") + 1:][:3]
BLOCKS = {  # name: (collection, (cx, cy), cell m, cells across (x = y), cells high, box z lo (kit), nozzle? - never)
    **{f"MortarCharge_{z}": (f"MORTAR_Charge_{z}", (60.0, 12.0 + dy), c, 1, 1, 0.0, False) for z, c, dy in (("250", 2.5, 0.0), ("150", 1.5, 8.0), ("050", 0.5, 14.0))},
    **{f"MortarBreech_{z}": (f"MORTAR_Breech_{z}", (0.0, 12.0 + dy), c, 1, h, c - h * c, False) for z, c, dy, h in (("250", 2.5, 0.0, 0.5), ("150", 1.5, 8.0, 0.5), ("050", 0.5, 14.0, 1))},
    **{f"MortarRound_{z}": (f"MORTAR_Round_{z}", (0.0, 12.0 + dy), c, 1, 0.5, 0.0, False) for z, c, dy in (("250", 2.5, 0.0), ("150", 1.5, 8.0), ("050", 0.5, 14.0))},
}
# The game gives each mount-face CELL to the first fracture piece a cell-sized box at it touches
# (BlockMountPointsGenerator.AssignMountPointSurfelsToFractures): the seal must hold the whole front cell and the casing none of it, so
# the seal plane is a cell and 2 cm in (in game 2026-10-23: a 0.2 m seal on the 2.5 m breech left its front mounts to the casing - no
# split; the one-cell 0.5 m breech gave both faces to one piece - it came off both ways). Each piece's hull is the body CLAMPED to its
# side of the plane (2 cm in: hull_mesh's margin), not its faces' hull - a tall face would carry the casing's hull down to the front.
# The 0.5 m breech is a whole cube (build_sizes.py): a half cube there is ONE cell, both faces' - its seal is its lower half (the plane
# at its middle: each hull 2 cm off it, the front cell the seal's, the back cell the casing's).
SEAL_CELL = 0.27
seal_of = lambda S, H: min(SEAL_CELL, H / 2)   # (m)
COLL, (CX, CY), S, CW, CH, ZLO, NOZZLE = BLOCKS[N]
W, H = CW * S, CH * S
SEAL_Z = -H / 2 + seal_of(S, H)                    # (the breech's seal plane, block z)
ZC = ZLO + H / 2                                   # the box centre's kit z
K = S / 2.5
DISPLACE = dict(strength=0.075, size=0.25, depth=4)
RATIOS = [1.0, 0.5, 0.25, 0.1]
O = bpy.data.objects; C = bpy.data.collections; sc = bpy.context.scene
bpy.context.view_layer.update()
dg = bpy.context.evaluated_depsgraph_get()

# ---- 0. the kit's parts: the collection's direct, visible meshes ----
kit = C[COLL]
parts, skipped = [], []
for o in kit.objects:
    if o.type != "MESH": skipped.append((o.name, "not a mesh")); continue
    if not o.visible_get(): skipped.append((o.name, "hidden")); continue
    if not len(o.data.vertices): skipped.append((o.name, "no vertices")); continue
    parts.append(o)
excluded_children = {ch.name: len(ch.all_objects) for ch in kit.children}
part_names = [o.name for o in parts]
print(f"SRRIG {N}: {COLL}: parts {len(parts)}, skipped {skipped}, child collections NOT taken {excluded_children}")

T = Matrix.Translation((-CX, -CY, -ZC))          # kit -> block: a pure translation
T_INV = T.inverted()


def kit_pts(objs):
    out = []
    for o in objs:
        ev = o.evaluated_get(dg); me = ev.to_mesh(); M = o.matrix_world
        out += [M @ v.co for v in me.vertices]; ev.to_mesh_clear()
    return out


def world_mesh(o, M=T):
    """o's evaluated mesh in M @ world space (the block frame); materials and custom normals kept; one UV map "UVMap"."""
    me = bpy.data.meshes.new_from_object(o.evaluated_get(dg), preserve_all_data_layers=True, depsgraph=dg)
    Wm = M @ o.matrix_world
    custom = me.has_custom_normals
    if custom:   # (the corner normals as the viewport draws them: the object's normal matrix, the inverse transpose)
        Rn = Wm.to_3x3().inverted_safe().transposed()
        want = [(Rn @ Vector(n.vector)).normalized() for n in me.corner_normals]
    me.transform(Wm)
    if Wm.determinant() < 0: me.flip_normals()
    if custom:   # (mesh.transform keeps them in their corner fan spaces: under a non-uniform object scale - the kit's vanilla
                 #  Pillars parts, (0.01, 0.0033, 0.01) - that is not what the user saw; set them to the drawn normals)
        me.normals_split_custom_set([tuple(n) for n in want])
    while len(me.uv_layers) > 1: me.uv_layers.remove(me.uv_layers[-1])
    if not me.uv_layers: me.uv_layers.new(name="UVMap")
    me.uv_layers[0].name = "UVMap"
    return me


def stats_pts(pts):
    lo = Vector([min(p[i] for p in pts) for i in range(3)]); hi = Vector([max(p[i] for p in pts) for i in range(3)])
    return {"verts": len(pts), "lo": [round(x, 5) for x in lo], "hi": [round(x, 5) for x in hi],
            "centroid": [round(x, 5) for x in sum(pts, Vector()) / len(pts)]}


# ---- 1. the source meshes, in the block frame ----
src = [world_mesh(o) for o in parts]
before_kit = stats_pts(kit_pts(parts))

# custom normals survive the bake: every custom-normal part's corner normals against the baked mesh's (a translation: unchanged)
normal_dev, normal_worst, n_corners, n_off = 0.0, None, 0, 0
for o, me1 in zip(parts, src):
    if not o.data.has_custom_normals: continue
    ev = o.evaluated_get(dg); me0 = ev.to_mesh()
    R = o.matrix_world.to_3x3().inverted_safe().transposed()
    a = [(R @ Vector(n.vector)).normalized() for n in me0.corner_normals]; ev.to_mesh_clear()
    lp = [0.0] * len(a)
    for p in me1.polygons:
        for li in p.loop_indices: lp[li] = p.area
    b = [Vector(n.vector) for n in me1.corner_normals]
    if len(a) != len(b): normal_dev = float("inf"); normal_worst = o.name; continue
    devs = [(x - y).length if ar > 1e-10 else 0.0 for x, y, ar in zip(a, b, lp)]   # (zero-area faces: no normal)
    n_corners += len(devs); n_off += sum(d > 1e-3 for d in devs)
    k = max(range(len(devs)), key=devs.__getitem__)
    if devs[k] > normal_dev: normal_dev, normal_worst = devs[k], f"{o.name} (face area {lp[k]:.2e} m2)"
print(f"SRRIG custom normals ({sum(o.data.has_custom_normals for o in parts)} parts with them): max corner-normal deviation after the bake "
      f"{normal_dev:.2e} (worst {normal_worst}); corners off by > 1e-3: {n_off} of {n_corners}")

# materials actually used by faces (the vanilla parts carry many unused slots)
used = set()
for me in src:
    idx = {p.material_index for p in me.polygons}
    used |= {me.materials[i].name for i in idx if i < len(me.materials) and me.materials[i]}
print(f"SRRIG materials used by faces: {sorted(used)}")

bpts = [v.co.copy() for me in src for v in me.vertices]
body_lo = Vector([min(p[i] for p in bpts) for i in range(3)]); body_hi = Vector([max(p[i] for p in bpts) for i in range(3)])
blo, bhi = Vector((-W / 2, -W / 2, -H / 2)), Vector((W / 2, W / 2, H / 2))
exhaust_z = body_lo.z                                      # the bell's exit plane (the lowest body point)
# the bell exit's radius: the widest point of the parts within 2 % of a cell of the exit plane
exit_r = max(math.hypot(p.x, p.y) for p in bpts if p.z <= exhaust_z + 0.02 * S)

# flush faces: body polygons on the box's top / bottom planes (within TOL), facing out; their x / y extents and area
TOL = 0.02 * S
flush = {}
for f in ("+Z", "-Z"):
    sgn = 1 if f[0] == "+" else -1; lo2 = [1e9, 1e9]; hi2 = [-1e9, -1e9]; area = 0.0
    for me in src:
        for p in me.polygons:
            if sgn * p.normal.z < 0.95: continue
            vs = [me.vertices[k].co for k in p.vertices]
            if min(sgn * v.z for v in vs) < H / 2 - TOL: continue
            area += p.area
            for i in range(2):
                lo2[i] = min(lo2[i], min(v[i] for v in vs)); hi2[i] = max(hi2[i], max(v[i] for v in vs))
    flush[f] = {"lo": [round(x, 4) for x in lo2], "hi": [round(x, 4) for x in hi2], "area_m2": round(area, 4)} if area else None
print(f"SRRIG flush faces (box half-height {H / 2}, tol {TOL}): {flush}")

# ---- 2. the source out of the way (dropped at the end) ----
for c in list(C): c.name = "src_" + c.name
src_objs = sorted(o.name for o in sc.objects)


def coll(name, parent=None):
    c = C.new(name); (parent or sc.collection).children.link(c); return c


def join(name, meshes, c):
    objs = []
    for i, me in enumerate(meshes):
        o = O.new(f"{name}_p{i}", me); c.objects.link(o); objs.append(o)
    if len(objs) > 1:
        with bpy.context.temp_override(active_object=objs[0], selected_editable_objects=objs, selected_objects=objs):
            bpy.ops.object.join()
    o = objs[0]; o.name = name; o.data.name = name
    # only the materials faces use (the vanilla parts carry many unused slots): remap the face indices, rebuild the slots
    me = o.data; mi = [0] * len(me.polygons); me.polygons.foreach_get("material_index", mi)
    keep = sorted(set(mi)); remap = {k: j for j, k in enumerate(keep)}; mats_ = [me.materials[k] for k in keep]
    me.materials.clear()
    for m_ in mats_: me.materials.append(m_)
    me.polygons.foreach_set("material_index", [remap[k] for k in mi]); me.update()
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
    if lo is not None:
        for v in bm.verts: v.co = Vector([max(lo[i] + m, min(hi[i] - m, v.co[i])) for i in range(3)])
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free(); return me


def halves(me):
    """The fracture pieces: the body's halves by x - the BREECH's: the casing (Fracture_01) and its SEAL (Fracture_02: the faces below
    the seal plane - the face toward the round)."""
    out = []
    seal = N.startswith("MortarBreech")
    for first in (True, False):
        bm = bmesh.new(); bm.from_mesh(me)
        side = (lambda f: f.calc_center_median().z > SEAL_Z) if seal else (lambda f: f.calc_center_median().x < 0)
        bmesh.ops.delete(bm, geom=[f for f in bm.faces if side(f) != first], context="FACES")
        h = me.copy(); bm.to_mesh(h); bm.free(); out.append(h)
    return out


# ---- 3. the export layout ----
made = {}
for n, r in enumerate(RATIOS):
    c = coll(f"Body_LOD{n}")
    if n == 0:
        o = join("Body", src, c)
    else:
        o = O.new(f"Body_LOD{n}", made[0].data.copy()); o.data.name = o.name; c.objects.link(o)
        decimate(o, r)
    made[n] = o
mats = sorted(s.material.name for s in made[0].material_slots if s.material)

dc = coll("Dummies")
if NOZZLE:
    e = O.new("Dummy_Exhaust", None); e.location = (0.0, 0.0, exhaust_z); e.empty_display_type = "SINGLE_ARROW"
    e.rotation_euler = (math.pi, 0.0, 0.0)          # (its arrow, local +Z, points down: the exhaust)
    e.empty_display_size = 0.6 * K; e["exhaust_dir_blender"] = (0.0, 0.0, -1.0); dc.objects.link(e)

tex = bpy.data.textures.get("Deform_Clouds") or bpy.data.textures.new("Deform_Clouds", "CLOUDS")
tex.noise_type = "SOFT_NOISE"; tex.noise_scale = DISPLACE["size"] * K; tex.noise_depth = DISPLACE["depth"]; tex.cloud_type = "GRAYSCALE"
fr, de = coll("Fractured"), coll("Deformed")
PIECES = ("Fracture_01", "Fracture_02")
for n in range(4):
    fc, dc2 = coll(f"Fractured_LOD{n}", fr), coll(f"Deformed_LOD{n}", de)
    srcme = made[n].data; pp = []
    if n < 3:
        for pn, me in zip(PIECES, halves(srcme)):
            p = O.new(pn, me); fc.objects.link(p); p["group"] = p["ColliderMeshGroups"] = pn; pp.append(p)
    else:
        p = O.new(f"{N}_Fractured_LOD3", srcme.copy()); fc.objects.link(p); pp.append(p)
    for p in pp:
        p["LOD"] = n
        d2 = p.copy(); d2.data = p.data.copy(); dc2.objects.link(d2)
        m = d2.modifiers.new("Deform", "DISPLACE"); m.texture = tex; m.texture_coords = "GLOBAL"
        m.direction = "RGB_TO_XYZ"; m.space = "LOCAL"; m.strength = DISPLACE["strength"] * K

# mount faces: top (both kinds) + bottom (segments): the flush region's x / y extents, snapped inward to 0.05 cell
STEP = 0.05 * S
EDGE = min(0.06, 0.1 * S)
mount_faces = ("+Z",) if NOZZLE else ("+Z", "-Z")
mrect = {}
for f in mount_faces:
    fl = flush[f]; assert fl, f"no flush polygons on the {f} face"
    # (a flush region within EDGE of the box edge reaches it: the vanilla tank base plates' edge chamfers, 0.5 - 4.1 cm - the
    #  MODELLING_RULES inset band of 2 - 6 cm on mount faces; otherwise snapped inward to the step)
    lo2 = [-W / 2 if fl["lo"][i] <= -W / 2 + EDGE else math.ceil(fl["lo"][i] / STEP - 1e-6) * STEP for i in range(2)]
    hi2 = [W / 2 if fl["hi"][i] >= W / 2 - EDGE else math.floor(fl["hi"][i] / STEP + 1e-6) * STEP for i in range(2)]
    mrect[f] = (lo2, hi2)
    assert all(hi2[i] - lo2[i] >= 0.5 * W for i in range(2)), f"flush region on {f} under half the face: {fl}"

cc, cf = coll("Collision"), coll("Collision_Fractured")
def add(c, name, me, export=None):
    o = O.new(name, me); c.objects.link(o); o["ExportName"] = export or name; o.display_type = "WIRE"; return o
body0 = made[0].data
add(cc, f"{N}_Collision", hull_mesh(f"{N}_Collision", [body0], blo, bhi))
add(cc, f"occupancy_{N}_0", box_mesh(f"occupancy_{N}_0", blo, bhi))
t = 0.05
mounts = {}
for f in mount_faces:
    (l2, h2) = mrect[f]
    a = Vector((l2[0], l2[1], 0.0)); b = Vector((h2[0], h2[1], 0.0))
    if f[0] == "+": a.z, b.z = bhi.z - t, bhi.z
    else: a.z, b.z = blo.z, blo.z + t
    ex = f"mountpoints_{N}_{f[0].replace('+', 'P').replace('-', 'M')}{f[1]}"
    add(cc, f"mountpoints_{N}_{f}", box_mesh(f"mountpoints_{N}_{f}", a, b), ex); mounts[f] = (a.copy(), b.copy())
if N.startswith("MortarBreech"):   # (the casing above the seal plane, the seal below it: the body's hull clamped to each side)
    for pn, lo_, hi_ in (("Fracture_01", Vector((blo.x, blo.y, SEAL_Z)), bhi), ("Fracture_02", blo, Vector((bhi.x, bhi.y, SEAL_Z)))):
        add(cf, pn + "_Collision", hull_mesh(pn + "_Collision", [body0], lo_, hi_), pn)
else:
    for pn, me in zip(PIECES, halves(body0)):
        add(cf, pn + "_Collision", hull_mesh(pn + "_Collision", [me], blo, bhi), pn)

rc = coll("Ref")
o = O.new("Ref_Box", box_mesh("Ref_Box", blo, bhi, faces=False)); rc.objects.link(o)
bm = bmesh.new()
for k in range(math.ceil(CH) + 1):
    z = -H / 2 + k * S
    for j in range(CW + 1):
        u = -W / 2 + j * S
        for a_, b_ in (((u, -W / 2, z), (u, W / 2, z)), ((-W / 2, u, z), (W / 2, u, z))):
            bm.edges.new((bm.verts.new(a_), bm.verts.new(b_)))
me = bpy.data.meshes.new("Ref_CellLines"); bm.to_mesh(me); bm.free(); o = O.new("Ref_CellLines", me); rc.objects.link(o)

# ---- 4. the source dropped, extra collections excluded ----
for nm in src_objs:
    if nm in O: O.remove(O[nm])
def drop(c):
    for ch in list(c.children): drop(ch)
    C.remove(c)
for c in [c for c in sc.collection.children if c.name.startswith("src_")]: drop(c)
for c in [c for c in C if c.name.startswith("src_")]: C.remove(c)
for cam in [c for c in bpy.data.cameras if c.users == 0]: bpy.data.cameras.remove(cam)
bpy.data.orphans_purge(do_local_ids=True, do_linked_ids=True, do_recursive=True)

def lc_of(nm, layer=None):
    layer = layer or bpy.context.view_layer.layer_collection
    if layer.collection.name == nm: return layer
    for ch in layer.children:
        r = lc_of(nm, ch)
        if r: return r
for nm in [f"Body_LOD{n}" for n in (1, 2, 3)] + ["Fractured", "Deformed", "Collision_Fractured", "Ref"]:
    lc_of(nm).exclude = True

# ---- 5. checks + the spec ----
Vb = lambda v: [round(v[0], 5), round(v[2], 5), round(-v[1], 5)]   # Blender -> VRage block space
R5 = lambda v: [round(x, 5) for x in v]
pts = [made[0].matrix_world @ v.co for v in made[0].data.vertices]
after_blk = stats_pts(pts); after_kit = stats_pts([T_INV @ p for p in pts])
moved = round(1000 * max(max(abs(before_kit[k][i] - after_kit[k][i]) for i in range(3)) for k in ("lo", "hi", "centroid")), 4)
hull = O[f"{N}_Collision"].data
hull_ok = all(blo[i] + 0.0199 <= v.co[i] <= bhi[i] - 0.0199 for v in hull.vertices for i in range(3))
overhang = [round(max(0.0, blo[i] - after_blk["lo"][i], after_blk["hi"][i] - bhi[i]), 4) for i in range(3)]
lods = {f"Body_LOD{n}": len(made[n].data.polygons) for n in range(4)}
spec = {
    "name": N, "kind": "mortar_" + N[6:N.index("_")].lower(), "collection": COLL, "size": N[-3:], "cell_m": S,
    "scale_vs_250": K, "source": "Solid_kit.blend (the user's approved kitbash, 2026-10-17)",
    "frame": "Blender metres; up +Z (the stack axis), forward +Y; VRage = (x, z, -y) (+0.125 LOD0 model offset in the definitions)",
    "kit_to_block": {"translation": R5((-CX, -CY, -ZC)), "rotation": None, "kit_box_z": [ZLO, ZLO + H]},
    "cells": {"x": CW, "y": CW, "z": CH}, "box": {"lo": R5(blo), "hi": R5(bhi)},
    "body_bbox": {"lo": R5(body_lo), "hi": R5(body_hi)}, "overhang_m_xyz": overhang,
    "occupancy": [[R5(blo), R5(bhi)]],
    "mount_faces": {f: {"lo": R5(a), "hi": R5(b)} for f, (a, b) in mounts.items()}, "flush_faces": flush,
    "polycount": lods, "materials": {"Body": mats},
    "checks": {"moved_vs_kit_mm": moved, "collision_inside_box_2cm": hull_ok, "custom_normal_dev": normal_dev, "custom_normal_worst_part": normal_worst, "custom_normal_corners_off_1e-3": [n_off, n_corners],
               "occupancy_whole_cells": all(abs((bhi[i] - blo[i]) / S - round((bhi[i] - blo[i]) / S)) < 1e-9 for i in range(3)),
               "parts": part_names, "skipped": skipped, "child_collections_not_taken": excluded_children},
    "check_kit_before": before_kit, "check_kit_after": after_kit, "check_block_after": after_blk,
}
if NOZZLE:
    spec["exhaust"] = {"blender": [0.0, 0.0, round(exhaust_z, 5)], "vrage": Vb((0.0, 0.0, exhaust_z)), "exit_radius": round(exit_r, 4),
                       "dummy": "Dummy_Exhaust", "note": "the bell's exit plane (the lowest body point), on the axis; exhaust leaves -Z (VRage -Y)",
                       "below_box_bottom_m": round(exhaust_z - blo.z, 4)}
    spec["thrust_axis_blender"] = [0, 0, 1]; spec["thrust_axis_vrage"] = [0, 1, 0]; spec["thrust_direction"] = "Up"
json.dump(spec, open(SPEC, "w"), indent=1)
bpy.ops.wm.save_as_mainfile(filepath=OUT, copy=False)
print(f"SRRIG {N}: box {R5(blo)} .. {R5(bhi)} ({CW}x{CW}x{CH} cells of {S} m); body {R5(body_lo)} .. {R5(body_hi)}; overhang {overhang}; "
      f"exhaust z {exhaust_z if NOZZLE else None} r {round(exit_r, 3) if NOZZLE else None}; mounts {[(f, R5(a), R5(b)) for f, (a, b) in mounts.items()]}; "
      f"moved vs kit (mm) {moved}; hull inside {hull_ok}; LODs {lods}; mats {mats}")
