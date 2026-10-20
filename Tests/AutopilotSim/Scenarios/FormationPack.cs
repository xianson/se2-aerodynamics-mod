using AeroMod;

/// <summary>
/// FORMATION FLYING in a multi-ship flight (Flight): a leader flown by its own pilot (Flight Hold - its heading, height and speed
/// changed as a pilot would) and one or two wingmen in Engage / Formation with the leader as their target - its motion only
/// observed (the track from its positions). Each flight style: AIRPLANE (the analytic plane - no polar needed), HELICOPTER (the
/// H25, tilt movement), SIX-AXIS (the thruster gunship). The phases: the CATCH-UP from well off the slot (the time until the slot
/// error stays inside the style's tolerance), the leader's 90 deg TURN, its CLIMB, STRAIGHT again - the slot error's RMS and worst
/// in each, PASS/FAIL against the style's thresholds; two wingmen on opposite sides never closer than a safe gap. The ledger on
/// every ship (pilot inputs, real actuators only).
/// </summary>
sealed class FormationPack : IScenarioPack
{
    public void Register(ScenarioRegistry r)
    {
        foreach (var st in new[] { ApStyle.Airplane, ApStyle.Helicopter, ApStyle.SixAxis })
        {
            r.Add($"formation: {Word(st)} - catch-up, the leader's turn and climb, slot error RMS", true, () => Fly(st, 1));
            r.Add($"formation: {Word(st)} - two wingmen either side through the turn and climb", true, () => Fly(st, 2));
        }
    }

    static string Word(ApStyle s) => s == ApStyle.Airplane ? "airplane" : s == ApStyle.Helicopter ? "helicopter (tilt)" : "six-axis";

    /// <summary>A style's numbers: the leader's speed (m/s), the slot (right, back m), the start off it (back, right m), the turn
    /// and climb, the catch-up tolerance (m) and limit (s), and the RMS / worst thresholds per phase (m).</summary>
    sealed class Spec
    {
        public float Speed, Right, Back, StartBack, StartRight, Climb, Tol, CatchMax, TurnRms, TurnMax, ClimbRms, ClimbMax, StraightRms, MinGap;
        public float CatchT, TurnT, ClimbT, StraightT, TurnRate = 4f;
    }

    // (the thresholds: about 1.5..2x what the formation flies today - 2026-10-20: airplane catch-up 39 s, turn RMS 5.6 m / worst
    //  12, climb 5.0 / 11, straight 2.2; helicopter 41 s, 0.9 / 1.6, 1.8 / 7.7, 0.74; six-axis 26 s, 0.4 / 1.3, 1.5 / 6.8, 0.21)
    static Spec SpecOf(ApStyle st) => st switch
    {
        ApStyle.Airplane => new Spec { Speed = 150f, Right = 40f, Back = 40f, StartBack = 400f, StartRight = 150f, Climb = 200f, Tol = 15f, CatchMax = 60f,
            TurnRms = 12f, TurnMax = 25f, ClimbRms = 9f, ClimbMax = 18f, StraightRms = 4f, MinGap = 30f, CatchT = 80f, TurnT = 45f, ClimbT = 40f, StraightT = 30f },
        ApStyle.Helicopter => new Spec { Speed = 15f, Right = 25f, Back = 25f, StartBack = 120f, StartRight = 60f, Climb = 40f, Tol = 4f, CatchMax = 55f,
            TurnRms = 2.5f, TurnMax = 5f, ClimbRms = 4f, ClimbMax = 12f, StraightRms = 1.5f, MinGap = 20f, CatchT = 60f, TurnT = 40f, ClimbT = 30f, StraightT = 30f },
        _ => new Spec { Speed = 20f, Right = 20f, Back = 20f, StartBack = 120f, StartRight = 60f, Climb = 40f, Tol = 3f, CatchMax = 40f,
            TurnRms = 1.5f, TurnMax = 4f, ClimbRms = 3.5f, ClimbMax = 11f, StraightRms = 1f, MinGap = 15f, CatchT = 50f, TurnT = 40f, ClimbT = 30f, StraightT = 30f },
    };

