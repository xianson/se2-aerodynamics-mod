"""Put a split helicopter rotor back together for its icon (run before render_icon.py, the file is not saved): the hub at
Dummy_PropRotor, three blade copies at Dummy_RotorBlade1..3 (each turned so its span, Blender -Y, points out from the axis),
all in a collection "IconRotor":

  blender -b HelicopterRotor_150_Export.blend -P rotor_icon_assemble.py -P render_icon.py -- <out.png> keep=Body_LOD0,IconRotor
"""
import bpy, math
N = next(c.name.split("|")[0] for c in bpy.data.collections if c.name.endswith("|Blade_LOD0"))
c = bpy.data.collections.new("IconRotor"); bpy.context.scene.collection.children.link(c)
O = bpy.data.objects
hub = O[f"{N}_Hub"].copy(); hub.location = O["Dummy_PropRotor"].location; c.objects.link(hub)
for k in range(1, 4):
    d = O[f"Dummy_RotorBlade{k}"]; b = O[f"{N}_Blade"].copy(); c.objects.link(b)
    b.location = d.location; b.rotation_euler = (0, 0, math.atan2(d.location.y, d.location.x) + math.pi / 2)
