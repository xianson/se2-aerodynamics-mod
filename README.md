# Aerodynamics Mod

Real aerodynamics for Space Engineers 2. Every ship flies by its own shape: a sleek hull slips through the air, a brick
fights it, wings give lift and stall, the sound barrier pushes back, and re-entry heats the hull until it glows. The air
loads the ship's structure too, so pulling too hard can bend, crack and tear it apart. There is nothing to configure on
the blocks to make it work: the whole ship's shape counts, wings or no wings.

The mod also adds aircraft parts: control surfaces, retractable landing gear, airbrakes, turboprops, ducted fans,
helicopter rotors, ramjets and solid rocket motors (G menu, category **Aerodynamics**).

## For players

### Requirements

- **Space Engineers 2, version 2.4.x** (made and tested on 2.4.0.95).
- **Add `-loadScripts` to the game's launch options.** Steam > Library > Space Engineers 2 > right-click > Properties >
  General > Launch Options: type `-loadScripts`. Without it the game loads the mod's blocks and effects but never runs
  its scripts, and ships fly exactly as in the unmodded game.
- Add the mod to the world's mod list. It needs no other mod.
- **Optional: the Orbital Mod.** The two work together: the Orbital Mod's flight HUD shows this mod's stall and
  loss-of-control warnings, and its railed re-entries heat the hull like a real one. Without it everything else works.

### What it does

- **Drag and lift from the shape.** The exposed hull is measured and every face of it meets the air: pressure, shadowing,
  skin friction, rotation damping and supersonic wave drag. Wings are found from the blocks and give lift, with stall and
  Mach effects. Wind moves the air; ground effect near the ground. Damage changes the forces within a fraction of a
  second.
- **Flight controls through real parts.** Control surfaces deflect with your steering; the mod's attitude control flies
  your gyros, thrusters and surfaces together, knowing what the air is doing. Thrust acts where each thruster is, so an
  unbalanced ship turns.
- **Honest dampeners.** The game's dampeners hold a ship up with one weight of thrust against gravity the world doubles
  (its gravity multiplier, 2 in vanilla), so a ship that falls with dampeners off floats with them on, and still climbs
  with almost all its thrust. Here the dampeners hold with real thrust against the gravity the ship really feels: **a ship
  needs thrust for its full (doubled) weight to hover**, one short of it sinks slowly with dampeners on, and the thrust that
  holds it up is not there to climb with. The world's "super dampeners" option brakes with the thrusters' real thrust too
  (no 2-5x braking). `Physics.HonestDampeners` off (Flight model, Advanced) gives the game's own dampeners back.
- **Engines that care about speed.** Atmospheric thrusters lose thrust through the sound barrier and gain from ram air
  above it; jets flame out with the air coming from the side or behind. Turboprops are strong and slow, ramjets need
  supersonic speed, solid rockets burn to the end once lit, helicopter rotors lift and steer with the cyclic.
- **Structure and damage.** On the ship you fly, air, thrust, gyro and contact loads spread through the blocks. Overloaded
  sections wear, fatigue, bend, crack across and tear off, with creaks, groans, shake and smoke. Rotor and propeller
  blades are damaged by what they hit.
- **Re-entry.** Plasma, vapour cones and wingtip trails, hull heating with heat damage, and scorch marks where it got hot.
- **Warnings.** A stall warning from the whole ship's lift, and a predicted loss-of-control warning (shown by the Orbital
  Mod's HUD). The game's own "thrust overload" warning stays quiet while the wings carry the ship. Optional Mach readout.

### Settings

In a world: **pause menu > Settings > Mods** tab, section **Aerodynamics** (the tab only exists inside a world). Basic
settings are shown; each group has an **Advanced** section and a **Reset to defaults**. World settings (forces, heat,
structure, performance) are saved with the world and only the host / single player can change them; client settings
(effects, HUD, feel) are your own. The same settings live in text files, re-read while you play:
`%APPDATA%\SpaceEngineers2\ModSettings\AerodynamicsMod.world.cfg` (host overrides, shown locked in the menu) and
`AerodynamicsMod.client.cfg`. Everything can be switched off: `Aero.Enabled` off gives the game's own flight.

### Performance

