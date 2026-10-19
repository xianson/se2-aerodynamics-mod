"""The retractable gears' TEST definitions (2026-10-13: "start testing a dummy model while we wait" - the hi-fi model
comes later) for the "Aerodynamics Mod Blocks" project: Assets/Aero/RetractableGear/<size>/.

Each gear is the vanilla suspension block rebuilt on the base template the control surfaces use (BasePowerableBlockBreakable:
we have its sources - the vanilla suspension's own compiled definitions are diffs the builder cannot use without the
SDK's sources), with the suspension's own components added exactly as the vanilla template WheelCornerModule does
(Content\\Templates\\Blocks\\Breakable\\WheelCornerModule_*.def):
  server: WheelCornerModuleComponent (the per-size vanilla WheelCornerModuleDefinition: its wheel prefab, tyre, brakes),
          WheelOnGroundCollectorComponent; the prefab's WheelCornerModuleObjectBuilder (damping 0.4, stiffness 0.08)
  client: WheelCornerModuleAudioComponent (the per-size vanilla audio)
and the VANILLA block model, breakable and effects definitions referenced by GUID (its mesh, its "Wheel Rest Position"
dummy, its fractures): a dummy look, the real mechanics. Ours: the block definition (name, kind, power), the prefabs
(the aero mod injects AeroGearComponent / AeroGearAnimComponent by their GUIDs - AeroBlockGuids.Gear*Prefabs).

  python gen_gear_defs.py      then: python tools\\shared\\build_mod_content.py "<Aerodynamics Mod>"

Writes only its own folder's definition files (re-runnable, fixed GUIDs, nothing removed).
"""
import json, os, uuid

BLOCKS = r"C:\Users\slob\Documents\SpaceEngineers2\Mods\Aerodynamics Mod"   # (the block content lives in this mod since 2026-10-17: the "Aerodynamics Mod Blocks" companion was folded in)
ROOT_REL = os.path.join("Aero", "RetractableGear")
NS = uuid.UUID("ae200001-ae00-9e00-0000-00000000c533")
VER_META = "2.4.0.16"
B_GAME = {"Game2": "2.0.1.1811", "System.Runtime": "1.0.0.0", "VRage": "2.0.1.1811"}
B_VRAGE = {"System.Runtime": "1.0.0.0", "VRage": "2.0.1.1811"}
G = lambda sid, n: f"ae200001-ae00-{sid}-{n:04x}-000000000001"
SHARED = "9e00"
KIND, GROUP = G(SHARED, 0x20), G(SHARED, 0x21)

# BasePowerableBlockBreakable (the control surfaces' base; keys as gen_cs3_defs.py)
BASE = dict(binding="e90997d9-6c5d-4493-ba8b-8bda08151bed", client="57ffe0a6-678c-4090-afc8-b344b970f474",
            ccomp="5931874e-15e7-4b21-aee9-68a125b76778", server="1fa87190-7256-4125-9fda-29ce92b98f86",
            scomp="d6b1a072-ef93-47fe-836d-165b00dac2dc", fbd="794c5233-6a1c-4932-b37c-03a0e539d069")
SKEYS = ["b7bf405c-71aa-46a1-a5ec-4c690f2ab7e3", "e58b8f69-45da-40d4-82b0-f1a82564a53a", "d358358e-30b3-484a-b8b8-2e5568c7298b",
         "bc5652a0-1296-4040-adc3-a2b9c35d5afc", "4f2bc5f9-6a6d-4347-a1eb-5937cbce6727"]
CKEYS = ["b7bf405c-71aa-46a1-a5ec-4c690f2ab7e3", "e58b8f69-45da-40d4-82b0-f1a82564a53a", "a1687822-b4c2-4bcb-961e-3e44fae3221d",
         "d358358e-30b3-484a-b8b8-2e5568c7298b", "bc5652a0-1296-4040-adc3-a2b9c35d5afc", "4f2bc5f9-6a6d-4347-a1eb-5937cbce6727",
         "72e2ef60-d699-48a2-9959-4e246eecdacc", "24f8dfe2-6b3f-4764-a9db-6e4383dcae5d", "30a7eb7b-1873-479a-b7d8-fe5db6a0567e"]
# the vanilla WheelCornerModule template's own component keys (WheelCornerModule_ServerComposition / _ClientComposition)
K_WHEEL, K_GROUND, K_AUDIO = "7ce342ce-6693-49d1-8032-30202ae29d3c", "acfad556-a86e-44cc-8cb7-6df0ccf6bfd0", "fa416de9-a73c-4e7f-85c8-9786af0ffb33"
# our own placement (2026-10-13, the user): the vanilla suspension's (WheelCornerModule_PlacementDefinition) mirrors a
# block by turning it 180 deg about its up when mirrored left / right - the wheel facing out - which on a gear flips the
# fold backwards. Ours, as quarter turns (x, y, z) per mirror axis (PlacementDefinition.BlockSymmetriesDefinition):
# mirrored left / right - as it is; up / down - 180 deg about its forward; front / back - 180 deg about its up. And a
# 0.25 m snap by default (BestSnapSize, in cells): the 1 m gear, two 0.5 m cells wide, centres on a centreline.
PLACEMENT, SYMMETRIES = G("9e00", 0x50), G("9e00", 0x51)
BASE_BMD, BASE_BRK = "f104e970-bdcc-4a09-892d-8cc96e039998", "a74c6fd1-fa39-4f24-89cb-17a46e65cfd7"   # (BaseFunctionalBlockBreakable's, as gen_cs3_defs.py)
ELECTRICITY = "bcded093-f5c0-4997-af3a-a6fbd853ad66"
RECIPE = "5b4cadcc-ac0c-494e-9e32-269edb1cc5bb"   # (the vanilla 2.5 m suspension's, for every size while testing)

