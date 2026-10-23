"""The PAD-FOOT retractable gears (2026-10-20, the user: "No lock, custom wheel model that is the landing gear, and constrained";
then "landing gear models available"): Assets/Aero/PadGear/<size>/.

A vanilla landing-gear model (SE2 4.0, GameData/Vanilla: LandingGear100 / LandingGear500; 2026-10-23: no magnetic plates - the user:
"only landing gears") on each Wheeled Landing Gear housing, scaled to it (gen_gear_defs.py's
housings, leg and slide - their model, breakable and subpart definitions referenced by GUID, nothing copied). The gear.s
FOOT is a visual subpart of the block (the landing-gear mesh - the SDK's FBX, copied in - a straight reuse of a vanilla mesh as a
PLACEHOLDER: no kitbash, nothing reshaped; scaled whole where the housing needs it), hinged at the ankle pin and turned by the client
to the ground the suspension's wheel touches (Gear/FootIk.cs, AeroGearAnimComponent). The suspension's WHEEL (2026-10-21) is the
vanilla wheel of the housing's size, never drawn (AeroHiddenWheelComponent), its pivot at its centre and its rest moved so its bottom
is the foot's sole; heavier (WheelMass), its corner module with no drive and no steering.

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
MATERIALS = os.path.join(os.path.dirname(os.path.dirname(SDK)), "Content", "BlockMaterials")   # (the vanilla textures: the icons')
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
# wheels: 1 m 200, 1.5 m 300, 2.5 m 500); rated: the weight one gear is sized for (kg) - its sag at the default stiffness within
# half its stroke (GearTests). name: the block (the game appends the size).
VARIANTS = [
    # (2026-10-23, the user: "get rid of the mag plate vers, only landing gears": the 0.5 m is the 1 m pad at half size - was
    #  Magneticplate50; same block, so ships carrying it keep it)
    dict(size="050", sid="9e21", wid="9e31", wheel="100", name="Retractable Gear (Pad)", model="LandingGear100",
         dir=r"LandingGear\LandingGear\100\Models\NonFractured", lods=["LandingGear100", "LandingGear100_LOD1", "LandingGear100_LOD2"],
         lo=(-0.251, -0.501, -0.500), hi=(0.251, 0.501, 0.502), scale=0.5, mass=150, rated=1500, ankle=1),
    dict(size="100", sid="9e22", wid="9e32", wheel="100", name="Retractable Gear (Pad)", model="LandingGear100",
         dir=r"LandingGear\LandingGear\100\Models\NonFractured", lods=["LandingGear100", "LandingGear100_LOD1", "LandingGear100_LOD2"],
         lo=(-0.251, -0.501, -0.500), hi=(0.251, 0.501, 0.502), scale=1.0, mass=250, rated=5000, ankle=1),
    # (2026-10-23, the user: "500 is more like a 2.5 one ... move the 500 to the 2.5 m": the 2.5 m is LandingGear500 at half size, a
    #  2.5 m pad; the 1.5 m takes the 1 m pad at 1.5x - MagneticPlate250 is no longer used)
    dict(size="150", sid="9e23", wid="9e33", wheel="150", name="Retractable Gear (Pad)", model="LandingGear100",
         dir=r"LandingGear\LandingGear\100\Models\NonFractured", lods=["LandingGear100", "LandingGear100_LOD1", "LandingGear100_LOD2"],
         lo=(-0.251, -0.501, -0.500), hi=(0.251, 0.501, 0.502), scale=1.5, mass=600, rated=20000, ankle=1),
    dict(size="250", sid="9e24", wid="9e34", wheel="250", name="Retractable Gear (Pad)", model="LandingGear500",
         dir=r"LandingGear\LandingGear\500\Models\NonFractured", lods=["LandingGear500", "LandingGear500_LOD1", "LandingGear500_LOD2"],
         lo=(-1.263, -2.500, -2.505), hi=(1.263, 2.504, 2.501), scale=0.5, mass=1200, rated=60000, ankle=-1),
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


FOOT_RAISE = {"050": 0.2, "100": 0.4, "150": 0.63, "250": 1.0}   # (GearLayout.FootRaise: the ankle this far above the rod's hub, block +Y -
                   #  2026-10-23, the user: "how come this still isn't tight?" - on the hub the pad hung low in the fork with a tall gap
                   #  under its crossbar; raised, the ankle's top is ~0.4 x its half height under the crossbar (room to tilt 45 deg).
                   #  The rods' lowest point over the ankle, measured: 0.29 / 0.58 / 0.90 / 1.45 m above the hub)
FOOT_SIDE = 0.1    # (GearLayout.FootSide: the foot's dummy this far along the hinge axis (+X) from the ankle, its mesh shifted back by
                   #  as much - the same hinge line, but the client tells the foot from the rod (both would spawn at the hub) by where
                   #  each spawned: AeroGearAnimComponent.FindParts, 5 cm)
WHEEL_R = {"100": 0.5, "150": 0.75, "250": 1.25}   # (the vanilla wheels' radii: the hidden wheel's)
DT_FOOT = G("9e20", 0x30)                            # (the foot subpart's dummy type)


def layout(v):
    """2026-10-21 (the user: "go back to wheel suspension ... a separate, physics-off landing gear entity where the invisible wheel is.
    the landing gear and hinge IK to the wheel contact surface normal"): the FOOT is a visual subpart of the block (no collider) hinged
    at its ANKLE - up the fork above the rod's hub, FOOT_RAISE (block model space, as the dummies) - its mesh's ankle centre there (the pads: the top-corner
    cube; the plates: the top centre), hanging along the block's -Y; the suspension's WHEEL is the vanilla wheel of that size, never drawn (AeroHiddenWheelComponent),
    its pivot moved to its centre and its rest moved so its bottom is the foot's sole (gear down, unloaded): the foot stands where
    the wheel touches. The client turns the foot about the ankle to the wheel's ground normal (FootIk)."""
    s = v["scale"]; lo, hi = v["lo"], v["hi"]; t0 = v["t0"]
    # the hinge (2026-10-23, the user: "if the landing gear is 1x1x0.5, the ankle is like 0.25x0.25x0.25 in the top corner ... i want
    # the hinge centered on that ankle part"): the pads' ankle is the cube of a quarter of the mesh's height and length in its top
    # corner - the end the mesh's upper part stands on (v["ankle"]: +1 / -1 = +Z / -Z, model space; LandingGear100 +Z, LandingGear500
    # -Z, from their vertices); the plates (no ankle, 0) hinge at their top centre. Model space: LOD0 raw + its translation.
    a = v["ankle"]; mid = tuple((lo[i] + hi[i]) / 2 for i in range(3)); span = tuple(hi[i] - lo[i] for i in range(3))
    top = (mid[0] + t0[0], hi[1] - (span[1] / 8 if a else 0.0) + t0[1], (mid[2] if not a else (hi[2] - span[2] / 8 if a > 0 else lo[2] + span[2] / 8)) + t0[2])
    w = tuple(s * span[i] for i in range(3)); H = s * (top[1] - (lo[1] + t0[1]))   # (H: the ankle down to the sole)
    # ONE orientation for every pad (2026-10-23, the user: "whatever the convention to make this the standard orientation" - the 1 m's):
    # the ankle at the block's +Z, the pad reaching toward -Z (forward, away from the leg). Keen's meshes are mirrored front to back
    # (LandingGear500's ankle is at -Z), so a -Z ankle is turned half a turn about Y - by the foot's dummy (the subpart's rest pose).
    foot_q = (0.0, 1.0, 0.0, 0.0) if a < 0 else (0.0, 0.0, 0.0, 1.0)
    pad_dz = s * ((mid[2] + t0[2]) - top[2]) * (-1 if a < 0 else 1)  # (the pad's centre from the ankle, block Z: the wheel stands under it)
    side = -FOOT_SIDE if a >= 0 else FOOT_SIDE                       # (the mesh's shift back to the ankle, in its own X: turned, X flips)
    T = (-s * top[0] + side, -s * top[1], -s * top[2])
    hd = housing(v["size"])
    rest_p0, rest_q, travel = hd["Wheel Rest Position"]
    hub = hd["GearRodHub"][0]
    ankle = (hub[0], hub[1] + FOOT_RAISE[v["size"]], hub[2])           # (up the fork, the rod's +Y: block +Y, gear down)
    foot = (ankle[0] + FOOT_SIDE, ankle[1], ankle[2])                 # (the foot's dummy: on the hinge line, FOOT_SIDE along it)
    sole = (ankle[0], ankle[1] - H, ankle[2])
    r = WHEEL_R[v["wheel"]]
    centre = (sole[0], sole[1] + r, sole[2] + pad_dz)              # (the wheel's centre, idle: its bottom on the sole, under the pad's centre)
    up = qrot(rest_q, (0.0, 1.0, 0.0))
    rest_p = tuple(centre[i] + travel / 2 * up[i] for i in range(3))   # (the suspension's idle point is rest - travel/2 x up)
    return dict(T=T, t0=t0, H=H, raise_=FOOT_RAISE[v["size"]], size=w, pad_dz=pad_dz, foot_q=foot_q, ankle=ankle, foot=foot, sole=sole, wheel_r=r, wheel_centre=centre, rest_p=rest_p, rest_q=rest_q, travel=travel)


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
    """The foot's FBX (LOD0..2) copied from the SDK and its .model (the vanilla mesh, scaled, top centre at 0) - visual only, no collider."""
    src = os.path.join(SDK, v["dir"]); md = os.path.join(d, "Models"); os.makedirs(md, exist_ok=True)
    ids = []
    for stem in v["lods"]:
        out = os.path.join(md, stem + ".fbx"); shutil.copy(os.path.join(src, stem + ".fbx"), out)
        fid = ggd.file_id(os.path.join(rel, "Models", stem + ".fbx")); ids.append(fid); ggd.write(out + ".meta", ggd.file_meta(fid))
    N = f"PadGear_{v['size']}_Foot"
    vm = vanilla_model(v)
    last = {m["MaterialName"] for m in vm["MaterialAssignments"] if m["MaterialName"].endswith("_LastLOD")}
    mats = [m for m in vm["MaterialAssignments"] if m["MaterialName"] not in last]
    s = v["scale"]; T = L["T"]; t0 = L["t0"]
    geo = []
    for i, fid in enumerate(ids):
        # (2026-10-23, the user: zoomed out the foot "is not proper, zoomed in it fades to proper location": the LODs. The content
        #  builder places EVERY LOD by LOD0's transform - the vanilla models and all of ours give LOD1+ a zero translation and scale
        #  1; a LOD1+ given the whole placement again was moved twice. The LOD meshes share LOD0's raw frame (their bounds match).)
        tr = tuple(T[k] + s * t0[k] for k in range(3)) if i == 0 else (0.0, 0.0, 0.0)
        used = [u for u in vm["GeometryAssets"][i]["UsedMaterials"] if u not in last]
        geo.append({"AssetReference": "{G}" + fid, "UsedMaterials": used,
                    "Transformations": {"Translation": {"X": round(tr[0], 5), "Y": round(tr[1], 5), "Z": round(tr[2], 5)}, "Rotation": {"X": 0, "Y": 0, "Z": 0}, "Scale": s if i == 0 else 1}})
    m = G(v["wid"], 0x105)
    path = os.path.join(md, N + ".model")
    ggd.write(path, {"$Bundles": {"System.Runtime": "1.0.0.0", "VRage": "2.0.1.118"}, "$Type": "VRage:Keen.VRage.ContentPipeline.Models.ModelAssetData",
                     "$Value": {"MaterialAssignments": mats, "MaterialStateOverrides": [], "GeometryAssets": geo, "SnapToGrid": False, "SuggestedModelSize": None,
                                "CollisionShapesAsset": {"AssetReference": None, "GroupsByFracture": [], "GeneratePreciseCollider": False}}})
    ggd.write(path + ".meta", ggd.asset_meta(m, ("P_MODEL", 40), N + ".vrm"))
    return m


def foot_subpart(v, d, m):
    """The foot as a client subpart (as the leg and the rod: gen_gear_defs.own_model): prefab, composite, model component."""
    N = f"PadGear_{v['size']}_Foot"; sid = v["sid"]
    pref, comp, mcd = G(sid, 0x30), G(sid, 0x31), G(sid, 0x32)
    sp = os.path.join(d, "Subparts")
    ggd.definition(os.path.join(sp, f"{N}_Client.def"), {"$Bundles": ggd.B_VRAGE,
        "$Type": "VRage:Keen.VRage.Core.Game.Definitions.PrefabDefinitionObjectBuilder",
        "$Value": {"Guid": pref, "_entity": {"Definition": comp, "ObjectBuilders": ggd.SUBPART_OB(), "DebugName": "Root"}}}, pref, False)
    ggd.definition(os.path.join(sp, f"{N}_Client_Composite.def"), {"$Bundles": ggd.B_GAME,
        "$Type": "VRage:Keen.VRage.DCS.Definitions.EntityCompositeDefinitionObjectBuilder",
        "$Value": {"Guid": comp, "TagSlots": [{"$Key": t, "$Value": k_} for t, k_, _ in ggd.SUBPART_COMPONENTS],
                   "Components": [{"Key": k_, "Value": {"Definition": mcd if t == "ModelComponent" else None, "Type": ty}} for t, k_, ty in ggd.SUBPART_COMPONENTS]}},
        comp, False)
    ggd.definition(os.path.join(sp, f"{N}_ModelComponentDefinition.def"), {"$Bundles": ggd.B_VRAGE,
        "$Type": "VRage:Keen.VRage.Game.Components.Models.ModelComponentDefinitionObjectBuilder",
        "$Value": {"Guid": mcd, "Model": "{G}" + m, "Dummies": []}}, mcd, False)
    return pref


def block_model(v, L, d, foot_pref):
    """The pad gear's own block model and subparts: the housing's (gen_gear_defs.own_model) with its wheel rest moved under the
    foot's sole (the hidden wheel) and a Foot dummy at the ankle pin; the subparts: the housing's leg and rod, and the foot."""
    hsid = ggd.SIZES[v["size"]]["sid"]; sid = v["sid"]; N = f"PadGear_{v['size']}"
    bmd, csp = G(sid, 0x33), G(sid, 0x34)
    hkeys = [G(hsid, 0x41 + i) for i in range(3)]
    foot = ggd.dummy("GearFoot", [round(c, 5) for c in L["foot"]], DT_FOOT, L["foot_q"])
    ggd.definition(os.path.join(d, f"{N}_BlockModelDefinition.partialdef"), ggd.partial(G(hsid, 0x15), {"Guid": bmd,
        "Dummies": {"$DeltaEncoded": True, "Keys": hkeys + [G(sid, 0x35)], "Changed": [
            {"Kind": "Update", "Index": 0, "Value": {"Transform": {"Transform": {"Position": dict(zip("XYZ", (round(c, 5) for c in L["rest_p"])))}}}},
            {"Kind": "Insert", "Index": 3, "Value": foot}], "Removed": []}},
        "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.Models.BlockModelComponentDefinition"), bmd, True)
    ggd.definition(os.path.join(d, f"{N}_ConditionalSubPartsDefinition.def"), {"$Bundles": ggd.B_GAME,
        "$Type": "Game2:Keen.Game2.Client.WorldObjects.CubeBlocks.Effects.ConditionalSubPartsDefinitionObjectBuilder",
        "$Value": {"Guid": csp, "SubParts": {ggd.DT_LEG: G(hsid, 0x18), ggd.DT_ROD: G(hsid, 0x1b), DT_FOOT: foot_pref}, "MeshPartsToHideWhenActive": []}}, csp, False)
    return bmd, csp


def wheel_model(v, L, d, rel):
    """The hidden wheel's OWN model (2026-10-23, the user: "the wheel should be an invisible model, not a real one toggled invisible"):
    nothing to see (one 1 mm triangle at the wheel's centre, inside the foot - tiny_fbx.py's HiddenWheel_Tiny.fbx: the content builder
    wants at least one FBX with geometry; so nothing shows anywhere: the placement preview, projections, the block) and a thin convex
    puck of the wheel's radius as its collision shape, centred on the model's origin (the wheel's pivot). The game builds the wheel's
    sphere from it (WheelColliderComponent.AllocateColliderFromModelCollider: a convex hull wider than long is fitted as a cylinder ->
    a sphere of its radius at its centre), the same sphere the vanilla wheel's model gives."""
    md = os.path.join(d, "Models"); os.makedirs(md, exist_ok=True)
    N = f"PadGear_{v['size']}_HiddenWheel"; r = L["wheel_r"]
    col = os.path.join(md, N + "_collision.gltf")
    write_puck(col, N + "_Collision", r, 0.05 * r, 0.0)
    fbx = os.path.join(md, N + ".fbx"); shutil.copy(os.path.join(HERE, "HiddenWheel_Tiny.fbx"), fbx)
    for f in (col, os.path.join(md, N + "_collision.bin"), fbx):
        ggd.write(f + ".meta", ggd.file_meta(ggd.file_id(os.path.join(rel, "Models", os.path.basename(f)))))
    geo = [{"AssetReference": "{G}" + ggd.file_id(os.path.join(rel, "Models", os.path.basename(fbx))), "UsedMaterials": ["PaintedMetalColorable"],
            "Transformations": {"Translation": {"X": 0, "Y": 0, "Z": 0}, "Rotation": {"X": 0, "Y": 0, "Z": 0}, "Scale": 1.0}}]
    m = G(v["wid"], 0x106)
    path = os.path.join(md, N + ".model")
    ggd.write(path, {"$Bundles": {"System.Runtime": "1.0.0.0", "VRage": "2.0.1.118"}, "$Type": "VRage:Keen.VRage.ContentPipeline.Models.ModelAssetData",
                     "$Value": {"MaterialAssignments": [{"MaterialName": "PaintedMetalColorable", "MaterialReference": "be89c5c3-25ce-42e9-93f9-72ba32c3d8a7"}],
                                "MaterialStateOverrides": [], "GeometryAssets": geo, "SnapToGrid": False, "SuggestedModelSize": None,
                                "CollisionShapesAsset": {"AssetReference": "{G}" + ggd.file_id(os.path.join(rel, "Models", os.path.basename(col))),
                                                         "GroupsByFracture": [], "GeneratePreciseCollider": True}}})
    ggd.write(path + ".meta", ggd.asset_meta(m, ("P_MODEL", 40), N + ".vrm"))
    return m


def foot_wheel(v, L, d, m):
    """The suspension's HIDDEN wheel: the vanilla wheel's definitions (its model kept, never drawn - AeroHiddenWheelComponent on its
    client prefab), its pivot dummy moved to its centre (the suspension's point is the wheel's centre: under the foot), its body
    heavier (WheelMass), and a corner module with no drive and no steering."""
    W = WHEELS[v["wheel"]]; wid = v["wid"]; N = f"PadGear_{v['size']}_HiddenWheel"
    mcd, col, scomp, spref, ccomp, cpref, bind, module = (G(wid, 0x61 + i) for i in range(8))
    ggd.definition(os.path.join(d, f"{N}_ModelComponentDefinition.partialdef"), ggd.partial(W["mcd"], {"Guid": mcd, "Model": "{G}" + m,
        "Dummies": {"$DeltaEncoded": True, "Keys": W["dkeys"], "Changed": [
            {"Kind": "Update", "Index": 0, "Value": {"Transform": {"Transform": {"Position": {"X": 0, "Y": 0, "Z": 0}}}}}], "Removed": []}},
        "VRage:Keen.VRage.Game.Components.Models.ModelComponentDefinition", ggd.B_VRAGE), mcd, True)
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


def block(v, d, module, icon, bmd, csp):
    """The block: the housing size's composition (gen_gear_defs.gen_size, own model) on the foot's corner module."""
    B = ggd.BASE; size = v["size"]; sid = v["sid"]; hs = ggd.SIZES[size]; hsid = hs["sid"]; N = f"PadGear_{size}"
    model, brk = bmd, G(hsid, 0x16)
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
                   # (the foot as in game: its LOD0 placement (scale, then T + s t0) in the subpart's frame, the subpart at the foot's
                   #  dummy turned by foot_q)
                   "foot": [os.path.join(SDK, v["dir"], v["lods"][0] + ".fbx"), v["scale"],
                            [L["T"][k] + v["scale"] * L["t0"][k] for k in range(3)], list(L["foot"]), list(L["foot_q"])],
                   "materials": MATERIALS},
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
        fpref = foot_subpart(v, os.path.join(d, "Foot"), m)
        module, wsp, wcp = foot_wheel(v, L, os.path.join(d, "Foot"), wheel_model(v, L, os.path.join(d, "Foot"), rel))
        bmd, csp = block_model(v, L, d, fpref)
        icon = None
        if os.path.exists(icon_path(v)):
            irel = os.path.join(ROOT_REL, v["size"], "Icon", f"PadGear_{v['size']}_Icon.png")
            ggd.write(icon_path(v) + ".meta", ggd.asset_meta(ggd.file_id(irel), ("P_PNG", 2), f"PadGear_{v['size']}_Icon.png"))
            icon = "{G}" + ggd.file_id(irel)
        else:
            icon = ggd.SIZES[v["size"]].get("icon") or "{G}" + ggd.file_id(os.path.join("Aero", "RetractableGear", v["size"], "Icon", f"RetractableGear_{v['size']}_Icon.png"))
        bsp, bcp = block(v, d, module, icon, bmd, csp)
        spec[v["size"]].update(name=v["name"], model=v["model"], scale=v["scale"], mass=v["mass"], rated=v["rated"],
                               block_server=bsp, block_client=bcp, block=G(v["sid"], 0x1), module=module, wheel_server=wsp, wheel_client=wcp,
                               default_stiffness=DEFAULT_STIFFNESS, foot_subpart=fpref, block_model=bmd, subparts=csp)
    ggd.definition(os.path.join(root, "PadGear_BlockKind.def"), {"$Bundles": ggd.B_GAME,
        "$Type": "Game2:Keen.Game2.Simulation.StreamedUI.Categories.BlockKindDefinitionObjectBuilder",
        "$Value": {"Guid": KIND, "Name": "Retractable Gear - Pad Foot",
                   "Description": "Retractable landing gear with a landing-gear pad for a foot: it rides a suspension, slides instead of rolling and folds into its bay.",
                   "OrderingPriority": 0, "BlockGroups": [{"Key": G("9e20", 0x22), "Value": GROUP}]}}, KIND, False)
    ggd.definition(os.path.join(root, "PadGear_BlockGroup.def"), {"$Bundles": ggd.B_GAME,
        "$Type": "Game2:Keen.Game2.Simulation.StreamedUI.Categories.BlockGroupDefinitionObjectBuilder",
        "$Value": {"Guid": GROUP, "Name": "Retractable Gear - Pad Foot", "Order": 3,
                   "Subcategories": [{"Key": G("9e20", 0x23), "Value": SUBCATEGORY}], "BlockKinds": []}}, GROUP, False)
    ggd.definition(os.path.join(root, "PadGear_GearFoot_DummyTypeDefinition.def"), {"$Bundles": ggd.B_VRAGE,
        "$Type": "VRage:Keen.VRage.Core.Model.DummyTypeDefinitionObjectBuilder", "$Value": {"Guid": DT_FOOT}}, DT_FOOT, False)
    for rt, dirs, _ in os.walk(root):
        for dn in dirs:
            rel = os.path.relpath(os.path.join(rt, dn), os.path.join(ROOT, "Assets"))
            ggd.write(os.path.join(rt, dn.lower() + ".meta"), ggd.folder_meta(rel))
    ggd.write(os.path.join(os.path.dirname(root), "padgear.meta"), ggd.folder_meta(ROOT_REL))
    with open(os.path.join(HERE, "padgear_spec.json"), "w", newline="\n") as f:
        json.dump(spec, f, indent=1, default=list)
    print(f"generated {sum(len(f) for _, _, f in os.walk(root))} files under {root}")
    for k, L in spec.items():
        print(f"  {k}: {L['name']} {L['model']} x{L['scale']} foot {L['size'][0]:.2f} x {L['H']:.2f} x {L['size'][2]:.2f} m at {[round(c, 3) for c in L['foot']]}, hidden wheel r {L['wheel_r']} centre {[round(c, 3) for c in L['wheel_centre']]}")


if __name__ == "__main__": main()
