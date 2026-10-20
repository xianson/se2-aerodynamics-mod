# Aerodynamics Mod API

The Aerodynamics Mod exposes its data to other mods through one class, `AeroMod.AeroApi` (`Scripts/Simulation/Api/AeroApi.cs`).

## How to call it

Space Engineers 2 compiles each mod on its own, so your mod cannot reference this one. Call the API **by reflection** instead:

1. Find the type `AeroMod.AeroApi` in `AppDomain.CurrentDomain.GetAssemblies()`.
2. Read `Version`. It is a `const`, so use `FieldInfo.GetRawConstantValue()`.
3. Find each member by its name **and its parameter types** (`GetMethod(name, Type[])`; an `out` parameter is `typeof(T).MakeByRefType()`) and cache it. For a call made often, bind it once as a typed delegate (`Delegate.CreateDelegate(typeof(YourDelegate), method, false)`: no argument array and no boxing per call).

Every signature uses only .NET and engine types (`Entity`, `Vector3`, `Vector3D`, `Vector3I`, `Delegate`, arrays of these and of primitives; `List<T>` in members up to version 6), never an aero type. From version 7 a member takes caller-owned **arrays** (no allocation per call); no member is overloaded or has a default parameter.

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
            var f = typeof(float).MakeByRefType();
            _tryGetFlight = t.GetMethod("TryGetFlight", new[] { typeof(Entity), typeof(Vector3).MakeByRefType(), f, f, f, f });
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
- **Threads:** any. Each answer is one consistent snapshot of the last simulation tick (version 7: of the tick or structure evaluation it was published at - see *How version 7 answers*).
- **Locks (the lock rule):** never call this API while holding a lock of your own, and aero never calls another mod while holding one of its own (its reads of the Orbital Mod are made with none held). A call that waits on the other mod's lock while the other waits on yours is a deadlock neither mod can see. The Orbital Mod collects its calls into aero under its frame lock and makes them after it.
- **Stability:** members are only **added**; an existing member's signature and meaning never change, and each version's additions are listed under its heading below. `Tests/ApiTests` fails the gate on a changed version-2-6 signature, an overload, a type a caller cannot name, a default parameter, or an undocumented member.
- **Never overloaded:** no two public members share a name. A name-only lookup (`GetMethod(name)`) throws `AmbiguousMatchException` the day an overload appears, so a new shape gets a **new name** and the old member stays. Look members up with their parameter types anyway. The Orbital Mod's `tools/check_contract.sh` fails its build on an overload in either mod's API.

## Version 2

A grid can be passed as either its server copy or a client copy in the same process. The detailed calls (surface, wings, prediction) need the grid's server-side model, so a client copy must be flying (published) to answer.

| Member | What it gives |
|---|---|
| `const int Version` | The API contract (10 now; check `>=` the version that added what you use - members are only added). |
| `bool TryGetFlight(Entity grid, out Vector3 travelLocal, out float airspeed, out float mach, out float density, out float dynamicPressure)` | The grid's air: travel (local, unit), airspeed (m/s), Mach, density (kg/m³), dynamic pressure (Pa). `false` when the grid is under 1 m/s or not in air. |
| `bool TryGetForces(Entity grid, out Vector3 forceLocal, out Vector3 torqueLocal, out float drag, out float lift, out float frontalArea)` | The aerodynamic load (local), with wings and control surfaces included: force (N), torque (N·m), drag and lift (N), and frontal area (m²). |
| `bool TryGetLift(Entity grid, out Vector3 liftLocal, out float liftCoefficient)` | Lift direction (local, unit) and its coefficient on the frontal area. |
| `bool RequestForceTable(Entity grid)` | Build the grid's force table even out of air and gravity (a grid on rails); true once it exists (then `PredictHullForces` answers). |
| `bool TryGetGroundHeight(Entity grid, out float height)` | Height above the ground under the grid (m, at its origin along gravity; the ship itself ignored); -1 when none within 2 km. |
| `bool TryGetEntry(Entity grid, out float strength, out bool external)` | Entry heating now, 0..1, and whether an entry source owns it. |
| `float EntryStrength(float mach, float density)` | Aero's heating law: onset at Mach 2, full at about Mach 2.5, scaled by density on a log scale. |
| `int GetWingTips(Entity grid, List<Vector3> tipsLocal, List<Vector3> normalsLocal)` | The detected wing tips (local m, at the trailing edge for the last tick's airflow; before the first, the edge at the high end of the wing's grid axis) and each wing's lift normal. Returns the count. |
| `int GetSurface(Entity grid, List<Vector3> centresLocal, List<Vector3> normalsLocal, List<float> areas, List<float> cp)` | Every exposed hull face: centre (m), outward normal, area (m²), and the pressure coefficient from the last face-model evaluation. Cp is 0 for a grid that runs on its force table. Pass `null` for lists you don't need. Returns the count, or 0 while the grid rebuilds and until the grid's next tick after your first call (the grid publishes its faces each tick while they are asked for). Large ships have about 10⁵ faces, so call it rarely. |
| `int GetWingCount(Entity grid)` | The number of wings aero detected (index them from 0). |
| `bool TryGetWing(Entity grid, int index, out Vector3 normalLocal, out Vector3 spanAxisLocal, out Vector3 chordAxisLocal, out Vector3 aeroCenterLocal, out float area, out float span, out float meanChord, out float sweep, out float clAlpha, out float clMax, out float alphaStall)` | A wing's geometry and aerodynamic properties: lift normal; span and chord axes (chord toward the trailing edge); quarter-chord point; planform area; span and mean chord; leading-edge sweep (rad); lift slope (per rad); maximum CL; stall angle (rad). Which edge leads is the air's: the chord axis, quarter chord and sweep are for the last tick's airflow (before the first, the edge at the low end of the wing's grid axis leads). |
| `bool TryGetWingState(Entity grid, int index, out Vector3 liftLocal, out Vector3 inducedDragLocal, out Vector3 pointLocal, out float cl, out float cdi, out float alphaEffective)` | A wing on the last tick: lift and drag (N; the drag is the wing's whole: drag due to lift, profile, separation and, since 2026-10-18, its own wave drag), where they act, CL, CDi (drag due to lift: CL^2/(pi e AR), toward CL^2/CLa as the leading-edge suction goes supersonic), and effective angle of attack (rad). |
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
| `bool TryGetAtmosphereAt(Vector3D worldPoint, out float density, out float pressure, out float temperature, out float speedOfSound)` | The air at any world point. Density is the game's generator sum (the engine's own law: full inside a generator's constant distance, then linearly less out to its affect distance) with aero's thin upper tail where the game's air would stop dead (`AtmosphereTail`: the fade's outer part continues exponentially, one scale height = a quarter of the fade, out to 9 scale heights; never less than the game's). `false` until aero has simulated a grid in this process and listed the generators; vacuum answers `true` with zeros. The generators are re-listed every ~10 s. |
| `bool TryGetAtmosphere(Entity entity, out float density, out float pressure, out float temperature, out float speedOfSound)` | The air an entity is in as aero flies it: the game's (its `AirData`, this tick: grids, characters, any simulated entity) or, above where the game's air stops, aero's upper tail. `false` with no air data and no tail there. |
| `void AtmosphereAtDensity(float density, out float pressure, out float temperature, out float speedOfSound)` | Aero's model at a density (kg/m³ → Pa, K, m/s). Pure. |
| `string GetAtmosphereStatus()` / `int GetAtmosphereGeneratorCount()` | Why the point query cannot answer (`""` when it can), and how many generators it knows. |

### Thrust

| Member | What it gives |
|---|---|
| `int GetThrustProfiles(List<string> names)` | The profile names: Rocket, Propeller (piston engine, fixed-pitch prop), Turboprop (the Turboprop blocks' curve), DuctedFan (the fans' airspeed fade, by Mach at sea level), Jet (the vanilla atmospheric thrusters' curve: 85 % at Mach 0.85, then the ram rise above the rating - 108 % at Mach 1, 198 % at Mach 1.6 - gone at 2.8), Turbofan, Turbojet, Ramjet (the Ramjet blocks' curve: nothing below Mach 0.5, 100 % at 2.5-3.5, gone past 6), Scramjet, HelicopterRotor. |
| `float ThrustEffectiveness(string profile, float mach)` | Thrust relative to the static (Mach 0) rating: 0 outside the operating envelope, above 1 where ram pressure helps (a turbojet at Mach 2.5 gives 1.25). `-1` for an unknown profile. Pure. |
| `bool TryGetThrustProfile(string profile, out float minMach, out float maxMach)` | A profile's operating envelope. |
| `int GetThrusters(Entity grid, List<Entity> thrusters, List<string> profiles, List<float> effectiveness)` | The grid's thrusters as aero sees them: the block entity, its profile (vanilla atmospheric thrusters are Jet; hydrogen and ion are Rocket, flat; the mod's propulsor blocks their own - Turboprop, HelicopterRotor, Ramjet (hydrogen thrusters underneath), or DuctedFan for the fans; each sampled by Mach for axial flight at sea level, the blocks themselves following their true airspeed), and its effectiveness now, as applied: with `Aero.ThrustTorque` on (the default) a vanilla atmospheric thruster's Jet factor at the grid's Mach (above 1 in the supersonic ram rise: up to 1.98 at Mach 1.6) x its inlet-angle factor in flight (full within 28 deg of straight-in air, 0.15 by 65 deg, 0 with the air from behind past 125 deg; faded in from 20 to 60 m/s true airspeed) - its game maximum is shaped by both, the game's air-density efficiency on top - and 1 for the rest (a propulsor block's own curve is already in its maximum - a ramjet's with its density factor and its own inlet unstart; turboprops, fans and rotors have no inlet factor); with `Aero.ThrustTorque` off the game's own thrust is untouched: 1 for every thruster. `GetThrustAlong` / the design probe assume straight-in air. |

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
| `int GetAeroBlocks(Entity grid, List<string> kinds, List<Vector3I> cells, List<Vector3> positionsLocal)` | Every aero block: kind (`ControlSurface`, `Airbrake`, `AirIntake`, `AtmosphericScoop`), grid cell, action point (local m). |
| `int GetAeroBlockState(Entity grid, Vector3I cell, List<string> names, List<float> values)` | The block at a cell, as named values from the last tick (below). Returns the count; 0 when no aero block is there. |

| Kind | Values |
|---|---|
| ControlSurface | `DeflectionInput` (−1..1), `MaxDeflection` (deg), `Lift`, `Drag` (N), `AoA` (deg) |
| Airbrake | `DeployFraction` (0..1), `Drag` (N), `Effectiveness` (0..1) |
| AirIntake | `MassFlow` (kg/s), `TotalPressure` (Pa), `RamPressureRatio` |
| AtmosphericScoop | `MassFlow` (kg/s), `CollectionRate`, `Collected`, `Drag` (N) |

