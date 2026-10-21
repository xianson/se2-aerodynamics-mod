"""The MAGNETIC LANDING GEAR's definitions (2026-10-20; Scripts/Simulation/Integration/AeroMagGearComponent.cs):
Assets/Aero/MagneticGear/150/.

The block is the 1.5 m Wheeled Landing Gear (gen_gear_defs.py: the vanilla suspension on the control surfaces' base, its own
housing, leg and slide - the 150's model, breakable and subpart definitions referenced by GUID, nothing copied) with its wheel
swapped for a HIDDEN WHEEL: the vanilla 1.5 m wheel's prefab (DrivingWheel150: its model, so the corner module can fit its
cylinder; its client composition, so the mod hides its render - AeroHiddenWheelComponent) with a heavier collider (WHEEL_MASS: the
suspension's spring grows with the wheel's mass - GearStiffness, MagGearSizes.Wheel150 must match) and a corner module pointing at
it. The mod spawns the foot (a vanilla LandingGear100 pad grid) at run time: no definition of its own.

  python tools/meshgen/gen_maggear_defs.py [MOD_ROOT]     (default: this checkout) then: python tools/shared/build_mod_content.py .

Writes only its own folder's definition files (re-runnable, fixed GUIDs, nothing removed).
"""
import os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import gen_gear_defs as ggd   # (its helpers: the same base templates, keys and metas)

ROOT = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else os.path.abspath(os.path.join(HERE, "..", ".."))
ggd.BLOCKS = ROOT   # (its helpers write under BLOCKS: this checkout, never the live mod folder)
ROOT_REL = os.path.join("Aero", "MagneticGear")
G = ggd.G

WHEEL_MASS = 600            # kg (the vanilla 1.5 m wheel: 300) - MagGearSizes.Wheel150
DEFAULT_STIFFNESS = 0.3     # the terminal's suspension stiffness on a new block (x StiffnessScale 3, capped: GearStiffness)
DEFAULT_DAMPING = 0.5

# the vanilla 1.5 m wheel (Content\Blocks\SuspensionWheels\150): binding, prefabs, compositions, collider; its corner module
W150 = dict(binding="8661ba64-f626-4db3-9195-12bb06c3d30a", server="5bd17b72-35b2-4d08-abb1-014485e64d73", scomp="829af77f-cceb-44a1-85f7-863a03376fad",
            client="9d87a389-2f5e-4fa1-a854-688b89cb9f7f", ccomp="24d1bce7-83f4-4afe-8014-cea31aeffc6c", collider="04032345-3728-4f66-b8f8-78a7df386020",
            module="a9c8413a-751c-4d41-9913-e00e320ced95", audio="d1f1b6fe-c1da-423c-994f-ddc0c8e1a1cc")
W150_SKEYS = ["1afc026f-8012-4dec-9304-d8c3a4f68831", "f187949e-f943-405d-a055-ae10170c078c", "072ac9da-303a-48fc-bb5b-d5fdf4b2c374",
              "bb23b3f1-5af5-48de-a2ab-3ef4dac6514d", "08724a85-e8db-4f50-8fbe-9cd8db839436", "b98c3a33-f623-4d0a-abe5-113aabeab565",
              "c0ded670-782f-4e20-921a-63199aaabd65"]
W150_CKEYS = ["1afc026f-8012-4dec-9304-d8c3a4f68831", "e04a0bf5-5d94-4d3b-bbaa-de15acec4b97", "f187949e-f943-405d-a055-ae10170c078c",
              "a8e5b84c-db63-4361-bf65-ad5810d28416", "072ac9da-303a-48fc-bb5b-d5fdf4b2c374", "bb23b3f1-5af5-48de-a2ab-3ef4dac6514d",
              "08724a85-e8db-4f50-8fbe-9cd8db839436", "3e6742bc-2e1f-400c-b63b-ddadd68a5278", "5f5bcbaa-007f-4607-8d7d-6c08ca8be1b2",
              "6a3d2168-8216-47c0-98b6-cbba4c0fabc2"]
