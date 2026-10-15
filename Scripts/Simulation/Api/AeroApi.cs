#pragma warning disable
using System;
using System.Collections.Generic;
using Keen.VRage.Core;
namespace AeroMod;

/// <summary>
/// THE AERODYNAMICS MOD'S PUBLIC API - for other mods (and their scripts) that want what aero knows.
///
/// Mods compile separately, so a consumer reaches this class BY REFLECTION: find the type "AeroMod.AeroApi" in the
/// loaded assemblies, check <see cref="Version"/> (a const: read it with FieldInfo.GetRawConstantValue), cache the
/// MethodInfos. Every signature uses only .NET and engine types (Entity, Vector3, List), never an aero type.
///
/// Frames and units: "local" vectors are in the grid's own frame; "world" in world axes. SI units (m, s, kg, N, Pa).
/// Air: the direction the grid travels INTO the air (so the wind it feels is the opposite).
///
/// Where it works: on the machine that runs the aero simulation (single player, a host, a dedicated server). A grid's
/// server copy answers directly; a client copy in the same process (single player / host) answers from the nearest
/// published record. A remote multiplayer client gets false (nothing is published to it yet).
///
/// Threads: any. Every answer is one consistent snapshot of the last simulation tick (copied under a lock). Version 7's
/// members (AeroApi.Flight.cs, AeroApi.Structure.cs: partial files of this class) answer from records published while
/// someone reads them, into the caller's own arrays - no allocation, no exception handling on the way.
/// LOCKS: never call this API while holding a lock of your own, and aero never calls another mod while holding one of
/// its own (aero's reads of the Orbital mod - OrbitalLink, AeroEntryFx.EntryStateAt - are made with none held): a call
/// that waits on the other's lock while the other waits on yours is a deadlock neither mod can see.
///
/// Stability: members are only ADDED within a version; a change to an existing one bumps <see cref="Version"/>. A public
/// member is NEVER OVERLOADED (no second member of the same name): callers find members by name - pass the parameter types
/// too - and an overload makes a name-only lookup throw AmbiguousMatchException. A new shape gets a new name.
/// The Orbital Mod's fast gate checks the members it uses from source (its tools/check_contract.sh).
/// Documented in docs/API.md (the same text, with an example).
/// </summary>
public static partial class AeroApi
{
    /// <summary>The API contract. 2: the first published API (entry sources were contract 2 before it). 3: the
    /// atmosphere, thrust profiles, change counters, aero blocks (read), the on/off switch and the settings - every
    /// version-2 member unchanged.</summary>
    public const int Version = 7;   // (7: the per-tick flight state, control surfaces, thruster forces, the structure and hull heat block by block and face by face, failure events, another heat model's temperatures - AeroApi.Flight.cs, AeroApi.Structure.cs; 6: SetDrag, SetPlateDrag, ClearDrag, TryGetDrag - other mods' drag on a grid; 5: GetTopSpeedAt, GetMass, GetDesignPressures; 4: the design probe - TryDesign, GetTopSpeed, GetThrustAlong, TryGetCenterOfMass, GetWavePeak)

    // ── the air, per grid ──

    /// <summary>The grid's air (last tick): its travel into the air (local, unit), airspeed (m/s), Mach, air density
    /// (kg/m3), dynamic pressure (Pa). False: not moving through air (under 1 m/s, no atmosphere) or not available here.</summary>
    public static bool TryGetFlight(Keen.VRage.DCS.Components.Entity grid, out Vector3 travelLocal, out float airspeed, out float mach, out float density, out float dynamicPressure)
    {
        travelLocal = default; airspeed = mach = density = dynamicPressure = 0f;
        if (!AeroEntryFx.FlowOf(grid, out var p)) return false;
        travelLocal = p.TravelLocal; airspeed = p.Speed; mach = p.Mach; density = p.Density; dynamicPressure = p.Q;
        return true;
    }

    /// <summary>The aerodynamic load (last tick, local): total force (N) and torque (N m) - wings and control surfaces
    /// included - its drag and lift magnitudes (N), the frontal area (m2) the drag acts on.</summary>
    public static bool TryGetForces(Keen.VRage.DCS.Components.Entity grid, out Vector3 forceLocal, out Vector3 torqueLocal, out float drag, out float lift, out float frontalArea)
    {
        forceLocal = torqueLocal = default; drag = lift = frontalArea = 0f;
        if (!AeroEntryFx.FlowOf(grid, out var p)) return false;
        forceLocal = p.ForceLocal; torqueLocal = p.TorqueLocal; drag = p.Drag; lift = p.Lift; frontalArea = p.FrontalArea;
        return true;
    }

    /// <summary>The lift: its direction (local, unit; zero when none) and strength as a coefficient on the frontal area
    /// (lift / (dynamic pressure x frontal area)).</summary>
    public static bool TryGetLift(Keen.VRage.DCS.Components.Entity grid, out Vector3 liftLocal, out float liftCoefficient)
    {
        liftLocal = default; liftCoefficient = 0f;
        if (!AeroEntryFx.FlowOf(grid, out var p)) return false;
        liftLocal = p.LiftLocal; liftCoefficient = p.LiftCoef;
        return true;
    }

    /// <summary>Height above the ground under the grid (m, measured at its origin along gravity; the ship and what is
    /// attached to it ignored), -1 when none within 2 km or out of gravity.</summary>
    public static bool TryGetGroundHeight(Keen.VRage.DCS.Components.Entity grid, out float height)
    {
        height = -1f;
        var aero = AeroOf(grid);
        if (aero == null) return false;
        height = aero.GroundHeight;
        return true;
    }

