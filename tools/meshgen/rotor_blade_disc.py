"""THE BLADES' COLLIDER (2026-10-23, the user: "a collision model so that you can shoot at the blades ... the broken blade radius ...
and just always have that collision on"). Run on a main rotor's export scene, saved in place (idempotent):

  blender -b HelicopterRotor_150_Export.blend -P rotor_blade_disc.py -- <stub radius from the axis, m>

Adds <N>_BladeDisc to N|Collision: a thin, convex disc (24 sides) on the spin axis at the blade plane (the blade dummies'
height), out to the stubs' end (BladeBreakBlocks.g.cs StubR) and BladeDiscThickness x cell thick. It does not spin; it only
gives shots and impacts something to hit - AeroRotorStrikeComponent.OnBlockDamaged turns the damage into the blade's wear there.
It lies inside the rotor's own disc-layer cells (no other block can be there).
"""
import bpy, bmesh, json, math, os, sys

STUB_R = float(sys.argv[sys.argv.index("--") + 1])
BladeDiscThickness = 0.15
O, C = bpy.data.objects, bpy.data.collections
N = next(c.name.split("|")[0] for c in C if c.name.endswith("|Blade_LOD0"))
spec = json.load(open(os.path.join(os.path.dirname(bpy.data.filepath), "propulsor_spec.json")))[N]
z = O["Dummy_RotorBlade1"].location.z   # (the blade plane, Blender up = the spin axis)
t = BladeDiscThickness * float(spec["cell_m"])
name = f"{N}_BladeDisc"
if name in O: O.remove(O[name], do_unlink=True)
bm = bmesh.new()
bmesh.ops.create_cone(bm, cap_ends=True, segments=24, radius1=STUB_R, radius2=STUB_R, depth=t)
me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
me.transform(__import__("mathutils").Matrix.Translation((0.0, 0.0, z)))
o = O.new(name, me); C[f"{N}|Collision"].objects.link(o); o["ExportName"] = name
bpy.ops.wm.save_mainfile()
print(f"DISC {N}: r {STUB_R:.3f} m, {t:.3f} m thick at z {z:.3f}")
