using AeroMod;

/// <summary>
/// THE HEIGHT WITH THE GROUND OUT OF THE RAYS' REACH (in game 2026-10-19: a typed height over the ground with the ground past the
/// rays changed the panel's number and nothing else; a level hold engaged with no ground read "holding 0.0 m altitude" at 130 m).
/// The rays mocked as the game's (PlanetTerrainPack.BlindRays, and ApSense.Agl blind past the same reach); the planet's height as
/// PlanetTerrainPack.FlatPlanet. With the planet: the typed height is flown over the real ground. Without: held along the
/// gravity, over the last ground seen, and the status line says so; a ground lost mid-hold keeps the height, no jump.
/// </summary>
sealed class HeightNoGroundPack : IScenarioPack
{
    const float Reach = 150f;

    public void Register(ScenarioRegistry r)
    {
        r.Add("no ground: a typed height past the rays' reach - the planet's height flies it over the ground", true, () => Typed(planet: true));
        r.Add("no ground: a typed height past the rays' reach, no planet - held along the gravity, said", true, () => Typed(planet: false));
        r.Add("no ground: the ground lost mid-hold - the height kept, no jump", true, Lost);
        r.Add("no ground: a level hold engaged out of reach - the planet's datum, else said", true, Level);
        r.Add("block: a mode the ship never takes up times out to Off, said; the steering toast keeps height hold", true, Words);
    }

    static Run Make(string name, bool planet, float y)
    {
        var run = new Run(name, Ships.Heli(3f));
        var rays = new PlanetTerrainPack.BlindRays { T = run.P.Terrain, Reach = Reach };
        run.P.AglReach = Reach;
        run.P.Fd.Terrain = planet ? new CombinedTerrain(rays, new PlanetTerrainQuery(new PlanetTerrainPack.FlatPlanet { T = run.P.Terrain })) : rays;
        run.P.P = new Vector3D(0, y, 0);
        return run;
    }

    static void Typed(bool planet)
    {
        string tag = planet ? "planet" : "no planet";
        var run = Make("typed height " + tag, planet, 60f);
        var p = ApPanel.Defaults; p.HeightRef = ApHeightRef.Ground;
        run.P.AddBlock(p);
        run.Fly(0.5f);
        run.P.Panel.Mode = ApMode.HoverHold; run.Fly(12f);
        Sim.Check(run.P.Fd.Engaged && !run.P.Fd.NoGround, $"typed ({tag}): hover hold engaged over the ground in reach ({run.P.Agl:F1} m)");
        run.P.Panel.HeightM = 300f;
        run.Fly(90f);
        float agl = run.P.Agl;
        string st = ApBlockLink.Status(run.P.Panel.Mode, ApReason.None, run.P.Fd.Engaged, true, false, run.P.Fd.HeldHeight(out float hh) ? hh : float.NaN, run.P.Fd.HeightRef, ApProt.None, run.P.Fd.NoGround);
        Sim.Note($"typed ({tag}): 300 m typed - flew to {agl:F1} m over the ground (rays reach {Reach} m), panel {run.P.Panel.HeightM:F1}, '{st}'");
        Sim.Check(MathF.Abs(agl - 300f) < 3f && MathF.Abs(run.P.Panel.HeightM - 300f) < 0.01f,
            $"typed ({tag}): the typed 300 m flown past the rays' reach ({agl:F1} m), the panel keeps it ({run.P.Panel.HeightM:F1})");
        if (planet) Sim.Check(!run.P.Fd.NoGround && !st.Contains("no ground"), $"typed ({tag}): the planet's height is the ground - nothing to say ('{st}')");
        else Sim.Check(run.P.Fd.NoGround && st.Contains("no ground in sight: held along the gravity"), $"typed ({tag}): held along the gravity, said ('{st}')");
        Program.LedgerPublic(run, false);
    }

