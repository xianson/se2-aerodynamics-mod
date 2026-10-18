"""TURBOPROP v5 (2026-10-16) - v4's big masses kept, its surface fixed (the user: the tubes look stupid, it doesn't use
the box, and "weird armor plates instead of a solid smooth surface"):
  nozzle cylinder (back) -> wide octagon (rear block) -> small dark octagon (waist) -> wide octagon (main block) ->
  short tapered octagon -> dark collar -> cone spinner; the intake block underneath.
  * each mass is ONE solid, flat faces, chamfered ends; the wide blocks' flat faces sit flush on the 2.5 m box (top,
    bottom, sides), the 45 deg corner facets carry the octagon;
  * detail = a few recesses cut into the solids (dark side vent windows, an inset top panel, the intake mouth) + decals
    (panel lines, bolts, plates); no plates, no pipes.

  blender -b --factory-startup -P turboprop_v5.py -- <out.blend> <stamps.json> [concept.png]
"""
import bpy, bmesh, json, math, os, sys
from mathutils import Matrix, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import rotorcraft_blockouts as rb
import rotorcraft_detail as rd

args = sys.argv[sys.argv.index("--") + 1:]
OUT, STAMPS = args[0], args[1]
CONCEPT = args[2] if len(args) > 2 else None
B = 1.25


def octo(h, c):
    return [(h, -(h - c)), (h, h - c), (h - c, h), (-(h - c), h), (-h, h - c), (-h, -(h - c)), (-(h - c), -h), (h - c, -h)]


def loft(bm, sections):
    rings = [[bm.verts.new(Vector((x, y, z))) for (x, z) in octo(h, c)] for (y, h, c) in sections]
    for A, Bb in zip(rings, rings[1:]):
        for i in range(8): bm.faces.new((A[i], A[(i + 1) % 8], Bb[(i + 1) % 8], Bb[i]))
    bm.faces.new(rings[0][::-1]); bm.faces.new(rings[-1])


def ring(bm, y0, y1, ro, ri, seg=48, ch=0.0):
    prof = [(y0, ro - ch), (y0 + ch, ro), (y1 - ch, ro), (y1, ro - ch)] if ch > 0 else [(y0, ro), (y1, ro)]
    pts = prof + [(y1, ri), (y0, ri)]
    rings = [[bm.verts.new(Vector((r * math.cos(2 * math.pi * j / seg), y, r * math.sin(2 * math.pi * j / seg)))) for j in range(seg)] for (y, r) in pts]
    for k in range(len(rings)):
        A, Bb = rings[k], rings[(k + 1) % len(rings)]
        for j in range(seg): bm.faces.new((A[j], A[(j + 1) % seg], Bb[(j + 1) % seg], Bb[j]))


def side_prism(bm, profile, x0, x1):
    a = [bm.verts.new(Vector((x0, y, z))) for (y, z) in profile]; b = [bm.verts.new(Vector((x1, y, z))) for (y, z) in profile]
    n = len(profile); bm.faces.new(a[::-1]); bm.faces.new(b)
    for i in range(n): bm.faces.new((a[i], a[(i + 1) % n], b[(i + 1) % n], b[i]))


