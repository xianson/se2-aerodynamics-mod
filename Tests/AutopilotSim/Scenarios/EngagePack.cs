using AeroMod;

/// <summary>
/// THE BEHAVIOURS against a target (SimTarget: straight, turning, weaving, accelerating): attack runs (flyover and U-turn: passes,
/// their distance, the re-run), broadside (angle, range), circle (radius), pursuit lead / pure / lag (range, aim), hover above,
/// point at, standoff (range kept as it closes) on the plane, the gun helicopter and the six-axis gunship; the nose on the lead
/// (aim error, time in the cone); the arbiter's hysteresis; the lead's arithmetic against a flown projectile; the protection
/// clipping a pursuit of a hard turn; an idle patrol that engages a target when it appears and goes back when it is gone.
/// </summary>
sealed class EngagePack : IScenarioPack
{
    public void Register(ScenarioRegistry r)
    {
        r.Add("engage: attack run (flyover) on a slow target (Plane2) - passes, pass distance, re-run", true, () => AttackRun(false));
        r.Add("engage: attack run U-turn (Plane2) - more passes than the flyover", true, () => AttackRun(true));
        r.Add("engage: broadside 90 on a slow target (Plane2) - angle and range", true, () => PlaneBroadside());
        r.Add("engage: circle a slow target (Plane2) - radius", true, () => PlaneCircle());
        r.Add("engage: pursuit lead vs straight / turning / weaving targets (Plane2) - range, aim, time in cone", true, () => { foreach (var k in new[] { "straight", "turn", "weave" }) PlanePursuit(ApBehaviour.PursuitLead, k); });
        r.Add("engage: pursuit pure and lag (Plane2) - geometry", false, () => { PlanePursuit(ApBehaviour.PursuitPure, "straight"); PlanePursuit(ApBehaviour.PursuitLag, "straight"); });
        r.Add("engage: helicopter standoff vs straight / weaving / accelerating - range kept, nose on the lead", true, () => { foreach (var k in new[] { "straight", "weave", "accelerate" }) Hover(FlightShips.GunHeli(), ApBehaviour.Standoff, k); });
        r.Add("engage: helicopter hover above a moving target", true, () => Hover(FlightShips.GunHeli(), ApBehaviour.HoverAbove, "straight"));
        r.Add("engage: helicopter point at a crossing target - held, aimed", true, () => Hover(FlightShips.GunHeli(), ApBehaviour.PointAt, "crossing"));
        r.Add("engage: six-axis standoff vs weaving / accelerating - range, nose on the lead", true, () => { foreach (var k in new[] { "weave", "accelerate" }) Hover(FlightShips.SixAxis(), ApBehaviour.Standoff, k); });
        r.Add("engage: six-axis broadside 90 - the side to the target, the range", true, () => Hover(FlightShips.SixAxis(), ApBehaviour.Broadside, "straight"));
        r.Add("engage: six-axis circle - radius", true, () => Hover(FlightShips.SixAxis(), ApBehaviour.Circle, "slow"));
        r.Add("engage: one-tail helicopter and VTOL standoff (straight)", false, () => { var h = FlightShips.GunHeli(3f, twin: false); Hover(h, ApBehaviour.Standoff, "straight"); var v = Ships.Vtol(); v.GunAxis = -Vector3.UnitZ; v.Muzzle = 700f; v.GunRange = 1200f; Hover(v, ApBehaviour.Standoff, "straight"); });
        r.Add("engage: evasive jinking on a pursuit (Plane2) - it jinks, the range kept, within the limits", true, Jink);
        r.Add("engage: arbiter hysteresis - no flip-flop at a boundary; a chosen behaviour holds", true, Hysteresis);
        r.Add("engage: the lead point against a flown projectile (miss distance)", true, LeadMath);
        r.Add("engage: pursuit of a hard turn - the protection clips the pull (Plane2)", true, ProtClip);
        r.Add("engage: idle loiter, a target appears (engaged), it is gone (back to the loiter)", true, IdleEngage);
    }

