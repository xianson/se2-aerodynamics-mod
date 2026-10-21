# Changelog - Aerodynamics Mod

Numbers below come from the commits' own measurements (AeroBench on the dumped ships, the offline suites); nothing here
was flown in game for this release. The commit hashes are on the integration branch.

## Unreleased - the magnetic landing gear

- **Magnetic Landing Gear 1.5 m** (Aircraft Parts): the 1.5 m retractable gear's housing, leg and slide; its foot a vanilla
  magnetic landing gear pad on a hidden 600 kg wheel. Lock with P or the panel's Lock; Auto-Lock on touchdown. Never locks
  onto its own ship; retracting is refused while locked. The pad follows the ship's owner (the Enemy switch too).
- Every gear: the stiffness slider's top is capped at 2.78x vanilla (was 3x): past that the suspension went unstable.
- Offline: `Tests/GearTests` (pad life, magnet gates, stiffness cap), `Tests/BlockSaveTests` (its saved key). Not flown in game yet.

## Unreleased - the Autopilot panel, redone

- Panel texts show in game (they were raw keys like "ApBehaviour_Auto"): the mod reads its texts into the game's table once a world.
- Status first, wrapped. One **Mode** dropdown: Off, Hover, Hold height, Cruise, Follow route, Return home, Follow ship, Attack.
  A line under it says what the mode does. **Level Off** is the only button. Then only that mode's settings; **Safety limits** last.
- Renamed: **Fly as** (Detect / Plane / Helicopter / Thruster ship), **Attack movement** (Strafing run, Turn-back run, Circle it,
  Keep distance, Hover over / under, Chase - gun lead / direct / trailing, Dogfight, Rocket), **Target** (Locked target / GPS /
  Ship by name / Other mod), **Keep flying when I leave the seat**. Each dropdown: a line under it for the choice.
- **Facing** (Direction of travel / Nose at target / Side at target - left / right) and **Aim priority**: how far the flight gives
  way to point that face at the target. Movement always wins; the safety limits still clip. Replaces Point At, Broadside and the
  aim switch - old saves convert (Point At: Keep distance + nose; Broadside: Circle it + a side; aim on: the nose).
- Toolbar: every mode is still an action; existing toolbars keep working.

## Unreleased - test spawns (creative / admin)