# per size: the vanilla suspension's definitions (GameData\Vanilla\Content\Blocks\SuspensionWheels\<size>)
SIZES = {
    "100": dict(sid="9e01", name="1 m", module="5301f8e8-a1de-4512-aa5a-57970a180110", model="154c16a1-95ba-4ee0-b990-0c92e2f5747a",
                breakable="65f4fc91-ccde-49d3-a961-89dac22a1c80", effects="b1986764-9a12-41a4-8197-a61e7a5464f5",
                audio="94c11831-256c-4d02-94da-20893c2d4cea", icon="{G}daa5d17a-9c39-4c00-8ec0-eaa37b90f116", size=1.0, pcu=600, kw=2),
    "150": dict(sid="9e02", name="1.5 m", module="a9c8413a-751c-4d41-9913-e00e320ced95", model="57d1f7c2-7ba4-4331-9fb9-9e64f2a9ac04",
                breakable="213ba009-ba62-4c77-a80d-b92ef8d13e11", effects="91cbb184-2726-416a-8b09-21013a095469",
                audio="d1f1b6fe-c1da-423c-994f-ddc0c8e1a1cc", icon="{G}d940a63f-67f4-4832-a93f-17c507b60a03", size=1.5, pcu=700, kw=3),
    "250": dict(sid="9e03", name="2.5 m", module="a240c373-8a4f-422b-83c2-caf6d609b121", model="e0580cfb-5cd8-4c77-80d8-1ac7969b6af1",
                breakable="b1f8f6ea-d417-47d2-8ff2-65cd98bcec94", effects="72935a0a-f38c-4e39-80f2-be34c3e30eeb",
                audio="1f244dd4-bc76-45b9-aa6e-8e15b7effd3c", icon="{G}f8f561de-44ca-4890-a3b1-10129e5173fb", size=2.5, pcu=900, kw=5),
    # the 0.5 m: no vanilla wheel that small - our own wheel (gen_wheel_050: the vanilla 1 m wheel's prefab with
    # wheel_half.py's half-size model) on our own corner module (the vanilla 1 m one pointing at it)
    "050": dict(sid="9e04", name="0.5 m", module=G("9e04", 0x60), model=None, breakable=None, effects=None,
                audio="94c11831-256c-4d02-94da-20893c2d4cea", icon=None, size=0.5, pcu=400, kw=1),
}


def write(path, obj):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f: json.dump(obj, f, indent=2)


def asset_meta(asset_id, processor, output):
    items = [{"$Type": "VRage:Keen.VRage.ContentPipeline.Project.Structure.FileInfoMetaDataItem", "FileHash": ""},
             {"$Type": "VRage:Keen.VRage.ContentPipeline.Project.Features.AssetProcessing.AssetProcessingMetaDataItem",
              "ProcessorInfo": {"Version": processor[1], "Identifier": processor[0]},
              "ContentFiles": {"MainOutput": {"FileHash": "", "FileName": output}, "AdditionalOutputs": []},
              "Problems": [], "DependenciesHashes": [], "AssetHash": "", "OverrideDetachedState": False, "IgnoredErrors": "None"}]
    return {"$Bundles": {"System.Runtime": "1.0.0.0", "VRage": VER_META}, "$Type": "VRage:Keen.VRage.ContentPipeline.Metafiles.MetaData",
            "$Value": {"AssetID": asset_id, "InternalItems": items}}


def folder_meta(path_rel):
    return {"$Bundles": {"System.Runtime": "1.0.0.0", "VRage": VER_META}, "$Type": "VRage:Keen.VRage.ContentPipeline.Metafiles.MetaData",
            "$Value": {"AssetID": str(uuid.uuid5(NS, "folder:" + path_rel.lower())), "InternalItems": []}}


def definition(path, obj, guid, partial):
    write(path, obj)
    out = os.path.splitext(os.path.basename(path))[0] + ".def"
    write(path + ".meta", asset_meta(guid, ("P_PARTIAL_DEF", 1) if partial else ("P_DEF", 1), out))


def partial(base, manip, dtype, bundles=B_GAME):
    return {"$Bundles": bundles, "$Type": "VRage:Keen.VRage.ContentPipeline.Definitions.PartialDefinitionDiff",
            "$Value": {"BaseDefinition": base, "PartialDefinitionKind": "Copy", "Manipulator": manip, "DefinitionType": dtype, "PriorityOverride": False}}


