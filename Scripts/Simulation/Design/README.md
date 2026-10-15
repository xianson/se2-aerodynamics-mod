# Design

| File | What |
|---|---|
| `DesignProbe.cs` | The whole ship's (grid + jointed subgrids) air force for any flow direction and Mach, as if flying: centre of pressure, static stability, top speed, control ratio, pressure overlay. Also `ServerAero` - the server copy a client grid pairs with. |

Entry points: `AeroApi` (build hints, the Orbital mod's HUD), the stall warning and LOAC evaluations
(`DesignProbe.ForcesAt`, in the structure's background evaluation), `StructureFeel` (`ServerAero`).

## Threading
Pure reads of a grid's current shape; called from client jobs (API) and the structure's background evaluation. Its
caches (`_wingModels`, `_ships`, `_overlays`) are locked; `Forget` drops a grid's on removal.
