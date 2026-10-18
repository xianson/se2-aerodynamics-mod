"""TURBOPROP REPLICA, LIVE AND NON-DESTRUCTIVE (2026-10-16): turboprop_replica.py's traced pieces, rebuilt in the running
Blender (via the MCP add-on's socket) the way the hard-surface / blender-modeler skills lay a model out:

  COL_Turboprop
    COL_Reference   the concept's side / top / front views as image empties, at the traced scale (ortho views only)
    COL_Blockout    the primary masses (keel, groove core, mid body, rear block) - live UNION operands of SM_Body
    COL_Booleans    SM_Cutter_Profile (INTERSECT: intake face, keel floor, belly), SM_Cutter_Tuck
      COL_Cuts      SM_Cutter_* pockets (spinner recess, intake mouth, window frames and openings) - their faces
                    keep the cutters' materials (TRANSFER)
    COL_Geo         SM_Body (the cowl, with the stack: Union -> Profile -> Tuck -> Cuts -> Bevel -> Weighted Normal),
                    SM_Compressor_L/R (one mesh, instanced), SM_Spinner, SM_Blades
Nothing is applied: move a mass or a cutter and the body follows. The bevel is Keen's micro chamfer (45 deg, one
segment, ~1 % of the part: 5 mm) on edges over 30 deg, hardened normals, then Weighted Normal (keep sharp).

  run inside Blender: exec(open(<this file>).read())   (OUT below)
"""
import bpy, bmesh, math, os, sys, importlib
from mathutils import Vector, Matrix

MESHGEN = r"C:\Users\slob\Documents\SpaceEngineers2\Mods\Aerodynamics Mod\tools\meshgen"
REF = r"D:\Blender Brojects\SE2\Turboprop\ref"
OUT = r"D:\Blender Brojects\SE2\Turboprop\Turboprop_replica_live.blend"
sys.path.insert(0, MESHGEN)
import rotorcraft_blockouts as rb
importlib.reload(rb)

# ── a clean scene (this session's own scratch file is open; it is saved under a new name at the end) ──
sc = bpy.context.scene; rb.sc = sc
for o in list(bpy.data.objects): bpy.data.objects.remove(o)
for c in list(bpy.data.collections): bpy.data.collections.remove(c)
bpy.data.orphans_purge(do_recursive=True)
rb.BODY = rb.mat("PaintedMetalColorable", (0.62, 0.64, 0.68)); rb.DARK = rb.mat("PaintedMetalDarker", (0.24, 0.25, 0.28))
rb.METAL = rb.mat("BrushedMetal", (0.55, 0.56, 0.58)); rb.BLADE = rb.DARK; rb.STRIPE = rb.mat("WarningStripes", (0.92, 0.72, 0.08))
FRAME = rb.mat("MetalDull", (0.38, 0.40, 0.43))


def coll(name, parent):
    c = bpy.data.collections.new(name); parent.children.link(c); return c


TOP = coll("COL_Turboprop", sc.collection)
C_REF, C_BLOCK, C_BOOL, C_GEO = (coll(n, TOP) for n in ("COL_Reference", "COL_Blockout", "COL_Booleans", "COL_Geo"))
C_CUTS = coll("COL_Cuts", C_BOOL)

S = 0.5 / 139.0                                               # (m per drawing px: the body width 139 px = 0.5 m)
V = lambda x, p, z: Vector((x * S, (180.0 - p) * S, z * S))   # (drawing px -> metres; nose +Y, centred)
octo = lambda h, t, b, c: [(h - c, t), (h, t - c), (h, -(b - c)), (h - c, -b)]


def loft(name, rings, col, mat=None):
    bm = bmesh.new(); R = [[bm.verts.new(V(x, p, z)) for x, z in h + [(-x, z) for x, z in reversed(h)]] for p, h in rings]; n = len(R[0])
    for A, B in zip(R, R[1:]):
        for k in range(n): bm.faces.new((A[k], A[(k + 1) % n], B[(k + 1) % n], B[k]))
    bm.faces.new(R[0][::-1]); bm.faces.new(R[-1])
    return rb.obj(name, bm, [mat or rb.BODY], col, bevel=None)