    // ── entry heating ──

    /// <summary>The grid's entry heating now, 0..1 (the plasma's strength), and whether an entry source owns it (another
    /// mod - see <see cref="RegisterEntrySource"/>). Aero's own model: onset at Mach 2, full ~Mach 2.5, x density.</summary>
    public static bool TryGetEntry(Keen.VRage.DCS.Components.Entity grid, out float strength, out bool external)
    {
        strength = 0f; external = false;
        if (!AeroEntryFx.FlowOf(grid, out var p)) return false;
        external = p.ExtStrength >= 0f;
        strength = external ? p.ExtStrength : AeroEntryFx.EntryStrength(p.Mach, p.Density);
        return true;
    }

    /// <summary>The effects' consistency since the last reset (every drawn frame of every flight field): frames, pops (the drawn
    /// strength stepping over 0.05 in a frame), rebuilds while lit, frames lost, group toggles, the largest step, the peak.
    /// reset: start counting again. For tests.</summary>
    public static string GetFxConsistency(bool reset)
    {
        string r = FlightField.RecReport() + " | " + FlightField.TrkReport();
        if (reset) { FlightField.RecReset(); FlightField.TrkReset(); }
        return r;
    }

    /// <summary>Aero's entry heating law: strength 0..1 at a Mach number and air density (kg/m3).</summary>
    public static float EntryStrength(float mach, float density) => AeroEntryFx.EntryStrength(mach, density);

    // ── the shape ──

    /// <summary>The wing tips aero detected on the grid (local m, at the trailing edge) and each tip's wing lift normal
    /// (local, unit). Lists are cleared first. Returns the count (0: no wings, or the grid not built yet).</summary>
    public static int GetWingTips(Keen.VRage.DCS.Components.Entity grid, List<Vector3> tipsLocal, List<Vector3> normalsLocal)
    {
        if (grid == null || tipsLocal == null || normalsLocal == null) return 0;
        var e = grid;
        if (AeroEntryFx.FlowOf(grid, out var p) && p.Aero?.Entity != null) e = p.Aero.Entity;   // (a client copy: its server copy's wings)
        return AeroEntryFx.WingTips(e, tipsLocal, normalsLocal);
    }

    // ── the model in detail ──

    /// <summary>The grid's server-side aero (its own, or a client copy's flow record's). Null: not available here.</summary>
    static AeroGridComponent AeroOf(Keen.VRage.DCS.Components.Entity grid)
    {
        if (grid == null) return null;
        foreach (var c in grid.Components) if (c is AeroGridComponent a && a.IsServerScene) return a;
        return AeroEntryFx.FlowOf(grid, out var p) ? p.Aero : null;
    }

    /// <summary>The hull's exposed faces (local): centre (m), outward normal (unit), area (m2), and the pressure coefficient
    /// Cp of the last face-model evaluation (0 where none: a grid on its force table evaluates no faces each tick). Lists
    /// are cleared first; pass null for any you do not want. Returns the count (0 while the grid is (re)building, and
    /// until the grid's next simulation tick after the first call: the faces are published by its tick while they are
    /// asked for). Large ships have 10^5 faces: call it rarely and keep the lists.</summary>
    public static int GetSurface(Keen.VRage.DCS.Components.Entity grid, List<Vector3> centresLocal, List<Vector3> normalsLocal, List<float> areas, List<float> cp)
    {
        centresLocal?.Clear(); normalsLocal?.Clear(); areas?.Clear(); cp?.Clear();
        // (from the faces the grid's tick publishes - AeroGridComponent.PublishApiHull: this read the live face list,
        //  which the simulation thread updates and a rebuild refills, and tore or threw, 2026-10-15)
        var s = Watch(grid, false);
        if (s == null) return 0;
        long until = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(WatchSeconds * System.Diagnostics.Stopwatch.Frequency);
        if (until > s.HullUntil) s.HullUntil = until;
        lock (s.Lock)
        {
            int n = Math.Max(s.HullCount, 0);
            for (int i = 0; i < n; i++)
            {
                centresLocal?.Add(s.HullPos[i]); normalsLocal?.Add(s.HullNrm[i]); areas?.Add(s.HullArea[i]); cp?.Add(s.HullCp[i]);
            }
            return n;
        }
    }

    /// <summary>How many wings aero detected on the grid (index them 0..count-1 in the calls below).</summary>
    public static int GetWingCount(Keen.VRage.DCS.Components.Entity grid) => AeroOf(grid)?.FxWings?.Count ?? 0;

    /// <summary>A detected wing (local): lift normal, span and chord axes (unit; chord toward the trailing edge), quarter-chord
    /// point (m), planform area (m2), span and mean chord (m), leading-edge sweep (rad), lift slope (per rad), maximum
    /// lift coefficient, stall angle (rad).</summary>
    public static bool TryGetWing(Keen.VRage.DCS.Components.Entity grid, int index, out Vector3 normalLocal, out Vector3 spanAxisLocal, out Vector3 chordAxisLocal,
        out Vector3 aeroCenterLocal, out float area, out float span, out float meanChord, out float sweep, out float clAlpha, out float clMax, out float alphaStall)
    {
        normalLocal = spanAxisLocal = chordAxisLocal = aeroCenterLocal = default; area = span = meanChord = sweep = clAlpha = clMax = alphaStall = 0f;
        var wings = AeroOf(grid)?.FxWings;
        if (wings == null || index < 0 || index >= wings.Count) return false;
        var w = wings[index];
        normalLocal = w.Normal; spanAxisLocal = w.SpanAxis; chordAxisLocal = w.ChordAxis; aeroCenterLocal = w.AeroCenter;
        area = w.PlanformArea; span = w.Span; meanChord = w.MeanChord; sweep = w.SweepAngle; clAlpha = w.CLAlpha; clMax = w.CLMax; alphaStall = w.AlphaStall;
        return true;
    }

