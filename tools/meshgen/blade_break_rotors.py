"""PER-BLADE BREAK MODELS for the helicopter rotors (2026-10-23, the user: strikes break blade tips and scatter them; 3 steps per
blade - whole / tip lost / stub). Run on a COPY of the approved BladeBreak.blend (D:\\Blender Brojects\\SE2\\BladeBreak), saved
in place:

  blender -b BladeBreak_v2.blend -P blade_break_rotors.py

For each main rotor (the longer blades of 2026-10-23, and the 1 m rotor) the blade meshes are taken fresh from its export
scene (TurbopropBlock\\HelicopterRotor_<N>_Export.blend) and cut again with the approved cutter (BladeBreak\\blade_break.py:
build + hulls - its FR1 / FR2 shares). The Reversible Rotor keeps its cut (its blades did not change). Then, per rotor:
  N|BladeTipLost_LODn   the stub with chunk A back on (the tip, chunk B, gone)
  N|BladeCCWStub_LODn, N|BladeCCWTipLost_LODn   those two mirrored across the chord (the counter-rotating set; main rotors)
and the record (BladeBreak\\blade_break_spec.json): per rotor the cut radii from the hinge (stub, split), the whole blade's
volume, each chunk's offset (centre of mass, blade frame), volume and half extents.
Export: blade_break_rotors_export.py (a visible Blender).
"""
import bpy, json, math, os, sys
from mathutils import Matrix, Vector

BB = r"D:\Blender Brojects\SE2\BladeBreak"
TP = r"D:\Blender Brojects\SE2\TurbopropBlock"
sys.path.insert(0, BB)
import blade_break as bb

MAIN = ["HelicopterRotor_050", "HelicopterRotor_100", "HelicopterRotor_150", "HelicopterRotor_250"]
ALL = MAIN + ["HelicopterTailRotor_050"]
C, O = bpy.data.collections, bpy.data.objects
SPEC = os.path.join(BB, "blade_break_spec.json")
B2V = Matrix(((1, 0, 0), (0, 0, 1), (0, -1, 0)))


def drop(c):
    for ch in list(c.children): drop(ch)
    for o in list(c.objects): O.remove(o, do_unlink=True)
    C.remove(c)


def fresh_blades(N):
    """SRC_N / OUT_N replaced: the export scene's Blade_LOD0..2 (the hinge at the origin, the span along -Y)."""
    for nm in ("SRC_" + N, "OUT_" + N):
        if nm in C: drop(C[nm])
    for c in [c for c in C if c.name.startswith(N + "|")]: drop(c)
    src = C.new("SRC_" + N); bpy.context.scene.collection.children.link(src)
    names = [f"{N}_Blade" + (f"_LOD{n}" if n else "") for n in range(3)]
    with bpy.data.libraries.load(os.path.join(TP, f"{N}_Export.blend"), link=False) as (fr, to):
        to.objects = [n for n in fr.objects if n in names]
    for n in range(3):
        o = next(x for x in to.objects if x.name.startswith(names[n]) and (n or "_LOD" not in x.name))
        c = C.new(f"{N}|Blade_LOD{n}"); src.children.link(c); c.objects.link(o)


def volume(me):
    v = 0.0
    for p in me.polygons:
        a = me.vertices[p.vertices[0]].co
        for i in range(1, len(p.vertices) - 1):
            v += a.dot(me.vertices[p.vertices[i]].co.cross(me.vertices[p.vertices[i + 1]].co))
    return abs(v) / 6


def copy_obj(src, name, coll, M=None):
    me = src.data.copy(); me.name = name
    if M is not None: me.transform(M)
    o = O.new(name, me); coll.objects.link(o); return o


def mirrored(src, name, coll):
    o = copy_obj(src, name, coll)
    o.scale = (-1.0, 1.0, 1.0)   # (Blender's own negative-scale apply: the winding and custom normals flip with it)
    with bpy.context.temp_override(object=o, active_object=o, selected_objects=[o], selected_editable_objects=[o]):
        bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    return o


spec = json.load(open(SPEC))
for N in MAIN:
    fresh_blades(N)
    spec[N] = bb.build(N, None)
    for lod in range(3):
        for k in ("ChunkA", "ChunkB"):
            for o in C[f"{N}|{k}_LOD{lod}"].objects: o.location = (0, 0, 0)
    bb.hulls(N)

