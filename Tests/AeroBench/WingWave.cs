using System.IO;
using AeroMod;

// AeroBench gate: THE WINGS' OWN WAVE DRAG AND SUPERSONIC DRAG DUE TO LIFT (2026-10-18, fix a-wing-wave). Wings had no wave
// drag of their own (only the hull's one multiplier by streamlining) and their drag due to lift was CL^2/(pi e AR) at every
// Mach - full leading-edge suction, so supersonic lift came far too cheap. CompressibleWingModel.WingWaveDrag (Korn's drag
// divergence, Lock's rise, Ackeret on the normal Mach, transonic-similarity cap; less what the hull already charges on the
// wing's edge faces) and SupersonicLiftDrag (toward CL^2/CLa as the suction goes). Checks: Korn exactly; a thin swept wing
// under a thick straight one; supersonic CDi over the subsonic formula; no jumps across the blend bands; no double count on
// bench wings with real hull edges (a blocky square edge, a stepped/sloped one); finite everywhere; a mirrored wing the same.
//   dotnet run -c Release -- wingwave           (the gate's checks alone)
//   dotnet run -c Release -- wingwaveprobe      (the double-count measurement: hull edge charge vs theory on bench wings,
//                                                and the F-6's drag at CL 0.5, Mach 1.5 with the wing terms off / on)
//   dotnet run -c Release -- wingwavelist       (every fixture's wings: t/c detected vs own, edge Newtonian area)
static partial class Program
{
    /// <summary>A bench wing on the small grid (0.25 m cells): chord along X (cells 0..chord-1), span along Z, thickness
    /// along Y by chord station (thick(x): cells, centred on y 0).</summary>
    static List<(Vector3I, Vector3I)> BenchWing(int chord, int span, Func<int, int> thick)
    {
        var boxes = new List<(Vector3I, Vector3I)>();
        for (int x = 0; x < chord; x++)
        {
            int t = Math.Max(1, thick(x)), y0 = -(t / 2);
            boxes.Add((new Vector3I(x, y0, 0), new Vector3I(x, y0 + t - 1, span - 1)));
        }
        return boxes;
    }

    /// <summary>The bench wings of the measurement (24 x 60 cells: chord 6 m, span 15 m).</summary>
    static readonly (string name, Func<int, int> thick)[] BenchShapes =
    {
        ("blocky, 1 cell (t/c 0.04)", x => 1),
        ("blocky, 2 cells (t/c 0.08)", x => 2),
        ("blocky, 4 cells (t/c 0.17)", x => 4),
        ("stepped 1-2-3-4 cells over 2 cells each (t/c 0.13 own)", x => Math.Min(4, 1 + Math.Min(x, 23 - x) / 2)),
        ("stepped 1 -> 2 cells, 4 cells in (t/c 0.07 own)", x => Math.Min(x, 23 - x) < 4 ? 1 : 2),
    };

    /// <summary>A bench ship with its force table installed (the flight path: the hull's blend by its own streamlining).</summary>
    static Ship BenchShip(string name, List<(Vector3I, Vector3I)> boxes)
    {
        var ship = Build(name, boxes);
        var inner = (DampedShadowedDragModel)ship.Model.InnerModel;
        ship.Model.InstallForceTable(inner.BuildForceTable(ship.Grid, ship.Surface, ship.Manifold, ship.Com, 8));
        return ship;
    }

    /// <summary>Hull (the wing's edge faces: a bench wing alone has no other hull) and wing drag coefficients on the planform,
    /// flying toward -X at this Mach and angle of attack (deg) in sea-level air.</summary>
    static (float hull, float wing, WingForceResult wf) BenchDrag(Ship ship, float mach, float alphaDeg = 0f, Vector3? dir = null)
    {
        var atm = WallAir(SeaRho);
        float a = alphaDeg * MathF.PI / 180f;
        var d = dir ?? -Vector3.UnitX;
        var v = (d * MathF.Cos(a) - Vector3.UnitY * MathF.Sin(a)) * (mach * (float)atm.SpeedOfSound);
        var ctx = new AeroContext(ship.Grid, ship.Surface, v, atm, ship.Com, BlockSize, Vector3.Zero, -1f, ship.Manifold);
        ship.Model.Compute(ctx);
        float S = 0f; foreach (var w in ship.Model.Wings) S += w.PlanformArea;
        float q = 0.5f * SeaRho * v.LengthSquared() * S;
        return (ship.Model.LastInnerDrag / q, ship.Model.LastWingsDrag / q, ship.Model.LastWingForces[0]);
    }

