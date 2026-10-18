"""HELICOPTER ROTOR ANIMATION PARTS (2026-10-16, the user: "proper helicopter rotor blade animation" - governed RPM,
collective, cyclic, coning, a blur disc). The export scene (propulsor_prep.py -> Propulsors_Export.blend) has each rotor's
hub and blades as ONE spinning subpart (N|Rotor_LODn). This step, run on that scene, writes a NEW file holding only the
helicopter rotors with the spinning part split for AeroRotorAnimComponent (the mod drives every part each frame):

  N|Hub_LOD0..2    the hub (the BrushedMetal faces of N|Rotor_LODn), origin at the rotor pivot (spec "subpart")
  N|Blade_LOD0..2  ONE blade, origin at its flap hinge (the blade root, radius 0.45 cell on the span), span along
                   VRage +Z (Blender -Y), its pitch axis = that span line (the blade section pivots at 35 % chord);
                   regenerated with rotorcraft_blockouts.blade() (imported, the file untouched) with the blade angles
                   NEGATED: the blockout's blades had the leading edge DOWN for a leading-edge-first spin (the user's
                   placeholder; the vanilla spin turned them trailing edge first). Here the leading edge (-X) is up and
                   the rotor turns CLOCKWISE seen from above (about VRage -Y): leading edge first.
  N|Blur_LOD0..2   the blur disc: a thin conical annulus at the blade plane (hub to tip, the coning at ConeRefDeg less the
                   blade's own droop), ONE translucent material (AeroRotorBlur: gen_propulsor_defs.py writes it - a
                   PBRTransparent material whose alpha texture is the blur: denser at the root, the tip stripes' band)
                   mapped flat from above (the tip at BLUR_TIP_RHO of the texture's half-width); origin on the axis at the
                   hinge height. The mod fades it in (entity opacity, dithered only while it is < 1) as the blades fade out.
and a spec (rotor_anim_spec.json): per rotor the dummies (VRage block space, the model's +0.125 left to the defs) - the
hub at the pivot, three blades at their hinges (each turned 120 deg about Y), the blur disc - and the numbers the mod's
kinematics use (hinge radius, tip radius, the baked pitch at 75 % radius, the blur disc's cone).
Body / Fractured / Deformed / Collision collections are kept as they are (the same export as before).

  blender -b Propulsors_Export.blend -P rotor_split.py -- <out.blend> <anim_spec.json>
then: blender <out.blend> --python propulsor_export.py -- <Export dir> only=HelicopterRotor_150,HelicopterRotor_250
"""
import bpy, bmesh, json, math, os, sys
from mathutils import Matrix, Vector

argv = sys.argv[sys.argv.index("--") + 1:]
OUT, SPEC_OUT = argv[0], argv[1]
HERE = os.path.dirname(os.path.abspath(__file__)) if "__file__" in globals() else r"C:\Users\slob\Documents\SpaceEngineers2\Mods\Aerodynamics Mod\tools\meshgen"
sys.path.insert(0, HERE)
import rotorcraft_blockouts as rb   # (read-only use: blade() and obj(); its main() is not run)

PROP_SPEC = os.path.join(os.path.dirname(OUT), "propulsor_spec.json")
ROTORS = {"HelicopterRotor_150": 1.5, "HelicopterRotor_250": 2.5}
# the blockout's helicopter() blade call: blade(bm, 0.45 s, 4.45 s, 0.42 s, 14, 6, angle, "rotor", droop=0.10 s), plane at 2.42 s;
# the pivot (the old spinning subpart's origin) is at 2.55 s: the blade plane is 0.13 s below it
R0, R1, CHORD, B_ROOT, B_TIP, DROOP, PLANE = 0.45, 4.45, 0.42, 14.0, 6.0, 0.10, -0.13
CONE_REF_DEG = 2.5          # the blur disc's cone (the coning it shows without a shift)
BLUR_MAT, BLUR_TIP_RHO = "AeroRotorBlur", 0.98   # (gen_propulsor_defs.py: the material, the tip's place in its texture)
LOD_RATIOS = [1.0, 0.5, 0.25]
SEGS = [96, 48, 24]
O = bpy.data.objects
sc = bpy.context.scene
mats = {m.name: m for m in bpy.data.materials}


