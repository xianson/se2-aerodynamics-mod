using System.IO;
using AeroMod;

// AeroBench 'stall' (and in the gate): the whole ship's lift peak (Lift/LiftPeak - what the game's stall warning reads)
// - the best lift within reach - against a fine sweep of the same window, on real ships: the best lift within 2%.
//   dotnet run -c Release -- stall [ship ...]
static partial class Program
{
    static int StallCheck(string dataDir, AtmosphereState atmo, string[] names)
    {
        int fails = 0;
        foreach (var name in names)
        {
            var path = Path.Combine(dataDir, name + ".boxes");
            if (!File.Exists(path)) { Console.WriteLine($"   {name}: no data, skipped"); continue; }
            var boxes = LoadBoxes(path);
            var ship = Build(name, boxes);
            var (nose, up) = FlightAxes(ship, atmo, boxes);
            foreach (float alpha0 in new[] { 0f, 6f, 14f, 25f })
            {
                float a = alpha0 * MathF.PI / 180f;
                var v = Vector3.Normalize(nose * MathF.Cos(a) - up * MathF.Sin(a));
                const float V = 150f;
                Vector3 F(Vector3 dir) => ship.Model.Compute(new AeroContext(ship.Grid, ship.Surface, dir * V, atmo, ship.Com, BlockSize, Vector3.Zero, -1f, ship.Manifold)).Force;
                var l = LiftPeak.LiftDir(v, F(v), up);
                float Lift(float t) => Vector3.Dot(F(LiftPeak.Air(v, l, t)), LiftPeak.Normal(v, l, t));
                int evals = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                LiftPeak.Find(t => { evals++; return Lift(t); }, out float tp, out float lp, out float l0);
                double ms = sw.Elapsed.TotalMilliseconds;
                // the reference: the best lift over the same window, every 0.25 deg
                float bl = float.MinValue, bt = 0f;
                for (float dd = LiftPeak.Lo * 180f / MathF.PI; dd <= LiftPeak.Hi * 180f / MathF.PI + 1e-3f; dd += 0.25f) { float t = dd * MathF.PI / 180f, f = Lift(t); if (f > bl) { bl = f; bt = t; } }
                float dDeg = MathF.Abs(tp - bt) * 180f / MathF.PI, dLift = MathF.Abs(lp - bl) / MathF.Max(1f, MathF.Abs(bl));
                int state = LiftPeak.State(tp, lp, l0, 0.05f * MathF.Abs(lp));
                // (on a flat or ragged top two angles can give the same best lift: the lift decides, the angle only where it differs)
                bool ok = dLift < 0.02f;
                if (!ok) fails++;
                Console.WriteLine($"   {(ok ? "ok  " : "FAIL")} {name} at {alpha0:F0} deg: peak {tp * 180f / MathF.PI:+0.0;-0.0} deg from now (sweep {bt * 180f / MathF.PI:+0.0;-0.0}), lift {lp / 1000:F0} kN (sweep {bl / 1000:F0}), now {l0 / 1000:F0} kN, state {state} - {evals} evaluations, {ms:F1} ms");
            }
        }
        Console.WriteLine($"AeroBench: stall peak {(fails == 0 ? "matches the sweep (ok)" : $"FAILED {fails}")}");
        return fails;
    }
}
