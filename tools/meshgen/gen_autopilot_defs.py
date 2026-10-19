"""The AUTOPILOT block's content (2026-10-19, the user: "control it through a SINGLE BLOCK ... a stand-in for now"): two sizes on
the base template every aero block uses (BasePowerableBlockBreakable: on / off, a small power draw, the game's power panel),
each LOOKING like one vanilla functional block - its own block model, breakable set, icon, placement and recipe referenced
by GUID, nothing copied, no model of ours (the user approves models; this is a stand-in, as the gears' first test blocks were):
  Large: the vanilla Event Controller (1 m: GameData/Vanilla/Content/Blocks/EventController/100)
  Small: the vanilla Timer (0.5 m: GameData/Vanilla/Content/Blocks/Timer/50)
None of those blocks' own logic comes with it (their event / timer components are in their compositions, not the template's).
Ours: the block definitions (name, kind, power), the prefabs - the aero mod injects AeroAutopilotComponent (its panel, the
saved state) by the server prefabs' GUIDs (AeroBlockGuids.AutopilotServerPrefabs), the kind and group (Aircraft Parts).

  python gen_autopilot_defs.py [<mod root>]      then: python tools\\shared\\build_mod_content.py "<mod root>"

The mod root defaults to the repository this script is in (never the live mod folder unless given). Writes only
Assets/Aero/Autopilot/ (re-runnable, fixed GUIDs, nothing removed). Helpers from gen_gear_defs.py.
"""
import os, sys, uuid
import gen_gear_defs as gd

ROOT = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
ROOT_REL = os.path.join("Aero", "Autopilot")
NS = uuid.UUID("ae200001-ae00-a900-0000-00000000c533")
G = lambda sid, n: f"ae200001-ae00-{sid}-{n:04x}-000000000001"
SHARED = "a900"
KIND, GROUP = G(SHARED, 0x20), G(SHARED, 0x21)
AERO_SUBCATEGORY = "ae200001-ae00-0000-0002-000000000001"   # ("Aircraft Parts": Assets/Aero/Menu/AeroSubcategory.def)

# per size: the vanilla block it looks like (its definitions in the Mod SDK's GameData/Vanilla/Content/Blocks/<folder>)
SIZES = {
    "Large": dict(sid="a901", kw=0.05, pcu=50, look="Event Controller (1 m)",
                  model="41a963b2-eb00-4ff9-99f6-73f66c46191c", breakable="884a02cc-32d8-4aee-a863-42f889bc34d0",
                  icon="{G}ebd17b65-e603-43d9-85d1-a5cbd6cc40df", placer="3f307784-9f66-4733-aa7f-8dc130520674",
                  recipe="f5ae2fd9-7701-42fb-b8de-37a4148a162d"),
    "Small": dict(sid="a902", kw=0.02, pcu=25, look="Timer (0.5 m)",
                  model="5a916eba-a37c-42b2-8f8e-96f961770fa3", breakable="2ab9e7be-44a2-43a7-a23a-a179e32c6c58",
                  icon="{G}d7b04087-e064-48c3-999f-7ff49562d4af", placer=None,
                  recipe="6e3ac5b6-0b6a-41c8-b7c0-8743ae3aded8"),
}


def folder_meta(rel):
    return {"$Bundles": {"System.Runtime": "1.0.0.0", "VRage": gd.VER_META}, "$Type": "VRage:Keen.VRage.ContentPipeline.Metafiles.MetaData",
            "$Value": {"AssetID": str(uuid.uuid5(NS, "folder:" + rel.lower())), "InternalItems": []}}


def gen_size(root, size, c):
    sid = c["sid"]; N = f"Autopilot_{size}"
    d = os.path.join(root, size)
    fbd, bind, spref, cpref, scomp, ccomp = G(sid, 0x1), G(sid, 0x10), G(sid, 0x11), G(sid, 0x12), G(sid, 0x13), G(sid, 0x14)
    bmd, brk = c["model"], c["breakable"]   # (the vanilla block's own: its look, its fractures)
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
    gd.definition(os.path.join(d, f"{N}_Client.partialdef"), gd.partial(B["client"], {"Guid": cpref, "_entity": {"Definition": ccomp}},
        "VRage:Keen.VRage.Core.Game.Definitions.PrefabDefinition"), cpref, True)
    gd.definition(os.path.join(d, f"{N}_ClientComposition.partialdef"), gd.partial(B["ccomp"], {"Guid": ccomp, "Components": {
        "$DeltaEncoded": True, "Keys": gd.CKEYS, "Changed": [
            {"Kind": "Update", "Index": 3, "Value": {"Definition": bmd}}, {"Kind": "Update", "Index": 4, "Value": {"Definition": brk}},
            {"Kind": "Update", "Index": 5, "Value": {"Definition": fbd}}], "Removed": []}},
        "VRage:Keen.VRage.DCS.Definitions.EntityCompositeDefinition"), ccomp, True)
    block = {"Guid": fbd, "UIData": {"Name": "Autopilot", "Icon": c["icon"]}, "Density": "d8adcfdc-f8e2-467e-9d27-78deae4057da",
             "Recipe": c["recipe"], "RecipeEfficiency": "30e09afc-4437-49fe-aff5-54553d05d3c4", "BlockKind": KIND, "PCU": c["pcu"],
             "ConsumedResource": {"Type": gd.ELECTRICITY, "Amount": c["kw"]}, "ConsumptionPriority": {"Priority": 6}}
    if c["placer"]: block["PlacerDefinition"] = c["placer"]
    gd.definition(os.path.join(d, f"{N}_PowerableBlockDefinition.partialdef"), gd.partial(B["fbd"], block,
        "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.PowerableBlockDefinition"), fbd, True)
    print(f"{N}: the vanilla {c['look']}'s look, {c['kw'] * 1000:.0f} W, server prefab {spref}")


def main():
    root = os.path.join(ROOT, "Assets", ROOT_REL)
    for size, c in SIZES.items(): gen_size(root, size, c)
    gd.definition(os.path.join(root, "Autopilot_BlockKind.def"), {"$Bundles": gd.B_GAME,
        "$Type": "Game2:Keen.Game2.Simulation.StreamedUI.Categories.BlockKindDefinitionObjectBuilder",
        "$Value": {"Guid": KIND, "Name": "Autopilot",
                   "Description": "Flight computer: hover hold and height hold for its ship, with envelope protection - set in its control panel.",
                   "OrderingPriority": 0, "BlockGroups": [{"Key": G(SHARED, 0x22), "Value": GROUP}]}}, KIND, False)
    gd.definition(os.path.join(root, "Autopilot_BlockGroup.def"), {"$Bundles": gd.B_GAME,
        "$Type": "Game2:Keen.Game2.Simulation.StreamedUI.Categories.BlockGroupDefinitionObjectBuilder",
        "$Value": {"Guid": GROUP, "Name": "Autopilot", "Order": 5,
                   "Subcategories": [{"Key": G(SHARED, 0x23), "Value": AERO_SUBCATEGORY}], "BlockKinds": []}}, GROUP, False)
    for rt, dirs, _ in os.walk(root):
        for dn in dirs:
            rel = os.path.relpath(os.path.join(rt, dn), os.path.join(ROOT, "Assets"))
            gd.write(os.path.join(rt, dn.lower() + ".meta"), folder_meta(rel))
    gd.write(os.path.join(os.path.dirname(root), "autopilot.meta"), folder_meta(ROOT_REL))
    print(f"generated {sum(len(f) for _, _, f in os.walk(root))} files under {root}")


main()
