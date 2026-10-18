"""RAMJET rig (2026-10-17): the user's approved ramjet kitbash (D:/Blender Brojects/SE2/Ramjet/Ramjet_kit.blend, approved
2026-10-17) -> the SE2 export layout, one size per run, written to a NEW file (the kit is only read: run this on a COPY).
turboprop_rig.py's conventions throughout; the look is the kit's, untouched (every part keeps its world position relative to
the kit; the rig only turns and centres the whole engine).

Kit (per S = 100 / 250 / 500 / 1000, collection KIT_Atmo<S>): the stretched vanilla atmo body parts Atmo<S>_p#### (loose, custom
normals), KIT_Atmo<S>_Turbine (250, 500: static, body), KIT_Atmo<S>_Intake (SM_Atmo<S>_IntakeCowl: body; SM_Atmo<S>_Spike +
SM_Atmo<S>_SpikeProbe: the translating SPIKE subpart), KIT_Atmo<S>_Nozzle (NozzleBell, NozzleLip: body; on the 250 they carry an
object transform the user set by hand - their world transform is used), KIT_Atmo<S>_Removed (never exported). In the kit the
engines lie along world X, axis on y = z = 0, the intake (spike) at min x, the exhaust at +x.

Block frame (Blender, metres): nose = the intake = +Y, up +Z, the axis = Y through x = z = 0; origin = the block box's centre;
VRage = (x, z, -y) (+0.125 LOD0 model offset in the definitions). The thrust pushes the ship towards the intake: +Y (VRage -Z).
The box is whole cells; its front face is on the spike probe's tip (the exhaust sits up to 5 cm inside the back face).
  block        kit   cell  cells (x, z, y)  box m              body half-width (vanilla corner-panel overhang, as the turboprops')
  Ramjet_050   100   0.5   1 x 1 x 4        0.5 x 0.5 x 2.0    0.2512
  Ramjet_150   250   1.5   1 x 1 x 3        1.5 x 1.5 x 4.5    0.7508
  Ramjet_250   500   2.5   1 x 1 x 4        2.5 x 2.5 x 10.0   1.2505
  Ramjet_750   1000  2.5   3 x 3 x 8        7.5 x 7.5 x 20.0   3.755

Output collections (turboprop_rig.py's):
  Body_LOD0..3        Body (every static part merged: body parts, turbine, intake cowl, nozzle), LOD1..3 decimated 0.5 / 0.25 / 0.1
  Spike_LOD0..3       Spike = spike + probe, ONE subpart, origin ON the axis at the spike's base (its aft end)
  Dummies             Dummy_Spike (identity rotation; its local +Y = the intake direction; the spike retracts along -Y)
  Collision           <N>_Collision (hull of the body + the spike at rest, 2 cm inside the box), occupancy_<N>_0 (the box),
                      mountpoints_<N>_<face> (the four SIDES +-X, +-Z over the span where the body is flush on all four - its
                      flush polygons' y extents, snapped inward to the 0.25 m grid; never the intake or the exhaust face)
  Fractured_LOD0..3 / Deformed_LOD0..3 / Collision_Fractured    the body's halves (by x)
  Ref                 Ref_Box + Ref_CellLines (wire)
LOD1..3, Fractured, Deformed, Collision_Fractured and Ref are EXCLUDED from the view layer (not hidden).
Preview: a driver retracts the spike by its measured free travel and back over frames 1..48 (frame 1 = rest, as modelled).

  blender -b <copy of Ramjet_kit.blend> -P ramjet_rig.py -- <out.blend> <spec.json> 050|150|250|750
"""
import bpy, bmesh, json, math, sys
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

OUT, SPEC, SIZE = sys.argv[sys.argv.index("--") + 1:][:3]
KIT, S, CW, CL = {"050": ("100", 0.5, 1, 4), "150": ("250", 1.5, 1, 3), "250": ("500", 2.5, 1, 4), "750": ("1000", 2.5, 3, 8)}[SIZE]
N = f"Ramjet_{SIZE}"
W, L = CW * S, CL * S                      # box width (x, z) and length (y), metres
K = S / 2.5
DISPLACE = dict(strength=0.075, size=0.25, depth=4)
RATIOS = [1.0, 0.5, 0.25, 0.1]
FRAMES = 48
O = bpy.data.objects; C = bpy.data.collections; sc = bpy.context.scene
bpy.context.view_layer.update()
dg = bpy.context.evaluated_depsgraph_get()

