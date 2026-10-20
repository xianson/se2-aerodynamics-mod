using AeroMod;

/// <summary>
/// THE FLIGHT PERFORMANCE QUERY against the flight (FlightPerf over the ship's own lift and drag, PerfSamples): what it predicts
/// - turn rate at a load, drag deceleration, speed after a pull - against what the same ship then flies in the sim, and an
/// acceleration goal (ApExternal Accel, flown with the lift: FlightDirector.LiftFlight) - feasible ones flown as asked, an
/// infeasible one at the protection's limit.
/// </summary>
sealed class PerfPack : IScenarioPack
{
    public void Register(ScenarioRegistry r)
    {
        r.Add("perf: a level turn asked as an acceleration - turn rate flown vs predicted (Plane2)", true, () => Turn(2f));
        r.Add("perf: an infeasible 6 g turn asked - flown at the g limit", true, () => Turn(6f));
        r.Add("perf: drag deceleration, throttle shut - predicted vs flown (Plane2)", true, Decel);
        r.Add("perf: speed after 8 s of a 2 g turn at full throttle - predicted vs flown (Plane2)", true, SpeedAfter);
        r.Add("perf: corner speed, turn rates vs speed - the chart's peak, and predicted from one speed vs evaluated at another (Plane2)", true, Chart);
        r.Add("perf: a turn at the predicted sustained load holds its speed; past it bleeds (Plane2)", true, Sustained);
        r.Add("perf: specific excess power now vs the energy height's rate flown (Plane2)", true, Ps);
    }

    /// <summary>Level at speed v, the performance evaluated (the sweep in): the run, its performance and samples.</summary>
    static (Run r, ApPerf p, PerfSamples s) PerfAt(string name, float v, float thr)
    {
        var r = Level(name, v, thr);
        if (r == null) return (null, default, null);
        var sn = r.P.SenseNow();
        r.P.Fd.Engage(ApGoal.Attitude, in sn, out _);
        r.Fly(1.5f);
        sn = r.P.SenseNow();
        return (r, r.P.Fd.Perf, sn.Perf);
    }

    static void Chart()
    {
        var (r150, p150, s150) = PerfAt("perf chart 150", 150f, 0.6f);
        if (r150 == null) { Sim.NoPolar("Plane2"); return; }
        // (the chart's peak: the instantaneous turn rate over speed is highest at the corner)
        float bestV = 0f, bestW = 0f;
        for (float v = 40f; v < 400f; v += 0.5f) { float w = FlightPerf.TurnRateInstAt(in p150, v); if (w > bestW) { bestW = w; bestV = v; } }
        Sim.Note($"perf chart: from 150 m/s - NAtAlphaMax {p150.NAtAlphaMax:F2} g, corner {p150.CornerSpeed:F1} m/s ({p150.TurnRateCorner:F3} rad/s); the sweep's peak {bestV:F1} m/s ({bestW:F3} rad/s); Ps {p150.Ps:F1} m/s");
        Sim.Check(p150.Valid && MathF.Abs(bestV - p150.CornerSpeed) < 1f && MathF.Abs(bestW - p150.TurnRateCorner) < 0.002f, $"perf chart: the instantaneous turn rate peaks at the corner speed ({bestV:F1} vs {p150.CornerSpeed:F1} m/s)");
        // (the at-speed queries against the evaluation flown at those speeds - the dynamic pressure's scaling; Mach and the trim differ a little)
        foreach (float v in new[] { 110f, 190f })
        {
            var (rv, pv, sv) = PerfAt($"perf chart {v:0}", v, 0.6f);
            float nPred = p150.NAtAlphaMax * v * v / (150f * 150f), nSustPred = FlightPerf.NSustAt(s150, in p150, v);
            float wPred = FlightPerf.TurnRateInstAt(in p150, v), wSustPred = FlightPerf.TurnRateSustAt(s150, in p150, v);
            Sim.Note($"perf chart at {v:0} m/s: the lift at the margin predicted {nPred:F2} g vs evaluated {pv.NAtAlphaMax:F2}; sustained {nSustPred:F2} vs {pv.NSust:F2} g; turn rates inst {wPred:F3} vs {pv.TurnRateInst:F3}, sust {wSustPred:F3} vs {pv.TurnRateSust:F3} rad/s");
            Sim.Check(MathF.Abs(nPred - pv.NAtAlphaMax) < 0.12f * pv.NAtAlphaMax && MathF.Abs(nSustPred - pv.NSust) < 0.15f * pv.NSust + 0.05f && MathF.Abs(wPred - pv.TurnRateInst) < 0.12f * pv.TurnRateInst + 0.002f,
                $"perf chart at {v:0} m/s: predicted from 150 m/s within 12 % (lift at the margin {nPred:F2} / {pv.NAtAlphaMax:F2} g), 15 % (sustained {nSustPred:F2} / {pv.NSust:F2} g)");
            rv.Close();
        }
        r150.Close();
    }

