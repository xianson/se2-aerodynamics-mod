"""TURBOPROP v7 (2026-10-16, after v6: "the back 1 m flush to the bb, get rid of the weird cylinder", shading problems on
the back octagon, the intake too small and not true to the concept). v6 notes follow.

TURBOPROP v6 (2026-10-16) - v5's masses on the grid, with joints that breathe (the user, on v5's close-ups: the nozzle
poking through the block's chamfer, the intake hanging out under the cowl, the gable miter at the block ends, z-fighting:
"every contour needs room to breathe").

Rules:
  * masses and flush faces on the 0.5 m grid (nacelle y -3.75 .. 3.75; x / z +-1.25, +-0.75, +-0.25), features on 0.25;
    the wide blocks are octagons with 0.5 m 45 deg corners (flats +-0.75), so the octagon itself sits on the grid;
  * no coincident faces: where two pieces meet one sinks clearly (>= 10 cm) into the other; a smaller piece ends well
    inside the face it meets (>= 10 cm clear of that face's edges); features stay >= 0.25 m in from a face's edges;
  * block ends are uniform frustums (every side inset alike) - no gable miters;
  * round only: nozzle (well inside the rear end face), collar, spinner.

  blender -b --factory-startup -P turboprop_v6.py -- <out.blend> <stamps.json> [concept.png]
"""
import bpy, bmesh, json, math, os, sys
from mathutils import Matrix, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import rotorcraft_blockouts as rb
import rotorcraft_detail as rd

args = sys.argv[sys.argv.index("--") + 1:]
OUT, STAMPS = args[0], args[1]
CONCEPT = args[2] if len(args) > 2 else None
B, C = 1.25, 0.50          # (the box half-width; the octagon corner)
END = 0.15                 # (a block end's uniform inset: the frustum chamfer)
K = math.sqrt(2) - 1       # (an octagon inset by d: h - d, c - K d)


def octo(h, c):
    return [(h, -(h - c)), (h, h - c), (h - c, h), (-(h - c), h), (-h, h - c), (-h, -(h - c)), (-(h - c), -h), (h - c, -h)]


def loft(bm, sections):
    rings = [[bm.verts.new(Vector((x, y, z))) for (x, z) in octo(h, c)] for (y, h, c) in sections]
    for A, Bb in zip(rings, rings[1:]):
        for i in range(8): bm.faces.new((A[i], A[(i + 1) % 8], Bb[(i + 1) % 8], Bb[i]))
    bm.faces.new(rings[0][::-1]); bm.faces.new(rings[-1])


def block(bm, y0, y1, h=B, c=C, e0=END, e1=END):
    """A wide octagonal block from y0 to y1 with uniform frustum ends (every side inset by e)."""
    sec = []
    if e0: sec.append((y0, h - e0, c - K * e0))
    sec.append((y0 + e0, h, c))
    sec.append((y1 - e1, h, c))
    if e1: sec.append((y1, h - e1, c - K * e1))
    loft(bm, sec)


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


def solid(name, bm, mats, col, cuts=(), dark_cuts=(), chamfer=0.02):
    o = rb.obj(name, bm, mats, col, bevel=None, sharp=30.0)
    for (lo, hi) in list(cuts) + list(dark_cuts):
        cb = bmesh.new(); r = bmesh.ops.create_cube(cb, size=1.0)
        for v in r["verts"]: v.co = Vector(tuple((lo[i] + hi[i]) / 2 + v.co[i] * (hi[i] - lo[i]) for i in range(3)))
        cm = bpy.data.meshes.new("_cut"); cb.to_mesh(cm); cb.free(); co = bpy.data.objects.new("_cut", cm); col.objects.link(co)
        md = o.modifiers.new("cut", "BOOLEAN"); md.object = co; md.operation = "DIFFERENCE"; md.solver = "EXACT"
        with bpy.context.temp_override(object=o, active_object=o, selected_objects=[o]): bpy.ops.object.modifier_apply(modifier=md.name)
        bpy.data.objects.remove(co); bpy.data.meshes.remove(cm)
    for p in o.data.polygons:
        c = o.matrix_world @ p.center
        for (lo, hi) in dark_cuts:
            if all(lo[i] - 0.003 <= c[i] <= hi[i] + 0.003 for i in range(3)): p.material_index = 1
    if chamfer:
        bv = o.modifiers.new("ch", "BEVEL"); bv.width = chamfer; bv.segments = 1; bv.limit_method = "ANGLE"; bv.angle_limit = math.radians(30)
        with bpy.context.temp_override(object=o, active_object=o, selected_objects=[o]): bpy.ops.object.modifier_apply(modifier=bv.name)
    o.data.shade_flat()   # (flat: boolean cuts + bevels leave n-gons smooth shading smears - v6's back octagon)
    return o


