using System.IO;
using System.Linq;
using System.Reflection;
using AeroMod;

// SHARED by both mods' Tests/SettingsUiTests: written once in the Aerodynamics Mod's tools/shared/modsrc/files/, copied by
// tools/shared/modsrc/sync.sh (only the 'using' of the mod's namespace differs; edit it there). It reaches the registry only
// through the adapter (SettingsTab.Gather / CanEditWorld / IsForced) and the registry class's SawServer / SawClient / _forced
// fields, found by reflection - so the same file tests either mod.
namespace SettingsUiTests
{
    /// <summary>
    /// Offline checks of the settings menu's model (Ui/SettingsModel.cs) over the mod's REAL registry (through Ui/SettingsTab.Mod.cs):
    /// every setting becomes a row; groups in registration order with Hidden rows left out and Advanced ones apart; each row's
    /// control (switch, choice, slider); sliders reach Min and Max exactly in at most MaxSliderSteps steps; world rows read-only
    /// where the world cannot be edited, locked when the host file forces them; a slider's edit applies once it holds still.
    /// No game, no Avalonia; exit 0 = all pass.
    /// </summary>
    internal static class Program
    {
        static int _passed, _failed;
        static void Ok(string name, bool ok, string detail = "")
        {
            if (ok) _passed++;
            else { _failed++; Console.WriteLine("FAIL  " + name + (detail.Length > 0 ? ": " + detail : "")); }
        }
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        /// <summary>The registry class (OrbitalSettings / AeroSettings): the one with SawServer and SawClient.</summary>
        static readonly Type Registry = typeof(SettingsTab).Assembly.GetTypes().First(t => t.GetField("SawServer", Any) != null && t.GetField("SawClient", Any) != null);

