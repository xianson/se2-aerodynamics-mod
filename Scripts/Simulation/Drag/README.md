# Drag - hull forces (engine-free)

| File | What |
|---|---|
| `DampedShadowedDragModel.cs` | The hull model: per-face Newtonian / bluff pressure with shadowing, rotation damping, lee-side recovery; tuning switches and the per-face arrays |
| `DampedShadowedDragModel.Recovery.cs` | Pressure recovery (`RecoveryPasses` lattices) |
| `DampedShadowedDragModel.Table.cs` | The force table build and the overlay's pressures |
| `DampedShadowedDragModel.Compute.cs` | The per-frame face loop, used only before a grid has its table |
| `ForceTable.cs` | Forces for every flow direction (486): a lookup per frame. Its Mach law (the face loop's too, ExactTableValue): F = q x `HullWave` x lerp(F0, F1, t x (1 - `BlendHold`)) - the streamlining sets the onset, the wave peak (`WavePeak`: 3.2x for a brick down to `WavePeakNeedle` 1.12x for a sleek hull - 1.45x until 2026-10-17's Mach wall fix), where it peaks (`WavePeakMach`) and how fast it falls (`WaveFall`); a sleek hull (streamlining past `SleekFrom` 0.5) holds its subsonic recovery through the jet band (`BlendHold`, Mach 0.75-3). Below Mach 0.75 the old law exactly (Tests/AeroBench machwall). |
| `ChunkedTable.cs` | The table in 8 m chunks, for local updates after damage (the full build's occlusion rule; each chunk's pressure-recovery push kept from the full build, which a local update cannot redo) |
| `AeroTableCache.cs` | Built tables on disk by shape (`%TEMP%\AeroMod\tables`), `Version`, `ModelSwitches` |
| `OrphanChunks.cs` | A broken-off piece flies on its parent's shares until its own build |
| `IAeroDragModel.cs` | The model interface |

## Entry points
`LiftingSurfaceModel` (`Lift/`) wraps the hull model; `AeroGridComponent.StartBackgroundBuild` builds / loads tables;
`AeroGridComponent.TickLocal` installs chunk updates; `AeroApi.PredictHullForces` / `SampleForceTable` read tables.

The table sources are hashed by `Tests/AeroBench` (`Tests/table_sources.txt`): a change to how tables are built must
bump `AeroTableCache.Version`; a pure refactor re-records the hash (`dotnet run -- tablehash`).

## Threading
A model instance belongs to one grid (sim thread) or one build (a worker); table builders are pooled
(`AeroGridComponent.RentTableBuilder`, locked). A table build splits its directions over `AeroWork.ThreadsForJob`.
