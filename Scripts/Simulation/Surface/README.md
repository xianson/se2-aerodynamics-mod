# Surface - the grid's skin (engine-free)

| File | What |
|---|---|
| `SmoothSurfaceProvider.cs` | The exposed faces of the grid's cells with smoothed normals: state, the full and staggered builds |
| `SmoothSurfaceProvider.Incremental.cs` | Block changes, raw faces |
| `SmoothSurfaceProvider.Normals.cs` | Vertex-normal diffusion |
| `SmoothSurfaceProvider.Topology.cs` | Adjacency, creases, the six direction groups |
| `ManifoldClassifier.cs` | Marks faces hull or sealed cavity |
| `CubeSurfaceProvider.cs` | The plain cube-face provider (no callers today) |
| `SurfaceFace.cs`, `NormalGroup.cs`, `ISurfaceProvider.cs` | The shared types |

## Entry points
`AeroGridComponent` keeps two (`_surface` live, `_buildSurface` the spare): the background build fills the spare
(`StartBackgroundBuild`), small changes go through `OnBlocksChanged` (`TryCompute`). Offline: `Tests/SurfaceTests`.

## Threading
A surface belongs to one grid; a background build fills the spare one on a worker, swapped in on the simulation thread.
