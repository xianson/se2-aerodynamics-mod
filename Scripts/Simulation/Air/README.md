# Air - the air a grid flies through, from the game

| File | What |
|---|---|
| `AtmosphereBridge.cs` | SE2's `AirData.Density` (all the game gives) into a full `AtmosphereState` (temperature, speed of sound, viscosity) |
| `AeroWind.cs` | The game's wind at a grid: aero flies the grid through the moving air (velocity less wind) |

Threading: simulation thread (the sim job). The engine-free air model is in `Atmosphere/`.