# ── the gears with their OWN model (2026-10-13: the user's kitbash, 2.5 m first): gear_export_prep.py -> gear_export.py ──
EXPORT = r"D:\Blender Brojects\SE2\RetractableGear\Export"
SPEC = r"D:\Blender Brojects\SE2\RetractableGear\Export\{size}\gear_spec.json"
OWN_MODEL = {"250", "150", "100", "050"}   # (the 1.5 / 1 m: the 2.5 m kitbash scaled 0.6 / 0.4 - gear_export_prep.py)
ICON_SRC = r"D:\Blender Brojects\SE2\RetractableGear\Export\RetractableGear_Icon.png"   # (render_icon.py on the kitbash rig, gear down)
DT_WHEEL = "d1365bc4-d1ca-4280-a3ba-cda3e559a01d"   # the vanilla "Wheel Rest Position" dummy type (WheelCornerModuleDefinition.WheelPivotDummy)
DT_LEG, DT_ROD = G(SHARED, 0x30), G(SHARED, 0x31)
K_CSP = G(SHARED, 0x40)                             # (composite-local key of ConditionalSubPartsComponent)
OFF = 0.125                                         # (model space: LOD0 and the dummies on the 0.25 m cell grid, as gen_cs3_defs.py)
MATERIALS = {
    "PaintedMetalColorable": "be89c5c3-25ce-42e9-93f9-72ba32c3d8a7", "PaintedMetalDarker": "cb933a73-bce4-4224-a7d8-c5083935dac8",
    "BrushedMetal": "0c3be477-8bff-4e2c-8a0e-d7d842905ae9", "ConveyorsAtlas": "3020a2ff-e3b5-4990-8f96-6da1338c3de2",
    "FracturedMaterial01": "0f2772fd-72d6-4ee9-9211-38d1781ba8b9", "Chrome": "3312f76c-b118-4616-bd95-adfb9b59a996",
    "AtlasDecal_Trims01": "35d26308-2dc4-4b01-af36-0c713d10e571", "AtlasDecal_Trims02": "aa49b17e-58dc-4da8-8ba2-3bd643325681",
    "AtlasDecal_Trims03": "6166bee4-12a2-4d4f-82c8-0b481aa5823f", "AtlasDecal_Parts01": "0d1f9867-1aee-4388-b5bb-4a7ce0cdc5e2",
    "AtlasDecal_Parts02": "8affab53-a343-4c9d-b513-2f82180c0326", "AtlasDecal_Stickers01": "2a953884-e538-4bec-a334-1fef9d3712ce",
    "AtlasDecal_Stickers02": "c3e749ff-35ff-469c-b7ae-84a5a5d829b3", "MetalDull": "0e9e7d42-9533-47ed-b814-cf4be55b6aa8",
    "Copper": "87b4f3bd-8eb2-4a46-bf00-4662bed27260", "Plastic": "ccf57c6c-f07d-430e-a67b-fcecc29b2d71",
    "AtlasButton01": "acd39110-3c0d-49af-b40b-f6754054511e", "Display01": "d6dd4bf9-a685-425f-96c1-d90f0a33f280",
    "EmissiveOff": "d2648963-ef2b-442d-af7b-ee3d79dad677", "WindowGlass": "83c075f5-f177-460d-94a7-8aca19fd4d6f",
    "AtlasFont_BarlowCondensed": "481b2a1f-b394-4d99-b58a-1c370e013315", "WheelRubber": "2059f068-33d6-47a9-9bb0-7ce53ff5e346",
}
SUBPART_OB = lambda: [
    {"Key": "38b268a8-6f59-49d1-9bf2-6f73d5ad8a75", "Value": {"$Type": "VRage:Keen.VRage.Core.Game.Components.HierarchyComponentObjectBuilder", "Children": []}},
    {"Key": "6668ab9d-dd60-462f-b6e8-7dfe1730f9c0", "Value": {"$Type": "VRage:Keen.VRage.Core.Game.Components.ChildTransformComponentObjectBuilder",
        "TransformWithEulerHint": {"Transform": {"Position": {"X": 0, "Y": 0, "Z": 0}, "Orientation": {"X": 0, "Y": 0, "Z": 0, "W": 1}}, "EulerHint": {"X": 0, "Y": 0, "Z": 0}}}},
    {"Key": "33905a1a-c47d-4ca5-a87c-b9c993baf1e5", "Value": None},
    {"Key": "17fac7aa-348d-4ae4-b687-531ad1601bd4", "Value": None},
    {"Key": "a72db338-5407-4268-87f1-2faeb2cac274", "Value": None}]
SUBPART_COMPONENTS = [("HierarchyComponent", "38b268a8-6f59-49d1-9bf2-6f73d5ad8a75", "VRage:Keen.VRage.Core.Game.Components.HierarchyComponent"),
                      ("ChildTransformComponent", "6668ab9d-dd60-462f-b6e8-7dfe1730f9c0", "VRage:Keen.VRage.Core.Game.Components.ChildTransformComponent"),
                      ("ModelRenderBaseComponent", "33905a1a-c47d-4ca5-a87c-b9c993baf1e5", "Game2:Keen.Game2.Client.GameSystems.Render.ModelRenderComponent"),
                      ("ModelComponent", "17fac7aa-348d-4ae4-b687-531ad1601bd4", "VRage:Keen.VRage.Game.Components.Models.ModelComponent"),
                      ("IRenderParent", "a72db338-5407-4268-87f1-2faeb2cac274", "Game2:Keen.Game2.Client.GameSystems.Render.ParentRenderAdapterComponent")]


def file_id(rel): return str(uuid.uuid5(NS, "file:" + rel.replace("\\", "/").lower()))


def file_meta(asset_id):
    return {"$Bundles": {"System.Runtime": "1.0.0.0", "VRage": VER_META}, "$Type": "VRage:Keen.VRage.ContentPipeline.Metafiles.MetaData",
            "$Value": {"AssetID": asset_id, "InternalItems": [{"$Type": "VRage:Keen.VRage.ContentPipeline.Project.Structure.FileInfoMetaDataItem", "FileHash": ""},
                {"$Type": "VRage:Keen.VRage.ContentPipeline.Project.Features.AssetProcessing.AssetProcessingMetaDataItem",
                 "ProcessorInfo": {"Version": 0, "Identifier": ""}, "ContentFiles": None, "Problems": [], "DependenciesHashes": [],
                 "AssetHash": "", "OverrideDetachedState": False, "IgnoredErrors": "None"}]}}


