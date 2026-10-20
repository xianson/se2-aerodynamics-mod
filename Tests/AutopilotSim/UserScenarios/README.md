# UserScenarios - your own scenario packs

Put your scenario files here. Every class that implements `IScenarioPack` is found at start, the same as the built-in packs in `../Scenarios/`.

- Start from `TemplatePack.cs`. Copy it to a new file (say `MyPack.cs`) and rename the class.
- Give each scenario a name that starts with `user: `, so `-- "user:"` runs all of yours. Names must be unique.
- `fast: true` puts a scenario in the gate (`tools/test_fast.sh`). Use `false` for long sweeps. Those run by name or with `-- full`.

## Run

```
dotnet run --project Tests/AutopilotSim -c Release -- "user:"                 # every user scenario
dotnet run --project Tests/AutopilotSim -c Release -- "user: my test" verbose  # one, with its notes and every check
dotnet run --project Tests/AutopilotSim -c Release -- list                    # every scenario's name
dotnet run --project Tests/AutopilotSim -c Release -- "user:" csv C:/temp/ap  # plus a CSV trace per ship
```

Run these from the mod's folder. Exit code 0 means every check passed.

## What a scenario uses

- **Ships**: `Ships.Heli`, `Ships.Vtol`, `FlightShips.SixAxis`, `FlightShips.GunHeli`, `Planes.Analytic` (no polar file needed), `Ships.Plane("Plane2")` (needs the local polar file), `Rockets.Finned`, or your own `SimShip`.
- **One ship**: `new Run(name, ship)`. Use `Fly(seconds)`, `At(t, ...)` for timed events, `Each(...)` for per-tick hooks, and `Measure()` for the standard maxima.
- **Several ships**: `new Flight(name)`, then `f.Add(member, ship)` for each. `f.Fly(seconds)` steps every ship one tick in turn.
- **What a ship flies**:
  - `Flight.Pilot(run, panel, ApMode.FlightHold | Navigate | Engage, targets)`: the flight pilot, with a panel's values. Behaviours are set in `panel.Behaviour`.
  - `Flight.Source(run, myGoalSource, goals)`: any `IApGoalSource` of yours.
  - `run.P.AddBlock(panel)`: an Autopilot block, through its link.
  - Nothing: the ship flies unattended.
- **Targets**: `Flight.Target(otherRun)` is another ship. `new SimTargetSource { T = SimTarget.Turn(...) }` is a scripted point.
- **Metrics**: `Min`, `Max`, `Rms` and `Mean` by name on a run or a flight, read back with `Metric(name)`. `run.Trace(column, value)` adds a column to that ship's CSV.
- **The verdict**: `Sim.Check(ok, text)` is PASS or FAIL. `Sim.Note(text)` shows with `verbose`. `f.Ledger()` or `Program.LedgerPublic(run, crashOk)` checks every ship's forces came from real actuators within their limits. Call it last on every run.

## Rules

- Control only through what a pilot does: goal sources, panels and keys. Never set a ship's velocity or attitude while it flies. The ledger fails a scenario that does.
- Wind: `run.Air` (`Steady`, `Gusts`, `Turbulence`). Terrain: `run.P.Terrain = SimTerrain.Hills()`.
