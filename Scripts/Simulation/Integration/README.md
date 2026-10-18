# Integration - gear and airbrake game side, and a local dev file

What is left here after the folder reorg: the retractable gear's and the airbrake's game components (owned by the gear
/ airbrake work; their pure kinematics are in `Gear/`, their drag in `Components/`), and one local dev file.

| File | What |
|---|---|
| `AeroGearComponent.cs` | Server: a retractable gear on the vanilla suspension - retracts the live wheel physically, collision off in the well; publishes progress (`ThrustTorque.PublishGear`) |
| `AeroGearAnimComponent.cs` | Client: the strut, oleo and doors animated from the published progress |
| `AeroGearBlockDetailModel.cs` | The gear's terminal (Extend / Retract, Toggle on the toolbar) |
| `AeroAirbrakeComponent.cs` | Server: the 3x3x3 airbrake opens with the parking brake to its slider's angle; max opening saved with the block (`Blocks/AeroBlockSave.cs`) |
| `AeroAirbrakeAnimComponent.cs` | Client: its two leaves turned by the published opening |
| `AeroAirbrakeBlockDetailModel.cs` | The airbrake's terminal (max opening) |
| `AeroSpeedSpike.cs` | A local dev file (a teleport-stepping spike; the Orbital harness's `aerospike`) - not part of the shipped feature set; leave it here. (`AeroTestHarness.cs`, the phantom-torque path's scripted scenarios, was removed with that path, 2026-10-17.) |

## Entry points
- Injected by `InjectAeroComponents` (`AeroBlockGuids.Gear*Prefabs`, `Airbrake*Prefabs`).
- Server jobs `OnGear` / `OnAirbrake` (before `Simulation`); client jobs after `RenderSubmissionBegin`.
- `AeroSpeedSpike.Tick` from `AeroGridComponent`'s sim job.

## Threading
Server components on the simulation thread, per block; animators on the client render-submission jobs; gear state
crosses through `ThrustTorque`'s published records (matched by pose).
