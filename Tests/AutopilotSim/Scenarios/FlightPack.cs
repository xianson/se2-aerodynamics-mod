using AeroMod;

/// <summary>
/// THE FLIGHT PILOT's in-air modes on the dumped planes (their AeroBench polars) and the hovering styles: heading hold (overshoot,
/// the height and speed through the turn), altitude and speed steps (TECS: the coupling), a GPS route in a crosswind (cross-track),
/// a loiter (its radius), the helicopter's and six-axis ship's holds and routes, terrain under a route, style detection, refusals.
/// </summary>
sealed class FlightPack : IScenarioPack
{
    public void Register(ScenarioRegistry r)
    {
        r.Add("flight: heading hold 90 deg turn (Plane2) - overshoot, height and speed held", true, () => Heading("Plane2", 150f, true));
        r.Add("flight: heading hold on the Tu-134", false, () => Heading("tu134_game", 160f, false));
        r.Add("flight: heading hold on the jetliner", false, () => Heading("jetliner", 170f, false));
        r.Add("flight: altitude step +300 m, speed held (Plane2) - TECS coupling", true, () => AltStep("Plane2", 150f, true));
        r.Add("flight: altitude step on the Tu-134", false, () => AltStep("tu134_game", 160f, false));
        r.Add("flight: speed step +30 m/s, height held (Plane2) - TECS coupling", true, () => SpeedStep("Plane2"));
        r.Add("flight: GPS route in a 15 m/s crosswind (Plane2) - cross-track", true, () => Route("Plane2", 15f));
        r.Add("flight: loiter at the route's end (Plane2) - radius", true, () => Loiter("Plane2"));
        r.Add("flight: helicopter flight hold - heading and 15 m/s, height held", true, HeliHold);
        r.Add("flight: helicopter GPS route (tilt movement)", true, HeliRoute);
        r.Add("flight: six-axis GPS route and hold", true, SixRoute);
        r.Add("flight: route over hills keeps the terrain minimum (Plane2)", true, HillsRoute);
        r.Add("flight: style detection - planes, helicopters, VTOL, six-axis", true, Styles);
        r.Add("flight: refusals with reasons (slow plane, no waypoints, six-axis without thrust across)", true, Refusals);
    }

    // ── helpers ──

    /// <summary>A plane trimmed level at 'speed', 3000 m up, unattended, dampeners off, its stall sweep in (1.5 s of attitude hold).</summary>
    internal static Run PlaneAt(string name, string polar, float speed, float tw = 0.6f, double y = 3000)
    {
        var s = Ships.Plane(polar, tw: tw, gyro: 3e5f, surfPerQ: new Vector3(1200f, 1200f, 1600f));
        if (s == null) return null;
        var air = (PolarAir)s.Air;
        var r = new Run(name, s);
        r.P.Damp = false; r.P.Rho = 1.225f; r.P.P = new Vector3D(0, y, 0);
        float weight = s.Mass * s.PlanetG * s.Gm, trim = 0f;
        for (float a = 0f; a < 20f * ApMath.Deg; a += 0.05f * ApMath.Deg)
        {
            var q = Quaternion.CreateFromAxisAngle(air.Right, a);
            air.Forces(Vector3.Transform(air.Nose * speed, Quaternion.Inverse(q)), Vector3.Zero, 1.225f, out var f, out _);
            trim = a; if (Vector3.Transform(f, q).Y >= weight) break;
        }
        r.P.Q = Quaternion.CreateFromAxisAngle(air.Right, trim); r.P.V = air.Nose * speed;
        r.P.PilotKeys = air.Nose * 0.5f; r.P.Piloted = false; r.P.Move = air.Nose * 0.5f;
        var sn = r.P.SenseNow();
        r.P.Fd.Engage(ApGoal.Attitude, in sn, out _);
        r.Fly(1.5f);
        return r;
    }

    /// <summary>The pilot engaged in a mode with this panel (checked); the attitude hold before it let go.</summary>
    internal static ApPilot Engage(Run r, ApPanel cfg, ApMode mode, IApTargetSource src = null, List<ApRoute.Point> route = null)
    {
        var p = new ApPilot { Cfg = cfg, Fd = r.P.Fd, Targets = src };
        if (route != null) p.SetRoute(route);
        var s = r.P.SenseNow();
        bool ok = p.Begin(mode, in s, out var why);
        Sim.Check(ok, $"{r.Name}: the pilot began {mode} ({why}, style {p.Style})");
        if (r.P.Fd.Engaged) r.P.Fd.Disengage(ApReason.None);
        bool ok2 = ok && r.P.Fd.Engage(ApPilot.GoalsFor(p.Style), in s, out var why2, p);
        Sim.Check(ok2, $"{r.Name}: the flight director took it ({r.P.Fd.Reason})");
        return p;
    }

