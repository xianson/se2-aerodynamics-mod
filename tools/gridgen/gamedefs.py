"""gamedefs.py - every block's shape and mass from the GAME's own data, for the blocks the measured catalogue
(data/catalog.txt) lacks: heavy armour, conveyors, tanks, cargo, trusses...

How the game makes them (decompiled Game2.Simulation, 4.0.0.3151):
  * occupied cells: GeneratedBlockDataLoader hands each CubeBlockDefinition its model's occupancy (BlockOccupancyData:
    inclusive 0.25 m cell boxes, the block's local frame) from the GeneratedBlockData blobs of the content cache
    (Vanilla Content/contentcache.vrb) - dumped by `VrbReader --occupancy` (tools/shared/VrbReader).
  * mass: CubeBlockDefinition.ComputeMassAndHP  =  Density.MassCurveModifier * sqrt(n) * log10(n) + MinBlockMass, n the
    occupied cell count; MinBlockMass 5 (System/Configurations/CubeBlockMassConfiguration.def); the density
    (Blocks/Shared/Density: Hollow 7, Mostly Hollow 11, Mostly Solid 20, Solid 35) from the block's definition, else its
    template's: a block's definition file is NAME_<Template>.def, the template Templates/**/<Template>.def (light armour:
    Mostly Hollow, heavy armour: Solid). Checked against every measured catalogue block (tests/test_gridgen.py).
  * heavy armour shares the light armour's model (ArmorBlockDefinition.Model): the same cells, Solid instead of Mostly Hollow.

The mod's own blocks (Assets/**/*.partialdef) join the index too: density from their definition or its vanilla base,
cells from MOD_SIZES (their models' occupancy is not in any cache: 'assumed': 'box'), roles from MOD_ROLES.

Index: name -> composite (the GUID a blueprint stores), prefab (the server prefab: catalogue key), model, density, cells,
bounds, groups, mass. Built once (needs the built VrbReader and the game), cached in data/gameblocks.json (committed: the
tools run without the game). Rebuild: python gamedefs.py --rebuild
"""
from __future__ import annotations

import glob
import json
import math
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
GAME_ROOT = os.environ.get("SE2_ROOT", r"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers2")
CONTENT = os.path.join(GAME_ROOT, "GameData", "Vanilla", "Content")
CACHE = os.path.join(HERE, "data", "gameblocks.json")
VRBREADER = os.path.normpath(os.path.join(HERE, "..", "shared", "VrbReader", "bin", "Release", "net9.0", "VrbReader.exe"))
MIN_BLOCK_MASS = 5.0     # CubeBlockMassConfiguration.MinBlockMass (read from the game when indexing)


def block_mass(cells: int, curve: float | None, min_mass: float = MIN_BLOCK_MASS) -> float:
    """CubeBlockDefinition.ComputeMassAndHP."""
    if curve is None or cells <= 0:
        return min_mass
    return curve * math.sqrt(cells) * math.log10(cells) + min_mass


def _load(path):
    try:
        return json.load(open(path, encoding="utf-8-sig"))
    except Exception:
        return None


def _guid(s):
    return s[3:].lower() if isinstance(s, str) and s.startswith("{G}") else (s.lower() if isinstance(s, str) else None)


def _occupancy(cache_vrb):
    if not os.path.exists(VRBREADER):
        raise SystemExit(f"gamedefs: no {VRBREADER} - build it: dotnet build -c Release tools/shared/VrbReader")
    out = subprocess.run([VRBREADER, "--occupancy", cache_vrb], capture_output=True, check=True).stdout
    return json.loads(out)


# CubeBlockDefinition and its subclasses (decompiled Game2.Simulation): the definition ComputeMassAndHP runs on
CUBE_BLOCK_DEF_TYPES = {"CubeBlockDefinitionObjectBuilder", "FunctionalBlockDefinitionObjectBuilder",
                        "PowerableBlockDefinitionObjectBuilder"}


def _words(s):
    return sorted(re.findall(r"[A-Z][a-z0-9]*|[a-z0-9]+", s))


def _template_density(templates, tmpl):
    """The template's Density: the template of that name, else one named with the same words (the light armour's
    LightArmorCubeBlockDefinition is the template ArmorLightCubeBlockDefinition)."""
    if templates.get(tmpl):
        return templates[tmpl]
    w = _words(tmpl)
    for k, v in templates.items():
        if v and _words(k) == w:
            return v
    return None


def _refs(v):
    """Every GUID a composition names as a component Definition."""
    out = []
    if isinstance(v, dict):
        for k, x in v.items():
            if k == "Definition" and isinstance(x, str):
                out.append(_guid(x))
            else:
                out += _refs(x)
    elif isinstance(v, list):
        for x in v:
            out += _refs(x)
    return out


