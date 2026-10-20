using System.IO;
using AeroMod;

/// <summary>
/// THE HARNESS ITSELF: a Flight of three different members (a pilot in Flight Hold, a goal source holding a slot on it, an
/// unattended hover) stepped together; the per-run and per-flight metric hooks (Min / Max / Rms / Mean); a run's CSV trace with a
/// scenario's own columns; the UserScenarios folder's packs discovered like any other.
/// </summary>
sealed class HarnessPack : IScenarioPack
{
    ScenarioRegistry _reg;
    public void Register(ScenarioRegistry r)
    {
        _reg = r;
        r.Add("harness: a flight of three - a pilot, a goal source on it, an unattended hover; metrics; CSV columns; user packs found", true, Three);
    }

    void Three()
    {
        string was = Run.CsvDir, dir = Path.Combine(Path.GetTempPath(), "aero-apsim-harness");
        Run.CsvDir = dir;
        try
        {
            var f = new Flight("harness three");
            var lead = f.Add("leader", Ships.Heli(3f, true)); lead.P.P = new Vector3D(0, 120, 0);
            var follow = f.Add("follower", Ships.Vtol()); follow.P.P = new Vector3D(-40, 125, 15);
            var idle = f.Add("unattended", Ships.Heli(3f, true)); idle.P.P = new Vector3D(300, 80, 0);
            f.Fly(0.5f);
            var lp = Flight.Pilot(lead, ApPanel.Defaults, ApMode.FlightHold);
            lp.Cfg.HeadingDeg = 90f; lp.Cfg.SpeedMs = 8f;
            var src = new FollowShip { Ship = lead, Offset = new Vector3(-20f, 5f, 10f) };
            Sim.Check(Flight.Source(follow, src, ApGoal.Hover | ApGoal.Height | ApGoal.External), $"harness: the follower took its goal source ({follow.P.Fd.Reason})");
            idle.Measure(); Sim.Check(idle.Engage(ApGoal.Hover | ApGoal.Height), "harness: the third holds its hover");
            follow.Trace("slot_err_m", rr => (rr.P.P - (lead.P.P + (Vector3D)src.Offset)).Length());
            follow.Each(rr => { if (rr.P.Time > 30f) { rr.Rms("slot rms", (rr.P.P - (lead.P.P + (Vector3D)src.Offset)).Length()); rr.Max("slot max", (rr.P.P - (lead.P.P + (Vector3D)src.Offset)).Length()); } });
            f.Each(ff => { ff.Min("gap", (lead.P.P - follow.P.P).Length()); ff.Mean("lead speed", lead.P.HorizontalV.Length()); });
            f.Fly(50f);
            bool sameClock = MathF.Abs(lead.P.Time - follow.P.Time) < 1e-4f && MathF.Abs(lead.P.Time - idle.P.Time) < 1e-4f;
            double rms = follow.Metric("slot rms"), max = follow.Metric("slot max");
            Sim.Note($"harness: three ships {f.Ticks} ticks, clocks {lead.P.Time:F2} / {follow.P.Time:F2} / {idle.P.Time:F2} s; the follower's slot RMS {rms:F2} m (worst {max:F2}) after 30 s; the gap at least {f.Metric("gap"):F1} m; the leader's mean speed {f.Metric("lead speed"):F1} m/s; the hover {idle.HorizErr:F2} m off");
            Sim.Check(sameClock && f.Ticks == 3030, $"harness: every member stepped every tick ({f.Ticks}, the clocks the same: {sameClock})");
            Sim.Check(rms < 2f && max < 4f && f.Metric("gap") > 10f, $"harness: the goal source held its slot on the flown leader - RMS {rms:F2} m (< 2), worst {max:F2} (< 4)");
            Sim.Check(idle.P.Fd.Engaged && idle.HorizErr < 1f, $"harness: the third member flew on its own ({idle.HorizErr:F2} m off its place)");
            var acc = new Run("harness rms", Ships.Heli(3f)); acc.Rms("x", 3); acc.Rms("x", 4); acc.Mean("y", 2); acc.Mean("y", 6); acc.Min("z", 5); acc.Min("z", 2); acc.Close();
            Sim.Check(MathF.Abs((float)acc.Metric("x") - MathF.Sqrt(12.5f)) < 1e-5f && acc.Metric("y") == 4 && acc.Metric("z") == 2 && double.IsNaN(acc.Metric("none")),
                $"harness: the metric hooks - Rms {acc.Metric("x"):F4}, Mean {acc.Metric("y")}, Min {acc.Metric("z")}");
            f.Ledger();
            string csv = Path.Combine(dir, "harness_three_-_follower.csv");
            var lines = File.Exists(csv) ? File.ReadAllLines(csv) : Array.Empty<string>();
            Sim.Check(lines.Length > 400 && lines[0].EndsWith(",slot_err_m") && lines[1].Split(',').Length == lines[0].Split(',').Length,
                $"harness: the follower's CSV trace - {lines.Length} lines, its own column last ({(lines.Length > 0 ? lines[0].Substring(Math.Max(0, lines[0].Length - 24)) : "none")})");
        }
        finally { Run.CsvDir = was; }
        int user = _reg.All.Count(s => s.name.StartsWith("user: "));
        Sim.Check(user >= 1, $"harness: the UserScenarios packs found and registered ({user} scenario{(user == 1 ? "" : "s")} named 'user: ...')");
    }
}
