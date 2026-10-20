#pragma warning disable
using System.IO;
using AeroMod;

/// <summary>
/// SurfaceStallTests (2026-10-19): control surfaces past the stall, measured. A measurement run on dumped real ships found
/// their post-stall authority 2-10x under a flat plate's and the allocator / keyboard mixer pushing the wrong way there; the
/// fix (Components/ControlSurface.cs: Section, PostStallAuthorityFloor, Measure, Refine, MixerInput) is checked here
///  1. single plates in free air: the force normal to the plate and the deflection's change against a flat-plate reference;
///  2. attached flow (both angles under the stall): the new Compute IS the old one (Legacy.cs);
///  3. Refine (the allocator's second step) never ends worse than doing nothing, nor with a surface against the demand;
///  4. the flight assists' pure parts (FlightAssists: the uncommanded offset torque, post-stall lift, stall angle, presets);
///     4b. on a plate wing through the real wing model (Wings.cs): all at 1 bit-identical, the stall angle and post-stall lift move;
///  5. dumped ships (Tests/data/surfaces/*.boxes + *.ship - conv.py from blueprints, local only: skipped when absent):
///     nose-down / up, roll, yaw authority over the angle of attack 0-90 deg at 100 m/s, before (Legacy) and after - the best
///     a scan of each surface's inputs finds, what the allocator gives, what the keyboard mixer gives; no reversal anywhere;
///     the deep-stall lock-in angle with and without gyros; a time-stepped pitch recovery from 35 deg.
/// Exit 0: all pass.
/// </summary>
static partial class Program
{
    static int _checks, _fails;
    static void Check(bool ok, string what) { _checks++; if (!ok) { _fails++; if (_fails <= 40) Console.WriteLine("FAIL " + what); } }
    static readonly AtmosphereState Sea = new() { Density = 1.225, Pressure = 101325, Temperature = 288, SpeedOfSound = 340, DynamicViscosity = 1.8e-5 };
    const float D2R = MathF.PI / 180f;

    static int Main(string[] args)
    {
        var r = new Random(11);
        Plates();
        AttachedUnchanged(r);
        RefineChecks(r);
        AssistChecks();
        WingAssists();
        Ships(args.Length > 0 && args[0] == "quick");
        Console.WriteLine($"SurfaceStallTests: {_checks - _fails}/{_checks} passed");
        return _fails == 0 ? 0 : 1;
    }

    static LocalAeroConditions Cond(Vector3 velocity)
    {
        float speed = velocity.Length();
        return new LocalAeroConditions(velocity, speed, velocity / speed, (float)Sea.GetDynamicPressure(speed), (float)Sea.GetMachNumber(speed), Sea, Vector3.Zero);
    }
    static ComponentForceResult Eval(ControlSurface cs, in LocalAeroConditions c, float u, bool legacy)
    {
        float keep = cs.DeflectionInput; cs.DeflectionInput = u;
        var res = legacy ? Legacy.Compute(cs, c) : cs.Compute(c);
        cs.DeflectionInput = keep; return res;
    }

