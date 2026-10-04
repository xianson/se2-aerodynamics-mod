# Aerodynamics Mod API

The Aerodynamics Mod exposes its data to other mods through one class, `AeroMod.AeroApi` (`Scripts/Simulation/Integration/AeroApi.cs`).

## How to call it

Space Engineers 2 compiles each mod on its own, so your mod cannot reference this one. Call the API **by reflection** instead:

1. Find the type `AeroMod.AeroApi` in `AppDomain.CurrentDomain.GetAssemblies()`.
2. Read `Version`. It is a `const`, so use `FieldInfo.GetRawConstantValue()`.
3. Cache the `MethodInfo`s you use.

Every signature uses only .NET and engine types (`Entity`, `Vector3`, `Vector3D`, `Vector3I`, `List<T>`, `Delegate`), never an aero type.

```csharp
static MethodInfo _tryGetFlight;
static bool _looked;

static bool AeroFlight(Entity grid, out float mach, out float airspeed)
{
    mach = airspeed = 0f;
    if (!_looked)
    {
        _looked = true;   // (look again when the loaded assemblies change, if aero may load after you)
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = a.GetType("AeroMod.AeroApi");
            if (t == null) continue;
            if ((int)t.GetField("Version").GetRawConstantValue() < 2) break;   // (too old for what you use)
            _tryGetFlight = t.GetMethod("TryGetFlight");
            break;
        }
    }
    if (_tryGetFlight == null) return false;   // (the aero mod is not installed)
    var args = new object[] { grid, null, null, null, null, null };
    if (!(bool)_tryGetFlight.Invoke(null, args)) return false;
    airspeed = (float)args[2]; mach = (float)args[3];
    return true;
}
```

## Conventions

- **Frames:**
  - *local* vectors are in the grid's own frame;
  - *world* vectors are in world axes;
  - *travel* is the direction the grid moves **into** the air, so the wind it feels is the opposite.
- **Units:** SI (m, s, kg, N, Pa).
- **Where it answers:** on the machine that runs the aero simulation (single player, a host, a dedicated server).
  - A grid's server copy answers directly.
  - A client copy in the same process (single player or host) answers from the nearest published record.
  - A remote multiplayer client gets `false`, because nothing is sent to clients yet.
- **Threads:** any. Each answer is one consistent snapshot of the last simulation tick.
- **Stability:** members are only **added** within a version. Changing an existing member bumps `Version`.

## Version 2

A grid can be passed as either its server copy or a client copy in the same process. The detailed calls (surface, wings, prediction) need the grid's server-side model, so a client copy must be flying (published) to answer.