    static int WingWaveList(string dataDir)
    {
        foreach (var path in Directory.GetFiles(dataDir, "*.boxes", SearchOption.AllDirectories))
        {
            var ship = Build(Path.GetFileNameWithoutExtension(path), LoadBoxes(path));
            Console.WriteLine($"   {ship.Name} (block {BlockSize}): {ship.Model.Wings?.Count ?? 0} wings");
            if (ship.Model.Wings != null)
                foreach (var w in ship.Model.Wings)
                    Console.WriteLine($"      {w}  chord {w.MeanChord:F1}  edge P/S {w.EdgeLo / w.PlanformArea:F3}/{w.EdgeHi / w.PlanformArea:F3}  own t/c {w.OwnThickness:F3}");
        }
        return 0;
    }

    static int WingWaveProbe(string dataDir)
    {
        float bs = BlockSize; BlockSize = 0.5f;
        try
        {
            Console.WriteLine("   THE DOUBLE-COUNT MEASUREMENT: drag coefficients on the planform, 0 deg, sea level. hull = the face model on the");
            Console.WriteLine("   wing's edge faces (its only hull faces); rise = hull over its Mach 0.5 level; est = the guard's estimate of that");
            Console.WriteLine("   rise; theory = the wing's thickness wave drag (Korn/Lock/Ackeret); CDw = what the wing now adds (theory - est, >= 0)");
            foreach (var (name, thick) in BenchShapes)
            {
                var ship = BenchShip("benchwing", BenchWing(24, 60, thick));
                var wings = ship.Model.Wings;
                if (wings == null || wings.Count == 0) { Console.WriteLine($"   {name}: no wing"); continue; }
                var wm = (CompressibleWingModel)ship.Model.LiftModel;
                float S = 0f; foreach (var w in wings) S += w.PlanformArea;
                Console.WriteLine($"   {name}: {wings[0]}, own t/c {wings[0].OwnThickness:F3}, edge Newtonian area {wings[0].EdgeLo:F2} m2");
                Console.WriteLine("      Mach   hull   rise    est  theory    CDw | total old  total new");
                float h05 = BenchDrag(ship, 0.5f).hull;
                foreach (float m in new[] { 0.5f, 0.7f, 0.8f, 0.9f, 1.0f, 1.1f, 1.2f, 1.5f, 2f, 3f })
                {
                    var (h, wg, wf) = BenchDrag(ship, m);
                    float est = wm.HullEdgeRise(wings[0], wf.Lead) / S;
                    float th = CompressibleWingModel.ThicknessWaveCd(m, wings[0].SweepFor(wf.Lead), CompressibleWingModel.WaveThickness(wings[0]), wf.CL);
                    var rw = WingTermsOff(); var (h0, w0, _) = BenchDrag(ship, m); rw();
                    Console.WriteLine($"      {m,4:F1}  {h,6:F4} {h - h05,6:F4} {est,6:F4} {th,6:F4} {wf.CDw,6:F4} |   {h0 + w0,7:F4}    {h + wg,7:F4}");
                }
            }

            // the F-6 at CL 0.5 (its wings' lift over q x their area), Mach 1.5, sea level: a sustained turn's drag
            var path = Directory.GetFiles(dataDir, "3737149863.boxes", SearchOption.AllDirectories).FirstOrDefault();
            if (path != null)
            {
                var boxes = LoadBoxes(path);
                var f6 = Build("F-6", boxes);
                var table = ((DampedShadowedDragModel)f6.Model.InnerModel).BuildForceTable(f6.Grid, f6.Surface, f6.Manifold, f6.Com, 8);
                var air = new AtmosphereState { Density = 1.225, Pressure = 101325, Temperature = 288, SpeedOfSound = 340 };
                var (nose, up) = FlightAxes(f6, air, boxes);
                float S = 0f; foreach (var w in f6.Model.Wings) S += w.PlanformArea;
                foreach (float mach in new[] { 0.6f, 1.5f })
                {
                    (float cl, float wingD, float totalD, float cdi, float cdw) At(float alphaDeg)
                    {
                        var atm = WallAir(SeaRho);
                        float a = alphaDeg * MathF.PI / 180f;
                        var v = (nose * MathF.Cos(a) - up * MathF.Sin(a)) * (mach * (float)atm.SpeedOfSound);
                        var ctx = new AeroContext(null, null, v, atm, f6.Com);
                        var inner = (DampedShadowedDragModel)f6.Model.InnerModel;
                        var r = table.Evaluate(ctx, inner.SubsonicLimit, inner.SupersonicLimit, inner.Streamlining);
                        var wm = new CompressibleWingModel(); f6.Model.HandHullEdges(wm, ctx, table);
                        var wf = wm.ComputeWingForces(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(f6.Model.Wings), ctx);
                        var vh = Vector3.Normalize(v); float q = 0.5f * SeaRho * v.LengthSquared();
                        float L = 0f, Dw = 0f, cdi = 0f, cdw = 0f;
                        for (int i = 0; i < wf.Count; i++) { L += Vector3.Dot(wf[i].LiftForce, up); Dw += -Vector3.Dot(wf[i].InducedDrag, vh); cdi += wf[i].CDi * f6.Model.Wings[i].PlanformArea / S; cdw += wf[i].CDw * f6.Model.Wings[i].PlanformArea / S; }
                        return (L / (q * S), Dw, Dw - Vector3.Dot(r.Force, vh), cdi, cdw);
                    }
                    (float, float, float, float, float, float) AtCl(float target)
                    {
                        float lo = -5f, hi = 25f;
                        for (int k = 0; k < 40; k++) { float mid = 0.5f * (lo + hi); if (At(mid).cl < target) lo = mid; else hi = mid; }
                        var x = At(0.5f * (lo + hi)); return (0.5f * (lo + hi), x.cl, x.wingD, x.totalD, x.cdi, x.cdw);
                    }
                    var nowR = AtCl(0.5f);
                    var rw = WingTermsOff(); var oldR = AtCl(0.5f); rw();
                    Console.WriteLine($"   F-6 at wing CL 0.5, Mach {mach}, sea level: alpha {oldR.Item1:F2} deg; wing drag {oldR.Item3 / 1e3f:F0} -> {nowR.Item3 / 1e3f:F0} kN (CDi {oldR.Item5:F4} -> {nowR.Item5:F4}, CDw {oldR.Item6:F4} -> {nowR.Item6:F4}), whole ship {oldR.Item4 / 1e3f:F0} -> {nowR.Item4 / 1e3f:F0} kN ({nowR.Item4 / oldR.Item4:F2}x)");
                }
            }
        }
        finally { BlockSize = bs; }
        return 0;
    }