    internal static List<ApRoute.Point> Pts(params (double x, double y, double z)[] ps)
    {
        var l = new List<ApRoute.Point>();
        int i = 1; foreach (var p in ps) l.Add(new ApRoute.Point { Name = $"WP{i++}", Pos = new Vector3D(p.x, p.y, p.z), Found = true });
        return l;
    }

    static float Hdg(Run r) => ApCompass.Heading(new Vector3(r.P.V.X, 0f, r.P.V.Z), Vector3.UnitY);

    // ── scenarios ──

    static void Heading(string polar, float speed, bool fast)
    {
        var r = PlaneAt($"heading {polar}", polar, speed);
        if (r == null) { Sim.NoPolar(polar); return; }
        var cfg = ApPanel.Defaults;
        var p = Engage(r, cfg, ApMode.FlightHold);
        r.Fly(5f);
        float h0 = p.HeightNow, v0 = p.SpeedNow, start = Hdg(r), want = (start + 90f) % 360f;
        p.Cfg.HeadingDeg = want;
        float over = 0f, dh = 0f, dv = 0f, settle = -1f, t0 = r.P.Time;
        r.Each(rr =>
        {
            float e = ApCompass.Diff(Hdg(rr), want);   // (+: past it, turning right)
            over = MathF.Max(over, e);
            dh = MathF.Max(dh, MathF.Abs(p.HeightNow - h0)); dv = MathF.Max(dv, MathF.Abs(p.SpeedNow - v0));
            if (MathF.Abs(e) < 2f) { if (settle < 0f) settle = rr.P.Time - t0; } else settle = -1f;
        });
        r.Fly(70f);
        Sim.Note($"{r.Name}: 90 deg right at {speed:F0} m/s - overshoot {over:F1} deg, within 2 deg after {settle:F0} s, height within {dh:F1} m, speed within {dv:F1} m/s, prot {r.ProtSeen}");
        Sim.Check(over < 5f && settle > 0f && settle < 60f && dh < 40f && dv < 12f, $"{r.Name}: heading hold - overshoot {over:F1} deg (< 5), settled {settle:F0} s (< 60), height {dh:F1} m (< 40), speed {dv:F1} m/s (< 12)");
        Program.LedgerPublic(r, false);
    }

    static void AltStep(string polar, float speed, bool fast)
    {
        var r = PlaneAt($"alt step {polar}", polar, speed);
        if (r == null) { Sim.NoPolar(polar); return; }
        var p = Engage(r, ApPanel.Defaults, ApMode.FlightHold);
        r.Fly(5f);
        float h0 = p.HeightNow, v0 = p.SpeedNow;
        p.Cfg.AltM = p.Cfg.AltM + 300f;
        float want = p.Cfg.AltM, dv = 0f, over = 0f, t0 = r.P.Time, reach = -1f;
        r.Each(rr => { dv = MathF.Max(dv, MathF.Abs(p.SpeedNow - v0)); over = MathF.Max(over, p.HeightNow - want); if (reach < 0f && p.HeightNow > want - 10f) reach = rr.P.Time - t0; });
        r.Fly(80f);
        float err = MathF.Abs(p.HeightNow - want);
        Sim.Note($"{r.Name}: +300 m at VS {p.Cfg.VsMs} m/s - within 10 m after {reach:F0} s, overshoot {over:F1} m, final error {err:F1} m; speed held within {dv:F1} m/s ({v0:F0}), thrust-limited {p.Tecs.ThrustLimited}");
        Sim.Check(reach > 0f && over < 25f && err < 8f && dv < 15f, $"{r.Name}: altitude step - reached in {reach:F0} s, overshoot {over:F1} m (< 25), error {err:F1} m (< 8), speed within {dv:F1} m/s (< 15)");
        Program.LedgerPublic(r, false);
    }

