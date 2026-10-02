# gen_entry.py - the reentry plasma effect's definitions (Assets/Particles/AeroEntry_*.def + .meta), from the game's
# own 2.4 definitions as templates, with the values of the Flip and Burn mod's SE1 entry effect (EntryParticle.sbc):
# a dense spray of tiny streaking sparks off the windward face that collide with the hull and slide over it, so the
# ship's own shape makes the sheath; plus an orange light.
#
# The effect's TIME is its strength: the code fixes the effect time at heat (0..1 s, FixEffectTime), and the spawn
# rate and the light follow that timeline. Run, then tools: build_mod_content.py <this mod> to compile.
import json, os, copy, uuid

GAME = r"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers2\GameData\Vanilla\Content\System\Particles"
MOD = os.path.normpath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(MOD, "Assets", "Particles")

EFFECT_GUID = "a3e0c7d1-5b2f-4e8a-9c61-0d7e2f4a1b01"   # (AeroEntryFx.EffectGuid in the scripts)
EMITTER_GUID = "a3e0c7d1-5b2f-4e8a-9c61-0d7e2f4a1b02"
LIGHT_GUID = "a3e0c7d1-5b2f-4e8a-9c61-0d7e2f4a1b03"

def load(rel):
    with open(os.path.join(GAME, rel), encoding="utf-8-sig") as f: return json.load(f)

def kid(): return str(uuid.uuid4())

def const(v): return {"KeyFrames": {"_data": [{"Key": kid(), "Value": {"Value": v, "Key": 0}}]}}
def curve(points): return {"KeyFrames": {"_data": [{"Key": kid(), "Value": {"Value": v, "Key": t}} for t, v in points]}}
def over_life(points):
    # (the game rejects the whole definition set - the world fails to load - past 4 keys over a particle's life)
    assert 1 <= len(points) <= 4, f"over-life curve with {len(points)} keys (the game allows 1-4)"
    return const(curve(points))   # (2D: one curve over a particle's life, at every effect time)
def v3(x, y, z): return {"X": x, "Y": y, "Z": z}
def v4(x, y, z, w): return {"X": x, "Y": y, "Z": z, "W": w}

def emitter():
    d = load(r"Blocks\DrillBlock\DrillingOnBlock\ParticleEmitter_DrillSparks_B.def")
    v = d["$Value"]
    v["Guid"] = EMITTER_GUID
    t = v["TimeLines"]
    t["ParticlesPerSecond"] = curve([(0, 0), (0.05, 300), (1, 4000), (2, 4000)])   # (strength = effect time, 0..1)
    t["ParticleLifeSpan"] = const(0.8)
    t["EmitterSize"] = const(v3(0.5, 0.5, 0.5))
    t["EmitterShellThickness"] = const(0)
    t["ConeAngle"] = const(120)
    t["ConeInnerAngle"] = const(0.5)
    t["LinearVelocity"] = const(2)
    t["LinearVelocityVariance"] = const(0.1)
    t["AccelerationFactor"] = over_life([(0, 0), (1, 0)])   # (the push downwind: GravityForce, set by the code)
    t["Color"] = over_life([(0, v4(0.92, 0.38, 0.10, 1)), (0.48, v4(0.52, 0.05, 0.053, 0.57)), (1, v4(0.3, 0.02, 0.02, 0))])
    t["Emissivity"] = over_life([(0, 600), (0.05, 1000), (0.3, 800), (1, 0)])
    # (the code scales the whole effect by the frontal radius, m: per metre of radius, these give 0.1-0.3 m
    # streaks off a radius-sized emitter - Flip and Burn's SE1 sizes, scaled by radius / 5, were a dot in SE2)
    t["ParticleSize"] = over_life([(0, 0), (0.008, 0.018), (0.054, 0.009), (1, 0.004)])
    t["ParticleThickness"] = over_life([(0, 1), (1, 1)])
    t["Offset"] = const(v3(0, 0, -0.05))
    v["EmissionParameters"].update({"ParticleBurst": 0, "ParticleLifeSpanVariance": 0.5, "Direction": v3(0, 0, 1)})
    v["EmitterFlags"].update({"EnableStreaks": True, "EnableCollisions": True, "EnableRandomRotation": True,
                              "EnableLocalSpaceSimulation": False})
    v["RenderingParameters"].update({"StreakMultiplier": 1.0, "SoftParticleDistanceScale": 1, "DistanceScalingFactor": 0.08,
                                     "ParticleSizeVariance": 0.1, "AmbientMultiplier": 10})
    v["SimulationParameters"].update({"Bounciness": 0.4, "CollisionCountToKill": 0, "EmitterMotionInheritance": 1,
                                      "AngularVelocityCollisionMultiplier": 1})
    return d

