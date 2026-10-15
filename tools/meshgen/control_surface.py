"""Control-surface block presets (SE2): one shape, 3 span x 3 chord x 1 thick blocks, large (2.5 m) and small (0.5 m).

Run headless:  blender -b --factory-startup -P control_surface.py -- <out_dir> [large|small ...]

Each variant becomes <out_dir>/ControlSurface_<Size>_3x3x1.blend holding:
  Collections Body/Body_LOD0..3 and Flap/Flap_LOD0..3 (LOD1..3 excluded from the view layer: tick them to work on
  them), and Collision.
  Body_LOD0..3   the fixed part: a flat full-thickness mounting face at the front (it attaches to the wing's armor),
                 flat top and bottom, and a concave cove around the hinge
  Flap_LOD0..3   the moving part (a subpart in game): a round nose centred ON the hinge line, a straight taper to a
                 sharp trailing edge. Its object origin is the hinge, so rotating it about its local Y deflects it.
  Collision      one box over the whole 3x3x1 envelope: the game derives the block's occupied cells from its colliders,
                 and SE2 colliders do not move with a subpart anyway
  Dummy_Hinge    an empty on the hinge line (where the game spawns the flap subpart)
  Hinge_Base_{L,R}_LODn (in Body_LODn) and Hinge_Head_{L,R}_LODn (children of Flap_LODn): Keen's vanilla hinge
                 kitbashed at both end blocks, centred on the hinge line (scaled 0.2x on the small block)
Blender axes: X = chord (front / mounting face at -X), Y = span (the hinge axis), Z = up; the envelope is centred at
the origin. One material slot, PaintedMetalColorable (SE2's library material). Names and tags are placeholders until
they are matched to Keen's VRAGE-Tools conventions.
Also writes <name>_preview.png (workbench render, flap at +20 degrees) per variant.
"""
import bpy, bmesh, math, os, sys
from mathutils import Matrix, Vector

SIZES = {"large": 2.5, "small": 0.5}
P = dict(span_blocks=3, chord_blocks=3, thick_blocks=1,
         hinge_blocks=1.0,      # hinge station from the front face (blocks): the fixed part's chord; the flap gets the rest
         nose_frac=0.70,        # flap nose radius / half thickness
         cove_gap=0.12,         # cove radius = nose radius x (1 + this): clearance for deflection
         te_frac=0.02,          # trailing-edge thickness / block thickness
         lod_arc=(16, 8, 4, 2), # segments on the half-circle nose / cove per LOD
         material="PaintedMetalColorable",
         # kitbash: Keen's vanilla hinge (2.5 m cube, pivot at its centre, drum axis Y, base mount -Z, head mount +Z) at
         # each end block, centred on the hinge line. Base -> the body, head -> the flap (it turns with it).
         hinge_dir=r"D:\SteamLibrary\steamapps\common\Space Engineers 2 - Mod SDK\GameData\Vanilla\Assets\Blocks\Hinge\250\Models\NonFractured",
         hinge_blocks_y=(-1, 1),          # span stations (blocks from the centre) that get a hinge
         hinge_lod_offset={"large": 0, "small": 2})   # the small block (0.2x) uses the hinge's coarser LODs


def profiles(B, k):
    """(body, flap, hinge) in the XZ plane, metres, envelope centred at the origin."""
    L, T = P["chord_blocks"] * B, P["thick_blocks"] * B
    h = T / 2
    x0 = -L / 2
    xh = x0 + P["hinge_blocks"] * B
    r = P["nose_frac"] * h
    rc = r * (1 + P["cove_gap"])
    te = P["te_frac"] * T / 2
    # body, counter-clockwise seen from -Y: front face, bottom, cove (concave, around the hinge), top
    cove = [(xh - rc * math.sin(a), -rc * math.cos(a)) for a in (math.pi * i / k for i in range(k + 1))]  # bottom -> top
    body = [(x0, h), (x0, -h), (xh, -h)] + cove + [(xh, h)]
    # flap: nose half-circle from top to bottom through the front, then the taper to the trailing edge
    nose = [(xh - r * math.sin(a), r * math.cos(a)) for a in (math.pi * i / k for i in range(k + 1))]       # top -> bottom
    flap = nose + [(x0 + L, -te), (x0 + L, te)]
    return body, flap, (xh, 0.0)


