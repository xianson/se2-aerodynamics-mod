"""TURBOPROP v4 (2026-10-16) - the concept's own large forms (variant 01, read with the user): simple flat-faced shapes
with chamfers, not cylinders ("very lincoln loggy" - v3's drums). Round only where the concept is: the spinner, the
collar, the nozzle and the bent manifold pipes.

  1 spinner (cone)  2 front cowl (octagonal frustum + dark collar)  3 chin intake (side-profile box + slats)
  4 main housing (octagonal prism, flat panelled facets)  5 top hump (raised chamfered box, copper strap)
  6 rear housing (octagonal prism, a recessed side window with the exhaust manifold)  7 nozzle (dark ribbed cylinder)

Block fit (the vanilla flush study): what touches the 2.5 m box is flat and composite - the top hump (top), the
thickened cheek / window-frame plates (sides), the chin intake (bottom); the body itself sits 10 cm inside the box.

  blender -b --factory-startup -P turboprop_v4.py -- <out.blend> <stamps.json> [concept.png]
"""
import bpy, bmesh, json, math, os, sys
from mathutils import Matrix, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import rotorcraft_blockouts as rb
import rotorcraft_detail as rd

args = sys.argv[sys.argv.index("--") + 1:]
OUT, STAMPS = args[0], args[1]
CONCEPT = args[2] if len(args) > 2 else None
B = 1.25   # (the block box half-width)

# the dark core's sections along Y: (y, half-width, corner cut) - octagons with flats on the axes and 45 deg corners
CORE = [(-3.00, 0.95, 0.36), (-2.85, 1.05, 0.40), (-0.95, 1.05, 0.40), (-0.92, 0.92, 0.35), (-0.58, 0.92, 0.35),
        (-0.55, 1.05, 0.40), (1.55, 1.05, 0.40), (3.10, 0.72, 0.28)]
PANEL_T = 0.10


def octo(h, c):
    """An octagon (x, z), counter-clockwise from the right face's bottom: facets 0 right, 1 upper-right, 2 top, 3 upper-left,
    4 left, 5 lower-left, 6 bottom, 7 lower-right."""
    return [(h, -(h - c)), (h, h - c), (h - c, h), (-(h - c), h), (-h, h - c), (-h, -(h - c)), (-(h - c), -h), (h - c, -h)]


def at(y):
    """The core's octagon at y (interpolated between the sections)."""
    for (y0, h0, c0), (y1, h1, c1) in zip(CORE, CORE[1:]):
        if y0 <= y <= y1:
            t = (y - y0) / (y1 - y0); return octo(h0 + (h1 - h0) * t, c0 + (c1 - c0) * t)
    return octo(*CORE[0][1:]) if y < CORE[0][0] else octo(*CORE[-1][1:])


def loft(bm, sections):
    """A closed loft through (y, outline) sections of equal point counts (flat faces between them)."""
    rings = [[bm.verts.new(Vector((x, y, z))) for (x, z) in pts] for (y, pts) in sections]
    n = len(rings[0])
    for A, Bb in zip(rings, rings[1:]):
        for i in range(n): bm.faces.new((A[i], A[(i + 1) % n], Bb[(i + 1) % n], Bb[i]))
    bm.faces.new(rings[0][::-1]); bm.faces.new(rings[-1])


def side_prism(bm, profile, x0, x1):
    """A shape drawn in side view (y, z), extruded across x0..x1."""
    a = [bm.verts.new(Vector((x0, y, z))) for (y, z) in profile]; b = [bm.verts.new(Vector((x1, y, z))) for (y, z) in profile]
    n = len(profile)
    bm.faces.new(a[::-1]); bm.faces.new(b)
    for i in range(n): bm.faces.new((a[i], a[(i + 1) % n], b[(i + 1) % n], b[i]))


def facet_quad(i, ya, yb):
    """Facet i of the core between ya and yb: its four corners (a0, a1 at ya; b1, b0 at yb) and outward normal."""
    A, Bo = at(ya), at(yb)
    a0, a1 = Vector((A[i][0], ya, A[i][1])), Vector((A[(i + 1) % 8][0], ya, A[(i + 1) % 8][1]))
    b0, b1 = Vector((Bo[i][0], yb, Bo[i][1])), Vector((Bo[(i + 1) % 8][0], yb, Bo[(i + 1) % 8][1]))
    n = (a1 - a0).cross(b0 - a0).normalized()
    if n.dot((a0 + a1) / 2 - Vector((0, ya, 0))) < 0: n = -n
    return a0, a1, b1, b0, n


