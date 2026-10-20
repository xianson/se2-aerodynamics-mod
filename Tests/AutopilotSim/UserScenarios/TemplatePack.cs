using AeroMod;

// THE AUTHOR'S SCENARIO PACKS live in this folder (see README.md here): any class implementing IScenarioPack is found at start.
// This one is the template - copy it to a file of your own (MyPack.cs, a class MyPack), rename the scenarios, and fly yours.
// Kept out of the gate (fast: false); run it by name: dotnet run --project Tests/AutopilotSim -c Release -- "user: template"
sealed class TemplatePack : IScenarioPack
{
    public void Register(ScenarioRegistry r)
    {
        r.Add("user: template - a wingman holding a slot on a helicopter leader in Flight Hold", false, Template);
    }

    static void Template()
    {
        // 1. the flight: its members - each a ship (Ships.*, FlightShips.*, Planes.*, Rockets.*, or a SimShip of your own) at a place
        var f = new Flight("template");
        var lead = f.Add("leader", Ships.Heli(3f, true)); lead.P.P = new Vector3D(0, 120, 0);
        var wing = f.Add("wing", Ships.Heli(3f, true)); wing.P.P = new Vector3D(-80, 120, 40);
        f.Fly(0.5f);
        // 2. what each flies: a pilot mode with a panel (ApPanel - the block's values), a goal source of your own, or nothing
        var lp = Flight.Pilot(lead, ApPanel.Defaults, ApMode.FlightHold);
        lp.Cfg.HeadingDeg = 90f; lp.Cfg.SpeedMs = 12f;
        var wc = ApPanel.Defaults; wc.Behaviour = ApBehaviour.Formation; wc.FmRightM = 25f; wc.FmBackM = 25f; wc.HeightRef = ApHeightRef.Inertial;
        var wp = Flight.Pilot(wing, wc, ApMode.Engage, Flight.Target(lead));
        // 3. your metrics (each tick, after every member stepped) and trace columns (AP_CSV=folder or -- csv folder)
        wing.Trace("slot_err_m", _ => wp.Formation.Err);
        f.Each(ff => { if (ff.Time > 40f) ff.Rms("slot rms", wp.Formation.Err); });
        // 4. the timeline: fly, change what the leader does, fly on
        f.Fly(45f);
        lp.Cfg.AltM += 20f;
        f.Fly(20f);
        // 5. the verdict: Sim.Note (shown with "verbose"), Sim.Check (PASS / FAIL - the exit code), every member's ledger
        Sim.Note($"user template: the slot RMS {f.Metric("slot rms"):F2} m after 40 s");
        Sim.Check(f.Metric("slot rms") < 5.0, $"user template: the wingman held its slot (RMS {f.Metric("slot rms"):F2} m < 5)");
        f.Ledger();
    }
}