def prism(name, prof, span, origin=(0.0, 0.0), uv_scale=1.0, y0=0.0):
    me = bpy.data.meshes.new(name); bm = bmesh.new()
    ox, oz = origin
    a = [bm.verts.new((x - ox, y0 - span / 2, z - oz)) for x, z in prof]
    b = [bm.verts.new((x - ox, y0 + span / 2, z - oz)) for x, z in prof]
    N = len(prof)
    per = [0.0]
    for i in range(1, N + 1):
        per.append(per[-1] + math.dist(prof[i - 1], prof[i % N]))
    uv = bm.loops.layers.uv.new("UVMap")
    for i in range(N):
        j = (i + 1) % N
        f = bm.faces.new((a[i], a[j], b[j], b[i]))
        for l, (u, v) in zip(f.loops, ((per[i], 0), (per[i + 1], 0), (per[i + 1], span), (per[i], span))):
            l[uv].uv = (u / uv_scale, v / uv_scale)
    for ring, rev in ((a, True), (b, False)):
        f = bm.faces.new(ring[::-1] if rev else ring)
        for l in f.loops: l[uv].uv = (l.vert.co.x / uv_scale, l.vert.co.z / uv_scale)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    for f in bm.faces: f.smooth = True
    for e in bm.edges:   # hard edges at creases over 30 degrees (corners, trailing edge, cove lips); the arcs stay smooth
        if len(e.link_faces) == 2 and e.calc_face_angle(0) > math.radians(30): e.smooth = False
    bmesh.ops.triangulate(bm, faces=[f for f in bm.faces if len(f.verts) > 4])
    bm.to_mesh(me); bm.free()
    ob = bpy.data.objects.new(name, me)
    ob.location = (ox, 0, oz)
    return ob


def join(objs, name):
    """Join meshes into the first (bpy.ops in a context override: works headless)."""
    objs = [o for o in objs if o.type == "MESH"]
    if len(objs) > 1:
        with bpy.context.temp_override(active_object=objs[0], selected_editable_objects=objs, selected_objects=objs):
            bpy.ops.object.join()
    objs[0].name = name; objs[0].data.name = name
    return objs[0]


def import_hinge_part(part, lod, M):
    """The vanilla hinge's Base or Head at one LOD as meshes baked to world space by M (Keen's FBX is in cm: x100)."""
    path = os.path.join(P["hinge_dir"], part, f"Hinge_250_{part}_LOD{lod}.fbx")
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=path, global_scale=100.0)
    new = [o for o in bpy.data.objects if o not in before]
    bpy.context.view_layer.update()
    # Keen's hinge LOD0 is tagged cm, LOD1..4 are tagged m with the same cm numbers (they import 100x too big): size
    # each file by its own extent, to the nearest power of ten that makes it the 2.5 m block
    pts = [o.matrix_world @ Vector(c) for o in new if o.type == "MESH" for c in o.bound_box]
    ext = max(max(q[i] for q in pts) - min(q[i] for q in pts) for i in range(3))
    M = M @ Matrix.Scale(10.0 ** round(math.log10(2.5 / ext)), 4)
    meshes = []
    for o in new:
        if o.type == "MESH":
            W = o.matrix_world.copy()
            o.parent = None
            o.data = o.data.copy(); o.data.transform(M @ W); o.matrix_world = Matrix.Identity(4)
            if W.determinant() < 0: o.data.flip_normals()
            meshes.append(o)
    for o in new:
        if o.type != "MESH": bpy.data.objects.remove(o)
    for o in meshes:   # imported duplicates (PaintedMetalColorable.001) -> one material per library name
        for sl in o.material_slots:
            m = sl.material
            if m and "." in m.name and m.name.rsplit(".", 1)[1].isdigit():
                base = m.name.rsplit(".", 1)[0]
                if base in bpy.data.materials: sl.material = bpy.data.materials[base]
                else: m.name = base
    return meshes


