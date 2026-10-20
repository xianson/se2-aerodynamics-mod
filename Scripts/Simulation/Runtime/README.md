# Runtime - settings, switches, profiling, physics access

| File | What |
|---|---|
| `AeroSettings.cs` | The mod's settings registry: named values over static tuning fields, world (server, saved with the world) and client scope (this PC's effects), each with its UI metadata (label, group, tier, unit, step, choices; registration order = screen order) |
| `AeroSettings.Store.cs` | The settings' API (get / set / info), who may change what (`CanEditWorld`, `IsForced` by the host file, `ResetGroup`, `WorldUnsaved`), their `name = value` text, the world save's load and unload, the two `.cfg` files. What is written is each setting's `Stored` value (set through the store), never a harness's direct field write |
| `AeroWorldSettingsComponent.cs` | Carries the world scope in the save (injected into server planets) |
| `AeroClientHost.cs` | The client's session tick (`AeroClientHostComponent`, injected into the render camera): the settings poll and menu, the HUD readout, quick launch's key, autopilot toasts, creaks - with no grid in the world too; whether the server runs in this process |
| `AeroConfig.cs` | `AeroSwitch` (aero on/off), `AeroTuning`, `AeroConfig` (min density / speed, block sizes) |
| `AeroCost.cs` | Where aero's time goes per second; the `[AERO-COST]` log line |
| `AeroStats.cs` | The engine stat overlay's "Grid" and "All" sections |
| `AeroGc.cs` | SustainedLowLatency GC while a big grid builds |
| `GridPhysics.cs` | Typed facade over the physics / entity data (velocity, mass, gravity, thrust override, ground probe) |
| `AeroPhysics.cs` | Applying force and torque to a rigid body (`IPhysics`) |

## Entry points
- `AeroSettings.Poll` (the sim job, server; `AeroClientHost.Frame`, client - the render camera's job); `AeroWorldSettingsComponent` (injected
  into server planets) loads and saves the world scope; `AeroApi` settings calls; the in-game menu (`Ui/SettingsTab.Mod.cs`).
  Offline: `Tests/SettingsTests`, `Tests/SettingsUiTests`.
- `AeroPhysics.ApplyForceAndTorque` (the sim job, `ThrustTorque`); `GridPhysics` reads everywhere a grid's physics is needed.
- `AeroCost.*.Start/Stop` around every section; `AeroCost.MaybeLog` once a second when `AeroCost.Log` (`aerocost on`).

## Threading
Everything here is called from the simulation threads, many grids at once (the settings poll also from the client's
session tick) - statics are interlocked or
locked. `AeroSettings.Poll` reads the settings files off the job threads (a pool task); what it read is applied by the next job's poll. `AeroCost` is thread-safe.
Harnesses read and set these statics by name (`aeroget` / `aeroset`): do not rename them.
