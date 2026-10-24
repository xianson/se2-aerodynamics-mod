"""The 3x3x1 control surfaces' content for the "Aerodynamics Mod Blocks" project: the exported models (export_models.py)
copied into Assets/Aero/ControlSurface3x3/<Size>/, every .meta, the four .model files per size and the definition set,
following the spec in docs (hand-authored: the SDK editor does not run on 2.4). Re-runnable: GUIDs are fixed (defs) or
derived from the file's path (assets), so a rebuild after a model change keeps every reference.

  python gen_cs3_defs.py [--defs]   (--defs: the definition files only, rewritten in place - no model copy, nothing removed)
  then: python tools\\shared\\build_mod_content.py "<Aerodynamics Mod>"

The block: a copy of the vanilla template "BasePowerableBlockBreakable" (PowerableBlockComponent - a FunctionalBlockComponent:
the flap subpart only spawns on a functional block; the powerable one adds the game's on/off toggle and a power draw, as a
gyroscope: PowerableBlockDefinition.ConsumedResource, kW), its BlockModelDefinition pointing at our NonFractured / Fractured / Deformed models and
carrying the hinge dummy; the client composition adds ConditionalSubPartsComponent, which spawns the flap prefab at the
dummy (the mod's AeroFlapComponent turns it). Coordinates: VRage = Blender (x, z, -y); body LOD0 shifted (0.125, 0.125,
0.125) onto the 0.25 m cell grid (no shift for an odd number of cells: the 0.25 m one, 3 x 1 x 3); the hinge dummy at
Blender (-2.5, 0, 0) x size + that shift.

Sizes (2026-10-23): 3 x 3 x 1 blocks of 0.25 / 0.5 / 1 / 1.5 / 2.5 m, each the large model scaled whole (make_small.py
<out.blend> <scale> <name> on the large .blend, then export_models.py and render_icon.py on that copy); the game shows the
block size (DisplaySizeOverride).
"""
import json, os, shutil, uuid

EXPORT = r"D:\Blender Brojects\SE2\ControlSurface\Export"
BLOCKS = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))   # (this checkout of the mod: the block content lives in it since 2026-10-17)
ROOT_REL = os.path.join("Aero", "ControlSurface3x3")
NS = uuid.UUID("ae200001-ae00-c500-0000-00000000c533")
VER_META = "2.4.0.16"
B_GAME = {"Game2": "2.0.1.1811", "System.Runtime": "1.0.0.0", "VRage": "2.0.1.1811"}
B_VRAGE = {"System.Runtime": "1.0.0.0", "VRage": "2.0.1.1811"}

# b: the block size (m), the model the large one x b / 2.5; the key names the folder and files
SIZES = {"Large": {"sid": "c502", "b": 2.5}, "Small": {"sid": "c503", "b": 0.5},
         "025": {"sid": "c504", "b": 0.25}, "100": {"sid": "c505", "b": 1.0}, "150": {"sid": "c506", "b": 1.5}}
G = lambda sid, n: f"ae200001-ae00-{sid}-{n:04x}-000000000001"
SHARED = "c500"
DT_HINGE = G(SHARED, 0x30)
KIND, GROUP = G(SHARED, 0x20), G(SHARED, 0x21)
# its own placement (2026-10-16, the user: "control surface mirroring ... fucked"): a mirrored block is the original turned by
# its symmetry rotation for that axis, then reflected (BlockPlacementBuilding: M (R S) M) - right only when S x the mirror is
# a symmetry of the shape. The 3x3x1 is symmetric along its span (Forward) and through its thickness (Up),
# not along its chord (Right: hinge ahead, flap behind) - so mirrored across Right it turns 180 deg about Up (the flap
# back behind the hinge); the others as they are.
PLACEMENT, SYMMETRIES = G(SHARED, 0x50), G(SHARED, 0x51)
K_CSP = G(SHARED, 0x40)   # (composite-local key of ConditionalSubPartsComponent)

