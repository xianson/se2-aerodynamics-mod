using AeroMod;

/// <summary>
/// SMALL COLLISION AVOIDANCE (Autopilot/ApAvoid.cs, applied by FlightDirector on top of every mode): a parked ship on the route
/// (Plane2 at 150 m/s; the six-axis gunship), an asteroid on the route (Plane2), a target an attack run with a 5 m abort distance
/// would ram, the hover's sidestep; each with the avoidance off and on - the least separation (past both radii), the most it left
/// its path, back on it after. The arithmetic of the closest approach and the target's clearance on its own.
/// </summary>
sealed class AvoidPack : IScenarioPack
{
    public void Register(ScenarioRegistry r)
    {
        r.Add("avoid: the closest approach's arithmetic, the target's clearance by behaviour", true, Math_);
        r.Add("avoid: a parked ship on the route (Plane2, 150 m/s) - off: hits it; on: passes it, back on the route", true, () => Route("Plane2", 30f, "parked ship"));
        r.Add("avoid: an asteroid on the route (Plane2, 150 m/s, 8 s look-ahead)", true, () => Route("Plane2", 120f, "asteroid", look: 8f));
        r.Add("avoid: a parked ship on the six-axis gunship's route", true, SixRoute);
        r.Add("avoid: an attack run with a 5 m abort distance would ram the target - on: it passes clear", true, Ram);
        r.Add("avoid: hovering, a ship drifting into it - the hover steps aside and comes back", true, HoverDrift);
    }

    static void Math_()
    {
        var obs = new ApObstacles();
        var av = new ApAvoid();
        // head-on, 400 m ahead at 100 m/s closing, both 10 m: a conflict in 4 s, pushed up (straight at it: up)
        obs.Add(new Vector3D(0, 0, -400), Vector3.Zero, 10f);
        av.Step(obs, Vector3D.Zero, new Vector3(0, 0, -100), 10f, 5f, 20f, float.PositiveInfinity, Vector3.UnitY, -1f, 1f / 60f);
        Sim.Check(av.Active && MathF.Abs(av.Tca - 4f) < 0.01f && av.Dir.Y > 0.99f && av.Accel > 0f, $"head-on: a conflict in {av.Tca:F2} s (4), up ({av.Dir.Y:F2}), {av.Accel:F1} m/s2");
        // beyond the look-ahead: nothing
        var av2 = new ApAvoid(); av2.Step(obs, Vector3D.Zero, new Vector3(0, 0, -100), 10f, 3f, 20f, float.PositiveInfinity, Vector3.UnitY, -1f, 1f / 60f);
        Sim.Check(!av2.Active, "a conflict past the look-ahead (4 s > 3 s): nothing yet");
        // passing 60 m to the side: clear (the clearance 10 + 10 + 5 + 5)
        obs.Clear(); obs.Add(new Vector3D(60, 0, -400), Vector3.Zero, 10f);
        var av3 = new ApAvoid(); av3.Step(obs, Vector3D.Zero, new Vector3(0, 0, -100), 10f, 5f, 20f, float.PositiveInfinity, Vector3.UnitY, -1f, 1f / 60f);
        Sim.Check(!av3.Active && MathF.Abs(av3.MinMiss - 40f) < 0.1f, $"a pass 60 m to the side: clear, the miss {av3.MinMiss:F1} m past the radii");
        // 15 m to the side: pushed away from it (-X), near the ground: level
        obs.Clear(); obs.Add(new Vector3D(15, -2, -400), Vector3.Zero, 10f);
        var av4 = new ApAvoid(); av4.Step(obs, Vector3D.Zero, new Vector3(0, 0, -100), 10f, 5f, 20f, float.PositiveInfinity, Vector3.UnitY, 20f, 1f / 60f);
        Sim.Check(av4.Active && av4.Dir.X < -0.9f && MathF.Abs(av4.Dir.Y) < 0.2f, $"a near miss to the right: pushed left ({av4.Dir.X:F2}, {av4.Dir.Y:F2})");
        // moving away from us: no conflict
        obs.Clear(); obs.Add(new Vector3D(0, 0, -100), new Vector3(0, 0, -150), 10f);
        var av5 = new ApAvoid(); av5.Step(obs, Vector3D.Zero, new Vector3(0, 0, -100), 10f, 5f, 20f, float.PositiveInfinity, Vector3.UnitY, -1f, 1f / 60f);
        Sim.Check(!av5.Active, "an obstacle running away faster: no conflict");
        // released ReleaseS after the last conflict
        var av6 = new ApAvoid(); obs.Clear(); obs.Add(new Vector3D(0, 0, -200), Vector3.Zero, 10f);
        av6.Step(obs, Vector3D.Zero, new Vector3(0, 0, -100), 10f, 5f, 20f, float.PositiveInfinity, Vector3.UnitY, -1f, 1f / 60f);
        obs.Clear(); int k = 0; while (av6.Active && k < 600) { av6.Step(obs, Vector3D.Zero, new Vector3(0, 0, -100), 10f, 5f, 20f, float.PositiveInfinity, Vector3.UnitY, -1f, 1f / 60f); k++; }
        Sim.Check(k >= 59 && k <= 61, $"let go {k / 60f:F2} s after the last conflict ({ApAvoid.ReleaseS} s)");
        // the target's clearance: half the behaviour's own closest distance
        var p = ApPanel.Defaults; p.AbortM = 250f; p.RangeM = 800f; p.AltOffsetM = 60f;
        Sim.Check(ApAvoid.TargetClear(in p, ApBehaviour.AttackRun) == 125f && ApAvoid.TargetClear(in p, ApBehaviour.HoverAbove) == 30f
                  && ApAvoid.TargetClear(in p, ApBehaviour.Standoff) == 400f && ApAvoid.TargetClear(in p, ApBehaviour.Auto) == 30f,
                  "target clearance: attack run 125 (abort 250), hover above 30 (60 over), standoff 400 (range 800), auto the least");
    }