def slab(bm, q, u0, u1, w0, w1, t, lift=0.0, cut=0.0):
    """A flat slab on a facet quad: the sub-rectangle (u along the facet's width, w along Y; 0..1) lifted 'lift' off it and
    't' thick, its two leading corners cut at 45 deg by 'cut' (metres)."""
    a0, a1, b1, b0, n = q
    P = lambda u, w: a0.lerp(a1, u).lerp(b0.lerp(b1, u), w)
    corners = [P(u0, w0), P(u1, w0), P(u1, w1), P(u0, w1)]
    if cut > 0:
        e_u = (corners[1] - corners[0]).normalized(); e_w = (corners[3] - corners[0]).normalized()
        c2, c3 = corners[2], corners[3]
        corners = [corners[0], corners[1], c2 - e_w * cut, c2 - e_u * cut, c3 + e_u * cut, c3 - e_w * cut][:2] + [c2 - e_w * cut, c2 - e_u * cut, c3 + e_u * cut, c3 - e_w * cut]
    lo = [bm.verts.new(p + n * lift) for p in corners]; hi = [bm.verts.new(p + n * (lift + t)) for p in corners]
    k = len(corners)
    bm.faces.new(lo[::-1]); bm.faces.new(hi)
    for i in range(k): bm.faces.new((lo[i], lo[(i + 1) % k], hi[(i + 1) % k], hi[i]))


def ring(bm, y0, y1, ro, ri, seg=48, ch=0.0):
    prof = [(y0, ro - ch), (y0 + ch, ro), (y1 - ch, ro), (y1, ro - ch)] if ch > 0 else [(y0, ro), (y1, ro)]
    pts = prof + [(y1, ri), (y0, ri)]
    rings = [[bm.verts.new(Vector((r * math.cos(2 * math.pi * j / seg), y, r * math.sin(2 * math.pi * j / seg)))) for j in range(seg)] for (y, r) in pts]
    for k in range(len(rings)):
        A, Bb = rings[k], rings[(k + 1) % len(rings)]
        for j in range(seg): bm.faces.new((A[j], A[(j + 1) % seg], Bb[(j + 1) % seg], Bb[j]))


def tube(name, pts, r, col):
    cu = bpy.data.curves.new(name, "CURVE"); cu.dimensions = "3D"; cu.bevel_depth = r; cu.bevel_resolution = 2; cu.use_fill_caps = True
    sp = cu.splines.new("POLY"); sp.points.add(len(pts) - 1)
    for p, q in zip(sp.points, pts): p.co = (*q, 1)
    ob = bpy.data.objects.new(name, cu); col.objects.link(ob); bpy.context.view_layer.update()
    me = bpy.data.meshes.new_from_object(ob.evaluated_get(bpy.context.evaluated_depsgraph_get()))
    bpy.data.objects.remove(ob); bpy.data.curves.remove(cu)
    bm = bmesh.new(); bm.from_mesh(me); bpy.data.meshes.remove(me); return bm


def elbow_path(p0, p1, p2, r_bend=0.12, n=4):
    """A pipe path p0 -> p1 -> p2 with a rounded (circular-ish) bend at p1."""
    d0 = (p1 - p0).normalized(); d1 = (p2 - p1).normalized()
    a = p1 - d0 * r_bend; b = p1 + d1 * r_bend
    pts = [p0, a]
    for k in range(1, n):
        t = k / n; pts.append((a.lerp(p1, t)).lerp(p1.lerp(b, t), t))
    return pts + [b, p2]


