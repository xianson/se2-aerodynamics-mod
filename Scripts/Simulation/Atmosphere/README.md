# Atmosphere - the air model (engine-free)

Pure math, compiled by the offline tests. The game side is `Air/`.

| File | What |
|---|---|
| `AtmosphereModel.cs` | Layered barometric atmosphere |
| `AtmosphereState.cs` | Density, pressure, temperature, speed of sound, viscosity |
| `CompressibleFlow.cs` | The wave-drag multiplier (`ForceTable`) |
| `AtmosphereTail.cs` | The air's upper tail past the game's linear generator edge |

## Entry points
- `AtmosphereBridge` (`Air/`) builds states from `AtmosphereModel`; `CompressibleFlow` by
  `ForceTable`; `AtmosphereTail` by `AtmosphereProbe` and `AeroApi`.

## Threading
Thread-agnostic (no shared state).
