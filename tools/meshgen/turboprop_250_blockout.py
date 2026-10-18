"""TURBOPROP 2.5 m - primary-shape blockout (2026-10-16, approved as the overall contour).

Rebuilds D:\\Blender Brojects\\SE2\\Turboprop_Fresh\\Turboprop_250_blockout.blend from numbers. Metres, nose +Y, prop on
the block centre line. Nacelle = 3 cells (y -3.75 .. 3.75), prop disc layer = 5 x 5 x 1 cells ahead (y 3.75 .. 6.25).

Layout (one 45-degree octagon section, 0.25 corners, everywhere):
  rear cell   tail shell (top flush = mount spine; sides to +-1.0, belly up to -0.75; uniform end frustum)
  -1.50..-1.25  core ring (dark, even 0.25 inset of the body section)
  middle cell full-box section, flush on all four sides, side windows (frame +-0.5, opening +-0.25, compressor discs)
  1.25..1.50  core ring
  front cell  nose shell: one flat taper to the spinner face (+-0.75) at y 4.75; belly flush to 3.75, then the chin
              rakes up; trapezoid chin intake = constant 0.1 inset of the chin face
  prop        tangent-ogive spinner (R 0.55, tip 6.2), 3 blades with stout cuffs, parented to PROP_Pivot on the axis

  blender -b --factory-startup -P turboprop_250_blockout.py -- <out.blend>
  or inside Blender: exec(open(<this file>).read())   (rebuilds the scene in place - save your work first)
"""
import bpy, bmesh, math, os, sys, importlib
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)) if "__file__" in globals()
                else r"C:\Users\slob\Documents\SpaceEngineers2\Mods\Aerodynamics Mod\tools\meshgen")
import blockout_lib as bl
importlib.reload(bl)
from blockout_lib import ins, on_line

H = 1.25
BODY = (H, H, -H, 0.25, 0.25)          # the one section
CORE = ins(BODY, 0.25)                 # core rings
FACE = (0.75, 0.75, -0.75, 0.25, 0.25) # spinner face
TAIL = (1.00, H, -0.75, 0.25, 0.25)    # tail end

# ---- scene ----
for o in list(bpy.data.objects):
    bpy.data.objects.remove(o, do_unlink=True)
for c in list(bpy.data.collections):
    bpy.data.collections.remove(c)
ROOT = bl.collection("COL_Turboprop")
CREF, CGEO, CBOOL, CPROP = (bl.collection(n, ROOT) for n in ("COL_Reference", "COL_Geo", "COL_Booleans", "COL_Prop"))
M_BODY = bl.material("PaintedMetalColorable", (0.62, 0.63, 0.60), 0.3, 0.5)
M_DARK = bl.material("PaintedMetalDarker", (0.18, 0.19, 0.20), 0.4, 0.5)
M_HOLE = bl.material("Recess_Black", (0.02, 0.02, 0.02), 0.0, 0.9)
M_METAL = bl.material("MetalDull", (0.45, 0.45, 0.47), 0.9, 0.35)
SHELL = [M_BODY, M_DARK]

# ---- front cell ----
ya, yc, yb = 1.55, 3.75, 4.75
chin = on_line(yc, ya, BODY, yb, FACE)
chin = (chin[0], chin[1], -H, 0.25, 0.25)                          # top/sides on the taper line, belly still flush
front = bl.build("SM_Nacelle_Front", [([(1.15, *CORE), (1.60, *CORE)], 1),
                                      ([(1.50, *ins(BODY, 0.05)), (ya, *BODY), (yc, *chin), (yb, *FACE)], 0)], CGEO, SHELL)
hw = chin[0]
A, B = Vector((-(hw-0.25), yc, -H)), Vector((hw-0.25, yc, -H))
Cc, D = Vector((FACE[0]-FACE[4], yb, FACE[2])), Vector((-(FACE[0]-FACE[4]), yb, FACE[2]))
n = (B-A).cross(D-A).normalized()
n = -n if n.z > 0 else n
q = bl.inset_poly([A, B, Cc, D], n, 0.10)
mouth = bl.cutter(bl.hull("SM_Cutter_IntakeMouth", [tuple(p+n*0.2) for p in q]+[tuple(p-n*0.6) for p in q], CBOOL, [M_HOLE]))
bl.boolean(front, mouth, "Bool_IntakeMouth")

# ---- middle cell ----
mid = bl.build("SM_Nacelle_Mid", [([(-1.245, *ins(BODY, 0.04)), (-1.205, *BODY), (1.205, *BODY), (1.245, *ins(BODY, 0.04))], 0)], CGEO, [M_BODY])
bl.boolean(mid, bl.side_slabs("SM_Cutter_WindowFrame", -0.5, 0.5, -0.5, 0.5, 1.17, CBOOL, [M_DARK]), "Bool_WindowFrame")
bl.boolean(mid, bl.side_slabs("SM_Cutter_WindowOpening", -0.25, 0.25, -0.25, 0.25, 0.80, CBOOL, [M_HOLE]), "Bool_WindowOpening")
bm = bmesh.new()
for y in (-0.22, 0.0, 0.22):
    bl.ring_y(bm, 0.98, y-0.06, y+0.06, seg=24)