# ---- 0. classify the kit's parts ----
kit = C[f"KIT_Atmo{KIT}"]
body_objs, spike_objs, removed = [], [], set()
for ch in kit.children:
    if ch.name == f"KIT_Atmo{KIT}_Removed": removed |= {o.name for o in ch.all_objects}
spike_names = {f"SM_Atmo{KIT}_Spike", f"SM_Atmo{KIT}_SpikeProbe"}
static_names = {f"SM_Atmo{KIT}_IntakeCowl", f"SM_Atmo{KIT}_NozzleBell", f"SM_Atmo{KIT}_NozzleLip"}
for o in kit.objects:
    if o.type == "MESH": body_objs.append(o)
for ch in kit.children:
    if ch.name.endswith("_Removed"): continue
    for o in ch.all_objects:
        if o.type != "MESH": continue
        if o.name in spike_names: spike_objs.append(o)
        elif ch.name.endswith(("_Turbine", "_Ports")) or o.name in static_names: body_objs.append(o)   # (_Ports: the side conveyor ports + sockets, 2026-10-17)
        else: raise SystemExit(f"RJRIG unclassified kit part {o.name} in {ch.name}")
assert {o.name for o in spike_objs} == spike_names, [o.name for o in spike_objs]
assert not ({o.name for o in body_objs} & removed)
print(f"RJRIG {N}: kit KIT_Atmo{KIT}: body parts {len(body_objs)}, spike parts {len(spike_objs)}, removed (not exported) {len(removed)}")


def kit_pts(objs):
    out = []
    for o in objs:
        ev = o.evaluated_get(dg); me = ev.to_mesh(); M = o.matrix_world
        out += [M @ v.co for v in me.vertices]; ev.to_mesh_clear()
    return out


spike_kit_pts = kit_pts(spike_objs)
tip_x = min(p.x for p in spike_kit_pts)
XC = tip_x + L / 2                          # the box centre on the kit's axis
# kit -> block: the kit's -X (intake) -> +Y; T = Rz(-90 deg) about the box centre: (x, y, z) -> (y, -(x - XC), z)
T = Matrix.Rotation(-math.pi / 2, 4, "Z") @ Matrix.Translation((-XC, 0.0, 0.0))
T_INV = T.inverted()


def world_mesh(o, M=T):
    """o's evaluated mesh in M @ world space (the block frame); materials and custom normals kept; one UV map "UVMap"."""
    me = bpy.data.meshes.new_from_object(o.evaluated_get(dg), preserve_all_data_layers=True, depsgraph=dg)
    Wm = M @ o.matrix_world
    me.transform(Wm)
    if Wm.determinant() < 0: me.flip_normals()
    while len(me.uv_layers) > 1: me.uv_layers.remove(me.uv_layers[-1])
    if not me.uv_layers: me.uv_layers.new(name="UVMap")
    me.uv_layers[0].name = "UVMap"
    return me


def stats_pts(pts):
    lo = Vector([min(p[i] for p in pts) for i in range(3)]); hi = Vector([max(p[i] for p in pts) for i in range(3)])
    return {"verts": len(pts), "lo": [round(x, 5) for x in lo], "hi": [round(x, 5) for x in hi],
            "centroid": [round(x, 5) for x in sum(pts, Vector()) / len(pts)]}


# ---- 1. the source meshes, in the block frame ----
src = {"Body": [world_mesh(o) for o in body_objs], "Spike": [world_mesh(o) for o in spike_objs]}
before_kit = {"Body": stats_pts(kit_pts(body_objs)), "Spike": stats_pts(spike_kit_pts)}

# custom normals survive the bake: a sample part's corner normals, rotated by T, against the baked mesh's
def corner_normals(me):
    return [Vector(n.vector) for n in me.corner_normals]