    static int WingWaveChecks()
    {
        int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; Console.WriteLine($"   {(ok ? "ok  " : "FAIL")} {what}"); }
        float deg = MathF.PI / 180f;

        // (a) Korn exactly: M_dd cos L = 0.87 - t/c / cos L - CL / (10 cos^2 L); falls with t/c, rises with sweep
        {
            float worst = 0f; bool mono = true;
            foreach (float cl in new[] { 0f, 0.3f, 0.6f })
                foreach (float sw in new[] { 0f, 15f, 30f, 45f, 60f })
                    foreach (float tc in new[] { 0.04f, 0.08f, 0.12f, 0.16f })
                    {
                        // (the normal M_dd floored at 0.4, as documented: a slab thicker than the relation knows)
                        double c = Math.Cos(sw * deg), want = Math.Max(0.4, 0.87 - tc / c - cl / (10 * c * c)) / c;
                        worst = MathF.Max(worst, MathF.Abs(CompressibleWingModel.KornMdd(tc, sw * deg, cl) - (float)want));
                        float k0 = CompressibleWingModel.KornMdd(tc, sw * deg, cl);
                        if (CompressibleWingModel.KornMdd(tc + 0.02f, sw * deg, cl) > k0 + 1e-7f) mono = false;
                        // (rises with sweep up to CL 0.3: at CL 0.6 and 45 -> 60 deg Korn's own lift term wins - its value checked above)
                        if (sw < 60f && cl <= 0.3f && CompressibleWingModel.KornMdd(tc, (sw + 15f) * deg, cl) < k0 - 1e-7f) mono = false;
                    }
            // the onset: Lock's slope 0.1 at M_dd (t/c 0.10, straight, CL 0.2: M_dd 0.75 - the cap far above)
            float mdd = CompressibleWingModel.KornMdd(0.10f, 0f, 0.2f), h = 1e-3f;
            float slope = (CompressibleWingModel.ThicknessWaveCd(mdd + h, 0f, 0.10f, 0.2f) - CompressibleWingModel.ThicknessWaveCd(mdd - h, 0f, 0.10f, 0.2f)) / (2 * h);
            float atMdd = CompressibleWingModel.ThicknessWaveCd(mdd, 0f, 0.10f, 0.2f);
            Check(worst < 1e-5f && mono && MathF.Abs(slope - 0.1f) < 0.003f && MathF.Abs(atMdd - 20f * MathF.Pow(0.10772f, 4f)) < 1e-5f,
                $"wing wave: M_dd per Korn (worst {worst:E1} off over t/c 0.04-0.16, sweep 0-60, CL 0-0.6; falls with t/c, rises with sweep (CL <= 0.3): {mono}); t/c 0.10 CL 0.2: M_dd {mdd:F3}, dCd/dM there {slope:F3} (Lock: 0.1), Cd {atMdd:F4} (0.0027)");
        }

