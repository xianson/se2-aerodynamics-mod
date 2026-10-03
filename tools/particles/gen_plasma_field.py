# gen_plasma_field.py - the reentry plasma as PARTICLES over the bow shock (the Orbital Mod's PlasmaSpike Mode 5 places
# them): many small effects, one per point of the shock surface (Shock) and of its rim (Stream), each the meteor fire
# (V3: its texture, colours and brightness as the game draws a meteor) re-timed:
#   Shock  - puffs that stay near their point and drift a little downwind (the glowing cap);
#   Stream - a fast spray downwind off the silhouette (the streaming tail).
# Authored per metre: the code scales each effect (EmitterScaleMultiplier: sizes and emitter) and its speeds
# (VelocityMultiplier) by the spacing of the points. Local space (+Z downwind; the code turns each effect to the flow),
# no distance scaling: speed independent, its true size far away. Looping, constant rate (no strength curve yet).
# usage: python gen_plasma_field.py  ->  prints "name effectGuid"
import json, os, copy, uuid

GAME = r"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers2\GameData\Vanilla\Content\System\Particles"
MOD = os.path.normpath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(MOD, "Assets", "Particles")
NS = uuid.UUID("5d0c7a3e-2f1b-4c8e-9a64-7e3b1d2c0f58")

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

def emitter(name, *, pps, life, size, velocity, emitter_size, cone, life_var=0.3, size_var=0.4, base="fire", emissive=None):
    # fire: the meteor fire (its atlas, colours, emissivity kept); sparks: the game's fire sparks (streaks, spark texture)
    d = load(r"Planets\Weather\Meteor\V3\ParticleEmitter_MeteorFire_V3.def" if base == "fire" else r"Generic\Fire\ParticleEmitter_FireSparks.def")
    v = d["$Value"]
    g = str(uuid.uuid5(NS, "emitter/" + name)); v["Guid"] = g
    t = v["TimeLines"]
    t["ParticlesPerSecond"] = const(pps)
    t["ParticleLifeSpan"] = const(life)
    t["EmitterSize"] = const(v3(emitter_size, emitter_size, emitter_size))
    t["EmitterShellThickness"] = const(0)
    t["ConeAngle"] = const(cone)
    t["ConeInnerAngle"] = const(0)
    t["LinearVelocity"] = const(velocity)
    t["LinearVelocityVariance"] = const(velocity * 0.25)
    t["AccelerationFactor"] = over_life([(0, 0), (1, 0)])
    t["ParticleSize"] = over_life(size)
    if emissive: t["Emissivity"] = over_life(emissive)
    t["Offset"] = const(v3(0, 0, 0))
    v["EmissionParameters"].update({"ParticleBurst": 0, "ParticleLifeSpanVariance": life_var, "Direction": v3(0, 0, 1)})
    v["EmitterFlags"].update({"EnableCollisions": False, "EnableLocalSpaceSimulation": True})
    v["RenderingParameters"].update({"DistanceScalingFactor": 0, "ParticleSizeVariance": size_var})
    v["SimulationParameters"].update({"EmitterMotionInheritance": 0})
    write(f"PlasmaField_{name}_ParticleEmitter.def", d, g)
    return g

def effect(name, emitters):
    d = load(r"Planets\Weather\Meteor\V3\MeteorFireV3_ParticleEffect.def")
    v = d["$Value"]
    g = str(uuid.uuid5(NS, "effect/" + name)); v["Guid"] = g
    v["EffectDuration"] = 2; v["EffectDurationVariation"] = 0; v["EffectMaxDistance"] = 20000; v["IsLooping"] = True
    v["ParticleEmitters"] = [{"Key": kid(), "Value": {"Definition": e, "Parameters": {
        "GlobalScaleFactor": 1, "EmitterLifeTime": 2, "EmitterStartTime": 0, "GravityMultiplier": 0, "WindMultiplier": 0}}}
        for e in emitters]
    v["ParticleAudio"] = None
    v["ParticleLight"] = []
    write(f"PlasmaField_{name}_ParticleEffect.def", d, g)
    print(name, g)

