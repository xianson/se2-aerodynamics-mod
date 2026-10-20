# Hud - cockpit warnings and readouts

The stall and LOAC warnings are computed here and drawn by the Orbital mod's flight HUD (`AeroApi.TryGetStall` /
`TryGetLoac`); aero's own HUD pieces are the game's thrust-overload warning and the optional Mach readout.

| File | What |
|---|---|
| `StallWarning.cs` | The stall warning: settings (`Hud.StallWarning`) and `AeroGridComponent.EvaluateStall` - the whole ship's best lift within reach (`Lift/LiftPeak`, `DesignProbe.ForcesAt`) |
| `Loac.cs` | `LoacWarning` settings (`Hud.Loac`) and `AeroGridComponent.EvaluateLoac` - the predicted loss of attitude control on the way to the commanded attitude; `SurfaceTravel` (the surfaces' reach) |
| `AeroStallWarning.cs` | Server job, right after the game's thrust-stall report: the game's THRUST OVERLOAD ratio cleared while the wings carry the weight |
| `GameStallWarning.cs` | Client: the game's own stall warning thresholds set out of reach (reflection; `Hud.GameStallWarning` puts them back). Applied from the settings poll |
| `MachHud.cs` | `{Aero:Mach}` appended to the speedometer templates (`Hud.Mach`, off by default) |

## Entry points
- `EvaluateStall` / `EvaluateLoac`: `StructureServer.EvaluateStructure` (background, player-flown grids only).
- `AeroStallJob` (`OnAeroStall`, after `GridMovementCollectorComponent.ThrusterUpdate.ReportStall`).
- `GameStallWarning.Apply` (`AeroSettings`), `MachHud.Tick` (`AeroClientHost.Frame`, the client's session tick - grid or no grid).

## Threading
- Background (`AeroWork`): the two evaluations write the grid's own `StallState` / `LoacState` fields; `AeroApi` reads them.
- Server simulation thread: `AeroStallJob`. Client effects job: `MachHud.Tick` (the UI thread reads its suffix, a string reference).
- Reflection here is in `docs/REFLECTION_AND_WHITELIST.md`.
