using AeroMod;

/// <summary>
/// TERRAIN AWARENESS and the EXTERNAL GOAL SOURCE (a scenario pack: registered through IScenarioPack, as any outside pack is).
/// The autopilot sees the terrain only through its ITerrainQuery (the ground below, the look ahead along its path).
/// </summary>
sealed class TerrainPack : IScenarioPack
{
    public void Register(ScenarioRegistry r)
    {
        r.Add("terrain: height hold over hills, over the ground (AGL)", true, () => HillsAgl(ApHeightRef.Ground));
        r.Add("terrain: height hold over hills, along the gravity (the floor protects)", false, () => HillsAgl(ApHeightRef.Inertial));
        r.Add("terrain: hover over a 30 % slope, then flown up it", true, SlopeHover);
        r.Add("terrain: a canyon flown along at 10 m AGL", false, Canyon);
        r.Add("terrain: a cliff ahead at 150 m/s (Plane2) - pulled up in time, within the limits", true, Cliff);
        r.Add("terrain: the ground ray's reach - no ground found, the height held along the gravity", true, NoGround);
        r.Add("follow: hover holds a slot on a moving reference (external goal source)", true, Follow);
    }

    static Run Hover(string name, SimShip ship, SimTerrain terrain, double agl, bool piloted)
    {
        var r = new Run(name, ship);
        r.P.Terrain = terrain;
        r.P.P = new Vector3D(0, terrain.H(0, 0) + ship.Bottom + agl, 0); r.P.Piloted = piloted;
        r.Fly(0.5f);
        var s = r.P.SenseNow();
        Sim.Check(r.P.Fd.Engage(ApGoal.Hover | ApGoal.Height, in s, out var why), $"{name}: engaged ({why})");
        r.Fly(3f);
        return r;
    }

    /// <summary>Flown forward at the keys' 10 m/s for 60 s over 60 m hills, 30 m up: over the ground the height follows the hills;
    /// along the gravity it holds its height and the floor / look ahead keep it off the crests.</summary>
    static void HillsAgl(ApHeightRef href)
    {
        bool keep = Autopilot.HoldAboveGround; Autopilot.HoldAboveGround = href == ApHeightRef.Ground;
        try
        {
            var r = Hover($"hills {href}", Ships.Heli(3f), SimTerrain.Hills(60, 400), 30, piloted: true);
            r.Measure();
            r.P.PilotKeys = new Vector3(0.7071f, 0f, -0.7071f) * 1f;   // (forward-right: the hills' crests met across both ridge sets)
            r.Each(rr => { rr.Min("agl", rr.P.Agl); rr.Max("agl", rr.P.Agl); });
            r.Fly(60f);
            double lo = r.Metrics["agl"], hi = r.Metrics.TryGetValue("agl", out var _) ? r.Metrics["agl"] : 0;
            r.Metrics.Remove("agl");
            float aMin = float.MaxValue, aMax = 0f;
            var r2 = r; // (min / max kept separately below)
            Sim.Note($"{r.Name}: 60 s over 60 m hills at 10 m/s - prot {r.ProtSeen}, lowest {r.MinAgl:F1} m over the ground, highest {r.MaxAgl:F1} m, travelled {r.HorizErr:F0} m");
            if (href == ApHeightRef.Ground)
                Sim.Check(!r.P.Crashed && r.MinAgl > 18f && r.MaxAgl < 45f && r.P.Fd.Engaged, $"{r.Name}: the height over the ground held within {r.MinAgl:F1}..{r.MaxAgl:F1} m (30 asked; 18..45) over the hills, no crash");
            else
                Sim.Check(!r.P.Crashed && r.MinAgl > Autopilot.FloorM - 4f && r.P.Fd.Engaged, $"{r.Name}: along the gravity the crests come up under it - the floor and the look ahead kept it {r.MinAgl:F1} m off them (floor {Autopilot.FloorM}), no crash ({r.ProtSeen})");
            Program.LedgerPublic(r, false);
        }
        finally { Autopilot.HoldAboveGround = keep; }
    }

    /// <summary>Hovering 12 m over a 30 % slope; then flown up it at the keys' speed for 20 s.</summary>
    static void SlopeHover()
    {
        var r = Hover("slope hover", Ships.Heli(3f), SimTerrain.Slope(0.3), 12, piloted: true);
        r.Measure();
        r.Fly(20f);
        float holdErr = r.MaxPosErr, aglMin0 = r.MinAgl;
        r.P.PilotKeys = new Vector3(1f, 0f, 0f);   // (local +X: up the slope - it rises toward world +X)
        r.Fly(20f);
        Sim.Note($"{r.Name}: held over the slope within {holdErr:F2} m (AGL at least {aglMin0:F1} m), then flown up it: lowest {r.MinAgl:F1} m over the ground, prot {r.ProtSeen}");
        Sim.Check(!r.P.Crashed && holdErr < 0.6f && r.MinAgl > 8f && r.P.Fd.Engaged, $"{r.Name}: holds over the slope ({holdErr:F2} m) and climbs with it when flown up (at least {r.MinAgl:F1} m over the ground), no crash");
        Program.LedgerPublic(r, false);
    }

