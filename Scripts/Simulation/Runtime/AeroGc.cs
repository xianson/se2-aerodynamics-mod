#pragma warning disable
using System.Runtime;

namespace AeroMod;

/// <summary>
/// While a big grid's surface is being built (hundreds of MB, on a worker thread), the runtime is asked for
/// SustainedLowLatency: it then avoids blocking full garbage collections - the game keeps running - and does
/// concurrent ones only (unless memory runs short). Red Ship's first build otherwise drew a 5 s blocking collection.
/// Process-wide, so counted across grids and always restored to the game's own mode.
/// </summary>
public static class AeroGc
{
    /// <summary>A build is big above this many cells (Red Ship: 3 million; the Jetliner: 54 thousand).</summary>
    public const long BigBuildCells = 500_000;

    static readonly object _lock = new();
    static int _depth;
    static GCLatencyMode _saved;

    public static void Enter()
    {
        lock (_lock)
        {
            if (_depth++ == 0)
            {
                _saved = GCSettings.LatencyMode;
                try { GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency; } catch { }
            }
        }
    }

    public static void Exit()
    {
        lock (_lock)
        {
            if (_depth > 0 && --_depth == 0)
                try { GCSettings.LatencyMode = _saved; } catch { }
        }
    }
}
