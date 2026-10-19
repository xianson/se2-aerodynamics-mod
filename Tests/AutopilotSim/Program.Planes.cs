using AeroMod;

// ENVELOPE PROTECTION ON THE DUMPED PLANES (their AeroBench force polars): the autopilot's attitude goal (ACAH - what the
// fixed-wing modes will fly through) is given a command past what the ship can fly; the protection must clip it - STALL LIMIT
// short of the whole ship's best lift (LiftPeak over the polar, as the game's stall warning), G LIMIT at Autopilot.GLimit, the
// predicted loss of control (LoacMath over the polar's moment against the reach) - and the same command with protection off
// must go past (the clip is the protection's, not luck). Low energy and overspeed act on the throttle.
static partial class Program
{
    static void RegisterPlanes()
    {
        Scn("protection: stall limit on Plane2 (a 30 deg pitch-up asked)", true, () => AlphaProt("Plane2", 102f));
        Scn("protection: stall limit on the Tu-134", false, () => AlphaProt("tu134_game", 160f, 4f));
        Scn("protection: stall limit on the jetliner", false, () => AlphaProt("jetliner", 170f, 4f));
        Scn("protection: g limit on Plane2 at Mach 0.6", true, () => GLimit("Plane2"));
        Scn("protection: loss of control predicted on Plane2 (weak pitch)", true, () => LoacProt("Plane2"));
        Scn("protection: low energy on Plane2 (slow, throttle shut)", true, () => LowEnergyPlane("Plane2"));
        Scn("protection: overspeed on Plane2", true, () => Overspeed("Plane2"));
    }

    /// <summary>The plane flying straight and level-ish at 'speed' (its trim angle of attack found on the polar), the throttle at
    /// 'thr', piloted (the throttle is theirs), dampeners off, the attitude goal holding the trim attitude.</summary>
    static Run Plane(SimShip s, float speed, float thr, string tag)
    {
        var air = (PolarAir)s.Air;
        var r = new Run($"{tag} {s.Name}", s);
        r.P.Damp = false; r.P.Rho = 1.225f;
        r.P.P = new Vector3D(0, 3000, 0);
        float weight = s.Mass * s.PlanetG * s.Gm, trim = 0f;
        for (float a = 0f; a < 20f * ApMath.Deg; a += 0.05f * ApMath.Deg)
        {
            var q = Quaternion.CreateFromAxisAngle(air.Right, a);
            var velW = Vector3.Transform(air.Nose, Quaternion.Identity) * speed;
            air.Forces(Vector3.Transform(velW, Quaternion.Inverse(q)), Vector3.Zero, 1.225f, out var f, out _);
            if (Vector3.Transform(f, q).Y >= weight) { trim = a; break; }
            trim = a;
        }
        r.P.Q = Quaternion.CreateFromAxisAngle(air.Right, trim);
        r.P.V = air.Nose * speed;
        r.P.Piloted = true; r.P.PilotKeys = air.Nose * thr;
        Check(r.Engage(ApGoal.Attitude), $"{r.Name}: attitude hold engaged");
        Note($"{r.Name}: trimmed at {trim / ApMath.Deg:F1} deg angle of attack, {speed:F0} m/s");
        return r;
    }

    /// <summary>The plane's angle of attack now (deg).</summary>
    static float Aoa(Run r) { ((PolarAir)r.P.S.Air).Angles(r.P.AirLocal(), out float a, out _); return a / ApMath.Deg; }

    /// <summary>The whole ship's best-lift angle at this speed (deg): LiftPeak over the polar, straight flight.</summary>
    static float PeakAlpha(SimShip s, float speed)
    {
        var air = (PolarAir)s.Air; float best = 0f, at = 0f;
        for (float a = -10f; a <= 60f; a += 0.25f)
        {
            float ar = a * ApMath.Deg;
            var d = air.Nose * MathF.Cos(ar) - air.Up * MathF.Sin(ar);
            air.Forces(d * speed, Vector3.Zero, 1.225f, out var f, out _);
            var l = LiftPeak.Normal(air.Nose, -air.Up, 0f);
            float lift = Vector3.Dot(f, Vector3.Normalize(air.Up * MathF.Cos(ar) + air.Nose * MathF.Sin(ar)));
            if (lift > best) { best = lift; at = a; }
        }
        return at;
    }

