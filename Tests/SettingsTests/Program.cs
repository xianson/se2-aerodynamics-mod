// SettingsTests - the settings registry's offline checks (dotnet run -c Release; exit 0 = all pass). The REAL
// Runtime/AeroSettings.cs (the registry) and AeroSettings.Store.cs (set, text, host file, groups) over the real structure
// model; the game-bound classes' fields come from Stubs.cs, which Honest() checks against the mod's own declarations.
// 1. HONEST STUBS: every stub member is declared in the mod with the same type and initializer (so every Default here
//    is the game's).
// 2. THE SHAPE: Setting carries the UI fields with the names and types OrbitalSettings.Setting uses (settings DESIGN.md W1).
// 3. EVERY SETTING: a label, a known group (one scope per group), a tier; Min <= Default <= Max; an int / bool default
//    and step whole; choices only on an int, one per value; its rule (Check) holds at the defaults.
// 4. TEXT: Text -> Apply gives back every value (all values moved off their defaults first), and the save's text too.
// 5. GROUPS: ResetGroup restores a group and nothing else; an unknown group says so.
// 6. AUTHORITY: a name the host file sets is forced - Set refuses it, the save's text and the file still apply;
//    CanEditWorld false refuses world sets (and a world group's reset), not client ones; WorldUnsaved follows a set.
// 7. DENYLIST: no Dev* / Test* / Debug* / Probe* / Trace* / *Log setting or field written (registry names, and a scan of
//    every setter in the registry's source).
// 8. PERSISTENCE (2026-10-15): a field written directly (a harness's aeroset, a dev override) is never what the files or
//    the world save write, even after an unrelated Set rewrites them; a Set of that same value makes it the player's.
// 9. WORLD UNLOAD: the host file's forced names do not carry over to the next world.
// 10. NO CODE WRITES: outside the harness (Dev/, harness verb switches) no code writes a setting's field - a runtime state
//    ("failed, off for the session") is a flag of its own (the Orbital Mod's ProxyAtmospheres, 2026-10-15).
// 12. FLIGHT ASSISTS (2026-10-19): every default realistic (FlightAssists' Realistic preset); the preset sets its members through
//    the store, is worked out from them (Custom when they match none) and is never saved itself; a forced member keeps its value;
//    a group reset is realistic again.
// 11. FILE POLL (2026-10-15, in a temp folder - never the player's): what the background poll reads is applied only on the
//    next tick-thread Poll (setters touch tick-thread state); a forced poll applies at once; a world unload discards a
//    host-file read still waiting. A Set made while the client file is being written stays marked: the next poll writes
//    it (it was cleared unconditionally and could miss the file).
global using Keen.VRage.Library.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using AeroMod;

static class Program
{
    static int _fails, _checks;
    static void Check(bool ok, string what) { _checks++; if (!ok) { _fails++; Console.WriteLine("FAIL " + what); } else if (Environment.GetEnvironmentVariable("VERBOSE") != null) Console.WriteLine("ok   " + what); }