    // ── 1. single plates ──
    static void Plates()
    {
        Console.WriteLine("\n== 1. single plates in free air, 100 m/s, full throw +-20 deg: force normal to the plate / (q x own area)");
        var cases = new (string name, float host, float span, int plates)[] { ("all-moving 1.5 m plate (AR 1)", 0f, 1.5f, 0), ("flap behind a 2 m host, span 3 m", 2.0f, 3.0f, 0), ("flap behind 4.6 m (SSTO fin root, end plate)", 4.58f, 1.5f, 1) };
        foreach (var (name, host, span, plates) in cases)
        {
            var cs = new ControlSurface(Vector3.Zero, Vector3I.Zero, Vector3.UnitX, Vector3.UnitZ, 2.25f) { MaxDeflection = 20f, AspectRatio = 1f, Cd0 = 0.03f, Extents = new Vector3(1.5f, 0.5f, 1.5f) };
            if (host > 0f || plates > 0)
            {
                var g = new SurfaceSurroundings(); for (int e = 0; e < 2; e++) { g.HostChord[e] = host; g.SectionSpan[e] = span; g.EndPlates[e] = plates; }
                cs.Surroundings = g;
            }
            Vector3 n0 = Vector3.Normalize(Vector3.Cross(Vector3.UnitX, Vector3.UnitZ));
            Console.WriteLine($"\n   {name}");
            Console.WriteLine($"   alpha0 |  CN(0) before  after | |dF|/2 before  after | ref plate | floor | (|dF|/2: half the force change between full throw each way, per q S own; ref: 1.17 sin turned by tau delta, on the strip)");
            float prevCn = -1f; bool mono = true;
            foreach (float deg in new[] { 0f, 5f, 10f, 15f, 20f, 25f, 30f, 35f, 40f, 45f, 50f, 60f, 70f, 80f, 90f })
            {
                float a = deg * D2R;
                Vector3 airflow = Vector3.UnitZ * MathF.Cos(a) + n0 * MathF.Sin(a);
                var c = Cond(-airflow * 100f);
                float q = c.DynamicPressure;
                float CN(bool legacy) => Vector3.Dot(Eval(cs, c, 0f, legacy).Force, n0) / (q * cs.Area);
                float DF(bool legacy) => 0.5f * (Eval(cs, c, 1f, legacy).Force - Eval(cs, c, -1f, legacy).Force).Length() / (q * cs.Area);
                float cnB = CN(true), cnA = CN(false), dfB = DF(true), dfA = DF(false);
                Eval(cs, c, 0f, false);
                float tau = cs.LastTau, strip = (1.5f + cs.LastHostChord) / 1.5f, d = 20f * D2R * tau;
                // (the reference: a 1.17 sin(a) plate's normal force turned with the effective plate, its change between +-tau delta)
                Vector2 F(float x) => new Vector2(MathF.Cos(x), MathF.Sin(x)) * 1.17f * MathF.Sin(x);
                float refD = 0.5f * strip * (F(a + d) - F(a - d)).Length();
                float clA = CompressibleWingModel.ComputeCLAlpha(100f / 340f, cs.LastAspectRatio, 0f, 0.85f, 1.15f);
                float floorD = ControlSurface.PostStallAuthorityFloor * clA * d * strip;
                Console.WriteLine($"   {deg,6:F0} | {cnB,13:F2} {cnA,6:F2} | {dfB,13:F3} {dfA,6:F3} | {refD,9:F3} | {floorD,5:F3}");
                float stall = ControlSurface.StallAngle(cs.LastAspectRatio) / D2R;
                if (deg > stall)
                {
                    Check(dfA >= 0.98f * floorD, $"plate '{name}' {deg} deg: the deflection's change {dfA:F3} under the floor {floorD:F3}");
                    if (host == 0f && deg >= 45f) Check(dfA > 0.5f * refD && dfA < 1.8f * refD, $"plate '{name}' {deg} deg: |dF| {dfA:F3} vs flat-plate reference {refD:F3}");
                }
                if (deg >= 45f) { if (prevCn > cnA + 1e-3f) mono = false; prevCn = cnA; }
                if (host == 0f && deg == 90f) Check(MathF.Abs(cnA - ControlSurface.Cd90(1f)) < 0.06f, $"plate AR 1 at 90 deg: CN {cnA:F2}, want CD90 {ControlSurface.Cd90(1f):F2} (Hoerner 1.17)");
            }
            Check(mono, $"plate '{name}': the normal force falls somewhere from 45 to 90 deg");
        }
        // the section curve itself: past the blend the normal force never falls up to 90 deg (low aspect: from the stall on)
        foreach (float ar in new[] { 0.5f, 1f, 1.5f, 2f, 4f, 8f, 12f })
        {
            float cla = CompressibleWingModel.ComputeCLAlpha(0.3f, ar, 0f, 0.85f, 1.15f), st = ControlSurface.StallAngle(ar);
            float band = MathF.Max(ControlSurface.StallBand, 0.5f * st), prev = -1f; bool ok = true; float worst = 0f;
            for (float a = st + band; a <= MathF.PI / 2 + 1e-4f; a += 0.25f * D2R)
            {
                ControlSurface.Section(a, cla, st, ar, out float cl, out float cd, out _);
                float cn = cl * MathF.Cos(a) + cd * MathF.Sin(a);
                if (cn < prev - 1e-4f) { ok = false; worst = MathF.Max(worst, prev - cn); }
                prev = cn;
            }
            Check(ok, $"section AR {ar}: the plate's normal force falls past the blend (by {worst:F4})");
            Check(MathF.Abs(prev - ControlSurface.Cd90(ar)) < 0.02f, $"section AR {ar}: CN at 90 deg {prev:F3}, want CD90 {ControlSurface.Cd90(ar):F3}");
        }
        Check(MathF.Abs(ControlSurface.Cd90(1f) - 1.17f) < 0.01f && MathF.Abs(ControlSurface.Cd90(10f) - 1.28f) < 0.02f && ControlSurface.Cd90(1e4f) > 1.95f, "CD90: Hoerner's 1.17 (AR 1), 1.28 (AR 10), ~2 (2-D)");
    }