def coll(name):
    c = bpy.data.collections.new(name); sc.collection.children.link(c); return c


def decimate(o, ratio):
    if ratio >= 1: return
    m = o.modifiers.new("Dec", "DECIMATE"); m.ratio = ratio
    with bpy.context.temp_override(object=o, active_object=o):
        bpy.ops.object.modifier_apply(modifier=m.name)


def drop_coll(c):
    for ch in list(c.children): drop_coll(ch)
    for o in list(c.objects): bpy.data.objects.remove(o, do_unlink=True)
    bpy.data.collections.remove(c)


def hub_from(rotor_obj, name, c):
    """The hub: the rotor mesh's BrushedMetal faces, in the same frame (origin at the pivot)."""
    me = rotor_obj.data.copy(); me.name = name
    keep = next(i for i, s in enumerate(rotor_obj.material_slots) if s.material and s.material.name == "BrushedMetal")
    bm = bmesh.new(); bm.from_mesh(me)
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if f.material_index != keep], context="FACES")
    for f in bm.faces: f.material_index = 0
    bm.to_mesh(me); bm.free()
    me.materials.clear(); me.materials.append(mats["BrushedMetal"])
    o = bpy.data.objects.new(name, me); c.objects.link(o); return o


def blade_obj(name, s, c):
    """One blade (blade 0), origin at its hinge: pre-remap generator coordinates (= VRage block axes) -> Blender (x, -z, y),
    the hinge (0, 0, R0 s) at the origin."""
    bm = bmesh.new()
    rb.blade(bm, R0 * s, R1 * s, CHORD * s, -B_ROOT, -B_TIP, 0.0, "rotor", droop=DROOP * s)
    for v in bm.verts: v.co = Vector((v.co.x, -(v.co.z - R0 * s), v.co.y))
    return rb.obj(name, bm, [mats["PaintedMetalDarker"], mats["WarningStripes"]], c, bevel=None, sharp=80.0)


def blur_mat():
    """The blur disc's material (its name is what the game maps; the colour is only Blender's preview)."""
    m = bpy.data.materials.get(BLUR_MAT)
    if m is None:
        m = bpy.data.materials.new(BLUR_MAT); m.diffuse_color = (0.05, 0.05, 0.06, 0.35)
    return m


def blur_obj(name, s, c, seg):
    """The blur disc: a thin conical annulus from the hinge radius to the tip, its height at radius r (from the hinge plane)
    the blade's at the reference coning: (r - R0 s) tan(cone) - droop f^2 (f the span fraction); its UVs flat from above,
    the tip at BLUR_TIP_RHO of the texture's half-width (the texture is the whole blur, the tip band included)."""
    ri, ro = R0 * s, R1 * s; band = R0 * s + rb.TIP_BAND * (R1 - R0) * s
    t = 0.004 * s; cone = math.tan(math.radians(CONE_REF_DEG))
    radii = [ri + (band - ri) * k / 6 for k in range(6)] + [band, ro]
    h = lambda r: (r - ri) * cone - DROOP * s * ((r - ri) / (ro - ri)) ** 2
    bm = bmesh.new(); uv = bm.loops.layers.uv.new("UVMap")
    rings = []
    for r in radii:
        top = [bm.verts.new(Vector((r * math.cos(2 * math.pi * j / seg), r * math.sin(2 * math.pi * j / seg), h(r) + t / 2))) for j in range(seg)]
        bot = [bm.verts.new(Vector((v.co.x, v.co.y, h(r) - t / 2))) for v in top]
        rings.append((r, top, bot))
    def face(vs, mi):
        f = bm.faces.new(vs); f.material_index = mi
        for l in f.loops: l[uv].uv = (0.5 + 0.5 * BLUR_TIP_RHO * l.vert.co.x / ro, 0.5 + 0.5 * BLUR_TIP_RHO * l.vert.co.y / ro)
    for (ra, ta, ba), (rb_, tb, bb) in zip(rings, rings[1:]):
        mi = 1 if ra >= band - 1e-6 else 0
        for j in range(seg):
            k = (j + 1) % seg
            face((ta[j], tb[j], tb[k], ta[k]), mi)    # top (normal up)
            face((ba[k], bb[k], bb[j], ba[j]), mi)    # bottom (normal down)
    for (r, tp, bt), outer in ((rings[0], False), (rings[-1], True)):
        for j in range(seg):
            k = (j + 1) % seg
            face((tp[j], tp[k], bt[k], bt[j]) if outer else (tp[k], tp[j], bt[j], bt[k]), 1 if outer else 0)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    for f in me.polygons: f.material_index = 0
    me.materials.append(blur_mat())
    me.shade_smooth()
    o = bpy.data.objects.new(name, me); c.objects.link(o); return o