# the 1.5 m Wheeled Landing Gear's own model definitions (gen_gear_defs.py own_model, sid 9e02)
GEAR150 = dict(model=G("9e02", 0x15), breakable=G("9e02", 0x16), csp=G("9e02", 0x17))
GEAR150_ICON = os.path.join("Aero", "RetractableGear", "150", "Icon", "RetractableGear_150_Icon.png")
SID, WID = "9e15", "9e16"          # the block, its hidden wheel (AeroBlockGuids, AeroGearVariants)
KIND, GROUP = G(SID, 0x20), G(SID, 0x21)
SUBCATEGORY = "ae200001-ae00-0000-0002-000000000001"   # (the Wheeled Landing Gear's)


def hidden_wheel(d):
    """The hidden wheel: copies of the vanilla 1.5 m wheel's definitions, a heavier collider; a corner module pointing at it."""
    module, col, scomp, spref, ccomp, cpref, bind = G(WID, 0x60), G(WID, 0x62), G(WID, 0x63), G(WID, 0x64), G(WID, 0x65), G(WID, 0x66), G(WID, 0x67)
    N = "MagneticGear_150_HiddenWheel"
    ggd.definition(os.path.join(d, f"{N}_ColliderDefinition.partialdef"), ggd.partial(W150["collider"], {"Guid": col, "WheelMass": WHEEL_MASS},
        "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks.Wheels.WheelColliderDefinition"), col, True)
    ggd.definition(os.path.join(d, f"{N}_ServerComposition.partialdef"), ggd.partial(W150["scomp"], {"Guid": scomp, "Components": {"$DeltaEncoded": True,
        "Keys": W150_SKEYS, "Changed": [{"Kind": "Update", "Index": 3, "Value": {"Definition": col}}], "Removed": []}},
        "VRage:Keen.VRage.DCS.Definitions.EntityCompositeDefinition"), scomp, True)
    ggd.definition(os.path.join(d, f"{N}_Server.partialdef"), ggd.partial(W150["server"], {"Guid": spref, "_entity": {"Definition": scomp}},
        "VRage:Keen.VRage.Core.Game.Definitions.PrefabDefinition", ggd.B_VRAGE), spref, True)
    ggd.definition(os.path.join(d, f"{N}_ClientComposition.partialdef"), ggd.partial(W150["ccomp"], {"Guid": ccomp, "Components": {"$DeltaEncoded": True,
        "Keys": W150_CKEYS, "Changed": [{"Kind": "Update", "Index": 5, "Value": {"Definition": col}}], "Removed": []}},
        "VRage:Keen.VRage.DCS.Definitions.EntityCompositeDefinition"), ccomp, True)
    ggd.definition(os.path.join(d, f"{N}_Client.partialdef"), ggd.partial(W150["client"], {"Guid": cpref, "_entity": {"Definition": ccomp}},
        "VRage:Keen.VRage.Core.Game.Definitions.PrefabDefinition", ggd.B_VRAGE), cpref, True)
    ggd.definition(os.path.join(d, f"{N}.partialdef"), ggd.partial(W150["binding"], {"Guid": bind, "ServerComposite": scomp, "ClientPrefab": cpref},
        "VRage:Keen.VRage.Multiplayer.Data.PrefabBindingDefinition", ggd.B_VRAGE), bind, True)
    ggd.definition(os.path.join(d, "MagneticGear_150_WheelCornerModuleDefinition.partialdef"), ggd.partial(W150["module"], {"Guid": module, "WheelPrefab": spref},
        "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks.Wheels.WheelCornerModuleDefinition"), module, True)
    return module


