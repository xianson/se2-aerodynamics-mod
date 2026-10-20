# Autopilot - hover and height hold, flight in the air, target behaviours, envelope protection

Flown from the **Autopilot block's** control panel (one block per ship; several: the first working one by position flies).
One core for every autopilot user: goals -> guidance -> protection -> the seam (what a seated pilot writes: a target
attitude, the movement keys) -> the ship's own aero-aware attitude law and allocator (`Propulsion/ThrustTorque.cs`), which
fly it through the real gyros, thrusters, rotors' cyclic and control surfaces. The autopilot never applies a force or a torque.

| File | What |
|---|---|
| `FlightDirector.cs` | The core (engine-free): goals (`ApGoal`: attitude, hover, position, height, external), hover's speed loop -> lean (the integral finds a single tail's trim lean), height hold over the ground or along the gravity, the external goal source (`Track`, `Accel` flown with the lift - `LiftFlight` - or the thrust, `Attitude`), terrain response, the watch (reasons for letting go), the readout and words (`ApText`) |
| `EnvelopeGuard.cs` | The reference governor (engine-free): stall margin (LiftPeak's), g limit, the predicted loss of control for the command before it is given; a bank never cut by the stall caps (the step's roll about the flow kept) |
| `ApTerrain.cs` | `ITerrainQuery` (the ground below, a ray along the path), `PlanetTerrainQuery` (the same from a planet's height, `IPlanetHeight`: marched and bisected, normals from samples), `CombinedTerrain` (rays + planet: the nearer answer; `ILongRangeTerrain`) and `TerrainWatch` (height over the ground - its shape filtered, the ship's own motion not -, the ground's rise, the time to impact straight and along the bent path; 6 km ahead, 20 km below with the planet) |
| `ApPerf.cs` | The flight performance (engine-free): `PerfSamples` (the stall sweep's lift and drag over the angle of attack), `FlightPerf` (load, turn rates, drag, excess thrust, speed after a pull, the angle for a lift) |
| `ApTypes.cs` | `ApSense` (what the instruments show), `ApCommand` (the seam), the enums, `ApExternal` / `IApGoalSource`, `ApMath`, `LoacMath` (Loac.cs's arithmetic), `SideAuthority` |
| `ApConfig.cs` | The world's caps and the client readout (`Autopilot.*`; the rest are a new block's panel defaults) and `ApTuning` (the one constant the game and the sim share) |
| `ApPanel.cs` | The Autopilot block's panel (`ApPanel`, `ApMode`, `ApHeightRef`) and the limits it flies with (`ApLimits.Of`: the panel within the world's caps) |
| `ApPilot.cs` | THE FLIGHT PILOT (an external goal source): Flight Hold, Navigate, Engage; the style's executor (airplane: an acceleration - L1 across, TECS vertical - on the lift with TECS's throttle; helicopter: a moving place for the hover's lean; six-axis: the same place on the thrusters, the nose free), the terrain minimum, the aim, jinking, its view (`ApPilotView.cs`: the status line) |
| `ApNav.cs` | `ApAltimeter` (the height over the engage datum; the planet's centre where the gravity's lines meet), `ApCompass`, `ApL1` (L1 toward a point, a circle; a vector field onto a line flown by a braking course law), `ApTecs`, `ApRoute` (the route's text: GPS names, coordinates) |
| `ApBehaviours.cs` | The behaviours (attack run, U-turn run, broadside, circle, hover above / below, point at, standoff, pursuit lead / pure / lag, extend) as parameterised goals (`ApNavGoal`), and the arbiter (`ApArbiterBh`: utility scores, hysteresis, dwell) |
| `ApAim.cs` | The target's track (`ApTargetTrack`: its acceleration estimated), the lead (`ApAim.Lead`: the intercept iterated), the style's detection (`ApStyleDetect`), the target source interface (`IApTargetSource`) |
| `ApEnemy.cs` | The target filter (`ApRelations`: the game's relation as own / friendly / neutral / hostile, the filter, `ApTargetPicker` - the scan's nearest) and the Enemy switch (`ApEnemy` rules: the world's rule, owner, creative / admin, the lock; `ApEnemySwitch`: ask, step, the saved owner before; `IApOwnership`) |
| `ApBlockLink.cs` | The block and the flight director in step (`ApBlockLink`: a mode chosen engages, a let-go / the held height written back, the block dark lets go), the modes as goals (`ApModes`), which block flies (`ApArbiter`), the status line |
| `Game/AeroGridComponent.Autopilot.cs` | Per grid: the server's flight director each tick before ThrustTorque (`AutopilotStep` from `AeroSimJob.AttitudeJob`), its instruments (`ApSenseNow`), the keys written in place (`ControlData.Movement`), the record (`ThrustTorque.ApPub`) - the client copy flies the same target and hands the server the pilot's keys and steering |
| `Game/ApGameSense.cs` | The target in game (`ApGameTargets`: the grid's `TargetingGridComponent` - locked, priority, nearest; a GPS marker; a grid by name; the API's), GPS markers (`ApGps`: the owner's, then every player's), the fixed guns (`ApGunScan`: their axis, muzzle speed, range; the nose without guns: the block's front) |
| `Game/ApEnemyGame.cs` | Ownership and relations in game: `OwnershipSessionComponent.TryTransferToNPC` / `TryTransferOwnership` (the block's server job, one at a time), `IsHostileNPC`, the in-process player (`LocalIdentity`, `IsCreative`), `ApRelationReader` (`IRelations.GetRelationFromOwners` - the turrets' rule -, `IFactions`, `PriorityTargetComponent`) |
| `Game/PlanetHeight.cs` | The planet's height in game (`GamePlanetHeight`): `VoxelPlanetComponent.QuerySurfacePosition` by reflection (a closed delegate per planet), the generated shape at any range; GridTerrain's rays kept for what is near, dug or built |
| `Game/GridTerrain.cs` | The terrain in game: `IPhysics.CastRay`, synchronous, own buffers (voxel colliders exist only near entities: a miss is "not known") |
| `Game/AeroAutopilotComponent.cs` | The Autopilot block (server, injected by its prefabs): its panel under a lock, its status line, its step (registered with its own grid, which keeps the list: `AeroGridComponent.ApBlocks`) |
| `Game/AeroAutopilotBlockDetailModel.cs` | Its terminal: the mode buttons (toolbar actions too), the status line, the setpoints and limits |
| `Game/ApToasts.cs` | The autopilot's words to the seated pilot (client toasts) |

## The pilot modes (2026-10-19)
The panel's Flight Hold / Navigate / Engage are `ApPilot` on the flight director: the goals `ApPilot.GoalsFor(style)` (a helicopter:
Hover + External; else External) with the pilot as the source. Only tactical numbers on the panel; the turn radius, the
speeds, the g and the climb come from `FlightPerf` and the thrust. A helicopter aims by yaw and by height (pitching its lift axis
would move it); six-axis aims anywhere its side thrusters still hold the weight; an airplane blends the nose off its path within
30 deg and gives it back 4..7 s before the target. NPC ships: an unpiloted grid with an Autopilot block in Engage engages what its
turrets' targeting sees (no turret: the nearest the filter takes within 4 km). The Enemy switch makes a player's ship the
game's hostile NPC's (2026-10-19; the game's own owner change - replicated, saved with the grid); spawning NPC ships is not here
(an encounter would place the block with its panel saved in the prefab - the saved key carries every value).

## Entry points
- `AeroGridComponent.AutopilotStep` (AttitudeJob, both scenes; a null check when nothing is engaged).
- `AeroAutopilotComponent.BlockJob` (server, before the simulation: the block registers with its grid and keeps it awake while a mode is on); its panel's setters (the terminal, host / single player), its save (`AeroBlockSave.cs`).
- `ApToasts.ClientTick` (the client effects job, beside QuickLaunch).
- `AeroApi.TryGetAutopilot` / `RequestAutopilot` / `TryGetFlightPerformance` (version 11).
- The seam inside ThrustTorque: `AttitudeTarget` returns the autopilot's target first (`ApFlying`); the aware law flies it in any air, the game's gyro law taken back (the rate law's impulse for an unpiloted grid: `GyroControl.Undo(..., targetLaw: false)`).
- Inputs from the background evaluation (Structure/Server): the stall sweep's lift and drag and lift direction (`Hud/StallWarning.cs`), the loss of control predicted at the autopilot's probe (`Hud/Loac.cs EvaluateApProbe`); both run while it flies, whatever the HUD settings.

## Threading
The flight director: the server grid's attitude job only. The record: under its own lock (a few fields a tick). The stall
sweep's samples and the performance box: written by the background evaluation into a back buffer, published by a reference
swap (a reader holds one for a tick). The block's panel: under the block's lock (the terminal's thread writes, the grid's step reads a copy and writes back a
let-go); its status line built when the terminal reads it, from inputs the grid's step swaps in four times a second.

## Tests
Pilot modes: `Scenarios/FlightPack.cs` (holds, TECS, routes, wind, loiter, terrain, styles, refusals), `EngagePack.cs` (every
behaviour on the plane, the gun helicopter and the six-axis gunship against straight / turning / weaving / accelerating
targets; aim and time in the cone; the arbiter; the lead against a flown shot; the protection clipping a chase; idle patrol),
`PilotBlockPack.cs` (the panel through the link; the saved form), `EnemyPack.cs` (the relations by the game's rule, mocked; the
filter; the Enemy switch's rules and state over a mocked world; an enemy chasing the player, a drone's filter), `PlanetTerrainPack.cs`
(the planet's height mocked: the query, TerrainWatch's reach, a cliff flown with the rays alone and with the planet).

`Tests/AutopilotSim` (in the gate; `-- full` the sweeps): the real core and protection over the game's thrust law and the
mod's allocator / rotor trim / attitude law, on the dumped H25 helicopter (1.5x / 2.5x / 3x, one and two tails), VTOLs, and
the dumped planes on their AeroBench polars; terrain, gusts, noise, lag, mass changes, engine-out, pilot keys, every reason;
the block's panel through the link (`Scenarios/BlockPack.cs`: engage, refusal, let-go write-back, typed height, reference
switch, block dark, a saved mode at load, limits, arbitration); a ledger every tick (every force and torque from a real actuator within its limits). Scenario packs: `IScenarioPack`.
