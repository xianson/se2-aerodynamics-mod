# The Blocks mod folded in (2026-10-17)

Until 2026-10-17 the blocks lived in a companion mod, **Aerodynamics Mod Blocks** (project GUID
`483980e4-2c19-4ef2-8274-c58f3354c645`, never published, not a git repo). The user approved folding it back into this
mod (`de164d94-7a67-4832-81e4-34c06c6a65f1`). Phase A (files and code, done while the game ran) is in this repo; phase B
(the content build and the in-game checks, game closed) is `tools/blocks_merge/phase_b.sh`.

## What moved

`tools/blocks_merge/merge_blocks_assets.py` copied the Blocks mod's `Assets/` here, path for path: 3955 files, 222 MB.

| Where | What |
|---|---|
| `Assets/Aero/Airbrake`, `Airbrake3x3x3` | airbrakes 1x1 and 3x3x3 (large / small) |
| `Assets/Aero/ControlSurface`, `ControlSurface3x3` | control surfaces 1x1 and 3x3x1 (large / small) |
| `Assets/Aero/RetractableGear` | gear 0.5 / 1 / 1.5 / 2.5 m |
| `Assets/Aero/Propulsors` | turboprops, ducted fans, helicopter and tail rotors, ramjets, solid segments and nozzles, their materials and exhaust effects (166 MB) |
| `Assets/Aero/Scoop`, `Menu` | the scoop; the build-menu groups |
| `Assets/System` | the overrides of `PhysicsSessionConfiguration`, `ThrustClassesConfiguration` and the four hydrogen thrusters |

- **GUIDs unchanged.** Every `.meta` is copied byte for byte (the generated ones are uuid5 of the Assets-relative path, so
  the paths stay the same too), every definition keeps its GUID: 2149 ids, none shared with this mod's 347. Saved blocks
  find their definitions by GUID, and the scripts inject their components by prefab GUID (`Components/AeroBlockGuids.cs`,
  the generated `*.g.cs`) - no script change.
- **The one overlap:** both mods had `System/Override of CharacterPhysics.partialdef` (the same `MaxVelocity 1000`, each
  with its own asset id); this mod's is kept. `Assets.meta` / `System.meta` are this mod's.