    static void SpeedStep(string polar)
    {
        var r = PlaneAt($"speed step {polar}", polar, 140f, tw: 0.8f);
        if (r == null) { Sim.NoPolar(polar); return; }
        var p = Engage(r, ApPanel.Defaults, ApMode.FlightHold);
        r.Fly(5f);
        float h0 = p.HeightNow;
        p.Cfg.SpeedMs = p.Cfg.SpeedMs + 30f;
        float want = p.Cfg.SpeedMs, dh = 0f;
        r.Each(rr => dh = MathF.Max(dh, MathF.Abs(p.HeightNow - h0)));
        r.Fly(60f);
        float err = MathF.Abs(p.SpeedNow - want);
        Sim.Note($"{r.Name}: +30 m/s - speed error after 60 s {err:F1} m/s, height held within {dh:F1} m");
        Sim.Check(err < 4f && dh < 30f, $"{r.Name}: speed step - {err:F1} m/s off (< 4), height within {dh:F1} m (< 30)");
        Program.LedgerPublic(r, false);
    }

    static void Route(string polar, float wind)
    {
        var r = PlaneAt($"route {polar} wind {wind}", polar, 150f);
        if (r == null) { Sim.NoPolar(polar); return; }
        if (Environment.GetEnvironmentVariable("AP_WIND") is string ws) wind = float.Parse(ws);
        r.Air.Steady = new Vector3(0f, 0f, wind);
        var cfg = ApPanel.Defaults; cfg.AltM = 3000f; cfg.SpeedMs = 150f; cfg.HeightRef = ApHeightRef.Inertial;
        var pts = Pts((-4000, 3000, 0), (-8000, 3000, -4000), (-4000, 3000, -8000));
        var p = Engage(r, cfg, ApMode.Navigate, route: pts);
        float xt = 0f; int wpSeen = 0;
        r.Each(rr => { if (p.Wp >= 1 && !p.Loitering && rr.P.Time > 0f) { if (p.Wp != wpSeen) { wpSeen = p.Wp; } } });
        // (the cross-track measured on the second leg, once captured: from 1.5 km after its start)
        r.Fly(2f);
        // (the second leg: the capture's overshoot - past the line after first reaching it - and the steady cross-track over the
        //  leg's last 40 %)
        float worst = 0f, over = 0f; bool capturing = true, crossed = false; float legStart = 0f, x0 = float.NaN, legLen = 5657f;
        r.Each(rr =>
        {
            if (p.Wp == 2 && !p.Loitering)
            {
                if (legStart == 0f) { legStart = rr.P.Time; x0 = p.CrossTrack; }
                if (!crossed && MathF.Sign(p.CrossTrack) != MathF.Sign(x0)) crossed = true;
                if (crossed) over = MathF.Max(over, MathF.Abs(p.CrossTrack));
                if (rr.P.Time - legStart > 3f && p.WpDist < 0.4f * legLen) { capturing = false; worst = MathF.Max(worst, MathF.Abs(p.CrossTrack)); }

                if (Environment.GetEnvironmentVariable("AP_TRACE") == "route" && rr.Ticks % 120 == 0) Console.WriteLine($"      t {rr.P.Time - legStart:F0} xt {p.CrossTrack:F0} wp {p.Wp} hdg {Hdg(rr):F0} dist {p.WpDist:F0} v {rr.P.V.Length():F0} wind {rr.P.Wind}");

            }
        });
        r.Fly(150f);
        Sim.Note($"{r.Name}: three waypoints in a {wind} m/s crosswind - the 90 deg corner captured with {over:F0} m past the line (turn radius {p.TurnR:F0} m), then the leg's last 40 % within {worst:F1} m; reached wp {p.Wp}/{p.WpCount}, loitering {p.Loitering}");
        Sim.Check(!capturing && worst < 30f && over < 0.15f * MathF.Max(p.TurnR, 100f), $"{r.Name}: route - capture overshoot {over:F0} m (< 15 % of the turn radius), steady cross-track {worst:F1} m (< 30) with the wind across");

        Program.LedgerPublic(r, false);
    }

    static void Loiter(string polar)
    {
        var r = PlaneAt($"loiter {polar}", polar, 150f);
        if (r == null) { Sim.NoPolar(polar); return; }
        var cfg = ApPanel.Defaults; cfg.AltM = 3000f; cfg.SpeedMs = 150f; cfg.LoiterM = 1500f; cfg.HeightRef = ApHeightRef.Inertial;
        var p = Engage(r, cfg, ApMode.Navigate, route: Pts((-3000, 3000, 0)));
        r.Fly(60f);
        float worst = 0f, sum = 0f; int n = 0;
        r.Each(rr => { if (p.Loitering) { worst = MathF.Max(worst, MathF.Abs(p.RadiusErr)); sum += p.RadiusErr * p.RadiusErr; n++; } });
        r.Fly(120f);
        float rms = n > 0 ? MathF.Sqrt(sum / n) : float.NaN;
        Sim.Note($"{r.Name}: loiter radius {cfg.LoiterM} m (turn radius {p.TurnR:F0} m) - radius error RMS {rms:F1} m, at most {worst:F1} m");
        Sim.Check(n > 0 && rms < 40f && worst < 120f, $"{r.Name}: loiter - radius RMS {rms:F1} m (< 40), worst {worst:F1} m (< 120)");
        Program.LedgerPublic(r, false);
    }