        // a hand-built wing (no hull edges: the full theory value) through the real model at alpha (deg), Mach
        static LiftingSurface HandWing(float tc, float sweepDeg, float ar = 3f) =>
            new LiftingSurface(Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ, 30f, MathF.Sqrt(30f * ar), tc, sweepDeg * MathF.PI / 180f, sweepDeg * MathF.PI / 180f, Vector3.Zero, 0.25f * 30f / MathF.Sqrt(30f * ar), 120);
        static WingForceResult Run(in LiftingSurface w, float mach, float alphaDeg)
        {
            var atm = new AtmosphereState { Density = 1.225, Pressure = 101325, Temperature = 288, SpeedOfSound = 340 };
            float a = alphaDeg * MathF.PI / 180f;
            var v = new Vector3(0, -MathF.Sin(a), -MathF.Cos(a)) * (mach * 340f);   // (toward -ChordAxis: the low end leads)
            var ctx = new AeroContext(null, null, v, atm, Vector3.Zero, 0.5f, Vector3.Zero, -1f, null, Vector3.Zero);
            return new CompressibleWingModel().ComputeWingForces(new[] { w }, ctx)[0];
        }

        // (b) a thin swept wing has less wave drag than a thick straight one
        {
            var thin = HandWing(0.05f, 45f); var thick = HandWing(0.15f, 0f);
            bool ok = true; var sb = new System.Text.StringBuilder();
            foreach (float m in new[] { 0.9f, 1.2f, 2f })
            {
                float a = Run(thin, m, 0f).CDw, b = Run(thick, m, 0f).CDw;
                ok &= a < b && b > 0f; sb.Append($" M{m}: {a:F4} vs {b:F4};");
            }
            Check(ok, $"wing wave: thin swept (t/c 0.05, 45 deg) under thick straight (t/c 0.15, 0 deg) -{sb}");
        }

