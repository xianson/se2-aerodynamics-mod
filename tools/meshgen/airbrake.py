"""The AIRBRAKE block (2026-10-14, the user: "an airbrake block that deploys with the parking gear signal, just a flap that
bisects and expands up and down ... a 3x3x3 bounding box to prevent cheats, and maybe a structural truss in the
center"; a clamshell): a 3x3x3 block, large (2.5 m) and small (0.5 m), written ready to export (gear_export.py):

  Body_LOD0..3        a lattice truss along the hinge line at the front, across the whole span (the structure)
  LeafUp_LOD0..2      the upper leaf, its origin ON the hinge (+5 mm up); it turns about X, trailing edge up
  LeafDown_LOD0..2    the lower leaf, origin on the hinge (5 mm down); trailing edge down
  Fractured_LOD0..3 / Deformed_LOD0..3   the truss in two pieces, its halves (Fracture_01 / _02)
  Collision           <N>_Collision: the truss's box; mountpoints_<N>_L / _R / _F: slabs on the left, right and front
                      faces (it mounts by its span ends or its front)
  Collision_Fractured each half's box
Stowed, the leaves lie flat together through the middle of the box; open, they make a V: CHORD blocks long, each turned
OPEN_DEG - its trailing edge stays inside the box. Blender axes: X across (the hinge axis), Y forward (into the air:
the block's Forward), Z up; the box centred on the origin.

  blender -b --factory-startup -P airbrake.py -- <out_dir> [large small]     -> <out_dir>/Airbrake_<Size>.blend (+ .json)
"""
import bpy, bmesh, json, math, os, sys
from mathutils import Matrix, Vector

SIZES = {"large": 2.5, "small": 0.5}
N_BLOCKS = 3
TRUSS = 0.40          # the truss's square section (blocks)
CHORD = 2.10          # each leaf's length, hinge to trailing edge (blocks)
OPEN_DEG = 45.0       # each leaf fully open (a 90 deg V; 2.1 x sin 45 = 1.48 blocks: inside the 1.5 half-height)
LEAF_T = 0.06         # each leaf's thickness (blocks)
BAR = 0.06            # the truss members' section (blocks)
DISPLACE = dict(strength=0.075, size=0.25, depth=4)


def lib_mat(name, rgb):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name); m.diffuse_color = (*rgb, 1.0); return m


def box(bm, lo, hi, uv):
    """A box into bm, its faces UV'd from the coordinates (planar, per face)."""
    vs = [bm.verts.new((x, y, z)) for x in (lo[0], hi[0]) for y in (lo[1], hi[1]) for z in (lo[2], hi[2])]
    idx = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)]
    for f in idx:
        face = bm.faces.new([vs[i] for i in f])
        n = face.normal
        for l in face.loops:
            p = l.vert.co
            l[uv].uv = (p.y, p.z) if abs(n.x) > 0.5 else ((p.x, p.z) if abs(n.y) > 0.5 else (p.x, p.y))
    return vs


def beam(bm, a, b, s, uv):
    """A square bar of section s from a to b (any direction): a box along the segment."""
    a, b = Vector(a), Vector(b); d = b - a; L = d.length
    z = d.normalized(); x = z.orthogonal().normalized(); y = z.cross(x)
    M = Matrix((x, y, z)).transposed()
    vs = box(bm, (-s / 2, -s / 2, 0), (s / 2, s / 2, L), uv)
    for v in vs: v.co = a + M @ v.co
    return vs


def mesh(name, build, mat, coll):
    bm = bmesh.new(); uv = bm.loops.layers.uv.new("UVMap"); build(bm, uv)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free(); me.materials.append(mat)
    o = bpy.data.objects.new(name, me); coll.objects.link(o); return o


def coll(name, parent=None):
    c = bpy.data.collections.new(name); (parent or bpy.context.scene.collection).children.link(c); return c