    /// <summary>A pitch-up of 30 deg asked of the attitude goal: with protection the angle of attack stays short of the best
    /// lift's (less Autopilot.AoaMarginDeg); without, the same command passes it.</summary>
    /// <summary>(tol: the AoA over the cap allowed - 2 deg the law's overshoot; the airliners' hull lift has a broad flat top past
    /// 40 deg where the whole-ship best lift moves with the flow: 4.)</summary>
    static void AlphaProt(string name, float speed, float tol = 2f)
    {
        float maxOn = 0f, maxOff = 0f, peak = 0f; ApProt seen = ApProt.None; int stalledOn = 0;
        foreach (bool prot in new[] { true, false })
        {
            var s = Ships.Plane(name, tw: 0.5f, gyro: 3e5f, surfPerQ: new Vector3(800f, 800f, 1600f));
            if (s == null) { Sim.NoPolar(name); return; }
            peak = PeakAlpha(s, speed);
            Autopilot.Protection = prot;
            try
            {
                var r = Plane(s, speed, 0.6f, prot ? "alpha-prot on" : "alpha-prot off");
                var air = (PolarAir)s.Air;
                r.Fly(2f);
                r.P.Fd.SetAttitude(Quaternion.Normalize(r.P.Q * Quaternion.CreateFromAxisAngle(air.Right, (peak + 25f - Aoa(r)) * ApMath.Deg)));   // (15 deg past the best lift)
                float maxA = 0f; int stalled = 0;
                r.Fly(10f, rr => { maxA = MathF.Max(maxA, Aoa(rr)); });
                r.Fly(0.01f);
                if (prot) { maxOn = maxA; seen = r.ProtSeen; stalledOn = stalled; Ledger(r, crashOk: true); }
                else { maxOff = maxA; r.Close(); }
            }
            finally { Autopilot.Protection = true; }
        }
        float cap = peak - Autopilot.AoaMarginDeg;
        Note($"stall limit {name}: best lift at {peak:F1} deg at {speed:F0} m/s; 25 deg past it asked - protection on: angle of attack at most {maxOn:F1} deg (cap {cap:F1}), {seen}; off: {maxOff:F1} deg");
        Check((seen & ApProt.Alpha) != 0 && maxOn <= cap + tol && maxOff > peak + 1f && maxOff > maxOn + 5f,
            $"stall limit {name}: the command clipped - AoA at most {maxOn:F1} deg (best lift {peak:F1} less the {Autopilot.AoaMarginDeg} deg margin: {cap:F1}, +{tol} tolerance); unprotected the same command took it to {maxOff:F1} deg, past the best lift");
    }

    /// <summary>At Mach 0.6 a 12 deg pitch-up would pull far past Autopilot.GLimit: held to it.</summary>
    static void GLimit(string name)
    {
        float nOn = 0f, nOff = 0f; ApProt seen = ApProt.None;
        foreach (bool prot in new[] { true, false })
        {
            var s = Ships.Plane(name, tw: 0.5f, gyro: 3e5f, surfPerQ: new Vector3(800f, 800f, 1600f));
            if (s == null) { Sim.NoPolar(name); return; }
            Autopilot.Protection = prot;
            try
            {
                var r = Plane(s, 204f, 0.8f, prot ? "g-limit on" : "g-limit off");
                var air = (PolarAir)s.Air;
                r.Fly(2f);
                r.P.Fd.SetAttitude(Quaternion.Normalize(r.P.Q * Quaternion.CreateFromAxisAngle(air.Right, 12f * ApMath.Deg)));
                float nMax = 0f;
                r.Fly(6f, rr => nMax = MathF.Max(nMax, rr.P.LoadFactor));
                if (prot) { nOn = nMax; seen = r.ProtSeen; Ledger(r, crashOk: true); } else { nOff = nMax; r.Close(); }
            }
            finally { Autopilot.Protection = true; }
        }
        Note($"g limit {name}: 12 deg pitch-up at 204 m/s - protection on: at most {nOn:F2} g (limit {Autopilot.GLimit}), {seen}; off: {nOff:F2} g");
        Check((seen & ApProt.GLimit) != 0 && nOn <= Autopilot.GLimit * 1.12f && nOff > Autopilot.GLimit * 1.15f,
            $"g limit {name}: held to {nOn:F2} g (limit {Autopilot.GLimit}, +12 %); unprotected {nOff:F2} g");
    }