MATERIALS = {
    "PaintedMetalColorable": "be89c5c3-25ce-42e9-93f9-72ba32c3d8a7", "PaintedMetalDarker": "cb933a73-bce4-4224-a7d8-c5083935dac8",
    "BrushedMetal": "0c3be477-8bff-4e2c-8a0e-d7d842905ae9", "ConveyorsAtlas": "3020a2ff-e3b5-4990-8f96-6da1338c3de2",
    "FracturedMaterial01": "0f2772fd-72d6-4ee9-9211-38d1781ba8b9", "FracturedMaterial02": "403fce1f-8ff2-489f-be60-68b986711007",
    "Chrome": "3312f76c-b118-4616-bd95-adfb9b59a996",
    "AtlasDecal_Trims01": "35d26308-2dc4-4b01-af36-0c713d10e571", "AtlasDecal_Trims02": "aa49b17e-58dc-4da8-8ba2-3bd643325681",
    "AtlasDecal_Trims03": "6166bee4-12a2-4d4f-82c8-0b481aa5823f", "AtlasDecal_Parts01": "0d1f9867-1aee-4388-b5bb-4a7ce0cdc5e2",
    "AtlasDecal_Parts02": "8affab53-a343-4c9d-b513-2f82180c0326", "AtlasDecal_Stickers01": "2a953884-e538-4bec-a334-1fef9d3712ce",
    "AtlasDecal_Stickers02": "c3e749ff-35ff-469c-b7ae-84a5a5d829b3",
}
# vanilla bases (2.4 compiled keys checked against the game's Content\Templates\Base*BlockBreakable*.def)
BASE_FUNCTIONAL = dict(binding="3d1ed69d-e474-49c2-8d3c-7810710cd9f0", client="b43855d8-9780-4343-9a5c-7d03c6973629",
            ccomp="dd94c0be-8cf1-4518-9c45-36c71f659a69", server="69d6b4d6-589d-405d-b25a-fb45a99b3ac9",
            scomp="5c2e1b3e-f111-4918-8fcf-09c9ac0b42d3", fbd="ea3505ef-f162-4e25-8af3-eba6e68a186a",
            bmd="f104e970-bdcc-4a09-892d-8cc96e039998", brk="a74c6fd1-fa39-4f24-89cb-17a46e65cfd7")
CKEYS = ["b7bf405c-71aa-46a1-a5ec-4c690f2ab7e3", "e58b8f69-45da-40d4-82b0-f1a82564a53a", "a1687822-b4c2-4bcb-961e-3e44fae3221d",
         "d358358e-30b3-484a-b8b8-2e5568c7298b", "bc5652a0-1296-4040-adc3-a2b9c35d5afc", "91e12dcf-837b-4bec-8269-cf48c52a1e1f",
         "4c7a5e12-ff2f-4dc1-be4b-2a103a951fcd", "82387aa9-8ce8-40da-a2be-2f5230bb007a", "0d739550-839b-414f-859c-6586bb6bcf48"]
SKEYS = ["b7bf405c-71aa-46a1-a5ec-4c690f2ab7e3", "e58b8f69-45da-40d4-82b0-f1a82564a53a", "d358358e-30b3-484a-b8b8-2e5568c7298b",
         "bc5652a0-1296-4040-adc3-a2b9c35d5afc", "91e12dcf-837b-4bec-8269-cf48c52a1e1f"]
# "BasePowerableBlockBreakable": the same layout (same indices), its own powerable key and client keys
BASE = dict(binding="e90997d9-6c5d-4493-ba8b-8bda08151bed", client="57ffe0a6-678c-4090-afc8-b344b970f474",
            ccomp="5931874e-15e7-4b21-aee9-68a125b76778", server="1fa87190-7256-4125-9fda-29ce92b98f86",
            scomp="d6b1a072-ef93-47fe-836d-165b00dac2dc", fbd="794c5233-6a1c-4932-b37c-03a0e539d069",
            bmd=BASE_FUNCTIONAL["bmd"], brk=BASE_FUNCTIONAL["brk"])