    static ApPanel Cfg(ApBehaviour b, float range, float alt = 100f)
    {
        var c = ApPanel.Defaults; c.Behaviour = b; c.RangeM = range; c.AltOffsetM = alt; c.HeightRef = ApHeightRef.Inertial; c.MinAglM = 0f; return c;
    }

    // ── the plane ──

    static void AttackRun(bool uturn)
    {
        var r = FlightPack.PlaneAt($"attack run{(uturn ? " U-turn" : "")}", "Plane2", 150f);
        if (r == null) { Sim.NoPolar("Plane2"); return; }
        var tgt = SimTarget.Straight(new Vector3D(-3000, 2850, 400), new Vector3(0f, 0f, -10f)); r.Targets.Add(tgt);
        var cfg = Cfg(uturn ? ApBehaviour.AttackRunUTurn : ApBehaviour.AttackRun, 2500f); cfg.AbortM = 400f; cfg.RerunS = 60f;
        var p = FlightPack.Engage(r, cfg, ApMode.Engage, new SimTargetSource { T = tgt });
        float minPass = float.MaxValue; int passes = 0, lastRuns = 0; float inCone = 0f, aimTicks = 0f;
        r.Each(rr =>
        {
            if (p.Bh.Runs != lastRuns) { lastRuns = p.Bh.Runs; passes++; minPass = MathF.Min(minPass, p.Bh.LastPass); }
            if (p.AimOn) { aimTicks++; if (p.InCone) inCone++; }
        });
        r.Fly(240f);
        Sim.Note($"{r.Name}: 240 s on a 10 m/s target 150 m below - {passes} passes, the closest {minPass:F0} m (abort {cfg.AbortM}), {inCone / 60f:F1} s in the cone of {aimTicks / 60f:F1} s aimed, prot {r.ProtSeen}");
        Sim.Check(passes >= (uturn ? 3 : 2) && minPass < cfg.AbortM && minPass > 60f && !r.P.Crashed, $"{r.Name}: {passes} passes (>= {(uturn ? 3 : 2)}), closest {minPass:F0} m (60..{cfg.AbortM})");
        Sim.Check(inCone > 60f, $"{r.Name}: the nose on the lead in the run - {inCone / 60f:F1} s in the cone (> 1 s)");
        if (uturn) _uturnPasses = passes; else _flyPasses = passes;
        if (uturn && _flyPasses > 0) Sim.Check(_uturnPasses > _flyPasses, $"attack run: the U-turn re-attacks sooner - {_uturnPasses} passes vs the flyover's {_flyPasses}");
        Program.LedgerPublic(r, false);
    }
    static int _flyPasses, _uturnPasses;