    /// <summary>Plane2 navigating level along -X at 3000 m; an obstacle (radius r) parked on the line 3 km ahead.</summary>
    static void Route(string polar, float radius, string what, float look = 4f)
    {
        float sepOff = 0, sepOn = 0, dev = 0, back = 0, nMax = 0;
        foreach (bool on in new[] { false, true })
        {
            var r = FlightPack.PlaneAt($"{what} on the route ({(on ? "avoid on" : "off")})", polar, 150f);
            if (r == null) { Sim.NoPolar(polar); return; }
            r.P.S.Radius = 15f;
            var air = (PolarAir)r.P.S.Air;
            Vector3 fwd = Vector3.Normalize(new Vector3(air.Nose.X, 0f, air.Nose.Z));
            Vector3D start = r.P.P, at = start + (Vector3D)(fwd * 3000f);
            var obs = new ApObstacles(); obs.Add(at, Vector3.Zero, radius);
            r.P.Fd.Obstacles = obs;
            var cfg = ApPanel.Defaults; cfg.AltM = 3000f; cfg.SpeedMs = 150f; cfg.HeightRef = ApHeightRef.Inertial; cfg.MinAglM = 0f; cfg.AvoidOn = on; cfg.AvoidS = look;
            r.P.LimOverride = ApLimits.Of(in cfg);
            Vector3D far = start + (Vector3D)(fwd * 12000f);
            FlightPack.Engage(r, cfg, ApMode.Navigate, route: FlightPack.Pts((far.X, far.Y, far.Z)));
            float minSep = float.MaxValue, maxDev = 0f; float endDev = 0f; float n = 0f;
            Vector3 side = Vector3.Cross(fwd, Vector3.UnitY);
            r.Each(rr =>
            {
                float sep = (float)(rr.P.P - at).Length() - radius - rr.P.S.Radius;
                minSep = MathF.Min(minSep, sep);
                var off = (Vector3)(rr.P.P - start); float along = Vector3.Dot(off, fwd);
                float lat = MathF.Sqrt(MathF.Max(0f, off.LengthSquared() - along * along));
                maxDev = MathF.Max(maxDev, lat); endDev = lat; n = MathF.Max(n, rr.P.LoadFactor);
            });
            r.Fly(50f);
            if (on) { sepOn = minSep; dev = maxDev; back = endDev; nMax = n; Program.LedgerPublic(r, false); } else { sepOff = minSep; r.Close(); }
        }
        Sim.Note($"{what} (radius {radius} m) on Plane2's route at 150 m/s, look-ahead {look} s: off - the least separation {sepOff:F0} m (past both radii; < 0: hit); on - {sepOn:F0} m, at most {dev:F0} m off the line, {back:F0} m off it 30 s after, at most {nMax:F2} g");
        Sim.Check(sepOff < 0f && sepOn > 0f && back < 60f, $"{what}: off it hits ({sepOff:F0} m), on it passes {sepOn:F0} m clear and is back near the line ({back:F0} m)");
    }

