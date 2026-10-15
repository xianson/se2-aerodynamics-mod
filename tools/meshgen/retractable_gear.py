"""Retractable gear BLOCKOUTS (SE2): primitive, exactly dimensioned starting points to kitbash on - one per vanilla
wheel size (1 / 1.5 / 2.5 m). FORWARD FOLD (2026-10-13, the user: "stowed forward and then rotating down"): the leg is
hinged at the well's top rear and swings 90 deg from pointing down to pointing forward. The vanilla wheel can only move
in a straight line (along its suspension axis - AeroGearComponent slides it by the constraint's limits), so the axis is
RAKED along the straight line between the wheel's down and stowed positions - 45 deg up and forward for a 90 deg fold -
and the visible leg turns about the hinge to point at the wheel's hub, telescoping as it goes (the "raked slide").

Run headless:  blender -b --factory-startup -P retractable_gear.py -- <out_dir> [100 150 250]

Each size becomes <out_dir>/RetractableGear_<size>_FoldForward.blend (+ _preview.png) holding:
  Well          the block: a box open at the bottom (outer PaintedMetalColorable, inner PaintedMetalDarker)
  Doors         Door_L / Door_R: clamshell panels closing the bottom; each object's origin on its hinge edge (the long
                outer edges), so rotating it about its local Y opens it
  Strut         Leg: the oleo's outer cylinder, its origin ON the hinge (it turns about its local X), with its knuckle;
                Rod: the chrome piston + the axle stub, its origin at the axle's end (where the vanilla wheel attaches) -
                it rides the wheel along the raked axis and turns with the leg
  Collision     one box over the well (the block's cells)
  Reference     WheelRef (down), WheelRef_Stowed (forward, in the well): the vanilla wheel's size (wireframe; not
                exported); RakeLine: the wheel's straight path between them
  Dummies       Wheel Rest Position (the vanilla suspension's pivot, its local up ALONG the rake: centre = rest + axis x
                travel / 2, scale Y = the vanilla travel), Gear_Leg (the hinge), Gear_Rod, Gear_DoorL, Gear_DoorR
Blender axes: X = across (the axle and the hinge), Y = forward, Z = up; the well's envelope is centred at the origin;
VRage = (x, z, -y): Blender forward +Y is the block's Forward (-Z). Numbers: SIZES (wheel diameter / width measured
from the vanilla models); the gear's numbers for the mod (AeroGearVariants) are printed per size.
"""
import bpy, bmesh, math, os, sys
from mathutils import Matrix, Vector

# wheel diameter D and width Wd (m, the vanilla Wheel100/150/250 models), the vanilla suspension travel (m), and the
# well in SMALL BLOCKS (0.5 m; the user, 2026-10-13: tight, "think about it in 0.5 blocks") - across x forward x up: the
# fewest whole blocks holding the upright stowed wheel (up), the centred wheel and its fork (across), and the deployed
# wheel under the rear + the leg + the stowed wheel ahead (forward)
SIZES = {
    "100": dict(D=0.99, Wd=0.64, travel=0.25, blocks=(2, 6, 3)),
    "150": dict(D=1.50, Wd=1.14, travel=0.40, blocks=(4, 8, 4)),
    "250": dict(D=2.52, Wd=1.80, travel=0.60, blocks=(5, 12, 6)),
}
BLOCK = 0.5         # the small block (m)
DROP = 0.05         # (the stowed leg's length: as if the wheel hung DROP under the floor with a fixed leg)
OVERLAP = 0.2       # (each telescope stage keeps this fraction of itself inside the one before, fully out)
EXT = 2.0           # the telescoping leg: deployed length / stowed (two stages; 1.6 one, ~3.5 four as the vanilla piston)


