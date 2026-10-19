#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// THE SHIP'S LIFT AND DRAG over the angle of attack, at the speed and air now - taken from the mod's own whole-ship model (the
/// stall evaluation's sweep: DesignProbe.ForcesAt in game - the force table's faces and the wings, Mach and density in -; the
/// dumped ships' polars offline), relative to the flow it was taken at (theta: further nose-up, rad). Filled by one thread, then
/// published whole (a reference swap) and read-only after: no allocation once made.
/// </summary>
public sealed class PerfSamples
{
    public const int Max = 96;
    public readonly float[] Theta = new float[Max], Lift = new float[Max], Drag = new float[Max];
    public int Count;
    /// <summary>The flow (local unit, the way the ship moved through the air), the lift direction (local unit), speed (m/s), density.</summary>
    public Vector3 Flow, LiftDir; public float Speed, Density;

    public void Clear() { Count = 0; }

    /// <summary>The force F (local N) at the flow turned theta nose-up from v along the lift direction l: its lift (across the
    /// turned flow, LiftPeak.Normal) and drag (against it).</summary>
    public void Record(float theta, Vector3 force, Vector3 v, Vector3 l)
    {
        if (Count >= Max) return;
        Vector3 air = v * MathF.Cos(theta) - l * MathF.Sin(theta), nrm = v * MathF.Sin(theta) + l * MathF.Cos(theta);
        Theta[Count] = theta; Lift[Count] = Vector3.Dot(force, nrm); Drag[Count] = -Vector3.Dot(force, air); Count++;
    }

    /// <summary>Sorted by theta (insertion: a few dozen samples, once per evaluation).</summary>
    public void Sort()
    {
        for (int i = 1; i < Count; i++)
        {
            float t = Theta[i], l = Lift[i], d = Drag[i]; int j = i - 1;
            while (j >= 0 && Theta[j] > t) { Theta[j + 1] = Theta[j]; Lift[j + 1] = Lift[j]; Drag[j + 1] = Drag[j]; j--; }
            Theta[j + 1] = t; Lift[j + 1] = l; Drag[j + 1] = d;
        }
    }
}

/// <summary>
/// FLIGHT PERFORMANCE now (read-only, engine-free; FlightPerf.Evaluate): what the ship can do at its speed and air, from its own
/// lift and drag (PerfSamples) and its thrust - for any goal source (fixed-wing modes, navigation, AI) to plan with.
/// Accelerations in m/s2, rates in rad/s, angles in rad from the flow now; NaN / 0 where not known (Valid false).
/// </summary>
public struct ApPerf
{
    public bool Valid;
    /// <summary>The most nose-up the flow may be turned (the stall margin less Autopilot.AoaMarginDeg; the protection's own cap).</summary>
    public float AlphaMax;
    /// <summary>The load (g of the real weight) the lift gives at AlphaMax, the same within Autopilot.GLimit (instantaneous), and
    /// the most it holds with drag no more than the full thrust along the path (sustained).</summary>
    public float NAtAlphaMax, NInst, NSust;
    /// <summary>Level turn rates at those loads (rad/s: g sqrt(n^2 - 1) / V).</summary>
    public float TurnRateInst, TurnRateSust;
    /// <summary>The normal (lateral) acceleration available now (m/s2: NInst x g).</summary>
    public float NormalAccelMax;
    /// <summary>The drag's deceleration now and at AlphaMax (m/s2), and full thrust less the drag now along the path (m/s2).</summary>
    public float DragDecelNow, DragDecelAtMax, ExcessAccel;
}

/// <summary>The flight performance's arithmetic over PerfSamples (engine-free, allocation-free).</summary>
public static class FlightPerf
{
    /// <summary>Lift / drag (N) at theta (rad from the samples' flow), linear between samples, held past the ends.</summary>
    public static float LiftAt(PerfSamples p, float theta) => At(p, p.Lift, theta);
    public static float DragAt(PerfSamples p, float theta) => At(p, p.Drag, theta);
    static float At(PerfSamples p, float[] y, float x)
    {
        int n = p.Count; if (n == 0) return 0f;
        if (x <= p.Theta[0]) return y[0];
        if (x >= p.Theta[n - 1]) return y[n - 1];
        int lo = 0, hi = n - 1;
        while (hi - lo > 1) { int m = (lo + hi) >> 1; if (p.Theta[m] <= x) lo = m; else hi = m; }
        float t0 = p.Theta[lo], t1 = p.Theta[hi], f = t1 > t0 ? (x - t0) / (t1 - t0) : 0f;
        return y[lo] + (y[hi] - y[lo]) * f;
    }

