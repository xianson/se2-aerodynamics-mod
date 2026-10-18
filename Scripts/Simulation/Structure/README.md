# Structure - loads, damage, heat, and how the ship feels it

The ship's structure: loads spread over its blocks, the solve, wear / fatigue / failure, hull heat, and what the
player sees, hears and feels of it. Only grids a player flies are evaluated (`StructureLoads.Rate` per second).

## Model/ - engine-free (offline-tested)
Compiled as-is by `Tests/StructureTests`, `Tests/ApiTests`, `Tests/AeroBench` (`struct`, `fail`, `replay`) and `Tests/TearTests`
(TearMath only). No game types here; keep it that way.

| File | What |
|---|---|
| `ShipStructure.cs` (+ `.Network`, `.Sections`, `.Shape`) | The blocks' joint network, cuts and sections, the free-body solve, utilisation per joint. Thrusters are rigid (2026-10-17, `Thruster`, `RigidThrusters`): never worn or failed inside, a joint to one is its mount's - the mount breaks (`Breaks`) and the engine leaves whole. |
| `StructureEval.cs` | One evaluation: the loads (`Loads`) spread onto the blocks, then the damage to do (`Damage`, by section). Each block component's air (control surfaces, airbrakes, gear legs, intakes, scoops, drag patches) goes on its own block at its point (2026-10-17, `ComponentLoadsAtBlocks` - `Structure.ComponentLoads`, aeroset-able; `CompPlaced` / `CompMissed`), taken off the rest of the air like the wings' - the ship's total load unchanged; extra block loads (`Loads.LoadBlock`, `PublishBlockLoad`) add at their block. |
| `StructureFatigue.cs` | Health loss per utilisation (fatigue from the tensile part at the full rate; compression at `BucklingFatigueShare` 1 for a buckling-limited member, `CompressionFatigueShare` 0.2 for a stocky one: `TensilePart`, `CompressionShare`), and the per-grid `Ledger` of loss not yet written to the game (it re-anchors on each fresh health reading: welds and outside damage). |
| `StructureBend.cs` | Permanent bends: which joints set, the far side, the displacement. |
| `StructMaterial.cs` | Material classes from block names: wall thickness, strength by temperature, burn rate, each class's scorch thresholds (`ScorchAt`). |
| `HullHeat.cs` | Hull skin / core temperatures from the flow (stagnation heating, radiation, conduction). |
| `HullHeat.Api.cs` | Each face's film coefficient and the recovery temperature of the last step (the API's heat input for another heat model). |
| `ContactBalance.cs` | The contact loads' momentum balance (what the physics says held the ship). |
| `TearMath.cs` | Tearing's maths: the hold's ramp, the anchors over the failed section and each one's release (`PickAnchors`, `AnchorEnd`), cell boxes. |
| `FractureMath.cs` | How a failed cut lets go (2026-10-15): each member's mode (pulled side tears, pushed side is the hinge, crushed past `CrushUtil`), the crack's release order over `CrackSeconds` (`Plan`), the jagged fracture line a block either side of the plane with its separation check (`JagCut`). |
| `CutWatch.cs` | A failed cut's separation watch, game-free (2026-10-15): its split-count baseline (the crack's START - a piece that parts mid-crack is credited), ticks and stage (stubs spared, then the plane's rule - a split judged on the jagged surface in either stage, stage 1 forcing only a cut that never split or still bridges; bridges are pairs the game MOUNTS, `CanConnectBlock`), and `Sever` (a member split at its face but still mounted across it goes whole); the seeds' verdict (2026-10-17: both sides still in one grid = joined - forced again for up to `MaxRounds`, never dropped quietly) and `CutHolders` (what holds the far side, walked anywhere); the server's `CheckCutSeparated` runs on it. |

## Server/ - the game side (server scene)
| File | What |
|---|---|
| `StructureLoads.cs` | Settings (`Structure.*`, `Heat.*`) and diagnostics: `aeroget/aeroset StructureLoads.X`. |
| `StructureServer.cs` | `AeroGridComponent` partial: the snapshot (`TickStructure`), the background evaluation (`EvaluateStructure`), the steer cap. |
| `StructureServer.BlockLoads.cs` | The snapshot's block-component and drag-patch air (`SnapComponentLoads`: the world's multipliers as on the total, `AeroTuning.ScalePart`), and `PublishBlockLoad` - a force at a block this tick the structure sees nowhere else (a rotor's cyclic, a mod engine's thrust; latest per block, any thread; `isEngine`: rigid like a thruster). |
| `StructureServer.Rebuild.cs` | The blocks read for a rebuild (cells, mass, kind, mount points; definitions by reflection) and the heat faces. |
| `StructureServer.Damage.cs` | The fatigue ledger's writes to the game's blocks, `Gone` (every damage call's guard). |
| `StructureServer.Failure.cs` | A failing block split or destroyed; a failed cut's crack run member by member (`StepCracks`, the hinge crumpled); a failed cut watched until the grid splits (the jagged line's stubs spared first). |
| `StructureServer.Deform.cs` | Dents and permanent bends through the deformation bones. |
| `StructureServer.Api.cs` | The public API's side (AeroApi version 7): another heat model's temperatures in (`ApiHeatOverride`), the failures into the event queue and the blocks' / faces' state out (`ApiPublishStructure`), both from the background evaluation. |
| `StructureServer.Dump.cs` | (harness) `.struct` / `.loads` dumps for the offline bench. |
| `StructureContacts.cs` | Collisions and ground contact read from the physics as loads; gear touchdowns for the gear. |
| `StructureTear.cs` | A large piece torn off stays tied by soft constraints - one spring per anchor over the failed section, let go in the crack's order (the hinge last) over `TearSeconds`. |
| `WingStrainServer.cs` | Wing root strain (Lift/WingStrain) and its wear / breaks (`Structure.WingStrain`, off by default). |