    // ── 2. attached flow: unchanged ──
    static void AttachedUnchanged(Random r)
    {
        int n = 0; float worst = 0f;
        for (int i = 0; i < 20000; i++)
        {
            var h = RandUnit(r); var c = Vector3.Normalize(Vector3.Cross(h, RandUnit(r))); var nn = Vector3.Cross(h, c);
            var cs = new ControlSurface(RandUnit(r) * 5f, Vector3I.Zero, h, c, 2.25f) { MaxDeflection = 5f + (float)r.NextDouble() * 25f, AspectRatio = 1f, Cd0 = 0.03f, Extents = Vector3.Abs(h * 1.5f + c * 1.5f + nn * 0.5f) };
            if (r.Next(2) == 0)
            {
                cs.Surroundings = new SurfaceSurroundings();
                for (int e = 0; e < 2; e++) { cs.Surroundings.HostChord[e] = r.Next(2) == 0 ? 0f : (float)r.NextDouble() * 6f; cs.Surroundings.SectionSpan[e] = 1.5f + (float)r.NextDouble() * 30f; cs.Surroundings.EndPlates[e] = r.Next(3); }
            }
            var air = Vector3.Normalize((r.Next(2) == 0 ? -c : c) + nn * (float)(r.NextDouble() * 0.5 - 0.25) + h * (float)(r.NextDouble() * 0.3 - 0.15));
            var cond = Cond(-air * (20f + (float)r.NextDouble() * 300f));
            float u = (float)(r.NextDouble() * 2 - 1);
            var nw = Eval(cs, cond, u, false);
            float ae = MathF.Abs(cs.EffectiveAoA) * D2R, st = ControlSurface.StallAngle(cs.LastAspectRatio);
            // (the section's own angle too: the plate's angle to the air along the chord, from the result's AoA less the deflection's share)
            Vector3 chord = Vector3.Dot(air, c) >= 0f ? c : -c; Vector3 n0 = Vector3.Normalize(Vector3.Cross(h, chord));
            float a0 = MathF.Abs(MathF.Atan2(Vector3.Dot(air, n0), Vector3.Dot(air, chord)));
            if (ae > st || a0 > st) continue;
            n++;
            var old = Eval(cs, cond, u, true);
            float df = (nw.Force - old.Force).Length() / MathF.Max(old.Force.Length(), 1f), dt = (nw.DirectTorque - old.DirectTorque).Length() / MathF.Max(old.DirectTorque.Length(), 1f);
            worst = MathF.Max(worst, MathF.Max(df, dt));
        }
        Console.WriteLine($"\n== 2. attached flow: {n} random placements and inputs under the stall - the new Compute against the old: worst relative difference {worst:E1}");
        Check(n > 3000 && worst < 1e-5f, $"attached flow changed: worst relative difference {worst:E2} over {n}");
    }

