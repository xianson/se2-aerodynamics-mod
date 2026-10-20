using AeroMod;

/// <summary>
/// THE FACING AS A DESIRE (2026-10-20; the user: "the facing is like a desire and it's gotta fly how it's gotta fly; you compromise
/// a little to put the guns on target"): the panel's Facing (the nose on the lead, a side on the target) blended into the movement's
/// attitude by the Aim Priority within each style's caps (ApPilot.Aim). Priority 0: the path only (no blend, the report only);
/// 1: as much as the caps allow (the old aim switch's flight - EngagePack runs every movement there); between: in between. Per
/// style: the six-axis gunship (Blend = p; its tilt still capped by what its side thrusters hold), the gun helicopter (its heading
/// turned from travel toward the target by p), the plane (the 30 deg window off its path and the pull-out - movement wins: no blend
/// past the window, whatever p). A side facing: a hover turns that side in; a plane circles the way that keeps it there. The
/// protection still clips (a hard-turn chase at priority 1 within the g limit).
/// </summary>
sealed class FacingPack : IScenarioPack
{
    public void Register(ScenarioRegistry r)
    {
        r.Add("facing: six-axis keep distance, nose - aim priority 0 / 0.5 / 1 (path only .. all the caps allow), tilt capped", true, () => Priority(FlightShips.SixAxis(), "weave"));
        r.Add("facing: helicopter keep distance, nose - aim priority 0 / 0.5 / 1 (its heading from travel to the lead)", true, () => Priority(FlightShips.GunHeli(), "crossing"));
        r.Add("facing: six-axis and helicopter circle it, left side at the target - priority 1 on it, 0 not", true, Sides);
        r.Add("facing: plane strafing runs, nose - priority 0 never blends, 1 only inside the window (movement wins), more time on target", true, PlanePriority);
        r.Add("facing: the protection still clips - a hard-turn chase at priority 1 (Plane2) within the g limit", true, ProtClips);
        r.Add("facing: travel - no aim, no report; the status line names the facing", true, Words);
    }

    static ApPanel Cfg(ApBehaviour b, ApFacing f, float p, float range)
    {
        var c = ApPanel.Defaults; c.Behaviour = b; c.Facing = f; c.AimPriority = p; c.RangeM = range; c.AltOffsetM = 20f;
        c.HeightRef = ApHeightRef.Inertial; c.MinAglM = 0f; c.SpeedMs = 25f; c.ConeDeg = 2f;
        return c.Clamped();
    }

    sealed class Stats { public float Err, Cone, BlendMax, Tilt, Rms; public int N; public string Name; }

    static Stats FlyHover(SimShip ship, ApBehaviour b, ApFacing f, float p, string kind, float D = 250f)
    {
        var r = new Run($"{ship.Name} {b} {f} p{p:F1} {kind}", ship);
        r.P.P = new Vector3D(0, 150, 0);
        r.Fly(0.5f);
        var at = new Vector3D(250, 120, -150);
        var tgt = kind == "crossing" ? SimTarget.Straight(new Vector3D(-150, 130, -300), new Vector3(8f, 0f, 0f))
            : kind == "weave" ? SimTarget.Weave(at, new Vector3(6f, 0f, 2f), 4f, 12f)
            : kind == "high" ? SimTarget.Straight(new Vector3D(60, 900, -40), new Vector3(2f, 0f, 0f))
            : SimTarget.Straight(at, new Vector3(1f, 0f, 0f));
        r.Targets.Add(tgt);
        var pl = FlightPack.Engage(r, Cfg(b, f, p, D), ApMode.Engage, new SimTargetSource { T = tgt });
        if (Environment.GetEnvironmentVariable("AP_TRACE") == "facing")
            r.Each(rr => { if (rr.Ticks % 30 == 0) { var rel = rr.Rel(tgt); Console.WriteLine($"      t {rr.P.Time:F1} tilt {rr.P.TiltDeg:F0} err {pl.AimErrDeg:F0} w {pl.LeadBlend:F2} dy {rel.R.Y:F0} range {rel.Range:F0} bh {pl.ActiveBh} hw {pl.HeightWanted:F0}"); } });
        r.Fly(30f);
        var st = new Stats { Name = r.Name };
        float sr = 0f;
        r.Measure();
        r.Each(rr =>
        {
            var rel = rr.Rel(tgt); float flat = new Vector2(rel.R.X, rel.R.Z).Length(); sr += (flat - D) * (flat - D);
            if (float.IsFinite(pl.AimErrDeg)) st.Err += pl.AimErrDeg;
            if (pl.InCone) st.Cone++;
            st.BlendMax = MathF.Max(st.BlendMax, pl.LeadBlend); st.N++;
        });
        r.Fly(60f);
        st.Err /= MathF.Max(st.N, 1); st.Cone /= MathF.Max(st.N, 1); st.Rms = MathF.Sqrt(sr / MathF.Max(st.N, 1)); st.Tilt = r.MaxTilt;
        Sim.Note($"{st.Name}: aim error mean {st.Err:F1} deg, in the 2 deg cone {st.Cone * 100f:F0} %, blend at most {st.BlendMax:F2}, range RMS {st.Rms:F1} m, tilt at most {st.Tilt:F1} deg");
        Program.LedgerPublic(r, false);
        return st;
    }