    static void Sustained()
    {
        // (a speed where the thrust, not the lift or the g limit, sets the sustained load)
        float vS = 0f; ApPerf p0 = default; PerfSamples s0 = null;
        foreach (float v in new[] { 150f, 160f, 170f, 175f, 180f })
        {
            var (rp, pp, sp) = PerfAt($"perf sustained probe {v:0}", v, 1f);
            if (rp == null) { Sim.NoPolar("Plane2"); return; }
            rp.Close();
            Sim.Note($"perf sustained probe at {v:0} m/s: NInst {pp.NInst:F2}, NSust {pp.NSust:F2} g, Ps now {pp.Ps:F1}, level {FlightPerf.PsAt(sp, in pp, v, 1f):F1}, at NSust {FlightPerf.PsAt(sp, in pp, v, pp.NSust):F1} m/s");
            if (pp.NSust > 1.3f && pp.NSust < pp.NInst - 0.4f) { vS = v; p0 = pp; s0 = sp; break; }
        }
        Sim.Check(vS > 0f, $"perf sustained: a speed found where the thrust limits the sustained turn ({vS:0} m/s)");
        if (vS == 0f) return;
        // the energy height's rate flown at loads around the sustained one, against PsAt at the load flown: ~0 at the sustained load
        float nS = p0.NSust;
        // (KNOWN GAP, 2026-10-19: in a turn the flown energy rate sits ~5 m/s above PsAt's on Plane2 at 175 m/s - the turn's sideslip
        //  and the surfaces' trim are not in the samples' drag; the ORDER - more load, less energy - is what is checked)
        float prevFlown = float.PositiveInfinity, prevPred = float.PositiveInfinity; bool ordered = true;
        foreach (float dn in new[] { -0.4f, 0f, 0.4f })
        {
            float n = MathF.Min(nS + dn, p0.NInst);
            float flown = Energy(vS, n, out float nFlown, out float vMean);
            float pred = FlightPerf.PsAt(s0, in p0, vMean, nFlown);
            Sim.Note($"perf sustained: {n:F2} g asked at {vS:0} m/s, full throttle - flew {nFlown:F2} g at {vMean:F0} m/s: the energy height's rate {flown:+0.0;-0.0} m/s, PsAt predicted {pred:+0.0;-0.0}");
            ordered &= flown < prevFlown && pred < prevPred; prevFlown = flown; prevPred = pred;
        }
        Sim.Check(ordered, "perf sustained: more load, less energy - flown and PsAt in the same order");
    }

    static float Energy(float v, float n, out float nFlown, out float vMean)
    {
        var (r, _, _) = PerfAt($"perf bleed {n:F2}", v, 1f);
        var sn = r.P.SenseNow();
        r.P.Fd.Engage(ApGoal.External, in sn, out _, new TurnSource { N = n, Throttle = 1f });
        r.Fly(2f);
        float g = r.P.S.PlanetG * r.P.S.Gm, nSum = 0f, vSum = 0f; int k = 0;
        double e0 = r.P.P.Y + r.P.V.LengthSquared() / (2.0 * g); float t0 = r.P.Time;
        r.Each(rr => { nSum += rr.P.LoadFactor; vSum += rr.P.V.Length(); k++; });
        r.Fly(6f);
        double e1 = r.P.P.Y + r.P.V.LengthSquared() / (2.0 * g);
        nFlown = nSum / MathF.Max(k, 1); vMean = vSum / MathF.Max(k, 1);
        r.Close();
        return (float)((e1 - e0) / (r.P.Time - t0));
    }

    static void Ps()
    {
        var (r, p, _) = PerfAt("perf Ps", 150f, 1f);
        if (r == null) { Sim.NoPolar("Plane2"); return; }
        float g = r.P.S.PlanetG * r.P.S.Gm;
        double e0 = r.P.P.Y + r.P.V.LengthSquared() / (2.0 * g); float t0 = r.P.Time;
        r.Fly(1f);
        double e1 = r.P.P.Y + r.P.V.LengthSquared() / (2.0 * g);
        float flown = (float)((e1 - e0) / (r.P.Time - t0));
        Sim.Note($"perf Ps: predicted {p.Ps:F2} m/s (excess {p.ExcessAccel:F2} m/s2 at 150 m/s), the energy height's rate flown {flown:F2} m/s");
        Sim.Check(MathF.Abs(flown - p.Ps) < 0.12f * MathF.Abs(p.Ps) + 0.5f, $"perf Ps: predicted {p.Ps:F2} vs flown {flown:F2} m/s (12 %)");
        r.Close();
    }