    /// <summary>A wing's state on the last tick (local): its lift and induced drag (N), where they act (m), lift and
    /// induced-drag coefficients, effective angle of attack (rad). False: no such wing, or not computed this tick.</summary>
    public static bool TryGetWingState(Keen.VRage.DCS.Components.Entity grid, int index, out Vector3 liftLocal, out Vector3 inducedDragLocal, out Vector3 pointLocal,
        out float cl, out float cdi, out float alphaEffective)
    {
        liftLocal = inducedDragLocal = pointLocal = default; cl = cdi = alphaEffective = 0f;
        var forces = AeroOf(grid)?.FxModel?.LastWingForces;
        if (forces == null || index < 0 || index >= forces.Count) return false;
        var r = forces[index];
        liftLocal = r.LiftForce; inducedDragLocal = r.InducedDrag; pointLocal = r.ApplicationPoint; cl = r.CL; cdi = r.CDi; alphaEffective = r.AlphaEffective;
        return true;
    }

    /// <summary>A wing's structural strain (last tick, smoothed over ~0.15 s): its root bending moment over the root's
    /// capacity - 1 is the limit (past it the root wears, with Structure.WingStrain on), 1.5 breaks it off. Root moment and
    /// capacity in N m (the worse side's, for a wing across the centre of mass). Computed whether or not the damage is on.
    /// False: no such wing, or not computed yet (server copy, or the same-process client copy).</summary>
    public static bool TryGetWingStrain(Keen.VRage.DCS.Components.Entity grid, int index, out float strain, out float rootMoment, out float capacity)
    {
        strain = rootMoment = capacity = 0f;
        var aero = AeroOf(grid);
        return aero != null && aero.TryGetWingStrain(index, out strain, out rootMoment, out capacity);
    }

    /// <summary>The grid's STRUCTURE and HULL HEAT (StructureServer: the method of sections on its blocks, the hull's
    /// aerodynamic heating; a background evaluation a few times a second, server). maxUtilisation: the worst joint's load
    /// over its strength now (heat and damage in; 1: yield - the joint wears past it with Structure.Damage on, and breaks
    /// at Structure.BreakAt); maxSkinK: the hottest hull skin (K); heatShare: the hottest block's way from softening to
    /// burning (0..1). False: not evaluated yet (or Structure.Loads off).</summary>
    public static bool TryGetStructure(Keen.VRage.DCS.Components.Entity grid, out float maxUtilisation, out float maxSkinK, out float heatShare)
    {
        maxUtilisation = maxSkinK = heatShare = 0f;
        var aero = AeroOf(grid);
        if (aero == null || aero.StructMaxSkinK <= 0f) return false;
        maxUtilisation = aero.StructMaxUtil; maxSkinK = aero.StructMaxSkinK; heatShare = aero.StructHeatShare;
        return true;
    }

    /// <summary>The grid was just moved by someone else (a teleport, a frame shift, a rails stow or arrival): its physics
    /// contacts for the next steps are not loads (StructureContactLoads.MaskSteps). Server, any thread before the physics step.
    /// Takes no lock (safe to call while holding one of yours - though the lock rule says not to). The Orbital mod also
    /// publishes a move stamp aero reads itself (OrbitalApi.TryGetGridEdits): this is the push, that the pull.</summary>
    public static void NoteMoved(Keen.VRage.DCS.Components.Entity grid)
    {
        var aero = grid?.TryGet<AeroGridComponent>();
        aero?.MaskContacts(StructureContactLoads.MaskSteps);
    }

    /// <summary>The grid a player is flying (seated in) - call it about once a second while they are. Only such grids'
    /// structure and hull heat are evaluated (gauges, damage), at the full rate; it stays so for Structure FocusLinger s
    /// after the last call (a pilot bailing out mid-dive: the dive still ends as it would).</summary>
    public static void SetFocusGrid(Keen.VRage.DCS.Components.Entity grid)
    {
        var aero = AeroOf(grid);
        if (aero != null) aero.StructFocusUntil = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(StructureLoads.FocusLinger * System.Diagnostics.Stopwatch.Frequency);
    }

    /// <summary>The STALL WARNING of a player's grid (StallWarning.cs: the whole ship's best lift within reach, -15 to +60
    /// deg from where it flies; any shape, no wings needed): state 0 none, 1 approaching (the best lift just ahead, or the
    /// lift now on its flat top), 2 stalled (the best behind, the lift now clearly short of it) - each held 0.3 s; the
    /// best lift's angle from now (deg, negative: behind), the share of it in use now, and the g it would give. False: not
    /// a player's grid, or never evaluated.</summary>
    public static bool TryGetStall(Keen.VRage.DCS.Components.Entity grid, out int state, out float marginDeg, out float liftShare, out float gAvailable)
    {
        state = 0; marginDeg = liftShare = gAvailable = 0f;
        var aero = AeroOf(grid);
        if (aero == null || aero.StallGAvailable <= 0f) return false;
        state = aero.StallState; marginDeg = aero.StallMarginDeg; liftShare = aero.StallLiftInUse; gAvailable = aero.StallGAvailable;
        return true;
    }

