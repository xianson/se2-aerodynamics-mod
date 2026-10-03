# gen_vapor.py - transonic vapour (the condensation cone and the edge sheets), placed by the Orbital Mod's PlasmaSpike
# (Mode 6) from the hull and driven by the aero mod's Mach and density (AeroEntryFx.TryGetFlow).
# Base: the meteor's smoke (V3) - soft, lit by the scene (scattering), not additive: white mist, no glow.
#   VaporCone - ONE per grid at its widest station: a thin ellipsoid shell (a ring round the body; the GPU sends each
#               particle out along its spawn offset - ParticleEmission.hlsl - so they leave the ring outward and, the
#               shell being a little deep along the flow, back: a cone). Authored per metre of the grid's radius (the code
#               scales the effect by it: sizes, ring, speeds).
#   VaporEdge - per outline point, half a chord downwind: a short soft spray down +Z (the sheets over the wings).
# Both: local space; no collisions (the GPU collision test assumes world-space positions - see vapour()); no gravity (a multiplier > 0 attaches a planet-gravity probe applied in the local frame);
# the spawn rate follows the effect's held time (0..1 s of the 2 s effect = none..full): the code holds it at the
# Mach band x density x local weight.
# Guids: VaporCone 5d0c7a3e-0101-4000-8000-000000000001, VaporEdge 5d0c7a3e-0102-4000-8000-000000000001.
# usage: python gen_vapor.py
import json, os, uuid

GAME = r"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers2\GameData\Vanilla\Content\System\Particles"
MOD = os.path.normpath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(MOD, "Assets", "Particles")
NS = uuid.UUID("6a1e2b3c-4d5e-4f60-8172-93a4b5c6d7e8")

def load(rel):
    with open(os.path.join(GAME, rel), encoding="utf-8-sig") as f: return json.load(f)

def meta(guid):
    return {"$Bundles": {"System.Runtime": "1.0.0.0", "VRage": "2.4.0.16"},
            "$Type": "VRage:Keen.VRage.ContentPipeline.Metafiles.MetaData",
            "$Value": {"AssetID": guid, "InternalItems": []}}

def write(name, obj, guid):
    p = os.path.join(OUT, name)
    with open(p, "w", encoding="utf-8", newline="\n") as f: json.dump(obj, f, indent=2)
    if not os.path.exists(p + ".meta"):
        with open(p + ".meta", "w", encoding="utf-8", newline="\n") as f: json.dump(meta(guid), f, indent=2)

_k = 0
def kid():
    global _k; _k += 1
    return str(uuid.uuid5(NS, f"key-{_k}"))
def const(v): return {"KeyFrames": {"_data": [{"Key": kid(), "Value": {"Value": v, "Key": 0}}]}}
def curve(points): return {"KeyFrames": {"_data": [{"Key": kid(), "Value": {"Value": v, "Key": t}} for t, v in points]}}
def over_life(points):
    assert 1 <= len(points) <= 4, "the game allows 1-4 over-life keys"
    return const(curve(points))
def v3(x, y, z): return {"X": x, "Y": y, "Z": z}
def c4(r, g, b, a): return {"X": r, "Y": g, "Z": b, "W": a}
def by_time(pps): return curve([(0, 0), (0.5, pps), (1, pps)])

# (the first pass, alpha 0.35, read as faint grey haze: more solid white, a touch of glow so its shadowed side stays white)
MIST = [(0, c4(1, 1, 1, 0)), (0.15, c4(1, 1, 1, 0.75)), (0.6, c4(1, 1, 1, 0.55)), (1, c4(0.97, 0.98, 1, 0))]
GLOW = 25