smp = max(body_objs, key=lambda o: len(o.data.polygons))
ev = smp.evaluated_get(dg); me0 = ev.to_mesh()
R = (T @ smp.matrix_world).to_3x3().inverted().transposed()
cn0 = [(R @ n).normalized() for n in corner_normals(me0)]; ev.to_mesh_clear()
cn1 = corner_normals(src["Body"][body_objs.index(smp)])
normal_dev = max((a - b).length for a, b in zip(cn0, cn1)) if len(cn0) == len(cn1) else float("inf")
print(f"RJRIG custom normals ({smp.name}, has_custom_normals={smp.data.has_custom_normals}): max corner-normal deviation after the bake {normal_dev:.2e}")

# the spike: pivot on the axis at its base (the aft end); its profile
spts = [v.co.copy() for me in src["Spike"] for v in me.vertices]
spike_tip_y = max(p.y for p in spts); spike_aft_y = min(p.y for p in spts)
spike_r = max(math.hypot(p.x, p.z) for p in spts)
spike_rmax_y = max((p for p in spts), key=lambda p: math.hypot(p.x, p.z)).y
spike_pivot = Vector((0.0, spike_aft_y, 0.0))
bpts = [v.co.copy() for me in src["Body"] for v in me.vertices]   # (copies: the meshes are freed by the join)
body_lo = Vector([min(p[i] for p in bpts) for i in range(3)]); body_hi = Vector([max(p[i] for p in bpts) for i in range(3)])
exhaust_y = body_lo.y
# the flush side faces: body polygons facing out along +-X / +-Z that lie on the box face (within TOL); their y extents, merged
# across seams (gaps up to 0.1 cell) - the mount faces go only where the body is flush (MODELLING_RULES: mount faces are flush)
TOL = 0.02 * S
flush_iv = {}
for f in ("+X", "-X", "+Z", "-Z"):
    i = "XYZ".index(f[1]); sgn = 1 if f[0] == "+" else -1; iv = []
    for me in src["Body"]:
        for p in me.polygons:
            if sgn * p.normal[i] < 0.95: continue
            vs = [me.vertices[k].co for k in p.vertices]
            if min(sgn * v[i] for v in vs) >= W / 2 - TOL: iv.append((min(v.y for v in vs), max(v.y for v in vs)))
    iv.sort(); merged = []
    for a, b in iv:
        if merged and a <= merged[-1][1] + 0.1 * S: merged[-1][1] = max(merged[-1][1], b)
        else: merged.append([a, b])
    flush_iv[f] = [[round(a, 4), round(b, 4)] for a, b in merged]
                                            # (the aft-most body point: the nozzle exit plane)
intake_y = spike_tip_y                                           # (the probe's tip, on the box front face)

# the spike's free travel aft (retracting into the inlet): BVH overlap pairs body x spike, against the rest pose's
def bvh(meshes, off=Vector()):
    vs, ps = [], []
    for me in meshes:
        b = len(vs); vs += [v.co + off for v in me.vertices]; ps += [[b + i for i in p.vertices] for p in me.polygons]
    return BVHTree.FromPolygons(vs, ps)
body_bvh = bvh(src["Body"])
base_pairs = len(body_bvh.overlap(bvh(src["Spike"])))
step = 0.005 * S; travel = 0.0; d = step; limit = 0.5 * (spike_tip_y - spike_aft_y)
body_polys = [p.center.copy() for me in src["Body"] for p in me.polygons]
first_hit = None
while d <= limit + 1e-9:
    hits = body_bvh.overlap(bvh(src["Spike"], Vector((0.0, -d, 0.0))))
    if len(hits) > base_pairs:
        c_ = body_polys[hits[0][0]]; first_hit = [round(c_.x, 4), round(c_.y, 4), round(c_.z, 4), round(math.hypot(c_.x, c_.z), 4)]; break
    travel = d; d += step