def model(path, guid, lods, materials, collider, offset):
    geo = [{"AssetReference": "{G}" + ref, "UsedMaterials": sorted(materials),
            "Transformations": {"Translation": dict(zip("XYZ", offset if i == 0 else (0.0, 0.0, 0.0))), "Rotation": {"X": 0, "Y": 0, "Z": 0}, "Scale": 1}}
           for i, ref in enumerate(lods)]
    write(path, {"$Bundles": {"System.Runtime": "1.0.0.0", "VRage": "2.0.1.118"}, "$Type": "VRage:Keen.VRage.ContentPipeline.Models.ModelAssetData",
                 "$Value": {"MaterialAssignments": [{"MaterialName": m, "MaterialReference": MATERIALS[m]} for m in sorted(materials)],
                            "MaterialStateOverrides": [], "GeometryAssets": geo, "SnapToGrid": False, "SuggestedModelSize": None,
                            "CollisionShapesAsset": {"AssetReference": ("{G}" + collider) if collider else None, "GroupsByFracture": [],
                                                     "GeneratePreciseCollider": bool(collider)}}})
    write(path + ".meta", asset_meta(guid, ("P_MODEL", 40), os.path.splitext(os.path.basename(path))[0] + ".vrm"))


def dummy(name, pos, typ, rot=(0, 0, 0, 1), scale=(1, 1, 1)):
    return {"Name": name, "Transform": {"Transform": {"Position": dict(zip("XYZ", pos)), "Orientation": dict(zip("XYZW", rot))},
                                        "EulerHint": {"X": 0, "Y": 0, "Z": 0}},
            "Scale": dict(zip("XYZ", scale)), "Type": typ, "SupportingFracture": None, "HighlightSection": None}


def quat_y_to(a):
    """The turn taking +Y onto unit a, as (x, y, z, w)."""
    import math
    ax, ay, az = a
    if ay > 0.999999: return (0, 0, 0, 1)
    q = (az, 0.0, -ax, 1 + ay)   # (Y x a, 1 + Y.a)
    n = math.sqrt(sum(v * v for v in q))
    return tuple(round(v / n, 7) for v in q)


