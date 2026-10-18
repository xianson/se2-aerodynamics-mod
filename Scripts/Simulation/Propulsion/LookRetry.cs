#pragma warning disable
namespace AeroMod;

/// <summary>
/// A component's FIRST LOOK, retried (2026-10-17 audit; game-free - Tests/ThrustTests LookRetryChecks): the propulsor, solid nozzle,
/// solid segment and exhaust components read their block's definition row and sibling components once, on their first step - and a
/// look that failed then (the block's definition or its components not readable yet) left the block un-modelled for good: never
/// shaped, never in its motor, no exhaust. Now a failed look is tried again every RetryTicks steps, for as long as it fails (one
/// dictionary lookup each time: the component is injected only into its own kind's prefabs, so it does succeed); a component that
/// leaves the scene looks again when it comes back (Reset).
/// Use: if (!look.Done) { if (!look.Due()) return; ...; look.Done = found; if (!found) return; }
/// </summary>
public struct LookRetry
{
    /// <summary>Steps between two tries of a failed look.</summary>
    public const int RetryTicks = 60;
    /// <summary>The look succeeded: nothing more to do.</summary>
    public bool Done;
    int _wait;

    /// <summary>True: look now (not done yet and the wait since the last failed try is over - the first try at once).</summary>
    public bool Due()
    {
        if (Done) return false;
        if (_wait > 0) { _wait--; return false; }
        _wait = RetryTicks;
        return true;
    }

    /// <summary>Look again from the start (the component left the scene: its block may come back as another).</summary>
    public void Reset() { Done = false; _wait = 0; }
}
