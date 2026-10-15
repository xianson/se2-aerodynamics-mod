using System;
using System.Collections.Generic;
using System.Globalization;

#pragma warning disable
namespace AeroMod;

/// <summary>
/// THE SETTINGS MENU'S MODEL (settings DESIGN.md W4, 2026-10-15): what the in-game "Mods" tab shows, from the mod's settings
/// registry - its groups, which rows are on it (Basic) or under a group's collapsed "Advanced" (Advanced; Hidden never), what
/// control each row gets (a switch, a choice, a slider), a slider's steps, a value's text, why a row is locked, and when a
/// control's edit is applied. No engine and no Avalonia here: the offline tests (Tests/SettingsUiTests) run it over the real
/// registry. The tab itself is SettingsTab.cs; the controls, AvaloniaReflect.cs.
///
/// SHARED: written once in the Orbital mod (Scripts/Simulation/Ui/), copied into the Aerodynamics Mod by
/// tools/sync_settings_ui.sh (only the namespace line differs; tools/check_contract.sh checks the copies). Edit it here.
///
/// THE MENU'S DEFAULTS (the user's answers to DESIGN.md's open questions, 2026-10-15) - change them here:
///  1. ONE shared "Mods" tab, an expander per mod: TabHeader / TabTag (the cross-mod contract: SettingsTab.cs).
///  2. No difficulty presets (none are built).
///  3. Basic rows shown, an "Advanced" expander per group, collapsed: ShowAdvanced / AdvancedExpanded; Hidden never shown.
///  4. Fairness settings stay as registered (scope is the registry's).
///  5. No key bindings (no "key" rows).
///  6. World rows editable only where the mod's CanEditWorld(); else read-only with HostOnly; a host-file-forced row locked
///     with "forced by <path>": LockReason.
///  7. Settings only inside a world (mods do not run in the main menu); the tab's note says world.cfg holds host defaults:
///     TabNote.
/// </summary>
public static class SettingsModel
{
    // ── the defaults ──
    /// <summary>1. The shared tab: its header, and its Tag (the contract every mod finds it by).</summary>
    public const string TabHeader = "Mods", TabTag = "ModSettings/v1";
    /// <summary>3. Advanced rows on the menu at all (false: Basic only, Advanced left to the files), and their expander open.</summary>
    public static bool ShowAdvanced = true, AdvancedExpanded = false;
    /// <summary>The bool rows' control: "CheckBox" or "ToggleSwitch" (both Avalonia ToggleButtons: IsChecked).</summary>
    public static string BoolControl = "CheckBox";
    /// <summary>6. A world row's reason where this process may not change world settings.</summary>
    public const string HostOnly = "Only the host can change world settings.";
    /// <summary>7. The tab's note (by whichever mod made the tab): {0} is the settings folder.</summary>
    public const string TabNote = "Mod settings apply at once. World settings are saved with the world (on its next save) and are the same for every player; "
        + "each mod's <Mod>.world.cfg in {0} holds the host's defaults - its uncommented lines win over every world's own. "
        + "This-PC settings are in each mod's <Mod>.client.cfg. Both files can also be edited while playing.";
    /// <summary>A section's hint while a world setting changed since the world was loaded or saved.</summary>
    public const string WorldUnsavedHint = "World changes take effect for everyone in this world now, and will save with the world on its next save.";
    /// <summary>A slider is linear up to this many steps; over it, logarithmic in LogSteps (Scale).</summary>
    public const int MaxSliderSteps = 200, LogSteps = 240;

