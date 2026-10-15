#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// DEV: the harness's reads of the SERVER blocks, done on the server's own step (2026-10-15 scene audit). The surface /
/// gear / airbrake summaries (aeroget AeroSurfaceSettingsComponent.Summary, AeroGearComponent.Summary,
/// AeroAirbrakeComponent.Summary) and the panel probe's "the server settings nearest this client block"
/// (AeroPanelProbe) walked the server components and read their entities (power, transforms, wheels) from the caller's
/// thread - the harness's and the client's: the cross-thread read the game's DataGuard asserts on. Now a read asks, the
/// next server grid step (AeroSimJobCore: one read while nothing is asked) answers, and the caller reads the answer: a
/// summary is the one the server built after the previous ask (the first: "asked" - read it again), the nearest
/// settings come back to the probe a step later.
/// </summary>
internal static class ServerDevReads
{
    /// <summary>Non-zero: something asked (the server step's one read).</summary>
    internal static volatile int Pending;

    public const int Surfaces = 0, Gears = 1, Airbrakes = 2;
    const string Asked = "asked: the server step builds it - read it again";
    static readonly int[] _want = new int[3];
    static readonly string[] _last = { Asked, Asked, Asked };

    /// <summary>Any thread: the summary the server step built last (asked again now).</summary>
    internal static string Summary(int which)
    {
        System.Threading.Volatile.Write(ref _want[which], 1); Pending = 1;
        return System.Threading.Volatile.Read(ref _last[which]);
    }

    // ── the panel probe's nearest server settings ──
    static Vector3D _nearAt; static int _nearWant, _nearDone;
    static bool _nearFound; static float _nearMax; static string _nearWhat;

    /// <summary>The probe (client): find the server settings within 2 m of this point (the client block's: its own read).</summary>
    internal static void AskNear(Vector3D at)
    {
        _nearAt = at; System.Threading.Volatile.Write(ref _nearDone, 0);
        System.Threading.Volatile.Write(ref _nearWant, 1); Pending = 1;
    }

    /// <summary>The probe: the answer, once the server step has given it (found: a settings component near; its max
    /// deflection and its Describe()).</summary>
    internal static bool NearAnswer(out bool found, out float maxDeflection, out string what)
    {
        found = false; maxDeflection = 0f; what = null;
        if (System.Threading.Volatile.Read(ref _nearDone) == 0) return false;
        found = _nearFound; maxDeflection = _nearMax; what = _nearWhat;
        return true;
    }

    /// <summary>Server, a grid's own step: whatever was asked (each by one step: the asks are taken).</summary>
    internal static void Step()
    {
        if (Pending == 0) return;
        Pending = 0;
        if (System.Threading.Interlocked.Exchange(ref _want[Surfaces], 0) != 0) System.Threading.Volatile.Write(ref _last[Surfaces], AeroSurfaceSettingsComponent.BuildSummary());
        if (System.Threading.Interlocked.Exchange(ref _want[Gears], 0) != 0) System.Threading.Volatile.Write(ref _last[Gears], AeroGearComponent.BuildSummary());
        if (System.Threading.Interlocked.Exchange(ref _want[Airbrakes], 0) != 0) System.Threading.Volatile.Write(ref _last[Airbrakes], AeroAirbrakeComponent.BuildSummary());
        if (System.Threading.Interlocked.Exchange(ref _nearWant, 0) != 0)
        {
            // (AeroSurfaceSettingsComponent.All: the server prefabs' only - this scene's)
            var at = _nearAt; AeroSurfaceSettingsComponent best = null; double bd = 2.0;
            lock (AeroSurfaceSettingsComponent.All)
                foreach (var s in AeroSurfaceSettingsComponent.All)
                {
                    if (s.Entity == null) continue;
                    double d = (s.Entity.Data.GetWorldTransform().Position - at).Length();
                    if (d < bd) { bd = d; best = s; }
                }
            _nearFound = best != null; _nearMax = best?.MaxDeflection ?? 0f; _nearWhat = best?.Describe();
            System.Threading.Volatile.Write(ref _nearDone, 1);
        }
    }
}
