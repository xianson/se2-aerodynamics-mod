# Core - shared types of the engine-free model

| File | What |
|---|---|
| `AeroContext.cs` | Everything a model gets each compute (velocity, spin, air, centre of mass, ground, surfaces) |
| `AeroResult.cs` | What a model returns (force, torque, drag, lift, frontal area, Mach, q) |
| `IGridAccessor.cs` | Cells of a grid |
| `SnapshotGridAccessor.cs` | Cells from captured block boxes: the background builds and the offline tests; cell scale for large grids |
| `Se2GridAccessor.cs` | Cells from the live block octree - the one file here that touches SE2 types |
| `LongKey.cs` | The hash for packed long keys |
| `CellCoords.cs` | Cells to grid-local metres as the game has them (block centres: thruster arms) |
| `AeroLog.cs` | The log switches: `AeroLog.Verbose` (periodic traces, off) and `AeroLog.Detail` (per-grid / per-build lines; on only with the dev flag `%TEMP%\OrbitalMod\dev.flag`) |

## Entry points
Built by `AeroGridComponent.TryCompute` (`AeroContext`) and the rebuild (`SnapshotGridAccessor`); consumed by every model folder.

## Threading
No engine types except `Se2GridAccessor.cs` (the test projects compile this folder without it). Thread-agnostic: each
instance belongs to one build or one grid.