def build(size):
    B = SIZES[size]
    span, L, T = P["span_blocks"] * B, P["chord_blocks"] * B, P["thick_blocks"] * B
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.unit_settings.system = "METRIC"; scene.unit_settings.scale_length = 1.0
    mat = bpy.data.materials.new(P["material"])
    # one collection per model per LOD (VRAGE-Tools exports a LOD at a time, e.g. "Active Collection"); LOD1..3 are
    # EXCLUDED from the view layer, not hidden: its Fix Project Materials selects every view-layer mesh, and a hidden
    # object cannot be selected (material_slot_remove_unused: "context is incorrect")
    cols = {}
    for part in ("Body", "Flap"):
        parent = bpy.data.collections.new(part); scene.collection.children.link(parent)
        for lod in range(len(P["lod_arc"])):
            c = bpy.data.collections.new(f"{part}_LOD{lod}"); parent.children.link(c); cols[(part, lod)] = c
    cols["Collision"] = bpy.data.collections.new("Collision"); scene.collection.children.link(cols["Collision"])
    tris = []
    for lod, k in enumerate(P["lod_arc"]):
        body, flap, hinge = profiles(B, k)
        # the body block by block: a hinge station keeps only the strip in front of the hinge cube (the hinge's base
        # fills the rest); the others take the full profile with its cove
        x0, xf = -L / 2, hinge[0] - B / 2
        strip = [(x0, T / 2), (x0, -T / 2), (xf, -T / 2), (xf, T / 2)]
        pieces = []
        for j in range(P["span_blocks"]):
            yc = (j - (P["span_blocks"] - 1) / 2) * B
            prof = strip if round(yc / B) in P["hinge_blocks_y"] else body
            o = prism(f"Body_LOD{lod}_{j}", prof, B, uv_scale=B, y0=yc)
            scene.collection.objects.link(o); o.data.materials.append(mat); pieces.append(o)
        bo = join(pieces, f"Body_LOD{lod}")
        scene.collection.objects.unlink(bo)
        fo = prism(f"Flap_LOD{lod}", flap, span, origin=hinge, uv_scale=B)
        fo.data.materials.append(mat)
        for o, c in ((bo, "Body"), (fo, "Flap")):
            cols[(c, lod)].objects.link(o); o["LOD"] = lod
        fo["Subpart"] = "Flap"; fo["HingeAxis"] = "Y"
        # the vanilla hinges: +90 deg about Y (base mount -Z -> the front, head mount +Z -> aft), scaled to the block
        hl = min(4, lod + P["hinge_lod_offset"][size])
        hinge_tris = 0
        for yb in P["hinge_blocks_y"]:
            side = "L" if yb < 0 else "R"
            M = Matrix.Translation((hinge[0], yb * B, hinge[1])) @ Matrix.Rotation(math.pi / 2, 4, "Y") @ Matrix.Scale(B / 2.5, 4)
            base = join(import_hinge_part("Base", hl, M), f"Hinge_Base_{side}_LOD{lod}")
            head = join(import_hinge_part("Head", hl, M), f"Hinge_Head_{side}_LOD{lod}")
            for o in (base, head):
                for c in list(o.users_collection): c.objects.unlink(o)
            cols[("Body", lod)].objects.link(base)
            cols[("Flap", lod)].objects.link(head)
            # the head turns with the flap: its mesh in the flap's frame, parented to it
            head.data.transform(fo.matrix_world.inverted()); head.parent = fo
            head.matrix_parent_inverse = Matrix.Identity(4); head.matrix_basis = Matrix.Identity(4)
            base["LOD"] = head["LOD"] = lod; base["Kitbash"] = head["Kitbash"] = f"Hinge_250 LOD{hl}"
            hinge_tris += sum(len(p.vertices) - 2 for o in (base, head) for p in o.data.polygons)
        tris.append((lod, len(bo.data.polygons), len(fo.data.polygons), hinge_tris, hl))
    # collider: the whole envelope
    me = bpy.data.meshes.new("Collision"); bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0); bm.to_mesh(me); bm.free()
    col = bpy.data.objects.new("Collision", me); cols["Collision"].objects.link(col)
    col.scale = (L, span, T); col.display_type = "WIRE"; col["Collider"] = "Box"; col.hide_render = True
    # hinge dummy
    hd = bpy.data.objects.new("Dummy_Hinge", None); scene.collection.objects.link(hd)
    hd.empty_display_type = "SINGLE_ARROW"; hd.empty_display_size = B * 0.5
    hd.location = (hinge[0], 0, hinge[1]); hd.rotation_euler = (-math.pi / 2, 0, 0)   # arrow along +Y (the hinge axis)
    hd["Subpart"] = "Flap"
    for (part, lod), c in ((k, v) for k, v in cols.items() if isinstance(k, tuple)):
        if lod: scene.view_layers[0].layer_collection.children[part].children[c.name].exclude = True
    return tris, (hinge[0], hinge[1]), (L, span, T)


def preview(path, B):
    scene = bpy.context.scene
    f = bpy.data.objects["Flap_LOD0"]; f.rotation_euler[1] = math.radians(20)
    cam = bpy.data.objects.new("PreviewCam", bpy.data.cameras.new("PreviewCam")); scene.collection.objects.link(cam)
    cam.location = Vector((3.2 * B, -4.4 * B, 3.0 * B))
    cam.rotation_euler = (-cam.location).to_track_quat("-Z", "Y").to_euler()
    scene.camera = cam
    scene.render.engine = "BLENDER_WORKBENCH"; scene.render.resolution_x, scene.render.resolution_y = 900, 560
    scene.display.shading.light = "STUDIO"; scene.display.shading.show_cavity = True
    scene.render.filepath = path; bpy.ops.render.render(write_still=True)
    f.rotation_euler[1] = 0.0
    bpy.data.objects.remove(cam)
    scene.render.engine = "BLENDER_EEVEE"   # (left on Workbench, the viewport offers no Material Preview: all grey)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    out = argv[0] if argv else os.path.dirname(os.path.abspath(__file__))
    sizes = [a for a in argv[1:] if a in SIZES] or list(SIZES)
    os.makedirs(out, exist_ok=True)
    for size in sizes:
        name = f"ControlSurface_{size.capitalize()}_3x3x1"
        tris, hinge, dims = build(size)
        preview(os.path.join(out, name + "_preview.png"), SIZES[size])
        bpy.ops.wm.save_as_mainfile(filepath=os.path.join(out, name + ".blend"))
        print(f"{name}: envelope {dims[0]:.2f} x {dims[1]:.2f} x {dims[2]:.2f} m (chord x span x thick), "
              f"hinge at x={hinge[0]:+.3f} z={hinge[1]:+.3f}")
        for lod, b, f, ht, hl in tris: print(f"  LOD{lod}: body {b} faces, flap {f} faces, hinges {ht} tris (vanilla LOD{hl})")


main()
