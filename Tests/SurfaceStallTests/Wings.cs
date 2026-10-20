#pragma warning disable
using AeroMod;

// 4b. THE FLIGHT ASSISTS ON A WING (2026-10-19): a flat plate wing of blocks (10 m span, 3 m chord) through the real detector and
// the real wing model (CompressibleWingModel, the whole LiftingSurfaceModel), swept over the angle of attack at 100 m/s:
//  - every assist at 1: the force bit-identical to the defaults' at every angle;
//  - Assists.StallAngleScale 1.3: the best-lift angle later, its lift higher; the attached part under the old stall the same;
//  - Assists.PostStallLift 1.5: more lift past the stall, never more than at the stall; under it the same.
static partial class Program
{
    static void WingAssists()
    {
        var boxes = new List<(Vector3I, Vector3I)> { (new Vector3I(0, 0, 0), new Vector3I(19, 0, 5)) };   // (span X, chord Z, normal Y)
        var angles = new List<float>(); for (float a = 0f; a <= 50f; a += 0.5f) angles.Add(a);
        // the ship's velocity at angle of attack a: forward along -Z, sinking - the air from below
        Vector3 Vel(float a) => new Vector3(0f, -MathF.Sin(a * D2R), -MathF.Cos(a * D2R)) * 100f;
        Vector3[] Sweep()
        {
            var s = Build("plate wing", boxes);
            s.Com = new Vector3(5f, 0f, 1.5f);
            var f = new Vector3[angles.Count];
            for (int i = 0; i < angles.Count; i++)
            {
                AeroResult r = default;
                for (int k = 0; k < 3; k++) r = s.Model.Compute(Ctx(s, Vel(angles[i])));
                f[i] = r.Force;
            }
            return f;
        }
        float Lift(Vector3 f, float a) { var v = Vector3.Normalize(Vel(a)); var up = Vector3.Normalize(Vector3.Cross(Vector3.UnitX, -v)); if (up.Y < 0f) up = -up; return Vector3.Dot(f, up); }
        (float at, float best) PeakOf(Vector3[] f) { float best = float.MinValue, at = 0f; for (int i = 0; i < f.Length; i++) { float l = Lift(f[i], angles[i]); if (l > best) { best = l; at = angles[i]; } } return (at, best); }
        float LiftAt(Vector3[] f, float deg) { int i = angles.IndexOf(deg); return i >= 0 ? Lift(f[i], deg) : float.NaN; }

        var baseF = Sweep();
        var (a0, l0) = PeakOf(baseF);
        // every assist explicitly at 1 (realistic): bit-identical
        FlightAssists.StallAngleScale = 1f; FlightAssists.PostStallLift = 1f; FlightAssists.SurfaceAuthority = 1f;
        var oneF = Sweep(); int diff = 0;
        for (int i = 0; i < baseF.Length; i++) if (baseF[i].X != oneF[i].X || baseF[i].Y != oneF[i].Y || baseF[i].Z != oneF[i].Z) diff++;
        Check(diff == 0, $"wing assists at 1: the force bit-identical at every angle ({diff} of {baseF.Length} differ)");
        // the stall angle x1.3
        FlightAssists.StallAngleScale = 1.3f;
        var stallF = Sweep();
        FlightAssists.StallAngleScale = 1f;
        var (a1, l1) = PeakOf(stallF);
        float under = MathF.Floor(a0 * 0.5f);
        Check(a1 >= a0 * 1.15f && a1 <= a0 * 1.5f && l1 > l0 * 1.05f, $"wing stall angle x1.3: best lift at {a1:F1} deg (was {a0:F1}), {l1 / 1e3f:F1} kN (was {l0 / 1e3f:F1})");
        Check(MathF.Abs(LiftAt(stallF, under) - LiftAt(baseF, under)) <= 1e-3f * MathF.Abs(LiftAt(baseF, under)) + 1f, $"wing stall angle x1.3: the attached lift at {under:F0} deg unchanged");
        // the post-stall lift x1.5
        FlightAssists.PostStallLift = 1.5f;
        var postF = Sweep();
        FlightAssists.PostStallLift = 1f;
        float past = MathF.Min(50f, MathF.Round(a0 + 15f)), lp = LiftAt(postF, past), lb = LiftAt(baseF, past);
        var (a2, l2) = PeakOf(postF);
        Check(lp > lb * 1.1f && lp <= l0 * 1.02f, $"wing post-stall lift x1.5: at {past:F0} deg {lp / 1e3f:F1} kN (was {lb / 1e3f:F1}; the stall's {l0 / 1e3f:F1})");
        Check(LiftAt(postF, under) == LiftAt(baseF, under), $"wing post-stall lift x1.5: the attached lift at {under:F0} deg bit-identical");
        Console.WriteLine($"\n== 4b. wing assists (10 x 3 m plate, 100 m/s): best lift {l0 / 1e3f:F1} kN at {a0:F1} deg; stall angle x1.3: {l1 / 1e3f:F1} kN at {a1:F1} deg; post-stall lift x1.5: at {past:F0} deg {lb / 1e3f:F1} -> {lp / 1e3f:F1} kN; all at 1: bit-identical");
    }
}
