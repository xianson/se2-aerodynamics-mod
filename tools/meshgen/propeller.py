"""PROPELLERS, generated (2026-10-16, the user: "need better props to kitbash ... or can we generate some?"): parametric
props for the turboprop's kitbash - real NACA 4-digit sections lofted root to tip, twist from the prop's pitch (each
section at atan(P / 2 pi r) less a little incidence), tapered chord, optional scimitar sweep, round or square tips, a
cuffed root and an ogive spinner. Each prop is one object group in a "Props (generated)" collection: its spin axis is
Blender Y (the block's Forward), centred on the origin of its group, so it drops onto the turboprop's spin axis.

  blender -b <file.blend> -P propeller.py -- [diameter_m=2.46] [--at x y z]     appends every STYLE beside the model; saves

Materials (SE2 library names): blades PaintedMetalDarker, tips PaintedMetalColorable (paintable stripe), spinner
PaintedMetalColorable, hub BrushedMetal.
"""
import bpy, bmesh, math, sys
from mathutils import Matrix, Vector

args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
D = float(args[0]) if args and not args[0].startswith("--") else 2.46
AT = Vector((0, 0, 0))
if "--at" in args:
    i = args.index("--at"); AT = Vector(tuple(float(v) for v in args[i + 1:i + 4]))

# blades, pitch / D, chord at root / mid / tip (x R), sweep (tip offset x R, back against the rotation), tip shape, spinner
STYLES = {
    "Prop_Classic3":   dict(n=3, pitch=0.9, chord=(0.16, 0.20, 0.13), sweep=0.0,  tip="round",  spinner=(0.16, 0.30), camber=2),
    "Prop_Paddle4":    dict(n=4, pitch=1.0, chord=(0.18, 0.26, 0.22), sweep=0.0,  tip="square", spinner=(0.18, 0.28), camber=2),
    "Prop_Turboprop4": dict(n=4, pitch=0.85, chord=(0.14, 0.17, 0.09), sweep=0.08, tip="round",  spinner=(0.17, 0.42), camber=3),
    "Prop_Scimitar6":  dict(n=6, pitch=0.8, chord=(0.12, 0.16, 0.07), sweep=0.30, tip="round",  spinner=(0.19, 0.50), camber=3),
    "Prop_Propfan8":   dict(n=8, pitch=0.75, chord=(0.11, 0.15, 0.06), sweep=0.45, tip="round",  spinner=(0.22, 0.60), camber=4),
    # futuristic (the user: "will also want them to be futuristic"): a tip ring tying the blades, a ducted fan in a shroud
    "Prop_Ring6":      dict(n=6, pitch=0.8, chord=(0.12, 0.15, 0.11), sweep=0.25, tip="square", spinner=(0.20, 0.55), camber=3, ring=True),
    "Prop_Ducted12":   dict(n=12, pitch=0.7, chord=(0.10, 0.11, 0.10), sweep=0.10, tip="square", spinner=(0.30, 0.45), camber=3, duct=True, span=0.9),
}
SECTIONS, CHORD_PTS = 22, 24
R = D / 2


def mat(name, rgb):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name); m.diffuse_color = (*rgb, 1.0); return m


def naca(t, m, p=0.4, n=CHORD_PTS):
    """A NACA 4-digit section (chord 1, from the leading edge at x 0): closed loop, upper then lower, 2n points."""
    pts_u, pts_l = [], []
    for i in range(n):
        x = 0.5 * (1 - math.cos(math.pi * i / (n - 1)))   # (cosine spacing: rounder nose)
        yt = 5 * t * (0.2969 * math.sqrt(x) - 0.1260 * x - 0.3516 * x * x + 0.2843 * x ** 3 - 0.1036 * x ** 4)
        if x < p: yc, dy = m / p ** 2 * (2 * p * x - x * x), 2 * m / p ** 2 * (p - x)
        else: yc, dy = m / (1 - p) ** 2 * ((1 - 2 * p) + 2 * p * x - x * x), 2 * m / (1 - p) ** 2 * (p - x)
        th = math.atan(dy)
        pts_u.append((x - yt * math.sin(th), yc + yt * math.cos(th)))
        pts_l.append((x + yt * math.sin(th), yc - yt * math.cos(th)))
    return pts_u + pts_l[::-1][1:-1]


