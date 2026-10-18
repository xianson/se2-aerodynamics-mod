"""TURBOPROP REPLICA (2026-10-16): the user's Gemini concept (images/32: side, top, front, 3/4 on one sheet) reproduced at its
own proportions - every number below is traced off the sheet in pixels (side view: body front face x 105, top flush y 57,
axis y 120; top view: front face x 595; front view: top y 336) and scaled once (the body width 139 px = 0.5 m).
Fitting to the block's bounding box (the chin intake hangs below the box) is the next, separate step.

  p (px from the body's front face, aft +)   z (px up from the axis)   half-widths in px
  cowl     p 0..122    front face octagon h 43 (+-41.5, corners 15) -> 45: octagon h 69.5 (+-62.5, corners 29)
  keel     p 13..277   under the cowl and mid body, to z -92.5; its front the angled intake face (13,-42.5)->(37,-92.5)
  groove   p 122..127  a ring 3 px deep
  mid      p 127..290  top corners 16; waist p 200..237: sides in to 60.5, top corners grow to 8 x 33
  belly    p 175..271  slopes up from -92.5 to -56
  rear     p 277..360  the mount block: h 69.5, +-62.5, corners 15, ends chamfered 6
  window   p 130..222, z -28.5..26.5: a frame 3.5 deep, the opening inset 7, 25 deep, compressor discs inside
  spinner  r 22, 45 long, a collar r 13; 3 blades R 13..115, chord 28, hazard tips

  blender -b --factory-startup -P turboprop_replica.py -- <out.blend>
"""
import bpy, bmesh, math, os, sys
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import rotorcraft_blockouts as rb

OUT = sys.argv[sys.argv.index("--") + 1]
S = 0.5 / 139.0                      # (m per drawing px)
rb.reset()
FRAME = rb.mat("MetalDull", (0.38, 0.40, 0.43))
col = rb.collection("Turboprop_replica")
V = lambda x, p, z: Vector((x * S, (180.0 - p) * S, z * S))   # (drawing px -> block metres, nose +Y, centred)


def ring_pts(half):
    """A symmetric section from its right half [(x, z)] listed top to bottom."""
    return half + [(-x, z) for x, z in reversed(half)]


def loft(name, rings, mat=rb.BODY):
    """Sections [(p, half)] lofted along the length, capped."""
    bm = bmesh.new(); R = [[bm.verts.new(V(x, p, z)) for x, z in ring_pts(h)] for p, h in rings]; n = len(R[0])
    for A, B in zip(R, R[1:]):
        for k in range(n): bm.faces.new((A[k], A[(k + 1) % n], B[(k + 1) % n], B[k]))
    bm.faces.new(R[0][::-1]); bm.faces.new(R[-1])
    return rb.obj(name, bm, [mat], col, bevel=None)


def side_prism(name, pts, x0, x1, mat=rb.BODY):
    """A side-view outline [(p, z)] extruded across x0..x1."""
    bm = bmesh.new(); a = [bm.verts.new(V(x0, p, z)) for p, z in pts]; b = [bm.verts.new(V(x1, p, z)) for p, z in pts]; n = len(pts)
    bm.faces.new(a[::-1]); bm.faces.new(b)
    for i in range(n): bm.faces.new((a[i], a[(i + 1) % n], b[(i + 1) % n], b[i]))
    return rb.obj(name, bm, [mat], col, bevel=None)


def boolean(o, cutter, op, transfer=False):
    md = o.modifiers.new("b", "BOOLEAN"); md.object = cutter; md.operation = op; md.solver = "EXACT"
    if transfer: md.material_mode = "TRANSFER"
    with bpy.context.temp_override(object=o, active_object=o, selected_objects=[o]): bpy.ops.object.modifier_apply(modifier=md.name)
    bpy.data.objects.remove(cutter)


