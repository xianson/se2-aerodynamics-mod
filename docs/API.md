# Aerodynamics Mod API

The Aerodynamics Mod exposes its data to other mods through one class, `AeroMod.AeroApi` (`Scripts/Simulation/Integration/AeroApi.cs`).

## How to call it

Space Engineers 2 compiles each mod on its own, so your mod cannot reference this one. Call the API **by reflection** instead:

1. Find the type `AeroMod.AeroApi` in `AppDomain.CurrentDomain.GetAssemblies()`.
2. Read `Version`. It is a `const`, so use `FieldInfo.GetRawConstantValue()`.
3. Cache the `MethodInfo`s you use.

Every signature uses only .NET and engine types (`Entity`, `Vector3`, `List<Vector3>`, `Delegate`), never an aero type.

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
| `const int Version` | The API contract (2). |
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
| `int RegisterEntrySource(string owner, Delegate source)` | Tell aero about grids it cannot see moving. `source` is a `Func<Entity, ValueTuple<Vector3, float>>`: the air's velocity past the grid (world m/s) and how hard it burns (0..1; ≤ 0 means none). Returns `Version`, or 0 for a wrong delegate type. See below. |
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
