"""ROTORCRAFT BLOCKOUTS (2026-10-16, the user: "beef them up, more angular: a 3-blade turboprop, a 3-blade helicopter and a
3-blade fan ... the blade in the bounding area"): basic angular blockouts with their occupied cells (wire), to react to
before kitbashing. One .blend, each type in its own collection beside the others (Blender Y = the block's Forward / the
thrust, Z up):

  Turboprop_250   cell 2.5: nacelle 3x1x1 cells, prop disc 5x5x1 in front of it (T-shaped occupancy), 3 blades
  Helicopter_250  cell 2.5: mast 1x1x2, rotor disc 9x9x1 on top, 3 blades, swashplate + hub
  Fan_250         2.5 face x 1.5 thick: an octagonal duct, 3 wide blades, 3 stators
  Fan_050         0.5 face x 0.25 thick: the drone fan (the same, scaled flat)
Blades: faceted (a 6-point angular section, flat-shaded), wide chord, thick root, twisted to a pitch; hubs hexagonal.

  blender -b --factory-startup -P rotorcraft_blockouts.py -- <out.blend>
"""
import bpy, bmesh, math, os, sys
from mathutils import Matrix, Vector

sc = BODY = DARK = BLADE = TIP = METAL = STRIPE = None


def mat(name, rgb):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name); m.diffuse_color = (*rgb, 1.0); return m


def reset():
    """An empty scene and the placeholder materials (swapped for the library's by use_library_materials at the end). The
    detail pass (rotorcraft_detail.py) imports this module and calls reset() too."""
    global sc, BODY, DARK, BLADE, TIP, METAL, STRIPE
    bpy.ops.wm.read_factory_settings(use_empty=True)
    sc = bpy.context.scene
    BODY, DARK, BLADE, TIP, METAL = (mat("PaintedMetalColorable", (0.62, 0.64, 0.68)), mat("PaintedMetalDarker", (0.24, 0.25, 0.28)),
                                     mat("PaintedMetalDarker", (0.24, 0.25, 0.28)), mat("AtlasDecal_Trims01", (0.85, 0.65, 0.1)), mat("BrushedMetal", (0.55, 0.56, 0.58)))
    STRIPE = mat("WarningStripes", (0.92, 0.72, 0.08))   # (the user: "a stripe texture on all blade tips" - Keen's hazard stripe)


def finish(out):
    """Library materials in, texture paths absolute, saved."""
    use_library_materials()
    for im in bpy.data.images:   # (the library's texture paths are relative to it: made absolute so they resolve from here)
        if im.filepath.startswith("//"): im.filepath = bpy.path.abspath(im.filepath, library=None, start=os.path.dirname(LIBRARY))
    bpy.ops.wm.save_as_mainfile(filepath=out)


UV_M = 2.5
LIBRARY = r"D:/Blender Brojects/SE2/AssetLibrary/SE2Decal&Material.blend"


def use_library_materials():
    """The placeholders (a viewport colour only) swapped for Keen's real materials from the SE2 asset library, by name
    (the user, 2026-10-16: "materials are not rendering") - appended, so the file stands alone."""
    names = sorted({m.name for m in bpy.data.materials})
    with bpy.data.libraries.load(LIBRARY, link=False) as (src, dst):
        dst.materials = [n for n in names if n in src.materials]
    for real in dst.materials:
        if real is None: continue
        local = next((m for m in bpy.data.materials if m.name == real.name.rsplit(".", 1)[0] and m is not real and not m.library and m.node_tree is None or (m.name == real.name.rsplit(".", 1)[0] and m is not real and not m.use_nodes)), None)
        base = real.name.rsplit(".", 1)[0] if real.name.endswith(".001") else real.name
        for m in list(bpy.data.materials):
            if m is not real and m.name == base:
                m.user_remap(real); bpy.data.materials.remove(m)
        real.name = base
        print("MATERIAL", base, "from the library")