    static void Priority(SimShip ship, string kind)
    {
        var s0 = FlyHover(ship, ApBehaviour.Standoff, ApFacing.Nose, 0f, kind);
        var s5 = FlyHover(ship, ApBehaviour.Standoff, ApFacing.Nose, 0.5f, kind);
        var s1 = FlyHover(ship, ApBehaviour.Standoff, ApFacing.Nose, 1f, kind);
        Sim.Check(s0.BlendMax == 0f && s0.Err > 10f, $"{ship.Name}: priority 0 - the path only: no blend ({s0.BlendMax:F2}), the nose {s0.Err:F1} deg off the lead (> 10)");
        Sim.Check(s1.Err < s5.Err && s5.Err < s0.Err, $"{ship.Name}: the aim error falls with the priority - {s0.Err:F1} / {s5.Err:F1} / {s1.Err:F1} deg at 0 / 0.5 / 1");
        Sim.Check(s1.Cone > 0.6f, $"{ship.Name}: priority 1 - {s1.Cone * 100f:F0} % in the cone (> 60)");
        Sim.Check(s0.Rms < 12f && s5.Rms < 12f && s1.Rms < 12f, $"{ship.Name}: the movement flown whatever the priority - range RMS {s0.Rms:F1} / {s5.Rms:F1} / {s1.Rms:F1} m (< 12)");
        if (ship.Name.StartsWith("six"))
        {
            // (a target high above: the nose wants up, the tilt stays where the side thrusters still hold the weight)
            var hi = FlyHover(ship, ApBehaviour.Standoff, ApFacing.Nose, 1f, "high", 150f);
            Sim.Check(hi.Tilt < 40f, $"{ship.Name}: a target high above at priority 1 - tilt at most {hi.Tilt:F1} deg (< 40: the side thrusters' cap)");
        }
    }

    static void Sides()
    {
        foreach (var ship in new[] { FlightShips.SixAxis(), FlightShips.GunHeli() })
        {
            var on = FlyHover(ship, ApBehaviour.Circle, ApFacing.SideLeft, 1f, "slow", 200f);
            var off = FlyHover(ship, ApBehaviour.Circle, ApFacing.SideLeft, 0f, "slow", 200f);
            Sim.Check(on.Err < 8f && on.Rms < 15f, $"{ship.Name}: circle it, left side at priority 1 - the side {on.Err:F1} deg off the target (< 8), radius RMS {on.Rms:F1} m (< 15)");
            Sim.Check(off.BlendMax == 0f && off.Rms < 15f, $"{ship.Name}: at priority 0 the circle alone (blend {off.BlendMax:F2}, the side {off.Err:F1} deg off; radius RMS {off.Rms:F1} m)");
        }
    }