octo = lambda h, t, b, c: [(h - c, t), (h, t - c), (h, -(b - c)), (h - c, -b)]
# ── the masses ──
body = loft("Turboprop_Body", [(0, octo(43, 41.5, 41.5, 15)), (45, octo(69.5, 62.5, 62.5, 29)), (122, octo(69.5, 62.5, 62.5, 29))])
keel_a = [(69.5, -30), (69.5, -62.5), (39, -92.5)]
mid = lambda w, cv, lo=-62.5: [(53.5, 62.5), (w, 62.5 - cv), (w, lo), (39, -92.5)]   # (lo: where the side meets the keel facet)
pieces = [loft("_keel", [(10, keel_a), (122, keel_a)]),
          loft("_groove", [(119, [(37.5, 59.5), (66.5, 30.5), (66.5, -61.5), (37.5, -89.5)]), (130, [(37.5, 59.5), (66.5, 30.5), (66.5, -61.5), (37.5, -89.5)])]),
          loft("_mid", [(127, mid(69.5, 16)), (200, mid(69.5, 16)), (237, mid(60.5, 33, -50)), (290, mid(60.5, 33, -50))]),
          loft("_rear", [(277, octo(63.5, 56.5, 56.5, 12)), (283, octo(69.5, 62.5, 62.5, 15)), (354, octo(69.5, 62.5, 62.5, 15)), (360, octo(63.5, 56.5, 56.5, 12))])]
for pc in pieces: boolean(body, pc, "UNION")
# the side profile: the intake's angled face, the keel's floor, the belly sweeping up to the rear block
boolean(body, side_prism("_profile", [(-1, 70), (361, 70), (361, -70), (283, -70), (283, -50), (275, -50), (271, -56), (155.3, -100), (40.6, -100), (13, -42.5), (-1, -42.5)], -100, 100), "INTERSECT")

boolean(body, side_prism("_tuck", [(282, -62.5), (296, -62.5), (296, -120), (282, -120)], -100, 100), "DIFFERENCE")   # (the mid keel's end, inside the rear block)
# ── cuts with their own faces ──
boolean(body, loft("_face", [(-5, octo(38, 37, 37, 13)), (5, octo(38, 37, 37, 13))], rb.DARK), "DIFFERENCE", transfer=True)   # (the dark recess round the spinner)
k = 0.475   # (the intake face: dp/dz along its slope)
boolean(body, side_prism("_mouth", [(18.5 - 6, -54), (29.9 - 6, -78), (29.9 + 10, -78), (18.5 + 10, -54)], -33, 33, rb.DARK), "DIFFERENCE", transfer=True)
win = lambda p0, p1, z0, z1, c: [(p0 + c, z1), (p1 - c, z1), (p1, z1 - c), (p1, z0 + c), (p1 - c, z0), (p0 + c, z0), (p0, z0 + c), (p0, z1 - c)]
for sx in (-1, 1):
    lo, hi = (66, 100) if sx > 0 else (-100, -66)
    boolean(body, side_prism("_frame", win(130, 222, -28.5, 26.5, 8), lo, hi, FRAME), "DIFFERENCE", transfer=True)
    lo, hi = (44, 100) if sx > 0 else (-100, -44)
    boolean(body, side_prism("_open", win(137, 215, -21.5, 19.5, 5), lo, hi, rb.DARK), "DIFFERENCE", transfer=True)
body.data.shade_flat()

# ── the compressor glimpsed in each window: a shaft and four discs ──
bm = bmesh.new()
for sx in (-1, 1):
    for p0, p1, r in [(136, 216, 4)] + [(pc - 1.5, pc + 1.5, r) for pc, r in ((150, 10), (165, 13), (180, 16), (195, 18))]:
        before = set(bm.verts)
        rb.prism(bm, 12, r * S, (180 - p1) * S, (180 - p0) * S)
        for v in set(bm.verts) - before: v.co.x += sx * 44 * S; v.co.z += -1 * S
o = rb.obj("Turboprop_Compressor", bm, [rb.METAL], col, bevel=None); o.data.shade_flat()

# ── spinner and blades ──
y = lambda p: (180.0 - p) * S
bm = bmesh.new(); rb.lathe(bm, [(y(4), 0.0), (y(4), 13 * S), (y(-6), 13 * S)] + rb.ogive(y(-6), 45 * S, 22 * S, tip=0.15), seg=32)
rb.obj("Turboprop_Spinner", bm, [rb.DARK], col, bevel=None)
bm = bmesh.new()
for b in range(3): rb.blade(bm, 13 * S, 115 * S, 28 * S, 55, 22, 2 * math.pi * b / 3, "prop", sweep=4 * S)
for v in bm.verts: v.co.y += y(-15)
rb.obj("Turboprop_Blades", bm, [rb.BLADE, rb.STRIPE], col, bevel=None, sharp=80.0)
rb.finish(OUT)
lo = [min(v.co[i] for v in body.data.vertices) for i in range(3)]; hi = [max(v.co[i] for v in body.data.vertices) for i in range(3)]
print("REPLICA saved", OUT, len(body.data.polygons), "body faces; body extents", [round(h - l, 3) for l, h in zip(lo, hi)])