- **Ctrl+Shift+I** spawns an enemy **missile**, **Ctrl+Shift+O** an enemy **bogey**, **Ctrl+Shift+N** removes them (not
  M / B: the game's M opens the map and Ctrl+B the blueprints whatever else is held). Each is a blueprint from your
  Blueprints folder ("Missile", "Bogey"; settings `Spawn.MissileBlueprint` / `Spawn.BogeyBlueprint`) with an Autopilot block.
- It appears 2000 m / 4000 m away (random, behind, ahead or a side), at your height, out of the ground and clear of grids;
  1..8 missiles per press.
- Made the game's hostile NPC's through the Autopilot block's Enemy switch (the whole construct); its panel set to Engage with
  Rocket Guidance / Dogfight and you (your grid, else your character) as the target; launched at its own speed (a bogey at its
  cruise) or the `Spawn.MissileSpeed` / `Spawn.BogeySpeed` setting.
- Who may: `Spawn.Allowed` (world; creative or an admin by default). Removed after `Spawn.DespawnMinutes` (10; 0 never).
- Says why when it cannot: no such blueprint, no Autopilot block, the slot not written, not allowed, the world's autopilot off.
- API 14: `RequestTestSpawn`, and text settings (`TryGetSettingText`, `SetSettingText`). Offline: `Tests/SpawnTests` (132
  checks). Not flown in game yet.

## Unreleased - the Autopilot block (hover hold, height hold, flight in the air, target behaviours)

- **Flight Hold** (the Autopilot block's panel; also Level Off): heading hold or wings level, altitude hold with a climb
  rate, speed hold - each on or off; engaging catches what the ship has. TECS flies height and speed together (the most
  climb is the excess thrust's). Plane2: 90 deg turn with no overshoot (2 deg in 22 s), height within 4 m; +300 m in 37 s
  with speed held within 1.1 m/s; +30 m/s with height within 5 m. Level Off from a 50 deg bank, 20 deg dive: 39 m lost.
- **Navigate:** GPS marker names or coordinates, once (then a loiter at the end) or round; **Return** to the engage point;
  the terrain minimum kept along the path. Plane2 in a 15 m/s crosswind: a 90 deg corner caught with 103 m past the line
  (turn radius 970 m), then within 9 m; a 1500 m loiter within 22 m RMS; 250 m hills at 150 m asked (100 m minimum), never under 195 m - over the ground the ground's rise is in the climb, and terrain climbs as hard as the excess thrust allows.
- **Engage** (From the Depths' attack modes on a target): attack run (begin / abort distance, re-run time), attack run
  U-turn, broadside (angle, range; 90 circles), circle, hover above / below, point at, standoff, pursuit lead / pure /
  lag, jinking. Auto picks one (utility scores with hysteresis: 1 switch in 60 s across a boundary). Tactical numbers only -
  the turn, the speeds and the g are the ship's own.
- **Flight styles**, detected or set: airplane, helicopter (tilt movement - the hover's lean toward the motion), six-axis
  (thrusters every way, the nose free).
- **Nose on the target's lead** for fixed forward guns (their axis, muzzle speed and range read from the grid's weapon
  blocks; SE2 4.0 shots fly straight): aim error and "in cone" on the status line and the API. Pursuit on Plane2: 100 % of
  the time in a 2 deg cone at 480..510 m (straight, turning and weaving targets); six-axis standoff: 100 %; a helicopter
  aims by yaw and by height (25..100 % against a weaving target).
- **Target:** the grid's own targeting (locked, else priority, else nearest seen by the turrets), a GPS marker, a grid
  by name, or another mod (`AeroApi` 12: `SetAutopilotTarget`, `TryGetAutopilotAim`, ...). Runs unpiloted.
- **Collision avoidance** (on by default; panel: on / off and a look-ahead, 4 s): other grids, asteroids and its own target
  - when the closest approach along the velocity now would come too near, a sidestep or a pull-up through the mode's own
  flying (every mode); no path planning. Plane2 at 150 m/s past a ship parked on its route: 17 m clear (off: it hits),
  back on the line after; a 120 m asteroid (8 s look-ahead): 13 m clear; an attack run with a 5 m abort distance: 5.5 m
  clear (off: it rams); a hover steps aside from a drifting ship and comes back within 0.6 m.
- **Terrain at any range on planets:** the autopilot reads the planet's own height (its generated surface) as well as the
  ground rays, which see only near the ship: it looks 6 km ahead and 20 km down. A 300 m cliff at 300 m/s is seen 2970 m
  off (the rays alone: 360 m); Plane2 navigating at 150 m into a 400 m cliff climbs from 1.1 km before it and keeps 146 m
  (the rays alone: it flies into it). Dug holes and placed voxels are still the rays' only.
- **Target Filter:** hostile only (default) / hostile + neutral / everything, on every target source, by the game's own
  relation (the turrets' rule). With no turret, or a wider filter, the block finds the nearest target within 4 km itself.
  A refused named grid says why ("no target - X is neutral").
- **Enemy** switch (Engage): the whole ship - every grid on its rotors, hinges and pistons (the game's own "ship") - becomes
  the game's hostile NPC's (as encounter ships are): its turrets, subgrid turrets too, and its Engage fight players; off gives
  each grid back to its own owner (saved with the block, per grid). A grid joined later turns hostile too; one detached
  stays as it is. Refused when a joined grid belongs to someone else. Docked ships (connectors) are not part of it. World setting `Autopilot.EnemyRule`: off /
  creative or admin (default) / everyone; only the owner switches it on; switching it off and editing an enemy's panel:
  creative or admin. An enemy ship follows the game's NPC rules (no fuel used, the NPC grid cleanup limit). `AeroApi` 12:
  `TryGetAutopilotEnemy`.
- The panel shows only the chosen mode's fields. Every refusal and let-go says why ("too slow to fly on the wings",
  "no waypoints", ...). Saved with the block.


- **Autopilot block** (Aircraft Parts; a stand-in look for now: the vanilla Event Controller, 1 m, and Timer, 0.5 m): its
  control panel is the autopilot. **Hover Hold** holds the ship's place and height - your movement keys fly it at up to its
  Hover Speed (10 m/s) and it stops and holds where you let go; up / down change the height; steering lets go of the hover
  (the height hold stays, and the panel says so). **Height Hold** holds the height while you fly. **Autopilot Off**. All three
  (and Toggle Hover / Height Hold) are toolbar actions. Also on the panel: a status line (the mode, the height held, why it
  last let go), Height (engaging shows the height caught; type a new one and it flies there), Height Over Ground (off: a
  level altitude), the protection's switch, g limit, stall margin, terrain floor and look-ahead, and Keep Mode Unattended.
- The block flies its own ship; several on one ship: the first working one by position flies, the others say "Standby".
  Switched off, unpowered, damaged or removed: the autopilot lets go and says why. Its panel is saved with it - a helicopter
  left in hover hold holds it again after a reload. The world's settings (`Autopilot.*`) are the host's caps: autopilot
  allowed, unattended allowed, most lean, climb rate.
- **The single-tail helicopter's sideways drift - confirmed and fixed.** Offline first (`Tests/AutopilotSim` "drift cause"):
  unpiloted, level, nothing leans the ship, so the anti-torque tail's own yaw thrust (18 kN on the 30 t H25) moves it
  sideways - 12.7 m/s after 20 s, within 3 % of that thrust's impulse over the mass; two tails (a yaw couple) hold their place.
  Hover hold leans the lift axis into it: the speed loop's integral finds the steady lean by itself (4.2 deg on H25).
  H25 3x / 2.5x, one tail: settles in 2.2 s, then holds its place within 0.3 m, 0.07 m/s and its height within 0.02 m
  for 60 s (two tails: 0.09 m); a 0.05 s actuator lag changes nothing, it holds up to 0.12 s.
- **Envelope protection** on every autopilot command: short of the whole ship's best-lift angle (the stall warning's own
  evaluation, less the panel's stall margin), within its g limit, within what its controls can hold (the predicted loss of
  control, asked for the command before it is given), off the terrain (the panel's floor, and a look ahead along the path -
  pulls up in time, inside those same limits), full thrust when out of energy, the throttle eased at the speed limit.
- **Terrain aware:** height over the ground (the terrain followed), the ground's slope fed
  forward, the time to impact along the path - from the physics' own rays (the game builds voxel colliders only near ships:
  a long ray that finds nothing is "not known").
- **No cheats:** the autopilot writes only what a seated pilot writes - a target attitude and the movement keys - and the
  ship's own aero-aware attitude law flies it through its real gyros, thrusters, rotors' cyclic and control surfaces.
  (While it flies a rotorcraft the law plans its braking with 35 % of the cyclic's reach too: `ApTuning.CyclicPlanShare`.)
- **For later modes:** one core (`Autopilot/FlightDirector.cs`) - goals, guidance, protection, the attitude law - with an
  external goal source (a track, an acceleration flown with the lift, or an attitude), a flight-performance query
  (`FlightPerf`: load, turn rates, drag, speed after a pull - from the ship's own lift and drag) and a terrain query.
  `AeroApi` version 11: `TryGetAutopilot`, `RequestAutopilot`, `TryGetFlightPerformance`.
- **Offline flight simulator:** `Tests/AutopilotSim` - the real core on the game's thrust law and the mod's allocator, rotor
  trim and attitude law (ThrustTorque's order), the dumped planes on their AeroBench polars (`appolar`), terrain models,
  gusts, noise, lag, mass changes, engine-out, and the block's panel through the same link the game runs (engage, refusal,
  let-go written back, typed height, reference switch, block dark, a saved mode at load, arbitration); a ledger proves every
  tick's forces come from real actuators. In the gate.
- **Formation** (Engage behaviour): a slot right / up / back of a leader grid (any grid the Target Source picks, the filter
  not applied), the leader's motion from its observed positions only. Offline, a leader in Flight Hold turning 90 deg and
  climbing, one or two wingmen: airplane caught the slot in 39 s, 5.6 m RMS in a 30 deg-bank turn (worst 12), 5.0 m in a 200 m
  climb, 2.2 m straight; helicopter 0.9 / 1.8 / 0.7 m; six-axis 0.4 / 1.5 / 0.2 m. An airplane wingman feeds the slot's turn
  forward, leads its climb and damps its closure (before: 180 m outside the turn, 60 m low in the climb, a +-13 m surge).
- **Dogfight** and **Rocket Guidance** (Engage behaviours 13, 14) with their panel values (preferred fight, head-on, bingo
  fuel / damage, hard deck, ceiling, skill; guidance law, navigation constant, target-acceleration gain, lateral g cap, gravity
  compensation, arm distance, timeout) - the logic is the author's (`Scripts/Simulation/Autopilot/AUTHORING.md`).
- **For behaviours:** a lift-vector goal (`ApExternalKind.Lift`), an observed-motion track of any entity (`ApMotionTrack`),
  corner speed, turn rates and specific excess power (`FlightPerf`). `AeroApi` 13: `TryGetAutopilotSlot`.
- **Thrust vectoring:** a solid rocket nozzle's Thrust Vectoring switch and Gimbal Cone (0.5..15 deg, default 5; saved) - lit,
  the ship's attitude control swivels its thrust (30 deg/s) for what the thrusters and gyros leave; any thruster can be given
  a cone (`AeroApi.SetThrusterGimbal`; `TryGetThrustVectoring`). Real thrust only: its side force and moment. Offline, a 150 kg
  finned rocket climbing at 60 deg through two 30 m/s side gusts stays within 0.95 deg (a 2 deg cone: 9.3; fins alone never
  hold the climb). The nozzle mesh does not swivel.
- **Offline simulator, several ships:** `Tests/AutopilotSim` `Flight` (each ship any mode, goal source, block or none; another
  ship as a target), metric hooks (min / max / RMS / mean), CSV columns of a scenario's own, and `UserScenarios/` for the
  author's packs.
- Not tested in game yet: the block, its panel and toolbar actions, the readout, the toasts, an unattended hover after the
  pilot leaves and after a reload, the terrain rays, formation, the slots, thrust vectoring.

### Control surfaces past the stall

- **A stalled surface is a flat plate.** Its force grows to ~1.2 x q x area broadside (Hoerner's plates; it was ~0.03), its
  drag included (tails now steady a ship at high angles). No dead zone or reversal from 30 to 50 deg. Under the stall: unchanged.
- **Deflection keeps at least 35 % of its bite past the stall** (Hoerner / NASA flap data; was down to ~0).
- **The allocator and the keyboard mixer measure each surface** at the inputs they may give it: no wrong-way pushes at any
  angle. SSTO (full) nose-down at 30 deg, 100 m/s: 55 -> 162 kN m best, the allocator's -21 -> 162. With its gyro it no
  longer locks into a deep stall (was: past 79 deg). Cost: +4-7 us per piloted grid per tick, no allocation.
- `Tests/SurfaceStallTests`: before / after on single plates and dumped ships.

### Flight assists

- **New world settings group "Flight assists"**, every default realistic (nothing changes unless you turn it on). Presets
  Realistic / Assisted / Forgiving (`Assists.Preset`, worked out from the values; Custom when they match none).
- **Stall protection in manual flight** (`Assists.StallProtection`): your steering is held short of the stall, under the
  autopilot's g limit and short of what the controls can hold - the autopilot's envelope protection on your target. No
  physics change. The speed readout says STALL LIMIT / G LIMIT / CONTROL LIMIT while it holds you back. AutopilotSim: a
  pull 25 deg past the stall stops at 10.7 deg (unprotected 19.3); a 10 deg pull at 200 m/s 2.6 g (4.7); from 35 deg,
  surfaces only, the nose is under the stall in 3.4 s (unprotected it stays at 27+). Inside the envelope: identical.
- **Changes physics:** stall angle, lift kept past the stall, control bite past the stall (`Assists.StallAngleScale`,
  `PostStallLift`, `SurfaceAuthority`). 1 is bit-identical to realistic. A 10 x 3 m plate wing at 100 m/s: stall angle
  x1.3 moves its best lift 17.5 -> 23 deg (48.8 -> 63.2 kN); post-stall lift x1.5 at 32 deg 29.2 -> 45.5 kN.
- `Gyros.TorqueScale` moved here (same name; saves unchanged).
- **Unrealistic, opt-in:** `Assists.OffsetThrust` scales only the offset-thrust torque the attitude control could not
  cancel; what it asks the thrusters for is never touched. Realistic (1): the controller already cancels offset thrust
  with real gyros and thrusters while they can. AutopilotSim: a cancelled offset loses 0.05 % (the solve's residual); a
  weak lifter's 5.6 deg roll is 2.8 deg at 0.5, none at 0.
- **Fixed: the settings menu (Options > Mods) without a ship.** Aero's section appeared only once a ship was loaded near
  you (or with the Orbital Mod, which ticks the shared tab). Now it is there in a fresh world, on foot, with nothing built;
  the settings files are read then too. The Mach readout, creaks and cockpit shake no longer stay stuck when the last ship
  goes. In a world with no planets and no ship, single player, the world's settings were read-only: fixed.
- **Fixed: world settings in a world with no planet** (space only, the Empty World) were never saved - they were kept only
  on the planets. The players' character and spectator now carry them too; older saves still load from the planets (a
  body's copy wins where both exist). The world's end (quick launch's pending ask, the autopilot's messages, the API's
  structure events) is now seen in planetless worlds too.

## 1.0.4 - saving fixed on SE2 4.0

- **A world with a newly placed aero block could not be saved** ("Failed save ... Serializing dead Entities ... AeroSurfaceSettingsComponent").
  SE2 4.0 checks every entity a save points to; each aero block's saved settings pointed at the block itself, which 4.0's save
  does not hold as an entity of its own. They now point at the block's grid (only the settings text is read back, so
  nothing else changes). Checked in game: placing a control surface then saving failed before the fix and saves after it, in a
  saved test world and in a fresh world with both mods.

## 1.0.3 - works on SE2 4.0

Ported to Space Engineers 2 **4.0.0.3151** (from 2.4.0.95; 4.0 only from this version on). Checked in game on 4.0
(three runs of an SSTO test world, both mods): both compile and load, flight, the structure failure (crack, tear,
split, alerts), debris drag on broken blocks, warp billing, the settings tab, the control-surface panel, the map; no
exception in our code, no crash.

- **Compiles on 4.0.** 4.0 runs Keen's serializer generators on mod scripts: the control-panel models (surface, airbrake,
  gear, solid rocket nozzle / segment, thruster) are `partial` (VRG1006) and `[NoSerialize]` - the generated serializer
  needs an assembly mod scripts cannot reference (CS0012).
- **Control panels open again.** 4.0 hands a block's panel models to the client through the server transport's
  replication tracker: a mod model was not in it, the whole request was dropped ("Skipping story ... completion failed
  patching") and the block's panel - the game's own parts too - never came. `DetailModelPairing` pairs the model there
  as well (checked in game: the control surface's six models come back, its sliders reach the block).
- Control-panel labels have en-US text entries now (`Assets/Texts`, `AeroMod.loc-texts`). 4.0 checks every label as a
  localization key and logs an error line for each it cannot find - it still does: 4.0 does not pick up the mods' en-US
  texts (the Orbital Mod's block names are reported too). Harmless: the label shown is its own (English) text.
- **Effects hold their time again.** 4.0 removed `ParticleEffectHandle.FixEffectTime`, which the flight field (plasma,
  vapour, tip trails), the exhaust and the thruster flames used: `Fx/EffectTime.cs` sets the same render state through
  `ParticleEffectEntity.SetParameter` (bound once, typed: no allocation per call - FlightField's four reflection
  `Invoke`s per write are gone too). The thruster flames are the game's parameter-driven handles in 4.0;
  `AeroFlameComponent` drives them through the same call.
- **Content tracks 4.0's vanilla:** the exhaust effects re-copied from the 4.0 hydrogen flames and rocket trail
  (`gen_propulsor_defs.py gen_exhaust`; not parameter-driven, as ours are held by EffectTime), the vapour from the 4.0
  meteor smoke (`gen_vapor.py`), and the two plasma emitters on a texture the game retired moved to its successor.
  No asset reference unknown to 4.0 is left (all definitions and textures checked).
- **Honest dampeners**: 4.0's gravity hold ignores a pilot input under 0.001 (`HonestHold.InputDeadzone`); the rest of the
  thrust law, the gyro law and the gravity multiplier (2) are unchanged in 4.0 (decompiles diffed).
- Dev: `AeroPanelProbe` passes the detail types as the `HashSet` 4.0's `Attach` takes.
- Tools (`tools/shared`): ModCheck / GameVerdict / WhitelistGen for 4.0 (VRage.Multiplayer is a mod reference and
  whitelist seed now; the KCG analyzers run - four of their rules are errors in 4.0; `gameverdict.sh --analyzers`,
  `--dump-generated`); `build_mod_content.py` stubs the builder's editor plugin at the version the game's builder asks
  for and keeps the Mod SDK half of its vanilla index across game updates.

## 1.0.2 - lighter helicopter torque, settings look

- **Helicopter torque lowered** (the user's call): the tail rotor needs 3 % of the main rotor's rated thrust (was 10 %;
  `RotorKinematics.ReactionScale` 0.344 -> 0.1032), and the Reversible Rotor is 38 kN (was 128 kN; it out-thrust the 0.5 m
  main rotor).
- **Settings menu:** backplates behind labels, locked rows greyed, "Reset <group> to defaults", groups with only advanced
  rows shown flat (shared with the Orbital Mechanics Mod).

## 1.0.1 - honest dampeners, gyro rebalance, quick launch

- **Gyro rebalance** (the user, 2026-10-18: "just do 1, maybe a lil 2. have them both as settings"). Vanilla gyros are
  huge reactionless torque (one large gyro on a 39 t plane: 44 MN m), so they held lopsided designs straight and out-torqued
  the surfaces and stalls.
  - `Gyros.TorqueScale` (world, Flight model, 0.05..1, default 0.25): every gyro block's own maximum
    (`GyroscopeData.MaxTorque`, the game's) is its definition's x the scale - the game's gyros weaker, no counter-torque; the
    grid's `MaxTorqueData` follows (the game sums it), so the game's gyro laws, the aero-aware law, the allocator, the
    loss-of-control prediction, the rotor limiter and the design probe all use the scaled torque. Measured
    (`Tests/AeroBench gyros`, 150 m/s sea level, the vanilla target-law budget min(0.01 m^2, 0.25 x MaxTorque)): vanilla
    gyros held one main engine out on Plane2 (2.78x) and Red Ship (1.40x) and their own stall moment on 5 of 7 ships (up to
    3.67x); at 0.25 none holds either (worst 0.69x and 0.92x), and they still hold a 5 deg upset on 5 of 7 (trim,
    stabilising) with 0.7..106 deg/s2 of pitch acceleration. One scale everywhere, space too (thrusters still steer).
  - `Gyros.Saturation` (on) / `Gyros.SaturationSeconds` (15 s): the gyros store the momentum they give (signed, grid-local);
    from half full their torque fades smoothly to a quarter, and bleeds back while lightly loaded. A full-torque hold fades
    from ~7.5 s; turn-and-stop manoeuvring and small trims never fill it. Server, per grid (a split grid starts empty).
  - Flight state 75-76: `GyroSaturation`, `GyroTorqueShare` - `AeroApi` version 10.
  - Offline: `Tests/ThrustTests` GyroBalanceChecks (the fade, the factor, the write rule never compounding, the store's fill,
    recovery, signed cancel, trim, gyros lost); the measurement `Tests/AeroBench gyros`.
  - Not tested in game yet.

- **Quick launch (creative only)** - moved here from the Orbital Mod (its 1.0.1 had it; the user, 2026-10-18: "ctrl+T
  should be in aero creative"). Seated in a cockpit, `Ctrl` + `T` lifts the ship and everything joined to it to 1 km over
  the ground (aero's ground ray; no ground found within 2 km: 1 km up from where it is), never lower than it is, never
  closer to the ground than its own radius + 100 m; turns it level about itself on the cockpit's heading; and sends it
  along the nose at its cruise speed (its top speed at 3/4 throttle, 150 m/s without one; at most 0.99 x the physics'
  speed cap). Refused with a toast in survival, on foot, on a station or a ship docked / locked to one, away from a planet.
  Settings: world `QuickLaunch.Creative` (on), `QuickLaunch.AltitudeM` (1000), `QuickLaunch.SpeedMps` (0: cruise) in a new
  "Quick launch" group; client `QuickLaunch.Key` (T) / `QuickLaunch.Modifier` (Ctrl) in a new "Controls" group.
  - Server-checked from its own state (`Launch/QuickLaunch.Server.cs`, the teleport phase on the planets' settings carrier):
    the client sends only its player identity; the server finds its character, creative (`GameModeSessionComponent.IsCreative`),
    the seat that character pilots and that seat's grid.
  - The move: with the Orbital Mod loaded, the Orbital Mod's (`OrbitalApi.TryQuickMove`, new in its 1.0.1: its safe move
    path, refused on rails, clamped under its planet's space border); without it aero's own (`Launch/QuickLaunchMove.cs`:
    the game's fast-travel way - the teleport phase, the joined set together, nothing static moved, world areas released,
    contacts masked) and the cockpit's target attitude turned with the ship on the client.
  - `AeroApi` version 9: `RequestQuickLaunch` (the Orbital Mod's harness key `quicklaunch`).
  - Offline: `Tests/QuickLaunchTests` (decisions, placement over 2000 random places, the level turn over 500 random ships,
    speed, lines, binding, a scan of the engine side); settings stubs and groups (`SettingsTests`), the registry class
    (`SceneTests`).
  - Not tested in game yet: the key, creative on a live session, both moves (aero's own and the Orbital Mod's for aero).

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
- **Mach 1 retuned: no engine pinch, block wings read as thinner** (2026-10-18, the user: "1 and 2"). With the wings'
  own wave drag the sound barrier had become an endgame wall (the F-6 needed T/W 3.4 at sea level). Two levers:
  (1) the atmospheric thrusters' Mach curve has **no transonic pinch** any more (it dipped to 76 % at Mach 1: a real
  afterburning engine doesn't, its thrust rises with ram from the high subsonic on) - the ram rise now starts there:
  85 % at Mach 0.85, 108 % at Mach 1 (`ThrustProfiles.JetMach1`), 148 % at 1.2, 194 % at 1.45, **198 % at Mach 1.6**
  (`JetRamPeak`; was 180 % at 1.7), then the same fall as before from Mach 1.8 (the ramjet still takes over at Mach
  1.98); (2) **block wings' wave drag takes 0.4 of their thickness ratio** (`CompressibleWingModel.BlockWingThicknessScale`):
  block wings are 12-19 % thick where a real fighter's are 4-6 %, so they now pay the wave drag of the 5-8 % wing a
  builder means - a thicker or straighter wing still pays more (thin swept vs thick straight: 7.8x less at Mach 1.2,
  7.3x at Mach 2). The supersonic drag due to lift is unchanged. AeroBench, the F-6: T/W to break Mach 1 at sea level
  3.36 -> **1.55** (0.8 kg/m3: 1.99 -> 0.92; 0.4 kg/m3: 2.53 -> **1.17**), then on to Mach 1.44 on that thrust (1.73
  before); its drag at Mach 1 1.92x -> 1.26x its subsonic drag; still ~2x what a real F-16 needs (SE ships are about
  twice as draggy per ton). A brick of the same frontal area stalls at Mach 0.45 on that thrust. The Tu-134 at Mach
  0.79: 886 -> 707 kN (its wings' wave drag now starts past its cruise Mach). Below Mach 0.75 nothing changes.
- **Rotor ratings x1.5** (the user's choice for honest dampeners, df2331a): main rotors 100 kN / 900 kN / 2.5 MN, the
  Reversible Rotor 128 kN; `RotorKinematics.ReactionScale` 0.42 -> 0.344 (the tail keeps its 10 % share).
- **Rotor trim:** the cyclic now trims an off-axis rotor's lift torque, so an unpiloted helicopter on dampeners stays
  level instead of slowly tipping over (`Propulsion/RotorTrim.cs`; a 3x craft hovers level at 67 % rotor). A single tail
  rotor's sideways push still drifts the ship when nobody flies it.
- **Turboprops hold full thrust to 100 m/s** before constant power (`ThrustProfiles.TurbopropV0` 40 -> 100, b57c845).
- **Contact loads use the gravity the ship really feels** (the world's multiplier and the dampeners' hold): a parked
  ship's gear and ground loads read its true weight (they read half), and hovering or falling ships no longer get a
  phantom support load.
- **Fixes:** a client crash ("Collection was modified") in the wingtip trails after a grid split (b03665a); the effects
  of a split-off grid no longer carry the original's state.

## 1.0.0 - first public release (for players)

The first Workshop release. In plain words, what you get:

- **Ships fly by their shape.** Drag, lift, stall, the sound barrier and supersonic flight come from the hull you built;
  wings are found from your blocks. Wind and ground effect count, and damage changes how a ship flies at once.
- **Aircraft parts** in the G menu (Aerodynamics): hinged control surfaces, retractable landing gear in four wheel sizes,
  clamshell airbrakes (open with the parking brake), turboprops, ducted fans, helicopter main rotors with cyclic
  steering, reversible rotors, ramjets in four sizes, and solid rocket motors you stack from segments.
- **Engines by speed.** Atmospheric thrusters lapse a little up to Mach 0.85, pinch through Mach 1 (76 %), then gain from ram air (1.8x
  their rating at Mach 1.7) and fade fast past it (the pinch is gone in 1.0.1); jets flame out
  with the air from the side or behind; ramjets only work supersonic.
- **A structure that can break.** Air, thrust and impact loads run through the ship you fly; overloaded parts wear,
  fatigue, bend, crack across and tear off, with sound, shake and smoke. Rotor blades break on what they hit; weld the
  block to repair them.
- **Re-entry.** Plasma, vapour and wingtip trails, hull heating with heat damage and scorch marks.
- **Warnings** for stall and coming loss of control (on the Orbital Mod's HUD), and an optional Mach readout.
- **Settings menu** under pause menu > Settings > Mods, plus text files for hosts.

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
  thruster-override and grid-override helpers in `GridPhysics`, the retired solid-rocket segment key reader
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
  `GridPhysics.UncapSpeed` / `ApplyDeltaVAndTorque`, `EngineOverrides.UncapSpeed` / `TrySetConfigProperty`,
  `FlightField.LightPlace` and the old mesh-plasma leftovers, unused structure knobs and diagnostics; unused
  usings in 71 files; stale comments fixed.
- **Shared helpers**: one `FindType`, `IsFinite`, `FloorDiv`, SAS torque, symmetric 3x3 solve, block-definition read,
  block health, bone ledger step.
- **Tests**: new suites `ApiTests` (the API's shape from source, the event ring, the cross-mod binding), `SettingsTests`,
  `SettingsUiTests`, `DebrisTests`; AeroBench `BugChecks` (wing merge, masks, chunk updates, manifold components, thread
  priority); StructureTests for the ledger, thresholds, scorch, tension / compression and the jagged line; TearTests for
  the anchors.
- **Docs**: `docs/ARCHITECTURE.md` (new), `docs/API.md` versions 4-8, folder READMEs, this changelog.
