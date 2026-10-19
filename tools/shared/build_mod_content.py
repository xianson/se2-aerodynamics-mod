# build_mod_content.py <mod project dir> — compile a SE2 mod project's Assets into its Content (the compiled
# definitions plus contentcache.vrb / definitionsets.vrb), which is all the game mounts for a mod.
#
# SE2 2.4 ships no working content builder for mods: the Mod SDK is stale (2.0.2 editor, a half-updated 2.2
# Game2) and the game's own builder (Game2\VRage.ContentPipeline.Builder.exe, 2.4) needs an editor plugin it
# does not ship and a Vanilla project WITH SOURCES (the game has only compiled Content). The recipe (proven on
# the Orbital Mod's sensor blocks; see that mod's tools/blocks/gen_blocks.py), in BUILD_ENV, outside every repo
# and never touching the game or the SDK install:
#   game2\   a copy of the game's Game2 plus a stand-in VRage.Plugin.Editor.dll (empty, its version only)
#   root\GameData\Vanilla\   a stand-in Vanilla project (the real one's GUID) holding just the game's own
#            definitions the project's partial definitions derive from, as Vanilla's OWN SOURCES (from the Mod
#            SDK; the game rejects a definition one set calls partial and another not), each at the path of
#            its compiled 2.4 output; its Content is a junction to the game's real Vanilla Content
# The build runs on a staging copy (the Assets only: the builder would also compile the scripts), then the
# compiled Content is copied into the project. Its previous Content is kept first as Content.before-<stamp>.
import json, os, sys, shutil, subprocess, time

GAME = r"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers2"
VANILLA = os.path.join(GAME, "GameData", "Vanilla", "Content")
SDK_ASSETS = r"D:\SteamLibrary\steamapps\common\Space Engineers 2 - Mod SDK\GameData\Vanilla\Assets"
BUILD_ENV = os.path.join(os.environ["LOCALAPPDATA"], "OrbitalModBuild")   # (shared with the Orbital Mod's build)
VER = "2.4.0.16"

def load_raw(path):
    with open(path, encoding="utf-8-sig") as f: return json.load(f)

def write(path, obj):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f: json.dump(obj, f, indent=2)

def meta(asset_id):
    return {"$Bundles": {"System.Runtime": "1.0.0.0", "VRage": VER},
            "$Type": "VRage:Keen.VRage.ContentPipeline.Metafiles.MetaData",
            "$Value": {"AssetID": asset_id, "InternalItems": []}}

def game2_copy():
    game2 = os.path.join(BUILD_ENV, "game2")
    ver_file = os.path.join(game2, ".copied_from")
    stamp = str(os.path.getmtime(os.path.join(GAME, "Game2", "Game2.Simulation.dll")))
    if not os.path.exists(ver_file) or open(ver_file).read() != stamp:
        print("copying the game's Game2 (a game update since the last build) ...")
        shutil.rmtree(game2, ignore_errors=True)
        shutil.copytree(os.path.join(GAME, "Game2"), game2)
        stub = os.path.join(BUILD_ENV, "stub")
        os.makedirs(stub, exist_ok=True)
        open(os.path.join(stub, "Stub.csproj"), "w").write(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework>'
            '<AssemblyName>VRage.Plugin.Editor</AssemblyName><AssemblyVersion>2.4.0.95</AssemblyVersion></PropertyGroup></Project>')
        open(os.path.join(stub, "Stub.cs"), "w").write("namespace Keen.VRage.Plugin.Editor { internal static class Stub { } }")
        subprocess.run(["dotnet", "build", "-c", "Release", "-o", "out"], cwd=stub, check=True, capture_output=True)
        shutil.copy(os.path.join(stub, "out", "VRage.Plugin.Editor.dll"), game2)
        open(ver_file, "w").write(stamp)
    return game2

def base_roots(project):
    """The game's definitions the project's partial definitions derive from (not its own)."""
    own, bases = set(), set()
    for rt, _, files in os.walk(os.path.join(project, "Assets")):
        for f in files:
            p = os.path.join(rt, f)
            if f.endswith(".def") or f.endswith(".partialdef"):
                try: v = load_raw(p)["$Value"]
                except (OSError, ValueError, KeyError): continue
                if f.endswith(".partialdef"):
                    if v.get("BaseDefinition"): bases.add(v["BaseDefinition"])
                    m = v.get("Manipulator") or {}
                    if m.get("Guid"): own.add(m["Guid"])
                elif v.get("Guid"): own.add(v["Guid"])
    return sorted(bases - own)

