using AeroMod;

/// <summary>
/// THE PLANET'S HEIGHT AS TERRAIN (Autopilot/ApTerrain.cs: PlanetTerrainQuery over an IPlanetHeight, CombinedTerrain with the
/// rays; in game the height is the planet's own query - Game/PlanetHeight.cs). The query mocked: a sphere planet (60 km, a bump),
/// and the sim's flat-world terrains as a planet whose centre is straight below. The rays mocked as the game's: blind past the
/// colliders the game builds near the ship (BlindRays: 400 m). The query's height, normal and cast; TerrainWatch seeing a cliff
/// 3 km ahead at 300 m/s; a plane navigating at 150 m over the ground toward a 400 m cliff with the rays alone and with the planet.
/// </summary>
sealed class PlanetTerrainPack : IScenarioPack
{
    public void Register(ScenarioRegistry r)
    {
        r.Add("planet height: the query on a mocked sphere planet - height, normal, a cast to a bump 3 km off", true, Query);
        r.Add("planet height: TerrainWatch sees a 300 m cliff 3 km ahead at 300 m/s (rays alone: 400 m)", true, Watch);
        r.Add("planet height: Plane2 navigating at 150 m AGL into a 400 m cliff - rays alone vs + the planet", true, PlaneCliff);
    }

    /// <summary>A sphere planet: radius R about C, with a round bump 'bumpH' high, 'bumpW' wide, centred under 'bumpAt' (a direction).</summary>
    sealed class Sphere : IPlanetHeight
    {
        public Vector3D C = Vector3D.Zero; public double R = 60000, BumpH, BumpW = 300; public Vector3D BumpDir = new(1, 0, 0); public int Calls;
        public bool Surface(Vector3D p, int lod, out Vector3D centre, out double r)
        {
            Calls++;
            centre = C; var d = Vector3D.Normalize(p - C);
            double arc = Math.Acos(Math.Clamp(Vector3D.Dot(d, BumpDir), -1, 1)) * R;
            r = R + BumpH * Math.Max(0, 1 - arc / BumpW);
            return true;
        }
    }

    /// <summary>The sim's flat-world terrain as a planet: its centre straight below, 60 km down (the radial is the vertical).</summary>
    public sealed class FlatPlanet : IPlanetHeight
    {
        public SimTerrain T; public const double R = 60000; public int Calls; public readonly int[] Lods = new int[9];
        public bool Surface(Vector3D p, int lod, out Vector3D centre, out double r)
        {
            Calls++; Lods[Math.Clamp(lod, 0, 8)]++;
            centre = new Vector3D(p.X, -R, p.Z); r = R + T.H(p.X, p.Z);
            return true;
        }
    }

    /// <summary>The game's rays: the terrain only within 'reach' of the ship (the voxel colliders are built near entities).</summary>
    public sealed class BlindRays : ITerrainQuery
    {
        public SimTerrain T; public float Reach = 400f;
        public bool Below(Vector3D p, Vector3 down, float reach, out float height, out Vector3 normal) => T.Below(p, down, MathF.Min(reach, Reach), out height, out normal);
        public bool Cast(Vector3D p, Vector3 dir, float length, out float distance, out Vector3 normal) => T.Cast(p, dir, MathF.Min(length, Reach), out distance, out normal);
    }

    static void Query()
    {
        var sp = new Sphere { BumpH = 300, BumpW = 400, BumpDir = Vector3D.Normalize(new Vector3D(Math.Cos(0.05), Math.Sin(0.05), 0)) };
        var q = new PlanetTerrainQuery(sp);
        // 150 m over the sphere, away from the bump
        var at = new Vector3D(0, 60150, 0);
        Sim.Check(q.Below(at, -Vector3.UnitY, 2000f, out float h, out var n) && MathF.Abs(h - 150f) < 0.5f && Vector3.Dot(n, Vector3.UnitY) > 0.999f,
                  $"planet query: 150 m over a smooth sphere - height {h:F2} m, the normal up ({Vector3.Dot(n, Vector3.UnitY):F4})");
        Sim.Check(!q.Below(at, -Vector3.UnitY, 100f, out _, out _), "planet query: beyond the reach asked - nothing");
        // a level ray along the surface toward the bump 3 km off (at 0.05 rad = 3000 m of arc): over a curving planet a level ray rises
        var p0 = new Vector3D(60000 * Math.Cos(0.0), 60000 * Math.Sin(0.0), 0) * (60100.0 / 60000.0);   // (100 m up at the arc's start)
        var tangent = new Vector3(0, 1, 0);
        bool hit = q.Cast(p0, tangent, 6000f, out float dist, out var hn);
        double arcHit = Math.Atan2((p0 + (Vector3D)tangent * dist).Y, (p0 + (Vector3D)tangent * dist).X) * 60000;
        Sim.Note($"planet query: a level ray 100 m over a 60 km sphere toward a 300 m bump 3 km off - hit {hit} at {dist:F0} m (the bump's slope at {arcHit:F0} m of arc), {sp.Calls} samples");
        Sim.Check(hit && dist > 2500f && dist < 3100f && Vector3.Dot(hn, -tangent) > 0.2f, $"planet query: the cast finds the bump at {dist:F0} m (2500..3100), its face toward the ray");
        // the flat-world mock agrees with the sim terrain exactly
        var cliff = SimTerrain.Cliff(-3000, 300, 30, toward: -1);
        var fq = new PlanetTerrainQuery(new FlatPlanet { T = cliff });
        bool fh = fq.Cast(new Vector3D(0, 100, 0), -Vector3.UnitX, 6000f, out float fd, out _);
        bool sh = cliff.Cast(new Vector3D(0, 100, 0), -Vector3.UnitX, 6000f, out float sd, out _);
        Sim.Check(fh && sh && MathF.Abs(fd - sd) < 10f, $"planet query on the flat mock: the cliff at {fd:F0} m (the sim's own cast {sd:F0} m)");
        Sim.Check(PlanetTerrainQuery.LodFor(100) == 0 && PlanetTerrainQuery.LodFor(800) == 2 && PlanetTerrainQuery.LodFor(5000) == 4, "lod: finest near, coarser far, at most 4");
    }

