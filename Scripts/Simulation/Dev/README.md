# Dev - test aids (harness-driven, off in normal play)

| File | What |
|---|---|
| `AeroFlightTest.cs` | Scripted flight-test autopilot (synthetic pilot input) |
| `AeroExport.cs` | `aeroexport on`: each background build writes its grid's block boxes for `Tests/AeroBench` |
| `FlightFixture.cs` | A grid as the thruster steering sees it, as text for `Tests/ThrustTests` (Orbital's DevHarness `flightdump`) |

(`Integration/AeroTestHarness.cs` is a local dev file and stays where it is.)
Threading: simulation thread, except `AeroExport.Write`, called from the background build.