# per metre of point spacing: puffs about 1.5-2.5 spacings across (neighbours overlap), living ~0.5 s
effect("Shock", [emitter("Shock", pps=40, life=0.5, size=[(0, 1.4), (0.5, 2.0), (1, 2.6)], velocity=1.0, emitter_size=0.5, cone=30)])
# the tail: ~8 spacings long (velocity x life), widening
effect("Stream", [emitter("Stream", pps=60, life=0.7, size=[(0, 1.2), (0.4, 2.0), (1, 3.2)], velocity=12, emitter_size=0.4, cone=6)])

# sparks: a spray off every point, pushed downwind (+Z) with some spread - streaks, so they read as flow, not puffs.
# Per metre of spacing (VelocityMultiplier = spacing): 6 x ~9 m spacing = ~55 m/s, ~20 m before they die.
HOT = {"X": 1, "Y": 0.85, "Z": 0.55, "W": 1}; ORANGE = {"X": 1, "Y": 0.45, "Z": 0.08, "W": 1}
RED = {"X": 0.75, "Y": 0.15, "Z": 0.04, "W": 1}; GONE = {"X": 0.3, "Y": 0.03, "Z": 0.01, "W": 0}
def sparks(name, *, pps, life, velocity, cone, size):
    g = emitter(name, base="sparks", pps=pps, life=life, size=size, velocity=velocity, emitter_size=0.6, cone=cone,
                life_var=0.4, size_var=0.5, emissive=[(0, 6000), (0.3, 4000), (0.7, 1500), (1, 0)])
    p = os.path.join(OUT, f"PlasmaField_{name}_ParticleEmitter.def")
    d = json.load(open(p, encoding="utf-8")); v = d["$Value"]
    v["TimeLines"]["Color"] = over_life([(0, HOT), (0.25, ORANGE), (0.65, RED), (1, GONE)])
    v["EmitterFlags"]["EnableStreaks"] = True
    v["RenderingParameters"]["StreakMultiplier"] = 4
    write(f"PlasmaField_{name}_ParticleEmitter.def", d, g)
    return g
effect("ShockSparks", [sparks("ShockSparks", pps=250, life=0.4, velocity=6, cone=25, size=[(0, 0.04), (0.5, 0.035), (1, 0.02)])])
effect("TailSparks", [sparks("TailSparks", pps=300, life=0.8, velocity=9, cone=8, size=[(0, 0.05), (0.5, 0.04), (1, 0.025)])])

# the same sparks cooling through magenta to violet (the KSP / shuttle-footage plasma colours)
MAGENTA = {"X": 0.95, "Y": 0.25, "Z": 0.65, "W": 1}; VIOLET = {"X": 0.45, "Y": 0.12, "Z": 1.0, "W": 0}
def sparks_violet(name, **kw):
    g = sparks(name, **kw)
    p = os.path.join(OUT, f"PlasmaField_{name}_ParticleEmitter.def")
    d = json.load(open(p, encoding="utf-8"))
    d["$Value"]["TimeLines"]["Color"] = over_life([(0, HOT), (0.2, ORANGE), (0.5, MAGENTA), (1, VIOLET)])
    write(f"PlasmaField_{name}_ParticleEmitter.def", d, g)
    return g
effect("ShockSparksViolet", [sparks_violet("ShockSparksViolet", pps=250, life=0.4, velocity=6, cone=25, size=[(0, 0.04), (0.5, 0.035), (1, 0.02)])])
effect("TailSparksViolet", [sparks_violet("TailSparksViolet", pps=300, life=0.8, velocity=9, cone=8, size=[(0, 0.05), (0.5, 0.04), (1, 0.025)])])

