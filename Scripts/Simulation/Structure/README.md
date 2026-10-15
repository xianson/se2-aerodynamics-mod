# Structure

The ship's structure: loads spread over its blocks, the solve, wear / fatigue / failure, hull heat, and what the
player sees, hears and feels of it. Only grids a player flies are evaluated (`StructureLoads.Rate` per second).

## Model/ - engine-free (offline-tested)
Compiled as-is by `Tests/StructureTests`, `Tests/ApiTests`, `Tests/AeroBench` (`struct`, `fail`, `replay`) and `Tests/TearTests`
(TearMath only). No game types here; keep it that way.

| File | What |
|---|---|
| `ShipStructure.cs` (+ `.Network`, `.Sections`, `.Shape`) | The blocks' joint network, cuts and sections, the free-body solve, utilisation per joint. |
| `StructureEval.cs` | One evaluation: the loads (`Loads`) spread onto the blocks, then the damage to do (`Damage`, by section). |
| `StructureFatigue.cs` | Health loss per utilisation, and the per-grid `Ledger` of loss not yet written to the game. |
| `StructureBend.cs` | Permanent bends: which joints set, the far side, the displacement. |
| `StructMaterial.cs` | Material classes from block names: wall thickness, strength by temperature, burn rate. |
| `HullHeat.cs` | Hull skin / core temperatures from the flow (stagnation heating, radiation, conduction). |
| `HullHeat.Api.cs` | Each face's film coefficient and the recovery temperature of the last step (the API's heat input for another heat model). |
| `ContactBalance.cs` | The contact loads' momentum balance (what the physics says held the ship). |
| `TearMath.cs` | Tearing's maths: the hold's ramp, anchors, cell boxes. |

## Server/ - the game side (server scene)
| File | What |
|---|---|
| `StructureLoads.cs` | Settings (`Structure.*`, `Heat.*`) and diagnostics: `aeroget/aeroset StructureLoads.X`. |
| `StructureServer.cs` | `AeroGridComponent` partial: the snapshot (`TickStructure`), the background evaluation (`EvaluateStructure`), the steer cap. |
| `StructureServer.Rebuild.cs` | The blocks read for a rebuild (cells, mass, kind, mount points; definitions by reflection) and the heat faces. |
| `StructureServer.Damage.cs` | The fatigue ledger's writes to the game's blocks, `Gone` (every damage call's guard). |
| `StructureServer.Failure.cs` | A failing block split or destroyed; a failed cut watched until the grid splits. |
| `StructureServer.Deform.cs` | Dents and permanent bends through the deformation bones. |
| `StructureServer.Api.cs` | The public API's side (AeroApi version 7): another heat model's temperatures in (`ApiHeatOverride`), the failures into the event queue and the blocks' / faces' state out (`ApiPublishStructure`), both from the background evaluation. |
| `StructureServer.Dump.cs` | (harness) `.struct` / `.loads` dumps for the offline bench. |
| `StructureContacts.cs` | Collisions and ground contact read from the physics as loads; gear touchdowns for the gear. |
| `StructureTear.cs` | A large piece torn off stays tied by soft constraints for `TearSeconds`. |
| `WingStrainServer.cs` | Wing root strain (Lift/WingStrain) and its wear / breaks (`Structure.WingStrain`, off by default). |

## Fx/ - what the player gets of it (client, except HeatScorch.Update)
| File | What |
|---|---|
| `StructureFeel.cs` | Creaks, groans and cockpit shake with the structure's utilisation. |
| `TearSmoke.cs` | Smoke and debris at a torn edge (the server publishes, the client grid that pairs plays it). |
| `HeatScorch.cs` | Re-entry scorch decals: spots gathered on the server, stamped on the client. |

## Threading
- Simulation thread (`AeroSimJob`, per grid): `TickStructure` (snapshot, contacts, results applied: damage, failure,
  bends), `TickWingStrain`, `HeatScorch.Update`. `TearJob` once a tick (the first server grid's), before physics.
- Background (`AeroWork`, one evaluation per grid, `StructureLoads.MaxInFlight` in all): `EvaluateStructure` -
  rebuild, heat, loads, solve, damage plan, bends plan; it writes only the grid's own `_st*New`, ledger and lists, read
  back on the simulation thread once the job is done.
- Client effects job (`AeroEntryFxComponent`): `StructureFeel.Tick`, `TearSmoke.Tick`, `HeatScorch.Sync`.
- Reflection used here is listed in `docs/REFLECTION_AND_WHITELIST.md` §3.9.
