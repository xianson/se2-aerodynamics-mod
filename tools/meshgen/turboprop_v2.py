"""TURBOPROP v2 (2026-10-16): modelled from scratch to the user's Gemini concept sheet (4 variants; the user: "some combo
of them"). The mix: variant 1's body, chin intake grille and side exhaust manifold; variant 3's armoured cheek plates;
variant 2's recessed top vent; variant 4's copper strap bands. Shared DNA: pointed gunmetal spinner -> narrow gearbox
collar -> a faceted (octagonal) body that swells -> a smaller dark ribbed rear nozzle; big chamfered light panels over a
dark core, detail concentrated at the seams.

  blender -b --factory-startup -P turboprop_v2.py -- <out.blend> <stamps.json> [concept.png]

Cell 2.5: nacelle y -3.75 .. 3.75 (3 cells), x / z within +-1.25; the prop disc cell y 3.75 .. 6.25. Blender Y = Forward.
"""
import bpy, bmesh, json, math, os, sys
from mathutils import Matrix, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import rotorcraft_blockouts as rb
import rotorcraft_detail as rd

args = sys.argv[sys.argv.index("--") + 1:]
OUT, STAMPS = args[0], args[1]
CONCEPT = args[2] if len(args) > 2 else None
T8 = math.tan(math.pi / 8)

# the body's dark core: (y, inradius) - octagonal, flats on the axes and diagonals
CORE = [(-2.95, 0.96), (-2.85, 1.00), (-2.20, 1.02), (1.55, 1.02), (2.55, 0.70), (2.65, 0.64)]
# panel segments (y0, y1) over the core, with the dark bands between them
SEGS = [(-2.85, -2.22), (-2.12, -0.68), (-0.56, 1.50), (1.60, 2.52)]


def core_r(y):
    for (y0, r0), (y1, r1) in zip(CORE, CORE[1:]):
        if y0 <= y <= y1: return r0 + (r1 - r0) * (y - y0) / (y1 - y0)
    return CORE[0][1] if y < CORE[0][0] else CORE[-1][1]


def facet(k):
    """Facet k of the octagon: its normal N (k * 45 deg around Y from +X) and its tangent V."""
    a = k * math.pi / 4
    return Vector((math.cos(a), 0, math.sin(a))), Vector((-math.sin(a), 0, math.cos(a)))


def octo_loft(bm, prof):
    """An octagonal loft along Y through (y, inradius) - flats on the facets of facet()."""
    rings = []
    for (y, r) in prof:
        R = r / math.cos(math.pi / 8)
        rings.append([bm.verts.new(Vector((R * math.cos(k * math.pi / 4 + math.pi / 8), y, R * math.sin(k * math.pi / 4 + math.pi / 8)))) for k in range(8)])
    for A, B in zip(rings, rings[1:]):
        for k in range(8): bm.faces.new((A[k], A[(k + 1) % 8], B[(k + 1) % 8], B[k]))
    bm.faces.new(rings[0][::-1]); bm.faces.new(rings[-1])


def panel(bm, k, y0, y1, lift=0.04, t=0.11, margin=0.06, tilt=0.0):
    """A panel slab on facet k from y0 to y1 following the core's slope, inset by margin (dark gaps show), lifted off the
    core by 'lift', 't' thick; returns its outer centre and frame for decals."""
    N, V = facet(k); U = Vector((0, 1, 0))
    ya, yb = y0 + margin, y1 - margin
    pts = []
    for (y, side) in ((ya, -1), (yb, -1), (yb, 1), (ya, 1)):
        r = core_r(y); hw = r * T8 - margin
        pts.append((y, side * hw, r))
    for d in (lift, lift + t):
        pass
    lo = [bm.verts.new(U * y + V * v + N * (r + lift)) for (y, v, r) in pts]
    hi = [bm.verts.new(U * y + V * v + N * (r + lift + t)) for (y, v, r) in pts]
    bm.faces.new(lo[::-1]); bm.faces.new(hi)
    for i in range(4): bm.faces.new((lo[i], lo[(i + 1) % 4], hi[(i + 1) % 4], hi[i]))
    yc = (ya + yb) / 2
    return N * (core_r(yc) + lift + t), U, V, N, yb - ya, 2 * (core_r(yc) * T8 - margin)