def build(name, col):
    D = rd.Decals()
    DULL = rb.mat("MetalDull", (0.35, 0.36, 0.38))
    U, X, Z = Vector((0, 1, 0)), Vector((1, 0, 0)), Vector((0, 0, 1))
    # ── rear block (-3.25 .. -1.25, four grid steps); dark vent windows cut into its side flats (y -2.75 .. -1.75, z +-0.5:
    #    0.25 m in from every edge of the flat), 12 cm deep ──
    # ── the back 1 m: a square section flush with the box on all four sides and the back (a rear mount face); a dark
    #    exhaust recess cut into the back, 0.5 m in from its edges, horizontal slats in it ──
    bm = bmesh.new(); rb.chamfer_box(bm, (-B, -3.75, -B), (B, -2.75, B), 0.06)
    solid(name + "_RearMount", bm, [rb.BODY, rb.DARK], col, dark_cuts=[((-0.75, -3.80, -0.75), (0.75, -3.60, 0.75))])
    bm = bmesh.new()
    for k in range(5): z = -0.56 + k * 0.28; rb.chamfer_box(bm, (-0.70, -3.66, z - 0.04), (0.70, -3.62, z + 0.04), 0.008)
    rb.obj(name + "_ExhaustSlats", bm, [DULL], col)
    bm = bmesh.new(); block(bm, -2.80, -1.25)
    win = [((1.13, -2.50, -0.50), (1.40, -1.50, 0.50)), ((-1.40, -2.50, -0.50), (-1.13, -1.50, 0.50))]
    solid(name + "_RearBlock", bm, [rb.BODY, rb.DARK], col, dark_cuts=win)
    bm = bmesh.new()
    for sx in (-1, 1):   # (slats: standing in the window, 4 cm back from the face, clear of its walls)
        for k in range(6): y = -2.36 + k * 0.144; rb.chamfer_box(bm, (sx * 1.15 - 0.04, y - 0.025, -0.44), (sx * 1.15 + 0.06, y + 0.025, 0.44), 0.006)
    for v in bm.verts: v.co.x = max(min(v.co.x, 1.21), -1.21)
    rb.obj(name + "_Vents", bm, [DULL], col)
    # ── the waist: a dark octagon 25 cm in from the box, sunk 10 cm into both blocks (no coincident end faces) ──
    bm = bmesh.new(); loft(bm, [(-1.35, 1.00, C - K * 0.25), (-0.65, 1.00, C - K * 0.25)]); solid(name + "_Waist", bm, [rb.DARK], col)
    # ── main block (-0.75 .. 1.75, five steps); an inset panel cut 4 cm into the top flat (x +-0.5, y -0.25 .. 1.25) ──
    bm = bmesh.new(); block(bm, -0.75, 1.75)
    solid(name + "_MainBlock", bm, [rb.BODY, rb.DARK], col, cuts=[((-0.50, -0.25, B - 0.04), (0.50, 1.25, B + 0.1))])
    # ── the front taper: starts 10 cm inside the main block (its base well inside the block's end face), tapers to the collar ──
    hb = B - END - 0.12                       # (its base: 12 cm inside the block's end face on every side)
    bm = bmesh.new(); loft(bm, [(1.65, hb, C - K * (B - hb)), (3.10, 0.74, C - K * (B - 0.74)), (3.25, 0.66, C - K * (B - 0.66))])
    solid(name + "_Cowl", bm, [rb.BODY], col)
    # ── the chin intake (the concept's: big, under the front): y 1.90 .. 3.20 (clear of the main block by 15 cm), 1.7 m wide,
    #    flush with the box bottom, its top buried in the taper; a sloped front with a dark grille cut in, slats, and
    #    horizontal grooves cut along its sides ──
    # (1.3 m wide: inside the taper's footprint so its buried top never shows; a vertical front, chamfered at the bottom)
    bm = bmesh.new(); side_prism(bm, [(1.90, -0.40), (3.20, -0.40), (3.20, -1.05), (3.00, -B), (2.05, -B), (1.90, -1.10)], -0.65, 0.65)
    grooves = []
    for sx in (-1, 1):
        for z in (-0.82, -1.02):   # (3 cm deep grooves in the side faces at x = +-0.85)
            x0, x1 = (0.62, 0.70) if sx > 0 else (-0.70, -0.62)
            grooves.append(((x0, 2.15, z - 0.025), (x1, 2.85, z + 0.025)))
    solid(name + "_Intake", bm, [rb.BODY, rb.DARK], col, dark_cuts=[((-0.45, 3.06, -1.00), (0.45, 3.30, -0.78))] + grooves)   # (the grille: below the taper, 0.2 m in from the front's edges)
    bm = bmesh.new()
    for k in range(6): x = -0.375 + k * 0.15; rb.chamfer_box(bm, (x - 0.022, 3.10, -0.98), (x + 0.022, 3.16, -0.80), 0.006)
    rb.obj(name + "_IntakeSlats", bm, [DULL], col)
    # ── round parts: the collar (inside the taper's end face), the spinner (inside the collar), the nozzle (inside the rear
    #    block's end face, sunk into it) ──
    bm = bmesh.new(); ring(bm, 3.15, 3.75, 0.60, 0.30, 48, ch=0.04); rb.obj(name + "_Collar", bm, [rb.DARK], col)
    bm = bmesh.new(); rb.lathe(bm, [(3.68, 0.0), (3.68, 0.54), (3.78, 0.57), (4.40, 0.48), (5.00, 0.30), (5.35, 0.10), (5.45, 0.0)], 48)
    rb.obj(name + "_Spinner", bm, [rb.DARK], col, sharp=20.0)
    bm = bmesh.new()
    for b in range(3): rb.blade(bm, 0.40, 6.1, 1.10, 52, 20, 2 * math.pi * b / 3, "prop", sweep=0.2)
    for v in bm.verts: v.co.y += 4.25
    rb.obj(name + "_Blades", bm, [rb.BLADE, rb.STRIPE], col, bevel=None, sharp=80.0)
    # ── decals (2 mm up): panel lines on grid lines, clear of features; bolts and plates 0.25 m in from edges ──
    for N, V in ((Z, X), (-Z, X)):
        D.strip(N * B + U * -2.25, V, U, N, 1.30, 0.035, "dark")
    for N, V in ((X, Z), (-X, Z), (-Z, X)):
        D.strip(N * B + U * 0.25, V, U, N, 1.30, 0.035, "dark")
    for sx in (-1, 1):
        for y in (-0.10, 1.10): D.stamp(22, Vector((sx * 0.38, y, B - 0.04)), U, X, Z, 0.07)
    D.stamp(695, Vector((0, 0.50, B - 0.04)), U, X, Z, 0.70)
    for sx in (-1, 1):
        N = Vector((sx, 0, 0)); V = Vector((0, 0, sx))
        D.stamp(1359, N * B + U * 1.00 + Z * 0.30, U, V, N, 0.30)
    D.finish(name + "_Decals", col)


