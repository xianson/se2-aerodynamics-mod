using System;
#pragma warning disable
namespace AeroMod;

/// <summary>
/// Global scheduler that prevents multiple grids from doing expensive
/// surface rebuilds on the same tick. Grids enqueue themselves when dirty;
/// the scheduler grants cell budget round-robin each tick.
/// </summary>
public static class AeroScheduler
{
    // ── Rebuild queue (grids waiting for a staggered build slot) ──
    private static readonly List<AeroGridComponent> _rebuildQueue = new();

    // ── Currently active rebuilds (grids mid-staggered-build) ──
    private static readonly List<AeroGridComponent> _activeRebuilds = new();

    /// <summary>
    /// Enqueue a grid for staggered rebuild. No-op if already queued or active.
    /// </summary>
    public static void EnqueueRebuild(AeroGridComponent grid)
    {
        lock (_lock)
        {
            if (_activeRebuilds.Contains(grid)) return;
            if (_rebuildQueue.Contains(grid)) return;
            _rebuildQueue.Add(grid);
        }
    }

    // Grids' jobs run in parallel: every touch of the queues goes through this lock.
    private static readonly object _lock = new();
    /// <summary>The most a frame's rebuild work may take (ms); the rest waits for the next frame.</summary>
    public const double FrameBudgetMs = 2.0;

    /// <summary>
    /// Remove a grid from the scheduler (e.g. on scene removal).
    /// </summary>
    public static void Remove(AeroGridComponent grid)
    {
        lock (_lock) { _rebuildQueue.Remove(grid); _activeRebuilds.Remove(grid); }
    }

    /// <summary>
    /// Called once per tick (from the first grid that runs TryCompute).
    /// Promotes queued grids into active slots and distributes cell budget.
    /// </summary>
    public static void Tick()
    {
        lock (_lock) TickLocked();
    }

    private static void TickLocked()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        // Promote queued grids into active slots
        while (_activeRebuilds.Count < AeroConfig.MaxConcurrentRebuilds && _rebuildQueue.Count > 0)
        {
            int best = 0; float bp = float.MinValue;
            for (int q = 0; q < _rebuildQueue.Count; q++) { float p = _rebuildQueue[q].BuildPriority; if (p > bp) { bp = p; best = q; } }
            var grid = _rebuildQueue[best];
            _rebuildQueue.RemoveAt(best);
            grid.BeginStaggeredBuild();
            _activeRebuilds.Add(grid);
        }

        if (_activeRebuilds.Count == 0) return;

        // Distribute budget evenly across active rebuilds
        int perGrid = AeroConfig.GlobalCellBudget / _activeRebuilds.Count;

        for (int i = _activeRebuilds.Count - 1; i >= 0; i--)
        {
            if ((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency > FrameBudgetMs) break;
            var grid = _activeRebuilds[i];
            bool done = grid.TickStaggeredBuild(perGrid);
            if (done)
            {
                grid.FinalizeStaggeredBuild();
                _activeRebuilds.RemoveAt(i);
            }
        }
    }



    // ── Tick-once guard ──
    // At most once per 15 ms (one 60 Hz frame). It used Environment.TickCount (1 ms resolution): with ~90 grids'
    // jobs spread over a frame, it ticked about once a MILLISECOND, each tick granting the whole cell budget, so a
    // frame's rebuild work multiplied (17.6 ms frames on the Red Ship).
    private static long _lastTick;
    private static readonly long _minGap = System.Diagnostics.Stopwatch.Frequency * 15 / 1000;

    /// <summary>Call from every grid's TryCompute: only the first call in a frame does work.</summary>
    public static void EnsureTicked()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp(), last = System.Threading.Interlocked.Read(ref _lastTick);
        if (now - last < _minGap) return;
        if (System.Threading.Interlocked.CompareExchange(ref _lastTick, now, last) != last) return;
        Tick();
    }
}
