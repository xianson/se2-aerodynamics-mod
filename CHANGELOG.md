# Changelog - Aerodynamics Mod

Numbers below come from the commits' own measurements (AeroBench on the dumped ships, the offline suites); nothing here
was flown in game for this release. The commit hashes are on the integration branch.

## 1.0.0 - first public release (for players)

The first Workshop release. In plain words, what you get:

- **Ships fly by their shape.** Drag, lift, stall, the sound barrier and supersonic flight come from the hull you built;
  wings are found from your blocks. Wind and ground effect count, and damage changes how a ship flies at once.
- **Aircraft parts** in the G menu (Aerodynamics): hinged control surfaces, retractable landing gear in four wheel sizes,
  clamshell airbrakes (open with the parking brake), turboprops, ducted fans, helicopter main rotors with cyclic
  steering, reversible rotors, ramjets in four sizes, and solid rocket motors you stack from segments.
- **Engines by speed.** Atmospheric thrusters lose thrust through Mach 1 and gain from ram air above it; jets flame out
  with the air from the side or behind; ramjets only work supersonic.
- **A structure that can break.** Air, thrust and impact loads run through the ship you fly; overloaded parts wear,
  fatigue, bend, crack across and tear off, with sound, shake and smoke. Rotor blades break on what they hit; weld the
  block to repair them.
- **Re-entry.** Plasma, vapour and wingtip trails, hull heating with heat damage and scorch marks.
- **Warnings** for stall and coming loss of control (on the Orbital Mod's HUD), and an optional Mach readout.
- **Settings menu** under pause menu > Settings > Mods, plus text files for hosts.
- **Honest dampeners** (2026-10-18, `Physics.HonestDampeners`, on by default). The game's dampeners cancel the world's
  doubled gravity (GravityMultiplier 2) with one weight of thrust, so a ship that cannot fly with dampeners off floats with
  them on and climbs with nearly all its thrust. Now the hold is real thrust against the real gravity, within the
  thrusters' maxima: a ship needs thrust for twice its planet weight to hover (thrust 2.5x: it hovers at 80 % and climbs
  at 0.5 g; the game's 1.5 g), one at 1.5x sinks at 0.5 g with dampeners on (the game's: hovers, climbs at 0.5 g), and the
  world's super dampeners no longer brake at 2-5x the thrusters' real thrust (from 100 m/s at 5 m/s2: 1000 m, the boost's
  379 m). Off: the game's own. (Tests/ThrustTests `HonestDampChecks`.)
- **Wings lead with the edge facing the air** (2026-10-18, fix a-wing-edge). A wing's leading edge was decided by the
  direction the ship was built along, not the one it flies: a ship flying toward the grid's + axis along its chord (the
  jetliner, nose +X) had its wing lift at 3/4 chord from its real leading edge, its transonic tuck forward instead of aft,
  and its sweep measured from the trailing edge. Now each wing takes its leading edge from the air every tick. AeroBench,
  jetliner at 4 deg: main wing lift point +2.6 m forward at Mach 0.5; Mach 0.5 -> 1.2 it now moves 2.0 m aft (it moved
  2.0 m forward); main wing lift -7 % at Mach 0.5 (its real, more swept leading edge). Ships built nose toward the grid's
  - axis (the Pelican, the F-6) keep their lift points; their wing lift moves 1-4 % (a merged wing's sweep is now the
  same whichever way the ship is built). Saved force tables rebuild once (cache version 21).