    static void Lost()
    {
        var run = Make("ground lost", false, 60f);
        var p = ApPanel.Defaults; p.HeightRef = ApHeightRef.Ground;
        run.P.AddBlock(p);
        run.Fly(0.5f);
        run.P.Panel.Mode = ApMode.HoverHold; run.Fly(5f);
        run.P.Panel.HeightM = 100f; run.Fly(40f);
        float before = run.P.Agl;
        // (the rays go short: the ground out of sight from here on)
        ((PlanetTerrainPack.BlindRays)run.P.Fd.Terrain).Reach = 50f; run.P.AglReach = 50f;
        float lo = float.MaxValue, hi = float.MinValue;
        run.Fly(30f, r => { lo = MathF.Min(lo, r.P.Agl); hi = MathF.Max(hi, r.P.Agl); });
        Sim.Check(MathF.Abs(before - 100f) < 2f && run.P.Fd.NoGround && lo > before - 2f && hi < before + 2f && run.P.Fd.Engaged,
            $"ground lost: at {before:F1} m when the ground went out of sight, then {lo:F1}..{hi:F1} m for 30 s along the gravity (no jump), NoGround {run.P.Fd.NoGround}");
        Program.LedgerPublic(run, false);
    }

    static void Level()
    {
        foreach (bool planet in new[] { true, false })
        {
            string tag = planet ? "planet" : "no planet";
            var run = Make("level " + tag, planet, 300f);
            var p = ApPanel.Defaults; p.HeightRef = ApHeightRef.Inertial;
            run.P.AddBlock(p);
            run.Fly(0.2f);
            run.P.Panel.Mode = ApMode.HoverHold; run.Fly(15f);
            bool has = run.P.Fd.HeldHeight(out float h);
            string st = ApBlockLink.Status(run.P.Panel.Mode, ApReason.None, run.P.Fd.Engaged, true, false, has ? h : float.NaN, run.P.Fd.HeightRef, ApProt.None, run.P.Fd.NoGround);
            Sim.Note($"level ({tag}): engaged at {run.P.Agl:F1} m over the ground - '{st}'");
            if (planet) Sim.Check(has && MathF.Abs(h - run.P.Agl) < 3f && !run.P.Fd.NoGround, $"level ({tag}): the datum is the planet's ground - reads {h:F1} m at {run.P.Agl:F1} m ('{st}')");
            else Sim.Check(run.P.Fd.NoGround && st.Contains("no ground found"), $"level ({tag}): no ground known - said ('{st}')");
            Program.LedgerPublic(run, false);
        }
    }

    static void Words()
    {
        var w = new ApEngageWatch();
        int fired = 0, at = -1;
        for (int i = 1; i <= ApEngageWatch.TimeoutTicks + 5; i++) if (w.Step(ApMode.HoverHold, false, false)) { fired++; at = i; }
        Sim.Check(fired == 1 && at == ApEngageWatch.TimeoutTicks, $"engage watch: a hover hold never flown - timed out once, at tick {at} ({fired}x)");
        var w2 = new ApEngageWatch(); bool any = false;
        for (int i = 0; i < 3 * ApEngageWatch.TimeoutTicks; i++) any |= w2.Step(ApMode.HoverHold, i > 100, false);
        var w3 = new ApEngageWatch();
        for (int i = 0; i < 3 * ApEngageWatch.TimeoutTicks; i++) any |= w3.Step(ApMode.HeightHold, false, true);
        var w4 = new ApEngageWatch();
        for (int i = 0; i < 3 * ApEngageWatch.TimeoutTicks; i++) any |= w4.Step(i % 400 < 200 ? ApMode.HoverHold : ApMode.HeightHold, false, false);
        Sim.Check(!any, "engage watch: flying, standing by, or a mode changed within the time - never times out");
        string st = ApBlockLink.Status(ApMode.Off, ApReason.NoResponse, false, true, false, float.NaN, ApHeightRef.Ground, ApProt.None);
        Sim.Check(st.StartsWith("Off - ") && st.Contains("thrusters or gyros"), $"engage watch: the status says why ('{st}')");
        string t1 = ApText.Off(ApReason.Steered, ApGoal.Height), t2 = ApText.Off(ApReason.Steered), t3 = ApText.Off(ApReason.TurnedOff);
        Sim.Check(t1 == "Hover off - you steered (height hold on)" && t2 == "Autopilot off - you steered" && t3 == "Autopilot off",
            $"toasts: a steer that keeps height hold says so ('{t1}'; all off: '{t2}', '{t3}')");
    }
}
