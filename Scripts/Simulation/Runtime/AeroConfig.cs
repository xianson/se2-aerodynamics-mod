#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// The whole aero simulation on or off (for A/B performance tests: the Orbital Mod's harness 'aero on|off').
/// Off: no aero forces, no thrust offsets, block changes ignored; on again, every grid rebuilds from scratch.
/// </summary>
public static class AeroSwitch
{
    public static bool Enabled = true;
    /// <summary>Bumped on every switch-on: a grid that sees a new generation rebuilds.</summary>
    public static int Generation;
    internal static volatile bool WasOff;
}

/// <summary>
/// The world's aerodynamic strength (AeroSettings "Aero.DragMultiplier" / "Aero.LiftMultiplier", server): the model's
/// force split along the grid's motion (drag) and across it (lift), each scaled, applied by AeroSimJob. Torque has no
/// such split: it is scaled by the two multipliers weighted by the drag and lift magnitudes (exact when they are
/// equal). 1 / 1: the model as is. Read every tick: a change takes effect on the next one.
/// </summary>
public static class AeroTuning
{
    public static float DragMultiplier = 1f, LiftMultiplier = 1f;

    /// <summary>Scale a grid-frame force and torque by the multipliers, for the grid moving at velocityLocal.</summary>
    internal static void Scale(ref Vector3 force, ref Vector3 torque, Vector3 velocityLocal)
    {
        float dm = DragMultiplier, lm = LiftMultiplier;
        float v2 = velocityLocal.LengthSquared();
        if (v2 < 1e-6f) { force *= dm; torque *= dm; return; }
        var t = velocityLocal / MathF.Sqrt(v2);
        Vector3 drag = Vector3.Dot(force, t) * t, lift = force - drag;
        float d = drag.Length(), l = lift.Length();
        force = drag * dm + lift * lm;
        torque *= d + l > 1e-6f ? (d * dm + l * lm) / (d + l) : dm;
    }

    /// <summary>Scale's factor on the torque for this (unscaled) force - its drag and lift parts' weights (2026-10-17: the
    /// structure scales each block component's moment by the total's, StructureServer.BlockLoads).</summary>
    internal static float TorqueScale(Vector3 force, Vector3 velocityLocal)
    {
        float dm = DragMultiplier, lm = LiftMultiplier;
        float v2 = velocityLocal.LengthSquared();
        if (v2 < 1e-6f) return dm;
        var t = velocityLocal / MathF.Sqrt(v2);
        Vector3 drag = Vector3.Dot(force, t) * t, lift = force - drag;
        float d = drag.Length(), l = lift.Length();
        return d + l > 1e-6f ? (d * dm + l * lm) / (d + l) : dm;
    }

    /// <summary>Scale on a PART of a total (a block component's force at its point, its moment about that point - the
    /// structure's, 2026-10-17), so the scaled parts add up to the scaled total: the force's drag part times DragMultiplier,
    /// its lift part times LiftMultiplier (linear in the force); its moment about the centre of mass com times the TOTAL's
    /// torque factor torqueScale (TorqueScale of the total force), written back about the point.</summary>
    internal static void ScalePart(ref Vector3 force, ref Vector3 moment, Vector3 point, Vector3 com, float torqueScale, Vector3 velocityLocal)
    {
        float dm = DragMultiplier, lm = LiftMultiplier;
        var mc = Vector3.Cross(point - com, force) + moment;
        float v2 = velocityLocal.LengthSquared();
        if (v2 < 1e-6f) force *= dm;
        else
        {
            var t = velocityLocal / MathF.Sqrt(v2);
            Vector3 drag = Vector3.Dot(force, t) * t;
            force = drag * dm + (force - drag) * lm;
        }
        moment = mc * torqueScale - Vector3.Cross(point - com, force);
    }
}

/// <summary>
/// Tuning constants for the aerodynamics integration.
/// </summary>
public static class AeroConfig
{
    /// <summary>Simulation timestep (60 Hz): AeroPhysics.Dt.</summary>
    public const float Dt = AeroPhysics.Dt;

    /// <summary>Below this speed (m/s), skip aero computation entirely.</summary>
    public const float MinSpeed = 0.5f;

    /// <summary>Below this density (kg/m³), treat as vacuum.</summary>
    public const float MinDensity = 1e-6f;

    /// <summary>Large grid block size in meters (10×10×10 cells at 0.25m each).</summary>
    public const float LargeBlockSize = 2.5f;

    /// <summary>Small grid block size in meters (2×2×2 cells at 0.25m each).</summary>
    public const float SmallBlockSize = 0.5f;

    /// <summary>Total cell budget per tick shared across all grids doing staggered rebuilds.</summary>
    public const int GlobalCellBudget = 8000;

    /// <summary>Max grids allowed to run staggered rebuilds concurrently.</summary>
    public const int MaxConcurrentRebuilds = 2;
}