- **Wings have wave drag, and supersonic lift costs what it should** (2026-10-18, fix a-wing-wave). Wings had no wave
  drag of their own - only the hull's, by its streamlining - and their drag due to lift assumed full leading-edge
  suction at every speed, so a thick straight wing crossed Mach 1 as cheaply as a thin swept one and supersonic lift came
  about 3x too cheap. Now each wing's drag rises past its drag-divergence Mach (Korn: lower for a thick wing, higher for
  a swept one or at less lift), peaks near Mach 1 and falls supersonic (Lock, then Ackeret on the Mach across its leading
  edge); what the hull already charges on the wing's edge faces comes off (a square-edged wing's edges already pay more
  than the theory: nothing added; a stepped one's almost nothing until Mach 1.6: the theory added). Its drag due to lift
  goes from CL^2/(pi e AR) to CL x alpha as the leading-edge suction is lost through the transonic band. AeroBench, the
  F-6 (straight wings, t/c 0.12-0.19): T/W to break Mach 1 at sea level 1.94 -> 3.36 (0.4 kg/m3: 1.46 -> 2.53), still
  on to Mach 1.7 on that thrust; drag at wing CL 0.5, Mach 1.5 x1.79; the Tu-134 at Mach 0.79 x1.25; subsonic below
  each wing's critical Mach unchanged. Both switch off for the A/B (`CompressibleWingModel.WingWaveDrag`,
  `SupersonicLiftDrag`). Saved force tables rebuild once (cache version 22; the tables themselves are unchanged).

Good to know:

- Add **`-loadScripts`** to the game's Steam launch options, or the mod's scripts never run.
- The first time a world loads, every ship builds its drag table once; very large ships take up to about a minute and
  fly roughly until it is done. After that the table is cached on disk.
- Single player and hosting only for now; English only.
- Your game log stays quiet: the mod writes its startup summary, settings, warnings, failures and structure breaks only.

## Unreleased (2026-10-15 cleanup)

A big cleanup of the scripts (feature folders, no behaviour change of its own), plus an in-game settings menu, organic
structural failure, drag on debris, AeroApi versions 7 and 8 and about 35 bug fixes, several of which change how ships
fly.

### One mod: the blocks are in the Aerodynamics Mod (2026-10-17)

- The companion mod **Aerodynamics Mod Blocks** is folded in: its block definitions, models and textures (control
  surfaces 1x1 and 3x3x1, retractable gear, airbrakes 1x1 and 3x3x3, the propulsors - turboprops, ducted fans, rotors,
  ramjets, solid rockets - the scoop, the build-menu groups) and its game-wide overrides (`PhysicsSessionConfiguration`,
  `ThrustClassesConfiguration`, the four hydrogen thrusters) are now this mod's `Assets/Aero/` and `Assets/System/`.
  Every definition and asset keeps its GUID, so ships keep their blocks. (The `CharacterPhysics` override both mods had
  is kept once.)
- **Existing worlds:** a world that lists "Aerodynamics Mod Blocks" does not load once that mod's folder is gone (the
  game refuses a world whose local mod it cannot find), and should not load both (the same content twice). Untick
  "Aerodynamics Mod Blocks" in the world's mod list (Load -> the world -> Mods) before loading it, or use
  `tools/blocks_merge/phase_b.sh worlds` - [`docs/BLOCKS_MERGE.md`](docs/BLOCKS_MERGE.md).
- For modders: the block GUIDs are unchanged; a mod that listed "Aerodynamics Mod Blocks" as a dependency now needs only
  the Aerodynamics Mod.

### New features

- **Settings in game.** Pause menu -> Settings -> **Mods**: one tab shared with the Orbital Mod, this mod's section
  "Aerodynamics". Built from the settings themselves: basic settings shown, each group's advanced ones under a collapsed
  "Advanced", a switch, a choice or a slider (with value and unit) per setting, the description in the screen's panel,
  "Reset to defaults" per group. World settings can be changed only by the host / in single player (read-only with the
  reason otherwise), and one the host's `AerodynamicsMod.world.cfg` forces is locked with the file's path. A changed world
  setting is saved with the world's next save (the tab says so). Only inside a world (mods do not run in the main menu);
  if the tab cannot be added, one toast per session points to the `.cfg` files. (`Ui/`, 7837998)
