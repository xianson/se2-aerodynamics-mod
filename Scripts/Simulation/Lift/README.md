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

## Which edge leads (2026-10-18, fix a-wing-edge)
The detector cannot know which way a ship flies: a wing's `ChordAxis` is a positive grid axis, unsigned. So a
`LiftingSurface` is orientation-free: its mid-chord `Centroid`, `MeanChord`, `AcOffset` (the quarter chord's distance
from the centroid, 0.25 x the mean chord) and both chord edges' sweeps, `SweepLo` (the edge at the low end of
`ChordAxis`) and `SweepHi` (the high end); `SweepAngle` is only their mean, for the precomputed coefficients and logs.
`CompressibleWingModel` picks the leading edge per wing per tick from the air: the lead is the chordwise component of
the wing's motion through the air, +1 toward +`ChordAxis` (the high end leads), -1 toward -`ChordAxis`. The aero centre
sits a quarter chord from that edge (`AeroCenterFor(lead)`), that edge's sweep (`SweepFor(lead)`) sets M_perp and the
lift slope, the camber is read from that end, and the AoA and Mach centre-of-pressure shifts run downstream (toward the
trailing edge). Within `LeadBlend` (0.1) of the wing's speed the lead goes linearly through 0 - near-spanwise flow
(sideslip, a flat fall) slides the force point through the mid chord instead of flipping it; no state. Each
`WingForceResult` carries its `Lead`; the API's `TryGetWing` / `GetWingTips` report the last tick's edges. Before, the
low end always led: a ship flying toward +`ChordAxis` (the jetliner, nose +X) had its wing lift at 3/4 chord, its Mach
tuck forward and its sweep from the trailing edge. `Tests/AeroBench` `wingedge` (in the gate): a swept, tapered wing
built mirrored along X and along Z, and the Pelican mirrored along its nose - the same lift, quarter chord, sweep and
pitching moment, the tuck aft in both.

## Entry points
`AeroGridComponent` makes one `LiftingSurfaceModel` per grid (`OnAddedToScene`) and calls `Compute`, `DetectWings`,
`UpdateWings` (`TryCompute`, the background build). `LiftPeak`: `AeroGridComponent.EvaluateStall`. `WingStrain`:
`WingStrainServer`.

## Threading
As `Drag/`: a model belongs to one grid (simulation thread) or one build (a worker).
