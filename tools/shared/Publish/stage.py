# stage.py <mod dir> <out dir> - stage a Space Engineers 2 (2.4.0.95) SCRIPT mod for a Steam Workshop upload.
#
# What ModHub's "Publish" does (WorkshopComponent.PublishAsync -> ProjectPublishHelper.LoadAndPrepareProjectForPublishingAsync)
# is reproduced with the game's own code: the game's content builder (VRage.ContentPipeline.Builder.exe, the same
# ProjectPublishHelper) runs --prepareForPublish on a scratch COPY of the mod. That gives exactly the folder ModHub
# uploads: Assets stripped per their metas (only .def .partialdef .texpreset .comptex .mvx2 .model .animation .cubemap
# .comparm .armorside .loc-texts .fshash kept next to every .meta), Content = only the build outputs the metas list plus
# freshly generated contentcache.vrb / definitionsets.vrb, Worlds/, the .vrgproj verbatim. ModHub then adds thumb.jpg
# and dummy.txt. ModHub NEVER copies Scripts/ - but the game compiles Scripts/ of a downloaded mod, so this tool adds
# Scripts/Simulation and Scripts/Client (*.cs, COMMITTED content only: `git ls-files` + `git show HEAD:<path>`).
#
# The builder rewrites the metas and Content of the project it runs on, so it only ever runs on the copy in
# <out dir>/work (the live mod folder is only read). The builder setup (a copy of the game's Game2, a stand-in
# Vanilla project) is the one of D:\aero\tools\build_mod_content.py, reused here.
#
# Output (<out dir> must not exist; nothing is ever deleted - remove old stagings yourself):
#   <out>/content/        the folder to upload (SetItemContent)
#   <out>/publish.json    what the uploader needs: title, summary, tags, project GUID, mod id, thumb, verdict
#   <out>/manifest.txt    every staged file with its size
#   <out>/work/           the scratch copy the builder ran on + its logs
# Then the game's own compile verdict runs on <out>/content (D:\aero\tools\ModCheck\gameverdict.sh).
#
# Speed: the scratch copy holds only the builder's inputs (Assets/ Worlds/ the top-level metas; robocopy /MT), and
# the content build is cached: its result (every file the build wrote or changed in the copy) is kept under
# %LOCALAPPDATA%\OrbitalModBuild\stagecache\<project guid>\<key>\, keyed by a content hash of everything the build
# reads (the copy's files, the work .vrgproj, the builder files, the stand-in Vanilla, the game's Vanilla Content and
# this tool's build settings). A matching key restores that result into the copy (verified byte for byte) instead of
# building; --prepareForPublish (validation + caches + the stripped copy) runs on it either way. --no-cache builds.
#
# usage: python stage.py "<mod dir>" "<out dir>" [--thumb <jpg>] [--exclude <name or glob>]... [--allow-dirty]
#                        [--include-untracked <Scripts/... path>]... [--keep-referenced-assemblies]
#                        [--mod-id <published file id>] [--skip-verdict] [--no-cache]
import argparse, concurrent.futures, fnmatch, hashlib, inspect, json, os, re, shutil, subprocess, sys, time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))   # D:\aero\tools
import build_mod_content as bmc   # noqa: E402

GAMEVERDICT = os.path.join(os.path.dirname(HERE), "ModCheck", "gameverdict.sh")
# scripts that never ship (dev spikes); any untracked file is excluded anyway (only `git ls-files` ships)
DENYLIST = ["AeroSpeedSpike.cs"]
# nothing from these may end up in the upload
FORBIDDEN_TOP = {"tests", "docs", "tools"}                               # the mod repo's own top-level folders
FORBIDDEN_DIRS = {".git", ".claude", "__pycache__", "bin", "obj"}        # anywhere
FORBIDDEN_FILES = {".hashescache.json", ".referencescache.json", ".startupblobscache.json", "modproject.modhub-metadata",
                   ".steam-ugc-metadata", ".ugc-handle", ".gitignore", ".gitattributes"}
KNOWN_TAGS = {"ModTagArmor", "ModTagAudio", "ModTagAsteroid", "ModTagBlock", "ModTagCharacter", "ModTagMaterial",
              "ModTagParticles", "ModTagSkybox", "ModTagOther"}
