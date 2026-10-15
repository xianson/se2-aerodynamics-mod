# Runtime - settings, switches, profiling, physics access

| File | What |
|---|---|
| `AeroSettings.cs` | The mod's settings: named values over static tuning fields, world (server, saved with the world by `AeroWorldSettingsComponent`) and client scope (this PC's effects) |
| `AeroConfig.cs` | `AeroSwitch` (aero on/off), `AeroTuning`, `AeroConfig` (min density / speed, block sizes) |
| `AeroCost.cs` | Where aero's time goes per second; the `[AERO-COST]` log line |
| `AeroStats.cs` | The engine stat overlay's "Grid" and "All" sections |
| `AeroGc.cs` | SustainedLowLatency GC while a big grid builds |
| `PhysicsHack.cs` | Typed facade over the physics / entity data (velocity, mass, gravity, thrust override, ground probe) |
| `AeroPhysics.cs` | Applying force and torque to a rigid body (`IPhysics`) |

Threading: everything here is called from the simulation threads, many grids at once - statics are interlocked or
locked. `AeroSettings.Poll` reads the settings files off the job threads (a pool task). `AeroCost` is thread-safe.
Harnesses read and set these statics by name (`aeroget` / `aeroset`): do not rename them.