def _steam_build(common_dir):
    """(appid, buildid, LastUpdated) of the Steam app installed in <steamapps>/common/<name>, or None."""
    steamapps = os.path.dirname(os.path.dirname(common_dir))
    name = os.path.basename(common_dir)
    try: acfs = [f for f in os.listdir(steamapps) if f.startswith("appmanifest_") and f.endswith(".acf")]
    except OSError: return None
    for f in acfs:
        try: t = open(os.path.join(steamapps, f), encoding="utf-8", errors="replace").read()
        except OSError: continue
        def kv(k):
            i = t.find('"' + k + '"')
            return t[i:].split('"')[3] if i >= 0 else None
        if (kv("installdir") or "").lower() == name.lower():
            return [kv("appid"), kv("buildid"), kv("LastUpdated")]
    return None

def vanilla_indexes():
    """(Mod SDK def/partialdef source by AssetID, compiled 2.4 Vanilla .def by Guid). Reading every SDK .meta (~96k
    files on the SDK's drive) takes 10+ min with a cold disk cache, so the two maps are kept in BUILD_ENV, keyed by the
    Steam builds of the SDK and the game plus the Vanilla content index files; any change re-reads them."""
    sdk_root = os.path.dirname(os.path.dirname(os.path.dirname(SDK_ASSETS)))
    sig = []
    for f in ("contentcache.vrb", "definitionsets.vrb"):
        try: st = os.stat(os.path.join(VANILLA, f)); sig.append([f, st.st_size, st.st_mtime_ns])
        except OSError: sig.append([f, None, None])
    key = {"v": 1, "sdk_assets": SDK_ASSETS, "sdk": _steam_build(sdk_root), "vanilla": VANILLA, "game": _steam_build(GAME),
           "vanilla_sig": sig}
    cache = os.path.join(BUILD_ENV, "vanilla-index.json")
    usable = key["sdk"] is not None and key["game"] is not None
    if usable and os.path.exists(cache):
        try:
            c = load_raw(cache)
            if c.get("key") == key: return c["index"], c["compiled"]
        except (OSError, ValueError, KeyError): pass
    index, compiled = {}, {}
    for rt, _, files in os.walk(SDK_ASSETS):
        for f in files:
            if f.endswith(".def.meta") or f.endswith(".partialdef.meta"):
                try:
                    with open(os.path.join(rt, f), encoding="utf-8-sig") as fh: t = fh.read(400)
                    k = t.find('"AssetID": "')
                    if k >= 0: index[t[k + 12:k + 48]] = os.path.join(rt, f[:-5])
                except OSError: pass
    for rt, _, files in os.walk(VANILLA):
        for f in files:
            if f.endswith(".def"):
                try:
                    with open(os.path.join(rt, f), encoding="utf-8-sig") as fh: t = fh.read(600)
                    k = t.find('"Guid": "')
                    if k >= 0: compiled.setdefault(t[k + 9:k + 45], os.path.relpath(os.path.join(rt, f), VANILLA))
                except OSError: pass
    if usable:
        tmp = cache + ".tmp"
        write(tmp, {"key": key, "index": index, "compiled": compiled})
        os.replace(tmp, cache)
    return index, compiled

def stand_in_vanilla(game2, roots):
    root = os.path.join(BUILD_ENV, "root")
    van = os.path.join(root, "GameData", "Vanilla")
    jc = os.path.join(van, "Content")
    if os.path.lexists(jc): os.rmdir(jc)   # (a junction: removing it leaves the game's content alone)
    shutil.rmtree(van, ignore_errors=True)
    os.makedirs(os.path.join(van, "Assets"))
    if not os.path.exists(os.path.join(root, "Game2")):
        subprocess.run(["cmd", "/c", "mklink", "/J", os.path.join(root, "Game2"), game2], check=True, capture_output=True)
    pj = load_raw(os.path.join(GAME, "GameData", "Vanilla", "Vanilla.vrgproj"))
    pj["$Value"]["ProjectDirectories"] = [os.path.join(GAME, "VRage", "GameData")]
    write(os.path.join(van, "Vanilla.vrgproj"), pj)
    a = meta("0b17ffff-5e50-4000-8000-000000000001")
    a["$Value"]["InternalItems"] = [{"$Type": "VRage:Keen.VRage.ContentPipeline.Definitions.DefinitionSetItem", "DefinitionSetName": "World"}]
    write(os.path.join(van, "Assets.meta"), a)
    subprocess.run(["cmd", "/c", "mklink", "/J", jc, VANILLA], check=True, capture_output=True)
    index, compiled = vanilla_indexes()
    want, seen, folders, missing = list(roots), set(), set(), []
    while want:
        gid = want.pop()
        if gid in seen: continue
        seen.add(gid)
        src, out = index.get(gid), compiled.get(gid)
        if (src is not None and not os.path.exists(src)) or (out is not None and not os.path.exists(os.path.join(VANILLA, out))):
            try: os.remove(os.path.join(BUILD_ENV, "vanilla-index.json"))
            except OSError: pass
            index, compiled = vanilla_indexes()
            src, out = index.get(gid), compiled.get(gid)
        if src is None or out is None: missing.append((gid, src is None, out is None)); continue
        rel = out[:-4] + (".partialdef" if src.endswith(".partialdef") else ".def")
        dst = os.path.join(van, "Assets", rel)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy(src, dst)
        m = load_raw(src + ".meta")
        for it in m["$Value"]["InternalItems"]:
            if "ContentFiles" in it: it["ContentFiles"]["MainOutput"]["FileName"] = os.path.basename(out)
        write(dst + ".meta", m)
        parts = os.path.dirname(rel).split(os.sep)
        for k in range(1, len(parts) + 1): folders.add(os.sep.join(parts[:k]))
        if src.endswith(".partialdef"): want.append(load_raw(src)["$Value"]["BaseDefinition"])
    for n_, f in enumerate(sorted(folders)):
        parent, name = os.path.split(f)
        write(os.path.join(van, "Assets", parent, name.lower() + ".meta"), meta(f"0b17fffe-5e50-4000-8000-{n_ + 1:012x}"))
    print(f"stand-in Vanilla: {len(seen) - len(missing)} source definition(s) for {len(roots)} base(s)")
    for gid, no_src, no_out in missing:
        print(f"  base {gid}: {'no source in the Mod SDK' if no_src else ''}{' no compiled output in the game' if no_out else ''}")
    return root