        static int Main(string[] args)
        {
            Console.WriteLine("=== settings menu model (" + Registry.Name + ") ===\n");
            var dir = Path.Combine(Path.GetTempPath(), "SettingsUiTests-" + Environment.ProcessId);
            Registry.GetField("_dir", Any)?.SetValue(null, dir);   // (never %APPDATA%: nothing here writes, but be sure)
            var rows = new List<SettingsModel.Row>();
            SettingsTab.Gather(rows);
            if (args.Length > 0 && args[0] == "dump") { Dump(rows); return 0; }
            Rows(rows);
            Groups(rows);
            Controls(rows);
            Sliders(rows);
            Locks(rows);
            Edits();
            Texts();
            Looks();
            Console.WriteLine($"\n{_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        /// <summary>dotnet run -- dump: the menu as text (what the tab shows, in its order).</summary>
        static void Dump(List<SettingsModel.Row> rows)
        {
            foreach (var g in SettingsModel.Build(rows))
            {
                Console.WriteLine($"\n[{g.Name}] ({g.Scope})");
                foreach (var r in g.Basic.Concat(g.Advanced))
                {
                    string c = r.Kind == "choice" ? string.Join(" / ", r.Choices) : r.Kind;
                    if (r.Kind == "number") { SettingsModel.Scale(r, out double st, out int n); c = $"slider {SettingsModel.Format(r, r.Min)} .. {SettingsModel.Format(r, r.Max)} ({n} x {st})"; }
                    Console.WriteLine($"  {(r.Tier == "advanced" ? "  adv " : "")}{r.Label}: {c}{(r.Note != null ? "  - " + r.Note : "")}");
                }
            }
        }

        static void Rows(List<SettingsModel.Row> rows)
        {
            Console.WriteLine("-- every setting is a row");
            var names = (List<string>)null;
            var m = Registry.GetMethod("Names", Any);
            names = new List<string>(); m.Invoke(null, new object[] { names });
            Ok($"{rows.Count} rows for {names.Count} settings", rows.Count == names.Count && rows.Count > 20);
            for (int k = 0; k < rows.Count; k++)
            {
                var r = rows[k];
                Ok($"{r.Name}: in registration order", r.Order == k && r.Name == names[k]);
                Ok($"{r.Name}: a label", !string.IsNullOrWhiteSpace(r.Label) && r.Label != r.Name, r.Label ?? "");
                Ok($"{r.Name}: a group", !string.IsNullOrWhiteSpace(r.Group));
                Ok($"{r.Name}: a tier", r.Tier == "basic" || r.Tier == "advanced" || r.Tier == "hidden", r.Tier ?? "");
                Ok($"{r.Name}: a scope", r.Scope == "world" || r.Scope == "client", r.Scope ?? "");
                Ok($"{r.Name}: a doc (the description)", !string.IsNullOrWhiteSpace(r.Doc));
                Ok($"{r.Name}: Min <= Max", r.Min <= r.Max);
            }
        }

        static void Groups(List<SettingsModel.Row> rows)
        {
            Console.WriteLine("-- groups: registration order, Hidden left out, Advanced apart");
            var groups = SettingsModel.Build(rows);
            var placed = groups.SelectMany(g => g.Basic.Concat(g.Advanced)).ToList();
            Ok("no row twice", placed.Distinct().Count() == placed.Count);
            Ok("Shown = the rows placed", SettingsModel.Shown(rows) == placed.Count, $"{SettingsModel.Shown(rows)} vs {placed.Count}");
            foreach (var r in rows)
            {
                bool on = placed.Contains(r);
                Ok($"{r.Name} ({r.Tier}): {(r.Tier == "hidden" ? "not shown" : "shown")}", on == (r.Tier != "hidden"));
            }
            var firstSeen = new List<string>();
            foreach (var r in rows) if (r.Tier != "hidden" && !firstSeen.Contains(r.Group)) firstSeen.Add(r.Group);
            Ok("groups in the order their first shown setting was registered", groups.Select(g => g.Name).SequenceEqual(firstSeen), string.Join(" | ", groups.Select(g => g.Name)));
            foreach (var g in groups)
            {
                Ok($"{g.Name}: Basic rows are basic (or all advanced, shown as its own), Advanced rows advanced", (g.AllAdvanced ? g.Basic.All(r => r.Tier == "advanced") && g.Advanced.Count == 0 : g.Basic.All(r => r.Tier == "basic")) && g.Advanced.All(r => r.Tier == "advanced"));
                Ok($"{g.Name}: rows in registration order", Sorted(g.Basic) && Sorted(g.Advanced));
                Ok($"{g.Name}: every row of the group", g.Basic.Concat(g.Advanced).All(r => r.Group == g.Name));
                var scopes = g.Basic.Concat(g.Advanced).Select(r => r.Scope).Distinct().ToList();
                Ok($"{g.Name}: its scope ({g.Scope})", scopes.Count == 1 ? g.Scope == scopes[0] : g.Scope == "mixed");
                Ok($"{g.Name}: not empty", g.Count > 0);
            }
            SettingsModel.ShowAdvanced = false;
            try
            {
                var basic = SettingsModel.Build(rows);
                Ok("Advanced off: only Basic rows", basic.All(g => g.Advanced.Count == 0) && basic.Sum(g => g.Count) == rows.Count(r => r.Tier == "basic"));
                Ok("Advanced off: Shown counts Basic only", SettingsModel.Shown(rows) == rows.Count(r => r.Tier == "basic"));
            }
            finally { SettingsModel.ShowAdvanced = true; }
            var none = new List<SettingsModel.Row> { new SettingsModel.Row { Name = "x", Type = "bool", Scope = "client", Tier = "basic", Order = 0 } };
            Ok("a row with no group goes to Other", SettingsModel.Build(none).Single().Name == "Other");
            // (UI audit C7, 2026-10-18: "Performance (host)" was a heading over a lone "Advanced")
            var advOnly = new List<SettingsModel.Row> { new SettingsModel.Row { Name = "a", Type = "bool", Scope = "world", Group = "Perf", Tier = "advanced", Order = 0 }, new SettingsModel.Row { Name = "b", Type = "bool", Scope = "world", Group = "Perf", Tier = "advanced", Order = 1 } };
            var ag = SettingsModel.Build(advOnly).Single();
            Ok("a group of Advanced rows only: its rows shown as its own, marked", ag.AllAdvanced && ag.Basic.Count == 2 && ag.Advanced.Count == 0);
            var mixed = new List<SettingsModel.Row> { advOnly[0], new SettingsModel.Row { Name = "c", Type = "bool", Scope = "world", Group = "Perf", Tier = "basic", Order = 2 } };
            var mg = SettingsModel.Build(mixed).Single();
            Ok("a group with a Basic row keeps its Advanced expander", !mg.AllAdvanced && mg.Basic.Count == 1 && mg.Advanced.Count == 1);
        }
        static bool Sorted(List<SettingsModel.Row> l) { for (int k = 1; k < l.Count; k++) if (l[k].Order <= l[k - 1].Order) return false; return true; }

        static void Controls(List<SettingsModel.Row> rows)
        {
            Console.WriteLine("-- each row's control");
            foreach (var r in rows)
            {
                string want = r.Type == "bool" ? "bool" : r.Choices != null ? "choice" : "number";
                Ok($"{r.Name}: a {want}", r.Kind == want, r.Kind + (r.Choices != null ? $" ({r.Choices.Length} labels for {r.Min}..{r.Max})" : ""));
                if (r.Kind == "bool") Ok($"{r.Name}: a bool is 0..1", r.Min == 0 && r.Max == 1);
                if (r.Kind == "choice")
                {
                    Ok($"{r.Name}: a label per value", r.Choices.Length == (int)(r.Max - r.Min) + 1 && r.Choices.All(c => !string.IsNullOrWhiteSpace(c)));
                    for (double v = r.Min; v <= r.Max; v++)
                        Ok($"{r.Name}: choice {v} round-trips", SettingsModel.FromControl(r, SettingsModel.ToControl(r, v)) == v);
                }
            }
        }

        static void Sliders(List<SettingsModel.Row> rows)
        {
            Console.WriteLine("-- sliders: Min and Max exactly, at most MaxSliderSteps steps");
            foreach (var r in rows.Where(r => r.Kind == "number"))
            {
                SettingsModel.Scale(r, out double step, out int n);
                Ok($"{r.Name}: 1..{SettingsModel.MaxSliderSteps} steps", n >= 1 && n <= SettingsModel.MaxSliderSteps, $"{n} of {step}");
                Ok($"{r.Name}: the first step is Min", SettingsModel.FromIndex(r, 0) == r.Min);
                Ok($"{r.Name}: the last step is Max", SettingsModel.FromIndex(r, n) == r.Max);
                bool mono = true, round = true, integral = true, inRange = true;
                double last = double.NegativeInfinity;
                for (int k = 0; k <= n; k++)
                {
                    double v = SettingsModel.FromIndex(r, k);
                    if (v < last) mono = false;
                    last = v;
                    if (SettingsModel.ToIndex(r, v) != k) round = false;
                    if (r.Type == "int" && v != Math.Round(v)) integral = false;
                    if (v < r.Min || v > r.Max) inRange = false;
                }
                Ok($"{r.Name}: steps rise", mono);
                Ok($"{r.Name}: every step maps back to itself", round);
                Ok($"{r.Name}: in range", inRange);
                if (r.Type == "int") Ok($"{r.Name}: an int's steps are whole", integral);
                if (r.Step > 0 && r.Type == "float" && !SettingsModel.IsLog(r))
                    Ok($"{r.Name}: the registered step ({r.Step})", Math.Abs(step - r.Step) < 1e-12, step.ToString());
                if (SettingsModel.IsLog(r))
                {
                    Ok($"{r.Name}: a logarithmic slider of at most LogSteps + 3 values", n <= SettingsModel.LogSteps + 2);
                    bool onStep = true;
                    for (int k = 1; k < n; k++)
                    {
                        double v = SettingsModel.FromIndex(r, k), q = v / step;
                        if (r.Min <= 0 && Math.Abs(q - Math.Round(q)) > 1e-6 && v != 0) onStep = false;
                    }
                    Ok($"{r.Name}: logarithmic values on the registered step", onStep);
                }
                // (any value - a default between steps - shows at the nearest step)
                foreach (double v in new[] { r.Min, r.Max, Math.Min(r.Max, Math.Max(r.Min, r.Default)), (r.Min + r.Max) / 2, r.Min + (r.Max - r.Min) / 1000 })
                {
                    int k = SettingsModel.ToIndex(r, v);
                    double back = SettingsModel.FromIndex(r, k), d = Math.Abs(back - v);
                    bool nearest = (k == 0 || Math.Abs(SettingsModel.FromIndex(r, k - 1) - v) >= d - 1e-12)
                                && (k == n || Math.Abs(SettingsModel.FromIndex(r, k + 1) - v) >= d - 1e-12);
                    Ok($"{r.Name}: {v} shows at the nearest step", nearest && SettingsModel.FromControl(r, SettingsModel.ToControl(r, v)) == back, $"{back} (index {k})");
                }
                Ok($"{r.Name}: a value text", SettingsModel.Format(r, r.Min).Length > 0 && (r.Unit == "" || SettingsModel.Format(r, r.Min).Contains(r.Unit)));
            }
        }

        static void Locks(List<SettingsModel.Row> rows)
        {
            Console.WriteLine("-- who may change a row");
            var world = rows.FirstOrDefault(r => r.World);
            var client = rows.FirstOrDefault(r => !r.World);
            Ok("the registry has world and client settings", world != null && client != null);
            if (world == null || client == null) return;
            const string path = @"C:\x\Mod.world.cfg";
            Ok("a client row: never locked", SettingsModel.LockReason(client, false, true, path) == null);
            Ok("a world row, host, not forced: editable", SettingsModel.LockReason(world, true, false, path) == null);
            Ok("a world row, not the host: read-only, the reason", SettingsModel.LockReason(world, false, false, path) == SettingsModel.HostOnly);
            Ok("a world row the host file forces: locked, its path", SettingsModel.LockReason(world, true, true, path) == SettingsModel.ForcedText(path));
            Ok("forced and not the host: the file says why", SettingsModel.LockReason(world, false, true, path) == SettingsModel.ForcedText(path));

            // the adapter over the real registry: a process that only saw a client refuses world edits
            var sawServer = Registry.GetField("SawServer", Any); var sawClient = Registry.GetField("SawClient", Any);
            object s0 = sawServer.GetValue(null), c0 = sawClient.GetValue(null);
            try
            {
                Ok("fresh: the world can be edited here", SettingsTab.CanEditWorld());
                sawServer.SetValue(null, false); sawClient.SetValue(null, true);
                Ok("a remote client: the world cannot be edited", !SettingsTab.CanEditWorld());
                foreach (var r in rows)
                    Ok($"{r.Name} on a remote client: {(r.World ? "read-only" : "editable")}",
                       (SettingsModel.LockReason(r, SettingsTab.CanEditWorld(), SettingsTab.IsForced(r.Name), SettingsTab.WorldPath) == SettingsModel.HostOnly) == r.World);
            }
            finally { sawServer.SetValue(null, s0); sawClient.SetValue(null, c0); }

            // the host file's names (the store's _forced) lock exactly those rows
            var forced = Registry.GetField("_forced", Any);
            object f0 = forced.GetValue(null);
            try
            {
                forced.SetValue(null, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { world.Name });
                Ok($"{world.Name} forced: IsForced", SettingsTab.IsForced(world.Name));
                Ok($"{world.Name} forced: locked with the world file's path",
                   SettingsModel.LockReason(world, SettingsTab.CanEditWorld(), SettingsTab.IsForced(world.Name), SettingsTab.WorldPath) == SettingsModel.ForcedText(SettingsTab.WorldPath));
                Ok("the world file is the mod's own", SettingsTab.WorldPath.EndsWith(".world.cfg") && SettingsTab.ClientPath.EndsWith(".client.cfg"));
                var other = rows.FirstOrDefault(r => r.World && r != world);
                if (other != null) Ok($"{other.Name} not forced: editable", SettingsModel.LockReason(other, true, SettingsTab.IsForced(other.Name), SettingsTab.WorldPath) == null);
            }
            finally { forced.SetValue(null, f0); }
        }

        static void Edits()
        {
            Console.WriteLine("-- a control's edits");
            var sw = new SettingsModel.Edit(false);
            Ok("nothing shown yet: the control's starting value is no edit", !sw.Observe(0, out _) && !sw.Observe(1, out _));
            sw.Showing(0);
            Ok("a switch as shown: nothing", !sw.Observe(0, out _));
            Ok("a switch flipped: applies at once", sw.Observe(1, out double a) && a == 1);
            sw.Showing(1);
            Ok("no value (indeterminate): nothing", !sw.Observe(double.NaN, out _));
            var sl = new SettingsModel.Edit(true);
            sl.Showing(10);
            Ok("a slider moved: waits", !sl.Observe(14, out _) && sl.Moving);
            Ok("still moving: waits", !sl.Observe(17, out _));
            Ok("held still for a read: applies", sl.Observe(17, out double b) && b == 17 && !sl.Moving);
            sl.Showing(17);
            Ok("back where it was shown: nothing", !sl.Observe(17, out _));
            Ok("moved and back before it settled: nothing", !sl.Observe(20, out _) && !sl.Observe(17, out _) && !sl.Moving);
            Ok("the last read (the tab left): a moved slider applies at once", !sl.Observe(30, out _) && sl.Observe(31, out double c, now: true) && c == 31);
        }

        static void Texts()
        {
            Console.WriteLine("-- numbers and values");
            Ok("Nice(0.013) = 0.02", Math.Abs(SettingsModel.Nice(0.013) - 0.02) < 1e-15);
            Ok("Nice(1) = 1", SettingsModel.Nice(1) == 1);
            Ok("Nice(7) = 10", SettingsModel.Nice(7) == 10);
            Ok("Nice(10000) = 10000", SettingsModel.Nice(10000) == 10000);
            Ok("Decimals(0.25) = 2", SettingsModel.Decimals(0.25) == 2);
            Ok("Decimals(60) = 0", SettingsModel.Decimals(60) == 0);
            Ok("Decimals(0.0005) = 4", SettingsModel.Decimals(0.0005) == 4);
            var s = new SettingsModel.Row { Name = "t", Type = "float", Unit = "s", Min = 0, Max = 100000, Step = 0.5 };
            Ok("12.5 s", SettingsModel.Format(s, 12.5) == "12.5 s", SettingsModel.Format(s, 12.5));
            Ok("86,400.0 s (grouped; the registered step's digits)", SettingsModel.Format(s, 86400) == "86,400.0 s", SettingsModel.Format(s, 86400));
            SettingsModel.Scale(s, out double st, out int n);
            Ok("a wide range: logarithmic", SettingsModel.IsLog(s) && n <= SettingsModel.LogSteps + 2 && st == 0.5, $"{st} x {n}");
            Ok("logarithmic: Min first, then the step", SettingsModel.FromIndex(s, 0) == 0 && SettingsModel.FromIndex(s, 1) == 0.5);
            Ok("logarithmic: two significant digits", SettingsModel.Table(s).All(v => v < 100 || v == s.Max || Math.Abs(v / Math.Pow(10, Math.Floor(Math.Log10(v)) - 1) % 1) < 1e-9),
               string.Join(" ", SettingsModel.Table(s).Where((v, k) => k % 24 == 0)));
            var narrow = new SettingsModel.Row { Name = "n", Type = "float", Min = 0, Max = 10, Step = 0.05 };
            Ok("200 steps: still linear", !SettingsModel.IsLog(narrow));
            var x = new SettingsModel.Row { Name = "x", Type = "float", Unit = "×", Min = 0, Max = 10, Step = 0.05 };
            Ok("1.50×", SettingsModel.Format(x, 1.5) == "1.50×", SettingsModel.Format(x, 1.5));
            // (UI audit C7: the planet day's "-1 s" - a value that is a word)
            var day = new SettingsModel.Row { Name = "d", Type = "float", Unit = "s", Min = -1, Max = 1e7, Step = 60, Specials = new Dictionary<double, string> { [-1] = "the world's own" } };
            Ok("a special value says its word", SettingsModel.Format(day, -1) == "the world's own", SettingsModel.Format(day, -1));
            Ok("other values stay numbers", SettingsModel.Format(day, 120) == "120 s", SettingsModel.Format(day, 120));
            Ok("a forced row names the file, not its path", SettingsModel.ForcedText(@"C:\Users\x\AppData\Mod.world.cfg") == "Forced by the host's Mod.world.cfg" && SettingsModel.ForcedText("a/b/Mod.world.cfg") == "Forced by the host's Mod.world.cfg", SettingsModel.ForcedText(@"C:\Users\x\Mod.world.cfg"));
            Ok("a reset button names its group", SettingsModel.ResetLabel("Map") == "Reset Map to defaults" && SettingsModel.ResetLabel(null) == "Reset to defaults");
            var i = new SettingsModel.Row { Name = "i", Type = "int", Min = 16, Max = 4096, Step = 1 };
            SettingsModel.Scale(i, out double ist, out int inn);
            Ok("a wide int range: logarithmic, whole values", SettingsModel.IsLog(i) && SettingsModel.Table(i).All(v => v == Math.Round(v)), $"{ist} x {inn}");
            Ok("an int's first and last steps are its Min and Max", SettingsModel.FromIndex(i, 0) == 16 && SettingsModel.FromIndex(i, inn) == 4096);
            var neg = new SettingsModel.Row { Name = "d", Type = "float", Unit = "s", Min = -1, Max = 1e7, Step = 60 };
            Ok("Min -1 (a special value) is the first step, 0 the second", SettingsModel.FromIndex(neg, 0) == -1 && SettingsModel.ToIndex(neg, -1) == 0
               && SettingsModel.FromIndex(neg, 1) == 0 && SettingsModel.ToIndex(neg, 0) == 1 && SettingsModel.FromIndex(neg, 2) == 60);
            Ok("a day (86,400 s) shows within 1%", Math.Abs(SettingsModel.FromIndex(neg, SettingsModel.ToIndex(neg, 86400)) / 86400 - 1) < 0.01,
               SettingsModel.FromIndex(neg, SettingsModel.ToIndex(neg, 86400)).ToString());
            var empty = new SettingsModel.Row { Name = "e", Type = "float", Min = 5, Max = 5 };
            SettingsModel.Scale(empty, out _, out int en);
            Ok("an empty range: no steps, no throw", en == 0 && SettingsModel.FromIndex(empty, 0) == 5 && SettingsModel.ToIndex(empty, 5) == 0);
        }

        static void Looks()
        {
            Console.WriteLine("-- the look (the game's resources as grid lengths)");
            Ok("Pixel 516 -> 516", SettingsModel.LengthSpec(516, "Pixel", "x") == "516");
            Ok("Star 0.65 -> 0.65*", SettingsModel.LengthSpec(0.65, "Star", "x") == "0.65*");
            Ok("Auto -> Auto", SettingsModel.LengthSpec(1, "Auto", "x") == "Auto");
            Ok("NaN, negative, unknown unit -> the fallback", SettingsModel.LengthSpec(double.NaN, "Pixel", "15") == "15"
               && SettingsModel.LengthSpec(-1, "Pixel", "15") == "15" && SettingsModel.LengthSpec(3, null, "15") == "15");
            Ok("15.5 px: invariant digits", SettingsModel.LengthSpec(15.5, "Pixel", "x") == "15.5");
            string cols = SettingsModel.RowColumns(300.4, "15", "516");
            Ok("a row: label (fixed), gap, control, value Auto, the rest *", cols == "300,15,516,Auto,*", cols);
            Ok("the fallbacks are the game's values", SettingsModel.RowColumns(300, SettingsModel.SpaceWidthFallback, SettingsModel.ValueWidthFallback) == cols);

            Console.WriteLine("-- the label column (one width per section: every control at one x)");
            var labels = new[] { "Wind", "Off-centre thrust turns ships", "Aerodynamics" };
            double est = SettingsModel.LabelWidth(labels, null, 20, 32);
            Ok("estimated: the longest label's characters x 0.55 em + the margins", est == Math.Ceiling(29 * 0.55 * 20 + 32), est.ToString());
            Ok("no font size: the fallback size", SettingsModel.LabelWidth(labels, null, 0, 32) == est && SettingsModel.LabelWidth(labels, null, double.NaN, 32) == est);
            Ok("measured: the widest measured + the margins", SettingsModel.LabelWidth(labels, s => s.Length * 10.0, 20, 32) == 29 * 10 + 32);
            Ok("a failed measurement (NaN, 0) falls back to the estimate for that label",
               SettingsModel.LabelWidth(labels, s => s == "Wind" ? 40 : double.NaN, 20, 32) == est
               && SettingsModel.LabelWidth(labels, s => 0, 20, 32) == est);
            Ok("short labels: the minimum", SettingsModel.LabelWidth(new[] { "Wind" }, null, 20, 32) == SettingsModel.LabelMinWidth);
            Ok("a very long label: the maximum", SettingsModel.LabelWidth(new[] { new string('x', 200) }, null, 20, 32) == SettingsModel.LabelMaxWidth);
            Ok("no labels (or nulls): the minimum, no throw", SettingsModel.LabelWidth(null, null, 20, 32) == SettingsModel.LabelMinWidth
               && SettingsModel.LabelWidth(new string[] { null, "" }, null, 20, 32) == SettingsModel.LabelMinWidth);
            Ok("negative or infinite margins are ignored", SettingsModel.LabelWidth(labels, s => 300, 20, -5) == 300
               && SettingsModel.LabelWidth(labels, s => 300, 20, double.PositiveInfinity) == 300);
        }
    }
}
