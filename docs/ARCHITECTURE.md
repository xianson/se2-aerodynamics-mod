# Aerodynamics Mod: architecture

How the mod works end to end. Written 2026-10-15, after the folder reorg; every claim names the code it comes from
(`Type.Member`). Scripts live under `Scripts/Simulation/`, namespace `AeroMod`. Each folder has its own short
`README.md` (files, entry points, threads); this page is how they fit together.

Companions: `docs/API.md` (the public API other mods call), `docs/REFLECTION_AND_WHITELIST.md` (every reflection use).

## 1. Folder map

| Folder | What lives there | Engine types? |
|---|---|---|
| `Core/` | What a model gets and returns (`AeroContext`, `AeroResult`), cell access (`IGridAccessor`, `SnapshotGridAccessor`, `Se2GridAccessor`), `AeroLog` | only `Se2GridAccessor.cs` |
| `Surface/` | The grid's skin: exposed faces with smoothed normals (`SmoothSurfaceProvider`), hull vs sealed cavity (`ManifoldClassifier`) | no |
| `Shadow/` | Face visibility before a grid has its table (`PrecomputedShadowMap`) | no |
| `Drag/` | The hull model (`DampedShadowedDragModel`), the per-direction force table (`ForceTable`, `ChunkedTable`), its disk cache (`AeroTableCache`), broken-off pieces (`OrphanChunks`) | no |
| `Lift/` | Wing detection (`ConnectedComponentWingDetector`), the wing lift model (`CompressibleWingModel`), the whole-ship wrapper (`LiftingSurfaceModel`), the ship's stall (`LiftPeak`), wing root loads (`WingStrain`), ground effect | no |
| `Atmosphere/` | The air model: layered atmosphere, isentropic / shock relations, the thin upper tail | no |
| `Components/` | Block-level aero components (`IAeroBlockComponent`): control surfaces, airbrake and gear drag, intakes, scoops, rotors, other mods' drag patches; their registry and factory | mostly no (`BlockComponentFactory` reads blocks) |
| `Debris/` | Drag on the game's loose fracture debris (not grids): the job (`AeroDebrisDrag`, client scene) and its engine-free step (`DebrisDragMath`, run by `Tests/DebrisTests`) | `AeroDebrisDrag.cs` only |
| `Air/` | The game's air at a grid: density into a full state (`AtmosphereBridge`), wind (`AeroWind`) | yes |
| `Grid/` | `AeroGridComponent` (one per grid) and its jobs, the rebuild scheduler, the background work queue, the frame budget | yes |
| `Propulsion/` | Thrust torque and the aero-aware attitude law (`ThrustTorque`, `ThrustAllocator`, `GyroControl`), the older per-thruster path (`OffsetThrustJob`), thrust-vs-Mach profiles, flames, fuel mass | yes (`ThrustAllocator` is pure) |
| `Structure/Model/` | The structure: joint network and solve (`ShipStructure`), one evaluation (`StructureEval`), fatigue, bends, materials, hull heat, tear maths | no |
| `Structure/Server/` | The structure's game side: snapshot and background evaluation (`StructureServer*` partials of `AeroGridComponent`), contacts, tearing, wing strain, settings (`StructureLoads`) | yes |
| `Structure/Fx/` | What the player gets of it: creaks and shake (`StructureFeel`), torn-edge smoke (`TearSmoke`), re-entry scorch (`HeatScorch`) | yes |
| `Design/` | `DesignProbe`: the whole ship's air force at any attitude and Mach (stability, top speed, the stall / LOAC lookups) | yes |
| `Fx/` | The published flight state (`AeroEntryFx`) and the client flight effects (`FlightField`: plasma, vapour, tip trails) | yes |
| `Hud/` | Stall warning, loss-of-attitude-control warning, the game's thrust-overload warning, Mach in the speedometer | yes |
| `Blocks/` | The mod's own blocks' components and terminals (3x3x1 control surfaces, hinge flaps, gear variants) | yes |
| `Gear/`, `Integration/` | Retractable gear and airbrake (kinematics, server and client components, terminals); `Integration/` also holds the local dev harness files | yes |
| `Api/` | `AeroApi` (what other mods call; partials by version area), its published copies (`ApiGridState`) and event ring (`SeqRing`), `OrbitalLink` (what aero calls in the Orbital mod) on the binding rule `CrossModLink`, `AtmosphereProbe` | yes (`SeqRing`, `CrossModLink` are engine-free) |
| `Runtime/` | Settings (`AeroSettings`: the registry; `AeroSettings.Store`: API, text, files, the world save), switches and tuning (`AeroConfig`), profiling (`AeroCost`, `AeroStats`), GC mode, physics access (`PhysicsHack`, `AeroPhysics`) | yes |
| `Ui/` | The Options screen's **Mods** tab (`SettingsTab`, `SettingsModel`, `AvaloniaReflect`: copies of the Orbital mod's; `SettingsTab.Mod.cs`: this mod's adapter) | Avalonia by reflection (`SettingsModel` is engine-free) |
| `Dev/` | Test aids: flight-test autopilot, shape export for the bench, thrust fixtures | yes |
| `Injection/` | Adds the mod's components to the game's prefabs at definition load (`EntityInjector`, `InjectAeroComponents`) | yes |