def own_model(size, d, sid):
    """Copy the exported model in, write its .model files and the definitions that make it the block's: the block model
    (dummies: the wheel's rest position, the leg's hinge, the rod's hub), the breakable set (one piece), the two subparts
    the client spawns (ConditionalSubParts) and AeroGearAnimComponent turns."""
    import shutil
    spec = json.load(open(SPEC.format(size=size)))
    N = f"RetractableGear_{size}"; dest_rel = os.path.join(ROOT_REL, size); src = os.path.join(EXPORT, size, "Models")
    ids = {}
    for rt, _, files in os.walk(src):
        for fn in files:
            if not fn.lower().endswith((".fbx", ".gltf", ".bin")): continue
            rel_in = os.path.relpath(os.path.join(rt, fn), src); out = os.path.join(d, "Models", rel_in)
            os.makedirs(os.path.dirname(out), exist_ok=True); shutil.copy(os.path.join(rt, fn), out)
            rel = os.path.join(dest_rel, "Models", rel_in); ids[rel_in.replace("\\", "/")] = file_id(rel)
            write(out + ".meta", file_meta(file_id(rel)))
    lod = lambda var, n, stem: ids[f"{var}/{stem}{'_LOD%d' % n if n else ''}.fbx"]
    mats = spec["materials"]; off = (OFF, OFF, OFF); md = os.path.join(d, "Models")
    m_nf, m_fr, m_de, m_leg, m_rod = (G(sid, 0x100 + i) for i in range(5))
    model(os.path.join(md, "NonFractured", f"{N}.model"), m_nf, [lod("NonFractured", n, N) for n in range(4)], mats["Body"], ids[f"NonFractured/{N}_collision.gltf"], off)
    model(os.path.join(md, "Fractured", f"{N}_Fractured.model"), m_fr, [lod("Fractured", n, N + "_Fractured") for n in range(4)], mats["Body"], ids[f"Fractured/{N}_Fractured_collision.gltf"], off)
    model(os.path.join(md, "Deformed", f"{N}_Deformed.model"), m_de, [lod("Deformed", n, N + "_Deformed") for n in range(4)], mats["Body"], ids[f"Deformed/{N}_Deformed_collision.gltf"], off)
    model(os.path.join(md, "NonFractured", "Subpart01", f"{N}_S1_Leg.model"), m_leg, [lod("NonFractured/Subpart01", n, N + "_S1_Leg") for n in range(3)], mats["Leg"], None, (0.0, 0.0, 0.0))
    model(os.path.join(md, "NonFractured", "Subpart02", f"{N}_S2_Rod.model"), m_rod, [lod("NonFractured/Subpart02", n, N + "_S2_Rod") for n in range(3)], mats["Rod"], None, (0.0, 0.0, 0.0))

    bmd, brk, csp = G(sid, 0x15), G(sid, 0x16), G(sid, 0x17)
    P = lambda v: [round(x + OFF, 5) for x in v]
    dummies = [dummy("Wheel Rest Position", P(spec["wheel_rest_dummy"]), DT_WHEEL, quat_y_to(spec["rake_axis"]), (0.1, spec["travel"], 0.1)),
               dummy("GearLegHinge", P(spec["pivot"]), DT_LEG), dummy("GearRodHub", P(spec["hub_deployed"]), DT_ROD)]
    definition(os.path.join(d, f"{N}_BlockModelDefinition.partialdef"), partial(BASE_BMD, {"Guid": bmd, "Model": "{G}" + m_nf,
        "Dummies": {"$DeltaEncoded": True, "Keys": [G(sid, 0x41 + i) for i in range(len(dummies))],
                    "Changed": [{"Kind": "Insert", "Index": i, "Value": v} for i, v in enumerate(dummies)], "Removed": []},
        "FractureModel": "{G}" + m_fr, "DamagedModel": "{G}" + m_de,
        "BuildProgressModels": [
            {"Key": G(sid, 0x48), "Value": {"BuildRatioLowerBound": 0, "Model": "{G}" + m_fr, "PartChanges": {"_data": []}}},
            {"Key": G(sid, 0x49), "Value": {"BuildRatioLowerBound": 1, "Model": "{G}" + m_nf, "PartChanges": {"_data": []}}}]},
        "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.Models.BlockModelComponentDefinition"), bmd, True)
    definition(os.path.join(d, f"{N}_BreakableBlockDefinition.partialdef"), partial(BASE_BRK, {"Guid": brk,
        "CriticalFractures": {"$DeltaEncoded": True, "Removed": [], "Added": [{"Id": "Fracture_01"}, {"Id": "Fracture_02"}]}},
        "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.BreakableBlockDefinition"), brk, True)
    subs, sp = {}, os.path.join(d, "Subparts")
    for k, (role, m, dt) in enumerate((("Leg", m_leg, DT_LEG), ("Rod", m_rod, DT_ROD))):
        pref, comp, mcd = G(sid, 0x18 + 3 * k), G(sid, 0x19 + 3 * k), G(sid, 0x1a + 3 * k)
        subs[dt] = pref
        definition(os.path.join(sp, f"{N}_{role}_Client.def"), {"$Bundles": B_VRAGE,
            "$Type": "VRage:Keen.VRage.Core.Game.Definitions.PrefabDefinitionObjectBuilder",
            "$Value": {"Guid": pref, "_entity": {"Definition": comp, "ObjectBuilders": SUBPART_OB(), "DebugName": "Root"}}}, pref, False)
        definition(os.path.join(sp, f"{N}_{role}_Client_Composite.def"), {"$Bundles": B_GAME,
            "$Type": "VRage:Keen.VRage.DCS.Definitions.EntityCompositeDefinitionObjectBuilder",
            "$Value": {"Guid": comp, "TagSlots": [{"$Key": t, "$Value": k_} for t, k_, _ in SUBPART_COMPONENTS],
                       "Components": [{"Key": k_, "Value": {"Definition": mcd if t == "ModelComponent" else None, "Type": ty}} for t, k_, ty in SUBPART_COMPONENTS]}},
            comp, False)
        definition(os.path.join(sp, f"{N}_{role}_ModelComponentDefinition.def"), {"$Bundles": B_VRAGE,
            "$Type": "VRage:Keen.VRage.Game.Components.Models.ModelComponentDefinitionObjectBuilder",
            "$Value": {"Guid": mcd, "Model": "{G}" + m, "Dummies": []}}, mcd, False)
    definition(os.path.join(d, f"{N}_ConditionalSubPartsDefinition.def"), {"$Bundles": B_GAME,
        "$Type": "Game2:Keen.Game2.Client.WorldObjects.CubeBlocks.Effects.ConditionalSubPartsDefinitionObjectBuilder",
        "$Value": {"Guid": csp, "SubParts": subs, "MeshPartsToHideWhenActive": []}}, csp, False)
    print(f"{N}: own model - " + ", ".join(f"{x['Name']} {list(x['Transform']['Transform']['Position'].values())}" for x in dummies))
    return bmd, brk, csp


# the vanilla 1 m wheel's definitions (Content\Blocks\SuspensionWheels@) our 0.5 m wheel copies
W100 = dict(binding="ae63d11a-ed9c-4134-b899-1d170c9edab3", server="b7192c3a-fe3d-4d03-912e-5ab71af23f43", scomp="38f45994-0334-461e-bdf6-7ec4fcde9208",
            client="03e5c3b4-8ba6-4d66-91fb-ab92ad507fe9", ccomp="b664ded1-78cd-416f-a66d-25cb863875ae", mcd="e9fd9923-88b6-49f0-83ea-d25199516983",
            collider="33211f44-cd8b-417f-9025-32c301872341", module="5301f8e8-a1de-4512-aa5a-57970a180110")
W100_SKEYS = ["1afc026f-8012-4dec-9304-d8c3a4f68831", "f187949e-f943-405d-a055-ae10170c078c", "072ac9da-303a-48fc-bb5b-d5fdf4b2c374",
              "bb23b3f1-5af5-48de-a2ab-3ef4dac6514d", "08724a85-e8db-4f50-8fbe-9cd8db839436", "b98c3a33-f623-4d0a-abe5-113aabeab565"]
W100_CKEYS = ["1afc026f-8012-4dec-9304-d8c3a4f68831", "e04a0bf5-5d94-4d3b-bbaa-de15acec4b97", "f187949e-f943-405d-a055-ae10170c078c",
              "a8e5b84c-db63-4361-bf65-ad5810d28416", "072ac9da-303a-48fc-bb5b-d5fdf4b2c374", "bb23b3f1-5af5-48de-a2ab-3ef4dac6514d",
              "08724a85-e8db-4f50-8fbe-9cd8db839436", "3e6742bc-2e1f-400c-b63b-ddadd68a5278", "5f5bcbaa-007f-4607-8d7d-6c08ca8be1b2",
              "6a3d2168-8216-47c0-98b6-cbba4c0fabc2"]