    static void HeliHold()
    {
        var r = new Run("heli flight hold", Ships.Heli(3f, true));
        r.Fly(0.5f);
        var cfg = ApPanel.Defaults;
        var p = Engage(r, cfg, ApMode.FlightHold);
        Sim.Check(p.Style == ApStyle.Helicopter, $"heli flight hold: the style detected {p.Style}");
        r.Fly(3f);
        p.Cfg.HeadingDeg = 90f; p.Cfg.SpeedMs = 15f;
        r.Fly(25f);
        float dh = 0f, dv = 0f; double h0 = p.HeightNow; float hd = 0f;
        r.Each(rr => { dh = MathF.Max(dh, MathF.Abs(p.HeightNow - (float)h0)); dv = MathF.Max(dv, MathF.Abs(p.SpeedNow - 15f)); hd = MathF.Max(hd, MathF.Abs(ApCompass.Diff(Hdg(rr), 90f))); });
        r.Fly(20f);
        Sim.Note($"heli flight hold: heading 090 at 15 m/s - speed within {dv:F2} m/s, track within {hd:F1} deg, height within {dh:F2} m; tilt {r.MaxTilt:F1} deg");
        Sim.Check(dv < 1.5f && hd < 5f && dh < 1.5f, $"heli flight hold: speed within {dv:F2} m/s (< 1.5), track within {hd:F1} deg (< 5), height within {dh:F2} m (< 1.5)");
        Program.LedgerPublic(r, false);
    }

    static void HeliRoute()
    {
        var r = new Run("heli route", Ships.Heli(3f, true));
        r.P.P = new Vector3D(0, 60, 0);
        r.Fly(0.5f);
        var cfg = ApPanel.Defaults; cfg.SpeedMs = 20f; cfg.AltM = 50f; cfg.HeightRef = ApHeightRef.Ground; cfg.MinAglM = 20f;
        var p = Engage(r, cfg, ApMode.Navigate, route: Pts((300, 0, 0), (300, 0, 300), (0, 0, 300)));
        float vmax = 0f; r.Each(rr => vmax = MathF.Max(vmax, rr.P.HorizontalV.Length()));
        r.Fly(90f);
        var end = new Vector3D(0, r.P.P.Y, 300);
        float off = (float)(r.P.P - end).Length();
        Sim.Note($"heli route: three waypoints at 20 m/s - reached {p.Wp}/{p.WpCount}, loitering {p.Loitering}, {off:F1} m from the last, fastest {vmax:F1} m/s, height over the ground {r.P.Agl:F1} m (50 asked)");
        Sim.Check(p.Loitering && off < 3f && vmax < 24f && MathF.Abs(r.P.Agl - 50f) < 2f, $"heli route: at the last waypoint ({off:F1} m), speed at most {vmax:F1} m/s (20 asked), height {r.P.Agl:F1} m");
        Program.LedgerPublic(r, false);
    }

    static void SixRoute()
    {
        var r = new Run("six-axis route", FlightShips.SixAxis());
        r.P.P = new Vector3D(0, 100, 0);
        r.Fly(0.5f);
        var cfg = ApPanel.Defaults; cfg.SpeedMs = 25f; cfg.AltM = 120f; cfg.HeightRef = ApHeightRef.Ground;
        var p = Engage(r, cfg, ApMode.Navigate, route: Pts((400, 0, 0), (400, 0, 400)));
        Sim.Check(p.Style == ApStyle.SixAxis, $"six-axis route: the style detected {p.Style}");
        float tilt = 0f, vmax = 0f; r.Each(rr => { tilt = MathF.Max(tilt, rr.P.TiltDeg); vmax = MathF.Max(vmax, rr.P.HorizontalV.Length()); });
        r.Fly(70f);
        var end = new Vector3D(400, r.P.P.Y, 400);
        float off = (float)(r.P.P - end).Length();
        r.Measure(); r.Fly(10f);
        Sim.Note($"six-axis route: reached {p.Wp}/{p.WpCount}, {off:F1} m from the last waypoint, held within {r.MaxPosErr:F2} m after; fastest {vmax:F1} m/s (25), tilt at most {tilt:F1} deg (no lean), height {r.P.Agl:F1} m");
        Sim.Check(p.Loitering && off < 3f && r.MaxPosErr < 1f && tilt < 5f && vmax < 28f, $"six-axis route: at the end ({off:F1} m), held within {r.MaxPosErr:F2} m, level (tilt {tilt:F1} deg), speed at most {vmax:F1} m/s");
        Program.LedgerPublic(r, false);
    }