def side_prism(name, pts, x0, x1, col, mat=None):
    bm = bmesh.new(); a = [bm.verts.new(V(x0, p, z)) for p, z in pts]; b = [bm.verts.new(V(x1, p, z)) for p, z in pts]; n = len(pts)
    bm.faces.new(a[::-1]); bm.faces.new(b)
    for i in range(n): bm.faces.new((a[i], a[(i + 1) % n], b[(i + 1) % n], b[i]))
    return rb.obj(name, bm, [mat or rb.BODY], col, bevel=None)


def operand(o, wire=True):
    o.display_type = "WIRE" if wire else "TEXTURED"; o.hide_render = True; o.hide_set(True); return o


# ── primary masses ──
body = loft("SM_Body", [(0, octo(43, 41.5, 41.5, 15)), (45, octo(69.5, 62.5, 62.5, 29)), (122, octo(69.5, 62.5, 62.5, 29))], C_GEO)   # (the cowl)
keel_a = [(69.5, -30), (69.5, -62.5), (39, -92.5)]
mid = lambda w, cv, lo=-62.5: [(53.5, 62.5), (w, 62.5 - cv), (w, lo), (39, -92.5)]
core = [(37.5, 59.5), (66.5, 30.5), (66.5, -61.5), (37.5, -89.5)]
for o in (loft("SM_Keel", [(10, keel_a), (122, keel_a)], C_BLOCK),
          loft("SM_GrooveCore", [(119, core), (130, core)], C_BLOCK),
          loft("SM_MidBody", [(127, mid(69.5, 16)), (200, mid(69.5, 16)), (237, mid(60.5, 33, -50)), (290, mid(60.5, 33, -50))], C_BLOCK),
          loft("SM_RearBlock", [(277, octo(63.5, 56.5, 56.5, 12)), (283, octo(69.5, 62.5, 62.5, 15)), (354, octo(69.5, 62.5, 62.5, 15)), (360, octo(63.5, 56.5, 56.5, 12))], C_BLOCK)):
    operand(o, wire=False)

# ── cutters ──
prof = operand(side_prism("SM_Cutter_Profile", [(-1, 70), (361, 70), (361, -70), (283, -70), (283, -50), (275, -50), (271, -56), (155.3, -100), (40.6, -100), (13, -42.5), (-1, -42.5)], -100, 100, C_BOOL))
tuck = operand(side_prism("SM_Cutter_Tuck", [(282, -62.5), (296, -62.5), (296, -120), (282, -120)], -100, 100, C_BOOL))
operand(loft("SM_Cutter_SpinnerRecess", [(-5, octo(38, 37, 37, 13)), (5, octo(38, 37, 37, 13))], C_CUTS, rb.DARK))
operand(side_prism("SM_Cutter_IntakeMouth", [(12.5, -54), (23.9, -78), (39.9, -78), (28.5, -54)], -33, 33, C_CUTS, rb.DARK))
win = lambda p0, p1, z0, z1, c: [(p0 + c, z1), (p1 - c, z1), (p1, z1 - c), (p1, z0 + c), (p1 - c, z0), (p0 + c, z0), (p0, z0 + c), (p0, z1 - c)]
for side, sx in (("L", -1), ("R", 1)):
    operand(side_prism(f"SM_Cutter_WindowFrame_{side}", win(130, 222, -28.5, 26.5, 8), *((66, 100) if sx > 0 else (-100, -66)), C_CUTS, FRAME))
    operand(side_prism(f"SM_Cutter_WindowOpening_{side}", win(137, 215, -21.5, 19.5, 5), *((44, 100) if sx > 0 else (-100, -44)), C_CUTS, rb.DARK))

# ── SM_Body's stack: live booleans, then the chamfer and the normals ──
def boolean(name, op, target, transfer=False):
    md = body.modifiers.new(name, "BOOLEAN"); md.operation = op; md.solver = "EXACT"
    if isinstance(target, bpy.types.Collection): md.operand_type = "COLLECTION"; md.collection = target
    else: md.object = target
    if transfer: md.material_mode = "TRANSFER"
    return md