    static void Watch()
    {
        var cliff = SimTerrain.Cliff(-3000, 300, 30, toward: -1);
        float First(ITerrainQuery t)
        {
            var w = new TerrainWatch();
            var p = new Vector3D(0, 150, 0); var v = new Vector3(-300f, 0f, 0f);
            for (int k = 0; k < 60 * 12; k++)
            {
                w.Step(t, p, v, Vector3.Zero, -Vector3.UnitY, 2f, 1f / 60f);
                if (float.IsFinite(w.Tti)) return (float)(-3000 - p.X) * -1f;
                p += (Vector3D)(v / 60f);
            }
            return 0f;
        }
        float rays = First(new BlindRays { T = cliff });
        var fp = new FlatPlanet { T = cliff };
        var comb = new CombinedTerrain(new BlindRays { T = cliff }, new PlanetTerrainQuery(fp));
        float both = First(comb);
        Sim.Note($"planet height: a 300 m cliff ahead at 300 m/s - the rays alone see it {rays:F0} m off ({rays / 300f:F1} s), with the planet {both:F0} m off ({both / 300f:F1} s); {fp.Calls} height samples (lod 0..4: {string.Join("/", fp.Lods.Take(5))})");
        Sim.Check(rays < 450f && both > 2500f, $"TerrainWatch: the planet's height sees the cliff at {both:F0} m (> 2500; the rays alone: {rays:F0} m)");
    }

    static void PlaneCliff()
    {
        float clearRays = 0, clearBoth = 0, nRays = 0, nBoth = 0, climbAt = 0; bool crashRays = false, crashBoth = false;
        foreach (bool planet in new[] { false, true })
        {
            var r = FlightPack.PlaneAt($"cliff route Plane2 ({(planet ? "rays + planet" : "rays alone")})", "Plane2", 150f, y: 150);
            if (r == null) { Sim.NoPolar("Plane2"); return; }
            var cliff = SimTerrain.Cliff(-3500, 400, 40, toward: -1);
            r.P.Terrain = cliff;
            var rays = new BlindRays { T = cliff };
            r.P.Fd.Terrain = planet ? new CombinedTerrain(rays, new PlanetTerrainQuery(new FlatPlanet { T = cliff })) : rays;
            var cfg = ApPanel.Defaults; cfg.AltM = 150f; cfg.SpeedMs = 150f; cfg.HeightRef = ApHeightRef.Ground; cfg.MinAglM = 100f;
            FlightPack.Engage(r, cfg, ApMode.Navigate, route: FlightPack.Pts((-12000, 0, 0)));
            r.Measure();
            float startClimb = float.NaN; double y0 = r.P.P.Y;
            r.Each(rr => { rr.Max("n", rr.P.LoadFactor); if (float.IsNaN(startClimb) && rr.P.P.Y > y0 + 20) startClimb = (float)(-3500 - rr.P.P.X) * -1f; });
            r.Fly(50f);
            float n = (float)r.Metrics["n"];
            if (planet) { clearBoth = r.MinAgl; nBoth = n; crashBoth = r.P.Crashed; climbAt = startClimb; Program.LedgerPublic(r, false); }
            else { clearRays = r.MinAgl; nRays = n; crashRays = r.P.Crashed; r.Close(); }   // (the rays alone may fly into it: shown, not failed)
        }
        Sim.Note($"cliff route (400 m cliff, 150 m over the ground asked, 100 m minimum): rays alone - {(crashRays ? "CRASHED" : $"lowest {clearRays:F0} m")}, at most {nRays:F2} g; rays + planet - {(crashBoth ? "CRASHED" : $"lowest {clearBoth:F0} m")}, at most {nBoth:F2} g, climbing from {climbAt:F0} m before the cliff");
        Sim.Check(!crashBoth && clearBoth > 90f && (crashRays || clearBoth >= clearRays - 5f), $"cliff route: with the planet's height the minimum kept ({clearBoth:F0} m > 90), no worse than the rays alone ({(crashRays ? "crashed" : clearRays.ToString("F0") + " m")})");
    }
}