def light():
    d = load(r"Generic\Fire\FireLarge_ParticleLightDefinition.def")
    v = d["$Value"]
    v["Guid"] = LIGHT_GUID
    t = v["TimeLines"]
    t["Color"] = const(v4(1, 0.3, 0.11, 1))
    t["Intensity"] = curve([(0, 0), (0.05, 20), (1, 300), (2, 300)])
    t["IntensityVariance"] = const(0)
    t["FalloffMultiplier"] = const(1)
    t["FalloffMultiplierVariance"] = const(0)
    v["LifeTime"] = 2
    v["FlickeringParams"] = {"EnableFlickering": True, "Frequency1": 7, "Frequency2": 13, "Amplitude1": 0.15, "Amplitude2": 0.08, "PhaseShift": 0.3}
    return d

def effect():
    d = load(r"Planets\Weather\Meteor\V2\MeteorFireV2_ParticleEffect.def")
    v = d["$Value"]
    v["Guid"] = EFFECT_GUID
    v["EffectDuration"] = 2   # (the code holds the time in 0..1: never at the emitters' end)
    v["EffectDurationVariation"] = 0
    v["EffectMaxDistance"] = 5000
    v["IsLooping"] = True
    v["ParticleEmitters"] = [{"Key": kid(), "Value": {"Definition": EMITTER_GUID, "Parameters": {
        "GlobalScaleFactor": 1, "EmitterLifeTime": 2, "EmitterStartTime": 0, "GravityMultiplier": 1, "WindMultiplier": 0}}}]
    v["ParticleAudio"] = None
    v["ParticleLight"] = [{"Key": kid(), "Value": LIGHT_GUID}]
    return d

def meta(guid):
    return {"$Bundles": {"System.Runtime": "1.0.0.0", "VRage": "2.4.0.16"},
            "$Type": "VRage:Keen.VRage.ContentPipeline.Metafiles.MetaData",
            "$Value": {"AssetID": guid, "InternalItems": []}}

def write(name, obj, guid):
    os.makedirs(OUT, exist_ok=True)
    p = os.path.join(OUT, name)
    with open(p, "w", encoding="utf-8", newline="\n") as f: json.dump(obj, f, indent=2)
    if not os.path.exists(p + ".meta"):   # (the builder keeps its own state in the meta: written once)
        with open(p + ".meta", "w", encoding="utf-8", newline="\n") as f: json.dump(meta(guid), f, indent=2)
    print("wrote", p)

write("AeroEntry_ParticleEmitter.def", emitter(), EMITTER_GUID)
write("AeroEntry_ParticleLightDefinition.def", light(), LIGHT_GUID)
write("AeroEntry_ParticleEffect.def", effect(), EFFECT_GUID)
if not os.path.exists(OUT + ".meta"):
    with open(OUT + ".meta", "w", encoding="utf-8", newline="\n") as f:
        json.dump({"$Bundles": {"System.Runtime": "1.0.0.0", "VRage": "2.4.0.16"},
                   "$Type": "VRage:Keen.VRage.ContentPipeline.Metafiles.MetaData",
                   "$Value": {"AssetID": str(uuid.uuid5(uuid.NAMESPACE_URL, "aero/Assets/Particles")), "InternalItems": []}}, f, indent=2)
