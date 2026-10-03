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
/// Threads: any. Every answer is one consistent snapshot of the last simulation tick (copied under a lock).
///
/// Stability: members are only ADDED within a version; a change to an existing one bumps <see cref="Version"/>.
/// The Orbital Mod's fast gate checks the members it uses from source (its tools/check_contract.sh).
/// Documented in docs/API.md (the same text, with an example).
/// </summary>
public static class AeroApi
{
    /// <summary>The API contract. 2: the first published API (entry sources were contract 2 before it).</summary>
    public const int Version = 2;

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
    /// are cleared first; pass null for any you do not want. Returns the count (0 while the grid is (re)building). Large
    /// ships have 10^5 faces: call it rarely and keep the lists.</summary>
    public static int GetSurface(Keen.VRage.DCS.Components.Entity grid, List<Vector3> centresLocal, List<Vector3> normalsLocal, List<float> areas, List<float> cp)
    {
        centresLocal?.Clear(); normalsLocal?.Clear(); areas?.Clear(); cp?.Clear();
        var aero = AeroOf(grid);
        var surf = aero?.FxSurface;
        if (surf == null || aero.Rebuilding) return 0;
        try
        {
            var faces = surf.Faces;
            var cps = (aero.FxModel?.InnerModel as DampedShadowedDragModel)?.FaceCp;
            int n = faces.Count;
            for (int i = 0; i < n; i++)
            {
                var f = faces[i];
                centresLocal?.Add(f.Position); normalsLocal?.Add(f.Normal); areas?.Add(f.Area);
                cp?.Add(cps != null && i < cps.Count ? cps[i] : 0f);
            }
            return n;
        }
        catch (Exception) { centresLocal?.Clear(); normalsLocal?.Clear(); areas?.Clear(); cp?.Clear(); return 0; }   // (rebuilt under us)
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

    // ── telling aero what it cannot see ──

    /// <summary>Register an entry source under an owner name (registering again replaces): a
    /// <c>Func&lt;Entity, ValueTuple&lt;Vector3, float&gt;&gt;</c> returning, per grid, the air's velocity past it (world
    /// m/s) and how hard it burns (0..1; &lt;= 0: none). While it answers with a strength it OWNS that grid's entry: its air
    /// replaces the grid's motion for the flow and the effects, its strength aero's heating. For grids whose motion aero
    /// cannot see (the Orbital Mod's reentry on rails). Returns <see cref="Version"/>, or 0 for a wrong delegate type. A
    /// source that throws is dropped (register again).</summary>
    public static int RegisterEntrySource(string owner, Delegate source) => AeroEntryFx.RegisterEntrySource(owner, source);
    public static bool UnregisterEntrySource(string owner) => AeroEntryFx.UnregisterEntrySource(owner);
    /// <summary>Still registered (cheap: a session change or a reloaded aero mod drops it - check now and then).</summary>
    public static bool HasEntrySource(string owner, Delegate source) => AeroEntryFx.HasEntrySource(owner, source);

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