K_POWERABLE = "4f2bc5f9-6a6d-4347-a1eb-5937cbce6727"
CKEYS = CKEYS[:5] + [K_POWERABLE, "72e2ef60-d699-48a2-9959-4e246eecdacc", "24f8dfe2-6b3f-4764-a9db-6e4383dcae5d",
                     "30a7eb7b-1873-479a-b7d8-fe5db6a0567e"]
SKEYS = SKEYS[:4] + [K_POWERABLE]
ELECTRICITY = "bcded093-f5c0-4997-af3a-a6fbd853ad66"   # (Content\System\ResourceTypes\Electricity.def: kW)
POWER_KW = {"Large": 5, "Small": 1, "025": 0.5, "100": 2, "150": 3}   # (its actuators' idle draw: a large one as a gyroscope; 2 kW per m, as the gears)
DEFS_ONLY = "--defs" in __import__("sys").argv
# the icon: render_icon.py's 200 x 200 PNG of the block (Export/<Size>/Icon), a P_PNG asset as the game's own icons
RECIPE = "6f209028-b561-4d9e-a1c8-ab9994b0a7e4"        # (LightArmor250's, as the 1x1)
CRITICAL = ["Fracture_01", "Fracture_03", "Fracture_05"]  # the hinge-side pieces: any dead -> broken -> the flap despawns


def write(path, obj):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f: json.dump(obj, f, indent=2)


def asset_meta(asset_id, processor=None, output=None):
    items = [{"$Type": "VRage:Keen.VRage.ContentPipeline.Project.Structure.FileInfoMetaDataItem", "FileHash": ""}]
    items.append({"$Type": "VRage:Keen.VRage.ContentPipeline.Project.Features.AssetProcessing.AssetProcessingMetaDataItem",
                  "ProcessorInfo": {"Version": processor[1] if processor else 0, "Identifier": processor[0] if processor else ""},
                  "ContentFiles": {"MainOutput": {"FileHash": "", "FileName": output}, "AdditionalOutputs": []} if output else None,
                  "Problems": [], "DependenciesHashes": [], "AssetHash": "", "OverrideDetachedState": False, "IgnoredErrors": "None"})
    return {"$Bundles": {"System.Runtime": "1.0.0.0", "VRage": VER_META}, "$Type": "VRage:Keen.VRage.ContentPipeline.Metafiles.MetaData",
            "$Value": {"AssetID": asset_id, "InternalItems": items}}


def folder_meta(path_rel):
    return {"$Bundles": {"System.Runtime": "1.0.0.0", "VRage": VER_META}, "$Type": "VRage:Keen.VRage.ContentPipeline.Metafiles.MetaData",
            "$Value": {"AssetID": str(uuid.uuid5(NS, "folder:" + path_rel.lower())), "InternalItems": []}}


def file_id(rel): return str(uuid.uuid5(NS, "file:" + rel.replace("\\", "/").lower()))


def definition(path, obj, guid, partial):
    write(path, obj)
    out = os.path.splitext(os.path.basename(path))[0] + ".def"
    write(path + ".meta", asset_meta(guid, ("P_PARTIAL_DEF", 1) if partial else ("P_DEF", 1), out))


def partial(base, manip, dtype, bundles=B_GAME):
    return {"$Bundles": bundles, "$Type": "VRage:Keen.VRage.ContentPipeline.Definitions.PartialDefinitionDiff",
            "$Value": {"BaseDefinition": base, "PartialDefinitionKind": "Copy", "Manipulator": manip, "DefinitionType": dtype, "PriorityOverride": False}}


