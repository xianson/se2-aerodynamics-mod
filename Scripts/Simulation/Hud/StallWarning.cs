#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>The stall warning's settings (AeroSettings: Hud.StallWarning).</summary>
public static class StallWarning
{
    public static bool Enabled = true;
    /// <summary>A state must hold this long (s) before it shows, and clears only after this long too (no flicker).</summary>
    public static float Hold = 0.3f;
    /// <summary>Below this airspeed (m/s) or air density (kg/m3): no warning.</summary>
    public static float MinSpeed = 20f, MinDensity = 0.01f;
    // diagnostics (harness aeroget)
    public static float LastMs; public static string Last = "-";
}

/// <summary>
/// THE STALL WARNING (2026-10-13, the user: "we need a stall warning"; "not every ship has wings - wings are not to be
/// trusted for an entire mechanic"). The whole ship's air force, any shape (Lift/LiftPeak: the best lift within reach,
/// -15 to +60 deg from where it flies): stalled when the best is behind and the lift now clearly short of it - pulling
/// further only loses lift; approaching when the best is just ahead or the lift is on its flat top. In the structure's
/// background evaluation (the player's grid only, a few times a second): DesignProbe.ForcesAt, ~55 lookups of the force
/// table and the wings' model. Also: the share of the best lift in use now, and the g available (the best lift over the
/// weight).
/// </summary>
public partial class AeroGridComponent
{
    /// <summary>0 none, 1 approaching, 2 stalled (each held StallWarning.Hold); the best lift's angle from now (deg,
    /// negative: behind), the share of it in use, and the g it would give.</summary>
    internal int StallState;
    internal float StallMarginDeg, StallLiftInUse, StallGAvailable;
    int _stallRaw; float _stallHeld;

    void EvaluateStall(StructSnap snap)
    {
        var L = snap.L;
        float speed = L.AirVel.Length();
        int raw = 0;
        // (also while the autopilot flies, whatever the HUD warning's setting: its protection reads it)
        if ((StallWarning.Enabled || ApFlying) && speed > StallWarning.MinSpeed && snap.Density > StallWarning.MinDensity && snap.Weight > 0f)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var v = L.AirVel / speed;
            float mach = speed / MathF.Max(snap.SoS, 1f);
            var com = L.AeroCom;
            Vector3 F(Vector3 dir) => DesignProbe.ForcesAt(this, dir, mach, snap.Density, snap.SoS, com, out var f, out _, out _) ? f : Vector3.Zero;
            var l = LiftPeak.LiftDir(v, F(v), snap.Up);
            // (every angle the sweep looks at recorded - lift and drag: the autopilot's flight performance, FlightPerf - into the back
            //  buffer, published by a swap; the sweep's own evaluations, none added)
            var ps = ApPerfBack; ps.Clear(); ps.Flow = v; ps.LiftDir = l; ps.Speed = speed; ps.Density = snap.Density;
            LiftPeak.Find(t => { var ft = F(LiftPeak.Air(v, l, t)); ps.Record(t, ft, v, l); return Vector3.Dot(ft, LiftPeak.Normal(v, l, t)); }, out float tp, out float lp, out float l0);
            ps.Sort(); ApPerfBack = ApPerfFront; ApPerfFront = ps; ApPerf = ps; StallFlowDir = v;
            raw = LiftPeak.State(tp, lp, l0, 0.05f * snap.Weight);
            StallMarginDeg = tp * 180f / MathF.PI;
            StallLiftInUse = lp > 0f ? l0 / lp : 0f;
            StallGAvailable = lp / snap.Weight;
            StallLiftDir = l; System.Threading.Volatile.Write(ref StallStamp, System.Diagnostics.Stopwatch.GetTimestamp());   // (the autopilot's alpha protection: EnvelopeGuard)
            // (the flight performance for the API - AeroApi.TryGetFlightPerformance: the same arithmetic the autopilot reads, at the
            //  flow of this evaluation; two boxes swapped, no garbage)
            var ss = new ApSense { Mass = snap.Mass, GravityTrue = snap.GravityTrue, Gravity = snap.GravityTrue, ThrustPos = snap.ThrustPos, ThrustNeg = snap.ThrustNeg,
                StallFresh = true, StallMarginDeg = StallMarginDeg, StallLiftInUse = StallLiftInUse, StallG = StallGAvailable, LiftDir = l, StallFlow = v, AirVel = L.AirVel };
            var box = PerfBoxBack; box.P = FlightPerf.Evaluate(ps, in ss); box.Speed = speed; box.Stamp = System.Diagnostics.Stopwatch.GetTimestamp();
            PerfBoxBack = PerfBoxNow; PerfBoxNow = box;
            StallWarning.LastMs = (float)System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            StallWarning.Last = $"{Entity?.DebugName}: state {raw}, best lift {StallMarginDeg:+0.0;-0.0} deg away, using {StallLiftInUse * 100f:0}%, {StallGAvailable:0.0} g available ({StallWarning.LastMs:F1} ms)";
        }
        if (raw != _stallRaw) { _stallRaw = raw; _stallHeld = 0f; }
        else _stallHeld += snap.Dt;
        if (_stallHeld >= StallWarning.Hold || raw == StallState) StallState = raw;
    }
}