    static void SixRoute()
    {
        float sepOff = 0, sepOn = 0, dev = 0;
        foreach (bool on in new[] { false, true })
        {
            var ship = FlightShips.SixAxis(); ship.Radius = 6f;
            var r = new Run($"six-axis route ({(on ? "avoid on" : "off")})", ship);
            r.P.P = new Vector3D(0, 150, 0); r.Fly(0.5f);
            var at = new Vector3D(400, 150, 0);
            var obs = new ApObstacles(); obs.Add(at, Vector3.Zero, 12f);
            r.P.Fd.Obstacles = obs;
            var cfg = ApPanel.Defaults; cfg.AltM = 150f; cfg.SpeedMs = 20f; cfg.HeightRef = ApHeightRef.Inertial; cfg.MinAglM = 0f; cfg.AvoidOn = on;
            r.P.LimOverride = ApLimits.Of(in cfg);
            FlightPack.Engage(r, cfg, ApMode.Navigate, route: FlightPack.Pts((1200, 150, 0)));
            float minSep = float.MaxValue, maxDev = 0f;
            r.Each(rr => { minSep = MathF.Min(minSep, (float)(rr.P.P - at).Length() - 12f - 6f); maxDev = MathF.Max(maxDev, (float)Math.Sqrt((rr.P.P.Y - 150) * (rr.P.P.Y - 150) + rr.P.P.Z * rr.P.P.Z)); });
            r.Fly(50f);
            if (on) { sepOn = minSep; dev = maxDev; Program.LedgerPublic(r, false); } else { sepOff = minSep; r.Close(); }
        }
        Sim.Note($"six-axis route at 20 m/s past a parked 12 m ship: off - the least separation {sepOff:F1} m; on - {sepOn:F1} m, at most {dev:F0} m off the line");
        Sim.Check(sepOff < 0f && sepOn > 0f, $"six-axis: off it hits ({sepOff:F1} m), on it passes {sepOn:F1} m clear");
    }