for N in ALL:
    top = C["OUT_" + N]
    blade = O[f"{N}_Blade"]
    span = -min(v.co.y for v in blade.data.vertices)          # (the tip, from the hinge)
    r = spec[N]
    # the cut radii, from the chunks: the stub ends where chunk A begins, chunk B begins where chunk A ends (inner faces, along -Y)
    a, b = O[f"{N}_BladeChunkA"], O[f"{N}_BladeChunkB"]
    oa, ob = Vector(r["ChunkA"]["offset"]), Vector(r["ChunkB"]["offset"])
    ya = [-(v.co.y + oa.y) for v in a.data.vertices]; yb = [-(v.co.y + ob.y) for v in b.data.vertices]
    r["stub_r"] = round(sum(sorted(ya)[:max(1, len(ya) // 20)]) / max(1, len(ya) // 20), 4)    # (the jagged face's mean, its inner 5 %)
    r["split_r"] = round(sum(sorted(yb)[:max(1, len(yb) // 20)]) / max(1, len(yb) // 20), 4)
    r["tip_r"] = round(span, 4)
    r["blade_volume"] = volume(blade.data)
    for k, o in (("ChunkA", a), ("ChunkB", b)):
        xs = [v.co for v in o.data.vertices]
        he = [(max(p[i] for p in xs) - min(p[i] for p in xs)) / 2 for i in range(3)]
        r[k]["half_extents"] = [round(x, 5) for x in he]
        r[k]["half_extents_vrage"] = [round(abs(x), 5) for x in (B2V @ Vector(he))]
        r[k]["offset_vrage"] = list(B2V @ Vector(r[k]["offset"]))
        r[k]["frame_vrage"] = [list(x) for x in (B2V @ Matrix(r[k]["frame"]) @ B2V.inverted())]
    # the tip-lost blade (stub + chunk A back at its place) and the mirrored set
    for lod in range(3):
        sfx = f"_LOD{lod}" if lod else ""
        for nm in (f"{N}|BladeTipLost_LOD{lod}", f"{N}|BladeCCWStub_LOD{lod}", f"{N}|BladeCCWTipLost_LOD{lod}"):
            if nm in C: drop(C[nm])
        ct = C.new(f"{N}|BladeTipLost_LOD{lod}"); top.children.link(ct)
        st = O[f"{N}_BladeStub{sfx}"]; ca = O[f"{N}_BladeChunkA{sfx}"]
        s1 = copy_obj(st, f"{N}_BladeTipLost{sfx}", ct)
        s2 = copy_obj(ca, f"{N}_tmpA{sfx}", ct, Matrix.Translation(oa))
        with bpy.context.temp_override(active_object=s1, selected_editable_objects=[s1, s2], selected_objects=[s1, s2]):
            bpy.ops.object.join()
        if N in MAIN:
            cs = C.new(f"{N}|BladeCCWStub_LOD{lod}"); top.children.link(cs)
            cl = C.new(f"{N}|BladeCCWTipLost_LOD{lod}"); top.children.link(cl)
            mirrored(st, f"{N}_BladeCCWStub{sfx}", cs)
            mirrored(s1, f"{N}_BladeCCWTipLost{sfx}", cl)
    print(f"BREAK {N}: tip {r['tip_r']:.3f} stub {r['stub_r']:.3f} split {r['split_r']:.3f} m from the hinge; "
          f"chunk A {r['ChunkA']['volume'] / r['blade_volume'] * 100:.0f} %, B {r['ChunkB']['volume'] / r['blade_volume'] * 100:.0f} % of the blade")

# (2026-10-23, in game: the broken blades were MAGENTA - the export names a mesh part after its material, and the blades
#  appended from the export scenes brought their materials in as "PaintedMetalDarker.009" etc. beside the file's own; the
#  content builder matches exact names ("Material not assigned"). Every suffixed copy folds into its base material.)
import re
for m in list(bpy.data.materials):
    base = re.sub(r"\.\d{3}$", "", m.name)
    if base == m.name: continue
    keep = bpy.data.materials.get(base)
    if keep is None: m.name = base
    else: m.user_remap(keep); bpy.data.materials.remove(m)
print("MATERIALS", sorted(m.name for m in bpy.data.materials if m.users))

json.dump(spec, open(SPEC, "w"), indent=1)
bpy.ops.wm.save_mainfile()