### Structure and hull heat
| Call | What |
|---|---|
| `bool TryGetStall(Entity grid, out int state, out float marginDeg, out float liftShare, out float gAvailable)` | The stall warning of a player's grid, from the whole ship's air force (any shape, no wings needed): the best lift within reach (-15° to +60° from where it flies). `state` 0 none, 1 approaching, 2 stalled (pulling further only loses lift), each held 0.3 s; `marginDeg` the best lift's angle from now (negative: behind); `liftShare` the share of it in use; `gAvailable` the g it would give. `false`: not evaluated. |
| `bool TryGetLoac(Entity grid, out int state, out float ratio)` | Loss of attitude control of a player's grid, predicted: the commanded attitude and the way to it - the air's torque there at the speed now against the gyros', thrusters' and control surfaces' reach. `state` 0 none, 1 at the limit (over 90% needed), 2 lost (more than available), held 0.25 s; `ratio` the worst needed / available. |
| `void SetFocusGrid(Entity grid)` | The grid a player is flying (call about once a second while seated). Only such grids' structure and hull heat are evaluated (gauges, damage); a grid stays one for 10 s after the last call. |
| `void NoteMoved(Entity grid)` | The grid was just moved by someone else (a teleport, a frame shift, a rails stow or arrival). Its physics contacts for the next steps (`StructureContactLoads.MaskSteps`) are not taken as loads on its structure. Server; call after `SetWorldTransform`, before the next physics step. Takes no lock. The Orbital Mod's moves are also read back (the pull side: `OrbitalApi.TryGetGridEdits`' move stamp), so a push that comes late or never is covered. |
| `bool TryGetStructure(Entity grid, out float maxUtilisation, out float maxSkinK, out float heatShare)` | The grid's structure (the method of sections on its blocks: air, thrust, gyros and its own turning) and its hull's aerodynamic heating, evaluated in the background a few times a second. `maxUtilisation`: the worst joint's load over its strength now (heat and damage weaken it; 1 is yield, from `Structure.EnduranceUtil` the joint tires (`Structure.Fatigue`), past 1 it wears through - faster the further over - and fails when its health is out, or at once at `Structure.SnapUtil`, with `Structure.Damage` on); `maxSkinK`: the hottest hull skin (K); `heatShare`: the hottest block's way from softening to burning (0..1). `false`: not evaluated yet, or `Structure.Loads` off. |

### Wing strain

| Member | What it gives |
|---|---|
| `bool TryGetWingStrain(Entity grid, int index, out float strain, out float rootMoment, out float capacity)` | A wing's structural strain (last tick, smoothed over ~0.15 s): its root bending moment over the root's capacity. 1 is the limit (past it the root wears, with `Structure.WingStrain` on); 1.5 breaks the wing off. Root moment and capacity in N·m (for a wing across the centre of mass, the worse side's). Computed whether or not the damage is on. `false`: no such wing, or not computed yet. |
| `float GetMaxWingStrain(Entity grid)` | The grid's highest wing strain, as above; 0 with no wings. |

The model (`Lift/WingStrain.cs`): a wing is a cantilever rooted at its span end nearest the centre of mass (a wing the centre of mass lies across, one wing through the fuselage, is two). Its load (lift and induced drag along its normal) spread evenly along it gives the bending moment at each span station; the blocks in that station's slab (within the wing's chord, and 2 blocks - at least 2.5 m - above and below it, so a fuselage counts) give its capacity, per chord column width × depth² (one block deep: a per-cell strength going as the block size cubed; heavy armour 4×). The worst station decides, and its blocks are the ones that wear and break: the root of a plain wing, where it leaves the fuselage for a wing through one. At `Structure.WingStrength` 1 it is calibrated so that a light-armour wing 4 blocks long and 1 block deep, large or small grid, snaps at 40 kPa wing loading (wear from 26.7 kPa); strain goes as the length squared (8 blocks: snaps at 10 kPa). Long slender wings are far weaker: on the bench ships (light armour assumed) the small-grid jetliner's main wing snaps at about 0.9 kPa, the pelican's at 10 kPa (`dotnet run -c Release -- strain` in `Tests/AeroBench`); its default, 25, holds those real ships to ~6 g.

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

## Versions 4 and 5: the design probe

The build hints: the ship **as if it were flying**, at any direction and Mach, from its force table (`Design/DesignProbe.cs`). Each answers for a client grid too (paired with its server copy), and covers the whole ship (the grid and its subgrids). Any thread; a probe evaluates the table (no simulation state is touched).

Freshness (2026-10-15): the ship's mass, centre of mass, gyros, thrusters and joints are what the server grid's own simulation step last published for the probe (a snapshot every 0.25 s while it is asked, kept for 2 s after the last ask; used up to 1 s old) - never read from your thread. So the **first** call after a quiet spell (none in the last 2 s) answers as "not ready yet": `TryDesign` / `TryGetCenterOfMass` `false`, `GetTopSpeed(At)` / `GetControlRatio` NaN, `GetThrustAlong` / `GetMass` / `GetDesignPressures` 0 - call again a frame later. A build hint polled every frame sees this once. Figures lag the grid by at most ~0.25 s; with aero switched off (no simulation step) every member answers "not ready".

| Member | Since | What it gives |
|---|---|---|
| `bool TryDesign(Entity grid, Vector3 travelLocal, float mach, out Vector3 copLocal, out Vector3 forceLocal, out Vector3 torqueLocal, out float stabilityA, out float stabilityB, out float streamlining)` | 4 | The whole ship (hull and wings) travelling along `travelLocal` at this Mach in sea-level air: its centre of pressure (grid-local: the force's line nearest the centre of mass), force (N) and torque (N·m, about the centre of mass), static stability about the two axes across the flow (positive: the air turns it back; per radian, over q × area × size) and its streamlining. `false`: no aero, or the table is not built yet (asked for: try again shortly). |
| `float GetTopSpeed(Entity grid, Vector3 dirLocal)` | 4 | Sea-level top speed along `dirLocal` (m/s): forward thrust (each thruster's profile at the Mach, where one applies - the legacy thrust path only; by default the game's thrust as it is) against drag. NaN no aero or table; 0 too little thrust; +∞ past 3 km/s. |
| `float GetThrustAlong(Entity grid, Vector3 dirLocal, float mach, out Vector3 centreLocal)` | 4 | Thrust along `dirLocal` at sea level and this Mach (N), and where it acts on average: a vanilla atmospheric thruster's rating x its Jet curve at that Mach, a propulsor block's rating x its kind's curve at that Mach (for axial flight: Turboprop, DuctedFan, HelicopterRotor, Ramjet - not its live maximum, which carries its airspeed, rpm and torque cap now), hydrogen and ion flat. |
| `bool TryGetCenterOfMass(Entity grid, out Vector3 comLocal)` | 4 | The ship's centre of mass (grid-local; subgrids included). |
| `float GetWavePeak(float streamlining)` | 4 | The transonic drag peak (× subsonic) a streamlining gives: 3.2 for a brick down to 1.12 for a sleek hull (2026-10-17: a sharp peak at Mach 1, falling fast past it - `ForceTable.HullWave`; 1.45 until the Mach wall was made reachable the same day). Pure. |
| `float GetTopSpeedAt(Entity grid, Vector3 dirLocal, float throttle)` | 5 | As `GetTopSpeed` at throttle 0..1 (cruise). |
| `float GetControlRatio(Entity grid, Vector3 dirLocal, float speed)` | 5 | How well the ship holds its attitude along `dirLocal` at `speed` (m/s, sea level): the gyros' and thrusters' torque over the air's torque at a 5° upset. Over 1: it can. NaN: no aero or table. |
| `float GetMass(Entity grid)` | 5 | The ship's mass (kg, subgrids included); 0 unknown. |
| `int GetDesignPressures(Entity grid, Vector3 travelLocal, float mach, List<Vector3> centresLocal, List<Vector3> normalsLocal, List<float> areas, List<float> cp, int max)` | 5 | The drag overlay: hull faces and their pressure coefficients along `travelLocal` at this Mach (positive pushes in, negative pulls), shadowed windward faces and wing skins left out, at most `max` (spread over the hull). Returns the count. |

```csharp
// the build hint: stable along the nose (-Z) at Mach 0.8?
var args = new object[] { grid, new Vector3(0, 0, -1), 0.8f, null, null, null, null, null, null };
if ((bool)_tryDesign.Invoke(null, args) && (float)args[6] > 0f && (float)args[7] > 0f) Hint("stable");
```

### For tests

| Member | What it gives |
|---|---|
| `string GetFxConsistency(bool reset)` | The flight effects' consistency since the last reset (frames, pops, rebuilds while lit, frames lost, group toggles, the largest step, the peak). The Orbital Mod's `fxcheck` harness command. Not for gameplay. |

## Version 6

### Drag from your mod

Put drag on a grid without depending on the aero mod's block types: a parachute, a deployable airbrake, landing gear, a
door left open, cargo hanging in the air. Aero applies it every tick as part of the grid's own air.

| Member | What it does |
|---|---|
| `bool SetDrag(Entity grid, string key, Vector3 pointLocal, float cdA)` | Drag area `cdA` (Cd × area, m²) at a grid-local point. The force is `q × cdA` against **that point's** travel through the air (the grid's rotation included), so drag off the centre of mass also turns the ship. The same key replaces the drag; `cdA` 0 removes it. |
| `bool SetPlateDrag(Entity grid, string key, Vector3 pointLocal, float cdA, Vector3 normalLocal)` | The same for a **flat plate**: `cdA` is its face-on drag area, scaled by how squarely the air meets it (`|normal · travel|`). Edge-on it gives nothing. |
| `int ClearDrag(Entity grid, string keyPrefix)` | Remove every drag whose key starts with the prefix (`null` or `""`: all). Returns how many went. |
| `bool TryGetDrag(Entity grid, string key, out Vector3 pointLocal, out float cdA, out Vector3 normalLocal)` | One drag as set (normal zero: not a plate). |

- **Keys:** prefix them with your mod's name (`"MyMod.Chute#3"`) so `ClearDrag(grid, "MyMod.")` removes only yours.
- **Lifetime:** server copy only, and not saved. Set it again when your block loads or changes; it goes when the grid
  goes.
