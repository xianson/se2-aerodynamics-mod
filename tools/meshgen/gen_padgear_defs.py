"""The PAD-FOOT retractable gears (2026-10-20, the user: "No lock, custom wheel model that is the landing gear, and constrained";
then "landing gear models available"): Assets/Aero/PadGear/<size>/.

One gear per vanilla landing-gear model in SE2 4.0 (GameData/Vanilla: the magnetic landing gears LandingGear100 / LandingGear500 and
the magnetic plates Magneticplate50 / MagneticPlate250), each on the Wheeled Landing Gear housing it fits (gen_gear_defs.py's
housings, leg and slide - their model, breakable and subpart definitions referenced by GUID, nothing copied). The suspension's
WHEEL is the foot: a copy of the vanilla wheel prefab of that size whose model is the landing-gear mesh (the SDK's FBX, copied in -
a straight reuse of a vanilla mesh as a PLACEHOLDER: no kitbash, nothing reshaped; scaled whole where the housing needs it) and
whose collider is a flat puck the corner module can fit a cylinder to (DetermineWheelRadius); the mod swaps the body's collider for a
box of the foot's bounds, holds it on ONE slider constraint (the vanilla wheel constraint released) and carries it on the oleo strut
(Integration/AeroGearComponent.PadFoot.cs). The foot is heavy
(WheelMass, per size) and its corner module has no drive and no steering.

The numbers the scripts use are written to padgear_spec.json beside this file (AeroGearVariants must match: Tests/GearTests).

  python tools/meshgen/gen_padgear_defs.py [MOD_ROOT]        (default: this checkout)
  python tools/meshgen/gen_padgear_defs.py [MOD_ROOT] --icons   (also the icons: Blender, background, Cycles)
  then: python tools/shared/build_mod_content.py <MOD_ROOT>

Reads the SDK (the FBX and the vanilla .model files) and the housings' definitions; writes only its own folder (re-runnable, fixed
GUIDs, nothing removed). The FBX are git-ignored (Assets/Aero/**/*.fbx): this script copies them in again.
"""
import json, math, os, shutil, struct, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import gen_gear_defs as ggd

ARGS = [a for a in sys.argv[1:] if not a.startswith("--")]
ROOT = os.path.abspath(ARGS[0]) if ARGS else os.path.abspath(os.path.join(HERE, "..", ".."))
ggd.BLOCKS = ROOT
ROOT_REL = os.path.join("Aero", "PadGear")
SDK = r"D:\SteamLibrary\steamapps\common\Space Engineers 2 - Mod SDK\GameData\Vanilla\Assets\Blocks"
BLENDER = r"D:\downloadsarchive\blender-5.0.1-windows-x64\blender.exe"
G = ggd.G
DEFAULT_STIFFNESS, DEFAULT_DAMPING = 0.3, 0.5