def build(size):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    B = SIZES[size]; H = N_BLOCKS * B / 2; N = f"Airbrake_{size.capitalize()}"
    frame, panel = lib_mat("PaintedMetalDarker", (0.25, 0.26, 0.28)), lib_mat("PaintedMetalColorable", (0.55, 0.57, 0.6))
    ts, bar = TRUSS * B, BAR * B
    yc = H - ts / 2 - 0.02 * B                 # the truss's centre line (just inside the front face)
    yh = yc - ts / 2                           # the hinge: the truss's back face, mid-height
    span = 2 * H - 0.04 * B

    def truss(detail):
        def f(bm, uv):
            x0, x1 = -H, H
            cs = [(yc - ts / 2 + bar / 2, -ts / 2 + bar / 2), (yc + ts / 2 - bar / 2, -ts / 2 + bar / 2),
                  (yc + ts / 2 - bar / 2, ts / 2 - bar / 2), (yc - ts / 2 + bar / 2, ts / 2 - bar / 2)]
            if detail == 0:
                box(bm, (x0, yc - ts / 2, -ts / 2), (x1, yc + ts / 2, ts / 2), uv); return
            for (y, z) in cs: box(bm, (x0, y - bar / 2, z - bar / 2), (x1, y + bar / 2, z + bar / 2), uv)   # the longerons
            if detail < 2: return
            n = max(2, int(round(2 * H / ts)))
            for i in range(n):       # a zigzag on each of the four faces
                xa, xb = x0 + 2 * H * i / n, x0 + 2 * H * (i + 1) / n
                for k in range(4):
                    p, q = cs[k], cs[(k + 1) % 4]
                    a, b = (p, q) if i % 2 == 0 else (q, p)
                    beam(bm, (xa, *a), (xb, *b), bar * 0.8, uv)
            for xe in (x0 + bar / 2, x1 - bar / 2):   # the end frames
                for k in range(4): beam(bm, (xe, *cs[k]), (xe, *cs[(k + 1) % 4]), bar, uv)
        return f

    def leaf(sign):
        # (origin on the hinge: the leaf extends aft, -Y; up: z 0..t, down: -t..0)
        def f(bm, uv):
            t = LEAF_T * B
            box(bm, (-span / 2, -CHORD * B, 0 if sign > 0 else -t), (span / 2, 0, t if sign > 0 else 0), uv)
        return f

    made = {}
    for n, detail in enumerate((2, 2, 1, 0)):
        c = coll(f"Body_LOD{n}")
        made[("Body", n)] = mesh(N + ("_LOD%d" % n if n else ""), truss(detail), frame, c)
    for role, sign in (("LeafUp", 1), ("LeafDown", -1)):
        for n in range(3):
            c = coll(f"{role}_LOD{n}")
            made[(role, n)] = mesh(f"{N}_{role}" + ("_LOD%d" % n if n else ""), leaf(sign), panel, c)

    # fractured / deformed: the truss's halves
    def halves(me):
        out = []
        for left in (True, False):
            bm = bmesh.new(); bm.from_mesh(me)
            bmesh.ops.delete(bm, geom=[fa for fa in bm.faces if (fa.calc_center_median().x < 0) != left], context="FACES")
            h = me.copy(); bm.to_mesh(h); bm.free(); out.append(h)
        return out
    tex = bpy.data.textures.new("Deform_Clouds", "CLOUDS")
    tex.noise_type = "SOFT_NOISE"; tex.noise_scale = DISPLACE["size"]; tex.noise_depth = DISPLACE["depth"]; tex.cloud_type = "GRAYSCALE"
    fr, de = coll("Fractured"), coll("Deformed")
    for n in range(4):
        fc, dc = coll(f"Fractured_LOD{n}", fr), coll(f"Deformed_LOD{n}", de)
        body = made[("Body", n)].data
        parts = []
        if n < 3:
            for name, me in zip(("Fracture_01", "Fracture_02"), halves(body)):
                p = bpy.data.objects.new(name, me); fc.objects.link(p); p["group"] = p["ColliderMeshGroups"] = name; parts.append(p)
        else:
            p = bpy.data.objects.new(f"{N}_Fractured_LOD3", body.copy()); fc.objects.link(p); parts.append(p)
        for p in parts:
            p["LOD"] = n
            d = p.copy(); d.data = p.data.copy(); dc.objects.link(d)
            m = d.modifiers.new("Deform", "DISPLACE"); m.texture = tex; m.texture_coords = "GLOBAL"
            m.direction = "RGB_TO_XYZ"; m.space = "LOCAL"; m.strength = DISPLACE["strength"] * B / 2.5

    # collision: the truss's box; mount slabs left, right, front
    cc, cf = coll("Collision"), coll("Collision_Fractured")
    def add(c, name, lo, hi):
        o = mesh(name, lambda bm, uv: box(bm, lo, hi, uv), frame, c); o["ExportName"] = name; o.display_type = "WIRE"; return o
    add(cc, f"{N}_Collision", (-H + 0.02 * B, yc - ts / 2, -ts / 2), (H - 0.02 * B, yc + ts / 2, ts / 2))
    s = 0.05 * B
    add(cc, f"mountpoints_{N}_L", (-H, -H, -H), (-H + s, H, H))
    add(cc, f"mountpoints_{N}_R", (H - s, -H, -H), (H, H, H))
    add(cc, f"mountpoints_{N}_F", (-H, H - s, -H), (H, H, H))
    for i, name in enumerate(("Fracture_01", "Fracture_02")):
        lo, hi = (-H + 0.02 * B, 0.0) if i == 0 else (0.0, H - 0.02 * B)
        o = add(cf, name + "_Collision", (lo, yc - ts / 2, -ts / 2), (hi, yc + ts / 2, ts / 2)); o["ExportName"] = name

    # only LOD0 shown
    def lc(name, layer=None):
        layer = layer or bpy.context.view_layer.layer_collection
        if layer.collection.name == name: return layer
        for ch in layer.children:
            r = lc(name, ch)
            if r: return r
    for name in ["Collision_Fractured"] + [f"{v}_LOD{n}" for v in ("Fractured", "Deformed") for n in range(4)] + \
                [f"{g}_LOD{n}" for g in ("Body", "LeafUp", "LeafDown") for n in range(1, 4) if (g, n) in made]:
        l = lc(name)
        if l: l.exclude = True
    # the layout (Blender) for the definitions and the mod: the hinge, the leaves' dummies (5 mm apart, up / down)
    spec = {"name": N, "block": B, "hinge": [0.0, yh, 0.0], "leaf_up": [0.0, yh, 0.005], "leaf_down": [0.0, yh, -0.005],
            "chord": CHORD * B, "span": span, "open_deg": OPEN_DEG, "materials": {"Body": ["PaintedMetalDarker"], "LeafUp": ["PaintedMetalColorable"], "LeafDown": ["PaintedMetalColorable"]}}
    return N, spec


def main():
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    out = args[0]; which = args[1:] or list(SIZES)
    os.makedirs(out, exist_ok=True)
    for size in which:
        N, spec = build(size)
        bpy.ops.wm.save_as_mainfile(filepath=os.path.join(out, N + ".blend"))
        json.dump(spec, open(os.path.join(out, N + ".json"), "w"), indent=1)
        print(f"AIRBRAKE {N}: box {N_BLOCKS * SIZES[size]:.1f} m, leaves {spec['chord']:.2f} x {spec['span']:.2f} m opening {OPEN_DEG:.0f} deg each, hinge y {spec['hinge'][1]:.3f}")


main()
