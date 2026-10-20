#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// STALL PROTECTION IN MANUAL FLIGHT (2026-10-19, the flight assist Assists.StallProtection - off by default): the PILOT's own
/// target attitude (the reticle the cockpit writes) governed by the autopilot's envelope protection (EnvelopeGuard: the stall
/// margin, the g limit, the predicted loss of control) before the aero-aware law flies it - fly-by-wire style. Honest: it only
/// changes the attitude asked for, never a force or a torque; the ship's real controls fly what is left. The limits are the
/// world's autopilot caps (Autopilot.AoaMarginDeg, Autopilot.GLimit); protection always on while it runs.
/// The path's own turn (pathRate) is measured from the velocity as the flight director measures it. Engine-free
/// (Tests/AutopilotSim: Scenarios/AssistPack); one per grid; allocation-free.
/// </summary>
public sealed class PilotProtection
{
    public readonly EnvelopeGuard Guard = new();
    /// <summary>What clipped the pilot's target this tick (None: it flew as asked).</summary>
    public ApProt Prot { get; private set; }
    /// <summary>What the readout shows: the last clip, held ShowHold s after it ends (a clip a tick at a time still reads).</summary>
    public ApProt Shown => _hold > 0f ? _shown : ApProt.None;
    /// <summary>The readout's hold after the last clip (s).</summary>
    public const float ShowHold = 0.75f;

    Vector3 _vPrev, _aMeas; bool _hasV; float _hold; ApProt _shown;

    public void Reset() { Guard.Reset(); _hasV = false; _aMeas = Vector3.Zero; _hold = 0f; _shown = ApProt.None; Prot = ApProt.None; }

    /// <summary>The pilot's target governed (s: the instruments now, as the autopilot's - its stall and loss-of-control
    /// evaluations for this target as the probe).</summary>
    public Quaternion Step(in ApSense s, Quaternion pilotTarget)
    {
        float dt = s.Dt > 0f ? s.Dt : 1f / 60f;
        // (the path's turn: the measured acceleration across the velocity over the speed - FlightDirector's)
        if (_hasV) _aMeas += ((s.Vel - _vPrev) / dt - _aMeas) * MathF.Min(1f, dt / 0.3f);
        _vPrev = s.Vel; _hasV = true;
        float vs = s.Vel.Length();
        Vector3 pathRate = vs > 5f ? Vector3.Cross(s.Vel, ApMath.Flat(_aMeas, s.Vel / vs)) / (vs * vs) : Vector3.Zero;
        ApSense g = s; g.Lim.Protection = true;
        ApProt p = ApProt.None;
        Quaternion q = Guard.Govern(in g, pilotTarget, ref p, pathRate);
        Prot = p;
        if (p != ApProt.None) { _shown = p; _hold = ShowHold; } else _hold -= dt;
        return q;
    }
}