- **19 more settings** you can now change (defaults unchanged): `Aero.GroundEffect`, `Aero.UpperAtmosphere`,
  `Control.ThrustTurnRate`, `Control.RespectStructure`, `Structure.Dents`, `Structure.GyroLoads`, `Structure.Contacts`,
  `Structure.GearSturdiness`, `Structure.Rate`, `Heat.Scorch`, `Heat.ScorchSpots`, `Feel.CreakFrom`, `Fx.VapourCone`,
  `Fx.Brightness`, `Fx.ScorchVisible` (hide scorch marks on this PC; marks already placed stay), `Fx.ThrusterFlamesFollow`,
  `Fx.DebrisScale`, `Debris.Drag`, `Debris.DragScale`. Every setting now has a player-facing label, group and tier.
  (897b0e1, 8b5be02)
- **12 settings for how a ship breaks** (advanced, world; defaults unchanged): `Structure.Crack`, `Structure.CrackSeconds`,
  `Structure.CrackSmokes`, `Structure.Jagged`, `Structure.JaggedShare`, `Structure.CrushUtil`, `Structure.CrumpleDepth`,
  `Structure.HingeToCompression`, `Structure.CompressionFatigueShare`, `Structure.BucklingFatigueShare`,
  `Structure.TearAnchors`, `Structure.TearPeelFirst`. (b6523e6)
- **Organic structural failure.** When a section of the ship fails it no longer lets go as one flat, instant cut
  (WHEN it fails is unchanged):
  - **The crack runs across the section** over 0.3 s (`FractureMath.CrackSeconds`): the pulled (tension) side tears
    first, the pushed (compression) side is the **hinge** and lets go last; members pushed past twice their strength
    (`CrushUtil` 2) are crushed. Creaks and jolts follow each release; 3 smoke puffs along the crack.
  - **A jagged fracture line**: each member breaks on the plane or a block either side of it, from its stress and a seed
    of the grid and cell (repeatable), never leaving a bridge across the cut.
  - **The hinge crumples**: its block stays as a stub, dented 0.15 m toward the cut (at most 0.25 m with other dents).
  - **Torn-off pieces peel**: a large piece hangs from up to 6 springs spread over the failed section, each let go in the
    crack's order (the crack's origin at 0.35 of `Structure.TearSeconds`, the hinge last) - it swings on its last
    ligament instead of the whole hold fading at once.
  - **Bends fold to the compressed side** (about the most pushed member's edge).
  (7de47c5, 65acb1f)
- **Tension vs compression in fatigue.** Fatigue now comes from a member's tension (and shear) at the full rate; what
  its compression adds depends on how it fails in compression. A **buckling-limited** member - a thin light-armour wall,
  most governing members of real ships - tires at the full rate (`Structure.BucklingFatigueShare` 1: a plate that buckles
  and straightens every pull cracks at its edges); only a **stocky**, yield-limited member tires at 0.2 of it
  (`Structure.CompressionFatigueShare`). Real ships therefore fatigue as before (AeroBench, 40 pulls at 0.8: worst health
  lost 0.142 on Plane2, 0.139 on the Tu-134; with every compression at 0.2 it was 0.028 - ~5x slower, the user's
  decision 2026-10-15 to keep the full rate). Yield wear past 1 is unchanged. (7de47c5, 078ab3a)
- **Debris drag.** The loose chips a broken block throws off (the game's fracture debris, not grids) had no air force but
  the game's wind push (none in still air). They now get slight quadratic drag on their bounding box against the air
  (`Debris.DragScale`, Cd 0.4 by default), the wind taken off and the game's own wind push counted once, plus a little
  drag on their tumbling. Solved exactly over the step (never reverses). Client side only (the pieces exist only there).
  (8b5be02, 622e0d0)
- **Scorch marks per material.** A heat shield no longer scorches dark far below its limit: the light / dark thresholds
  now follow each block class's own heat limits. Armour and trim 700 / 950 K (unchanged, still `HeatScorch.LightK` /
  `DarkK`), heat shield 1400 / 1800 K, hydrogen thruster 1077 / 1400 K, atmospheric thruster 862 / 1100 K, ion thruster and
  mounts 700 / 925 K, functional blocks 646 / 850 K. (5ca86b1)

### Changed behaviour you will feel

- **One thrust path (2026-10-17 legacy clean-up).** The old per-thruster path that ran with `Aero.ThrustTorque` off -
  Mach-scaled thrust impulses, its own thruster overrides and a phantom attitude torque - is gone. `Aero.ThrustTorque`
  off now leaves the game's own thrust untouched (applied at the centre of mass, the game's gyros and dampeners, no Mach
  curve on the vanilla atmospherics); `GetThrusters` then reports effectiveness 1. A helicopter rotor's reaction torque
  is always on (the never-used built-in anti-torque fallback, `RotorCyclic.YawArm` / `Reaction`, went with it).
