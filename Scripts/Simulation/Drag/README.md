# Drag - hull forces (engine-free)

| File | What |
|---|---|
| `DampedShadowedDragModel.cs` | The hull model: per-face Newtonian / bluff pressure with shadowing, rotation damping, lee-side recovery; tuning switches and the per-face arrays |
| `DampedShadowedDragModel.Recovery.cs` | Pressure recovery (`RecoveryPasses` lattices) |
| `DampedShadowedDragModel.Table.cs` | The force table build and the overlay's pressures |
| `DampedShadowedDragModel.Compute.cs` | The per-frame face loop, used only before a grid has its table |
| `ForceTable.cs` | Forces for every flow direction (486): a lookup per frame |
| `ChunkedTable.cs` | The table in 8 m chunks, for local updates after damage |
| `AeroTableCache.cs` | Built tables on disk by shape (`%TEMP%\AeroMod\tables`), `Version`, `ModelSwitches` |
| `OrphanChunks.cs` | A broken-off piece flies on its parent's shares until its own build |
| `IAeroDragModel.cs` | The model interface |
| `NewtonianDragModel.cs`, `AxisCdDragModel.cs` | Simpler models, never constructed today (`NewtonianDragModel` appears only in a type check in `LiftingSurfaceModel`) |

## Entry points
`LiftingSurfaceModel` (`Lift/`) wraps the hull model; `AeroGridComponent.StartBackgroundBuild` builds / loads tables;
`AeroGridComponent.TickLocal` installs chunk updates; `AeroApi.PredictHullForces` / `SampleForceTable` read tables.

The table sources are hashed by `Tests/AeroBench` (`Tests/table_sources.txt`): a change to how tables are built must
bump `AeroTableCache.Version`; a pure refactor re-records the hash (`dotnet run -- tablehash`).

## Threading
A model instance belongs to one grid (sim thread) or one build (a worker); table builders are pooled
(`AeroGridComponent.RentTableBuilder`, locked). A table build splits its directions over `AeroWork.ThreadsForJob`.