def dims(c):
    D, Wd = c["D"], c["Wd"]; R = D / 2
    r_leg = max(0.06, 0.12 * D)                            # the oleo (centre line)
    r_arm = max(0.04, 0.05 * D)                            # the fork's arms (either side of the wheel)
    W, L, H = (n * BLOCK for n in c["blocks"])
    arm_x = min(Wd / 2 + r_arm + 0.04, W / 2 - r_arm - 0.02)   # the arms' centre lines (inside the well)
    bottom, top = -H / 2, H / 2
    CLEAR = (H - D) / 2                                    # (what the block count leaves above and below the wheel)
    hinge_z = top - CLEAR - R                              # the stowed hub's height (the leg lies level, forward)
    leg = hinge_z - (bottom - DROP - R)                    # hinge -> hub: down, the wheel's top DROP under the floor
    rear = -L / 2
    slack = L - (2 * R + leg)                              # (the length to spare, half at each end)
    if slack < 0: raise SystemExit(f"the well is too short by {-slack:.2f} m")
    hinge_y = rear + slack / 2 + R                         # (the deployed wheel inside the footprint, under the rear)
    x_face = Wd / 2                                        # the wheel's inner face / where the vanilla wheel attaches
    # the leg stows level at its short length and deploys straight down telescoped to EXT x it: the wheel's straight
    # path between the two is the rake (atan(1 / EXT) from vertical - 27 deg at 2x)
    leg_down = EXT * leg
    down = (hinge_y, hinge_z - leg_down); stow = (hinge_y + leg, hinge_z)
    retract = math.hypot(stow[0] - down[0], stow[1] - down[1])
    crown = leg_down - R - CLEAR                           # hinge -> the fork's crown (just above the deployed wheel)
    # the telescope: the outer fits the stowed leg (hinge -> stowed crown); n nested stages as long as it, each keeping
    # >= OVERLAP of itself inside the one before - as few as cover the extension (the leg's (EXT - 1) x leg)
    Lo = leg - R - CLEAR
    stages = math.ceil((crown - Lo) / ((1 - OVERLAP) * Lo))
    Ls = Lo - 0.02; ext = (crown - Lo) / stages
    return dict(W=W, L=L, H=H, R=R, Wd=Wd, D=D, r_leg=r_leg, r_arm=r_arm, arm_x=arm_x, crown=crown, bottom=bottom,
                top=top, rear=rear, hinge_y=hinge_y, hinge_z=hinge_z, leg=leg, down=down, stow=stow, retract=retract,
                x_face=x_face, Lo=Lo, stages=stages, Ls=Ls, ext=ext, x_wheel=0.0, x_strut=0.0, travel=c["travel"], t=max(0.04, 0.025 * H))


HINGE_DIR = r"D:\SteamLibrary\steamapps\common\Space Engineers 2 - Mod SDK\GameData\Vanilla\Assets\Blocks\Hinge\250\Models\NonFractured"


def import_hinge_part(part, M):
    """The vanilla hinge's Base or Head (LOD0) as meshes baked to world space by M - Keen's 2.5 m hinge: pivot at its
    centre, drum axis Y, base mount -Z, head mount +Z. Its FBX units vary: sized by its own extent to the 2.5 m block
    (as control_surface.py)."""
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=os.path.join(HINGE_DIR, part, f"Hinge_250_{part}_LOD0.fbx"), global_scale=100.0)
    new = [o for o in bpy.data.objects if o not in before]
    bpy.context.view_layer.update()
    pts = [o.matrix_world @ Vector(c) for o in new if o.type == "MESH" for c in o.bound_box]
    ext = max(max(q[i] for q in pts) - min(q[i] for q in pts) for i in range(3))
    M = M @ Matrix.Scale(10.0 ** round(math.log10(2.5 / ext)), 4)
    meshes = []
    for o in new:
        if o.type == "MESH":
            W = o.matrix_world.copy(); o.parent = None
            o.data = o.data.copy(); o.data.transform(M @ W); o.matrix_world = Matrix.Identity(4)
            if W.determinant() < 0: o.data.flip_normals()
            meshes.append(o)
    for o in new:
        if o.type != "MESH": bpy.data.objects.remove(o)
    return meshes


