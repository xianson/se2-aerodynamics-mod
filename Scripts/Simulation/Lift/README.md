# Lift - wings and the whole ship's lift (engine-free)

Mechanics use the whole grid's air force; wings never gate a mechanic.

| File | What |
|---|---|
| `ConnectedComponentWingDetector.cs` | Finds wings in the grid's cells (low-aspect plates too: `LowAspectRatio`) |
| `.Analysis.cs`, `.Columns.cs`, `.Incremental.cs`, `.Merge.cs` | The flood-fill path and each component's planform (the default); the planform-column detector (`ColumnMode`, off); updates after block changes; coplanar wings joined |
| `LiftingSurfaceModel.cs` | Wraps the hull drag model and the wing lift model: the grid's whole model (aero-2c's) |
| `CompressibleWingModel.cs` | Wing lift and drag (Mach effects: drag due to lift, its own wave drag); the one `IWingLiftModel`, also `LiftingSurfaceModel`'s default. `IWingLiftModel`, `IWingDetector`, `LiftingSurface`, `WingForceResult` are the shared types |
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

## Wave drag (2026-10-18, fix a-wing-wave)
Wings had no wave drag of their own: only the hull's one multiplier by streamlining (`ForceTable.HullWave`), so a thick
straight slab crossed Mach 1 as cheaply as a thin swept blade. `CompressibleWingModel.WingWaveDrag` (on; false: off for
the A/B) gives each wing, per tick, from its thickness ratio, this tick's leading-edge sweep L (the air's - "Which edge
leads") and its CL:
- **Drag divergence (Korn):** M_dd cos L = `KornKappa` (0.87, a conventional section) - (t/c) / cos L - |CL| / (10 cos^2 L);
  the normal component floored at 0.4.
- **Transonic rise (Lock):** dCd = 20 (M - M_crit)^4, M_crit = M_dd - (0.1/80)^(1/3) (slope 0.1 at M_dd: 0.0027 there).
- **Supersonic (Ackeret, simple sweep theory):** Cd = `WaveK` (t/c)^2 cos L / sqrt(Mn^2 - 1), Mn = M cos L (the normal
  section's 2D wave drag K (t/c_n)^2 / beta_n, x cos^3 L). `WaveK` 16/3, a biconvex section (the double wedge, the minimum
  for its thickness, is 4; a flat-topped hexagon with ramps over a fraction f of the chord is 2/f).
- **The singularity at Mn = 1:** linear theory holds while beta^2 >> ((gamma + 1) tau)^(2/3) (transonic similarity, tau
  the normal t/c), so Lock's rise is capped at the Ackeret value at beta_cap = ((gamma + 1) tau)^(1/3) - a peak
  ~ tau^(5/3) (NACA 0012: ~0.12 against ~0.1 measured at Mach 1) - and handed to Ackeret by a smoothstep from Mn 1 to
  sqrt(1 + beta_cap^2), where both equal the cap: no jump anywhere (`wingwave` samples every 0.0005).
- **A subsonic leading edge at M > 1** (Mn < 1) gets no Ackeret term - its normal flow is subsonic - only Lock's rise
  against Korn's swept M_dd, which stays small while Mn < Mn_dd; the volume's wave drag at M > 1 stays the hull's.
- **t/c is the wing's own** (`LiftingSurface.OwnThickness`: its columns' mean depth between its two skins over its
  rows' mean chord extent). The detector's `ThicknessRatio` takes each merged piece's thickness from every occupied
  cell across it: 0.3-0.5 on most block wings (the F-6's 0.28-0.42, its own 0.12-0.19; the jetliner's main wing 0.19,
  its own 0.07). `ThicknessRatio` still sets `CLMax`, and a wing given by hand (no cells: the bench's) takes it; a
  detected wing never measured (no live surface at its detection) gets no wave drag of its own.

**No double count.** A wing's leading-edge, trailing-edge and tip faces are HULL faces (only its skins are left out,
`ExcludeWingFaces`), and the hull's speed-of-sound law charges them: the bluff Cp subsonic, the Newtonian one (CpMax)
supersonic, x its wave multiplier. The detector measures each wing's edge faces on the surface it detected on
(`ConnectedComponentWingDetector.MeasureEdges`, into `LiftingSurface.EdgeLo` / `EdgeHi` - saved with the wing in the table
cache: a big or cached grid's surface is released after its build): P = sum of area x cos^3 over the windward faces of its
columns that are not skins, for each end leading - the face model charges
CdBluff x P subsonic (square law) and CpMax x P at its Newtonian end. Per tick `HandHullEdges` hands over the hull's
own blend t and wave multiplier (`ForceTable.NewtonBlend`, `HullWave`, at the ship's streamlining along the flow - the
flight path and `DesignProbe.ForcesAt` both), and the wing adds only theory - P (wave (r CdBluff (1 - t) + CpMax t) -
r CdBluff) / S, never below 0; r = `EdgeBluffKept` 0.4, the share of the bluff charge pressure recovery leaves
subsonic. Measured on bench wings (6 m chord, 15 m span; `dotnet run -c Release -- wingwaveprobe`; hull = the face model
on the edges, CD on the planform):

| bench wing (own t/c) | Mach | hull edges | their rise | guard's estimate | theory | wing adds |
|---|---|---|---|---|---|---|
| blocky 2 cells (0.083) | 0.9 | 0.109 | 0.076 | 0.073 | 0.048 | 0 |
| | 1.2 | 0.172 | 0.139 | 0.158 | 0.056 | 0 |
| | 2.0 | 0.118 | 0.085 | 0.104 | 0.021 | 0 |
| blocky 4 cells (0.167) | 0.9 | 0.243 | 0.170 | 0.162 | 0.172 | 0.009 |
| | 2.0 | 0.262 | 0.189 | 0.232 | 0.086 | 0 |
| stepped 1 -> 2 cells (0.069) | 0.9 | 0.012 | 0.001 | 0.001 | 0.047 | 0.046 |
| | 1.2 | 0.012 | 0.000 | 0.000 | 0.039 | 0.038 |
| | 2.0 | 0.020 | 0.009 | 0.009 | 0.015 | 0.006 |
| | 3.0 | 0.056 | 0.044 | 0.043 | 0.009 | 0 |

A square edge's rise is already 1-3x the thickness wave drag through the transonic band and 2-25x supersonic (a blunt
edge IS draggier than a sharp section): the wing adds nothing, or a little where the hull's blend has not caught up yet
(the 4-cell slab at Mach 0.9: 0.009). A stepped edge (smoothed to a ramp, a sleek hull holding its subsonic recovery through the jet band)
is charged almost nothing until Mach 1.6: the wing adds the theory value, and less as the hull's Newtonian blend comes
in (none by Mach 3). The estimate is within 5 % of the measured rise where it matters (the sleek band) and above it on
square edges (the guard errs toward adding less).

