using AeroMod;

/// <summary>
/// UNPILOTED ROCKETS AS AUTOPILOT VEHICLES (navigation only): a 150 kg finned rocket, one motor on a gimbal (ThrustVector), no gyros -
/// the flight director's attitude goal held from launch (the gimbal alone at first, the fins as the speed builds) through a gust;
/// the same without the gimbal (fins alone) for contrast; the
/// gimbal's arithmetic (the torque asked given within its cone, a roll it cannot give left); gust rejection in flight with the
/// solid nozzle's gimbal as the game sets it (its default cone - GimbalLimits - and rate), a narrower cone, the fins alone. The
/// ledger every tick (the gimbal within its cone and rate).
/// </summary>
sealed class RocketPack : IScenarioPack
{
    public void Register(ScenarioRegistry r)
    {
        r.Add("rocket: thrust vectoring - the torque asked within the cone, the rest left; the reach", true, Arithmetic);
        r.Add("rocket: attitude held from launch through a gust - gimbal and fins (no gyros)", true, () => Hold(true));
        r.Add("rocket: the same with the fins alone (no gimbal) - departs at launch, for contrast", true, () => Hold(false));
        r.Add("rocket: gust rejection in flight - the nozzle's default cone (5 deg, 30 deg/s) vs 2 deg vs the fins alone", true, Gusts);
    }

    sealed class AttitudeSource : IApGoalSource
    {
        public Quaternion Q; public float Throttle = 1f;
        public bool Step(in ApSense s, ref ApExternal g) { g.Kind = ApExternalKind.Attitude; g.Attitude = Q; g.Throttle = Throttle; return true; }
    }

    static void Arithmetic()
    {
        var n = new ThrustVector.Nozzle[] { new() { Pos = new Vector3(0f, 0f, 2f), Dir = -Vector3.UnitZ, Thrust = 5000f, MaxRad = 5f * ApMath.Deg, RateRad = 1000f } };
        ThrustVector.Solve(new Vector3(200f, -100f, 0f), n, 1, 1f / 60f, out var f, out var t);
        Sim.Check((t - new Vector3(200f, -100f, 0f)).Length() < 2f, $"tvc: a torque inside the cone given ({t.X:F0}, {t.Y:F0}, {t.Z:F0} for 200, -100, 0)");
        var reach = ThrustVector.Reach(n, 1);
        float exp = 5000f * MathF.Sin(5f * ApMath.Deg) * 2f;
        Sim.Check(MathF.Abs(reach.X - exp) < 1f && MathF.Abs(reach.Y - exp) < 1f && reach.Z < 1e-3f, $"tvc: the reach {reach.X:F0} / {reach.Y:F0} / {reach.Z:F2} N m (pitch, yaw {exp:F0}; no roll from one nozzle on the axis)");
        ThrustVector.Solve(new Vector3(5000f, 0f, 300f), n, 1, 1f / 60f, out f, out t);
        float ang = n[0].Deflect.Length() / ApMath.Deg;
        Sim.Check(MathF.Abs(ang - 5f) < 0.01f && MathF.Abs(t.Z) < 1e-3f && t.X > 0.9f * exp / 2f, $"tvc: past the cone - held at it ({ang:F2} deg, {t.X:F0} N m), the roll it cannot give left ({t.Z:F3})");
        var slow = new ThrustVector.Nozzle[] { new() { Pos = new Vector3(0f, 0f, 2f), Dir = -Vector3.UnitZ, Thrust = 5000f, MaxRad = 5f * ApMath.Deg, RateRad = 30f * ApMath.Deg } };
        ThrustVector.Solve(new Vector3(5000f, 0f, 0f), slow, 1, 1f / 60f, out _, out _);
        Sim.Check(MathF.Abs(slow[0].Deflect.Length() - 0.5f * ApMath.Deg) < 1e-4f, $"tvc: the rate - 0.5 deg in a tick at 30 deg/s ({slow[0].Deflect.Length() / ApMath.Deg:F3})");
    }

