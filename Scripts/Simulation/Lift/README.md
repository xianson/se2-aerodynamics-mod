# Lift - wings and the whole ship's lift (engine-free)

`ConnectedComponentWingDetector` finds wings in the grid's cells: `.Analysis.cs` (the flood-fill path and each
component's planform - the default), `.Columns.cs` (the planform-column detector, `ColumnMode`, off), `.Incremental.cs` (after block
changes), `.Merge.cs` (coplanar wings joined). `LiftingSurfaceModel` (aero-2c's) wraps the drag model and the wing
lift model (`CompressibleWingModel`). `LiftPeak` is the whole ship's stall (the stall warning), `WingStrain` the
wings' root loads. `GroundEffect.cs` belongs to the gear/airbrake work.

Mechanics use the whole grid's air force; wings never gate a mechanic. Threading: as Drag/.
