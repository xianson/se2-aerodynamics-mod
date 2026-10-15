# Design - the whole ship's air force at any attitude

| File | What |
|---|---|
| `DesignProbe.cs` | The whole ship's (grid + jointed subgrids) air force for any flow direction and Mach, as if flying: centre of pressure, static stability, top speed, control ratio, pressure overlay. Also `ServerAero` - the server copy a client grid pairs with - and `PublishDesign` / `PublishPose`, the server grid's own step filling what the probes read. |
| `DesignSnapshot.cs` | Engine-free: a server grid's `DesignSnapshot` (pose, mass and centre, gyros, thrusters at sea level), its `DesignWatch` (the ask window, the overlay's block boxes) and `DesignShip` (the members' figures in the aimed grid's axes, their sums). Run offline by `Tests/SceneTests`. |

## Entry points
- `AeroApi` (`TryDesign`, `GetTopSpeed(At)`, `GetControlRatio`, `GetDesignPressures`...: the Orbital mod's build hints and HUD).
- `DesignProbe.ForcesAt` from the stall and LOAC evaluations (`Hud/`, in the structure's background evaluation).
- `DesignProbe.ServerAero` from `StructureFeel`; `DesignProbe.Forget` from `AeroGridComponent.OnBeforeRemovedFromScene`.
- `DesignProbe.PublishPose` / `PublishDesign` from `AeroSimJobCore` (server scene, every step: one read each while nothing asks).

## Threading
ONE SCENE PER THREAD: the callers are the client's frame (API, overlay), `StructureFeel` and background evaluations; a
server grid's engine data is read only on its own step. `ServerAero` pairs a client grid with the poses the server grids
publish while it is asked (`PublishPose`, 10 a second) - never their entities (`AeroGridComponent.Live` holds both
scenes'). Every probe works on the members' `DesignSnapshot`s: each ask (`ShipOf`) pushes the grid's `DesignWatch` on for
2 s, and while it is on the grid's step takes a new snapshot every 0.25 s (mass, centre of mass, gyros, each thruster's
sea-level maximum and profile, the jointed grids - `RefreshFxGroup` runs there now) and reads the overlay's block boxes
when it asks for a new shape. A snapshot is used up to 1 s old. The first ask after a quiet spell finds none: the probes
answer "not ready" (false / NaN / 0) and the next step has one; a subgrid without one yet in the first 0.5 s makes the
whole ship "not ready", after that it is left out. Snapshots are never changed once published (any thread, no lock); the
caches (`_wingModels`, `_ships`, `_overlays`) are locked; `Forget` drops a grid's on removal. The force table and the
wings are mod state, read as before (`ForcesAt`).