    /// <summary>The look (2026-10-15, after the first in-game screenshot: a light-grey slab, a raw slider number, a small
    /// centred header): the game's own style classes and resources (its Options views' - AudioView, GraphicsView,
    /// OptionsScreen), each with the value it had in the game's build, used when the lookup fails.
    /// A row is [label | SpaceWidth | ValueWidth: the control | its value | the rest], the label column one width for the
    /// whole section (SharedSizeGroup LabelGroup): labels right-aligned against the controls, nothing stretched to the left.</summary>
    public const string SectionHeaderClass = "OptionTitle", GroupHeaderClass = "SubTitle", LabelClass = "OptionSubTitle",
                        SmallClass = "Small", BrightClass = "Bright", LabelGroup = "ModSettingsLabel";
    /// <summary>The game Slider template's value box (its raw Value: our sliders run on a step index), hidden.</summary>
    public const string SliderValuePart = "PART_NumericUpDown";
    public const string SpaceWidthKey = "SpaceWidth", SpaceWidthFallback = "15", ValueWidthKey = "ValueWidth", ValueWidthFallback = "516";
    /// <summary>Text brushes: notes and paths (muted), a lock or the unsaved-world hint (warning), a refusal (error).</summary>
    public const string MutedBrushKey = "Interactive-Primary-Disabled", MutedFallback = "Gray", WarningBrushKey = "Warning",
                        WarningFallback = "Goldenrod", ErrorBrushKey = "Error", ErrorFallback = "OrangeRed";

    /// <summary>A row's columns: label (Auto; shared), the gap, the control, the value (Auto), the rest (*).</summary>
    public static string RowColumns(string space, string value) => "Auto," + space + "," + value + ",Auto,*";

