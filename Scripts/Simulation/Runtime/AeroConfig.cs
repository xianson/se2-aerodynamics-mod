#pragma warning disable
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
}

/// <summary>
/// Tuning constants for the aerodynamics integration.
/// </summary>
public static class AeroConfig
{
    /// <summary>Simulation timestep (60 Hz).</summary>
    public const float Dt = 1f / 60f;

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
