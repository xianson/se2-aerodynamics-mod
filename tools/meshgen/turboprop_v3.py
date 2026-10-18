"""TURBOPROP v3 (2026-10-16) - built the way Keen builds blocks (studied on the vanilla meshes; tools: the shape / primitive
/ flush studies):
  * a block is a pile of separate simple solids that overlap (no booleans): chamfered boxes and slabs, straight-segment
    extrusions, and circles only (cylinders, rings, straight-segment cones) - no splines, no lofts, no freeform;
  * the MOUNT face first: a composite flush plate (frame + shallow inset panel + collar bands down the sides), its edges on
    the 0.5 m grid - what touches the block box is flat and grid-aligned, so neighbours meet it cleanly;
  * everything else (the round body, pipes, nozzle, spinner) stays inside the box, touching it only as rims;
  * chamfers: main ~8 % of a part's size on silhouette edges, a micro chamfer on every other edge (rb.obj's auto);
  * big primitives broken into sections, detail at the seams.
The user's concept sheet (variants mixed) sets the anatomy: spinner -> stepped gearbox -> drum -> ribbed nozzle, chin
intake, side exhaust stubs, copper band.

  blender -b --factory-startup -P turboprop_v3.py -- <out.blend> <stamps.json> [concept.png]

Cell 2.5: nacelle x, z in +-1.25, y -3.75 .. 3.75; the prop disc cell y 3.75 .. 6.25. Blender Y = Forward, Z up.
"""
import bpy, bmesh, json, math, os, sys
from mathutils import Matrix, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import rotorcraft_blockouts as rb
import rotorcraft_detail as rd

args = sys.argv[sys.argv.index("--") + 1:]
OUT, STAMPS = args[0], args[1]
CONCEPT = args[2] if len(args) > 2 else None
H = 1.25                      # (the box half-width)
MOUNT_Y = (-2.75, 1.75)       # (the mount section along the nacelle: 9 grid steps of 0.5 m)


def box(bm, lo, hi, ch):
    """A chamfered box (ch: the main 45 deg chamfer on all its edges)."""
    rb.chamfer_box(bm, lo, hi, ch)


def ring(bm, y0, y1, ro, ri, seg=48, ch=0.0, axis="Y"):
    """A ring / short tube about the axis: outer ro, bore ri, y0..y1, its outer rims chamfered by ch (a circle-only solid)."""
    prof_out = [(y0, ro - ch), (y0 + ch, ro), (y1 - ch, ro), (y1, ro - ch)] if ch > 0 else [(y0, ro), (y1, ro)]
    pts = prof_out + [(y1, ri), (y0, ri)]
    rings = []
    for (y, r) in pts:
        rings.append([bm.verts.new(Vector((r * math.cos(2 * math.pi * j / seg), y, r * math.sin(2 * math.pi * j / seg)))) for j in range(seg)])
    n = len(rings)
    for k in range(n):
        A, B = rings[k], rings[(k + 1) % n]
        for j in range(seg): bm.faces.new((A[j], A[(j + 1) % seg], B[(j + 1) % seg], B[j]))
    if axis == "Z":
        for rr in rings:
            for v in rr: v.co = Vector((v.co.x, -v.co.z, v.co.y))


def cyl(bm, y0, y1, r, seg=48, ch=0.0):
    """A solid cylinder about Y with chamfered rims (a closed revolve of straight segments)."""
    rb.lathe(bm, [(y0, 0.0), (y0, r - ch), (y0 + ch, r), (y1 - ch, r), (y1, r - ch), (y1, 0.0)] if ch > 0 else [(y0, 0.0), (y0, r), (y1, r), (y1, 0.0)], seg)