        // (c) supersonic drag due to lift over CL^2/(pi e AR) at the same CL; ~CL^2/CLa with a supersonic leading edge
        {
            var w = HandWing(0.06f, 0f);
            float e = Math.Clamp(w.OswaldE, 0.3f, 0.95f), kSub = 1f / (MathF.PI * e * w.AspectRatio);
            bool ok = true; var sb = new System.Text.StringBuilder();
            foreach (float m in new[] { 1.5f, 1.75f, 2f })
            {
                var r = Run(w, m, 3f);
                float sub = r.CL * r.CL * kSub, cla = CompressibleWingModel.ComputeCLAlpha(m, w.AspectRatio, 0f, 0.85f, 1.15f), nos = r.CL * r.CL / cla;
                ok &= r.CDi > 1.5f * sub && MathF.Abs(r.CDi - nos) < 0.01f * nos;
                sb.Append($" M{m}: CL {r.CL:F3} CDi {r.CDi:F4} (CL^2/(pi e AR) {sub:F4}, CL^2/CLa {nos:F4});");
            }
            // subsonic: the old formula exactly; switched off: the old formula at every Mach
            var s05 = Run(w, 0.5f, 3f);
            ok &= MathF.Abs(s05.CDi - s05.CL * s05.CL * kSub) < 1e-6f;
            CompressibleWingModel.SupersonicLiftDrag = false;
            var off = Run(w, 2f, 3f);
            CompressibleWingModel.SupersonicLiftDrag = true;
            ok &= MathF.Abs(off.CDi - off.CL * off.CL * kSub) < 1e-6f;
            // a subsonic leading edge (60 deg at Mach 1.5: M_perp 0.75) keeps part of its suction: between the two
            var sw = HandWing(0.04f, 60f); var rs = Run(sw, 1.5f, 3f);
            float eS = Math.Clamp(sw.OswaldE, 0.3f, 0.95f), subS = rs.CL * rs.CL / (MathF.PI * eS * sw.AspectRatio);
            float claS = CompressibleWingModel.ComputeCLAlpha(0.75f, sw.AspectRatio, 60f * deg, 0.85f, 1.15f), nosS = rs.CL * rs.CL / claS;
            ok &= rs.CDi > subS && rs.CDi < nosS;
            Check(ok, $"wing lift drag: supersonic CDi over the subsonic formula -{sb} subsonic leading edge (60 deg, M1.5): {rs.CDi:F5} between {subS:F5} and {nosS:F5}; M0.5 and switched off: CL^2/(pi e AR) exactly");
        }

        // (d) continuity: no jumps across the blend bands (fine Mach sweeps)
        {
            static float WorstJump(Func<float, float> f, float m0, float m1, float dm)
            {
                // a jump: one step's change above 3x both neighbours' - by how much (a kink, the slope changing at a
                // band's edge, passes: its steps are all small; a jump is one step of its own size)
                float worst = 0f; float a = f(m0), b = f(m0 + dm), c = f(m0 + 2 * dm);
                for (float m = m0 + 3 * dm; m <= m1; m += dm)
                {
                    float d = f(m), d1 = MathF.Abs(b - a), d2 = MathF.Abs(c - b), d3 = MathF.Abs(d - c);
                    worst = MathF.Max(worst, d2 - 3f * MathF.Max(d1, d3));
                    a = b; b = c; c = d;
                }
                return worst;
            }
            float worst = 0f, maxStep = 0f;
            foreach (float tc in new[] { 0.03f, 0.08f, 0.15f, 0.2f })
                foreach (float sw in new[] { 0f, 30f, 55f })
                    foreach (float cl in new[] { 0f, 0.5f })
                    {
                        Func<float, float> f = m => CompressibleWingModel.ThicknessWaveCd(m, sw * deg, tc, cl);
                        worst = MathF.Max(worst, WorstJump(f, 0.3f, 3.5f, 5e-4f));
                        for (float m = 0.3f; m < 3.5f; m += 5e-4f) maxStep = MathF.Max(maxStep, MathF.Abs(f(m + 5e-4f) - f(m)));
                    }
            // the whole wing (drag due to lift and wave, the model's blends) at 4 deg, straight and swept
            float worstW = 0f;
            foreach (float sw in new[] { 0f, 40f })
            {
                var w = HandWing(0.08f, sw);
                worstW = MathF.Max(worstW, WorstJump(m => { var r = Run(w, m, 4f); return r.CDi + r.CDw; }, 0.5f, 3f, 1e-3f));
            }
            // and with real hull edges (the guard's blend: the hull's own law), a stepped bench wing
            float bs = BlockSize; BlockSize = 0.5f;
            float worstH;
            try { var ship = BenchShip("benchwing", BenchWing(24, 60, BenchShapes[4].thick)); worstH = WorstJump(m => BenchDrag(ship, m, 2f).wing, 0.5f, 3f, 2e-3f); }
            finally { BlockSize = bs; }
            Check(worst < 1e-4f && maxStep < 0.003f && worstW < 1e-4f && worstH < 5e-4f,
                $"wing wave/lift drag continuous (no step above 3x its neighbours by more than 1e-4 / 5e-4): theory over Mach 0.3-3.5 per 0.0005 (t/c 0.03-0.2, sweep 0-55, CL 0/0.5) worst {worst:E1}, largest step {maxStep:F5}; the model at 4 deg per 0.001 {worstW:E1}; with hull edges per 0.002 {worstH:E1}");
        }

