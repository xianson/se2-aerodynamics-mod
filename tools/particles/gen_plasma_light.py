# gen_plasma_light.py - the reentry plasma's own light: light-only particle effects (no emitters), the entry light
# (AeroEntry_ParticleLightDefinition) at several strengths. The plasma mesh is lit by the scene (PBRTransparent): a
# bright orange light at the shock lights it - and the hull - by night as by day.
# Effect-time keys are FRACTIONS of the effect's duration (0..1); the plasma code holds the time at its strength.
import json, os, copy, uuid

MOD = os.path.normpath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(MOD, "Assets", "Particles")
NS = uuid.UUID("2f6c8e1a-9b4d-4e3f-8a71-5c0d2e9f7b13")
STRENGTHS = (1000,)   # (a light definition is at most 1000: stronger is the code's, via the effect's scale)

def load(name):
    with open(os.path.join(OUT, name), encoding="utf-8-sig") as f: return json.load(f)

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

for s in STRENGTHS:
    light = copy.deepcopy(load("AeroEntry_ParticleLightDefinition.def"))
    lv = light["$Value"]
    lg = str(uuid.uuid5(NS, f"light/{s}")); lv["Guid"] = lg
    # (a light definition's intensity is at most 1000 - more fails validation and the world does not load; stronger
    #  is the code's: intensity x EmitterScaleMultiplier^2. The 4000 / 16000 files are held at 1000 - unused, to delete)
    i = min(s, 1000)
    lv["TimeLines"]["Intensity"] = {"KeyFrames": {"_data": [
        {"Key": kid(), "Value": {"Value": 0, "Key": 0}}, {"Key": kid(), "Value": {"Value": i * 0.1, "Key": 0.025}},
        {"Key": kid(), "Value": {"Value": i, "Key": 0.5}}, {"Key": kid(), "Value": {"Value": i, "Key": 1}}]}}
    lv["TimeLines"]["Color"] = {"KeyFrames": {"_data": [{"Key": kid(), "Value": {"Value": {"X": 1, "Y": 0.42, "Z": 0.12, "W": 1}, "Key": 0}}]}}
    lv["TimeLines"]["FalloffMultiplier"] = {"KeyFrames": {"_data": [{"Key": kid(), "Value": {"Value": 1, "Key": 0}}]}}
    lv["FlickeringParams"] = {"EnableFlickering": True, "Frequency1": 7, "Frequency2": 13, "Amplitude1": 0.08, "Amplitude2": 0.04, "PhaseShift": 0.3}
    write(f"PlasmaLight_{s}_ParticleLightDefinition.def", light, lg)
    eff = copy.deepcopy(load("AeroEntry_ParticleEffect.def"))
    ev = eff["$Value"]
    eg = str(uuid.uuid5(NS, f"effect/{s}")); ev["Guid"] = eg
    ev["ParticleEmitters"] = []
    ev["ParticleLight"] = [{"Key": kid(), "Value": lg}]
    ev["EffectMaxDistance"] = 20000
    write(f"PlasmaLight_{s}_ParticleEffect.def", eff, eg)
    print(f"PlasmaLight_{s} {eg}")