print(f"RJRIG spike: tip y {spike_tip_y:.4f}, aft y {spike_aft_y:.4f}, max r {spike_r:.4f} at y {spike_rmax_y:.4f}; rest-pose overlap pairs {base_pairs}; "
      f"free aft travel {travel:.4f} m (searched to {limit:.3f}, step {step:.4f}); first body contact (x, y, z, r) {first_hit}")

# ---- 2. the source out of the way (dropped at the end) ----
src_cols = list(C)
for c in src_cols: c.name = "src_" + c.name
src_objs = sorted(o.name for o in sc.objects)


def coll(name, parent=None):
    c = C.new(name); (parent or sc.collection).children.link(c); return c


def join(name, meshes, c, at=Vector()):
    objs = []
    for i, me in enumerate(meshes):
        o = O.new(f"{name}_p{i}", me); c.objects.link(o); objs.append(o)
    if len(objs) > 1:
        with bpy.context.temp_override(active_object=objs[0], selected_editable_objects=objs, selected_objects=objs):
            bpy.ops.object.join()
    o = objs[0]; o.name = name; o.data.name = name
    if at.length:
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
    if lo is not None:
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
pivots = {"Body": Vector(), "Spike": spike_pivot}
GROUPS = ("Body", "Spike")
for g in GROUPS:
    for n, r in enumerate(RATIOS):
        c = coll(f"{g}_LOD{n}")
        if n == 0:
            o = join(g, src[g], c, pivots[g])
        else:
            o = O.new(f"{g}_LOD{n}", made[(g, 0)].data.copy()); o.data.name = o.name; c.objects.link(o); o.location = pivots[g]
            decimate(o, r)
        made[(g, n)] = o
        mats.setdefault(g, set()).update(s.material.name for s in o.material_slots if s.material)

dc = coll("Dummies")
e = O.new("Dummy_Spike", None); e.location = spike_pivot; e.empty_display_type = "ARROWS"; e.empty_display_size = 0.6 * K
e["translate_axis"] = (0.0, -1.0, 0.0); e["free_travel_m"] = round(travel, 4); dc.objects.link(e)

blo, bhi = Vector((-W / 2, -L / 2, -W / 2)), Vector((W / 2, L / 2, W / 2))

tex = bpy.data.textures.get("Deform_Clouds") or bpy.data.textures.new("Deform_Clouds", "CLOUDS")
tex.noise_type = "SOFT_NOISE"; tex.noise_scale = DISPLACE["size"] * K; tex.noise_depth = DISPLACE["depth"]; tex.cloud_type = "GRAYSCALE"
fr, de = coll("Fractured"), coll("Deformed")
PIECES = ("Fracture_01", "Fracture_02")
for n in range(4):
    fc, dc2 = coll(f"Fractured_LOD{n}", fr), coll(f"Deformed_LOD{n}", de)
    srcme = made[("Body", n)].data; parts = []
    if n < 3:
        for pn, me in zip(PIECES, halves(srcme)):
            p = O.new(pn, me); fc.objects.link(p); p["group"] = p["ColliderMeshGroups"] = pn; parts.append(p)
    else:
        p = O.new(f"{N}_Fractured_LOD3", srcme.copy()); fc.objects.link(p); parts.append(p)
    for p in parts:
        p["LOD"] = n
        d2 = p.copy(); d2.data = p.data.copy(); dc2.objects.link(d2)
        m = d2.modifiers.new("Deform", "DISPLACE"); m.texture = tex; m.texture_coords = "GLOBAL"
        m.direction = "RGB_TO_XYZ"; m.space = "LOCAL"; m.strength = DISPLACE["strength"] * K

# mount faces: the four sides, over the longest flush span common to all four, snapped inward to the 0.25 m grid
def longest(iv): return max(iv, key=lambda ab: ab[1] - ab[0])
spans = {f: longest(iv) for f, iv in flush_iv.items()}
ylo = max(sp[0] for sp in spans.values()); yhi = min(sp[1] for sp in spans.values())
mlo_y, mhi_y = math.ceil(ylo / 0.25 - 1e-6) * 0.25, math.floor(yhi / 0.25 + 1e-6) * 0.25
flush = flush_iv
run = [round(mlo_y, 4), round(mhi_y, 4)]
print(f"RJRIG flush spans (y) per side, box half-width {W / 2}, tol {TOL}: {flush_iv}; common {ylo:.4f} .. {yhi:.4f} -> mount y {mlo_y} .. {mhi_y}")
assert mhi_y - mlo_y >= 0.25, "no flush side span"

