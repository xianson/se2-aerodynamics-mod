"""Helicopter top rotor, 1.5 m block (disc 13.5 m). Axis = world Z, hub centre at origin. Blades are separate objects,
origin on the flap hinge, local X = span (pitch axis, through 35% chord), local Y = chord, local Z = up."""
import bpy, bmesh, math, sys, os
sys.path.insert(0, r"C:\Users\slob\Documents\SpaceEngineers2\Mods\Aerodynamics Mod\tools\meshgen")
import importlib, blockout_lib as bl
importlib.reload(bl)
from mathutils import Matrix, Vector

OUT = r"D:\Blender Brojects\SE2\Rotor_Fresh\Rotor_150_v1.blend"
os.makedirs(os.path.dirname(OUT), exist_ok=True)
bpy.ops.wm.read_homefile(use_empty=True)
LIB = r"D:\Blender Brojects\SE2\AssetLibrary\SE2Decal&Material.blend"
with bpy.data.libraries.load(LIB, link=False) as (src, dst):
    dst.materials = ["PaintedMetalDarker", "PaintedMetalYellow", "PaintedMetalColorable", "BrushedMetal", "MetalDull", "Chrome"]
for m in dst.materials:
    m["se2_lib"] = True
MD, MY, MC, MB, MM, MCH = dst.materials

root = bl.collection("COL_Rotor")
GEO = bl.collection("COL_Rotor_Hub", root)
BLC = bl.collection("COL_Rotor_Blades", root)
REF = bl.collection("COL_Rotor_Ref", root)


def uv_box(bm, s=0.6):
    uv = bm.loops.layers.uv.verify()
    for f in bm.faces:
        n = f.normal
        ax = max(range(3), key=lambda i: abs(n[i]))
        for l in f.loops:
            c = l.vert.co
            a, b = [c[i] for i in range(3) if i != ax]
            l[uv].uv = (a*s, b*s)


def cyl(bm, p0, p1, r, seg=24, r1=None, mi=0, smooth=True):
    r1 = r if r1 is None else r1
    ax = (p1-p0).normalized()
    a = ax.orthogonal().normalized()
    b = ax.cross(a)
    R0 = [bm.verts.new(p0+(a*math.cos(2*math.pi*i/seg)+b*math.sin(2*math.pi*i/seg))*r) for i in range(seg)]
    R1 = [bm.verts.new(p1+(a*math.cos(2*math.pi*i/seg)+b*math.sin(2*math.pi*i/seg))*r1) for i in range(seg)]
    for i in range(seg):
        f = bm.faces.new((R0[i], R0[(i+1) % seg], R1[(i+1) % seg], R1[i]))
        f.smooth = smooth
        f.material_index = mi
    for f in (bm.faces.new(list(reversed(R0))), bm.faces.new(R1)):
        f.material_index = mi
        f.smooth = False


def box(bm, c, h, mi=0):
    r = bmesh.ops.create_cube(bm, size=1.0)
    for v in r["verts"]:
        v.co = Vector((c[0]+v.co.x*2*h[0], c[1]+v.co.y*2*h[1], c[2]+v.co.z*2*h[2]))
    for f in {f for v in r["verts"] for f in v.link_faces}:
        f.material_index = mi
        f.smooth = False


HINGE = 0.75     # flap hinge radius = blade origin
ROOT = 1.30      # aerofoil starts here
TIP = 6.60


def merge(bm, sub, R=None):
    if R is not None:
        for v in sub.verts:
            v.co = R @ v.co
    me = bpy.data.meshes.new("tmp")
    sub.to_mesh(me)
    sub.free()
    bm.from_mesh(me)
    bpy.data.meshes.remove(me)