| Member | What it gives |
|---|---|
| `const int Version` | The API contract (3 since version 3; check `>= 2` for these members). |
| `bool TryGetFlight(Entity grid, out Vector3 travelLocal, out float airspeed, out float mach, out float density, out float dynamicPressure)` | The grid's air: travel (local, unit), airspeed (m/s), Mach, density (kg/m³), dynamic pressure (Pa). `false` when the grid is under 1 m/s or not in air. |
| `bool TryGetForces(Entity grid, out Vector3 forceLocal, out Vector3 torqueLocal, out float drag, out float lift, out float frontalArea)` | The aerodynamic load (local), with wings and control surfaces included: force (N), torque (N·m), drag and lift (N), and frontal area (m²). |
| `bool TryGetLift(Entity grid, out Vector3 liftLocal, out float liftCoefficient)` | Lift direction (local, unit) and its coefficient on the frontal area. |
| `bool TryGetEntry(Entity grid, out float strength, out bool external)` | Entry heating now, 0..1, and whether an entry source owns it. |
| `float EntryStrength(float mach, float density)` | Aero's heating law: onset at Mach 2, full at about Mach 2.5, scaled by density on a log scale. |
| `int GetWingTips(Entity grid, List<Vector3> tipsLocal, List<Vector3> normalsLocal)` | The detected wing tips (local m, at the trailing edge) and each wing's lift normal. Returns the count. |
| `int GetSurface(Entity grid, List<Vector3> centresLocal, List<Vector3> normalsLocal, List<float> areas, List<float> cp)` | Every exposed hull face: centre (m), outward normal, area (m²), and the pressure coefficient from the last face-model evaluation. Cp is 0 for a grid that runs on its force table. Pass `null` for lists you don't need. Returns the count, or 0 while the grid rebuilds. Large ships have about 10⁵ faces, so call it rarely. |
| `int GetWingCount(Entity grid)` | The number of wings aero detected (index them from 0). |
| `bool TryGetWing(Entity grid, int index, out Vector3 normalLocal, out Vector3 spanAxisLocal, out Vector3 chordAxisLocal, out Vector3 aeroCenterLocal, out float area, out float span, out float meanChord, out float sweep, out float clAlpha, out float clMax, out float alphaStall)` | A wing's geometry and aerodynamic properties: lift normal; span and chord axes; quarter-chord point; planform area; span and mean chord; leading-edge sweep (rad); lift slope (per rad); maximum CL; stall angle (rad). |
| `bool TryGetWingState(Entity grid, int index, out Vector3 liftLocal, out Vector3 inducedDragLocal, out Vector3 pointLocal, out float cl, out float cdi, out float alphaEffective)` | A wing on the last tick: lift and induced drag (N), where they act, CL, CDi, and effective angle of attack (rad). |
| `bool PredictHullForces(Entity grid, Vector3 velocityLocal, float density, float speedOfSound, out Vector3 forceLocal, out Vector3 torqueLocal, out float frontalArea)` | **Predict** the hull's force and torque for any flight state you choose, from the grid's precomputed force table (shadowing, the subsonic-to-Newtonian blend, wave drag). Torque is about the centre of mass the table was built for. Wings are not included. It is pure, so it suits autopilots and planners. |
| `bool SampleForceTable(Entity grid, Vector3 travelLocal, float[] into)` | The raw table at a direction, for unit dynamic pressure, as 13 floats: F0(3) F1(3) T0(3) T1(3) frontal(1). F0 is the subsonic blend and F1 the Newtonian blend; torques are about the grid origin. |
| `int RegisterEntrySource(string owner, Delegate source)` | Tell aero about grids it cannot see moving. `source` is a `Func<Entity, ValueTuple<Vector3, float>>`: the air's velocity past the grid (world m/s) and how hard it burns (0..1; ≤ 0 means none). Returns the entry-source contract, 2 (it did not change in version 3), or 0 for a wrong delegate type. See below. |
| `bool UnregisterEntrySource(string owner)` / `bool HasEntrySource(string owner, Delegate source)` | Remove a source / check that it is still registered. |
| `void SuppressEffects(Entity grid, bool suppress)` | Hide aero's flight effects (plasma, vapour, tip trails) on a grid, for example a cloaked ship. Not saved. |

### Entry sources

An entry source is for grids whose motion aero cannot see. The case it was built for is the Orbital Mod's reentry "on rails": grids sit still in their frame while the frame is braked through the atmosphere.

- **It owns the grid:** while a source answers with a strength for a grid:
  - its air replaces the grid's own motion for the flow and every effect;
  - its strength replaces aero's heating model.
- **Registering again** under the same owner replaces the old source.
- **A source that throws** is dropped.
- **Sessions:** a new session or a reloaded aero mod starts with no sources. Call `HasEntrySource` every few seconds and register again when it returns `false`. The Orbital Mod's `EntryHost.HookAero` shows the pattern.

## Version 3

Every version-2 member is unchanged. New:

### The atmosphere

SE2 gives only a density: the atmosphere generators (planets, stations) write it into each simulated entity's `AirData` every tick. Aero derives the rest from its own model: the temperature at that density in the standard atmosphere, then the ideal gas.

| Member | What it gives |
|---|---|
| `bool TryGetAtmosphereAt(Vector3D worldPoint, out float density, out float pressure, out float temperature, out float speedOfSound)` | The air at any world point. Density is the game's generator sum (the engine's own law: full inside a generator's constant distance, then linearly less out to its affect distance). `false` until aero has simulated a grid in this process and listed the generators; vacuum answers `true` with zeros. The generators are re-listed every ~10 s. |
| `bool TryGetAtmosphere(Entity entity, out float density, out float pressure, out float temperature, out float speedOfSound)` | The air an entity is in, exactly as the game gave it this tick (its `AirData`): grids, characters, any simulated entity. `false` when it carries no air data. |
| `void AtmosphereAtDensity(float density, out float pressure, out float temperature, out float speedOfSound)` | Aero's model at a density (kg/m³ → Pa, K, m/s). Pure. |
| `string GetAtmosphereStatus()` / `int GetAtmosphereGeneratorCount()` | Why the point query cannot answer (`""` when it can), and how many generators it knows. |

