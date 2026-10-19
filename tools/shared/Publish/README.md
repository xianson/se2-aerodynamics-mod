# Publish: stage + upload SE2 script mods to the Steam Workshop

SE2 2.4.0.95 has no supported way to publish a **script** mod: ModHub's Publish
(`WorkshopComponent.PublishAsync`) stages the project with `ProjectPublishHelper` and that copy **never includes
`Scripts/`**, although the game compiles `Scripts/Simulation` + `Scripts/Client` of every downloaded mod. These two
tools do what ModHub does, with the game's own code wherever possible, plus `Scripts/`:

| tool | does |
|---|---|
| `stage.py` | scratch copy of the mod -> the game's content builder builds it and runs `--prepareForPublish` (ModHub's staging, byte for byte the same code) -> + `dummy.txt`, `thumb.jpg`, committed `Scripts/**/*.cs`, sanitized `.vrgproj` -> refuses anything that must not ship -> the game's compile verdict (`../ModCheck/gameverdict.sh`) on the staged folder |
| `Uploader/` | C# console on the game's own `Steamworks.NET.dll` + `steam_api64.dll` (AppID 1133870): every Steam call `SteamUGCServiceComponent` makes, in its order, with the metadata string made by the game's own serializer. **Dry run by default** - nothing reaches Steam without `--i-have-user-approval`. |

Nothing here writes into the live mod folders (`Documents\SpaceEngineers2\Mods\...`): the builder runs on a copy,
the item id is written into the **staged** `.vrgproj` and a write-back copy. Nothing is ever deleted either; old
stagings are yours to remove.

## Release procedure

Do this with the game closed only where a step says so; staging and dry runs never touch the game.

1. **Commit.** Only committed scripts ship (`git ls-files` + `git show HEAD:<path>`); untracked files never do, and
   staging refuses uncommitted changes under `Assets/ Content/ Scripts/ Worlds/` or the `.vrgproj` (`--allow-dirty`
   overrides for Assets; scripts still come from HEAD). Set `Name` (<= 50), `Summary` (<= 250) and `Tags` in
   `ModProject.modhub-metadata` - they become the Workshop title, description and tags, as in ModHub. Put a
   `thumb.jpg` (< 1 MB) in the mod folder or pass `--thumb`.
2. **Stage.**
   ```
   python "C:\Users\slob\Documents\SpaceEngineers2\Mods\Aerodynamics Mod\tools\shared\Publish\stage.py" "C:\Users\slob\Documents\SpaceEngineers2\Mods\Aerodynamics Mod" D:\aero-release\aero-1.0.0
   ```
   `<out>\content` is the upload folder, `<out>\manifest.txt` lists every file, `<out>\publish.json` carries the
   rest. It ends `STAGED OK` only if the game's compile verdict passes. Read the warnings: placeholder summary, no
   thumb, Content drift (the staged Content is rebuilt from Assets; files the live Content has but no meta lists are
   not shipped - exactly like ModHub).
   - A committed script using a type that only lives in an untracked file fails here with a `HINT:` naming the file:
     commit it, drop the uses, or `--include-untracked Scripts/Simulation/...cs` (ships the file from disk; warned).
   - `--exclude <glob>` adds to the denylist (`AeroSpeedSpike.cs` by default).
   - It ends with a `timings:` table (also in `publish.json` `timings_s`). See **Staging speed** below for the build
     cache; `--no-cache` forces a fresh content build.
