# Lift - wings and the whole ship's lift (engine-free)

Mechanics use the whole grid's air force; wings never gate a mechanic.

| File | What |
|---|---|
| `ConnectedComponentWingDetector.cs` | Finds wings in the grid's cells (low-aspect plates too: `LowAspectRatio`) |
| `.Analysis.cs`, `.Columns.cs`, `.Incremental.cs`, `.Merge.cs` | The flood-fill path and each component's planform (the default); the planform-column detector (`ColumnMode`, off); updates after block changes; coplanar wings joined |
| `LiftingSurfaceModel.cs` | Wraps the hull drag model and the wing lift model: the grid's whole model (aero-2c's) |
| `CompressibleWingModel.cs` | Wing lift and induced drag (Mach effects); the one `IWingLiftModel`, also `LiftingSurfaceModel`'s default. `IWingLiftModel`, `IWingDetector`, `LiftingSurface`, `WingForceResult` are the shared types |
| `GroundEffect.cs` | Ground effect for every kind of lift (belongs to the gear / airbrake work) |
| `LiftPeak.cs` | The whole ship's stall: the best lift within reach (the stall warning) |
| `WingStrain.cs` | The wings' root loads (`Tests/WingStrainTests`) |

## Entry points
`AeroGridComponent` makes one `LiftingSurfaceModel` per grid (`OnAddedToScene`) and calls `Compute`, `DetectWings`,
`UpdateWings` (`TryCompute`, the background build). `LiftPeak`: `AeroGridComponent.EvaluateStall`. `WingStrain`:
`WingStrainServer`.

## Threading
As `Drag/`: a model belongs to one grid (simulation thread) or one build (a worker).