def block(d, module):
    """The block: the 1.5 m gear's composition (gen_gear_defs.gen_size, own model) on the hidden wheel's corner module."""
    B = ggd.BASE; N = "MagneticGear_150"
    fbd, bind, spref, cpref, scomp, ccomp = G(SID, 0x1), G(SID, 0x10), G(SID, 0x11), G(SID, 0x12), G(SID, 0x13), G(SID, 0x14)
    ggd.definition(os.path.join(d, f"{N}.partialdef"), ggd.partial(B["binding"], {"Guid": bind, "ServerComposite": scomp, "ClientPrefab": cpref},
        "VRage:Keen.VRage.Multiplayer.Data.PrefabBindingDefinition", ggd.B_VRAGE), bind, True)
    ggd.definition(os.path.join(d, f"{N}_ServerComposition.partialdef"), ggd.partial(B["scomp"], {"Guid": scomp,
        "TagSlots": {"$DeltaEncoded": True, "RemovedKeys": [], "ModifiedKeys": [
            {"$Key": "WheelCornerModuleComponent", "$Value": ggd.K_WHEEL}, {"$Key": "WheelOnGroundCollectorComponent", "$Value": ggd.K_GROUND}]},
        "Components": {"$DeltaEncoded": True, "Keys": ggd.SKEYS + [ggd.K_WHEEL, ggd.K_GROUND], "Changed": [
            {"Kind": "Update", "Index": 2, "Value": {"Definition": GEAR150["model"]}},
            {"Kind": "Update", "Index": 3, "Value": {"Definition": GEAR150["breakable"]}},
            {"Kind": "Update", "Index": 4, "Value": {"Definition": fbd}},
            {"Kind": "Insert", "Index": 5, "Value": {"Definition": module, "Type": "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks.Wheels.WheelCornerModuleComponent"}},
            {"Kind": "Insert", "Index": 6, "Value": {"Definition": None, "Type": "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks.Wheels.WheelOnGroundCollectorComponent"}}],
            "Removed": []}},
        "VRage:Keen.VRage.DCS.Definitions.EntityCompositeDefinition"), scomp, True)
    ggd.definition(os.path.join(d, f"{N}_Server.partialdef"), ggd.partial(B["server"], {"Guid": spref, "_entity": {"Definition": scomp, "ObjectBuilders": {
        "$DeltaEncoded": True, "Keys": ggd.SKEYS + [ggd.K_WHEEL, ggd.K_GROUND], "Changed": [
            {"Kind": "Insert", "Index": 5, "Value": {"$Type": "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks.Wheels.WheelCornerModuleObjectBuilder",
                "SuspensionDamping": DEFAULT_DAMPING, "SuspensionStiffness": DEFAULT_STIFFNESS, "TangentialAccelerationRate": 1, "CurrentSuspensionDistance": 0,
                "IsParked": False, "ResourceConsumptionState": "IdleAndSupplied"}},
            {"Kind": "Insert", "Index": 6, "Value": None}], "Removed": []}}},
        "VRage:Keen.VRage.Core.Game.Definitions.PrefabDefinition", ggd.B_GAME), spref, True)
    ckeys = ggd.CKEYS + [ggd.K_AUDIO, ggd.K_CSP]
    tags = [{"$Key": "WheelCornerModuleAudioComponent", "$Value": ggd.K_AUDIO}, {"$Key": "ConditionalSubPartsComponent", "$Value": ggd.K_CSP}]
    changed = [{"Kind": "Update", "Index": 3, "Value": {"Definition": GEAR150["model"]}},
               {"Kind": "Update", "Index": 4, "Value": {"Definition": GEAR150["breakable"]}},
               {"Kind": "Update", "Index": 5, "Value": {"Definition": fbd}},
               {"Kind": "Insert", "Index": 9, "Value": {"Definition": W150["audio"], "Type": "Game2:Keen.Game2.Client.WorldObjects.CubeBlocks.MechanicalBlocks.Wheels.WheelCornerModuleAudioComponent"}},
               {"Kind": "Insert", "Index": 10, "Value": {"Definition": GEAR150["csp"], "Type": "Game2:Keen.Game2.Client.WorldObjects.CubeBlocks.Effects.ConditionalSubPartsComponent"}}]
    ggd.definition(os.path.join(d, f"{N}_ClientComposition.partialdef"), ggd.partial(B["ccomp"], {"Guid": ccomp,
        "TagSlots": {"$DeltaEncoded": True, "RemovedKeys": [], "ModifiedKeys": tags},
        "Components": {"$DeltaEncoded": True, "Keys": ckeys, "Changed": changed, "Removed": []}},
        "VRage:Keen.VRage.DCS.Definitions.EntityCompositeDefinition"), ccomp, True)
    ggd.definition(os.path.join(d, f"{N}_Client.partialdef"), ggd.partial(B["client"], {"Guid": cpref, "_entity": {"Definition": ccomp, "ObjectBuilders": {
        "$DeltaEncoded": True, "Keys": ckeys, "Changed": [{"Kind": "Insert", "Index": 9 + i, "Value": None} for i in range(len(ckeys) - len(ggd.CKEYS))], "Removed": []}}},
        "VRage:Keen.VRage.Core.Game.Definitions.PrefabDefinition", ggd.B_GAME), cpref, True)
    ggd.definition(os.path.join(d, f"{N}_PowerableBlockDefinition.partialdef"), ggd.partial(B["fbd"], {"Guid": fbd,
        "UIData": {"Name": "Magnetic Landing Gear", "Icon": "{G}" + ggd.file_id(GEAR150_ICON)}, "PlacerDefinition": ggd.PLACEMENT,
        "DisplaySizeOverride": 1.5, "Density": "d8adcfdc-f8e2-467e-9d27-78deae4057da", "Recipe": ggd.RECIPE,
        "RecipeEfficiency": "30e09afc-4437-49fe-aff5-54553d05d3c4", "BlockKind": KIND, "PCU": 800,
        "ConsumedResource": {"Type": ggd.ELECTRICITY, "Amount": 3}, "ConsumptionPriority": {"Priority": 6}},
        "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.PowerableBlockDefinition"), fbd, True)


