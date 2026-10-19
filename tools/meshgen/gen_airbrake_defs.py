"""The 3x3x3 clamshell AIRBRAKES' content for the "Aerodynamics Mod Blocks" project (2026-10-14; the user's kitbash:
airbrake_from_kitbash.py -> gear_export.py -> Export/<Size>/Models): Assets/Aero/Airbrake3x3x3/<Size>/ - the models, their
.model files, and the definitions: the block on BasePowerableBlockBreakable (on / off and a small power draw), its block
model (the two leaves' dummies on the hinge), the breakable set (two pieces), the leaves as client subparts
(ConditionalSubParts), the block kind and group. The mod injects AeroAirbrakeComponent / AeroAirbrakeAnimComponent by the
prefab GUIDs (AeroBlockGuids.Airbrake*Prefabs). Helpers from gen_gear_defs.py.

  python gen_airbrake_defs.py      then: python tools\\shared\\build_mod_content.py "<Aerodynamics Mod>"
"""
import json, os, shutil, uuid
import gen_gear_defs as gd

ROOT_REL = os.path.join("Aero", "Airbrake3x3x3")
EXPORT = r"D:\Blender Brojects\SE2\Airbrake\Export"
ICON_SRC = r"D:\Blender Brojects\SE2\Airbrake\Export\Airbrake_Icon.png"
NS = uuid.UUID("ae200001-ae00-ab00-0000-00000000c533")
G = lambda sid, n: f"ae200001-ae00-{sid}-{n:04x}-000000000001"
SHARED = "ab00"
KIND, GROUP = G(SHARED, 0x20), G(SHARED, 0x21)
DT_UP, DT_DOWN = G(SHARED, 0x30), G(SHARED, 0x31)
K_CSP = G(SHARED, 0x40)
# its own placement (2026-10-16, the user: "control surface mirroring ... fucked"): a mirrored block is the original turned by
# its symmetry rotation for that axis, then reflected (BlockPlacementBuilding: M (R S) M) - right only when S x the mirror is
# a symmetry of the shape. The airbrake is symmetric across (X: span) and up / down (Y: the two leaves), not
# fore / aft (Z: the hinge at the front) - so mirrored across Z it turns 180 deg about Up (the hinge forward again).
PLACEMENT, SYMMETRIES = G(SHARED, 0x50), G(SHARED, 0x51)
SIZES = {"Large": dict(sid="ab02", kw=1.0, pcu=150), "Small": dict(sid="ab03", kw=0.2, pcu=60)}
RECIPE = "6f209028-b561-4d9e-a1c8-ab9994b0a7e4"   # (LightArmor250's, as the control surfaces)
OFF = gd.OFF


def file_id(rel): return str(uuid.uuid5(NS, "file:" + rel.replace("\\", "/").lower()))


def folder_meta(rel):
    return {"$Bundles": {"System.Runtime": "1.0.0.0", "VRage": gd.VER_META}, "$Type": "VRage:Keen.VRage.ContentPipeline.Metafiles.MetaData",
            "$Value": {"AssetID": str(uuid.uuid5(NS, "folder:" + rel.lower())), "InternalItems": []}}


def V(b): return [round(b[0] + OFF, 5), round(b[2] + OFF, 5), round(-b[1] + OFF, 5)]   # Blender -> the model's space


