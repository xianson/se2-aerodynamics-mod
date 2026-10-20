using AeroMod;

/// <summary>
/// THE AUTOPILOT BLOCK's panel and the core (ApBlockLink, ApModes, ApLimits.Of, ApArbiter - the game's AeroGridComponent.ServerAp
/// runs the same link around the flight director): a mode chosen on the panel engages, a refusal or a let-go is written back with
/// its reason, a typed height is flown to, the held height is shown, the block going dark lets go, the panel's limits reach the
/// core within the world's caps, a saved mode engages again at load, and the first working block by position flies.
/// </summary>
sealed class BlockPack : IScenarioPack
{
    public void Register(ScenarioRegistry r)
    {
        r.Add("block: hover hold from the panel - engaged, the height shown, a typed height flown to", true, PanelHover);
        r.Add("block: steering lets go of the hover - the panel shows height hold and why", true, PanelSteer);
        r.Add("block: the block off / unpowered / removed lets go (BlockOff), the panel back to Off", true, PanelDark);
        r.Add("block: a refused mode goes back to Off with its reason; a load's mode tried again while the ship comes up", true, PanelRefuse);
        r.Add("block: the height reference switched while holding - no jump", true, PanelRef);
        r.Add("block: the panel's limits reach the core within the world's caps", true, PanelLimits);
        r.Add("block: the first working block by position flies (arbitration)", true, Arbitration);
    }

    static Run Hovering(string name, out float aglAtEngage, bool piloted = false)
    {
        var run = new Run(name, Ships.Heli(3f));
        run.P.Piloted = piloted;
        run.P.AddBlock(ApPanel.Defaults);
        run.Fly(0.5f);
        aglAtEngage = run.P.Agl;
        run.P.Panel.Mode = ApMode.HoverHold;   // (the player presses Hover Hold on the block's panel)
        run.Fly(12f);
        return run;
    }

    static void PanelHover()
    {
        var run = Hovering("block hover", out float agl0);
        var fd = run.P.Fd;
        Sim.Check(fd.Engaged && (fd.Goals & (ApGoal.Hover | ApGoal.Height)) == (ApGoal.Hover | ApGoal.Height) && run.P.Panel.Mode == ApMode.HoverHold,
            $"block hover: the panel's Hover Hold engaged hover + height ({fd.Goals}), the panel still says {run.P.Panel.Mode}");
        Sim.Check(MathF.Abs(run.P.Panel.HeightM - agl0) < 1.5f, $"block hover: the height caught at the engage shown on the panel ({run.P.Panel.HeightM:F1} m; the ship was at {agl0:F1} m over the ground)");
        // a new number typed on the panel: flown there at the climb rate, and never written over
        float want = run.P.Panel.HeightM + 20f;
        run.P.Panel.HeightM = want;
        run.Measure(); run.Fly(20f);
        float agl = run.P.Agl;
        Sim.Note($"block hover: typed {want:F1} m - flew to {agl:F2} m over the ground, the panel says {run.P.Panel.HeightM:F1} m; drifted {run.HorizErr:F2} m across");
        Sim.Check(MathF.Abs(agl - want) < 0.6f && MathF.Abs(run.P.Panel.HeightM - want) < 0.01f && run.HorizErr < 1.5f,
            $"block hover: the typed height flown to ({agl:F2} m of {want:F1}), the typed number kept ({run.P.Panel.HeightM:F1}), the place held ({run.HorizErr:F2} m)");
        Program.LedgerPublic(run, false);
    }

