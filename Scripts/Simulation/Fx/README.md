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
| `ExhaustLaw.cs` | THE EXHAUST'S LAW (2026-10-17), game-free (`Tests/ExhaustTests`): what a ramjet's and a solid rocket nozzle's exhaust shows. Ramjet: an almost clear blue-violet flame, nothing below Mach 0.5 (gated in to 0.65), its strength the thrust it gives (share x its thrust factor), its length with it; shock diamonds from Mach 1.1, more (up to 5 at Mach 4), sharper and brighter with the Mach, spaced 1.2 + 0.5 sqrt(M^2 - 1) exit radii; an inlet unstart pops (x1.8 for 70 ms), sputters 0.35 s and drops out until it relights. Solid: the plume's length 7 x (thrust / one segment's)^0.5 exit radii (stack x level x steering), the vacuum balloon and the dimmed core as the air thins, the smoke thinning away (none below 2 % of sea-level pressure), a 0.7 s sputter at burnout; the smoke's life / rate cap. Knobs `aeroset ExhaustLaw.X`. |
| `AeroExhaustComponent.cs` | Client, the ramjets' and solid nozzles' client prefabs (EntityInjector): feeds `ExhaustLaw` from the block's own components (`AeroPropulsorComponent` - Mach, factor, `InletUnstarted`; its `AeroFlameComponent`'s share or the game's command; `AeroSolidNozzleComponent` - `ThrustNow`, `PressureRatio`) and draws the result with the mod's own effects (`Blocks/ExhaustBlocks(.g).cs`; the content: `tools/meshgen/gen_propulsor_defs.py` `gen_exhaust`, the Blocks project's `Aero/Propulsors/Exhaust`), at the block's exhaust under its grid's render root. Spawned on first light, emission stopped when dark (a smoke trail lingers), disposed after 20 s dark; caps `MaxEngines` 32 / `MaxSmokers` 8. Harness: `DevThrust`, `DevMach`, `DevPressure`, `DevUnstart`, `aeroget AeroExhaustComponent.Summary` / `LastRamjet` / `LastSolid`. |
| `ExhaustParticles.cs` | The exhaust's particle plumbing, late-bound once; the per-frame handle calls as typed delegates (no garbage), parameters written only when they move (`docs/REFLECTION_AND_WHITELIST.md`). |

## Entry points
- Server: `AeroEntryFx.Flow` / `AeroEntryFx.Server` (each step of `AeroGridComponent.AeroSimJobCore`), `AeroEntryFx.Forget` (removal).
- Client: `AeroExhaustComponent.ExhaustJob` (each ramjet / solid nozzle, after `RenderSubmissionBegin`): the exhaust.
- Client: `AeroEntryFxComponent.EntryJob` / `TrailDrawJob` (injected into every grid by `InjectAeroComponents`) ->
  `FlightJob` -> `FlightField.Update`; the same job ticks `TearSmoke`, `HeatScorch.Sync` (twice a second, flight field on or
  not) and quick launch's cockpit turn. The session-level work (settings, `MachHud`, `StructureFeel`, the settings tab) is
  the render camera's, `Runtime/AeroClientHost.cs`; this job only calls it as its fallback.
- Other mods: `AeroApi.RegisterEntrySource` -> `AeroEntryFx.RegisterEntrySource`; `AeroEntryFx.TryGetFlow` (Orbital, by reflection).

## Threading
- Server: `AeroSimJob` (per grid, parallel) calls `AeroEntryFx.Flow` / `Server`; the flow table is locked.
- Client: `AeroEntryFxComponent`'s job (after `RenderSubmissionBegin`) runs the fields; a `FlightField` is touched only from there.