def solid(name, bm, mats, col, cuts=(), dark_cuts=(), chamfer=0.025):
    """A solid object: recesses cut in (boolean boxes (lo, hi)); faces inside a dark cut get the second material; then
    a flat micro chamfer (one segment) on every edge sharper than 30 deg."""
    o = rb.obj(name, bm, mats, col, bevel=None, sharp=30.0)
    for k, (lo, hi) in enumerate(list(cuts) + list(dark_cuts)):
        cb = bmesh.new(); r = bmesh.ops.create_cube(cb, size=1.0)
        for v in r["verts"]: v.co = Vector(tuple((lo[i] + hi[i]) / 2 + v.co[i] * (hi[i] - lo[i]) for i in range(3)))
        cm = bpy.data.meshes.new("_cut"); cb.to_mesh(cm); cb.free(); co = bpy.data.objects.new("_cut", cm); col.objects.link(co)
        md = o.modifiers.new("cut", "BOOLEAN"); md.object = co; md.operation = "DIFFERENCE"; md.solver = "EXACT"
        with bpy.context.temp_override(object=o, active_object=o, selected_objects=[o]): bpy.ops.object.modifier_apply(modifier=md.name)
        bpy.data.objects.remove(co); bpy.data.meshes.remove(cm)
    for p in o.data.polygons:   # (the recesses' walls and floors dark)
        c = o.matrix_world @ p.center
        for (lo, hi) in dark_cuts:
            if all(lo[i] - 0.003 <= c[i] <= hi[i] + 0.003 for i in range(3)): p.material_index = 1
    if chamfer:
        bv = o.modifiers.new("ch", "BEVEL"); bv.width = chamfer; bv.segments = 1; bv.limit_method = "ANGLE"; bv.angle_limit = math.radians(30)
        bv.harden_normals = False; bv.miter_outer = "MITER_ARC"
        with bpy.context.temp_override(object=o, active_object=o, selected_objects=[o]): bpy.ops.object.modifier_apply(modifier=bv.name)
    o.data.shade_smooth(); o.data.set_sharp_from_angle(angle=math.radians(30))
    return o


def build(name, col):
    D = rd.Decals()
    DULL = rb.mat("MetalDull", (0.35, 0.36, 0.38))
    U, X, Z = Vector((0, 1, 0)), Vector((1, 0, 0)), Vector((0, 0, 1))
    # ── rear block: wide octagon flush on the box, chamfered ends, a dark vent window cut into each side ──
    bm = bmesh.new(); loft(bm, [(-3.05, 1.08, 0.43), (-2.88, B, 0.50), (-1.17, B, 0.50), (-1.00, 1.08, 0.43)])
    win = [((1.10, -2.55, -0.42), (1.40, -1.45, 0.42)), ((-1.40, -2.55, -0.42), (-1.10, -1.45, 0.42))]
    solid(name + "_RearBlock", bm, [rb.BODY, rb.DARK], col, dark_cuts=win)
    bm = bmesh.new()
    for sx in (-1, 1):
        for k in range(7): y = -2.45 + k * 0.15; rb.chamfer_box(bm, (sx * 1.10 - 0.03, y - 0.03, -0.38), (sx * 1.10 + 0.05, y + 0.03, 0.38), 0.008)
    rb.obj(name + "_Vents", bm, [DULL], col)
    # ── the waist: a smaller dark octagon, 28 cm in from the box ──
    bm = bmesh.new(); loft(bm, [(-1.10, 0.97, 0.39), (-0.50, 0.97, 0.39)]); solid(name + "_Waist", bm, [rb.DARK], col)
    # ── main block: wide octagon flush on the box, an inset panel cut into the top ──
    bm = bmesh.new(); loft(bm, [(-0.60, 1.08, 0.43), (-0.43, B, 0.50), (1.33, B, 0.50), (1.50, 1.08, 0.43)])
    solid(name + "_MainBlock", bm, [rb.BODY, rb.DARK], col, cuts=[((-0.70, -0.15, B - 0.04), (0.70, 1.05, B + 0.1))])
    # ── the short tapered octagon up to the collar ──
    bm = bmesh.new(); loft(bm, [(1.45, 1.02, 0.41), (1.60, 1.06, 0.42), (2.95, 0.74, 0.30), (3.10, 0.68, 0.27)])
    solid(name + "_Cowl", bm, [rb.BODY], col)
    # ── the intake block underneath: drawn in side view, flush with the box bottom, a dark recessed mouth ──
    bm = bmesh.new(); side_prism(bm, [(0.10, -0.85), (2.55, -0.85), (2.55, -1.05), (2.35, -B), (0.40, -B), (0.10, -1.05)], -0.62, 0.62)
    solid(name + "_Intake", bm, [rb.BODY, rb.DARK], col, dark_cuts=[((-0.48, 2.30, -1.12), (0.48, 2.70, -0.96))])
    # ── the round parts: collar, spinner, nozzle ──
    bm = bmesh.new(); ring(bm, 3.05, 3.40, 0.72, 0.40, 48, ch=0.04); rb.obj(name + "_Collar", bm, [rb.DARK], col)
    bm = bmesh.new(); rb.lathe(bm, [(3.40, 0.0), (3.40, 0.64), (3.50, 0.68), (4.20, 0.58), (4.85, 0.37), (5.25, 0.13), (5.35, 0.0)], 48)
    rb.obj(name + "_Spinner", bm, [rb.DARK], col, sharp=20.0)
    bm = bmesh.new()
    ring(bm, -3.75, -3.58, 1.00, 0.86, 48, ch=0.03)
    for y in (-3.46, -3.29, -3.12): ring(bm, y - 0.06, y + 0.06, 1.02, 0.86, 48, ch=0.02)
    ring(bm, -3.58, -2.98, 0.94, 0.86, 48)
    rb.lathe(bm, [(-3.62, 0.0), (-3.62, 0.10), (-3.20, 0.42), (-2.98, 0.42), (-2.98, 0.0)], 24)
    rb.obj(name + "_Nozzle", bm, [rb.DARK], col)
    bm = bmesh.new()
    for b in range(3): rb.blade(bm, 0.45, 6.1, 1.20, 52, 20, 2 * math.pi * b / 3, "prop", sweep=0.2)
    for v in bm.verts: v.co.y += 3.95
    rb.obj(name + "_Blades", bm, [rb.BLADE, rb.STRIPE], col, bevel=None, sharp=80.0)
    # ── decals: panel lines across the big blocks' faces, bolts round the inset, plates on the main block's sides ──
    for (ya, yb, lines) in ((-2.88, -1.17, (-2.02,)), (-0.43, 1.33, (0.45,))):
        for (N, V) in ((Z, X), (-Z, X), (X, Z), (-X, Z)):
            for y in lines:
                if N.x != 0 and ya < -1.5: continue   # (not across the vent windows)
                D.strip(N * B + U * y, V, U, N, 2 * (B - 0.50) - 0.10, 0.035, "dark")
    for sx in (-1, 1):
        for y in (-0.05, 0.95): D.stamp(22, Vector((sx * 0.85, y, B)), U, X, Z, 0.08)
    D.stamp(695, Vector((0, 0.45, B - 0.04)), U, X, Z, 0.85)
    for sx in (-1, 1):
        N = Vector((sx, 0, 0)); V = Vector((0, 0, sx))
        D.stamp(1359, N * B + U * 0.95 + Z * 0.30, U, V, N, 0.32)
        for y in (-2.70, -1.30):
            for z in (-0.60, 0.60): D.stamp(22, N * B + U * y + Z * z, U, V, N, 0.08)
    D.stamp(3323, Vector((0, 1.2, -B)), U, X, -Z, 0.6)
    D.finish(name + "_Decals", col)