def octo_band(bm, y, w, r_out, r_in):
    """An octagonal strap band (a ring) at y, w wide."""
    prof_o = [(y - w / 2, r_out), (y + w / 2, r_out)]
    rings = []
    for rr in (r_out, r_in):
        R = rr / math.cos(math.pi / 8)
        for yy in (y - w / 2, y + w / 2):
            rings.append([bm.verts.new(Vector((R * math.cos(k * math.pi / 4 + math.pi / 8), yy, R * math.sin(k * math.pi / 4 + math.pi / 8)))) for k in range(8)])
    o0, o1, i0, i1 = rings
    for k in range(8):
        n = (k + 1) % 8
        bm.faces.new((o0[k], o0[n], o1[n], o1[k])); bm.faces.new((i0[k], i1[k], i1[n], i0[n]))
        bm.faces.new((o0[k], i0[k], i0[n], o0[n])); bm.faces.new((o1[k], o1[n], i1[n], i1[k]))


def tube(name, pts, r, mats, col):
    """A pipe swept along points (a smooth curve with rounded bends), as a mesh object."""
    cu = bpy.data.curves.new(name, "CURVE"); cu.dimensions = "3D"; cu.bevel_depth = r; cu.bevel_resolution = 3; cu.use_fill_caps = True
    sp = cu.splines.new("BEZIER"); sp.bezier_points.add(len(pts) - 1)
    for bp, p in zip(sp.bezier_points, pts): bp.co = p; bp.handle_left_type = bp.handle_right_type = "AUTO"
    ob = bpy.data.objects.new(name + "_c", cu); col.objects.link(ob)
    bpy.context.view_layer.update()
    me = bpy.data.meshes.new_from_object(ob.evaluated_get(bpy.context.evaluated_depsgraph_get()))
    bpy.data.objects.remove(ob); bpy.data.curves.remove(cu)
    bm = bmesh.new(); bm.from_mesh(me); bpy.data.meshes.remove(me)
    return bm