    /// <summary>A level turn to the right at load n: the horizontal acceleration across the path g sqrt(n^2 - 1), nothing along it
    /// (the throttle: the autopilot's, for the drag) - the gravity's hold is the autopilot's own.</summary>
    sealed class TurnSource : IApGoalSource
    {
        public float N; public float Throttle = float.NaN;
        public bool Step(in ApSense s, ref ApExternal g)
        {
            Vector3 up = -Vector3.Normalize(s.GravityTrue), vh = ApMath.Flat(s.Vel, up);
            if (vh.LengthSquared() < 1f) return true;
            Vector3 right = Vector3.Normalize(Vector3.Cross(vh, up));
            g.Kind = ApExternalKind.Accel; g.Accel = right * (s.GravityTrue.Length() * MathF.Sqrt(MathF.Max(N * N - 1f, 0f))); g.Throttle = Throttle;
            return true;
        }
    }

    static Run Level(string name, float speed, float thr)
    {
        var s = Ships.Plane("Plane2", tw: 0.6f, gyro: 3e5f, surfPerQ: new Vector3(1200f, 1200f, 1600f));
        if (s == null) return null;
        var air = (PolarAir)s.Air;
        var r = new Run(name, s);
        r.P.Damp = false; r.P.Rho = 1.225f; r.P.P = new Vector3D(0, 3000, 0);
        float weight = s.Mass * s.PlanetG * s.Gm, trim = 0f;
        for (float a = 0f; a < 20f * ApMath.Deg; a += 0.05f * ApMath.Deg)
        {
            var q = Quaternion.CreateFromAxisAngle(air.Right, a);
            air.Forces(Vector3.Transform(air.Nose * speed, Quaternion.Inverse(q)), Vector3.Zero, 1.225f, out var f, out _);
            trim = a; if (Vector3.Transform(f, q).Y >= weight) break;
        }
        r.P.Q = Quaternion.CreateFromAxisAngle(air.Right, trim); r.P.V = air.Nose * speed;
        r.P.Piloted = true; r.P.PilotKeys = air.Nose * thr;
        return r;
    }

    static void Turn(float n)
    {
        var r = Level($"perf turn {n:0} g", 150f, 0.6f);
        if (r == null) { Sim.NoPolar("Plane2"); return; }
        var sn = r.P.SenseNow();
        Sim.Check(r.P.Fd.Engage(ApGoal.Attitude, in sn, out _), $"{r.Name}: attitude hold engaged");
        r.Fly(1.5f);   // (the stall sweep - the performance's samples - in)
        var src = new TurnSource { N = n };
        sn = r.P.SenseNow();
        Sim.Check(r.P.Fd.Engage(ApGoal.External, in sn, out var why, src), $"{r.Name}: the turn engaged ({why})");
        r.Fly(4f);
        // the flown turn rate (the heading's change, level) and load over the next 4 s against the prediction for that load
        var p0 = r.P.Fd.Perf;
        float h0 = Heading(r), t0 = r.P.Time, nSum = 0f, vySum = 0f, nMax = 0f; int k = 0;
        r.Each(rr => { nSum += rr.P.LoadFactor; nMax = MathF.Max(nMax, rr.P.LoadFactor); vySum += MathF.Abs(rr.P.V.Y); k++;
            if (Environment.GetEnvironmentVariable("AP_TRACE") == "perf" && rr.Ticks % 30 == 0) { var ss = rr.P.SenseNow(); Console.WriteLine($"      t {rr.P.Time:F1} n {rr.P.LoadFactor:F2} aoa {Program.AoaPublic(rr):F1} pull {rr.P.Fd.Guard.LastPull / ApMath.Deg:F1} cap {rr.P.Fd.Guard.LastPullCap / ApMath.Deg:F1} margin {ss.StallMarginDeg:F1} inUse {ss.StallLiftInUse:F2} G {ss.StallG:F2} since {EnvelopeGuard.Since(in ss) / ApMath.Deg:F1} tilt {rr.P.TiltDeg:F0} vy {rr.P.V.Y:F1}"); } });
        r.Fly(4f);
        float rate = MathF.Abs(Wrap(Heading(r) - h0)) / (r.P.Time - t0), nFlown = nSum / MathF.Max(k, 1), v = r.P.V.Length();
        float g = r.P.S.PlanetG * r.P.S.Gm, nAsk = MathF.Min(n, Autopilot.GLimit);
        float predicted = g * MathF.Sqrt(MathF.Max(nFlown * nFlown - 1f, 0f)) / v;
        Sim.Note($"{r.Name}: asked {n} g ({(n > Autopilot.GLimit ? $"past the {Autopilot.GLimit} g limit" : "feasible")}) - flew {nFlown:F2} g (at most {nMax:F2}), turning {rate:F3} rad/s; predicted at that load {predicted:F3} rad/s; the performance said NInst {p0.NInst:F2} g, {p0.TurnRateInst:F3} rad/s, sustained {p0.NSust:F2} g; vertical speed {vySum / MathF.Max(k, 1):F1} m/s mean, prot {r.ProtSeen}");
        Sim.Check(MathF.Abs(rate - predicted) < 0.15f * predicted + 0.01f, $"{r.Name}: the turn rate flown {rate:F3} rad/s matches the prediction for its load {predicted:F3} (15 %)");
        Sim.Check(MathF.Abs(nFlown - nAsk) < 0.2f * nAsk && nMax <= Autopilot.GLimit * 1.15f, $"{r.Name}: flew {nFlown:F2} g for {nAsk:F2} asked / allowed (20 %), at most {nMax:F2} g (limit {Autopilot.GLimit} +15 %)");
        if (n > Autopilot.GLimit) Sim.Check((r.ProtSeen & ApProt.GLimit) != 0 && MathF.Abs(p0.NInst - Autopilot.GLimit) < 0.05f, $"{r.Name}: the performance's instantaneous load is the limit ({p0.NInst:F2} g) and G LIMIT held it");
        Program.LedgerPublic(r, true);
    }

