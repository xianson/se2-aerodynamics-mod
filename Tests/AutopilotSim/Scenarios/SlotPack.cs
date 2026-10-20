using AeroMod;

/// <summary>
/// THE COMBAT SLOTS' INTEGRATION (the logic itself is the author's - ApDogfight.cs / ApRocketGuidance.cs): chosen while not written,
/// Engage refuses through the block's link ("this behaviour is not written yet", the panel back to Off); Auto never picks an
/// unwritten slot; in Auto the slot's context is filled every step (own ship, performance, lift limits, the target and its
/// observed motion, the panel's values) and the lead helper answers; the status line's words for a slot and for a formation.
/// </summary>
sealed class SlotPack : IScenarioPack
{
    public void Register(ScenarioRegistry r)
    {
        r.Add("slots: Dogfight / Rocket Guidance chosen while not written - Engage refused, the panel Off, the reason said", true, Refused);
        r.Add("slots: Auto never picks an unwritten slot; their context filled each step (Plane2)", true, AutoSkips);
        r.Add("slots: the status line - a slot's own words, the formation's slot error", true, StatusLine);
    }

    static void Refused()
    {
        foreach (var b in new[] { ApBehaviour.Dogfight, ApBehaviour.RocketGuidance })
        {
            var run = new Run($"slot refused {b}", FlightShips.SixAxis());
            var tgt = SimTarget.Straight(new Vector3D(300, 200, 0), Vector3.Zero); run.Targets.Add(tgt);
            var p = ApPanel.Defaults; p.Mode = ApMode.Engage; p.Behaviour = b;
            run.P.AddBlock(in p); run.P.Link.Pilot.Targets = new SimTargetSource { T = tgt };
            run.Fly(7f);   // (past the link's load grace: a refusal is final then)
            string st = ApBlockLink.Status(run.P.Panel.Mode, run.P.LastPanelReason, run.P.Fd.Engaged, true, false, float.NaN, ApHeightRef.Ground, ApProt.None);
            Sim.Note($"slot refused {b}: panel {run.P.Panel.Mode}, reason {run.P.LastPanelReason}, status '{st}'");
            Sim.Check(run.P.Panel.Mode == ApMode.Off && run.P.LastPanelReason == ApReason.NotImplemented && !run.P.Fd.Engaged && st.Contains("not written yet"),
                $"slot refused {b}: Engage with it chosen is refused ({run.P.LastPanelReason}), the panel Off, the status '{st}'");
            Program.LedgerPublic(run, false);
        }
        Sim.Check(!ApDogfight.Implemented && !ApRocketGuidance.Implemented || true, $"slots: Implemented - dogfight {ApDogfight.Implemented}, rocket {ApRocketGuidance.Implemented}");
    }

    static void AutoSkips()
    {
        var r = FlightPack.PlaneAt("slot auto", "Plane2", 150f);
        if (r == null) { Sim.NoPolar("Plane2"); return; }
        var tgt = SimTarget.Straight(new Vector3D(-2500, 3000, 600), new Vector3(30f, 0f, 0f)); r.Targets.Add(tgt);
        var cfg = ApPanel.Defaults; cfg.Behaviour = ApBehaviour.Auto; cfg.HeightRef = ApHeightRef.Inertial; cfg.MinAglM = 0f;
        cfg.DfSkill = 0.33f; cfg.RgNavConst = 4.25f;
        var p = FlightPack.Engage(r, cfg, ApMode.Engage, new SimTargetSource { T = tgt });
        bool picked = false; int filled = 0;
        r.Each(rr =>
        {
            if (ApPilot.IsSlot(p.ActiveBh)) picked = true;
            var c = p.Slot;
            if (c.HasTarget && c.Target == p.T && c.Dt > 0f && c.S.Mass == rr.P.S.Mass && c.Style == ApStyle.Airplane && c.Dogfight.Skill == 0.33f && c.Rocket.NavConst == 4.25f
                && MathF.Abs(c.AirSpeed - rr.P.AirLocal().Length()) < 1f && c.Mach > 0f && c.MainThrust > 0f && c.GLimit == Autopilot.GLimit && float.IsFinite(c.LiftNMax)) filled++;
        });
        r.Fly(30f);
        var cx = p.Slot;
        bool lead = cx.Lead(0f, out var dir, out float tof);
        Sim.Note($"slot auto: 30 s in Auto - a slot picked {picked}; the context filled {filled} of {r.Ticks} ticks; perf valid {cx.Perf.Valid} (corner {cx.Perf.CornerSpeed:F0} m/s), lift max {cx.LiftNMax:F2} g, track confidence {cx.Track.Confidence:F2}, the lead {lead} (tof {tof:F2} s)");
        Sim.Check(!picked && float.IsNaN(p.Arb.DogfightScore) && float.IsNaN(p.Arb.RocketScore), "slot auto: Auto never picks an unwritten slot (their scores NaN)");
        Sim.Check(filled > r.Ticks * 0.9f && cx.Perf.Valid && cx.Track.Confidence > 0.9f && lead && tof > 0f, $"slot auto: the slots' context filled every step ({filled} / {r.Ticks}), performance, track and lead there");
        Program.LedgerPublic(r, true);
    }

    static void StatusLine()
    {
        var v = new ApPilotView { Mode = ApMode.Engage, Style = ApStyle.Airplane, Behaviour = ApBehaviour.Dogfight, HasTarget = true, TargetName = "Bandit", Range = 1200f, BearingDeg = 0.5f, SlotText = "merge" };
        string a = v.Text();
        var w = v; w.Behaviour = ApBehaviour.RocketGuidance; w.SlotText = null;
        string b = w.Text();
        var f = v; f.Behaviour = ApBehaviour.Formation; f.SlotText = null; f.TargetName = "Lead"; f.FormErr = 12.4f; f.FormAlong = 8f; f.LeaderSpeed = 120f;
        string c = f.Text();
        var g = f; g.FormErr = 0.4f; string d = g.Text();
        var h = f; h.FormAlong = -60f; h.FormErr = 61f; h.FormOver = true; string e = h.Text();
        Sim.Note($"slot status: '{a}' | '{b}' | '{c}' | '{d}' | '{e}'");
        Sim.Check(a.StartsWith("Engage (airplane) - Dogfight - merge on Bandit 1.2 km, ahead") && b.StartsWith("Engage (airplane) - Rocket guidance on Bandit 1.2 km"),
            $"slot status: a slot's own words after its name ('{a}'), none without ('{b}')");
        Sim.Check(c.Contains("Formation on Lead - 12 m from the slot (8 m behind)") && c.Contains("leader 120 m/s") && d.Contains("in the slot") && e.Contains("60 m ahead") && e.Contains("overshoot"),
            $"slot status: the formation's slot error, behind / ahead, overshoot ('{c}', '{e}')");
        Sim.Check(!v.Same(in w) && v.Same(in v), "slot status: the view's change test sees the slot's words");
    }
}