def obj(name, bm, mats, col, bevel="auto", sharp=30.0):
    """A mesh object (2026-10-16, the user: "get rid of fillets and go to chamfers ... look at a conveyor tube"): every edge
    sharper than 'sharp' degrees gets a flat 45 deg micro chamfer (one segment) as Keen's parts have (measured on the
    vanilla conveyors: ~0.01 m micro chamfers besides the ~0.1 m main ones, which chamfer_box makes); bevel "auto" =
    0.35 % of the part's size, 0.4 .. 2.5 cm; None = none. Shaded smooth with the chamfer edges sharp: flat facets."""
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-6)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    if bevel is not None and bm.verts:
        if bevel == "auto":
            lo = Vector(tuple(min(v.co[i] for v in bm.verts) for i in range(3))); hi = Vector(tuple(max(v.co[i] for v in bm.verts) for i in range(3)))
            bevel = min(0.025, max(0.004, 0.0035 * (hi - lo).length))
        edges = [e for e in bm.edges if e.is_manifold and e.calc_face_angle(0.0) > math.radians(sharp)]
        if edges: bmesh.ops.bevel(bm, geom=edges, offset=bevel, offset_type="WIDTH", segments=1, affect="EDGES", clamp_overlap=True)
    uv = bm.loops.layers.uv.active
    if uv is None:   # (box-projected UVs, 1 UV unit = UV_M metres, so the library's textures map at an even density)
        uv = bm.loops.layers.uv.new("UVMap")
        for fc in bm.faces:
            n = fc.normal; ax = max(range(3), key=lambda i: abs(n[i])); a_, b_ = [i for i in range(3) if i != ax]
            for l in fc.loops: l[uv].uv = (l.vert.co[a_] / UV_M, l.vert.co[b_] / UV_M)
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    for m in mats: me.materials.append(m)
    me.shade_smooth(); me.set_sharp_from_angle(angle=math.radians(sharp))
    o = bpy.data.objects.new(name, me); col.objects.link(o); return o


def chamfer_box(bm, lo, hi, c):
    """A box with its edges chamfered by c (angular, not round)."""
    r = bmesh.ops.create_cube(bm, size=1.0)
    vs = r["verts"]
    for v in vs: v.co = Vector(tuple((lo[i] + hi[i]) / 2 + v.co[i] * (hi[i] - lo[i]) for i in range(3)))
    bmesh.ops.bevel(bm, geom=list({e for v in vs for e in v.link_edges}), offset=c, segments=1, affect="EDGES")


def prism(bm, sides, r, y0, y1, rot=0.0, axis="Y", r1=None):
    """A regular polygonal prism (or frustum) along an axis: a hexagonal hub, an octagonal ring part."""
    r1 = r if r1 is None else r1
    ring = lambda y, rr: [bm.verts.new(Vector((rr * math.cos(rot + 2 * math.pi * k / sides), y, rr * math.sin(rot + 2 * math.pi * k / sides)))) for k in range(sides)]
    a, b = ring(y0, r), ring(y1, r1)
    for k in range(sides): bm.faces.new((a[k], a[(k + 1) % sides], b[(k + 1) % sides], b[k]))
    bm.faces.new(a[::-1]); bm.faces.new(b)
    if axis == "Z":
        for v in a + b: v.co = Vector((v.co.x, -v.co.z, v.co.y))


