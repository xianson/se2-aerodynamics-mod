# Debris - drag on the game's loose fracture pieces

A block that breaks throws its pieces out as loose bodies, not grids (the game's `FractureDebris_Client` prefab:
`FracturedBodyComponent`, `DebrisComponent`, `FractureWindBindingComponent`, an 8-10 s timeout, at most 100 alive).
Aero's grid model never sees them; the game only pushes them with the wind (none in still air). This folder gives
them drag.

| File | What |
|---|---|
| `AeroDebrisDrag.cs` | The job: every piece with the wind binding, after the game's wind push, before physics - quadratic drag against the air (the wind taken off, the game's own push counted once) and a little on the spin. Knobs: `Enabled`, `DragScale` (Cd, 0.4: "slight"), `SpinDragScale` (1); settings `Debris.Drag`, `Debris.DragScale`. Diagnostics: `Steps`, `LastDv` |
| `DebrisDragMath.cs` | Engine-free: projected area of the box, the step's exact quadratic drag (never past zero), the composition with the game's wind, spin damping. `Tests/DebrisTests` runs it as is |

Not covered: the damaged-model debris of non-breakable blocks (`DamagedBlockDebrisPrefab`: no wind binding, so no
air density and no `SimulatedComponent`) and flora debris.

## Entry points
`AeroDebrisDrag.DebrisDragJob` (a DCS job, `[WhenSimulated]`, `[MustHave(FractureWindBindingComponent)]`).

## Threading
Simulation jobs, pieces in parallel, CLIENT scene only (the game's `DebrisSpawnerSessionComponent` spawns debris
there; the server never has any). Each call writes only its own piece's body; the statics are read-only but the two
diagnostics (plain writes).