def model(path, guid, lods, materials, collider, offset):
    geo = []
    for i, ref in enumerate(lods):
        t = offset if i == 0 else (0.0, 0.0, 0.0)
        geo.append({"AssetReference": "{G}" + ref, "UsedMaterials": sorted(materials),
                    "Transformations": {"Translation": {"X": t[0], "Y": t[1], "Z": t[2]}, "Rotation": {"X": 0, "Y": 0, "Z": 0}, "Scale": 1}})
    assign = []
    for m in sorted(materials):
        lib = m[:-len("_Subpart")] if m.endswith("_Subpart") else m   # (the stand-in's aliases: same material, own mesh-part name)
        assign.append({"MaterialName": m, "MaterialReference": MATERIALS[lib]})
    obj = {"$Bundles": {"System.Runtime": "1.0.0.0", "VRage": "2.0.1.118"}, "$Type": "VRage:Keen.VRage.ContentPipeline.Models.ModelAssetData",
           "$Value": {"MaterialAssignments": assign, "MaterialStateOverrides": [], "GeometryAssets": geo, "SnapToGrid": False,
                      "SuggestedModelSize": None,
                      "CollisionShapesAsset": {"AssetReference": ("{G}" + collider) if collider else None, "GroupsByFracture": [],
                                               "GeneratePreciseCollider": bool(collider)}}}
    write(path, obj)
    write(path + ".meta", asset_meta(guid, ("P_MODEL", 40), os.path.splitext(os.path.basename(path))[0] + ".vrm"))


def add_mountpoints(gltf_path, name):
    """A mountpoints_* collider over the whole block (the processor takes it out of physics into extra mount-point shapes):
    the flap is a subpart, absent from the mesh the mount points are generated from - without it the block attaches only
    where the hinge body is."""
    g = json.load(open(gltf_path, encoding="utf-8"))
    if any(n.get("name", "").startswith("mountpoints") for n in g["nodes"]): return
    cols = g["extensions"]["MSFT_collision_primitives"]["colliders"]
    cols.append({"collisionSystems": ["System_0"], "collideWithSystems": ["System_0"], "convex": {"mesh": 0}})
    g["nodes"].append({"extensions": {"MSFT_rigid_bodies": {"rigidBody": {"inverseMass": 1}, "collider": len(cols) - 1, "physicsMaterial": 0}},
                       "mesh": 0, "name": "mountpoints_" + name})
    g["scenes"][0]["nodes"].append(len(g["nodes"]) - 1)
    json.dump(g, open(gltf_path, "w", encoding="utf-8"), indent=1)


