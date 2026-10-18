"""TURBOPROP LOW-POLY BLOCKOUT (2026-10-16): the pieces measured off the user's flat-colour orthographic concept (side view,
top-right row; tools: colour segmentation), mapped onto the block's rules with the user:
  a dark ring behind the spinner +3.75..+3.25 inset | b front body +3.25..-0.25 inset 0.25 (bevelled front) |
  c top spine +2.25..-0.25 flush on top | d dark side window in b | e belly +2.25..+0.25 flush on the bottom (intake notch) |
  f collar -0.25..-1.25 flush all round | g dark core -1.25..-2.25 inset | h copper canister on the sides -0.75..-2.25 |
  i dark spacer -2.25..-2.75 inset | j rear block -2.75..-3.75 flush all round (the back 1 m) | no nozzle.
Low poly: octagonal prisms / chamfered boxes, one flat chamfer, flat shading; every joint sunk ~10 cm (no coincident faces).

  blender -b --factory-startup -P turboprop_blockout.py -- <out.blend>
"""
import bpy, bmesh, math, os, sys
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import rotorcraft_blockouts as rb

OUT = sys.argv[sys.argv.index("--") + 1]
B = 1.25
K = math.sqrt(2) - 1


def octo(h, c):
    return [(h, -(h - c)), (h, h - c), (h - c, h), (-(h - c), h), (-h, h - c), (-h, -(h - c)), (-(h - c), -h), (h - c, -h)]


def loft(bm, sections):
    """An octagonal prism / frustum along Y through (y, h, c) sections."""
    rings = [[bm.verts.new(Vector((x, y, z))) for (x, z) in octo(h, c)] for (y, h, c) in sections]
    for A, Bb in zip(rings, rings[1:]):
        for i in range(8): bm.faces.new((A[i], A[(i + 1) % 8], Bb[(i + 1) % 8], Bb[i]))
    bm.faces.new(rings[0][::-1]); bm.faces.new(rings[-1])


def side_prism(bm, profile, x0, x1):
    """A shape drawn in side view (y, z), extruded across x0..x1."""
    a = [bm.verts.new(Vector((x0, y, z))) for (y, z) in profile]; b = [bm.verts.new(Vector((x1, y, z))) for (y, z) in profile]
    n = len(profile); bm.faces.new(a[::-1]); bm.faces.new(b)
    for i in range(n): bm.faces.new((a[i], a[(i + 1) % n], b[(i + 1) % n], b[i]))


def piece(name, bm, mats, col, cuts=(), chamfer=0.04):
    """A solid: dark recesses cut in (boxes (lo, hi): their faces get material 2), one flat chamfer, flat shaded."""
    o = rb.obj(name, bm, mats, col, bevel=None)
    for (lo, hi) in cuts:
        cb = bmesh.new(); r = bmesh.ops.create_cube(cb, size=1.0)
        for v in r["verts"]: v.co = Vector(tuple((lo[i] + hi[i]) / 2 + v.co[i] * (hi[i] - lo[i]) for i in range(3)))
        cm = bpy.data.meshes.new("_c"); cb.to_mesh(cm); cb.free(); co = bpy.data.objects.new("_c", cm); col.objects.link(co)
        md = o.modifiers.new("c", "BOOLEAN"); md.object = co; md.operation = "DIFFERENCE"; md.solver = "EXACT"
        with bpy.context.temp_override(object=o, active_object=o, selected_objects=[o]): bpy.ops.object.modifier_apply(modifier=md.name)
        bpy.data.objects.remove(co); bpy.data.meshes.remove(cm)
        for p in o.data.polygons:
            c = p.center
            if all(lo[i] - 0.003 <= c[i] <= hi[i] + 0.003 for i in range(3)): p.material_index = len(mats) - 1
    if chamfer:
        bv = o.modifiers.new("ch", "BEVEL"); bv.width = chamfer; bv.segments = 1; bv.limit_method = "ANGLE"; bv.angle_limit = math.radians(30)
        with bpy.context.temp_override(object=o, active_object=o, selected_objects=[o]): bpy.ops.object.modifier_apply(modifier=bv.name)
    o.data.shade_flat()
    return o