def _prefab_composite(defs, prefab):
    p = defs.get(_guid(prefab) or "")
    return p[2].get("_entity", {}).get("Definition") if p else None


def build_index(content=CONTENT):
    occ = _occupancy(os.path.join(content, "contentcache.vrb"))
    cfg = _load(os.path.join(content, "System", "Configurations", "CubeBlockMassConfiguration.def")) or {}
    min_mass = float(cfg.get("$Value", {}).get("MinBlockMass", MIN_BLOCK_MASS))
    curves = {}
    for f in glob.glob(os.path.join(content, "Blocks", "Shared", "Density", "*.def")):
        v = (_load(f) or {}).get("$Value", {})
        if "MassCurveModifier" in v:
            curves[v["Guid"].lower()] = float(v["MassCurveModifier"])
    templates, cubedefs = {}, {}     # template name -> density GUID; any CubeBlockDefinition GUID -> density GUID
    for f in glob.glob(os.path.join(content, "Templates", "**", "*.def"), recursive=True):
        v = (_load(f) or {}).get("$Value", {})
        templates[os.path.splitext(os.path.basename(f))[0].strip()] = v.get("Density")
        if isinstance(v.get("Guid"), str):
            cubedefs[v["Guid"].lower()] = v.get("Density")
    idx = {}
    for sub in ("Armors", "Blocks"):
        for root, _, files in os.walk(os.path.join(content, sub)):
            # the folder's definitions by GUID: a block's parts are found through its compositions' references, not by
            # file name (names carry typos and stray spaces: DetailingCubet25_..., "ConveyorTJunction50 _Server.def")
            defs = {}
            for f in files:
                if f.endswith(".def"):
                    d = _load(os.path.join(root, f)) or {}
                    g = d.get("$Value", {}).get("Guid")
                    if isinstance(g, str):
                        defs[g.lower()] = (f, d.get("$Type", ""), d["$Value"])
            for f, t, v in list(defs.values()):
                if "ServerComposite" not in v or not t.endswith("PrefabBindingDefinitionObjectBuilder"):
                    continue
                name = f[:-4]
                e = {"composite": v["ServerComposite"].lower()}
                refs = []
                for comp in (v["ServerComposite"], _prefab_composite(defs, v.get("ClientPrefab"))):
                    if comp and comp.lower() in defs:
                        refs += _refs(defs[comp.lower()][2])
                for pf, pt, pv in defs.values():   # the server prefab: the catalogue's key
                    if pt.endswith("PrefabDefinitionObjectBuilder") and _guid(pv.get("_entity", {}).get("Definition")) == e["composite"]:
                        e["prefab"] = pv["Guid"].lower()
                for r in refs:
                    if r not in defs:
                        continue
                    g, gt, gv = defs[r]
                    if isinstance(gv.get("Model"), str) and ("ArmorBlockDefinition" in gt or "BlockModel" in gt):
                        e.setdefault("model", _guid(gv["Model"]))
                    if gt.rsplit(".", 1)[-1] in CUBE_BLOCK_DEF_TYPES and "template" not in e:
                        tmpl = g[:-4].split("_", 1)[-1].strip()      # NAME_<Template>.def
                        dens = gv.get("Density") or _template_density(templates, tmpl)
                        e["template"] = tmpl
                        e["curve"] = curves.get(dens.lower()) if dens else None
                        cubedefs[r] = dens
                o = occ.get(e.get("model") or "")
                if o:
                    e["cells"] = o["cells"]
                    e["bounds"] = o["bounds"]
                    if len(o["groups"]) > 1 or o["groups"][0] != o["bounds"]:
                        e["groups"] = o["groups"]
                    if "template" in e:
                        e["mass"] = round(block_mass(o["cells"], e["curve"], min_mass), 2)
                idx[name] = e
    if os.path.isdir(MOD_ASSETS):
        idx.update(_mod_index(MOD_ASSETS, occ, curves, cubedefs, min_mass))
    return idx