- **Block settings are saved with the block.** A control surface's axes, max deflection and trim, a gear's down / up and
  its **Airshock** switch (new: it was not saved before), an airbrake's max opening and a thruster's attitude share now
  live in each block's own saved data instead of lines matched to blocks by world position. They survive a grid that
  moved between save and load and come along with blueprints, copy-paste and splits. Old saves load as before (their
  lines are still read for this release) and convert on their first save. A world saved with the mod still loads with
  the mod removed (only the game's own data types are used). (`Blocks/AeroBlockSave.cs`)

- **Wing detection fixed - bigger planes have bigger wings.** Coplanar wings were merged using another wing's span
  (from four wings on). Pelican main wing span **17 -> 23 m** (its real width), aspect ratio 3.2 -> 5.8: lift at 6 deg
  276 -> **366 kN (+33%)**, stall at ~8 deg instead of ~10, peak L/D 4.9 -> 5.1. Jetliner: its two fin skins are one fin
  now (lift and drag within 1 kN; fin stall 25 -> 43 deg). Diamondback tail span 7.5 -> 8.2 m (lift +-2%). Bluefighter,
  Red Ship unchanged. (67b1730)
- **Every solid of a grid is hull.** Parts that meet only at a corner, or blocks held by mount points with no cell
  touching, were culled as sealed cavities (no drag, never wings). Diamondback zero-lift drag 42.5 -> **48.2 kN (+13%)**,
  L/D at 10 deg 4.4 -> 3.9, lift unchanged; Bluefighter, Pelican, Jetliner, Red Ship within 1 kN. (772c184)
- **Damage no longer ~doubles the forces.** After damage, a chunked ship's local force updates were off by 81-121%
  (median) until the full rebuild after 5 s of quiet; now 0.4-9% (Bluefighter, Pelican; Red Ship half gone 27.8 ->
  16.9%). No more drag jump and pitch / yaw kick when a ship takes damage in flight. The rebuilt chunks also keep the
  ship's own skin friction (~1-2% of total drag). (e3feca4, 86073c1)
- **No fake thrust couple.** Thrusters were placed 0.125 m off on every axis: a symmetric ship got ~250 kN m of pitch and
  yaw per 2 MN of thrust from nowhere. Thrust now acts at the block centre as the game has it; build hints' thrust
  centres moved the same. (3e4abfa)
- **Pieces keep their spin.** A grid's first aero frame zeroed its rotation: a piece breaking off a tumbling ship, a
  pasted or re-added grid and every world load lost their spin. They keep it now (the gyros / dampeners stop it as any
  spin). (432ce85)
- **Unpiloted ships only damp their rotation.** A ship left unpiloted (pilot out, a drone, a broken-off piece with
  surfaces) no longer has its control surfaces pull it back toward the attitude it was left at (up to ~57 deg). (4fb3e0d)
- **Build hints' top speed for atmospheric ships is higher (the real one).** The reports applied the Turbofan curve
  (x0.85 / 0.7 / 0.6 / 0.45 at Mach 0.3 / 0.6 / 0.8 / 1.0) that the default flight path does not apply. Flight itself is
  unchanged. (a578ac4)
- **Welding repairs fatigue again; grinding and weapon damage count.** The fatigue ledger stopped seeing new health
  readings after the first: a weld never reached it (the block stayed worn) and outside damage was ignored. (1457cf7)
- **Blocks fail at their own destruction threshold.** Wear stopped at 0.3 while a block with a higher definition
  threshold was removed by the game first - no split, no smoke, no tear. Matters for modded blocks (every vanilla block is
  0.3). (cc3361b)
- **Heavy-armour wing roots are heavy.** The wing-strain path (`Structure.WingStrain`, off by default) gave every
  heavy-armour root light armour's strength. (06caa22)
- **The plasma's hull light only with the plasma on** (`Fx.Plasma` off, `Fx.SpotLight` on still lit the hull). (7a4c6eb)
- **First load rebuilds every ship's force table once**: the table cache version went 17 -> 18 (wings, chunk recovery and
  skin friction are now saved in it). (67b1730, e3feca4, 86073c1)

