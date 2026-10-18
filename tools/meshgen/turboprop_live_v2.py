"""TURBOPROP 0.5 - LIVE v2 (2026-10-16): the traced replica (turboprop_replica_live.py) fitted to the block and refined.
The user: "more than just matching the reference: fix it and beautify". Same live, non-destructive layout (COL_ collections,
SM_ names, live boolean operands, micro bevel + weighted normals); metres, nose +Y, d = distance aft of the front face.

Fit (the 0.5 block, nacelle 3x1x1 = 1.5 x 0.5 x 0.5; prop disc ahead):
  * the top is flush (z +0.25) from the cowl to the tail - one long spine; the rear block is flush all round (the mount);
  * the keel and the intake now sit inside the box: the keel floor is flush with the bottom face (z -0.25);
  * sections on the 0.125 grid, features on 0.0625: cowl 0..0.4375 | groove | mid front 0.4625..0.875 | panel break |
    mid rear (the waist) 0.885..1.25 | rear block 1.25..1.5; the belly sweeps up 0.625..1.0 to z -0.18.
Refinements (hard-surface: panel breaks on structural seams, detail grouped, calm surfaces elsewhere):
  * a panel break at the waist (the mid body is two pieces over an inset core);
  * the intake mouth framed (a shallow frame cut, then the deep opening);
  * a top access hatch on the mid body (a groove ring), a recessed panel in the rear mount face (~25 % of it);
  * blade root cuffs (one mesh, instanced three times).

  run inside Blender: exec(open(<this file>).read())
"""
import bpy, bmesh, math, os, sys, importlib
from mathutils import Vector, Matrix

MESHGEN = r"C:\Users\slob\Documents\SpaceEngineers2\Mods\Aerodynamics Mod\tools\meshgen"
REF = r"D:\Blender Brojects\SE2\Turboprop\ref"
OUT = r"D:\Blender Brojects\SE2\Turboprop\Turboprop_050_live_v2.blend"
sys.path.insert(0, MESHGEN)
import rotorcraft_blockouts as rb
importlib.reload(rb)

sc = bpy.context.scene; rb.sc = sc
for o in list(bpy.data.objects): bpy.data.objects.remove(o)
for c in list(bpy.data.collections): bpy.data.collections.remove(c)
bpy.data.orphans_purge(do_recursive=True)
rb.BODY = rb.mat("PaintedMetalColorable", (0.62, 0.64, 0.68)); rb.DARK = rb.mat("PaintedMetalDarker", (0.24, 0.25, 0.28))
rb.METAL = rb.mat("BrushedMetal", (0.55, 0.56, 0.58)); rb.BLADE = rb.DARK; rb.STRIPE = rb.mat("WarningStripes", (0.92, 0.72, 0.08))
FRAME = rb.mat("MetalDull", (0.38, 0.40, 0.43))


def coll(name, parent):
    c = bpy.data.collections.new(name); parent.children.link(c); return c


TOP = coll("COL_Turboprop_050", sc.collection)
C_REF, C_BLOCK, C_BOOL, C_GEO, C_INST = (coll(n, TOP) for n in ("COL_Reference", "COL_Blockout", "COL_Booleans", "COL_Geo", "COL_Instances"))
C_CUTS = coll("COL_Cuts", C_BOOL)

H = 0.25                                         # (the box half-size)
V = lambda x, d, z: Vector((x, 0.75 - d, z))     # (d aft of the front face -> block coordinates)
octo = lambda h, t, b, c: [(h - c, t), (h, t - c), (h, -(b - c)), (h - c, -b)]


def loft(name, rings, col, mat=None):
    bm = bmesh.new(); R = [[bm.verts.new(V(x, d, z)) for x, z in h + [(-x, z) for x, z in reversed(h)]] for d, h in rings]; n = len(R[0])
    for A, B in zip(R, R[1:]):
        for k in range(n): bm.faces.new((A[k], A[(k + 1) % n], B[(k + 1) % n], B[k]))
    bm.faces.new(R[0][::-1]); bm.faces.new(R[-1])
    return rb.obj(name, bm, [mat or rb.BODY], col, bevel=None)