    static Vector3 RandUnit(Random r)
    {
        while (true)
        {
            var v = new Vector3((float)(r.NextDouble() * 2 - 1), (float)(r.NextDouble() * 2 - 1), (float)(r.NextDouble() * 2 - 1));
            float l = v.Length(); if (l > 0.2f && l < 1f) return v / l;
        }
    }

    // ── 3. Refine ──
    static void RefineChecks(Random r)
    {
        int worse = 0, against = 0, cases = 4000, K = ControlSurface.Knots;
        var a = new List<Vector3>(); var u = new float[32]; var dT = new Vector3[32 * K]; var du = new float[32 * K];
        for (int i = 0; i < cases; i++)
        {
            int n = 2 + r.Next(12); a.Clear();
            for (int k = 0; k < n; k++)
            {
                // (knots unlike a line: past the stall a side may be weak, turned, push the other side's way, or peak inside)
                float b = r.Next(3) == 0 ? (float)(r.NextDouble() * 1.6 - 0.8) : 0f;
                int o = k * K; float lo = -1f - b, hi = 1f - b;
                du[o] = lo; du[o + 1] = 0.5f * lo; du[o + 2] = 0f; du[o + 3] = 0.5f * hi; du[o + 4] = hi;
                var p = RandUnit(r) * (1000f + (float)r.NextDouble() * 50000f);
                for (int j = 0; j < K; j++) dT[o + j] = r.Next(4) == 0 ? RandUnit(r) * p.Length() * (float)r.NextDouble() : p * du[o + j] * (0.3f + (float)r.NextDouble());
                dT[o + 2] = Vector3.Zero;
                a.Add(0.5f * (dT[o + K - 1] - dT[o]));
            }
            var want = RandUnit(r) * (float)(r.NextDouble() * 2e5);
            ControlSurface.Allocate(a, want, u);
            ControlSurface.Refine(dT, du, want, u, n);
            Vector3 got = Vector3.Zero;
            for (int k = 0; k < n; k++) got += ControlSurface.AtKnots(dT, du, k * K, u[k]);
            float miss = (got - want).Length();
            if (miss > want.Length() * 1.0001f + 1f) worse++;
            // (no one surface against it: taking any one back to its base never leaves less miss)
            for (int k = 0; k < n; k++)
            {
                var mine = ControlSurface.AtKnots(dT, du, k * K, u[k]);
                if ((got - mine - want).Length() < miss * 0.999f - 1f) { against++; break; }
            }
        }
        Console.WriteLine($"\n== 3. Refine over {cases} random sets (knots unlike a line, trims): worse than nothing {worse}, a surface against the demand {against}");
        Check(worse == 0, $"Refine: {worse} cases ended worse than no input");
        Check(against == 0, $"Refine: {against} cases with a surface working against the demand");
    }