def join(objs, name, col):
    for o in objs:
        for c in list(o.users_collection): c.objects.unlink(o)
        col.objects.link(o)
    if not objs: return None
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs: o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.join()
    objs[0].name = name
    return objs[0]


def mat(name, rgb):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.diffuse_color = (*rgb, 1.0)
    return m


def link(obj, col): col.objects.link(obj); return obj


def box(name, size, at, material, col):
    bm = bmesh.new(); bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts: v.co = Vector((v.co.x * size[0], v.co.y * size[1], v.co.z * size[2]))
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    o = bpy.data.objects.new(name, me); o.location = at; me.materials.append(material)
    return link(o, col)


def cylinder(name, radius, depth, at, axis, material, col, segments=24):
    bm = bmesh.new(); bmesh.ops.create_cone(bm, cap_ends=True, segments=segments, radius1=radius, radius2=radius, depth=depth)
    if axis == "X": bmesh.ops.rotate(bm, verts=bm.verts, cent=(0, 0, 0), matrix=__import__("mathutils").Matrix.Rotation(math.pi / 2, 3, "Y"))
    elif axis == "Y": bmesh.ops.rotate(bm, verts=bm.verts, cent=(0, 0, 0), matrix=__import__("mathutils").Matrix.Rotation(math.pi / 2, 3, "X"))
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    o = bpy.data.objects.new(name, me); o.location = at; me.materials.append(material)
    return link(o, col)


def empty(name, at, scale=(1, 1, 1), col=None, size=0.2):
    e = bpy.data.objects.new(name, None); e.location = at; e.scale = scale; e.empty_display_size = size
    e.empty_display_type = "ARROWS"; (col or bpy.context.scene.collection).objects.link(e); return e