# LINE emitters: the spawn box 1 m along local X and ~2 cm across (EmitterSize is per scale; the code stretches X to
# its outline segment with EmitterSizeMultiplier = segment length / EmitterScaleMultiplier), spraying down +Z
def line(name, violet, **kw):
    g = sparks_violet(name, **kw) if violet else sparks(name, **kw)
    p = os.path.join(OUT, f"PlasmaField_{name}_ParticleEmitter.def")
    d = json.load(open(p, encoding="utf-8"))
    d["$Value"]["TimeLines"]["EmitterSize"] = const(v3(1.0, 0.02, 0.02))
    write(f"PlasmaField_{name}_ParticleEmitter.def", d, g)
    return g
for violet, tag in ((True, "Violet"), (False, "Orange")):
    effect(f"LineShock{tag}", [line(f"LineShock{tag}", violet, pps=200, life=0.4, velocity=6, cone=20, size=[(0, 0.04), (0.5, 0.035), (1, 0.02)])])
    effect(f"LineTail{tag}", [line(f"LineTail{tag}", violet, pps=200, life=0.8, velocity=9, cone=8, size=[(0, 0.05), (0.5, 0.04), (1, 0.025)])])

# MATCHED to the plasma's spot light (PlasmaSpike: the same colour, PLASMA): sparks born that colour, cooling through
# orange and magenta to violet; three brightnesses (emissivity at birth) so the spot and the sparks can be balanced
PLASMA = {"X": 1.0, "Y": 0.55, "Z": 0.25, "W": 1}
for lvl, em in ((1, 3000), (2, 1000), (3, 330), (4, 165), (5, 80)):
    def matched(name, **kw):
        g = emitter(name, base="sparks", life_var=0.4, size_var=0.5, emitter_size=0.6,
                    emissive=[(0, em), (0.3, em * 0.7), (0.7, em * 0.3), (1, 0)], **kw)
        p = os.path.join(OUT, f"PlasmaField_{name}_ParticleEmitter.def")
        d = json.load(open(p, encoding="utf-8")); v = d["$Value"]
        v["TimeLines"]["Color"] = over_life([(0, PLASMA), (0.25, ORANGE), (0.55, MAGENTA), (1, VIOLET)])
        v["EmitterFlags"]["EnableStreaks"] = True
        v["RenderingParameters"]["StreakMultiplier"] = 4
        write(f"PlasmaField_{name}_ParticleEmitter.def", d, g)
        return g
    effect(f"ShockMatched{lvl}", [matched(f"ShockMatched{lvl}", pps=250, life=0.4, size=[(0, 0.04), (0.5, 0.035), (1, 0.02)], velocity=6, cone=25)])
    effect(f"TailMatched{lvl}", [matched(f"TailMatched{lvl}", pps=300, life=0.8, size=[(0, 0.05), (0.5, 0.04), (1, 0.025)], velocity=9, cone=8)])

# RATE variants of the matched sparks (fewer sparks per emitter, the emitters as dense as before): every brightness
# level x spawn rate 100 / 75 / 50 / 25 %. Their effect Guids follow a pattern the code builds (no table):
#   5d0c7a3e-000K-4LLL-8RRR-000000000001   K 1 shock, 2 tail; LLL the level; RRR the rate (%), all hex
def rate_guid(kind, lvl, rate): return f"5d0c7a3e-{kind:04x}-4{lvl:03x}-8{rate:03x}-000000000001"
def effect_fixed(name, emitters, guid):
    d = load(r"Planets\Weather\Meteor\V3\MeteorFireV3_ParticleEffect.def")
    v = d["$Value"]; v["Guid"] = guid
    v["EffectDuration"] = 2; v["EffectDurationVariation"] = 0; v["EffectMaxDistance"] = 20000; v["IsLooping"] = True
    v["ParticleEmitters"] = [{"Key": kid(), "Value": {"Definition": e, "Parameters": {
        "GlobalScaleFactor": 1, "EmitterLifeTime": 2, "EmitterStartTime": 0, "GravityMultiplier": 0, "WindMultiplier": 0}}}
        for e in emitters]
    v["ParticleAudio"] = None; v["ParticleLight"] = []
    write(f"PlasmaField_{name}_ParticleEffect.def", d, guid)