def build(name, col):
    D = rd.Decals()
    COPPER = rb.mat("Copper", (0.72, 0.42, 0.25))
    # ── the dark core ──
    bm = bmesh.new(); octo_loft(bm, CORE); rb.obj(name + "_Core", bm, [rb.DARK], col)
    # ── panels: every facet of every segment, less the openings (manifold sides, the chin intake) ──
    bm = bmesh.new(); armour = bmesh.new(); dec = []
    for si, (y0, y1) in enumerate(SEGS):
        for k in range(8):
            if si == 2 and k in (0, 4): continue                     # (the sides of the main body: the exhaust manifold)
            if si in (2, 3) and k == 6: continue                     # (the bottom front: the chin intake)
            if si in (1, 2) and k == 2: continue                     # (the top: the mounting saddle)
            if si == 3 and k in (0, 4, 1, 3):                        # (variant 3's armoured cheeks: thick plates)
                dec.append((si, k, panel(armour, k, y0, y1, lift=0.03, t=0.13, margin=0.04)))
                continue
            dec.append((si, k, panel(bm, k, y0, y1)))
    rb.obj(name + "_Panels", bm, [rb.BODY], col)
    rb.obj(name + "_Armour", armour, [rb.BODY], col)
    # ── the mounting saddle (the user: "it has to attach well to other blocks"): a flat plate flush with the cell's top
    #    face, the full 2.5 m wide, over the main body - the block's mount face (wings / pylons / hull) ──
    bm = bmesh.new(); rb.chamfer_box(bm, (-1.25, -2.15, 1.08), (1.25, 1.52, 1.25), 0.05)
    rb.obj(name + "_Saddle", bm, [rb.BODY], col)
    bm = bmesh.new()
    for sx in (-1, 1):   # (its dark side rails, down onto the upper diagonals)
        rb.chamfer_box(bm, (sx * 1.25 - (0.22 if sx > 0 else -0.02), -2.05, 0.80), (sx * 1.25 + (0.02 if sx < 0 else 0.0) - (0.0 if sx > 0 else -0.20), 1.42, 1.10), 0.03)
    rb.obj(name + "_SaddleRails", bm, [rb.DARK], col)
    D.stamp(695, Vector((0, -0.3, 1.25)), Vector((0, 1, 0)), Vector((1, 0, 0)), Vector((0, 0, 1)), 1.1)
    for sx in (-1, 1):
        for y in (-1.95, 1.32): D.stamp(22, Vector((sx * 1.08, y, 1.25)), Vector((0, 1, 0)), Vector((1, 0, 0)), Vector((0, 0, 1)), 0.07)
    # ── copper strap bands (variant 4) in two of the dark seams ──
    bm = bmesh.new()
    for y in (-2.17, 1.55): octo_band(bm, y, 0.07, core_r(y) + 0.115, core_r(y) - 0.02)
    rb.obj(name + "_Straps", bm, [COPPER], col)
    # ── the gearbox collar, the spinner, the blades ──
    bm = bmesh.new()
    rb.lathe(bm, [(2.60, 0.0), (2.60, 0.64), (2.66, 0.70), (3.20, 0.70), (3.26, 0.64), (3.55, 0.60)], 64)
    rb.obj(name + "_Collar", bm, [rb.DARK], col)
    bm = bmesh.new(); rb.lathe(bm, [(2.80, 0.72), (2.86, 0.76), (2.98, 0.76), (3.04, 0.72)], 64)
    rb.obj(name + "_CollarRing", bm, [COPPER], col)
    bm = bmesh.new(); rb.lathe(bm, [(3.55, 0.0), (3.55, 0.62), (3.63, 0.68)] + rb.ogive(3.63, 1.25, 0.68, n=20)[1:], 64)
    rb.obj(name + "_Spinner", bm, [rb.DARK], col)
    bm = bmesh.new()
    for b in range(3): rb.blade(bm, 0.45, 6.1, 1.25, 52, 20, 2 * math.pi * b / 3, "prop", sweep=0.2)
    for v in bm.verts: v.co.y += 4.05
    rb.obj(name + "_Blades", bm, [rb.BLADE, rb.STRIPE], col, bevel=None, sharp=80.0)
    # ── the rear nozzle: dark, ribbed, an inner cone ──
    bm = bmesh.new()
    rb.lathe(bm, [(-2.95, 0.98), (-3.02, 1.06), (-3.14, 1.06), (-3.18, 0.99), (-3.28, 0.99), (-3.32, 1.06), (-3.44, 1.06), (-3.48, 0.99),
                  (-3.58, 0.99), (-3.64, 1.10), (-3.74, 1.10), (-3.74, 0.90), (-3.30, 0.78)], 64)
    rb.lathe(bm, [(-3.66, 0.0), (-3.66, 0.16), (-3.30, 0.50), (-3.05, 0.50)], 48)
    rb.obj(name + "_Nozzle", bm, [rb.DARK], col)
    # ── the chin intake (variant 1): a chamfered scoop, a dark recess, vertical slats ──
    bm = bmesh.new(); rb.chamfer_box(bm, (-0.60, 0.20, -1.24), (0.60, 3.10, -0.62), 0.08)
    rb.obj(name + "_Intake", bm, [rb.BODY], col)
    bm = bmesh.new(); rb.chamfer_box(bm, (-0.50, 3.06, -1.16), (0.50, 3.14, -0.70), 0.015)
    rb.obj(name + "_IntakeRecess", bm, [rb.DARK], col)
    bm = bmesh.new()
    for i in range(7):
        x = -0.42 + i * 0.14; rb.chamfer_box(bm, (x - 0.025, 3.08, -1.13), (x + 0.025, 3.16, -0.73), 0.008)
    rb.obj(name + "_IntakeSlats", bm, [rb.mat("MetalDull", (0.35, 0.36, 0.38))], col)
    # ── the side exhaust manifold (variant 1): four stubs out of each side, bending back into a collector ──
    pipes = bmesh.new(); rings = bmesh.new()
    for sx in (-1, 1):
        for i, y in enumerate((1.15, 0.65, 0.15, -0.35)):
            z = 0.42 - i * 0.06
            p = [Vector((sx * 0.98, y, z)), Vector((sx * 1.10, y - 0.05, z - 0.05)), Vector((sx * 1.16, y - 0.30, -0.10)), Vector((sx * 1.16, y - 0.45, -0.28))]
            b = tube(f"{name}_ex{sx}{i}", p, 0.055, None, col); m = bpy.data.meshes.new("_t"); b.to_mesh(m); b.free(); pipes.from_mesh(m); bpy.data.meshes.remove(m)
            rd.oriented_lathe(rings, [(0.0, 0.075), (0.06, 0.075)], 24, Vector((sx * 1.02, y, z)), Vector((sx, 0, 0)))
        c = [Vector((sx * 1.16, 1.0, -0.30)), Vector((sx * 1.16, -0.9, -0.30)), Vector((sx * 1.12, -1.6, -0.22)), Vector((sx * 1.02, -2.05, -0.12))]
        b = tube(f"{name}_col{sx}", c, 0.085, None, col); m = bpy.data.meshes.new("_t"); b.to_mesh(m); b.free(); pipes.from_mesh(m); bpy.data.meshes.remove(m)
        for y in (0.9, -0.95): rd.oriented_lathe(rings, [(0.0, 0.11), (0.08, 0.11)], 24, Vector((sx * 1.16, y, -0.30)), Vector((0, -1, 0)))
    rb.obj(name + "_Manifold", pipes, [rb.mat("MetalDull", (0.35, 0.36, 0.38))], col, bevel=None, sharp=60.0)
    rb.obj(name + "_ManifoldRings", rings, [COPPER], col)
    # ── the top vent (variant 2): a raised frame with a dark slotted recess on the rear top panel ──
    bm = bmesh.new(); y0, y1 = -1.85, -0.95; r = core_r(-1.4) + 0.10
    for (a, b_) in (((-0.34, y0, r), (0.34, y0 + 0.07, r + 0.06)), ((-0.34, y1 - 0.07, r), (0.34, y1, r + 0.06)),
                    ((-0.34, y0, r), (-0.27, y1, r + 0.06)), ((0.27, y0, r), (0.34, y1, r + 0.06))):
        rb.chamfer_box(bm, a, b_, 0.012)
    rb.obj(name + "_VentFrame", bm, [rb.mat("MetalDull", (0.35, 0.36, 0.38))], col)
    bm = bmesh.new(); rb.chamfer_box(bm, (-0.27, y0 + 0.07, r - 0.01), (0.27, y1 - 0.07, r + 0.02), 0.005)
    for i in range(5): rb.chamfer_box(bm, (-0.25, y0 + 0.14 + i * 0.13, r), (0.25, y0 + 0.18 + i * 0.13, r + 0.045), 0.008)
    rb.obj(name + "_Vent", bm, [rb.DARK], col)

    # ── decals: restrained - bolts on the big panels, a dark trim along the top panels, a warning plate on the cheeks ──
    for si, k, (C, U, V, N, L, W) in dec:
        if si in (1, 2) and W > 0.5:
            for su in (-1, 1):
                for sv in (-1, 1): D.stamp(22, C + U * (su * (L / 2 - 0.09)) + V * (sv * (W / 2 - 0.09)), U, V, N, 0.045)
        if si in (1, 2) and k == 2: D.strip(C + V * (W / 2 - 0.10), U, V, N, L - 0.30, 0.08, "dark")
        if si == 3 and k in (0, 4): D.stamp(1359, C + U * 0.05, U, V, N, 0.30, rot=0.0)
        if si == 1 and k in (1, 3): D.strip(C, U, V, N, L - 0.4, 0.05, "white")
    D.finish(name + "_Decals", col)


