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
        // GRANTS ONLY. It used to run every queued grid's Begin / Tick / Finalize itself - on whichever grid's job
        // thread got here first, while those grids' own jobs (in parallel) read and swapped the same surfaces, wing lists
        // and component lists. Now each grid does its own build steps in its own job (RunMine), with the slot and the
        // cell budget granted here.
        while (_activeRebuilds.Count < AeroConfig.MaxConcurrentRebuilds && _rebuildQueue.Count > 0)
        {
            int best = 0; float bp = float.MinValue;
            for (int q = 0; q < _rebuildQueue.Count; q++) { float p = _rebuildQueue[q].CachedPriority; if (p > bp) { bp = p; best = q; } }   // (its own job's cached value: never another grid's entity)
            var grid = _rebuildQueue[best];
            _rebuildQueue.RemoveAt(best);
            grid.SchedBegin = true;
            _activeRebuilds.Add(grid);
        }
        if (_activeRebuilds.Count == 0) return;
        int perGrid = AeroConfig.GlobalCellBudget / _activeRebuilds.Count;
        foreach (var grid in _activeRebuilds) System.Threading.Interlocked.Exchange(ref grid.SchedGrant, perGrid);
    }

    /// <summary>A grid's own build steps, in its own job: begin when granted a slot, then its granted cells this frame,
    /// then finalize. (Each grid's job calls this; the work is that grid's only.)</summary>
    internal static void RunMine(AeroGridComponent grid)
    {
        if (grid.SchedBegin) { grid.SchedBegin = false; grid.BeginStaggeredBuild(); }
        int cells = System.Threading.Interlocked.Exchange(ref grid.SchedGrant, 0);
        if (cells <= 0) return;
        if (grid.TickStaggeredBuild(cells))
        {
            grid.FinalizeStaggeredBuild();
            lock (_lock) _activeRebuilds.Remove(grid);
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