        // (e) no double count: bench wings with real hull edges at Mach 2
        {
            float bs = BlockSize; BlockSize = 0.5f;
            try
            {
                var blocky = BenchShip("benchwing", BenchWing(24, 60, BenchShapes[1].thick));
                var (h, wg, wf) = BenchDrag(blocky, 2f);
                var rw = WingTermsOff(); var (h0, w0, _) = BenchDrag(blocky, 2f); rw();
                var wm = (CompressibleWingModel)blocky.Model.LiftModel;
                float th = CompressibleWingModel.ThicknessWaveCd(2f, 0f, CompressibleWingModel.WaveThickness(blocky.Model.Wings[0]), wf.CL);
                bool okB = (h + wg) - (h0 + w0) <= 0.1f * th && h + wg >= th;
                Check(okB, $"wing wave, no double count: blocky square-edged bench wing (t/c {CompressibleWingModel.WaveThickness(blocky.Model.Wings[0]):F3}) at Mach 2: hull edges {h:F4} + wing {wg:F4} = {h + wg:F4} (theory thickness drag {th:F4}: the square edges charge {h / th:F1}x it already); the wing adds {(h + wg) - (h0 + w0):F4} (<= 10 % of theory)");

                var stepped = BenchShip("benchwing", BenchWing(24, 60, BenchShapes[4].thick));
                var (hs, ws, wfs) = BenchDrag(stepped, 2f);
                float hs05 = BenchDrag(stepped, 0.5f).hull;
                var wms = (CompressibleWingModel)stepped.Model.LiftModel;
                float ths = CompressibleWingModel.ThicknessWaveCd(2f, 0f, CompressibleWingModel.WaveThickness(stepped.Model.Wings[0]), wfs.CL);
                float cd0 = 2.4f * DampedShadowedDragModel.TurbulentCf(stepped.Model.Wings[0].MeanChord);
                float expect = hs05 + cd0 + ths, ratio = (hs + ws) / expect;
                var rw2 = WingTermsOff(); var (hs0, ws0, _) = BenchDrag(stepped, 2f); rw2();
                Check(ratio > 0.85f && ratio < 1.3f && hs + ws > hs0 + ws0,
                    $"wing wave, no double count: stepped bench wing (t/c {CompressibleWingModel.WaveThickness(stepped.Model.Wings[0]):F3}) at Mach 2: hull edges {hs:F4} + wing {ws:F4} = {hs + ws:F4} against its subsonic edge drag + skins + theory = {expect:F4} ({ratio:F2}x; before {hs0 + ws0:F4})");
            }
            finally { BlockSize = bs; }
        }

