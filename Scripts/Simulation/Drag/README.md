# Drag - hull forces (engine-free)

`DampedShadowedDragModel` is the hull model: per-face Newtonian / bluff pressure with shadowing, rotation damping and
lee-side pressure recovery. It is split by concern: `.cs` (tuning, the per-face arrays), `.Recovery.cs` (pressure
recovery), `.Table.cs` (the force table build and the overlay's pressures), `.Compute.cs` (the per-frame face loop,
used only before a grid has its table).

`ForceTable` (forces for every flow direction, a lookup per frame), `ChunkedTable` (the table in chunks, for local
updates after damage), `AeroTableCache` (built tables on disk by shape), `OrphanChunks` (a broken-off piece flies on
its parent's shares until its own build).

The table sources are hashed by `Tests/AeroBench` (`Tests/table_sources.txt`): a change to how tables are built must
bump `AeroTableCache.Version`; a pure refactor re-records the hash (`dotnet run -- tablehash`).
Threading: a model instance belongs to one grid (sim thread) or one build (a worker); table builders are pooled.