def build(name, col):
    D = rd.Decals()
    COPPER = rb.mat("Copper", (0.72, 0.42, 0.25)); DULL = rb.mat("MetalDull", (0.35, 0.36, 0.38))
    U = Vector((0, 1, 0))
    # ── the dark core (one flat-faced loft through the sections: rear chamfer, rear housing, waist, main, cowl taper) ──
    bm = bmesh.new(); loft(bm, [(y, octo(h, c)) for (y, h, c) in CORE]); rb.obj(name + "_Core", bm, [rb.DARK], col)
    # ── panels: flat slabs on every facet, sections split on the grid, 3 cm seams ──
    pan = bmesh.new(); thick = bmesh.new(); m = 0.03
    segs = [("rear", -2.85, -1.90), ("rear", -1.90, -0.95), ("main", -0.55, 0.50), ("main", 0.50, 1.55), ("cowl", 1.55, 3.10)]
    for (part, ya, yb) in segs:
        for i in range(8):
            q = facet_quad(i, ya, yb); width = (q[1] - q[0]).length; length = yb - ya
            mu, mw = m / max(width, 1e-6), m / length
            if part == "rear" and i in (0, 4):
                # (the side window: a thick frame flush with the box side, the dark core showing through)
                if ya < -1.5: continue
                tt = B - 1.05
                slab(thick, facet_quad(i, -2.85, -0.95), mu, 1 - mu, 0.02, 0.18, tt)
                slab(thick, facet_quad(i, -2.85, -0.95), mu, 1 - mu, 0.82, 0.98, tt)
                slab(thick, facet_quad(i, -2.85, -0.95), mu, 0.16, 0.18, 0.82, tt)
                slab(thick, facet_quad(i, -2.85, -0.95), 0.84, 1 - mu, 0.18, 0.82, tt)
                continue
            if part == "main" and i in (0, 4) and ya < 0:
                slab(thick, q, mu, 1 - mu, mw, 1 - mw, B - 1.05, cut=0.12)       # (cheek plates, flush with the box side)
                continue
            if part == "main" and i == 2: continue                              # (the top hump sits here)
            if i == 6 and part in ("main", "cowl"): continue                     # (the chin intake)
            slab(pan, q, mu, 1 - mu, mw, 1 - mw, PANEL_T, cut=0.08 if part != "cowl" else 0.0)
    rb.obj(name + "_Panels", pan, [rb.BODY], col)
    rb.obj(name + "_Armour", thick, [rb.BODY], col)
    # ── the top hump (flush with the box top: the mount face), a copper strap set into it ──
    bm = bmesh.new(); side_prism(bm, [(-0.50, 0.95), (1.40, 0.95), (1.40, 1.10), (1.20, B), (-0.30, B), (-0.50, 1.08)], -0.70, 0.70)
    rb.obj(name + "_Hump", bm, [rb.BODY], col)
    bm = bmesh.new(); side_prism(bm, [(0.20, 1.00), (0.42, 1.00), (0.42, B + 0.0), (0.20, B + 0.0)], -0.74, 0.74)
    for v in bm.verts: v.co.z = min(v.co.z, B - 0.012)
    rb.obj(name + "_HumpStrap", bm, [COPPER], col)
    # ── the chin intake: drawn in side view (a sloped front), flush with the box bottom, a dark mouth and slats ──
    bm = bmesh.new(); side_prism(bm, [(0.60, -0.85), (2.95, -0.85), (2.95, -1.05), (2.70, -B), (0.95, -B), (0.60, -1.05)], -0.62, 0.62)
    rb.obj(name + "_Intake", bm, [rb.BODY], col)
    bm = bmesh.new(); rb.chamfer_box(bm, (-0.50, 2.93, -1.10), (0.50, 3.00, -0.92), 0.01); rb.obj(name + "_IntakeMouth", bm, [rb.DARK], col)
    bm = bmesh.new()
    for k in range(6): x = -0.40 + k * 0.16; rb.chamfer_box(bm, (x - 0.025, 2.95, -1.09), (x + 0.025, 3.02, -0.93), 0.006)
    rb.obj(name + "_IntakeSlats", bm, [DULL], col)
    # ── the collar, spinner, nozzle (the round parts) ──
    bm = bmesh.new(); ring(bm, 3.05, 3.40, 0.74, 0.40, 48, ch=0.04); rb.obj(name + "_Collar", bm, [rb.DARK], col)
    bm = bmesh.new(); rb.lathe(bm, [(3.40, 0.0), (3.40, 0.66), (3.50, 0.70), (4.20, 0.60), (4.85, 0.38), (5.25, 0.13), (5.35, 0.0)], 48)
    rb.obj(name + "_Spinner", bm, [rb.DARK], col, sharp=20.0)
    bm = bmesh.new()
    ring(bm, -3.75, -3.58, 0.98, 0.84, 48, ch=0.03)
    for y in (-3.47, -3.30, -3.13): ring(bm, y - 0.06, y + 0.06, 1.00, 0.84, 48, ch=0.02)
    ring(bm, -3.58, -2.95, 0.92, 0.84, 48)
    rb.lathe(bm, [(-3.62, 0.0), (-3.62, 0.10), (-3.20, 0.42), (-2.98, 0.42), (-2.98, 0.0)], 24)
    rb.obj(name + "_Nozzle", bm, [rb.DARK], col)
    bm = bmesh.new()
    for b in range(3): rb.blade(bm, 0.45, 6.1, 1.20, 52, 20, 2 * math.pi * b / 3, "prop", sweep=0.2)
    for v in bm.verts: v.co.y += 3.95
    rb.obj(name + "_Blades", bm, [rb.BLADE, rb.STRIPE], col, bevel=None, sharp=80.0)
    # ── the exhaust manifold out of each side window: three stubs bending back into a collector, copper couplers ──
    pipes = bmesh.new(); cps = bmesh.new()
    for sx in (-1, 1):
        for k, y in enumerate((-1.25, -1.62, -1.99)):
            p0 = Vector((sx * 0.98, y, 0.30 - k * 0.05)); p1 = Vector((sx * 1.13, y, 0.30 - k * 0.05)); p2 = Vector((sx * 1.13, y - 0.35, -0.12))
            b_ = tube(f"m{sx}{k}", elbow_path(p0, p1, p2), 0.06, col); me = bpy.data.meshes.new("_"); b_.to_mesh(me); b_.free(); pipes.from_mesh(me); bpy.data.meshes.remove(me)
            rd.oriented_lathe(cps, [(0.0, 0.08), (0.07, 0.08)], 20, Vector((sx * 1.02, y, 0.30 - k * 0.05)), Vector((sx, 0, 0)))
        b_ = tube(f"c{sx}", [Vector((sx * 1.13, -1.30, -0.15)), Vector((sx * 1.13, -2.55, -0.15)), Vector((sx * 1.00, -2.75, -0.15))], 0.08, col)
        me = bpy.data.meshes.new("_"); b_.to_mesh(me); b_.free(); pipes.from_mesh(me); bpy.data.meshes.remove(me)
        rd.oriented_lathe(cps, [(0.0, 0.10), (0.10, 0.10)], 20, Vector((sx * 1.13, -1.45, -0.15)), Vector((0, -1, 0)))
    rb.obj(name + "_Manifold", pipes, [DULL], col, bevel=None, sharp=50.0); rb.obj(name + "_Couplers", cps, [COPPER], col)
    # ── decals (few): bolts on panel corners of the main / rear housings, a hatch on the hump, a warning plate on the cheeks ──
    X, Z = Vector((1, 0, 0)), Vector((0, 0, 1))
    D.stamp(695, Vector((0, 0.35, B)) + Z * 0.0, U, X, Z, 0.9)
    for sx in (-1, 1):
        N = Vector((sx, 0, 0)); V = Vector((0, 0, sx))
        D.stamp(1359, N * B + U * (-0.02) + Z * 0.35, U, V, N, 0.30)
        for y in (-0.45, 0.40):
            for z in (-0.55, 0.55): D.stamp(22, N * B + U * y + Z * z, U, V, N, 0.07)
    D.finish(name + "_Decals", col)


