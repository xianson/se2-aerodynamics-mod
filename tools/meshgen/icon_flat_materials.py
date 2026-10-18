"""Run before render_icon.py on a scene whose SE2 materials have no textures here (the placeholder blockouts render
magenta): every material becomes a plain Principled BSDF in its look's colour. Never saves the .blend.
  blender -b <file.blend> --python icon_flat_materials.py --python render_icon.py -- <out.png> keep=...
"""
import bpy
LOOK = {"PaintedMetalColorable": ((0.62, 0.64, 0.66), 0.4, 0.45), "PaintedMetalDarker": ((0.12, 0.13, 0.14), 0.4, 0.5),
        "BrushedMetal": ((0.75, 0.75, 0.76), 1.0, 0.3), "WarningStripes": ((0.85, 0.65, 0.05), 0.2, 0.5),
        "Chrome": ((0.9, 0.9, 0.9), 1.0, 0.1), "PaintedMetalYellow": ((0.85, 0.62, 0.08), 0.2, 0.5), "Copper": ((0.72, 0.42, 0.25), 1.0, 0.35),
        # (the ramjets, 2026-10-17: the vanilla hydrogen thruster's cone materials, glass, plastic; decals as the dark trim)
        "HydrogenThrusterCone": ((0.30, 0.28, 0.27), 1.0, 0.35), "HydrogenThrusterConePlates": ((0.20, 0.20, 0.21), 0.9, 0.4),
        "HydrogenThrusterInjector": ((0.55, 0.35, 0.22), 1.0, 0.35), "ClearGlass": ((0.35, 0.45, 0.5), 0.0, 0.05),
        "Plastic": ((0.10, 0.10, 0.11), 0.0, 0.6), "AtlasDecal_Parts02": ((0.25, 0.26, 0.27), 0.5, 0.5),
        "AtlasDecal_Trims02": ((0.25, 0.26, 0.27), 0.5, 0.5), "AtlasDecal_Trims03": ((0.25, 0.26, 0.27), 0.5, 0.5),
        "AtlasDecal_Stickers01": ((0.55, 0.56, 0.57), 0.3, 0.5), "AtlasDecal_Stickers02": ((0.55, 0.56, 0.57), 0.3, 0.5)}
for m in bpy.data.materials:
    base = m.name.split(".")[0]
    col, metal, rough = LOOK.get(base, ((0.5, 0.5, 0.5), 0.3, 0.5))
    m.use_nodes = True; nt = m.node_tree; nt.nodes.clear()
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled"); out = nt.nodes.new("ShaderNodeOutputMaterial")
    bsdf.inputs["Base Color"].default_value = (*col, 1.0); bsdf.inputs["Metallic"].default_value = metal; bsdf.inputs["Roughness"].default_value = rough
    nt.links.new(bsdf.outputs[0], out.inputs[0])