    /// <summary>LOSS OF ATTITUDE CONTROL of a player's grid, PREDICTED (Loac.cs): the commanded attitude and the way to it,
    /// the air's torque there at the speed now against the gyros', thrusters' and control surfaces' reach. state 0 none,
    /// 1 at the limit (over 90% of the torque available needed), 2 lost (more than available) - held 0.25 s; the worst
    /// needed / available. False: not a player's grid.</summary>
    public static bool TryGetLoac(Keen.VRage.DCS.Components.Entity grid, out int state, out float ratio)
    {
        state = 0; ratio = 0f;
        var aero = AeroOf(grid);
        if (aero == null) return false;
        state = aero.LoacState; ratio = aero.LoacRatio;
        return true;
    }

    /// <summary>The grid's highest wing strain (as TryGetWingStrain), 0 with no wings.</summary>
    public static float GetMaxWingStrain(Keen.VRage.DCS.Components.Entity grid) => AeroOf(grid)?.MaxWingStrain ?? 0f;

    /// <summary>PREDICT the hull's aerodynamic force for any flight state, from the grid's precomputed force table (the
    /// face model: shadowing, the subsonic-to-Newtonian blend, wave drag): the grid moving at velocityLocal (m/s, its own
    /// frame) through air of this density (kg/m3) and speed of sound (m/s). Force (N) and torque (N m, about the centre of
    /// mass the table was built for), frontal area (m2). Wings and control surfaces are NOT included (see the wing calls).
    /// False: no table yet. Pure: safe to call as often as you like.</summary>
    public static bool PredictHullForces(Keen.VRage.DCS.Components.Entity grid, Vector3 velocityLocal, float density, float speedOfSound,
        out Vector3 forceLocal, out Vector3 torqueLocal, out float frontalArea)
    {
        forceLocal = torqueLocal = default; frontalArea = 0f;
        var aero = AeroOf(grid);
        var model = aero?.FxModel; var table = model?.Table;
        if (table == null || model.InnerModel is not DampedShadowedDragModel d) return false;
        var atm = new AtmosphereState { Density = density, SpeedOfSound = speedOfSound, Pressure = 0, Temperature = 0, DynamicViscosity = 0 };
        var ctx = new AeroContext(null, null, velocityLocal, atm, table.BuildCom);
        var r = table.Evaluate(ctx, d.SubsonicLimit, d.SupersonicLimit, d.Streamlining);
        forceLocal = r.Force; torqueLocal = r.Torque; frontalArea = r.FrontalArea;
        return true;
    }

    /// <summary>Ask for the grid's force table to be built even where aero would not build one yet - out of air and
    /// gravity (a grid on rails in space: another mod predicting its drag). Built on the grid's next aero step (from the
    /// shape cache when its shape is known). True: the table exists now (PredictHullForces / SampleForceTable answer).</summary>
    public static bool RequestForceTable(Keen.VRage.DCS.Components.Entity grid)
    {
        var aero = AeroOf(grid);
        if (aero == null) return false;
        if (aero.FxModel?.Table != null) return true;
        aero.EntryWantsTable = true;
        return false;
    }

    /// <summary>The raw force table at a travel direction (local; normalised for you), for unit dynamic pressure, into 13
    /// floats: F0 (3: force, subsonic blend), F1 (3: force, Newtonian blend), T0 (3), T1 (3: torques about the grid origin),
    /// frontal area (1). PredictHullForces does the rest of the arithmetic. False: no table, or into shorter than 13.</summary>
    public static bool SampleForceTable(Keen.VRage.DCS.Components.Entity grid, Vector3 travelLocal, float[] into)
    {
        var table = AeroOf(grid)?.FxModel?.Table;
        if (table == null || into == null || into.Length < ForceTable.Stride || travelLocal.LengthSquared() < 1e-8f) return false;
        table.Sample(Vector3.Normalize(travelLocal), into.AsSpan(0, ForceTable.Stride));
        return true;
    }

    // ── the design probe (version 4): the build hints - the ship as if flying, at any direction and Mach (DesignProbe) ──

    /// <summary>The whole ship (hull and wings) as if travelling along travelLocal (grid-local) at this Mach in sea-level air:
    /// its centre of pressure (grid-local: the force's line nearest the centre of mass), force and torque about the centre of
    /// mass, its static stability about the two axes across the flow (positive: the air turns it back; per radian, over q x
    /// area x size) and its streamlining along it. Works for a client grid (paired with its server copy). False: no aero,
    /// or its force table is not built yet (asked for: try again shortly).</summary>
    public static bool TryDesign(Keen.VRage.DCS.Components.Entity grid, Vector3 travelLocal, float mach, out Vector3 copLocal, out Vector3 forceLocal,
        out Vector3 torqueLocal, out float stabilityA, out float stabilityB, out float streamlining)
    {
        copLocal = forceLocal = torqueLocal = default; stabilityA = stabilityB = 0f; streamlining = -1f;
        var aero = DesignProbe.ServerAero(grid);
        return aero != null && DesignProbe.Probe(aero, travelLocal, mach, out copLocal, out forceLocal, out torqueLocal, out stabilityA, out stabilityB, out streamlining);
    }

    /// <summary>The top speed at sea level travelling along dirLocal (m/s): forward thrust (each thruster's profile at the
    /// Mach) against drag. NaN: no aero or table yet; 0: not enough thrust to move; +inf: past 3 km/s.</summary>
    public static float GetTopSpeed(Keen.VRage.DCS.Components.Entity grid, Vector3 dirLocal)
    {
        var aero = DesignProbe.ServerAero(grid);
        return aero == null || dirLocal.LengthSquared() < 1e-8f ? float.NaN : DesignProbe.TopSpeed(aero, dirLocal);
    }

