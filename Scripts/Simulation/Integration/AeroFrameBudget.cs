#pragma warning disable
using System;

namespace AeroMod;

/// <summary>
/// The most aero may take of a frame on the simulation thread (BudgetMs). Grids whose forces are due once it is
/// spent use their last ones again (as between LOD recomputes) - except piloted or fast grids, always computed, and
/// any grid that has waited MaxSkips frames in a row. Many grids then cost at most the budget, whatever their number.
/// </summary>
public static class AeroFrameBudget
{
    public static double BudgetMs = 1.0;
    public const int MaxSkips = 8;
    static long _frameStart, _spent;
    static readonly long _frameGap = System.Diagnostics.Stopwatch.Frequency * 10 / 1000;
    public static int SkippedThisSecond, ComputedThisSecond;

    /// <summary>Called at each grid's job: a new frame starts the count again.</summary>
    public static void Touch()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now - System.Threading.Interlocked.Read(ref _frameStart) > _frameGap) { System.Threading.Interlocked.Exchange(ref _frameStart, now); System.Threading.Interlocked.Exchange(ref _spent, 0); }
    }

    public static bool Spent => System.Threading.Interlocked.Read(ref _spent) > (long)(BudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);

    public static void Add(long ticks) => System.Threading.Interlocked.Add(ref _spent, ticks);
}