3. **Verify the staged folder** before any upload: `STAGED OK` means the game's builder validated the content
   (`ProjectPublishHelper.ValidateProject`, the check ModHub runs) and the game's own compiler accepted the staged
   `Scripts/Simulation` (GameVerdict). Read `manifest.txt` once per release: nothing but `Assets/ Content/ Scripts/`,
   the `.vrgproj`, `Assets.meta`, `dummy.txt`, `thumb.jpg` (and `Worlds/` if the mod has one). Don't copy the staged
   folder into `Mods\` for a local test: it has the live mod's project GUID, two projects with one GUID collide. The
   in-game test is step 7, on the unlisted upload.
4. **Dry run the upload** (default) and read every call:
   ```
   cd "C:\Users\slob\Documents\SpaceEngineers2\Mods\Aerodynamics Mod\tools\shared\Publish\Uploader" && dotnet build -c Release
   bin\Release\net9.0\Uploader.exe D:\aero-release\aero-1.0.0
   ```
   It prints the KV tag, title, tags, visibility, the content folder, writes `uploader-dryrun-metadata.json` (the exact
   `SetItemMetadata` string, round-tripped through the game's deserializer and `TryGetModProjectID`), and the ProjectID
   block the write-back will produce.
5. **First upload - UNLISTED** (the user's call for now; `--visibility private` for a private one; ModHub would
   force Public), only with the user's approval:
   ```
   bin\Release\net9.0\Uploader.exe D:\aero-release\aero-1.0.0 --create --i-have-user-approval
   ```
   Steam must be running and logged in as the publishing account. It queries for an existing item with this project
   GUID (refuses if one exists), `CreateItem`, writes the new id into the staged `.vrgproj`, does the full update and
   subscribes you. `<out>\published.json` holds the id/URL.
6. **Write the id back** (`WorkshopComponent.cs:215-218` does this into the project): copy
   `<out>\writeback\<Name>.vrgproj` over the live `.vrgproj` (only `ProjectID.ModId`/`ServiceId` differ) and commit.
   Every later staging then carries the id, and the uploader defaults to `--update <id>`.
7. **Copy check on this account** (no second account needed for the download side): `Uploader.exe --subscribe-only <id>
   [--subscribe-only <id2>] --i-have-user-approval` subscribes, waits until Steam has installed each item (15 min cap) and
   prints its folder; `diff -rq <out>\content <that folder>` must be empty. Then a world COPY whose mod list names the
   Workshop items (`ServiceId "Steam"`, `ModId {GuidFromULong(id), "Steam"}` - the Aero mod's `tools/modlist --drop <guid>
   --add-steam <guid> <id> <name>`), launched with `-loadScripts` and without the local mods in `-projectPaths`.
   **Second-account check:** subscribe from a second Steam account (or a friend) - unlisted items are reachable by URL.
   Let it download, start SE2 with `-loadScripts`, add the mod to a new world, and confirm: the mod is listed, its
   blocks/definitions load, the scripts compiled (mod log), and the mod behaves (for the Aero mod: a test craft shows
   drag/lift; for Orbital: the HUD/frames come up). Check `steamapps\workshop\content\1133870\<id>\Scripts\Simulation`
   exists.
8. **Make public** when 7 passes: `Uploader.exe <out> --update <id> --visibility public --i-have-user-approval` (or
   change it on the Workshop page).

9. **Tag the release.** Every Workshop publish (first upload or update) is a release: after a successful upload, an
   annotated tag `v<version>` in the mod's repo on the EXACT commit that was staged (`<out>\publish.json` records it -
   not whatever HEAD is by then):
   ```
   git tag -a v1.0.1 <staged commit> -m "1.0.1 - Workshop update (unlisted), item <id>, <date>" -m "<the change note>"
   ```
   If the staged tree held anything that commit does not (`--allow-dirty` Assets, `--include-untracked` scripts), say so
   in the tag message. Tags are pushed only with the user's OK.

Updates: commit -> stage a new out dir -> dry run -> `--update <id> --change-note "..." --i-have-user-approval`
(keeps the current visibility unless `--visibility`) -> tag (step 9). `--content-only <id>` replaces just the files (one
`SetItemContent` + `SubmitItemUpdate`), for an item whose title/tags/metadata are already right.

**Never publish the same project from ModHub afterwards:** its staging drops `Scripts/`, so a ModHub publish of
the same GUID replaces the item with a script-less copy.

## Staging speed

- **The scratch copy holds only the builder's inputs:** `Assets/`, `Worlds/` and the top-level `*.meta` (robocopy
  `/MT:16`, timestamps kept; `bin/ obj/ __pycache__ .git .claude *.bak* *.orig *.pyc` skipped at any depth). The rest is
  read from the mod folder where it is needed - `.vrgproj`, `ModProject.modhub-metadata`, `thumb.jpg` - or from git
  (`Scripts/`); `Content/` is never copied (it is rebuilt), `Tests/ docs/ tools/ Content.before-*` never.
- **Vanilla index cache:** the stand-in Vanilla needs an AssetID -> source map of the Mod SDK (~96k files on the
  SDK's drive: 10+ min with a cold disk cache) and a Guid -> output map of the game's Vanilla Content.
  `../build_mod_content.py` keeps both in `%LOCALAPPDATA%\OrbitalModBuild\vanilla-index.json`, keyed by the Steam
  builds (appmanifest `buildid`/`LastUpdated`) of the SDK and the game plus the Vanilla `contentcache.vrb` /
  `definitionsets.vrb` stamps; a cached path that is gone re-reads it.
- **Content build cache:** `%LOCALAPPDATA%\OrbitalModBuild\stagecache\<project guid>\<key>\`. The key is a hash of
  everything the build pass reads: the content (blake2b) of every file in the scratch copy including the work
  `.vrgproj`, the builder (every file of the Game2 copy: size + mtime), the stand-in Vanilla (content), the game's
  Vanilla Content (Steam build + index stamps) and `VRage\GameData`, and `stage.py`'s build settings (`BUILD_ARGS`,
  the `run_builder` source, `CACHE_FORMAT`). The builder's own hash caches (`.hashescache.json`,
  `.referencescache.json`, `.startupblobscache.json` - it rewrites them in every project it loads, the game's
  `VRage\GameData\Engine` included) are not inputs and are left out of the key. An entry holds every file the build wrote or changed in the copy (all `.meta`, `Content/`,
  `.hashescache.json`, `.referencescache.json`; content hashes in `entry.json`), the deletions and the build log.
  - **miss:** the builder runs as before; its result is copied into `<key>.tmp-<pid>` and renamed to `<key>` only after
    `--prepareForPublish` passed on it.
  - **hit:** the builder is skipped; the entry is restored into the fresh scratch copy and every restored file is
    re-hashed against `entry.json` (refuses on any mismatch), so the copy is byte for byte the state a fresh build of
    the same inputs leaves. `--prepareForPublish` - ModHub's validation of the metas/hashes, the cache generation and the
    stripped copy - always runs on it, hit or miss.
  - Any input change is a new key, i.e. a build. At most 2 entries per mod are kept (the least recently used goes;
    unfinished `.tmp-*` older than 6 h too). `--no-cache` neither reads nor stores. Bump `CACHE_FORMAT` in `stage.py`
    when the build settings or what is cached change; deleting `stagecache\` is always safe.
  - `publish.json` records `build_cache` (key, hit, entry) and `timings_s`.
- **Measured** (Aerodynamics Mod, 925 block files, 2026-10-18): the release run with the old stager spent ~13 min in the
  stand-in Vanilla (cold SDK read) + 41 s build + 9 s publish prep; now (Vanilla index cached: setup ~6 s) a cache miss is ~80 s in all (build 35 s) and a
  hit ~45 s (restore + verify 4-5 s; the rest is the publish prep, checks and GameVerdict). Orbital: 31 s miss, 29 s hit.
- **Keep the out dir short:** the builder's model importer cannot open paths of 260+ characters (it logs only a
  `warn:` "Importing of asset ... was not successful", then the publish validation fails). Staging refuses up front
  when `<out>\work\<mod>\<longest asset path>` would exceed 259 characters, and counts any such build line as a failure.
- Two stagings never produce a byte-identical `Content\contentcache.vrb` (`--prepareForPublish` regenerates it; same
  size, different bytes even between two fresh builds); every other staged file is identical between a miss and a hit.

## The rules the game enforces

- **One project GUID = one Workshop item.** The item carries the KV tag `VRageProjectID = <GUID with '_' for '-'>`
  (`SteamKV.GuidToSteam`). On a first publish the game searches all items for that tag and refuses
  (`InvalidModUploadReason.IdExists`, `SteamUGCServiceComponent.cs:342,351`); on an update it refuses when the item's
  tag names another GUID (`IdMismatch`, `:329-334`). The uploader does both checks. Never reuse a mod's `.vrgproj`
  GUID for another mod (e.g. a copy of the folder), and fold-ins (Aerodynamics Mod Blocks -> Aerodynamics Mod) keep
  the receiving mod's GUID.
- **The mod id lives in the `.vrgproj`:** `ProjectID.ModId = {"Id": UGCHelper.GuidFromULong(<published file id>),
  "ServiceName": "Steam"}`, `ServiceId: "Steam"` (e.g. id 3493184025 -> `00000000-0000-0000-19c2-35d000000000`).
- **The metadata string** (`SetItemMetadata`, max 5000 bytes) is the JSON of `SteamUgcMetadata {Id, PreviewUrl,
  FullMetadata = ModMetadata {Id, ProjectID, Version, BaseMetadata}, Author, IsReferenceToRemote false,
  LastLocallyUpdated}`. Without it a subscriber's game cannot find the mod's metadata (`GetModDataFilesystemAsync`:
  "Failed to find metadata for UGC item"). The uploader builds it with the game's serializer, not by hand.
- **Load side:** a world references the mod by its `VRageProjectID` (GUID + ModId); `SteamProjectLocatorComponent`
  downloads the item, opens the folder as a project (must be `Type: Mod`) and the game compiles `Scripts/Simulation`
  (+ `Scripts/Client`) - only when the game was started with `-loadScripts`.
- **Staged `.vrgproj`:** `ProjectDirectories`, `GameExecutableSearchPaths`, `ReferencedAssembliesSearchPaths`,
  `EditorExtensionAssembliesSearchPaths` and `ReferencedAssemblies` are emptied, as in every published item
  (`GameCodeProvider` throws "Could not load referenced assemblies" when a listed one is not found on the player's
  PC; the game has the Game2 assemblies loaded anyway). `--keep-referenced-assemblies` keeps the list.
- **Never in the upload:** `Tests/ docs/ tools/ .git/ .claude/ Content.before-*`, caches, `ModProject.modhub-metadata`,
  hidden files (ModHub's copy skips them), `.md/.py/.bak/.log`, `.cs` outside `Scripts/Simulation|Client`. Staging
  refuses if any appear.

## Workshop description template (`--description-file`)

The Steam description defaults to the Summary (ModHub's behaviour). A longer one:

```
[b]Script mod - start Space Engineers 2 with -loadScripts[/b]
Steam > Library > Space Engineers 2 > Properties > Launch options: -loadScripts
Without it the game loads the mod's blocks but never compiles its scripts (no error is shown).

<what the mod does>

[h2]Install[/h2]
Subscribe, add it to the world's mods, start the game with -loadScripts.

[h2]Version[/h2]
<version> for Space Engineers 2 2.4.0.95. Changelog: <link>
```

## Notes and risks

- **Unofficial route.** Keen's tooling does not upload scripts; this replicates 2.4.0.95's private code paths
  (`WorkshopComponent`, `SteamUGCServiceComponent`, `SteamUgcMetadata`). Any game update can change the metadata
  schema, the KV tag, the staging or the load side: after an update re-read those classes (redecompile) and re-run a
  dry run; `$Bundles` in the metadata follows the installed game's version.
- The builder setup is `../build_mod_content.py`'s (a copy of Game2 + a stand-in Vanilla under
  `%LOCALAPPDATA%\OrbitalModBuild`) - don't run a staging and a `build_mod_content.py` build at the same time.
- Steam: the real run calls `SteamAPI.InitEx` as app 1133870 - Steam shows you "in game" for its duration. The dry
  run never initialises Steam. A failed upload after `CreateItem` leaves an empty item; it is reported, not deleted.
- `gameverdict.sh` compiles `Scripts/Simulation` only; a mod with `Scripts/Client` gets a warning to check it with
  ModCheck.