    /// <summary>The thrust pushing along dirLocal at sea level and this Mach (N), and where it acts on average (grid-local).</summary>
    public static float GetThrustAlong(Keen.VRage.DCS.Components.Entity grid, Vector3 dirLocal, float mach, out Vector3 centreLocal)
    {
        centreLocal = default;
        var aero = DesignProbe.ServerAero(grid);
        return aero == null || dirLocal.LengthSquared() < 1e-8f ? 0f : DesignProbe.ThrustAlong(aero, dirLocal, mach, out centreLocal);
    }

    /// <summary>The grid's centre of mass (grid-local). False: no mass data.</summary>
    public static bool TryGetCenterOfMass(Keen.VRage.DCS.Components.Entity grid, out Vector3 comLocal)
    {
        comLocal = default;
        var aero = DesignProbe.ServerAero(grid);
        return aero != null && DesignProbe.CenterOfMass(aero, out comLocal);   // (the ship's: its subgrids too)
    }

    /// <summary>The transonic drag peak (x subsonic) a streamlining gives (ForceTable.WavePeak).</summary>
    public static float GetWavePeak(float streamlining) => ForceTable.WavePeak(streamlining);

    /// <summary>(version 5) The sea-level speed along dirLocal where drag meets throttle (0..1) x the forward thrust; NaN no
    /// aero or table; +infinity past 3000 m/s; 0 too little thrust. GetTopSpeed is this at throttle 1.</summary>
    public static float GetTopSpeedAt(Keen.VRage.DCS.Components.Entity grid, Vector3 dirLocal, float throttle)
    {
        var aero = DesignProbe.ServerAero(grid);
        return aero == null || dirLocal.LengthSquared() < 1e-8f ? float.NaN : DesignProbe.TopSpeed(aero, dirLocal, Math.Clamp(throttle, 0f, 1f));
    }

    /// <summary>(version 5) How well the ship holds its attitude travelling along dirLocal at speed (m/s, sea level): the
    /// torque its gyros and thrusters can give over the air's torque at a 5 degree upset (DesignProbe.ControlRatio). Over
    /// 1: it can hold it. NaN: no aero or table. (Design figures are for the whole ship: the grid and its subgrids.)</summary>
    public static float GetControlRatio(Keen.VRage.DCS.Components.Entity grid, Vector3 dirLocal, float speed)
    {
        var aero = DesignProbe.ServerAero(grid);
        return aero == null ? float.NaN : DesignProbe.ControlRatio(aero, dirLocal, speed);
    }

    /// <summary>(version 5) The ship's mass, kg - the grid and its subgrids (0: unknown).</summary>
    public static float GetMass(Keen.VRage.DCS.Components.Entity grid) => DesignProbe.Mass(DesignProbe.ServerAero(grid));

    /// <summary>(version 5) THE DRAG OVERLAY: the hull's faces (grid-local centres, normals, areas) and their pressure
    /// coefficients for the grid travelling along travelLocal at this Mach - positive pushes in (drag at the front),
    /// negative pulls (suction behind); shadowed windward faces and wing skins left out. At most max faces (spread over
    /// the hull). Returns the count; 0 no aero, or its model not built yet.</summary>
    public static int GetDesignPressures(Keen.VRage.DCS.Components.Entity grid, Vector3 travelLocal, float mach, List<Vector3> centresLocal,
        List<Vector3> normalsLocal, List<float> areas, List<float> cp, int max)
        => DesignProbe.Pressures(DesignProbe.ServerAero(grid), travelLocal, mach, centresLocal, normalsLocal, areas, cp, max);

    // ── the atmosphere (version 3) ──

    /// <summary>The air at a world point: density (kg/m3) as the game's atmosphere generators make it (their sum, the
    /// engine's own law), and from it aero's model: pressure (Pa), temperature (K), speed of sound (m/s). False: the
    /// generator list is not known yet (no aero grid simulated in this process - see <see cref="GetAtmosphereStatus"/>).
    /// Vacuum answers true with zeros. Any thread; the generators are refreshed every ~10 s.</summary>
    public static bool TryGetAtmosphereAt(Vector3D worldPoint, out float density, out float pressure, out float temperature, out float speedOfSound)
    {
        density = pressure = temperature = speedOfSound = 0f;
        if (!AtmosphereProbe.DensityAt(worldPoint, out density)) return false;
        AtmosphereAtDensity(density, out pressure, out temperature, out speedOfSound);
        return true;
    }

    /// <summary>The air an entity is in as aero flies it: the game's (its AirData, this tick: grids, characters - any
    /// simulated entity in an atmosphere) or, above where the game's air stops, aero's thin upper tail (AtmosphereTail),
    /// with aero's pressure, temperature and speed of sound. False: no air data and no tail here.</summary>
    public static bool TryGetAtmosphere(Keen.VRage.DCS.Components.Entity entity, out float density, out float pressure, out float temperature, out float speedOfSound)
    {
        density = pressure = temperature = speedOfSound = 0f;
        if (entity == null || !PhysicsHack.Alive(entity.Data)) return false;
        bool has = entity.Data.TryGet<AirData>(out var air);
        density = AtmosphereProbe.WithTail(entity.Data.GetWorldTransform().Position, has ? air.Density : 0f);
        if (!has && density <= 0f) return false;
        AtmosphereAtDensity(density, out pressure, out temperature, out speedOfSound);
        return true;
    }

