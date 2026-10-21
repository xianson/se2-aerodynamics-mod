# Integration - gear and airbrake game side, and a local dev file

What is left here after the folder reorg: the retractable gear's and the airbrake's game components (owned by the gear
/ airbrake work; their pure kinematics are in `Gear/`, their drag in `Components/`), and one local dev file.

| File | What |
|---|---|
| `AeroGearComponent.cs` | Server: a retractable gear on the vanilla suspension - retracts the live wheel physically, collision off in the well; publishes progress (`ThrustTorque.PublishGear`) |
| `AeroGearAnimComponent.cs` | Client: the strut, oleo and doors animated from the published progress |
| `AeroGearBlockDetailModel.cs` | The gear's terminal (Extend / Retract, Toggle on the toolbar) |
| `AeroGearComponent.PadFoot.cs` | Server: the pad-foot gears - the foot's box collider and its orientation lock (6-DOF, the axle only), made per wheel. Pure core: `Gear/PadFoot.cs`; content: `tools/meshgen/gen_padgear_defs.py` |
| `AeroAirbrakeComponent.cs` | Server: the 3x3x3 airbrake opens with the parking brake to its slider's angle; max opening saved with the block (`Blocks/AeroBlockSave.cs`) |
| `AeroAirbrakeAnimComponent.cs` | Client: its two leaves turned by the published opening |
| `AeroAirbrakeBlockDetailModel.cs` | The airbrake's terminal (max opening) |

## Entry points
- Injected by `InjectAeroComponents` (`AeroBlockGuids.Gear*Prefabs`, `Airbrake*Prefabs`).
- Server jobs `OnGear` / `OnAirbrake` (before `Simulation`); client jobs after `RenderSubmissionBegin`.

## Threading
Server components on the simulation thread, per block; animators on the client render-submission jobs; gear state
crosses through `ThrustTorque`'s published records (matched by pose).