def lathe(bm, prof, seg=64, axis="Y"):
    """A round body of revolution (the user, 2026-10-16: "non polygon noses"): prof = [(along, radius)], a radius 0 is a
    point (a nose / tail tip); closed with caps where the profile ends at a radius."""
    rings = []
    for (y, r) in prof:
        rings.append([bm.verts.new(Vector((0, y, 0)))] if r <= 0 else
                     [bm.verts.new(Vector((r * math.cos(2 * math.pi * j / seg), y, r * math.sin(2 * math.pi * j / seg)))) for j in range(seg)])
    for A, B in zip(rings, rings[1:]):
        for j in range(seg):
            if len(A) == 1 and len(B) > 1: bm.faces.new((A[0], B[(j + 1) % seg], B[j]))
            elif len(B) == 1 and len(A) > 1: bm.faces.new((A[j], A[(j + 1) % seg], B[0]))
            elif len(A) > 1: bm.faces.new((A[j], A[(j + 1) % seg], B[(j + 1) % seg], B[j]))
    if len(rings[0]) > 1: bm.faces.new(rings[0][::-1])
    if len(rings[-1]) > 1: bm.faces.new(rings[-1])
    if axis == "Z":
        for ring in rings:
            for v in ring: v.co = Vector((v.co.x, -v.co.z, v.co.y))


def ogive(y0, length, r, n=14, tip=0.0):
    """A nose profile from (y0, r) forward to a point (or a rounded tip of radius tip x r), tangent-ogive-ish."""
    return [(y0 + length * k / n, r * math.sqrt(max(0.0, 1 - (k / n) ** 2)) ** 0.85 if k < n else r * tip) for k in range(n + 1)]


def ring_prism(bm, sides, ro, ri, y0, y1, rot=0.0):
    """A polygonal ring (a duct): outer and inner walls and the two end faces, built directly (no boolean)."""
    P = lambda r, y, k: bm.verts.new(Vector((r * math.cos(rot + 2 * math.pi * k / sides), y, r * math.sin(rot + 2 * math.pi * k / sides))))
    o0, o1 = [P(ro, y0, k) for k in range(sides)], [P(ro, y1, k) for k in range(sides)]
    i0, i1 = [P(ri, y0, k) for k in range(sides)], [P(ri, y1, k) for k in range(sides)]
    for k in range(sides):
        n = (k + 1) % sides
        bm.faces.new((o0[k], o0[n], o1[n], o1[k])); bm.faces.new((i0[k], i1[k], i1[n], i0[n]))
        bm.faces.new((o0[k], i0[k], i0[n], o0[n])); bm.faces.new((o1[k], o1[n], i1[n], i1[k]))


def outline_ring(bm, outer, inner, y0, y1, lip=0.10, steps=10):
    """A duct from two outlines with the same point count (outer housing, inner bore; (x, z) pairs, the same angles): the
    bore flares into a bell-mouth over the front (+Y, the intake) 30 % - by 'lip' of its radius - and a little at the exit."""
    n = len(outer)
    P = lambda p, y, sc=1.0: bm.verts.new(Vector((p[0] * sc, y, p[1] * sc)))
    o0, o1 = [P(p, y0) for p in outer], [P(p, y1) for p in outer]
    L = y1 - y0
    prof = [(0.0, 1 + 0.03)] + [(0.10 * u / 3, 1 + 0.03 * (1 - u / 3) ** 2) for u in range(1, 4)] + [(0.70, 1.0)] +            [(0.70 + 0.30 * u / steps, 1 + lip * (u / steps) ** 2) for u in range(1, steps + 1)]
    rings = [[P(p, y0 + L * t, sc) for p in inner] for (t, sc) in prof]
    for k in range(n):
        m = (k + 1) % n
        bm.faces.new((o0[k], o0[m], o1[m], o1[k]))
        for a, b in zip(rings, rings[1:]): bm.faces.new((a[k], b[k], b[m], a[m]))
        bm.faces.new((o0[k], rings[0][k], rings[0][m], o0[m])); bm.faces.new((o1[k], o1[m], rings[-1][m], rings[-1][k]))


