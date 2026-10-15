# Air - the air a grid flies through, from the game

The game side of the air model (the engine-free model is in `Atmosphere/`).

| File | What |
|---|---|
| `AtmosphereBridge.cs` | SE2's `AirData.Density` (all the game gives) into a full `AtmosphereState` (temperature, speed of sound, viscosity), via `AtmosphereModel` |
| `AeroWind.cs` | The game's wind at a grid: aero flies the grid through the moving air (velocity less wind), and takes back the game's own wind push |

## Entry points
- `AeroWind.At` (`AeroGridComponent.AeroSimJobCore`), `AeroWind.UndoGameForce` (`AeroGridComponent.WindUndoJob`).
- `AtmosphereBridge.GetState` (`TryCompute`, the structure snapshot).

## Threading
Simulation threads (each grid's sim / wind-undo job). `AtmosphereBridge` is one shared instance (`AeroGridComponent._atmosphereBridge`).