    static void PlanePriority()
    {
        var res = new (float cone, float blendMax, int outside)[2];
        for (int k = 0; k < 2; k++)
        {
            float p = k == 0 ? 0f : 1f;
            var r = FlightPack.PlaneAt($"strafing run p{p:F0}", "Plane2", 150f);
            if (r == null) { Sim.NoPolar("Plane2"); return; }
            var tgt = SimTarget.Straight(new Vector3D(-3000, 2850, 400), new Vector3(0f, 0f, -10f)); r.Targets.Add(tgt);
            var cfg = Cfg(ApBehaviour.AttackRun, ApFacing.Nose, p, 2500f); cfg.AbortM = 400f; cfg.RerunS = 60f; cfg.AltOffsetM = 100f; cfg.SpeedMs = 0f; cfg.ConeDeg = 3f;
            var pl = FlightPack.Engage(r, cfg, ApMode.Engage, new SimTargetSource { T = tgt });
            float cone = 0f, bmax = 0f; int outside = 0;
            r.Each(rr =>
            {
                if (pl.InCone) cone++;
                bmax = MathF.Max(bmax, pl.LeadBlend);
                // (movement wins: no blend with the lead well outside the 30 deg window off the path - the gun axis is the nose here)
                if (pl.LeadBlend > 0f && float.IsFinite(pl.AimErrDeg) && pl.AimErrDeg > 45f) outside++;
            });
            r.Fly(240f);
            res[k] = (cone / 60f, bmax, outside);
            Sim.Note($"{r.Name}: {cone / 60f:F1} s in the cone in 240 s, blend at most {bmax:F2}, blended outside the window {outside} ticks, prot {r.ProtSeen}");
            Sim.Check(!r.P.Crashed, $"{r.Name}: flown");
            Program.LedgerPublic(r, false);
        }
        Sim.Check(res[0].blendMax == 0f, $"plane: priority 0 never blends the nose off the path ({res[0].blendMax:F2})");
        Sim.Check(res[1].blendMax > 0.5f && res[1].outside == 0, $"plane: priority 1 blends ({res[1].blendMax:F2}) only inside the window ({res[1].outside} ticks outside)");
        Sim.Check(res[1].cone > res[0].cone, $"plane: more time on target at priority 1 - {res[1].cone:F1} s vs {res[0].cone:F1} s");
    }

    static void ProtClips()
    {
        var r = FlightPack.PlaneAt("hard-turn chase p1", "Plane2", 150f);
        if (r == null) { Sim.NoPolar("Plane2"); return; }
        var air = (PolarAir)r.P.S.Air;
        var fwd = Vector3.Normalize(new Vector3(air.Nose.X, 0f, air.Nose.Z));
        var tgt = SimTarget.Turn(r.P.P + (Vector3D)(fwd * 800f), fwd * 120f, 0.25f); r.Targets.Add(tgt);
        var cfg = Cfg(ApBehaviour.PursuitLead, ApFacing.Nose, 1f, 400f); cfg.SpeedMs = 150f; cfg.AltOffsetM = 0f;
        FlightPack.Engage(r, cfg, ApMode.Engage, new SimTargetSource { T = tgt });
        float nMax = 0f;
        r.Each(rr => nMax = MathF.Max(nMax, rr.P.LoadFactor));
        r.Fly(60f);
        Sim.Note($"{r.Name}: load at most {nMax:F2} g (limit {Autopilot.GLimit}), prot {r.ProtSeen}");
        Sim.Check(nMax <= Autopilot.GLimit * 1.1f && r.ProtSeen != ApProt.None && !r.P.Crashed, $"{r.Name}: the safety limits clip the aim's pull - at most {nMax:F2} g ({Autopilot.GLimit} limit), prot {r.ProtSeen}");
        Program.LedgerPublic(r, false);
    }

    static void Words()
    {
        var t = FlyHover(FlightShips.SixAxis(), ApBehaviour.Standoff, ApFacing.Travel, 1f, "slow");
        Sim.Check(t.BlendMax == 0f && t.Err == 0f && t.Cone == 0f, $"facing travel: no blend, no aim report (blend {t.BlendMax:F2}, error sum {t.Err:F1})");
        var v = new ApPilotView { Mode = ApMode.Engage, Style = ApStyle.SixAxis, Behaviour = ApBehaviour.Circle, HasTarget = true, TargetName = "Raider", Range = 1200f,
            AimOn = true, Facing = ApFacing.SideLeft, AimErrDeg = 4.2f, InRange = true };
        string s = v.Text();
        Sim.Check(s.StartsWith("Attack (thruster ship) - Circle it on Raider 1.2 km") && s.Contains("left side at target 4° off"), $"facing status: '{s}'");
        v.Facing = ApFacing.Nose; v.AimErrDeg = 1.4f; v.InCone = true; v.Behaviour = ApBehaviour.PursuitLead; v.Style = ApStyle.Airplane;
        s = v.Text();
        Sim.Check(s.StartsWith("Attack (plane) - Chase - gun lead on Raider") && s.Contains("nose at target 1.4° IN CONE"), $"facing status: '{s}'");
        v.Facing = ApFacing.Travel; v.AimOn = false;
        s = v.Text();
        Sim.Check(!s.Contains("at target") && !s.Contains("aim"), $"facing status, travel: no aim words ('{s}')");
    }
}
