# Aerodynamics Mod for Space Engineers 2

Real aerodynamics for Space Engineers 2. Every ship flies by its own shape: a sleek hull slips through the air, a brick
fights it, wings give lift and stall, the sound barrier pushes back, and re-entry heats the hull until it glows. The
air also loads the ship's structure, so pulling too hard can bend, crack and tear it apart. There's nothing to set up
on your blocks: the whole ship's shape counts, wings or no wings. The mod also adds aircraft parts, from control
surfaces and retractable gear to helicopter rotors, ramjets and solid rockets.

**Version 1.0.0** · **[Wiki](https://github.com/xianson/se2-aerodynamics-mod/wiki)** ·
**Steam Workshop:** _coming soon_ <!-- TODO --> ·
**[Report a bug](https://github.com/xianson/se2-aerodynamics-mod/issues)**

## Features

- **Drag and lift from the shape:** every exposed face meets the air (pressure, shadowing, skin friction, rotation
  damping, supersonic wave drag). Wings are found from the blocks, with stall and Mach effects. Wind, ground effect, and
  damage that changes the forces at once.
- **Flight controls through real parts:** control surfaces move with your steering. Gyros, thrusters, surfaces and
  rotors fly the ship together, aware of the air. Thrust acts where each thruster sits, and nothing turns the ship by
  magic.
- **Engines by speed:** turboprops rule subsonic flight, vanilla atmospheric thrusters (jets) pinch through Mach 1 and
  gain from ram air above it, and ramjets own Mach 2-6. Jets and ramjets care about the air's angle.
- **Aircraft parts** (G menu > Aerodynamics > Aircraft Parts): control surfaces, retractable landing gear, clamshell
  airbrakes, turboprops, helicopter rotors with collective / cyclic and real torque, the Reversible Rotor, ramjets, and
  stackable solid rocket motors.
- **Structure and damage:** air, thrust, gyro and impact loads spread through the ship you fly. Overloaded sections
  wear, fatigue, bend, crack across and tear off, with creaks, shake and smoke.
- **Rotor strikes:** rotors and props are damaged by what they hit, and damage it back. Weld to repair.
- **Re-entry:** plasma, vapour cones, wingtip trails, hull heating with heat damage, and scorch marks.
- **Warnings:** a stall warning and a predicted loss-of-control warning (on the Orbital Mod's HUD), and an optional Mach
  readout.
- **Settings:** an in-game menu (pause menu > Settings > Mods), plus host override files.

## Requirements

- **Space Engineers 2, version 2.4.x** (made and tested on 2.4.0.95).
- **Add `-loadScripts` to the game's launch options.** Steam > Library > Space Engineers 2 > right-click > Properties >
  General > Launch Options: type `-loadScripts`. Without it the game loads the mod's blocks and effects but never runs
  its scripts, and ships fly exactly as in the unmodded game. (It turns scripts on for every mod you load.)
- No other mod is needed. **Optional:** the Orbital Mod, whose flight HUD shows this mod's warnings and whose railed
  re-entries heat the hull.
- **Single player or hosting only.** SE2 2.4 runs the server inside the game, so a remote client sees the game's normal
  flight.

## Install

- **Steam Workshop:** _coming soon_. Subscribe, then add the mod to your world's mod list.
- **Local:** unzip the release package from [Releases](https://github.com/xianson/se2-aerodynamics-mod/releases)
  <!-- TODO: confirm a release zip; the git tree alone lacks the compiled blocks --> into
  `Documents\SpaceEngineers2\Mods\Aerodynamics Mod`, then add it to the world's mod list.
- **Coming from "Aerodynamics Mod Blocks":** that companion mod is now part of this one. Remove it from the world's mod
  list before loading. Ships keep their blocks ([`docs/BLOCKS_MERGE.md`](docs/BLOCKS_MERGE.md)).

## Documentation (wiki)

| | |
|---|---|
| Start | [Getting Started](https://github.com/xianson/se2-aerodynamics-mod/wiki/Getting-Started) · [How Flight Works](https://github.com/xianson/se2-aerodynamics-mod/wiki/How-Flight-Works) · [Speed and Engines](https://github.com/xianson/se2-aerodynamics-mod/wiki/Speed-and-Engines) |
| Blocks | [Control Surfaces](https://github.com/xianson/se2-aerodynamics-mod/wiki/Control-Surfaces) · [Airbrakes](https://github.com/xianson/se2-aerodynamics-mod/wiki/Airbrakes) · [Landing Gear](https://github.com/xianson/se2-aerodynamics-mod/wiki/Landing-Gear) · [Turboprops](https://github.com/xianson/se2-aerodynamics-mod/wiki/Turboprops) · [Helicopter Rotors](https://github.com/xianson/se2-aerodynamics-mod/wiki/Helicopter-Rotors) · [Reversible Rotor](https://github.com/xianson/se2-aerodynamics-mod/wiki/Reversible-Rotor) · [Ramjets](https://github.com/xianson/se2-aerodynamics-mod/wiki/Ramjets) · [Solid Rocket Motors](https://github.com/xianson/se2-aerodynamics-mod/wiki/Solid-Rocket-Motors) · [Vanilla Thrusters in Atmosphere](https://github.com/xianson/se2-aerodynamics-mod/wiki/Vanilla-Thrusters-in-Atmosphere) |
| More | [Rotor Strikes](https://github.com/xianson/se2-aerodynamics-mod/wiki/Rotor-Strikes) · [Settings](https://github.com/xianson/se2-aerodynamics-mod/wiki/Settings) · [Troubleshooting and FAQ](https://github.com/xianson/se2-aerodynamics-mod/wiki/Troubleshooting-and-FAQ) · [For Modders](https://github.com/xianson/se2-aerodynamics-mod/wiki/For-Modders) · [Changelog](https://github.com/xianson/se2-aerodynamics-mod/wiki/Changelog) |

**Quick troubleshooting:** no **Aerodynamics** section under Settings > Mods means `-loadScripts` is missing. To report
a problem, attach the newest log from `%APPDATA%\SpaceEngineers2\Temp\Logs\` (the mod's lines start with `[AERO`).

---

## For developers

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
  - **ModCheck:** the game's exact script compile and whitelist analyzers (`D:\aero\tools\ModCheck`);
  - every suite in `Tests/` (AeroBench runs its `gate`: known shapes, table vs exact forces, table cache, wing
    repeatability, rebuild garbage, cache version vs sources). Suites include `SurfaceTests`, `StructureTests`,
    `TearTests`, `ThrustTests`, `RotorTests`, `RotorStrikeTests`, `SolidRocketTests`, `ExhaustTests`,
    `ControlSurfaceTests`, `GearTests`, `ShockGridTests`, `WingStrainTests`, `DebrisTests`, `SceneTests`,
    `SettingsTests`, `SettingsUiTests` (`-- dump` prints the settings menu), `ApiTests`, `BlockSaveTests`.
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

`D:\aero\tools\build_mod_content.py` compiles `Assets` into `Content`. Each build keeps the previous Content as
`Content.before-<stamp>` (gitignored). It builds a staging copy holding only `Assets` (the game's builder would compile
`Scripts/` too, and fail), then copies the compiled Content back. Run it with the game closed (the game holds Content
open); it takes about 15 minutes:

    python D:\aero\tools\build_mod_content.py "C:\Users\slob\Documents\SpaceEngineers2\Mods\Aerodynamics Mod"

The block generators (`tools/meshgen/gen_*_defs.py`) write into `Assets/Aero/...`; build after them. The compiled
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

## Licence and credits

<!-- TODO: no LICENSE file in the repo yet - add one, or state the terms here. -->
By xianson. <!-- TODO: credits (modeller, testers, the Orbital Mod) -->
