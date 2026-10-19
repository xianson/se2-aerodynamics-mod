# Autopilot - hover hold, height hold, envelope protection

One core for every autopilot user: goals -> guidance -> protection -> the seam (what a seated pilot writes: a target
attitude, the movement keys) -> the ship's own aero-aware attitude law and allocator (`Propulsion/ThrustTorque.cs`), which
fly it through the real gyros, thrusters, rotors' cyclic and control surfaces. The autopilot never applies a force or a torque.

| File | What |
|---|---|
| `FlightDirector.cs` | The core (engine-free): goals (`ApGoal`: attitude, hover, position, height, external), hover's speed loop -> lean (the integral finds a single tail's trim lean), height hold over the ground or along the gravity, the external goal source (`Track`, `Accel` flown with the lift - `LiftFlight` - or the thrust, `Attitude`), terrain response, the watch (reasons for letting go), the readout and words (`ApText`) |
| `EnvelopeGuard.cs` | The reference governor (engine-free): stall margin (LiftPeak's), g limit, the predicted loss of control for the command before it is given; a bank never cut by the stall caps (the step's roll about the flow kept) |
| `ApTerrain.cs` | `ITerrainQuery` (the ground below, a ray along the path) and `TerrainWatch` (height over the ground - its shape filtered, the ship's own motion not -, the ground's rise, the time to impact straight and along the bent path) |
| `ApPerf.cs` | The flight performance (engine-free): `PerfSamples` (the stall sweep's lift and drag over the angle of attack), `FlightPerf` (load, turn rates, drag, excess thrust, speed after a pull, the angle for a lift) |
| `ApTypes.cs` | `ApSense` (what the instruments show), `ApCommand` (the seam), the enums, `ApExternal` / `IApGoalSource`, `ApMath`, `LoacMath` (Loac.cs's arithmetic), `SideAuthority` |
| `ApConfig.cs` | The settings' fields (`Autopilot.*`) and `ApTuning` (the one constant the game and the sim share) |
| `Game/AeroGridComponent.Autopilot.cs` | Per grid: the server's flight director each tick before ThrustTorque (`AutopilotStep` from `AeroSimJob.AttitudeJob`), its instruments (`ApSenseNow`), the keys written in place (`ControlData.Movement`), the record (`ThrustTorque.ApPub`) - the client copy flies the same target and hands the server the pilot's keys and steering |
| `Game/GridTerrain.cs` | The terrain in game: `IPhysics.CastRay`, synchronous, own buffers (voxel colliders exist only near entities: a miss is "not known") |
| `Game/ApKeys.cs` | The keys (client: Ctrl+H hover, Ctrl+J height) and their ask (server: the asking player's own seat's grid), the toasts |

## Entry points
- `AeroGridComponent.AutopilotStep` (AttitudeJob, both scenes; a null check when nothing is engaged).
- `ApKeys.ClientTick` (the client effects job, beside QuickLaunch), `AeroWorldSettingsComponent.OnAutopilotAsk` (server, before the simulation).
- `AeroApi.TryGetAutopilot` / `RequestAutopilot` / `TryGetFlightPerformance` (version 11).
- The seam inside ThrustTorque: `AttitudeTarget` returns the autopilot's target first (`ApFlying`); the aware law flies it in any air, the game's gyro law taken back (the rate law's impulse for an unpiloted grid: `GyroControl.Undo(..., targetLaw: false)`).
- Inputs from the background evaluation (Structure/Server): the stall sweep's lift and drag and lift direction (`Hud/StallWarning.cs`), the loss of control predicted at the autopilot's probe (`Hud/Loac.cs EvaluateApProbe`); both run while it flies, whatever the HUD settings.

## Threading
The flight director: the server grid's attitude job only. The record: under its own lock (a few fields a tick). The stall
sweep's samples and the performance box: written by the background evaluation into a back buffer, published by a reference
swap (a reader holds one for a tick). Keys and toasts: as quick launch (an ask swapped whole, stamped, taken once).

## Tests
`Tests/AutopilotSim` (in the gate; `-- full` the sweeps): the real core and protection over the game's thrust law and the
mod's allocator / rotor trim / attitude law, on the dumped H25 helicopter (1.5x / 2.5x / 3x, one and two tails), VTOLs, and
the dumped planes on their AeroBench polars; terrain, gusts, noise, lag, mass changes, engine-out, pilot keys, every reason;
a ledger every tick (every force and torque from a real actuator within its limits). Scenario packs: `IScenarioPack`.