W050_PIVOT = (0.14, 0.0, 0.0)   # (just inside its inner face, as the vanilla wheels': gear_export_prep.py's "050" wheel_pivot)


def gen_wheel_050():
    """The 0.5 m wheel: wheel_half.py's model (export: Export/050/Wheel) in copies of the vanilla 1 m wheel's prefab, model,
    collider (a quarter of its mass: half size) and corner module (pointing at this wheel; a quarter of its torque)."""
    import shutil
    sid = "9e04"; N = "RetractableGear_050_Wheel"
    d = os.path.join(BLOCKS, "Assets", ROOT_REL, "050", "Wheel"); dest_rel = os.path.join(ROOT_REL, "050", "Wheel")
    src = os.path.join(EXPORT, "050", "Wheel", "Models"); ids = {}
    for rt, _, files in os.walk(src):
        for fn in files:
            if not fn.lower().endswith((".fbx", ".gltf", ".bin")): continue
            rel_in = os.path.relpath(os.path.join(rt, fn), src); out = os.path.join(d, "Models", rel_in)
            os.makedirs(os.path.dirname(out), exist_ok=True); shutil.copy(os.path.join(rt, fn), out)
            rel = os.path.join(dest_rel, "Models", rel_in); ids[rel_in.replace("\\", "/")] = file_id(rel)
            write(out + ".meta", file_meta(file_id(rel)))
    m = G(sid, 0x105)
    mats = ["AtlasDecal_Parts01", "AtlasDecal_Parts02", "AtlasDecal_Trims02", "AtlasDecal_Trims03", "Chrome", "PaintedMetalColorable", "PaintedMetalDarker", "WheelRubber"]
    model(os.path.join(d, "Models", "NonFractured", f"{N}.model"), m, [ids[f"NonFractured/{N}{'_LOD%d' % n if n else ''}.fbx"] for n in range(4)],
          mats, ids[f"NonFractured/{N}_collision.gltf"], (0.0, 0.0, 0.0))
    mcd, col, scomp, spref, ccomp, cpref, bind = (G(sid, 0x61 + i) for i in range(7))
    definition(os.path.join(d, f"{N}_ModelComponentDefinition.partialdef"), partial(W100["mcd"], {"Guid": mcd, "Model": "{G}" + m,
        "Dummies": {"$DeltaEncoded": True, "Keys": ["851464db-4855-4cc2-8779-dc4c9e3d271c", "12b25a84-f6b2-456a-8795-c3388a040662"], "Changed": [
            {"Kind": "Update", "Index": 0, "Value": {"Transform": {"Transform": {"Position": dict(zip("XYZ", W050_PIVOT))}}, "Scale": {"X": 0.05, "Y": 0.05, "Z": 0.05}}},
            {"Kind": "Update", "Index": 1, "Value": {"Transform": {"Transform": {"Position": {"X": 0, "Y": 0, "Z": 0}}}, "Scale": {"X": 0.1, "Y": 0.1, "Z": 0.1}}}],
            "Removed": []}}, "VRage:Keen.VRage.Game.Components.Models.ModelComponentDefinition", B_VRAGE), mcd, True)
    definition(os.path.join(d, f"{N}_ColliderDefinition.partialdef"), partial(W100["collider"], {"Guid": col, "WheelMass": 50},
        "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks.Wheels.WheelColliderDefinition"), col, True)
    definition(os.path.join(d, f"{N}_ServerComposition.partialdef"), partial(W100["scomp"], {"Guid": scomp, "Components": {"$DeltaEncoded": True,
        "Keys": W100_SKEYS, "Changed": [{"Kind": "Update", "Index": 1, "Value": {"Definition": mcd}}, {"Kind": "Update", "Index": 3, "Value": {"Definition": col}}],
        "Removed": []}}, "VRage:Keen.VRage.DCS.Definitions.EntityCompositeDefinition"), scomp, True)
    definition(os.path.join(d, f"{N}_Server.partialdef"), partial(W100["server"], {"Guid": spref, "_entity": {"Definition": scomp}},
        "VRage:Keen.VRage.Core.Game.Definitions.PrefabDefinition", B_VRAGE), spref, True)
    definition(os.path.join(d, f"{N}_ClientComposition.partialdef"), partial(W100["ccomp"], {"Guid": ccomp, "Components": {"$DeltaEncoded": True,
        "Keys": W100_CKEYS, "Changed": [{"Kind": "Update", "Index": 2, "Value": {"Definition": mcd}}, {"Kind": "Update", "Index": 5, "Value": {"Definition": col}}],
        "Removed": []}}, "VRage:Keen.VRage.DCS.Definitions.EntityCompositeDefinition"), ccomp, True)
    definition(os.path.join(d, f"{N}_Client.partialdef"), partial(W100["client"], {"Guid": cpref, "_entity": {"Definition": ccomp}},
        "VRage:Keen.VRage.Core.Game.Definitions.PrefabDefinition", B_VRAGE), cpref, True)
    definition(os.path.join(d, f"{N}.partialdef"), partial(W100["binding"], {"Guid": bind, "ServerComposite": scomp, "ClientPrefab": cpref},
        "VRage:Keen.VRage.Multiplayer.Data.PrefabBindingDefinition", B_VRAGE), bind, True)
    definition(os.path.join(d, "RetractableGear_050_WheelCornerModuleDefinition.partialdef"), partial(W100["module"], {"Guid": SIZES["050"]["module"],
        "WheelPrefab": spref, "AxleFriction": 50, "MaxTorque": 50000},
        "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks.Wheels.WheelCornerModuleDefinition"), SIZES["050"]["module"], True)