# the vanilla wheels the pad wheels copy (Content\Blocks\SuspensionWheels\<size>): prefab binding, server / client prefabs and
# compositions, model component definition and its dummy keys (0: the wheel pivot), collider, corner module
WHEELS = {
    "100": dict(binding="ae63d11a-ed9c-4134-b899-1d170c9edab3", server="b7192c3a-fe3d-4d03-912e-5ab71af23f43", scomp="38f45994-0334-461e-bdf6-7ec4fcde9208",
                client="03e5c3b4-8ba6-4d66-91fb-ab92ad507fe9", ccomp="b664ded1-78cd-416f-a66d-25cb863875ae", mcd="e9fd9923-88b6-49f0-83ea-d25199516983",
                dkeys=["851464db-4855-4cc2-8779-dc4c9e3d271c", "12b25a84-f6b2-456a-8795-c3388a040662"],
                collider="33211f44-cd8b-417f-9025-32c301872341", module="5301f8e8-a1de-4512-aa5a-57970a180110",
                skeys=ggd.W100_SKEYS, ckeys=ggd.W100_CKEYS),
    "150": dict(binding="8661ba64-f626-4db3-9195-12bb06c3d30a", server="5bd17b72-35b2-4d08-abb1-014485e64d73", scomp="829af77f-cceb-44a1-85f7-863a03376fad",
                client="9d87a389-2f5e-4fa1-a854-688b89cb9f7f", ccomp="24d1bce7-83f4-4afe-8014-cea31aeffc6c", mcd="26bef8bf-8f25-491c-9318-b5db7180bd0b",
                dkeys=["851464db-4855-4cc2-8779-dc4c9e3d271c", "d7a95522-d8b6-466f-937c-ab59443dd0b6"],
                collider="04032345-3728-4f66-b8f8-78a7df386020", module="a9c8413a-751c-4d41-9913-e00e320ced95",
                skeys=ggd.W100_SKEYS + ["c0ded670-782f-4e20-921a-63199aaabd65"], ckeys=ggd.W100_CKEYS),
    "250": dict(binding="31510e3f-f1ab-4640-95a6-c36e3648adfb", server="f6460357-a264-4946-a124-e3198ee9f722", scomp="f4b59af9-03a3-4944-b14f-31da9ddb8669",
                client="486495c1-14fd-47cb-b659-f8f4a49d9afe", ccomp="206cd2bf-c2a1-4b1a-b9e4-ff5cd00a5cbb", mcd="858f6f57-27bd-4b43-9ccb-63fef344e883",
                dkeys=["851464db-4855-4cc2-8779-dc4c9e3d271c", "5de653ca-71e6-4b41-b8de-4b286338e2c7"],
                collider="7591e314-3476-44d3-a29f-df3984324db0", module="a240c373-8a4f-422b-83c2-caf6d609b121",
                skeys=ggd.W100_SKEYS, ckeys=ggd.W100_CKEYS),
}

# THE VARIANTS: one per vanilla landing-gear model. bounds: the LOD0 FBX's own (VRage axes, m - measured in Blender, lgprobe);
# t0: the vanilla .model's LOD0 translation; scale: whole-mesh scale to fit the housing. mass: the wheel body (kg; the vanilla
# wheels: 1 m 200, 1.5 m 300, 2.5 m 500); rated: the mass one gear carries at 2 g (kg) - the vanilla wheel of its housing's
# (WheelRating.RatedFor: 0.5 / 1 / 1.5 / 2.5 m - the oleo strut carries it, PadFootSpec's table). name: the block (the game appends the size).
VARIANTS = [
    dict(size="050", sid="9e21", wid="9e31", wheel="100", name="Retractable Gear (Plate)", model="Magneticplate50",
         dir=r"MagneticPlates\50\Models\NonFractured", lods=["Magneticplate50", "Magneticplate50_LOD1", "Magneticplate50_LOD2"],
         lo=(-0.251, -0.126, -0.251), hi=(0.251, 0.126, 0.251), scale=1.0, mass=150, rated=3750),
    dict(size="100", sid="9e22", wid="9e32", wheel="100", name="Retractable Gear (Pad)", model="LandingGear100",
         dir=r"LandingGear\LandingGear\100\Models\NonFractured", lods=["LandingGear100", "LandingGear100_LOD1", "LandingGear100_LOD2"],
         lo=(-0.251, -0.501, -0.500), hi=(0.251, 0.501, 0.502), scale=1.0, mass=250, rated=15000),
    dict(size="150", sid="9e23", wid="9e33", wheel="150", name="Retractable Gear (Pad)", model="LandingGear500",
         dir=r"LandingGear\LandingGear\500\Models\NonFractured", lods=["LandingGear500", "LandingGear500_LOD1", "LandingGear500_LOD2"],
         lo=(-1.263, -2.500, -2.505), hi=(1.263, 2.504, 2.501), scale=0.3, mass=600, rated=33750),
    dict(size="250", sid="9e24", wid="9e34", wheel="250", name="Retractable Gear (Plate)", model="MagneticPlate250",
         dir=r"MagneticPlates\250\Models\NonFractured", lods=["MagneticPlate250", "MagneticPlate250_LOD1", "MagneticPlate250_LOD2"],
         lo=(-1.251, -0.255, -1.251), hi=(1.251, 0.251, 1.251), scale=1.0, mass=1200, rated=93750),
]
KIND, GROUP = G("9e20", 0x20), G("9e20", 0x21)
SUBCATEGORY = "ae200001-ae00-0000-0002-000000000001"   # (the Wheeled Landing Gear's)