- **Limits:** `false` for a bad value (not finite, or a point over 2 km from the grid's origin); `cdA` is capped at
  10,000 m².
- **How the control law sees it:** it's counted as the airframe's air, in `TryGetForces` and in what the attitude
  control answers, not as control-surface torque.
- **Changing it each tick is fine:** a deploying chute can set its growing area every frame (one dictionary write).

```csharp
// a tail chute, 30 m behind the grid's origin, opening over 2 s
_setDrag.Invoke(null, new object[] { grid, "MyMod.Chute", new Vector3(0, 0, 30), 60f * Math.Min(1f, t / 2f) });
```

## Version 7

Every earlier member is unchanged. New: the flight state as one packed record, the control surfaces and thrusters per tick,
the structure and the hull's heat **block by block and face by face**, the structure's failures as a polled event queue,
and a way for **another heat model** (the planned thermal mod) to take the temperatures over. Code:
`Api/AeroApi.Flight.cs`, `Api/AeroApi.Structure.cs`; the publishing: `Grid/AeroGridComponent.Api.cs` (the tick) and
`Structure/Server/StructureServer.Api.cs` (the structure's evaluation).

### How version 7 answers

- **Watched, then published.** Aero copies these records for a grid only while someone reads them: the first read of a
  record (or `WatchGrid`) starts it, and every read keeps it on for 5 s more. Each record has its own watch - the flight
  record (flight state, control surfaces, thruster forces), the structure's block state, the heat faces, `GetSurface`'s
  faces - and a read keeps on only the record it reads. So the **first read of a record answers 0 / `false`**, and
  the next simulation tick (flight) or structure evaluation (blocks, faces) has the answer. A record nobody has read for
  10 s (its 5 s, then 5 s more) is dropped - its memory freed - and answers 0 again on its next first read. A grid nobody
  reads costs one null test a tick.
- **Buffers are yours.** Every array is the caller's; aero fills `min(array length, count)` and returns the full count, so
  a short array tells you to grow it. Pass `null` for what you do not want. No call allocates (`GetFlightStateNames`
  aside: call it once), none has exception handling on the way, and none hands out aero's own arrays.
- **Any thread.** Each call copies one consistent snapshot out under the grid's own small lock (the writer fills a spare
  buffer and swaps it in under the lock, so no reader waits on a computation). The lock rule above still holds.
- **The layout.** Per-block arrays are in the structure's block order, `layout`. Every per-block call returns the layout
  its data is of; it moves when the structure is rebuilt (blocks added, removed or broken off) - read
  `GetStructureBlocks` again then. A state call answers 0 until the first evaluation of a new layout is published.
- **Server copy.** As before: the machine that runs the simulation. A client copy in the same process is answered from its
  server copy.
- **The structure is evaluated only for a grid a player flies** (seated, `SetFocusGrid`, or steered by its target), with
  `Structure.Loads` on, a few times a second (`Structure.Rate`). Other grids have a flight state but no block state.

### Watching and the version

| Member | Since | What it does |
|---|---|---|
| `bool WatchGrid(Entity grid, float seconds, bool faces)` | 7 | Publish the grid's version-7 records for `seconds` (at most 600) from now - call it a tick before your first read. `faces`: the heat faces too (big ships have 10⁵: only if you use them). `false`: no aero for this grid here. |
| `int GetApiVersion()` | 7 | `Version` as a call, for a caller that binds typed delegates only. |

### The flight state

| Member | Since | What it gives |
|---|---|---|
| `int GetFlightState(Entity grid, float[] into)` | 7 | The last tick's flight state, packed (the table below). Fills `min(into.Length, length)`; returns how many (0: not published yet, or no aero). |
| `string[] GetFlightStateNames()` | 7 | The values' names in index order (a new array: call it once). Its length is the state's length in this version. |
| `int GetFlightStateIndex(string name)` | 7 | A value's index by name; -1 none. Look your indices up once. |

**Stability of the layout:** an index never changes meaning; later versions only **append** (the state grows). Size your
array from `GetFlightStateNames().Length`, or just for the indices you use.

Frames: *local* = the grid's frame; the travel is the direction the grid moves **into** the air.

| Index | Name | Unit | What |
|---|---|---|---|
| 0 | `Age` | s | Since the tick it was taken (filled at your read). A quiet grid at rest publishes only every 30 ticks. |
| 1 | `Airspeed` | m/s | Through the air (the wind in). |
| 2 | `GroundSpeed` | m/s | Its physics speed. |
| 3 | `Mach` | | The airspeed over the local speed of sound, every step (the aero model's speed of sound; the value the Jet curve and the propulsors use). |
| 4 | `Density` | kg/m³ | The air aero flew it in (the game's, or the upper tail). |
| 5 | `DynamicPressure` | Pa | q of the model's last result. |
| 6-8 | `TravelX/Y/Z` | unit, local | Into the air; zero under 0.1 m/s. |
| 9-11 | `WindX/Y/Z` | m/s, **world** | The wind at the grid (`AeroWind`). |
| 12-14 | `AngularVelocityX/Y/Z` | rad/s, local | |
| 15 | `Mass` | kg | The physics mass (this grid). |
| 16-18 | `CenterOfMassX/Y/Z` | m, local | |
| 19 | `GroundHeight` | m | As `TryGetGroundHeight`; -1 none. |
| 20-22 | `UpX/Y/Z` | unit, local | Against gravity; zero out of gravity. |
| 23 | `Gravity` | m/s² | The planet's (`GravityEffectData.GravitySum`). The engine applies it x the world's `GravityMultiplier` (vanilla 2) less the dampeners' gravity hold: a ship nothing holds weighs `Mass` x this x the multiplier. |
| 24-26 | `ForceX/Y/Z` | N, local | The air's force **applied** this tick (the world's drag / lift multipliers in; other mods' drag in; zero when not applied). |
| 27-29 | `TorqueX/Y/Z` | N·m, local | Its torque about the centre of mass. |
| 30-32 | `AirframeTorqueX/Y/Z` | N·m, local | Of that, the airframe's own (hull, wings) - what the attitude control feeds forward. |
| 33-35 | `SurfaceTorqueX/Y/Z` | N·m, local | Of that, the control surfaces' (actuators). |
| 36 | `Drag` | N | The model's (before the multipliers). |
| 37 | `Lift` | N | The model's. |
| 38 | `FrontalArea` | m² | |
| 39 | `LoadFactor` | g | The applied air force over the weight at 9.81 m/s². |
| 40 | `StallState` | | As `TryGetStall`: 0 none, 1 approaching, 2 stalled (a player's grid; 0 otherwise). |
| 41 | `StallMarginDeg` | deg | The best lift's angle from now (negative: behind). |
| 42 | `StallLiftShare` | 0..1 | Of the best lift, in use now. |
| 43 | `StallGAvailable` | g | The best lift's g; 0: never evaluated. |
| 44 | `LoacState` | | As `TryGetLoac`: 0 none, 1 at the limit, 2 lost. |
| 45 | `LoacRatio` | | The worst needed / available torque. |
| 46 | `MaxUtilisation` | | As `TryGetStructure` (1: yield). |
| 47 | `MaxSkinK` | K | The hottest hull skin (aero's own faces). |
| 48 | `HeatShare` | 0..1 | The hottest block's way from softening to burning. |
| 49 | `MaxWingStrain` | | As `GetMaxWingStrain`. |
| 50 | `SteerCap` | 0..1 | The flight controller's steering thrust × this (1 unless it would overload the structure). |
| 51 | `ControlValid` | 0/1 | The aero-aware attitude law flew this tick (then 52-54 mean something). |
| 52-54 | `ControlDemandX/Y/Z` | N·m, local | The attitude law's whole torque demand (I × its acceleration, the airframe's torque taken out). |
| 55 | `AttitudeAware` | 0/1 | The aero-aware law flew the gyros and thrusters (else the game's own laws). |
| 56-58 | `GyroTorqueX/Y/Z` | N·m, local | The gyros' torque it gave. |
| 59-61 | `ThrusterTorqueX/Y/Z` | N·m, local | The thrusters' steering torque. |
| 62-64 | `ReachX/Y/Z` | N·m per axis | The gyros' and thrusters' reach (authority) this tick. |
| 65-67 | `NeedX/Y/Z` | N·m, local | The law's ask of gyros and thrusters (after the surfaces' share). |
| 68-70 | `DeliveredX/Y/Z` | N·m, local | What they gave. |
| 71 | `SurfacesCommanded` | | Control surfaces the allocator commanded last step. |
| 72 | `SurfacesSaturated` | | Of them, at their limit. |
| 73 | `AeroApplied` | 0/1 | Aero applied its forces this tick. |
| 74 | `EntryStrength` | 0..1 | Aero's own heating law at this Mach and density (an entry source's own strength: `TryGetEntry`). |
| 75 | `GyroSaturation` (10) | 0..1 | The gyros' momentum store, full share (`Gyros.Saturation`; they fade from 0.5). Server's; 0 with saturation off. |
| 76 | `GyroTorqueShare` (10) | 0..1 | The share of their vanilla torque the gyros give now: `Gyros.TorqueScale` x the saturation fade (1 with aero off). |

The thrust and attitude values (51-72) are the attitude job's, which runs after the aero tick: they are one tick older
than the air's.

```csharp
// once
var names = (string[])_getNames.Invoke(null, null);
int iMach = Array.IndexOf(names, "Mach"), iG = Array.IndexOf(names, "LoadFactor");
var state = new float[names.Length];
var getState = (Func<Entity, float[], int>)Delegate.CreateDelegate(typeof(Func<Entity, float[], int>), _getFlightState);
// each frame (no allocation)
if (getState(grid, state) > iG) Hud($"M {state[iMach]:F2}  {state[iG]:F1} g");
```

### Control surfaces and thrusters (per tick)

| Member | Since | What it gives |
|---|---|---|
| `int GetControlSurfaces(Entity grid, Vector3I[] cells, float[] commands, float[] deflectionsDeg, float[] liftN, float[] dragN)` | 7 | Every control surface on the last tick: block cell, command (-1..1: the pilot's and the allocator's together), deflection (deg: −command × its maximum), lift and drag (N). Returns the count. |
| `int GetThrusterForces(Entity grid, float[] forceN, out int thrusterList)` | 7 | Each thruster's force on the last tick (N, along its thrust direction) in `GetThrusters`' order, as the attitude law set it; `thrusterList`: the thruster list's generation - when it moves, read `GetThrusters` again. 0 when the attitude law is off (`Aero.ThrustTorque`). |

### The structure, block by block

| Member | Since | What it gives |
|---|---|---|
| `bool TryGetStructureCounters(Entity grid, out int layout, out int evaluation, out int blockCount, out bool externalHeat)` | 7 | The block order (`layout`), the evaluations published (only up), the block count, and whether the last evaluation ran on your temperatures (`SetBlockTemperatures`). `false`: no structure published yet. |
| `int GetStructureBlocks(Entity grid, Entity[] blocks, Vector3[] centresLocal, float[] massKg, out int layout)` | 7 | The blocks in layout order: entity, centre (grid-local m), mass (kg, as the structure counts it). |
| `int GetStructureBlockState(Entity grid, float[] utilisation, float[] health, float[] tempK, float[] skinMaxK, float[] heatInW, out int layout)` | 7 | Per block at the last evaluation: **utilisation** (its worst joint's load over its strength now; 1 yield; heat and damage in), **health** (0..1: the game's, less fatigue not yet written to the block), **temperature** (K, mass-weighted: what its strength reads), **hottest skin** (K: what burns it), **heat in** from the air (W over all its hull faces; negative: the air cools it). |
| `int GetStructureBlockLoads(Entity grid, Vector3[] forceLocal, Vector3[] momentLocal, out int layout)` | 7 | Per block: the external load the structure solved with (N, and N·m about its centre; grid-local) - the air (hull and wings), thrust, gyros, contacts, a railed entry's braking. The ship's own inertia is not in it. |

### The structure's failures (events)

A polled queue across every grid, with sequence numbers: keep the number you read up to.

| Member | Since | What it gives |
|---|---|---|
| `long GetStructureEventSequence()` | 7 | The next event's number (= every failure so far, kept or not; it only grows, across worlds too). Read it once to start from now. Recording runs while someone polls: from a call to this or `ReadStructureEvents` until 10 s after the last one; failures outside that are not kept but still take their numbers (a reader back after a pause sees the gap). A world's end drops the kept events (their grids and blocks let go); the numbers go on, so a cursor stays valid. |
| `int ReadStructureEvents(long fromSequence, long[] sequences, int[] kinds, Entity[] grids, Entity[] blocks, Vector3[] pointsLocal, Vector3[] directionsLocal, float[] values, out long nextSequence)` | 7 | The events since `fromSequence`, oldest first, up to the shortest array you pass; `nextSequence` is where to read next. The newest 512 are kept: a first `sequences[0]` above your `fromSequence` means you missed some. |

| Kind | What | Block | Point (grid-local) | Direction | Value |
|---|---|---|---|---|---|
| 1 | a block fails from its load | the block | the failing joint | into what goes | its utilisation |
| 2 | a block burns through (`Heat.Damage`) | the block | its middle | zero | its hottest skin (K) |
| 3 | a whole cut (section) fails together; its members follow as kind 1 | null | the cut's centre | out into what it takes off | its member count |

Events are what an evaluation **decided**; the game removes the blocks on the next simulation tick (one already gone is
skipped there) - a failed cut's members over its crack instead, about 0.3 s (`FractureMath.CrackSeconds`: the pulled side
first, the hinge last; the events come with the evaluation that decided it).

```csharp
long _next = (long)_getSeq.Invoke(null, null);   // start from now
var seq = new long[64]; var kind = new int[64]; var where = new Vector3[64];
// every second or so
var a = new object[] { _next, seq, kind, null, null, where, null, null, 0L };
int n = (int)_readEvents.Invoke(null, a); _next = (long)a[8];
for (int i = 0; i < n; i++) if (kind[i] == 3) Warn("structural failure");
```

### The hull's heat, face by face, and another heat model

Aero heats the hull (`Structure/Model/HullHeat.cs`): per face, the air brings `h (T0 − Twall)` W/m² - `h` the face's
film coefficient (the stagnation point's Sutton-Graves heating on the windward faces by their local nose radius, plus the
attached flow's Reynolds-analogy heating on every face), `T0` the flow's recovery temperature - and each face re-radiates
and conducts into its block. A **block thermal model of your own** takes the air's side from aero and owns the
temperatures:

| Member | Since | What it gives |
|---|---|---|
| `int GetHeatFaces(Entity grid, Vector3[] centresLocal, Vector3[] normalsLocal, float[] areas, int[] blockIndex, out int layout)` | 7 | The hull's heat faces (the faces seen from outside, on the structure's blocks): centre, outward normal (grid-local), area (m²), the block each is on (layout index; -1 none). They change only with the layout. Reading this (or the next) publishes the faces each evaluation for 5 s. |
| `int GetHeatFaceState(Entity grid, float[] heatInW, float[] skinK, float[] filmCoefficient, out float recoveryK, out int layout)` | 7 | Per face at the last evaluation: the heat in (W, at aero's own wall temperature), aero's skin temperature (K), the film coefficient `h` (W/m²·K); `recoveryK` the flow's `T0` (K). At your wall temperature `Tw` a face takes `h × area × (T0 − Tw)` W. |
| `bool SetBlockTemperatures(Entity grid, int layout, float[] tempK, float[] skinMaxK)` | 7 | **Your temperatures in place of aero's** for the next evaluation: every block's temperature (K, mass-weighted - its strength softens with it) and hottest skin (K - it burns past its burn temperature with `Heat.Damage`; the heat gauge). `skinMaxK` null: the same as `tempK`. Set again each evaluation; left 2 s, aero's own come back. `false`: not the current layout, short arrays, or a value not finite. |
| `bool ClearBlockTemperatures(Entity grid)` | 7 | Aero's own temperatures again at once. |

- **Aero's own model keeps running** under yours: it is the fallback, and it is what computes the heat input you read
  (`GetHeatFaceState`'s `h` and `T0` do not depend on its wall temperatures; `heatInW` does).
- **What your temperatures drive:** the blocks' strength (utilisation, wear, failure), heat damage (burn-through, kind 2
  events) and `TryGetStructure`'s `heatShare`. **Still aero's own:** the scorch marks and the max-skin gauge
  (`TryGetStructure`'s `maxSkinK`, flight state 47).
- `TryGetStructureCounters`' `externalHeat` tells you the evaluation used yours.

```csharp
// the thermal mod, each evaluation (TryGetStructureCounters' evaluation moved)
int n = GetHeatFaceState(grid, null, null, film, out float t0, out int layout);   // (your typed delegate)
for (int i = 0; i < n; i++) { int b = faceBlock[i]; if (b >= 0) blockIn[b] += film[i] * area[i] * (t0 - myWallK[b]); }
StepMyModel(blockIn);   // conduction, capacity, radiation - yours
SetBlockTemperatures(grid, layout, myTempK, mySkinK);
```

## Version 8

Every earlier member is unchanged. New: what a **settings screen** needs to build itself from the settings, and who may
change them. Build the screen from `GetSettings` (its order is the screen's order) plus `TryGetSettingInfo`,
`TryGetSettingUi` and `TryGetSetting`; change values with `SetSetting`; watch `GetSettingsVersion` to refresh.

| Member | Since | What it does |
|---|---|---|
| `bool TryGetSettingUi(string name, out string label, out string group, out string tier, out string unit, out double step, out string[] choices)` | 8 | The player-facing label, the group (section) it belongs to, the tier (`basic`: always shown; `advanced`: a collapsed section; `hidden`: the files and the API only), a number's unit (`""` none), a slider's step (0: pick one; an int's is 1), and for an int shown as a choice one label per value min..max (a new array; `null`: a number). |
| `bool IsSettingForced(string name)` | 8 | A world setting the host's file (`AerodynamicsMod.world.cfg`) forces: show it locked. `SetSetting` refuses it ("forced by the host file ..."). |
| `bool CanEditWorldSettings()` | 8 | World settings can be changed from this process (the host, single player). False: show them read-only; `SetSetting` refuses them. SE2 2.4: wherever the server runs (it shares the client's process). |
| `int ResetSettingsGroup(string scope, string group, out string why)` | 8 | Every setting of a group (scope `world`, `client`, or `null` for either) back to its default, through `SetSetting`'s rules. Returns how many changed; `why`: the first refusal, or no such group. |
| `bool AreWorldSettingsUnsaved()` | 8 | A world setting changed since the world was last saved: it is written into the save on the world's next save (nothing is written at once). |

The settings' groups, in screen order: world - Flight model, Structure and damage, Re-entry heat, Quick launch,
Performance (host); client - HUD, Feel and audio, Visual effects, Effects quality, Controls. The table under
[Settings](#settings) lists each setting's label, group and tier.

## Version 9

Every earlier member is unchanged. New (2026-10-18): **creative quick launch** moved here from the Orbital Mod (its 1.0.1
had it; it now only moves the ship for aero, `OrbitalApi.TryQuickMove`).

| Member | Since | What it does |
|---|---|---|
| `void RequestQuickLaunch()` | 9 | Asks for the local player's quick launch as its key does (Ctrl+T seated, creative only; settings `QuickLaunch.*`) - the key's own checks (the binding, not while typing) skipped, the server's all kept: the world's switch, creative (the server's game mode of its own character), the server's own seat, a dynamic ship not joined to a station, near a planet. The next client frame asks; the answer is the player's toast (and `QuickLaunch.Status`, `aeroget`). One ask a second. Client, any thread, no lock. The Orbital Mod's DevHarness key `quicklaunch` calls it. |

What happens to the ship: lifted straight up to `QuickLaunch.AltitudeM` over the ground under it (aero's 2 km ground ray;
no ground found: over where it is), never lower than it is, never closer to the ground than its own radius + 100 m;
turned level about itself (the cockpit's nose horizontal on its heading, its top up); every body of it and of what is
joined to it (wheels, rotor and hinge parts, docked or locked ships) at `QuickLaunch.SpeedMps` along that nose (0: its
top speed at 3/4 throttle, 150 m/s without one; at most 0.99 x the physics' speed cap). With the Orbital Mod loaded the
move is the Orbital Mod's (its safe move path; clamped under its planet's space border, refused when that would leave
the ship under its clearance; refused on rails); without it aero moves the ship itself in the teleport phase (before the
simulation, as the game's fast travel does), the cockpit's target attitude turned with it. A station, or a ship joined
to one, is never moved.

## Version 10

Every earlier member is unchanged. New (2026-10-18): the **gyro rebalance's** state, appended to the per-tick flight
state (`GetFlightState`; its length 75 -> 77): index 75 `GyroSaturation` (the gyros' momentum store, full share) and 76
`GyroTorqueShare` (the share of their vanilla torque they give now) - see the flight-state table and the settings
`Gyros.TorqueScale`, `Gyros.Saturation`, `Gyros.SaturationSeconds`. No new member.

## Version 11

Every earlier member is unchanged. New (2026-10-19): the **autopilot** (hover hold, height hold, envelope protection -
`Scripts/Simulation/Autopilot/`; players fly it from the **Autopilot block's** control panel) and the **flight performance**.

| Member | Since | What it does |
|---|---|---|
| `bool TryGetAutopilot(Entity grid, out int goals, out int protections, out int reason, out string readout)` | 11 | The grid's autopilot: the goals it holds (bits: 1 attitude, 2 hover, 4 position, 8 height, 16 an external goal source), the protections in force this tick (bits: 1 lean limit, 2 thrust limit, 4 stall limit, 8 g limit, 16 control limit - the predicted loss of control, 32 overspeed, 64 terrain floor, 128 full thrust - low energy, 256 terrain ahead, 512 collision avoidance), why it last let go or refused (`reason`, below; 0 none) and its readout (`"AP HOVER POS AGL · FLOOR"`, `""` off). False: never engaged on this grid, or no server grid found for it. Any thread, no lock. |
| `bool RequestAutopilot(Entity grid, int goals)` | 11 | Asks the grid's autopilot for exactly these goals (the bits above; 0: all off), as the Autopilot block's buttons do (Hover Hold is 2 \| 8, Height Hold is 8). The world's `Autopilot.*` settings and the autopilot's own checks apply; a refusal sets `reason`. An Autopilot block on the grid keeps its own panel: a mode chosen there, or the block going dark, replaces the ask. Queued for the grid's next simulation step. False: no server grid found (a client grid not paired yet - ask again a tick later). |
| `bool TryGetFlightPerformance(Entity grid, out float nInstant, out float nSustained, out float turnRateInstant, out float turnRateSustained, out float dragDecel, out float excessAccel, out float alphaMaxDeg)` | 11 | What a flown grid can do now, from the mod's own whole-ship lift and drag over the angle of attack (the stall evaluation's sweep - the force table's faces and the wings, Mach and density in - a few times a second) and its thrust: the most load the lift gives within the stall margin and the g limit (the flying Autopilot block's; g of the real weight) and the most its full thrust sustains, the level turn rates at those (rad/s), the drag's deceleration now (m/s2), full thrust less the drag along the path (m/s2), the most nose-up the flow may turn before the stall margin (deg). False: not evaluated (under 20 m/s, thin air, or a grid nobody flies). |

`reason`: 1 turned off, 2 the pilot steered, 3 the dampeners were switched off, 4 not allowed in this world, 5 no gravity,
6 not enough upward thrust to hover, 7 too fast to hover, 8 lost control of the attitude (upset), 9 the pilot left the seat
(`Autopilot.Unattended` off), 10 can't hold the height (thrust short), 11 nothing to steer with, 12 the dampeners must be
on, 13 no goal source, 14 goal reached, 15 the Autopilot block was switched off, lost its power, was damaged or removed.

**What it does.** Only what a seated pilot writes: a target attitude (handed to the aero-aware attitude law as a pilot's
reticle is - the real gyros, thrusters, rotors' cyclic and control surfaces fly it), the movement keys
(`ControlData.Movement`, written in place on the server grid) and nothing else; no force, torque or velocity is ever
applied by it (`Tests/AutopilotSim` checks that every tick). Hover hold leans the lift axis toward the speed it wants (the
steady lean a single tail rotor's sideways thrust needs found by its integral), height hold flies the up key, the
protection cuts every attitude command to the stall margin, the g limit and what the control authority holds (the
loss-of-control prediction made for the command before it is given), and keeps the ship off the terrain (a floor under it
and a look ahead along its path - the physics' own rays: the game builds voxel colliders only near entities, so a long ray
that finds nothing means "not known"). The limits are the flying Autopilot block's panel (the world's `Autopilot.*` caps in);
an API ask on a grid without one flies the panel defaults.

**The Autopilot block** (`Autopilot/Game/AeroAutopilotComponent.cs`, its panel `AeroAutopilotBlockDetailModel.cs`): its own grid
is the one it flies. Several on one grid: the first that is built, powered and switched on, by block position (x, then y,
then z), flies; the others show "Standby". Its panel: Hover Hold / Height Hold / Autopilot Off (also toolbar actions, with
Toggle Hover Hold / Toggle Height Hold), a status line, Height (engaging catches the ship's own and shows it; a new number
flies there), Height Over Ground (off: a level altitude over the ground where it was engaged - the planet's sea level is not
readable by mods: `VoxelPlanetComponent` is in VRage.Voxels), Hover Speed, Envelope Protection, G Limit, Stall Margin, Terrain
Floor, Terrain Look-Ahead, Keep Mode Unattended. A let-go is written back to it (steering: Height hold; anything else: Off) with
the reason in the status line. Saved with the block (`aero-autopilot:1 ...`): a ship left in hover hold engages it again after
a load.

**Inside the mod (engine-free, for later modes - waypoints, fixed-wing modes, formation, AI pilots):** a goal source
implements `IApGoalSource.Step(in ApSense, ref ApExternal)` - a `Track` (a place and its velocity: a hover slot, a moving
pad), an `Accel` (world m/s2 without gravity: flown with the lift when the ship has it - `FlightDirector.LiftFlight`: the
lift rolled onto it, the angle of attack for it from the ship's own lift curve, the thrust for its part along the path - else
with the main thrust pointed along it) or an `Attitude` with a throttle - and is engaged with
`FlightDirector.Engage(goals | ApGoal.External, in sense, out why, source)`; returning false lets go ("goal reached"). It
reads the flight performance (`FlightDirector.Perf`, `FlightPerf.Evaluate(sense.Perf, in sense)`, `FlightPerf.SpeedAfter`,
`ThetaFor`, `LiftAt`, `DragAt`) and the terrain (`ITerrainQuery.Below` / `Cast`, `TerrainWatch`: height over the ground,
the ground's normal and rise, the time to impact along the path). Protection applies to every source. Offline,
`Tests/AutopilotSim` flies them: a pack of scenarios is a class implementing `IScenarioPack` anywhere in that project
(`Sim.Check`, `Run`, `Ships`, `SimTerrain`, `SimTarget`).

## Version 12

Every earlier member is unchanged. New (2026-10-19): the **flight pilot** - the Autopilot block's Flight Hold, Navigate and
Engage modes (`Scripts/Simulation/Autopilot/ApPilot.cs`). Its target, its aim and its state. It never fires a weapon: read the
aim and fire from your own mod.

| Member | Since | What it does |
|---|---|---|
| `bool SetAutopilotTarget(Entity grid, Entity target)` | 12 | The target the grid's Autopilot block engages when its Target Source is **API**: an entity (a grid or a block; its top grid's velocity is used). False: no server grid found. |
| `bool SetAutopilotTargetPoint(Entity grid, Vector3D position, Vector3 velocity)` | 12 | ... or a point (world) moving at a velocity (world m/s). |
| `bool ClearAutopilotTarget(Entity grid)` | 12 | No API target: the block waits (flies its route, or loiters where it was engaged). |
| `bool TryGetAutopilotAim(Entity grid, out float errorDeg, out bool inCone, out bool inRange, out float range, out float timeOfFlight)` | 12 | The angle between the fixed guns' axis (or the nose) and the target's lead point (deg), inside the block's aim cone (or the miss inside the target's size), the target inside the guns' range, the range (m) and the shot's time of flight (s). False: nothing aimed at now. |
| `bool TryGetAutopilotPilot(Entity grid, out int mode, out int style, out int behaviour, out bool hasTarget, out int waypoint)` | 12 | The mode flying (3 flight hold, 4 navigate, 5 engage), the style (1 airplane, 2 helicopter, 3 six-axis), the behaviour active (1 attack run, 2 attack run U-turn, 3 broadside, 4 circle, 5 hover above, 6 hover below, 7 point at, 8 standoff, 9-11 pursuit lead / pure / lag, 12 extend), a target held, the next waypoint (-1 loitering). False: no pilot mode flying. |
| `bool TryGetAutopilotEnemy(Entity grid, out bool enemy, out int targetFilter)` | 12 | The grid's Autopilot block: the grid is an **enemy** now (the game's hostile NPC owns it - by the block's Enemy switch or otherwise: its turrets and Engage fight players) and the block's **Target Filter** (0 hostile only, 1 hostile + neutral, 2 everything). The switch is the panel's (the world's `Autopilot.EnemyRule`, the owner checks); to change ownership yourself call the game's `OwnershipSessionComponent.TryTransferToNPC`. False: no Autopilot block. |

**The target filter** applies to every source, with the game's own relation (`TargetingGridComponent.IsEligibleTarget`,
the turrets' rule: `IRelations.GetRelationFromOwners` - hostile when exactly one owner is the hostile NPC; no owner: only a
`PriorityTargetComponent`): hostile; neutral is not hostile and not the same owner or faction (`IFactions.InSameFaction`).
The Targeting source's lists are hostile already; with them empty (no turret) or a wider filter, the block scans every 2 s for
the nearest grid (5+ blocks) or player character within 4 km (never its own owner's). A named grid or an API entity the
filter refuses is no target ("no target - X is neutral"); a GPS or API point is always taken.

New `reason` values (`TryGetAutopilot`): 16 too slow to fly on the wings, 17 no air, 18 no waypoints, 19 not enough thrust
across for six-axis, 20 the style set does not fit the ship.

**The lead.** SE2 4.0's projectiles fly straight at the muzzle speed plus the shooter's velocity (no drop:
`SimulatedProjectileDefinition.IsAffectedByBulletDrop` is never read). The intercept time is iterated against the target's
velocity and its estimated acceleration. The muzzle speed and range come from the grid's fixed guns (weapon blocks without a
turret), or the block's Muzzle Speed.

## Roadmap / gaps

What the API does **not** expose yet, and why. Each would be a new member (never a change to an existing one).

| Gap | Why not yet |
|---|---|
| Remote multiplayer clients | Nothing is replicated to clients (SE2 2.4's networking is a mock in one process). Every call answers on the simulation's machine only. |
| Commands to aero blocks (deflect a control surface, deploy an airbrake) | The blocks' control path is still owned and changing (gear, airbrakes, control surfaces). Another mod can already add drag (`SetDrag`, `SetPlateDrag`). |
| Gear and airbrake state beyond `GetAeroBlockState` | Owned by the gear / airbrake work in progress; to be published when it settles. |
| Dents and permanent bends | The game's deformation bones hold them; health (`GetStructureBlockState`) already has the unwritten fatigue in. |
| Heat for grids nobody flies | The structure and hull heat are evaluated only for a flown grid (cost). A thermal mod that wants every grid needs aero to heat every grid - a setting, not an API. |
| Stopping aero's own heat model under an external one | Aero's bucket still runs (cheap, and it computes the heat input). A switch to skip its temperature step would be a setting. |
| The scorch marks and max-skin gauge from external temperatures | They read aero's own faces; they would need per-face temperatures from the thermal mod. |
| Callbacks (events pushed to you) | Deliberately polled: a callback runs on aero's thread inside its tick, and a slow or throwing consumer would stall or break the simulation. |
| Performance counters (aero's cost per frame) | Developer-only today (`AeroCost`, the harness's `aerocost` / `aeroget`); not a stable contract. |
| Per-face pressure for a flying grid | `GetSurface`'s `cp` is 0 for a grid on its force table (most grids); `GetDesignPressures` predicts it for any state. |

## Settings

Two scopes. Every change takes effect at once: the values are fields the simulation reads each tick or frame. `Model.LargeGridCellScale`, `Structure.MountJoints` and `Structure.TouchingShare` rebuild every grid (a few seconds of rebuilding on big ships; the joint settings rebuild the structures too).

- **World** (the same for every player): saved **with the world**, in a component on the server planets (`AeroWorldSettingsComponent`, the engine's `EntityNameSessionComponentObjectBuilder`, key `aero-settings:`). Only values off their default are saved. A host or dedicated server can force values in `%APPDATA%\SpaceEngineers2\ModSettings\AerodynamicsMod.world.cfg`: uncommented lines win over the save. The mod writes that file, all commented out, the first time.
- **Client** (this PC): `%APPDATA%\SpaceEngineers2\ModSettings\AerodynamicsMod.client.cfg`, written with every setting and its description, and rewritten after a change through the API.

Both files are read again within ~2 s of an edit, so they can be changed while playing. The format is `name = value`, `#` for comments, `true` / `false` for switches.

**In game:** pause menu → Settings → **Mods** (inside a world: mods do not run in the main menu). One tab for every mod - this mod's section ("Aerodynamics") beside the Orbital mod's, generated from the registry: Basic settings shown, each group's Advanced ones in a collapsed "Advanced", hidden ones never; a switch, a choice or a slider (with its value and unit) per setting, its description in the screen's description panel, "Reset to defaults" per group. World settings are editable only where `CanEditWorldSettings` holds (read-only otherwise, with the reason), and a host-file-forced one is locked with the file's path. The tab is `Scripts/Simulation/Ui/SettingsTab*.cs` (a copy of the Orbital mod's, synced by its `tools/sync_settings_ui.sh`; `SettingsTab.Mod.cs` is this mod's own). **Another mod can add its own section** the same way, with no code shared: in the game's `OptionsScreen.PART_TabControl.Items`, find the `TabItem` whose `Tag` is `"ModSettings/v1"` (or add it: `Header` "Mods", `Content` a `ScrollViewer` whose `Content` is a `StackPanel` tagged `"ModSettings/v1/host"`), and add an `Expander` tagged `"ModSettings/v1/<YourModId>"` to that panel's `Children` if it is not there. Where it cannot be added, a toast once per session points to the files.

A name the host file sets is **forced**: `SetSetting` refuses it ("forced by the host file ...") until the file stops setting it (`IsSettingForced`). World settings change only where `CanEditWorldSettings` holds (the host or single player); a change is written into the save on the world's next save (`AreWorldSettingsUnsaved`). Tiers: `basic` settings are for every player, `advanced` ones for tinkerers, `hidden` ones (legacy or solver feel) only in the files and the API. Settings marked *ClientVisible* in the code (the entry Mach pair, the heat lag, the scorch switch, the structure's loads and wear-through load) are world values a client draws from too: they will need replicating once SE2 has real multiplayer.

| Setting | Scope | Type | Default | Range | On the settings screen | What it does |
|---|---|---|---|---|---|---|
| `Aero.Enabled` | world | bool | true |  | Aerodynamics · Flight model · basic | The whole aero simulation. On again: every grid rebuilds. |
| `Aero.DragMultiplier` | world | float | 1 | 0..10 | Drag strength · Flight model · basic | The force along the motion, times this. |
| `Aero.LiftMultiplier` | world | float | 1 | 0..10 | Lift strength · Flight model · basic | The force across the motion (wings, control surfaces, the hull's own lift), times this. Torque is scaled by both, weighted by the drag and lift magnitudes. |
| `Aero.ThrustTorque` | world | bool | true |  | Off-centre thrust turns ships · Flight model · basic | Thrusters off the centre of mass turn the ship (and steer to the pilot's target). Off: the game's own thrust, untouched. |
| `Gyros.TorqueScale` | world | float | 0.25 | 0.05..1 | Gyro strength · Flight model · basic | Every gyroscope's torque, times this (1: vanilla): each gyro block's own maximum (`GyroscopeData.MaxTorque`) is its definition's x this (x the saturation fade), so the game sums it into the grid's `MaxTorqueData` and every gyro law (the game's, the aero-aware one, the loss-of-control prediction, the design probe) spends the scaled torque. Default measured on the dumped ships (`Tests/AeroBench gyros`): at 0.25 no ship's gyros hold one main engine out or its own stall moment, and they still hold a 5° upset at 150 m/s on most. One scale everywhere, space too (thrusters still steer). Server; aero off: vanilla. `Propulsion/GyroBalance.cs`, `Grid/AeroGridComponent.Gyros.cs`. |
| `Weather.Wind` | world | bool | true |  | Wind · Flight model · basic | The game's wind in the air the ship flies through (headwinds, crosswinds, gusts), in place of the game's own box-shaped wind push. |
| `Control.HingeSurfaces` | world | int | 0 | 0..1 | Hinge control surfaces · Flight model · basic | Vanilla hinges carrying flaps as control surfaces, driven from the pilot's input. 0 none, 1 every hinge on a piloted grid. |
| `Control.HingeMaxDeflection` | world | float | 20 | 1..60 | Hinge flap travel · Flight model · advanced | Hinge control surfaces: degrees at full command. |
| `Model.LargeGridCellScale` | world | int | 2 | 1..4 | Hull detail (large grid) · Flight model · advanced | Large-grid surface resolution (1 = 0.25 m cells). **Rebuilds every grid.** |
| `Aero.GroundEffect` | world | bool | true |  | Ground effect · Flight model · advanced | Ground effect: wings close to the ground (within about a span) get more lift and less drag. |
| `Aero.UpperAtmosphere` | world | bool | true |  | Thin upper air · Flight model · advanced | The air thins out exponentially past the planet's visible atmosphere edge, so a fast ship meets faint drag there instead of a wall. Off: the game's linear fade, the air ending at the edge. |
| `Control.ThrustTurnRate` | world | float | 0.8 | 0.1..3 | Thruster steering max turn rate · Flight model · advanced | The fastest turn (rad/s) thruster steering asks for when turning the ship to the pilot's target. |
| `Physics.HonestDampeners` | world | bool | true |  | Honest dampeners · Flight model · advanced | The dampeners' gravity hold as real thrust against the gravity the ship really feels (GravitySum x the world's GravityMultiplier, vanilla 2), within the thrusters' maxima, and no super-dampener braking boost (the grids' `MovementData.SuperDampeners` cleared). A ship needs thrust for its full weight x the multiplier to hover; what holds it is not there to climb with. Off: the game's own (one weight of thrust cancels all the multiplied gravity; braking x2..5 with the world's super dampeners). `Propulsion/HonestHold.cs`, `HonestDampeners.cs`. |
| `Gyros.Saturation` | world | bool | true |  | Gyro saturation · Flight model · advanced | The gyros store the angular momentum they give (per grid, grid-local, signed: a turn and its stop cancel). Past half the store (`Gyros.SaturationSeconds` x their full torque) their torque fades smoothly to a quarter; it bleeds back (at half the full torque) while the torque given is under a quarter of full - a small trim never fills it. A new grid starts empty; a grid that loses gyros keeps their share only. Off: gyros never tire. The fill and the share given: flight state `GyroSaturation`, `GyroTorqueShare`. |
| `Gyros.SaturationSeconds` | world | float | 15 | 2..120 | Gyro momentum capacity · Flight model · advanced | Gyro saturation: the store, in seconds of the gyros' full torque (fading from half: ~7.5 s of a steady full-torque hold; empty again ~30 s after). |
| `Structure.Loads` | world | bool | true |  | Structural loads · Structure and damage · basic | The loads in every ship's structure and its hull's heating, computed in the background (`TryGetStructure`). |
| `Structure.Damage` | world | bool | true |  | Structural damage · Structure and damage · basic | Overloaded joints of the ship a player flies wear, and break off what hangs from them (AI ships and wrecks never). The flight controller limits its own steering so it never overloads the ship. |
| `Structure.Strength` | world | float | 7 | 0.1..100 | Structural strength · Structure and damage · basic | Every block's strength, times this (7: a small-block airliner yields near 3 g, a light large-block plane near 8 g; thin wall panels buckle in compression). |
| `Structure.Fatigue` | world | bool | true |  | Metal fatigue · Structure and damage · basic | FATIGUE below the yield: from `Structure.EnduranceUtil` of the yield up, every joint loses health slowly, as the cube of how far it is between the endurance limit and the yield - from its tension at the full rate, and from compression at `Structure.BucklingFatigueShare` (1) where the member buckles (thin walls) or `Structure.CompressionFatigueShare` (0.2) where it is stocky. Health lost is strength lost (a tired joint fails at a lower load). Kept in the blocks' own health - saved with the world, replicated, repaired by welding; the mod writes it in steps (5% of a block's health, at most once a second a block, at once when it crosses a damage stage or fails) so the game's damage sound and network traffic stay proportionate. |
| `Structure.Tear` | world | bool | true |  | Slow tearing · Structure and damage · basic | A large piece the structure breaks off tears loose over a moment: the game splits it off, and for `Structure.TearSeconds` it stays tied over the failed section by up to 6 soft springs (physics constraints), each eased to nothing and let go in the crack's order - the torn side first, the hinge last - so it peels and swings on its last ligament. Server only; clients see the motion. Small bits still pop off at once. |
| `Structure.Bends` | world | bool | true |  | Permanent bends · Structure and damage · basic | PERMANENT BENDS: a joint past its yield that holds (it wears, not breaks) leaves the structure outboard of it - the slice's lighter side, connected to it - turned a little about the joint, the way its load turned it (up after a hard pull on a light wing, down for a heavy pod, twisted under torsion): the armour sags or twists as one piece, nothing at the joint, more toward the tip. It bends again only past its last peak. The game's deformation bones move armour only (a part past the joint that is more than a tenth other blocks is not bent). With `Structure.Damage`; server, replicated by the game. |
| `Structure.Dents` | world | bool | true |  | Dents · Structure and damage · basic | Overloaded armour dents as its joints wear (the game's deformation, more the further over). With Structure.Damage. |
| `Structure.Crack` | world | bool | true |  | Cracks run across the break · Structure and damage · basic | THE CRACK RUNS ACROSS THE SECTION: a failing cut's blocks let go one after another over `Structure.CrackSeconds` - the most pulled side first, the pushed side (the hinge) crumpled and last. The cut is committed: no zipper along the span while it runs. Off: every block across the break in the same tick. |
| `Structure.Jagged` | world | bool | true |  | Jagged break lines · Structure and damage · basic | The fracture line steps a block either side of the cut's plane (the least loaded blocks most; only into a block of the same material class and at most 1.5x its mass - never a cockpit), each step checked to part the piece where the flat cut did. Off: ruler-straight breaks. |
| `Structure.SplitBlocks` | world | bool | true |  | Split broken blocks · Structure and damage · advanced | A joint that fails breaks only its block's fracture pieces on the failing side (the game's own fracturing): the block stays, broken, on one side, and the ship parts along the failed face. A block of a single critical piece is destroyed as before. |
| `Structure.BySection` | world | bool | true |  | Fail whole sections · Structure and damage · advanced | A ship's weak neck wears and fails across its whole section at once - every block across the overloaded cut together, and what hangs from it leaves in one go. Off: block by block, the most loaded first (a fracture creeps along the neck). |
| `Structure.MountJoints` | world | bool | false |  | Joints by mount points · Structure and damage · advanced | Blocks join at full strength only where their mount points mate (the game's own connection test); faces that only touch - armour pressed against a tank's or a thruster's side - carry `Structure.TouchingShare` of that. Off: every touching face at full strength. **Rebuilds every grid.** |
| `Structure.TouchingShare` | world | float | 0.75 | 0..1 | Touching face strength · Structure and damage · advanced | With `Structure.MountJoints`: a touching face's strength and stiffness where no mount point mates, as a share of a mated one's (0: carries nothing - strict mounts; 1: as strong as mated). **Rebuilds every grid.** |
| `Structure.CrackSeconds` | world | float | 0.3 | 0.05..3 | Crack run time · Structure and damage · advanced | Seconds from a cut's first block letting go to its last (the hinge). With `Structure.Crack`. |
| `Structure.JaggedShare` | world | float | 0.4 | 0..1 | Break line roughness · Structure and damage · advanced | The chance a cut's least loaded block steps off the plane (the most loaded: this x 0.25). With `Structure.Jagged`. |
| `Structure.CrushUtil` | world | float | 2 | 1.05..10 | Crush load · Structure and damage · advanced | A block on the pushed side of a failing cut is CRUSHED - goes whole - at this compressive utilisation (against its strength now); below it, it crumples as the hinge and stays as a stub. |
| `Structure.CrumpleDepth` | world | float | 0.15 | 0..0.25 | Hinge crumple depth · Structure and damage · advanced | How far (m) a crumpling hinge's armour bones are pushed back toward the cut and bulged out (0: no crumple; dents and crumples together are capped at 0.25 m a bone). |
| `Structure.CrackSmokes` | world | int | 3 | 1..10 | Smoke puffs per crack · Structure and damage · advanced | Torn-edge smoke puffs along a running crack: at its first release, then evenly to the hinge. World: the server picks them; `Fx.TornSmoke` hides the smoke on a PC. |
| `Structure.ThrustLoads` | world | bool | true |  | Engine mount loads · Structure and damage · advanced | Engine thrust loads the structure (mounts, and the push through the ship). Off: engines never strain or break a ship. |
| `Structure.EntryLoads` | world | bool | true |  | Re-entry braking loads · Structure and damage · advanced | A re-entry the Orbital Mod flies on rails loads the structure with its braking (`OrbitalApi.TryGetEntryState2`; up to ~5 g): the ship's mass × the deceleration, pressed onto the windward faces by the same Newtonian weights as the air's own load (cos² × area along the travel), with no net moment; the free-body solve carries it through the whole ship (the nose and heat shield take it). Not a contact. Off: a railed entry heats the hull but never strains it. |
| `Structure.GyroLoads` | world | bool | true |  | Gyro loads · Structure and damage · advanced | Gyroscopes' torque loads the structure (their mounts, and the turn through the ship). Off: gyros never strain a ship. |
| `Structure.ComponentLoads` | world | bool | true |  | Component loads at their blocks · Structure and damage · advanced | Each block component's air loads its own block: a control surface's lift its hinge, an open airbrake's drag its mount, a gear leg's, an intake's or scoop's drag, another mod's drag patch at its point - taken off the rest of the air, which is spread over the windward faces. The ship's total load is unchanged; only where it acts. Off: the components' air spread over the windward faces with the hull's (before 2026-10-17). |
| `Structure.Contacts` | world | bool | true |  | Impact and landing loads · Structure and damage · advanced | Collisions and touchdowns load the structure (the contact's push, from the ship's change of momentum). Off: only flight loads it. |
| `Structure.GearSturdiness` | world | float | 1 | 0..5 | Landing gear sturdiness · Structure and damage · advanced | How much of a touchdown's load landing gear takes, times this (1: as any block; higher: gear is sturdier than its block). |
| `Control.RespectStructure` | world | bool | true |  | Protective autopilot · Structure and damage · advanced | The flight controller limits its own steering to what the structure carries: it holds back before it overstresses the ship. |
| `Structure.BreakAt` | world | float | 1.5 | 1.05..10 | Wear-through load · Structure and damage · advanced | PROGRESSIVE FAILURE: past its yield a joint wears through over a moment, in proportion to how far over; at this many times its yield load it loses `Structure.WearRate` of its health a second. It lets go when its health is exhausted (its block definition's destruction threshold, 0.3 of full for every vanilla block, never under 0.3) (or at once past `Structure.SnapUtil`). Health lost is strength lost, so a worn joint runs away: it bends and dents, the creaks rise, then it goes. |
| `Structure.WearRate` | world | float | 0.45 | 0..100 | Wear rate · Structure and damage · advanced | Health (fraction of full) a joint loses per second at `Structure.BreakAt`. 0.45 puts the first break of a fast pull where the old instant break was (a small-block airliner ~3.8 g, a light large-block plane ~13 g); a sustained pull fails earlier, a brief spike (0.5 s at 1.5 x the yield) costs about a quarter of a block's health instead of snapping it. |
| `Structure.SnapUtil` | world | float | 2.75 | 1.05..100 | Instant snap load · Structure and damage · advanced | A joint snaps at once at this many times its INTACT yield load (a genuine overload); below it, it wears through. |
| `Structure.FatigueRate` | world | float | 0.005 | 0..1 | Fatigue speed · Structure and damage · advanced | Health a joint loses per second from fatigue while held at its yield (a tenth in 20 s; at 90% of the yield ~40 s, at 70% ~5 min, at 60% ~40 min). Hard flying at 80% of the limit (3 s pulls every 10 s) wears a small-block airliner's worst joint to 85% in ~7 min and to its yield in ~13 min. |
| `Structure.EnduranceUtil` | world | float | 0.5 | 0..1 | Fatigue-free load · Structure and damage · advanced | The endurance limit: a joint loaded under this share of its yield never tires (cruising never fatigues a ship). |
| `Structure.CompressionFatigueShare` | world | float | 0.2 | 0..1 | Compression fatigue (solid members) · Structure and damage · advanced | TENSION VS COMPRESSION FATIGUE: a fatigue crack grows only while pulled open, so a member tires at the full rate for its tensile part (pull and shear) and, if STOCKY (yield-limited), at this share for what its compression adds (0.2: pure compression lasts ~5x as long). The yield wear past 1 is not scaled. |
| `Structure.BucklingFatigueShare` | world | float | 1 | 0..1 | Compression fatigue (buckling walls) · Structure and damage · advanced | BUCKLING FATIGUE: a buckling-limited member's compression share (thin light-armour walls - most governing members): a plate that buckles and straightens every pull cracks at its edges, so it tires at the full rate (1, the user's decision 2026-10-15). |
| `Structure.TearSeconds` | world | float | 1.2 | 0.1..10 | Tear duration · Structure and damage · advanced | Seconds a large broken-off piece takes to tear loose. |
| `Structure.TearMinMass` | world | float | 2000 | 0..1e7 | Slow-tear minimum mass · Structure and damage · advanced | Pieces at least this heavy (kg) tear loose slowly; lighter ones pop off at once. |
| `Structure.TearAnchors` | world | int | 6 | 3..8 | Tear anchor points · Structure and damage · advanced | The most anchors (springs, each its own motor) a tearing piece hangs from, picked over the failed section: the crack's origin first, then the farthest. |
| `Structure.TearPeelFirst` | world | float | 0.35 | 0.05..1 | Tear peel start · Structure and damage · advanced | The tear peels in the crack's order: the origin's anchor lets go at this share of `Structure.TearSeconds`, the hinge's at the end (1: all together). |
| `Structure.TearHold` | world | float | 6 | 0..50 | Tear hold · Structure and damage · hidden | The hold at the start of a tear, in g of the piece's own weight (all its anchors together). |
| `Structure.BendPerUtil` | world | float | 0.05 | 0..1 | Bend amount · Structure and damage · advanced | The permanent turn (rad) at a joint per unit of utilisation over its last peak (1 at first): a 10 m wing's tip sets ~10 cm at 1.2 x its yield, ~25 cm at 1.5 (just short of breaking at the default `Structure.BreakAt`). |
| `Structure.BendMax` | world | float | 0.3 | 0..1 | Maximum bend · Structure and damage · advanced | The most any point (deformation bone) is bent in total, m. The game also clamps a bone by its armour: within 0.25 m of a 0.25 m block. |
| `Structure.BendMaxBlocks` | world | int | 300 | 1..5000 | Largest bendable part · Structure and damage · advanced | The most blocks one bend moves; a bigger part past the joint is the ship's body, not a tip: not bent. |
| `Structure.HingeToCompression` | world | float | 1 | 0..1 | Bends fold at the pushed side · Structure and damage · advanced | Where a permanent bend pivots, as a share of the way from the cut's centre (0) to its most pushed member's pushed edge (1: a plastic hinge on the crushed face, the pulled face opening). |
| `Heat.Multiplier` | world | float | 1 | 0..10 | Re-entry heating · Re-entry heat · basic | The air's heating of the hull, times this. |
| `Heat.Damage` | world | bool | true |  | Heat damage · Re-entry heat · basic | Hull blocks past their burn temperature lose health (heat weakens the structure either way). |
| `Heat.Scorch` | world | bool | true |  | Scorch marks · Re-entry heat · basic | Heat leaves a few large burn marks on the hull where it got hot in a re-entry. Worked out on the server; Fx.ScorchVisible hides them on this PC. |
| `Entry.OnsetMach` | world | float | 2 | 0.5..25 | Heating starts at Mach · Re-entry heat · advanced | Entry heating starts here. Must stay under `Entry.FullMach`; changing it keeps the full point where it is. |
| `Entry.FullMach` | world | float | 2.506 | 0.6..30 | Full heating at Mach · Re-entry heat · advanced | Entry heating is full here. |
| `Entry.HeatLag` | world | float | 2 | 0.05..30 | Heat-up / cool-down time · Re-entry heat · advanced | Seconds the hull takes to heat up and cool down. |
| `Heat.ScorchSpots` | world | int | 4 | 0..16 | Scorch marks per ship · Re-entry heat · advanced | The most scorch marks one ship gets (the hot faces are gathered into this many spots). |
| `QuickLaunch.Creative` | world | bool | true |  | Quick launch in creative · Quick launch · basic | *(1.0.1)* In creative, a seated pilot's quick launch key (Controls) lifts the ship over the ground, turns it level and sends it flying along its nose. Survival never has it. Off: not in this world either. |
| `QuickLaunch.AltitudeM` | world | float | 1000 | 100..5000 | Launch altitude · Quick launch · basic | *(1.0.1)* How high over the ground (m; no ground found under the ship: over where it is). Never lower than the ship is, never into the ground; with the Orbital Mod, always under the planet's space border. |
| `QuickLaunch.SpeedMps` | world | float | 0 | 0..1000 | Launch speed (0: cruise) · Quick launch · advanced | *(1.0.1)* The speed along the nose (m/s). 0: the ship's cruise speed (its top speed at 3/4 throttle; 150 m/s when it has none). At most 0.99 x the world's speed cap. |
| `Autopilot.Enabled` | world | bool | true |  | Autopilot · Autopilot · basic | *(Unreleased)* Autopilot blocks may fly ships in this world (hover hold, height hold). Off: every engaged one lets go. |
| `Autopilot.Unattended` | world | bool | true |  | Allow unattended autopilot · Autopilot · basic | *(Unreleased)* A ship with nobody seated may keep its autopilot mode (a helicopter left in hover hold holds its hover); each block's "Keep Mode Unattended" chooses within it. Off: it lets go when the pilot leaves. |
| `Autopilot.MaxLeanDeg` | world | float | 20 | 2..45 | Hover: most lean · Autopilot · advanced | *(Unreleased)* The most a hover leans to move or hold its place (deg). |
| `Autopilot.ClimbRate` | world | float | 4 | 0.5..20 | Hover: climb rate · Autopilot · advanced | *(Unreleased)* The climb / descent rate the up / down keys ask for in hover or height hold, and the most it climbs or sinks to a new height (m/s). |
| `Autopilot.EnemyRule` | world | int | 1 | 0..2 | Enemy ships from autopilot blocks · Autopilot · basic | *(Unreleased)* Who may switch an Autopilot block's Enemy toggle on (0 off, 1 creative or admin, 2 everyone): its ship becomes the game's hostile NPC's - its turrets and its Engage fight players. Only the ship's owner can; only creative or an admin can switch it back or change an enemy ship's panel. |
| `Perf.FrameBudgetMs` | world | float | 1 | 0.1..16 | Aero time budget per frame · Performance (host) · advanced | The most aero takes of a simulation frame; past it, grids not piloted wait a few frames. |
| `Perf.Workers` | world | int | 2 | 1..16 | Background threads · Performance (host) · advanced | Background cores for hull and force-table builds. |
| `Perf.TableCache` | world | bool | true |  | Reuse force tables · Performance (host) · advanced | Reuse built force tables. |
| `Perf.TableCacheMB` | world | int | 256 | 16..4096 | Force-table cache size · Performance (host) · advanced | The table cache's size. |
| `Perf.LocalUpdates` | world | bool | true |  | Partial hull updates · Performance (host) · advanced | Update only the changed part of a hull. |
| `Structure.Rate` | world | float | 4 | 1..20 | Structure updates per second · Performance (host) · advanced | Structure evaluations per second of a player's ship (higher: quicker to react, more background work). |
| `Perf.ChunksPerUpdate` | world | int | 8 | 1..64 | Hull chunks per update · Performance (host) · hidden | Hull chunks updated per pass. |
| `Perf.MinSpeedForUpdates` | world | float | 10 | 0..1000 | Hull update minimum speed · Performance (host) · hidden | Below this speed (m/s) an edited hull waits before updating its forces. |
| `Perf.QuietBeforeFullRebuild` | world | float | 5 | 0.5..120 | Quiet time before a full rebuild · Performance (host) · hidden | Seconds without block changes before a cleanup rebuild. |
| `Hud.StallWarning` | client | bool | true |  | Stall warning · HUD · basic | The stall warning from the whole ship's air force: STALL when pulling further only loses lift, amber close to it. |
| `Hud.Loac` | client | bool | true |  | Loss-of-control warning · HUD · basic | The loss-of-control warning, predicted: CONTROL LIMIT / LOSS OF CONTROL when holding the commanded attitude needs nearly / more than the torque available at this speed. |
| `Hud.Mach` | client | bool | false |  | Mach in speed readout · HUD · basic | Mach in the game's speed readout ("312 m/s · M 0.92"), for what you are flying. Off by default: the flight boxes by ALT show it. |
| `Hud.GameStallWarning` | client | bool | false |  | Game's own stall text · HUD · basic | The game's own stall warning (the big red text when the thrusters cannot hold the ship against gravity; it ignores the wings' lift). Off: removed. |
| `Autopilot.Readout` | client | bool | true |  | Autopilot readout · HUD · basic | *(Unreleased)* The engaged autopilot mode and its protection in the speed readout ("12 m/s · AP HOVER POS AGL · FLOOR"). |
| `Feel.Creaks` | client | bool | true |  | Airframe creaks · Feel and audio · basic | The airframe creaks and groans as the structure is loaded (the ship you are seated in: from 0.6 of its worst joint's strength, more often and louder toward 1, heavier groans from 0.85), and cracks when a block wears or breaks. The game's own sound events, at the cockpit. |
| `Feel.CreakVolume` | client | float | 1 | 0..2 | Creak volume · Feel and audio · basic | The creaks' and cracks' volume, times this. |
| `Feel.Shake` | client | bool | true |  | Load shake · Feel and audio · basic | The cockpit buzzes with the structure's load (from 0.5, noticeable near 1), a jolt when a block breaks. Through the game's camera shake (its own switches apply). |
| `Feel.ShakeScale` | client | float | 1 | 0..5 | Load shake strength · Feel and audio · basic | The load shake's strength, times this. |
| `Feel.CreakFrom` | client | float | 0.6 | 0.3..1 | Creaks start at load · Feel and audio · advanced | The creaks start at this share of the structure's strength (0.6: at 60%). |
| `Fx.Effects` | client | bool | true |  | Aero visual effects · Visual effects · basic | Every aero visual effect. |
| `Fx.FlightField` | client | bool | true |  | Full flight effects · Visual effects · advanced | The full flight effects (plasma, vapour, tip trails). Off: the older single plasma at the nose, no vapour or trails (`Fx.Effects` off: none at all). |
| `Fx.Plasma` | client | bool | true |  | Re-entry plasma · Visual effects · basic | Entry plasma sparks over the hull. |
| `Fx.Vapour` | client | bool | true |  | Transonic vapour · Visual effects · basic | Transonic vapour along the hull's edges. |
| `Fx.Tips` | client | bool | true |  | Wingtip trails · Visual effects · basic | Wing-tip vapour trails. |
| `Fx.SpotLight` | client | bool | true |  | Plasma glow light · Visual effects · basic | The plasma's light on the hull (one shadowed spot light per glowing grid). |
| `Fx.TornSmoke` | client | bool | true |  | Smoke from breaks · Visual effects · basic | A joint the structure breaks smokes (the game's damaged-block smoke) and sheds debris (its fracture debris) for `Fx.TornSmokeSeconds`, on the ship where the block was and on the piece that tears off, at its torn edge; parented to each, so the smoke trails behind them. Off with `Fx.Effects`. Single player and a host. |
| `Fx.TornSmokeSeconds` | client | float | 4 | 0.5..20 | Break smoke duration · Visual effects · advanced | Seconds a torn joint smokes. |
| `Fx.VapourCone` | client | bool | false |  | Vapour cone (experimental) · Visual effects · advanced | The transonic shock's vapour cone round the body (experimental: off until tuned). |
| `Fx.Brightness` | client | float | 1 | 0..3 | Plasma brightness · Visual effects · advanced | The plasma's and glow's brightness, times this. |
| `Fx.ScorchVisible` | client | bool | true |  | Show scorch marks · Visual effects · advanced | Draw the hull's scorch marks on this PC (Heat.Scorch, the world's, decides whether there are any). Turning it off stops new marks only: those already placed stay. |
| `Fx.ThrusterFlamesFollow` | client | bool | true |  | Thruster flames show attitude thrust · Visual effects · advanced | Thruster flames show the share each thruster gives to steering (off: the game's own flames). |
| `Fx.DebrisScale` | client | float | 1 | 0..3 | Break debris amount · Visual effects · advanced | The debris a break sheds, times this. |
| `Debris.Drag` | client | bool | true | | Debris drag · Visual effects · advanced | Loose fracture pieces (a broken block's chips, not grids) slow in the air: quadratic drag on their bounding box against the air (the wind taken off, the game's own wind push counted in), and a little on their tumbling. The pieces exist on this PC only. |
| `Debris.DragScale` | client | float | 0.4 | 0..10 | Debris drag strength · Visual effects · advanced | The pieces' drag coefficient on their bounding box (1: a blunt chunk; 0.4: slight). |
| `Fx.MaxGrids` | client | int | 4 | 1..32 | Ships with effects · Effects quality · basic | Grids with flight effects at once (the nearest). |
| `Fx.Range` | client | float | 4000 | 100..50000 | Effect distance · Effects quality · basic | Distance (m) past which a grid shows no flight effects. |
| `Fx.SpawnPerTick` | client | int | 48 | 1..200 | Effect build-up speed · Effects quality · advanced | Effects started per frame while a grid's effects build. |
| `QuickLaunch.Key` | client | int | 20 | 0..26 | Quick launch key · Controls · basic | *(1.0.1)* The quick launch key: 0 off, 1..26 the letters A..Z (20: T). Seated, in creative worlds that allow it. |
| `QuickLaunch.Modifier` | client | int | 1 | 0..3 | Quick launch modifier · Controls · basic | *(1.0.1)* Held with it: 0 none (no Ctrl, Shift or Alt held), 1 Ctrl, 2 Shift, 3 Alt. |

**Multiplayer:** SE2 2.4 runs its server in the same process as the client (its networking is a mock), so world values are the server's and the flight effects read them directly. A process that has only seen a client scene refuses world sets. When SE2 gets real multiplayer, the world values the clients' effects use (the entry Mach pair) will need replicating.

## The Orbital Mod

The two mods compile apart and meet only by reflection. The Orbital Mod's `tools/check_contract.sh` (its fast gate) checks every link below against both sources: the names, the parameter lists, that every lookup passes its parameter types, and that neither API overloads a name.

**Aero → Orbital** (`OrbitalMod.OrbitalApi`, that mod's `docs/API.md`; every lookup in `Api/OrbitalLink.cs`: the last loaded copy of `OrbitalApi`, each member found by name and parameter types and bound as a typed delegate, bound again when the Orbital Mod's assembly changes (a hot reload), looked up again every 5 s while missing, dropped if one throws and looked up again after 10 s doubling to 300 s):

| Member | Aero's caller | What for |
|---|---|---|
| `TryGetGridEdits(Entity, out Vector3D, out long)` | `OrbitalLink` → `StructureContacts` | The contact loads' momentum balance. Orbital writes the physics velocity after the step (a frame anchor's motion drained into the rails every tick, warp's rescale and steering, differential gravity, a spinning planet's fictitious forces): none of it is a force. The change of its running sum since the last read is taken off the balance (× mass), and a changed move stamp masks the step as `NoteMoved` does. Without it an anchor's own thrust read as plate load, a warp switch as a giant impulse, the fictitious forces as a phantom support. |
| `TryGetEntryState2(Entity, out Vector3, out float, out float, out Vector3)`, else `TryGetEntryState(Entity, out Vector3, out float)` | `OrbitalLink` (and `AeroEntryFx.EntryStateAt` through it) | A railed re-entry: the true travel into the air, density and the band's temperature (the hull heats from them, with that temperature as its ambient), and the frame's braking acceleration (`Structure.EntryLoads`: the structure is loaded with it). |
| `TryQuickMove(Entity, Vector3D, Quaternion, Vector3D, double, out Vector3D, out string)` *(Orbital 1.0.1, 2026-10-18)* | `OrbitalLink` → `QuickLaunch.Server.cs` | Quick launch's move when the Orbital Mod is loaded: aero never teleports a grid behind its back (its frames, berths and queued moves would not know). Orbital moves the grid and its joined set by its own safe path (the teleport phase; statics and sets joined to one refused), refuses a grid on rails or outside a planet's cell, and clamps the target under its planet's space border (down by at most the clearance room aero passes, else refused). An Orbital Mod without the member (older) refuses the launch rather than aero moving the ship itself. |

**Orbital → Aero** (this API): `RegisterEntrySource` / `HasEntrySource` (the plasma of a railed re-entry), `PredictHullForces` / `RequestForceTable` (each railed frame's ballistic coefficient), `TryGetAtmosphereAt` (the air at a planet's border: the band's density and temperature), `NoteMoved` (its teleports and turns), `SetFocusGrid`, `TryGetFlight`, `TryGetForces`, `TryGetStructure`, `TryGetStall`, `TryGetLoac` and the design probe (`TryDesign`, `GetTopSpeed`, `GetTopSpeedAt`, `GetThrustAlong`, `TryGetCenterOfMass`, `GetMass`, `GetDesignPressures`, `GetControlRatio`: its build hints and flight gauges), version 7's `GetStructureEventSequence` / `ReadStructureEvents` (`StructureAlerts`: the seated ship's structural failures and burn-throughs as a toast, typed delegates, polled every 0.5 s), and, for its dev harness only, `RequestQuickLaunch` (its key `quicklaunch`), `TryGetLift`, `TryGetGroundHeight`, `TryGetEntry`, `TryGetAtmosphere`, `GetFxConsistency`, `FlightFixture.Build`, `GridPhysics.GetGravityDirection` and static fields by name.

**The physics' speed cap:** aero reads the engine's own (`IVelocityLimitProvider.LinearVelocityLimit`, the world definition's `Physics.MaximumSpeedLinear`; never assumed 1000) for the contact balance: at the cap the velocity's change along itself is the clamp's, not a contact's. The Orbital Mod publishes the same value as `GetPhysicsSpeedCap`; its `GetSpeedCap` is its arrival cap (990).

## Reaching aero from a script

SE2 2.4 has the engine side of programmable blocks (`InGameScriptingComponent`, compiled against the same whitelist as mods), but no vanilla block uses it, so there is no player script yet. A script, once there is one, could reach this API the same way a mod does: by reflection, if its whitelist keeps `System.Reflection`. There is no mod-to-mod message channel and no mod settings screen of SE2's own (the mods add a "Mods" tab to its Options screen, §Settings); the settings files and this API are the way in.
