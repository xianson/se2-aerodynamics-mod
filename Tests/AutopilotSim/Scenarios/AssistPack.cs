using AeroMod;

/// <summary>
/// THE FLIGHT ASSISTS (2026-10-19, Lift/FlightAssists.cs) through the plant:
///  - OFFSET THRUST (Assists.OffsetThrust): only the uncommanded part of the thrusters' offset torque is scaled - an offset the
///    attitude law cancels is untouched (the flight the same), one past the thrusters' reach is taken off at 0; every tick the
///    torque applied is never further from the law's ask than the real one, never past it.
/// </summary>
sealed class AssistPack : IScenarioPack
{
    public void Register(ScenarioRegistry r)
    {
        r.Add("assist: offset thrust - a cancelled offset is untouched", true, CancelledOffset);
        r.Add("assist: offset thrust - past the thrusters' reach it is taken off", true, OffsetPastReach);
        r.Add("assist: stall protection - a pilot's pull past the stall is clipped", true, PilotAlpha);
        r.Add("assist: stall protection - a pilot's pull past the g limit is clipped", true, PilotG);
        r.Add("assist: stall protection - inside the envelope the flight is unchanged", true, PilotInside);
        r.Add("assist: stall protection - from 35 deg, surfaces only, the nose comes back", true, PilotRecovery);
    }

    // ── stall protection in manual flight ──

    /// <summary>The analytic wing ship: nose -Z, up +Y, right +X (pitch +X nose up). CL 4.4 / rad to the stall at 14 deg (CLmax
    /// ~1.07), then only down (0.5 by 34 deg); drag CD0 + induced + a plate's; a stable pitching moment and rate damping.
    /// Surface reach per local axis = SurfPerQ x q.</summary>
    sealed class WingAir : IAir
    {
        public float S = 60f, Chord = 3f, StallDeg = 14f, ClA = 4.4f, CmA = 0.5f, TrimDeg = 3f, PitchDamp = 4e5f;
        public Vector3 SurfPerQ = new(40f, 40f, 40f);
        public bool Lifts => true;
        public Vector3 SurfaceReach(float q) => SurfPerQ * q;
        public float Cl(float a)
        {
            float s = StallDeg * ApMath.Deg, aa = MathF.Abs(a), max = ClA * s;
            // (past the stall it only falls - to 0.5 by 20 deg past it, then with cos: no second, flat-plate peak for the stall
            //  evaluation to find a "margin" toward)
            float cl = aa <= s ? ClA * aa : aa <= s + 0.35f ? max - (max - 0.5f) * (aa - s) / 0.35f : 0.5f * MathF.Cos(aa - s - 0.35f);
            return MathF.CopySign(cl, a);
        }
        /// <summary>The angle of attack (rad) of the air past the ship (local).</summary>
        public static float Aoa(Vector3 air) => MathF.Atan2(-air.Y, -air.Z);
        /// <summary>The air past the ship at angle of attack a (rad), unit.</summary>
        public static Vector3 Flow(float a) => new(0f, -MathF.Sin(a), -MathF.Cos(a));
        public void Forces(Vector3 a, Vector3 w, float rho, out Vector3 f, out Vector3 t)
        {
            float speed = a.Length(); f = Vector3.Zero; t = -new Vector3(PitchDamp, PitchDamp, PitchDamp) * w;
            if (speed < 0.1f) return;
            float q = 0.5f * rho * speed * speed, al = Aoa(a), cl = Cl(al), sa = MathF.Sin(al);
            float cd = 0.03f + 0.05f * cl * cl + 1.2f * sa * sa;
            Vector3 v = a / speed;   // (the air's way past the ship)
            Vector3 liftDir = Vector3.Normalize(Vector3.Cross(Vector3.UnitX, v));   // (across the flow, in the pitch plane)
            if (liftDir.Y < 0f) liftDir = -liftDir;
            f = q * S * (cl * liftDir + cd * v);
            t += new Vector3(-q * S * Chord * CmA * (al - TrimDeg * ApMath.Deg), -q * S * Chord * 0.3f * MathF.Atan2(a.X, -a.Z), 0f);
        }
    }

    static SimShip Glider(float tw = 0.4f, float gyro = 3e5f, string name = "wing")
    {
        var air = new WingAir();
        var s = new SimShip { Name = name, Mass = 10000f, I = new Vector3(2e5f, 3e5f, 1.5e5f), PlanetG = 9.81f, Gm = 2f, Air = air, Gyro = new Vector3(gyro), Bottom = 2f };
        s.Add("engine", Vector3.Zero, -Vector3.UnitZ, tw * s.Mass * 9.81f * 2f);
        return s;
    }