def housing(shape, F, n=32):
    """The duct's outer outline (n points from angle 0, counter-clockwise, in x / z): 'oct' an octagon, 'square' the full
    square face (its corners chamfered a little), 'D' flat-cornered below and round above (it sits on its flat side)."""
    h = 0.5 * F * 0.999; pts = []
    for k in range(n):
        a = 2 * math.pi * k / n; c, sn = math.cos(a), math.sin(a)
        if shape == "oct":
            r = h / math.cos(((a + math.pi / 8) % (math.pi / 4)) - math.pi / 8)
        elif shape == "square" or (shape == "D" and sn < 0):
            r = h / max(abs(c), abs(sn))
            r = min(r, (2 - 0.16) * h / (abs(c) + abs(sn)))   # (corners: a flat 45 deg chamfer, 8 % of the face each way - no fillet)
        else:
            r = h                                     # (the D's round top)
        pts.append((r * c, r * sn))
    return pts


# blade planforms: (span fraction, chord / max chord, thickness / chord, LE offset / max chord - + forward)
PLANS = {
    # a prop: a thick shank, a broad blade at 30-50 %, tapering to a raked angular tip
    "prop": [(0.00, 0.34, 0.50, 0.00), (0.10, 0.38, 0.40, 0.00), (0.19, 1.00, 0.13, 0.14), (0.62, 0.86, 0.08, 0.05),
             (0.88, 0.62, 0.065, -0.10), (1.00, 0.22, 0.06, -0.42)],      # (few stations: straight edges, crisp corners)
    # a rotor: a shank, then a constant chord, and a swept angular (BERP-ish) tip
    "rotor": [(0.00, 0.45, 0.50, 0.00), (0.06, 0.55, 0.35, 0.00), (0.14, 1.00, 0.12, 0.00), (0.50, 1.00, 0.11, 0.00), (0.84, 1.00, 0.10, 0.00),
              (0.88, 1.12, 0.10, 0.12), (0.95, 1.05, 0.09, -0.15), (1.00, 0.55, 0.08, -0.55)],
    # a fan: wide, little taper, a square tip running close to the duct
    "fan": [(0.00, 0.50, 0.30, 0.00), (0.14, 0.72, 0.15, 0.04), (0.86, 1.05, 0.09, 0.03), (1.00, 1.00, 0.08, -0.08)],   # (widening to the tip)
}
# the section: a sharp faceted airfoil (x along the chord from the LE, y up; thickness 1): a ridge at 30 %, a bevelled TE, a flat-ish bottom
SECTION = [(0.00, 0.00), (0.03, 0.36), (0.12, 0.56), (0.30, 0.64), (0.55, 0.56), (0.80, 0.34), (1.00, 0.12),
           (1.00, -0.10), (0.80, -0.26), (0.55, -0.40), (0.30, -0.46), (0.12, -0.40), (0.03, -0.27)]
# (the user, 2026-10-16: "everything needs to be tastefully thick at section, nothing flat": both faces bulge, a blunt TE,
#  every planform's thickness x THICK and never under MIN_TC of the chord)
THICK, MIN_TC = 1.8, 0.15
TIP_BAND = 0.90   # (the tip stripe from 90 % of the span out)


