global using System.Runtime.CompilerServices;
global using Keen.VRage.Library.Diagnostics;

namespace AeroMod;

/// <summary>Test stand-in for the in-game profiler (Integration/AeroStats.cs uses the engine's stat API).</summary>
public static class AeroStats
{
    public static long Timestamp() => System.Diagnostics.Stopwatch.GetTimestamp();
    public static long ElapsedUs(long t0) => (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;
    public static void SetForce(long us) { }
    public static void SetLift(long us) { }
    public static void SetShadow(long us) { }
}
