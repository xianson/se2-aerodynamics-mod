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
caches (`_wingModels`, `_ships`, `_overlays`) are locked; `Forget` drops a grid's on removal.