def zfight_check(col, tol=0.002):
    """Coplanar overlapping faces between different solids (z-fighting): same plane within tol, overlapping footprints."""
    obs = [o for o in col.objects if o.type == "MESH" and not o.name.endswith(("Occupancy", "Decals"))]
    faces = []
    for o in obs:
        for p in o.data.polygons:
            n = (o.matrix_world.to_3x3() @ p.normal).normalized(); c = o.matrix_world @ p.center
            vs = [o.matrix_world @ o.data.vertices[i].co for i in p.vertices]
            lo = Vector(tuple(min(v[i] for v in vs) for i in range(3))); hi = Vector(tuple(max(v[i] for v in vs) for i in range(3)))
            faces.append((o.name, n, n.dot(c), lo, hi, p.area))
    hits = []
    for i, a in enumerate(faces):
        if a[5] < 1e-4: continue
        for b in faces[i + 1:]:
            if a[0] == b[0] or b[5] < 1e-4: continue
            if abs(abs(a[1].dot(b[1])) - 1) > 1e-4: continue
            if abs(a[2] - (b[2] if a[1].dot(b[1]) > 0 else -b[2])) > tol: continue
            if all(min(a[4][k], b[4][k]) - max(a[3][k], b[3][k]) > -1e-4 for k in range(3)): hits.append((a[0], b[0], round(a[5], 4)))
    return hits


def main():
    rb.reset()
    for mname in ("AtlasDecal_Trims03", "AtlasDecal_Parts02", "MetalDull"): rb.mat(mname, (0.5, 0.5, 0.5))
    rd.STAMP.update({s["id"]: s for s in json.load(open(STAMPS))["stamps"]})
    rd.GAP_D = 0.002
    col = rb.collection("Turboprop_250_v7")
    build("TPv7", col)
    hits = zfight_check(col)
    print("ZFIGHT", len(hits), hits[:12])
    rb.occupancy("TPv7_Occupancy", [((-0.5, -1.5, -0.5), (0.5, 1.5, 0.5)), ((-2.5, 1.5, -2.5), (2.5, 2.5, 2.5))], col, 2.5)
    if CONCEPT and os.path.exists(CONCEPT):
        img = bpy.data.images.load(CONCEPT)
        e = bpy.data.objects.new("Concept_Reference", None); e.empty_display_type = "IMAGE"; e.data = img
        e.empty_display_size = 12; e.location = (-7, 0, 0); e.rotation_euler = (math.pi / 2, 0, -math.pi / 2)
        bpy.context.scene.collection.objects.link(e)
    rb.finish(OUT)
    for mm in bpy.data.materials:
        if mm.name.startswith("AtlasDecal") and hasattr(mm, "blend_method"): mm.blend_method = "HASHED"
    bpy.ops.wm.save_mainfile()
    print("TPV7 saved", OUT, sum(len(o.data.polygons) for o in bpy.data.objects if o.type == "MESH"), "faces")


main()