def gen_size(size, c):
    sid = c["sid"]; N = f"Airbrake_{size}"
    spec = json.load(open(os.path.join(EXPORT, size, "airbrake_spec.json")))
    dest_rel = os.path.join(ROOT_REL, size); d = os.path.join(gd.BLOCKS, "Assets", dest_rel); src = os.path.join(EXPORT, size, "Models")
    ids = {}
    for rt, _, files in os.walk(src):
        for fn in files:
            if not fn.lower().endswith((".fbx", ".gltf", ".bin")): continue
            rel_in = os.path.relpath(os.path.join(rt, fn), src); out = os.path.join(d, "Models", rel_in)
            os.makedirs(os.path.dirname(out), exist_ok=True); shutil.copy(os.path.join(rt, fn), out)
            rel = os.path.join(dest_rel, "Models", rel_in); ids[rel_in.replace("\\", "/")] = file_id(rel)
            gd.write(out + ".meta", gd.file_meta(file_id(rel)))
    lod = lambda var, n, stem: ids[f"{var}/{stem}{'_LOD%d' % n if n else ''}.fbx"]
    mats = spec["materials"]; md = os.path.join(d, "Models"); off = (OFF, OFF, OFF)
    m_nf, m_fr, m_de, m_up, m_dn = (G(sid, 0x100 + i) for i in range(5))
    gd.model(os.path.join(md, "NonFractured", f"{N}.model"), m_nf, [lod("NonFractured", n, N) for n in range(4)], mats["Body"], ids[f"NonFractured/{N}_collision.gltf"], off)
    gd.model(os.path.join(md, "Fractured", f"{N}_Fractured.model"), m_fr, [lod("Fractured", n, N + "_Fractured") for n in range(4)], mats["Body"], ids[f"Fractured/{N}_Fractured_collision.gltf"], off)
    gd.model(os.path.join(md, "Deformed", f"{N}_Deformed.model"), m_de, [lod("Deformed", n, N + "_Deformed") for n in range(4)], mats["Body"], ids[f"Deformed/{N}_Deformed_collision.gltf"], off)
    gd.model(os.path.join(md, "NonFractured", "Subpart01", f"{N}_S1_LeafUp.model"), m_up, [lod("NonFractured/Subpart01", n, N + "_S1_LeafUp") for n in range(3)], mats["LeafUp"], None, (0.0, 0.0, 0.0))
    gd.model(os.path.join(md, "NonFractured", "Subpart02", f"{N}_S2_LeafDown.model"), m_dn, [lod("NonFractured/Subpart02", n, N + "_S2_LeafDown") for n in range(3)], mats["LeafDown"], None, (0.0, 0.0, 0.0))

    # the icon
    icon_rel = os.path.join(dest_rel, "Icon", f"{N}_Icon.png"); icon = os.path.join(gd.BLOCKS, "Assets", icon_rel)
    os.makedirs(os.path.dirname(icon), exist_ok=True); shutil.copy(ICON_SRC, icon)
    gd.write(icon + ".meta", gd.asset_meta(file_id(icon_rel), ("P_PNG", 2), f"{N}_Icon.png"))

    fbd, bind, spref, cpref, scomp, ccomp = G(sid, 0x1), G(sid, 0x10), G(sid, 0x11), G(sid, 0x12), G(sid, 0x13), G(sid, 0x14)
    bmd, brk, csp = G(sid, 0x15), G(sid, 0x16), G(sid, 0x17)
    dummies = [gd.dummy("AirbrakeLeafUp", V(spec["leaf_up"]), DT_UP), gd.dummy("AirbrakeLeafDown", V(spec["leaf_down"]), DT_DOWN)]
    gd.definition(os.path.join(d, f"{N}_BlockModelDefinition.partialdef"), gd.partial(gd.BASE_BMD, {"Guid": bmd, "Model": "{G}" + m_nf,
        "Dummies": {"$DeltaEncoded": True, "Keys": [G(sid, 0x41 + i) for i in range(len(dummies))],
                    "Changed": [{"Kind": "Insert", "Index": i, "Value": v} for i, v in enumerate(dummies)], "Removed": []},
        "FractureModel": "{G}" + m_fr, "DamagedModel": "{G}" + m_de,
        "BuildProgressModels": [
            {"Key": G(sid, 0x48), "Value": {"BuildRatioLowerBound": 0, "Model": "{G}" + m_fr, "PartChanges": {"_data": []}}},
            {"Key": G(sid, 0x49), "Value": {"BuildRatioLowerBound": 1, "Model": "{G}" + m_nf, "PartChanges": {"_data": []}}}]},
        "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.Models.BlockModelComponentDefinition"), bmd, True)
    gd.definition(os.path.join(d, f"{N}_BreakableBlockDefinition.partialdef"), gd.partial(gd.BASE_BRK, {"Guid": brk,
        "CriticalFractures": {"$DeltaEncoded": True, "Removed": [], "Added": [{"Id": "Fracture_01"}, {"Id": "Fracture_02"}]}},
        "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.BreakableBlockDefinition"), brk, True)
    subs, sp = {}, os.path.join(d, "Subparts")
    for k, (role, m, dt) in enumerate((("LeafUp", m_up, DT_UP), ("LeafDown", m_dn, DT_DOWN))):
        pref, comp, mcd = G(sid, 0x18 + 3 * k), G(sid, 0x19 + 3 * k), G(sid, 0x1a + 3 * k)
        subs[dt] = pref
        gd.definition(os.path.join(sp, f"{N}_{role}_Client.def"), {"$Bundles": gd.B_VRAGE,
            "$Type": "VRage:Keen.VRage.Core.Game.Definitions.PrefabDefinitionObjectBuilder",
            "$Value": {"Guid": pref, "_entity": {"Definition": comp, "ObjectBuilders": gd.SUBPART_OB(), "DebugName": "Root"}}}, pref, False)
        gd.definition(os.path.join(sp, f"{N}_{role}_Client_Composite.def"), {"$Bundles": gd.B_GAME,
            "$Type": "VRage:Keen.VRage.DCS.Definitions.EntityCompositeDefinitionObjectBuilder",
            "$Value": {"Guid": comp, "TagSlots": [{"$Key": t, "$Value": k_} for t, k_, _ in gd.SUBPART_COMPONENTS],
                       "Components": [{"Key": k_, "Value": {"Definition": mcd if t == "ModelComponent" else None, "Type": ty}} for t, k_, ty in gd.SUBPART_COMPONENTS]}},
            comp, False)
        gd.definition(os.path.join(sp, f"{N}_{role}_ModelComponentDefinition.def"), {"$Bundles": gd.B_VRAGE,
            "$Type": "VRage:Keen.VRage.Game.Components.Models.ModelComponentDefinitionObjectBuilder",
            "$Value": {"Guid": mcd, "Model": "{G}" + m, "Dummies": []}}, mcd, False)
    gd.definition(os.path.join(d, f"{N}_ConditionalSubPartsDefinition.def"), {"$Bundles": gd.B_GAME,
        "$Type": "Game2:Keen.Game2.Client.WorldObjects.CubeBlocks.Effects.ConditionalSubPartsDefinitionObjectBuilder",
        "$Value": {"Guid": csp, "SubParts": subs, "MeshPartsToHideWhenActive": []}}, csp, False)

    B = gd.BASE
    gd.definition(os.path.join(d, f"{N}.partialdef"), gd.partial(B["binding"], {"Guid": bind, "ServerComposite": scomp, "ClientPrefab": cpref},
        "VRage:Keen.VRage.Multiplayer.Data.PrefabBindingDefinition", gd.B_VRAGE), bind, True)
    gd.definition(os.path.join(d, f"{N}_Server.partialdef"), gd.partial(B["server"], {"Guid": spref, "_entity": {"Definition": scomp}},
        "VRage:Keen.VRage.Core.Game.Definitions.PrefabDefinition", gd.B_VRAGE), spref, True)
    gd.definition(os.path.join(d, f"{N}_ServerComposition.partialdef"), gd.partial(B["scomp"], {"Guid": scomp, "Components": {
        "$DeltaEncoded": True, "Keys": gd.SKEYS, "Changed": [
            {"Kind": "Update", "Index": 2, "Value": {"Definition": bmd}}, {"Kind": "Update", "Index": 3, "Value": {"Definition": brk}},
            {"Kind": "Update", "Index": 4, "Value": {"Definition": fbd}}], "Removed": []}},
        "VRage:Keen.VRage.DCS.Definitions.EntityCompositeDefinition"), scomp, True)
    gd.definition(os.path.join(d, f"{N}_Client.partialdef"), gd.partial(B["client"], {"Guid": cpref, "_entity": {"Definition": ccomp, "ObjectBuilders": {
        "$DeltaEncoded": True, "Keys": gd.CKEYS + [K_CSP], "Changed": [{"Kind": "Insert", "Index": 9, "Value": None}], "Removed": []}}},
        "VRage:Keen.VRage.Core.Game.Definitions.PrefabDefinition"), cpref, True)
    gd.definition(os.path.join(d, f"{N}_ClientComposition.partialdef"), gd.partial(B["ccomp"], {"Guid": ccomp,
        "TagSlots": {"$DeltaEncoded": True, "RemovedKeys": [], "ModifiedKeys": [{"$Key": "ConditionalSubPartsComponent", "$Value": K_CSP}]},
        "Components": {"$DeltaEncoded": True, "Keys": gd.CKEYS + [K_CSP], "Changed": [
            {"Kind": "Update", "Index": 3, "Value": {"Definition": bmd}}, {"Kind": "Update", "Index": 4, "Value": {"Definition": brk}},
            {"Kind": "Update", "Index": 5, "Value": {"Definition": fbd}},
            {"Kind": "Insert", "Index": 9, "Value": {"Definition": csp, "Type": "Game2:Keen.Game2.Client.WorldObjects.CubeBlocks.Effects.ConditionalSubPartsComponent"}}], "Removed": []}},
        "VRage:Keen.VRage.DCS.Definitions.EntityCompositeDefinition"), ccomp, True)
    gd.definition(os.path.join(d, f"{N}_PowerableBlockDefinition.partialdef"), gd.partial(B["fbd"], {"Guid": fbd,
        "UIData": {"Name": "Airbrake", "Icon": "{G}" + file_id(icon_rel)}, "PlacerDefinition": PLACEMENT, "Density": "d8adcfdc-f8e2-467e-9d27-78deae4057da",
        "Recipe": RECIPE, "RecipeEfficiency": "30e09afc-4437-49fe-aff5-54553d05d3c4", "BlockKind": KIND, "PCU": c["pcu"],
        "ConsumedResource": {"Type": gd.ELECTRICITY, "Amount": c["kw"]}, "ConsumptionPriority": {"Priority": 6}},
        "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.PowerableBlockDefinition"), fbd, True)
    print(f"{N}: leaves at {[x['Transform']['Transform']['Position'] for x in dummies]}")