The engine-free folders (`Core` minus `Se2GridAccessor`, `Surface`, `Shadow`, `Drag`, `Lift`, `Atmosphere`,
`Structure/Model`, parts of `Components`, `Propulsion/ThrustAllocator.cs`, `Debris/DebrisDragMath.cs`, `Api/SeqRing.cs`,
`Api/CrossModLink.cs`, `Ui/SettingsModel.cs` and the settings registry) are compiled as-is by the offline test
projects in `Tests/`; keep game types out of them.

## 2. How the mod gets into the game

`EntityInjector` is a `[DefinitionPostProcessor]` over every `PrefabDefinition`; `InjectAeroComponents.Please` adds
(by reflection on the composition: `Injections.Add`, see the reflection doc):

| Prefab has | Gets |
|---|---|
| `CubeGridComponent` (every grid, server and client) | `AeroGridComponent`, `AeroEntryFxComponent` |
| `ThrusterComponent` | `AeroThrustSettingsComponent` (per-thruster attitude authority; saved with the block, section 11) |
| `ThrusterEffectsComponent` (client thruster), with `EntityInjector.InjectFlames` | `AeroFlameComponent` |
| `ResourceContainerComponent` (tanks) | `AeroFuelMassComponent` |
| `DiscoverablePlanetComponent` (server planets) | `AeroWorldSettingsComponent` (carries the world settings in the save) |
| the mod's control-surface / gear / airbrake prefabs (`AeroBlockGuids`) | `AeroSurfaceSettingsComponent` / `AeroFlapComponent`, `AeroGearComponent` / `AeroGearAnimComponent`, `AeroAirbrakeComponent` / `AeroAirbrakeAnimComponent` (server / client; the server ones saved with the block, section 11) |

## 3. Scenes and threads

SE2 2.4 runs the server and the client in one process (its networking is a mock: `AeroSettings` header). Every grid
exists twice, a **server copy** and a **client copy**, each with its own `AeroGridComponent`.
`AeroGridComponent.IsServerScene` is set in `OnAddedToScene` from the scene's name ("Client" vs anything else).