def main():
    rb.reset()
    for mname in ("AtlasDecal_Trims03", "AtlasDecal_Parts02", "Copper", "MetalDull"): rb.mat(mname, (0.5, 0.5, 0.5))
    rd.STAMP.update({s["id"]: s for s in json.load(open(STAMPS))["stamps"]})
    col = rb.collection("Turboprop_250_v4")
    build("TPv4", col)
    rb.occupancy("TPv4_Occupancy", [((-0.5, -1.5, -0.5), (0.5, 1.5, 0.5)), ((-2.5, 1.5, -2.5), (2.5, 2.5, 2.5))], col, 2.5)
    if CONCEPT and os.path.exists(CONCEPT):
        img = bpy.data.images.load(CONCEPT)
        e = bpy.data.objects.new("Concept_Reference", None); e.empty_display_type = "IMAGE"; e.data = img
        e.empty_display_size = 12; e.location = (-7, 0, 0); e.rotation_euler = (math.pi / 2, 0, -math.pi / 2)
        bpy.context.scene.collection.objects.link(e)
    rb.finish(OUT)
    for mm in bpy.data.materials:
        if mm.name.startswith("AtlasDecal") and hasattr(mm, "blend_method"): mm.blend_method = "HASHED"
    bpy.ops.wm.save_mainfile()
    print("TPV4 saved", OUT, sum(len(o.data.polygons) for o in bpy.data.objects if o.type == "MESH"), "faces")


main()