    /// <summary>The wing ship flying at 'speed', trimmed for level flight (its angle of attack found on its own lift; or pitched
    /// pitchDeg), a pilot holding that attitude by the reticle (PilotTarget), dampeners off, the throttle theirs; protected or not.</summary>
    static Run Flying(float speed, bool protect, string tag, float pitchDeg = float.NaN)
    {
        var s = Glider();
        var air = (WingAir)s.Air;
        float weight = s.Mass * s.PlanetG * s.Gm, trim = 0f;
        for (float a = 0f; a < 20f * ApMath.Deg; a += 0.02f * ApMath.Deg) { trim = a; if (0.5f * 1.225f * speed * speed * air.S * air.Cl(a) >= weight) break; }
        if (!float.IsNaN(pitchDeg)) trim = pitchDeg * ApMath.Deg;
        var r = new Run($"{tag} ({(protect ? "protected" : "unprotected")})", s);
        r.P.Damp = false; r.P.Rho = 1.225f; r.P.P = new Vector3D(0, 3000, 0);
        r.P.Q = Quaternion.CreateFromAxisAngle(Vector3.UnitX, trim);
        r.P.V = -Vector3.UnitZ * speed;
        r.P.Piloted = true; r.P.PilotKeys = -Vector3.UnitZ * 0.6f;
        r.P.PilotTargetOn = true; r.P.PilotTarget = r.P.Q;
        r.P.PilotProt = protect ? new PilotProtection() : null;
        return r;
    }

    /// <summary>The whole ship's best-lift angle at this speed (deg) on its own forces, straight flight.</summary>
    static float Peak(float speed)
    {
        var air = new WingAir(); float best = 0f, at = 0f;
        for (float a = 0f; a <= 60f; a += 0.1f)
        {
            var v = WingAir.Flow(a * ApMath.Deg);
            air.Forces(v * speed, Vector3.Zero, 1.225f, out var f, out _);
            var up = Vector3.Normalize(Vector3.Cross(Vector3.UnitX, v)); if (up.Y < 0f) up = -up;
            float lift = Vector3.Dot(f, up);
            if (lift > best) { best = lift; at = a; }
        }
        return at;
    }

    static float Aoa(Run r) => WingAir.Aoa(r.P.AirLocal()) / ApMath.Deg;

    static void PilotAlpha()
    {
        float peak = Peak(100f), cap = peak - Autopilot.AoaMarginDeg;
        float maxOn = 0f, maxOff = 0f; ApProt seen = ApProt.None;
        foreach (bool prot in new[] { true, false })
        {
            var r = Flying(100f, prot, "pilot pull past the stall");
            r.Fly(2f);
            r.P.PilotTarget = Quaternion.Normalize(r.P.Q * Quaternion.CreateFromAxisAngle(Vector3.UnitX, (peak + 25f - Aoa(r)) * ApMath.Deg));
            float maxA = 0f; ApProt p = ApProt.None;
            r.Fly(10f, rr => { maxA = MathF.Max(maxA, Aoa(rr)); p |= rr.P.LastPilotProt; });
            if (prot) { maxOn = maxA; seen = p; } else maxOff = maxA;
            Program.LedgerPublic(r, true);
        }
        Sim.Note($"stall protection (pilot): best lift at {peak:F1} deg at 100 m/s, the reticle 25 deg past it - protected: AoA at most {maxOn:F1} deg (cap {cap:F1}), {seen}; unprotected {maxOff:F1} deg");
        Sim.Check((seen & ApProt.Alpha) != 0 && maxOn <= cap + 2f && maxOff > peak + 1f && maxOff > maxOn + 5f,
            $"stall protection (pilot): the pull clipped at {maxOn:F1} deg (cap {cap:F1} +2), unprotected the same reticle took it to {maxOff:F1} deg past the best lift {peak:F1}");
    }