    static SimShip ShipOf(ApStyle st, string name) => st switch
    {
        ApStyle.Airplane => Planes.Analytic(name: name),
        ApStyle.Helicopter => Ships.Heli(3f, true, name: name),
        _ => FlightShips.SixAxis(name: name),
    };

    static Run Member(Flight f, ApStyle st, string member, Vector3D at, float speed)
    {
        var s = ShipOf(st, $"{Word(st)} {member}");
        if (st == ApStyle.Airplane) return f.Add(Planes.TrimmedAt($"{f.Name} - {member}", s, at, speed, 90f));
        var r = f.Add(member, s); r.P.P = at;
        return r;
    }

    static void Fly(ApStyle st, int wingmen)
    {
        var sp = SpecOf(st);
        var f = new Flight($"formation {Word(st)} x{wingmen}");
        double y = st == ApStyle.Airplane ? 3000 : 150;
        // the leader heading east (+X); the wingmen behind it (-X) and off to its right (+Z: east's right is south) / left
        var lead = Member(f, st, "leader", new Vector3D(0, y, 0), sp.Speed);
        var wing = new List<Run>();
        for (int k = 0; k < wingmen; k++)
        {
            float side = k == 0 ? 1f : -1f;
            wing.Add(Member(f, st, $"wing {k + 1}", new Vector3D(-sp.StartBack - 60 * k, y - 20, side * sp.StartRight), st == ApStyle.Airplane ? sp.Speed : 0f));
        }
        if (st != ApStyle.Airplane) f.Fly(0.5f);
        // the leader's pilot: Flight Hold east at its speed and this height (the scenario changes them as a pilot would)
        // (its bank 30 deg: the wingmen's 45 - a leader turning at the wingmen's own limit leaves them nothing to catch up with)
        var lc = ApPanel.Defaults; lc.HeightRef = ApHeightRef.Inertial; lc.MinAglM = 0f; lc.BankDeg = 30f;
        var lp = Flight.Pilot(lead, lc, ApMode.FlightHold);
        lp.Cfg.HeadingDeg = 90f; lp.Cfg.SpeedMs = sp.Speed;
        if (st != ApStyle.Airplane) f.Fly(20f);   // (a hovering leader up to its speed first)
        var pilots = new List<ApPilot>();
        for (int k = 0; k < wingmen; k++)
        {
            var c = ApPanel.Defaults; c.Behaviour = ApBehaviour.Formation; c.HeightRef = ApHeightRef.Inertial; c.MinAglM = 0f;
            c.FmRightM = k == 0 ? sp.Right : -sp.Right; c.FmBackM = sp.Back; c.FmUpM = 0f;
            pilots.Add(Flight.Pilot(wing[k], c, ApMode.Engage, Flight.Target(lead)));
        }
        for (int k = 0; k < wingmen; k++)
        {
            var p = pilots[k];   // (the trace's own columns: AP_CSV / -- csv folder)
            wing[k].Trace("slot_err_m", _ => p.Formation.Err).Trace("slot_along_m", _ => p.Formation.Along).Trace("slot_across_m", _ => p.Formation.Across).Trace("slot_up_m", _ => p.Formation.Up);
        }
        string phase = "catch";
        float tIn = -1f, t0 = f.Time, caught = -1f;
        f.Each(ff =>
        {
            for (int k = 0; k < wingmen; k++)
            {
                float e = pilots[k].Formation.Err;
                ff.Max($"{phase} max", e); ff.Rms($"{phase} rms", e);
            }
            if (phase == "catch")
            {
                float worst = 0f; foreach (var p in pilots) worst = MathF.Max(worst, p.Formation.Err);
                if (worst < sp.Tol) { if (tIn < 0f) tIn = ff.Time; if (caught < 0f && ff.Time - tIn >= 3f) caught = tIn - t0; } else tIn = -1f;
            }
            if (wingmen == 2) ff.Min("gap", (wing[0].P.P - wing[1].P.P).Length());
            foreach (var w in wing) ff.Min("lead gap", (w.P.P - lead.P.P).Length());
        });
        f.Fly(sp.CatchT);
        // the leader's turn: 90 deg right (south), then its climb, then straight
        // (an airplane leader's turn is its bank's - 30 deg; a hovering leader's heading is turned as a pilot turns it, TurnRate deg/s
        //  - its course set at once would swing round in a few seconds, the slot with it)
        phase = "turn";
        if (st == ApStyle.Airplane) lp.Cfg.HeadingDeg = 180f;
        else f.Each(ff => { if (phase == "turn") lp.Cfg.HeadingDeg = MathF.Min(180f, lp.Cfg.HeadingDeg + sp.TurnRate / 60f); });
        f.Fly(sp.TurnT);
        phase = "climb"; lp.Cfg.AltM = lp.Cfg.AltM + sp.Climb; f.Fly(sp.ClimbT);
        phase = "straight"; f.Fly(sp.StraightT);
        double M(string k) => f.Metric(k);
        string slots = string.Join(", ", pilots.Select(p => $"{p.Formation.Err:F1} m (along {p.Formation.Along:F1}, across {p.Formation.Across:F1}, up {p.Formation.Up:F1})"));
        Sim.Note($"{f.Name}: caught the slot (< {sp.Tol} m for 3 s) after {caught:F1} s; turn RMS {M("turn rms"):F1} m (worst {M("turn max"):F1}), climb RMS {M("climb rms"):F1} m (worst {M("climb max"):F1}), straight RMS {M("straight rms"):F2} m; at the end {slots}; closest to the leader {M("lead gap"):F0} m{(wingmen == 2 ? $", the wingmen {M("gap"):F0} m apart at closest" : "")}; leader heading {lp.HeadingNow:F0}, height {lp.HeightNow:F0} ({lp.Cfg.AltM:F0} asked)");
        Sim.Check(caught >= 0f && caught < sp.CatchMax, $"{f.Name}: catch-up - in the slot (< {sp.Tol} m, 3 s) after {caught:F1} s (< {sp.CatchMax})");
        Sim.Check(M("turn rms") < sp.TurnRms && M("turn max") < sp.TurnMax, $"{f.Name}: the leader's 90 deg turn - slot error RMS {M("turn rms"):F1} m (< {sp.TurnRms}), worst {M("turn max"):F1} m (< {sp.TurnMax})");
        Sim.Check(M("climb rms") < sp.ClimbRms && M("climb max") < sp.ClimbMax, $"{f.Name}: the leader's {sp.Climb:F0} m climb - slot error RMS {M("climb rms"):F1} m (< {sp.ClimbRms}), worst {M("climb max"):F1} m (< {sp.ClimbMax})");
        Sim.Check(M("straight rms") < sp.StraightRms, $"{f.Name}: straight again - slot error RMS {M("straight rms"):F2} m (< {sp.StraightRms})");
        Sim.Check(M("lead gap") > sp.MinGap * 0.5f && (wingmen < 2 || M("gap") > sp.MinGap), $"{f.Name}: no ship too close - the leader {M("lead gap"):F0} m (> {sp.MinGap * 0.5f:F0}){(wingmen == 2 ? $", the wingmen {M("gap"):F0} m (> {sp.MinGap})" : "")}");
        Sim.Check(MathF.Abs(ApCompass.Diff(lp.HeadingNow, 180f)) < 5f && MathF.Abs(lp.HeightNow - lp.Cfg.AltM) < 15f, $"{f.Name}: the leader flew its turn and climb (heading {lp.HeadingNow:F0}, height {lp.HeightNow:F0} of {lp.Cfg.AltM:F0})");
        foreach (var w in wing) Sim.Check(w.P.Fd.Engaged, $"{w.Name}: still in formation at the end ({w.P.Fd.Reason})");
        f.Ledger();
    }
}
