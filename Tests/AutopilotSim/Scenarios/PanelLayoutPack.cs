using AeroMod;
using System.IO;
using System.Text.RegularExpressions;

/// <summary>
/// THE AUTOPILOT BLOCK'S PANEL, as a player sees it (the model needs the game, so its SOURCE is read: the member order it hands
/// ModDetailTypeInfo.Build - what the inspector sorts by - and each member's [HideInInspector] / [Button] /
/// [ShowInInspectorWhen] attributes, the switches evaluated with ApPanelLayout as the model does). Mode by mode: the status line,
/// the mode buttons, the chosen mode's fields top down, unattended, the protection last. Every mod panel declares the
/// TypeInfoHolder the game's order lookup needs (without it the panel came out shuffled in game, 2026-10-19).
/// </summary>
sealed class PanelLayoutPack : IScenarioPack
{
    public void Register(ScenarioRegistry r)
    {
        r.Add("panel layout: status, mode buttons, the mode's fields, protection last - mode by mode", true, Layout);
        r.Add("panel layout: every mod panel declares its TypeInfoHolder (the game's member order)", true, Holders);
        r.Add("block power: it draws a whole kW (the info pane shows int kW), and flies only built, on and supplied", true, Power);
    }

    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Scripts", "Simulation", "Autopilot", "Game", "AeroAutopilotBlockDetailModel.cs"))) d = d.Parent;
        return d?.FullName;
    }

    sealed class Member { public string Name, Cond; public bool Hidden, Button, Labelled; }

    /// <summary>The model's members in the order given to Build, with their attributes.</summary>
    static List<Member> Read(string src, out List<string> unlisted)
    {
        var b = Regex.Match(src, @"ModDetailTypeInfo\.Build<AeroAutopilotBlockDetailModel>\((.*?)\);", RegexOptions.Singleline);
        var order = Regex.Matches(b.Groups[1].Value, @"nameof\((\w+)\)").Select(m => m.Groups[1].Value).ToList();
        var lines = src.Replace("\r", "").Split('\n');
        var all = new Dictionary<string, Member>();
        for (int i = 0; i < lines.Length; i++)
        {
            var m = Regex.Match(lines[i], @"^(.*?)public (?:void|[\w<>\[\]]+) (\w+)\s*(\(|\{|=>)");
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
                Name = m.Groups[2].Value, Hidden = attrs.Contains("[HideInInspector]"), Button = attrs.Contains("[Button"),
                Labelled = attrs.Contains("[Label("), Cond = c.Success ? c.Groups[1].Value : null,
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
        var buttons = ms.Where(m => m.Button && !m.Hidden).Select(m => m.Name).ToArray();
        var wantButtons = new[] { "HoverHold", "HeightHold", "FlightHold", "Navigate", "Engage", "LevelOff", "Return", "AutopilotOff" };
        Sim.Check(buttons.SequenceEqual(wantButtons), $"panel layout: the mode buttons, in order ({string.Join(", ", buttons)})");

        string[] head = new[] { "Status" }.Concat(wantButtons).ToArray();
        string[] Tail(bool hover, bool avoid) => new[] { "Unattended", "Protection", "GLimit", "AoaMargin" }
            .Concat(hover ? new[] { "FloorM" } : Array.Empty<string>()).Concat(new[] { "LookAhead", "AvoidOn" })
            .Concat(avoid ? new[] { "AvoidS" } : Array.Empty<string>()).ToArray();
        string[] pilot = { "Style" }, holds = { "AltOn", "AltM", "AltOverGround", "VsMs", "SpeedOn", "SpeedMs", "BankDeg", "MinAglM" };
        string[] route = { "Route", "RouteLoop", "LoiterM" };

        var p = ApPanel.Defaults;
        p.AltOn = p.SpeedOn = p.HeadingOn = true; p.AimOn = true; p.AvoidOn = true;
        p.Behaviour = ApBehaviour.AttackRun; p.TargetSrc = ApTargetSrc.Gps;
        var cases = new (string name, ApMode mode, bool enemy, string[] want)[]
        {
            ("Off", ApMode.Off, false, head.Concat(Tail(false, true)).ToArray()),
            ("Hover hold", ApMode.HoverHold, false, head.Concat(new[] { "HeightM", "OverGround", "HoverSpeed" }).Concat(Tail(true, true)).ToArray()),
            ("Height hold", ApMode.HeightHold, false, head.Concat(new[] { "HeightM", "OverGround", "HoverSpeed" }).Concat(Tail(true, true)).ToArray()),
            ("Flight hold", ApMode.FlightHold, false, head.Concat(pilot).Concat(new[] { "HeadingOn", "HeadingDeg" }).Concat(holds).Concat(Tail(false, true)).ToArray()),
            ("Navigate", ApMode.Navigate, false, head.Concat(pilot).Concat(holds).Concat(route).Concat(Tail(false, true)).ToArray()),
            ("Engage", ApMode.Engage, false, head.Concat(pilot).Concat(holds.Where(h => h != "AltOverGround")).Concat(route)
                .Concat(new[] { "Behaviour", "RangeM", "AbortM", "RerunS", "AltOffsetM", "Evasive", "Aggression", "DeckM", "CeilingM", "TargetSrc", "TargetText", "TargetFilter",
                    "AimOn", "MuzzleSpeed", "ConeDeg", "Enemy", "EnemyText" }).Concat(Tail(false, true)).ToArray()),
            ("Off, an enemy ship", ApMode.Off, true, head.Concat(new[] { "Enemy", "EnemyText" }).Concat(Tail(false, true)).ToArray()),
        };
        var unknown = new List<string>();
        foreach (var c in cases)
        {
            var q = p; q.Mode = c.mode;
            var v = Visible(ms, in q, ApEnemyRule.CreativeOrAdmin, c.enemy, unknown);
            bool ok = v.SequenceEqual(c.want);
            Sim.Check(ok, $"panel layout, {c.name}: {string.Join(" / ", v)}" + (ok ? "" : $"\n      wanted: {string.Join(" / ", c.want)}"));
        }
        // (the holds off: their numbers go; avoidance off: its look-ahead goes; a broadside: its angle, no abort / re-run)
        {
            var q = p; q.Mode = ApMode.FlightHold; q.AltOn = q.SpeedOn = q.HeadingOn = false; q.AvoidOn = false;
            var v = Visible(ms, in q, ApEnemyRule.CreativeOrAdmin, false, unknown);
            Sim.Check(!v.Contains("AltM") && !v.Contains("AltOverGround") && !v.Contains("SpeedMs") && !v.Contains("HeadingDeg") && !v.Contains("AvoidS") && v.Contains("AltOn"),
                $"panel layout: holds off - only their switches ({string.Join(" / ", v)})");
            q = p; q.Mode = ApMode.Engage; q.Behaviour = ApBehaviour.Broadside; q.TargetSrc = ApTargetSrc.Targeting;
            v = Visible(ms, in q, ApEnemyRule.Off, false, unknown);
            Sim.Check(v.Contains("AngleDeg") && !v.Contains("AbortM") && !v.Contains("TargetText") && !v.Contains("Enemy"),
                $"panel layout: a broadside by the targeting, the enemy switch not allowed ({string.Join(" / ", v)})");
        }
        // (2026-10-19: per behaviour - the combat slots' own values, the formation's slot; the built-in tactics, the band, the
        //  filter and the aim only where they mean something)
        {
            string[] engHead = head.Concat(pilot).Concat(holds.Where(h => h != "AltOverGround")).Concat(route).Concat(new[] { "Behaviour" }).ToArray();
            var slots = new (ApBehaviour b, string[] mid)[]
            {
                (ApBehaviour.Dogfight, new[] { "Aggression", "DfFight", "DfHeadOn", "DfRespectGuns", "DfSkill", "DfBingoFuelPct", "DfBingoDamagePct", "DeckM", "CeilingM",
                    "TargetSrc", "TargetText", "TargetFilter", "AimOn", "MuzzleSpeed", "ConeDeg" }),
                (ApBehaviour.RocketGuidance, new[] { "RgLaw", "RgNavConst", "RgTargetAccelGain", "RgMaxLatG", "RgGravityComp", "RgArmM", "RgTimeoutS",
                    "TargetSrc", "TargetText", "TargetFilter" }),
                (ApBehaviour.Formation, new[] { "FmRightM", "FmUpM", "FmBackM", "FmCatchUpMs", "TargetSrc", "TargetText" }),
            };
            foreach (var (b, mid) in slots)
            {
                var q = p; q.Mode = ApMode.Engage; q.Behaviour = b;
                var want = engHead.Concat(mid).Concat(new[] { "Enemy", "EnemyText" }).Concat(Tail(false, true)).ToArray();
                var v = Visible(ms, in q, ApEnemyRule.CreativeOrAdmin, false, unknown);
                bool ok = v.SequenceEqual(want);
                Sim.Check(ok, $"panel layout, Engage {b}: {string.Join(" / ", v)}" + (ok ? "" : $"\n      wanted: {string.Join(" / ", want)}"));
            }
            // (a slot's values never show in another mode or for another behaviour)
            string[] own = { "DfFight", "DfHeadOn", "DfRespectGuns", "DfSkill", "DfBingoFuelPct", "DfBingoDamagePct", "RgLaw", "RgNavConst", "RgTargetAccelGain", "RgMaxLatG",
                "RgGravityComp", "RgArmM", "RgTimeoutS", "FmRightM", "FmUpM", "FmBackM", "FmCatchUpMs" };
            bool leak = false;
            foreach (ApMode m in Enum.GetValues(typeof(ApMode)))
                foreach (ApBehaviour b in Enum.GetValues(typeof(ApBehaviour)))
                {
                    var q = p; q.Mode = m; q.Behaviour = b;
                    var v = Visible(ms, in q, ApEnemyRule.CreativeOrAdmin, false, unknown);
                    string pre = b == ApBehaviour.Dogfight ? "Df" : b == ApBehaviour.RocketGuidance ? "Rg" : b == ApBehaviour.Formation ? "Fm" : "--";
                    foreach (var n in own) if (v.Contains(n) && (m != ApMode.Engage || !n.StartsWith(pre))) { leak = true; Sim.Note($"panel layout: {n} shown in {m} / {b}"); }
                }
            Sim.Check(!leak, "panel layout: each slot's and the formation's values show only for that behaviour in Engage");
        }
        Sim.Check(unknown.Count == 0, $"panel layout: every [ShowInInspectorWhen] switch is one ApPanelLayout knows ({string.Join(", ", unknown.Distinct())})");
    }

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