def side_prism(name, pts, x0, x1, col, mat=None):
    """A side-view outline [(d, z)] extruded across x0..x1."""
    bm = bmesh.new(); a = [bm.verts.new(V(x0, d, z)) for d, z in pts]; b = [bm.verts.new(V(x1, d, z)) for d, z in pts]; n = len(pts)
    bm.faces.new(a[::-1]); bm.faces.new(b)
    for i in range(n): bm.faces.new((a[i], a[(i + 1) % n], b[(i + 1) % n], b[i]))
    return rb.obj(name, bm, [mat or rb.BODY], col, bevel=None)


def plan_prism(name, pts, z0, z1, col, mat=None):
    """A top-view outline [(x, d)] extruded across z0..z1."""
    bm = bmesh.new(); a = [bm.verts.new(V(x, d, z0)) for x, d in pts]; b = [bm.verts.new(V(x, d, z1)) for x, d in pts]; n = len(pts)
    bm.faces.new(a[::-1]); bm.faces.new(b)
    for i in range(n): bm.faces.new((a[i], a[(i + 1) % n], b[(i + 1) % n], b[i]))
    return rb.obj(name, bm, [mat or rb.BODY], col, bevel=None)


def operand(o, wire=True):
    o.display_type = "WIRE" if wire else "TEXTURED"; o.hide_render = True; o.hide_set(True); return o


def rrect(a0, a1, b0, b1, c):
    """A rectangle with chamfered corners, as a polygon in (a, b)."""
    return [(a0 + c, b1), (a1 - c, b1), (a1, b1 - c), (a1, b0 + c), (a1 - c, b0), (a0 + c, b0), (a0, b0 + c), (a0, b1 - c)]


# ── primary masses ──
# the cowl: the concept's big-cornered octagon, tapering to the spinner face; its lower half carries the keel (flush floor)
cowl = lambda h, t, c, lo, kb: [(h - c, t), (h, t - c), (h, lo), (0.14 * kb, -H)]
body = loft("SM_Body", [(0.0, [(0.10, 0.15), (0.155, 0.095), (0.155, -0.095), (0.10, -0.15)]),
                        (0.125, cowl(H, H, 0.105, -0.16, 1.0)), (0.4375, cowl(H, H, 0.105, -0.16, 1.0))], C_GEO)
mid = lambda w, cv, lo, kz: [(0.19, H), (w, H - cv), (w, lo), (0.14, kz)]
core = lambda h: [(h - 0.115, h), (h, h - 0.115), (h, -0.17), (0.13, -h)]
for o in (loft("SM_KeelNose", [(0.02, [(H, -0.10), (H, -0.16), (0.14, -H)]), (0.44, [(H, -0.10), (H, -0.16), (0.14, -H)])], C_BLOCK),
          loft("SM_GrooveCore", [(0.43, core(0.24)), (0.47, core(0.24))], C_BLOCK),
          loft("SM_MidFront", [(0.4625, mid(H, 0.06, -0.16, -H)), (0.875, mid(H, 0.06, -0.16, -H))], C_BLOCK),
          loft("SM_WaistCore", [(0.87, mid(0.244, 0.062, -0.165, -0.244)), (0.89, mid(0.244, 0.062, -0.165, -0.244))], C_BLOCK),
          loft("SM_MidRear", [(0.885, mid(H, 0.06, -0.16, -H)), (1.0, mid(0.22, 0.12, -0.12, -0.20)), (1.30, mid(0.22, 0.12, -0.12, -0.20))], C_BLOCK),
          loft("SM_RearBlock", [(1.25, octo(0.23, 0.23, 0.23, 0.045)), (1.27, octo(H, H, H, 0.055)), (1.48, octo(H, H, H, 0.055)), (1.5, octo(0.23, 0.23, 0.23, 0.045))], C_BLOCK)):
    operand(o, wire=False)

# ── cutters ──
# the side profile: the intake's angled face under the cowl, the flush keel floor, the belly sweeping up to the waist
prof = operand(side_prism("SM_Cutter_Profile", [(-0.01, 0.3), (1.51, 0.3), (1.51, -0.3), (1.255, -0.3), (1.255, -0.18), (1.0, -0.18),
                                                (0.625, -H), (0.625, -0.3), (0.115, -0.3), (0.035, -0.15), (-0.01, -0.15)], -0.3, 0.3, C_BOOL))
