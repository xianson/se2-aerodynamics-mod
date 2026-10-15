# Design - the whole ship's air force at any attitude

| File | What |
|---|---|
| `DesignProbe.cs` | The whole ship's (grid + jointed subgrids) air force for any flow direction and Mach, as if flying: centre of pressure, static stability, top speed, control ratio, pressure overlay. Also `ServerAero` - the server copy a client grid pairs with. |

## Entry points
- `AeroApi` (`TryDesign`, `GetTopSpeed(At)`, `GetControlRatio`, `GetDesignPressures`...: the Orbital mod's build hints and HUD).
- `DesignProbe.ForcesAt` from the stall and LOAC evaluations (`Hud/`, in the structure's background evaluation).
- `DesignProbe.ServerAero` from `StructureFeel`; `DesignProbe.Forget` from `AeroGridComponent.OnBeforeRemovedFromScene`.

## Threading
Pure reads of a grid's current shape; called from client jobs (API) and the structure's background evaluation. Its
caches (`_wingModels`, `_ships`, `_overlays`) are locked; `Forget` drops a grid's on removal. `ServerAero` pairs a client
grid with the poses the server grids publish from their own step while it is asked (`PublishPose`, 10 a second) - never
their entities (`AeroGridComponent.Live` holds both scenes'). The probes themselves (`ShipOf`, `ShipMass`, `Forces`,
`ThrustAlong`, `ControlRatio`, the overlay's `CaptureBoxes`) still read the server copy's entity from the caller's thread:
known, to move onto published data (2026-10-15 scene audit).
