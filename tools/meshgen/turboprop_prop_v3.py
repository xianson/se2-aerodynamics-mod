import bpy, bmesh, math, sys
sys.path.insert(0, r"C:\Users\slob\Documents\SpaceEngineers2\Mods\Aerodynamics Mod\tools\meshgen")
import blockout_lib as bl
from mathutils import Matrix, Vector
O=bpy.data.objects; C=bpy.data.collections; add=C["COL_TP3_Added"]; tp=O["TP3_Root"]
MD,MY=(bpy.data.materials[n] for n in ("PaintedMetalDarker","PaintedMetalYellow"))
for o in [o for o in O if o.name.startswith(("PROP_","SPN_"))]: O.remove(o)
for me in [m for m in bpy.data.meshes if m.name.startswith("PROP_") and m.users==0]: bpy.data.meshes.remove(me)
def uv_box(bm, s=0.6):
    uv=bm.loops.layers.uv.verify()
    for f in bm.faces:
        n=f.normal; ax=max(range(3),key=lambda i:abs(n[i]))
        for l in f.loops:
            c=l.vert.co; a,b=[c[i] for i in range(3) if i!=ax]; l[uv].uv=(a*s,b*s)

# ---- spinner: matte dark gunmetal ogive, base just ahead of the intake ----
SR, SY0, SBAR, SL = 0.68, 1.27, 0.22, 0.92
bm=bmesh.new(); fs=bl.ogive_y(bm, SR, SY0, SBAR, SL, seg=56, n=18)
for f in fs: f.smooth=True
bmesh.ops.recalc_face_normals(bm,faces=bm.faces); uv_box(bm)
sp=bl.to_object("PROP_Spinner",bm,add,[MD])
for p in sp.data.polygons: p.use_smooth = abs(p.normal.y)<0.999
sp.modifiers.new("WeightedNormal",'WEIGHTED_NORMAL').keep_sharp=True
bl.parent_keep(sp,tp)

# ---- blade: frame x = tangential (chord), y = axial, z = radial ----
# flat slab with bevelled edges (8-point section), low pitch
SEC=[(-0.50,0.0),(-0.42,0.5),(0.42,0.5),(0.50,0.0),(0.42,-0.5),(-0.42,-0.5)]
N=len(SEC)
def section(r,c,t,beta):
    cb,sb=math.cos(math.radians(beta)),math.sin(math.radians(beta))
    return [Vector((u*c*cb - v*t*sb, u*c*sb + v*t*cb, r)) for u,v in SEC]
#   r     chord  thick  pitch
KEY=[(0.30,0.36,0.16,30),(0.80,0.38,0.14,28),(1.05,0.50,0.12,26),(1.30,0.60,0.11,24),(1.60,0.62,0.10,22),
     (2.60,0.62,0.085,17),(2.94,0.62,0.08,16),(3.00,0.50,0.075,16)]          # square tip, chamfered corners (last station)
bm=bmesh.new(); rings=[[bm.verts.new(p) for p in section(r,c,t,b)] for r,c,t,b in KEY]
for a,b in zip(rings,rings[1:]):
    for i in range(N):
        j=(i+1)%N; bm.faces.new((a[i],a[j],b[j],b[i]))
bm.faces.new(list(reversed(rings[0]))); bm.faces.new(rings[-1])
bmesh.ops.recalc_face_normals(bm,faces=bm.faces)
# diagonal hazard stripes on the last 0.45 m: cut with parallel planes at 45 deg in the chord/radial plane
dirn=Vector((1,0,1)).normalized()          # stripe normal (chord + radial)
W=0.11                                      # stripe width (measured along the normal)
cuts=[]
for k in range(-12,13):
    co=Vector((0,0,2.75))+dirn*(k*W)
    geom=bm.verts[:]+bm.edges[:]+bm.faces[:]
    bmesh.ops.bisect_plane(bm, geom=geom, dist=1e-6, plane_co=co, plane_no=dirn)
bmesh.ops.bisect_plane(bm, geom=bm.verts[:]+bm.edges[:]+bm.faces[:], dist=1e-6, plane_co=(0,0,2.55), plane_no=(0,0,1))
for f in bm.faces:
    c=f.calc_center_median()
    if c.z>2.55:
        s=math.floor(((c-Vector((0,0,2.75))).dot(dirn))/W)
        f.material_index = 1 if s%2==0 else 0
for f in bm.faces: f.smooth=False
uv_box(bm,0.8)
me=bpy.data.meshes.new("PROP_Blade"); bm.to_mesh(me); bm.free()
for m in (MD,MY): me.materials.append(m)
YH=SY0+0.20                                 # blades enter the spinner at its widest part
for k in range(3):
    o=O.new(f"PROP_Blade.{k}",me); add.objects.link(o)
    o.matrix_world=Matrix.Translation((0,YH,0))@Matrix.Rotation(math.radians(120*k),4,'Y')
    bl.parent_keep(o,tp)
bpy.context.view_layer.update()
lo=min((o.matrix_world@v.co).y for o in O if o.name.startswith("PROP_Blade") for v in o.data.vertices if math.hypot((o.matrix_world@v.co).x,(o.matrix_world@v.co).z)>0.90)
import collections
print("blade min y at r>0.90: %.3f (engine front 1.251)"%lo, collections.Counter(p.material_index for p in me.polygons), "spinner tip y", SY0+SBAR+SL)
bpy.ops.wm.save_mainfile()
