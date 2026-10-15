using System;
using AeroMod;

#pragma warning disable
static partial class Program
{
    /// <summary>
    /// ROLL RESPONSE (2026-10-14, the user: "the control surfaces respond eventually but are slow, esp. on Q/E"): one axis,
    /// 60 Hz, the mod's attitude law (ThrustAllocator.AttitudeAccel - the game's code, compiled in) asking for I x its
    /// acceleration, delivered by gyros (up to G) and the control surfaces (up to S x delivered share: the surfaces' real
    /// torque may fall short of what the law was told). OLD: the law plans with the gyros' reach alone (it clamps its
    /// acceleration to it - the surfaces only ever took a share of that). NEW: gyros + surfaces (ThrustTorque.AwareNeed adds
    /// SurfacePush/Pull). Cases: a 30 deg step; Q held (the target running ahead at a constant rate) - time to 30 deg, the
    /// rate after 1 s, overshoot past the step, settle (under 0.1 deg and 0.005 rad/s).
    /// </summary>
    /// <summary>The share of the surfaces' reach the law is told (ThrustTorque.SurfaceReachShare).</summary>
    static float SurfaceShare => 0.75f;   // (= ThrustTorque.SurfaceReachShare - the bench does not compile ThrustTorque)

    static int RollResponse()
    {
        const float dt = 1f / 60f;
        float I = 4.0e5f;                       // kg m2 about roll (a ~40 t plane)
        float G = 1.0e5f;                       // gyros, N m
        int fails = 0;
        Console.WriteLine($"   roll inertia {I:E2} kg m2, gyros {G / 1e3f:F0} kN m; law gain {ThrustAllocator.LinearGain}/s, rate time {ThrustAllocator.RateTime} s, margin {ThrustAllocator.Margin}");
        foreach (float sRatio in new[] { 2f, 5f })
        foreach (float delivered in new[] { 1.0f, 0.8f, 0.6f })
        {
            float S = sRatio * G;
            Console.WriteLine($"   surfaces {S / 1e3f:F0} kN m ({sRatio}x gyros), delivering {delivered * 100:F0}% of what the law is told:");
            foreach (bool aware in new[] { false, true })
            {
                var (t30, overshoot, settle) = Step(I, G, S, delivered, aware, 30f * MathF.PI / 180f, dt);
                var (t30h, rate1) = Held(I, G, S, delivered, aware, 60f * MathF.PI / 180f, dt);
                string tag = aware ? "NEW (gyros+surfaces)" : "OLD (gyros only)    ";
                Console.WriteLine($"     {tag}  step 30 deg: reached in {t30:F2} s, overshoot {overshoot:F2} deg, settled {settle:F2} s | Q held (60 deg/s asked): 30 deg in {t30h:F2} s, rate after 1 s {rate1:F1} deg/s");
                // (pass: no more than 1 deg past the step while the surfaces give at least 80% of their reach - their reach is
                //  the model's own, a tick old; 60% is a stress case, reported)
                if (aware && delivered >= 0.8f && overshoot > 1.0f) { fails++; Console.WriteLine("       FAIL: overshoot over 1 deg"); }
            }
        }
        Console.WriteLine(fails == 0 ? "   roll response: PASS" : $"   roll response: {fails} FAIL");
        return fails;
    }

    // a step of the target: when it first reaches it, how far past it goes, when it settles
    static (float t30, float overshoot, float settle) Step(float I, float G, float S, float delivered, bool aware, float target, float dt)
    {
        float th = 0f, w = 0f, t = 0f, t30 = float.NaN, peak = 0f, settle = float.NaN;
        float reach = (aware ? G + SurfaceShare * S : G) / I;
        for (int k = 0; k < 60 * 8; k++, t += dt)
        {
            var acc = ThrustAllocator.AttitudeAccel(new Vector3(target - th, 0, 0), new Vector3(w, 0, 0), new Vector3(reach, 0, 0), Vector3.Zero, 0f, 0f).X;
            float tau = Apply(I * acc, G, S, delivered);
            w += tau / I * dt; th += w * dt;
            if (float.IsNaN(t30) && th >= target * 0.98f) t30 = t;
            peak = MathF.Max(peak, th);
            bool still = MathF.Abs(target - th) < 0.1f * MathF.PI / 180f && MathF.Abs(w) < 0.005f;
            if (still && float.IsNaN(settle)) settle = t; else if (!still) settle = float.NaN;
        }
        return (t30, (peak - target) * 180f / MathF.PI, settle);
    }

    // Q held: the target runs ahead at the asked rate (the pilot's input) - when it has turned 30 deg, its rate after 1 s
    static (float t30, float rate1) Held(float I, float G, float S, float delivered, bool aware, float askedRate, float dt)
    {
        float th = 0f, w = 0f, t = 0f, tgt = 0f, t30 = float.NaN, rate1 = 0f;
        float reach = (aware ? G + SurfaceShare * S : G) / I;
        for (int k = 0; k < 60 * 6; k++, t += dt)
        {
            tgt += askedRate * dt;
            var acc = ThrustAllocator.AttitudeAccel(new Vector3(tgt - th, 0, 0), new Vector3(w, 0, 0), new Vector3(reach, 0, 0), Vector3.Zero, 0f, 0f).X;
            float tau = Apply(I * acc, G, S, delivered);
            w += tau / I * dt; th += w * dt;
            if (float.IsNaN(t30) && th >= 30f * MathF.PI / 180f) t30 = t;
            if (k == 59) rate1 = w * 180f / MathF.PI;
        }
        return (t30, rate1);
    }

    // the surfaces first (up to S x delivered), the gyros the rest (up to G)
    static float Apply(float want, float G, float S, float delivered)
    {
        float s = Math.Clamp(want, -S, S) * delivered;
        float g = Math.Clamp(want - s, -G, G);
        return s + g;
    }
}
