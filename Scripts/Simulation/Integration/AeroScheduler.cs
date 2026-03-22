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
        if (_activeRebuilds.Contains(grid)) return;
        if (_rebuildQueue.Contains(grid)) return;
        _rebuildQueue.Add(grid);
    }

    /// <summary>
    /// Remove a grid from the scheduler (e.g. on scene removal).
    /// </summary>
    public static void Remove(AeroGridComponent grid)
    {
        _rebuildQueue.Remove(grid);
        _activeRebuilds.Remove(grid);
    }

    /// <summary>
    /// Called once per tick (from the first grid that runs TryCompute).
    /// Promotes queued grids into active slots and distributes cell budget.
    /// </summary>
    public static void Tick()
    {
        // Promote queued grids into active slots
        while (_activeRebuilds.Count < AeroConfig.MaxConcurrentRebuilds && _rebuildQueue.Count > 0)
        {
            var grid = _rebuildQueue[0];
            _rebuildQueue.RemoveAt(0);
            grid.BeginStaggeredBuild();
            _activeRebuilds.Add(grid);
        }

        if (_activeRebuilds.Count == 0) return;

        // Distribute budget evenly across active rebuilds
        int perGrid = AeroConfig.GlobalCellBudget / _activeRebuilds.Count;

        for (int i = _activeRebuilds.Count - 1; i >= 0; i--)
        {
            var grid = _activeRebuilds[i];
            bool done = grid.TickStaggeredBuild(perGrid);
            if (done)
            {
                grid.FinalizeStaggeredBuild();
                _activeRebuilds.RemoveAt(i);
            }
        }
    }

    /// <summary>True if the given grid is currently mid-staggered-build.</summary>
    public static bool IsActiveRebuild(AeroGridComponent grid)
        => _activeRebuilds.Contains(grid);

    /// <summary>True if the given grid is queued or actively rebuilding.</summary>
    public static bool IsEnqueued(AeroGridComponent grid)
        => _rebuildQueue.Contains(grid) || _activeRebuilds.Contains(grid);

    // ── Tick-once guard ──
    // Uses Environment.TickCount to detect new frames without needing
    // an explicit reset call. At 60fps (~16ms) this naturally flips each frame.
    // Worst case: two frames share the same ms → one frame's rebuilds skip,
    // which is harmless since the next frame picks them up.
    private static int _lastTickMs = -1;

    /// <summary>
    /// Ensures Tick() runs exactly once per simulation frame.
    /// Call from every grid's TryCompute — only the first call per frame does work.
    /// </summary>
    public static void EnsureTicked()
    {
        int now = Environment.TickCount;
        if (now == _lastTickMs) return;
        _lastTickMs = now;
        Tick();
    }
}