def qrot(q, v):
    x, y, z, w = q; vx, vy, vz = v
    tx, ty, tz = 2 * (y * vz - z * vy), 2 * (z * vx - x * vz), 2 * (x * vy - y * vx)
    return (vx + w * tx + (y * tz - z * ty), vy + w * ty + (z * tx - x * tz), vz + w * tz + (x * ty - y * tx))


def housing(size):
    """The housing's dummies (its BlockModelDefinition): the wheel rest (position, orientation, travel = its scale Y), the rod hub."""
    d = json.load(open(os.path.join(ROOT, "Assets", "Aero", "RetractableGear", size, f"RetractableGear_{size}_BlockModelDefinition.partialdef"), encoding="utf-8"))
    out = {}
    for c in d["$Value"]["Manipulator"]["Dummies"]["Changed"]:
        v = c["Value"]; t = v["Transform"]["Transform"]
        out[v["Name"]] = (tuple(t["Position"][k] for k in "XYZ"), tuple(t["Orientation"][k] for k in "XYZW"), v["Scale"]["Y"])
    return out


def layout(v):
    """The foot in its wheel's frame (the wheel turned as the block: its pivot dummy is the rest dummy's turn): the mesh scaled, its
    top centre at the wheel's origin, which the suspension puts on the rod hub (the pivot = attach(0) - hub); the box and the puck."""
    s = v["scale"]; lo, hi = v["lo"], v["hi"]
    t0 = v["t0"]
    top = ((lo[0] + hi[0]) / 2 + t0[0], hi[1] + t0[1], (lo[2] + hi[2]) / 2 + t0[2])   # (model space: LOD0 raw + its translation)
    T = tuple(-s * c for c in top)
    w = tuple(s * (hi[i] - lo[i]) for i in range(3))
    H = w[1]
    rest_p, rest_q, travel = housing(v["size"])["Wheel Rest Position"]
    hub = housing(v["size"])["GearRodHub"][0]
    up = qrot(rest_q, (0.0, 1.0, 0.0))
    attach0 = tuple(rest_p[i] - travel / 2 * up[i] for i in range(3))
    pivot = tuple(attach0[i] - hub[i] for i in range(3))
    r = 0.5 * min(w[0], w[2])
    return dict(T=T, t0=t0, H=H, size=w, box_center=(0.0, -H / 2, 0.0), box_half=(w[0] / 2, H / 2, w[2] / 2),
                pivot=pivot, pivot_q=rest_q, travel=travel, puck_r=r, puck_h=0.4 * r, puck_y=-H + r)


def vanilla_model(v):
    path = os.path.join(SDK, v["dir"], v["model"] + ".model")
    return json.load(open(path, encoding="utf-8-sig"))["$Value"]