cc, cf = coll("Collision"), coll("Collision_Fractured")
def add(c, name, me, export=None):
    o = O.new(name, me); c.objects.link(o); o["ExportName"] = export or name; o.display_type = "WIRE"; return o
body0 = made[("Body", 0)].data
spike0 = made[("Spike", 0)].data.copy(); spike0.transform(Matrix.Translation(spike_pivot))
add(cc, f"{N}_Collision", hull_mesh(f"{N}_Collision", [body0, spike0], blo, bhi))
bpy.data.meshes.remove(spike0)
add(cc, f"occupancy_{N}_0", box_mesh(f"occupancy_{N}_0", blo, bhi))
t = 0.05
mounts = {}
for f in ("+X", "-X", "+Z", "-Z"):
    i = "XYZ".index(f[1]); a, b = blo.copy(), bhi.copy(); a.y, b.y = mlo_y, mhi_y
    if f[0] == "+": a[i] = b[i] - t
    else: b[i] = a[i] + t
    ex = f"mountpoints_{N}_{f[0].replace('+', 'P').replace('-', 'M')}{f[1]}"
    add(cc, f"mountpoints_{N}_{f}", box_mesh(f"mountpoints_{N}_{f}", a, b), ex); mounts[f] = (a.copy(), b.copy())
for pn, me in zip(PIECES, halves(body0)):
    add(cf, pn + "_Collision", hull_mesh(pn + "_Collision", [me]), pn)

rc = coll("Ref")
o = O.new("Ref_Box", box_mesh("Ref_Box", blo, bhi, faces=False)); rc.objects.link(o)
bm = bmesh.new()
for k in range(CL + 1):
    y = -L / 2 + k * S
    for j in range(CW + 1):
        u = -W / 2 + j * S
        for a_, b_ in (((u, y, -W / 2), (u, y, W / 2)), ((-W / 2, y, u), (W / 2, y, u))):
            bm.edges.new((bm.verts.new(a_), bm.verts.new(b_)))
me = bpy.data.meshes.new("Ref_CellLines"); bm.to_mesh(me); bm.free(); o = O.new("Ref_CellLines", me); rc.objects.link(o)

# ---- 4. the preview: the spike retracts by its free travel and returns (frame 1 = rest) ----
sc.frame_start, sc.frame_end = 1, FRAMES; sc.render.fps = 24
so = made[("Spike", 0)]
fc_ = so.driver_add("location", 1); drv = fc_.driver; drv.type = "SCRIPTED"
drv.expression = f"{spike_pivot.y:.6f} - {travel:.6f} * 0.5 * (1 - cos((frame - 1) / {FRAMES} * 2 * pi))"
sc.frame_set(1)

# ---- 5. the source dropped, extra collections excluded ----
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
for nm in [f"{g}_LOD{n}" for g in GROUPS for n in (1, 2, 3)] + ["Fractured", "Deformed", "Collision_Fractured", "Ref"]:
    lc_of(nm).exclude = True

# ---- 6. checks + the spec ----
Vb = lambda v: [round(v[0], 5), round(v[2], 5), round(-v[1], 5)]   # Blender -> VRage block space
R5 = lambda v: [round(x, 5) for x in v]
after_kit, after_blk = {}, {}
for g in GROUPS:
    o = made[(g, 0)]
    pts = [o.matrix_world @ v.co for v in o.data.vertices]
    if g == "Spike": pts = [p - Vector((0.0, so.location.y - spike_pivot.y, 0.0)) for p in pts]   # (rest pose)
    after_blk[g] = stats_pts(pts); after_kit[g] = stats_pts([T_INV @ p for p in pts])
