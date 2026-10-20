using System.IO;
using System.Text.RegularExpressions;
using AeroMod;
using K = AeroMod.EnemySpawnRules.Kind;
using V = AeroMod.EnemySpawnRules.Verdict;

// Offline checks for Launch/EnemySpawnRules.cs - the TEST SPAWNS (2026-10-20: Ctrl+Shift+I an enemy missile, Ctrl+Shift+O an enemy bogey, both
// from a blueprint with an Autopilot block, going for the player; Ctrl+Shift+N removes them) - the Enemy switch's test-spawn path
// (ApEnemySwitch.RequestSpawned over a mocked world), the panel a spawned block gets through its saved form, and a scan of the
// engine side for the rules it must keep (server thread, async spawn, the API target, the keys' edges, no client entity in the
// ask). Exit 0 = all pass.
static class Program
{
    static int _pass, _fail;
    static void Check(bool ok, string what)
    {
        if (ok) { _pass++; if (Environment.GetEnvironmentVariable("VERBOSE") != null) Console.WriteLine("  PASS  " + what); }
        else { _fail++; Console.WriteLine("  FAIL  " + what); }
    }

    static int Main()
    {
        Permission();
        Validation();
        Keys();
        Names();
        Placement();
        Pointing();
        Speeds();
        Panel();
        EnemySwitch();
        Cleanup();
        SourceScan();
        Console.WriteLine($"== SpawnTests: {_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    static readonly EnemySpawnRules.Rule Off = EnemySpawnRules.Rule.Off, CoA = EnemySpawnRules.Rule.CreativeOrAdmin, All = EnemySpawnRules.Rule.Everyone;

    static void Permission()
    {
        Console.WriteLine("[who may spawn]");
        foreach (bool c in new[] { false, true })
            foreach (bool a in new[] { false, true })
            {
                Check(EnemySpawnRules.Allowed(Off, c, a) == V.Off, $"off: nobody (creative {c}, admin {a})");
                Check(EnemySpawnRules.Allowed(All, c, a) == V.Go, $"everyone: anyone (creative {c}, admin {a})");
                Check(EnemySpawnRules.Allowed(CoA, c, a) == (c || a ? V.Go : V.NotCreativeOrAdmin), $"creative or admin (creative {c}, admin {a})");
            }
        Check(EnemySpawnRules.Check(Off, true, true, false, false, false, false, false) == V.Off, "the world's rule is said first");
        Check(EnemySpawnRules.Check(CoA, false, false, true, true, true, true, true) == V.NotCreativeOrAdmin, "a survival non-admin in a creative-or-admin world: refused, said why");
    }

    static void Validation()
    {
        Console.WriteLine("[validation and its words]");
        Check(EnemySpawnRules.Check(CoA, true, false, true, true, true, true, true) == V.Go, "creative, found, autopilot on and unattended, a block, the slot written: go");
        Check(EnemySpawnRules.Check(CoA, true, false, false, true, true, true, true) == V.NoPlayer, "the server knows no character of the player: refused");
        Check(EnemySpawnRules.Check(CoA, true, false, true, true, true, false, true) == V.NoAutopilot, "no Autopilot block in the blueprint: refused");
        Check(EnemySpawnRules.Check(CoA, true, false, true, false, true, true, true) == V.AutopilotOff, "the world's autopilot off: refused (nothing would fly it)");
        Check(EnemySpawnRules.Check(CoA, true, false, true, true, false, true, true) == V.Unattended, "unattended autopilot off: refused (an unpiloted ship lets go)");
        Check(EnemySpawnRules.Check(CoA, true, false, true, true, true, true, false) == V.SlotMissing, "the slot's logic not written: refused");
        Check(EnemySpawnRules.Say(V.NoBlueprint, K.Missile, "Missile") == "no blueprint 'Missile' - save one with that name", "missing blueprint: the user's words");
        Check(EnemySpawnRules.Say(V.NoBlueprint, K.Bogey, null) == "no blueprint 'Bogey' - save one with that name", "missing blueprint, no name given: the kind's default name");
        Check(EnemySpawnRules.Say(V.NoAutopilot, K.Missile, "Missile") == "the blueprint needs an Autopilot block", "no Autopilot block: the user's words");
        Check(EnemySpawnRules.Say(V.SlotMissing, K.Missile, null).Contains("Rocket Guidance") && EnemySpawnRules.Say(V.SlotMissing, K.Bogey, null).Contains("Dogfight")
              && EnemySpawnRules.Say(V.SlotMissing, K.Bogey, null).Contains("not written yet"), "slot not written: names the slot and says so");
        Check(EnemySpawnRules.Say(V.Off, K.Missile, null).Contains("Spawn.Allowed") && EnemySpawnRules.Say(V.NotCreativeOrAdmin, K.Missile, null).Contains("creative or an admin"), "not allowed: says why");
        Check(EnemySpawnRules.Say(V.Unattended, K.Missile, null).Contains("Autopilot.Unattended") && EnemySpawnRules.Say(V.AutopilotOff, K.Missile, null).Contains("Autopilot.Enabled"), "the world's autopilot: names the setting");
        Check(EnemySpawnRules.Say(V.Go, K.Missile, null) == null, "go: nothing said");
        foreach (V v in Enum.GetValues(typeof(V))) if (v != V.Go) Check(!string.IsNullOrWhiteSpace(EnemySpawnRules.Say(v, K.Missile, "Missile")), $"every refusal has words ({v})");
        Check(EnemySpawnRules.Slot(K.Missile) == ApBehaviour.RocketGuidance && EnemySpawnRules.Slot(K.Bogey) == ApBehaviour.Dogfight, "the missile flies Rocket Guidance, the bogey Dogfight");
    }

    static void Keys()
    {
        Console.WriteLine("[the keys]");
        Check(EnemySpawnRules.BindingText(9, 4) == "Ctrl+Shift+I" && EnemySpawnRules.BindingText(15, 4) == "Ctrl+Shift+O" && EnemySpawnRules.BindingText(14, 4) == "Ctrl+Shift+N", "Ctrl+Shift+I / O / N read as such");
        Check(EnemySpawnRules.BindingText(0, 1) == "off" && EnemySpawnRules.BindingText(13, 3) == "Alt+M", "off, and a rebinding");
        // one press per key press, never while held, never a press not seen up first (QuickLaunchRules.KeyEdge, shared)
        var e = new QuickLaunchRules.KeyEdge();
        Check(!e.Step(true, true), "a key already down at load is no press");
        Check(!e.Step(false, true) && e.Step(true, true) && !e.Step(true, true) && !e.Step(true, true), "up, then down with Ctrl: one press; held: no more");
        Check(!e.Step(false, false) && !e.Step(true, false) && !e.Step(true, true), "down without its modifier uses the press (adding Ctrl to a held M is no press)");
        Check(QuickLaunchRules.ModifierHeld(1, true, false, false) && !QuickLaunchRules.ModifierHeld(1, false, false, false), "Ctrl wanted: Ctrl held");
        Check(QuickLaunchRules.ModifierHeld(4, true, true, false) && !QuickLaunchRules.ModifierHeld(4, true, false, false) && !QuickLaunchRules.ModifierHeld(4, false, true, false),
              "Ctrl+Shift wanted: both held, not one alone");
    }

    static void Names()
    {
        Console.WriteLine("[blueprint names]");
        Check(EnemySpawnRules.CleanName("  Missile ") == "Missile" && EnemySpawnRules.CleanName("Tupolev Tu-134") == "Tupolev Tu-134", "trimmed, spaces inside kept");
        foreach (var bad in new[] { "", "   ", null, "a/b", "..", "a\\b", "x:y", "q?", new string('x', 65) })
            Check(EnemySpawnRules.CleanName(bad) == null && (bad == null || EnemySpawnRules.NameRule(bad) != null), $"refused: '{bad}'");
        Check(EnemySpawnRules.NameRule("Missile") == null, "a plain name passes the rule");
        Check(EnemySpawnRules.Tagged("Missile") == "Missile [aero test]" && EnemySpawnRules.Tagged("Missile [aero test]") == "Missile [aero test]", "tagged once");
        Check(EnemySpawnRules.IsTagged("Bogey [aero test]") && !EnemySpawnRules.IsTagged("Bogey") && !EnemySpawnRules.IsTagged(null), "the tag is found");
    }

    static bool Near(Vector3D a, Vector3D b, double tol) => (a - b).Length() <= tol;

    static void Placement()
    {
        Console.WriteLine("[where it appears]");
        var c = new Vector3D(0, 0, 0); const double R = 60000;
        var up = new Vector3D(0, 1, 0);
        var t = up * (R + 1500);
        var nose = new Vector3D(0, 0, -1);
        // heading: velocity when moving, else the nose; always horizontal
        var h = EnemySpawnRules.Heading(up, new Vector3D(200, 50, 0), nose);
        Check(Near(h, new Vector3D(1, 0, 0), 1e-9), "moving: the heading is the velocity's, made level");
        h = EnemySpawnRules.Heading(up, new Vector3D(1, 0, 0), new Vector3D(0, -0.5, -1));
        Check(Near(h, new Vector3D(0, 0, -1), 1e-9), "slower than 5 m/s: the nose's, made level");
        h = EnemySpawnRules.Heading(up, Vector3D.Zero, up);
        Check(Math.Abs(h.Length() - 1) < 1e-9 && Math.Abs(Vector3D.Dot(h, up)) < 1e-9, "nose straight up, still: some level heading");
        var hd = new Vector3D(0, 0, -1);
        var right = Vector3D.Cross(hd, up);
        Check(Near(EnemySpawnRules.Direction(EnemySpawnRules.Dir.Behind, up, hd, 0.3), -hd, 1e-9), "behind: against the heading");
        Check(Near(EnemySpawnRules.Direction(EnemySpawnRules.Dir.Ahead, up, hd, 0.3), hd, 1e-9), "ahead: along the heading");
        var sL = EnemySpawnRules.Direction(EnemySpawnRules.Dir.Side, up, hd, 0.2); var sR = EnemySpawnRules.Direction(EnemySpawnRules.Dir.Side, up, hd, 0.8);
        Check(Near(sL, -right, 1e-9) && Near(sR, right, 1e-9), "side: left or right, across the heading");
        bool level = true, unit = true; double minDot = 1, maxDot = -1;
        var rnd = new Random(7);
        for (int i = 0; i < 200; i++)
        {
            var d = EnemySpawnRules.Direction(EnemySpawnRules.Dir.Random, up, hd, rnd.NextDouble());
            if (Math.Abs(Vector3D.Dot(d, up)) > 1e-9) level = false;
            if (Math.Abs(d.Length() - 1) > 1e-9) unit = false;
            minDot = Math.Min(minDot, Vector3D.Dot(d, hd)); maxDot = Math.Max(maxDot, Vector3D.Dot(d, hd));
        }
        Check(level && unit && minDot < -0.9 && maxDot > 0.9, "random: level unit directions all round");
        // at the target's height over the planet, about the distance away
        var dir = EnemySpawnRules.Direction(EnemySpawnRules.Dir.Behind, up, hd, 0);
        var p = EnemySpawnRules.Place(t, c, dir, 2000);
        Check(Math.Abs((p - c).Length() - (t - c).Length()) < 1e-6, "on the planet: at the target's height");
        Check(Math.Abs((p - t).Length() - 2000) < 5, $"on the planet: about the distance away ({(p - t).Length():F1} m)");
        var pf = EnemySpawnRules.Place(t, new Vector3D(double.NaN), dir, 2000);
        Check(Near(pf, t + dir * 2000, 1e-9), "no planet: straight along the direction");
        // out of the ground: a hill under the point raises it to radius + clearance over the surface
        var under = EnemySpawnRules.OverGround(p, c, R + 1450, 20);
        Check(Math.Abs((under - c).Length() - (R + 1450 + 20 + EnemySpawnRules.MinClearance)) < 1e-6, "a hill under it: lifted to its radius + 100 m over the ground");
        Check(Near(EnemySpawnRules.OverGround(p, c, R, 20), p, 1e-9), "ground far below: left where it is");
        Check(Near(EnemySpawnRules.OverGround(p, c, double.NaN, 20), p, 1e-9), "ground not known: left where it is");
        Check(EnemySpawnRules.RaiseStep(20) == 60 && EnemySpawnRules.RaiseStep(0) > 2, "inside a grid: moved up two radii and 20 m a try");
        // a salvo side by side, centred, never overlapping
        int n = EnemySpawnRules.SalvoCount(K.Missile, 4);
        double sum = 0, minGap = double.MaxValue;
        for (int i = 0; i < n; i++) { sum += EnemySpawnRules.SalvoOffset(i, n, 6); if (i > 0) minGap = Math.Min(minGap, EnemySpawnRules.SalvoOffset(i, n, 6) - EnemySpawnRules.SalvoOffset(i - 1, n, 6)); }
        Check(n == 4 && Math.Abs(sum) < 1e-9 && minGap >= 2 * 6 + EnemySpawnRules.SalvoGap - 1e-9, "a salvo of 4: centred, a ship's width and a gap apart");
        Check(EnemySpawnRules.SalvoCount(K.Missile, 0) == 1 && EnemySpawnRules.SalvoCount(K.Missile, 99) == EnemySpawnRules.SalvoMax && EnemySpawnRules.SalvoCount(K.Bogey, 5) == 1, "the salvo clamped 1..8; a bogey is one");
        Check(EnemySpawnRules.SalvoOffset(0, 1, 50) == 0, "one missile: on the line");
    }

    static void Pointing()
    {
        Console.WriteLine("[how it points]");
        var up = new Vector3D(0, 1, 0);
        var from = new Vector3D(2000, 300, 0); var target = new Vector3D(0, 0, 0);
        var mn = EnemySpawnRules.Nose(K.Missile, from, target, up);
        Check(Near(mn, Vector3D.Normalize(target - from), 1e-9), "a missile: straight at the target");
        var bn = EnemySpawnRules.Nose(K.Bogey, from, target, up);
        Check(Math.Abs(Vector3D.Dot(bn, up)) < 1e-9 && Vector3D.Dot(bn, target - from) > 0, "a bogey: level, toward the target");
        // the Autopilot block's front onto the nose, whatever way the block sits in its grid
        var rnd = new Random(3); double worst = 0, worstTop = 0;
        var blockDirs = new[] { Vector3.Forward, Vector3.Backward, Vector3.Up, Vector3.Down, Vector3.Left, Vector3.Right };
        foreach (var nl in blockDirs)
            foreach (var tl in blockDirs)
            {
                if (Math.Abs(Vector3.Dot(nl, tl)) > 0.5f) continue;
                for (int k = 0; k < 10; k++)
                {
                    var nw = Vector3D.Normalize(new Vector3D(rnd.NextDouble() - 0.5, (rnd.NextDouble() - 0.5) * 0.6, rnd.NextDouble() - 0.5));
                    var q = EnemySpawnRules.Aim(nl, tl, nw, up);
                    var got = Vector3D.Transform((Vector3D)nl, q);
                    worst = Math.Max(worst, (got - nw).Length());
                    var top = Vector3D.Transform((Vector3D)tl, q);
                    worstTop = Math.Max(worstTop, Math.Abs(Vector3D.Dot(top, nw)));
                    if (Vector3D.Dot(top, up) < 0) worstTop = 1;   // (its top never below the horizon)
                }
            }
        Check(worst < 1e-4, $"the block's front points along the nose, any block orientation (worst {worst:E1})");
        Check(worstTop < 1e-4, "its top stays square to the nose and up-side");
    }

    static void Speeds()
    {
        Console.WriteLine("[how fast]");
        Check(EnemySpawnRules.Speed(K.Missile, 250, double.NaN, 80, 0) == 250, "the setting wins");
        Check(EnemySpawnRules.Speed(K.Missile, 0, double.NaN, 80, 0) == 80, "missile, 0: the blueprint panel's own speed");
        Check(EnemySpawnRules.Speed(K.Missile, 0, double.NaN, 0, 0) == EnemySpawnRules.MissileFallbackSpeed, "missile, none: 100 m/s");
        Check(EnemySpawnRules.Speed(K.Bogey, 0, 210, 80, 0) == 210, "bogey, 0: its cruise when known");
        Check(EnemySpawnRules.Speed(K.Bogey, 0, double.NaN, 0, 0) == EnemySpawnRules.BogeyFallbackSpeed, "bogey, nothing known: 150 m/s");
        Check(EnemySpawnRules.Speed(K.Missile, 5000, double.NaN, 0, 990) == 990, "at most the cap");
        Check(EnemySpawnRules.WantsCruise(K.Bogey, 0, 0) && !EnemySpawnRules.WantsCruise(K.Bogey, 100, 0) && !EnemySpawnRules.WantsCruise(K.Bogey, 0, 120) && !EnemySpawnRules.WantsCruise(K.Missile, 0, 0),
              "only a bogey with no speed of its own waits for its cruise");
    }

    static void Panel()
    {
        Console.WriteLine("[the panel a spawned block gets]");
        var bp = ApPanel.Defaults;
        bp.Mode = ApMode.HoverHold; bp.Behaviour = ApBehaviour.Circle; bp.TargetSrc = ApTargetSrc.Grid; bp.TargetText = "Bob's Miner";
        bp.TargetFilter = ApTargetFilter.Hostile; bp.Unattended = false; bp.HasHome = true; bp.HomeX = 5; bp.ReturnHome = true;
        bp.RgNavConst = 4.5f; bp.RgLaw = ApGuidanceLaw.AugmentedPN; bp.DfSkill = 0.9f; bp.RangeM = 1500; bp.RgSaved = "old state"; bp.Enemy = false;
        foreach (var k in new[] { K.Missile, K.Bogey })
        {
            var p = EnemySpawnRules.Configure(bp, k);
            Check(p.Mode == ApMode.Engage && p.Behaviour == EnemySpawnRules.Slot(k), $"{k}: Engage with its slot");
            Check(p.TargetSrc == ApTargetSrc.Api && p.TargetText == "" && p.TargetFilter == ApTargetFilter.Everything, $"{k}: the target from the API (the player), every relation taken");
            Check(p.Unattended && !p.HasHome && !p.ReturnHome && p.RgSaved == "" && p.DfSaved == "", $"{k}: kept with nobody seated, no return point, no old slot state");
            Check(p.RgNavConst == 4.5f && p.RgLaw == ApGuidanceLaw.AugmentedPN && p.DfSkill == 0.9f && p.RangeM == 1500, $"{k}: the blueprint's own tactical numbers and slot parameters kept");
            // the saved form round trip: what the block saves and restores is this panel
            string key = AeroBlockSaveForm.Autopilot(in p);
            Check(AeroBlockSaveForm.TryReadAutopilot(key, out var back), $"{k}: the panel's saved key reads back");
            Check(back.Mode == p.Mode && back.Behaviour == p.Behaviour && back.TargetSrc == p.TargetSrc && back.TargetFilter == p.TargetFilter && back.Unattended == p.Unattended
                  && back.RgNavConst == p.RgNavConst && back.RgLaw == p.RgLaw && back.DfSkill == p.DfSkill && back.RangeM == p.RangeM, $"{k}: round trip - the spawned block's panel survives a save");
            Check(AeroBlockSaveForm.Autopilot(in back) == key, $"{k}: round trip - the same key again");
        }
        // a hand-edited, broken blueprint panel is clamped
        var bad = bp; bad.RangeM = float.NaN; bad.Behaviour = (ApBehaviour)200;
        var cp = EnemySpawnRules.Configure(bad, K.Missile);
        Check(float.IsFinite(cp.RangeM) && cp.Behaviour == ApBehaviour.RocketGuidance, "a broken blueprint panel: clamped, the slot still set");
    }

    // ── the Enemy switch's test-spawn path, over a mocked world ──
    sealed class World : IApOwnership
    {
        public Dictionary<long, long> Owners = new();   // grid -> owner
        public long Block = 1, Requester = 7; public ApEnemyWho WhoIs;
        public bool IsEnemy(out bool hasGrid) { hasGrid = Owners.ContainsKey(Block); return hasGrid && Owners[Block] == ApEnemy.HostileNpc; }
        public ApEnemyWho Who(out long requester) { requester = Requester; return WhoIs; }
        public long BlockGrid => Block;
        public void Ship(List<ApGridOwner> into) { foreach (var kv in Owners) into.Add(new ApGridOwner { Grid = kv.Key, Owner = kv.Value }); }
        public bool KnownPlayer(long owner) => owner > 2;
        public bool MakeEnemy(long grid) { Owners[grid] = ApEnemy.HostileNpc; return true; }
        public bool GiveTo(long grid, long owner) { Owners[grid] = owner; return true; }
    }

    static void EnemySwitch()
    {
        Console.WriteLine("[the Enemy switch for a test spawn]");
        // spawned for player 9 while the host (7) is the in-process player and the world's Enemy rule is Off: still turned
        var w = new World { WhoIs = new ApEnemyWho { Owner = false, Creative = false, Admin = false } };
        w.Owners[1] = 9; w.Owners[2] = 9; w.Owners[3] = 9;   // (the main grid, a rotor head, a hinge part)
        var sw = new ApEnemySwitch();
        bool en = false; long prev = 0;
        sw.RequestSpawned();
        Check(sw.Ask == 3, "asked (3: a test spawn)");
        sw.Step(w, ApEnemyRule.Off, ref en, ref prev);
        Check(w.Owners.Values.All(o => o == ApEnemy.HostileNpc) && en && prev == 9 && sw.Flipped == 3, "the whole construct the hostile NPC's - the world's Enemy rule and the asker not judged (the spawn's own rule was)");
        Check(sw.Prev.Count == 3 && sw.Prev.Values.All(o => o == 9), "every grid's owner before kept (the spawning player)");
        // a grid of someone else's in the construct: never taken
        var w2 = new World { WhoIs = default }; w2.Owners[1] = 9; w2.Owners[2] = 5;
        var sw2 = new ApEnemySwitch(); bool en2 = false; long prev2 = 0;
        sw2.RequestSpawned(); sw2.Step(w2, ApEnemyRule.Everyone, ref en2, ref prev2);
        Check(!en2 && w2.Owners[1] == 9 && w2.Owners[2] == 5 && sw2.Text().Contains("belongs to someone else"), "a joined grid of another owner: refused, nothing changed");
        // the panel's own switch is unchanged: the world's rule still holds it
        var w3 = new World { WhoIs = new ApEnemyWho { Owner = true, Creative = true, Admin = true } }; w3.Owners[1] = 7;
        var sw3 = new ApEnemySwitch(); bool en3 = false; long prev3 = 0;
        sw3.Request(true, w3, ApEnemyRule.Off);
        sw3.Step(w3, ApEnemyRule.Off, ref en3, ref prev3);
        Check(!en3 && w3.Owners[1] == 7, "the panel's switch with the world's rule Off: still refused");
        // already an enemy: nothing to do
        var sw4 = new ApEnemySwitch(); bool en4 = true; long prev4 = 9;
        sw4.RequestSpawned(); sw4.Step(w, ApEnemyRule.Off, ref en4, ref prev4);
        Check(en4 && w.Owners.Values.All(o => o == ApEnemy.HostileNpc), "already the hostile NPC's: stays so");
    }

    static void Cleanup()
    {
        Console.WriteLine("[cleanup]");
        long f = 1000;
        Check(!EnemySpawnRules.Expired(0, 10 * 60 * f - 1, 10, f) && EnemySpawnRules.Expired(0, 10 * 60 * f, 10, f), "removed after its minutes, not before");
        Check(!EnemySpawnRules.Expired(0, long.MaxValue / 2, 0, f) && !EnemySpawnRules.Expired(0, long.MaxValue / 2, double.NaN, f), "0: never");
    }

    // ── the engine side ──
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Scripts", "Simulation", "Launch", "EnemySpawn.cs"))) d = d.Parent;
        return d?.FullName ?? throw new Exception("no Scripts/Simulation/Launch/EnemySpawn.cs above " + AppContext.BaseDirectory);
    }
    /// <summary>The default keys' letters bound by no vanilla control (the installed game's ActionControlMapping.def): a vanilla binding
    /// fires whatever other modifiers are held (VRage.Input CompositeInputControl: its own modifiers only) - Ctrl+M opened the map
    /// (M), Ctrl+B the blueprints. Skipped (said) without the game installed.</summary>
    static void VanillaClash(string cl)
    {
        string def = Path.Combine(Environment.GetEnvironmentVariable("SE2_GAME_DATA") ?? @"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers2\GameData",
            "Vanilla", "Content", "MainMenuData", "Input", "ActionControlMapping.def");
        if (!File.Exists(def)) { Console.WriteLine("  (skipped: no " + def + ")"); return; }
        var used = new HashSet<string>(Regex.Matches(File.ReadAllText(def), @"""Keyboard::([A-Z])""").Select(m => m.Groups[1].Value));
        var m = Regex.Match(cl, @"public static int MissileKey = (\d+), BogeyKey = (\d+), DespawnKey = (\d+)");
        foreach (int i in new[] { 1, 2, 3 })
        {
            int k = m.Success ? int.Parse(m.Groups[i].Value) : 0;
            string letter = k >= 1 && k <= 26 ? ((char)('A' + k - 1)).ToString() : "?";
            Check(letter != "?" && !used.Contains(letter), $"the default spawn key {letter}: no vanilla control binds it (vanilla binds {string.Concat(used.OrderBy(x => x))})");
        }
    }

    static string Src(params string[] p) => File.ReadAllText(Path.Combine(new[] { Root(), "Scripts", "Simulation" }.Concat(p).ToArray()));
    static string Body(string src, string start)
    {
        int i = src.IndexOf(start, StringComparison.Ordinal);
        if (i < 0) return "";
        int b = src.IndexOf('{', i), depth = 0;
        for (int k = b; k < src.Length; k++) { if (src[k] == '{') depth++; else if (src[k] == '}' && --depth == 0) return src.Substring(i, k - i + 1); }
        return "";
    }

    static void SourceScan()
    {
        Console.WriteLine("[the engine side]");
        string cl = Src("Launch", "EnemySpawn.cs"), srv = Src("Launch", "EnemySpawn.Server.cs");
        Check(Regex.IsMatch(cl, @"public static int MissileKey = 9, BogeyKey = 15, DespawnKey = 14, Modifier = 4;"), "the default keys: Ctrl+Shift+I, Ctrl+Shift+O, Ctrl+Shift+N");
        VanillaClash(cl);
        Check(Regex.IsMatch(cl, @"public static string MissileBlueprint = ""Missile"", BogeyBlueprint = ""Bogey"";") && Regex.IsMatch(cl, @"public static double MissileDistance = 2000, BogeyDistance = 4000;"), "the default blueprints and distances");
        string ask = Body(cl, "internal sealed class Ask");
        Check(ask.Length > 0 && !Regex.IsMatch(ask, @"\bEntity\s+\w+\s*[;,]") && ask.Contains("IdentityId Who"), "the ask names only who asks, the blueprint's builders and the choices (no client entity)");
        string tick = Body(cl, "static void Tick(");
        Check(tick.Contains("QuickLaunch.TopScreenNeedsInput(session)") && tick.Contains("now - _lastAsk < f"), "the keys: never while typing, one ask a second");
        Check(Regex.Matches(cl, @"edge\.Step\(down, held\)").Count == 1 && cl.Contains("QuickLaunchRules.KeyEdge"), "the keys: the rules' KeyEdge (one press per press)");
        Check(cl.Contains("TryLoadAsync(new BlueprintId(") && cl.Contains("TryLoadGridAsync("), "the blueprint: the game's own blueprint service (as its paste tool)");
        string serve = Body(srv, "static void ServeAsk(");
        Check(serve.Contains("QuickLaunchRules.Fresh(ask.At,") && serve.Contains("EnemySpawnRules.Check(rule,"), "the server: a fresh ask, judged by the rules");
        Check(serve.Contains("IsCreative(ch)") && serve.Contains("Autopilot.Enabled, Autopilot.Unattended") && serve.Contains("ApPilot.SlotImplemented("), "the server's own facts: creative of its character, the world's autopilot, the slot");
        Check(serve.Contains("PrepareForSpawn(owner, ref bundle)") && serve.Contains("TransformBundle(") && serve.Contains("CloneBundle()"), "the paste's own preparation; each salvo member its own copy");
        Check(srv.Contains("SpawnBundleAsync(") && !Regex.IsMatch(srv, @"\.SpawnBundle\(|\.SpawnEntity\("), "spawned staged and async (never synchronously - DevTestShip's crash)");
        Check(serve.Contains("Occupied(") && serve.Contains("OverGround(") && serve.Contains("_height.Surface("), "out of the ground and out of grids");
        string step = Body(srv, "static bool Step(");
        Check(step.Contains("AeroApi.SetAutopilotTarget(g, p.Target)") && step.Contains("EnemySpawnRules.Configure(a.Panel, p.Kind)") && step.Contains("a.AskEnemySpawned()"), "set up: the API target, the panel, the Enemy switch (its own step flips the construct)");
        Check(srv.Contains("DeleteEntity(") && srv.Contains("EnemySpawnRules.IsTagged(name)") && srv.Contains("EnemySpawnRules.Expired("), "cleanup: tagged grids, the timer, the game's delete");
        string ql = Src("Launch", "QuickLaunch.Server.cs"), host = Src("Runtime", "AeroClientHost.cs");
        Check(Body(ql, "private static void QuickLaunchJob(").Contains("EnemySpawn.ServerTick(c)"), "the server step: the planets' teleport-phase job (the server thread)");
        Check(host.Contains("EnemySpawn.ClientTick(any)"), "the keys: the client's session tick");
        string st = Body(srv, "internal static void ServerTick(");
        Check(Regex.IsMatch(st, @"if \(!Asked && _pendingCount == 0 && _trackedCount == 0\) return;"), "nothing to do: three reads, no lock, no allocation");
        Check(Src("Runtime", "AeroWorldSettingsComponent.cs").Contains("EnemySpawn.ResetWorld()"), "a world's end drops its asks, spawns and tracked ships");
        string set = Src("Runtime", "AeroSettings.cs");
        bool reg = true;
        foreach (var n in new[] { "Spawn.Allowed\", \"world\"", "Spawn.DespawnMinutes\", \"world\"", "Spawn.MissileKey\", \"client\"", "Spawn.BogeyKey\", \"client\"", "Spawn.DespawnKey\", \"client\"",
                                  "Spawn.Modifier\", \"client\"", "Spawn.MissileBlueprint\", \"client\"", "Spawn.BogeyBlueprint\", \"client\"", "Spawn.MissileDistance\", \"client\"",
                                  "Spawn.BogeyDistance\", \"client\"", "Spawn.Direction\", \"client\"", "Spawn.MissileSalvo\", \"client\"", "Spawn.MissileSpeed\", \"client\"", "Spawn.BogeySpeed\", \"client\"" })
            if (!set.Contains(n)) { reg = false; Console.WriteLine("    missing " + n); }
        Check(reg, "the settings registered (two world, twelve client)");
        Check(Src("Api", "AeroApi.cs").Contains("public static void RequestTestSpawn(int kind) => EnemySpawn.DevAsk(kind);"), "AeroApi.RequestTestSpawn: the keys' ask");
        // the author's files are never touched by this feature
        Check(!Src("Autopilot", "ApRocketGuidance.cs").Contains("EnemySpawn") && !Src("Autopilot", "ApDogfight.cs").Contains("EnemySpawn"), "the slots' logic knows nothing of the spawns");
    }
}
