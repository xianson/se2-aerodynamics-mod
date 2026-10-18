"""Import Keen's vanilla parts (FBX, LOD0) into a .blend as loose kitbash pieces, ONE BLOCK AT A TIME: each block's parts
(a piston's base, head and stages; a hinge's base and head; a door's frame and panels; a wheel) come in ASSEMBLED - in
their own relative positions, each part its own object with Keen's pivot - at true size, in a "Kitbash" collection (not
part of any export collection), the blocks laid out in a row beside the model (+X).

Keen's FBX units vary (some cm, some metres, some LODs mis-tagged), so each BLOCK gets one power-of-ten scale: the one
that makes the union of its parts the block's nominal size (a lone stage or panel would fool a per-part guess).

  blender -b <file.blend> --python kitbash_import.py -- [--clear] NOMINAL_M a.fbx b.fbx ... :: NOMINAL_M c.fbx ... :: ...

--clear empties the Kitbash collection first. Saves the .blend in place.
"""
import bpy, math, os, sys
from mathutils import Matrix, Vector

args = sys.argv[sys.argv.index("--") + 1:]
clear = "--clear" in args
args = [a for a in args if a != "--clear"]
groups, cur = [], []
for a in args:
    if a == "::":
        if cur: groups.append(cur); cur = []
    else: cur.append(a)
if cur: groups.append(cur)

sc = bpy.context.scene
col = bpy.data.collections.get("Kitbash")
if col is None:
    col = bpy.data.collections.new("Kitbash"); sc.collection.children.link(col)
if clear:
    for o in list(col.objects): bpy.data.objects.remove(o)

xs = [(o.matrix_world @ Vector(c)).x for o in sc.objects if o.type == "MESH" and col not in o.users_collection for c in o.bound_box]
x = (max(xs) if xs else 0.0) + 1.0
for o in col.objects:   # (after what is already in the row)
    x = max(x, max((o.matrix_world @ Vector(c)).x for c in o.bound_box) + 0.75)

made_now = set()
for g in groups:
    nominal, files = float(g[0]), g[1:]
    parts = []
    for path in files:
        name = os.path.splitext(os.path.basename(path))[0].replace("_LOD0", "")
        if name in made_now: name = f"{name}_{g[0]}m"   # (the same file name at another size in this run: both kept)
        made_now.add(name)
        old = bpy.data.objects.get(name)
        if old is not None: bpy.data.objects.remove(old)
        before = set(bpy.data.objects)
        bpy.ops.import_scene.fbx(filepath=path, global_scale=100.0)
        new = [o for o in bpy.data.objects if o not in before]
        bpy.context.view_layer.update()
        meshes = [o for o in new if o.type == "MESH"]
        for o in new:
            if o.type != "MESH": continue
            W = o.matrix_world.copy(); o.parent = None
            o.data = o.data.copy(); o.data.transform(W); o.matrix_world = Matrix.Identity(4)
            if W.determinant() < 0: o.data.flip_normals()
            for c in list(o.users_collection): c.objects.unlink(o)
            col.objects.link(o)
        for o in new:
            if o.type != "MESH": bpy.data.objects.remove(o)
        if not meshes: print("KITBASH nothing in " + path); continue
        bpy.ops.object.select_all(action="DESELECT")
        for o in meshes: o.select_set(True)
        bpy.context.view_layer.objects.active = meshes[0]
        if len(meshes) > 1: bpy.ops.object.join()
        part = bpy.context.view_layer.objects.active; part.name = name
        for sl in part.material_slots:   # (imported duplicates -> the library material's name)
            m = sl.material
            if m and "." in m.name and m.name.rsplit(".", 1)[1].isdigit():
                base = m.name.rsplit(".", 1)[0]
                if base in bpy.data.materials: sl.material = bpy.data.materials[base]
        parts.append(part)
    if not parts: continue
    # one scale for the whole block, from the union of its parts
    pts = [Vector(v.co) for p in parts for v in p.data.vertices]
    ext = max(max(q[i] for q in pts) - min(q[i] for q in pts) for i in range(3))
    k = 10.0 ** round(math.log10(nominal / ext)) if ext > 0 else 1.0
    lo_x = min(q.x for q in pts) * k; hi_x = max(q.x for q in pts) * k
    for p in parts:
        p.data.transform(Matrix.Scale(k, 4))
        p.location.x += x - lo_x          # (the whole block moved together: its parts stay assembled)
    bpy.context.view_layer.update()       # (else the dimensions are the unscaled ones)
    for p in parts:
        d = p.dimensions
        print(f"KITBASH {p.name}: {d.x:.2f} x {d.y:.2f} x {d.z:.2f} m")
    x += (hi_x - lo_x) + 0.75

bpy.ops.wm.save_mainfile()
print("KITBASH saved " + bpy.data.filepath)
