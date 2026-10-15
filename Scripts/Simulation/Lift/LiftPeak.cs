#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// THE WHOLE SHIP'S STALL - pure (AeroBench 'stall' checks it against a fine sweep). Any shape: the whole grid's air
/// force (its hull's table and whatever lifts on it), no wings needed or trusted.
///
/// With the air along v (unit, the way the ship moves through it, grid-local) and its lift along L (unit, across v), the
/// airflow tilted theta further "nose up" is v(theta) = cos v - sin L and the lift direction with it
/// n(theta) = sin v + cos L; lift(theta) = F(v(theta)) . n(theta).
/// The best lift WITHIN REACH: over a window of angles from Lo below to Hi above where the ship flies (Step apart, then
/// golden-section refinement round the best). A blocky hull's lift curve is ragged - the Blue Fighter's total lift
/// falls 20% from -4 to 0 deg on its shading alone - so no local slope or "first peak" is trustworthy; the best in the
/// window is. Stalled: the best is behind (a lower angle) and the lift now fell clearly short of it - pulling further
/// only loses lift. Approaching: the best is just ahead, or the lift now is on its flat top.
/// The window reaches 60 deg up: a blocky ship's whole lift - hull and all - goes on rising like a flat plate's to ~45
/// deg (the Blue Fighter's and the Pelican's did, past their wings' own stall at ~10-15), so its real peak is that far.
/// 1.5 deg apart: a winged ship's sharp peak (the Pelican's at +9.5 deg, ~2 deg wide) falls between 2.5-deg samples.
/// </summary>
public static class LiftPeak
{
    public static float Lo = -15f * MathF.PI / 180f, Hi = 60f * MathF.PI / 180f, Step = 1.5f * MathF.PI / 180f;
    public static int Refine = 5;

    /// <summary>The lift direction for travel v and the force F there: F's part across v; none to speak of (along the
    /// air, a shape with no lift there), the given up's part across v.</summary>
    public static Vector3 LiftDir(Vector3 v, Vector3 force, Vector3 up)
    {
        var l = force - Vector3.Dot(force, v) * v;
        if (l.LengthSquared() > 1e-6f * MathF.Max(force.LengthSquared(), 1f)) return Vector3.Normalize(l);
        var u = up - Vector3.Dot(up, v) * v;
        if (u.LengthSquared() < 1e-6f) u = MathF.Abs(v.Y) < 0.9f ? Vector3.UnitY - v.Y * v : Vector3.UnitX - v.X * v;
        return Vector3.Normalize(u);
    }

    public static Vector3 Air(Vector3 v, Vector3 l, float theta) => v * MathF.Cos(theta) - l * MathF.Sin(theta);
    public static Vector3 Normal(Vector3 v, Vector3 l, float theta) => v * MathF.Sin(theta) + l * MathF.Cos(theta);

    /// <summary>The best lift within reach: its angle (rad from now, in [Lo, Hi]) and lift (N), and the lift now.</summary>
    public static void Find(Func<float, float> lift, out float thetaPeak, out float liftPeak, out float liftNow)
    {
        float l0 = liftNow = lift(0f);
        float best = l0, bestT = 0f;
        int n = (int)MathF.Round((Hi - Lo) / Step);
        for (int i = 0; i <= n; i++)
        {
            float t = Lo + i * Step;
            if (MathF.Abs(t) < 1e-5f) continue;
            float f = lift(t);
            if (f > best) { best = f; bestT = t; }
        }
        float a = MathF.Max(Lo, bestT - Step), b = MathF.Min(Hi, bestT + Step);
        const float G = 0.618034f;
        float c = b - G * (b - a), d = a + G * (b - a);
        float fc = lift(c), fd = lift(d);
        for (int k = 1; k < Refine; k++)
        {
            if (fc > fd) { b = d; d = c; fd = fc; c = b - G * (b - a); fc = lift(c); }
            else { a = c; c = d; fc = fd; d = a + G * (b - a); fd = lift(d); }
        }
        if (fc > best) { best = fc; bestT = c; }
        if (fd > best) { best = fd; bestT = d; }
        thetaPeak = bestT; liftPeak = best;
    }

    /// <summary>The stall state from Find's answer: 2 stalled (the best behind, by more than BehindDeg, and the lift now
    /// short of it by more than Tol), 1 approaching (the best within AheadDeg ahead, or the lift now within Tol of it with
    /// it behind), 0 neither. 0 too when the best is no real lift (minLift N: a ship flying edge-on, or barely moving).</summary>
    public static float BehindDeg = 1f, AheadDeg = 4f, Tol = 0.03f;
    public static int State(float thetaPeak, float liftPeak, float liftNow, float minLift)
    {
        if (liftPeak < minLift) return 0;
        float deg = thetaPeak * 180f / MathF.PI;
        bool shortOfIt = liftNow < liftPeak * (1f - Tol);
        if (deg < -BehindDeg) return shortOfIt ? 2 : 1;
        return deg < AheadDeg ? 1 : 0;
    }
}