## Supersonic drag due to lift (2026-10-18, fix a-wing-wave)
CDi was CL^2 / (pi e AR) at every Mach - full leading-edge suction - so supersonic lift came far too cheap.
`CompressibleWingModel.SupersonicLiftDrag` (on; false: the old formula at every Mach):
CDi = CL^2 [k_sub + (1/CLa - k_sub) s], k_sub = 1/(pi e AR) x the ground factor (as before), 1/CLa the drag of lift
with no suction (the force normal to the plate, CL x alpha - with the supersonic slope 4/beta, CL^2 sqrt(M^2 - 1)/4 for
a supersonic leading edge), CLa this wing's own runtime slope at its leading-edge Mach (the lift's), s the suction lost:
a smoothstep in M_perp over the lift slope's own blend band (`SubsonicBlendLimit` 0.85 .. `SupersonicBlendLimit` 1.15,
as `ComputeCLAlpha`), and for a subsonic leading edge in supersonic flight at least `SubsonicEdgeSuctionLoss` (0.5) of
it (smoothstep in the free-stream Mach over the same band) - linear theory keeps some suction on a swept edge, a real
(and a blocky) one loses much of it. Past the stall the no-suction share fades as the separated flow's flat-plate drag
(`cdSep`, the same normal force) takes over. Never below k_sub. Subsonic: unchanged exactly. AeroBench: a straight
AR 3 wing at 3 deg, Mach 1.5: CDi 0.0083 (CL^2/CLa) where the old formula gave 0.0028; a 60-degree wing at Mach 1.5
(M_perp 0.75) 0.0053, between its 0.0026 and 0.0080.

What it does to a ship (AeroBench `machwall`, the F-6: straight wings, own t/c 0.12-0.19): T/W to break Mach 1 at sea
level 1.94 -> 3.36, at 0.8 kg/m3 1.15 -> 1.99, at 0.4 kg/m3 1.46 -> 2.53, its bottleneck still Mach 1.01 and on that
thrust on to Mach 1.73; its drag at wing CL 0.5 and Mach 1.5 1382 -> 2472 kN (wing drag 162 -> 1252 kN: CDi 0.018 ->
0.077, wave 0 -> 0.108); at Mach 0.6 unchanged. The Tu-134 at Mach 0.79: 706 -> 886 kN (its t/c 0.16 wings past their
Korn M_crit ~0.6). The machwall gate keeps the 2026-10-17 goals on the hull's wall (the wing terms off) and checks the
whole model against its own (3.6 / 2.7). `Tests/AeroBench` `wingwave` (in the gate): Korn exactly, Lock's slope 0.1
at M_dd; thin swept under thick straight at Mach 0.9 / 1.2 / 2; supersonic CDi over the subsonic formula, ~CL^2/CLa;
no jumps; no double count on the blocky and stepped bench wings at Mach 2; finite everywhere; mirrored wings the same.

## Entry points
`AeroGridComponent` makes one `LiftingSurfaceModel` per grid (`OnAddedToScene`) and calls `Compute`, `DetectWings`,
`UpdateWings` (`TryCompute`, the background build). `LiftPeak`: `AeroGridComponent.EvaluateStall`. `WingStrain`:
`WingStrainServer`.

## Threading
As `Drag/`: a model belongs to one grid (simulation thread) or one build (a worker).