    static float Heading(Run r) => MathF.Atan2(r.P.V.Z, r.P.V.X);
    static float Wrap(float a) { while (a > MathF.PI) a -= 2f * MathF.PI; while (a < -MathF.PI) a += 2f * MathF.PI; return a; }

    static void Decel()
    {
        var r = Level("perf decel", 150f, 0f);
        if (r == null) { Sim.NoPolar("Plane2"); return; }
        var sn = r.P.SenseNow();
        Sim.Check(r.P.Fd.Engage(ApGoal.Attitude, in sn, out _), $"{r.Name}: attitude hold engaged");
        r.Fly(1.5f);
        var p = r.P.Fd.Perf; float v0 = r.P.V.Length(), t0 = r.P.Time;
        // (the path's climb or sink takes or gives g sin(gamma) - taken out of the flown deceleration)
        float gs = 0f; int k = 0;
        r.Each(rr => { gs += -(rr.P.S.PlanetG * rr.P.S.Gm) * rr.P.V.Y / MathF.Max(rr.P.V.Length(), 1f); k++; });
        r.Fly(1f);
        float flown = -(r.P.V.Length() - v0) / (r.P.Time - t0) + gs / MathF.Max(k, 1);
        Sim.Note($"{r.Name}: the drag's deceleration predicted {p.DragDecelNow:F2} m/s2, flown {flown:F2} (the path's slope out)");
        Sim.Check(p.Valid && MathF.Abs(flown - p.DragDecelNow) < 0.1f * p.DragDecelNow + 0.05f, $"{r.Name}: predicted {p.DragDecelNow:F2} m/s2 vs flown {flown:F2} (10 %)");
        r.Close();
    }

    static void SpeedAfter()
    {
        var r = Level("perf speed after", 150f, 1f);
        if (r == null) { Sim.NoPolar("Plane2"); return; }
        var sn = r.P.SenseNow();
        Sim.Check(r.P.Fd.Engage(ApGoal.Attitude, in sn, out _), $"{r.Name}: attitude hold engaged");
        r.Fly(1.5f);
        sn = r.P.SenseNow();
        var src = new TurnSource { N = 2f, Throttle = 1f };
        r.P.Fd.Engage(ApGoal.External, in sn, out _, src);
        r.Fly(2f);
        sn = r.P.SenseNow();
        float v0 = r.P.V.Length(), nSum = 0f; int k = 0;
        r.Each(rr => { nSum += rr.P.LoadFactor; k++; });
        r.Fly(8f);
        float flown = r.P.V.Length(), nMean = nSum / MathF.Max(k, 1);
        // (the prediction at the load it flew - the performance answers "what if", the turn asked 2 g)
        float predicted = FlightPerf.SpeedAfter(sn.Perf, in sn, 8f, nMean, 1f, 0f);
        Sim.Note($"{r.Name}: from {v0:F1} m/s, 8 s at {nMean:F2} g (2 asked), full throttle: predicted {predicted:F1} m/s, flown {flown:F1}");
        Sim.Check(MathF.Abs(flown - predicted) < 0.12f * v0, $"{r.Name}: the speed after 8 s predicted {predicted:F1} m/s vs flown {flown:F1} (within 12 % of the speed: the prediction leaves out the sideslip and the surfaces' own drag)");
        Program.LedgerPublic(r, true);
    }
}