def prism(bm, pts2d, z0, z1, mi=0, inset_top=0.0):
    """extrude a convex 2D outline (XY) from z0 to z1; optional chamfer at the top"""
    lo = [bm.verts.new((x, y, z0)) for x, y in pts2d]
    if inset_top > 0:
        cx = sum(p[0] for p in pts2d)/len(pts2d)
        cy = sum(p[1] for p in pts2d)/len(pts2d)
        mid = [bm.verts.new((x, y, z1-inset_top)) for x, y in pts2d]
        k = 1-inset_top/max(math.hypot(x-cx, y-cy) for x, y in pts2d)
        hi = [bm.verts.new((cx+(x-cx)*k, cy+(y-cy)*k, z1)) for x, y in pts2d]
        rings = [lo, mid, hi]
    else:
        rings = [lo, [bm.verts.new((x, y, z1)) for x, y in pts2d]]
    n = len(pts2d)
    for a, b in zip(rings, rings[1:]):
        for i in range(n):
            f = bm.faces.new((a[i], a[(i+1) % n], b[(i+1) % n], b[i]))
            f.material_index = mi
            f.smooth = False
    for f in (bm.faces.new(list(reversed(rings[0]))), bm.faces.new(rings[-1])):
        f.material_index = mi


def star_outline(r_arm, w, r_hub, seg=10):
    """three-arm yoke outline: hub circle joined to round-ended arms (convex hull per arm handled by union of prisms)"""
    return None


# ---------------- rotor head (spins with the rotor) ----------------
bm = bmesh.new()
# mast (static in reality; kept here for the look, it goes to the base later)
cyl(bm, Vector((0, 0, -0.75)), Vector((0, 0, 0.30)), 0.17, seg=32, mi=4)
# lower and upper hub plates: centre discs
cyl(bm, Vector((0, 0, -0.22)), Vector((0, 0, -0.12)), 0.46, seg=48, mi=2, smooth=True)
cyl(bm, Vector((0, 0, 0.12)), Vector((0, 0, 0.22)), 0.46, seg=48, mi=2, smooth=True)
cyl(bm, Vector((0, 0, -0.12)), Vector((0, 0, 0.12)), 0.30, seg=48, mi=4, smooth=True)   # hub core between plates
# yoke arms: each a round-ended slab top and bottom, joined by the spindle housing at the hinge
for k in range(3):
    R = Matrix.Rotation(math.radians(120*k), 3, 'Z')
    sub = bmesh.new()
    for z0, z1 in ((-0.22, -0.12), (0.12, 0.22)):
        out = [(0.30, -0.24), (0.62, -0.20)]
        out += [(HINGE-0.02+0.20*math.cos(t), 0.20*math.sin(t)) for t in [math.radians(a) for a in range(-75, 76, 25)]]
        out += [(0.62, 0.20), (0.30, 0.24)]
        prism(sub, out, z0, z1, mi=2)
    cyl(sub, Vector((HINGE, 0, -0.27)), Vector((HINGE, 0, 0.27)), 0.07, seg=20, mi=3)    # hinge bolt through both plates
    for zz in (0.22, -0.26):
        cyl(sub, Vector((HINGE, 0, zz)), Vector((HINGE, 0, zz+0.04)), 0.11, seg=6, mi=5, smooth=False)   # hex head / nut
    merge(bm, sub, R)
# JESUS NUT: washer, hex body with chamfer, castellated crown, mast tip and lock pin
cyl(bm, Vector((0, 0, 0.22)), Vector((0, 0, 0.26)), 0.36, seg=48, mi=3)                  # washer
hexo = [(0.33*math.cos(math.radians(60*i+30)), 0.33*math.sin(math.radians(60*i+30))) for i in range(6)]
prism(bm, hexo, 0.26, 0.48, mi=4, inset_top=0.04)                                         # hex body
for i in range(12):                                                                       # castellations
    if i % 2:
        continue
    a0, a1 = math.radians(30*i), math.radians(30*(i+1))
    sub = bmesh.new()
    pts = [(r*math.cos(a), r*math.sin(a)) for r in (0.17, 0.25) for a in (a0, a1)]
    pts = [pts[0], pts[1], pts[3], pts[2]]
    prism(sub, pts, 0.46, 0.56, mi=4)
    merge(bm, sub)