# (collide off by default, audited: the GPU's depth-buffer collision tests a particle's position as WORLD space, but a
#  local-space particle's position is in the effect's frame - it "hit" phantom surfaces and bounced off at random: the
#  detached clumps. The emitters are snapped onto the skin instead)
def vapour(name, *, pps, life, velocity, size, emitter, shell, cone, inner=0, collide=False, life_var=0.3):
    d = load(r"Planets\Weather\Meteor\V3\ParticleEmitter_MeteorSmoke_V3.def")
    v = d["$Value"]
    g = str(uuid.uuid5(NS, "emitter/" + name)); v["Guid"] = g
    t = v["TimeLines"]
    t["ParticlesPerSecond"] = by_time(pps)
    t["ParticleLifeSpan"] = const(life)
    t["EmitterSize"] = const(v3(*emitter))
    t["EmitterShellThickness"] = const(shell)
    t["ConeAngle"] = const(cone)
    t["ConeInnerAngle"] = const(inner)
    t["LinearVelocity"] = const(velocity)
    t["LinearVelocityVariance"] = const(velocity * 0.25)
    t["AccelerationFactor"] = over_life([(0, 0), (1, 0)])
    t["ParticleSize"] = over_life(size)
    t["Color"] = over_life(MIST)
    t["Emissivity"] = over_life([(0, GLOW), (1, GLOW * 0.5)])
    t["ScatteringIntensity"] = over_life([(0, 10), (0.8, 8), (1, 0)])
    t["Offset"] = const(v3(0, 0, 0))
    v["EmissionParameters"].update({"ParticleBurst": 0, "ParticleLifeSpanVariance": life_var, "Direction": v3(0, 0, 1)})
    # (the smoke atlas is a puff GROWING over 64 frames at 12/s: a 0.4 s particle only ever showed the first ~5 - nearly
    #  empty specks. The dense middle of the animation, played over the particle's life)
    v["AnimationParameters"].update({"FirstFrameIndex": 16, "NumFramesInAnimation": 32, "AnimationFramerate": round(32 / life, 1)})
    v["EmitterFlags"].update({"EnableCollisions": collide, "EnableLocalSpaceSimulation": True, "EnableStreaks": False})
    # (the meteor smoke's ParticleSizeVariance 10 made puffs up to 10x their size - a fog bank; its particle shadows
    #  (ShadowParticleAlphaMultiplier 3) darkened what is under them grey)
    v["RenderingParameters"].update({"DistanceScalingFactor": 0, "ParticleSizeVariance": 0.3, "ShadowParticleAlphaMultiplier": 0})
    v["SimulationParameters"].update({"EmitterMotionInheritance": 0, "CollisionCountToKill": 1, "Bounciness": 0})
    write(f"Vapour_{name}_ParticleEmitter.def", d, g)
    return g

def effect(name, emitter_guid, guid):
    d = load(r"Planets\Weather\Meteor\V3\MeteorSmokeV3_ParticleEffect.def")
    v = d["$Value"]; v["Guid"] = guid
    v["EffectDuration"] = 2; v["EffectDurationVariation"] = 0; v["EffectMaxDistance"] = 20000; v["IsLooping"] = True
    v["ParticleEmitters"] = [{"Key": kid(), "Value": {"Definition": emitter_guid, "Parameters": {
        "GlobalScaleFactor": 1, "EmitterLifeTime": 2, "EmitterStartTime": 0, "GravityMultiplier": 0, "WindMultiplier": 0}}}]
    v["ParticleAudio"] = None; v["ParticleLight"] = []
    write(f"Vapour_{name}_ParticleEffect.def", d, guid)
    print(name, guid)

# the cone: per metre of the grid's radius - a ring 1 across, 0.2 deep along the flow, puffs 0.1-0.2 of it, drifting
# out and back ~0.1 of it before they fade (0.6 s)
# (the cone: spawned only on a ring tilted 50-60 degrees off the flow - ConeInnerAngle 100 + spread 20 - on an ellipsoid 1
#  across and 0.6 deep, so each particle leaves along its offset: outward and ~22 degrees back - a cone surface, not a disc.
#  Thin puffs, many, short-lived: a sheet)
# (v2: the first read as a smoke ring - 0.4 s at 0.25 / m barely moved. Now they travel ~1 radius in 1 s, leaving the
#  ring ~45 degrees back (the ellipsoid 1.5 deep): a bell trailing from the widest station; little life spread - its back
#  edge crisper)
effect("Cone", vapour("Cone", pps=4000, life=1.0, velocity=1.0, size=[(0, 0.03), (0.5, 0.06), (1, 0.1)],
                      emitter=(1, 1, 1.5), shell=0.03, cone=20, inner=100, life_var=0.1), "5d0c7a3e-0101-4000-8000-000000000001")
