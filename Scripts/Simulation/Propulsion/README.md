# Propulsion - thrust, attitude and flames

Where the game's thrust acts, how thrusters and gyros steer, thrust-vs-Mach curves, flames.

| File | What |
|---|---|
| `ThrustTorque.cs` | Thrust torque (each thruster's share acts at its block, r x F) and the aero-aware attitude law over thrusters, gyros and surfaces; publishes flame shares, flap angles and gear state for the client components. Its gear parts belong to the gear work (do not reshape `PublishGear` / `GearState`). |
| `ThrustAllocator.cs` | Pure maths (no game): the game's share per thruster, the rate command, the allocation solve. Offline-tested by `Tests/ThrustTests` (also compiled into AeroBench and ControlSurfaceTests). |
| `GyroControl.cs` | Reads back the game's own gyro impulse (private `ActiveGridGyroscopeData`) so the mod's law flies the gyros within the game's budget. |
| `OffsetThrustJob*.cs` | The older per-thruster path: `.cs` Execute (Mach thrust scaling, damping/override writes, `[AERO-TRACE]` / `[AERO-THR]` logs), `.Attitude.cs` the RCS override law, `.Cache.cs` thruster / gyro caches, `ProfileByGuid`, the `_definition` reflection. |
| `ThrustProfile*.cs` | Thrust vs Mach curves (turbofan etc.), by block GUID in `OffsetThrustJob.ProfileByGuid`. They scale real thrust only on the older path (`ThrustTorque.Enabled` off); `DesignProbe` and `AeroApi` apply them only as the simulation does (`OffsetThrustJob.AppliedProfile`: none by default). |
| `EngineOverrides.cs` | The engine write that needs reflection: the protected `Component.SetData<T>` (thrust override / control data). |
| `AeroFlameComponent.cs` | Client: each thruster's flame drawn at its published share. |
| `AeroFuelMassComponent.cs` | Hydrogen tank fuel adds mass (`IDynamicMassProvider`). |
| `AeroThrustSettingsComponent.cs`, `AeroThrustBlockDetailModel.cs` | Per-thruster attitude authority (`AttitudeFraction`, saved with the world as `thrust x y z f` lines by `AeroSurfaceSaved`) and its terminal slider - the slider is off for now (`[BlockDetailProvider]` commented out: it crashed the control panel), so every thruster is at 0.5. |

## Entry points
- `ThrustTorque.Apply` (`AeroGridComponent.AttitudeJob`, after the game's grid systems, before physics).
- `OffsetThrustJob.RebuildThrusterCache` / `RebuildGyroCache` (the sim job, when dirty); `OffsetThrustJob.Execute` only with
  `ThrustTorque.Enabled` off.
- Injected: `AeroThrustSettingsComponent` (thrusters), `AeroFlameComponent` (client thrusters), `AeroFuelMassComponent` (tanks).

## Threading
- `AeroSimJob` (a DCS job per grid, both scenes, in parallel across grids): `OffsetThrustJob.Execute` and the
  cache rebuilds; the attitude job (after the grid systems, before physics) calls `ThrustTorque.Apply`, which uses
  `ThrustAllocator` and `GyroControl`. Statics touched from there are per-grid records under their own locks.
- Client render jobs (after `RenderSubmissionBegin`): `AeroFlameComponent` reads the shares `ThrustTorque` published.
- Reflection used here is listed in `docs/REFLECTION_AND_WHITELIST.md`.