    static void HillsRoute()
    {
        var r = PlaneAt("hills route Plane2", "Plane2", 150f, y: 400);
        if (r == null) { Sim.NoPolar("Plane2"); return; }
        r.P.Terrain = SimTerrain.Hills(250, 3000);
        var cfg = ApPanel.Defaults; cfg.AltM = 150f; cfg.SpeedMs = 150f; cfg.HeightRef = ApHeightRef.Ground; cfg.MinAglM = 100f;
        var p = Engage(r, cfg, ApMode.Navigate, route: Pts((-12000, 0, 0)));
        r.Measure();
        r.Fly(80f);
        Sim.Note($"hills route: 150 m over the ground asked, 100 m minimum, over 250 m hills - lowest {r.MinAgl:F0} m, highest {r.MaxAgl:F0} m, prot {r.ProtSeen}");
        Sim.Check(!r.P.Crashed && r.MinAgl > 60f, $"hills route: the terrain kept under it - lowest {r.MinAgl:F0} m over the ground (> 60)");
        Program.LedgerPublic(r, false);
    }

    static void Styles()
    {
        foreach (var (ship, want) in new (SimShip, ApStyle)[]
        {
            (Ships.Plane("Plane2"), ApStyle.Airplane), (Ships.Plane("tu134_game"), ApStyle.Airplane), (Ships.Plane("jetliner"), ApStyle.Airplane),
            (Ships.Heli(3f), ApStyle.Helicopter), (Ships.Heli(2.5f, true), ApStyle.Helicopter), (Ships.Vtol(), ApStyle.Helicopter),
            (FlightShips.SixAxis(), ApStyle.SixAxis),
        })
        {
            if (ship == null) continue;
            var r = new Run("style " + ship.Name, ship);
            var s = r.P.SenseNow();
            var got = ApStyleDetect.Of(in s);
            Sim.Check(got == want, $"style: {ship.Name} detected {got} (expected {want})");
        }
        var sp = FlightShips.SixAxis(); sp.PlanetG = 0f;
        var rs = new Run("style space", sp); var ss = rs.P.SenseNow();
        Sim.Check(ApStyleDetect.Of(in ss) == ApStyle.SixAxis, "style: the gunship in space - six-axis");
    }

    static void Refusals()
    {
        var slow = PlaneAt("refuse slow plane", "Plane2", 150f);
        if (slow != null)
        {
            slow.P.V = Vector3.Normalize(slow.P.V) * 15f;
            var p = new ApPilot { Cfg = ApPanel.Defaults, Fd = slow.P.Fd }; var s = slow.P.SenseNow();
            Sim.Check(!p.Begin(ApMode.FlightHold, in s, out var why) && why == ApReason.TooSlow, $"refused: a plane at 15 m/s - '{ApText.Say(why)}'");
        }
        {
            var r = new Run("refuse no route", Ships.Heli(3f)); var p = new ApPilot { Cfg = ApPanel.Defaults, Fd = r.P.Fd }; var s = r.P.SenseNow();
            Sim.Check(!p.Begin(ApMode.Navigate, in s, out var why) && why == ApReason.NoWaypoints, $"refused: navigate with no waypoints - '{ApText.Say(why)}'");
        }
        {
            var r = new Run("refuse six on a heli", Ships.Heli(3f)); var cfg = ApPanel.Defaults; cfg.Style = ApStyle.SixAxis;
            var p = new ApPilot { Cfg = cfg, Fd = r.P.Fd }; var s = r.P.SenseNow();
            Sim.Check(!p.Begin(ApMode.FlightHold, in s, out var why) && why == ApReason.NoTranslation, $"refused: six-axis forced on a helicopter - '{ApText.Say(why)}'");
        }
    }
}