def chord_at(s, c):
    """Chord (x R) at s = r / R from 0.2 to 1: root -> mid (s 0.6) -> tip, smooth."""
    r0, rm, rt = c
    if s < 0.6: f = (s - 0.2) / 0.4; return r0 + (rm - r0) * (3 * f * f - 2 * f ** 3)
    f = (s - 0.6) / 0.4; return rm + (rt - rm) * (3 * f * f - 2 * f ** 3)


def blade(bm, uv, st, angle, mat_idx_tip):
    """One blade, radial along +Z turned by 'angle' about Y; sections face the rotation (Y forward)."""
    rows = []
    s0 = 0.20
    rot = Matrix.Rotation(angle, 4, "Y")
    for k in range(SECTIONS):
        s = s0 + (1 - s0) * (k / (SECTIONS - 1)) ** 0.85
        r = s * R * st.get("span", 1.0)
        c = chord_at(s, st["chord"]) * R
        if st["tip"] == "round" and s > 0.9: c *= math.sqrt(max(0.05, 1 - ((s - 0.9) / 0.1) ** 2))
        t = 0.14 - 0.08 * s                                   # (thick root, thin tip)
        twist = math.atan2(st["pitch"] * D, 2 * math.pi * r) - math.radians(2)
        sweep = st["sweep"] * R * s ** 2.5                     # (scimitar: back, against the rotation +X here)
        sec = naca(t, st["camber"] / 100.0)
        ring = []
        for (x, y) in sec:
            # chord along the rotation (X), thickness along -Y (forward face is the suction side), turned by the twist
            px, py = (x - 0.3) * c, -y * c
            qx = px * math.cos(twist) - py * math.sin(twist); qy = px * math.sin(twist) + py * math.cos(twist)
            ring.append(bm.verts.new(rot @ Vector((qx + sweep, qy, r))))
        rows.append((ring, s))
    n = len(rows[0][0])
    for k in range(len(rows) - 1):
        a, sa = rows[k]; b, sb = rows[k + 1]
        for i in range(n):
            f = bm.faces.new((a[i], a[(i + 1) % n], b[(i + 1) % n], b[i]))
            f.smooth = True
            if sb > 0.9: f.material_index = mat_idx_tip
            for l, (u, v) in zip(f.loops, ((i / n, sa), ((i + 1) / n, sa), ((i + 1) / n, sb), (i / n, sb))): l[uv].uv = (u, v)
    for ring, _ in (rows[0], rows[-1]):
        f = bm.faces.new(ring if ring is rows[-1][0] else ring[::-1]); f.material_index = mat_idx_tip if ring is rows[-1][0] else 0


def revolve(bm, uv, prof, seg=32, mat_idx=0, y0=0.0):
    """A body of revolution about Y from (y, radius) profile points."""
    rings = []
    for (y, r) in prof:
        rings.append([bm.verts.new(Vector((r * math.cos(2 * math.pi * j / seg), y0 + y, r * math.sin(2 * math.pi * j / seg)))) for j in range(seg)])
    for k in range(len(rings) - 1):
        for j in range(seg):
            a, b = rings[k], rings[k + 1]
            f = bm.faces.new((a[j], b[j], b[(j + 1) % seg], a[(j + 1) % seg])); f.smooth = True; f.material_index = mat_idx
            for l, (u, v) in zip(f.loops, ((j / seg, k), (j / seg, k + 1), ((j + 1) / seg, k + 1), ((j + 1) / seg, k))): l[uv].uv = (u, v / len(rings))
    f = bm.faces.new(rings[0][::-1]); f.material_index = mat_idx