### Fixes

**Flight model**
- A pooled table builder's flow-through masks no longer leak into the next ship (random faces lost drag, or wing skins
  counted twice; off by 14.3 N/Pa in the bench). (7a18a0c)
- Local updates after damage use the full build's occlusion rule and pressure recovery. (e3feca4)
- A grid removed and re-added mid-build (teleport, scene move) no longer flies on the old shape's forces. (d7b144a)
- A failed background build's fallback wings have their real size (were 2x span, ~4x lift). (e67e5cf)
- Table workers give their thread its priority back (the game's own pool work starved under load). (9637efb)
- `AeroEntryFx.DensityScale` never returns NaN (a NaN entry glow that never cleared). (b74fdfa)
- The debug overlay's lift-to-weight uses the planet's gravity, not 9.81. (dfccf76)

**Thrust and attitude**
- The legacy thrust path (`Aero.ThrustTorque` off) no longer shares per-grid results between grids running in parallel
  (a phantom spin from another grid). (93bea0d)
- Lazy engine lookups publish their result before their ready flag (a lost thrust-override write or a frame without
  ground height on a session's first frames). (083e74a)
- Dev test-turn knobs reset on a new world; a harness's zeroed gyro torque is restored per grid. (bff2e5b)

**Structure**
- The air's body load is shared over the loaded faces only (half of it could vanish; none on a block gave NaN). (fd6be07)
- Thrust loads pair with the current thruster list (one evaluation after a thruster change could load the wrong
  blocks). (5a23431)
- A contact-load fault turns contact loads off for 2 s (doubling to 60 s), not for the session. (5b55db3)
- A grid split ends only the watched cuts it separated (a second failed cut could hang on); no array allocated per cell
  in the bridge scan. (81562c6)
- Carried hull heat is keyed by place and the whole normal (two faces could swap temperatures after a rebuild). (753eecb)

**Effects**
- Scorch marks are stamped from the effects job twice a second, with the flight field off or out of range too; a fault
  waits 10 s instead of ending scorch for the session. (9bb287f)
- Plasma bake knobs and `FlightField.Rebuild` work again (a bake-level change gave IndexOutOfRange: no plasma). (3679bd1)
- Each wing-tip trail rate-limits its own updates (one tip's 3 s limit held the others). (1a67ec3)

**API and the Orbital link**
- `GetSurface`, `GetThrusters`, `GetAeroBlocks`, `GetAeroBlockState` read published copies or copy under the list's lock
  (torn lists or ~200 ms exceptions before). (c6f4cd3)
- The calls into the Orbital Mod bind to its last loaded copy and bind again after an Orbital reload (they kept the stale
  copy); a throwing or misshapen member waits 10 s, doubling to 300 s, instead of being retried every tick. (a689d62,
  8bd6baf, 7230118)
- Caches that kept removed grids alive are pruned (suppressed grids, thrust probes, design-probe caches, tear smoke on a
  dedicated server). (688ed8f)
- Structure events are recorded only while someone polls: from a call until 10 s after the last one; the ring then
  clears, and is cleared at a world unload (it held up to 512 destroyed grids' and blocks' references into the next
  world). A failure not kept still takes its number, so a reader back after a pause sees the gap. (0adfa8a, 8acc2d0)
- Version-7 records each have their own watch window (a HUD reading only the flight state no longer keeps the whole
  structure publish running), and a record's arrays are sized to the count and released 10 s after its last read (one
  `GetSurface` on a 10^5-face ship used to keep ~12-15 MB for the grid's life). (efcb6e2, 4226e58)
- `GetSurface` copies the faces under the grid's lock and fills your lists after it; `SetBlockTemperatures` checks your
  arrays before taking the lock. (0c2a059, 838ebb8)
- A running crack is abandoned cleanly when `Structure.Loads` goes off; the jagged line's separation check is bounded;
  no shared-word writes per grid or per debris piece each tick; the tear's constraint check binds a delegate (no boxing).
  (6ad799d, 6e8dd7e, 2b283af, 000d926, 6f9c42b)

**Settings**
- A harness / dev override (an `aeroset` write) is never saved as the player's setting; each setting keeps the value set
  through the store. (63efc2b)
- Thrusters' attitude share is saved with the world (lines `thrust x y z f`; older saves load as before). The terminal
  slider is still off, so nothing is written until it comes back. (c6e776c)
- The settings files are read in the background and applied on the simulation thread; a change made while the client
  file was being written is no longer lost. (f6b13a9, 7dce7bd)
- A scorch stamp failure no longer saves `Heat.Scorch` off. (897b0e1)
- Settings docs fixed: `Structure.WingStrength` default 25 (said 1), `Fx.SpawnPerTick` 48 (said 12). (59e773e)

### For modders

- **Fixed component ids for the saving block components** (2026-10-15): `AeroSurfaceSettingsComponent`
  `ae205e77-1a65-4c0d-9b1e-0000000000b1`, `AeroGearComponent` `...b2`, `AeroAirbrakeComponent` `...b3`,
  `AeroThrustSettingsComponent` `...b4` (on every thruster prefab), each with the engine's
  `EntityNameSessionComponentObjectBuilder`. Their save keys: `docs/ARCHITECTURE.md` section 11.

- **AeroApi version 8** (was 6). Members only added; versions 2-6 unchanged (checked by `Tests/ApiTests`). Reference:
  `docs/API.md`.
  - **Version 7** - flight state and structure, caller-owned arrays, no allocation, published only while read:
    `WatchGrid`, `GetApiVersion`, `GetFlightState` / `GetFlightStateNames` / `GetFlightStateIndex` (75 packed values,
    append-only), `GetControlSurfaces`, `GetThrusterForces`, `TryGetStructureCounters`, `GetStructureBlocks`,
    `GetStructureBlockState`, `GetStructureBlockLoads`, `GetStructureEventSequence` / `ReadStructureEvents` (a numbered
    ring of 512 failures: block failed, burnt through, cut failed), `GetHeatFaces`, `GetHeatFaceState`,
    `SetBlockTemperatures` / `ClearBlockTemperatures` (another heat model drives strength, burn damage and the gauge;
    aero's own is the fallback after 2 s). (d91b51f)
  - **Version 8** - what a settings screen needs: `TryGetSettingUi` (label, group, tier, unit, step, choices),
    `IsSettingForced`, `CanEditWorldSettings`, `ResetSettingsGroup`, `AreWorldSettingsUnsaved`. `GetSettings` lists the
    settings in screen order. (59e773e)
  - Changed answers: each version-7 record (flight, hull faces, structure blocks, heat faces) is watched on its own -
    the first read of a record answers 0 / `false`, every read keeps it published 5 s more, and 10 s after its last read
    its arrays go (it answers 0 again until republished); structure events record only while polled (10 s after the
    last poll) and clear per world; `GetSurface` answers 0 until the grid's next tick after your first call; `GetThrusters`'
    effectiveness is 1 by default (profiles apply only on the legacy thrust path); thrust centres moved 0.125 m (see above).
- **The settings menu contract `ModSettings/v1`** - any mod can add its own section to the shared Mods tab with no code
  shared: the `TabItem` tagged `"ModSettings/v1"` in `OptionsScreen.PART_TabControl.Items` (or add it: `Header` "Mods",
  `Content` a `ScrollViewer` around a `StackPanel` tagged `"ModSettings/v1/host"`), then an `Expander` tagged
  `"ModSettings/v1/<YourModId>"` in that panel. `docs/API.md` (Settings).
- **One cross-mod link each way.** Aero names `OrbitalMod.OrbitalApi` only in `Api/OrbitalLink.cs`, bound on one rule
  (`Api/CrossModLink.cs`): the last loaded copy, typed delegates by name and parameter types, bound again on a reload,
  backed off after a fault. The Orbital Mod's `tools/check_contract.sh` checks it.

### Internal

- **Legacy clean-up (2026-10-17)**: `OffsetThrustJob.Execute` / `LegacyScale` and `OffsetThrustJob.Attitude.cs`, the SAS
  / phantom torque and gyro hand-off in `AeroSimJob`, the old attitude test harness (`Integration/AeroTestHarness.cs`), the
  thruster-override and grid-override helpers in `PhysicsHack`, the retired solid-rocket segment key reader
  (`aero-solid:`), unused `CompressibleFlow` / `AtmosphereModel` relations, dead `FlightField` prototype knobs, and the
  duplicate direction helpers (the engine's `Base6Directions.GetVector`). `OffsetThrustJob` keeps its name: it holds the
  thruster and gyro caches.
- **Release prep (2026-10-17)**: the legacy wing-root wear / breaks and their settings `Structure.WingStrain` /
  `Structure.WingStrength` removed (off by default, superseded by `Structure.Damage`; old settings files still load, the
  API's wing strain is still measured); `StructureContactLoads.Counters` removed (unused). Quiet logs: the per-grid /
  per-build diagnostic lines are behind `AeroLog.Detail` (on only with the harness's dev flag) - one 50-minute session
  had written ~2000 aero lines, 750 of them a grid entering the scene.
- **Feature folders.** `Integration/` and the flat folders split by feature: `Grid/`, `Runtime/`, `Api/`, `Air/`, `Dev/`,
  `Propulsion/`, `Fx/`, `Hud/`, `Blocks/`, `Structure/{Model,Server,Fx}`, `Design/`, `Debris/`, `Ui/` (each with a README).
  Big files split into partials by concern (`AeroGridComponent`, `StructureServer`, `FlightField`, `OffsetThrustJob`,
  `DampedShadowedDragModel`, `ConnectedComponentWingDetector`, `SmoothSurfaceProvider`, `AeroSettings`). Public names
  unchanged (the harness reads them by name).
- **Dead code out**: the never-built drag / surface models `AxisCdDragModel`, `CubeSurfaceProvider`,
  `NewtonianDragModel`, the never-created `ThinAirfoilWingModel` and `HelicopterRotor` (no block definition carries
  its two GUIDs: its registrations, GUIDs, debug draw, control feed and the `GetAeroBlocks` / `GetAeroBlockState`
  "HelicopterRotor" kind went with it; the "HelicopterRotor" thrust profile stays) - deleted with the user's approval,
  `PhysicsHack.UncapSpeed` / `ApplyDeltaVAndTorque`, `EngineOverrides.UncapSpeed` / `TrySetConfigProperty`,
  `FlightField.LightPlace` and the old mesh-plasma leftovers, unused structure knobs and diagnostics; unused
  usings in 71 files; stale comments fixed.
- **Shared helpers**: one `FindType`, `IsFinite`, `FloorDiv`, SAS torque, symmetric 3x3 solve, block-definition read,
  block health, bone ledger step.
- **Tests**: new suites `ApiTests` (the API's shape from source, the event ring, the cross-mod binding), `SettingsTests`,
  `SettingsUiTests`, `DebrisTests`; AeroBench `BugChecks` (wing merge, masks, chunk updates, manifold components, thread
  priority); StructureTests for the ledger, thresholds, scorch, tension / compression and the jagged line; TearTests for
  the anchors.
- **Docs**: `docs/ARCHITECTURE.md` (new), `docs/API.md` versions 4-8, folder READMEs, this changelog.
