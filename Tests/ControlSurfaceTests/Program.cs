#pragma warning disable
using AeroMod;

/// <summary>
/// ControlSurfaceTests: the 3x3x1 surfaces must work however they are placed (the user: "like wheels: only the wind and
/// the cockpit's orientation matter"). The mixer (AeroGridComponent.UpdateControlSurfaceInputs) gives each surface its
/// input from effectiveness = r x liftDir, with liftDir = (hinge x chord), flipped when the air comes over the trailing
/// edge. Here: for random hinge / chord / airflow (both ways), the force a positive input ADDS must point along that
/// liftDir - else the mixer would steer that surface the wrong way. Also: finite forces in any flow, lift slope sane.
/// </summary>
static partial class Program
{
    static int _checks, _fails;
    static void Check(bool ok, string what) { _checks++; if (!ok) { _fails++; if (_fails <= 15) Console.WriteLine("FAIL " + what); } }

    static readonly AtmosphereState Sea = new() { Density = 1.225, Pressure = 101325, Temperature = 288, SpeedOfSound = 340, DynamicViscosity = 1.8e-5 };

    static Vector3 RandUnit(Random r)
    {
        while (true)
        {
            var v = new Vector3((float)(r.NextDouble() * 2 - 1), (float)(r.NextDouble() * 2 - 1), (float)(r.NextDouble() * 2 - 1));
            float l = v.Length(); if (l > 0.2f && l < 1f) return v / l;
        }
    }

    static Vector3 Force(ControlSurface cs, Vector3 velocity, float input)
    {
        cs.DeflectionInput = input;
        float speed = velocity.Length();
        var cond = new LocalAeroConditions(velocity, speed, velocity / speed, (float)Sea.GetDynamicPressure(speed), (float)Sea.GetMachNumber(speed), Sea, Vector3.Zero);
        return cs.Compute(cond).Force;
    }

