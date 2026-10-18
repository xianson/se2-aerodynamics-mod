"""TURBOPROP 0.5 - CLEAN SHEET (2026-10-16). The user's photo of a turboprop pod (fat front, long side window, belly sweeping
up to the tail, flat top running into the wing root), boxified and fitted to the block: nacelle 1.5 x 0.5 x 0.5
(3x1x1 cells of 0.5), prop disc 5x5x1 cells ahead. Sections on the 0.25 grid, features on 0.125.

  y (front +0.75)   piece
  +0.75 .. +0.50    cowl frustum: a 0.30 octagon stepping up to the full section; the chin under it (flush belly)
  +0.50 .. -0.75    body: grid octagon, flush at +-0.25 (0.06 corners); top and sides flush to the back
   0.00 .. -0.75    belly sweeps up to z -0.125 at the back face
  +0.375 .. -0.375  side windows: dark panels set in 3 cm, z +-0.125
  ahead             spinner (round), 3 blades
Low poly, flat shaded; the body, cowl and chin are one solid (unioned), pockets cut with their dark faces.

  blender -b --factory-startup -P turboprop_050_clean.py -- <out.blend>
"""
import bpy, bmesh, math, os, sys
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import rotorcraft_blockouts as rb

OUT = sys.argv[sys.argv.index("--") + 1]
rb.reset()
col = rb.collection("Turboprop_050")


def octo(h, c, cb=None):
    """An octagon of half-size h: top corners c, bottom corners cb (default c) - the lower corners grow toward the tail."""
    cb = c if cb is None else cb
    return [(h, -(h - cb)), (h, h - c), (h - c, h), (-(h - c), h), (-h, h - c), (-h, -(h - cb)), (-(h - cb), -h), (h - cb, -h)]


def loft(name, rings, mat):
    """Octagonal rings [(y, half, corner[, lower corner])] lofted along Y, capped."""
    bm = bmesh.new(); R = [[bm.verts.new(Vector((x, r[0], z))) for x, z in octo(*r[1:])] for r in rings]
    for A, B in zip(R, R[1:]):
        for k in range(8): bm.faces.new((A[k], A[(k + 1) % 8], B[(k + 1) % 8], B[k]))
    bm.faces.new(R[0][::-1]); bm.faces.new(R[-1])
    return rb.obj(name, bm, [mat], col, bevel=None)


def side_prism(name, pts, half_x, mat):
    """A side-view outline [(y, z)] extruded across X."""
    bm = bmesh.new(); a = [bm.verts.new(Vector((-half_x, y, z))) for y, z in pts]; b = [bm.verts.new(Vector((half_x, y, z))) for y, z in pts]
    n = len(pts); bm.faces.new(a[::-1]); bm.faces.new(b)
    for i in range(n): bm.faces.new((a[i], a[(i + 1) % n], b[(i + 1) % n], b[i]))
    return rb.obj(name, bm, [mat], col, bevel=None)


def box(name, lo, hi, mat, c=0.0):
    bm = bmesh.new()
    if c: rb.chamfer_box(bm, lo, hi, c)
    else:
        r = bmesh.ops.create_cube(bm, size=1.0)
        for v in r["verts"]: v.co = Vector(tuple((lo[i] + hi[i]) / 2 + v.co[i] * (hi[i] - lo[i]) for i in range(3)))
    return rb.obj(name, bm, [mat], col, bevel=None)


def boolean(o, cutter, op, transfer=False):
    md = o.modifiers.new("b", "BOOLEAN"); md.object = cutter; md.operation = op; md.solver = "EXACT"
    if transfer: md.material_mode = "TRANSFER"
    with bpy.context.temp_override(object=o, active_object=o, selected_objects=[o]): bpy.ops.object.modifier_apply(modifier=md.name)
    bpy.data.objects.remove(cutter)


# ── the body: side profile x octagon section, with the back end a uniform 3 cm frustum ──
body = side_prism("Turboprop_050_Body", [(0.50, 0.25), (-0.75, 0.25), (-0.75, -0.125), (0.0, -0.25), (0.50, -0.25)], 0.3, rb.BODY)
# the section: top corners 0.06 throughout; the lower corners grow from 0.06 (y 0) to 0.15 at the tail - the photo's
# lower body pulling in under the flat wing-root top
boolean(body, loft("_sect", [(0.6, 0.25, 0.06), (0.0, 0.25, 0.06), (-0.72, 0.25, 0.06, 0.15), (-0.75, 0.22, 0.05, 0.13)], rb.BODY), "INTERSECT")
# the cowl: a short 45 deg frustum off the spinner, then a full cowl ring (inset 1.5 cm) sunk 5 cm into the body
boolean(body, loft("_cowl", [(0.75, 0.16, 0.05), (0.675, 0.235, 0.06), (0.45, 0.235, 0.06)], rb.BODY), "UNION")
# the chin: under the cowl, its belly flush with the body's, sunk into both
boolean(body, box("_chin", (-0.16, 0.45, -0.25), (0.16, 0.72, -0.08), rb.BODY, c=0.02), "UNION")
# pockets (dark faces): the chin's intake mouth, the side windows
boolean(body, box("_mouth", (-0.12, 0.62, -0.21), (0.12, 0.80, -0.12), rb.DARK), "DIFFERENCE", transfer=True)
for sx in (-1, 1):   # (two panes per side, a 4 cm web between them at y 0: the photo's compressor | combustor)
    for y0, y1 in ((-0.375, -0.02), (0.02, 0.375)):
        boolean(body, box("_win", (0.22 if sx > 0 else -0.35, y0, -0.125), (0.35 if sx > 0 else -0.22, y1, 0.125), rb.DARK), "DIFFERENCE", transfer=True)
body.data.shade_flat()

# ── spinner and blades ──
bm = bmesh.new(); rb.lathe(bm, [(0.72, 0.0), (0.72, 0.13), (0.80, 0.13)] + rb.ogive(0.80, 0.20, 0.13, tip=0.12)[1:], seg=32)
rb.obj("Turboprop_050_Spinner", bm, [rb.DARK], col, bevel=None)
bm = bmesh.new()
for b in range(3): rb.blade(bm, 0.15, 1.225, 0.31, 55, 22, 2 * math.pi * b / 3, "prop", sweep=0.05)
for v in bm.verts: v.co.y += 0.88
rb.obj("Turboprop_050_Blades", bm, [rb.BLADE, rb.STRIPE], col, bevel=None, sharp=80.0)
rb.occupancy("Turboprop_050_Occupancy", [((-0.5, -1.5, -0.5), (0.5, 1.5, 0.5)), ((-2.5, 1.5, -2.5), (2.5, 2.5, 2.5))], col, 0.5)
rb.finish(OUT)
print("CLEAN saved", OUT, len(body.data.polygons), "body faces")