def build(col):
    COPPER = rb.mat("Copper", (0.72, 0.42, 0.25))
    L, D = rb.BODY, rb.DARK
    # a: the dark ring behind the spinner (sunk 10 cm into the front body)
    bm = bmesh.new(); loft(bm, [(3.15, 0.75, 0.30), (3.70, 0.75, 0.30)]); piece("a_Ring", bm, [D], col)
    # b: the front body, inset 0.25 (h 1.0); its front 0.5 m bevels down to 0.8; a dark window cut in each side; runs 10 cm into f
    bm = bmesh.new(); loft(bm, [(3.25, 0.80, 0.30), (2.75, 1.00, 0.36), (-0.35, 1.00, 0.36)])
    win = [((0.90, -0.10, -0.30), (1.10, 0.95, 0.30)), ((-1.10, -0.10, -0.30), (-0.90, 0.95, 0.30))]
    piece("b_FrontBody", bm, [L, D], col, cuts=win)
    # c: the top spine - flush on top (a mount strip), its front end sloped; into f by 10 cm
    bm = bmesh.new(); side_prism(bm, [(2.25, 0.90), (-0.40, 0.90), (-0.40, B), (2.00, B)], -0.50, 0.50); piece("c_TopSpine", bm, [L], col)
    # e: the belly - flush on the bottom, a sloped front, a dark intake notch cut into its underside
    bm = bmesh.new(); side_prism(bm, [(2.25, -0.85), (0.25, -0.85), (0.25, -B), (2.00, -B)], -0.60, 0.60)
    piece("e_Belly", bm, [L, D], col, cuts=[((-0.35, 0.80, -1.35), (0.35, 1.45, -1.12))])
    # f: the collar - flush on all four sides (the mount section)
    bm = bmesh.new(); rb.chamfer_box(bm, (-B, -1.25, -B), (B, -0.25, B), 0.06); piece("f_Collar", bm, [L], col, chamfer=0.0)
    # g: the dark core, inset 0.25; 10 cm into f and into i's space
    bm = bmesh.new(); loft(bm, [(-1.15, 1.00, 0.36), (-2.30, 1.00, 0.36)]); piece("g_Core", bm, [D], col)
    # i: the dark spacer, inset 0.4
    bm = bmesh.new(); loft(bm, [(-2.20, 0.85, 0.30), (-2.85, 0.85, 0.30)]); piece("i_Spacer", bm, [D], col)
    # j: the rear block - flush on all sides and the back (the back 1 m)
    bm = bmesh.new(); rb.chamfer_box(bm, (-B, -3.75, -B), (B, -2.75, B), 0.06); piece("j_RearBlock", bm, [L], col, chamfer=0.0)
    # h: the copper canisters on both sides (12-sided), from inside the collar back over the core
    bm = bmesh.new()
    for sx in (-1, 1):
        t = bmesh.new(); rb.lathe(t, [(-0.75, 0.0), (-0.75, 0.20), (-2.25, 0.20), (-2.25, 0.0)], 12)
        for v in t.verts: v.co.x += sx * 1.02
        me = bpy.data.meshes.new("_t"); t.to_mesh(me); t.free(); bm.from_mesh(me); bpy.data.meshes.remove(me)
    piece("h_Canisters", bm, [COPPER], col)
    # the spinner (low poly cone, ahead in the prop cell)
    bm = bmesh.new(); rb.lathe(bm, [(3.65, 0.0), (3.65, 0.62), (4.30, 0.55), (5.00, 0.30), (5.40, 0.0)], 12)
    piece("Spinner", bm, [D], col, chamfer=0.0)


def zfight(col, tol=0.002):
    obs = [o for o in col.objects if o.type == "MESH" and not o.name.endswith("Occupancy")]
    F = []
    for o in obs:
        for p in o.data.polygons:
            n = p.normal.normalized(); vs = [o.data.vertices[i].co for i in p.vertices]
            F.append((o.name, n, n.dot(p.center), Vector(tuple(min(v[i] for v in vs) for i in range(3))), Vector(tuple(max(v[i] for v in vs) for i in range(3))), p.area))
    hits = []
    for i, a in enumerate(F):
        for b in F[i + 1:]:
            if a[0] == b[0] or a[5] < 1e-4 or b[5] < 1e-4 or abs(abs(a[1].dot(b[1])) - 1) > 1e-4: continue
            if abs(a[2] - (b[2] if a[1].dot(b[1]) > 0 else -b[2])) > tol: continue
            if all(min(a[4][k], b[4][k]) - max(a[3][k], b[3][k]) > 1e-3 for k in range(3) if abs(a[1][k]) < 0.5): hits.append((a[0], b[0]))
    return sorted(set(hits))


def main():
    rb.reset(); rb.mat("Copper", (0.72, 0.42, 0.25))
    col = rb.collection("Turboprop_250_blockout")
    build(col)
    print("ZFIGHT", zfight(col))
    rb.occupancy("TPB_Occupancy", [((-0.5, -1.5, -0.5), (0.5, 1.5, 0.5))], col, 2.5)
    rb.finish(OUT)
    print("BLOCKOUT saved", OUT, sum(len(o.data.polygons) for o in bpy.data.objects if o.type == "MESH" and not o.name.endswith("Occupancy")), "faces")


main()