TYPE_TAG = "UGCModTypeTag"         # ModTag.TypeTag, always appended by WorkshopComponent.PublishAsync
MAX_NAME, MAX_SUMMARY = 50, 250    # ModLimit
THUMB_MAX = 1024 * 1024            # Steam's preview limit is 1 MB
# the scratch copy: only what the builder reads (thumb.jpg, ModProject.modhub-metadata and the .vrgproj are read from the
# mod folder, Scripts/ from git; Content/ is rebuilt). Never copied, at any depth below Assets/ and Worlds/:
COPY_SKIP_DIRS = [".git", ".claude", "__pycache__", "bin", "obj"]
COPY_SKIP_FILES = ["*.bak", "*.bak.*", "*.bak?", "*.orig", "*.pyc"]
# the content-build cache
STAGECACHE = os.path.join(bmc.BUILD_ENV, "stagecache")
CACHE_KEEP = 2                     # entries kept per mod (newest used first)
BUILD_ARGS = []                    # the build pass: builder.exe -p <work .vrgproj> <BUILD_ARGS> -v (run_builder)
BUILDER_CACHES = {".hashescache.json", ".referencescache.json", ".startupblobscache.json"}
CACHE_FORMAT = 1                   # bump when what is cached or how the key is made changes


def die(msg):
    print("REFUSED: " + msg)
    sys.exit(1)


def git(mod, *args, binary=False):
    r = subprocess.run(["git", "-C", mod, *args], capture_output=True)
    if r.returncode != 0:
        raise RuntimeError("git " + " ".join(args) + ": " + r.stderr.decode(errors="replace"))
    return r.stdout if binary else r.stdout.decode("utf-8", errors="replace")


def is_git_repo(mod):
    return subprocess.run(["git", "-C", mod, "rev-parse", "--show-toplevel"], capture_output=True).returncode == 0 and \
        os.path.normcase(os.path.abspath(git(mod, "rev-parse", "--show-toplevel").strip())) == os.path.normcase(os.path.abspath(mod))


def read_json_keep_format(path):
    raw = open(path, "rb").read()
    bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw.decode("utf-8-sig")
    nl = "\r\n" if "\r\n" in text else "\n"
    return json.loads(text), bom, nl, raw


def dump_json_like(obj, bom, nl):
    s = json.dumps(obj, indent=2, ensure_ascii=False).replace("\n", nl)
    return (b"\xef\xbb\xbf" if bom else b"") + s.encode("utf-8")


def guid_from_ulong(n):
    # UGCHelper.GuidFromULong: the 16 GUID bytes are (0UL, id) little-endian; .NET prints the last 8 bytes as-is
    b = (0).to_bytes(8, "little") + int(n).to_bytes(8, "little")
    import uuid
    return str(uuid.UUID(bytes_le=b))


def copy_tree(src, dst):
    """Copy src -> dst without COPY_SKIP_*; robocopy /MT (timestamps kept), shutil.copytree when robocopy is missing."""
    if shutil.which("robocopy"):
        r = subprocess.run(["robocopy", src, dst, "/E", "/MT:16", "/COPY:DAT", "/DCOPY:T", "/R:2", "/W:1", "/NFL", "/NDL",
                            "/NJH", "/NJS", "/NP", "/XJ", "/XD", *COPY_SKIP_DIRS, "/XF", *COPY_SKIP_FILES], capture_output=True, text=True)
        if r.returncode >= 8:   # robocopy: 0-7 = copied / nothing to do, 8+ = failures
            raise RuntimeError(f"robocopy {src} -> {dst}: exit {r.returncode}\n{r.stdout[-2000:]}{r.stderr[-2000:]}")
        return
    sd = {d.lower() for d in COPY_SKIP_DIRS}
    shutil.copytree(src, dst, ignore=lambda d, names: [n for n in names if (n.lower() in sd and os.path.isdir(os.path.join(d, n)))
                                                          or any(fnmatch.fnmatch(n.lower(), f) for f in COPY_SKIP_FILES)])


class Phases:
    def __init__(self):
        self.t0 = self.t = time.time()
        self.rows = []

    def done(self, name):
        now = time.time()
        self.rows.append((name, now - self.t))
        print(f"  [{now - self.t:6.1f} s] {name}", flush=True)
        self.t = now

    def report(self):
        print("\ntimings:")
        for n, d in self.rows:
            print(f"  {n:<52} {d:7.1f} s")
        print(f"  {'TOTAL':<52} {time.time() - self.t0:7.1f} s")


def walk_files(top):
    """{rel path ('/'): os.stat_result} of every file under top (junctions/symlinks not followed)."""
    out, stack = {}, [""]
    while stack:
        rel = stack.pop()
        with os.scandir(os.path.join(top, rel) if rel else top) as it:
            for e in it:
                r = rel + "/" + e.name if rel else e.name
                if e.is_dir(follow_symlinks=False):
                    if not e.is_junction():
                        stack.append(r)
                elif e.is_file(follow_symlinks=False):
                    out[r] = e.stat(follow_symlinks=False)
    return out


def file_hash(path):
    with open(path, "rb") as f:
        return hashlib.file_digest(f, "blake2b").hexdigest()[:40]


def hash_files(top, rels):
    """{rel: content hash} for rels under top, in parallel."""
    with concurrent.futures.ThreadPoolExecutor(8) as ex:
        return dict(zip(rels, ex.map(lambda r: file_hash(os.path.join(top, *r.split("/"))), rels)))