    static void PlaneBroadside()
    {
        var r = FlightPack.PlaneAt("broadside Plane2", "Plane2", 150f);
        if (r == null) { Sim.NoPolar("Plane2"); return; }
        var tgt = SimTarget.Straight(new Vector3D(-2500, 2900, 1500), new Vector3(5f, 0f, 0f)); r.Targets.Add(tgt);
        var cfg = Cfg(ApBehaviour.Broadside, 2000f); cfg.AngleDeg = 90f;
        var p = FlightPack.Engage(r, cfg, ApMode.Engage, new SimTargetSource { T = tgt });
        r.Fly(60f);
        float sa = 0f, sr = 0f; int n = 0;
        r.Each(rr => { var rel = rr.Rel(tgt); var vh = new Vector3(rr.P.V.X, 0f, rr.P.V.Z); var lh = new Vector3(rel.R.X, 0f, rel.R.Z);
            float ang = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(vh), Vector3.Normalize(lh)), -1f, 1f)) / ApMath.Deg; sa += (ang - 90f) * (ang - 90f); sr += (rel.Range - 2000f) * (rel.Range - 2000f); n++; });
        r.Fly(120f);
        float ea = MathF.Sqrt(sa / n), er = MathF.Sqrt(sr / n);
        Sim.Note($"{r.Name}: angle 90, range 2000 m (turn radius {p.TurnR:F0} m) - angle error RMS {ea:F1} deg, range error RMS {er:F0} m");
        Sim.Check(ea < 15f && er < 300f, $"{r.Name}: broadside - angle RMS {ea:F1} deg (< 15), range RMS {er:F0} m (< 300)");
        Program.LedgerPublic(r, false);
    }

    static void PlaneCircle()
    {
        var r = FlightPack.PlaneAt("circle Plane2", "Plane2", 150f);
        if (r == null) { Sim.NoPolar("Plane2"); return; }
        var tgt = SimTarget.Straight(new Vector3D(-2500, 2900, 0), new Vector3(0f, 0f, 3f)); r.Targets.Add(tgt);
        var p = FlightPack.Engage(r, Cfg(ApBehaviour.Circle, 1800f), ApMode.Engage, new SimTargetSource { T = tgt });
        r.Fly(60f);
        float s2 = 0f; int n = 0;
        r.Each(rr => { var rel = rr.Rel(tgt); float d = new Vector2(rel.R.X, rel.R.Z).Length(); s2 += (d - 1800f) * (d - 1800f); n++; });
        r.Fly(120f);
        float rms = MathF.Sqrt(s2 / n);
        Sim.Note($"{r.Name}: radius 1800 m about a 3 m/s target - radius error RMS {rms:F0} m");
        Sim.Check(rms < 60f, $"{r.Name}: circle - radius RMS {rms:F0} m (< 60)");
        Program.LedgerPublic(r, false);
    }

    static SimTarget Bogey(string kind, Vector3D at, Vector3 v) => kind switch
    {
        "turn" => SimTarget.Turn(at, v, 0.03f),
        "weave" => SimTarget.Weave(at, v, 4f, 12f),
        "accelerate" => SimTarget.Accelerate(at, v, 1.5f, 10f, 10f),
        _ => SimTarget.Straight(at, v),
    };

    static void PlanePursuit(ApBehaviour b, string kind)
    {
        var r = FlightPack.PlaneAt($"{b} {kind}", "Plane2", 150f);
        if (r == null) { Sim.NoPolar("Plane2"); return; }
        var air = (PolarAir)r.P.S.Air;
        // (a bogey 2.5 km ahead flying the same way at 110 m/s)
        var tgt = Bogey(kind, r.P.P + (Vector3D)(Vector3.Normalize(new Vector3(air.Nose.X, 0f, air.Nose.Z)) * 2500f + new Vector3(0f, 0f, 300f)), Vector3.Normalize(new Vector3(air.Nose.X, 0f, air.Nose.Z)) * 110f);
        r.Targets.Add(tgt);
        var cfg = Cfg(b, 500f, 0f); cfg.ConeDeg = 2f; cfg.SpeedMs = 160f;
        var p = FlightPack.Engage(r, cfg, ApMode.Engage, new SimTargetSource { T = tgt });
        r.Fly(60f);
        float rMin = float.MaxValue, rMax = 0f, cone = 0f, err = 0f; int n = 0;
        r.Each(rr => { var rel = rr.Rel(tgt); rMin = MathF.Min(rMin, rel.Range); rMax = MathF.Max(rMax, rel.Range); if (p.InCone) cone++; if (float.IsFinite(p.AimErrDeg)) err += p.AimErrDeg; n++; });
        if (Environment.GetEnvironmentVariable("AP_TRACE") == "pursuit")
            r.Each(rr => { if (rr.Ticks % 120 == 0) { var rel = rr.Rel(tgt); Console.WriteLine($"      t {rr.P.Time:F0} range {rel.Range:F0} closing {rel.Closing:F1} v {rr.P.V.Length():F0} vdem {p.Goal.Speed:F0} bh {p.ActiveBh} err {p.AimErrDeg:F1} w {p.LeadBlend:F2} n {rr.P.LoadFactor:F2} prot {rr.P.Cmd.Prot} dh {rel.R.Y:F0}"); } });

        r.Fly(60f);
        Sim.Note($"{r.Name}: gun range 500 m on a {tgt.Name} bogey at 110 m/s - after 60 s the range {rMin:F0}..{rMax:F0} m, aim error mean {err / n:F1} deg, in the 2 deg cone {cone * 100f / n:F0} % of the time, prot {r.ProtSeen}");
        float inR = b == ApBehaviour.PursuitLag ? 1200f : 900f;
        Sim.Check(!r.P.Crashed && rMax < inR && rMin > 80f, $"{r.Name}: the range held {rMin:F0}..{rMax:F0} m (80..{inR})");
        if (b == ApBehaviour.PursuitLead) Sim.Check(cone / n > (kind == "straight" ? 0.6f : 0.25f), $"{r.Name}: time in the cone {cone * 100f / n:F0} % (> {(kind == "straight" ? 60 : 25)} %)");
        Program.LedgerPublic(r, false);
    }

    // ── the hovering styles ──

    static void Hover(SimShip ship, ApBehaviour b, string kind)
    {
        var r = new Run($"{ship.Name} {b} {kind}", ship);
        r.P.P = new Vector3D(0, 150, 0);
        r.Fly(0.5f);
        var at = new Vector3D(250, 120, -150);
        var tgt = kind == "crossing" ? SimTarget.Straight(new Vector3D(-150, 130, -300), new Vector3(8f, 0f, 0f)) : kind == "slow" ? SimTarget.Straight(at, new Vector3(1f, 0f, 0f)) : Bogey(kind, at, new Vector3(6f, 0f, 2f));
        if (kind == "accelerate") tgt = SimTarget.Accelerate(at, new Vector3(4f, 0f, 0f), 0.8f, 8f, 10f);
        r.Targets.Add(tgt);
        float D = b == ApBehaviour.Circle ? 200f : 250f;
        var cfg = Cfg(b, D, b == ApBehaviour.HoverAbove ? 60f : 20f); cfg.SpeedMs = 25f; cfg.AngleDeg = 90f; cfg.ConeDeg = 2f;
        var p = FlightPack.Engage(r, cfg, ApMode.Engage, new SimTargetSource { T = tgt });
        var p0 = r.P.P;
        r.Fly(30f);
        float sr = 0f, cone = 0f, aim = 0f, side = 0f, hz = 0f, hv = 0f, drift = 0f; int n = 0, na = 0;
        r.Each(rr =>
        {
            var rel = rr.Rel(tgt); n++;
            float flat = new Vector2(rel.R.X, rel.R.Z).Length();
            sr += (flat - D) * (flat - D);
            if (p.InCone) cone++;
            if (float.IsFinite(p.AimErrDeg)) { aim += p.AimErrDeg; na++; }
            var nose = Vector3.Transform(rr.P.S.Nose, rr.P.Q); var nh = new Vector2(nose.X, nose.Z); var lh = new Vector2(rel.R.X, rel.R.Z);
            if (nh.LengthSquared() > 1e-4f && lh.LengthSquared() > 1f) side += MathF.Abs(MathF.Acos(Math.Clamp(Vector2.Dot(Vector2.Normalize(nh), Vector2.Normalize(lh)), -1f, 1f)) / ApMath.Deg - 90f);
            hz = MathF.Max(hz, flat); hv = MathF.Max(hv, MathF.Abs(rel.R.Y + 60f));
            drift = MathF.Max(drift, (float)new Vector2((float)(rr.P.P.X - p0.X), (float)(rr.P.P.Z - p0.Z)).Length());
        });
        r.Fly(60f);
        float rms = MathF.Sqrt(sr / n), tic = cone / n, ae = na > 0 ? aim / na : float.NaN, se = side / n;
        Sim.Note($"{r.Name}: {(b == ApBehaviour.HoverAbove ? $"over the target within {hz:F1} m across, {hv:F1} m in height" : b == ApBehaviour.PointAt ? $"held within {drift:F1} m" : $"range {D} m RMS error {rms:F1} m")}, aim error mean {ae:F1} deg, in the 2 deg cone {tic * 100f:F0} %{(b == ApBehaviour.Broadside ? $", the side off the target by {se:F1} deg mean" : "")}, tilt at most {r.MaxTilt:F1} deg, prot {r.ProtSeen}");
        switch (b)
        {
            case ApBehaviour.Standoff:
            {
                // (a helicopter yaws its whole airframe onto a weaving target: a slower aim than a six-axis ship's - honest numbers)
                float need = kind == "weave" && ship.Name.StartsWith("H25") ? 0.3f : 0.6f;
                Sim.Check(rms < 12f && tic > need, $"{r.Name}: standoff - range RMS {rms:F1} m (< 12), {tic * 100f:F0} % in the cone (> {need * 100f:F0})");
                break;
            }

            case ApBehaviour.HoverAbove: Sim.Check(hz < 6f && hv < 4f, $"{r.Name}: hover above - {hz:F1} m across (< 6), {hv:F1} m in height (< 4)"); break;
            case ApBehaviour.PointAt: Sim.Check(drift < 6f && tic > 0.6f, $"{r.Name}: point at - held within {drift:F1} m (< 6), {tic * 100f:F0} % in the cone (> 60)"); break;
            case ApBehaviour.Broadside: Sim.Check(rms < 15f && se < 8f, $"{r.Name}: broadside - range RMS {rms:F1} m (< 15), the side {se:F1} deg off (< 8)"); break;
            case ApBehaviour.Circle: Sim.Check(rms < 10f, $"{r.Name}: circle - radius RMS {rms:F1} m (< 10)"); break;
        }
        Program.LedgerPublic(r, false);
    }

    static void Jink()
    {
        var r = FlightPack.PlaneAt("jinking pursuit", "Plane2", 150f);
        if (r == null) { Sim.NoPolar("Plane2"); return; }
        var air = (PolarAir)r.P.S.Air;
        var fwd = Vector3.Normalize(new Vector3(air.Nose.X, 0f, air.Nose.Z));
        var tgt = SimTarget.Straight(r.P.P + (Vector3D)(fwd * 1500f), fwd * 110f); r.Targets.Add(tgt);
        var cfg = Cfg(ApBehaviour.PursuitPure, 600f, 0f); cfg.Evasive = true; cfg.Aggression = 0.8f; cfg.SpeedMs = 160f;
        var p = FlightPack.Engage(r, cfg, ApMode.Engage, new SimTargetSource { T = tgt });
        r.Fly(30f);
        float nMax = 0f, rMin = float.MaxValue, rMax = 0f; double vyAbs = 0, latAbs = 0; int n = 0, j0 = p.Jinks;
        var side = Vector3.Cross(fwd, Vector3.UnitY);
        r.Each(rr => { nMax = MathF.Max(nMax, rr.P.LoadFactor); var rl = rr.Rel(tgt); rMin = MathF.Min(rMin, rl.Range); rMax = MathF.Max(rMax, rl.Range); vyAbs += MathF.Abs(rr.P.V.Y); latAbs += MathF.Abs(Vector3.Dot(rr.P.V, side)); n++; });
        r.Fly(60f);
        int flips = p.Jinks - j0;
        Sim.Note($"{r.Name}: aggressiveness 0.8 - {flips} jinks in 60 s, the path off the target's by {latAbs / n:F1} m/s across and {vyAbs / n:F1} m/s vertical (mean), range {rMin:F0}..{rMax:F0} m (600), load at most {nMax:F2} g");
        Sim.Check(flips >= 15 && latAbs / n > 2f && rMax < 1500f
 && nMax <= Autopilot.GLimit * 1.15f && !r.P.Crashed, $"{r.Name}: jinks ({flips} reversals), keeps the range {rMin:F0}..{rMax:F0} m, at most {nMax:F2} g");
        Program.LedgerPublic(r, false);
    }

    // ── the arbiter, the lead, the protection, the idle patrol ──


    static void Hysteresis()
    {
        var arb = new ApArbiterBh();
        var track = new ApTargetTrack();
        var s = new ApSense { Pos = Vector3D.Zero, Vel = new Vector3(150f, 0f, 0f) };
        // (the target's speed swept back and forth across the attack-run / pursuit boundary - 0.35 of ours - every 0.5 s; our speed
        //  across the energy boundary too)
        int flips = 0; ApBehaviour last = ApBehaviour.Auto;
        for (int i = 0; i < 60 * 60; i++)
        {
            float t = i / 60f, wob = MathF.Sin(2f * MathF.PI * t / 1f);
            var fix = new ApTargetFix { Valid = true, Pos = new Vector3D(2000, 0, 0), Vel = new Vector3(150f * (0.35f + 0.05f * wob), 0f, 0f), Id = 1 };
            track.Update(in fix, in s, 1f / 60f);
            var c = new ApBhCtx { Style = ApStyle.Airplane, P = ApPanel.Defaults, Speed = 150f, Cruise = 150f, NMax = 1.6f + 0.1f * wob, T = track, Up = Vector3.UnitY };
            var b = arb.Pick(ApBehaviour.Auto, in c, 1f / 60f);
            if (b != last && last != ApBehaviour.Auto) flips++;
            last = b;
        }
        Sim.Note($"arbiter: 60 s with the target's speed and ours wobbling across the boundaries every second - {flips} switches (dwell {ApArbiterBh.MinDwell} s, bonus {ApArbiterBh.Hysteresis})");
        Sim.Check(flips <= 2, $"arbiter: no flip-flop at the boundary - {flips} switches in 60 s (<= 2)");
        arb.Reset(); int other = 0;
        for (int i = 0; i < 3600; i++)
        {
            var c = new ApBhCtx { Style = ApStyle.Airplane, P = ApPanel.Defaults, Speed = 150f, Cruise = 150f, NMax = 3f, T = track, Up = Vector3.UnitY };

            if (arb.Pick(ApBehaviour.Broadside, in c, 1f / 60f) != ApBehaviour.Broadside) other++;
        }
        Sim.Check(other == 0, $"arbiter: a behaviour chosen on the panel holds (with energy to spare) - {other} ticks of another");
    }

    static void LeadMath()
    {
        float worst = 0f;
        var rng = new Random(5);
        for (int k = 0; k < 200; k++)
        {
            Vector3 R = new((float)(rng.NextDouble() * 2000 - 1000), (float)(rng.NextDouble() * 400 - 200), (float)(rng.NextDouble() * 2000 - 1000));
            Vector3 vs = new((float)(rng.NextDouble() * 200 - 100), 0f, (float)(rng.NextDouble() * 200 - 100));
            Vector3 vt = new((float)(rng.NextDouble() * 300 - 150), (float)(rng.NextDouble() * 40 - 20), (float)(rng.NextDouble() * 300 - 150));
            const float u = 900f;
            if (!ApAim.Lead(R, vt - vs, Vector3.Zero, Vector3.Zero, u, out var dir, out float tof)) continue;
            // (the shot flown: the muzzle speed along the aim plus the shooter's velocity, 1/60 s steps as the game's)
            Vector3 shot = Vector3.Zero, tp = R; float best = float.MaxValue;
            // (the closest approach within each step: the relative motion is straight in a step)
            Vector3 rv = vt - (dir * u + vs);
            for (float t = 0f; t < tof + 1f; t += 1f / 60f)
            {
                Vector3 d0 = tp - shot; float tc = Math.Clamp(-Vector3.Dot(d0, rv) / MathF.Max(rv.LengthSquared(), 1e-6f), 0f, 1f / 60f);
                best = MathF.Min(best, (d0 + rv * tc).Length()); shot += (dir * u + vs) / 60f; tp += vt / 60f;
            }
            worst = MathF.Max(worst, best);
        }
        Sim.Note($"lead: 200 random geometries at 900 m/s - the flown shot passes within {worst:F2} m of the target at most");
        Sim.Check(worst < 0.5f, $"lead: the intercept iterated - worst miss {worst:F2} m (< 0.5)");
    }

    static void ProtClip()
    {
        var r = FlightPack.PlaneAt("pursuit of a hard turn", "Plane2", 150f);
        if (r == null) { Sim.NoPolar("Plane2"); return; }
        var air = (PolarAir)r.P.S.Air;
        var fwd = Vector3.Normalize(new Vector3(air.Nose.X, 0f, air.Nose.Z));
        var tgt = SimTarget.Turn(r.P.P + (Vector3D)(fwd * 900f), fwd * 120f, 0.25f); r.Targets.Add(tgt);
        var cfg = Cfg(ApBehaviour.PursuitLead, 400f, 0f); cfg.BankDeg = 80f;
        FlightPack.Engage(r, cfg, ApMode.Engage, new SimTargetSource { T = tgt });

        float nMax = 0f; r.Each(rr => nMax = MathF.Max(nMax, rr.P.LoadFactor));
        r.Fly(40f);
        Sim.Note($"{r.Name}: a 0.25 rad/s turning bogey chased - load at most {nMax:F2} g (limit {Autopilot.GLimit}), prot {r.ProtSeen}, the pull over its cap at most {r.MaxPullOverCap:F1} deg");
        Sim.Check(nMax <= Autopilot.GLimit * 1.15f && (r.ProtSeen & (ApProt.GLimit | ApProt.Alpha)) != 0 && !r.P.Crashed, $"{r.Name}: the protection clipped the chase - at most {nMax:F2} g (limit {Autopilot.GLimit} + 15 %), {r.ProtSeen}");
        Program.LedgerPublic(r, false);
    }

    static void IdleEngage()
    {
        var r = new Run("idle patrol six-axis", FlightShips.SixAxis());
        r.P.P = new Vector3D(0, 150, 0);
        r.Fly(0.5f);
        var tgt = SimTarget.Straight(new Vector3D(300, 140, 100), new Vector3(2f, 0f, 0f)); r.Targets.Add(tgt);
        var src = new SimTargetSource { T = tgt, On = false };
        var cfg = Cfg(ApBehaviour.Standoff, 150f, 10f); cfg.SpeedMs = 20f;
        var p = FlightPack.Engage(r, cfg, ApMode.Engage, src);
        var home = r.P.P;
        r.Fly(10f);
        float idle = (float)(r.P.P - home).Length();
        src.On = true; r.Fly(40f);
        float rng = r.Rel(tgt).Range; var bh = p.ActiveBh;
        src.On = false; r.Fly(40f);
        float back = (float)(new Vector3D(r.P.P.X, home.Y, r.P.P.Z) - home).Length();
        Sim.Note($"idle patrol: no target - held within {idle:F1} m of where it was engaged; a target appears - {bh} at {rng:F0} m (150 asked); it is gone - back within {back:F1} m of the engage point");
        Sim.Check(idle < 2f && bh == ApBehaviour.Standoff && MathF.Abs(rng - 150f) < 20f && back < 5f && r.P.Fd.Engaged, $"idle patrol: holds ({idle:F1} m), engages the target ({bh}, {rng:F0} m), goes back ({back:F1} m)");
        Program.LedgerPublic(r, false);
    }
}