    static void Ram()
    {
        float sepOff = 0, sepOn = 0; int runsOn = 0;
        foreach (bool on in new[] { false, true })
        {
            var r = FlightPack.PlaneAt($"attack run abort 5 m ({(on ? "avoid on" : "off")})", "Plane2", 150f);
            if (r == null) { Sim.NoPolar("Plane2"); return; }
            r.P.S.Radius = 15f;
            var air = (PolarAir)r.P.S.Air;
            Vector3 fwd = Vector3.Normalize(new Vector3(air.Nose.X, 0f, air.Nose.Z));
            var tgt = SimTarget.Straight(r.P.P + (Vector3D)(fwd * 2500f), fwd * -5f); r.Targets.Add(tgt);
            var cfg = ApPanel.Defaults; cfg.Behaviour = ApBehaviour.AttackRun; cfg.RangeM = 2000f; cfg.AbortM = 5f; cfg.RerunS = 60f; cfg.AltOffsetM = 0f;
            cfg.HeightRef = ApHeightRef.Inertial; cfg.MinAglM = 0f; cfg.AvoidOn = on;
            r.P.LimOverride = ApLimits.Of(in cfg);
            var p = FlightPack.Engage(r, cfg, ApMode.Engage, new SimTargetSource { T = tgt, Radius = 20f });
            float minSep = float.MaxValue;
            r.Each(rr => minSep = MathF.Min(minSep, rr.Rel(tgt).Range - 20f - 15f));
            if (Environment.GetEnvironmentVariable("AP_TRACE") == "ram")
                r.Each(rr => { if (rr.Ticks % 30 == 0) Console.WriteLine($"      t {rr.P.Time:F1} range {rr.Rel(tgt).Range:F0} avoid {rr.P.Fd.Avoid.Active} miss {rr.P.Fd.Avoid.MinMiss:F0} tca {rr.P.Fd.Avoid.Tca:F1} a {rr.P.Fd.Avoid.Accel:F1} conf {rr.P.Fd.Avoid.Conflicts} bh {p.ActiveBh} lim {rr.P.SenseNow().Lim.Avoid} clr {rr.P.Fd.TargetClearMax:F1} prot {rr.P.Cmd.Prot}"); });
            r.Fly(60f);
            if (on) { sepOn = minSep; runsOn = p.Bh.Runs; Program.LedgerPublic(r, false); } else { sepOff = minSep; r.Close(); }
        }
        Sim.Note($"attack run with a 5 m abort distance on a 20 m target: off - the least separation {sepOff:F1} m (< 0: rammed); on - {sepOn:F1} m (the clearance at most half the abort: 2.5 m), {runsOn} runs");
        Sim.Check(sepOff < 0f && sepOn > 0f && runsOn >= 1, $"attack run: off it rams ({sepOff:F1} m), on it passes {sepOn:F1} m clear and keeps attacking ({runsOn} runs)");
    }

    static void HoverDrift()
    {
        var ship = Ships.Heli(3f); ship.Radius = 10f;
        var r = new Run("hover, a ship drifting in", ship);
        r.P.Terrain = SimTerrain.Flat();
        r.P.P = new Vector3D(0, 40, 0); r.P.Piloted = false;
        r.Fly(0.5f);
        var cfg = ApPanel.Defaults; cfg.AvoidOn = true;
        r.P.LimOverride = ApLimits.Of(in cfg);
        var s = r.P.SenseNow();
        Sim.Check(r.P.Fd.Engage(ApGoal.Hover | ApGoal.Position | ApGoal.Height, in s, out var why), $"{r.Name}: engaged ({why})");
        r.Fly(3f);
        var home = r.P.P;
        var drifter = SimTarget.Straight(new Vector3D(-150, 40, 3), new Vector3(5f, 0f, 0f)); r.Targets.Add(drifter);
        var obs = new ApObstacles(); r.P.Fd.Obstacles = obs;
        float minSep = float.MaxValue, maxOff = 0f;
        r.Each(rr => { obs.Clear(); obs.Add(drifter.P, drifter.V, 12f); minSep = MathF.Min(minSep, rr.Rel(drifter).Range - 12f - 10f); maxOff = MathF.Max(maxOff, (float)(rr.P.P - home).Length()); });
        r.Fly(60f);
        float backOff = (float)(r.P.P - home).Length();
        Sim.Note($"hover: a 12 m ship drifting through its place at 5 m/s - the least separation {minSep:F1} m, stepped up to {maxOff:F0} m aside, back within {backOff:F1} m once it passed");
        Sim.Check(minSep > 0f && backOff < 3f && !r.P.Crashed, $"hover: steps aside ({minSep:F1} m clear) and back to its place ({backOff:F1} m)");
        Program.LedgerPublic(r, false);
    }
}