def build_key(work, game2, root):
    """Hash of everything the build pass reads (see the header). Returns (key, a summary of the parts for entry.json).
    The builder's own hash caches (.hashescache.json ...: content hashes by file stamp, rewritten by every build in each
    project it loads, VRage/GameData/Engine included) are not inputs and are left out."""
    def walk(top):
        return {r: st for r, st in walk_files(top).items() if os.path.basename(r).lower() not in BUILDER_CACHES}
    parts = {"format": CACHE_FORMAT, "build_args": BUILD_ARGS, "work_name": os.path.basename(work),
             "run_builder": inspect.getsource(run_builder)}                              # how the builder is started
    parts["inputs"] = hash_files(work, sorted(walk(work)))                          # the copy, its .vrgproj included
    parts["builder"] = sorted([r, s.st_size, s.st_mtime_ns] for r, s in walk(game2).items())   # bmc's copy of Game2
    van = os.path.join(root, "GameData", "Vanilla")
    parts["standin_vanilla"] = hash_files(van, sorted(walk(van)))                   # (its Content junction not followed)
    vc = bmc.VANILLA                                                                       # the game's Vanilla Content (the junction's target)
    parts["vanilla_content"] = [bmc._steam_build(bmc.GAME)] + [
        [f, os.stat(os.path.join(vc, f)).st_size, os.stat(os.path.join(vc, f)).st_mtime_ns]
        for f in ("contentcache.vrb", "definitionsets.vrb") if os.path.exists(os.path.join(vc, f))]
    vg = os.path.join(bmc.GAME, "VRage", "GameData")                                       # the other ProjectDirectory
    parts["vrage_gamedata"] = sorted([r, s.st_size, s.st_mtime_ns] for r, s in walk(vg).items())
    h = hashlib.blake2b(json.dumps(parts, sort_keys=True).encode(), digest_size=16).hexdigest()
    return h, {k: (v if k in ("format", "build_args", "work_name", "vanilla_content", "run_builder") else f"{len(v)} item(s)") for k, v in parts.items()}


def parallel_copy(pairs):
    def one(p):
        os.makedirs(os.path.dirname(p[1]), exist_ok=True)
        shutil.copy2(p[0], p[1])
    with concurrent.futures.ThreadPoolExecutor(8) as ex:
        list(ex.map(one, pairs))


def cache_entries(mod_cache):
    """Finished entries (dirs with entry.json), most recently used first."""
    if not os.path.isdir(mod_cache):
        return []
    es = [os.path.join(mod_cache, d) for d in os.listdir(mod_cache)]
    es = [e for e in es if os.path.isfile(os.path.join(e, "entry.json"))]
    return sorted(es, key=lambda e: os.path.getmtime(os.path.join(e, "entry.json")), reverse=True)


def cache_prune(mod_cache, keep):
    """Keep the `keep` most recently used entries; also drop unfinished ones (<key>.tmp-<pid>) older than 6 h.
    Only this tool writes under STAGECACHE."""
    for e in cache_entries(mod_cache)[keep:]:
        shutil.rmtree(e, ignore_errors=True)
        print("  cache: removed old entry " + os.path.basename(e))
    for d in os.listdir(mod_cache):
        p = os.path.join(mod_cache, d)
        if ".tmp-" in d and os.path.isdir(p) and time.time() - os.path.getmtime(p) > 6 * 3600:
            shutil.rmtree(p, ignore_errors=True)


def run_builder(game2, proj, args, log):
    exe = os.path.join(game2, "VRage.ContentPipeline.Builder.exe")
    t = time.time()
    r = subprocess.run([exe, "-p", proj, *args, "-v"], capture_output=True, text=True, cwd=game2, timeout=3600)
    out = r.stdout + r.stderr
    open(log, "w", encoding="utf-8").write(out)
    fails = [l for l in out.splitlines() if " fail: " in l]
    print(f"  builder {' '.join(args) or '(build)'}: exit {r.returncode}, {len(fails)} failure line(s), {time.time() - t:.0f} s; log {log}")
    for l in fails[:8]:
        print("    " + l[:400])
    return r.returncode, fails, out