        // (f) finite everywhere, a mirrored wing the same
        {
            var rng = new Random(18);
            bool fin = true;
            for (int k = 0; k < 20000; k++)
            {
                float m = (float)(rng.NextDouble() * 10), sw = (float)(rng.NextDouble() * 1.6), tc = (float)(rng.NextDouble() * 0.6), cl = (float)(rng.NextDouble() * 6 - 3);
                float v = CompressibleWingModel.ThicknessWaveCd(m, sw, tc, cl);
                fin &= float.IsFinite(v) && v >= 0f;
            }
            fin &= CompressibleWingModel.ThicknessWaveCd(float.NaN, 0f, 0.1f, 0f) == 0f && CompressibleWingModel.ThicknessWaveCd(1.2f, float.NaN, 0.1f, 0f) == 0f && CompressibleWingModel.ThicknessWaveCd(1.2f, 0f, float.NaN, 0f) == 0f;
            foreach (float tc in new[] { 0f, 0.5f })
                foreach (float sw in new[] { 0f, 85f })
                    foreach (float m in new[] { 0.99f, 1f, 1.0001f, 5f, 10f })
                        foreach (float a in new[] { 0f, 10f, 40f, 89f })
                        {
                            var r = Run(HandWing(tc, sw), m, a);
                            fin &= float.IsFinite(r.CDi) && float.IsFinite(r.CDw) && float.IsFinite(r.InducedDrag.X + r.InducedDrag.Y + r.InducedDrag.Z) && r.CDw >= 0f && r.CDi >= 0f;
                        }
            // the swept, tapered wing of the wing-edge gate built mirrored, through the real detector, hull edges and model
            float bs = BlockSize; BlockSize = 0.5f;
            float worst = 0f, worstW = 0f; var sb = new System.Text.StringBuilder();
            try
            {
                // (and a wing stepped at one end only - its leading edge a ramp, its trailing edge square: the guard's edge
                //  areas differ by which end leads - mirrored along its chord)
                var ramp = BenchWing(24, 60, x => x < 4 ? 1 : 2);
                var rampM = ramp.Select(b => (new Vector3I(-b.Item2.X - 1, b.Item1.Y, b.Item1.Z), new Vector3I(-b.Item1.X - 1, b.Item2.Y, b.Item2.Z))).ToList();
                foreach (var (o, mi, nm) in new[] { (BenchShip("edgewing", EdgeWing(false, false)), BenchShip("edgewing-m", EdgeWing(true, false)), "swept"),
                                                    (BenchShip("ramp", ramp), BenchShip("ramp-m", rampM), "ramp-led") })
                    foreach (float m in new[] { 0.9f, 1.2f, 2f })
                    {
                        var (ho, wo, fo) = BenchDrag(o, m, 3f, -Vector3.UnitX);   // (toward its leading edge: -X, the mirror +X)
                        var (hm, wmi, fm) = BenchDrag(mi, m, 3f, Vector3.UnitX);
                        // (the wing's drag but its wave drag, and its CDi, to 0.1 %; its CDw to 2e-4 - the guard reads the hull's streamlining along the
                        //  flow, and the hull's own face model is not exactly mirror-symmetric: up to 6 % apart here, pre-existing)
                        float d = MathF.Max(MathF.Abs((wo - fo.CDw) - (wmi - fm.CDw)) / MathF.Max(1e-6f, wo - fo.CDw), MathF.Abs(fo.CDi - fm.CDi) / MathF.Max(1e-6f, fo.CDi));
                        worst = MathF.Max(worst, d); worstW = MathF.Max(worstW, MathF.Abs(fo.CDw - fm.CDw));
                        sb.Append($" {nm} M{m}: wing {wo:F4}/{wmi:F4} CDw {fo.CDw:F4}/{fm.CDw:F4} (hull {ho:F4}/{hm:F4});");
                    }
            }
            finally { BlockSize = bs; }
            Check(fin, "wing wave/lift drag finite everywhere (20000 random states, Mach 0-10, sweep 0-92 deg, t/c 0-0.6, CL +-3; NaN in: 0; the model to 89 deg and Mach 10)");
            Check(worst < 1e-3f && worstW < 2e-4f, $"wing wave/lift drag mirrored: the same (wing drag less CDw, CDi: worst {worst:E1} relative; CDw {worstW:E1} apart) -{sb}");
        }
        return fails;
    }
}