def build(name, col):
    D = rd.Decals()
    COPPER = rb.mat("Copper", (0.72, 0.42, 0.25)); DULL = rb.mat("MetalDull", (0.35, 0.36, 0.38))
    Y0, Y1 = MOUNT_Y
    U, X, Z = Vector((0, 1, 0)), Vector((1, 0, 0)), Vector((0, 0, 1))

    # ═══ 1. the mount section (top), composite and flush, edges on the 0.5 m grid ═══
    bm = bmesh.new()
    # (the frame: four bars around the inset, flush on top; the big chamfer only on their underside edges)
    box(bm, (-H, Y0, 1.03), (H, Y0 + 0.50, H), 0.03)                      # (rear bar, one grid step)
    box(bm, (-H, Y1 - 0.50, 1.03), (H, Y1, H), 0.03)                      # (front bar)
    for sx in (-1, 1):
        box(bm, (sx * H - (0.50 if sx > 0 else 0.0), Y0 + 0.50, 1.03), (sx * H + (0.50 if sx < 0 else 0.0), Y1 - 0.50, H), 0.03)   # (side bars)
    rb.obj(name + "_MountFrame", bm, [rb.BODY], col)
    # (the inset panel: 4 cm down, in two sections split on the grid)
    bm = bmesh.new()
    box(bm, (-0.73, Y0 + 0.52, 1.00), (0.73, -0.27, H - 0.04), 0.02)
    box(bm, (-0.73, -0.23, 1.00), (0.73, Y1 - 0.52, H - 0.04), 0.02)
    rb.obj(name + "_MountInset", bm, [rb.BODY], col)
    # (collar bands: flush on the sides under the plate, three sections each, gaps on the grid lines)
    bm = bmesh.new()
    for sx in (-1, 1):
        for (a, b) in ((Y0, -1.27), (-1.23, 0.23), (0.27, Y1)):
            x0, x1 = (H - 0.22, H) if sx > 0 else (-H, -H + 0.22)
            box(bm, (x0, a, 0.25), (x1, b, 1.06), 0.04)
    rb.obj(name + "_Collar", bm, [rb.BODY], col)
    # (dark structure under the mount: a spine the plate and collars sit on)
    bm = bmesh.new(); box(bm, (-1.05, Y0 + 0.1, 0.55), (1.05, Y1 - 0.1, 1.04), 0.08)
    rb.obj(name + "_MountSpine", bm, [rb.DARK], col)

    # ═══ 2. the large primitives: drum (three sections), gearbox stack, nozzle, spinner ═══
    bm = bmesh.new()
    for (a, b) in ((-2.90, -1.30), (-1.20, 0.25), (0.35, 1.70)):
        cyl(bm, a, b, 1.10, 64, ch=0.08)
    rb.obj(name + "_Drum", bm, [rb.BODY], col)
    bm = bmesh.new()
    for (a, b) in ((-1.32, -1.18), (0.23, 0.37)): ring(bm, a, b, 1.04, 0.80, 64)    # (dark bands in the seams)
    cyl(bm, -3.00, 1.80, 0.98, 64)                                                    # (the dark core the sections sit on)
    rb.obj(name + "_DrumCore", bm, [rb.DARK], col)
    # (the gearbox: stepped cylinders down to the spinner, a copper band, a bearing ring)
    bm = bmesh.new()
    cyl(bm, 1.70, 2.45, 0.90, 64, ch=0.07); cyl(bm, 2.40, 3.25, 0.72, 64, ch=0.06)
    rb.obj(name + "_Gearbox", bm, [rb.DARK], col)
    bm = bmesh.new(); ring(bm, 2.62, 2.78, 0.76, 0.60, 64, ch=0.02); rb.obj(name + "_GearboxBand", bm, [COPPER], col)
    bm = bmesh.new(); ring(bm, 3.22, 3.40, 0.80, 0.50, 64, ch=0.03); rb.obj(name + "_Bearing", bm, [DULL], col)
    # (the spinner: a cone of straight segments, gunmetal)
    bm = bmesh.new(); rb.lathe(bm, [(3.40, 0.0), (3.40, 0.70), (3.55, 0.74), (4.30, 0.64), (4.95, 0.40), (5.35, 0.14), (5.45, 0.0)], 48)
    rb.obj(name + "_Spinner", bm, [rb.DARK], col, sharp=20.0)
    # (the rear nozzle: ribbed rings, a dark bore, an inner cone; its rim touches the back face)
    bm = bmesh.new()
    ring(bm, -3.75, -3.55, 1.00, 0.86, 64, ch=0.03)
    for y in (-3.45, -3.25, -3.05): ring(bm, y - 0.07, y + 0.07, 1.02, 0.86, 64, ch=0.02)
    ring(bm, -3.55, -2.85, 0.95, 0.86, 64)
    rb.lathe(bm, [(-3.65, 0.0), (-3.65, 0.10), (-3.20, 0.45), (-2.95, 0.45), (-2.95, 0.0)], 32)
    rb.obj(name + "_Nozzle", bm, [rb.DARK], col)
    bm = bmesh.new()
    for k in range(8):   # (a ring of brackets clamping the nozzle to the drum)
        before = set(bm.verts); box(bm, (-0.10, -2.98, 0.96), (0.10, -2.70, 1.14), 0.02)
        R = Matrix.Rotation(2 * math.pi * k / 8 + math.pi / 8, 3, "Y")
        for v in set(bm.verts) - before: v.co = R @ v.co
    rb.obj(name + "_NozzleClamps", bm, [DULL], col)
    # (the blades: the generated prop, on the spinner)
    bm = bmesh.new()
    for b in range(3): rb.blade(bm, 0.45, 6.1, 1.20, 52, 20, 2 * math.pi * b / 3, "prop", sweep=0.2)
    for v in bm.verts: v.co.y += 3.95
    rb.obj(name + "_Blades", bm, [rb.BLADE, rb.STRIPE], col, bevel=None, sharp=80.0)

    # ═══ 3. secondary pieces: chin intake, exhaust stubs, pipe runs ═══
    bm = bmesh.new(); box(bm, (-0.62, 0.40, -1.22), (0.62, 3.05, -0.70), 0.10)
    rb.obj(name + "_Intake", bm, [rb.BODY], col)
    bm = bmesh.new(); box(bm, (-0.50, 2.98, -1.12), (0.50, 3.08, -0.80), 0.01)
    rb.obj(name + "_IntakeMouth", bm, [rb.DARK], col)
    bm = bmesh.new()
    for i in range(6): x = -0.40 + i * 0.16; box(bm, (x - 0.03, 3.00, -1.10), (x + 0.03, 3.10, -0.82), 0.008)
    rb.obj(name + "_IntakeSlats", bm, [DULL], col)
    bm = bmesh.new(); bands = bmesh.new()
    for sx in (-1, 1):   # (exhaust stubs: straight tubes out of the sides, just inside the box)
        for z in (0.05, -0.40):
            rd.oriented_lathe(bm, [(0.0, 0.0), (0.0, 0.17), (0.32, 0.17), (0.32, 0.12), (0.10, 0.12), (0.10, 0.0)], 32, Vector((sx * 0.88, 1.95, z)), Vector((sx, 0, 0)))
            rd.oriented_lathe(bands, [(0.0, 0.19), (0.06, 0.19)], 32, Vector((sx * 0.92, 1.95, z)), Vector((sx, 0, 0)))
    rb.obj(name + "_Exhaust", bm, [rb.DARK], col); rb.obj(name + "_ExhaustBands", bands, [COPPER], col)
    bm = bmesh.new(); cl = bmesh.new()
    for sx in (-1, 1):   # (two straight pipe runs low on each side, clamped)
        for z in (-0.55, -0.75):
            rd.oriented_lathe(bm, [(0.0, 0.0), (0.0, 0.06), (4.0, 0.06), (4.0, 0.0)], 20, Vector((sx * 1.02, -2.60, z)), U)
        for y in (-2.2, -0.9, 0.6):
            box(cl, (sx * 1.02 - 0.09, y - 0.06, -0.86), (sx * 1.02 + 0.09, y + 0.06, -0.44), 0.015)
    rb.obj(name + "_Pipes", bm, [DULL], col); rb.obj(name + "_PipeClamps", cl, [rb.DARK], col)

    # ═══ 4. decals: restrained, at the seams and on the mount ═══
    top = Vector((0, 0, H))
    for sx in (-1, 1):
        for y in (Y0 + 0.25, Y1 - 0.25): D.stamp(22, top + X * (sx * (H - 0.25)) + U * y, U, X, Z, 0.08)
    D.stamp(695, top + Z * -0.04 + U * (-1.5), U, X, Z, 1.0)
    D.stamp(3323, top + Z * -0.04 + U * 0.75, U, X, Z, 0.7)
    for sx in (-1, 1):
        N = Vector((sx, 0, 0))
        for (a, b) in ((Y0, -1.27), (-1.23, 0.23), (0.27, Y1)):
            D.strip(N * H + U * ((a + b) / 2) + Z * 0.38, U, Vector((0, 0, 1)) * sx, N, (b - a) - 0.2, 0.08, "dark")
        D.stamp(1359, N * 0.95 + U * 2.1 + Z * 0.62, U, Vector((0, 0, 1)) * sx, N, 0.28)
    D.finish(name + "_Decals", col)


