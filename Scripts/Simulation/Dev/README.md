# Dev - test aids (harness-driven, off in normal play)

| File | What |
|---|---|
| `AeroFlightTest.cs` | Scripted flight-test autopilot (synthetic pilot input) |
| `AeroExport.cs` | `aeroexport on`: each background build writes its grid's block boxes for `Tests/AeroBench` |
| `ServerDevReads.cs` | The harness's reads of the server blocks (the surface / gear / airbrake `Summary` getters, `AeroPanelProbe`'s nearest settings), answered on the server step |
| `AeroBlockSaveDev.cs` | The per-block save observed and set from the harness: `AeroBlockSave.Restored` / `Written` / `LegacyClaimed` / `Counts` / `Last` (counted at load / save, never per tick), `aeroset AeroBlockSave.DevSet grid\|kind\|which\|value` and `aeroset AeroBlockSave.DevDump grid` (answers: `aeroget` the same), served on the server step (Orbital `tools/harness/test_blocksave.sh`) |
| `AeroBlockSaveDevForm.cs` | Its request / answer text, pure (`Tests/BlockSaveTests`) |
| `FlightFixture.cs` | A grid as the thruster steering sees it, as text for `Tests/ThrustTests` (Orbital's DevHarness `flightdump`) |

(`Integration/AeroTestHarness.cs` and `AeroSpeedSpike.cs` are local dev files and stay where they are.)

## Entry points
- `AeroFlightTest.Tick` (end of `AeroGridComponent.AeroSimJobCore`), `AeroExport.Write` (`StartBackgroundBuild`).
- `ServerDevReads.Step`, `AeroBlockSave.DevStep` (`AeroSimJobCore`, server scene, while something asked: one read otherwise).
- `FlightFixture`: only by reflection from the Orbital mod's DevHarness (`flightdump`, `tools/flightdump.sh`).

## Threading
Simulation thread. `AeroExport.Write` too: it is called as `StartBackgroundBuild` captures the boxes, before the
build is handed to the workers (it writes a file there while `aeroexport` is on). `ServerDevReads`: asked from any thread
(the harness, the client's flap job), answered on the next server grid step - a summary read returns the one built
after the previous read (the first: "asked" - read it again). `AeroBlockSave.DevSet` / `DevDump` the same way: the
setter only queues; the next server grid step applies / dumps (its own scene's blocks) and the getter returns that answer.