def write_puck(path_gltf, name, r, h, y, n=24):
    """A convex puck (axis Y, radius r, half height h, centre y) as the Havok glTF the content builder takes (as the Blender exports)."""
    pts = []
    for cap in (-h, h):
        for i in range(n):
            a = 2 * math.pi * i / n
            pts.append((r * math.cos(a), y + cap, r * math.sin(a)))
    idx = []
    for i in range(1, n - 1):
        idx += [0, i + 1, i]; idx += [n, n + i, n + i + 1]
    for i in range(n):
        j = (i + 1) % n
        idx += [i, j, n + i, j, n + j, n + i]
    vb = b"".join(struct.pack("<3f", *p) for p in pts); ib = b"".join(struct.pack("<H", k) for k in idx)
    if len(ib) % 4: ib += b"\0\0"
    binname = os.path.splitext(os.path.basename(path_gltf))[0] + ".bin"
    with open(os.path.join(os.path.dirname(path_gltf), binname), "wb") as f: f.write(vb + ib)
    lo = [min(p[k] for p in pts) for k in range(3)]; hi = [max(p[k] for p in pts) for k in range(3)]
    g = {"asset": {"generator": "gen_padgear_defs.py", "version": "2.0"}, "extensionsUsed": ["MSFT_collision_primitives", "MSFT_rigid_bodies"],
         "extensions": {"MSFT_collision_primitives": {"colliders": [{"collisionSystems": ["System_0"], "collideWithSystems": ["System_0"], "convex": {"mesh": 0}}]},
                        "MSFT_rigid_bodies": {"physicsMaterials": [{"staticFriction": 0.5, "dynamicFriction": 0.5, "restitution": 0}]}},
         "scene": 0, "scenes": [{"name": "Scene", "nodes": [0]}],
         "nodes": [{"extensions": {"MSFT_rigid_bodies": {"rigidBody": {"inverseMass": 1}, "collider": 0, "physicsMaterial": 0}},
                    "extras": {"ExportName": name}, "mesh": 0, "name": name}],
         "meshes": [{"name": name, "primitives": [{"attributes": {"POSITION": 0}, "indices": 1}]}],
         "accessors": [{"bufferView": 0, "componentType": 5126, "count": len(pts), "max": hi, "min": lo, "type": "VEC3"},
                       {"bufferView": 1, "componentType": 5123, "count": len(idx), "type": "SCALAR"}],
         "bufferViews": [{"buffer": 0, "byteLength": len(vb), "byteOffset": 0, "target": 34962},
                         {"buffer": 0, "byteLength": len(idx) * 2, "byteOffset": len(vb), "target": 34963}],
         "buffers": [{"byteLength": len(vb) + len(ib), "uri": binname}]}
    ggd.write(path_gltf, g)


def foot_model(v, L, d, rel):
    """The foot's FBX (LOD0..2) copied from the SDK, its puck collider, its .model (the vanilla mesh, scaled, top centre at 0)."""
    src = os.path.join(SDK, v["dir"]); md = os.path.join(d, "Models"); os.makedirs(md, exist_ok=True)
    ids = []
    for stem in v["lods"]:
        out = os.path.join(md, stem + ".fbx"); shutil.copy(os.path.join(src, stem + ".fbx"), out)
        fid = ggd.file_id(os.path.join(rel, "Models", stem + ".fbx")); ids.append(fid); ggd.write(out + ".meta", ggd.file_meta(fid))
    N = f"PadGear_{v['size']}_Foot"
    col = os.path.join(md, N + "_collision.gltf")
    write_puck(col, N + "_Collision", L["puck_r"], L["puck_h"], L["puck_y"])
    for f in (col, os.path.join(md, N + "_collision.bin")):
        ggd.write(f + ".meta", ggd.file_meta(ggd.file_id(os.path.join(rel, "Models", os.path.basename(f)))))
    vm = vanilla_model(v)
    last = {m["MaterialName"] for m in vm["MaterialAssignments"] if m["MaterialName"].endswith("_LastLOD")}
    mats = [m for m in vm["MaterialAssignments"] if m["MaterialName"] not in last]
    s = v["scale"]; T = L["T"]; t0 = L["t0"]
    geo = []
    for i, fid in enumerate(ids):
        tr = tuple(T[k] + (s * t0[k] if i == 0 else 0.0) for k in range(3))   # (LOD0 carries the vanilla offset; LOD1+ have it in their mesh)
        used = [u for u in vm["GeometryAssets"][i]["UsedMaterials"] if u not in last]
        geo.append({"AssetReference": "{G}" + fid, "UsedMaterials": used,
                    "Transformations": {"Translation": {"X": round(tr[0], 5), "Y": round(tr[1], 5), "Z": round(tr[2], 5)}, "Rotation": {"X": 0, "Y": 0, "Z": 0}, "Scale": s}})
    m = G(v["wid"], 0x105)
    path = os.path.join(md, N + ".model")
    ggd.write(path, {"$Bundles": {"System.Runtime": "1.0.0.0", "VRage": "2.0.1.118"}, "$Type": "VRage:Keen.VRage.ContentPipeline.Models.ModelAssetData",
                     "$Value": {"MaterialAssignments": mats, "MaterialStateOverrides": [], "GeometryAssets": geo, "SnapToGrid": False, "SuggestedModelSize": None,
                                "CollisionShapesAsset": {"AssetReference": "{G}" + ggd.file_id(os.path.join(rel, "Models", os.path.basename(col))),
                                                         "GroupsByFracture": [], "GeneratePreciseCollider": True}}})
    ggd.write(path + ".meta", ggd.asset_meta(m, ("P_MODEL", 40), N + ".vrm"))
    return m