def main():
    rb.reset()
    for m in ("AtlasDecal_Trims03", "AtlasDecal_Parts02", "Copper", "MetalDull"): rb.mat(m, (0.5, 0.5, 0.5))
    rd.STAMP.update({s["id"]: s for s in json.load(open(STAMPS))["stamps"]})
    col = rb.collection("Turboprop_250_v3")
    build("TPv3", col)
    rb.occupancy("TPv3_Occupancy", [((-0.5, -1.5, -0.5), (0.5, 1.5, 0.5)), ((-2.5, 1.5, -2.5), (2.5, 2.5, 2.5))], col, 2.5)
    if CONCEPT and os.path.exists(CONCEPT):
        img = bpy.data.images.load(CONCEPT)
        e = bpy.data.objects.new("Concept_Reference", None); e.empty_display_type = "IMAGE"; e.data = img
        e.empty_display_size = 12; e.location = (-7, 0, 0); e.rotation_euler = (math.pi / 2, 0, -math.pi / 2)
        bpy.context.scene.collection.objects.link(e)
    rb.finish(OUT)
    for m in bpy.data.materials:
        if m.name.startswith("AtlasDecal") and hasattr(m, "blend_method"): m.blend_method = "HASHED"
    bpy.ops.wm.save_mainfile()
    print("TPV3 saved", OUT, sum(len(o.data.polygons) for o in bpy.data.objects if o.type == "MESH"), "faces")


main()