    /// <summary>The smallest theta in [Theta0, thetaMax] whose lift reaches 'lift' (N) - the lift curve walked up from its low end;
    /// thetaMax if it never does.</summary>
    public static float ThetaFor(PerfSamples p, float lift, float thetaMax)
    {
        int n = p.Count; if (n == 0) return 0f;
        float prevT = p.Theta[0], prevL = p.Lift[0];
        if (prevL >= lift) return prevT;
        for (int i = 1; i < n && p.Theta[i - 1] < thetaMax; i++)
        {
            float t = MathF.Min(p.Theta[i], thetaMax), l = LiftAt(p, t);
            if (l >= lift) return prevT + (t - prevT) * Math.Clamp((lift - prevL) / MathF.Max(l - prevL, 1e-3f), 0f, 1f);
            prevT = t; prevL = l;
        }
        return thetaMax;
    }

    /// <summary>The flight performance for the samples and the ship now (s: mass, the gravity it falls with, its thrusters' maxima,
    /// the flow now - the samples' flow turned since: EnvelopeGuard.Since).</summary>
    public static ApPerf Evaluate(PerfSamples p, in ApSense s)
    {
        var r = new ApPerf();
        if (p == null || p.Count < 4 || !(s.Mass > 0f) || p.Speed < 1f) return r;
        float g = (s.GravityTrue.LengthSquared() > 0f ? s.GravityTrue : s.Gravity).Length();
        float weight = s.Mass * MathF.Max(g, 1e-3f);
        float since = EnvelopeGuard.Since(in s);
        float margin = s.StallFresh ? s.StallMarginDeg * ApMath.Deg - since : p.Theta[p.Count - 1];
        float capRel = margin - s.Lim.AoaMarginDeg * ApMath.Deg;   // (from the flow now)
        float thetaMax = since + capRel;                                  // (from the samples' flow)
        r.AlphaMax = capRel;
        float liftMax = LiftAt(p, thetaMax);
        r.NAtAlphaMax = liftMax / weight;
        r.NInst = MathF.Min(r.NAtAlphaMax, s.Lim.GLimit);
        r.NormalAccelMax = r.NInst * g;
        float v = MathF.Max(p.Speed, 1f), q0 = 1f;
        float thrust = ApMath.Along(s.ThrustPos, s.ThrustNeg, p.Flow);
        r.DragDecelNow = DragAt(p, since) / s.Mass; r.DragDecelAtMax = DragAt(p, thetaMax) / s.Mass;
        r.ExcessAccel = (thrust - DragAt(p, since)) / s.Mass;
        // sustained: the highest lift whose drag the full thrust still meets (walked up to the cap)
        float nS = 0f;
        for (int i = 0; i < p.Count && p.Theta[i] <= thetaMax; i++) if (p.Drag[i] <= thrust) nS = MathF.Max(nS, p.Lift[i] / weight);
        r.NSust = MathF.Min(nS, r.NInst);
        r.TurnRateInst = r.NInst > 1f ? g * MathF.Sqrt(r.NInst * r.NInst - 1f) / v : 0f;
        r.TurnRateSust = r.NSust > 1f ? g * MathF.Sqrt(r.NSust * r.NSust - 1f) / v : 0f;
        r.Valid = q0 > 0f;
        return r;
    }

    /// <summary>The speed after 'seconds' (m/s) pulling load n (g) at a throttle (0..1 of the thrust along the path), the path
    /// climbing at sinGamma - the drag at the lift that load needs, scaled with the dynamic pressure as the speed changes.</summary>
    public static float SpeedAfter(PerfSamples p, in ApSense s, float seconds, float n, float throttle, float sinGamma)
    {
        if (p == null || p.Count < 4 || !(s.Mass > 0f)) return float.NaN;
        float g = (s.GravityTrue.LengthSquared() > 0f ? s.GravityTrue : s.Gravity).Length();
        float thrust = ApMath.Along(s.ThrustPos, s.ThrustNeg, p.Flow) * Math.Clamp(throttle, 0f, 1f);
        float v = p.Speed, v0 = MathF.Max(p.Speed, 1f), weight = s.Mass * g;
        const float dt = 0.1f;
        for (float t = 0f; t < seconds; t += dt)
        {
            float qs = v * v / (v0 * v0);   // (forces with the dynamic pressure)
            float theta = ThetaFor(p, n * weight / MathF.Max(qs, 1e-3f), p.Theta[p.Count - 1]);
            float drag = DragAt(p, theta) * qs;
            v = MathF.Max(0f, v + ((thrust - drag) / s.Mass - g * sinGamma) * dt);
        }
        return v;
    }
}