    /// <summary>A grid length as a ColumnDefinitions spec piece: Pixel 516 -> "516", Star 0.65 -> "0.65*", Auto -> "Auto";
    /// anything else (NaN, a negative, an unknown unit) -> fallback.</summary>
    public static string LengthSpec(double value, string unit, string fallback)
    {
        if (unit == "Auto") return "Auto";
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) return fallback;
        string v = value.ToString("R", CultureInfo.InvariantCulture);
        if (unit == "Pixel") return v;
        if (unit == "Star") return v + "*";
        return fallback;
    }

    /// <summary>One registered setting, as the menu needs it (the registry's Info and UiInfo).</summary>
    public sealed class Row
    {
        public string Name, Type, Scope, Label, Group, Tier, Unit = "", Doc, Note;
        public double Min, Max, Default, Step;
        public string[] Choices;
        public int Order;
        /// <summary>A logarithmic slider's values (Table; made once).</summary>
        public double[] LogTable;

        /// <summary>The control: "bool" (a switch), "choice" (an int with a label per value Min..Max) or "number" (a slider).</summary>
        public string Kind => Type == "bool" ? "bool"
            : Type == "int" && Choices != null && Choices.Length == (int)Math.Round(Max - Min) + 1 ? "choice" : "number";
        public bool World => Scope == "world";
    }

    /// <summary>A menu section: its rows on the menu (Basic) and under its "Advanced", in registration order.</summary>
    public sealed class Group
    {
        public string Name;
        /// <summary>"world", "client", or "mixed" (both in one group).</summary>
        public string Scope;
        public readonly List<Row> Basic = new List<Row>(), Advanced = new List<Row>();
        public int Count => Basic.Count + Advanced.Count;
    }

    /// <summary>The menu: groups in the order their first setting was registered; Hidden rows (and Advanced ones, with
    /// ShowAdvanced off) left out; a row with no group goes to "Other".</summary>
    public static List<Group> Build(List<Row> rows)
    {
        var sorted = new List<Row>(rows);
        sorted.Sort((a, b) => a.Order.CompareTo(b.Order));
        var groups = new List<Group>();
        var by = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in sorted)
        {
            bool adv = r.Tier == "advanced";
            if (r.Tier == "hidden" || (adv && !ShowAdvanced)) continue;
            string name = string.IsNullOrEmpty(r.Group) ? "Other" : r.Group;
            if (!by.TryGetValue(name, out var g)) { g = new Group { Name = name, Scope = r.Scope }; by[name] = g; groups.Add(g); }
            else if (g.Scope != r.Scope) g.Scope = "mixed";
            (adv ? g.Advanced : g.Basic).Add(r);
        }
        return groups;
    }

    /// <summary>Rows the menu shows (the count a harness compares with the tab's).</summary>
    public static int Shown(List<Row> rows)
    {
        int n = 0;
        foreach (var r in rows) if (r.Tier != "hidden" && (r.Tier != "advanced" || ShowAdvanced)) n++;
        return n;
    }

    /// <summary>Why a row cannot be changed here (null: it can): a world setting the host file forces (its path), or any world
    /// setting where this process may not edit the world. Client settings are always this player's own.</summary>
    public static string LockReason(Row r, bool canEditWorld, bool forced, string worldFile)
    {
        if (!r.World) return null;
        if (forced) return "Forced by " + worldFile;
        if (!canEditWorld) return HostOnly;
        return null;
    }

    // ── a number's slider: index k = 0..steps. LINEAR: value Min + k * step (the last index is Max exactly). Over
    //    MaxSliderSteps steps (a planet day of -1 s .. 10,000,000 s in minutes): LOGARITHMIC - Min (and 0, below the first
    //    step), then LogSteps rising by a constant ratio to Max, each rounded to about two significant digits on the step, repeats
    //    dropped (Table). The files keep any value; the slider shows the nearest. ──

    /// <summary>The slider's step (the registered Step - an int's at least 1 - or a 1-2-5 step of about a hundredth of the
    /// range when none is given) and its step count (the table's, when logarithmic).</summary>
    public static void Scale(Row r, out double step, out int steps)
    {
        double range = r.Max - r.Min;
        step = BaseStep(r);
        if (!(range > 0)) { steps = 0; return; }
        if (IsLog(r)) { steps = Table(r).Length - 1; return; }
        steps = (int)Math.Ceiling(range / step - 1e-9);
    }

    static double BaseStep(Row r)
    {
        double range = r.Max - r.Min;
        double s = r.Step > 0 ? r.Step : r.Type == "float" && range > 0 ? Nice(range / 100) : 1;
        return r.Type == "float" ? s : Math.Max(1, Math.Round(s));
    }

    /// <summary>The slider is logarithmic: its linear steps would be over MaxSliderSteps (and Max is above 0).</summary>
    public static bool IsLog(Row r)
    {
        double range = r.Max - r.Min;
        return range > 0 && r.Max > 0 && range / BaseStep(r) > MaxSliderSteps;
    }

    /// <summary>A logarithmic slider's values, rising (made once per row).</summary>
    public static double[] Table(Row r)
    {
        if (r.LogTable != null) return r.LogTable;
        double s = BaseStep(r);
        var t = new List<double>();
        double lo = r.Min > 0 ? r.Min : Math.Min(r.Max, s);
        if (r.Min < lo) { t.Add(r.Min); if (r.Min < 0 && lo > 0) t.Add(0); }
        for (int k = 0; k <= LogSteps; k++)
        {
            double v = k == LogSteps ? r.Max : lo * Math.Pow(r.Max / lo, k / (double)LogSteps);
            if (k > 0 && k < LogSteps)
            {
                double sig = Math.Pow(10, Math.Floor(Math.Log10(v)) - 1);   // (about two significant digits, on the step)
                double q = s * Math.Max(1, Math.Round(sig / s));
                v = Math.Round(v / q) * q;
                v = r.Type == "float" ? Math.Round(v, Decimals(s)) : Math.Round(v);
            }
            if (v > r.Max) v = r.Max;
            if (t.Count == 0 || v > t[t.Count - 1]) t.Add(v);
        }
        if (t[t.Count - 1] < r.Max) t.Add(r.Max);
        return r.LogTable = t.ToArray();
    }

    /// <summary>The smallest 1, 2 or 5 x 10^n at or above x.</summary>
    public static double Nice(double x)
    {
        if (!(x > 0)) return 1;
        double p = Math.Pow(10, Math.Floor(Math.Log10(x)));
        foreach (double m in Mantissas) if (m * p >= x * (1 - 1e-12)) return m * p;
        return 10 * p;
    }
    static readonly double[] Mantissas = { 1, 2, 5, 10 };

    /// <summary>The slider index nearest a value.</summary>
    public static int ToIndex(Row r, double v)
    {
        Scale(r, out double s, out int n);
        if (n == 0) return 0;
        if (v >= r.Max) return n;
        if (v <= r.Min) return 0;
        if (IsLog(r))
        {
            var t = Table(r);
            int lo = 0, hi = t.Length - 1;   // (the last entry at or below v, then the nearer of it and the next)
            while (hi - lo > 1) { int mid = (lo + hi) / 2; if (t[mid] <= v) lo = mid; else hi = mid; }
            return v - t[lo] <= t[hi] - v ? lo : hi;
        }
        double k = Math.Round((v - r.Min) / s);
        return k < 0 ? 0 : k > n ? n : (int)k;
    }

    /// <summary>The value at a slider index.</summary>
    public static double FromIndex(Row r, int k)
    {
        Scale(r, out double s, out int n);
        if (k <= 0) return r.Min;
        if (k >= n) return r.Max;
        if (IsLog(r)) return Table(r)[k];
        double v = r.Min + k * s;
        if (r.Type != "float") return Math.Round(v);
        return Math.Round(v, Decimals(s));
    }

    /// <summary>Digits after the point that show a step exactly (0.25: 2; 60: 0), at most 10.</summary>
    public static int Decimals(double step)
    {
        if (!(step > 0)) return 0;
        for (int d = 0; d < 10; d++)
        {
            double x = step * Math.Pow(10, d);
            if (Math.Abs(x - Math.Round(x)) < 1e-9 * Math.Max(1, x)) return d;
        }
        return 10;
    }

    /// <summary>A number's text with its unit ("12.5 s", "1.5×", "86,400 s"): the registered step's digits (a coarse slider
    /// step does not hide them), at most 6.</summary>
    public static string Format(Row r, double v)
    {
        Scale(r, out double s, out _);
        int d = r.Type == "float" ? Math.Min(6, Decimals(r.Step > 0 ? r.Step : s)) : 0;
        string t = v.ToString(Math.Abs(v) >= 10000 ? "N" + d : "F" + d, CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(r.Unit)) return t;
        return r.Unit == "×" ? t + "×" : t + " " + r.Unit;
    }

    /// <summary>The control's value for a setting value: bool 0/1, choice index, slider index.</summary>
    public static double ToControl(Row r, double v)
    {
        switch (r.Kind)
        {
            case "bool": return v != 0 ? 1 : 0;
            case "choice": return Math.Round(v - r.Min);
            default: return ToIndex(r, v);
        }
    }

    /// <summary>The setting value for a control's value.</summary>
    public static double FromControl(Row r, double c)
    {
        switch (r.Kind)
        {
            case "bool": return c != 0 ? 1 : 0;
            case "choice": return r.Min + Math.Round(c);
            default: return FromIndex(r, (int)Math.Round(c));
        }
    }

    /// <summary>
    /// One control's edits, read about 4 times a second: Shown is the value the menu last put in the control (or saw
    /// applied). A changed switch or choice applies at once; a slider's change applies once it has held still for one read
    /// (not every position of a drag - each would be a setting change, a log line and a file write).
    /// </summary>
    public sealed class Edit
    {
        public double Shown = double.NaN;
        double _pending = double.NaN;
        public readonly bool Settle;
        public Edit(bool settle) { Settle = settle; }

        /// <summary>The control reads this: true with the value to apply when the player changed it (now: the last read -
        /// the tab left or the screen closed - a moved slider applies without waiting).</summary>
        public bool Observe(double read, out double apply, bool now = false)
        {
            apply = read;
            // (nothing shown yet: the control's own starting value is no edit)
            if (double.IsNaN(read) || double.IsNaN(Shown) || read == Shown) { _pending = double.NaN; return false; }
            if (Settle && !now && read != _pending) { _pending = read; return false; }
            _pending = double.NaN;
            return true;
        }

        /// <summary>A value is pending (a slider moved, not yet held still).</summary>
        public bool Moving => !double.IsNaN(_pending);

        /// <summary>The menu put this in the control (a refresh, a snap back, or the applied edit).</summary>
        public void Showing(double c) { Shown = c; _pending = double.NaN; }
    }
}