    static void PanelSteer()
    {
        var run = Hovering("block steer", out _, piloted: true);
        run.P.PilotSteerDeg = 10f; run.Fly(0.1f); run.P.PilotSteerDeg = 0f; run.Fly(1f);
        Sim.Check(run.P.Panel.Mode == ApMode.HeightHold && run.P.LastPanelReason == ApReason.Steered && (run.P.Fd.Goals & ApGoal.Height) != 0 && (run.P.Fd.Goals & ApGoal.Hover) == 0,
            $"block steer: steering let go of the hover - the panel says {run.P.Panel.Mode} ({run.P.LastPanelReason}), the core holds {run.P.Fd.Goals}");
        // the pilot presses Autopilot Off
        run.P.Panel.Mode = ApMode.Off; run.Fly(0.2f);
        Sim.Check(!run.P.Fd.Engaged && run.P.Fd.Reason == ApReason.TurnedOff, $"block steer: Autopilot Off let go of everything ({run.P.Fd.Reason})");
    }

    static void PanelDark()
    {
        var run = Hovering("block dark", out _);
        run.P.BlockReady = false; run.Fly(0.2f);
        Sim.Check(!run.P.Fd.Engaged && run.P.Fd.Reason == ApReason.BlockOff && run.P.Panel.Mode == ApMode.Off && run.P.LastPanelReason == ApReason.BlockOff,
            $"block dark: the block switched off let go ({run.P.Fd.Reason}), the panel says {run.P.Panel.Mode} ({run.P.LastPanelReason})");
        // powered again: it stays off (the player chooses again)
        run.P.BlockReady = true; run.Fly(1f);
        Sim.Check(!run.P.Fd.Engaged && run.P.Panel.Mode == ApMode.Off, "block dark: powered again it stays off until a mode is chosen");
        string st = ApBlockLink.Status(ApMode.Off, ApReason.BlockOff, false, false, false, float.NaN, ApHeightRef.Ground, ApProt.None);
        Sim.Check(st.StartsWith("Off - ") && st.Contains("unpowered"), $"block dark: the status line says so ('{st}')");
    }

    static void PanelRefuse()
    {
        // dampeners off for good: refused - after the load grace the panel goes back to Off, the reason kept
        var run = new Run("block refused", Ships.Heli(3f));
        run.P.AddBlock(ApPanel.Defaults); run.P.Damp = false;
        run.P.Panel.Mode = ApMode.HoverHold;
        run.Fly(ApBlockLink.LoadGraceSeconds + 0.5f, rr => rr.P.Damp = false);
        Sim.Check(!run.P.Fd.Engaged && run.P.Panel.Mode == ApMode.Off && run.P.LastPanelReason == ApReason.NeedsDampeners,
            $"block refused: the dampeners off - refused, the panel back to {run.P.Panel.Mode} ({run.P.LastPanelReason})");
        // a saved hover at load: the ship not ready for its first second (here: dampeners coming on) - engaged once it is
        var load = new Run("block load", Ships.Heli(3f));
        var saved = ApPanel.Defaults; saved.Mode = ApMode.HoverHold; saved.HeightM = 150f;
        load.P.AddBlock(saved); load.P.Damp = false;
        load.Fly(1f, rr => rr.P.Damp = false);
        load.P.Damp = true;   // (the pilot's dampeners as saved: on once the grid is in)
        load.Fly(12f);
        Sim.Check(load.P.Fd.Engaged && load.P.Panel.Mode == ApMode.HoverHold, $"block load: the saved Hover Hold engaged once the ship could ({load.P.Fd.Goals}, panel {load.P.Panel.Mode})");
        string st = ApBlockLink.Status(ApMode.HoverHold, ApReason.None, true, true, false, 25f, ApHeightRef.Ground, ApProt.None);
        Sim.Check(st == "Hover - holding 25.0 m over the ground", $"block load: the status line ('{st}')");
    }

    static void PanelRef()
    {
        var run = Hovering("block ref", out _);
        float agl0 = run.P.Agl; run.Measure();
        run.P.Panel.HeightRef = ApHeightRef.Inertial;
        float worst = 0f; run.Fly(8f, rr => worst = MathF.Max(worst, MathF.Abs(rr.P.Agl - agl0)));
        Sim.Note($"block ref: over the ground -> altitude while holding: the height moved at most {worst:F2} m; the panel shows {run.P.Panel.HeightM:F1} m (core: {run.P.Fd.HeightRef})");
        Sim.Check(run.P.Fd.HeightRef == ApHeightRef.Inertial && worst < 0.5f && MathF.Abs(run.P.Panel.HeightM - agl0) < 1.5f,
            $"block ref: switched to altitude without a jump ({worst:F2} m), the number in the new reference {run.P.Panel.HeightM:F1} m");
    }