# The mod's own blocks (Assets/**/*.partialdef: copies of vanilla definitions). Their models' occupancy is generated by the
# game at load (it is not in the built content cache), so their cells are the block's whole box, from the generators:
#   ControlSurface 3x3x1 (gen_cs3_defs.py, AeroBlockGuids): chord along local X, one block thick along Y, span along Z;
#   RetractableGear (gear_export_prep.py: W x H x L = 2.5 s x 2.5 s x 7.5 s, s = size / 2.5 m, VRage X Y Z);
#   the 1x1 blocks (copies of the light armour cube); Airbrake 3x3x3 (gen_airbrake_defs.py).
# A mod block on a vanilla model (the autopilot: the Event Controller's / Timer's) takes that model's real occupancy.
# Box: local cells (x, y, z), centred on the block's position (an even count: the model's origin is a cell corner, the box
# one cell longer on +; odd - the 0.25 m control surface and airbrake: on the middle cell).
MOD_SIZES = {
    "ControlSurface_Small": (6, 2, 6), "ControlSurface_Large": (30, 10, 30), "ControlSurface_1x1": (2, 2, 2),
    "ControlSurface_025": (3, 1, 3), "ControlSurface_100": (12, 4, 12), "ControlSurface_150": (18, 6, 18),
    "Airbrake_1x1": (2, 2, 2), "Scoop_1x1": (2, 2, 2), "Airbrake_Small": (6, 6, 6), "Airbrake_Large": (30, 30, 30),
    "Airbrake_025": (3, 3, 3), "Airbrake_100": (12, 12, 12), "Airbrake_150": (18, 18, 18),
    "RetractableGear_050": (2, 2, 6), "RetractableGear_100": (4, 4, 12), "RetractableGear_150": (6, 6, 18),
    "RetractableGear_250": (10, 10, 30),
}
# the role a tool gives them: 'surface' (a control surface: AeroBench / the surface harness model it), 'self' (its component
# models all its air: out of the voxel surface - BlockComponentFactory.IsSelfModelled)
MOD_ROLES = {"ControlSurface_Small": "surface self", "ControlSurface_Large": "surface self", "ControlSurface_1x1": "surface",
             "ControlSurface_025": "surface self", "ControlSurface_100": "surface self", "ControlSurface_150": "surface self",
             "Airbrake_Small": "self", "Airbrake_Large": "self", "Airbrake_025": "self", "Airbrake_100": "self", "Airbrake_150": "self"}
MOD_ASSETS = os.path.normpath(os.path.join(HERE, "..", "..", "Assets"))


def _mod_index(assets, occ, curves, cubedefs, min_mass):
    out = {}
    for root, _, files in os.walk(assets):
        pdefs = []
        for f in files:
            if f.endswith(".partialdef"):
                d = _load(os.path.join(root, f)) or {}
                pdefs.append((f, d.get("$Value", {})))
        for f, v in pdefs:
            m = v.get("Manipulator", {})
            if not v.get("DefinitionType", "").endswith("PrefabBindingDefinition") or "ServerComposite" not in m:
                continue
            name = f[: -len(".partialdef")]
            e = {"composite": m["ServerComposite"].lower(), "mod": True}
            if name in MOD_ROLES:
                e["role"] = MOD_ROLES[name]
            for g, gv in pdefs:
                gm, gt = gv.get("Manipulator", {}), gv.get("DefinitionType", "")
                if g.startswith(name + "_") and gt.rsplit(".", 1)[-1] + "ObjectBuilder" in CUBE_BLOCK_DEF_TYPES:
                    dens = gm.get("Density") or cubedefs.get(str(gv.get("BaseDefinition")).lower())
                    e["curve"] = curves.get(dens.lower()) if dens else None
                if g.startswith(name + "_") and isinstance(gm.get("Model"), str) and "BlockModel" in gt:
                    e["model"] = _guid(gm["Model"])
            o = occ.get(e.get("model") or "")
            if o:
                e["cells"], e["bounds"] = o["cells"], o["bounds"]
                if len(o["groups"]) > 1 or o["groups"][0] != o["bounds"]:
                    e["groups"] = o["groups"]
            elif name in MOD_SIZES:
                sz = MOD_SIZES[name]
                e["cells"] = sz[0] * sz[1] * sz[2]
                e["bounds"] = [-((c - 1) // 2) for c in sz] + [c // 2 for c in sz]
                e["assumed"] = "box"
            if "cells" in e and "curve" in e:
                e["mass"] = round(block_mass(e["cells"], e["curve"], min_mass), 2)
            out[name] = e
    return out


def rebuild():
    idx = build_index()
    with open(CACHE, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("{\n" + ",\n".join(f"{json.dumps(k)}: {json.dumps(v, separators=(',', ':'))}" for k, v in sorted(idx.items())) + "\n}\n")
    return idx


_INDEX = None


def index():
    global _INDEX
    if _INDEX is None:
        _INDEX = json.load(open(CACHE, encoding="utf-8")) if os.path.exists(CACHE) else rebuild()
    return _INDEX


def groups(e):
    """The block's occupied cell boxes (inclusive, local frame, identity orientation)."""
    return e.get("groups") or [e["bounds"]]


if __name__ == "__main__":
    if "--rebuild" in sys.argv:
        i = rebuild()
        print(f"{len(i)} blocks -> {CACHE}: {sum('mass' in e for e in i.values())} with cells and mass")
    else:
        print(__doc__)