    /// <summary>Along a 40 m deep canyon at 10 m over its floor: the height over the ground held, never into the walls' foot.</summary>
    static void Canyon()
    {
        var r = Hover("canyon", Ships.Heli(3f), SimTerrain.Canyon(100, 40, 120), 10, piloted: true);
        r.Measure();
        r.P.PilotKeys = new Vector3(1f, 0f, 0f);
        r.Fly(30f);
        Sim.Note($"{r.Name}: 30 s along the canyon at 10 m/s - lowest {r.MinAgl:F1} m, highest {r.MaxAgl:F1} m over the ground");
        Sim.Check(!r.P.Crashed && r.MinAgl > 6f && r.MaxAgl < 16f, $"{r.Name}: along its floor within {r.MinAgl:F1}..{r.MaxAgl:F1} m (10 asked)");
        Program.LedgerPublic(r, false);
    }

    /// <summary>Plane2 level at 150 m/s, 100 m over flat ground, a 150 m cliff 3 km ahead: protection pulls up in time (the
    /// pull-up clipped by the stall and g limits); without it, the ship flies into the cliff.</summary>
    static void Cliff()
    {
        float aoaMax = 0f, nMax = 0f, clearance = float.MaxValue, peak = 0f; bool crashOn = false, crashOff = false; ApProt seen = ApProt.None;
        foreach (bool prot in new[] { true, false })
        {
            var s = Ships.Plane("Plane2", tw: 0.6f, gyro: 3e5f, surfPerQ: new Vector3(800f, 800f, 1600f));
            if (s == null) { Sim.NoPolar("Plane2"); return; }
            Autopilot.Protection = prot;
            try
            {
                var air = (PolarAir)s.Air;
                var r = new Run($"cliff protection {(prot ? "on" : "off")}", s);
                r.P.Terrain = SimTerrain.Cliff(-3000, 150, 30, toward: -1);   // (the plane flies along its nose: -X; the cliff rises that way)
                // (trimmed level at 150 m/s, as Program.Plane does - here with the terrain set first)
                r.P.Damp = false; r.P.Rho = 1.225f; r.P.P = new Vector3D(0, 100 + s.Bottom, 0);
                float weight = s.Mass * s.PlanetG * s.Gm, trim = 0f;
                for (float a = 0f; a < 20f * ApMath.Deg; a += 0.05f * ApMath.Deg)
                {
                    var q = Quaternion.CreateFromAxisAngle(air.Right, a);
                    air.Forces(Vector3.Transform(air.Nose * 150f, Quaternion.Inverse(q)), Vector3.Zero, 1.225f, out var f, out _);
                    trim = a; if (Vector3.Transform(f, q).Y >= weight) break;
                }
                r.P.Q = Quaternion.CreateFromAxisAngle(air.Right, trim); r.P.V = air.Nose * 150f;
                r.P.Piloted = true; r.P.PilotKeys = air.Nose * 0.5f;
                var sn = r.P.SenseNow(); Sim.Check(r.P.Fd.Engage(ApGoal.Attitude, in sn, out _), $"{r.Name}: attitude hold engaged");
                if (prot) peak = Program.PeakAlphaPublic(s, 150f);
                r.Each(rr =>
                {
                    rr.Max("aoa", Program.AoaPublic(rr)); rr.Max("n", rr.P.LoadFactor);
                    // (clearance: the height over the cliff's top once over it, else over the ground)
                    rr.Min("clear", rr.P.Agl);
                    if (Environment.GetEnvironmentVariable("AP_TRACE") == "cliff" && rr.Ticks % 15 == 0) Console.WriteLine($"      t {rr.P.Time:F2} x {rr.P.P.X:F0} agl {rr.P.Agl:F0} v {rr.P.V.Length():F0} aoa {Program.AoaPublic(rr):F1} n {rr.P.LoadFactor:F2} tti {rr.P.Fd.Ground.Tti:F1} prot {rr.P.Cmd.Prot} margin {rr.P.LastStallMargin:F1} pull {rr.P.Fd.Guard.LastPull / ApMath.Deg:F1} cap {rr.P.Fd.Guard.LastPullCap / ApMath.Deg:F1} loac {rr.P.Fd.Guard.LoacShare:F2}");
                });
                r.Fly(40f);
                if (prot) { aoaMax = (float)r.Metrics["aoa"]; nMax = (float)r.Metrics["n"]; clearance = (float)r.Metrics["clear"]; crashOn = r.P.Crashed; seen = r.ProtSeen; Program.LedgerPublic(r, true); }
                else { crashOff = r.P.Crashed; r.Close(); }
            }
            finally { Autopilot.Protection = true; }
        }
        float cap = peak - Autopilot.AoaMarginDeg;
        Sim.Note($"cliff: protection on - {(crashOn ? "CRASHED" : "cleared")}, lowest {clearance:F0} m over the ground, AoA at most {aoaMax:F1} deg (stall cap {cap:F1}), at most {nMax:F2} g (limit {Autopilot.GLimit}), {seen}; off - {(crashOff ? "flew into the cliff" : "cleared")}");
        Sim.Check(!crashOn && clearance > 5f && (seen & ApProt.Terrain) != 0 && aoaMax <= cap + 1.5f && nMax <= Autopilot.GLimit * 1.15f && crashOff,
            $"cliff: pulled up in time ({clearance:F0} m clear), within the stall ({aoaMax:F1} <= {cap + 1.5f:F1} deg) and g limits ({nMax:F2} g); unprotected it hit the cliff");
    }