k = (0.10 - 0.035) / (0.25 - 0.15)   # (the intake face: dd per metre of drop)
face_d = lambda z: 0.035 + (-0.15 - z) * k
operand(loft("SM_Cutter_SpinnerRecess", [(-0.02, octo(0.135, 0.13, 0.13, 0.045)), (0.015, octo(0.135, 0.13, 0.13, 0.045))], C_CUTS, rb.DARK))
operand(side_prism("SM_Cutter_IntakeFrame", [(face_d(-0.165) - 0.03, -0.165), (face_d(-0.235) - 0.03, -0.235), (face_d(-0.235) + 0.008, -0.235), (face_d(-0.165) + 0.008, -0.165)], -0.125, 0.125, C_CUTS, FRAME))
operand(side_prism("SM_Cutter_IntakeOpening", [(face_d(-0.178) - 0.03, -0.178), (face_d(-0.222) - 0.03, -0.222), (face_d(-0.222) + 0.07, -0.222), (face_d(-0.178) + 0.07, -0.178)], -0.10, 0.10, C_CUTS, rb.DARK))
for side, sx in (("L", -1), ("R", 1)):
    operand(side_prism(f"SM_Cutter_WindowFrame_{side}", rrect(0.50, 0.8125, -0.10, 0.10, 0.03), *((0.238, 0.4) if sx > 0 else (-0.4, -0.238)), C_CUTS, FRAME))
    operand(side_prism(f"SM_Cutter_WindowOpening_{side}", rrect(0.525, 0.7875, -0.075, 0.075, 0.02), *((0.16, 0.4) if sx > 0 else (-0.4, -0.16)), C_CUTS, rb.DARK))
# the top hatch: a groove ring 8 mm wide, 5 mm deep (outer minus inner, as one cutter)
hatch = plan_prism("SM_Cutter_Hatch", rrect(-0.12, 0.12, 0.53, 0.78, 0.03), H - 0.005, H + 0.05, C_CUTS)
inner = plan_prism("_hatch_in", rrect(-0.112, 0.112, 0.538, 0.772, 0.026), H - 0.01, H + 0.06, C_CUTS)
md = hatch.modifiers.new("ring", "BOOLEAN"); md.object = inner; md.operation = "DIFFERENCE"; md.solver = "EXACT"
with bpy.context.temp_override(object=hatch, active_object=hatch, selected_objects=[hatch]): bpy.ops.object.modifier_apply(modifier=md.name)
bpy.data.objects.remove(inner); operand(hatch)
operand(loft("SM_Cutter_RearPanel", [(1.494, octo(0.125, 0.125, 0.125, 0.03)), (1.53, octo(0.125, 0.125, 0.125, 0.03))], C_CUTS, FRAME))

# ── SM_Body's stack ──
def boolean(name, op, target, transfer=False):
    md = body.modifiers.new(name, "BOOLEAN"); md.operation = op; md.solver = "EXACT"
    if isinstance(target, bpy.types.Collection): md.operand_type = "COLLECTION"; md.collection = target
    else: md.object = target
    if transfer: md.material_mode = "TRANSFER"
boolean("Union_Masses", "UNION", C_BLOCK)
boolean("Profile", "INTERSECT", prof)
boolean("Cuts", "DIFFERENCE", C_CUTS, transfer=True)
bev = body.modifiers.new("Bevel_Micro", "BEVEL"); bev.width = 0.004; bev.segments = 1; bev.limit_method = "ANGLE"
bev.angle_limit = math.radians(30); bev.harden_normals = True
wn = body.modifiers.new("WeightedNormal", "WEIGHTED_NORMAL"); wn.keep_sharp = True; wn.weight = 50
for m in (rb.DARK, FRAME): body.data.materials.append(m)

# ── secondary parts ──
bm = bmesh.new()   # (the compressor seen in each window: a shaft and four discs growing aft)
for d0, d1, r in [(0.53, 0.785, 0.015)] + [(dc - 0.006, dc + 0.006, r) for dc, r in ((0.58, 0.035), (0.63, 0.045), (0.68, 0.055), (0.73, 0.063))]:
    rb.prism(bm, 16, r, 0.75 - d1, 0.75 - d0)