## Fx/ - what the player gets of it (client, except HeatScorch.Update)
| File | What |
|---|---|
| `StructureFeel.cs` | Creaks, groans and cockpit shake with the structure's utilisation. |
| `TearSmoke.cs` | Smoke and debris at a torn edge (the server publishes, the client grid that pairs plays it). |
| `HeatScorch.cs` | Re-entry scorch decals: spots gathered on the server (light / dark by each block class's own heat limits), stamped on the client from the effects job twice a second; a fault waits 10 s (`RetrySeconds`). |

## Entry points
- `AeroGridComponent.TickStructure` (end of the sim job, server, after the air's forces are applied) and
  `TickWingStrain`; `TearJob` (`OnTear`); `AeroApi.SetFocusGrid` (who flies what), `TryGetStructure`, `NoteMoved`.
- Client: `StructureFeel.Tick`, `TearSmoke.Tick`, `HeatScorch.Sync` from `AeroEntryFxComponent.EntryJob` / `FlightJob`.
- The flow, step by step: `docs/ARCHITECTURE.md` §6.

## Threading
- `PublishBlockLoad`: any thread (a lock); read by the snapshot on the simulation thread.
- Simulation thread (`AeroSimJob`, per grid): `TickStructure` (snapshot, contacts, results applied: damage, failure,
  bends; a running crack's releases every tick - `StepCracks`, never the ledger), `TickWingStrain`, `HeatScorch.Update`. `TearJob` once a tick (the first server grid's), before physics.
- Background (`AeroWork`, one evaluation per grid, `StructureLoads.MaxInFlight` in all): `EvaluateStructure` -
  rebuild, heat, loads, solve, damage plan, bends plan; it writes only the grid's own `_st*New`, ledger and lists, read
  back on the simulation thread once the job is done.
- Client effects job (`AeroEntryFxComponent`): `StructureFeel.Tick`, `TearSmoke.Tick`, `HeatScorch.Sync`.
- Reflection used here is listed in `docs/REFLECTION_AND_WHITELIST.md` §3.9.
