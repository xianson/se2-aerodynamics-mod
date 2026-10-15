# Runtime - settings, switches, profiling, physics access

| File | What |
|---|---|
| `AeroSettings.cs` | The mod's settings registry: named values over static tuning fields, world (server, saved with the world) and client scope (this PC's effects), each with its UI metadata |
| `AeroSettings.Store.cs` | The settings' API (get / set / info), their `name = value` text, the world save's load and unload, the two `.cfg` files |
| `AeroWorldSettingsComponent.cs` | Carries the world scope in the save (injected into server planets) |
| `AeroConfig.cs` | `AeroSwitch` (aero on/off), `AeroTuning`, `AeroConfig` (min density / speed, block sizes) |
| `AeroCost.cs` | Where aero's time goes per second; the `[AERO-COST]` log line |
| `AeroStats.cs` | The engine stat overlay's "Grid" and "All" sections |
| `AeroGc.cs` | SustainedLowLatency GC while a big grid builds |
| `PhysicsHack.cs` | Typed facade over the physics / entity data (velocity, mass, gravity, thrust override, ground probe) |
| `AeroPhysics.cs` | Applying force and torque to a rigid body (`IPhysics`) |

## Entry points
- `AeroSettings.Poll` (the sim job, server; `AeroEntryFxComponent.EntryJob`, client); `AeroWorldSettingsComponent` (injected
  into server planets) loads and saves the world scope; `AeroApi` settings calls.
- `AeroPhysics.ApplyForceAndTorque` (the sim job, `ThrustTorque`); `PhysicsHack` reads everywhere a grid's physics is needed.
- `AeroCost.*.Start/Stop` around every section; `AeroCost.MaybeLog` once a second when `AeroCost.Log` (`aerocost on`).

## Threading
Everything here is called from the simulation threads, many grids at once (the settings poll also from the client
effects job) - statics are interlocked or
locked. `AeroSettings.Poll` reads the settings files off the job threads (a pool task). `AeroCost` is thread-safe.
Harnesses read and set these statics by name (`aeroget` / `aeroset`): do not rename them.