    // ── 4. the flight assists' pure parts ──
    static void AssistChecks()
    {
        // the uncommanded offset torque: met ask - none; no ask - all of the offset; offset past reach - what is left; a shortfall
        // against the offset (the law asked more than they could give) - never counted
        var off = new Vector3(10f, -4f, 0f);
        Check(FlightAssists.Uncommanded(off, new Vector3(2f, 1f, 3f), new Vector3(2f, 1f, 3f)) == Vector3.Zero, "uncommanded: a met ask leaves none");
        Check(FlightAssists.Uncommanded(off, off, Vector3.Zero) == off, "uncommanded: no ask - the whole offset");
        Check(FlightAssists.Uncommanded(off, new Vector3(7f, -1f, 0f), Vector3.Zero) == new Vector3(7f, -1f, 0f), "uncommanded: offset past the thrusters' reach - what is left");
        Check(FlightAssists.Uncommanded(off, new Vector3(13f, -4f, 0f), new Vector3(20f, -4f, 0f)) == Vector3.Zero, "uncommanded: the law's own shortfall (with the offset's way) is not the offset's");
        Check(FlightAssists.Uncommanded(off, new Vector3(7f, 0f, 0f), new Vector3(-10f, 0f, 0f)) == new Vector3(10f, 0f, 0f), "uncommanded: an ask against the offset, missed by more than it - at most the offset");
        Check(FlightAssists.Uncommanded(Vector3.Zero, new Vector3(5f, 5f, 5f), Vector3.Zero) == Vector3.Zero, "uncommanded: no offset - none (whatever the miss)");
        Check(FlightAssists.Uncommanded(off, new Vector3(-3f, 2f, 0f), new Vector3(1f, 0f, 0f)) == Vector3.Zero, "uncommanded: a miss against the offset never counts");
        float keep = FlightAssists.OffsetThrustScale;
        FlightAssists.OffsetThrustScale = 1f;
        Check(FlightAssists.ScaleOffset(new Vector3(7f, -1f, 0f), off, Vector3.Zero) == new Vector3(7f, -1f, 0f), "offset scale 1: the torque untouched");
        FlightAssists.OffsetThrustScale = 0f;
        Check(FlightAssists.ScaleOffset(new Vector3(7f, -1f, 0f), off, Vector3.Zero) == Vector3.Zero, "offset scale 0: no ask - none of the offset left");
        Check(FlightAssists.ScaleOffset(new Vector3(2f, 1f, 3f), off, new Vector3(2f, 1f, 3f)) == new Vector3(2f, 1f, 3f), "offset scale 0: a met ask untouched (the law's torque)");
        Check(FlightAssists.ScaleOffset(new Vector3(13f, -4f, 0f), off, new Vector3(20f, -4f, 0f)) == new Vector3(13f, -4f, 0f), "offset scale 0: the law's ask beyond reach - nothing added, nothing removed");
        // (random: the result is never further from the ask than the delivered torque, axis by axis, and never past the ask)
        var r = new Random(5); int bad = 0;
        for (int i = 0; i < 20000; i++)
        {
            var o = RandUnit(r) * 10f; var ask = RandUnit(r) * 10f * (float)r.NextDouble(); var del = ask + RandUnit(r) * 12f * (float)r.NextDouble();
            FlightAssists.OffsetThrustScale = (float)r.NextDouble();
            var got = FlightAssists.ScaleOffset(del, o, ask);
            for (int k = 0; k < 3; k++)
            {
                float g = k == 0 ? got.X : k == 1 ? got.Y : got.Z, d = k == 0 ? del.X : k == 1 ? del.Y : del.Z, q = k == 0 ? ask.X : k == 1 ? ask.Y : ask.Z, oo = k == 0 ? o.X : k == 1 ? o.Y : o.Z;
                if (MathF.Abs(g - q) > MathF.Abs(d - q) + 1e-4f) bad++;   // (never further from the ask)
                if ((d - q) * (g - q) < -1e-6f) bad++;                    // (never past it: the law's torque is not added to)
                if (MathF.Abs(g - d) > MathF.Abs(oo) + 1e-4f) bad++;      // (never more removed than the offset there)
            }
        }
        Check(bad == 0, $"offset scale: {bad} random cases moved away from the ask, past it, or by more than the offset");
        FlightAssists.OffsetThrustScale = keep;
        // post-stall lift: 1 untouched; more kept, never above the stall's lift; the stall angle scaled
        Check(FlightAssists.Retain(0.4f, 1f) == 0.4f && FlightAssists.Stall(0.3f) == 0.3f, "assists at realistic: lift and stall untouched");
        FlightAssists.PostStallLift = 2f;
        Check(MathF.Abs(FlightAssists.Retain(0.4f, 1f) - 0.8f) < 1e-6f && FlightAssists.Retain(0.7f, 1f) == 1f && FlightAssists.Retain(-0.7f, 1f) == -1f && FlightAssists.Retain(1.2f, 1f) == 1.2f,
            "post-stall lift: x2 kept, capped at the stall's lift (never lowers one above it)");
        FlightAssists.PostStallLift = 1f;
        FlightAssists.StallAngleScale = 1.3f; Check(MathF.Abs(FlightAssists.Stall(0.3f) - 0.39f) < 1e-6f, "stall angle scale"); FlightAssists.StallAngleScale = 1f;
        // a surface past its stall: the authority scale multiplies only the post-stall change; under the stall nothing
        {
            var cs = new ControlSurface(Vector3.Zero, Vector3I.Zero, Vector3.UnitX, Vector3.UnitZ, 2.25f) { MaxDeflection = 20f, AspectRatio = 1f, Cd0 = 0.03f, Extents = new Vector3(1.5f, 0.5f, 1.5f) };
            Vector3 n0 = Vector3.Normalize(Vector3.Cross(Vector3.UnitX, Vector3.UnitZ));
            float D(float deg) { var c = Cond(-(Vector3.UnitZ * MathF.Cos(deg * D2R) + n0 * MathF.Sin(deg * D2R)) * 100f); return (Eval(cs, c, 1f, false).Force - Eval(cs, c, -1f, false).Force).Length(); }
            float d5 = D(5f), d60 = D(60f);
            FlightAssists.SurfaceAuthority = 2f;
            Check(MathF.Abs(D(5f) - d5) < 1e-3f * d5, "surface authority: attached flow untouched");
            Check(D(60f) > 1.8f * d60, $"surface authority x2: the post-stall change {D(60f):F0} N vs {d60:F0} N");
            FlightAssists.SurfaceAuthority = 1f;
        }
        // presets: the defaults ARE realistic; each preset matches itself; a change off them is custom
        Check(FlightAssists.Matching(0.25f) == FlightAssists.Realistic, "presets: the defaults are Realistic");
        for (int p = FlightAssists.Realistic; p <= FlightAssists.Forgiving; p++)
        {
            Check(FlightAssists.Preset(p, out var v), $"preset {p}: has values");
            bool sp = FlightAssists.StallProtection; float sa = FlightAssists.StallAngleScale, pl = FlightAssists.PostStallLift, au = FlightAssists.SurfaceAuthority, ot = FlightAssists.OffsetThrustScale;
            FlightAssists.StallProtection = v.Protection; FlightAssists.StallAngleScale = v.StallAngle; FlightAssists.PostStallLift = v.Lift; FlightAssists.SurfaceAuthority = v.Authority; FlightAssists.OffsetThrustScale = v.OffsetThrust;
            Check(FlightAssists.Matching(v.Gyro) == p, $"preset {FlightAssists.PresetNames[p]}: matches itself");
            Check(FlightAssists.Matching(v.Gyro + 0.05f) == FlightAssists.Custom, $"preset {FlightAssists.PresetNames[p]}: another gyro strength is custom");
            FlightAssists.StallProtection = sp; FlightAssists.StallAngleScale = sa; FlightAssists.PostStallLift = pl; FlightAssists.SurfaceAuthority = au; FlightAssists.OffsetThrustScale = ot;
        }
        Check(!FlightAssists.Preset(FlightAssists.Custom, out _), "preset Custom: no values of its own");
        FlightAssists.Preset(FlightAssists.Realistic, out var real);
        Check(!real.Protection && real.StallAngle == 1f && real.Lift == 1f && real.Authority == 1f && real.OffsetThrust == 1f && real.Gyro == GyroDefault, "preset Realistic: every field at the mod's default");
        Console.WriteLine($"\n== 4. flight assists: uncommanded offset torque, post-stall lift, stall angle, surface authority, presets checked");
    }
    const float GyroDefault = 0.25f;   // (GyroBalance.TorqueScale's default - SettingsTests checks the registry's against the field)
}