    /// <summary>Aero's atmosphere model at a density (SE2 gives density only; the temperature follows from the standard
    /// atmosphere at that density, then the ideal gas): pressure (Pa), temperature (K), speed of sound (m/s). Vacuum
    /// (under 1e-6 kg/m3): zero pressure and speed of sound. Pure.</summary>
    public static void AtmosphereAtDensity(float density, out float pressure, out float temperature, out float speedOfSound)
    {
        var st = AtmosphereProbe.State(density);
        pressure = (float)st.Pressure; temperature = (float)st.Temperature; speedOfSound = (float)st.SpeedOfSound;
    }

    /// <summary>Why <see cref="TryGetAtmosphereAt"/> cannot answer ("" when it can).</summary>
    public static string GetAtmosphereStatus() => AtmosphereProbe.Why;
    /// <summary>How many atmosphere generators the point query knows.</summary>
    public static int GetAtmosphereGeneratorCount() => AtmosphereProbe.Generators;

    // ── thrust (version 3) ──

    /// <summary>The thrust profiles by name (Rocket, Propeller, Turbofan, Turbojet, Ramjet, Scramjet, HelicopterRotor).
    /// The list is cleared first; returns the count.</summary>
    public static int GetThrustProfiles(List<string> names)
    {
        if (names == null) return 0;
        names.Clear();
        foreach (var k in ThrustProfiles.All.Keys) names.Add(k);
        return names.Count;
    }

    /// <summary>A profile's thrust at a Mach number, relative to its static (Mach 0) rating: 0 outside its operating
    /// envelope, above 1 where ram pressure helps. -1: no such profile. Pure.</summary>
    public static float ThrustEffectiveness(string profile, float mach)
        => profile != null && ThrustProfiles.All.TryGetValue(profile, out var p) ? (float)p.Evaluate(mach) : -1f;

    /// <summary>A profile's operating envelope (Mach; the top is float.MaxValue for none). False: no such profile.</summary>
    public static bool TryGetThrustProfile(string profile, out float minMach, out float maxMach)
    {
        minMach = maxMach = 0f;
        if (profile == null || !ThrustProfiles.All.TryGetValue(profile, out var p)) return false;
        minMach = (float)p.MinOperatingMach; maxMach = (float)Math.Min(p.MaxOperatingMach, float.MaxValue);
        return true;
    }

    /// <summary>The grid's thrusters as aero sees them: each block entity, its profile ("Rocket": none, flat) and its
    /// effectiveness now (the profile at the grid's Mach and the air's angle into the intake; at rest, Mach 0). Lists
    /// are cleared first; pass null for any you do not want. Returns the count (0: no aero here or no thrusters).</summary>
    public static int GetThrusters(Keen.VRage.DCS.Components.Entity grid, List<Keen.VRage.DCS.Components.Entity> thrusters, List<string> profiles, List<float> effectiveness)
    {
        thrusters?.Clear(); profiles?.Clear(); effectiveness?.Clear();
        var aero = AeroOf(grid);
        if (aero == null) return 0;
        bool flying = AeroEntryFx.FlowOf(grid, out var p);
        float mach = flying ? p.Mach : 0f; Vector3 travel = flying ? p.TravelLocal : Vector3.Zero;
        // (the list as last published - AeroGridComponent.ThrusterSnap, a copy made at each rebuild: the live one is cleared
        //  and refilled by the simulation thread, iterated bare it tore or threw, 2026-10-15)
        ThrusterInfo[] cache = aero.ThrusterSnap;
        int n = cache.Length;
        for (int i = 0; i < n; i++)
        {
            var t = cache[i];
            thrusters?.Add(t.ThrusterEntity);
            profiles?.Add(t.Profile?.Name ?? "Rocket");
            effectiveness?.Add(t.Profile == null ? 1f : flying ? (float)t.Profile.Evaluate(mach, travel, t.IntakeDirection) : (float)t.Profile.Evaluate(0.0));
        }
        return n;
    }

    // ── change counters (version 3): cache what you read, read again when a counter moves ──

    /// <summary>The grid's change counters: hull (its exposed surface rebuilt, or updated cell by cell), wings (detected
    /// again, updated, or a wing cell lost) and table (a new force table installed: the hull's forces changed). Each only
    /// goes up; compare with what you saw last. False: no aero for this grid here. A rebuild of every grid (aero on
    /// again, a setting that needs it) also shows in <see cref="GetGeneration"/>.</summary>
    public static bool TryGetChangeCounters(Keen.VRage.DCS.Components.Entity grid, out int hull, out int wings, out int table)
    {
        hull = wings = table = 0;
        var aero = AeroOf(grid);
        if (aero == null) return false;
        var model = aero.FxModel;
        wings = model?.WingsVersion ?? 0; table = model?.TableVersion ?? 0;
        var surf = aero.FxSurface;
        int ver = surf?.Version ?? 0;
        // (the surface is double-buffered - two providers swapped at each rebuild, each with its own version that only
        //  goes up - so the hull counter moves whenever the (provider, version) pair moves: no pair comes back)
        lock (_hulls)
        {
            var h = _hulls.GetOrCreateValue(aero);
            if (!ReferenceEquals(h.Surface, surf) || h.SurfaceVersion != ver) { h.Surface = surf; h.SurfaceVersion = ver; h.Counter++; }
            hull = h.Counter;
        }
        return true;
    }