    static void PanelLimits()
    {
        bool wasU = Autopilot.Unattended;
        try
        {
            var p = ApPanel.Defaults; p.GLimit = 100f; p.HoverSpeed = 0f; p.FloorM = -5f; p.AoaMarginDeg = float.NaN; p.Unattended = true; p.HeightRef = ApHeightRef.Inertial;
            Autopilot.Unattended = false;
            var l = ApLimits.Of(p);
            Sim.Check(l.GLimit == ApPanel.GLimitMax && l.HoverSpeed == ApPanel.HoverSpeedMin && l.FloorM == ApPanel.FloorMin && float.IsFinite(l.AoaMarginDeg) && !l.Unattended && !l.HoldAboveGround,
                $"block limits: clamped to the panel's ranges (g {l.GLimit}, speed {l.HoverSpeed}, floor {l.FloorM}, margin {l.AoaMarginDeg}), the world's unattended cap wins ({l.Unattended}), altitude reference ({l.HoldAboveGround})");
            Autopilot.Unattended = true;
            Sim.Check(ApLimits.Of(p).Unattended && ApModes.GoalsOf(ApMode.HoverHold) == (ApGoal.Hover | ApGoal.Height) && ApModes.GoalsOf(ApMode.HeightHold) == ApGoal.Height
                && ApModes.ModeOf(ApGoal.Height) == ApMode.HeightHold && ApModes.ModeOf(ApGoal.Hover | ApGoal.Position | ApGoal.Height) == ApMode.HoverHold && ApModes.ModeOf(ApGoal.None) == ApMode.Off,
                "block limits: the modes map to the core's goals and back");
        }
        finally { Autopilot.Unattended = wasU; }
        // the panel's hover speed flies the keys: 4 m/s asked, piloted forward key
        var run = new Run("block speed", Ships.Heli(3f));
        run.P.Piloted = true;
        var q = ApPanel.Defaults; q.HoverSpeed = 4f; run.P.AddBlock(q);
        run.Fly(0.5f); run.P.Panel.Mode = ApMode.HoverHold; run.Fly(8f);
        run.P.PilotKeys = -Vector3.UnitZ; run.Fly(8f);
        float v = run.P.HorizontalV.Length();
        Sim.Check(MathF.Abs(v - 4f) < 1f, $"block speed: the panel's 4 m/s hover speed flown on the key ({v:F1} m/s)");
    }

    static void Arbitration()
    {
        Span<bool> ready = stackalloc bool[] { true, true, false, true };
        Span<int> x = stackalloc int[] { 5, 2, -9, 2 }, y = stackalloc int[] { 0, 3, 0, 1 }, z = stackalloc int[] { 0, 0, 0, 7 };
        int k = ApArbiter.Pick(ready, x, y, z);
        Sim.Check(k == 3, $"block arbitration: the first working block by position (x, then y, then z) flies - #{k} (the unpowered one at the lowest x stands aside)");
        ready[3] = false; k = ApArbiter.Pick(ready, x, y, z);
        Sim.Check(k == 1, $"block arbitration: that one dark, the next takes over - #{k}");
        ready.Clear(); k = ApArbiter.Pick(ready, x, y, z);
        Sim.Check(k == -1, "block arbitration: none working - none flies");
        string st = ApBlockLink.Status(ApMode.HoverHold, ApReason.None, false, true, true, 0f, ApHeightRef.Ground, ApProt.None);
        Sim.Check(st.StartsWith("Standby"), $"block arbitration: the others say '{st}'");
    }
}