    /// <summary>The ground ray finds nothing (the ship 2.5 km up, past its 2 km reach): the height over the ground falls back to the
    /// height along the gravity - held all the same.</summary>
    static void NoGround()
    {
        var r = Hover("no ground found", Ships.Heli(3f), SimTerrain.Flat(), 2500, piloted: false);
        r.Measure(); r.Fly(30f);
        Sim.Note($"{r.Name}: 2.5 km up, no ground in reach - the height held within {r.MaxHeightErr:F2} m, ground seen {r.P.Fd.Ground.HasGround}");
        Sim.Check(!r.P.Fd.Ground.HasGround && r.MaxHeightErr < 0.5f && r.P.Fd.Engaged, $"{r.Name}: no ground found, the height held along the gravity within {r.MaxHeightErr:F2} m");
        Program.LedgerPublic(r, false);
    }

    /// <summary>A reference moving at 5 m/s, then turning: the hover holds a slot 10 m behind and 5 m above it (the external
    /// goal source's Track) - its position and velocity fed each tick.</summary>
    static void Follow()
    {
        var r = new Run("follow a moving reference", Ships.Heli(3f));
        r.P.P = new Vector3D(0, 60, 0);
        var lead = SimTarget.Straight(new Vector3D(10, 55, 0), new Vector3(5f, 0f, 0f));
        r.Targets.Add(lead);
        r.Fly(0.5f);
        var src = new FollowSource { Ref = lead, Offset = new Vector3(-10f, 5f, 0f) };
        var s = r.P.SenseNow();
        Sim.Check(r.P.Fd.Engage(ApGoal.Hover | ApGoal.Height | ApGoal.External, in s, out var why, src), $"follow: engaged ({why})");
        if (Environment.GetEnvironmentVariable("AP_TRACE") == "follow") r.Each(rr => { if (rr.Ticks % 60 == 0) Console.WriteLine($"      t {rr.P.Time:F1} goals {rr.P.Fd.Goals} x {rr.P.P.X:F1} slot {lead.P.X + src.Offset.X:F1} vcmd {rr.P.Fd.VelCmd.X:F2} v {rr.P.V.X:F2}"); });
        r.Fly(20f);
        float err = 0f;
        r.Each(rr => err = MathF.Max(err, (float)(rr.P.P - (lead.P + (Vector3D)src.Offset)).Length()));
        r.Fly(20f);
        float straight = err; err = 0f;
        lead.Script = (t, sl) => Vector3.Cross(Vector3.UnitY * 0.05f, sl.V);   // (a level turn, 0.05 rad/s)
        r.Fly(10f); err = 0f; r.Fly(30f);
        var rel = r.Rel(lead);
        Sim.Note($"follow: the slot held within {straight:F2} m behind a 5 m/s reference, within {err:F2} m in its 0.05 rad/s turn (range now {rel.Range:F1} m, closing {rel.Closing:F2} m/s)");
        Sim.Check(r.P.Fd.Engaged && straight < 1.5f && err < 4f, $"follow: the slot held - {straight:F2} m straight (< 1.5), {err:F2} m in the turn (< 4)");
        Program.LedgerPublic(r, false);
    }
}