def gen_size(size, cfg, manifest):
    sid, B = cfg["sid"], cfg["b"]
    N = f"ControlSurface_{size}"
    dest_rel = os.path.join(ROOT_REL, size)
    dest = os.path.join(BLOCKS, "Assets", dest_rel)
    src = os.path.join(EXPORT, size, "Models")
    # 1. the exported files, each with its own (processor-less, detached) meta
    ids = {}
    for rt, _, files in ([] if DEFS_ONLY else os.walk(src)):
        for f in files:
            if not f.lower().endswith((".fbx", ".gltf", ".bin")): continue
            rel_in = os.path.relpath(os.path.join(rt, f), src)
            out = os.path.join(dest, "Models", rel_in)
            os.makedirs(os.path.dirname(out), exist_ok=True)
            shutil.copy(os.path.join(rt, f), out)
            if f == f"{N}_collision.gltf": add_mountpoints(out, f"{N}_Box")
            rel = os.path.join(dest_rel, "Models", rel_in)
            ids[rel_in.replace("\\", "/")] = file_id(rel)
            write(out + ".meta", asset_meta(file_id(rel)))
    lod = lambda var, n, stem: ids[f"{var}/{stem}{'_LOD%d' % n if n else ''}.fbx"]
    mats = lambda key: {m for k, v in manifest.items() if k.startswith(f"{size}\\Models\\{key}") for m in v["materials"]}
    o = 0.125 if round(B / 0.25) % 2 == 0 else 0.0   # (an even number of cells across: the centre on a cell corner)
    off = (o, o, o)
    m_nf, m_fr, m_de, m_flap = G(sid, 0x100), G(sid, 0x101), G(sid, 0x102), G(sid, 0x103)
    md = os.path.join(dest, "Models")
    if not DEFS_ONLY:
      model(os.path.join(md, "NonFractured", f"{N}.model"), m_nf, [lod("NonFractured", n, N) for n in range(4)],
          {m for k, v in manifest.items() if k.startswith(f"{size}\\Models\\NonFractured\\{N}") for m in v["materials"]},
          ids[f"NonFractured/{N}_collision.gltf"], off)
      model(os.path.join(md, "Fractured", f"{N}_Fractured.model"), m_fr, [lod("Fractured", n, N + "_Fractured") for n in range(4)],
          mats("Fractured"), ids[f"Fractured/{N}_Fractured_collision.gltf"], off)
      model(os.path.join(md, "Deformed", f"{N}_Deformed.model"), m_de, [lod("Deformed", n, N + "_Deformed") for n in range(4)],
          mats("Deformed"), ids[f"Deformed/{N}_Deformed_collision.gltf"], off)
      model(os.path.join(md, "NonFractured", "Subpart01", f"{N}_S1_Flap.model"), m_flap,
          [lod("NonFractured/Subpart01", n, N + "_S1_Flap") for n in range(3)], mats("NonFractured\\Subpart01"), None, (0.0, 0.0, 0.0))

    # 1b. the icon (copied in --defs too: it is not a model)
    icon_src = os.path.join(EXPORT, size, "Icon", f"{N}_Icon.png")
    icon_rel = os.path.join(dest_rel, "Icon", f"{N}_Icon.png")
    os.makedirs(os.path.join(dest, "Icon"), exist_ok=True)
    shutil.copy(icon_src, os.path.join(BLOCKS, "Assets", icon_rel))
    write(os.path.join(BLOCKS, "Assets", icon_rel) + ".meta", asset_meta(file_id(icon_rel), ("P_PNG", 2), f"{N}_Icon.png"))
    ICON = "{G}" + file_id(icon_rel)

    # 2. definitions
    d = dest
    bind, spref, cpref, scomp, ccomp = G(sid, 0x10), G(sid, 0x11), G(sid, 0x12), G(sid, 0x13), G(sid, 0x14)
    fbd, bmd, brk, csp = G(sid, 0x1), G(sid, 0x15), G(sid, 0x16), G(sid, 0x17)
    flap_pref, flap_comp, flap_mcd = G(sid, 0x18), G(sid, 0x19), G(sid, 0x1a)
    definition(os.path.join(d, f"{N}.partialdef"), partial(BASE["binding"], {"Guid": bind, "ServerComposite": scomp, "ClientPrefab": cpref},
               "VRage:Keen.VRage.Multiplayer.Data.PrefabBindingDefinition", B_VRAGE), bind, True)
    definition(os.path.join(d, f"{N}_Server.partialdef"), partial(BASE["server"], {"Guid": spref, "_entity": {"Definition": scomp}},
               "VRage:Keen.VRage.Core.Game.Definitions.PrefabDefinition", B_VRAGE), spref, True)
    definition(os.path.join(d, f"{N}_ServerComposition.partialdef"), partial(BASE["scomp"], {"Guid": scomp, "Components": {
        "$DeltaEncoded": True, "Keys": SKEYS, "Changed": [
            {"Kind": "Update", "Index": 2, "Value": {"Definition": bmd}}, {"Kind": "Update", "Index": 3, "Value": {"Definition": brk}},
            {"Kind": "Update", "Index": 4, "Value": {"Definition": fbd}}], "Removed": []}},
        "VRage:Keen.VRage.DCS.Definitions.EntityCompositeDefinition"), scomp, True)
    definition(os.path.join(d, f"{N}_Client.partialdef"), partial(BASE["client"], {"Guid": cpref, "_entity": {"Definition": ccomp, "ObjectBuilders": {
        "$DeltaEncoded": True, "Keys": CKEYS + [K_CSP], "Changed": [{"Kind": "Insert", "Index": 9, "Value": None}], "Removed": []}}},
        "VRage:Keen.VRage.Core.Game.Definitions.PrefabDefinition"), cpref, True)
    definition(os.path.join(d, f"{N}_ClientComposition.partialdef"), partial(BASE["ccomp"], {"Guid": ccomp,
        "TagSlots": {"$DeltaEncoded": True, "RemovedKeys": [], "ModifiedKeys": [{"$Key": "ConditionalSubPartsComponent", "$Value": K_CSP}]},
        "Components": {"$DeltaEncoded": True, "Keys": CKEYS + [K_CSP], "Changed": [
            {"Kind": "Update", "Index": 3, "Value": {"Definition": bmd}}, {"Kind": "Update", "Index": 4, "Value": {"Definition": brk}},
            {"Kind": "Update", "Index": 5, "Value": {"Definition": fbd}},
            {"Kind": "Insert", "Index": 9, "Value": {"Definition": csp, "Type": "Game2:Keen.Game2.Client.WorldObjects.CubeBlocks.Effects.ConditionalSubPartsComponent"}}], "Removed": []}},
        "VRage:Keen.VRage.DCS.Definitions.EntityCompositeDefinition"), ccomp, True)
    definition(os.path.join(d, f"{N}_FunctionalBlockDefinition.partialdef"), partial(BASE["fbd"], {"Guid": fbd,
        "UIData": {"Name": "Control Surface", "Icon": ICON}, "PlacerDefinition": PLACEMENT, "DisplaySizeOverride": B, "Density": "d8adcfdc-f8e2-467e-9d27-78deae4057da",
        "Recipe": RECIPE, "RecipeEfficiency": "30e09afc-4437-49fe-aff5-54553d05d3c4", "BlockKind": KIND, "PCU": 50,
        "ConsumedResource": {"Type": ELECTRICITY, "Amount": POWER_KW[size]}, "ConsumptionPriority": {"Priority": 6}},
        "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.PowerableBlockDefinition"), fbd, True)
    hx = -2.5 * B / 2.5   # (Blender hinge x: -2.5 m on the large block)
    definition(os.path.join(d, f"{N}_BlockModelDefinition.partialdef"), partial(BASE["bmd"], {"Guid": bmd, "Model": "{G}" + m_nf,
        "Dummies": {"$DeltaEncoded": True, "Keys": [G(sid, 0x41)], "Changed": [{"Kind": "Insert", "Index": 0, "Value": {
            "Name": "FlapHinge", "Transform": {"Transform": {"Position": {"X": hx + off[0], "Y": off[1], "Z": off[2]},
                                                             "Orientation": {"X": 0, "Y": 0, "Z": 0, "W": 1}}, "EulerHint": {"X": 0, "Y": 0, "Z": 0}},
            "Scale": {"X": 1, "Y": 1, "Z": 1}, "Type": DT_HINGE, "SupportingFracture": None}}], "Removed": []},
        "FractureModel": "{G}" + m_fr, "DamagedModel": "{G}" + m_de,
        "BuildProgressModels": [
            {"Key": G(sid, 0x42), "Value": {"BuildRatioLowerBound": 0, "Model": "{G}" + m_fr, "PartChanges": {"_data": []}}},
            {"Key": G(sid, 0x43), "Value": {"BuildRatioLowerBound": 1, "Model": "{G}" + m_nf, "PartChanges": {"_data": []}}}]},
        "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.Models.BlockModelComponentDefinition"), bmd, True)
    definition(os.path.join(d, f"{N}_BreakableBlockDefinition.partialdef"), partial(BASE["brk"], {"Guid": brk,
        "CriticalFractures": {"$DeltaEncoded": True, "Removed": [], "Added": [{"Id": c} for c in CRITICAL]}},
        "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.BreakableBlockDefinition"), brk, True)
    # the flap: hide its static stand-in (every *_Subpart mesh part of the Fractured / Deformed models) while it is spawned
    hide = sorted({p for k, v in manifest.items() if k.startswith(f"{size}\\Models\\Fractured") or k.startswith(f"{size}\\Models\\Deformed")
                   for p in v["parts"] if p.endswith("_Subpart")})
    definition(os.path.join(d, f"{N}_ConditionalSubPartsDefinition.def"), {"$Bundles": B_GAME,
        "$Type": "Game2:Keen.Game2.Client.WorldObjects.CubeBlocks.Effects.ConditionalSubPartsDefinitionObjectBuilder",
        "$Value": {"Guid": csp, "SubParts": {DT_HINGE: flap_pref},
                   "MeshPartsToHideWhenActive": [{"Key": str(uuid.uuid5(NS, f"hide:{size}:{p}")), "Value": p} for p in hide]}}, csp, False)
    sp = os.path.join(d, "Subparts")
    definition(os.path.join(sp, f"{N}_Flap_Client.def"), {"$Bundles": B_VRAGE,
        "$Type": "VRage:Keen.VRage.Core.Game.Definitions.PrefabDefinitionObjectBuilder",
        "$Value": {"Guid": flap_pref, "_entity": {"Definition": flap_comp, "ObjectBuilders": [
            {"Key": "38b268a8-6f59-49d1-9bf2-6f73d5ad8a75", "Value": {"$Type": "VRage:Keen.VRage.Core.Game.Components.HierarchyComponentObjectBuilder", "Children": []}},
            {"Key": "6668ab9d-dd60-462f-b6e8-7dfe1730f9c0", "Value": {"$Type": "VRage:Keen.VRage.Core.Game.Components.ChildTransformComponentObjectBuilder",
                "TransformWithEulerHint": {"Transform": {"Position": {"X": 0, "Y": 0, "Z": 0}, "Orientation": {"X": 0, "Y": 0, "Z": 0, "W": 1}}, "EulerHint": {"X": 0, "Y": 0, "Z": 0}}}},
            {"Key": "33905a1a-c47d-4ca5-a87c-b9c993baf1e5", "Value": None},
            {"Key": "17fac7aa-348d-4ae4-b687-531ad1601bd4", "Value": None},
            {"Key": "a72db338-5407-4268-87f1-2faeb2cac274", "Value": None}], "DebugName": "Root"}}}, flap_pref, False)
    definition(os.path.join(sp, f"{N}_Flap_Client_Composite.def"), {"$Bundles": B_GAME,
        "$Type": "VRage:Keen.VRage.DCS.Definitions.EntityCompositeDefinitionObjectBuilder",
        "$Value": {"Guid": flap_comp, "TagSlots": [
            {"$Key": "HierarchyComponent", "$Value": "38b268a8-6f59-49d1-9bf2-6f73d5ad8a75"},
            {"$Key": "ChildTransformComponent", "$Value": "6668ab9d-dd60-462f-b6e8-7dfe1730f9c0"},
            {"$Key": "ModelRenderBaseComponent", "$Value": "33905a1a-c47d-4ca5-a87c-b9c993baf1e5"},
            {"$Key": "ModelComponent", "$Value": "17fac7aa-348d-4ae4-b687-531ad1601bd4"},
            {"$Key": "IRenderParent", "$Value": "a72db338-5407-4268-87f1-2faeb2cac274"}],
            "Components": [
            {"Key": "38b268a8-6f59-49d1-9bf2-6f73d5ad8a75", "Value": {"Definition": None, "Type": "VRage:Keen.VRage.Core.Game.Components.HierarchyComponent"}},
            {"Key": "6668ab9d-dd60-462f-b6e8-7dfe1730f9c0", "Value": {"Definition": None, "Type": "VRage:Keen.VRage.Core.Game.Components.ChildTransformComponent"}},
            {"Key": "33905a1a-c47d-4ca5-a87c-b9c993baf1e5", "Value": {"Definition": None, "Type": "Game2:Keen.Game2.Client.GameSystems.Render.ModelRenderComponent"}},
            {"Key": "17fac7aa-348d-4ae4-b687-531ad1601bd4", "Value": {"Definition": flap_mcd, "Type": "VRage:Keen.VRage.Game.Components.Models.ModelComponent"}},
            {"Key": "a72db338-5407-4268-87f1-2faeb2cac274", "Value": {"Definition": None, "Type": "Game2:Keen.Game2.Client.GameSystems.Render.ParentRenderAdapterComponent"}}]}},
        flap_comp, False)
    definition(os.path.join(sp, f"{N}_Flap_ModelComponentDefinition.def"), {"$Bundles": B_VRAGE,
        "$Type": "VRage:Keen.VRage.Game.Components.Models.ModelComponentDefinitionObjectBuilder",
        "$Value": {"Guid": flap_mcd, "Model": "{G}" + m_flap, "Dummies": []}}, flap_mcd, False)
    return fbd