def foot_wheel(v, L, d, m):
    """The foot as the suspension's wheel: the vanilla wheel's definitions with this model (its pivot dummy turned as the wheel rest
    dummy: the foot spawns level), a heavy collider, and a corner module with no drive and no steering."""
    W = WHEELS[v["wheel"]]; wid = v["wid"]; N = f"PadGear_{v['size']}_Foot"
    mcd, col, scomp, spref, ccomp, cpref, bind, module = (G(wid, 0x61 + i) for i in range(8))
    q = L["pivot_q"]; p = L["pivot"]; H = L["H"]; sz = L["size"]
    ggd.definition(os.path.join(d, f"{N}_ModelComponentDefinition.partialdef"), ggd.partial(W["mcd"], {"Guid": mcd, "Model": "{G}" + m,
        "Dummies": {"$DeltaEncoded": True, "Keys": W["dkeys"], "Changed": [
            {"Kind": "Update", "Index": 0, "Value": {"Transform": {"Transform": {"Position": dict(zip("XYZ", (round(c, 5) for c in p))), "Orientation": dict(zip("XYZW", q))}},
                                                     "Scale": {"X": 0.1, "Y": 0.1, "Z": 0.1}}},
            {"Kind": "Update", "Index": 1, "Value": {"Transform": {"Transform": {"Position": {"X": 0, "Y": round(-H, 5), "Z": 0}}},
                                                     "Scale": {"X": round(sz[0], 4), "Y": 0.1, "Z": round(sz[2], 4)}}}],
            "Removed": []}}, "VRage:Keen.VRage.Game.Components.Models.ModelComponentDefinition", ggd.B_VRAGE), mcd, True)
    ggd.definition(os.path.join(d, f"{N}_ColliderDefinition.partialdef"), ggd.partial(W["collider"], {"Guid": col, "WheelMass": v["mass"]},
        "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks.Wheels.WheelColliderDefinition"), col, True)
    ggd.definition(os.path.join(d, f"{N}_ServerComposition.partialdef"), ggd.partial(W["scomp"], {"Guid": scomp, "Components": {"$DeltaEncoded": True,
        "Keys": W["skeys"], "Changed": [{"Kind": "Update", "Index": 1, "Value": {"Definition": mcd}}, {"Kind": "Update", "Index": 3, "Value": {"Definition": col}}],
        "Removed": []}}, "VRage:Keen.VRage.DCS.Definitions.EntityCompositeDefinition"), scomp, True)
    ggd.definition(os.path.join(d, f"{N}_Server.partialdef"), ggd.partial(W["server"], {"Guid": spref, "_entity": {"Definition": scomp}},
        "VRage:Keen.VRage.Core.Game.Definitions.PrefabDefinition", ggd.B_VRAGE), spref, True)
    ggd.definition(os.path.join(d, f"{N}_ClientComposition.partialdef"), ggd.partial(W["ccomp"], {"Guid": ccomp, "Components": {"$DeltaEncoded": True,
        "Keys": W["ckeys"], "Changed": [{"Kind": "Update", "Index": 2, "Value": {"Definition": mcd}}, {"Kind": "Update", "Index": 5, "Value": {"Definition": col}}],
        "Removed": []}}, "VRage:Keen.VRage.DCS.Definitions.EntityCompositeDefinition"), ccomp, True)
    ggd.definition(os.path.join(d, f"{N}_Client.partialdef"), ggd.partial(W["client"], {"Guid": cpref, "_entity": {"Definition": ccomp}},
        "VRage:Keen.VRage.Core.Game.Definitions.PrefabDefinition", ggd.B_VRAGE), cpref, True)
    ggd.definition(os.path.join(d, f"{N}.partialdef"), ggd.partial(W["binding"], {"Guid": bind, "ServerComposite": scomp, "ClientPrefab": cpref},
        "VRage:Keen.VRage.Multiplayer.Data.PrefabBindingDefinition", ggd.B_VRAGE), bind, True)
    ggd.definition(os.path.join(d, f"PadGear_{v['size']}_WheelCornerModuleDefinition.partialdef"), ggd.partial(W["module"], {"Guid": module,
        "WheelPrefab": spref, "MaxTorque": 0, "MaxSteeringAngle": 0},
        "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks.Wheels.WheelCornerModuleDefinition"), module, True)
    return module, spref, cpref


