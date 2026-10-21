using AeroMod;

/// <summary>
/// CRUISE FROM THE GROUND, THE HEIGHT'S REFERENCE, AND FOLLOW SHIP LOW (in game 2026-10-20, 'Orbital Test Autopilot 3'): Cruise
/// engaged on a helicopter resting on the ground caught "ALT 0 m" and it never lifted off; over terrain lower than the planet's
/// generated shape the planet's height read 0 m over the ground (clipped) and the terrain minimum climbed it hard; the default 60 m
/// terrain minimum kept a formation follower out of a low leader's slot. Each through the Autopilot block's panel (the game's link)
/// or the pilot as the formation flies it, on the H25 helicopter, with the game's rays mocked (PlanetTerrainPack.BlindRays).
/// </summary>
sealed class CruiseGroundPack : IScenarioPack
{
    public void Register(ScenarioRegistry r)
    {
        r.Add("cruise from the ground: the panel catches a climb to the terrain minimum, flown", true, () => FromGround(sees: true));
        r.Add("cruise from the ground, no ground in sight: the ground caught under it, a climb off it", true, () => FromGround(sees: false));
        r.Add("cruise over terrain lower than the planet's shape: the rays' ground, no runaway climb", true, UnderShape);
        r.Add("follow ship low: the terrain minimum never above the leader's own height", true, FollowLow);
        r.Add("cruise engaged before the ground is seen: caught again when it is", true, LateGround);
    }

    /// <summary>No terrain at all (the rays blind, no planet).</summary>
    sealed class Blind : ITerrainQuery
    {
        public bool Below(Vector3D p, Vector3 down, float reach, out float height, out Vector3 normal) { height = 0f; normal = -down; return false; }
        public bool Cast(Vector3D p, Vector3 dir, float length, out float distance, out Vector3 normal) { distance = 0f; normal = -dir; return false; }
    }

    /// <summary>The sim's terrain as a planet whose generated shape stands 'Above' m over the real ground (a basin under it).</summary>
    sealed class RaisedPlanet : IPlanetHeight
    {
        public SimTerrain T; public double Above = 80;
        public bool Surface(Vector3D p, int lod, out Vector3D centre, out double r)
        {
            centre = new Vector3D(p.X, -PlanetTerrainPack.FlatPlanet.R, p.Z); r = PlanetTerrainPack.FlatPlanet.R + T.H(p.X, p.Z) + Above;
            return true;
        }
    }

    static Run OnGround(string name)
    {
        var r = new Run(name, Ships.Heli(3f));
        r.P.P = new Vector3D(0, r.P.S.Bottom, 0); r.P.V = Vector3.Zero;
        return r;
    }

    static void FromGround(bool sees)
    {
        string tag = sees ? "ground seen" : "no ground in sight";
        var r = OnGround("cruise from the ground (" + tag + ")");
        if (!sees) { r.P.Fd.Terrain = new Blind(); r.P.AglReach = -1f; }
        r.Fly(1f);
        double y0 = r.P.P.Y;
        var p = ApPanel.Defaults; p.Unattended = true;
        r.P.AddBlock(p);
        r.P.Panel.Mode = ApMode.FlightHold;
        r.Fly(2f);
        r.P.Panel.SpeedOn = true; r.P.Panel.SpeedMs = 8f;   // (after the engage: it catches the speed it has - 0 - as the panel's)
        float want = MathF.Max(p.MinAglM, ApPilot.TakeoffM);
        Sim.Check(r.P.Fd.Engaged && r.P.Panel.Mode == ApMode.FlightHold && MathF.Abs(r.P.Panel.AltM - want) < 0.5f,
            $"cruise from the ground ({tag}): engaged, caught ALT {r.P.Panel.AltM:F0} m (the terrain minimum {p.MinAglM:F0}, at least {ApPilot.TakeoffM:F0})");
        r.Measure();
        r.Fly(60f);
        float up = (float)(r.P.P.Y - y0);
        Sim.Note($"cruise from the ground ({tag}): after 60 s {up:F1} m up ({r.P.Agl:F1} m over the ground), {r.P.HorizontalV.Length():F1} m/s, highest {r.MaxAgl:F1} m");
        Sim.Check(!r.P.Crashed && MathF.Abs(up - want) < 6f && r.MaxAgl < want + 15f, $"cruise from the ground ({tag}): lifted off and held the height caught ({up:F1} m up of {want:F0}, highest {r.MaxAgl:F1})");
        Program.LedgerPublic(r, false);
    }

    static void UnderShape()
    {
        var r = new Run("cruise under the planet's shape", Ships.Heli(3f));
        var flat = SimTerrain.Flat();
        r.P.Terrain = flat;
        r.P.Fd.Terrain = new CombinedTerrain(new PlanetTerrainPack.BlindRays { T = flat, Reach = 150f }, new PlanetTerrainQuery(new RaisedPlanet { T = flat, Above = 80 }));
        r.P.AglReach = 150f;
        r.P.P = new Vector3D(0, 40 + r.P.S.Bottom, 0);
        var p = ApPanel.Defaults; p.Unattended = true;
        r.P.AddBlock(p);
        r.P.Panel.Mode = ApMode.HoverHold; r.Fly(8f);
        r.P.Panel.Mode = ApMode.FlightHold;
        r.Fly(2f);
        r.P.Panel.SpeedOn = true; r.P.Panel.SpeedMs = 8f;
        float caught = r.P.Panel.AltM;
        r.Measure();
        r.Fly(60f);
        Sim.Note($"cruise under the planet's shape (its surface 80 m over the real ground): caught ALT {caught:F0} m (from 40 m); after 60 s {r.P.Agl:F1} m over the ground, highest {r.MaxAgl:F1} m");
        Sim.Check(MathF.Abs(caught - p.MinAglM) < 2f, $"cruise under the planet's shape: the rays' ground - caught {caught:F0} m (the terrain minimum {p.MinAglM:F0}, from 40 m), not 0");
        Sim.Check(MathF.Abs(r.P.Agl - p.MinAglM) < 6f && r.MaxAgl < p.MinAglM + 15f, $"cruise under the planet's shape: no runaway climb ({r.P.Agl:F1} m, highest {r.MaxAgl:F1} m, the minimum {p.MinAglM:F0})");
        Program.LedgerPublic(r, false);
    }