cyl(bm, Vector((0, 0, 0.30)), Vector((0, 0, 0.62)), 0.13, seg=24, mi=3)                  # threaded mast tip
cyl(bm, Vector((0, 0, 0.62)), Vector((0, 0, 0.66)), 0.10, seg=24, mi=3, smooth=False)    # tip chamfer
pa = math.radians(45)
cyl(bm, Vector((-0.30*math.cos(pa), -0.30*math.sin(pa), 0.53)), Vector((0.30*math.cos(pa), 0.30*math.sin(pa), 0.53)), 0.025, seg=10, mi=5)   # lock pin through two slots
# swashplate + 3 pitch links (to the pitch horns on the grips)
cyl(bm, Vector((0, 0, -0.62)), Vector((0, 0, -0.52)), 0.62, seg=48, mi=3)
cyl(bm, Vector((0, 0, -0.52)), Vector((0, 0, -0.44)), 0.56, seg=48, mi=2)
bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
uv_box(bm)
hub = bl.to_object("SM_RotorHub", bm, GEO, [MD, MY, MC, MB, MM, MCH])
hub.modifiers.new("WeightedNormal", 'WEIGHTED_NORMAL').keep_sharp = True

# ---------------- blade: local X span from the hinge, Y chord (LE at -Y), Z up ----------------
SEC = [(-0.35, 0.0), (-0.30, 0.45), (-0.10, 0.55), (0.30, 0.42), (0.62, 0.12), (0.65, 0.0), (0.62, -0.10), (0.30, -0.40), (-0.10, -0.50), (-0.30, -0.42)]
N = len(SEC)


def section(x, c, t, tw):
    ct, st = math.cos(math.radians(tw)), math.sin(math.radians(tw))
    return [Vector((x, u*c*ct - v*t*st, -(u*c*st) + v*t*ct)) for u, v in SEC]   # nose-up twist: LE (-Y) up


def roundsec(x, r):
    return [Vector((x, r*math.cos(2*math.pi*i/N), r*math.sin(2*math.pi*i/N))) for i in range(N)]


KEY = [(ROOT-HINGE,      0.30, 0.13,  9.0),
       (ROOT-HINGE+0.25, 0.46, 0.085, 8.5),
       (TIP-HINGE-0.28,  0.46, 0.06,  2.5),
       (TIP-HINGE-0.06,  0.46, 0.058, 2.2),
       (TIP-HINGE,       0.32, 0.055, 2.0)]
bm = bmesh.new()
rings = [[bm.verts.new(p) for p in roundsec(-0.10, 0.09)], [bm.verts.new(p) for p in roundsec(0.48, 0.09)]]
hub_eye = [[bm.verts.new(p) for p in [Vector((0.11*math.cos(2*math.pi*i/N), 0.11*math.sin(2*math.pi*i/N), z)) for i in range(N)]] for z in (-0.10, 0.10)]
for i in range(N):
    f = bm.faces.new((hub_eye[0][i], hub_eye[0][(i+1) % N], hub_eye[1][(i+1) % N], hub_eye[1][i])); f.material_index = 2
for f in (bm.faces.new(list(reversed(hub_eye[0]))), bm.faces.new(hub_eye[1])): f.material_index = 2
rings += [[bm.verts.new(p) for p in section(*k)] for k in KEY]
for a, b in zip(rings, rings[1:]):
    for i in range(N):
        bm.faces.new((a[i], a[(i+1) % N], b[(i+1) % N], b[i]))
bm.faces.new(list(reversed(rings[0])))
bm.faces.new(rings[-1])
bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
# hazard stripes on the outer 0.75 m
dirn = Vector((1, 1, 0)).normalized()
W = 0.10
c0 = Vector((TIP-HINGE-0.4, 0, 0))
for k in range(-14, 15):
    bmesh.ops.bisect_plane(bm, geom=bm.verts[:]+bm.edges[:]+bm.faces[:], dist=1e-6, plane_co=c0+dirn*(k*W), plane_no=dirn)
