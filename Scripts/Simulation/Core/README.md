# Core - shared types of the engine-free model

`AeroContext` (what a model gets each compute), `AeroResult` (what it returns), `IGridAccessor` (cells of a grid),
`SnapshotGridAccessor` (cells from captured block boxes: the background builds and the offline tests), `LongKey`
(the hash for packed long keys), `AeroLog`. `Se2GridAccessor` is the one file here that touches SE2 types.

No engine types except `Se2GridAccessor.cs` (the test projects compile this folder without it). Thread-agnostic:
each instance belongs to one build or one grid.
