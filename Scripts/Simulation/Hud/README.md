# Hud

Cockpit warnings and readouts.

| File | What |
|---|---|
| `StallWarning.cs` | The stall warning's settings (`Hud.StallWarning`). |
| `Loac.cs` | `LoacWarning`: the loss-of-attitude-control warning's settings (`Hud.Loac`). |
| `AeroStallWarning.cs` | Server job, right after the game's thrust-stall report: the game's THRUST OVERLOAD ratio cleared while the wings carry the weight. |
| `GameStallWarning.cs` | Client: the game's own stall warning thresholds set out of reach (reflection; `Hud.GameStallWarning` puts them back). Applied from the settings poll. |
| `MachHud.cs` | `{Aero:Mach}` appended to the speedometer templates; ticked from the client fx job. |

Reflection here is in `docs/REFLECTION_AND_WHITELIST.md`.