    sealed class HullTrack { public object Surface; public int SurfaceVersion, Counter; }
    static readonly ConditionalWeakTable<AeroGridComponent, HullTrack> _hulls = new();

    /// <summary>Bumped whenever every grid rebuilds from scratch (aero switched on again, a setting that needs it).</summary>
    public static int GetGeneration() => AeroSwitch.Generation;

    // ── aero blocks (version 3, read only) ──

    /// <summary>The grid's aero blocks: kind ("ControlSurface", "Airbrake", "AirIntake", "AtmosphericScoop",
    /// "HelicopterRotor"), grid cell, action point (local m). Lists are cleared first; pass null for any you do not
    /// want. Returns the count.</summary>
    public static int GetAeroBlocks(Keen.VRage.DCS.Components.Entity grid, List<string> kinds, List<Vector3I> cells, List<Vector3> positionsLocal)
    {
        kinds?.Clear(); cells?.Clear(); positionsLocal?.Clear();
        var reg = AeroOf(grid)?.Components;
        if (reg == null) return 0;
        // (under the registry's lock: the simulation thread clears and refills the list on a rebuild - iterated bare it
        //  tore or threw, 2026-10-15)
        lock (reg.SyncRoot)
        {
            var list = reg.Components;
            int n = list.Count;
            for (int i = 0; i < n; i++)
            {
                var c = list[i];
                kinds?.Add(c.GetType().Name); cells?.Add(c.BlockPosition); positionsLocal?.Add(c.Position);
            }
            return n;
        }
    }

    /// <summary>An aero block's state on the last tick, as named values (both lists cleared, then filled; units in
    /// docs/API.md). Control surface: DeflectionInput (-1..1), MaxDeflection (deg), Lift, Drag (N), AoA (deg). Airbrake:
    /// DeployFraction (0..1), Drag (N), Effectiveness (0..1). Intake: MassFlow (kg/s), TotalPressure (Pa),
    /// RamPressureRatio. Scoop: MassFlow (kg/s), CollectionRate, Collected, Drag (N). Rotor: Collective (0..1),
    /// Thrust (N), DensityScale, TranslationalLift, GroundEffect, Autorotation (N). Returns the count (0: no block there).</summary>
    public static int GetAeroBlockState(Keen.VRage.DCS.Components.Entity grid, Vector3I cell, List<string> names, List<float> values)
    {
        if (names == null || values == null) return 0;
        names.Clear(); values.Clear();
        var reg = AeroOf(grid)?.Components;
        if (reg == null) return 0;
        IAeroBlockComponent c = null;
        lock (reg.SyncRoot) { var list = reg.Components; for (int i = 0; i < list.Count; i++) if (list[i].BlockPosition == cell) { c = list[i]; break; } }
        switch (c)
        {
            case ControlSurface s: V(names, values, "DeflectionInput", s.DeflectionInput); V(names, values, "MaxDeflection", s.MaxDeflection); V(names, values, "Lift", s.CurrentLift); V(names, values, "Drag", s.CurrentDrag); V(names, values, "AoA", s.EffectiveAoA); break;
            case Airbrake b: V(names, values, "DeployFraction", b.DeployFraction); V(names, values, "Drag", b.DragForce); V(names, values, "Effectiveness", b.Effectiveness); break;
            case AirIntake a: V(names, values, "MassFlow", a.MassFlowRate); V(names, values, "TotalPressure", a.TotalPressure); V(names, values, "RamPressureRatio", a.RamPressureRatio); break;
            case AtmosphericScoop sc: V(names, values, "MassFlow", sc.MassFlowRate); V(names, values, "CollectionRate", sc.CollectionRate); V(names, values, "Collected", (float)sc.CumulativeCollected); V(names, values, "Drag", sc.DragForce); break;
            case HelicopterRotor r: V(names, values, "Collective", r.CollectivePitch); V(names, values, "Thrust", r.CurrentThrust); V(names, values, "DensityScale", r.DensityScale); V(names, values, "TranslationalLift", r.TranslationalLiftFactor); V(names, values, "GroundEffect", r.GroundEffectFactor); V(names, values, "Autorotation", r.AutorotationThrust); break;
        }
        return names.Count;
    }

    static void V(List<string> names, List<float> values, string n, float v) { names.Add(n); values.Add(v); }

    // ── on / off and the settings (version 3) ──

    /// <summary>The whole aero simulation on (forces, thrust offsets, block tracking) or off: the world setting
    /// "Aero.Enabled".</summary>
    public static bool GetEnabled() => AeroSwitch.Enabled;

    /// <summary>Switch aero on or off (the world setting "Aero.Enabled": every grid rebuilds when it comes back on).
    /// False: not allowed here (see <see cref="SetSetting"/>).</summary>
    public static bool SetEnabled(bool on) => AeroSettings.Set("Aero.Enabled", on ? 1 : 0, out _);

    /// <summary>Every setting's name (world ones first). The list is cleared first; returns the count.</summary>
    public static int GetSettings(List<string> names) => AeroSettings.Names(names);

    /// <summary>A setting's description: type ("bool", "int", "float"), default, range (min..max; a bool is 0..1), scope
    /// ("world": the same for everyone, saved with the world, set by the host / server; "client": this player's own,
    /// saved on this PC), whether a change rebuilds every grid (a few seconds of rebuilding), and what it does.</summary>
    public static bool TryGetSettingInfo(string name, out string type, out double defaultValue, out double min, out double max, out string scope, out bool rebuilds, out string description)
        => AeroSettings.Info(name, out type, out defaultValue, out min, out max, out scope, out rebuilds, out description);