XS = TIP-HINGE-0.75
bmesh.ops.bisect_plane(bm, geom=bm.verts[:]+bm.edges[:]+bm.faces[:], dist=1e-6, plane_co=(XS, 0, 0), plane_no=(1, 0, 0))
for f in bm.faces:
    c = f.calc_center_median()
    f.material_index = (1 if math.floor((c-c0).dot(dirn)/W) % 2 == 0 else 0) if c.x > XS else 0
    f.smooth = True
# grip sleeve (separate shell around the shank)
grip = [[bm.verts.new(p) for p in roundsec(x, 0.15)] for x in (0.24, 0.50)]
# pitch horn: a short arm off the grip toward the leading edge, the pitch link ends under it
horn = [bm.verts.new(p) for p in ((0.26, -0.16, -0.05), (0.40, -0.16, -0.05), (0.26, -0.34, -0.10), (0.40, -0.34, -0.10),
                                  (0.26, -0.16, 0.03), (0.40, -0.16, 0.03), (0.26, -0.34, -0.02), (0.40, -0.34, -0.02))]
hr = bmesh.ops.convex_hull(bm, input=horn)
for f in [g for g in hr["geom"] if isinstance(g, bmesh.types.BMFace)]:
    f.material_index = 2
for i in range(N):
    f = bm.faces.new((grip[0][i], grip[0][(i+1) % N], grip[1][(i+1) % N], grip[1][i]))
    f.material_index = 2
    f.smooth = True
for f in (bm.faces.new(list(reversed(grip[0]))), bm.faces.new(grip[1])):
    f.material_index = 2
bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
uv_box(bm, 0.8)
me = bpy.data.meshes.new("SM_RotorBlade")
bm.to_mesh(me)
bm.free()
for m in (MD, MY, MB):
    me.materials.append(m)
for k in range(3):
    o = bpy.data.objects.new(f"SM_RotorBlade.{k}", me)
    BLC.objects.link(o)
    a = math.radians(120*k)
    o.location = (HINGE*math.cos(a), HINGE*math.sin(a), 0.0)
    o.rotation_euler = (0, 0, a)
    o.modifiers.new("WeightedNormal", 'WEIGHTED_NORMAL').keep_sharp = True
    o["pitch_axis"] = "local X, through 35% chord"
    o["flap_hinge"] = "object origin"

# ---------------- pitch links: swashplate rim -> pitch-horn tip of each blade ----------------
bpy.context.view_layer.update()
lk = bmesh.new()
for k in range(3):
    o = bpy.data.objects[f"SM_RotorBlade.{k}"]
    top = o.matrix_world @ Vector((0.33, -0.34, -0.09))
    d = Vector((top.x, top.y, 0)).normalized()
    bot = d*0.58 + Vector((0, 0, -0.44))
    cyl(lk, bot, top, 0.035, seg=12, mi=0)
    for p in (bot, top):
        cyl(lk, p-Vector((0, 0, 0.04)), p+Vector((0, 0, 0.04)), 0.06, seg=12, mi=1)   # rod ends
bmesh.ops.recalc_face_normals(lk, faces=lk.faces)
links = bl.to_object("SM_RotorPitchLinks", lk, GEO, [MCH, MM])

# ---------------- reference cells (1.5 m): disc layer 9x9x1 on the hub, mast 1x1x2 below ----------------
bl.grid_lattice("REF_DiscLayer", (-6.75, -6.75, -0.75), (6.75, 6.75, 0.75), 1.5, REF)
bl.grid_lattice("REF_Mast", (-0.75, -0.75, -3.75), (0.75, 0.75, -0.75), 1.5, REF)
sc = bpy.context.scene
try:
    sc.render.engine = 'BLENDER_EEVEE'
except TypeError as e:
    print(e)
for w in bpy.context.window_manager.windows:
    for a in w.screen.areas:
        if a.type == 'VIEW_3D':
            a.spaces[0].shading.type = 'MATERIAL'
bpy.ops.wm.save_as_mainfile(filepath=OUT)
print("saved", OUT)