def build(size):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    d = dims(SIZES[size]); sc = bpy.context.scene
    cols = {}
    for n in ("Well", "Doors", "Strut", "Collision", "Reference", "Dummies"):
        cols[n] = bpy.data.collections.new(n); sc.collection.children.link(cols[n])
    outer, inner = mat("PaintedMetalColorable", (0.55, 0.57, 0.6)), mat("PaintedMetalDarker", (0.25, 0.26, 0.28))
    chrome = mat("Chrome", (0.85, 0.86, 0.88))

    # the well: a box, its bottom face removed, thickened inward (inner faces get the darker material)
    W, L, H, t = d["W"], d["L"], d["H"], d["t"]
    bm = bmesh.new(); bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts: v.co = Vector((v.co.x * W, v.co.y * L, v.co.z * H))
    bm.faces.ensure_lookup_table()
    bottom_face = min(bm.faces, key=lambda f: f.calc_center_median().z)
    bmesh.ops.delete(bm, geom=[bottom_face], context="FACES")
    me = bpy.data.meshes.new("Well"); bm.to_mesh(me); bm.free()
    well = link(bpy.data.objects.new("Well", me), cols["Well"])
    me.materials.append(outer); me.materials.append(inner)
    s = well.modifiers.new("Wall", "SOLIDIFY"); s.thickness = t; s.offset = -1.0; s.material_offset = 1; s.use_even_offset = True

    # the doors: two halves of the floor, each hinged on its outer long edge (origin there)
    for side, sign in (("L", -1), ("R", 1)):
        door = box(f"Door_{side}", (W / 2 - 0.01, L - 0.02, t), (0, 0, 0), outer, cols["Doors"])
        for v in door.data.vertices: v.co.x += -sign * (W / 4)          # (the panel inward of its hinge)
        door.location = (sign * W / 2, 0, d["bottom"] + t / 2)
        empty(f"Gear_Door{side}", door.location, col=cols["Dummies"])

    # the strut: the oleo ON the hinge, down the centre line to the fork's crown (deployed; it turns about X to point
    # forward). The rod subpart is the piston + crown + fork + axle, its origin at the axle's attachment (the wheel's
    # inner face, +X): it rides the wheel and turns with the leg.
    hinge = Vector((0.0, d["hinge_y"], d["hinge_z"]))
    oleo = d["Lo"]                                         # (the outer cylinder fits the stowed leg; the stages slide out of it)
    leg = cylinder("Leg", d["r_leg"], oleo, (0, 0, 0), "Z", outer, cols["Strut"])
    for v in leg.data.vertices: v.co.z -= oleo / 2
    knuckle = cylinder("Leg_Knuckle", d["r_leg"] * 1.3, 2.4 * d["r_leg"], (0, 0, 0), "X", outer, cols["Strut"])
    knuckle.parent = leg
    leg.location = hinge
    empty("Gear_Leg", hinge, col=cols["Dummies"])
    # (the rod's parts are built about the deployed hub, then moved so the object's origin is the attachment point)
    hub = Vector((0.0, d["down"][0], d["down"][1])); attach = Vector((d["x_face"], d["down"][0], d["down"][1]))
    crown_z = d["hinge_z"] - d["crown"]
    # the telescope: n nested stages, each as long as the outer (they all fit inside the stowed leg), each sliding out
    # d["ext"] of the one before; the last is the rod (it carries the fork). Deployed: stage k's bottom at
    # hinge - Lo - k x ext. Stages 1..n-1 are their own objects (origin at their top, on the strut's centre line; the
    # client sets each between the leg and the rod: Gear_Stage<k>)
    n, Ls, e = d["stages"], d["Ls"], d["ext"]
    for k in range(1, n):
        z_bot = d["hinge_z"] - oleo - k * e
        st = cylinder(f"Stage{k}", d["r_leg"] * (1 - 0.15 * k), Ls, (0, 0, 0), "Z", chrome, cols["Strut"])
        for v in st.data.vertices: v.co.z -= Ls / 2
        st.location = (0, d["hinge_y"], z_bot + Ls); empty(f"Gear_Stage{k}", st.location, col=cols["Dummies"])
    piston_len = Ls
    parts = [cylinder("Rod", d["r_leg"] * (1 - 0.15 * n), piston_len, (0, d["hinge_y"], crown_z + piston_len / 2), "Z", chrome, cols["Strut"]),
             box("Fork_Crown", (2 * (d["arm_x"] + d["r_arm"]), 2.2 * d["r_arm"], 2 * d["r_arm"]), (0, d["hinge_y"], crown_z), outer, cols["Strut"])]
    for sx in (-1, 1):
        parts.append(cylinder(f"Fork_Arm_{'L' if sx < 0 else 'R'}", d["r_arm"], crown_z - hub.z, (sx * d["arm_x"], d["hinge_y"], (crown_z + hub.z) / 2), "Z", outer, cols["Strut"]))
    parts.append(cylinder("Axle", 0.6 * d["r_arm"], 2 * d["arm_x"], (0, hub.y, hub.z), "X", chrome, cols["Strut"]))
    rod = parts[0]
    for p in parts:   # (every part in the rod object's frame, origin at the attachment; the meshes carry no rotation)
        for v in p.data.vertices: v.co = v.co + p.location - attach
        p.location = (0, 0, 0)
    for p in parts[1:]: p.parent = rod
    rod.location = attach
    empty("Gear_Rod", attach, col=cols["Dummies"])

    # the kitbash: Keen's vanilla hinge on the gear's hinge line - its drum axis across the well (Blender Y -> X), the
    # base mounted up to the ceiling (it stays with the well), the head down onto the leg (parented: it turns with it)
    s_h = 4.0 * d["r_leg"] / 2.5
    M = Matrix.Translation(hinge) @ Matrix.Rotation(math.pi / 2, 4, "Z") @ Matrix.Rotation(math.pi, 4, "X") @ Matrix.Scale(s_h, 4)
    if os.path.isdir(HINGE_DIR):
        base = join(import_hinge_part("Base", M), "Hinge_Base", cols["Well"])
        head = join(import_hinge_part("Head", M), "Hinge_Head", cols["Strut"])
        for v in head.data.vertices: v.co = v.co - hinge
        head.location = (0, 0, 0); head.parent = leg
    else:
        print("GEAR: no vanilla hinge FBX at " + HINGE_DIR + " - blockout without it")

    # the reference wheel down and stowed, and the straight path between them
    for nm, (y, z) in (("WheelRef", d["down"]), ("WheelRef_Stowed", d["stow"])):
        w = cylinder(nm, d["R"], d["Wd"], (d["x_wheel"], y, z), "X", outer, cols["Reference"], segments=32)
        w.display_type = "WIRE"; w.hide_render = True
    lm = bpy.data.meshes.new("RakeLine"); lm.from_pydata([(d["x_face"], *d["down"]), (d["x_face"], *d["stow"])], [(0, 1)], [])
    link(bpy.data.objects.new("RakeLine", lm), cols["Reference"])
    # the vanilla suspension's pivot dummy, its up along the rake: the wheel rests at its centre - axis x travel / 2
    axis = Vector((0, d["stow"][0] - d["down"][0], d["stow"][1] - d["down"][1])).normalized()
    centre = Vector((d["x_face"], d["down"][0], d["down"][1])) + axis * d["travel"] / 2
    rest = empty("Wheel Rest Position", centre, (0.1, d["travel"], 0.1), cols["Dummies"])
    rest.rotation_euler = (math.atan2(axis.z, axis.y) - math.pi / 2, 0, 0)   # (its local Y onto the rake)

    col_box = box("Collision_Box", (W, L, H), (0, 0, 0), outer, cols["Collision"]); col_box.display_type = "WIRE"
    return d