    static void PilotG()
    {
        float nOn = 0f, nOff = 0f; ApProt seen = ApProt.None;
        foreach (bool prot in new[] { true, false })
        {
            var r = Flying(200f, prot, "pilot pull past the g limit");
            r.Fly(2f);
            r.P.PilotTarget = Quaternion.Normalize(r.P.Q * Quaternion.CreateFromAxisAngle(Vector3.UnitX, 10f * ApMath.Deg));
            float n = 0f; ApProt p = ApProt.None;
            r.Fly(6f, rr => { n = MathF.Max(n, rr.P.LoadFactor); p |= rr.P.LastPilotProt; });
            if (prot) { nOn = n; seen = p; } else nOff = n;
            Program.LedgerPublic(r, true);
        }
        Sim.Note($"stall protection (pilot): a 10 deg pull at 200 m/s - protected at most {nOn:F2} g (limit {Autopilot.GLimit}), {seen}; unprotected {nOff:F2} g");
        Sim.Check((seen & ApProt.GLimit) != 0 && nOn <= Autopilot.GLimit * 1.12f && nOff > Autopilot.GLimit * 1.15f,
            $"stall protection (pilot): held to {nOn:F2} g (limit {Autopilot.GLimit} +12 %); unprotected {nOff:F2} g");
    }

    static void PilotInside()
    {
        // (a gentle 2 deg pull at 120 m/s: well inside the stall and the g limit - the governed target IS the pilot's)
        var states = new List<(Vector3D p, Quaternion q)>(); ApProt seen = ApProt.None; float worst = 0f;
        foreach (bool prot in new[] { true, false })
        {
            var r = Flying(120f, prot, "pilot inside the envelope");
            r.Fly(2f);
            r.P.PilotTarget = Quaternion.Normalize(r.P.Q * Quaternion.CreateFromAxisAngle(Vector3.UnitX, 2f * ApMath.Deg));
            r.Each(rr => { seen |= rr.P.LastPilotProt; worst = MathF.Max(worst, ApMath.Angle(rr.P.LastFlown, rr.P.PilotTarget) / ApMath.Deg); });
            r.Fly(8f);
            states.Add((r.P.P, r.P.Q));
            Program.LedgerPublic(r, true);
        }
        double dp = (states[0].p - states[1].p).Length(); float dq = ApMath.Angle(states[0].q, states[1].q) / ApMath.Deg;
        Sim.Note($"stall protection inside the envelope: nothing clipped ({seen}), the flown target at most {worst:F5} deg off the reticle; after 10 s {dp:F4} m / {dq:F5} deg from the unprotected flight");
        Sim.Check(seen == ApProt.None && worst < 1e-3f && dp < 0.01 && dq < 1e-3f, $"stall protection inside the envelope: unchanged ({seen}; {dp:F4} m, {dq:F5} deg)");
    }

    static void PilotRecovery()
    {
        // (35 deg angle of attack - past the 14 deg stall - at 90 m/s, and the pilot keeps pulling: the reticle 35 deg above the
        //  flow every tick (a stick held back). No gyros, no thrust: the control surfaces alone, the airframe nearly neutral in
        //  pitch. Protected, the reticle is governed back to the cap and the surfaces bring the nose under the stall; unprotected
        //  they hold the pilot's 35 deg)
        float peak = Peak(90f), cap = peak - Autopilot.AoaMarginDeg;
        float tOn = float.NaN, lateOn = 0f, lateOff = float.MaxValue;
        foreach (bool prot in new[] { true, false })
        {
            var r = Flying(90f, prot, "pilot at 35 deg", pitchDeg: 35f);
            r.P.S.Gyro = Vector3.Zero; r.P.PilotKeys = Vector3.Zero;
            ((WingAir)r.P.S.Air).CmA = 0.02f;   // (nearly neutral in pitch: the airframe alone neither recovers nor departs - the controls decide)
            float t = float.NaN, late = prot ? 0f : float.MaxValue;
            r.Fly(6f, rr =>
            {
                float a = Aoa(rr);
                rr.P.PilotTarget = Quaternion.Normalize(rr.P.Q * Quaternion.CreateFromAxisAngle(Vector3.UnitX, (35f - a) * ApMath.Deg));
                if (float.IsNaN(t) && a < peak) t = rr.P.Time;
                if (rr.P.Time > 4f) late = prot ? MathF.Max(late, a) : MathF.Min(late, a);
            });
            if (prot) { tOn = t; lateOn = late; } else lateOff = late;
            Program.LedgerPublic(r, true);
        }
        Sim.Note($"stall protection from 35 deg (surfaces only, 90 m/s, the pilot pulling): protected under the {peak:F1} deg stall after {tOn:F2} s, at most {lateOn:F1} deg in the last 2 s (cap {cap:F1}); unprotected at least {lateOff:F1} deg");
        Sim.Check(tOn < 4f && lateOn <= cap + 2f && lateOff > peak + 5f,
            $"stall protection from 35 deg: under the stall in {tOn:F2} s and held at {lateOn:F1} deg (cap {cap:F1} +2); unprotected held in the stall ({lateOff:F1} deg)");
    }