- **Not copied:** the Blocks mod's `Content/` (rebuilt here by phase B), its `.vrgproj` / `ModProject.modhub-metadata`
  (this mod's project stays the one), its SDK caches.
- `Assets/System/Thrusters/thrusters.meta` came along as it was: a stray folder meta inside its own folder (the builder
  warns "loose metafile"); a deletion candidate - not deleted.

The generators write here now: `tools/meshgen/gen_gear_defs.py` `BLOCKS` (also used by `gen_airbrake_defs.py` and
`gen_propulsor_defs.py` as `gd.BLOCKS`) and `gen_cs3_defs.py` `BLOCKS` point at this mod; `ROOT_REL` / `EXHAUST_REL` are
unchanged. The offline content checks (ExhaustTests, SolidRocketTests, ThrustTests) read `Assets/Aero/Propulsors` here.

## Git

Committed: the text and small files of the blocks - `.def`, `.partialdef`, `.meta`, `.model`, `.gltf`, `.comptex` and the
`.bin` meshes, 3266 files, 4.9 MB. Ignored: `Content/Aero/` (the compiled blocks, ~290 MB - rebuilt from Assets, like the
Orbital Mod's `Content/Sensors/`).

**Pending the user's decision:** the 640 `.fbx` (187 MB) and 49 `.png` (31 MB) are NOT committed - over the 50 MB line
for one change (the Orbital Mod keeps its big binaries out of git: 37 Assets files committed of 1.2 GB; this repo's
largest so far is 2.6 MB of particle Content). They sit untracked in the mod folder. Their sources are the Blender exports
under `D:\Blender Brojects\SE2\*\Export` (the generators copy them in), so they can be regenerated as long as those exist.
The options:

1. **Commit them anyway** - one 220 MB commit, the repo grows by that (and by every re-export: fbx do not delta well).
   Simple; clones and worktrees get the full mod.
2. **Git LFS** for `*.fbx` / `*.png` under `Assets/` - small repo, full mod on checkout; needs `git lfs install` and a
   remote with LFS (none today: the repo is local).
3. **Keep them outside git** (today's state) - add `Assets/**/*.fbx` and `Assets/**/*.png` to `.gitignore`; the live mod
   folder is the copy of record, backed up with the Blender exports; a worktree or clone needs
   `merge_blocks_assets.py` (from the Blocks folder, while it exists) or a re-run of the generators.

## Existing worlds

How the game handles it (decompiled 2.4.0.95):

- A save lists its mods in `modlist.vrb` by **project GUID** (+ ModId, ServiceId; the name is for humans) - the mods
  that were loaded when it was saved (`GameDataLoaderComponent.ConfigureSession`, `WorldSessionComponent`).
- A listed **local** mod the game cannot find stops the load: `LocalProjectLocator` throws "Couldn't find the project",
  the world does not load (a generic "loading failed" dialog, back to the menu). No missing-mod warning first.
- Both mods loaded together would mount the same resource ids twice; the content cache adds them to a dictionary and
  throws (`ContentCache.LoadContentCacheData`; from the code, not run) - so a world must not list both once this mod's
  Content has the blocks.
- A saved block whose definition no loaded mod has is dropped with a warning
  `[DefinitionManagerDefinitionSerializationContext] Could not find <guid>` and a "lost blocks" notice; the grid stays.
  With the same GUIDs in this mod nothing is lost - phase B checks it.

The 7 worlds that list the Blocks mod today (with this mod): Concordia Research Facility 6 and 7, Orbital Test Campaign
New, Orbital Test SSTO Blocksave, Orbital Test Tu-134 Control Surfaces, Orbital Test World 2, Quick Start Survival 1 1.

To migrate one (after phase B's build):

- **In game:** Load -> the world -> Mods: untick "Aerodynamics Mod Blocks", load. The game writes the new list into the
  save (`ContainerModManager.WriteChangesToContainerAsync`).
- **Offline:** `tools/blocks_merge/phase_b.sh worlds` lists them; `APPLY=1` backs each up into
  `D:\aero\save_backups\blocks-merge-<stamp>\` and drops the Blocks mod from its list with `tools/modlist`, which writes
  the edited list as JSON to the save's extension-less `modlist` - the file the game reads first (`modlist`, then
  `modlist.vrb`, then `modlist.bak.json`); `modlist.vrb` is left as it was, and the game's next save replaces both with a
  new `modlist.vrb`.

Then the Blocks mod's folder can leave `Documents\SpaceEngineers2\Mods` (moved, not deleted - the user's call). While it
is there and a world lists it, that world loads both.

## Phase B (game closed)

`tools/blocks_merge/phase_b.sh STEP`, in order - each prints PASS / FAIL lines and a summary; the launch steps are under
5 minutes each and refuse while a game runs:

    B=tools/blocks_merge/phase_b.sh
    $B check                        # phase A merged into the live repo, no game, the tools build
    $B assets                       # the .fbx / .png into the live Aero Assets; Aero's definitions recorded
    SET_ARGS=1 $B baseline          # launch 1, OLD content: copy the Tu-134 world -> "Orbital Test Tu-134 Merged", dump it
    $B build                        # ~15 min (run_in_background): build_mod_content.py on the live Aero mod
    $B verify                       # every Blocks definition (746) and compiled file (923) in the new Content
    $B migrate                      # the copy without the Blocks mod in its list
    SET_ARGS=1 $B after             # launch 2, NEW content: no "Could not find", every grid's blocks as in the baseline
    SET_ARGS=1 $B place             # launch 3: every one of the 42 blocks spawned alone in a fresh world (gen measure)
    $B worlds                       # the user's worlds still listing the Blocks mod; APPLY=1 only with the user's go

`SET_ARGS=1` puts the step's world into the `-start:` of `D:\aero\se2-launch-args.txt` and restores the file on exit.
Expected: about 1091 definitions after the build (this mod's 346 + the Blocks mod's 746, one vanilla base both list).
The build is the first of this project with the blocks in it (~925 source definitions: particle effects, a new resource
type, tanks, conveyors): plan for builder errors; `%LOCALAPPDATA%\OrbitalModBuild\build-Aerodynamics_Mod.log` has them
(and see the bundle-stamp note in the SE2 content pipeline memory).

## Phase B done (2026-10-18)

- `check`, `assets` PASS (0 copied, 3954 identical; the two G-menu .defs differ on purpose - 3d3ee45 renamed them here).
- `baseline` (Tu-134 copy, OLD content): 297 grids / 43 names, 120996 blocks, 1751 aero blocks, 0 "Could not find".
- `build`: builder exit 301 (warnings), 0 failure lines, 1269 files; `verify`: 1090 definitions - every one Aero had
  (346), every compiled Blocks file (923); of the Blocks mod's 746 definitions all but its own `Override of
  CharacterPhysics` (the documented overlap: this mod's copy is kept).
- `after` (NEW content, migrated copy): no "Could not find", the same 297 grids, every grid name's blocks identical; the
  aero dump the same but for one name the baseline's dump had missed a grid of (scene membership at that moment).
- `place`: all 42 blocks spawned alone, each with mass and cells.
- `worlds` APPLY=1: the 7 worlds migrated (backups `D:\aero\save_backups\blocks-merge-20261018-124221\`); the Blocks
  mod's folder moved to `Documents\SpaceEngineers2\ModsRetired\Aerodynamics Mod Blocks` (merge_blocks_assets.py's
  source, if ever needed again). A migrated world (Orbital Test Tu-134 Control Surfaces) loads with every block.
- `%APPDATA%\SpaceEngineers2\Temp\LocalMods\ModIndex` (ModHub's local-mod index) still names the old folder: the game
  logs "Discovered local mod 483980e4..." and only adds its parent (the Mods folder) as a search path - harmless.

## Publishing

ModHub publish (2.4.0.95) does not copy `Scripts/` (a research note; the publish path for script mods is open). For
when it is: the folder is one project now; it still needs a `thumb.jpg` at its root, and the `.vrgproj`
`ProjectDirectories` / `ReferencedAssembliesSearchPaths` hold this machine's absolute paths (Steam, the SDK) - clear them
for a published copy.