def preview(path, d):
    sc = bpy.context.scene
    cam = bpy.data.objects.new("PreviewCam", bpy.data.cameras.new("PreviewCam")); sc.collection.objects.link(cam); sc.camera = cam
    size = max(d["W"], d["L"], d["H"]) * 2.2
    view = Vector((1.0, -0.6, 0.15)).normalized(); centre = Vector((0, 0, (d["top"] + d["down"][1] - d["R"]) / 2))
    cam.location = centre + view * size; cam.rotation_euler = (-view).to_track_quat("-Z", "Y").to_euler()
    for n, e, dr in (("k", 3.0, (0.6, -1, 1.2)), ("f", 1.2, (-1, 0.4, 0.3))):
        l = bpy.data.objects.new(n, bpy.data.lights.new(n, "SUN")); l.data.energy = e; sc.collection.objects.link(l)
        l.rotation_euler = (-Vector(dr)).to_track_quat("-Z", "Y").to_euler()
    sc.render.engine = "BLENDER_WORKBENCH"; sc.render.resolution_x = sc.render.resolution_y = 600
    sc.display.shading.color_type = "MATERIAL"; sc.display.shading.show_xray = True; sc.render.filepath = path
    bpy.ops.render.render(write_still=True)
    bpy.data.objects.remove(cam)


def main():
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    out = args[0] if args else os.getcwd(); which = args[1:] or list(SIZES)
    os.makedirs(out, exist_ok=True)
    for size in which:
        d = build(size)
        name = f"RetractableGear_{size}_FoldForward"
        preview(os.path.join(out, name + "_preview.png"), d)
        bpy.ops.wm.save_as_mainfile(filepath=os.path.join(out, name + ".blend"))
        print(f"GEAR {size}: well {d['W']:.2f} x {d['L']:.2f} x {d['H']:.2f} m (across x forward x up), wheel D {d['D']} m, "
              f"leg {d['leg']:.2f} m stowed / {EXT * d['leg']:.2f} m down, rake {math.degrees(math.atan(1 / EXT)):.0f} deg, outer {d['Lo']:.2f} m + {d['stages']} stages x {d['ext']:.2f} m out, retract {d['retract']:.2f} m, collision off from {d['retract'] - 0.15:.2f} m, "
              f"hinge (x {d['x_strut']:.2f}, y {d['hinge_y']:.2f}, z {d['hinge_z']:.2f}), axle end x {d['x_face']:.2f}")


main()