def main():
    rb.reset()
    for m in ("AtlasDecal_Trims03", "AtlasDecal_Parts02", "Copper"): rb.mat(m, (0.5, 0.5, 0.5))
    rd.STAMP.update({s["id"]: s for s in json.load(open(STAMPS))["stamps"]})
    col = rb.collection("Turboprop_250_v2")
    build("TPv2", col)
    rb.occupancy("TPv2_Occupancy", [((-0.5, -1.5, -0.5), (0.5, 1.5, 0.5)), ((-2.5, 1.5, -2.5), (2.5, 2.5, 2.5))], col, 2.5)
    if CONCEPT and os.path.exists(CONCEPT):   # (the concept sheet as a reference plane behind the model)
        img = bpy.data.images.load(CONCEPT)
        e = bpy.data.objects.new("Concept_Reference", None); e.empty_display_type = "IMAGE"; e.data = img
        e.empty_display_size = 12; e.location = (-6, -3, 0); e.rotation_euler = (math.pi / 2, 0, -math.pi / 2)
        e.show_in_front = False; sc = bpy.context.scene; sc.collection.objects.link(e)
    rb.finish(OUT)
    for m in bpy.data.materials:
        if m.name.startswith("AtlasDecal") and hasattr(m, "blend_method"): m.blend_method = "HASHED"
    bpy.ops.wm.save_mainfile()
    print("TPV2 saved", OUT, sum(len(o.data.polygons) for o in bpy.data.objects if o.type == "MESH"), "faces")


main()