def blade(bm, R0, R1, cmax, beta_root, beta_tip, angle, plan="prop", sweep=0.0, droop=0.0):
    """One angular blade along +Z (turned by angle about Y): its planform's stations, the faceted SECTION, the blade
    angle from beta_root (deg, at the shank) to beta_tip - interpolated in tan (as a constant-pitch prop's), no 90 deg brick."""
    rot = Matrix.Rotation(angle, 4, "Y"); rows = []
    tb = math.tan(math.radians(beta_tip)); tr = math.tan(math.radians(beta_root))
    keys = PLANS[plan]; stations = []
    for (a, b) in zip(keys, keys[1:]):
        nsub = max(2, int(round((b[0] - a[0]) * 40)))
        stations += [tuple(a[i] + (b[i] - a[i]) * j / nsub for i in range(4)) for j in range(nsub)]
    stations.append(keys[-1])
    if not any(abs(st[0] - TIP_BAND) < 1e-6 for st in stations):   # (a station where the stripe starts)
        i = next(i for i, st in enumerate(stations) if st[0] > TIP_BAND); a_, b_ = stations[i - 1], stations[i]; u_ = (TIP_BAND - a_[0]) / (b_[0] - a_[0])
        stations.insert(i, tuple(a_[k] + (b_[k] - a_[k]) * u_ for k in range(4)))
    for (f, cf, tf, le) in stations:
        r = R0 + (R1 - R0) * f
        c = cf * cmax; t = max(MIN_TC, min(0.6, tf * THICK)) * c
        beta = math.atan(tb + (tr - tb) * (1 - f) ** 2)
        ring = []
        for (x, y) in SECTION:
            px, py = (x - 0.35) * c - le * cmax, y * t          # (pivot at 35 % chord; the LE offset moves the whole section)
            qx = px * math.cos(beta) - py * math.sin(beta); qy = px * math.sin(beta) + py * math.cos(beta)
            ring.append(bm.verts.new(rot @ Vector((qx + sweep * f * f, qy - droop * f * f, r))))
        rows.append((ring, f, r))
    n = len(SECTION); uv = bm.loops.layers.uv.verify()
    def arc(ring):
        d = [0.0]
        for i in range(n): d.append(d[-1] + (ring[(i + 1) % n].co - ring[i].co).length)
        return d
    for k in range(len(rows) - 1):
        (A, fa, ra), (B, fb, rb) = rows[k], rows[k + 1]; da, db = arc(A), arc(B)
        for i in range(n):
            fc = bm.faces.new((A[i], A[(i + 1) % n], B[(i + 1) % n], B[i]))
            if fa >= TIP_BAND - 1e-6: fc.material_index = 1
            for l, (u, v) in zip(fc.loops, ((da[i], ra), (da[i + 1], ra), (db[i + 1], rb), (db[i], rb))): l[uv].uv = (u, v)
    bm.faces.new([v for v in rows[0][0][::-1]])
    cap = bm.faces.new(rows[-1][0]); cap.material_index = 1


def occupancy(name, boxes, col, cell):
    """The block's occupied cells as wire boxes (each box (lo, hi) in cells x cell)."""
    bm = bmesh.new()
    for lo, hi in boxes:
        r = bmesh.ops.create_cube(bm, size=1.0)
        for v in r["verts"]: v.co = Vector(tuple((lo[i] + hi[i]) / 2 * cell + v.co[i] * (hi[i] - lo[i]) * cell for i in range(3)))
    o = obj(name, bm, [DARK], col, bevel=None); o.display_type = "WIRE"; o["occupancy"] = True; return o


def collection(name, parent=None):
    c = bpy.data.collections.new(name); (parent or sc.collection).children.link(c); return c


def place(col, x, y=0.0):
    for o in col.objects:
        if o.parent is None: o.location.x += x; o.location.y += y


# ── Turboprop (cell s): nacelle 3x1x1 (y -1.5 .. 1.5 cells), prop disc 5x5x1 ahead (y 1.5 .. 2.5) ──
def turboprop(name, s, c):
    bm = bmesh.new(); chamfer_box(bm, (-0.5 * s, -1.5 * s, -0.5 * s), (0.5 * s, 1.35 * s, 0.5 * s), 0.04 * s)
    chamfer_box(bm, (-0.22 * s, -0.4 * s, -0.62 * s), (0.22 * s, 1.1 * s, -0.42 * s), 0.02 * s)   # (the intake under the nose)
    for sx in (-1, 1): chamfer_box(bm, (sx * 0.62 * s - 0.1 * s, -1.2 * s, 0.0), (sx * 0.62 * s + 0.1 * s, -0.6 * s, 0.25 * s), 0.012 * s)   # (exhaust stacks)
    obj(name + "_Nacelle", bm, [BODY], c)
    bm = bmesh.new(); lathe(bm, [(1.30 * s, 0.30 * s), (1.35 * s, 0.40 * s), (1.75 * s, 0.40 * s)] + ogive(1.75 * s, 0.75 * s, 0.40 * s)[1:])   # (a round hub + an ogive spinner)
    obj(name + "_Spinner", bm, [METAL], c)
    bm = bmesh.new()
    for b in range(3): blade(bm, 0.3 * s, 2.45 * s, 0.62 * s, 55, 22, 2 * math.pi * b / 3, "prop", sweep=0.10 * s)
    for v in bm.verts: v.co.y += 1.9 * s
    obj(name + "_Blades", bm, [BLADE, STRIPE], c, bevel=None, sharp=80.0)
    occupancy(name + "_Occupancy", [((-0.5, -1.5, -0.5), (0.5, 1.5, 0.5)), ((-2.5, 1.5, -2.5), (2.5, 2.5, 2.5))], c, s)


