"""DECAL HARVEST (2026-10-16, the rotorcraft detail pass): every decal patch on Keen's vanilla models (the AtlasDecal_* faces -
one mesh ~1 mm over the hull) split into islands (linked faces of one material) and stored flattened: each island's 2D
shape in metres in its own plane (x along its longest edge), its exact atlas UVs, its material. detail stamps place these
on our surfaces, so our decals are Keen's own. Also measures the trim sheets' texel density (metres per UV unit).

  blender -b <reference.blend> -P decal_harvest.py -- <out.json> [collection ...]
"""
import bpy, bmesh, json, math, sys, collections
from mathutils import Vector

args = sys.argv[sys.argv.index("--") + 1:]
OUT, COLS = args[0], args[1:] or None
stamps, dens = [], collections.defaultdict(list)
for col in bpy.data.collections:
    if COLS and col.name not in COLS: continue
    for o in col.objects:
        if o.type != "MESH" or not o.data.materials: continue
        names = [m.name.split(".")[0] if m else "" for m in o.data.materials]
        if not any(n.startswith("AtlasDecal") for n in names): continue
        bm = bmesh.new(); bm.from_mesh(o.data); bm.transform(o.matrix_world); uv = bm.loops.layers.uv.active
        seen = set()
        for f0 in bm.faces:
            mn = names[f0.material_index]
            if f0 in seen or not mn.startswith("AtlasDecal"): continue
            isl, stack = [], [f0]; seen.add(f0)
            while stack:
                f = stack.pop(); isl.append(f)
                for e in f.edges:
                    for g in e.link_faces:
                        if g not in seen and names[g.material_index] == mn: seen.add(g); stack.append(g)
            # the island's plane: the area-weighted normal; x along its longest edge
            n = sum((f.normal * f.calc_area() for f in isl), Vector()); 
            if n.length < 1e-9: continue
            n.normalize(); c = sum((f.calc_center_median() * f.calc_area() for f in isl), Vector()) / max(1e-12, sum(f.calc_area() for f in isl))
            le = max((e for f in isl for e in f.edges), key=lambda e: e.calc_length())
            t = (le.verts[1].co - le.verts[0].co); t -= n * t.dot(n); t.normalize(); b = n.cross(t)
            dev = max(abs((l.vert.co - c).dot(n)) for f in isl for l in f.loops)
            faces = [[[round((l.vert.co - c).dot(t), 5), round((l.vert.co - c).dot(b), 5), round(l[uv].uv.x, 5), round(l[uv].uv.y, 5)] for l in f.loops] for f in isl]
            xs = [p[0] for fc in faces for p in fc]; ys = [p[1] for fc in faces for p in fc]
            us = [p[2] for fc in faces for p in fc]; vs = [p[3] for fc in faces for p in fc]
            stamps.append(dict(id=len(stamps), mat=mn, src=f"{col.name}/{o.name}", size=[round(max(xs) - min(xs), 4), round(max(ys) - min(ys), 4)],
                               uv=[round(min(us), 4), round(min(vs), 4), round(max(us), 4), round(max(vs), 4)], curv=round(dev, 4), faces=faces))
            # texel density: metres per UV unit along u and v (trim sheets)
            for f in isl:
                ls = f.loops
                for i in range(len(ls)):
                    a_, b_ = ls[i], ls[(i + 1) % len(ls)]
                    du = abs(b_[uv].uv.x - a_[uv].uv.x); dv = abs(b_[uv].uv.y - a_[uv].uv.y); L = (b_.vert.co - a_.vert.co).length
                    if du > 1e-4 and dv < du * 0.02: dens[(mn, "u")].append(L / du)
                    if dv > 1e-4 and du < dv * 0.02: dens[(mn, "v")].append(L / dv)
        bm.free()
med = lambda xs: sorted(xs)[len(xs) // 2] if xs else None
density = {f"{k[0]}:{k[1]}": round(med(v), 4) for k, v in dens.items()}
json.dump(dict(stamps=stamps, density=density), open(OUT, "w"))
print("HARVEST", len(stamps), "islands;", collections.Counter(s["mat"] for s in stamps))
print("DENSITY m per UV unit", density)