def block(v, d, module, icon):
    """The block: the housing size's composition (gen_gear_defs.gen_size, own model) on the foot's corner module."""
    B = ggd.BASE; size = v["size"]; sid = v["sid"]; hs = ggd.SIZES[size]; hsid = hs["sid"]; N = f"PadGear_{size}"
    model, brk, csp = G(hsid, 0x15), G(hsid, 0x16), G(hsid, 0x17)
    fbd, bind, spref, cpref, scomp, ccomp = G(sid, 0x1), G(sid, 0x10), G(sid, 0x11), G(sid, 0x12), G(sid, 0x13), G(sid, 0x14)
    ggd.definition(os.path.join(d, f"{N}.partialdef"), ggd.partial(B["binding"], {"Guid": bind, "ServerComposite": scomp, "ClientPrefab": cpref},
        "VRage:Keen.VRage.Multiplayer.Data.PrefabBindingDefinition", ggd.B_VRAGE), bind, True)
    ggd.definition(os.path.join(d, f"{N}_ServerComposition.partialdef"), ggd.partial(B["scomp"], {"Guid": scomp,
        "TagSlots": {"$DeltaEncoded": True, "RemovedKeys": [], "ModifiedKeys": [
            {"$Key": "WheelCornerModuleComponent", "$Value": ggd.K_WHEEL}, {"$Key": "WheelOnGroundCollectorComponent", "$Value": ggd.K_GROUND}]},
        "Components": {"$DeltaEncoded": True, "Keys": ggd.SKEYS + [ggd.K_WHEEL, ggd.K_GROUND], "Changed": [
            {"Kind": "Update", "Index": 2, "Value": {"Definition": model}},
            {"Kind": "Update", "Index": 3, "Value": {"Definition": brk}},
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
    changed = [{"Kind": "Update", "Index": 3, "Value": {"Definition": model}},
               {"Kind": "Update", "Index": 4, "Value": {"Definition": brk}},
               {"Kind": "Update", "Index": 5, "Value": {"Definition": fbd}},
               {"Kind": "Insert", "Index": 9, "Value": {"Definition": hs["audio"], "Type": "Game2:Keen.Game2.Client.WorldObjects.CubeBlocks.MechanicalBlocks.Wheels.WheelCornerModuleAudioComponent"}},
               {"Kind": "Insert", "Index": 10, "Value": {"Definition": csp, "Type": "Game2:Keen.Game2.Client.WorldObjects.CubeBlocks.Effects.ConditionalSubPartsComponent"}}]
    ggd.definition(os.path.join(d, f"{N}_ClientComposition.partialdef"), ggd.partial(B["ccomp"], {"Guid": ccomp,
        "TagSlots": {"$DeltaEncoded": True, "RemovedKeys": [], "ModifiedKeys": tags},
        "Components": {"$DeltaEncoded": True, "Keys": ckeys, "Changed": changed, "Removed": []}},
        "VRage:Keen.VRage.DCS.Definitions.EntityCompositeDefinition"), ccomp, True)
    ggd.definition(os.path.join(d, f"{N}_Client.partialdef"), ggd.partial(B["client"], {"Guid": cpref, "_entity": {"Definition": ccomp, "ObjectBuilders": {
        "$DeltaEncoded": True, "Keys": ckeys, "Changed": [{"Kind": "Insert", "Index": 9 + i, "Value": None} for i in range(len(ckeys) - len(ggd.CKEYS))], "Removed": []}}},
        "VRage:Keen.VRage.Core.Game.Definitions.PrefabDefinition", ggd.B_GAME), cpref, True)
    ggd.definition(os.path.join(d, f"{N}_PowerableBlockDefinition.partialdef"), ggd.partial(B["fbd"], {"Guid": fbd,
        "UIData": {"Name": v["name"], "Icon": icon}, "PlacerDefinition": ggd.PLACEMENT,
        "DisplaySizeOverride": hs["size"], "Density": "d8adcfdc-f8e2-467e-9d27-78deae4057da", "Recipe": ggd.RECIPE,
        "RecipeEfficiency": "30e09afc-4437-49fe-aff5-54553d05d3c4", "BlockKind": KIND, "PCU": hs["pcu"],
        "ConsumedResource": {"Type": ggd.ELECTRICITY, "Amount": hs["kw"]}, "ConsumptionPriority": {"Priority": 6}},
        "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.PowerableBlockDefinition"), fbd, True)
    return spref, cpref


def icon_path(v): return os.path.join(ROOT, "Assets", ROOT_REL, v["size"], "Icon", f"PadGear_{v['size']}_Icon.png")


def render_icons(spec):
    """Each gear's icon: its housing, leg and rod (gen_gear_defs.py's exports) and its foot, as placed in game, rendered by Blender
    (padgear_icon.py, background, Cycles - the GPU drawing render_icon.py uses is not there in background mode)."""
    for v in VARIANTS:
        job = os.path.join(os.environ.get("TEMP", HERE), f"padgear_icon_job_{v['size']}.json")
        L = spec[v["size"]]
        hdir = os.path.join(ROOT, "Assets", "Aero", "RetractableGear", v["size"], "Models", "NonFractured")
        hd = housing(v["size"])
        json.dump({"out": icon_path(v),
                   "housing": os.path.join(hdir, f"RetractableGear_{v['size']}.fbx"),
                   "leg": [os.path.join(hdir, "Subpart01", f"RetractableGear_{v['size']}_S1_Leg.fbx"), hd["GearLegHinge"][0]],
                   "rod": [os.path.join(hdir, "Subpart02", f"RetractableGear_{v['size']}_S2_Rod.fbx"), hd["GearRodHub"][0]],
                   "foot": [os.path.join(SDK, v["dir"], v["lods"][0] + ".fbx"), v["scale"],
                            [hd["GearRodHub"][0][k] + L["T"][k] + v["scale"] * L["t0"][k] for k in range(3)]]},
                  open(job, "w"))
        r = subprocess.run([BLENDER, "-b", "--python", os.path.join(HERE, "padgear_icon.py"), "--", job], capture_output=True, text=True, timeout=600)
        os.remove(job)
        print(f"icon {v['size']}: {'ok' if os.path.exists(icon_path(v)) else 'FAILED ' + r.stdout[-400:] + r.stderr[-400:]}")


def main():
    root = os.path.join(ROOT, "Assets", ROOT_REL)
    spec = {}
    for v in VARIANTS:
        v["t0"] = tuple(vanilla_model(v)["GeometryAssets"][0]["Transformations"]["Translation"][k] for k in "XYZ")
        spec[v["size"]] = L = layout(v)
    if "--icons" in sys.argv: render_icons(spec)
    for v in VARIANTS:
        L = spec[v["size"]]; d = os.path.join(root, v["size"]); rel = os.path.join(ROOT_REL, v["size"], "Foot")
        m = foot_model(v, L, os.path.join(d, "Foot"), rel)
        module, wsp, wcp = foot_wheel(v, L, os.path.join(d, "Foot"), m)
        icon = None
        if os.path.exists(icon_path(v)):
            irel = os.path.join(ROOT_REL, v["size"], "Icon", f"PadGear_{v['size']}_Icon.png")
            ggd.write(icon_path(v) + ".meta", ggd.asset_meta(ggd.file_id(irel), ("P_PNG", 2), f"PadGear_{v['size']}_Icon.png"))
            icon = "{G}" + ggd.file_id(irel)
        else:
            icon = ggd.SIZES[v["size"]].get("icon") or "{G}" + ggd.file_id(os.path.join("Aero", "RetractableGear", v["size"], "Icon", f"RetractableGear_{v['size']}_Icon.png"))
        bsp, bcp = block(v, d, module, icon)
        spec[v["size"]].update(name=v["name"], model=v["model"], scale=v["scale"], mass=v["mass"], rated=v["rated"],
                               block_server=bsp, block_client=bcp, block=G(v["sid"], 0x1), module=module, wheel_server=wsp, wheel_client=wcp,
                               default_stiffness=DEFAULT_STIFFNESS)
    ggd.definition(os.path.join(root, "PadGear_BlockKind.def"), {"$Bundles": ggd.B_GAME,
        "$Type": "Game2:Keen.Game2.Simulation.StreamedUI.Categories.BlockKindDefinitionObjectBuilder",
        "$Value": {"Guid": KIND, "Name": "Retractable Gear - Pad Foot",
                   "Description": "Retractable landing gear with a landing-gear pad for a foot: it rides a suspension, slides instead of rolling and folds into its bay.",
                   "OrderingPriority": 0, "BlockGroups": [{"Key": G("9e20", 0x22), "Value": GROUP}]}}, KIND, False)
    ggd.definition(os.path.join(root, "PadGear_BlockGroup.def"), {"$Bundles": ggd.B_GAME,
        "$Type": "Game2:Keen.Game2.Simulation.StreamedUI.Categories.BlockGroupDefinitionObjectBuilder",
        "$Value": {"Guid": GROUP, "Name": "Retractable Gear - Pad Foot", "Order": 3,
                   "Subcategories": [{"Key": G("9e20", 0x23), "Value": SUBCATEGORY}], "BlockKinds": []}}, GROUP, False)
    for rt, dirs, _ in os.walk(root):
        for dn in dirs:
            rel = os.path.relpath(os.path.join(rt, dn), os.path.join(ROOT, "Assets"))
            ggd.write(os.path.join(rt, dn.lower() + ".meta"), ggd.folder_meta(rel))
    ggd.write(os.path.join(os.path.dirname(root), "padgear.meta"), ggd.folder_meta(ROOT_REL))
    with open(os.path.join(HERE, "padgear_spec.json"), "w", newline="\n") as f:
        json.dump(spec, f, indent=1, default=list)
    print(f"generated {sum(len(f) for _, _, f in os.walk(root))} files under {root}")
    for k, L in spec.items():
        print(f"  {k}: {L['name']} {L['model']} x{L['scale']} foot {L['size'][0]:.2f} x {L['H']:.2f} x {L['size'][2]:.2f} m, pivot {[round(c, 3) for c in L['pivot']]}, puck r {L['puck_r']:.3f}")


if __name__ == "__main__": main()
