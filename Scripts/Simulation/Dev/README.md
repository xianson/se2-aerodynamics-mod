# Dev - test aids (harness-driven, off in normal play)

| File | What |
|---|---|
| `AeroFlightTest.cs` | Scripted flight-test autopilot (synthetic pilot input) |
| `AeroExport.cs` | `aeroexport on`: each background build writes its grid's block boxes for `Tests/AeroBench` |
| `FlightFixture.cs` | A grid as the thruster steering sees it, as text for `Tests/ThrustTests` (Orbital's DevHarness `flightdump`) |

(`Integration/AeroTestHarness.cs` and `AeroSpeedSpike.cs` are local dev files and stay where they are.)

## Entry points
- `AeroFlightTest.Tick` (end of `AeroGridComponent.AeroSimJobCore`), `AeroExport.Write` (`StartBackgroundBuild`).
- `FlightFixture`: only by reflection from the Orbital mod's DevHarness (`flightdump`, `tools/flightdump.sh`).

## Threading
Simulation thread. `AeroExport.Write` too: it is called as `StartBackgroundBuild` captures the boxes, before the
build is handed to the workers (it writes a file there while `aeroexport` is on).
