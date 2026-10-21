#pragma warning disable
using AeroMod;

/// <summary>WheelShockTests: the wheel shocks' laws and a time-stepped touchdown, offline (exit 0 = all pass). Prints the comparison
/// table (vanilla / airshock / airshock + oleo) for the vanilla wheel sizes and the Tu-134 Big's share.</summary>
static class Program
{
    static int _checks, _fails;
    static void Check(bool ok, string what) { _checks++; if (!ok) { _fails++; if (_fails <= 40) Console.WriteLine("FAIL " + what); } }
    static bool Near(float a, float b, float tol) => MathF.Abs(a - b) <= tol;

    static WheelRating Vanilla(int row, float scale = 1f)
        => WheelRating.Of(WheelRating.Diameters[row] / 2f, WheelRating.Strokes[row], WheelRating.WheelKg[row], scale);

    static Sim Run(Sim.Kind k, WheelRating r, float mass, float sink, float seconds = 4f)
        => new Sim { Mode = k, Rating = r, ShipMass = mass, Sink = sink, Seconds = seconds }.Run();

    static int Main()
    {
        // (by hand: `trace ROW SINK [MASS_SHARE] [MODE]` - one touchdown step by step)
        var argv = Environment.GetCommandLineArgs();
        int ti = Array.IndexOf(argv, "trace");
        if (ti >= 0)
        {
            var tr = Vanilla(int.Parse(argv[ti + 1]));
            float sink = float.Parse(argv[ti + 2], System.Globalization.CultureInfo.InvariantCulture);
            float share = argv.Length > ti + 3 ? float.Parse(argv[ti + 3], System.Globalization.CultureInfo.InvariantCulture) : 1f;
            var kind = argv.Length > ti + 4 ? Enum.Parse<Sim.Kind>(argv[ti + 4]) : Sim.Kind.Oleo;
            var ts = new Sim { Mode = kind, Rating = tr, ShipMass = share * tr.RatedMass, Sink = sink, Trace = true }.Run();
            Console.WriteLine(ts.Line());
            return 0;
        }
        // 1. the rating table
        {
            for (int i = 0; i < WheelRating.Diameters.Length; i++)
                Check(Near(WheelRating.RatedFor(WheelRating.Diameters[i]), WheelRating.RatedKg[i], 1f), $"rating row {i}: {WheelRating.RatedFor(WheelRating.Diameters[i])}");
            Check(Near(WheelRating.RatedFor(1f), 15000f, 1f) && Near(WheelRating.RatedFor(2.5f), 93750f, 1f), "the 1 m / 2.5 m ratings");
            Check(Near(WheelRating.RatedFor(2f), 60000f, 1f), $"a 2 m wheel between the rows: {WheelRating.RatedFor(2f)} (the diameter squared: 60 t)");
            Check(Near(WheelRating.RatedFor(0.5f), 3750f, 1f) && Near(WheelRating.RatedFor(25f), 2343750f * 4f, 10f), "past the ends: the diameter squared");
            float prev = 0f; bool mono = true;
            for (float d = 0.2f; d < 20f; d += 0.05f) { float v = WheelRating.RatedFor(d); if (!(v > prev)) mono = false; prev = v; }
            Check(mono, "the rating grows with the size everywhere");
            Check(WheelRating.RatedFor(0f) == 0f && WheelRating.RatedFor(float.NaN) == 0f && WheelRating.RatedFor(-1f) == 0f, "a bad size rates 0");
            var r = WheelRating.Of(0.5f, 0.25f, 200f, 2f);
            Check(Near(r.RatedMass, 30000f, 1f) && Near(r.Diameter, 1f, 1e-5f), "Wheels.OleoRating scales the load");
            Check(Near(WheelRating.Of(0.5f, 0.25f, 200f, 10f).RatedMass, 45000f, 1f) && Near(WheelRating.Of(0.5f, 0.25f, 200f, 0f).RatedMass, 7500f, 1f), "the rating scale clamped 0.5..3");
            Check(!new WheelRating(1f, 0f, 200f, 1000f).Valid && !new WheelRating(1f, 0.25f, 0f, 1000f).Valid && r.Valid, "validity");
        }
        // 2. the gas spring: static sag, stiffening
        for (int row = 0; row < 3; row++)
        {
            var r = Vanilla(row);
            float sag = WheelOleo.SagFor(r, r.RatedMass, WheelRating.DesignG);
            Check(Near(sag / r.Stroke, WheelOleo.StaticFraction, 1e-3f), $"{r}: rated sag {sag / r.Stroke:P1} of the stroke, want 60 %");
            Check(Near(WheelOleo.SpringForce(r, sag), r.RatedWeight, r.RatedWeight * 1e-3f), $"{r}: the spring at the sag carries the rated weight");
            Check(WheelOleo.SagFor(r, 3f * r.RatedMass, WheelRating.DesignG) < 0.9f * r.Stroke, $"{r}: three times the rating still within the stroke ({WheelOleo.SagFor(r, 3f * r.RatedMass, WheelRating.DesignG) / r.Stroke:P0})");
            Check(WheelOleo.SagFor(r, 0.5f * r.RatedMass, WheelRating.DesignG) < 0.5f * r.Stroke, "half the rating: under half the stroke");
            Check(WheelOleo.SpringForce(r, 0f) == 0f && WheelOleo.SpringForce(r, -0.1f) == 0f, "no preload at full extension");
            float end = WheelOleo.SpringForce(r, r.Stroke) / r.RatedWeight;
            Check(end > 6f && end < 12f, $"{r}: full compression pushes {end:F1} rated weights (want 6..12)");
            float k1 = WheelOleo.Stiffness(r, 0.2f * r.Stroke), k2 = WheelOleo.Stiffness(r, 0.6f * r.Stroke), k3 = WheelOleo.Stiffness(r, r.Stroke);
            Check(k1 < k2 && k2 < k3 && k3 > 10f * k2, $"{r}: stiffens toward the end ({k1:E2} < {k2:E2} < {k3:E2})");
            // the slope matches the force's difference
            float dx = 1e-4f * r.Stroke, num = (WheelOleo.SpringForce(r, 0.5f * r.Stroke + dx) - WheelOleo.SpringForce(r, 0.5f * r.Stroke - dx)) / (2f * dx);
            Check(Near(num, WheelOleo.Stiffness(r, 0.5f * r.Stroke), 0.01f * num), $"stiffness = dF/dx ({num:E3} vs {WheelOleo.Stiffness(r, 0.5f * r.Stroke):E3})");
            Check(WheelOleo.Damping(r, sag, -1f) > WheelOleo.Damping(r, sag, 1f), "rebound damps harder than compression");
        }
        // 3. the stiffness cap
        {
            Check(Near(WheelShockSettings.MaxStrength * WheelShockSettings.SolverTau, 1.67f, 1e-4f), "the cap is an effective tau of 1.67");
            Check(WheelShockSettings.CapStrength(3f) == WheelShockSettings.MaxStrength && WheelShockSettings.CapStrength(-1f) == 0f && WheelShockSettings.CapStrength(1f) == 1f, "CapStrength");
            Check(Near(WheelShockSettings.ConstraintSpringRate(1f, 200f, 1f / 60f), 0.6f * 200f * 3600f, 1f), "the constraint's spring: factor x tau x m_wheel / dt^2 (432 kN/m for a 1 m wheel)");
        }
        // 4. the step: guards, the implicit solve, the rate cap
        {
            var r = Vanilla(0); float last = 0f, dt = 1f / 60f;
            Check(WheelOleo.Step(r, 0f, -1f, -1f, 1000f, dt, ref last) == 0f && last == 0f, "hanging, extending: no force");
            Check(WheelOleo.Step(r, float.NaN, 0f, 0f, 1000f, dt, ref last) == 0f && WheelOleo.Step(r, 0.1f, 0f, 0f, 0f, dt, ref last) == 0f, "bad inputs: no force");
            // at rest on the ground at the rated sag with the step's gravity in v*: exactly the weight
            float sag = WheelOleo.SagFor(r, r.RatedMass, WheelRating.DesignG); last = r.RatedWeight;
            float f = WheelOleo.Step(r, sag, 0f, WheelRating.DesignG * dt, r.RatedMass, dt, ref last);
            Check(Near(f, r.RatedWeight, 0.002f * r.RatedWeight), $"at rest: the strut carries {f / r.RatedWeight:F4} of the weight (want 1)");
            // in the air (the wheel's mass) the same compression gives far less - the light wheel is not thrown
            last = 0f; float fa = WheelOleo.Step(r, sag, 0f, 0f, WheelOleo.AirMass(r.RatedMass, r.WheelMass), dt, ref last);
            Check(fa > 0f && fa < 0.2f * r.RatedWeight, $"in the air the wheel takes {fa / r.RatedWeight:F3} rated weights (want < 0.2)");
            // the rate cap: a hard hit (20 m/s) from nothing rises by the cap only, step by step; a fall is not held; no cap: all at once
            float cap = WheelShockSettings.ForceRateCap; last = 0f;
            float f1 = WheelOleo.Step(r, 0.01f, 20f, 20f, r.RatedMass, dt, ref last);
            Check(Near(f1, cap * r.RatedWeight * dt, 1f), $"rate cap: the first step {f1 / r.RatedWeight:F2} rated weights (cap {cap * dt:F2})");
            float f2 = WheelOleo.Step(r, 0.02f, 20f, 20f, r.RatedMass, dt, ref last);
            Check(Near(f2 - f1, cap * r.RatedWeight * dt, 1f), "rate cap: the second step rises by the cap too");
            float f3 = WheelOleo.Step(r, 0.02f, -2f, -2f, r.RatedMass, dt, ref last);
            Check(f3 < f2 - cap * r.RatedWeight * dt, "a falling force is not held by the cap");
            Check(f3 >= 0f && WheelOleo.Step(r, 0.05f, -20f, -20f, r.RatedMass, dt, ref last) == 0f, "push only: a fast extension gives no pull");
            WheelShockSettings.ForceRateCap = 0f; last = 0f;
            float fn = WheelOleo.Step(r, 0.01f, 20f, 20f, r.RatedMass, dt, ref last);
            WheelShockSettings.ForceRateCap = cap;
            Check(fn > f1 * 1.5f, $"no cap: the damper's whole force at once ({fn / r.RatedWeight:F1} rated weights)");
            // the metered damper: a light ship's damping follows the mass it moves, never past the rating
            Check(WheelOleo.Damping(r, sag, 1f, 0.1f * r.RatedMass) < WheelOleo.Damping(r, sag, 1f) && WheelOleo.Damping(r, sag, 1f, 10f * r.RatedMass) == WheelOleo.Damping(r, sag, 1f), "the damper meters to the moved mass, capped at the rating");
        }
        // 5. the airshock
        {
            Check(WheelAirshock.Coming(false, 3f, 1.4f) && !WheelAirshock.Coming(false, 3f, 1.6f) && WheelAirshock.Coming(false, 1f, 0.25f) && !WheelAirshock.Coming(true, 3f, 0.1f) && !WheelAirshock.Coming(false, 0.3f, 0.1f), "arming: within 0.5 s or 0.3 m, sinking, not touching");
            var a = new WheelAirshock();
            Check(a.Step(true, false, 3f, 5f, 0) == 0 && !a.Armed, "far: not armed");
            Check(a.Step(false, false, 3f, 1f, 0.1) == 0 && !a.Armed, "not usable: never armed");
            Check(a.Step(true, false, 3f, 1f, 0.2) == 1 && a.Armed, "close: armed");
            Check(a.Step(true, true, 1f, 0f, 0.4) == 0 && a.Armed, "touching, still sinking: held");
            a.Step(true, true, 0.1f, 0f, 0.5);
            Check(a.Step(true, true, 0.1f, 0f, 0.9) == 0 && a.Armed && a.Step(true, true, 0.1f, 0f, 1.05) == -1 && !a.Armed && a.State == "settled", "settled 0.5 s: released");
            var b = new WheelAirshock(); b.Step(true, false, 2f, 0.5f, 0);
            Check(b.Armed && b.Step(true, false, -1f, 2f, 0.1) == -1 && b.State == "released", "pulled up before touching: released");
            var c = new WheelAirshock(); c.Step(true, false, 2f, 0.5f, 0); c.Step(true, true, 2f, 0f, 0.1);
            Check(c.Step(true, true, 2f, 0f, 3.05) == 0 && c.Step(true, true, 2f, 0f, 3.11) == -1, "held 3 s after the touch at most");
            var d = new WheelAirshock(); d.Step(true, false, 2f, 0.5f, 0);
            Check(d.Step(false, false, 2f, 0.4f, 0.05) == -1, "no longer usable: released");
        }
        // 6. touchdowns at the rated load: within the stroke, little rebound, settled - every vanilla size, 1 / 3 / 5 m/s
        Console.WriteLine("== touchdowns at the rated load (2 g), airshock + oleo:");
        for (int row = 0; row < 3; row++)
        {
            var r = Vanilla(row);
            foreach (float v in new[] { 1f, 3f, 5f })
            {
                var s = Run(Sim.Kind.Oleo, r, r.RatedMass, v);
                Console.WriteLine($"  {WheelRating.Diameters[row],4} m  {v} m/s  {s.Line()}");
                Check(s.Finite, $"{r} {v} m/s: finite");
                Check(!s.Bottomed, $"{r} {v} m/s: bottomed ({s.MaxComp / r.Stroke:P0})");
                Check(s.MaxComp < 0.97f * r.Stroke, $"{r} {v} m/s: compression {s.MaxComp / r.Stroke:P0} (want < 97 %)");
                Check(s.Rebound < 0.15f * v, $"{r} {v} m/s: rebound {s.Rebound:F2} m/s (want < 15 % of the sink)");
                Check(s.Bounces == 0, $"{r} {v} m/s: {s.Bounces} bounce(s)");
                Check(!float.IsNaN(s.SettleTime) && s.SettleTime < 1.5f, $"{r} {v} m/s: settle {s.SettleTime:F2} s (want < 1.5)");
                Check(Near(s.FinalComp / r.Stroke, WheelOleo.StaticFraction, 0.03f), $"{r} {v} m/s: rests at {s.FinalComp / r.Stroke:P0} (want 60 %)");
                Check(s.PeakDecelG < 15f, $"{r} {v} m/s: peak {s.PeakDecelG:F1} g");   // (5 m/s into the 1 m wheel's 0.25 m: 7 g at the very least)
            }
        }
        // 7. overload: three times the rating at 5 m/s bottoms - honestly (the hard limit takes it; no NaN), no worse than vanilla does
        {
            var r = Vanilla(0);
            var s = Run(Sim.Kind.Oleo, r, 3f * r.RatedMass, 5f);
            var van = Run(Sim.Kind.Vanilla, r, 3f * r.RatedMass, 5f);
            Console.WriteLine($"== overload (3x the rating, 5 m/s):  {s.Line()}\n                                     {van.Line()}");
            Check(s.Finite && s.Bottomed, $"overload: bottoms ({s.MaxComp / r.Stroke:P0}, {s.BottomSteps} steps)");
            Check(s.BottomSteps < van.BottomSteps && s.Rebound <= van.Rebound + 0.5f, $"overload: no worse than vanilla (bottomed {s.BottomSteps} vs {van.BottomSteps} steps, rebound {s.Rebound:F2} vs {van.Rebound:F2})");
            Check(s.FinalComp < r.Stroke && s.FinalComp > 0.7f * r.Stroke, $"overload: rests at {s.FinalComp / r.Stroke:P0} of the stroke (in it, deep)");
        }
        // 8. a light ship (a tenth of the rating) - the stiff gas against a small mass stays stable
        foreach (float v in new[] { 1f, 5f })
        {
            var r = Vanilla(0);
            var s = Run(Sim.Kind.Oleo, r, 0.1f * r.RatedMass, v);
            Console.WriteLine($"== light ship (10 % of the rating, {v} m/s):  {s.Line()}");
            Check(s.Finite && s.MaxSpeed <= v + 0.5f && s.Rebound < 0.5f * v, $"light ship {v} m/s: stable (rebound {s.Rebound:F2}, max {s.MaxSpeed:F2})");
            Check(s.PeakDecelG < 30f, $"light ship {v} m/s: peak {s.PeakDecelG:F1} g (the metered damper: want < 30 - a 1.5 t share on a 15 t wheel is stiff, honestly)");
        }
        // 9. the rate cap: the hit spread over steps (never a rise past the cap), still within the stroke at 5 m/s. (In this ideal sim the
        //    peak deceleration is not lower with it - the ramp ends higher; its point in game is no one-step spike off a contact.)
        {
            var r = Vanilla(0);
            var with = Run(Sim.Kind.Oleo, r, r.RatedMass, 5f);
            float cap = WheelShockSettings.ForceRateCap; WheelShockSettings.ForceRateCap = 0f;
            var without = Run(Sim.Kind.Oleo, r, r.RatedMass, 5f);
            WheelShockSettings.ForceRateCap = cap;
            Console.WriteLine($"== rate cap at 5 m/s (1 m wheel): with {with.PeakDecelG:F1} g, largest rise {with.MaxRiseN / r.RatedWeight:F2} W/step, comp {with.MaxComp / r.Stroke:P0}; without {without.PeakDecelG:F1} g, {without.MaxRiseN / r.RatedWeight:F2} W/step, comp {without.MaxComp / r.Stroke:P0}");
            Check(with.MaxRiseN <= cap * r.RatedWeight * Sim.Dt * 1.001f && with.MaxRiseN < without.MaxRiseN, "the strut's force never rose past the cap");
            Check(!with.Bottomed, "with the cap: within the stroke at 5 m/s");
        }
        // 10. vanilla vs airshock vs airshock + oleo: the Tu-134 Big's share (56 t on four 1 m wheels: 14 t a wheel) and each size rated
        Console.WriteLine("== the three modes:");
        var cases = new List<(string name, WheelRating r, float mass)> { ("Tu-134 Big, 14 t on a 1 m wheel", Vanilla(0), 14000f) };
        for (int row = 0; row < 3; row++) cases.Add(($"{WheelRating.Diameters[row]} m wheel at its rating", Vanilla(row), Vanilla(row).RatedMass));
        foreach (var (name, r, mass) in cases)
        {
            Console.WriteLine($"  {name} ({r}):");
            foreach (float v in new[] { 1f, 3f, 5f })
            {
                var van = Run(Sim.Kind.Vanilla, r, mass, v); var air = Run(Sim.Kind.Airshock, r, mass, v); var ole = Run(Sim.Kind.Oleo, r, mass, v);
                Console.WriteLine($"    {v} m/s  {van.Line()}\n            {air.Line()}\n            {ole.Line()}");
                Check(van.Finite && air.Finite && ole.Finite, $"{name} {v}: finite");
                Check(!ole.Bottomed, $"{name} {v} m/s: the oleo bottomed");
                Check(ole.MaxComp <= air.MaxComp + 1e-3f || !air.Bottomed, $"{name} {v} m/s: the oleo used more stroke than a bottoming airshock");
                if (v >= 3f) Check(van.Bottomed, $"{name} {v} m/s: vanilla did NOT bottom (the sim's vanilla is too kind: {van.MaxComp / r.Stroke:P0})");
                Check(ole.Rebound <= van.Rebound + 1e-3f, $"{name} {v} m/s: the oleo rebounds more than vanilla");
            }
        }
        Console.WriteLine($"WheelShockTests: {_checks - _fails}/{_checks} passed");
        return _fails == 0 ? 0 : 1;
    }
}
