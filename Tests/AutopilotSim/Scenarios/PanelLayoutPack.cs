using AeroMod;
using System.IO;
using System.Text.RegularExpressions;

/// <summary>
/// THE AUTOPILOT BLOCK'S PANEL, as a player sees it (the model needs the game, so its SOURCE is read: the member order it hands
/// ModDetailTypeInfo.Build - what the inspector sorts by - and each member's [HideInInspector] / [Button] /
/// [ShowInInspectorWhen] attributes, the switches evaluated with ApPanelLayout as the model does). Mode by mode (2026-10-20: the
/// one Mode dropdown's choices): the status line, the mode and its line, Level Off (the one button, its row label blank), the chosen
/// mode's fields top down, unattended, the safety limits last; no label twice; the toolbar's actions kept (the old method names);
/// the choice onto the saved panel and back; a hint for every dropdown choice. Every mod panel declares the
/// TypeInfoHolder the game's order lookup needs (without it the panel came out shuffled in game, 2026-10-19).
/// </summary>
sealed class PanelLayoutPack : IScenarioPack
{
    public void Register(ScenarioRegistry r)
    {
        r.Add("panel layout: status, the mode dropdown, Level Off, the mode's fields, safety limits last - mode by mode", true, Layout);
        r.Add("panel layout: every mod panel declares its TypeInfoHolder (the game's member order)", true, Holders);
        r.Add("block power: it draws a whole kW (the info pane shows int kW), and flies only built, on and supplied", true, Power);
    }

    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Scripts", "Simulation", "Autopilot", "Game", "AeroAutopilotBlockDetailModel.cs"))) d = d.Parent;
        return d?.FullName;
    }

    sealed class Member { public string Name, Cond, Label = "", Content; public bool Hidden, Button, Labelled, NoToolbar; }

    /// <summary>The model's members in the order given to Build, with their attributes.</summary>
    static List<Member> Read(string src, out List<string> unlisted)
    {
        var b = Regex.Match(src, @"ModDetailTypeInfo\.Build<AeroAutopilotBlockDetailModel>\((.*?)\);", RegexOptions.Singleline);
        var order = Regex.Matches(b.Groups[1].Value, @"nameof\((\w+)\)").Select(m => m.Groups[1].Value).ToList();
        var lines = src.Replace("\r", "").Split('\n');
        var all = new Dictionary<string, Member>();
        for (int i = 0; i < lines.Length; i++)
        {
            var m = Regex.Match(lines[i], @"^(.*?)public (?:void|[\w<>\[\]]+) (\w+)\s*(\(|\{|=>|$)");
            if (!m.Success || lines[i].Contains(" class ") || lines[i].Contains("override")) continue;
            string attrs = m.Groups[1].Value;
            for (int k = i - 1; k >= 0; k--)
            {
                string t = lines[k].Trim();
                if (t.StartsWith("[") || t.StartsWith("///")) attrs = t + " " + attrs; else break;
            }
            var c = Regex.Match(attrs, @"ShowInInspectorWhen\(""(\w+)""");
            all[m.Groups[2].Value] = new Member
            {
                Name = m.Groups[2].Value, Button = attrs.Contains("[Button"),
                // (the game reads [HideInInspector] on a property only: every public [Button] method is shown unless its
                //  [ShowInInspectorWhen] is false - TerminalInspectorViewModel.InjectInspectable; in game 2026-10-20)
                Hidden = attrs.Contains("[HideInInspector]") && !attrs.Contains("[Button"),
                Labelled = attrs.Contains("[Label("), Cond = c.Success ? c.Groups[1].Value : null,
                Label = Regex.Match(attrs, @"\[Label\(""([^""]*)""\)\]").Groups[1].Value,
                Content = Regex.Match(attrs, @"\[Button\(""([^""]*)""\)\]") is var bc && bc.Success ? bc.Groups[1].Value : null,
                NoToolbar = attrs.Contains("[NoToolbarAction]"),
            };
        }
        unlisted = all.Values.Where(x => x.Labelled && !x.Hidden && !order.Contains(x.Name)).Select(x => x.Name).ToList();
        return order.Select(n => all.TryGetValue(n, out var x) ? x : new Member { Name = n + "?" }).ToList();
    }

    static List<string> Visible(List<Member> ms, in ApPanel p, ApEnemyRule rule, bool enemy, List<string> unknown)
    {
        var v = new List<string>();
        foreach (var m in ms)
        {
            if (m.Hidden) continue;
            bool show = true;
            if (m.Cond != null) { show = ApPanelLayout.Cond(m.Cond, in p, rule, enemy, out bool known); if (!known) unknown.Add(m.Cond); }
            if (show) v.Add(m.Name);
        }
        return v;
    }

    static void Layout()
    {
        string root = Root();
        Sim.Check(root != null, "panel layout: the mod's sources found from the test's folder");
        if (root == null) return;
        string src = File.ReadAllText(Path.Combine(root, "Scripts", "Simulation", "Autopilot", "Game", "AeroAutopilotBlockDetailModel.cs"));
        var ms = Read(src, out var unlisted);
        Sim.Check(ms.Count > 40 && ms.All(m => !m.Name.EndsWith("?")), $"panel layout: every name in the model's order is a member of it ({string.Join(", ", ms.Where(m => m.Name.EndsWith("?")).Select(m => m.Name))})");
        Sim.Check(unlisted.Count == 0, $"panel layout: every shown member (labelled, not hidden) is in the order - an unlisted button falls to the bottom ({string.Join(", ", unlisted)})");
        // (2026-10-20: ONE button on the panel - Level Off, its row label blank: the game puts a button's [Label] beside it and its
        //  content on it, the old panel said every mode twice; the modes are one dropdown; the toolbar's actions never shown)
        var buttons = ms.Where(m => m.Button && m.Cond != "ShowNever").Select(m => m.Name).ToArray();
        Sim.Check(buttons.SequenceEqual(new[] { "LevelOffNow" }), $"panel layout: the one button on the panel is Level Off ({string.Join(", ", buttons)})");
        var lo = ms.FirstOrDefault(m => m.Name == "LevelOffNow");
        Sim.Check(lo != null && lo.Label == " " && lo.Content == "Level Off" && lo.NoToolbar, "panel layout: Level Off - a blank row label, 'Level Off' on the button, not a toolbar action itself (the toolbar's is LevelOff)");
        // (the toolbar keeps every mode: the old panel's method names - what saved toolbars hold - and the new ones, never shown)
        string[] actions = { "HoverHold", "HeightHold", "FlightHold", "Navigate", "Engage", "LevelOff", "Return", "AutopilotOff",
            "ToggleHoverHold", "ToggleHeightHold", "ToggleFlightHold", "ToggleNavigate", "ToggleEngage", "ReturnHome", "FollowShip", "ToggleFollowShip" };
        var missing = actions.Where(a => !ms.Any(m => m.Name == a && m.Button && m.Cond == "ShowNever" && !m.NoToolbar && m.Label.Trim().Length > 0)).ToList();
        Sim.Check(missing.Count == 0, $"panel layout: every mode is a toolbar action with a label, off the panel ({string.Join(", ", missing)})");
        var acts = ms.Where(m => actions.Contains(m.Name)).Select(m => m.Label).ToList();
        Sim.Check(acts.Distinct().Count() == acts.Count, $"panel layout: the toolbar actions' labels differ ({string.Join(", ", acts)})");

        string[] head = { "Status", "Mode", "ModeHint", "LevelOffNow" };
        string[] Tail(bool hover, bool avoid) => new[] { "Unattended", "Protection", "GLimit", "AoaMargin" }
            .Concat(hover ? new[] { "FloorM" } : Array.Empty<string>()).Concat(new[] { "LookAhead", "AvoidOn" })
            .Concat(avoid ? new[] { "AvoidS" } : Array.Empty<string>()).ToArray();
        string[] pilot = { "Style", "StyleHint" }, holds = { "AltOn", "AltM", "AltOverGround", "VsMs", "SpeedOn", "SpeedMs", "BankDeg", "MinAglM" };
        string[] attackHolds = holds.Where(h => h != "AltOverGround").ToArray();
        string[] target = { "TargetSrc", "TargetHint", "TargetText" };

        var p = ApPanel.Defaults;
        p.AltOn = p.SpeedOn = p.HeadingOn = true; p.Facing = ApFacing.Nose; p.AvoidOn = true;
        p.Behaviour = ApBehaviour.AttackRun; p.TargetSrc = ApTargetSrc.Gps;
        var cases = new (string name, ApModeChoice mode, bool enemy, string[] want)[]
        {
            ("Off", ApModeChoice.Off, false, head.Concat(Tail(false, true)).ToArray()),
            ("Hover", ApModeChoice.Hover, false, head.Concat(new[] { "HeightM", "OverGround", "HoverSpeed" }).Concat(Tail(true, true)).ToArray()),
            ("Hold height", ApModeChoice.HoldHeight, false, head.Concat(new[] { "HeightM", "OverGround", "HoverSpeed" }).Concat(Tail(true, true)).ToArray()),
            ("Cruise", ApModeChoice.Cruise, false, head.Concat(pilot).Concat(new[] { "HeadingOn", "HeadingDeg" }).Concat(holds).Concat(Tail(false, true)).ToArray()),
            ("Follow route", ApModeChoice.FollowRoute, false, head.Concat(pilot).Concat(new[] { "Route", "RouteLoop", "LoiterM" }).Concat(holds).Concat(Tail(false, true)).ToArray()),
            ("Return home", ApModeChoice.ReturnHome, false, head.Concat(pilot).Concat(new[] { "LoiterM" }).Concat(holds).Concat(Tail(false, true)).ToArray()),
            ("Follow ship", ApModeChoice.FollowShip, false, head.Concat(pilot).Concat(target).Concat(new[] { "FmRightM", "FmUpM", "FmBackM", "FmCatchUpMs", "BankDeg", "MinAglM" })
                .Concat(Tail(false, true)).ToArray()),
            ("Attack", ApModeChoice.Attack, false, head.Concat(pilot).Concat(target).Concat(new[] { "TargetFilter", "AttackMove", "MoveHint", "RangeM", "AbortM", "RerunS", "AltOffsetM",
                "Evasive", "Aggression", "Facing", "FacingHint", "AimPriority", "MuzzleSpeed", "ConeDeg", "DeckM", "CeilingM", "Enemy", "EnemyText", "Route", "RouteLoop", "LoiterM" })
                .Concat(attackHolds).Concat(Tail(false, true)).ToArray()),
            ("Off, an enemy ship", ApModeChoice.Off, true, head.Concat(new[] { "Enemy", "EnemyText" }).Concat(Tail(false, true)).ToArray()),
        };
        var unknown = new List<string>();
        foreach (var c in cases)
        {
            var q = p; ApPanel.Choose(ref q, c.mode); q = q.Clamped();
            var v = Visible(ms, in q, ApEnemyRule.CreativeOrAdmin, c.enemy, unknown);
            bool ok = v.SequenceEqual(c.want);
            Sim.Check(ok, $"panel layout, {c.name}: {string.Join(" / ", v)}" + (ok ? "" : $"\n      wanted: {string.Join(" / ", c.want)}"));
            // (no label said twice on a screen: the blank rows aside - the dropdowns' lines and the button's - every row's own words)
            var labels = v.Select(n => ms.First(m => m.Name == n).Label).Where(l => l.Trim().Length > 0).ToList();
            var twice = labels.GroupBy(l => l).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Sim.Check(twice.Count == 0, $"panel layout, {c.name}: no label twice ({string.Join(", ", twice)})");
        }
        // (the holds off: their numbers go; avoidance off: its look-ahead goes; a facing other than the nose: no guns' numbers; the way
        //  it flies: no aim priority either; the targeting: no target text; the enemy switch not allowed)
        {
            var q = p; ApPanel.Choose(ref q, ApModeChoice.Cruise); q.AltOn = q.SpeedOn = q.HeadingOn = false; q.AvoidOn = false;
            var v = Visible(ms, in q, ApEnemyRule.CreativeOrAdmin, false, unknown);
            Sim.Check(!v.Contains("AltM") && !v.Contains("AltOverGround") && !v.Contains("SpeedMs") && !v.Contains("HeadingDeg") && !v.Contains("AvoidS") && v.Contains("AltOn"),
                $"panel layout: holds off - only their switches ({string.Join(" / ", v)})");
            q = p; ApPanel.Choose(ref q, ApModeChoice.Attack); q.Behaviour = ApBehaviour.Circle; q.Facing = ApFacing.SideLeft; q.TargetSrc = ApTargetSrc.Targeting;
            v = Visible(ms, in q, ApEnemyRule.Off, false, unknown);
            Sim.Check(v.Contains("AimPriority") && !v.Contains("MuzzleSpeed") && !v.Contains("ConeDeg") && !v.Contains("AbortM") && !v.Contains("TargetText") && !v.Contains("Enemy"),
                $"panel layout: circle it with a side, by the locked target, the enemy switch not allowed ({string.Join(" / ", v)})");
            q.Facing = ApFacing.Travel;
            v = Visible(ms, in q, ApEnemyRule.Off, false, unknown);
            Sim.Check(v.Contains("Facing") && !v.Contains("AimPriority") && !v.Contains("MuzzleSpeed"), $"panel layout: facing the way it flies - no aim priority, no guns ({string.Join(" / ", v)})");
        }
        // (the combat slots' own values; the built-in tactics, the band, the filter and the facing only where they mean something)
        {
            string[] atkHead = head.Concat(pilot).Concat(target).Concat(new[] { "TargetFilter", "AttackMove", "MoveHint" }).ToArray();
            string[] atkTail = new[] { "Enemy", "EnemyText", "Route", "RouteLoop", "LoiterM" }.Concat(attackHolds).Concat(Tail(false, true)).ToArray();
            var slots = new (ApBehaviour b, string[] mid)[]
            {
                (ApBehaviour.Dogfight, new[] { "Aggression", "DfFight", "DfHeadOn", "DfRespectGuns", "DfSkill", "DfBingoFuelPct", "DfBingoDamagePct",
                    "Facing", "FacingHint", "AimPriority", "MuzzleSpeed", "ConeDeg", "DeckM", "CeilingM" }),
                (ApBehaviour.RocketGuidance, new[] { "RgLaw", "RgNavConst", "RgTargetAccelGain", "RgMaxLatG", "RgGravityComp", "RgArmM", "RgTimeoutS" }),
            };
            foreach (var (b, mid) in slots)
            {
                var q = p; ApPanel.Choose(ref q, ApModeChoice.Attack); q.Behaviour = b; q = q.Clamped();
                var want = atkHead.Concat(mid).Concat(atkTail).ToArray();
                var v = Visible(ms, in q, ApEnemyRule.CreativeOrAdmin, false, unknown);
                bool ok = v.SequenceEqual(want);
                Sim.Check(ok, $"panel layout, Attack {b}: {string.Join(" / ", v)}" + (ok ? "" : $"\n      wanted: {string.Join(" / ", want)}"));
            }
            // (a slot's values never show in another mode or for another movement; the formation's only in Follow ship)
            string[] own = { "DfFight", "DfHeadOn", "DfRespectGuns", "DfSkill", "DfBingoFuelPct", "DfBingoDamagePct", "RgLaw", "RgNavConst", "RgTargetAccelGain", "RgMaxLatG",
                "RgGravityComp", "RgArmM", "RgTimeoutS", "FmRightM", "FmUpM", "FmBackM", "FmCatchUpMs" };
            bool leak = false;
            foreach (ApModeChoice c in Enum.GetValues(typeof(ApModeChoice)))
                foreach (ApMove mv in Enum.GetValues(typeof(ApMove)))
                {
                    var q = p; q.Behaviour = ApPanel.BhOf(mv); ApPanel.Choose(ref q, c); q = q.Clamped();
                    var v = Visible(ms, in q, ApEnemyRule.CreativeOrAdmin, false, unknown);
                    foreach (var n in own)
                    {
                        bool may = n.StartsWith("Fm") ? c == ApModeChoice.FollowShip
                            : c == ApModeChoice.Attack && (n.StartsWith("Df") ? mv == ApMove.Dogfight : mv == ApMove.Rocket);
                        if (v.Contains(n) != may) { leak = true; Sim.Note($"panel layout: {n} {(may ? "missing" : "shown")} in {c} / {mv}"); }
                    }
                }
            Sim.Check(!leak, "panel layout: each slot's values show exactly for that movement in Attack, the formation's exactly in Follow ship");
        }
        // (the mode dropdown onto the saved panel and back: every choice from every other; Follow ship then Attack gives the movement back)
        {
            bool round = true;
            foreach (ApModeChoice from in Enum.GetValues(typeof(ApModeChoice)))
                foreach (ApModeChoice to in Enum.GetValues(typeof(ApModeChoice)))
                {
                    var q = p; ApPanel.Choose(ref q, from); q = q.Clamped(); ApPanel.Choose(ref q, to); q = q.Clamped();
                    if (ApPanel.ChoiceOf(in q) != to) { round = false; Sim.Note($"panel mode: {from} then {to} reads {ApPanel.ChoiceOf(in q)}"); }
                }
            Sim.Check(round, "panel mode: every choice reads back as chosen, from every other");
            var a = p; a.Behaviour = ApBehaviour.PursuitLag; ApPanel.Choose(ref a, ApModeChoice.Attack); a = a.Clamped();
            ApPanel.Choose(ref a, ApModeChoice.FollowShip); a = a.Clamped();
            bool fs = a.Mode == ApMode.Engage && a.Behaviour == ApBehaviour.Formation;
            ApPanel.Choose(ref a, ApModeChoice.Attack); a = a.Clamped();
            Sim.Check(fs && a.Behaviour == ApBehaviour.PursuitLag && !a.ReturnHome, $"panel mode: Follow ship is the formation; Attack again gives the chase back ({a.Behaviour})");
            var h = p; ApPanel.Choose(ref h, ApModeChoice.ReturnHome); h = h.Clamped();
            Sim.Check(h.Mode == ApMode.Navigate && h.ReturnHome, "panel mode: Return home is the route's return");
            ApPanel.Choose(ref h, ApModeChoice.Cruise);
            Sim.Check(!h.ReturnHome && h.Mode == ApMode.FlightHold, "panel mode: another mode takes the return back");
        }
        Sim.Check(unknown.Count == 0, $"panel layout: every [ShowInInspectorWhen] switch is one ApPanelLayout knows ({string.Join(", ", unknown.Distinct())})");
        // (each dropdown's line: one for every choice, short; no tooltip texts - in game a row's tooltip is its label)
        {
            bool lines = true;
            foreach (ApModeChoice c in Enum.GetValues(typeof(ApModeChoice))) lines &= Short(ApPanelLayout.ModeHint(c));
            foreach (ApStyle c in Enum.GetValues(typeof(ApStyle))) lines &= Short(ApPanelLayout.StyleHint(c));
            foreach (ApMove c in Enum.GetValues(typeof(ApMove))) lines &= Short(ApPanelLayout.MoveHint(c));
            foreach (ApFacing c in Enum.GetValues(typeof(ApFacing))) lines &= Short(ApPanelLayout.FacingHint(c));
            foreach (ApTargetSrc c in Enum.GetValues(typeof(ApTargetSrc))) lines &= Short(ApPanelLayout.TargetHint(c));
            Sim.Check(lines, "panel hints: every choice of every dropdown has its own one-line hint (under 80 characters)");
            // (no tooltip texts: in game a row's tooltip is always its label - the control panel's style binds ToolTip.Tip to
            //  Label; a member's documentation is editor-only. The hint rows explain the choices.)
            Sim.Check(!Regex.IsMatch(src, @"Tips\(\)|MembersDocumentation") && !Regex.IsMatch(File.ReadAllText(Path.Combine(root, "Scripts", "Simulation", "Blocks", "ModDetailTypeInfo.cs")), @"IReadOnlyDictionary<string, string> tips"),
                "panel hints: no tooltip texts handed to the panel (dead in game; the hint rows explain)");
        }
    }

    static readonly HashSet<string> _hintsSeen = new();
    static bool Short(string h) => !string.IsNullOrWhiteSpace(h) && h.Length < 80 && _hintsSeen.Add(h);

    static void Holders()
    {
        string root = Root();
        if (root == null) { Sim.Check(false, "panel holders: the mod's sources found"); return; }
        int n = 0;
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "Scripts"), "*.cs", SearchOption.AllDirectories))
        {
            string s = File.ReadAllText(f);
            int builds = Regex.Matches(s, @"ModDetailTypeInfo\.Build<").Count;
            if (builds == 0 || f.EndsWith("ModDetailTypeInfo.cs")) continue;
            int holders = Regex.Matches(s, @"public static class TypeInfoHolder \{ public static readonly [\w.]*AccessibleTypeInfo Value = _typeInfo; \}").Count;
            n += builds;
            Sim.Check(holders == builds, $"panel holders: {Path.GetFileName(f)} - {builds} model(s), {holders} TypeInfoHolder(s)");
        }
        Sim.Check(n >= 6, $"panel holders: the mod's panels found ({n})");
    }

    static void Power()
    {
        // (the ready rule the block's Working uses: unpowered, off or damaged - ApBlockLink lets go: BlockPack's "block dark")
        Sim.Check(ApBlockLink.BlockReady(true, true, true, true, false) && !ApBlockLink.BlockReady(true, true, true, false, false)
            && !ApBlockLink.BlockReady(true, true, false, true, false) && !ApBlockLink.BlockReady(true, false, true, true, false)
            && ApBlockLink.BlockReady(true, true, true, false, true) && !ApBlockLink.BlockReady(true, true, false, false, true),
            "block power: ready = built and on and supplied (the harness's ignore-power skips only the supply)");
        string root = Root();
        if (root == null) { Sim.Check(false, "block power: the mod's sources found"); return; }
        foreach (var size in new[] { "Large", "Small" })
        {
            string f = Path.Combine(root, "Assets", "Aero", "Autopilot", size, $"Autopilot_{size}_PowerableBlockDefinition.partialdef");
            var m = File.Exists(f) ? Regex.Match(File.ReadAllText(f), @"""ConsumedResource""\s*:\s*\{[^}]*""Amount""\s*:\s*([0-9.]+)") : Match.Empty;
            float kw = m.Success ? float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : -1f;
            Sim.Check(kw >= 1f && kw == MathF.Floor(kw), $"block power: the {size} autopilot draws {kw} kW - a whole kW, so the game's panel shows it (0.05 read as 0kW)");
        }
    }
}