# ── Helicopter (cell s): mast 1x1x2 (z 0 .. 2 cells), rotor disc 9x9x1 on top (z 2 .. 3) ──
def helicopter(name, s, c):
    bm = bmesh.new(); chamfer_box(bm, (-0.5 * s, -0.5 * s, 0.0), (0.5 * s, 0.5 * s, 1.0 * s), 0.04 * s)   # (the gearbox)
    lathe(bm, [(0.98 * s, 0.30 * s), (1.05 * s, 0.22 * s), (2.2 * s, 0.18 * s)], axis="Z")   # (the mast, a flared foot)
    lathe(bm, [(1.52 * s, 0.30 * s), (1.56 * s, 0.44 * s), (1.70 * s, 0.44 * s), (1.76 * s, 0.30 * s)], axis="Z")   # (the swashplate: a round disc)
    obj(name + "_Mast", bm, [BODY], c)
    bm = bmesh.new(); lathe(bm, [(2.2 * s, 0.40 * s), (2.25 * s, 0.46 * s), (2.55 * s, 0.46 * s)] + ogive(2.55 * s, 0.35 * s, 0.46 * s, tip=0.15)[1:], axis="Z")   # (round hub + dome)
    for b in range(3):   # (blade grips: one per blade, turned about Z with it)
        before = set(bm.verts)
        chamfer_box(bm, (-0.12 * s, 0.35 * s, 2.25 * s), (0.12 * s, 0.85 * s, 2.55 * s), 0.012 * s)
        R = Matrix.Rotation(2 * math.pi * b / 3 + math.pi, 3, "Z")   # (+Y onto the blade's (sin a, -cos a))
        for v in set(bm.verts) - before: v.co = R @ v.co
    obj(name + "_Hub", bm, [METAL], c)
    bm = bmesh.new()
    for b in range(3): blade(bm, 0.45 * s, 4.45 * s, 0.42 * s, 14, 6, 2 * math.pi * b / 3, "rotor", droop=0.10 * s)
    for v in bm.verts: v.co = Vector((v.co.x, -v.co.z, v.co.y + 2.42 * s))   # (blades radial in the XY plane, at the hub's height)
    obj(name + "_Blades", bm, [BLADE, STRIPE], c, bevel=None, sharp=80.0)
    occupancy(name + "_Occupancy", [((-0.5, -0.5, 0.0), (0.5, 0.5, 2.0)), ((-4.5, -4.5, 2.0), (4.5, 4.5, 3.0))], c, s)


