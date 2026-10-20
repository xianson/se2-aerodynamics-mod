using AeroMod;

/// <summary>
/// THE AUTOPILOT BLOCK'S PILOT MODES through the link (ApBlockLink, as the game's AeroGridComponent.ServerAp runs it): Flight Hold
/// pressed engages and writes the holds it caught back to the panel; a refusal goes back to Off with its reason; steering lets go
/// with its reason; Level Off begins flight hold again from the flight now (a recovery from a bank and a dive); Return flies back
/// and loiters; a style set on the panel changes the flying style; a saved Engage mode at load engages again; the panel's
/// values reach the pilot each tick.
/// </summary>
sealed class PilotBlockPack : IScenarioPack
{
    public void Register(ScenarioRegistry r)
    {
        r.Add("pilot block: Flight Hold from the panel - the holds caught written back, a typed heading flown", true, FlightHold);
        r.Add("pilot block: refused (navigate without a route; a plane too slow) - back to Off with the reason", true, Refused);
        r.Add("pilot block: steering lets go of a pilot mode with its reason", true, Steer);
        r.Add("pilot block: Level Off recovers a banked dive - wings level, the height held", true, LevelOff);
        r.Add("pilot block: Return flies back to the engage point and loiters", true, Return);
        r.Add("pilot block: the style set on the panel (six-axis -> helicopter) begins the mode again in it", true, StyleSwitch);
        r.Add("pilot block: a saved Engage mode engages again at load", true, SavedEngage);
    }

    static void FlightHold()
    {
        var r = FlightPack.PlaneAt("block flight hold", "Plane2", 150f);
        if (r == null) { Sim.NoPolar("Plane2"); return; }
        r.P.Fd.Disengage(ApReason.None);
        r.P.AddBlock(ApPanel.Defaults);
        r.P.Panel.Mode = ApMode.FlightHold;
        r.Fly(2f);
        var pn = r.P.Panel;
        Sim.Check(r.P.Fd.Engaged && (r.P.Fd.Goals & ApGoal.External) != 0 && r.P.Link.Style == ApStyle.Airplane && pn.Mode == ApMode.FlightHold,
            $"block flight hold: engaged ({r.P.Fd.Goals}, {r.P.Link.Style}), the panel says {pn.Mode}");
        Sim.Check(pn.SpeedMs > 120f && pn.SpeedMs < 160f && pn.AltM > 0f && pn.HasHome, $"block flight hold: caught heading {pn.HeadingDeg:F0}, altitude {pn.AltM:F0} m, speed {pn.SpeedMs:F0} m/s, the return point set {pn.HasHome}");
        float want = (pn.HeadingDeg + 60f) % 360f;
        r.P.Panel.HeadingDeg = want;
        r.Fly(50f);
        float got = ApCompass.Heading(new Vector3(r.P.V.X, 0f, r.P.V.Z), Vector3.UnitY);
        Sim.Check(MathF.Abs(ApCompass.Diff(got, want)) < 2f, $"block flight hold: the typed heading {want:F0} flown ({got:F0})");
        Program.LedgerPublic(r, false);
    }

    static void Refused()
    {
        var r = new Run("block refused route", Ships.Heli(3f));
        r.P.AddBlock(ApPanel.Defaults);
        r.P.Panel.Mode = ApMode.Navigate;
        r.Fly(ApBlockLink.LoadGraceSeconds + 0.5f);
        Sim.Check(!r.P.Fd.Engaged && r.P.Panel.Mode == ApMode.Off && r.P.LastPanelReason == ApReason.NoWaypoints,
            $"block refused: navigate without a route - the panel back to {r.P.Panel.Mode} ('{ApText.Say(r.P.LastPanelReason)}')");
        var slow = FlightPack.PlaneAt("block refused slow", "Plane2", 150f);
        if (slow == null) return;
        slow.P.Fd.Disengage(ApReason.None);
        slow.P.V = Vector3.Normalize(slow.P.V) * 15f; slow.P.Damp = false;
        slow.P.AddBlock(ApPanel.Defaults);
        slow.P.Panel.Mode = ApMode.FlightHold; slow.P.Panel.Style = ApStyle.Airplane;
        slow.Fly(ApBlockLink.LoadGraceSeconds + 0.5f, rr => rr.P.V = Vector3.Normalize(rr.P.V) * 15f);
        Sim.Check(slow.P.Panel.Mode == ApMode.Off && slow.P.LastPanelReason == ApReason.TooSlow, $"block refused: a plane at 15 m/s - Off ('{ApText.Say(slow.P.LastPanelReason)}')");
        string st = ApBlockLink.Status(ApMode.Off, ApReason.TooSlow, false, true, false, float.NaN, ApHeightRef.Ground, ApProt.None);
        Sim.Check(st == "Off - " + ApText.Say(ApReason.TooSlow), $"block refused: the status line '{st}'");
    }