def build(name, st, col, x):
    blade_m, tip_m = mat("PaintedMetalDarker", (0.2, 0.21, 0.23)), mat("PaintedMetalColorable", (0.75, 0.7, 0.2))
    spin_m, hub_m = mat("PaintedMetalColorable", (0.75, 0.7, 0.2)), mat("BrushedMetal", (0.6, 0.6, 0.62))
    # blades
    bm = bmesh.new(); uv = bm.loops.layers.uv.new("UVMap")
    for b in range(st["n"]): blade(bm, uv, st, 2 * math.pi * b / st["n"], 1)
    me = bpy.data.meshes.new(name + "_Blades"); bm.to_mesh(me); bm.free()
    me.materials.append(blade_m); me.materials.append(tip_m)
    blades = bpy.data.objects.new(name + "_Blades", me); col.objects.link(blades)
    # spinner (an ogive forward of the hub) and the hub / cuffs ring
    rs, ls = st["spinner"][0] * R, st["spinner"][1] * R
    prof = [(0.0, rs)] + [(ls * k / 12, rs * math.sqrt(max(0.0, 1 - (k / 12) ** 2)) ** 0.9) for k in range(1, 12)] + [(ls * 0.995, rs * 0.04)]
    bm = bmesh.new(); uv = bm.loops.layers.uv.new("UVMap"); revolve(bm, uv, prof, 40, 0, y0=0.06 * R)
    me = bpy.data.meshes.new(name + "_Spinner"); bm.to_mesh(me); bm.free(); me.materials.append(spin_m)
    spinner = bpy.data.objects.new(name + "_Spinner", me); col.objects.link(spinner)
    bm = bmesh.new(); uv = bm.loops.layers.uv.new("UVMap")
    revolve(bm, uv, [(-0.18 * R, rs * 0.98), (0.06 * R, rs * 1.02)], 40, 0)
    me = bpy.data.meshes.new(name + "_Hub"); bm.to_mesh(me); bm.free(); me.materials.append(hub_m)
    hub = bpy.data.objects.new(name + "_Hub", me); col.objects.link(hub)
    parts = [blades, spinner, hub]
    ring_m = mat("PaintedMetalColorable", (0.75, 0.7, 0.2))
    if st.get("ring"):   # a thin tip ring tying the blades (its section a flattened circle, chord-wide along Y)
        bm = bmesh.new(); uv = bm.loops.layers.uv.new("UVMap"); seg, sides = 96, 12
        rr, ch, th = R * 0.985, 0.10 * R, 0.022 * R
        rings = [[bm.verts.new(Vector(((rr + th * math.cos(2 * math.pi * q / sides)) * math.cos(2 * math.pi * j / seg), ch * 0.5 * math.sin(2 * math.pi * q / sides),
                                       (rr + th * math.cos(2 * math.pi * q / sides)) * math.sin(2 * math.pi * j / seg)))) for q in range(sides)] for j in range(seg)]
        for j in range(seg):
            for q in range(sides):
                a, b = rings[j], rings[(j + 1) % seg]
                f = bm.faces.new((a[q], a[(q + 1) % sides], b[(q + 1) % sides], b[q])); f.smooth = True
        me = bpy.data.meshes.new(name + "_Ring"); bm.to_mesh(me); bm.free(); me.materials.append(ring_m)
        o = bpy.data.objects.new(name + "_Ring", me); col.objects.link(o); parts.append(o)
    if st.get("duct"):   # a shroud around the disc (fixed: it belongs to the body when kitbashed - a separate object)
        bm = bmesh.new(); uv = bm.loops.layers.uv.new("UVMap")
        ri, ln = R * 0.92, 0.55 * R
        outer = [(-ln * 0.6, ri * 1.10), (-ln * 0.2, ri * 1.12), (ln * 0.4, ri * 1.08)]
        inner = [(ln * 0.4, ri * 1.02), (0.0, ri * 0.995), (-ln * 0.6, ri * 1.03)]
        revolve(bm, uv, outer + inner + [outer[0]], 64, 0)
        me = bpy.data.meshes.new(name + "_Duct"); bm.to_mesh(me); bm.free(); me.materials.append(mat("PaintedMetalDarker", (0.2, 0.21, 0.23)))
        o = bpy.data.objects.new(name + "_Duct", me); col.objects.link(o); parts.append(o)
    root = bpy.data.objects.new(name, None); root.empty_display_type = "PLAIN_AXES"; root.empty_display_size = R * 0.4; col.objects.link(root)
    for o in parts: o.parent = root
    root.location = AT + Vector((x, 0, 0))
    return root


sc = bpy.context.scene
col = bpy.data.collections.get("Props (generated)")
if col is None: col = bpy.data.collections.new("Props (generated)"); sc.collection.children.link(col)
for o in list(col.objects): bpy.data.objects.remove(o)
x0 = AT.x
for i, (name, st) in enumerate(STYLES.items()):
    build(name, st, col, x0 + i * (D + 0.8) - AT.x)
    print(f"PROP {name}: {st['n']} blades, D {D:.2f} m, pitch/D {st['pitch']}, sweep {st['sweep']}")
bpy.ops.wm.save_mainfile()
print("PROPS saved", bpy.data.filepath)