    /// <summary>Climbing at 60 deg (launched 6 s before, held): a 30 m/s (1 - cos) side gust over 1.5 s, then a second the other
    /// way; the attitude's worst departure and the time back within 1 deg - with the solid nozzle's gimbal as the game sets it
    /// (GimbalLimits.DefaultDeg, ThrustVector.DefaultRateDeg), a 2 deg cone, and the fins alone.</summary>
    static void Gusts()
    {
        var res = new List<(string what, float before, float worst, float back, float tvc)>();
        foreach (float cone in new[] { GimbalLimits.DefaultDeg, 2f, 0f })
        {
            var s = Rockets.Finned(gimbalDeg: cone, name: $"rocket gust cone {cone:0}");
            var r = Launch($"rocket gust {(cone > 0f ? $"gimbal {cone:0} deg + fins" : "fins alone")}", s, 60f);
            var want = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 60f * ApMath.Deg);
            var src = new AttitudeSource { Q = want };
            var sn = r.P.SenseNow();
            Sim.Check(r.P.Fd.Engage(ApGoal.External, in sn, out var why, src), $"{r.Name}: engaged ({why})");
            r.Air.Gusts.Add((6f, 1.5f, new Vector3(30f, 0f, 0f)));
            r.Air.Gusts.Add((10f, 1.5f, new Vector3(-30f, 0f, 0f)));
            float worst = 0f, lastOut = 6f, tvc = 0f, before = 0f;
            r.Each(rr =>
            {
                if (rr.P.Time < 6f) { if (rr.P.Time > 4f) before = MathF.Max(before, ApMath.Angle(rr.P.Q, want) / ApMath.Deg); return; }
                float e = ApMath.Angle(rr.P.Q, want) / ApMath.Deg;
                worst = MathF.Max(worst, e); if (e > 1f) lastOut = rr.P.Time;
                tvc = MathF.Max(tvc, rr.P.LastTvcT.Length());
            });
            r.Fly(16f);
            float back = MathF.Max(0f, lastOut - 11.5f);   // (the second gust over at 11.5 s; 4.5: never back within 1 deg)
            res.Add((r.Name, before, worst, back, tvc));
            Sim.Note($"{r.Name}: {before:F2} deg off before; two 30 m/s side gusts - the attitude at most {worst:F2} deg off, within 1 deg {back:F1} s after the second passed; gimbal torque at most {tvc:F0} N m; {r.P.V.Length():F0} m/s");
            Program.LedgerPublic(r, true);
        }
        var (g5, g2, fins) = (res[0], res[1], res[2]);
        Sim.Check(g5.before < 0.5f && g5.worst < 3f && g5.back < 2f && g5.tvc > 0f, $"rocket gust: the default gimbal rejects the gusts - held ({g5.before:F2} deg) before, at most {g5.worst:F2} deg (< 3) in them, within 1 deg {g5.back:F1} s after (< 2)");
        Sim.Check(g2.before < 0.5f && g2.worst > g5.worst, $"rocket gust: a 2 deg cone holds the climb but not the gusts as well - {g2.worst:F2} deg vs {g5.worst:F2} (the cone is the authority)");
        Sim.Check(fins.before > 5f, $"rocket gust: the fins alone never held the climb ({fins.before:F1} deg off before the gusts) - the gimbal is what holds it");
    }

    static Run Launch(string name, SimShip s, float pitchDeg)
    {
        var r = new Run(name, s);
        r.P.Damp = false; r.P.Rho = 1.225f; r.P.P = new Vector3D(0, 300, 0);
        // (pitched up pitchDeg from the horizon, heading north - -Z; at rest)
        r.P.Q = Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitchDeg * ApMath.Deg);
        return r;
    }

    static void Hold(bool gimbal)
    {
        var s = Rockets.Finned(gimbalDeg: gimbal ? 5f : 0f);
        var r = Launch($"rocket hold {(gimbal ? "gimbal + fins" : "fins alone")}", s, 60f);
        var want = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 70f * ApMath.Deg);   // (asked 10 deg further up than launched)
        var src = new AttitudeSource { Q = want };
        var sn = r.P.SenseNow();
        Sim.Check(r.P.Fd.Engage(ApGoal.External, in sn, out var why, src), $"{r.Name}: engaged ({why})");
        r.Air.Gusts.Add((6f, 2f, new Vector3(25f, 0f, 0f)));
        float errLaunch = 0f, errLate = 0f, tvc = 0f, speed = 0f, maxDefl = 0f;
        r.Each(rr =>
        {
            float e = ApMath.Angle(rr.P.Q, want) / ApMath.Deg;
            if (rr.P.Time < 3f) errLaunch = MathF.Max(errLaunch, e); else if (rr.P.Time > 4f) errLate = MathF.Max(errLate, e);
            tvc = MathF.Max(tvc, rr.P.LastTvcT.Length()); speed = rr.P.V.Length();
            foreach (var g in rr.P.GimbalState) maxDefl = MathF.Max(maxDefl, g.Deflect.Length() / ApMath.Deg);
        });
        r.Fly(12f);
        Sim.Note($"{r.Name}: the attitude error at most {errLaunch:F1} deg in the first 3 s (from 10 deg off at rest), {errLate:F1} deg after 4 s through a 25 m/s gust; gimbal torque at most {tvc:F0} N m, deflection at most {maxDefl:F2} deg; {speed:F0} m/s at the end; prot {r.ProtSeen}");
        if (gimbal) Sim.Check(errLate < 4f && tvc > 0f && maxDefl <= 5.0001f && !r.P.Crashed, $"{r.Name}: held within {errLate:F1} deg (< 4) after launch through the gust, the gimbal working ({tvc:F0} N m) inside its cone");
        else Sim.Check(errLaunch > 2f || errLate > 2f, $"{r.Name}: without the gimbal the launch is not held as well ({errLaunch:F1} / {errLate:F1} deg) - the fins need speed");
        Program.LedgerPublic(r, true);
    }

}