- Small and medium ships: their drag table builds in milliseconds when the world loads or the ship changes.
- **Very large ships** (thousands of blocks, hundreds of tonnes) build theirs in the background for up to about a minute
  after the world loads or after a big change. Meanwhile they fly on a coarse quick table, so drag and lift feel rough
  for that first minute. The table is then cached on disk (`%TEMP%\AeroMod\tables`, at most 256 MB), so the next load
  of the same ship takes milliseconds.
- Parked ships with no air moving past them cost almost nothing. The heavier work (structure, rebuilds) runs on 2
  background workers and a 1 ms per frame budget (`Perf.Workers`, `Perf.FrameBudgetMs`).

### Known limits

- **Single player and hosting only.** SE2 2.4 runs the server inside the game; a remote client sees the game's own
  flight.
- The structure is simulated on the ships players fly, not on every grid.
- Texts are in English only.
- A world setting you change in the menu is stored with the world's next save.
- SE2 is in early access: a game update can break the mod until it is updated.

### Troubleshooting

- **The blocks are there but nothing flies differently, and there is no Aerodynamics section under Settings > Mods:**
  `-loadScripts` is missing from the launch options (see Requirements).
- **A huge ship handles oddly right after loading:** its table is still building (see Performance); give it a minute.
- **Reporting a problem:** include the game log, `%APPDATA%\SpaceEngineers2\Temp\Logs\` (the newest file). The mod's
  lines start with `[AERO`; warnings and failures are always logged.
- **A world made with the old companion mod "Aerodynamics Mod Blocks"** (before 2026-10-17): the blocks are now part
  of this mod. Remove the companion from the world's mod list (Load > the world > Mods); ships keep their blocks. Details:
  [`docs/BLOCKS_MERGE.md`](docs/BLOCKS_MERGE.md).

## Details

Layout: `Scripts/Simulation/` (the code), `Assets/` (the sources the content build compiles: `Aero/` the blocks,
`Particles/` the effects, `System/` the game-wide overrides), `Content/` (compiled), `Tests/` (offline suites), `tools/`
(generators: `meshgen/` blocks, `particles/` effects; `blocks_merge/`, `modlist/`).

Scripts: `Scripts/Simulation/` (each folder has a README). How it fits together: [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).
For other mods: [`docs/API.md`](docs/API.md). Reflection inventory: [`docs/REFLECTION_AND_WHITELIST.md`](docs/REFLECTION_AND_WHITELIST.md).
What changed: [`CHANGELOG.md`](CHANGELOG.md).

## Features in detail

- **Aerodynamic forces from the shape.** The grid's exposed surface (0.25 m cells, smoothed normals, sealed cavities
  ignored) gives per-face pressure with shadowing, lee-side pressure recovery, skin friction, rotation damping and wave
  drag, baked into a force table over 486 flow directions; wings are detected from the blocks and add lift with Mach
  effects. Wind moves the air (`Weather.Wind`). Ground effect near the ground. Damage updates the forces locally within
  a fraction of a second.
- **Control surfaces.** 1x1 and 3x3x1 surfaces (their terminal: axes, max deflection, trim) deflect from the pilot's
  input; vanilla hinges carrying flaps can be flown the same way (`Control.HingeSurfaces`, off by default).
- **Thrust torque and attitude.** Thrust acts at each thruster, not at the centre of mass: unbalanced thrust turns the
  ship. One aero-aware attitude law flies gyros, thrusters and surfaces toward the pilot's target, knowing the air's
  torque - no phantom torque. Flames show each thruster's share. (`Aero.ThrustTorque` off: the game's own thrust,
  untouched.)
- **Engines by speed.** Vanilla atmospheric thrusters lose thrust through the sound barrier (the Jet curve: 76 % at
  Mach 1), then gain from ram pressure above their rating (180 % at Mach 1.7) and fade fast past it, gone by 2.8 -
  applied to the game's own thrust, the game's air-density effect on top; hydrogen and ion stay flat. Air-breathers care where the air comes from: a jet keeps full thrust within
  28 deg of straight-in air, stalls to 15 % by 65 deg and flames out with the air from behind (0 from 125 deg); a
  ramjet unstarts past 10 deg and re-lights 1.5 s after the air is back within 6 deg; turboprops don't care. It
  fades in from 20 to 60 m/s true airspeed, so hovering and taxiing are untouched (`InletAngle`). A sleek hull meets a sharp transonic drag peak at Mach 1 and slips through
  to a supersonic cruise once past it; a brick stalls out subsonic. The mod's turboprops (the kings of thrust per kW up
  to ~Mach 0.6), helicopter rotors and Reversible Rotor (85 kN) have their own curves, by true airspeed and the local
  Mach. A main rotor's thrust is capped to what the ship can hold against its torque - reversible and
  sideways thrusters, gyros and control surfaces all count (the surfaces only with air over them).
- **Rotor strikes.** A turning helicopter rotor, Reversible Rotor or turboprop prop that hits something - terrain, another
  ship, its own ship's blocks, a player - takes blade damage by its blade speed and what it hit (rock worst, then metal, a
  person least): three full-speed rock strikes break the blades, a brush at low rpm only nicks them. Broken, it stops dead
  with no thrust and only a stub left; weld the block back to full integrity to repair the blades. The struck block takes
  real damage, a player in a turning disc dies (a barely turning one bruises) and is thrown, and the ship gets the strike's
  real kick plus the stopped rotor's angular momentum - skimming a hill throws a helicopter. Sparks and a crunch.
  (docs/PROPULSOR_SPECS.md "Rotor strikes".)
- **Ramjets** (4 sizes, the "Ramjets" group). Hydrogen-burning air-breathers for the supersonic band: no static thrust
  (boost them past Mach 0.5), 100 % of their rating at Mach 2.5-3.5 (1.5 x the vanilla atmospheric of their kit), gone
  past Mach 6 and in vacuum, and they unstart off the airflow. They draw hydrogen through conveyors like a hydrogen
  thruster, at 1/3 of a hydrogen thruster's hydrogen per kN, and only for the thrust they give (none below Mach 0.5).
  The inlet spike slides aft from Mach 1.6 to 3.2. (docs/PROPULSOR_SPECS.md "Ramjets".)
- **Solid rocket motors** (4 sizes, the "Solid Rockets" group). Stack segments of one size on a nozzle's top face. Set its
  power level BEFORE ignition with the nozzle's thrust override (50-100 %; unset = 100 %), then Ignite it from its terminal
  or a toolbar: it burns at that level (full = 2 x the vanilla hydrogen thruster of its size, +10 % in vacuum) until the
  stack is empty - about 8 s per segment at 100 %, longer at lower power (the same total impulse), no shutdown. Your
  thrusters, dampeners and flight controller never fire an unlit motor; once lit, the flight controller may vary it
  between 50 and 100 % to turn the ship (strap-on boosters steer by throttling differentially). Segments are tanks of a
  real "Solid Propellant" resource the nozzle draws through their own conveyor ports, which link only to solid rocket
  blocks of the same size, never to your conveyor network; they get lighter as they burn and are never refilled.
  (docs/PROPULSOR_SPECS.md "Solid rocket motor".)
- **Warnings.** A stall warning from the whole ship's lift curve and a predicted loss-of-attitude-control (LOAC)
  warning - shown by the Orbital mod's flight HUD. The game's thrust-overload warning is silenced while the wings carry
  the weight. Mach can be appended to the speedometer (`Hud.Mach`, off by default).
- **Re-entry.** Plasma, transonic vapour and wingtip trails around the hull (the flight field, client-side), hull
  heating with skin / core temperatures, heat-weakened structure and burn damage, and scorch decals where the hull got
  hot. A railed re-entry in the Orbital mod heats the hull as a real one.
- **Structure.** On the grid a player flies: air, thrust, gyro and contact loads spread over the blocks and solved;
  overloaded sections wear, fatigue (from tension, and from compression where thin walls buckle), fail and split; worn joints bend
  permanently and dent. A failing section cracks across over a fraction of a second - the pulled side tears first, the
  pushed side crumples and goes last as a hinge, along a jagged line - and a large piece peels off on several springs
  that let go in the crack's order. Creaks, groans and cockpit shake follow the load; smoke at torn edges. The flight
  controller never overloads its own ship.
- **Debris drag.** The loose chips a broken block throws off (the game's fracture debris, not grids) slow in the air:
  quadratic drag on their bounding box against the air and the wind (Cd 0.4), a little on their tumbling (`Debris/`).
- **Gear and airbrakes.** Retractable gear (0.5 / 1 / 1.5 / 2.5 m wheels) on the vanilla suspension, with doors and
  drag; 3x3x3 clamshell airbrakes that open with the parking brake, opening set by a terminal slider.
- **Settings.** In game: pause menu -> Settings -> **Mods** (the tab shared with the Orbital mod, section
  "Aerodynamics"; inside a world only). World settings (forces, entry heating, structure, performance) are saved with
  the world and can be changed by the host / in single player only; host overrides in
  `%APPDATA%\SpaceEngineers2\ModSettings\AerodynamicsMod.world.cfg` win and show locked. Client settings (effects, HUD,
  feel) in `AerodynamicsMod.client.cfg` next to it. Both files are re-read every ~2 s while playing. The full list, with
  ranges, menu labels and descriptions: `docs/API.md` (Settings), from `Runtime/AeroSettings.cs` (also readable through
  `AeroApi.GetSettings`).

  | Some world settings | Default | | Some client settings | Default |
  |---|---|---|---|---|
  | `Aero.Enabled` | on | | `Fx.Effects` (all aero effects) | on |
  | `Aero.DragMultiplier` / `LiftMultiplier` | 1 / 1 | | `Hud.StallWarning`, `Hud.Loac` | on |
  | `Aero.ThrustTorque` | on | | `Hud.GameStallWarning` (the game's own) | off |
  | `Physics.HonestDampeners` | on | | | |
  | `Structure.Loads`, `Structure.Damage` | on | | `Hud.Mach` | off |
  | `Structure.Fatigue`, `.Tear`, `.Bends`, `.SplitBlocks` | on | | `Feel.Creaks`, `Feel.Shake` | on |
  | `Heat.Damage` | on | | | |
  | `Perf.FrameBudgetMs` / `Perf.Workers` | 1 / 2 | | | |

- **Multiplayer.** Single player and a host only: SE2 2.4 runs the server in-process, and the client effects pair with
  the server's state in memory. A remote client sees the game's own flight.

## Developer notes

### Testing

- **`tools/test_fast.sh`** (no game). Run it before every commit:
  - **ModCheck:** the game's exact script compile and whitelist analyzers (`D:\aero\tools\ModCheck`);
  - every suite in `Tests/`: `AeroBench` (its `gate`: known shapes, table vs exact forces, table cache, wing
    repeatability, rebuild garbage, cache version vs sources), `SurfaceTests`, `StructureTests`, `TearTests`,
    `ThrustTests`, `ControlSurfaceTests`, `GearTests`, `ShockGridTests`, `WingStrainTests`, `DebrisTests`, `SettingsTests`,
    `SettingsUiTests` (`-- dump` prints the settings menu), `ApiTests`, `BlockSaveTests` (the blocks' saved data).
- **AeroBench modes** (`cd Tests/AeroBench; dotnet run -c Release -- <mode> <ships>`), e.g. `damage`, `impact`, `scale`,
  `cache`, `gcbuild` (`ALLOC=1` for per-type dumps), `polar`, `struct`, `fail`, `replay`, `tablehash`. Ships come from
  `Tests/data/*.boxes` (gitignored; exported in game with `aeroexport on`).
- **In game:** the Orbital mod's harness (`Orbital Mod/tools/harness`) drives the game. `aeroget` / `aeroset
  Type.Member [value]` read and set any public static here by name (so public statics are never renamed);
  `aerocost on` writes `[AERO-COST]` lines to the log; `flightdump` (`tools/flightdump.sh`) dumps a grid for
  `Tests/ThrustTests`.
- **Logs:** a player's log gets the startup summary, settings, warnings, failures and structure breaks only. The
  per-grid / per-build lines (`[AERO-SIM]`, `[AERO-PROF]`, `[AERO-MERGE]`, table builds, scene adds) are behind
  `AeroLog.Detail`, on by default only where `%TEMP%\OrbitalMod\dev.flag` exists (the harness's flag); the periodic
  traces behind `AeroLog.Verbose` (off). `aeroset AeroLog.Detail 1` any time.

### Force tables and their cache

- **Per frame, a lookup.** Each grid's forces come from a `ForceTable`: 486 flow directions, built in the background.
- **Damage updates.** A `ChunkedTable` (8 m chunks) updates the table locally after damage, without a full rebuild.
- **Disk cache** (`Drag/AeroTableCache.cs`, `%TEMP%\AeroMod\tables`).
  - Keyed by the grid's shape: block boxes in any order, block size, cell scale, face physics, engine boxes.
  - Also keyed by the model's switches (`AeroTableCache.ModelSwitches`). Add any new switch that changes faces, chunks
    or wings there.
  - A reloaded world, a pasted blueprint or a copy loads its table in milliseconds instead of rebuilding.
  - Trimmed to 256 MB (`Perf.TableCacheMB`), least recently used first.
- **The version rule.** Any change to how tables, chunks or wings are built must bump `AeroTableCache.Version`.
  Otherwise players load stale tables. The gate hashes the table-building sources into `Tests/table_sources.txt`;
  after a bump (or a pure refactor of those files) record the new hash with `dotnet run -- tablehash` in `Tests/AeroBench`.

### Flight model defaults (2026-10-11)

On by default: `SubsonicSquareLaw`, `PressureRecovery` (`RecoveryPasses` 4), `ReynoldsSkin`, `GeometricCamber`,
`WingSystems`, low-aspect plates as wings (`ConnectedComponentWingDetector.LowAspectRatio` 0.5, at least 2 m2, added
after the main pass), and since 2026-10-12 `FlowThroughEngines` (atmospheric engines' intakes are not walls).

- **Effect.** The in-game jetliner (273 t) cruises at L/D 7.8, up from 4.9; the bench gives 8.4 at 8 deg, up from 4.7.
- **Measuring it.** `tools/harness/aeroab.sh <tag>` in the Orbital Mod flies the A/B. `polar <ship>` in AeroBench prints
  the polar, with the pitching moment.
- **Still off:** `ColumnMode` (on the jetliner it found 60% of the main wing and pitched the ship past 50 deg).
- **AeroBench env overrides:** `AERO_SQUARE` etc. `=0` gives the old behaviour.

### Flight effects

- **Files:** `Fx/AeroEntryFx.cs` (the published flow and entry heat; entry sources from other mods) and
  `Fx/FlightField*.cs` (plasma, vapour, tip trails, client). Particle access is by reflection (`Fx/EntryFxBridge.cs`,
  `FlightField.Render.cs`): the particle API is outside the mod references.
- **Heat is by Mach:** heat = `OnsetHeat` (M / `OnsetMach`)^3, plasma from `Entry.OnsetMach` (2) to `Entry.FullMach`
  (~2.5), scaled down by density on a log scale (`AeroEntryFx.DensityScale`), lagged by `Entry.HeatLag` (2 s).
- **Particles** (`Assets/Particles/PlasmaField_*`, `PlasmaLight_*`, `Vapour_*`) come from `tools/particles/gen_*.py`. Compile with
  `D:\aero\tools\build_mod_content.py "<this mod>"`. The game allows at most 4 keyframes over a particle's life; more
  and the world fails to load.
- **Other mods:** `AeroApi.RegisterEntrySource` lets another mod supply a grid's air velocity and glow (the Orbital
  mod's railed re-entry).
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

### Content build

`D:\aero\tools\build_mod_content.py` compiles `Assets` into `Content`. Each build keeps the previous Content as
`Content.before-<stamp>`; those folders are gitignored. It builds a staging copy holding only `Assets` (the game's
builder would compile `Scripts/` too, and fail), then copies the compiled Content back - with the game closed (it holds
Content open), about 15 minutes:

    python D:\aero\tools\build_mod_content.py "C:\Users\slob\Documents\SpaceEngineers2\Mods\Aerodynamics Mod"

The block generators (`tools/meshgen/gen_*_defs.py`) write into this mod's `Assets/Aero/...`; build after them. The
compiled blocks (`Content/Aero/`, ~290 MB) are not in git; nor, for now, the block models and textures (`Assets/**/*.fbx`,
`*.png`, ~220 MB: [`docs/BLOCKS_MERGE.md`](docs/BLOCKS_MERGE.md) "Git").