def gen_size(size, c):
    sid = c["sid"]; N = f"RetractableGear_{size}"; d = os.path.join(BLOCKS, "Assets", ROOT_REL, size)
    fbd, bind, spref, cpref, scomp, ccomp = G(sid, 0x1), G(sid, 0x10), G(sid, 0x11), G(sid, 0x12), G(sid, 0x13), G(sid, 0x14)
    own = size in OWN_MODEL
    if own:
        bmd, brk, csp = own_model(size, d, sid)
        c = dict(c, model=bmd, breakable=brk, csp=csp)
        # its icon: the rig's render (P_PNG, as the control surfaces')
        import shutil
        icon_rel = os.path.join(ROOT_REL, size, "Icon", f"{N}_Icon.png"); icon = os.path.join(BLOCKS, "Assets", icon_rel)
        os.makedirs(os.path.dirname(icon), exist_ok=True); shutil.copy(ICON_SRC, icon)
        write(icon + ".meta", asset_meta(file_id(icon_rel), ("P_PNG", 2), f"{N}_Icon.png"))
        c["icon"] = "{G}" + file_id(icon_rel)
    definition(os.path.join(d, f"{N}.partialdef"), partial(BASE["binding"], {"Guid": bind, "ServerComposite": scomp, "ClientPrefab": cpref},
               "VRage:Keen.VRage.Multiplayer.Data.PrefabBindingDefinition", B_VRAGE), bind, True)
    # server: the base plus the suspension's two components (the vanilla template's keys, its types, the per-size wheel)
    definition(os.path.join(d, f"{N}_ServerComposition.partialdef"), partial(BASE["scomp"], {"Guid": scomp,
        "TagSlots": {"$DeltaEncoded": True, "RemovedKeys": [], "ModifiedKeys": [
            {"$Key": "WheelCornerModuleComponent", "$Value": K_WHEEL}, {"$Key": "WheelOnGroundCollectorComponent", "$Value": K_GROUND}]},
        "Components": {"$DeltaEncoded": True, "Keys": SKEYS + [K_WHEEL, K_GROUND], "Changed": [
            {"Kind": "Update", "Index": 2, "Value": {"Definition": c["model"]}},
            {"Kind": "Update", "Index": 3, "Value": {"Definition": c["breakable"]}},
            {"Kind": "Update", "Index": 4, "Value": {"Definition": fbd}},
            {"Kind": "Insert", "Index": 5, "Value": {"Definition": c["module"], "Type": "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks.Wheels.WheelCornerModuleComponent"}},
            {"Kind": "Insert", "Index": 6, "Value": {"Definition": None, "Type": "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks.Wheels.WheelOnGroundCollectorComponent"}}],
            "Removed": []}},
        "VRage:Keen.VRage.DCS.Definitions.EntityCompositeDefinition"), scomp, True)
    definition(os.path.join(d, f"{N}_Server.partialdef"), partial(BASE["server"], {"Guid": spref, "_entity": {"Definition": scomp, "ObjectBuilders": {
        "$DeltaEncoded": True, "Keys": SKEYS + [K_WHEEL, K_GROUND], "Changed": [
            {"Kind": "Insert", "Index": 5, "Value": {"$Type": "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks.Wheels.WheelCornerModuleObjectBuilder",
                "SuspensionDamping": 0.4, "SuspensionStiffness": 0.08, "TangentialAccelerationRate": 1, "CurrentSuspensionDistance": 0,
                "IsParked": False, "ResourceConsumptionState": "IdleAndSupplied"}},
            {"Kind": "Insert", "Index": 6, "Value": None}], "Removed": []}}},
        "VRage:Keen.VRage.Core.Game.Definitions.PrefabDefinition", B_GAME), spref, True)   # (Game2: the wheel's object builder)
    # client: the base plus the suspension's audio; the vanilla model, breakable and effects
    # (an own model: + ConditionalSubPartsComponent - it spawns the leg and the rod at their dummies; no vanilla effects,
    #  whose dummies the model does not have)
    ckeys = CKEYS + [K_AUDIO] + ([K_CSP] if own else [])
    tags = [{"$Key": "WheelCornerModuleAudioComponent", "$Value": K_AUDIO}] + ([{"$Key": "ConditionalSubPartsComponent", "$Value": K_CSP}] if own else [])
    changed = [{"Kind": "Update", "Index": 3, "Value": {"Definition": c["model"]}},
               {"Kind": "Update", "Index": 4, "Value": {"Definition": c["breakable"]}},
               {"Kind": "Update", "Index": 5, "Value": {"Definition": fbd}}]
    if not own: changed.append({"Kind": "Update", "Index": 6, "Value": {"Definition": c["effects"]}})
    changed.append({"Kind": "Insert", "Index": 9, "Value": {"Definition": c["audio"], "Type": "Game2:Keen.Game2.Client.WorldObjects.CubeBlocks.MechanicalBlocks.Wheels.WheelCornerModuleAudioComponent"}})
    if own: changed.append({"Kind": "Insert", "Index": 10, "Value": {"Definition": c["csp"], "Type": "Game2:Keen.Game2.Client.WorldObjects.CubeBlocks.Effects.ConditionalSubPartsComponent"}})
    definition(os.path.join(d, f"{N}_ClientComposition.partialdef"), partial(BASE["ccomp"], {"Guid": ccomp,
        "TagSlots": {"$DeltaEncoded": True, "RemovedKeys": [], "ModifiedKeys": tags},
        "Components": {"$DeltaEncoded": True, "Keys": ckeys, "Changed": changed, "Removed": []}},
        "VRage:Keen.VRage.DCS.Definitions.EntityCompositeDefinition"), ccomp, True)
    definition(os.path.join(d, f"{N}_Client.partialdef"), partial(BASE["client"], {"Guid": cpref, "_entity": {"Definition": ccomp, "ObjectBuilders": {
        "$DeltaEncoded": True, "Keys": ckeys, "Changed": [{"Kind": "Insert", "Index": 9 + i, "Value": None} for i in range(len(ckeys) - len(CKEYS))], "Removed": []}}},
        "VRage:Keen.VRage.Core.Game.Definitions.PrefabDefinition", B_GAME), cpref, True)
    # the block: ours (name, kind, power), the vanilla suspension's placement and size
    definition(os.path.join(d, f"{N}_PowerableBlockDefinition.partialdef"), partial(BASE["fbd"], {"Guid": fbd,
        "UIData": {"Name": "Wheeled Landing Gear" + ("" if own else " (test)"),   # (the game appends the size: DisplaySizeOverride)
                    "Icon": c["icon"]}, "PlacerDefinition": PLACEMENT,
        "DisplaySizeOverride": c["size"], "Density": "d8adcfdc-f8e2-467e-9d27-78deae4057da", "Recipe": RECIPE,
        "RecipeEfficiency": "30e09afc-4437-49fe-aff5-54553d05d3c4", "BlockKind": KIND, "PCU": c["pcu"],
        "ConsumedResource": {"Type": ELECTRICITY, "Amount": c["kw"]}, "ConsumptionPriority": {"Priority": 6}},
        "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.PowerableBlockDefinition"), fbd, True)


