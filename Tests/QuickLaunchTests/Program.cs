using System.IO;
using System.Text.RegularExpressions;
using AeroMod;

// Offline checks for Launch/QuickLaunchRules.cs - the server's decisions on a seated pilot's creative quick launch (2026-10-18:
// the Orbital Mod's 1.0.1 key, moved to the aero mod - "ctrl+T should be in aero creative") - and a scan of the engine side for
// the rules it must keep (the server's own character, seat and game mode; the Orbital Mod moves the ship when it is loaded and
// aero never does then; no static ever moved; the key never while typing). Ported from the Orbital Mod's Tests/QuickLaunchTests
// (48 checks); its space border clamp stays there (OrbitalApi.TryQuickMove). Exit 0 = all pass.
static class Program
{
    static int _pass, _fail;
    static void Check(bool ok, string what)
    {
        if (ok) { _pass++; Console.WriteLine("  PASS  " + what); }
        else { _fail++; Console.WriteLine("  FAIL  " + what); }
    }

    static int Main()
    {
        Decide();
        Where();
        Level();
        Speed();
        Say();
        Binding();
        Presses();
        SourceScan();
        Console.WriteLine($"== {_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    static QuickLaunchRules.Verdict C(bool enabled = true, bool pilot = true, bool creative = true, bool seated = true, bool alive = true,
                                      bool isStatic = false, bool joined = false, bool planet = true)
        => QuickLaunchRules.Check(enabled, pilot, creative, seated, alive, isStatic, joined, planet);

    static void Decide()
    {
        Console.WriteLine("[may it go]");
        var V = typeof(QuickLaunchRules.Verdict);
        Check(C() == QuickLaunchRules.Verdict.Go, "creative, seated, dynamic, free of stations, at a planet: go");
        Check(C(creative: false) == QuickLaunchRules.Verdict.NotCreative, "survival: refused (creative only)");
        Check(C(creative: false, enabled: false) == QuickLaunchRules.Verdict.Off, "the world turned it off: said first (survival too)");
        Check(C(enabled: false) == QuickLaunchRules.Verdict.Off, "creative, the world's switch off: refused");
        Check(C(pilot: false, creative: false) == QuickLaunchRules.Verdict.NotSeated, "the server knows no character of the asking player: 'sit in a cockpit' (never 'creative only' for a character nobody found)");
        Check(C(seated: false) == QuickLaunchRules.Verdict.NotSeated, "the server sees nobody seated: refused (its own seat - the client names no grid)");
        Check(C(alive: false) == QuickLaunchRules.Verdict.Gone, "the grid is gone: refused");
        Check(C(isStatic: true) == QuickLaunchRules.Verdict.Static, "a static grid (a station): refused");
        Check(C(joined: true) == QuickLaunchRules.Verdict.JoinedToStatic, "docked or locked to a station: refused (it would have to move the station)");
        Check(C(planet: false) == QuickLaunchRules.Verdict.NotAtPlanet, "no planet's gravity (deep space): refused");
        Check(C(creative: false, isStatic: true) == QuickLaunchRules.Verdict.NotCreative, "survival is said before anything about the ship");
    }

    const double R = 60000;   // (a Verdure-like planet's radius)

    static void Where()
    {
        Console.WriteLine("[where it ends]");
        var go = QuickLaunchRules.Verdict.Go;
        double c = QuickLaunchRules.MinClearance;
        Check(QuickLaunchRules.Lift(R + 20, R, 1000, 15, out double r, out double drop) == go && Math.Abs(r - (R + 1000)) < 1e-6, "parked on the ground: 1000 m over it");
        Check(Math.Abs(drop - (1000 - 15 - c)) < 1e-6, "  and it may be clamped down by 885 m at most (its radius plus the clearance kept)");
        Check(QuickLaunchRules.Lift(R + 820, R + 800, 1000, 15, out r, out _) == go && Math.Abs(r - (R + 1800)) < 1e-6, "on a hill: 1000 m over the hill, not over sea level");
        Check(QuickLaunchRules.Lift(R + 3000, R, 1000, 15, out r, out drop) == go && Math.Abs(r - (R + 3000)) < 1e-6, "already higher than the launch altitude: never dropped (stays at its height)");
        Check(Math.Abs(drop - (3000 - 15 - c)) < 1e-6, "  its clamp room is all of its height over the clearance");
        Check(QuickLaunchRules.Lift(R + 50, double.NaN, 1000, 15, out r, out _) == go && Math.Abs(r - (R + 1050)) < 1e-6, "no ground ray: lifted 1000 m from where it is (never into an unmeasured hill)");
        Check(QuickLaunchRules.Lift(R + 50, R + 400, 1000, 15, out r, out _) == go && Math.Abs(r - (R + 1050)) < 1e-6, "a ground ray above the ship (a cave, a bad ray): the ship's own place stands in");
        Check(QuickLaunchRules.Lift(R + 20, R, 100, 300, out r, out drop) == go && r >= R + 300 + c - 1e-6 && drop < 1e-6, "a long ship and a low setting: its radius plus the clearance over the ground, no clamp room");
        Check(QuickLaunchRules.Lift(R + 20, R, double.NaN, 15, out r, out _) == go && Math.Abs(r - (R + 15 + c)) < 1e-6, "no altitude (NaN): the clearance alone");
        Check(QuickLaunchRules.Lift(double.NaN, R, 1000, 15, out r, out _) == QuickLaunchRules.Verdict.NotAtPlanet, "no position: refused");
        Check(QuickLaunchRules.Lift(0, R, 1000, 15, out r, out _) == QuickLaunchRules.Verdict.NotAtPlanet, "at the planet's centre (0): refused");
        // never into the ground, over a sweep of places and settings
        bool ok = true, dropOk = true;
        var rnd = new Random(7);
        for (int i = 0; i < 2000; i++)
        {
            double ground = R + rnd.NextDouble() * 8000, now = ground + rnd.NextDouble() * 3000, alt = 100 + rnd.NextDouble() * 4900, ship = rnd.NextDouble() * 200;
            bool known = rnd.Next(3) > 0;
            var v = QuickLaunchRules.Lift(now, known ? ground : double.NaN, alt, ship, out r, out drop);
            double floor = known ? ground : now;
            if (v != go || r < now - 1e-6 || r < floor + ship + c - 1e-6) ok = false;
            if (drop < 0 || r - drop < floor + ship + c - 1e-6 || (r - drop) - (floor + ship + c) > 1e-6 && drop > 0) dropOk = false;
        }
        Check(ok, "2000 random places: never lower, never under the ship's clearance over the ground");
        Check(dropOk, "2000 random places: the clamp room lands exactly on the clearance, never under it");
        Check(QuickLaunchRules.Where(R + 1000, R + 20, R) == "1000 m over the ground", "the answer: the height over the ground");
        Check(QuickLaunchRules.Where(R + 1050, R + 50, double.NaN) == "1000 m up", "the answer, no ground known: the lift (never a height over a ground nobody measured)");
    }

    static Vector3D Rot(Quaternion q, Vector3D v) => Vector3D.Transform(v, q);

    static void Level()
    {
        Console.WriteLine("[the level turn]");
        var rnd = new Random(11);
        bool ok = true, still = true;
        double worst = 0;
        for (int i = 0; i < 500; i++)
        {
            // a grid turned anyhow, a cockpit on it turned anyhow (its own axes in the grid's), up anywhere
            var gq = Quaternion.Normalize(new Quaternion((float)(rnd.NextDouble() - 0.5), (float)(rnd.NextDouble() - 0.5), (float)(rnd.NextDouble() - 0.5), (float)(rnd.NextDouble() - 0.5)));
            var sl = Quaternion.Normalize(new Quaternion((float)(rnd.NextDouble() - 0.5), (float)(rnd.NextDouble() - 0.5), (float)(rnd.NextDouble() - 0.5), (float)(rnd.NextDouble() - 0.5)));
            var up = Vector3D.Normalize(new Vector3D(rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5));
            var sq = Quaternion.Normalize(gq * sl);
            Vector3D noseW = Rot(sq, -Vector3D.UnitZ), topW = Rot(sq, Vector3D.UnitY);
            var q = QuickLaunchRules.Level(gq, noseW, topW, up, out Vector3D h);
            Vector3D noseL = Rot(Quaternion.Inverse(gq), noseW), topL = Rot(Quaternion.Inverse(gq), topW);
            Vector3D nose2 = Rot(q, noseL), top2 = Rot(q, topL);
            double e = Math.Max((nose2 - h).Length(), (top2 - up).Length());
            worst = Math.Max(worst, e);
            if (e > 2e-3 || Math.Abs(Vector3D.Dot(h, up)) > 1e-4 || Math.Abs(h.Length() - 1) > 1e-6) ok = false;
            // the heading is the nose's own, flattened (it keeps pointing the way it did)
            if (Vector3D.Dot(h, noseW) < -1e-6) still = false;
        }
        Check(ok, $"500 random ships: the cockpit's nose ends horizontal on its heading, its top up (worst {worst:E1})");
        Check(still, "  the heading is the nose's own, flattened (never turned round)");
        // the nose straight up: its top's heading
        var up0 = Vector3D.UnitY;
        var q0 = QuickLaunchRules.Level(Quaternion.Identity, Vector3D.UnitY, Vector3D.UnitZ, up0, out Vector3D h0);
        Check((h0 - Vector3D.UnitZ).Length() < 1e-6, "the nose straight up: it levels toward where its top pointed");
        var q1 = QuickLaunchRules.Level(Quaternion.Identity, -Vector3D.UnitZ, Vector3D.UnitY, up0, out Vector3D h1);
        Check((h1 + Vector3D.UnitZ).Length() < 1e-6 && Math.Abs(Math.Abs(q1.W) - 1) < 1e-6, "already level: no turn");
    }

    static void Speed()
    {
        Console.WriteLine("[speed]");
        Check(QuickLaunchRules.Speed(0, 331, 150, 990) == 331, "0: the cruise speed (the top speed at 3/4 throttle)");
        Check(QuickLaunchRules.Speed(0, double.NaN, 150, 990) == 150, "0, no cruise known: the fallback");
        Check(QuickLaunchRules.Speed(0, 0.5, 150, 990) == 150, "0, a cruise under 1 m/s (no thrust that way): the fallback");
        Check(QuickLaunchRules.Speed(0, double.PositiveInfinity, 150, 990) == 150, "0, an infinite cruise: the fallback");
        Check(QuickLaunchRules.Speed(250, 331, 150, 990) == 250, "a set speed wins over cruise");
        Check(QuickLaunchRules.Speed(1500, 331, 150, 990) == 990, "never over the cap");
        Check(QuickLaunchRules.Speed(0, 5000, 150, 990) == 990, "a cruise over the cap: the cap");
        Check(QuickLaunchRules.Speed(400, 331, 150, 0) == 400, "no cap known (0): as set");
        Check(Math.Abs(1000 * QuickLaunchRules.CapShare - 990) < 1e-9, "the cap: 0.99 of the physics' (990 of 1000 - the Orbital Mod's arrival cap)");
    }

    static void Say()
    {
        Console.WriteLine("[what the player reads]");
        Check(QuickLaunchRules.Say(QuickLaunchRules.Verdict.Go) == null, "go: no refusal line");
        bool all = true;
        foreach (QuickLaunchRules.Verdict v in Enum.GetValues(typeof(QuickLaunchRules.Verdict)))
            if (v != QuickLaunchRules.Verdict.Go && string.IsNullOrEmpty(QuickLaunchRules.Say(v))) all = false;
        Check(all, "every refusal has a line");
        Check(QuickLaunchRules.Say(QuickLaunchRules.Verdict.NotCreative).Contains("creative"), "survival says creative only");
    }

    static void Binding()
    {
        Console.WriteLine("[the binding]");
        Check(QuickLaunchRules.ModifierHeld(1, true, false, false) && !QuickLaunchRules.ModifierHeld(1, false, false, false), "Ctrl: with Ctrl held, not without");
        Check(QuickLaunchRules.ModifierHeld(0, false, false, false) && !QuickLaunchRules.ModifierHeld(0, true, false, false) && !QuickLaunchRules.ModifierHeld(0, false, false, true),
              "none: the key alone - Ctrl+T bound elsewhere never also fires a bare T");
        Check(QuickLaunchRules.ModifierHeld(2, false, true, false) && QuickLaunchRules.ModifierHeld(3, false, false, true) && !QuickLaunchRules.ModifierHeld(9, true, true, true), "Shift, Alt; an unknown modifier never");
        Check(QuickLaunchRules.BindingText(20, 1) == "Ctrl+T" && QuickLaunchRules.BindingText(0, 1) == "off" && QuickLaunchRules.BindingText(1, 0) == "A", "the binding as read: Ctrl+T, off, A");
        var ch = QuickLaunchRules.KeyChoices();
        Check(ch.Length == 27 && ch[0] == "Off" && ch[20] == "T" && ch[26] == "Z", "the key's choices: Off, A..Z (20: T) - one per value 0..26");
    }

    // ── the key's presses (2026-10-18: the Orbital Mod's copy lifted ships nobody asked for - the SSTO 4 s after load, the
    //    Tu-134 6 s after sitting down, four more ~1.2 s apart after ONE harness ask) - QuickLaunch.Tick's order, played
    //    over key sequences at 60 Hz. F: a fake Stopwatch frequency (ticks a second). ──
    const long F = 1_000_000;

    /// <summary>One client tick's world: the binding's key, Ctrl, a screen taking the keyboard, seated, the device there.</summary>
    struct In { public bool T, Ctrl, Screen, Seated, Device; }

    /// <summary>QuickLaunch.Tick's steps on the rules (KeyEdge, Fresh, the 1 s spacing), and the server's take (Fresh on the
    /// ask's stamp, serverLag ticks later). Returns the launches the server would serve; asks/dropped out.</summary>
    sealed class Client
    {
        public readonly QuickLaunchRules.KeyEdge Edge = new QuickLaunchRules.KeyEdge();
        public long DevAt, LastAsk = long.MinValue / 2, AskAt;   // (AskAt: the pending ask's stamp, 0: none)
        public int Asks, Served, Dropped;
        public void Tick(In i, long now)
        {
            long devAt = DevAt; DevAt = 0;
            bool dev = QuickLaunchRules.Fresh(devAt, now, (long)(F * QuickLaunchRules.AskMaxAgeS));
            bool pressed;
            if (!i.Device) { Edge.Disarm(); pressed = false; }
            else pressed = Edge.Step(i.T, i.T && QuickLaunchRules.ModifierHeld(1, i.Ctrl, false, false));
            if (!dev && !pressed) return;
            if (!dev && i.Screen) return;
            if (now - LastAsk < F) return;
            if (!i.Seated) return;
            LastAsk = now; AskAt = now; Asks++;
        }
        public void Server(long now)
        {
            long at = AskAt; AskAt = 0;   // (TakeAsk: swapped out, taken at most once)
            if (at == 0) return;
            if (QuickLaunchRules.Fresh(at, now, (long)(F * QuickLaunchRules.AskMaxAgeS))) Served++; else Dropped++;
        }
    }

    /// <summary>Plays seconds of 60 Hz ticks; key(t) gives each tick's state; serverEvery: the server's take every n ticks
    /// (0: never, until the end + lagEnd s).</summary>
    static Client Play(double seconds, Func<double, In> key, Client c = null, int serverEvery = 1, double lagEnd = 0, Action<Client, double> each = null)
    {
        c ??= new Client();
        int n = (int)(seconds * 60);
        for (int k = 0; k < n; k++)
        {
            double s = k / 60.0; long now = 1 + (long)(s * F);
            each?.Invoke(c, s);
            c.Tick(key(s), now);
            if (serverEvery > 0 && k % serverEvery == 0) c.Server(now);
        }
        if (lagEnd > 0) c.Server(1 + (long)((seconds + lagEnd) * F));
        return c;
    }
    static In Idle(double s) => new In { Seated = true, Device = true };
    static In Press(double s, double from, double to, bool ctrl = true) => new In { T = s >= from && s < to, Ctrl = ctrl && s >= from - 0.05 && s < to + 0.05, Seated = true, Device = true };

    static void Presses()
    {
        Console.WriteLine("[one launch per press]");
        var c = Play(30, Idle);
        Check(c.Asks == 0 && c.Served == 0, $"seated 30 s, nobody at the keyboard: no ask ({c.Asks})");
        c = Play(5, s => Press(s, 1.0, 1.15));
        Check(c.Asks == 1 && c.Served == 1, $"one Ctrl+T press: one launch ({c.Served})");
        c = Play(8, s => Press(s, 1.0, 6.0));
        Check(c.Asks == 1 && c.Served == 1, $"Ctrl+T held 5 s: one launch, never a repeat while held ({c.Served})");
        c = Play(5, s => Press(s, 1.0, 1.2, ctrl: false));
        Check(c.Asks == 0, "a bare T (the binding is Ctrl+T): nothing");
        c = Play(5, s => new In { T = s >= 1 && s < 3, Ctrl = s >= 2 && s < 3, Seated = true, Device = true });
        Check(c.Asks == 0, "T held, then Ctrl added: no press (the key went down without its modifier)");
        c = Play(6, s => new In { T = s < 3 || (s >= 4 && s < 4.2), Ctrl = s < 3 || (s >= 3.95 && s < 4.25), Seated = true, Device = true });
        Check(c.Asks == 1, $"Ctrl+T down from the first tick (held through a load, a stale device): no ask until it is let go and pressed again - then one ({c.Asks})");
        c = Play(4, s => Press(s, 1.0, 1.1).T || Press(s, 1.3, 1.4).T ? Press(s, 0, 9) : Idle(s));
        Check(c.Asks == 1, $"a double tap 0.3 s apart: one launch (one a second) ({c.Asks})");
        c = Play(4, s => Press(s, 1.0, 1.1).T || Press(s, 2.5, 2.6).T ? Press(s, 0, 9) : Idle(s));
        Check(c.Asks == 2, $"two presses 1.5 s apart: two ({c.Asks})");
        c = Play(13, s => { double m = s % 1.2; return s >= 0.5 && m < 0.1 ? Press(s, 0, 99) : Idle(s); });
        Check(c.Asks == 10, $"ten presses 1.2 s apart (the in-game run's spacing): ten asks - each one a real press ({c.Asks})");
        c = Play(5, s => new In { T = (int)(s * 60) % 2 == 0, Ctrl = true, Seated = true, Device = true });
        Check(c.Asks <= 5, $"a key flickering every tick for 5 s (noise): at most one a second ({c.Asks})");
        c = Play(5, s => { var i = Press(s, 1.0, 3.0); i.Screen = s < 2.0; return i; });
        Check(c.Asks == 0, "Ctrl+T while the chat or a terminal takes the keyboard: nothing - nor when the screen closes with it still held");
        c = Play(5, s => { var i = Press(s, 1.0, 4.0); i.Seated = s >= 2.0; return i; });
        Check(c.Asks == 0, "Ctrl+T pressed on foot, still held when you sit down: nothing");
        c = Play(6, s => { var i = Press(s, 1.0, 4.0); i.Device = !(s >= 2.0 && s < 2.5); return i; });
        Check(c.Asks == 1, $"the device lost and found while the key is held: the one press only, no second from the new device ({c.Asks})");

        Console.WriteLine("[the harness's ask: one-shot]");
        c = Play(5, Idle, each: (cl, s) => { if (Math.Abs(s - 1.0) < 1e-9) cl.DevAt = 1 + (long)(s * F); });
        Check(c.Asks == 1 && c.Served == 1, $"AeroApi.RequestQuickLaunch once: one launch, the flag consumed ({c.Served})");
        c = new Client { DevAt = 1 };   // (asked at 0; no client tick for 30 s - no ship's effects job near - then one, seated)
        c.Tick(Idle(30), 1 + 30 * F); c.Server(1 + 30 * F);
        Check(c.Asks == 0 && c.Served == 0, "a harness ask no client tick took in time: dropped - not served 30 s later to the ship you sit in");
        c = Play(5, s => Press(s, 1.0, 1.1), serverEvery: 0, lagEnd: 3);
        Check(c.Asks == 1 && c.Served == 0 && c.Dropped == 1, "an ask the server took 3 s late: dropped, never served late");
        c = Play(5, s => Press(s, 4.85, 4.95), serverEvery: 0, lagEnd: 0.2);
        Check(c.Served == 1, "an ask taken 0.35 s after it was made: served");
        Check(!QuickLaunchRules.Fresh(0, 5, 10) && !QuickLaunchRules.Fresh(10, 5, 10) && QuickLaunchRules.Fresh(5, 15, 10) && !QuickLaunchRules.Fresh(5, 16, 10),
              "Fresh: no stamp never; one from the future never; up to its age, not past it");

        // (the edge before 2026-10-18 - its flag starting 'up': a key down at the first read was a press)
        bool was = false; int old = 0;
        for (int k = 0; k < 120; k++) { bool dn = true; if (dn && !was) old++; was = dn; }
        var e = new QuickLaunchRules.KeyEdge(); int now0 = 0;
        for (int k = 0; k < 120; k++) if (e.Step(true, true)) now0++;
        Check(old == 1 && now0 == 0, $"  a key down from the first read: the old edge asked {old}, KeyEdge {now0}");
    }

    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "Scripts", "Simulation", "Launch"))) d = d.Parent;
        return d?.FullName ?? throw new Exception("no mod root above " + AppContext.BaseDirectory);
    }
    static string Src(params string[] rel) => Regex.Replace(File.ReadAllText(Path.Combine(Root(), "Scripts", "Simulation", Path.Combine(rel))), @"//.*", "");
    static string Body(string src, string head) => Regex.Match(src, Regex.Escape(head) + @".*?\n    \}", RegexOptions.Singleline).Value;

    static void SourceScan()
    {
        Console.WriteLine("[the engine side]");
        string srv = Src("Launch", "QuickLaunch.Server.cs");
        string serve = Body(srv, "static void Serve(");
        Check(serve.Length > 0, "Serve found");
        Check(serve.Contains("GameModeSessionComponent") && serve.Contains("IsCreative(ch)"), "creative is the server's game mode (IsCreative of its own character)");
        Check(serve.Contains("SeatComponent") && serve.Contains("Pilot == ch") && serve.Contains("Grid?.Entity"), "the grid is the server's own seat's (the seat its character pilots)");
        Check(serve.Contains("QuickLaunchRules.Check(Creative,"), "the world's switch is read, through the rules");
        Check(serve.Contains("QuickLaunchMove.IsStatic(grid)") && serve.Contains("IsConnectedToStaticEntity(grid)"), "static and joined-to-a-station asked of the engine now");
        Check(serve.Contains("QuickLaunchRules.Lift(") && serve.Contains("QuickLaunchRules.Level(") && serve.Contains("QuickLaunchRules.Speed("), "where, the turn and the speed: the rules");
        int orb = serve.IndexOf("OrbitalLink.TryQuickMove("), own = serve.IndexOf("QuickLaunchMove.Move(");
        Check(orb > 0 && own > orb && Regex.IsMatch(serve, @"if \(o >= 0\)[^\n]*\n\s*else\s*\{[^}]*QuickLaunchMove\.Move\(", RegexOptions.Singleline),
              "the Orbital Mod moves it when loaded; aero's own move only when it is not (never behind its back)");
        Check(serve.Contains("NoteTurn("), "aero's own move turns the cockpit's target too (the gyros would swing it back)");
        Check(Regex.IsMatch(srv, @"\[Before\(typeof\(Keen\.VRage\.Core\.Systems\.Simulation\)\)\]\s*private class OnQuickLaunch"), "the server step runs in the teleport phase (before the simulation, where fast travel moves ships)");
        string link = Src("Api", "OrbitalLink.cs");
        string tqm = Body(link, "internal static int TryQuickMove(");
        Check(Regex.IsMatch(tqm, @"if \(_api\.Type == null\) return -1;") && !Regex.IsMatch(tqm.Replace("if (_api.Type == null) return -1;", ""), @"return -1"),
              "'no Orbital Mod' only when its type is not loaded (an older Orbital Mod refuses: it would not know of aero's move)");
        string mv = Src("Launch", "QuickLaunchMove.cs");
        Check(mv.Contains("IsConnectedToStaticEntity") && mv.Contains("IncludeConstrained = true") && Regex.IsMatch(mv, @"if \(IsStatic\(e\)\) \{[^}]*return false;"), "aero's move: the joined set, nothing moved when any of it is static");
        string move = Body(mv, "internal static bool Move(");
        int calc = move.IndexOf("to.Add("), write = move.IndexOf("SetWorldTransform(");
        Check(calc > 0 && write > calc && move.Contains("MaskContacts(") && move.Contains("TryUnmanage("), "  every place worked out before the first is written; contacts masked; world areas released");
        string cl = Src("Launch", "QuickLaunch.cs");
        Check(cl.Contains("TopScreenNeedsInput(session)") && cl.Contains("LocalPilot("), "the key: seated only, never while typing");
        Check(Regex.IsMatch(cl, @"public static int Key = 20;") && Regex.IsMatch(cl, @"public static int Modifier = 1;"), "the default binding is Ctrl+T (Key 20 = T, Modifier 1 = Ctrl)");
        Check(Regex.IsMatch(Body(cl, "internal sealed class Ask"), @"\{ public [^;]*IdentityId Who; public bool HasWho; public long At; \}"), "the ask carries only who asks and when (no client entity reaches the server)");
        string tick = Body(cl, "static void Tick(");
        Check(tick.Contains("QuickLaunchRules.Fresh(devAt,") && tick.Contains("Interlocked.Exchange(ref _devAsk, 0)"), "the harness's ask: taken once, only while fresh");
        Check(cl.Contains("return _edge.Step(down, held);") && !cl.Contains("_wasDown"), "the key: the rules' KeyEdge (no hand-rolled edge flag)");
        Check(Regex.Matches(cl, @"_edge\.Disarm\(\)").Count >= 4, "  disarmed when off, without a device, on a lost device and on a world reset");
        Check(Regex.IsMatch(Body(cl, "internal static void ClientTick("), @"CompareExchange\(ref _busy, 1, 0\) != 0\) return;"), "one job at a time at the key (the time claim alone let two in)");
        Check(Body(srv, "internal static void ServerStep(").Contains("QuickLaunchRules.Fresh(ask.At,"), "the server: an ask taken once (TakeAsk) and only while fresh");
        string fx = Src("Fx", "AeroEntryFx.cs");
        Check(fx.Contains("QuickLaunch.ClientTick(c.Entity)") && fx.Contains("QuickLaunch.TurnTarget(c.Entity)"), "the client effects job runs the key and the target turn");
        string api = Src("Api", "AeroApi.cs");
        Check(api.Contains("public static void RequestQuickLaunch() => QuickLaunch.DevAsk();"), "AeroApi.RequestQuickLaunch: the key's ask (the Orbital harness key quicklaunch)");
        string set = Src("Runtime", "AeroSettings.cs");
        bool reg = true;
        foreach (var n in new[] { "QuickLaunch.Creative\", \"world\"", "QuickLaunch.AltitudeM\", \"world\"", "QuickLaunch.SpeedMps\", \"world\"", "QuickLaunch.Key\", \"client\"", "QuickLaunch.Modifier\", \"client\"" })
            if (!set.Contains(n)) reg = false;
        Check(reg, "the five settings registered (three world, two client)");
    }
}
