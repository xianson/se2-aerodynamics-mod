# Grid - the per-grid aero component and its jobs

`AeroGridComponent` (a DCS component on every simulated grid) owns one grid's aero: its surface, wings, force table,
block components, and the forces it applies. It is one `partial` class split by concern:

| File | What |
|---|---|
| `AeroGridComponent.cs` | All fields, lifecycle (`OnAddedToScene` / `OnBeforeRemovedFromScene`), small accessors |
| `AeroGridComponent.Damage.cs` | Block-change signal, local chunk updates after damage (`TickLocal`), seeding a broken-off piece |
| `AeroGridComponent.Compute.cs` | `TryCompute`: rebuild triggers, surface update, wings, the model, components, drag patches |
| `AeroGridComponent.Surfaces.cs` | Control-surface mixer, allocation for the aero-aware attitude law, trim / axis masks, flap angles |
| `AeroGridComponent.Attitude.cs` | `AttitudeTarget` (read by ThrustTorque), the unpiloted hold (damps the rotation only; no remembered attitude: the control surfaces' HOLD rate) |
| `AeroGridComponent.Build.cs` | Full rebuild: scheduler callbacks, the background build (surface, classify, wings, table), the swap |
| `AeroGridComponent.Api.cs` | The tick's copies for AeroApi version 7 (flight state, surfaces, thruster forces, hull faces for `GetSurface`), published only while read (`Api/ApiGridState.cs`) |
| `AeroSimJob.cs` | The jobs: `OnAeroSim` (forces, 60 Hz), `OnAttitude` (ThrustTorque), `OnWindUndo` |
| `AeroDebugDraw.cs` | Render-rate debug draw (pressure, vectors, wings) |
| `AeroSpeedUncap.cs` | The speed-cap job (reads the physics speed cap for the contact loads; render-submission phase) |
| `DragPatches.cs` | Other mods' drag slots (`AeroApi.SetDrag` / `SetPlateDrag`) |
| `AeroScheduler.cs` | Which grids may start a rebuild this frame (global, locked queue) |
| `AeroWork.cs` | The background work queue: rebuilds, local updates and structure evaluations on `Workers` threads, by priority |
| `AeroFrameBudget.cs` | The per-frame budget for force recomputes (grids past it reuse their last forces) |

Other `AeroGridComponent` partials live with their feature: `Structure/Server/StructureServer*.cs`, `StructureContacts`,
`StructureTear`, `WingStrainServer`, `Hud/StallWarning.cs`, `Hud/Loac.cs`, `Hud/AeroStallWarning.cs`, `Fx/AeroFxGroup.cs`,
`Dev/AeroFlightTest.cs`.

## Entry points
- Injected into every grid prefab (`InjectAeroComponents`); `OnAddedToScene` / `OnBeforeRemovedFromScene`.
- DCS jobs: `AeroSimJob` (`OnAeroSim`), `AttitudeJob` (`OnAttitude`), `WindUndoJob` (`OnWindUndo`), `SpeedUncapJob`,
  the debug draw; the block-change signal `OnBlocksChanged`.
- The step in order: `docs/ARCHITECTURE.md` §4.

## Threading
The jobs run on the simulation threads (grids in parallel - shared statics are locked or interlocked).
`AeroWork` items (the build lambda in `Build.cs`, the local update in `Damage.cs`) run on aero's worker threads and only
touch the snapshot they were handed; their results are installed on the simulation thread (`FinalizeStaggeredBuild`,
`TickLocal`). The debug draw runs on the render-submission job and only reads.