def build(project):
    project = os.path.abspath(project)
    projs = [f for f in os.listdir(project) if f.endswith(".vrgproj")]
    if len(projs) != 1: raise SystemExit("no single .vrgproj in " + project)
    proj_src = os.path.join(project, projs[0])
    game2 = game2_copy()
    roots = base_roots(project)
    root = stand_in_vanilla(game2, roots)
    stage = os.path.join(BUILD_ENV, "stage", os.path.basename(project))
    shutil.rmtree(stage, ignore_errors=True)
    os.makedirs(stage)
    shutil.copytree(os.path.join(project, "Assets"), os.path.join(stage, "Assets"))
    shutil.copy(os.path.join(project, "Assets.meta"), stage)
    pj = load_raw(proj_src)
    pj["$Value"]["ProjectDirectories"] = [os.path.join(GAME, "VRage", "GameData"), os.path.join(root, "GameData")]
    pj["$Value"]["ReferencedAssemblies"] = []
    proj = os.path.join(stage, projs[0])
    write(proj, pj)
    log = os.path.join(BUILD_ENV, f"build-{os.path.basename(project).replace(' ', '_')}.log")
    exe = os.path.join(game2, "VRage.ContentPipeline.Builder.exe")
    r = subprocess.run([exe, "-p", proj, "-v"], capture_output=True, text=True, cwd=game2, timeout=1800)
    # then the caches (this pass crashes in the runtime's shutdown AFTER writing them: judged by the files)
    r2 = subprocess.run([exe, "-p", proj, "--generateCaches", "-v"], capture_output=True, text=True, cwd=game2, timeout=1800)
    out_all = r.stdout + r.stderr + " ==== caches ==== " + r2.stdout + r2.stderr
    open(log, "w", encoding="utf-8").write(out_all)
    fails = [l for l in out_all.splitlines() if " fail: " in l]
    ok = r.returncode == 0 and not fails and os.path.exists(os.path.join(stage, "Content", "contentcache.vrb"))
    n = sum(len(f) for _, _, f in os.walk(os.path.join(stage, "Content")))
    print(f"builder exit {r.returncode}, {n} file(s) in Content, {len(fails)} failure line(s); log {log}")
    for l in fails[:12]: print("  " + l[:300])
    if ok:
        content = os.path.join(project, "Content")
        if os.path.isdir(content):
            keep = content + ".before-" + time.strftime("%Y%m%d-%H%M%S")
            # (not the baked globe textures: hundreds of MB, regenerated by the Orbital Mod's tools/globes/bake_globes.py)
            shutil.copytree(content, keep, ignore=lambda d, names: [n for n in names if n == "PlanetGlobes"])
            print("previous Content kept as " + os.path.basename(keep))
        for rt, _, files in os.walk(os.path.join(stage, "Content")):
            for f in files:
                src = os.path.join(rt, f)
                dst = os.path.join(content, os.path.relpath(src, os.path.join(stage, "Content")))
                os.makedirs(os.path.dirname(dst), exist_ok=True)
                shutil.copy(src, dst)
        print("copied into " + content)
    return ok

if __name__ == "__main__":
    if len(sys.argv) != 2: raise SystemExit("usage: build_mod_content.py <mod project dir>")
    sys.exit(0 if build(sys.argv[1]) else 1)