for lvl, em in ((1, 3000), (2, 1000), (3, 330), (4, 165), (5, 80)):
    for rate in (100, 75, 50, 25):
        def matched_r(name, **kw):
            g = emitter(name, base="sparks", life_var=0.4, size_var=0.5, emitter_size=0.6,
                        emissive=[(0, em), (0.3, em * 0.7), (0.7, em * 0.3), (1, 0)], **kw)
            p = os.path.join(OUT, f"PlasmaField_{name}_ParticleEmitter.def")
            d = json.load(open(p, encoding="utf-8")); v = d["$Value"]
            v["TimeLines"]["Color"] = over_life([(0, PLASMA), (0.25, ORANGE), (0.55, MAGENTA), (1, VIOLET)])
            v["EmitterFlags"]["EnableStreaks"] = True
            v["RenderingParameters"]["StreakMultiplier"] = 4
            write(f"PlasmaField_{name}_ParticleEmitter.def", d, g)
            return g
        f = rate / 100
        effect_fixed(f"ShockL{lvl}R{rate}", [matched_r(f"ShockL{lvl}R{rate}", pps=250 * f, life=0.4, size=[(0, 0.04), (0.5, 0.035), (1, 0.02)], velocity=6, cone=25)], rate_guid(1, lvl, rate))
        effect_fixed(f"TailL{lvl}R{rate}", [matched_r(f"TailL{lvl}R{rate}", pps=300 * f, life=0.8, size=[(0, 0.05), (0.5, 0.04), (1, 0.025)], velocity=9, cone=8)], rate_guid(2, lvl, rate))
print("rate variants:", rate_guid(1, 4, 75))

# DEEP colour: born a saturated orange (not orange-white - overlaps then stay coloured instead of clipping to white
# under the night auto-exposure), thinner (x0.6). Guid kinds 3 shock, 4 tail; levels 2-5, rates 100 / 50 / 25.
DEEP = {"X": 1.0, "Y": 0.35, "Z": 0.06, "W": 1}; DEEP_RED = {"X": 0.85, "Y": 0.12, "Z": 0.08, "W": 1}
for lvl, em in ((2, 1000), (3, 330), (4, 165), (5, 80)):
    for rate in (100, 50, 25, 12, 6):
        def deep(name, **kw):
            g = emitter(name, base="sparks", life_var=0.4, size_var=0.5, emitter_size=0.6,
                        emissive=[(0, em), (0.3, em * 0.7), (0.7, em * 0.3), (1, 0)], **kw)
            p = os.path.join(OUT, f"PlasmaField_{name}_ParticleEmitter.def")
            d = json.load(open(p, encoding="utf-8")); v = d["$Value"]
            v["TimeLines"]["Color"] = over_life([(0, DEEP), (0.3, DEEP_RED), (0.6, MAGENTA), (1, VIOLET)])
            v["EmitterFlags"]["EnableStreaks"] = True
            v["RenderingParameters"]["StreakMultiplier"] = 4
            write(f"PlasmaField_{name}_ParticleEmitter.def", d, g)
            return g
        f = rate / 100
        effect_fixed(f"DeepShockL{lvl}R{rate}", [deep(f"DeepShockL{lvl}R{rate}", pps=250 * f, life=0.4, size=[(0, 0.024), (0.5, 0.021), (1, 0.012)], velocity=6, cone=25)], rate_guid(3, lvl, rate))
        effect_fixed(f"DeepTailL{lvl}R{rate}", [deep(f"DeepTailL{lvl}R{rate}", pps=300 * f, life=0.8, size=[(0, 0.03), (0.5, 0.024), (1, 0.015)], velocity=9, cone=8)], rate_guid(4, lvl, rate))
print("deep variants:", rate_guid(3, 4, 25))