    /// <summary>A helicopter leader on Cruise 25 m over the ground at 8 m/s, a follower 30 m behind it with this terrain minimum:
    /// the follower's lowest and highest height over the ground and its worst slot error in the last 30 s, the leader's height.</summary>
    static (float lo, float hi, float err, float lead) FollowLow(float minAgl, bool ledger)
    {
        var f = new Flight($"follow ship low (minimum {minAgl:F0} m)");
        var lead = f.Add("leader", Ships.Heli(3f, true, name: "H25 leader")); lead.P.P = new Vector3D(0, 25 + lead.P.S.Bottom, 0);
        var wing = f.Add("follower", Ships.Heli(3f, true, name: "H25 follower")); wing.P.P = new Vector3D(-60, 25 + wing.P.S.Bottom, 15);
        f.Fly(0.5f);
        var lc = ApPanel.Defaults; lc.HeightRef = ApHeightRef.Ground; lc.MinAglM = 15f;
        var lp = Flight.Pilot(lead, lc, ApMode.FlightHold);
        lp.Cfg.HeadingDeg = 90f; lp.Cfg.SpeedMs = 8f; lp.Cfg.AltM = 25f;
        var c = ApPanel.Defaults; c.Behaviour = ApBehaviour.Formation; c.FmRightM = 0f; c.FmBackM = 30f; c.FmUpM = 0f; c.MinAglM = minAgl;
        var wp = Flight.Pilot(wing, c, ApMode.Engage, Flight.Target(lead));
        f.Each(ff => { if (ff.Time > 45f) { ff.Max("err", wp.Formation.Err); ff.Max("hi", wing.P.Agl); ff.Min("lo", wing.P.Agl); } });
        f.Fly(75f);
        if (ledger) f.Ledger(); else foreach (var r in f.Ships) r.Close();
        return ((float)f.Metric("lo"), (float)f.Metric("hi"), (float)f.Metric("err"), lead.P.Agl);
    }

    static void FollowLow()
    {
        var with = FollowLow(ApPanel.Defaults.MinAglM, true);
        var none = FollowLow(0f, false);
        Sim.Note($"follow ship low: leader at {with.lead:F1} m; the follower with the default {ApPanel.Defaults.MinAglM:F0} m terrain minimum {with.lo:F1}..{with.hi:F1} m, slot error at most {with.err:F1} m (last 30 s); with none {none.lo:F1}..{none.hi:F1} m, {none.err:F1} m");
        Sim.Check(MathF.Abs(with.lead - 25f) < 4f, $"follow ship low: the leader holds 25 m ({with.lead:F1})");
        Sim.Check(with.hi < 40f && MathF.Abs(with.hi - none.hi) < 3f && MathF.Abs(with.err - none.err) < 3f,
            $"follow ship low: the default terrain minimum ({ApPanel.Defaults.MinAglM:F0} m) keeps the follower no higher than with none - {with.lo:F1}..{with.hi:F1} m vs {none.lo:F1}..{none.hi:F1} m, slot error {with.err:F1} vs {none.err:F1} m");
    }

    /// <summary>In game 2026-10-21 (f1): Cruise engaged at 33 m over the ground, drifting at 1 m/s, the ground ray and the planet not
    /// answering at that tick - "ALT 0 m" caught. Here every ground source is blind for the first second after the engage.</summary>
    static void LateGround()
    {
        var r = new Run("cruise before the ground is seen", Ships.Heli(3f));
        var blind = new Blind(); var rays = new PlanetTerrainPack.BlindRays { T = r.P.Terrain, Reach = 400f };
        r.P.Fd.Terrain = blind; r.P.AglReach = -1f;
        r.P.P = new Vector3D(0, 33 + r.P.S.Bottom, 0); r.P.V = new Vector3(1f, 0f, 0f);
        var p = ApPanel.Defaults; p.Unattended = true; p.HeightRef = ApHeightRef.Ground;
        r.P.AddBlock(p);
        r.P.Panel.Mode = ApMode.FlightHold;
        r.Fly(1f / 60f);
        float first = r.P.Panel.AltM;
        r.Fly(1f);
        r.P.Fd.Terrain = rays; r.P.AglReach = 400f;   // (the ground seen)
        r.Fly(2f);
        float caught = r.P.Panel.AltM;
        r.Fly(40f);
        Sim.Note($"cruise before the ground is seen: caught {first:F0} m blind, {caught:F0} m once the ground was seen (33 m over it, minimum {p.MinAglM:F0}); after 40 s {r.P.Agl:F1} m");
        Sim.Check(MathF.Abs(caught - p.MinAglM) < 1f && MathF.Abs(r.P.Agl - p.MinAglM) < 6f, $"cruise before the ground is seen: the height caught again over the ground ({caught:F0} m, flown {r.P.Agl:F1} m; the minimum {p.MinAglM:F0})");
        Program.LedgerPublic(r, false);
    }
}