def main():
    root = os.path.join(BLOCKS, "Assets", ROOT_REL)
    gen_wheel_050()
    for size, c in SIZES.items(): gen_size(size, c)
    definition(os.path.join(root, "RetractableGear_PlacementDefinition.def"), {"$Bundles": B_GAME,
        "$Type": "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.PlacementDefinitionObjectBuilder",
        "$Value": {"Guid": PLACEMENT, "BestSnapSize": 1, "AlignWithMountPoints": True, "AllowSquareSnapping": True, "AutoRotateFace": None,
                   "SizeGroup": None, "MaxInVoxelRatio": 0, "SnapAlignment": 0, "OverrideSymmetries": SYMMETRIES}}, PLACEMENT, False)
    definition(os.path.join(root, "RetractableGear_BlockSymmetriesDefinition.def"), {"$Bundles": B_GAME,
        "$Type": "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.PlacementDefinition+BlockSymmetriesDefinitionObjectBuilder",
        "$Value": {"Guid": SYMMETRIES, "XAxis": {"X": 0, "Y": 0, "Z": 0}, "YAxis": {"X": 0, "Y": 0, "Z": 2}, "ZAxis": {"X": 0, "Y": 2, "Z": 0}}}, SYMMETRIES, False)
    # the subparts' dummy types (ConditionalSubParts maps each to its prefab; the models' dummies carry them)
    for name, dt in (("GearLegHinge", DT_LEG), ("GearRodHub", DT_ROD)):
        definition(os.path.join(root, f"RetractableGear_{name}_DummyTypeDefinition.def"), {"$Bundles": B_VRAGE,
            "$Type": "VRage:Keen.VRage.Core.Model.DummyTypeDefinitionObjectBuilder", "$Value": {"Guid": dt}}, dt, False)
    definition(os.path.join(root, "RetractableGear_BlockKind.def"), {"$Bundles": B_GAME,
        "$Type": "Game2:Keen.Game2.Simulation.StreamedUI.Categories.BlockKindDefinitionObjectBuilder",
        "$Value": {"Guid": KIND, "Name": "Wheeled Landing Gear", "Description": "Landing gear on a wheel that folds away: the leg swings forward into its bay, the wheel with it.",
                   "OrderingPriority": 0, "BlockGroups": [{"Key": G(SHARED, 0x22), "Value": GROUP}]}}, KIND, False)
    definition(os.path.join(root, "RetractableGear_BlockGroup.def"), {"$Bundles": B_GAME,
        "$Type": "Game2:Keen.Game2.Simulation.StreamedUI.Categories.BlockGroupDefinitionObjectBuilder",
        "$Value": {"Guid": GROUP, "Name": "Wheeled Landing Gear", "Order": 2,
                   "Subcategories": [{"Key": G(SHARED, 0x23), "Value": "ae200001-ae00-0000-0002-000000000001"}], "BlockKinds": []}}, GROUP, False)
    for rt, dirs, _ in os.walk(root):
        for dn in dirs:
            rel = os.path.relpath(os.path.join(rt, dn), os.path.join(BLOCKS, "Assets"))
            write(os.path.join(rt, dn.lower() + ".meta"), folder_meta(rel))
    write(os.path.join(os.path.dirname(root), "retractablegear.meta"), folder_meta(ROOT_REL))
    print(f"generated {sum(len(f) for _, _, f in os.walk(root))} files under {root}")


if __name__ == "__main__": main()   # (importable: gen_airbrake_defs.py uses its helpers)