def main():
    rb.reset()
    for mname in ("AtlasDecal_Trims03", "AtlasDecal_Parts02", "MetalDull"): rb.mat(mname, (0.5, 0.5, 0.5))
    rd.STAMP.update({s["id"]: s for s in json.load(open(STAMPS))["stamps"]})
    col = rb.collection("Turboprop_250_v5")
    build("TPv5", col)
    rb.occupancy("TPv5_Occupancy", [((-0.5, -1.5, -0.5), (0.5, 1.5, 0.5)), ((-2.5, 1.5, -2.5), (2.5, 2.5, 2.5))], col, 2.5)
    if CONCEPT and os.path.exists(CONCEPT):
        img = bpy.data.images.load(CONCEPT)
        e = bpy.data.objects.new("Concept_Reference", None); e.empty_display_type = "IMAGE"; e.data = img
        e.empty_display_size = 12; e.location = (-7, 0, 0); e.rotation_euler = (math.pi / 2, 0, -math.pi / 2)
        bpy.context.scene.collection.objects.link(e)
    rb.finish(OUT)
    for mm in bpy.data.materials:
        if mm.name.startswith("AtlasDecal") and hasattr(mm, "blend_method"): mm.blend_method = "HASHED"
    bpy.ops.wm.save_mainfile()
    print("TPV5 saved", OUT, sum(len(o.data.polygons) for o in bpy.data.objects if o.type == "MESH"), "faces")


main()