moved = {g: round(1000 * max(max(abs(before_kit[g][k][i] - after_kit[g][k][i]) for i in range(3)) for k in ("lo", "hi", "centroid")), 4) for g in GROUPS}
hull = O[f"{N}_Collision"].data
hull_ok = all(blo[i] + 0.0199 <= v.co[i] <= bhi[i] - 0.0199 for v in hull.vertices for i in range(3))
inside = {g: [round(max(0.0, -(after_blk[g]["lo"][i] - blo[i]), after_blk[g]["hi"][i] - bhi[i]), 4) for i in range(3)] for g in GROUPS}
cells_q = [round((bhi[i] - blo[i]) / 0.25) for i in range(3)]
spec = {
    "name": N, "kit": f"KIT_Atmo{KIT}", "size": SIZE, "cell_m": S, "scale_vs_250": K,
    "source": "Ramjet_kit.blend (the user's approved kitbash, 2026-10-17)",
    "frame": "Blender metres; nose (intake) +Y, up +Z; VRage = (x, z, -y) (+0.125 LOD0 model offset in the definitions)",
    "kit_to_block": {"kit_axis_x_of_box_centre": round(XC, 5), "spike_tip_kit_x": round(tip_x, 5), "rotation": "Rz(-90 deg): kit -X (intake) -> +Y"},
    "cells": {"x": CW, "z": CW, "y": CL}, "box": {"lo": R5(blo), "hi": R5(bhi)}, "cells_quarter_m": cells_q,
    "body_bbox": {"lo": R5(body_lo), "hi": R5(body_hi)}, "overhang_m_xyz": inside,
    "intake": {"blender": [0.0, round(intake_y, 5), 0.0], "vrage": Vb((0.0, intake_y, 0.0)), "note": "the spike probe's tip (the box front face)"},
    "exhaust": {"blender": [0.0, round(exhaust_y, 5), 0.0], "vrage": Vb((0.0, exhaust_y, 0.0)), "note": "the nozzle exit plane (the aft-most body point)"},
    "thrust_axis_blender": [0, 1, 0], "thrust_axis_vrage": [0, 0, -1],
    "spike": {"blender": R5(spike_pivot), "vrage": Vb(spike_pivot), "dummy": "Dummy_Spike", "pivot": "on the axis at the spike's aft end (its base)",
              "tip_y": round(spike_tip_y, 5), "max_radius": round(spike_r, 4), "max_radius_y": round(spike_rmax_y, 5),
              "retract_axis_blender": [0, -1, 0], "retract_axis_vrage": [0, 0, 1],
              "free_aft_travel_m": round(travel, 4), "first_body_contact_xyzr": first_hit, "rest_overlap_pairs": base_pairs, "forward_room_m": round(bhi.y - spike_tip_y, 4)},
    "occupancy": [[R5(blo), R5(bhi)]],
    "mount_faces": {f: {"lo": R5(a), "hi": R5(b)} for f, (a, b) in mounts.items()}, "mount_span_y": run, "flush_spans_y_per_side": flush,
    "polycount": {f"{g}_LOD{n}": len(made[(g, n)].data.polygons) for g in GROUPS for n in range(4)},
    "materials": {g: sorted(m) for g, m in mats.items()},
    "checks": {"moved_vs_kit_mm": moved, "collision_inside_box_2cm": hull_ok, "custom_normal_dev": normal_dev,
               "occupancy_whole_cells": all(abs((bhi[i] - blo[i]) / (S) - round((bhi[i] - blo[i]) / S)) < 1e-9 for i in range(3))},
    "check_kit_before": before_kit, "check_kit_after": after_kit, "check_block_after": after_blk,
}
json.dump(spec, open(SPEC, "w"), indent=1)
bpy.ops.wm.save_as_mainfile(filepath=OUT, copy=False)
print(f"RJRIG {N}: box {R5(blo)} .. {R5(bhi)} ({CW}x{CW}x{CL} cells of {S} m); spike pivot {R5(spike_pivot)} (vrage {Vb(spike_pivot)}), "
      f"free travel {travel:.4f}; exhaust y {exhaust_y:.4f}; overhang {inside}; moved vs kit (mm) {moved}; hull inside {hull_ok}; mats {spec['materials']}")
