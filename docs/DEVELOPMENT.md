# Aerodynamics Mod: development

The developer notes that used to sit in the README. Players: see the [wiki](https://github.com/xianson/se2-aerodynamics-mod/wiki).


### Where things are

- `Scripts/Simulation/`: the code. Each folder has a README. How it fits together:
  [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).
- `Assets/`: the sources the content build compiles (`Aero/` the blocks, `Particles/` the effects, `System/` the
  game-wide overrides). `Content/` is the compiled output.
- `Tests/`: offline suites. `tools/`: generators (`meshgen/` blocks, `particles/` effects, `blocks_merge/`, `modlist/`)
  and scripts.
- For other mods: [`docs/API.md`](docs/API.md) (the public `AeroApi`, version 8, and every setting).
- Engines: [`docs/PROPULSOR_SPECS.md`](docs/PROPULSOR_SPECS.md). Reflection inventory:
  [`docs/REFLECTION_AND_WHITELIST.md`](docs/REFLECTION_AND_WHITELIST.md). Block modelling:
  [`docs/MODELLING_RULES.md`](docs/MODELLING_RULES.md). History: [`CHANGELOG.md`](CHANGELOG.md).

### Testing

- **`tools/test_fast.sh`** (no game). Run it before every commit:
  - **ModCheck:** the game's exact script compile and whitelist analyzers (`tools/shared/ModCheck`; `MODCHECK=<a ModCheck.csproj>`
    overrides it - a private copy with its own `obj/` for parallel checkouts);
  - every suite in `Tests/` (AeroBench runs its `gate`: known shapes, table vs exact forces, table cache, wing
    repeatability, rebuild garbage, cache version vs sources). Suites include `SurfaceTests`, `StructureTests`,
    `TearTests`, `ThrustTests`, `RotorTests`, `RotorStrikeTests`, `SolidRocketTests`, `ExhaustTests`,
    `ControlSurfaceTests`, `SurfaceStallTests` (ships from `Tests/data/surfaces/*.boxes` + `*.ship`: `conv.py` there, from
    blueprints; skipped when absent), `GearTests`, `ShockGridTests`, `WingStrainTests`, `DebrisTests`, `SceneTests`,
    `SettingsTests`, `SettingsUiTests` (`-- dump` prints the settings menu), `ApiTests`, `BlockSaveTests`, `QuickLaunchTests`,
    `AutopilotSim` (the autopilot's flight simulator: `-- full` the sweeps, `-- list`, `-- <name>`, `csv <folder>` traces).
- **AeroBench modes** (`cd Tests/AeroBench; dotnet run -c Release -- <mode> <ships>`), e.g. `damage`, `impact`, `scale`,
  `cache`, `gcbuild` (`ALLOC=1` for per-type dumps), `polar`, `struct`, `fail`, `replay`, `tablehash`, `machwall`.
  Ships come from `Tests/data/*.boxes` (gitignored; exported in game with `aeroexport on`).
- **In game:** the Orbital Mod's harness (`Orbital Mod/tools/harness`) drives the game. `aeroget` / `aeroset
  Type.Member [value]` read and set any public static here by name (so public statics are never renamed);
  `aerocost on` writes `[AERO-COST]` lines to the log; `flightdump` (`tools/flightdump.sh`) dumps a grid for
  `Tests/ThrustTests`.
- **Logs:** a player's log gets the startup summary, settings, warnings, failures and structure breaks only. The
  per-grid / per-build lines (`[AERO-SIM]`, `[AERO-PROF]`, `[AERO-MERGE]`, table builds, scene adds) are behind
  `AeroLog.Detail`, on by default only where `%TEMP%\OrbitalMod\dev.flag` exists (the harness's flag); the periodic
  traces are behind `AeroLog.Verbose` (off). `aeroset AeroLog.Detail 1` any time.

### Content build

`tools/shared/build_mod_content.py` compiles `Assets` into `Content`. Each build keeps the previous Content as
`Content.before-<stamp>` (gitignored). It builds a staging copy holding only `Assets` (the game's builder would compile
`Scripts/` too, and fail), then copies the compiled Content back. Run it with the game closed (the game holds Content
open); it takes about 15 minutes:

    python tools\shared\build_mod_content.py "C:\Users\slob\Documents\SpaceEngineers2\Mods\Aerodynamics Mod"

The block generators (`tools/meshgen/gen_*_defs.py`) write into `Assets/Aero/...`; build after them. (`gen_autopilot_defs.py`
takes the mod root as its argument - the repository it is in by default; the others write the live folder.) The compiled
blocks (`Content/Aero/`, ~290 MB) are not in git, and for now neither are the block models and textures
(`Assets/**/*.fbx`, `*.png`, ~220 MB: [`docs/BLOCKS_MERGE.md`](docs/BLOCKS_MERGE.md) "Git"). The particles
(`Assets/Particles/`) come from `tools/particles/gen_*.py`; the game allows at most 4 keyframes over a particle's
life (more and the world fails to load).

### Force tables and their cache

- **Per frame, a lookup.** Each grid's forces come from a `ForceTable`: 486 flow directions, built in the background.
- **Damage updates.** A `ChunkedTable` (8 m chunks) updates the table locally after damage, without a full rebuild.
- **Disk cache** (`Drag/AeroTableCache.cs`, `%TEMP%\AeroMod\tables`, trimmed to `Perf.TableCacheMB`, 256 MB, least
  recently used first). Keyed by the grid's shape (block boxes in any order, block size, cell scale, face physics,
  engine boxes) and by the model's switches (`AeroTableCache.ModelSwitches`: add any new switch that changes faces,
  chunks or wings there). A reloaded world, a pasted blueprint or a copy loads its table in milliseconds.
- **The version rule.** Any change to how tables, chunks or wings are built must bump `AeroTableCache.Version`,
  otherwise players load stale tables. The gate hashes the table-building sources into `Tests/table_sources.txt`; after
  a bump (or a pure refactor of those files) record the new hash with `dotnet run -- tablehash` in `Tests/AeroBench`.

### Flight model defaults (2026-10-11)

On by default: `SubsonicSquareLaw`, `PressureRecovery` (`RecoveryPasses` 4), `ReynoldsSkin`, `GeometricCamber`,
`WingSystems`, low-aspect plates as wings (`ConnectedComponentWingDetector.LowAspectRatio` 0.5, at least 2 m², added
after the main pass), and since 2026-10-12 `FlowThroughEngines` (atmospheric engines' intakes are not walls).

- **Effect.** The in-game jetliner (273 t) cruises at L/D 7.8, up from 4.9; the bench gives 8.4 at 8°, up from 4.7.
- **Measuring it.** `tools/harness/aeroab.sh <tag>` in the Orbital Mod flies the A/B. `polar <ship>` in AeroBench prints
  the polar, with the pitching moment.
- **Still off:** `ColumnMode` (on the jetliner it found 60 % of the main wing and pitched the ship past 50°).
- **AeroBench env overrides:** `AERO_SQUARE` etc. `=0` gives the old behaviour.

### Flight effects

- **Files:** `Fx/AeroEntryFx.cs` (the published flow and entry heat; entry sources from other mods) and
  `Fx/FlightField*.cs` (plasma, vapour, tip trails, client). Particle access is by reflection (`Fx/EntryFxBridge.cs`,
  `FlightField.Render.cs`).
- **Heat is by Mach:** heat = `OnsetHeat` (M / `OnsetMach`)³, plasma from `Entry.OnsetMach` (2) to `Entry.FullMach`
  (~2.5), scaled down by density on a log scale (`AeroEntryFx.DensityScale`), lagged by `Entry.HeatLag` (2 s).
- **Other mods:** `AeroApi.RegisterEntrySource` lets another mod supply a grid's air velocity and glow (the Orbital
  Mod's railed re-entry).
- **Test knobs:** `aeroset AeroEntryFx.TestSpeed 2000` makes parked grids glow as if flying; `TestForces 1` computes (not
  applies) forces at that airflow.

### Performance notes

- **Quiet grids:** parked, unpiloted grids with no air past them do only their reads and a full step every 30 ticks.
- **Garbage:** rebuilds and damage updates reuse pooled tables. A Red Ship rebuild produces about 20 MB of garbage; no
  table allocation recurs.
- **Frame budget:** `AeroFrameBudget` caps force recomputes at `Perf.FrameBudgetMs` (1 ms) per frame; piloted and fast
  grids are exempt.
- **Background work:** `AeroWork` runs rebuilds, local updates and structure evaluations in priority order on
  `Perf.Workers` (2) workers.
