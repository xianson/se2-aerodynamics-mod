using AeroMod;

/// <summary>
/// THE LIFT-VECTOR GOAL (ApExternalKind.Lift) on Plane2: a lift direction and a load flown on the wings - the load flown and the
/// lift's direction against the ones asked; an over-asked load clipped by the g limit and, with the g limit lifted, by the stall
/// (the direction kept either way); the ledger every tick.
/// </summary>
sealed class LiftPack : IScenarioPack
{
    public void Register(ScenarioRegistry r)
    {
        r.Add("lift goal: 2 g banked 60 deg right - load and lift direction flown as asked (Plane2)", true, () => Fly("lift 2 g", 2f, 60f, false));
        r.Add("lift goal: 6 g asked - clipped at the g limit, the direction kept (Plane2)", true, () => Fly("lift 6 g", 6f, 60f, false));
        r.Add("lift goal: the most (NaN) with the g limit at 15 - clipped at the stall margin (Plane2)", true, () => Fly("lift stall", float.NaN, 70f, true));
        r.Add("lift goal: ApLift from / to a path acceleration round trip", true, RoundTrip);
    }

    /// <summary>A lift-vector source: the lift banked 'bankDeg' from straight up about the path, at 'load' g.</summary>
    sealed class LiftSource : IApGoalSource
    {
        public float Load, BankDeg; public Vector3 Asked;
        public bool Step(in ApSense s, ref ApExternal g)
        {
            g.Kind = ApExternalKind.Lift; g.Lift = Asked = ApLift.Banked(s.Vel, s.GravityTrue, BankDeg * ApMath.Deg); g.Load = Load;
            return true;
        }
    }

    static void Fly(string name, float load, float bankDeg, bool stall)
    {
        var r = FlightPack.PlaneAt(name, "Plane2", 150f);
        if (r == null) { Sim.NoPolar("Plane2"); return; }
        if (stall) { var lim = ApLimits.Defaults; lim.GLimit = 15f; r.P.LimOverride = lim; }
        var src = new LiftSource { Load = load, BankDeg = bankDeg };
        var sn = r.P.SenseNow();
        Sim.Check(r.P.Fd.Engage(ApGoal.External, in sn, out var why, src), $"{r.Name}: engaged ({why})");
        r.Fly(3f);
        r.Measure();
        float nSum = 0f, nMax = 0f, angSum = 0f, aoaMax = 0f; int k = 0;
        r.Each(rr =>
        {
            float n = rr.P.LoadFactor; nSum += n; nMax = MathF.Max(nMax, n); k++;
            // the lift flown: the air force across the path (world), against the direction asked
            var airW = rr.P.V - rr.P.Wind; var vh = Vector3.Normalize(airW);
            var fW = ApMath.Flat(Vector3.Transform(rr.P.LastAeroF, rr.P.Q), vh);
            if (fW.LengthSquared() > 1f) angSum += MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(fW), src.Asked), -1f, 1f)) / ApMath.Deg;
            aoaMax = MathF.Max(aoaMax, Program.AoaPublic(rr));
        });
        r.Fly(5f);
        float nMean = nSum / MathF.Max(k, 1), ang = angSum / MathF.Max(k, 1), peak = Program.PeakAlphaPublic(r.P.S, r.P.V.Length());
        float gl = r.P.LimOverride?.GLimit ?? Autopilot.GLimit;
        Sim.Note($"{r.Name}: asked {(float.IsNaN(load) ? "the most" : $"{load} g")} banked {bankDeg} deg - flew {nMean:F2} g (at most {nMax:F2}; limit {gl}), the lift {ang:F1} deg off the asked direction, AoA at most {aoaMax:F1} deg (peak {peak:F1}), prot {r.ProtSeen}");
        Sim.Check(ang < 12f, $"{r.Name}: the lift's direction kept within {ang:F1} deg (< 12) of the asked");
        if (!float.IsNaN(load) && load <= gl) Sim.Check(MathF.Abs(nMean - load) < 0.2f * load, $"{r.Name}: the load flown {nMean:F2} g for {load} asked (20 %)");
        if (!float.IsNaN(load) && load > gl) Sim.Check((r.ProtSeen & ApProt.GLimit) != 0 && nMax <= gl * 1.15f, $"{r.Name}: G LIMIT clipped it - at most {nMax:F2} g (limit {gl} +15 %)");
        if (stall) Sim.Check((r.ProtSeen & ApProt.Alpha) != 0 && aoaMax <= peak + 1f, $"{r.Name}: STALL LIMIT clipped it - AoA at most {aoaMax:F1} deg (peak {peak:F1} +1)");
        Program.LedgerPublic(r, true);
    }

    static void RoundTrip()
    {
        var g = new Vector3(0f, -9.81f, 0f); var v = new Vector3(120f, 10f, -40f);
        float worst = 0f;
        foreach (var a in new[] { new Vector3(0f, 0f, 0f), new Vector3(0f, 20f, 0f), new Vector3(15f, -5f, 30f), new Vector3(-30f, 0f, 3f) })
        {
            var aPerp = ApMath.Flat(a, Vector3.Normalize(v));
            ApLift.FromAccel(v, aPerp, g, 20f, out var lift, out float n);
            worst = MathF.Max(worst, (ApLift.ToAccel(v, lift, n, g) - aPerp).Length());
        }
        Sim.Check(worst < 1e-3f, $"lift: FromAccel -> ToAccel returns the path acceleration (worst {worst:E1} m/s2)");
        ApLift.FromAccel(v, new Vector3(0f, 0f, 200f), g, 3f, out var l2, out float n2);
        Sim.Check(MathF.Abs(n2 - 3f) < 1e-4f, $"lift: an over-asked turn keeps its direction and pulls the cap ({n2:F2} g)");
    }
}