# ── Fans: an octagonal duct (face F, thickness T), 3 wide blades, 3 stators ──
def fan(name, F, T, col, shape="oct"):
    ri = 0.40 * F
    n = 128
    bore = [(ri * math.cos(2 * math.pi * k / n), ri * math.sin(2 * math.pi * k / n)) for k in range(n)]   # (a 128-gon bore: round)
    bm = bmesh.new(); outline_ring(bm, housing(shape, F, n), bore, -T / 2, T / 2)
    obj(name + "_Duct", bm, [BODY], col, sharp=20.0)   # (20: the corner chamfers' 22 deg kinks stay crisp)
    # the hub: a nacelle - spinner cone at the intake (+Y), a 24-sided body, a tail cone out of the exit (HD, 2026-10-16)
    rh = 0.13 * F; seg = 64
    bm = bmesh.new()
    lathe(bm, [(-T * 0.58, 0.0), (-T * 0.575, rh * 0.30), (-T * 0.30, rh)] + ogive(T * 0.22, T * 0.30, rh, n=20)[0:], seg)   # (tail cone, body, ogive nose)
    # stators: 3 thin vanes (an angular section, chord 0.35 T, cambered a little) from the hub to the bore, behind the blades
    for k in range(3):
        ang = 2 * math.pi * k / 3 + math.pi / 3
        sec = [(-0.5, 0.0), (-0.42, 0.14), (-0.2, 0.2), (0.2, 0.16), (0.5, 0.03), (0.5, -0.03), (0.2, -0.14), (-0.2, -0.18), (-0.42, -0.13)]   # (thick, both faces bulging)
        rows = []
        for rr in (rh * 0.9, ri * 1.02):
            rows.append([bm.verts.new(Matrix.Rotation(ang, 3, "Y") @ Vector((y_ * 0.35 * T * 0.45, -0.25 * T + x_ * 0.35 * T, rr))) for (x_, y_) in sec])
        n6 = len(sec)
        for i in range(n6): bm.faces.new((rows[0][i], rows[0][(i + 1) % n6], rows[1][(i + 1) % n6], rows[1][i]))
        bm.faces.new(rows[0][::-1]); bm.faces.new(rows[1])
    obj(name + "_Hub", bm, [METAL], col)
    bm = bmesh.new()
    for b in range(3): blade(bm, 0.11 * F, ri - 0.012 * F, 0.26 * F, 50, 28, 2 * math.pi * b / 3, "fan", sweep=0.03 * F)
    for v in bm.verts: v.co.y += 0.15 * T
    obj(name + "_Blades", bm, [BLADE, STRIPE], col, bevel=None, sharp=80.0)
    occupancy(name + "_Occupancy", [((-0.5, -0.5 * T / F, -0.5), (0.5, 0.5 * T / F, 0.5))], col, F)

# every size (the user, 2026-10-16): turboprop 0.5 / 1.5 / 2.5 cells, helicopter 1.5 / 2.5, fans 0.5 x 0.25, 1.5 x 0.5,
# 2.5 x 1.5, 7.5 x 2.5 (face x thickness), each fan octagonal, square and D (as SE1's flat atmospheric thrusters).
# One row per type (rows along -Y), sizes left to right; each size its own collection under its type's.
def main():
    OUT = sys.argv[sys.argv.index("--") + 1]
    reset()
    row = 0.0
    for kind, sizes, build, width in (("Turboprop", (0.5, 1.5, 2.5), turboprop, lambda s: 5 * s),
                                      ("Helicopter", (1.5, 2.5), helicopter, lambda s: 9 * s)):
        top = collection(kind); x = 0.0
        for s_ in sizes:
            tag = f"{kind}_{int(round(s_ * 100)):03d}"; c = collection(tag, top)
            build(tag, s_, c); x += width(s_) / 2; place(c, x, row); x += width(s_) / 2 + 4.0
        row -= 30.0 if kind == "Turboprop" else 40.0
    top = collection("Fans")
    for shape, label in (("oct", "Oct"), ("square", "Square"), ("D", "D")):
        x = 0.0
        for F, T in ((0.5, 0.25), (1.5, 0.5), (2.5, 1.5), (7.5, 2.5)):
            tag = f"Fan{label}_{int(round(F * 100)):03d}"; c = collection(tag, top)
            fan(tag, F, T, c, shape); x += F / 2; place(c, x, row); x += F / 2 + 3.0
        row -= 12.0
    finish(OUT)
    print("ROTORCRAFT saved", OUT)


if __name__ == "__main__":
    main()