| Thread | What runs there |
|---|---|
| **Simulation jobs** (DCS jobs, one per grid, grids in parallel; both scenes) | `AeroGridComponent.AeroSimJob` (`[During(GridUpdateOrder.UpdateSystems.Update)]`, after `ThrustComponent.OnComputeThrust`); `AttitudeJob` (after `GridUpdateOrder.UpdateSystems`, before `Physics`); `WindUndoJob` (after `RangedAffectGenerators`, before `Physics`); `AeroDebrisDrag.DebrisDragJob` (the same slot, one call per loose fracture piece - client scene only, where the game spawns them); `TearJob` (after `OnAttitude`, before `Physics`, server, once a tick); `AeroStallWarning.AeroStallJob` (after the game's `ReportStall`, server); the gear and airbrake server jobs (before `Simulation`) |
| **Aero workers** (`AeroWork`, `Workers` = 2 long-running tasks, highest priority first) | full rebuilds (`AeroGridComponent.StartBackgroundBuild`'s lambda), local chunk updates after damage (`Damage.cs`), structure evaluations (`EvaluateStructure`). They only touch the snapshot they were handed; results are installed on the simulation thread (`FinalizeStaggeredBuild`, `TickLocal`, `TickStructureCore`) |
| **Client render-submission jobs** (after `RenderSubmissionBegin`) | `AeroEntryFxComponent.EntryJob` / `TrailDrawJob` (flight field, scorch stamping twice a second, smoke, creaks, Mach readout, settings poll, the settings tab's 250 ms claim - `SettingsTab.Claim` / `Ticked`), `AeroFlameComponent`, `AeroFlapComponent`, gear / airbrake animators, `AeroDebugDraw`, and `AeroGridComponent.SpeedUncapJob` (reads the physics speed cap) |
| **Avalonia UI thread** | The settings tab (`SettingsTab.UiTick`, posted by the claim): attach to the open Options screen, read the controls (~4 Hz while the tab is selected), `AeroSettings.Set` |
| **Thread pool** | `AeroSettings.Poll` reads the settings files off the job threads; what it read is applied by the next job's poll, on that job's thread |

Statics shared across grids are locked or interlocked (folder READMEs say which). Shared state never crosses from a
worker into the game: workers write only the grid's own `_built*` / `_st*New` fields, polled by the simulation thread.

## 4. One simulation tick, per grid

`AeroGridComponent.AeroSimJobCore` (`Grid/AeroSimJob.cs`), in order:

1. **Switch and harness gates.** `AeroSwitch.Enabled` off: nothing. Switched back on: `AeroSwitch.Generation` bumps and
   every grid `ForceFullRebuild`s.
2. **Read the physics** (`PhysicsHack.TryGetVelocity`); the air velocity is velocity less the wind (`AeroWind.At`).
3. **Quiet path.** A grid nobody flies, not thrusting, with no air past it and not turning (`IsQuiet`) does only those
   reads after `QuietAfter` steps, and the full step once every `QuietRefresh`.
4. **Mass, inertia, density** (`AirData.Density` plus `AtmosphereProbe.WithTail` above the game's air edge). Server
   only: `AtmosphereProbe.Tick`, `AeroSettings.Poll`, the async ground ray (`PhysicsHack.GetGroundDistance`).
5. **Thruster and gyro caches** rebuilt when dirty (`OffsetThrustJob.RebuildThrusterCache` / `RebuildGyroCache`).
6. **Compute** (`TryCompute`, `Grid/AeroGridComponent.Compute.cs`) when due: every frame for piloted or fast (>= 80 m/s)
   grids, else every 2nd / 4th (`ComputeInterval`); once `AeroFrameBudget` is spent, due slow grids wait up to
   `AeroFrameBudget.MaxSkips` frames and reapply their last forces (`SkipCompute`).
7. **Publish the flow** (`AeroEntryFx.Flow`, `AeroEntryFx.Server`: travel, Mach, density, lift, forces, entry heat) and,
   server only, the wing strain (`TickWingStrain`).
8. **Apply** the result: the world's `AeroTuning.DragMultiplier` / `LiftMultiplier` (`AeroTuning.Scale`), then
   `AeroPhysics.ApplyForceAndTorque`. `LastAeroTorque` (airframe) and `LastSurfaceTorque` (block components) are kept
   apart for the attitude law. Both scenes apply the forces (the client copy predicts the same flight).
9. **Structure** (server only): `TickStructure` with the applied force and torque (section 6).
10. **Legacy attitude** only with `ThrustTorque.Enabled` off: `OffsetThrustJob.Execute` (Mach thrust profiles, per-thruster
    overrides) and its phantom PD torque. The default path is the separate `AttitudeJob` (section 5).
11. **Stats and dev hooks:** `AeroStats.CommitGrid`, `AeroTestHarness.Tick`, `AeroSpeedSpike.Tick`, `AeroFlightTest.Tick`.

### Inside TryCompute: shape to forces

- **Rebuilds.** A grid's first build is queued even parked (if it is in air or gravity, or `EntryWantsTable`):
  `AeroScheduler.EnqueueRebuild`. `AeroScheduler.EnsureTicked` / `RunMine` grant slots; `BeginStaggeredBuild` -> `StartBackgroundBuild`
  captures each block's occupied-cell boxes on the simulation thread and hands them to `AeroWork`. The
  worker loads the table from `AeroTableCache` if the shape was seen before, else builds the surface
  (`SmoothSurfaceProvider`), classifies it (`ManifoldClassifier`), detects wings, and bakes the `ForceTable` /
  `ChunkedTable`. `FinalizeStaggeredBuild` swaps it in. Air-breathing engines' boxes (`EngineBoxes`) make their faces
  flow-through (`DampedShadowedDragModel.FlowThroughEngines`).
- **Damage.** `OnBlocksChanged` (the grid's `BlocksChangedSignal`) marks cells dirty and bumps `StructBlockVersion`. A
  chunked grid updates only the touched chunks in the background (`TickLocal`, `ChunksPerUpdate` at a time), then a full
  rebuild after `QuietBeforeFullRebuild` s of quiet. A piece broken off flies on its parent's chunk shares
  (`OrphanChunks`, `TrySeed`) until its own build. Small grids without chunks take an incremental surface and wing
  update (`SmoothSurfaceProvider.OnBlocksChanged`, `LiftingSurfaceModel.UpdateWings`).
- **The model.** Below `AeroConfig.MinDensity` or `MinSpeed`: no forces. Else an `AeroContext` (local velocity, spin,
  `AtmosphereBridge.GetState(density)`, centre of mass, ground height, up) goes to `LiftingSurfaceModel.Compute`: the
  hull from the force table (a lookup per frame; the face loop in `DampedShadowedDragModel.Compute.cs` only before
  the table exists) plus the wings (`CompressibleWingModel`).
- **Control surfaces.** `UpdateControlSurfaceInputs` mixes the pilot's rotation into surface inputs; while the aero-aware
  law flies (`LastControlTorqueValid`, server) `AllocateSurfaces` gives them its torque demand instead.
  `PublishFlapAngles` publishes deflections for the client flap models. `HingeSurfaces` drives vanilla hinges
  (`Control.HingeSurfaces`, off by default).
- **Block components** (`AeroComponentRegistry.EvaluateAll`) and **other mods' drag** (`EvaluateDragPatches`,
  `AeroApi.SetDrag`) are added to `LastResult`.

## 5. Attitude: thrust torque and the aero-aware law

`AttitudeJob` runs after every grid system (the game's thrust and gyros) and before the physics step:
`ThrustTorque.Apply`. The game applies thrust at the centre of mass; `ThrustTorque` shares the game's thrust among the
thrusters as the game would and applies each share at its block (r x F), so unbalanced thrust turns the ship. Piloted
(`TargetControlData`) in air, one law (`ThrustAllocator.AttitudeAccel`) flies gyros and thrusters toward the target,
told the air's torque this frame (`LastAeroTorque`); the surfaces deliver their share through `AllocateSurfaces`.
`GyroControl` reads back the game's own gyro impulse so the law stays within the game's gyro budget. No phantom torque:
every torque comes from a thruster, gyro or surface that could produce it. `ThrustTorque.Publish` / `PublishFlaps` /
`PublishGear` publish flame shares, flap angles and gear state for the client components. The structure can cap the
controller's steering (`AeroGridComponent.UpdateSteerCap`) so the controller never overloads its own ship.

## 6. Structure, heat and damage (server, player-flown grids)

`TickStructure` (`Structure/Server/StructureServer.cs`), only with `StructureLoads.Enabled`, server scene:

- every tick: contacts accumulated as loads (`AccumulateContacts`, `Structure/Server/StructureContacts.cs`), a failed
  cut's separation watched, the scorch dose every 0.25 s (`HeatScorch.Update`);
- `StructureLoads.Rate` (4) times a second, and only for a grid someone flies (`StructFocusUntil` from
  `AeroApi.SetFocusGrid`, or `TargetControlData`): `TickStructureCore` installs the last evaluation's results
  (`ApplyStructureDamage`, `ApplyBends`), takes a snapshot (`StructSnap`: applied air force / torque, wing forces,
  thruster shares, gyro torque, contacts, block health, the Orbital mod's railed-entry state) and, after a block change
  and `RebuildCooldown`, the blocks themselves (`SnapshotBlocks`);
- in the background (`AeroWork`, at most `StructureLoads.MaxInFlight` evaluations in all): `EvaluateStructure` -
  rebuild (`ShipStructure.Build`, `BuildHeat`, heat carried over), hull heat (`HullHeat.Pass` / `Step`, from the real or
  railed air), loads spread onto blocks (`StructureEval.Apply`), the free-body solve (`ShipStructure.SolveFree`), the
  steer cap, the damage plan by section (`StructureEval.Damage`: wear, fatigue via `StructureFatigue`, heat damage,
  failed cuts), bends (`StructureBend.Plan`), and the stall and LOAC evaluations (`EvaluateStall`, `EvaluateLoac`, via
  `DesignProbe.ForcesAt`);
- back on the simulation thread: damage through the game's damage receiver (`StructureServer.Damage.cs`), failures
  split or destroy blocks (`StructureServer.Failure.cs`, `Structure.SplitBlocks`), dents and bends through deformation
  bones (`StructureServer.Deform.cs`); a failed cut lets go as a crack across it over `FractureMath.CrackSeconds` (the pulled
  side first, the pushed side - the hinge, crumpled - last; the fracture line jagged a block either side of the plane:
  `Structure/Model/FractureMath.cs`, `StepCracks`); a large piece torn off is held by soft constraints, one spring per
  anchor over the failed section, let go in the crack's order over `StructureTear.TearSeconds` (`StructureTear.Step`, from
  `TearJob`). While a crack runs, and after a failed cut until a rebuild, damage waits (`_stBreakHold`, "no zipper").

**How a failed cut lets go** (2026-10-15; WHEN it fails is unchanged - `StructureEval.Damage` by section, on one solve):

1. **The plan** (engine-free, `Structure/Model/FractureMath.cs`, in the background evaluation): each member of the cut by
   its side of the section (`ShipStructure.PieceTension` / `PieceCompression`): pulled - **torn**; pushed - the **hinge**;
   pushed past `FractureMath.CrushUtil` (2) - **crushed**. `FractureMath.Plan` orders the releases over `CrackSeconds`
   (0.3 s): torn members by utilisation first, the hinge last. `JagCut` moves each member's break onto the plane or a block
   either side (its stress plus a hash of the grid's seed and the cell), checking each step leaves no bridge across the
   cut; a hinge breaks at the block beyond, its crumpled block left as a stub. The plan rides in `StructureEval.CutFail`
   (`Release`, `Mode`, `Own`, `Stubs`, `Steps`).
2. **The crack** (simulation thread, `StructureServer.Failure.cs`): `ApplyStructureDamage` queues the cut (`Cracking`);
   `StepCracks` releases its members in order through `FailBlock` (crushed ones without a split), crumples the hinge's block
   (`CrumpleDepth` 0.15 m toward the cut, at most `CrumpleMax` 0.25 m with the dents), puffs smoke along it
   (`CrackSmokes` 3), and bumps `StructureLoads.Breaks` per release (creaks / jolts). `FinishCrack` stamps the separation
   watch; `CheckCutSeparated` spares the stubs at first and destroys what still holds one from beyond the line, then falls
   back to the plane's rule.
3. **The peel** (`StructureTear`): up to `MaxAnchors` (6) springs picked over the failed members' places
   (`TearMath.PickAnchors`: the hinge, the crack's origin, the farthest), each its own position motor and its own end
   (`TearMath.AnchorEnd`: the origin's at `PeelFirst` 0.35 of `TearSeconds`, the hinge's at `TearSeconds`); the start hold
   is `ForceFactor` g shared by them.
4. **Bends** (`StructureBend`) fold about the cut's most pushed member's edge (`HingeToCompression`).

**Fatigue by sign:** `StructureFatigue.TensilePart` counts tension (and shear) at the full rate; the compression on top
at `StructureFatigue.CompressionShare(buckling)`: `BucklingFatigueShare` (1, `Structure.BucklingFatigueShare`) for a
buckling-limited member (`ShipStructure.PieceBuckling`: buckled capacity under `BucklingLimitedBelow` 0.95 of the yield -
thin walls, most governing members), `CompressionFatigueShare` (0.2) for a stocky, yield-limited one (the user,
2026-10-15: real ships fatigue as before the split). Yield wear past 1 is the same either way. The break's knobs are
settings too (`Structure.Crack*`, `Jagged*`, `CrushUtil`, `CrumpleDepth`, `HingeToCompression`, `TearAnchors`,
`TearPeelFirst`).

Wing root strain (`WingStrainServer.cs`, `Structure.WingStrain`) is a separate, older path, off by default.

## 7. Server and client: what crosses

Nothing of aero's goes over the network: mod components do not replicate ([Replicate] / [SyncFromClients] are not
available to mods; `AeroSurfaceBlockDetailModel` header). The server copy publishes into static, locked tables; the
client copy in the same process finds its server copy **by pose** (position, orientation, local bounds):

| Published by the server copy | Read by the client | Matched by |
|---|---|---|
| Flow record (`AeroEntryFx.FlowPub`: travel, Mach, density, lift, forces, entry strength) | `AeroEntryFxComponent.FlightJob` -> `FlightField`; `MachHud`; `AeroApi` answers for a client copy | `AeroEntryFx.FindFlow` (nearest within 20 m, carried forward, < 0.5 s old, sticky) |
| Flame shares, flap angles, gear state (`ThrustTorque.Published`) | `AeroFlameComponent`, `AeroFlapComponent`, gear / airbrake animators | `ThrustTorque.FlameShare` / `FlapAngle` / `GearState` |
| Scorch spots (`HeatScorch`) | `HeatScorch.Sync` (from `EntryJob`, twice a second while `Fx.Effects` is on, the flight field on or not) | the flow pairing (paired itself when the field has not) |
| Tear events (`TearSmoke.Publish`) | `TearSmoke.Tick` | `TearSmoke.Pairs` (pose) |
| Structure utilisation (the server `AeroGridComponent`) | `StructureFeel.Tick` | `DesignProbe.ServerAero` |

What the **game** replicates for us: block damage, splits and deformation (written through the game's own systems),
and `ThrustStallData` (`AeroStallJob` writes it on the server). Settings: world scope saved with the world
(`AeroWorldSettingsComponent`), client scope in a local file; `AeroSettings.CanEditWorld` (`WorldWritable`: refuses world
sets in a process that only ever saw a client scene) - section 10. A remote multiplayer client therefore gets no aero effects and `AeroApi`
answers false there.

## 8. Cross-mod links

**One link each way.** Aero names `OrbitalMod.OrbitalApi` in one file only, `Api/OrbitalLink.cs`; the Orbital mod names
aero's types only through its `Api/AeroLink.cs`. Both follow one rule (`Api/CrossModLink.cs` here: `LastLoadedType` +
`CrossModBinding<T>`): the **last loaded** copy of the other mod's type, re-found when the loaded assemblies change (at
most every 5 s, a `Generation` counter), each member bound by name **and** parameter types as a typed delegate, bound
again on a new generation, looked up again every `OrbitalLink.RetrySeconds` while missing, and after a throw (or a member
of the wrong shape) left for 10 s, doubling to 300 s. The Orbital mod's `tools/check_contract.sh` fails on a second
lookup site.

**Aero calls the Orbital mod** (by reflection, no lock of aero's held):

| Call | Used for |
|---|---|
| `OrbitalApi.TryGetEntryState2` (fallback `TryGetEntryState`) via `OrbitalLink.TryGetEntryState2` | a railed re-entry's true air velocity, density, temperature and braking: hull heat and entry loads (`TickStructureCore`) |
| `OrbitalApi.TryGetEntryState` via `AeroEntryFx.EntryStateAt` (→ `OrbitalLink.TryGetEntryState`) | the scorch's "is re-entering" test (`HeatScorch.Update`) |
| `OrbitalApi.TryGetGridEdits` via `OrbitalLink.TryGetGridEdits` | velocity changes Orbital wrote into a grid, taken off the contact balance (`StructureContacts`) |

The **speed cap** is the physics world's own (`IVelocityLimitProvider.LinearVelocityLimit`, read by `SpeedUncapJob` into
`StructureContactLoads.PhysicsSpeedCap`): at the cap, a velocity change along the motion is the clamp's, not a contact.

**Other mods call aero** through `AeroApi` (contract `AeroApi.Version`, members only added, never overloaded; Orbital's
`tools/check_contract.sh` checks the members it uses). The Orbital mod's consumers: its build hints and flight HUD
(`TryDesign`, `GetTopSpeed(At)`, `TryGetFlight`, `TryGetForces`, `TryGetStructure`, `TryGetStall`, `TryGetLoac`,
`SetFocusGrid`), its railed entry (`RegisterEntrySource` -> `AeroEntryFx.RegisterEntrySource`, `PredictHullForces`,
`RequestForceTable`, `TryGetAtmosphereAt`), its frame shifts (`NoteMoved`), the seated ship's failure toast
(`GetStructureEventSequence`, `ReadStructureEvents`: `StructureAlerts`), and its harness (`GetFxConsistency`). The
stall and LOAC warnings are computed here and drawn by the Orbital mod's HUD; aero's own HUD pieces are the game's
thrust-overload warning (`AeroStallWarning`, `GameStallWarning`) and the optional Mach readout (`MachHud`).

## 9. Dev tooling

- **aeroget / aeroset** - the Orbital mod's DevHarness (`aeroget Type.Member`, `aeroset Type.Member value`; `AeroMod.`
  implied) reads and writes any public static of this mod by name. That is why public statics are never renamed.
  Other harness verbs: `aerocost on|off` (`AeroCost.Log`: the `[AERO-COST]` line once a second, `AeroCost.MaybeLog`),
  `aeroexport on` (`AeroExport`: block boxes for `Tests/AeroBench`), `flightdump` (`FlightFixture`, `tools/flightdump.sh`:
  `Tests/data/*.flight` for `Tests/ThrustTests`). Scripts: `Orbital Mod/tools/harness/*.sh`.
- **AeroTestHarness** (`Integration/AeroTestHarness.cs`, a local dev file) - scripted in-game scenarios; it freezes grids
  (`ShouldSkipGrid`) and ticks at the end of the sim step. `AeroFlightTest` (`Dev/`) is the scripted autopilot.
- **AeroCost / AeroStats** - per-section time (`AeroCost.Sim`, `.Compute`, `.Apply`, ...: zero cost while
  `AeroCost.Log` is off), the engine stat overlay's "Grid" / "All" sections; `StructureLoads` counters for the structure.
- **Offline tests** (`Tests/`, no game): `AeroBench` (shapes, table vs exact, cache, wings, structure `struct` / `fail` /
  `replay`, polars; `gate` mode in the gate), `SurfaceTests`, `StructureTests`, `TearTests`, `ThrustTests`,
  `ControlSurfaceTests`, `GearTests`, `ShockGridTests`, `WingStrainTests`, `DebrisTests`, `SettingsTests` (the settings
  registry: metadata, text round trip, groups, host-file forcing, authority, denylist, dev writes never saved, the
  background poll), `SettingsUiTests` (the menu's model over the real registry; `-- dump` prints the menu), `ApiTests`
  (the API's shape, the event ring, the cross-mod binding), `BlockSaveTests` (the blocks' save keys, the old lines'
  reader and migration, the engine serializer round trip with no mod assembly loaded). They compile the engine-free
  folders by path.
- **The gate** - `tools/test_fast.sh`: ModCheck (the game's own compiler and whitelist analyzers, `D:\aero\tools\ModCheck`)
  then every suite. AeroBench's gate also hashes the table-building sources (`Tests/table_sources.txt`): a change there
  must bump `AeroTableCache.Version` (or re-record the hash for a pure refactor: `dotnet run -- tablehash`).

## 10. Settings

- **The registry** (`Runtime/AeroSettings.cs`): every setting is a name over a static field, with its scope (`world` /
  `client`), range, description and menu metadata (`.Ui(label, group, tier, unit, step, choices)`; `Order` = registration
  order = screen order; `.SeenByClients()` sets `ClientVisible` on world values the client's effects read). The store
  (`AeroSettings.Store.cs`): `Set` / `Get` / info, the `name = value` text, the two files, the world save.
- **Where they live.** World scope: saved with the world (`AeroWorldSettingsComponent` on the server planets, only values
  off their default), host overrides in `%APPDATA%\SpaceEngineers2\ModSettings\AerodynamicsMod.world.cfg` (uncommented
  lines win: `IsForced`, refused by `Set`; the forced set is dropped with the world). Client scope:
  `AerodynamicsMod.client.cfg` next to it. Both read by `AeroSettings.Poll` (a pool task every 2 s; applied by the next
  job's poll on its own thread, at most every 1/4 s per thread).
- **Who may change world settings:** `CanEditWorld` - the host or single player (SE2 2.4: wherever the server runs; the
  one place an admin check goes later). `WorldUnsaved` tells the menu a change waits for the world's next save.
- **What is saved** is each setting's `Stored` value - the last one set through the store (API, menu, file, save) - never
  a harness's direct field write (`aeroset`), so a dev override cannot become the player's setting.
- **The menu** (`Ui/`): the Options screen's **Mods** tab, one tab for both mods (Tag contract `ModSettings/v1`, a section
  per mod), rows generated from the registry (`SettingsModel`: basic shown, advanced collapsed, hidden left out; a
  slider's edit applies once it holds still), reset per group (`ResetGroup`), refusal reasons shown. The shared files are
  the Orbital mod's (`tools/sync_settings_ui.sh` there writes them, its `check_contract.sh` checks the copies); edit them
  there. API: `docs/API.md` version 8 and Settings.

## 11. What the save holds

Everything aero saves is on the **engine's** `EntityNameSessionComponentObjectBuilder` (a string -> Entity map), never a
builder type of the mod's: a world file is a binary archive whose type table names every builder type, and with the mod
removed a mod type is missing and the reader throws - the world would not load (object-builder feasibility, 2026-10-15).
The engine type is always there, so a world saved with the mod still loads without it.

| What | Where | Key (value: the entity itself) |
|---|---|---|
| World settings (values off their default) | `AeroWorldSettingsComponent` on each server planet, component id `ae205e77-1a65-4c0d-9b1e-0000000000a5` | `aero-settings:<name = value lines>` |
| Control surface: pitch / yaw / roll, max deflection, trim | `AeroSurfaceSettingsComponent` on the block, id `...b1` | `aero-surface:1 p y r max trim` (every surface) |
| Gear: down / up, Airshock | `AeroGearComponent` on the block, id `...b2` | `aero-gear:1 down airshock` (every gear) |
| Airbrake: max opening | `AeroAirbrakeComponent` on the block, id `...b3` | `aero-airbrake:1 max` (every airbrake) |
| Thruster: attitude fraction | `AeroThrustSettingsComponent` on the block, id `...b4` | `aero-thrust:1 f` (only off the default 0.5) |

- **The blocks' keys** (since 2026-10-15; `Blocks/AeroBlockSave.cs`, the text in `Blocks/AeroBlockSaveForm.cs`, pure):
  each component is injected with a fixed component id and the builder (`InjectAeroComponents`); its `[Init]` reads the
  key, its `[Serializer]` writes a fresh map. Saved with the block, they follow it through a moved grid, blueprints,
  copy-paste and splits. A later format appends values (readers take what they know from any format >= 1).
- **Older saves** held the blocks' state as `surface / gear / airbrake / thrust x y z ...` lines after the world settings,
  matched back to the nearest block by world position (`AeroSurfaceSaved`). They are still READ: the block has no slot for
  the new id, the engine's composite migration inserts the prefab default (an empty map), the block keeps its defaults and
  the old line claims it as before (within 1 m, 0.1 m for thrusters; dropped after 60 s). A block restored from its own key
  is never claimed. Nothing writes the lines any more, so the first save after loading an old world converts it.
  **Remove the reader** (`AeroSurfaceSaved`'s line lists, `Load`, the legacy loops of `ApplyPending`,
  `AeroBlockSaveForm.ParseLegacy`) once one release has shipped with the block keys; a world last saved before that release
  would then load with its aero blocks at their defaults. Airshock was never in the old lines: an old save's gears start
  with it on (the default).
- **Server and client.** The server's entities are what is saved. Surfaces, gears and airbrakes exist on the server only;
  their terminals are handed the server object (`DetailModelPairing`), so the client-side terminal shows the restored
  state. Thrusters exist in both scenes: a server thruster restored from its key passes the value to its client-scene
  copy by position (`AeroSurfaceSaved.Follow`, within 0.1 m, as the old lines did), unless the copy's own builder brought
  it; at save, a server copy at the default writes a changed client copy's value (the old lines' order).
- **The mod removed:** the thruster's slot is dropped by the composite migration (`EOBUpdateContext`: Removes) and the
  mod's own blocks are pruned with their definitions, as before; nothing of the mod's is in the type table. Checked
  offline by `Tests/BlockSaveTests` (binary + Brotli and JSON through the engine serializer, no mod assembly in the type
  context).