### Thrust

| Member | What it gives |
|---|---|
| `int GetThrustProfiles(List<string> names)` | The profile names: Rocket, Propeller, Turbofan, Turbojet, Ramjet, Scramjet, HelicopterRotor. |
| `float ThrustEffectiveness(string profile, float mach)` | Thrust relative to the static (Mach 0) rating: 0 outside the operating envelope, above 1 where ram pressure helps (a turbojet at Mach 2.5 gives 1.25). `-1` for an unknown profile. Pure. |
| `bool TryGetThrustProfile(string profile, out float minMach, out float maxMach)` | A profile's operating envelope. |
| `int GetThrusters(Entity grid, List<Entity> thrusters, List<string> profiles, List<float> effectiveness)` | The grid's thrusters as aero sees them: the block entity, its profile (atmospheric thrusters are Turbofan; hydrogen and ion are Rocket, flat), and its effectiveness now (the profile at the grid's Mach and the air's angle into the intake; Mach 0 at rest). |

### Change counters

Cache what you read (surface, wings, force table) and read again only when a counter moves.

| Member | What it gives |
|---|---|
| `bool TryGetChangeCounters(Entity grid, out int hull, out int wings, out int table)` | Per grid. **hull:** the exposed surface was rebuilt or updated cell by cell. **wings:** detected again, updated, or a wing cell lost. **table:** a new force table installed (the hull's forces changed). Each only goes up; compare with what you saw last. |
| `int GetGeneration()` | Bumped when every grid rebuilds from scratch (aero switched on again, or a setting that needs it). |

### Aero blocks (read only)

Commands (setting a control surface's deflection, an airbrake's deployment) will follow once the blocks' own control path is final.

| Member | What it gives |
|---|---|
| `int GetAeroBlocks(Entity grid, List<string> kinds, List<Vector3I> cells, List<Vector3> positionsLocal)` | Every aero block: kind (`ControlSurface`, `Airbrake`, `AirIntake`, `AtmosphericScoop`, `HelicopterRotor`), grid cell, action point (local m). |
| `int GetAeroBlockState(Entity grid, Vector3I cell, List<string> names, List<float> values)` | The block at a cell, as named values from the last tick (below). Returns the count; 0 when no aero block is there. |

| Kind | Values |
|---|---|
| ControlSurface | `DeflectionInput` (−1..1), `MaxDeflection` (deg), `Lift`, `Drag` (N), `AoA` (deg) |
| Airbrake | `DeployFraction` (0..1), `Drag` (N), `Effectiveness` (0..1) |
| AirIntake | `MassFlow` (kg/s), `TotalPressure` (Pa), `RamPressureRatio` |
| AtmosphericScoop | `MassFlow` (kg/s), `CollectionRate`, `Collected`, `Drag` (N) |
| HelicopterRotor | `Collective` (0..1), `Thrust` (N), `DensityScale`, `TranslationalLift`, `GroundEffect`, `Autorotation` (N) |

### On / off and settings

| Member | What it gives |
|---|---|
| `bool GetEnabled()` / `bool SetEnabled(bool on)` | The whole simulation on or off (the world setting `Aero.Enabled`). |
| `int GetSettings(List<string> names)` | Every setting's name. |
| `bool TryGetSettingInfo(string name, out string type, out double defaultValue, out double min, out double max, out string scope, out bool rebuilds, out string description)` | Type (`bool`, `int`, `float`), default, range (a bool is 0..1), scope (`world` or `client`), whether a change rebuilds every grid, and what it does. |
| `bool TryGetSetting(string name, out double value)` | The value now (a bool as 0 / 1). |
| `bool SetSetting(string name, double value, out string why)` | Change it: it takes effect on the next tick or frame and is saved. `false` with a reason: no such setting, out of range, a rule between settings, or a world setting where the server does not run. |
| `int GetSettingsVersion()` | Bumped on every change (the API, a settings file, a world load). Watch it to re-read. |

Note: `TryGetForces` and the flow calls report the model's force **before** the world's drag and lift multipliers.

## Settings

Two scopes. Every change takes effect at once: the values are fields the simulation reads each tick or frame. `Model.LargeGridCellScale` is the one setting that rebuilds every grid (a few seconds of rebuilding on big ships).

- **World** (the same for every player): saved **with the world**, in a component on the server planets (`AeroWorldSettingsComponent`, the engine's `EntityNameSessionComponentObjectBuilder`, key `aero-settings:`). Only values off their default are saved. A host or dedicated server can force values in `%APPDATA%\SpaceEngineers2\ModSettings\AerodynamicsMod.world.cfg`: uncommented lines win over the save. The mod writes that file, all commented out, the first time.
- **Client** (this PC): `%APPDATA%\SpaceEngineers2\ModSettings\AerodynamicsMod.client.cfg`, written with every setting and its description, and rewritten after a change through the API.

Both files are read again within ~2 s of an edit, so they can be changed while playing. The format is `name = value`, `#` for comments, `true` / `false` for switches.

| Setting | Scope | Type | Default | Range | What it does |
|---|---|---|---|---|---|
| `Aero.Enabled` | world | bool | true | | The whole aero simulation. On again: every grid rebuilds. |
| `Aero.DragMultiplier` | world | float | 1 | 0..10 | The force along the motion, times this. |
| `Aero.LiftMultiplier` | world | float | 1 | 0..10 | The force across the motion (wings, control surfaces, the hull's own lift), times this. Torque is scaled by both, weighted by the drag and lift magnitudes. |
| `Aero.ThrustTorque` | world | bool | true | | Thrusters off the centre of mass turn the ship. Off: the older phantom attitude hold. |
| `Model.LargeGridCellScale` | world | int | 2 | 1..4 | Large-grid surface resolution (1 = 0.25 m cells). **Rebuilds every grid.** |
| `Entry.OnsetMach` | world | float | 2 | 0.5..25 | Entry heating starts here. Must stay under `Entry.FullMach`; changing it keeps the full point where it is. |
| `Entry.FullMach` | world | float | 2.5 | 0.6..30 | Entry heating is full here. |
| `Entry.HeatLag` | world | float | 2 | 0.05..30 | Seconds the hull takes to heat up and cool down. |
| `Perf.FrameBudgetMs` | world | float | 1 | 0.1..16 | The most aero takes of a simulation frame; past it, grids not piloted wait a few frames. |
| `Perf.Workers` | world | int | 2 | 1..16 | Background cores for hull and force-table builds. |
| `Perf.TableCache` | world | bool | true | | Reuse built force tables. |
| `Perf.TableCacheMB` | world | int | 256 | 16..4096 | The table cache's size. |
| `Perf.LocalUpdates` | world | bool | true | | Update only the changed part of a hull. |
| `Perf.ChunksPerUpdate` | world | int | 8 | 1..64 | Hull chunks updated per pass. |
| `Perf.MinSpeedForUpdates` | world | float | 10 | 0..1000 | Below this speed (m/s) an edited hull waits before updating its forces. |
| `Perf.QuietBeforeFullRebuild` | world | float | 5 | 0.5..120 | Seconds without block changes before a cleanup rebuild. |
| `Fx.Effects` | client | bool | true | | Every aero visual effect. |
| `Fx.FlightField` | client | bool | true | | The flight effects (plasma, vapour, tip trails); off: none. |
| `Fx.Plasma` / `Fx.Vapour` / `Fx.Tips` / `Fx.SpotLight` | client | bool | true | | Each group of the flight effects. |
| `Fx.MaxGrids` | client | int | 4 | 1..32 | Grids with flight effects at once (the nearest). |
| `Fx.Range` | client | float | 4000 | 100..50000 | Distance (m) past which a grid shows no flight effects. |
| `Fx.SpawnPerTick` | client | int | 12 | 1..200 | Effects started per frame while a grid's effects build. |

**Multiplayer:** SE2 2.4 runs its server in the same process as the client (its networking is a mock), so world values are the server's and the flight effects read them directly. A process that has only seen a client scene refuses world sets. When SE2 gets real multiplayer, the world values the clients' effects use (the entry Mach pair) will need replicating.

## Reaching aero from a script

SE2 2.4 has the engine side of programmable blocks (`InGameScriptingComponent`, compiled against the same whitelist as mods), but no vanilla block uses it, so there is no player script yet. A script, once there is one, could reach this API the same way a mod does: by reflection, if its whitelist keeps `System.Reflection`. There is no mod-to-mod message channel and no mod settings screen in SE2; the settings files and this API are the way in.