comp = rb.obj("SM_Compressor_R", bm, [rb.METAL], C_GEO, bevel=None); comp.location = (0.16, 0, 0)
twin = bpy.data.objects.new("SM_Compressor_L", comp.data); C_GEO.objects.link(twin); twin.location = (-0.16, 0, 0)
bm = bmesh.new(); rb.lathe(bm, [(0.74, 0.0), (0.74, 0.12), (0.765, 0.12), (0.765, 0.06), (0.78, 0.06)] + rb.ogive(0.78, 0.16, 0.08, tip=0.15), seg=48)
rb.obj("SM_Spinner", bm, [rb.DARK], C_GEO, bevel=None)   # (a back plate inside the recess, a collar, the ogive)
bm = bmesh.new()
for b in range(3): rb.blade(bm, 0.05, 0.415, 0.10, 55, 22, 2 * math.pi * b / 3, "prop", sweep=0.015)
for v in bm.verts: v.co.y += 0.80
rb.obj("SM_Blades", bm, [rb.BLADE, rb.STRIPE], C_GEO, bevel=None, sharp=80.0)
bm = bmesh.new(); rb.lathe(bm, [(0.04, 0.0), (0.04, 0.026), (0.11, 0.024), (0.125, 0.018), (0.125, 0.0)], seg=12, axis="Z")
cuff = rb.obj("SM_BladeCuff", bm, [rb.METAL], C_INST, bevel=None)   # (a root sleeve along +Z, instanced per blade)
for b in range(3):
    o = bpy.data.objects.new(f"SM_BladeCuff_{b}", cuff.data); C_GEO.objects.link(o)
    o.matrix_world = Matrix.Translation((0, 0.80, 0)) @ Matrix.Rotation(2 * math.pi * b / 3, 4, "Y")
C_INST.hide_viewport = True; cuff.hide_render = True

# ── reference: the concept's views, scaled with the traced model (0.5 / 139 m per px), for comparison ──
S = 0.5 / 139.0; SF = S / 1.04
def ref(name, file, size, loc, xaxis, yaxis):
    e = bpy.data.objects.new(name, None); e.empty_display_type = "IMAGE"; e.data = bpy.data.images.load(os.path.join(REF, file), check_existing=True)
    e.empty_display_size = size; e.empty_image_depth = "BACK"; e.show_empty_image_perspective = False; e.color[3] = 0.5; e.use_empty_image_alpha = True
    X, Y = Vector(xaxis), Vector(yaxis); e.matrix_world = Matrix.Translation(Vector(loc)) @ Matrix((X, Y, X.cross(Y))).transposed().to_4x4()
    C_REF.objects.link(e); return e
yref = lambda p: 0.75 - p * S   # (the concept registered at its front face)
ref("REF_Side", "concept32_side.png", 440 * S, (-0.6, yref(155), (119.5 - 135) * S), (0, -1, 0), (0, 0, 1))
ref("REF_Top", "concept32_top.png", 480 * S, ((126.5 - 135) * S, yref(155), -0.5), (0, -1, 0), (1, 0, 0))
ref("REF_Front", "concept32_front.png", 280 * SF, (-(260 - 257) * SF, -1.0, (62.5 - (390 - 336) / 1.04) * S), (-1, 0, 0), (0, 0, 1))
rb.occupancy("SM_Occupancy", [((-0.5, -1.5, -0.5), (0.5, 1.5, 0.5)), ((-2.5, 1.5, -2.5), (2.5, 2.5, 2.5))], C_REF, 0.5)
bpy.data.objects["SM_Occupancy"].hide_set(True)

rb.finish(OUT)
ev = body.evaluated_get(bpy.context.evaluated_depsgraph_get()).data
lo = [min(v.co[i] for v in ev.vertices) for i in range(3)]; hi = [max(v.co[i] for v in ev.vertices) for i in range(3)]
print("LIVE v2:", len(ev.polygons), "faces; body box", [round(a, 4) for a in lo], [round(b, 4) for b in hi], "saved", OUT)