    /// <summary>One run of an unpiloted ship (dampeners on: the damp branch) at an offset-thrust scale: the most tilt, the most
    /// removed (N m), the offset (N m), and the per-tick check's failures (further from the ask, or past it).</summary>
    static (float tilt, float removed, float offset, int bad, Quaternion end) Fly(SimShip ship, float scale, string tag)
    {
        float keep = FlightAssists.OffsetThrustScale;
        FlightAssists.OffsetThrustScale = scale;
        try
        {
            var run = new Run($"{tag} at {scale:0.0}", ship);
            float removed = 0f, offset = 0f; int bad = 0;
            run.Each(rr =>
            {
                var p = rr.P;
                removed = MathF.Max(removed, p.LastAssistRemoved.Length()); offset = MathF.Max(offset, p.LastOffset.Length());
                Vector3 before = p.LastThrApplied - p.LastThrAsked, after = before - p.LastAssistRemoved;
                for (int k = 0; k < 3; k++)
                {
                    float b = k == 0 ? before.X : k == 1 ? before.Y : before.Z, a = k == 0 ? after.X : k == 1 ? after.Y : after.Z;
                    if (MathF.Abs(a) > MathF.Abs(b) + 1e-2f || a * b < -1e-3f) bad++;
                }
            });
            run.Measure();
            run.Fly(16f);
            float tilt = run.MaxTilt;
            Program.LedgerPublic(run, true);
            return (tilt, removed, offset, bad, run.P.Q);
        }
        finally { FlightAssists.OffsetThrustScale = keep; }
    }

    static void CancelledOffset()
    {
        // (the centre of mass 0.8 m / 0.6 m off: the lift thrusters can re-share for it - the law cancels it with them)
        Func<SimShip> ship = () => Ships.Vtol(2.6f, comOff: new Vector3(0.8f, 0f, 0.6f), name: "VTOL CoM off (in reach)");
        var real = Fly(ship(), 1f, "offset in reach");
        var none = Fly(ship(), 0f, "offset in reach");
        float diff = ApMath.Angle(real.end, none.end) / ApMath.Deg;
        Sim.Note($"offset thrust in reach: offset {real.offset / 1e3f:F1} kN m; realistic tilt {real.tilt:F3} deg, at 0 {none.tilt:F3} deg, attitude {diff:F4} deg apart after 16 s; the most taken off {none.removed:F1} N m");
        // (what is taken off is only the solve's residual - the damp law rests inside its rest rate - a thousandth of the offset)
        Sim.Check(none.removed <= 1e-3f * MathF.Max(none.offset, 1f) && diff < 0.25f && real.tilt < 0.25f,
            $"offset thrust in reach: the cancelled offset is the law's - at most {none.removed:F1} N m taken off (0.1 % of {none.offset:F0}), the flight within {diff:F3} deg");
        Sim.Check(none.bad == 0, $"offset thrust in reach: the applied torque never further from the ask nor past it ({none.bad} axis-ticks)");
    }

    static void OffsetPastReach()
    {
        // (one lift thruster at 35 %, barely the thrust to hover, tiny gyros: the others cannot balance it - the ship rolls)
        Func<SimShip> ship = () => Ships.Vtol(2.2f, weak: 0.35f, gyro: 1e3f, name: "VTOL weak lifter (past reach)");
        var real = Fly(ship(), 1f, "offset past reach");
        var half = Fly(ship(), 0.5f, "offset past reach");
        var none = Fly(ship(), 0f, "offset past reach");
        Sim.Note($"offset thrust past reach: tilt realistic {real.tilt:F1} deg, at 0.5 {half.tilt:F1} deg, at 0 {none.tilt:F1} deg; the most taken off {none.removed / 1e3f:F1} kN m of {none.offset / 1e3f:F1}");
        Sim.Check(real.tilt > 3f && none.removed > 0f && none.tilt < 0.5f * real.tilt && half.tilt <= real.tilt + 0.1f,
            $"offset thrust past reach: realistic it departs ({real.tilt:F1} deg), at 0 the uncancelled offset is taken off ({none.tilt:F1} deg; {none.removed / 1e3f:F1} kN m)");
        Sim.Check(none.bad == 0 && half.bad == 0, $"offset thrust past reach: the applied torque never further from the ask nor past it ({none.bad} + {half.bad} axis-ticks)");
    }
}
