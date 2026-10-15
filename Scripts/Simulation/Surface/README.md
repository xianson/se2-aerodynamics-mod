# Surface - the grid's skin (engine-free)

`SmoothSurfaceProvider` builds the exposed faces of the grid's cells with smoothed normals: `.cs` (state, the full and
staggered builds), `.Incremental.cs` (block changes, raw faces), `.Normals.cs` (vertex-normal diffusion),
`.Topology.cs` (adjacency, creases, the six direction groups). `ManifoldClassifier` marks faces hull or sealed cavity.
`SurfaceFace`, `NormalGroup`, `ISurfaceProvider` are the shared types.

Threading: a surface belongs to one grid; a background build fills the spare one on a worker, swapped in on the
simulation thread.