    static readonly string[] Groups = { "Flight model", "Flight assists", "Structure and damage", "Re-entry heat", "Quick launch", "Autopilot", "Wheels and landing gear", "Test spawn (host)", "Performance (host)", "HUD", "Feel and audio", "Visual effects", "Effects quality", "Controls", "Test spawn" };

    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Scripts", "Simulation", "Runtime", "AeroSettings.cs"))) d = d.Parent;
        return d?.FullName ?? throw new Exception("no Scripts/Simulation/Runtime/AeroSettings.cs above " + AppContext.BaseDirectory);
    }

    static string Norm(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();

    // ── 1. the stubs are the mod's own declarations ──
    static void Honest(string root)
    {
        string sim = Path.Combine(root, "Scripts", "Simulation");
        var real = new Dictionary<string, List<MemberDeclarationSyntax>>();
        foreach (var f in Directory.GetFiles(sim, "*.cs", SearchOption.AllDirectories))
            foreach (var c in CSharpSyntaxTree.ParseText(File.ReadAllText(f)).GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (!real.TryGetValue(c.Identifier.Text, out var list)) real[c.Identifier.Text] = list = new();
                list.AddRange(c.Members);
            }
        var stubs = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "Tests", "SettingsTests", "Stubs.cs"))).GetRoot();
        int n = 0;
        foreach (var c in stubs.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            Check(real.ContainsKey(c.Identifier.Text), $"stub {c.Identifier.Text}: a class of the mod");
            if (!real.TryGetValue(c.Identifier.Text, out var members)) continue;
            foreach (var m in c.Members)
            {
                if (m is FieldDeclarationSyntax fd)
                    foreach (var v in fd.Declaration.Variables)
                    {
                        n++;
                        var hit = members.OfType<FieldDeclarationSyntax>().SelectMany(r => r.Declaration.Variables.Select(rv => (r, rv))).FirstOrDefault(x => x.rv.Identifier.Text == v.Identifier.Text);
                        Check(hit.rv != null, $"stub {c.Identifier.Text}.{v.Identifier.Text}: declared in the mod");
                        if (hit.rv == null) continue;
                        Check(Norm(hit.r.Declaration.Type.ToString()) == Norm(fd.Declaration.Type.ToString()), $"stub {c.Identifier.Text}.{v.Identifier.Text}: type {fd.Declaration.Type} (the mod: {hit.r.Declaration.Type})");
                        Check(Norm(hit.rv.Initializer?.Value.ToString()) == Norm(v.Initializer?.Value.ToString()), $"stub {c.Identifier.Text}.{v.Identifier.Text}: initializer '{v.Initializer?.Value}' (the mod: '{hit.rv.Initializer?.Value}') - update Stubs.cs");
                    }
                else if (m is PropertyDeclarationSyntax pd)
                {
                    n++;
                    var hit = members.OfType<PropertyDeclarationSyntax>().FirstOrDefault(r => r.Identifier.Text == pd.Identifier.Text);
                    Check(hit != null && Norm(hit.Type.ToString()) == Norm(pd.Type.ToString()) && Norm(hit.AccessorList?.ToString()) == Norm(pd.AccessorList?.ToString()),
                        $"stub {c.Identifier.Text}.{pd.Identifier.Text}: the mod's property, word for word");
                }
            }
        }
        Check(n >= 60, $"stubs: {n} members checked");
    }

    static List<AeroSettings.Setting> All() => (List<AeroSettings.Setting>)typeof(AeroSettings).GetField("_all", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

    // ── 2. the shape (the same fields as OrbitalSettings.Setting) ──
    static void Shape()
    {
        var t = typeof(AeroSettings.Setting);
        foreach (var (name, type) in new[] { ("Label", typeof(string)), ("Group", typeof(string)), ("Tier", typeof(AeroSettings.Tier)), ("Unit", typeof(string)), ("Step", typeof(double)), ("Choices", typeof(string[])), ("Order", typeof(int)), ("ClientVisible", typeof(bool)) })
        {
            var f = t.GetField(name);
            Check(f != null && f.FieldType == type, $"shape: Setting.{name} is a public {type.Name}");
        }
        Check(string.Join(",", Enum.GetNames(typeof(AeroSettings.Tier))) == "Basic,Advanced,Hidden", "shape: Tier is Basic, Advanced, Hidden");
    }

    // ── 3. every setting ──
    static void Every()
    {
        var all = All();
        var names = new List<string>();
        Check(AeroSettings.Names(names) == all.Count && all.Count >= 80, $"registry: {all.Count} settings");
        var groupScope = new Dictionary<string, string>();
        int lastGroup = -1;
        for (int i = 0; i < all.Count; i++)
        {
            var s = all[i];
            string n = s.Name;
            Check(names[i] == n && s.Order == i, $"{n}: Order {s.Order} is its place {i} (GetSettings' order is the screen's)");
            Check(!string.IsNullOrWhiteSpace(s.Label), $"{n}: a label");
            Check(!string.IsNullOrWhiteSpace(s.Doc), $"{n}: a description");
            int g = Array.IndexOf(Groups, s.Group);
            Check(g >= 0, $"{n}: group '{s.Group}' is a known one");
            Check(g >= lastGroup, $"{n}: registered in screen order (group '{s.Group}' after '{(lastGroup >= 0 ? Groups[lastGroup] : "-")}')");
            lastGroup = Math.Max(lastGroup, g);
            if (s.Group != null) { if (groupScope.TryGetValue(s.Group, out var sc)) Check(sc == s.Scope, $"{n}: group '{s.Group}' is one scope ({sc})"); else groupScope[s.Group] = s.Scope; }
            Check(Enum.IsDefined(typeof(AeroSettings.Tier), s.Tier), $"{n}: a tier");
            Check(s.Scope == "world" || s.Scope == "client", $"{n}: scope {s.Scope}");
            Check(s.Type == "bool" || s.Type == "int" || s.Type == "float" || s.Type == "text", $"{n}: type {s.Type}");
            if (s.Type == "text")
            {
                // (a text setting - 2026-10-20, the test spawn's blueprint names: no number, its own accessors, never in the menu)
                Check(s.GetText != null && s.SetText != null && s.GetText() == s.DefaultText && s.StoredText == s.DefaultText, $"{n}: a text at its default ('{s.DefaultText}')");
                Check(s.CheckText == null || s.CheckText(s.DefaultText) == null, $"{n}: its text rule holds at the default");
                Check(!AeroSettings.TryGet(n, out _) && !AeroSettings.Set(n, 1, out _), $"{n}: a text has no number (TryGet / Set refuse it)");
                Check(s.Tier == AeroSettings.Tier.Hidden, $"{n}: a text is hidden (the menu edits numbers)");
            }
            Check(s.Min <= s.Default && s.Default <= s.Max, $"{n}: {s.Min} <= default {s.Default} <= {s.Max}");
            if (s.Type != "float") Check(s.Default == Math.Round(s.Default) && s.Step >= 1 && s.Step == Math.Round(s.Step) && (s.Type == "int" || s.Step == 1), $"{n}: a whole default and a whole step ({s.Step})");
            else Check(s.Step > 0 && s.Step <= s.Max - s.Min, $"{n}: a step ({s.Step}) inside its range");
            Check(s.Unit != null, $"{n}: a unit (\"\" for none)");
            if (s.Choices != null)
            {
                Check(s.Type == "int", $"{n}: choices only on an int");
                Check(s.Choices.Length == (int)(s.Max - s.Min) + 1, $"{n}: {s.Choices.Length} choices for {s.Min}..{s.Max}");
                Check(s.Choices.All(c => !string.IsNullOrWhiteSpace(c)), $"{n}: every choice labelled");
            }
            if (s.Check != null) Check(s.Check(s.Default) == null, $"{n}: its rule holds at the defaults ({s.Check(s.Default)})");
            Check(s.Get() == s.Default, $"{n}: at its default");
            Check(!s.ClientVisible || s.Scope == "world", $"{n}: ClientVisible only on a world setting");
            // the API's view
            Check(AeroSettings.UiInfo(n, out var label, out var group, out var tier, out var unit, out var step, out var choices)
                  && label == s.Label && group == s.Group && tier == s.Tier.ToString().ToLowerInvariant() && unit == s.Unit && step == s.Step
                  && (choices == null) == (s.Choices == null) && (choices == null || (choices != s.Choices && choices.SequenceEqual(s.Choices))), $"{n}: UiInfo (TryGetSettingUi) gives its metadata (choices a copy)");
        }
        Check(!AeroSettings.UiInfo("No.Such", out _, out _, out _, out _, out _, out _), "UiInfo: false for no setting");
        foreach (var g in Groups) Check(groupScope.ContainsKey(g), $"group '{g}' has settings");
        int basic = all.Count(s => s.Tier == AeroSettings.Tier.Basic), adv = all.Count(s => s.Tier == AeroSettings.Tier.Advanced), hid = all.Count(s => s.Tier == AeroSettings.Tier.Hidden);
        Console.WriteLine($"  {all.Count} settings: {basic} basic, {adv} advanced, {hid} hidden; " + string.Join(", ", Groups.Select(g => $"{g} {all.Count(s => s.Group == g)}")));
    }

    // a value off the default, in range (a bool flipped; a number toward the far end of its range)
    static double Off(AeroSettings.Setting s)
    {
        if (s.Type == "bool") return 1 - s.Default;
        double v = s.Default - s.Min > s.Max - s.Default ? s.Min + (s.Default - s.Min) * 0.37 : s.Default + (s.Max - s.Default) * 0.37;
        if (s.Type == "int") v = Math.Round(v) == s.Default ? (s.Default < s.Max ? s.Default + 1 : s.Default - 1) : Math.Round(v);
        return v;
    }

    static void ResetAll() { AeroSettings.ResetScope("world"); AeroSettings.ResetScope("client"); }

    // ── 4. text round trip ──
    static void Text()
    {
        var all = All();
        foreach (var scope in new[] { "world", "client" })
        {
            ResetAll();
            Check(AeroSettings.Text(scope, all: false) == "", $"text {scope}: at the defaults the save's text is empty");
            int moved = 0;
            foreach (var s in all)
                if (s.Scope == scope && (s.Type == "text" ? AeroSettings.SetText(s.Name, s.DefaultText + "X 2", out _) : AeroSettings.Set(s.Name, Off(s), out _))) moved++;
            Check(moved >= all.Count(s => s.Scope == scope) - 2, $"text {scope}: {moved} values moved off their defaults");
            var want = all.Where(s => s.Scope == scope).ToDictionary(s => s.Name, s => s.Get());
            var wantText = all.Where(s => s.Scope == scope && s.Type == "text").ToDictionary(s => s.Name, s => s.GetText());
            foreach (var all_ in new[] { true, false })
            {
                string text = AeroSettings.Text(scope, all_);
                AeroSettings.ResetScope(scope);
                int n = AeroSettings.Apply(text, scope);
                bool same = true; string first = null;
                foreach (var s in all.Where(s => s.Scope == scope)) if (s.Get() != want[s.Name]) { same = false; first ??= $"{s.Name} {s.Get()} != {want[s.Name]}"; }
                foreach (var s in all.Where(s => s.Scope == scope && s.Type == "text")) if (s.GetText() != wantText[s.Name]) { same = false; first ??= $"{s.Name} '{s.GetText()}' != '{wantText[s.Name]}'"; }
                Check(same, $"text {scope} (all: {all_}): Text -> Apply gives back every value ({n} applied){(first != null ? ": " + first : "")}");
            }
            // the other scope's lines are ignored
            string other = AeroSettings.Text(scope == "world" ? "client" : "world", all: true);
            Check(AeroSettings.Apply(other, scope) == 0, $"text {scope}: the other scope's lines are not applied");
        }
        ResetAll();
        // a float's text is exact (R format)
        Check(AeroSettings.Set("Aero.DragMultiplier", 0.1 + 0.2, out _), "text: an odd float set");
        AeroSettings.TryGet("Aero.DragMultiplier", out double before);
        string t = AeroSettings.Text("world", all: false);
        AeroSettings.ResetScope("world"); AeroSettings.Apply(t, "world");
        AeroSettings.TryGet("Aero.DragMultiplier", out double after);
        Check(before == after, $"text: a float comes back exactly ({before:R} / {after:R})");
        ResetAll();
        // the world's gravity multiplier (2026-10-23): a world setting, default 1 (vanilla 2), 0..5, onto WorldGravity
        {
            Check(AeroSettings.TryGet("World.GravityMultiplier", out double g0) && g0 == 1.0 && WorldGravity.Multiplier == 1f, $"gravity multiplier: default 1 ({g0})");
            Check(AeroSettings.Text("world", all: true).Contains("World.GravityMultiplier") && !AeroSettings.Text("client", all: true).Contains("World.GravityMultiplier"), "gravity multiplier: a world (host) setting");
            Check(AeroSettings.Set("World.GravityMultiplier", 2.5, out _) && WorldGravity.Multiplier == 2.5f, $"gravity multiplier: set 2.5 reaches WorldGravity ({WorldGravity.Multiplier})");
            Check(!AeroSettings.Set("World.GravityMultiplier", 7, out _) && !AeroSettings.Set("World.GravityMultiplier", -1, out _) && WorldGravity.Multiplier == 2.5f, "gravity multiplier: outside 0..5 refused");
            Check(AeroSettings.Set("World.GravityMultiplier", 0, out _) && WorldGravity.Multiplier == 0f, "gravity multiplier: 0 allowed (no gravity)");
            object s1 = new object(), s2 = new object();
            Check(WorldGravity.Due(null, int.MinValue, s1, 0) && !WorldGravity.Due(s1, 3, s1, 3) && WorldGravity.Due(s1, 3, s1, 4) && WorldGravity.Due(s1, 3, s2, 3),
                "gravity multiplier: applied again on a new session or a changed value, not otherwise");
            Check(WorldGravity.Clamp(float.NaN) == 1f && WorldGravity.Clamp(9f) == 5f, "gravity multiplier: a bad value clamps (NaN -> 1)");
        }
        ResetAll();
        // a text setting (the test spawn's blueprint names): set, saved as its text, its rule, one line, reset
        string tv = null;
        Check(AeroSettings.SetText("Spawn.MissileBlueprint", "  My Missile 2 ", out string twhy) && AeroSettings.TryGetText("Spawn.MissileBlueprint", out tv) && tv == "My Missile 2", $"text setting: set, trimmed ('{tv}', {twhy})");
        Check(AeroSettings.Text("client", all: false).Contains(NL + "Spawn.MissileBlueprint = My Missile 2" + NL) || AeroSettings.Text("client", all: false).StartsWith("Spawn.MissileBlueprint = My Missile 2" + NL), "text setting: the file holds its text");
        Check(!AeroSettings.SetText("Spawn.MissileBlueprint", "a/b", out twhy) && twhy != null, $"text setting: its rule refuses a path ({twhy})");
        Check(!AeroSettings.SetText("Spawn.MissileBlueprint", "a" + NL + "b", out twhy) && twhy != null, $"text setting: one line only ({twhy})");
        Check(!AeroSettings.SetText("Spawn.MissileDistance", "x", out twhy) && twhy != null && !AeroSettings.TryGetText("Spawn.MissileDistance", out _), $"text setting: a number is not a text ({twhy})");
        string tt = AeroSettings.Text("client", all: false);
        AeroSettings.ResetScope("client");
        Check(AeroSettings.TryGetText("Spawn.MissileBlueprint", out tv) && tv == "Missile", $"text setting: a reset gives the default back ('{tv}')");
        AeroSettings.Apply(tt, "client");
        Check(AeroSettings.TryGetText("Spawn.MissileBlueprint", out tv) && tv == "My Missile 2", $"text setting: applied back from the text ('{tv}')");
        Check(AeroSettings.ResetGroup("client", "Test spawn", out twhy) >= 1 && AeroSettings.TryGetText("Spawn.MissileBlueprint", out tv) && tv == "Missile", $"text setting: a group reset includes it ('{tv}', {twhy})");
        ResetAll();
    }

    // ── 5. groups ──
    static void GroupsReset()
    {
        ResetAll();
        var all = All();
        foreach (var s in all.Where(s => s.Group == "Visual effects" || s.Group == "HUD")) AeroSettings.Set(s.Name, Off(s), out _);
        int fx = all.Count(s => s.Group == "Visual effects" && s.Get() != s.Default);
        int n = AeroSettings.ResetGroup("client", "Visual effects", out string why);
        Check(n == fx && why == null, $"reset group: {n} of {fx} changed back, why '{why}'");
        Check(all.Where(s => s.Group == "Visual effects").All(s => s.Get() == s.Default), "reset group: every one of the group at its default");
        Check(all.Where(s => s.Group == "HUD").All(s => s.Get() != s.Default), "reset group: another group untouched");
        Check(AeroSettings.ResetGroup(null, "hud", out why) == all.Count(s => s.Group == "HUD") && why == null, "reset group: any scope, any case");
        Check(AeroSettings.ResetGroup("world", "HUD", out why) == 0 && why != null, $"reset group: a group not in that scope ({why})");
        Check(AeroSettings.ResetGroup(null, "No such", out why) == 0 && why != null, $"reset group: no such group ({why})");
        // a pair under a rule: the reset gets both back whatever the order
        AeroSettings.Set("Entry.FullMach", 6, out _); AeroSettings.Set("Entry.OnsetMach", 5, out _);
        AeroSettings.ResetGroup("world", "Re-entry heat", out why);
        Check(all.Where(s => s.Group == "Re-entry heat").All(s => Math.Abs(s.Get() - s.Default) < 1e-6 * Math.Max(1, Math.Abs(s.Default))) && why == null, $"reset group: the entry Mach pair back (why '{why}')");
        ResetAll();
    }

    // ── 6. authority ──
    static void Authority()
    {
        ResetAll();
        Check(AeroSettings.CanEditWorld(), "authority: nothing seen yet - world settings editable (as before)");
        AeroSettings.ApplyHostFile("# a comment\nStructure.Strength = 9\n# Heat.Multiplier = 3\nFx.Vapour = false\nNo.Such = 1\nAero.LiftMultiplier = 99\n");
        Check(AeroSettings.TryGet("Structure.Strength", out double str) && str == 9, "host file: its value applied");
        Check(AeroSettings.IsForced("Structure.Strength") && AeroSettings.IsForced("structure.strength"), "host file: its name forced (any case)");
        Check(!AeroSettings.IsForced("Heat.Multiplier") && !AeroSettings.IsForced("Fx.Vapour") && !AeroSettings.IsForced("No.Such") && !AeroSettings.IsForced(null),
            "host file: a commented line, a client line, an unknown name - none forced");
        Check(!AeroSettings.IsForced("Aero.LiftMultiplier"), "host file: a refused value (out of range) is not forced");
        Check(!AeroSettings.Set("Structure.Strength", 3, out string why) && why != null && why.StartsWith("forced by the host file"), $"forced: Set refuses ({why})");
        Check(!AeroSettings.Set("Structure.Strength", 9, out why), "forced: even its own value (a screen shows it locked)");
        Check(AeroSettings.Set("Heat.Multiplier", 2, out why), $"forced: another world setting still sets ({why})");
        AeroSettings.ResetGroup("world", "Structure and damage", out why);
        Check(AeroSettings.TryGet("Structure.Strength", out str) && str == 9 && why != null && why.Contains("forced"), $"forced: a group reset keeps it ({why})");
        AeroSettings.WorldLoaded("Structure.Strength = 5\n");   // (the save loads over it; the host file applies again on the next poll)
        Check(AeroSettings.TryGet("Structure.Strength", out str) && str == 5, "forced: the save's own text still applies (storage)");
        AeroSettings.ApplyHostFile("# nothing\n");
        Check(!AeroSettings.IsForced("Structure.Strength") && AeroSettings.Set("Structure.Strength", 3, out why), $"host file emptied: nothing forced, Set works ({why})");
        AeroSettings.WorldDefaults();   // (the world unloaded: defaults)

        // WorldUnsaved
        AeroSettings.WorldSaved();
        Check(!AeroSettings.WorldUnsaved, "unsaved: not after a save");
        AeroSettings.Set("Fx.Vapour", 0, out _);
        Check(!AeroSettings.WorldUnsaved, "unsaved: a client set is not the world's");
        AeroSettings.Set("Heat.Multiplier", 2.5, out _);
        Check(AeroSettings.WorldUnsaved, "unsaved: after a world set");
        AeroSettings.WorldSaved();
        Check(!AeroSettings.WorldUnsaved, "unsaved: cleared by the save");

        // a process that only saw a client (a remote client to be)
        ResetAll();
        AeroSettings.SawClient = true; AeroSettings.SawServer = false;
        Check(!AeroSettings.CanEditWorld(), "authority: a client-only process cannot edit world settings");
        Check(!AeroSettings.Set("Heat.Multiplier", 2, out why) && why != null, $"authority: a world set refused ({why})");
        Check(AeroSettings.Set("Fx.Vapour", 0, out why), $"authority: a client set works ({why})");
        AeroSettings.Set("Fx.Vapour", 1, out _);
        AeroSettings.SawServer = true;   // (a world loaded here: the in-process server)
        Check(AeroSettings.CanEditWorld() && AeroSettings.Set("Heat.Multiplier", 2, out why), $"authority: the host edits world settings ({why})");
        AeroSettings.Set("Heat.Multiplier", 0, out _);
        AeroSettings.SawServer = false;
        Check(AeroSettings.ResetGroup("world", "Re-entry heat", out why) == 0 && why != null, $"authority: a world group's reset refused ({why})");
        AeroSettings.SawClient = false;
        ResetAll();
    }

    // ── 8. a direct field write is never persisted ──
    const string NL = "\n";
    static void Persistence()
    {
        ResetAll();
        HeatScorch.Visible = false;   // (as a harness writes it: the field, not Set)
        Check(AeroSettings.TryGet("Fx.ScorchVisible", out double live) && live == 0, "persist: the direct write is live");
        Check(AeroSettings.Set("Fx.Vapour", 0, out _), "persist: an unrelated client set");
        string client = AeroSettings.Text("client", all: true);
        Check(client.Contains(NL + "Fx.ScorchVisible = true" + NL) && client.Contains(NL + "Fx.Vapour = false" + NL), "persist: the client file keeps the player's Fx.ScorchVisible (true), with the set Fx.Vapour");
        AeroSettings.Info("Structure.Strength", out _, out double strDef, out _, out _, out _, out _, out _);
        HeatScorch.Enabled = false; ShipStructure.Strength = (float)strDef * 0.5f;   // (world fields, directly)
        Check(AeroSettings.Set("Heat.Multiplier", 2, out _), "persist: an unrelated world set");
        string world = AeroSettings.Text("world", all: false);
        Check(world == "Heat.Multiplier = 2" + NL, $"persist: the world save holds only the set value ('{world.Replace(NL, "|")}')");
        // the player then sets the dev value through the store: it is theirs now
        Check(AeroSettings.Set("Fx.ScorchVisible", 0, out _) && AeroSettings.Text("client", all: true).Contains(NL + "Fx.ScorchVisible = false" + NL), "persist: a Set of the live value makes it the player's");
        // a reset brings the live field back too, and the text matches
        AeroSettings.ResetScope("world");
        Check(HeatScorch.Enabled && ShipStructure.Strength == (float)strDef && AeroSettings.Text("world", all: false) == "", "persist: a world reset restores the directly written fields");
        HeatScorch.Visible = true;
        ResetAll();
        Check(AeroSettings.Text("client", all: false) == "", "persist: back at the defaults");
    }

    // ── 9. the forced set does not outlive its world ──
    static void ForcedUnload()
    {
        ResetAll();
        var world = new object();
        AeroSettings.Carriers.Added(world);   // (one world's carrier; CarrierAdded would poll the real files)
        AeroSettings.ApplyHostFile("Structure.Strength = 9" + NL + "Heat.Multiplier = 3" + NL);
        Check(AeroSettings.IsForced("Structure.Strength") && AeroSettings.IsForced("Heat.Multiplier"), "unload: forced while the world is loaded");
        AeroSettings.CarrierRemoved(world, unloading: true);
        Check(!AeroSettings.IsForced("Structure.Strength") && !AeroSettings.IsForced("Heat.Multiplier"), "unload: nothing forced after the world unloads");
        Check(AeroSettings.Set("Structure.Strength", 3, out string why), $"unload: the next world sets it ({why})");
        Check(AeroSettings.TryGet("Heat.Multiplier", out double hm) && AeroSettings.Info("Heat.Multiplier", out _, out double def, out _, out _, out _, out _, out _) && hm == def, "unload: the forced value went back to its default");
        ResetAll();
    }

    // ── 9b. a planetless world (2026-10-19): the players' bodies carry the world's settings (Save/WorldCarriers.cs) ──
    static void Planetless()
    {
        ResetAll();
        var body = WorldCarriers.Kind.Body; var planet = WorldCarriers.Kind.Planet;
        var s1 = new object();
        AeroSettings.Carriers.Added(s1); AeroSettings.Carriers.Added(s1);   // (a new world: its spectator and character, empty builders)
        AeroSettings.Info("Heat.Multiplier", out _, out double hmDef, out _, out _, out _, out _, out _);
        Check(AeroSettings.Set("Heat.Multiplier", 2.5, out string why), $"planetless: a world setting changes ({why})");
        string text = AeroSettings.Text("world", all: false);   // (what each body writes at the save)
        Check(AeroSettings.SavedText(new[] { "other:1", AeroSettings.SaveKey + text }) == text && text.Contains("Heat.Multiplier = 2.5"), $"planetless: the saved key holds the text ('{text.Replace(NL, "|")}')");
        Check(AeroSettings.SavedText(new[] { "other:1" }) == "" && AeroSettings.SavedText(null) == "", "planetless: no key - no text");
        AeroSettings.CarrierRemoved(s1, unloading: false);   // (the character died: a body gone mid-session)
        Check(AeroSettings.TryGet("Heat.Multiplier", out double hm) && hm == 2.5, "planetless: a body leaving mid-session keeps the world's settings");
        AeroSettings.CarrierRemoved(s1, unloading: true);   // (the world closes: its last carrier)
        Check(AeroSettings.TryGet("Heat.Multiplier", out hm) && hm == hmDef, "planetless: the unload resets the world's settings");
        var s2 = new object();
        AeroSettings.Carriers.Read(text, body, s2); AeroSettings.Carriers.Read(text, body, s2);
        AeroSettings.Carriers.Added(s2); AeroSettings.Carriers.Added(s2);
        Check(AeroSettings.TryGet("Heat.Multiplier", out hm) && hm == 2.5 && !AeroSettings.WorldUnsaved, "planetless: the save's value back after the reload (and nothing unsaved)");
        AeroSettings.CarrierRemoved(s2, true); AeroSettings.CarrierRemoved(s2, true);
        // precedence: an old save (planets only) still loads; a body's text wins over a planet's either way round
        var s3 = new object();
        AeroSettings.Carriers.Read("Heat.Multiplier = 3" + NL, planet, s3); AeroSettings.Carriers.Added(s3);
        Check(AeroSettings.TryGet("Heat.Multiplier", out hm) && hm == 3, "old save: the planets' text loads");
        AeroSettings.CarrierRemoved(s3, true);
        var s4 = new object();
        AeroSettings.Carriers.Read("Heat.Multiplier = 3" + NL, planet, s4); AeroSettings.Carriers.Read(text, body, s4);
        AeroSettings.Carriers.Added(s4); AeroSettings.Carriers.Added(s4);
        Check(AeroSettings.TryGet("Heat.Multiplier", out hm) && hm == 2.5, "precedence: planet then body - the body's");
        AeroSettings.CarrierRemoved(s4, true); AeroSettings.CarrierRemoved(s4, true);
        var s5 = new object();
        AeroSettings.Carriers.Read(text, body, s5); AeroSettings.Carriers.Read("Heat.Multiplier = 3" + NL, planet, s5);
        AeroSettings.Carriers.Added(s5); AeroSettings.Carriers.Added(s5);
        Check(AeroSettings.TryGet("Heat.Multiplier", out hm) && hm == 2.5, "precedence: body then planet - the body's");
        // the hooks run at the world's end (and at a new session whose last world's unload was not seen)
        int hooks = 0; var old = AeroSettings.WorldHooks; AeroSettings.WorldHooks = () => hooks++;
        AeroSettings.Carriers.Read(text, body, new object());
        Check(hooks == 1 && AeroSettings.TryGet("Heat.Multiplier", out hm) && hm == 2.5, "unseen unload: the hooks run, the new world's text applied after the reset");
        AeroSettings.WorldHooks = old;
        AeroSettings.WorldDefaults();
        ResetAll();
    }

    // ── 10. the code never switches a setting itself ──
    static void NoCodeWrites(string root)
    {
        string sim = Path.Combine(root, "Scripts", "Simulation");
        string reg = File.ReadAllText(Path.Combine(sim, "Runtime", "AeroSettings.cs"));
        var targets = Regex.Matches(reg, @"v\s*=>\s*\{?\s*([A-Z]\w*)\.([A-Z]\w*)\s*=(?![=>])").Cast<Match>()
            .Select(m => (cls: m.Groups[1].Value, field: m.Groups[2].Value)).Distinct().ToList();
        Check(targets.Count > 60, $"no code writes: {targets.Count} setting fields");
        int files = 0;
        foreach (var f in Directory.GetFiles(sim, "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sim, f).Replace('\\', '/');
            if (rel.StartsWith("Dev/") || Path.GetFileName(f).StartsWith("AeroSettings")) continue;
            files++;
            string src = Regex.Replace(File.ReadAllText(f), @"//.*", "");
            var lines = src.Split('\n');
            foreach (var (cls, field) in targets)
            {
                bool own = Regex.IsMatch(src, @"\b(class|struct)\s+" + cls + @"\b");
                var write = new Regex((own ? @"(?<![\w.])(" + cls + @"\.)?" : @"\b" + cls + @"\.") + field + @"\s*(=(?![=>])|\|=|&=|\^=)");
                for (int i = 0; i < lines.Length; i++)
                {
                    string l = lines[i];
                    if (!write.IsMatch(l) || Regex.IsMatch(l, @"\bstatic\b") || l.Contains("case \"")) continue;   // (its declaration; a harness verb's switch)
                    Check(false, $"no code writes: {rel}:{i + 1} writes {cls}.{field} (a setting) - keep a runtime flag of its own: {l.Trim()}");
                }
            }
        }
        Check(files > 50, $"no code writes: {files} files scanned");
    }

    // ── 11. the file poll: read in the background, applied on a tick ──
    static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Static;
    static void SetStatic(string name, object v) => typeof(AeroSettings).GetField(name, Priv).SetValue(null, v);
    static void PollFiles() => typeof(AeroSettings).GetMethod("PollFiles", Priv).Invoke(null, new object[] { false });   // (the background task's body)
    static void TickPoll(bool force = false) { SetStatic("_localNext", 0L); AeroSettings.Poll(force); SetStatic("_pollAt", long.MaxValue); }   // (no background task started)
    static DateTime _stamp = DateTime.UtcNow.AddHours(1);
    static void HandEdit(string path, string text) { File.WriteAllText(path, text); File.SetLastWriteTimeUtc(path, _stamp = _stamp.AddMinutes(1)); }

    static void FilePoll()
    {
        ResetAll();
        string dir = Path.Combine(Path.GetTempPath(), "AeroSettingsTests-" + Guid.NewGuid().ToString("N"));
        SetStatic("_dir", dir);
        string client = Path.Combine(dir, "AerodynamicsMod.client.cfg"), world = Path.Combine(dir, "AerodynamicsMod.world.cfg");
        AeroSettings.SawServer = true;
        SetStatic("_pollAt", long.MaxValue);
        try
        {
            PollFiles(); TickPoll();   // (both files written: defaults; the host file all comments)
            Check(File.Exists(client) && File.Exists(world) && AeroSettings.FileWhy == "", $"poll: the files written in the temp folder ({AeroSettings.FileWhy})");
            Check(FlightField.VapourOn, "poll: Fx.Vapour at its default (on)");
            HandEdit(client, "Fx.Vapour = false" + NL);
            PollFiles();
            Check(FlightField.VapourOn, "poll: a hand edit read in the background is not applied there");
            TickPoll();
            Check(!FlightField.VapourOn, "poll: the next tick-thread poll applies it");
            HandEdit(client, "Fx.Vapour = true" + NL);
            TickPoll(force: true);
            Check(FlightField.VapourOn, "poll: a forced poll (a world loaded) reads and applies at once");
            AeroSettings.TryGet("Heat.Multiplier", out double hmDef);
            HandEdit(world, "Heat.Multiplier = 3" + NL);
            PollFiles();
            Check(!AeroSettings.IsForced("Heat.Multiplier") && AeroSettings.TryGet("Heat.Multiplier", out double hm0) && hm0 == hmDef, "poll: the host file read in the background is not applied there");
            TickPoll();
            Check(AeroSettings.IsForced("Heat.Multiplier") && AeroSettings.TryGet("Heat.Multiplier", out double hm1) && hm1 == 3, "poll: the next tick-thread poll applies the host file (forced)");
            var loaded = new object();
            AeroSettings.Carriers.Added(loaded);
            HandEdit(world, "Heat.Multiplier = 4" + NL);
            PollFiles();   // (read; the world unloads before a tick applies it)
            AeroSettings.CarrierRemoved(loaded, unloading: true);
            TickPoll();
            Check(!AeroSettings.IsForced("Heat.Multiplier") && AeroSettings.TryGet("Heat.Multiplier", out double hm2) && hm2 == hmDef, "poll: a world unload discards a host-file read still waiting for a tick");
            // a change made while the client file is written (after the poll took its mark) is not lost
            var dirtyAt = typeof(AeroSettings).GetField("_clientDirtyAt", Priv);
            var write = typeof(AeroSettings).GetMethod("WriteClient", Priv);
            Check(AeroSettings.Set("Fx.Vapour", 0, out _), "poll: a client set");
            long mark = (long)dirtyAt.GetValue(null);
            Check(mark != 0, "poll: the set marks the client file for writing");
            System.Threading.Thread.Sleep(2);   // (the next set's mark a later tick)
            Check(AeroSettings.Set("Fx.Vapour", 1, out _), "poll: another client set, while the first is written");
            write.Invoke(null, new object[] { mark });   // (the write of the first mark)
            Check((long)dirtyAt.GetValue(null) != 0, "poll: a set during the write leaves the file marked");
            dirtyAt.SetValue(null, 1L);   // (settled: the next poll writes)
            PollFiles(); TickPoll();
            Check((long)dirtyAt.GetValue(null) == 0 && File.ReadAllText(client).Contains(NL + "Fx.Vapour = true" + NL), "poll: the next poll writes the set made during the write");
            Check(AeroSettings.Set("Fx.Vapour", 0, out _), "poll: a client set, not yet settled");
            write.Invoke(null, new object[] { 0L });   // (the file missing: written, the mark taken was none)
            Check((long)dirtyAt.GetValue(null) != 0, "poll: writing a missing file leaves a pending set marked");
            dirtyAt.SetValue(null, 0L);
        }
        finally
        {
            AeroSettings.SawServer = false;
            ResetAll();
            try { Directory.Delete(dir, true); } catch { }   // (this test's own temp folder)
        }
    }

    // ── 12. flight assists ──
    static double Get(string n) { AeroSettings.TryGet(n, out double v); return v; }
    static bool Is(FlightAssists.Values v) => Get("Assists.StallProtection") == (v.Protection ? 1 : 0) && Get("Assists.StallAngleScale") == v.StallAngle
        && Get("Assists.PostStallLift") == v.Lift && Get("Assists.SurfaceAuthority") == v.Authority && Get("Assists.OffsetThrust") == v.OffsetThrust
        && Math.Abs(Get("Gyros.TorqueScale") - v.Gyro) < 1e-6;
    static void Assists()
    {
        ResetAll();
        var all = All();
        FlightAssists.Preset(FlightAssists.Realistic, out var real);
        FlightAssists.Preset(FlightAssists.Assisted, out var assisted);
        FlightAssists.Preset(FlightAssists.Forgiving, out var forgiving);
        Check(Is(real) && Get("Assists.Preset") == FlightAssists.Realistic, "assists: every default is the Realistic preset");
        Check(!FlightAssists.StallProtection && FlightAssists.StallAngleScale == 1f && FlightAssists.PostStallLift == 1f && FlightAssists.SurfaceAuthority == 1f && FlightAssists.OffsetThrustScale == 1f,
            "assists: realistic - protection off, every multiplier 1");
        Check(all.Count(s => s.Derived) == 1 && all.Single(s => s.Derived).Name == "Assists.Preset", "assists: the preset is the only derived setting");
        string[] members = { "Assists.StallProtection", "Gyros.TorqueScale", "Assists.StallAngleScale", "Assists.PostStallLift", "Assists.SurfaceAuthority", "Assists.OffsetThrust" };
        Check(members.All(n => all.Any(s => s.Name == n && s.Group == "Flight assists" && s.Scope == "world")), "assists: every member in the Flight assists group (world)");
        Check(AeroSettings.Set("Assists.Preset", FlightAssists.Assisted, out string why) && Is(assisted) && Get("Assists.Preset") == FlightAssists.Assisted, $"assists: Assisted sets its members ({why})");
        string text = AeroSettings.Text("world", all: false);
        Check(!text.Contains("Assists.Preset") && text.Contains("Assists.StallProtection = true" + NL) && text.Contains("Gyros.TorqueScale = 0.5" + NL), $"assists: the save holds the members, not the preset ('{text.Replace(NL, "|")}')");
        Check(!AeroSettings.Text("world", all: true).Contains("Assists.Preset"), "assists: the world file never lists the preset");
        AeroSettings.ResetScope("world");
        Check(Is(real), "assists: a world reset is realistic");
        AeroSettings.Apply(text, "world");
        Check(Is(assisted) && Get("Assists.Preset") == FlightAssists.Assisted, "assists: the saved members load back as Assisted");
        Check(AeroSettings.Set("Assists.PostStallLift", 1.2, out _) && Get("Assists.Preset") == FlightAssists.Custom, "assists: a member moved off - Custom");
        Check(AeroSettings.Set("Assists.Preset", FlightAssists.Custom, out why) && Math.Abs(Get("Assists.PostStallLift") - 1.2) < 1e-6 && Get("Assists.StallProtection") == 1, $"assists: picking Custom changes nothing ({why}, {Get("Assists.PostStallLift")}, {Get("Assists.StallProtection")})");
        Check(AeroSettings.Set("Assists.Preset", FlightAssists.Forgiving, out _) && Is(forgiving), "assists: Forgiving sets its members");
        Check(AeroSettings.Set("Assists.Preset", FlightAssists.Realistic, out _) && Is(real) && AeroSettings.Text("world", all: false) == "", "assists: Realistic again - every member at its default, nothing to save");
        // a member the host file forces keeps its value; the rest follow the preset
        AeroSettings.ApplyHostFile("Gyros.TorqueScale = 0.3" + NL);
        Check(AeroSettings.Set("Assists.Preset", FlightAssists.Assisted, out why) && Math.Abs(Get("Gyros.TorqueScale") - 0.3) < 1e-6 && Get("Assists.StallProtection") == 1 && Get("Assists.Preset") == FlightAssists.Custom,
            $"assists: a forced member keeps its value, the others follow ({why})");
        AeroSettings.ApplyHostFile("# nothing" + NL);
        AeroSettings.Set("Assists.Preset", FlightAssists.Forgiving, out _);
        Check(AeroSettings.ResetGroup("world", "Flight assists", out why) > 0 && why == null && Is(real) && Get("Assists.Preset") == FlightAssists.Realistic, $"assists: the group's reset is realistic ({why})");
        ResetAll();
    }

    // ── 7. denylist ──
    static readonly Regex Deny = new(@"^(Dev|Test|Debug|Probe|Trace)|Log$");
    static void Denylist(string root)
    {
        foreach (var s in All())
        {
            string last = s.Name.Substring(s.Name.LastIndexOf('.') + 1);
            Check(!Deny.IsMatch(last), $"denylist: setting name {s.Name}");
        }
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "Scripts", "Simulation", "Runtime", "AeroSettings.cs"))).GetRoot();
        int setters = 0;
        foreach (var inv in tree.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is not IdentifierNameSyntax id || (id.Identifier.Text != "B" && id.Identifier.Text != "I" && id.Identifier.Text != "F")) continue;
            foreach (var a in inv.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                setters++;
                string target = a.Left is MemberAccessExpressionSyntax ma ? ma.Name.Identifier.Text : a.Left.ToString();
                Check(!Deny.IsMatch(target), $"denylist: a setter writes {a.Left}");
            }
        }
        Check(setters >= 75, $"denylist: {setters} setters scanned");
    }

    static int Main()
    {
        string root = Root();
        Honest(root); Shape(); Every(); Text(); GroupsReset(); Authority(); Persistence(); ForcedUnload(); Planetless(); Denylist(root); NoCodeWrites(root); FilePoll(); Assists();
        Console.WriteLine($"SettingsTests: {_checks - _fails}/{_checks} passed");
        return _fails == 0 ? 0 : 1;
    }
}
