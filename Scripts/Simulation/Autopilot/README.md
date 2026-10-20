# Autopilot - hover and height hold, flight in the air, target behaviours, envelope protection

Flown from the **Autopilot block's** control panel (one block per ship; several: the first working one by position flies).
One core for every autopilot user: goals -> guidance -> protection -> the seam (what a seated pilot writes: a target
attitude, the movement keys) -> the ship's own aero-aware attitude law and allocator (`Propulsion/ThrustTorque.cs`), which
fly it through the real gyros, thrusters, rotors' cyclic and control surfaces. The autopilot never applies a force or a torque.

| File | What |
|---|---|
| `FlightDirector.cs` | The core (engine-free): goals (`ApGoal`: attitude, hover, position, height, external), hover's speed loop -> lean (the integral finds a single tail's trim lean), height hold over the ground or along the gravity, the external goal source (`Track`, `Accel` flown with the lift - `LiftFlight` - or the thrust, `Attitude`), terrain response, the watch (reasons for letting go), the readout and words (`ApText`) |
| `EnvelopeGuard.cs` | The reference governor (engine-free): stall margin (LiftPeak's), g limit, the predicted loss of control for the command before it is given; a bank never cut by the stall caps (the step's roll about the flow kept) |
| `PilotProtection.cs` | The flight assist's stall protection in manual flight (engine-free): the pilot's own target through `EnvelopeGuard` (the world's caps, protection always on), the path's turn measured as the director measures it, the readout's hold. Its game side: `Grid/AeroGridComponent.Assists.cs` |
| `ApTerrain.cs` | `ITerrainQuery` (the ground below, a ray along the path), `PlanetTerrainQuery` (the same from a planet's height, `IPlanetHeight`: marched and bisected, normals from samples), `CombinedTerrain` (rays + planet: the nearer answer; `ILongRangeTerrain`) and `TerrainWatch` (height over the ground - its shape filtered, the ship's own motion not -, the ground's rise, the time to impact straight and along the bent path; 6 km ahead, 20 km below with the planet) |
| `ApPerf.cs` | The flight performance (engine-free): `PerfSamples` (the stall sweep's lift and drag over the angle of attack), `FlightPerf` (load, turn rates, drag, excess thrust, speed after a pull, the angle for a lift) |
| `ApTypes.cs` | `ApSense` (what the instruments show), `ApCommand` (the seam), the enums, `ApExternal` / `IApGoalSource`, `ApMath`, `LoacMath` (Loac.cs's arithmetic), `SideAuthority` |
| `ApConfig.cs` | The world's caps and the client readout (`Autopilot.*`; the rest are a new block's panel defaults) and `ApTuning` (the one constant the game and the sim share) |
| `ApPanel.cs` | The Autopilot block's panel (`ApPanel`, `ApMode`, `ApHeightRef`; the panel's own choices `ApModeChoice` / `ApMove` and their mapping onto the saved fields - `ChoiceOf` / `Choose`, `MoveOf` / `BhOf`; the facing `ApFacing` and the old Point At / Broadside's migration - `Migrate`) and the limits it flies with (`ApLimits.Of`: the panel within the world's caps) |
| `ApPanelLayout.cs` | What the panel shows per mode (the `[ShowInInspectorWhen]` switches), the line under each dropdown (`*Hint`), the choices' names and tooltips |
| `ApPilot.cs` | THE FLIGHT PILOT (an external goal source): Flight Hold, Navigate, Engage; the style's executor (airplane: an acceleration - L1 across, TECS vertical - on the lift with TECS's throttle; helicopter: a moving place for the hover's lean; six-axis: the same place on the thrusters, the nose free), the terrain minimum, the aim, jinking, its view (`ApPilotView.cs`: the status line) |
| `ApNav.cs` | `ApAltimeter` (the height over the engage datum; the planet's centre where the gravity's lines meet), `ApCompass`, `ApL1` (L1 toward a point, a circle; a vector field onto a line flown by a braking course law), `ApTecs`, `ApRoute` (the route's text: GPS names, coordinates) |
| `ApBehaviours.cs` | The attack movements (strafing run, turn-back run, circle it, hover over / under, keep distance, chase lead / direct / trailing, extend) as parameterised goals - movements only: the facing is the pilot's (`ApPilot.Aim`); a circle turns the way that keeps a chosen side in (`ApNavGoal`), and the arbiter (`ApArbiterBh`: utility scores, hysteresis, dwell) |
| `ApAim.cs` | The target's track (`ApTargetTrack`: its acceleration estimated), the lead (`ApAim.Lead`: the intercept iterated), the style's detection (`ApStyleDetect`), the target source interface (`IApTargetSource`) |
| `ApAvoid.cs` | Small collision avoidance (`ApAvoid`, on top of every mode): the closest approach to each obstacle (`ApObstacles`: other grids, asteroids; the target with half its behaviour's closest distance - `TargetClear`) along the velocity now within the look-ahead, a sidestep / pull-up flown through the mode's own command, let go 1 s after |
| `ApEnemy.cs` | The target filter (`ApRelations`: the game's relation as own / friendly / neutral / hostile, the filter, `ApTargetPicker` - the scan's nearest) and the Enemy switch (`ApEnemy` rules: the world's rule, owner, creative / admin, the lock; `ApEnemySwitch`: ask, step, the saved owner before; `IApOwnership`) |
| `ApFormation.cs` | FORMATION (Engage behaviour 15): a slot right / up / back in the leader's frame, the leader only observed (`ApMotionTrack`); hovering styles hold it as a moving place, an airplane flies L1 at the slot ahead on the leader's curved path with its turn fed forward (`TurnFeed`), the height led by its climb, the closure damped (`CloseDamp`), an overshoot widened out |
| `ApSlots.cs`, `ApDogfight.cs`, `ApRocketGuidance.cs` | THE COMBAT SLOTS (Engage behaviours 13, 14): `ApSlotCtx` (everything a slot gets each step), `ApSlotOut` (a nav goal or a direct goal, throttle, let-go, status, aim report), the panel's values (`ApDogfightParams`, `ApRocketParams`); the two logic files are the author's - **`AUTHORING.md`**: what to write, every input and output, every panel field, adding one, testing |
| `ApTrack.cs` | `ApMotionTrack`: any entity's position, velocity, acceleration, turn rate and energy from its observed positions alone (a fading-memory filter), with confidence and age |
| `ApLift.cs` | The lift goal's arithmetic (`ApExternalKind.Lift`): a lift vector from / to a path acceleration, a bank's lift direction |
| `ApBlockLink.cs` | The block and the flight director in step (`ApBlockLink`: a mode chosen engages, a let-go / the held height written back, the block dark lets go), the modes as goals (`ApModes`), which block flies (`ApArbiter`), the status line |
| `Game/AeroGridComponent.Autopilot.cs` | Per grid: the server's flight director each tick before ThrustTorque (`AutopilotStep` from `AeroSimJob.AttitudeJob`), its instruments (`ApSenseNow`), the keys written in place (`ControlData.Movement`), the record (`ThrustTorque.ApPub`) - the client copy flies the same target and hands the server the pilot's keys and steering |
| `Game/ApGameSense.cs` | The target in game (`ApGameTargets`: the grid's `TargetingGridComponent` - locked, priority, nearest; a GPS marker; a grid by name; the API's), GPS markers (`ApGps`: the owner's, then every player's), the fixed guns (`ApGunScan`: their axis, muzzle speed, range; the nose without guns: the block's front) |
| `Game/ApEnemyGame.cs` | Ownership and relations in game: `OwnershipSessionComponent.TryTransferToNPC` / `TryTransferOwnership` (the block's server job, one at a time), `IsHostileNPC`, the in-process player (`LocalIdentity`, `IsCreative`), `ApRelationReader` (`IRelations.GetRelationFromOwners` - the turrets' rule -, `IFactions`, `PriorityTargetComponent`) |
| `Game/ApGameObstacles.cs` | The obstacles in game: `IPhysics.QueryAABB` (`CollisionPreset.Bodies`, the game's AI movement's own query) around the ship every 10th tick, bounds as spheres, the top entity's velocity; not itself, what is joined to it, the target, planets |
| `Game/PlanetHeight.cs` | The planet's height in game (`GamePlanetHeight`): `VoxelPlanetComponent.QuerySurfacePosition` by reflection (a closed delegate per planet), the generated shape at any range; GridTerrain's rays kept for what is near, dug or built |
| `Game/GridTerrain.cs` | The terrain in game: `IPhysics.CastRay`, synchronous, own buffers (voxel colliders exist only near entities: a miss is "not known") |
| `Game/AeroAutopilotComponent.cs` | The Autopilot block (server, injected by its prefabs): its panel under a lock, its status line, its step (registered with its own grid, which keeps the list: `AeroGridComponent.ApBlocks`) |
| `Game/AeroAutopilotBlockDetailModel.cs` | Its terminal (2026-10-20): the status (wrapped), one Mode dropdown, Level Off, then only the mode's fields, the safety limits last; every mode a toolbar action (the old method names, off the panel) |
| `Game/ApToasts.cs` | The autopilot's words to the seated pilot (client toasts) |

## The panel (2026-10-20)
Status; Mode (Off, Hover, Hold height, Cruise, Follow route, Return home, Follow ship, Attack - `ApModeChoice` over the saved
Mode / ReturnHome / Behaviour: Follow ship is Engage with the formation, Return home the route's return); Level Off; then the mode's
fields: "Fly as" (Detect / Plane / Helicopter / Thruster ship), Follow ship's leader and slot, Attack's target, "Attack movement"
(`ApMove`), "Facing" and "Aim priority", the band; the route; the holds; "Keep flying when I leave the seat"; "Safety limits".
Each dropdown has a line under it and every choice in its tooltip. FACING IS A DESIRE (`ApFacing`: the way it flies / the nose on the
lead / a side on the target): `ApPilot.Aim` blends it into the movement's attitude by the aim priority, within each style's caps (a
plane: the 30 deg window and the pull-out - a side by the circle's direction; a helicopter: yaw; six-axis: anywhere its side
thrusters hold the weight, the heading turned first); movement wins, the protection clips. Old saves: Point At - Keep distance +
nose; Broadside - Circle it + a side (its angle: under 25 the nose, to 90 the right, past it the left); the aim switch - the nose
facing (format 3 of the saved key). Tests: `PanelLayoutPack`, `FacingPack`, `TextsPack`, BlockSaveTests.

## The pilot modes (2026-10-19)
The panel's Flight Hold / Navigate / Engage are `ApPilot` on the flight director: the goals `ApPilot.GoalsFor(style)` (a helicopter:
Hover + External; else External) with the pilot as the source. Only tactical numbers on the panel; the turn radius, the
speeds, the g and the climb come from `FlightPerf` and the thrust. A helicopter aims by yaw and by height (pitching its lift axis
would move it); six-axis aims anywhere its side thrusters still hold the weight; an airplane blends the nose off its path within
30 deg and gives it back 4..7 s before the target. NPC ships: an unpiloted grid with an Autopilot block in Engage engages what its
turrets' targeting sees (no turret: the nearest the filter takes within 4 km). The Enemy switch makes a player's ship - the whole
construct on its rotors, hinges and pistons (GetAllGridsInShip) - the game's hostile NPC's (2026-10-19; the game's own owner
change per grid - replicated, saved with each grid; each grid's owner before saved with the block, keyed by that grid); spawning NPC ships is not here
(an encounter would place the block with its panel saved in the prefab - the saved key carries every value).

## Entry points
- `AeroGridComponent.AutopilotStep`: the server copy from `AeroSimJob.AttitudeJob`; the client copy (its bridge: `ClientAp`) from the client effects job, `AeroEntryFxComponent.EntryJob` - the attitude job runs in the server scene only (a null check when nothing is engaged).
- `AeroAutopilotComponent.BlockJob` (server, before the simulation: the block registers with its grid and keeps it awake while a mode is on); its panel's setters (the terminal, host / single player), its save (`AeroBlockSave.cs`).
- `ApToasts.ClientTick` (the client's session tick, `Runtime/AeroClientHost.Frame`, beside QuickLaunch). The client bridge stays per grid in the effects job (one caller).
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
(the planet's height mocked: the query, TerrainWatch's reach, a cliff flown with the rays alone and with the planet), `AvoidPack.cs`
(the closest approach; a parked ship and an asteroid on a route, a six-axis route, an attack run that would ram, a hover's sidestep),
`FormationPack.cs` (a leader in Flight Hold and one or two wingmen in every style: catch-up, the leader's turn and climb, the slot
error's RMS), `SlotPack.cs` (the slots' integration), `RocketPack.cs` (a finned rocket's attitude held by its gimbal and fins).
Several ships in one run: `Tests/AutopilotSim/Flight.cs`; the author's own packs: `Tests/AutopilotSim/UserScenarios/`.

`Tests/AutopilotSim` (in the gate; `-- full` the sweeps): the real core and protection over the game's thrust law and the
mod's allocator / rotor trim / attitude law, on the dumped H25 helicopter (1.5x / 2.5x / 3x, one and two tails), VTOLs, and
the dumped planes on their AeroBench polars; terrain, gusts, noise, lag, mass changes, engine-out, pilot keys, every reason;
the block's panel through the link (`Scenarios/BlockPack.cs`: engage, refusal, let-go write-back, typed height, reference
switch, block dark, a saved mode at load, limits, arbitration); a ledger every tick (every force and torque from a real actuator within its limits). Scenario packs: `IScenarioPack`.