# the edge sheets: per metre of the outline's spray scale - short and soft, close to the skin
# (wispy: the user - thin layered mist, not cotton: ~38 % at most, a long fade, small at birth growing as it drifts)
# (faded more at both ends: the user - a slow fade in to a ~30 % peak, then a long fade out; the game allows 4 keys)
MIST_WISP = [(0, c4(1, 1, 1, 0)), (0.35, c4(1, 1, 1, 0.3)), (0.55, c4(1, 1, 1, 0.22)), (1, c4(0.97, 0.98, 1, 0))]
_mist = MIST; MIST = MIST_WISP
effect("Edge", vapour("Edge", pps=500, life=1.2, velocity=1.5, size=[(0, 0.06), (0.5, 0.22), (1, 0.4)],
                      emitter=(0.2, 0.2, 0.2), shell=0, cone=40), "5d0c7a3e-0102-4000-8000-000000000001")   # (longer, wider: the user)
MIST = _mist

# DIAGNOSTIC variants of the cone (the first one drew nothing; the edge wisps seemed to stay in world space): B without
# collisions, C without collisions or the inner-cone ring (a plain shell). 5d0c7a3e-0103 / -0104
effect("ConeNoCollide", vapour("ConeNoCollide", pps=2500, life=0.4, velocity=0.25, size=[(0, 0.04), (0.5, 0.07), (1, 0.09)],
                               emitter=(1, 1, 0.6), shell=0.03, cone=20, inner=100, collide=False), "5d0c7a3e-0103-4000-8000-000000000001")
effect("ConePlain", vapour("ConePlain", pps=2500, life=0.4, velocity=0.25, size=[(0, 0.04), (0.5, 0.07), (1, 0.09)],
                           emitter=(1, 1, 0.6), shell=0.03, cone=180, inner=0, collide=False), "5d0c7a3e-0104-4000-8000-000000000001")
effect("EdgeNoCollide", vapour("EdgeNoCollide", pps=160, life=0.35, velocity=0.4, size=[(0, 0.08), (0.5, 0.15), (1, 0.2)],
                               emitter=(0.2, 0.2, 0.2), shell=0, cone=12, collide=False), "5d0c7a3e-0105-4000-8000-000000000001")

# CONE SPRAY (the user: emitters in a ring projected onto the skin, a particle that sprays more and is thicker): a wide
# soft spray, bigger and more solid puffs, many - neighbours blend into one sheet. 5d0c7a3e-0106-4000-8000-000000000001
MIST_THICK = [(0, c4(1, 1, 1, 0)), (0.12, c4(1, 1, 1, 0.85)), (0.65, c4(1, 1, 1, 0.65)), (1, c4(0.97, 0.98, 1, 0))]
_mist = MIST
MIST = MIST_THICK
effect("ConeSpray", vapour("ConeSpray", pps=600, life=0.6, velocity=1.2, size=[(0, 0.15), (0.5, 0.35), (1, 0.6)],
                           emitter=(0.3, 0.3, 0.3), shell=0, cone=90, life_var=0.15), "5d0c7a3e-0106-4000-8000-000000000001")   # (wide: the user)
MIST = _mist

# WINGTIP LINE: the tip vortex's condensation trail - small tight puffs, long-lived, drifting slowly downwind in the
# ship's frame: a thin line streaming back from each tip. 5d0c7a3e-0107-4000-8000-000000000001
# (thin, sharp, long, very transparent: the user - tiny tight puffs barely growing, many of them, living 3 s: a fine
#  pencil line; ~12 % at most, faded in and out)
# (longer, thinner, fainter again: the user - 5 s, ~3-5 cm, ~6 %; the length set per airspeed by the code)
MIST_TIP = [(0, c4(1, 1, 1, 0)), (0.1, c4(1, 1, 1, 0.06)), (0.6, c4(1, 1, 1, 0.04)), (1, c4(0.97, 0.98, 1, 0))]
_mist = MIST
MIST = MIST_TIP
effect("Tip", vapour("Tip", pps=2000, life=5.0, velocity=4, size=[(0, 0.01), (0.5, 0.013), (1, 0.018)],
                     emitter=(0.01, 0.01, 0.01), shell=0, cone=0.3, life_var=0.05, collide=False), "5d0c7a3e-0107-4000-8000-000000000001")
MIST = _mist