def baked_pitch(f):
    """The blade's built-in pitch (deg, leading edge up) at span fraction f - blade()'s twist law with the angles negated."""
    tb, tr = math.tan(math.radians(B_TIP)), math.tan(math.radians(B_ROOT))
    return math.degrees(math.atan(tb + (tr - tb) * (1 - f) ** 2))


prop = json.load(open(PROP_SPEC))
# keep only the rotors' collections
for c in list(sc.collection.children):
    if c.name.split("|")[0] not in ROTORS: drop_coll(c)
spec, hide = {}, []
for N, s in ROTORS.items():
    pv = prop[N]["subpart"]                              # the pivot, VRage block space (no +0.125)
    for n in range(3):
        rc = bpy.data.collections.get(f"{N}|Rotor_LOD{n}")
        ro = next(o for o in rc.objects if o.type == "MESH")
        hc, bc, uc = coll(f"{N}|Hub_LOD{n}"), coll(f"{N}|Blade_LOD{n}"), coll(f"{N}|Blur_LOD{n}")
        sfx = f"_LOD{n}" if n else ""
        hub_from(ro, f"{N}_Hub{sfx}", hc)
        b = blade_obj(f"{N}_Blade{sfx}", s, bc); decimate(b, LOD_RATIOS[n])
        blur_obj(f"{N}_Blur{sfx}", s, uc, SEGS[n])
        drop_coll(rc)
        if n: hide += [hc.name, bc.name, uc.name]
    hinge_y = pv[1] + PLANE * s
    blades = []
    for k in range(3):
        a = 2 * math.pi * k / 3
        pos = [pv[0] + math.sin(a) * R0 * s, hinge_y, pv[2] + math.cos(a) * R0 * s]
        blades.append({"pos": [round(x, 5) for x in pos], "quat": [0.0, round(math.sin(a / 2), 6), 0.0, round(math.cos(a / 2), 6)]})
    spec[N] = {"cell": s, "hub": [round(x, 5) for x in pv], "blades": blades, "blur": [round(pv[0], 5), round(hinge_y, 5), round(pv[2], 5)],
               "hinge_radius": round(R0 * s, 4), "tip_radius": round(R1 * s, 4), "plane_below_hub": round(-PLANE * s, 4),
               "baked_pitch75_deg": round(baked_pitch((0.75 * R1 - R0) / (R1 - R0)), 3), "blur_cone_deg": CONE_REF_DEG,
               "spin": "clockwise from above (about VRage -Y), leading edge (-X of blade 0) first"}
    print(f"ROTORSPLIT {N}: hub {pv}, hinge y {hinge_y:.4f}, hinge r {R0 * s:.3f}, tip {R1 * s:.3f}")


def lc_of(nm, layer=None):
    layer = layer or bpy.context.view_layer.layer_collection
    if layer.collection.name == nm: return layer
    for ch in layer.children:
        r = lc_of(nm, ch)
        if r: return r
for nm in hide:
    l = lc_of(nm)
    if l: l.exclude = True
json.dump(spec, open(SPEC_OUT, "w"), indent=1)
bpy.ops.wm.save_as_mainfile(filepath=OUT, copy=False)
print(f"ROTORSPLIT saved {OUT} + {SPEC_OUT}")
