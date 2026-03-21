#pragma warning disable
namespace AeroMod;

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