def main():
    root = os.path.join(ROOT, "Assets", ROOT_REL)
    d = os.path.join(root, "150")
    block(d, hidden_wheel(os.path.join(d, "HiddenWheel")))
    ggd.definition(os.path.join(root, "MagneticGear_BlockKind.def"), {"$Bundles": ggd.B_GAME,
        "$Type": "Game2:Keen.Game2.Simulation.StreamedUI.Categories.BlockKindDefinitionObjectBuilder",
        "$Value": {"Guid": KIND, "Name": "Magnetic Landing Gear",
                   "Description": "Landing gear with a magnetic pad for a foot: it locks like a landing gear, rides a suspension and folds into its bay.",
                   "OrderingPriority": 0, "BlockGroups": [{"Key": G(SID, 0x22), "Value": GROUP}]}}, KIND, False)
    ggd.definition(os.path.join(root, "MagneticGear_BlockGroup.def"), {"$Bundles": ggd.B_GAME,
        "$Type": "Game2:Keen.Game2.Simulation.StreamedUI.Categories.BlockGroupDefinitionObjectBuilder",
        "$Value": {"Guid": GROUP, "Name": "Magnetic Landing Gear", "Order": 3,
                   "Subcategories": [{"Key": G(SID, 0x23), "Value": SUBCATEGORY}], "BlockKinds": []}}, GROUP, False)
    for rt, dirs, _ in os.walk(root):
        for dn in dirs:
            rel = os.path.relpath(os.path.join(rt, dn), os.path.join(ROOT, "Assets"))
            ggd.write(os.path.join(rt, dn.lower() + ".meta"), ggd.folder_meta(rel))
    ggd.write(os.path.join(os.path.dirname(root), "magneticgear.meta"), ggd.folder_meta(ROOT_REL))
    print(f"generated {sum(len(f) for _, _, f in os.walk(root))} files under {root}")


if __name__ == "__main__": main()