boolean("Union_Masses", "UNION", C_BLOCK)
boolean("Profile", "INTERSECT", prof)
boolean("Tuck", "DIFFERENCE", tuck)
boolean("Cuts", "DIFFERENCE", C_CUTS, transfer=True)
bev = body.modifiers.new("Bevel_Micro", "BEVEL"); bev.width = 0.005; bev.segments = 1; bev.limit_method = "ANGLE"
bev.angle_limit = math.radians(30); bev.harden_normals = True; bev.miter_outer = "MITER_ARC"
wn = body.modifiers.new("WeightedNormal", "WEIGHTED_NORMAL"); wn.keep_sharp = True; wn.weight = 50
for m in (rb.DARK, FRAME): body.data.materials.append(m)   # (the cut faces' materials, kept in the body's slots)

# ── secondary parts ──
bm = bmesh.new()
for p0, p1, r in [(136, 216, 4)] + [(pc - 1.5, pc + 1.5, r) for pc, r in ((150, 10), (165, 13), (180, 16), (195, 18))]:
    rb.prism(bm, 12, r * S, (180 - p1) * S, (180 - p0) * S)
comp = rb.obj("SM_Compressor_R", bm, [rb.METAL], C_GEO, bevel=None); comp.location = (44 * S, 0, -1 * S)
twin = bpy.data.objects.new("SM_Compressor_L", comp.data); C_GEO.objects.link(twin); twin.location = (-44 * S, 0, -1 * S)   # (an instance: shared mesh)
y = lambda p: (180.0 - p) * S
bm = bmesh.new(); rb.lathe(bm, [(y(4), 0.0), (y(4), 13 * S), (y(-6), 13 * S)] + rb.ogive(y(-6), 45 * S, 22 * S, tip=0.15), seg=48)
rb.obj("SM_Spinner", bm, [rb.DARK], C_GEO, bevel=None)
bm = bmesh.new()
for b in range(3): rb.blade(bm, 13 * S, 115 * S, 28 * S, 55, 22, 2 * math.pi * b / 3, "prop", sweep=4 * S)
for v in bm.verts: v.co.y += y(-15)
rb.obj("SM_Blades", bm, [rb.BLADE, rb.STRIPE], C_GEO, bevel=None, sharp=80.0)

# ── reference: the concept's views as image empties, registered to the traced geometry ──
def ref(name, file, size, loc, xaxis, yaxis):
    e = bpy.data.objects.new(name, None); e.empty_display_type = "IMAGE"; e.data = bpy.data.images.load(os.path.join(REF, file), check_existing=True)
    e.empty_display_size = size; e.empty_image_depth = "BACK"; e.show_empty_image_perspective = False; e.color[3] = 0.6; e.use_empty_image_alpha = True
    X, Y = Vector(xaxis), Vector(yaxis); e.matrix_world = Matrix.Translation(Vector(loc)) @ Matrix((X, Y, X.cross(Y))).transposed().to_4x4()
    C_REF.objects.link(e); return e
SF = S / 1.04   # (the front view was drawn ~4 % larger)
ref("REF_Side", "concept32_side.png", 440 * S, (-0.6, (180 - 155) * S, (119.5 - 135) * S), (0, -1, 0), (0, 0, 1))
ref("REF_Top", "concept32_top.png", 480 * S, ((126.5 - 135) * S, (180 - (750 - 595)) * S, -0.5), (0, -1, 0), (1, 0, 0))
ref("REF_Front", "concept32_front.png", 280 * SF, (-(260 - 257) * SF, -0.9, (62.5 - (390 - 336) / 1.04) * S), (-1, 0, 0), (0, 0, 1))

rb.finish(OUT)
ev = body.evaluated_get(bpy.context.evaluated_depsgraph_get()).data
print("LIVE built:", len(ev.polygons), "evaluated body faces;", [o.name for o in C_GEO.objects], "saved", OUT)