def main():
    ap = argparse.ArgumentParser(description="Stage a SE2 script mod for a Workshop upload (dry run of ModHub's publish + Scripts/).")
    ap.add_argument("mod")
    ap.add_argument("out")
    ap.add_argument("--thumb", help="preview image (JPEG, < 1 MB); default <mod>/thumb.jpg (what ModHub uses)")
    ap.add_argument("--exclude", action="append", default=[], help="extra script file name / glob (relative to Scripts/) that must not ship")
    ap.add_argument("--include-untracked", action="append", default=[], help="ship this UNTRACKED script (path relative to the mod, e.g. Scripts/Simulation/X.cs) as it is on disk; overrides the denylist")
    ap.add_argument("--allow-dirty", action="store_true", help="stage although tracked files under Assets/ Content/ Scripts/ or the .vrgproj have uncommitted changes")
    ap.add_argument("--keep-referenced-assemblies", action="store_true", help="keep the .vrgproj ReferencedAssemblies list (default: [] like every published item)")
    ap.add_argument("--mod-id", type=int, help="published file id to write into the staged .vrgproj (ProjectID.ModId) when the live one has none yet")
    ap.add_argument("--skip-verdict", action="store_true", help="do not run the game's compile verdict on the staged folder")
    ap.add_argument("--no-cache", action="store_true", help="always run the content build; neither reuse nor store a cached build")
    a = ap.parse_args()
    ph = Phases()

    mod = os.path.abspath(a.mod)
    out = os.path.abspath(a.out)
    if not os.path.isdir(mod):
        die("no mod folder " + mod)
    if os.path.exists(out):
        die(f"{out} exists - pick a new out dir (this tool never deletes anything)")
    projs = [f for f in os.listdir(mod) if f.endswith(".vrgproj")]
    if len(projs) != 1:
        die("no single .vrgproj in " + mod)
    proj_name = projs[0]
    warnings = []

    # ---- project + ModHub metadata -------------------------------------------------------------------------------------
    pj, pj_bom, pj_nl, pj_raw = read_json_keep_format(os.path.join(mod, proj_name))
    v = pj["$Value"]
    if v.get("Type") != "Mod":
        die(f".vrgproj Type is {v.get('Type')!r}: SteamProjectLocatorComponent only loads projects of Type Mod")
    project_guid = v["ProjectID"]["Guid"]
    if not project_guid or project_guid == "00000000-0000-0000-0000-000000000000":
        die("the .vrgproj has no ProjectID.Guid")
    meta_path = os.path.join(mod, "ModProject.modhub-metadata")
    mh = bmc.load_raw(meta_path)["$Value"] if os.path.exists(meta_path) else {}
    title = mh.get("Name") or v.get("Name") or os.path.basename(mod)
    summary = mh.get("Summary") or ""
    tags = []
    for t in (mh.get("Tags") or []):
        if t not in tags:
            tags.append(t)
    if TYPE_TAG not in tags:
        tags.append(TYPE_TAG)
    for t in tags:
        if t != TYPE_TAG and t not in KNOWN_TAGS:
            warnings.append(f"tag {t!r} is not one of ModHub's ModTag values")
    if len(title) > MAX_NAME:
        warnings.append(f"Name is {len(title)} chars (ModHub limit {MAX_NAME})")
    if len(summary) > MAX_SUMMARY:
        warnings.append(f"Summary is {len(summary)} chars (ModHub limit {MAX_SUMMARY})")
    if summary.strip() in ("", "Mod description"):
        warnings.append(f"Summary is the placeholder {summary!r} - set it in ModProject.modhub-metadata (or pass --summary to the uploader)")
    mod_id = None
    mid = v["ProjectID"].get("ModId")
    if mid and mid.get("Id") and mid["Id"] != "00000000-0000-0000-0000-000000000000":
        import uuid
        mod_id = int.from_bytes(uuid.UUID(mid["Id"]).bytes_le[8:], "little")
    if a.mod_id:
        if mod_id and mod_id != a.mod_id:
            die(f"--mod-id {a.mod_id} but the .vrgproj already records mod id {mod_id}")
        mod_id = a.mod_id

    # ---- git: only committed scripts ship ------------------------------------------------------------------------------
    repo = is_git_repo(mod)
    commit, scripts = None, []
    extra = []        # --include-untracked: (git-style path, shipped from disk)
    not_shipped = []  # untracked or denylisted scripts (for the verdict's hint)
    if repo:
        commit = git(mod, "rev-parse", "HEAD").strip()
        dirty = [l for l in git(mod, "status", "--porcelain", "--untracked-files=no").splitlines() if l.strip()]
        dirty_rel = [l[3:].strip().strip('"') for l in dirty]
        dirty_rel = [p for p in dirty_rel if p.startswith(("Assets/", "Content/", "Scripts/", "Worlds/")) or p == proj_name or p.endswith(".meta")]
        if dirty_rel:
            msg = "uncommitted changes to tracked files: " + ", ".join(dirty_rel[:10]) + (" ..." if len(dirty_rel) > 10 else "")
            if not a.allow_dirty:
                die(msg + " (commit them, or --allow-dirty: Assets/Content then ship as they are on disk, Scripts/ still as committed)")
            warnings.append(msg + " (--allow-dirty; Scripts/ ship as committed at HEAD)")
        tracked = git(mod, "ls-files", "-z", "--", "Scripts/Simulation", "Scripts/Client").split("\0")
        deny = DENYLIST + a.exclude
        for p in tracked:
            if not p or not p.endswith(".cs"):
                continue
            rel = p[len("Scripts/"):]
            if any(fnmatch.fnmatch(os.path.basename(p), d) or fnmatch.fnmatch(rel, d) for d in deny) \
                    and p not in [x.replace("\\", "/") for x in a.include_untracked]:
                print("  excluded by denylist: " + p)
                not_shipped.append(p)
                continue
            scripts.append(p)
        untracked = [p for p in git(mod, "ls-files", "-z", "--others", "--exclude-standard", "--", "Scripts").split("\0") if p.endswith(".cs")]
        inc = [x.replace("\\", "/").lstrip("./") for x in a.include_untracked]
        for p in inc:
            if p not in untracked:
                die(f"--include-untracked {p}: not an untracked .cs under Scripts/ (tracked files ship anyway)")
            if not p.startswith(("Scripts/Simulation/", "Scripts/Client/")):
                die(f"--include-untracked {p}: only Scripts/Simulation and Scripts/Client are compiled by the game")
            extra.append(p)
            warnings.append(f"ships UNTRACKED {p} as it is on disk (--include-untracked) - commit it so the release is reproducible")
        for p in untracked:
            if p not in inc:
                print("  untracked, not shipped: " + p)
                not_shipped.append(p)
    elif os.path.isdir(os.path.join(mod, "Scripts")):
        die("the mod has Scripts/ but is not a git repository - only committed scripts may ship")
    else:
        warnings.append("not a git repository (no Scripts/ to ship)")

    ph.done("checks + git")

    # ---- scratch copy + the game's builder -----------------------------------------------------------------------------
    os.makedirs(out)
    work_root = os.path.join(out, "work")
    work = os.path.join(work_root, os.path.basename(mod))
    content = os.path.join(out, "content")
    os.makedirs(work)
    # Content is NOT copied: the build regenerates every output from Assets, so outputs and the hashes in the metas agree
    # (the live metas can record hashes of an older build: build_mod_content.py copies Content back but not the metas,
    # and the publish validation rejects "Output file hash does not match saved hash")
    # (only the builder's inputs: Tests/ docs/ tools/ Scripts/ .git .claude Content* are never read by the build, and
    #  bin/ obj/ __pycache__ *.bak* *.orig are skipped below Assets/ and Worlds/ too)
    print(f"copying {mod} -> {work} (Assets, Worlds, metas; Content is rebuilt) ...")
    for d in ("Assets", "Worlds"):
        if os.path.isdir(os.path.join(mod, d)):
            copy_tree(os.path.join(mod, d), os.path.join(work, d))
    for f in ("Assets.meta", "Content.meta", "Worlds.meta"):
        if os.path.exists(os.path.join(mod, f)):
            shutil.copy2(os.path.join(mod, f), work)
    # the builder's model importer (Assimp) cannot open a path of 260+ characters: "Importing of asset ... was not
    # successful" -> the publish validation fails. The out dir's length decides it.
    longest = max((os.path.join(rt, f) for rt, _, fs in os.walk(work) for f in fs), key=len, default=work)
    if len(longest) > 259:
        die(f"{longest} is {len(longest)} characters - the builder cannot open paths over 259: "
            f"use an out dir at least {len(longest) - 259} character(s) shorter")
    ph.done("scratch copy")
    game2 = bmc.game2_copy()
    root = bmc.stand_in_vanilla(game2, bmc.base_roots(work))
    wpj = json.loads(json.dumps(pj))
    wpj["$Value"]["ProjectDirectories"] = [os.path.join(bmc.GAME, "VRage", "GameData"), os.path.join(root, "GameData")]
    wpj["$Value"]["ReferencedAssemblies"] = []
    bmc.write(os.path.join(work, proj_name), wpj)
    wproj = os.path.join(work, proj_name)
    ph.done("builder setup (Game2 copy, stand-in Vanilla)")
    # 1) build: every output from Assets, and the copy's metas brought up to date with them (else the publish
    #    validation fails: "Asset file hash does not match hash saved in metadata") - or the cached result of a
    #    build of exactly these inputs
    mod_cache = os.path.join(STAGECACHE, project_guid)
    key, key_parts, hit, cache_tmp = None, None, None, None
    if not a.no_cache:
        key, key_parts = build_key(work, game2, root)
        e = os.path.join(mod_cache, key)
        hit = e if os.path.isfile(os.path.join(e, "entry.json")) else None
        ph.done(f"cache key {key}")
    if hit:
        ent = bmc.load_raw(os.path.join(hit, "entry.json"))
        print(f"  cache HIT {key}: restoring the build result of {ent['built_at']} ({len(ent['files'])} file(s), "
              f"{len(ent['deleted'])} deleted) instead of building")
        for r in ent["deleted"]:
            os.remove(os.path.join(work, *r.split("/")))
        parallel_copy([(os.path.join(hit, "files", *r.split("/")), os.path.join(work, *r.split("/"))) for r in ent["files"]])
        got = hash_files(work, sorted(ent["files"]))
        bad_ = [r for r in ent["files"] if got[r] != ent["files"][r]]
        if bad_:
            die(f"the cached build {hit} does not restore byte-identically ({len(bad_)} file(s), e.g. {bad_[0]}) - "
                "remove that entry or pass --no-cache")
        shutil.copyfile(os.path.join(hit, "build.log"), os.path.join(work_root, "build.log"))
        os.utime(os.path.join(hit, "entry.json"))   # (most recently used: kept by the pruning)
        rc, fails, blog = ent["rc"], [], open(os.path.join(work_root, "build.log"), encoding="utf-8").read()
        print(f"  builder (build): skipped - cached exit {rc}; its log copied to {os.path.join(work_root, 'build.log')}")
        ph.done("build (cache HIT: restored + verified)")
    else:
        before = walk_files(work)
        rc, fails, blog = run_builder(game2, wproj, BUILD_ARGS, os.path.join(work_root, "build.log"))
        # a model the builder could not import is logged as warn: only (the publish validation then refuses the copy)
        fails = fails + ["not imported: " + x for x in re.findall(r"Importing of asset (\S+) was not successful", blog)]
        ph.done("build (builder)" + (" - cache MISS" if key else " - --no-cache"))
        if key and rc in (0, 301) and not fails:
            # every file the build wrote or changed (+ every .meta and Content/ file, whatever their stamps say)
            after = walk_files(work)
            changed = sorted(r for r, st in after.items()
                             if r not in before or r.endswith(".meta") or r.startswith("Content/")
                             or (st.st_size, st.st_mtime_ns) != (before[r].st_size, before[r].st_mtime_ns))
            deleted = sorted(set(before) - set(after))
            os.makedirs(mod_cache, exist_ok=True)
            cache_tmp = os.path.join(mod_cache, f"{key}.tmp-{os.getpid()}")
            parallel_copy([(os.path.join(work, *r.split("/")), os.path.join(cache_tmp, "files", *r.split("/"))) for r in changed])
            shutil.copyfile(os.path.join(work_root, "build.log"), os.path.join(cache_tmp, "build.log"))
            ent = {"format": CACHE_FORMAT, "key": key, "key_parts": key_parts, "mod_dir": mod, "built_at": time.strftime("%Y-%m-%dT%H:%M:%S"),
                   "rc": rc, "deleted": deleted, "files": hash_files(os.path.join(cache_tmp, "files"), changed)}
            ph.done(f"cache: build result copied ({len(changed)} file(s))")
    loose = re.findall(r"Found loose metafile '([^']+)'", blog)
    if loose:
        # a .meta whose source (.fbx, .png, ...) is not on disk: the build skips it, the publish validation then fails
        # every asset that imports it ("Asset file import failed, dependencies might not be found")
        exts = {}
        for p in loose:
            e = p[:-5].rsplit(".", 1)[-1].lower() if "." in p[:-5] else "?"
            exts[e] = exts.get(e, 0) + 1
        msg = (f"{len(loose)} source file(s) missing next to their .meta ({', '.join(f'{n} .{e}' for e, n in sorted(exts.items()))}), "
               f"e.g. {loose[0][:-5]} - copy the sources into the mod folder (they are not in git?)")
        print("  " + msg)
        warnings.append(msg)
    if rc not in (0, 301) or fails:
        die("the content build failed on the copy (see work/build.log)")
    # 2) the publish preparation itself (ProjectPublishHelper: validate, caches, stripped copy) into <out>/content
    rc, fails, log = run_builder(game2, wproj, ["--prepareForPublish", content], os.path.join(work_root, "prepareForPublish.log"))
    ph.done("--prepareForPublish (validate, caches, stripped copy)")
    if rc not in (0, 301) or fails or "Exiting from processing done with Success" not in log or not os.path.isdir(content):
        if cache_tmp:
            shutil.rmtree(cache_tmp, ignore_errors=True)
        die("--prepareForPublish failed (see work/prepareForPublish.log)" + (f" - likely cause: {len(loose)} missing source file(s), see above" if loose else ""))
    if cache_tmp:
        # stored only once the publish validation passed on it
        bmc.write(os.path.join(cache_tmp, "entry.json"), ent)
        dst = os.path.join(mod_cache, key)
        if os.path.exists(dst):
            shutil.rmtree(cache_tmp, ignore_errors=True)
        else:
            os.rename(cache_tmp, dst)
            print(f"  cache: stored {dst}")
        cache_prune(mod_cache, CACHE_KEEP)

    # ---- what ModHub adds: dummy.txt (WorkshopComponent.GetTempDirectory), thumb.jpg -----------------------------------
    open(os.path.join(content, "dummy.txt"), "wb").close()
    thumb = a.thumb or os.path.join(mod, "thumb.jpg")
    has_thumb = False
    if os.path.exists(thumb):
        data = open(thumb, "rb").read()
        if not data.startswith(b"\xff\xd8\xff"):
            die(f"{thumb} is not a JPEG")
        if len(data) >= THUMB_MAX:
            die(f"{thumb} is {len(data)} bytes (Steam's preview limit is 1 MB)")
        shutil.copyfile(thumb, os.path.join(content, "thumb.jpg"))
        has_thumb = True
    else:
        warnings.append(f"no preview image ({thumb} missing): ModHub would publish without one - pass --thumb")

    # ---- the staged .vrgproj: the original, its machine-local paths cleared (as in published items) --------------------
    spj = json.loads(json.dumps(pj))
    sv = spj["$Value"]
    for k in ("ProjectDirectories", "GameExecutableSearchPaths", "ReferencedAssembliesSearchPaths", "EditorExtensionAssembliesSearchPaths"):
        if k in sv:
            sv[k] = []
    if not a.keep_referenced_assemblies:
        sv["ReferencedAssemblies"] = []   # GameCodeProvider throws if a listed assembly cannot be found on the player's PC
    if mod_id:
        sv["ProjectID"]["ModId"] = {"Id": guid_from_ulong(mod_id), "ServiceName": "Steam"}
        sv["ProjectID"]["ServiceId"] = "Steam"
    if dump_json_like(pj, pj_bom, pj_nl) != pj_raw:
        warnings.append("the .vrgproj does not round-trip byte-exactly through json (formatting differs from the original)")
    open(os.path.join(content, proj_name), "wb").write(dump_json_like(spj, pj_bom, pj_nl))

    # ---- Scripts/ (committed content) ------------------------------------------------------------------------------------
    for p in scripts:
        dst = os.path.join(content, *p.split("/"))
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        open(dst, "wb").write(git(mod, "show", "HEAD:" + p, binary=True))
    for p in extra:
        dst = os.path.join(content, *p.split("/"))
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copyfile(os.path.join(mod, *p.split("/")), dst)

    # ---- refuse anything that must not ship ----------------------------------------------------------------------------
    bad, files = [], []
    for rt, dirs, fs in os.walk(content):
        for f in fs:
            full = os.path.join(rt, f)
            rel = os.path.relpath(full, content).replace("\\", "/")
            parts = rel.split("/")
            low = [x.lower() for x in parts]
            if (len(low) > 1 and (low[0] in FORBIDDEN_TOP or low[0].startswith("content.before-"))) \
                    or any(x in FORBIDDEN_DIRS for x in low[:-1]) or low[-1] in FORBIDDEN_FILES \
                    or (low[-1].startswith(".")) or (rel.endswith(".cs") and not rel.startswith(("Scripts/Simulation/", "Scripts/Client/"))) \
                    or rel.endswith((".md", ".py", ".pyc", ".bak", ".orig", ".log")):
                bad.append(rel)
            files.append((rel, os.path.getsize(full)))
    if bad:
        die("the staged folder contains files that must not ship: " + ", ".join(bad[:20]))

    # ---- Content drift: the staged Content is rebuilt from Assets; compare with what the live mod mounts ---------------
    def norm(b):
        return b.replace(b"\r\n", b"\n")
    live_c, st_c = os.path.join(mod, "Content"), os.path.join(content, "Content")
    def listing(d):
        s = set()
        for rt, _, fs in os.walk(d):
            for f in fs:
                s.add(os.path.relpath(os.path.join(rt, f), d).replace("\\", "/"))
        return s
    ls_live, ls_st = listing(live_c), listing(st_c)
    only_live = sorted(ls_live - ls_st)
    only_st = sorted(ls_st - ls_live)
    differ = []
    for r in sorted(ls_live & ls_st):
        if r.lower().endswith(".vrb"):
            continue   # regenerated caches
        a_, b_ = os.path.join(live_c, r), os.path.join(st_c, r)
        if os.path.getsize(a_) != os.path.getsize(b_) or open(a_, "rb").read() != open(b_, "rb").read():
            ra, rb = open(a_, "rb").read(), open(b_, "rb").read()
            if norm(ra) != norm(rb):
                try:   # same JSON values written differently (2000 vs 2000.0, key spacing) is not drift
                    same = json.loads(ra.decode("utf-8-sig")) == json.loads(rb.decode("utf-8-sig"))
                except ValueError:
                    same = False
                if not same:
                    differ.append(r)

    # ---- manifest ------------------------------------------------------------------------------------------------------
    files.sort()
    total = sum(s for _, s in files)
    groups = {}
    for rel, s in files:
        g = rel.split("/")[0] if "/" in rel else rel
        n, z = groups.get(g, (0, 0))
        groups[g] = (n + 1, z + s)
    with open(os.path.join(out, "manifest.txt"), "w", encoding="utf-8", newline="\n") as f:
        for rel, s in files:
            f.write(f"{s:>12}  {rel}\n")
        f.write(f"{total:>12}  TOTAL ({len(files)} files)\n")
    print()
    print(f"staged {os.path.basename(mod)} -> {content}")
    for g in sorted(groups):
        n, z = groups[g]
        print(f"  {g:<40} {n:>6} file(s) {z / 1048576:>10.2f} MB")
    print(f"  {'TOTAL':<40} {len(files):>6} file(s) {total / 1048576:>10.2f} MB   (full list: {os.path.join(out, 'manifest.txt')})")
    print(f"  scripts: {len(scripts)} .cs from git HEAD{f' + {len(extra)} untracked from disk' if extra else ''} {commit[:10] if commit else '-'}")
    print(f"  Content vs the live mod's Content: {len(only_live)} only live (not listed by any meta -> not shipped), "
          f"{len(only_st)} only staged, {len(differ)} differ in value (rebuilt from Assets; line endings and number formatting ignored)")
    for r in only_live[:5]:
        print("    only live: " + r)
    for r in differ[:5]:
        print("    differs:   " + r)
    if only_live or only_st or differ:
        warnings.append(f"staged Content differs from the live Content ({len(only_live)} only-live, {len(only_st)} only-staged, {len(differ)} differ): "
                        "the live one may be stale - test the staged folder, not the live mod")

    ph.done("staging extras, checks, manifest, drift")

    # ---- the game's own compile verdict on the staged folder -----------------------------------------------------------
    verdict = None
    if (scripts or extra) and not a.skip_verdict:
        print("\nGameVerdict on the staged folder (the game's own compile of Scripts/Simulation) ...")
        r = subprocess.run(["bash", GAMEVERDICT.replace("\\", "/"), content.replace("\\", "/")], capture_output=True, text=True)
        lines = (r.stdout + r.stderr).strip().splitlines()
        errors = [l for l in lines if ": error " in l]
        for l in errors[:30]:
            print("  " + l)
        for l in lines[-3:]:
            print("  " + l)
        # the usual cause: a committed script uses something declared only in a file that does not ship
        hints = []
        for name in sorted(set(re.findall(r"error CS(?:0103|0246|0234|0117): The (?:name|type or namespace name) '([A-Za-z_][A-Za-z0-9_]*)'", "\n".join(errors)))):
            for p in not_shipped:
                try:
                    src = open(os.path.join(mod, *p.split("/")), encoding="utf-8", errors="replace").read()
                except OSError:
                    continue
                if re.search(r"\b(class|struct|interface|enum|record)\s+" + name + r"\b", src):
                    hints.append(f"'{name}' is declared in {p}, which does NOT ship (untracked or denylisted): commit it, "
                                 f"remove the uses, or pass --include-untracked {p}")
        for h in hints:
            print("  HINT: " + h)
        verdict = {"exit": r.returncode, "ok": r.returncode == 0, "errors": errors[:50], "hints": hints, "tail": lines[-3:]}
        if r.returncode != 0:
            warnings.append(f"GameVerdict exit {r.returncode}: the game would NOT compile the staged scripts ({len(errors)} error line(s))")
            warnings.extend("HINT: " + h for h in hints)
        ph.done("GameVerdict")
    if os.path.isdir(os.path.join(content, "Scripts", "Client")):
        warnings.append("Scripts/Client ships but GameVerdict only compiles Scripts/Simulation - check Client with ModCheck")

    info = {
        "tool": "D:/aero/tools/Publish/stage.py", "staged_at": time.strftime("%Y-%m-%dT%H:%M:%S"),
        "mod_dir": mod, "content_dir": content, "project_file": proj_name,
        "project_guid": project_guid, "project_version": v.get("Version"), "mod_id": mod_id,
        "title": title, "summary": summary, "tags": tags, "thumb": "thumb.jpg" if has_thumb else None,
        "git_commit": commit, "scripts": len(scripts) + len(extra), "untracked_included": extra, "files": len(files), "bytes": total,
        "content_drift": {"only_live": only_live, "only_staged": only_st, "differ": differ},
        "gameverdict": verdict, "warnings": warnings,
        "build_cache": {"key": key, "hit": bool(hit), "entry": hit} if key else None,
        "timings_s": {n: round(d, 1) for n, d in ph.rows},
    }
    with open(os.path.join(out, "publish.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(info, f, indent=2)
    print()
    for w in warnings:
        print("WARNING: " + w)
    print(f"\npublish.json: {os.path.join(out, 'publish.json')}")
    ph.report()
    ok = verdict is None or verdict["ok"]
    print("STAGED OK" if ok else "STAGED, BUT THE GAME WOULD REJECT THE SCRIPTS")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