    static int Main()
    {
        var r = new Random(7);
        int reversed = 0;
        for (int i = 0; i < 4000; i++)
        {
            // a placement: any hinge, a chord square to it (the block's 24 orientations are special cases)
            var h = RandUnit(r);
            var c = Vector3.Normalize(Vector3.Cross(h, RandUnit(r)));
            var n = Vector3.Cross(h, c);
            var cs = new ControlSurface(Vector3.Zero, Vector3I.Zero, h, c, 56.25f) { MaxDeflection = 20f, AspectRatio = 1f };
            // the air: mostly along the chord, either way (a surface mounted backwards gets it over its trailing edge),
            // a small angle of attack (attached flow) and some spanwise slip
            bool back = r.Next(2) == 0; if (back) reversed++;
            var air = Vector3.Normalize((back ? -c : c) + n * (float)(r.NextDouble() * 0.2 - 0.1) + h * (float)(r.NextDouble() * 0.3 - 0.15));
            float speed = 30f + (float)r.NextDouble() * 150f;
            var vel = -air * speed;   // (the surface moves into the air)
            // the mixer's lift direction for this surface (AeroGridComponent: flipped when -velocity . chord < 0)
            var liftDir = n;
            if (Vector3.Dot(-vel, c) < 0f) liftDir = -liftDir;
            var f0 = Force(cs, vel, 0f);
            var dPlus = Force(cs, vel, 0.25f) - f0;
            var dMinus = Force(cs, vel, -0.25f) - f0;
            Check(Vector3.Dot(dPlus, liftDir) > 0f, $"#{i} {(back ? "backwards" : "forwards")}: +input adds force along -liftDir ({Vector3.Dot(dPlus, liftDir):F1} N)");
            Check(Vector3.Dot(dMinus, liftDir) < 0f, $"#{i} {(back ? "backwards" : "forwards")}: -input adds force along +liftDir ({Vector3.Dot(dMinus, liftDir):F1} N)");
            Check(float.IsFinite(f0.X) && float.IsFinite(f0.Y) && float.IsFinite(f0.Z), $"#{i}: force not finite");
        }
        // any flow at all (cross-flow, straight down the normal, along the span): finite, no NaN
        for (int i = 0; i < 2000; i++)
        {
            var h = RandUnit(r); var c = Vector3.Normalize(Vector3.Cross(h, RandUnit(r)));
            var cs = new ControlSurface(Vector3.Zero, Vector3I.Zero, h, c, 2.25f) { MaxDeflection = 20f, AspectRatio = 1f };
            var f = Force(cs, RandUnit(r) * 120f, (float)(r.NextDouble() * 2 - 1));
            Check(float.IsFinite(f.X) && float.IsFinite(f.Y) && float.IsFinite(f.Z), $"any-flow #{i}: force not finite");
        }
        // the lift slope: 5 deg of attack on the large surface at 100 m/s sea level - lift ~ q S CLa a, CLa of an AR 1 plate
        // ~1.5-2.5 /rad (thin-airfoil 2 pi cut down by the aspect ratio)
        {
            var cs = new ControlSurface(Vector3.Zero, Vector3I.Zero, new Vector3(0, 0, -1), new Vector3(1, 0, 0), 56.25f) { MaxDeflection = 20f, AspectRatio = 1f };
            float a = 5f * MathF.PI / 180f;
            var air = new Vector3(MathF.Cos(a), MathF.Sin(a), 0f);   // (air from the leading edge, rising into the lower face)
            var f = Force(cs, -air * 100f, 0f);
            double q = Sea.GetDynamicPressure(100); float lift = f.Length();
            double cla = lift / (q * 56.25 * a);
            Console.WriteLine($"lift slope: {lift:F0} N at 5 deg, CLa {cla:F2} /rad");
            Check(cla > 1.0 && cla < 3.5, $"lift slope {cla:F2} /rad outside 1..3.5 for an AR 1 surface");
        }
        // the rate law (ControlSurface.RateCommand): a turn held at the asked rate is ASSISTED (it was fought: damping on the
        // rate, not its error - in game, -240 kN m against a 0.5 rad/s roll on the Tu-134), a released turn is damped, and a
        // starting one gets at least the old push
        for (int i = 0; i < 4000; i++)
        {
            var eff = RandUnit(r); var want = RandUnit(r) * (float)(r.NextDouble() * 1.0); float gain = 0.2f + (float)r.NextDouble();
            float held = ControlSurface.RateCommand(eff, want, want, gain);
            Check(held * Vector3.Dot(eff, want) >= -1e-6f, $"rate #{i}: a held turn is fought ({held:F3} against {Vector3.Dot(eff, want):F3})");
            var spin = RandUnit(r) * (float)r.NextDouble();
            float released = ControlSurface.RateCommand(eff, Vector3.Zero, spin, gain);
            Check(released * Vector3.Dot(eff, spin) <= 1e-6f, $"rate #{i}: a released turn is not damped");
            float start = ControlSurface.RateCommand(eff, want, Vector3.Zero, gain), old = Vector3.Dot(eff, want) * gain;
            Check(MathF.Abs(start) >= MathF.Abs(old) - 1e-6f && start * old >= -1e-6f, $"rate #{i}: a starting turn gets less than the old push");
        }
        // the allocation (the surfaces as the attitude law's actuators): a demand within reach is met (|sum u a - want| small),
        // one beyond reach is never overshot along its own direction, and no input exceeds +-1
        for (int i = 0; i < 3000; i++)
        {
            int n = 3 + r.Next(6); var a = new List<Vector3>(); var u = new float[n];
            for (int k = 0; k < n; k++) a.Add(RandUnit(r) * (1000f + (float)r.NextDouble() * 50000f));
            var inReach = Vector3.Zero; for (int k = 0; k < n; k++) inReach += a[k] * (float)(r.NextDouble() * 1.2 - 0.6);
            Check(ControlSurface.Allocate(a, inReach, u), $"alloc #{i}: no solution");
            var got = Vector3.Zero; for (int k = 0; k < n; k++) { got += a[k] * u[k]; Check(MathF.Abs(u[k]) <= 1f, $"alloc #{i}: input {u[k]} beyond 1"); }
            Check((got - inReach).Length() <= 0.02f * inReach.Length() + 1f, $"alloc #{i}: demand in reach missed by {(got - inReach).Length():F0} of {inReach.Length():F0} N m");
            var far = inReach * 20f; ControlSurface.Allocate(a, far, u);
            got = Vector3.Zero; for (int k = 0; k < n; k++) got += a[k] * u[k];
            Check(Vector3.Dot(got, far) >= 0f && (got - far).Length() <= far.Length(), $"alloc #{i}: a demand beyond reach made worse than nothing");
        }
        // the cost per grid per tick of the allocation as AeroGridComponent runs it on the Tu-134 (7 surfaces): 3 Compute per
        // surface (its torque per unit input + undeflected) and the solve - reported, not checked
        {
            var cs7 = new List<ControlSurface>(); var pos = new List<Vector3>();
            for (int k = 0; k < 7; k++) { var h = RandUnit(r); var c = Vector3.Normalize(Vector3.Cross(h, RandUnit(r))); cs7.Add(new ControlSurface(RandUnit(r) * 10f, Vector3I.Zero, h, c, 2.25f) { MaxDeflection = 20f, AspectRatio = 1f }); }
            var vel = new Vector3(0, -10, -180f); float sp = vel.Length();
            var cond = new LocalAeroConditions(vel, sp, vel / sp, (float)Sea.GetDynamicPressure(sp), (float)Sea.GetMachNumber(sp), Sea, Vector3.Zero);
            var aa = new List<Vector3>(); var uu = new float[16];
            long g0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = System.Diagnostics.Stopwatch.StartNew(); int reps = 20000; double worst = 0; var times = new double[reps];
            for (int rep = 0; rep < reps; rep++)
            {
                long t0 = sw.ElapsedTicks;
                aa.Clear(); Vector3 t0sum = Vector3.Zero;
                foreach (var c in cs7)
                {
                    c.DeflectionInput = 0.5f; var tp = c.Compute(cond).TorqueAbout(Vector3.Zero);
                    c.DeflectionInput = -0.5f; var tm = c.Compute(cond).TorqueAbout(Vector3.Zero);
                    c.DeflectionInput = 0f; t0sum += c.Compute(cond).TorqueAbout(Vector3.Zero);
                    aa.Add(tp - tm);
                }
                var want = RandUnit(r) * (float)(r.NextDouble() * 3e5);   // (some within reach, some far beyond)
                ControlSurface.Allocate(aa, want - t0sum, uu);
                times[rep] = (sw.ElapsedTicks - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency; worst = Math.Max(worst, times[rep]);
            }
            double mean = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            for (int rep = 0; rep < reps; rep++) foreach (var c in cs7) { c.DeflectionInput = 0.5f; c.Compute(cond); c.DeflectionInput = -0.5f; c.Compute(cond); c.DeflectionInput = 0f; c.Compute(cond); }
            Console.WriteLine($"  of which the 21 Compute calls: {sw2.Elapsed.TotalMilliseconds * 1000.0 / reps:F1} us");
            long bytes = GC.GetAllocatedBytesForCurrentThread() - g0;
            Array.Sort(times);
            Console.WriteLine($"allocation cost, 7 surfaces: mean {mean:F1} us, p99 {times[(int)(reps * 0.99)]:F1} us, p99.9 {times[(int)(reps * 0.999)]:F1} us, worst {worst:F1} us per grid per tick, {bytes / (double)reps:F0} B allocated per tick");
        }
        SectionTests(r);
        RegistryTests(r);
        Console.WriteLine($"ControlSurfaceTests: {_checks - _fails}/{_checks} passed ({reversed} of 4000 placements had the air over the trailing edge)");
        return _fails == 0 ? 0 : 1;
    }
}