bl.to_object("SM_Compressor", bm, CGEO, [M_METAL])

# ---- rear cell ----
bl.build("SM_Nacelle_Rear", [([(-1.60, *CORE), (-1.15, *CORE)], 1),
                             ([(-3.75, *ins(TAIL, 0.12)), (-3.63, *TAIL), (-1.55, *BODY), (-1.50, *ins(BODY, 0.05))], 0)], CGEO, SHELL)

# ---- prop ----
piv = bpy.data.objects.new("PROP_Pivot", None)
CPROP.objects.link(piv)
piv.empty_display_type = 'SINGLE_ARROW'
piv.location = (0, yb, 0)
piv.rotation_euler = (-math.pi/2, 0, 0)                            # arrow along +Y, the thrust axis
bm = bmesh.new()
ogive = bl.ogive_y(bm, 0.55, 4.78, 0.12, 1.30)
plate = bl.ring_y(bm, 0.60, 4.70, 4.84, seg=24)
for f in plate:
    f.material_index = 1
bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
sp = bl.to_object("SM_Spinner", bm, CPROP, [M_BODY, M_DARK])
for p in sp.data.polygons:
    p.use_smooth = p.material_index == 0 and abs(p.normal.y) < 0.99


def section(r, chord, th, pitch):
    c, s = math.cos(math.radians(pitch)), math.sin(math.radians(pitch))
    return [((u*c-v*s), (u*s+v*c), r) for (u, v) in ((-chord*0.4, -th/2), (chord*0.6, -th/2*0.4), (chord*0.6, th/2*0.4), (-chord*0.4, th/2))]


bm = bmesh.new()
s12 = 12
a = [bm.verts.new((0.19*math.cos(2*math.pi*i/s12), 0.19*math.sin(2*math.pi*i/s12), 0.40)) for i in range(s12)]
b = [bm.verts.new((0.19*math.cos(2*math.pi*i/s12), 0.19*math.sin(2*math.pi*i/s12), 0.72)) for i in range(s12)]
for i in range(s12):
    bm.faces.new((a[i], a[(i+1) % s12], b[(i+1) % s12], b[i]))
bm.faces.new(list(reversed(a)))
bm.faces.new(b)
rings = [[bm.verts.new(p) for p in section(*x)] for x in ((0.66, 0.30, 0.16, 38), (1.15, 1.00, 0.15, 30), (3.5, 0.92, 0.11, 21), (5.6, 0.52, 0.07, 14))]
for r0, r1 in zip(rings, rings[1:]):
    for i in range(4):
        bm.faces.new((r0[i], r0[(i+1) % 4], r1[(i+1) % 4], r1[i]))
bm.faces.new(rings[0])
bm.faces.new(list(reversed(rings[-1])))
bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
blade = bl.to_object("SM_Blade", bm, CPROP, [M_DARK])
blades = [blade]+[bpy.data.objects.new(f"SM_Blade.{k:03d}", blade.data) for k in (1, 2)]
for k, o in enumerate(blades):
    if k:
        CPROP.objects.link(o)
    o.location = (0, 5.0, 0)
    o.rotation_euler = (0, math.radians(120*k), 0)
for o in [sp]+blades:
    bl.parent_keep(o, piv)

# ---- reference ----
bl.grid_lattice("REF_Grid_Nacelle_2p5", (-1.25, -3.75, -1.25), (1.25, 3.75, 1.25), 2.5, CREF)
bl.grid_lattice("REF_Grid_Disc_2p5", (-6.25, 3.75, -6.25), (6.25, 6.25, 6.25), 2.5, CREF)
if bpy.context.screen:
    for area in bpy.context.screen.areas:
        if area.type == 'VIEW_3D':
            area.spaces[0].overlay.grid_scale = 2.5
            area.spaces[0].overlay.grid_subdivisions = 5
            area.spaces[0].shading.color_type = 'MATERIAL'
lc = bpy.context.view_layer.layer_collection.children["COL_Turboprop"].children["COL_Booleans"]
lc.hide_viewport = True

# ---- checks ----
geo = ["SM_Nacelle_Front", "SM_Nacelle_Mid", "SM_Nacelle_Rear", "SM_Compressor", "SM_Spinner", "SM_Blade"]
print("coplanar:", bl.check_coplanar(geo))
print("non-manifold:", bl.check_manifold(geo))
print("off the 0.25 grid:", bl.check_grid(["SM_Nacelle_Front", "SM_Nacelle_Mid", "SM_Nacelle_Rear"]))

if "--" in sys.argv:
    out = sys.argv[sys.argv.index("--")+1]
    bpy.ops.wm.save_as_mainfile(filepath=out)
    print("saved", out)