def main():
    root = os.path.join(gd.BLOCKS, "Assets", ROOT_REL)
    for size, c in SIZES.items(): gen_size(size, c)
    gd.definition(os.path.join(root, "Airbrake_PlacementDefinition.def"), {"$Bundles": gd.B_GAME,
        "$Type": "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.PlacementDefinitionObjectBuilder",
        "$Value": {"Guid": PLACEMENT, "BestSnapSize": None, "AlignWithMountPoints": True, "AllowSquareSnapping": True, "AutoRotateFace": None,
                   "SizeGroup": None, "MaxInVoxelRatio": 0.5, "SnapAlignment": 0, "OverrideSymmetries": SYMMETRIES}}, PLACEMENT, False)
    gd.definition(os.path.join(root, "Airbrake_BlockSymmetriesDefinition.def"), {"$Bundles": gd.B_GAME,
        "$Type": "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.PlacementDefinition+BlockSymmetriesDefinitionObjectBuilder",
        "$Value": {"Guid": SYMMETRIES, "XAxis": {"X": 0, "Y": 0, "Z": 0}, "YAxis": {"X": 0, "Y": 0, "Z": 0}, "ZAxis": {"X": 0, "Y": 2, "Z": 0}}}, SYMMETRIES, False)
    for name, dt in (("AirbrakeLeafUp", DT_UP), ("AirbrakeLeafDown", DT_DOWN)):
        gd.definition(os.path.join(root, f"{name}_DummyTypeDefinition.def"), {"$Bundles": gd.B_VRAGE,
            "$Type": "VRage:Keen.VRage.Core.Model.DummyTypeDefinitionObjectBuilder", "$Value": {"Guid": dt}}, dt, False)
    gd.definition(os.path.join(root, "Airbrake_BlockKind.def"), {"$Bundles": gd.B_GAME,
        "$Type": "Game2:Keen.Game2.Simulation.StreamedUI.Categories.BlockKindDefinitionObjectBuilder",
        "$Value": {"Guid": KIND, "Name": "Airbrake", "Description": "Two leaves that split open to brake in the air - with the parking brake (P).",
                   "OrderingPriority": 0, "BlockGroups": [{"Key": G(SHARED, 0x22), "Value": GROUP}]}}, KIND, False)
    gd.definition(os.path.join(root, "Airbrake_BlockGroup.def"), {"$Bundles": gd.B_GAME,
        "$Type": "Game2:Keen.Game2.Simulation.StreamedUI.Categories.BlockGroupDefinitionObjectBuilder",
        "$Value": {"Guid": GROUP, "Name": "Airbrake", "Order": 3,
                   "Subcategories": [{"Key": G(SHARED, 0x23), "Value": "ae200001-ae00-0000-0002-000000000001"}], "BlockKinds": []}}, GROUP, False)
    for rt, dirs, _ in os.walk(root):
        for dn in dirs:
            rel = os.path.relpath(os.path.join(rt, dn), os.path.join(gd.BLOCKS, "Assets"))
            gd.write(os.path.join(rt, dn.lower() + ".meta"), folder_meta(rel))
    gd.write(os.path.join(os.path.dirname(root), "airbrake3x3x3.meta"), folder_meta(ROOT_REL))
    print(f"generated {sum(len(f) for _, _, f in os.walk(root))} files under {root}")


main()