def placement():
    root = os.path.join(BLOCKS, "Assets", ROOT_REL)
    definition(os.path.join(root, "ControlSurface3x3_PlacementDefinition.def"), {"$Bundles": B_GAME,
        "$Type": "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.PlacementDefinitionObjectBuilder",
        "$Value": {"Guid": PLACEMENT, "BestSnapSize": None, "AlignWithMountPoints": True, "AllowSquareSnapping": True, "AutoRotateFace": None,
                   "SizeGroup": None, "MaxInVoxelRatio": 0.5, "SnapAlignment": 0, "OverrideSymmetries": SYMMETRIES}}, PLACEMENT, False)
    definition(os.path.join(root, "ControlSurface3x3_BlockSymmetriesDefinition.def"), {"$Bundles": B_GAME,
        "$Type": "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.PlacementDefinition+BlockSymmetriesDefinitionObjectBuilder",
        "$Value": {"Guid": SYMMETRIES, "XAxis": {"X": 0, "Y": 2, "Z": 0}, "YAxis": {"X": 0, "Y": 0, "Z": 0}, "ZAxis": {"X": 0, "Y": 0, "Z": 0}}}, SYMMETRIES, False)


def main():
    # (the exported FBX's objects and their materials, per file - read from the 2026-10-12 export once; kept here since 2026-10-23:
    #  it lived in that session's scratchpad, a temp folder that could go)
    manifest = json.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "cs3_fbx_manifest.json")))
    for size in SIZES:   # (a scaled copy holds the large one's objects and materials: its entries under its own names)
        if not any(k.startswith(size + "\\") for k in manifest):
            manifest.update({k.replace("Large", size): v for k, v in list(manifest.items()) if k.startswith("Large\\")})
    root = os.path.join(BLOCKS, "Assets", ROOT_REL)
    if os.path.isdir(root) and not DEFS_ONLY: shutil.rmtree(root)   # (ours alone: regenerated whole)
    for size, cfg in SIZES.items(): gen_size(size, cfg, manifest)
    definition(os.path.join(root, "ControlSurface3x3_FlapHinge_DummyTypeDefinition.def"), {"$Bundles": B_VRAGE,
        "$Type": "VRage:Keen.VRage.Core.Model.DummyTypeDefinitionObjectBuilder", "$Value": {"Guid": DT_HINGE}}, DT_HINGE, False)
    definition(os.path.join(root, "ControlSurface3x3_BlockKind.def"), {"$Bundles": B_GAME,
        "$Type": "Game2:Keen.Game2.Simulation.StreamedUI.Categories.BlockKindDefinitionObjectBuilder",
        "$Value": {"Guid": KIND, "Name": "Control Surface", "Description": "A hinged flap: aileron, elevator, rudder - whichever way it is placed. Moves with the pilot's steering.",
                   "OrderingPriority": 0, "BlockGroups": [{"Key": G(SHARED, 0x22), "Value": GROUP}]}}, KIND, False)
    definition(os.path.join(root, "ControlSurface3x3_BlockGroup.def"), {"$Bundles": B_GAME,
        "$Type": "Game2:Keen.Game2.Simulation.StreamedUI.Categories.BlockGroupDefinitionObjectBuilder",
        "$Value": {"Guid": GROUP, "Name": "Control Surface", "Order": 1,
                   "Subcategories": [{"Key": G(SHARED, 0x23), "Value": "ae200001-ae00-0000-0002-000000000001"}], "BlockKinds": []}}, GROUP, False)
    placement()
    # folder metas (lowercase <name>.meta beside each folder, the project's convention)
    for rt, dirs, _ in os.walk(root):
        for dn in dirs:
            rel = os.path.relpath(os.path.join(rt, dn), os.path.join(BLOCKS, "Assets"))
            write(os.path.join(rt, dn.lower() + ".meta"), folder_meta(rel))
    write(os.path.join(os.path.dirname(root), "controlsurface3x3.meta"), folder_meta(ROOT_REL))
    n = sum(len(f) for _, _, f in os.walk(root))
    print(f"generated {n} files under {root}")


main()