    static void Steer()
    {
        var r = new Run("block steer pilot", Ships.Heli(3f, true));
        r.P.Piloted = true;
        r.P.AddBlock(ApPanel.Defaults);
        r.P.Panel.Mode = ApMode.FlightHold;
        r.Fly(3f);
        Sim.Check(r.P.Fd.Engaged, $"block steer: flight hold engaged on the helicopter ({r.P.Fd.Goals})");
        r.P.PilotSteerDeg = 8f; r.Fly(0.1f); r.P.PilotSteerDeg = 0f; r.Fly(0.5f);
        Sim.Check(!r.P.Fd.Engaged && r.P.Panel.Mode == ApMode.Off && r.P.LastPanelReason == ApReason.Steered, $"block steer: steering let go - panel {r.P.Panel.Mode} ('{ApText.Say(r.P.LastPanelReason)}')");
    }

    static void LevelOff()
    {
        var r = FlightPack.PlaneAt("block level off", "Plane2", 160f);
        if (r == null) { Sim.NoPolar("Plane2"); return; }
        var air = (PolarAir)r.P.S.Air;
        // (a 50 deg bank and 20 deg nose down, the stick let go)
        r.P.Fd.Disengage(ApReason.None);
        r.P.Q = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(air.Nose, 50f * ApMath.Deg) * Quaternion.CreateFromAxisAngle(air.Right, -20f * ApMath.Deg) * r.P.Q);
        r.P.V = Vector3.Transform(air.Nose, r.P.Q) * 160f;
        r.P.AddBlock(ApPanel.Defaults);
        r.Fly(1f);
        double y0 = r.P.P.Y;
        r.P.Panel.Mode = ApMode.FlightHold;   // (the Level Off button: flight hold, every hold on, caught now)
        r.P.Panel.HeadingOn = r.P.Panel.AltOn = r.P.Panel.SpeedOn = true;
        float lost = 0f, bankEnd = 0f;
        r.Fly(25f, rr => lost = MathF.Max(lost, (float)(y0 - rr.P.P.Y)));
        var up = Vector3.Transform(air.Up, r.P.Q);
        bankEnd = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(up - Vector3.UnitY * 0f), Vector3.UnitY), -1f, 1f)) / ApMath.Deg;
        float vs = r.P.V.Y;
        Sim.Note($"block level off: from a 50 deg bank, 20 deg nose down at 160 m/s - lost {lost:F0} m, after 25 s the wings {bankEnd:F1} deg off level, climbing {vs:F1} m/s, prot {r.ProtSeen}");
        Sim.Check(r.P.Fd.Engaged && lost < 250f && bankEnd < 8f && MathF.Abs(vs) < 3f && !r.P.Crashed, $"block level off: recovered - lost {lost:F0} m (< 250), level within {bankEnd:F1} deg, vertical speed {vs:F1} m/s");
        Program.LedgerPublic(r, false);
    }

    static void Return()
    {
        var r = new Run("block return", FlightShips.SixAxis());
        r.P.P = new Vector3D(0, 100, 0);
        var cfg = ApPanel.Defaults; cfg.Route = "x"; cfg.SpeedMs = 20f; cfg.AltM = 98f; cfg.HeightRef = ApHeightRef.Ground; cfg.MinAglM = 10f;
        r.P.AddBlock(cfg);
        r.P.Link.Pilot.SetRoute(FlightPack.Pts((300, 0, 0)));
        r.Fly(0.5f);
        r.P.Panel.Mode = ApMode.Navigate;
        r.Fly(25f);
        float far = (float)new Vector2((float)r.P.P.X, (float)r.P.P.Z).Length();
        r.P.Panel.ReturnHome = true;
        r.Fly(30f);
        float back = (float)new Vector2((float)r.P.P.X, (float)r.P.P.Z).Length();
        Sim.Check(far > 250f && back < 3f && r.P.Link.Pilot.Loitering, $"block return: out at {far:F0} m, back within {back:F1} m of the engage point, loitering {r.P.Link.Pilot.Loitering}");
        Program.LedgerPublic(r, false);
    }

    static void StyleSwitch()
    {
        var r = new Run("block style", Ships.Vtol(3f, sideShare: 0.5f));
        r.P.AddBlock(ApPanel.Defaults);
        r.Fly(0.5f);
        r.P.Panel.Mode = ApMode.FlightHold; r.P.Panel.Style = ApStyle.SixAxis;
        r.Fly(2f);
        var a = r.P.Link.Style; var ga = r.P.Fd.Goals;
        r.P.Panel.Style = ApStyle.Helicopter;
        r.Fly(2f);
        Sim.Check(a == ApStyle.SixAxis && (ga & ApGoal.Hover) == 0 && r.P.Link.Style == ApStyle.Helicopter && (r.P.Fd.Goals & ApGoal.Hover) != 0 && r.P.Panel.Mode == ApMode.FlightHold,
            $"block style: six-axis ({ga}) then helicopter ({r.P.Fd.Goals}) on the panel's word, still {r.P.Panel.Mode}");
        Program.LedgerPublic(r, false);
    }

    static void SavedEngage()
    {
        var saved = ApPanel.Defaults; saved.Mode = ApMode.Engage; saved.Behaviour = ApBehaviour.Standoff; saved.RangeM = 120f; saved.SpeedMs = 15f; saved.AltOffsetM = 0f;
        AeroBlockSaveFormBridge.RoundTrip(ref saved);
        var r = new Run("block saved engage", FlightShips.SixAxis());
        r.P.P = new Vector3D(0, 100, 0);
        r.P.AddBlock(saved);
        var tgt = SimTarget.Straight(new Vector3D(200, 100, 0), Vector3.Zero); r.Targets.Add(tgt);
        r.P.Link.Pilot.Targets = new SimTargetSource { T = tgt };
        r.Fly(25f, rr => { if (Environment.GetEnvironmentVariable("AP_TRACE") == "saved" && rr.Ticks % 60 == 0) Console.WriteLine($"      t {rr.P.Time:F0} pos {rr.P.P} range {rr.Rel(tgt).Range:F0} goals {rr.P.Fd.Goals} panel {rr.P.Panel.Mode} bh {rr.P.Link.Pilot.ActiveBh} tgt {rr.P.Link.Pilot.T.Valid} goal {rr.P.Link.Pilot.Goal.Kind} {rr.P.Link.Pilot.Goal.Point}"); });
        float rng = r.Rel(tgt).Range;
        Sim.Check(r.P.Fd.Engaged && r.P.Panel.Mode == ApMode.Engage && r.P.Link.Pilot.ActiveBh == ApBehaviour.Standoff && MathF.Abs(rng - 120f) < 5f,
            $"block saved engage: at load it engaged ({r.P.Link.Pilot.ActiveBh}) and holds {rng:F1} m (120)");
    }
}

/// <summary>The panel through its saved text and back (the sim compiles the save form's reader: AeroBlockSaveForm).</summary>
static class AeroBlockSaveFormBridge
{
    public static void RoundTrip(ref ApPanel p)
    {
        string key = AeroBlockSaveForm.Autopilot(in p);
        bool ok = AeroBlockSaveForm.TryReadAutopilot(key, out var back);
        Sim.Check(ok && back.Mode == p.Mode && back.Behaviour == p.Behaviour && back.RangeM == p.RangeM, $"saved panel round trip ('{key}')");
        p = back;
    }
}