    /// <summary>A setting's value now (a bool as 0 / 1).</summary>
    public static bool TryGetSetting(string name, out double value) => AeroSettings.TryGet(name, out value);

    /// <summary>Change a setting. It takes effect at once (the next tick or frame) and is saved (world: with the world;
    /// client: to this PC). False, with why: no such setting, out of range or not a number, a rule between settings
    /// (Entry.OnsetMach under Entry.FullMach), or a world setting where the server does not run.</summary>
    public static bool SetSetting(string name, double value, out string why) => AeroSettings.Set(name, value, out why);

    /// <summary>Bumped on every change of any setting (the API, a settings file, a world load): watch it to re-read.</summary>
    public static int GetSettingsVersion() => AeroSettings.Version;

    // ── telling aero what it cannot see ──

    /// <summary>Register an entry source under an owner name (registering again replaces): a
    /// <c>Func&lt;Entity, ValueTuple&lt;Vector3, float&gt;&gt;</c> returning, per grid, the air's velocity past it (world
    /// m/s) and how hard it burns (0..1; &lt;= 0: none). While it answers with a strength it OWNS that grid's entry: its air
    /// replaces the grid's motion for the flow and the effects, its strength aero's heating. For grids whose motion aero
    /// cannot see (the Orbital Mod's reentry on rails). Returns the entry-source contract (2, unchanged since API version
    /// 2: AeroEntryFx.EntrySourceContract), or 0 for a wrong delegate type. A source that throws is dropped (register
    /// again).</summary>
    public static int RegisterEntrySource(string owner, Delegate source) => AeroEntryFx.RegisterEntrySource(owner, source);
    public static bool UnregisterEntrySource(string owner) => AeroEntryFx.UnregisterEntrySource(owner);

    /// <summary>Still registered (cheap: a session change or a reloaded aero mod drops it - check now and then).</summary>
    public static bool HasEntrySource(string owner, Delegate source) => AeroEntryFx.HasEntrySource(owner, source);

    // ── other mods' drag (version 6) ──

    /// <summary>Put drag on a grid, or change it: a drag area <paramref name="cdA"/> (Cd x area, m2) acting at
    /// <paramref name="pointLocal"/> (grid-local m), against that point's travel through the air, as q x CdA - so drag off
    /// the centre of mass also turns the ship. Keyed: the same key replaces it; cdA 0 removes it (or ClearDrag). Prefix keys
    /// with your mod's name. Counted as the grid's own air (TryGetForces includes it). Server copy; not saved - set it again
    /// when your block loads. False: no aero grid here, or a bad value (not finite; a point over 2 km out). CdA is capped at
    /// 10000 m2.</summary>
    public static bool SetDrag(Keen.VRage.DCS.Components.Entity grid, string key, Vector3 pointLocal, float cdA)
        => AeroOf(grid)?.SetDragPatch(key, pointLocal, cdA, Vector3.Zero) ?? false;

    /// <summary>As SetDrag, for a FLAT PLATE: <paramref name="cdA"/> is its full drag area face-on, scaled by how squarely
    /// the air meets it (|normal . travel|) - an airbrake, a door, a parachute canopy's disc.</summary>
    public static bool SetPlateDrag(Keen.VRage.DCS.Components.Entity grid, string key, Vector3 pointLocal, float cdA, Vector3 normalLocal)
        => AeroOf(grid)?.SetDragPatch(key, pointLocal, cdA, normalLocal) ?? false;

    /// <summary>Remove every drag on the grid whose key starts with <paramref name="keyPrefix"/> (null or "": all of them).
    /// Returns how many went.</summary>
    public static int ClearDrag(Keen.VRage.DCS.Components.Entity grid, string keyPrefix)
        => AeroOf(grid)?.ClearDragPatches(keyPrefix) ?? 0;

    /// <summary>One drag set on the grid: its point, drag area, plate normal (zero: none). False: no such key.</summary>
    public static bool TryGetDrag(Keen.VRage.DCS.Components.Entity grid, string key, out Vector3 pointLocal, out float cdA, out Vector3 normalLocal)
    {
        pointLocal = normalLocal = Vector3.Zero; cdA = 0f;
        var a = AeroOf(grid);
        return a != null && key != null && a.TryGetDragPatch(key, out pointLocal, out cdA, out normalLocal);
    }

    // ── the effects ──

    /// <summary>Hide (or show again) aero's flight effects on a grid - plasma, vapour, tip trails - e.g. a cloaked or
    /// hidden ship. Pass the grid you have (server or client copy). Not saved.</summary>
    public static void SuppressEffects(Keen.VRage.DCS.Components.Entity grid, bool suppress)
    {
        if (grid == null) return;
        lock (_suppressed)
        {
            _suppressed.Remove(grid);
            if (suppress) _suppressed.Add(grid);
            _suppressCount = _suppressed.Count;
        }
    }

    static readonly List<Keen.VRage.DCS.Components.Entity> _suppressed = new();
    static volatile int _suppressCount;

    /// <summary>Whether a client grid's effects are suppressed (it, or a grid within 1 m of it - its server copy).</summary>
    internal static bool Suppressed(Keen.VRage.DCS.Components.Entity grid, in Vector3D at)
    {
        if (_suppressCount == 0) return false;
        lock (_suppressed)
            foreach (var g in _suppressed)
            {
                if (g == grid) return true;
                if (PhysicsHack.Alive(g.Data) && (g.Data.GetWorldTransform().Position - at).LengthSquared() < 1.0) return true;
            }
        return false;
    }
}