    /// <summary>Weak pitch control (surfaces and gyros): a 12 deg pitch-up needs more torque than there is - the predicted loss of
    /// control shrinks the step to what it can hold (need / reach under ~1, the attitude law not saturated for long); without,
    /// the law saturates and the ship does not hold what it was asked.</summary>
    static void LoacProt(string name)
    {
        float satOn = 0f, satOff = 0f, errOn = 0f, errOff = 0f; ApProt seen = ApProt.None;
        foreach (bool prot in new[] { true, false })
        {
            var s = Ships.Plane(name, tw: 0.5f, gyro: 1e5f, surfPerQ: new Vector3(500f, 500f, 380f));
            if (s == null) { Sim.NoPolar(name); return; }
            Autopilot.Protection = prot;
            try
            {
                var r = Plane(s, 102f, 0.6f, prot ? "loac on" : "loac off");
                var air = (PolarAir)s.Air;
                r.Fly(3f);
                r.P.Fd.SetAttitude(Quaternion.Normalize(r.P.Q * Quaternion.CreateFromAxisAngle(air.Right, 12f * ApMath.Deg)));
                int sat = 0, ticks = 0; float err = 0f;
                r.Fly(8f, rr => { ticks++; if (rr.P.LastShare > 1.0f) sat++; err = MathF.Max(err, ApMath.Angle(rr.P.Q, rr.P.Cmd.Target) / ApMath.Deg); });
                if (prot) { satOn = sat / (float)ticks; errOn = err; seen = r.ProtSeen; Ledger(r, crashOk: true); } else { satOff = sat / (float)ticks; errOff = err; r.Close(); }
            }
            finally { Autopilot.Protection = true; }
        }
        Note($"loss of control {name}: 12 deg pitch-up on weak pitch - protection on: the law saturated {satOn * 100:F0} % of the time, the target at most {errOn:F1} deg from the ship ({seen}); off: saturated {satOff * 100:F0} %, {errOff:F1} deg");
        Check((seen & ApProt.Loac) != 0 && satOn < 0.5f * satOff && satOn < 0.15f,
            $"loss of control {name}: the step cut to what the ship can hold - saturated {satOn * 100:F0} % of the time (unprotected {satOff * 100:F0} %)");
    }

    /// <summary>Slow (65 m/s) with the throttle shut, the attitude held nose-high: the stall evaluation approaching - full thrust.</summary>
    static void LowEnergyPlane(string name)
    {
        var s = Ships.Plane(name, tw: 0.5f, gyro: 3e5f, surfPerQ: new Vector3(800f, 800f, 1600f));
        if (s == null) { Sim.NoPolar(name); return; }
        var r = Plane(s, 70f, 0f, "low-energy");
        float vMin = float.MaxValue, thrMax = 0f;
        r.Fly(20f, rr => { vMin = MathF.Min(vMin, rr.P.V.Length()); thrMax = MathF.Max(thrMax, MathF.Abs(Vector3.Dot(rr.P.Move, ((PolarAir)rr.P.S.Air).Nose))); });
        Note($"low energy {name}: from 70 m/s with the throttle shut - the slowest {vMin:F0} m/s, the throttle the autopilot gave at most {thrMax:F2}, {r.ProtSeen}, {r.P.V.Length():F0} m/s after 20 s");
        Check((r.ProtSeen & ApProt.LowEnergy) != 0 && thrMax > 0.99f, $"low energy {name}: FULL THRUST near the stall (throttle {thrMax:F2})");
        Ledger(r, crashOk: true);
    }

    /// <summary>Over the speed limit with the throttle open: the throttle shut while over.</summary>
    static void Overspeed(string name)
    {
        var s = Ships.Plane(name, tw: 0.8f, gyro: 3e5f, surfPerQ: new Vector3(800f, 800f, 1600f));
        if (s == null) { Sim.NoPolar(name); return; }
        var r = Plane(s, 150f, 1f, "overspeed");
        r.P.SpeedLimit = 140f;
        float vMax = 0f;
        r.Fly(3f); r.Fly(10f, rr => vMax = MathF.Max(vMax, rr.P.V.Length()));
        Note($"overspeed {name}: from 150 m/s, full throttle, limit 140: at most {vMax:F1} m/s after the first 3 s, {r.ProtSeen}, {r.P.V.Length():F1} m/s at the end");
        Check((r.ProtSeen & ApProt.Overspeed) != 0 && vMax < 141f, $"overspeed {name}: held under the limit on full throttle (at most {vMax:F1} m/s, limit 140)");
        Ledger(r, crashOk: true);
    }
}
