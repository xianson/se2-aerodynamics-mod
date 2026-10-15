# Fx - flight effects and the flow record they are driven from

Client effects are visual only. The flow record is also what `AeroApi` answers from.

| File | What |
|---|---|
| `AeroEntryFx.cs` | The server copy publishes each grid's FLOW (travel, Mach, density, lift, entry strength) and entry heat; entry sources from other mods (the Orbital Mod's railed re-entry) register here. `AeroEntryFxComponent` (client) finds its grid's record and keeps / drops its `FlightField`. Its members are part of the cross-mod contract: Orbital's `tools/check_contract.sh` reads this file. |
| `EntryFxBridge.cs` | The grid's render parent and `FindType` (the game's client / render types are not mod references). |
| `AeroFxGroup.cs` | The ship a grid belongs to (one set of tip trails per ship). |
| `FlightField.cs` | One grid's field: plasma sparks, transonic vapour, tip trails. Knobs (`aeroset FlightField.X`) and state. |
| `FlightField.Host.cs` | The frame: `Update` / `FadeOut`, which groups the regime needs, easing, drops, re-layout carry, the consistency recorder, the aero model read. |
| `FlightField.Emitters.cs` | Where emitters go: the hull shadow's outline, line emitters, local-drag weights, hull normals and spray. |
| `FlightField.Vapour.cs`, `.Tips.cs`, `.Spawn.cs` | Vapour (cone, wingtip lines), tip trails (live wind, spawn line, tracker), the plasma's spawn pass. |
| `FlightField.Shock.cs` | The shock grid (hull shadow along the flow); pure, offline-tested by `Tests/ShockGridTests`. |
| `FlightField.Baked.cs`, `.Swing.cs` | The baked field and the swing between re-layouts. |
| `FlightField.Render.cs` | Particle and spot-light plumbing, late-bound (`docs/REFLECTION_AND_WHITELIST.md`), the emitter pool. |

## Entry points
- Server: `AeroEntryFx.Flow` / `AeroEntryFx.Server` (each step of `AeroGridComponent.AeroSimJobCore`), `AeroEntryFx.Forget` (removal).
- Client: `AeroEntryFxComponent.EntryJob` / `TrailDrawJob` (injected into every grid by `InjectAeroComponents`) ->
  `FlightJob` -> `FlightField.Update`; the same job ticks `MachHud`, `StructureFeel`, `TearSmoke`, `HeatScorch.Sync` (twice a
  second, flight field on or not) and claims the settings tab's 250 ms tick (`Ui/SettingsTab.Claim`).
- Other mods: `AeroApi.RegisterEntrySource` -> `AeroEntryFx.RegisterEntrySource`; `AeroEntryFx.TryGetFlow` (Orbital, by reflection).

## Threading
- Server: `AeroSimJob` (per grid, parallel) calls `AeroEntryFx.Flow` / `Server`; the flow table is locked.
- Client: `AeroEntryFxComponent`'s job (after `RenderSubmissionBegin`) runs the fields; a `FlightField` is touched only from there.
