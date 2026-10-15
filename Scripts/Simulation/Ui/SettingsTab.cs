using System;
using System.Collections;
using System.Collections.Generic;
using Keen.Game2.Simulation.GameSystems.UserMessages;
using Keen.VRage.Library.Localization;

#pragma warning disable
namespace AeroMod;

/// <summary>
/// THE IN-GAME SETTINGS MENU (settings DESIGN.md W4, 2026-10-15): a "Mods" tab in the game's own Options screen (pause menu
/// -> Settings -> Mods), generated from the mod's settings registry (SettingsModel.cs: groups, tiers, controls, locks).
///  - ONE tab for every mod, found by its Tag: the first mod to see the Options screen open adds a TabItem (Tag
///    "ModSettings/v1", Content: a ScrollViewer over a StackPanel tagged "ModSettings/v1/host", whose first child is the
///    tab's note); each mod then adds its own Expander (Tag "ModSettings/v1/&lt;ModId&gt;") to that panel, sorted by Tag, if
///    its own is not there yet. That Tag contract is all the mods share (no code): a third mod can add a section the same way.
///  - Rows: a switch (CheckBox), a choice (ComboBox of the registry's labels) or a slider (Slider + the value and unit);
///    the setting's doc is the Options screen's own description panel (the game's DescriptionControl; a ToolTip without it).
///    Per group: an "Advanced" expander (collapsed), "Reset to defaults", and a line for a refusal's reason. A world row is
///    read-only where this process may not edit the world, and locked when the host's world.cfg forces it (the reason under
///    it); a section line says when world changes wait for the world's next save.
///  - Wiring by polling, not events (the mods' style; no Avalonia event type can be named): while the tab is SELECTED, about
///    4 times a second, each control's value is read and a change goes through the registry's Set (refused: the control
///    snaps back and the reason shows); a registry Version change (the files, the API, a reset) refreshes the controls.
///  - Threads: Tick (any game thread, every frame: a timestamp compare; one caller per 250 ms) finds the game's
///    SharedUIComponent and posts UiTick to Avalonia's UI thread, where everything else runs: find the open OptionsScreen
///    (SharedUIComponent._activeScreenHandles), attach (once per screen instance), pump.
///  - Fails closed: anything missing (a type, PART_TabControl, the dispatcher) leaves the Options screen as the game made it,
///    puts the reason in Status, logs it once, and shows ONE toast per session pointing to the .cfg files (W5 b).
/// Reflection: AvaloniaReflect.cs (every member listed in docs/REFLECTION_AND_WHITELIST.md).
/// SHARED: written once in the Orbital mod, copied into the Aerodynamics Mod by tools/sync_settings_ui.sh (only the
/// namespace line differs; tools/check_contract.sh checks it). The per-mod part is SettingsTab.Mod.cs. Edit it here.
/// </summary>
public static partial class SettingsTab
{
    const string HostTag = SettingsModel.TabTag + "/host", NoteTag = SettingsModel.TabTag + "/note";
    /// <summary>This mod's section (the Expander), its body (Tag: "... rows=N", the rows it shows - for a harness), its rows.</summary>
    const string SectionTag = SettingsModel.TabTag + "/" + ModId, BodyTag = SectionTag + "/body rows=", RowTag = SectionTag + "/row/",
                 ResetTag = SectionTag + "/reset/";

    /// <summary>What the tab did last (diagnostics: "attached", "failed: ..."), and the last harness report (DevReport).</summary>
    public static string Status = "not attached", Report = "";

    // ── the game thread ──

    static long _next;
    static Session _session;
    static volatile object _shared;
    static long _sharedRetry;
    static int _sharedFails;
    static readonly Action _uiTick = UiTick;
    static volatile string _toastWhy;
    static bool _toastShown;

    /// <summary>Every frame from a game thread (cheap): every 250 ms, one caller posts the UI's check.</summary>
    public static void Tick(Session session) { if (Claim()) Ticked(session); }

    /// <summary>This frame's caller is the one for this 250 ms (the effects jobs call in parallel: one wins).</summary>
    internal static bool Claim()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp(), due = System.Threading.Interlocked.Read(ref _next);
        if (now < due) return false;
        return System.Threading.Interlocked.CompareExchange(ref _next, now + System.Diagnostics.Stopwatch.Frequency / 4, due) == due;
    }

    internal static void Ticked(Session session)
    {
        if (session == null) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!ReferenceEquals(session, _session))
        {
            _session = session; _shared = null; _sharedRetry = 0; _sharedFails = 0; _toastShown = false;
            lock (_warned) _warned.Clear();
        }
        if (_shared == null)
        {
            if (now < _sharedRetry) return;
            _shared = AvaloniaReflect.SharedUi(session);
            // (every 5 s; the toast once it has been missing for 15 s - not while the session is still coming up)
            if (_shared == null) { _sharedRetry = now + 5 * System.Diagnostics.Stopwatch.Frequency; Fail("no SharedUIComponent (" + AvaloniaReflect.Why + ")", ++_sharedFails >= 3); return; }
        }
        var why = _toastWhy;
        if (why != null && !_toastShown) { _toastShown = true; _toastWhy = null; Toast(session, why); }
        if (!AvaloniaReflect.Post(_uiTick)) Fail("no UI dispatcher (" + AvaloniaReflect.Why + ")", true);
    }

    static readonly HashSet<string> _warned = new HashSet<string>();
    /// <summary>A failure: Status, one log line per reason per session, and (toast) the session's one toast.</summary>
    static void Fail(string why, bool toast)
    {
        Status = "failed: " + why;
        bool first;
        lock (_warned) first = _warned.Add(why);
        if (first) Log.Default?.Warning($"{LogTag} settings tab: not added to the Options screen: {why}. The settings files still work: {ClientPath}, {WorldPath}");
        if (toast && !_toastShown) _toastWhy = why;
    }

    static void Toast(Session session, string why)
    {
        try
        {
            var msgs = session.TryGet<IUserMessages>();
            msgs?.DisplayMessage(new UserMessage
            {
                TitleId = LocKey.FromString(ModTitle + " settings"),
                ContentId = LocKey.FromString("The in-game menu (Options > Mods) could not be added (" + why + "). Edit " + ClientPath
                    + " (this PC) or " + WorldPath + " (host defaults): they apply while you play."),
                Type = UserMessageType.Game, TextTimeout = TimeSpan.FromSeconds(12),
                MessageKey = Keen.VRage.Library.Utils.StringId.Get("modsettings." + ModId),
            });
        }
        catch { }
    }

    // ── the UI thread ──

    static object _screen, _tab, _section, _failedScreen, _worldHint;
    static int _seenVersion = int.MinValue;
    static bool _wasSelected;
    static volatile bool _reportWanted;
    static readonly List<SettingsModel.Row> _rows = new List<SettingsModel.Row>();
    static readonly List<RowUi> _rowUi = new List<RowUi>();
    static readonly Dictionary<string, GroupUi> _groupUi = new Dictionary<string, GroupUi>();

    sealed class RowUi
    {
        public SettingsModel.Row R; public SettingsModel.Edit E; public GroupUi G;
        public object Control, Value, Sub;
        public string LockShown = "?";
    }
    sealed class GroupUi { public string Name; public object Reason; }

    static void UiTick()
    {
        try
        {
            var screen = AvaloniaReflect.ActiveScreen(_shared, "OptionsScreen");
            if (screen == null)
            {
                if (_section != null && _wasSelected) Pump(true);   // (the screen closed: an edit not yet applied still is)
                if (_screen != null) Drop();
                _failedScreen = null; _wasSelected = false;
                if (_reportWanted) { _reportWanted = false; Report = MakeReport(); }
                return;
            }
            if (!ReferenceEquals(screen, _screen)) { Drop(); _screen = screen; }
            if (Attached())
            {
                // (only while the tab shows; once more as it is left - an edit not yet applied still is)
                bool sel = AvaloniaReflect.Get(_tab, "IsSelected") is bool b && b;
                if (sel || _wasSelected) Pump(!sel);
                _wasSelected = sel;
            }
            if (_reportWanted) { _reportWanted = false; Report = MakeReport(); }
        }
        catch (Exception e)
        {
            // (while attaching: this screen is left as the game made it, and the toast says so; while pumping: logged once)
            bool attaching = _section == null;
            if (attaching) _failedScreen = _screen;
            Fail("tab: " + (e.InnerException ?? e).Message, attaching);
        }
    }

    /// <summary>The Options screen closed (or another opened): nothing of it kept.</summary>
    static void Drop()
    {
        _screen = _tab = _section = _worldHint = null;
        _rowUi.Clear(); _groupUi.Clear();
        _seenVersion = int.MinValue; _wasSelected = false;
    }

    /// <summary>Our section is on this screen's tab: added now if it is not (once per screen instance - a failure is
    /// tried again on the next opening).</summary>
    static bool Attached()
    {
        if (_section != null && _tab != null)
        {
            var host = Host(_tab);
            if (host != null && ReferenceEquals(AvaloniaReflect.Tagged(AvaloniaReflect.List(host, "Children"), SectionTag), _section)) return true;
            _section = null;   // (gone: someone rebuilt the tab - add ours again)
        }
        if (ReferenceEquals(_failedScreen, _screen)) return false;
        string why = Attach(_screen);
        if (why == null) { Status = "attached (" + _rowUi.Count + " rows)"; return true; }
        _failedScreen = _screen;
        Drop(); _screen = _failedScreen;
        Fail(why, true);
        return false;
    }

    static object Host(object tab)
    {
        var host = AvaloniaReflect.Get(AvaloniaReflect.Get(tab, "Content"), "Content");
        return AvaloniaReflect.Get(host, "Tag") as string == HostTag ? host : null;
    }

    /// <summary>Find or make the shared tab, then add this mod's section. Null, or why not.</summary>
    static string Attach(object screen)
    {
        // (every control type first: nothing is added to the game's screen unless all of it can be built)
        foreach (string t in Needed)
            if (AvaloniaReflect.Control(t) == null) return "no Avalonia.Controls." + t;
        var tc = AvaloniaReflect.Field(screen, "PART_TabControl");
        if (tc == null) return "OptionsScreen.PART_TabControl not found";
        var items = AvaloniaReflect.List(tc, "Items");
        if (items == null) return "the Options tabs have no Items";
        var tab = AvaloniaReflect.Tagged(items, SettingsModel.TabTag);
        if (tab == null)
        {
            tab = MakeTab();
            if (tab == null) return "the tab could not be made (" + AvaloniaReflect.Why + ")";
            items.Add(tab);
        }
        var host = Host(tab);
        var children = AvaloniaReflect.List(host, "Children");
        if (children == null) return "the Mods tab has no host panel";
        var old = AvaloniaReflect.Tagged(children, SectionTag);
        if (old != null) children.Remove(old);   // (another load of this mod made it: its controls are no longer pumped)
        var section = BuildSection();
        if (section == null) return "the section could not be built (" + AvaloniaReflect.Why + ")";
        // (sections sorted by Tag after the note: the same order whichever mod came first)
        int at = children.Count;
        for (int k = 0; k < children.Count; k++)
            if (AvaloniaReflect.Get(children[k], "Tag") is string t && t != NoteTag && string.CompareOrdinal(t, SectionTag) > 0) { at = k; break; }
        children.Insert(at, section);
        _tab = tab; _section = section;
        // (the values in before anything reads the controls: a control's own starting value is no edit)
        _seenVersion = SettingsVersion;
        foreach (var u in _rowUi) Show(u);
        if (!_loggedAttach) { _loggedAttach = true; Log.Default?.Info($"{LogTag} settings tab: added to the Options screen ({_rowUi.Count} rows)"); }
        return null;
    }
    static bool _loggedAttach;
    static readonly string[] Needed = { "TabItem", "ScrollViewer", "StackPanel", "Expander", "TextBlock", "Grid", "CheckBox", "ComboBox", "Slider", "Button", "ToolTip", "ColumnDefinitions", "RowDefinitions" };

    static object MakeTab()
    {
        var tab = AvaloniaReflect.New("TabItem");
        var scroll = AvaloniaReflect.New("ScrollViewer");
        var host = AvaloniaReflect.New("StackPanel");
        if (tab == null || scroll == null || host == null) return null;
        AvaloniaReflect.Set(tab, "Header", SettingsModel.TabHeader);
        AvaloniaReflect.Set(tab, "Tag", SettingsModel.TabTag);
        AvaloniaReflect.Set(scroll, "Tag", SettingsModel.TabTag + "/scroll");
        AvaloniaReflect.Set(host, "Tag", HostTag);
        AvaloniaReflect.Set(host, "Margin", AvaloniaReflect.Thickness(0, 0, 24, 24));
        var note = Text(string.Format(SettingsModel.TabNote, Folder), null);
        AvaloniaReflect.Set(note, "Tag", NoteTag);
        AvaloniaReflect.Set(note, "Margin", AvaloniaReflect.Thickness(0, 0, 0, 12));
        AvaloniaReflect.List(host, "Children")?.Add(note);
        if (!AvaloniaReflect.Set(scroll, "Content", host) || !AvaloniaReflect.Set(tab, "Content", scroll)) return null;
        return tab;
    }

    // ── the section ──

    static object BuildSection()
    {
        _rowUi.Clear(); _groupUi.Clear();
        Gather(_rows);
        var groups = SettingsModel.Build(_rows);
        var exp = AvaloniaReflect.New("Expander");
        var body = AvaloniaReflect.New("StackPanel");
        if (exp == null || body == null) return null;
        AvaloniaReflect.Set(exp, "Header", ModTitle);
        AvaloniaReflect.Set(exp, "Tag", SectionTag);
        AvaloniaReflect.Set(exp, "IsExpanded", true);
        AvaloniaReflect.Set(exp, "HorizontalAlignment", AvaloniaReflect.EnumValue("Avalonia.Layout.HorizontalAlignment", "Stretch"));
        AvaloniaReflect.Set(exp, "HorizontalContentAlignment", AvaloniaReflect.EnumValue("Avalonia.Layout.HorizontalAlignment", "Stretch"));
        AvaloniaReflect.Set(exp, "Margin", AvaloniaReflect.Thickness(0, 4, 0, 8));
        AvaloniaReflect.Set(body, "Tag", BodyTag + SettingsModel.Shown(_rows));
        var list = AvaloniaReflect.List(body, "Children");
        if (list == null) return null;
        list.Add(Text("This PC: " + ClientPath + "   ·   Host defaults: " + WorldPath, "Gray"));
        _worldHint = Text(SettingsModel.WorldUnsavedHint, "Goldenrod");
        AvaloniaReflect.Set(_worldHint, "IsVisible", false);
        list.Add(_worldHint);
        foreach (var g in groups)
        {
            var gu = new GroupUi { Name = g.Name };
            _groupUi[g.Name] = gu;
            var head = Text(g.Name + (g.Scope == "world" ? "  -  this world, every player" : g.Scope == "client" ? "  -  this PC" : ""), null);
            AvaloniaReflect.Set(head, "FontWeight", AvaloniaReflect.EnumValue("Avalonia.Media.FontWeight", "Bold"));
            AvaloniaReflect.Set(head, "Margin", AvaloniaReflect.Thickness(0, 16, 0, 4));
            list.Add(head);
            foreach (var r in g.Basic) { var row = MakeRow(r, gu); if (row == null) return null; list.Add(row); }
            if (g.Advanced.Count > 0)
            {
                var adv = AvaloniaReflect.New("Expander");
                var advBody = AvaloniaReflect.New("StackPanel");
                if (adv == null || advBody == null) return null;
                AvaloniaReflect.AddClass(adv, "Flat");   // (the game's own look for an inner expander: AudioView)
                AvaloniaReflect.Set(adv, "Header", "Advanced");
                AvaloniaReflect.Set(adv, "Tag", SectionTag + "/advanced/" + g.Name);
                AvaloniaReflect.Set(adv, "IsExpanded", SettingsModel.AdvancedExpanded);
                AvaloniaReflect.Set(adv, "HorizontalAlignment", AvaloniaReflect.EnumValue("Avalonia.Layout.HorizontalAlignment", "Stretch"));
                AvaloniaReflect.Set(adv, "HorizontalContentAlignment", AvaloniaReflect.EnumValue("Avalonia.Layout.HorizontalAlignment", "Stretch"));
                var advList = AvaloniaReflect.List(advBody, "Children");
                foreach (var r in g.Advanced) { var row = MakeRow(r, gu); if (row == null) return null; advList?.Add(row); }
                AvaloniaReflect.Set(adv, "Content", advBody);
                list.Add(adv);
            }
            var foot = AvaloniaReflect.New("StackPanel");
            var reset = AvaloniaReflect.New("Button");
            if (foot == null || reset == null) return null;
            AvaloniaReflect.Set(foot, "Orientation", AvaloniaReflect.EnumValue("Avalonia.Layout.Orientation", "Horizontal"));
            AvaloniaReflect.Set(foot, "Margin", AvaloniaReflect.Thickness(0, 6, 0, 0));
            AvaloniaReflect.Set(reset, "Content", "Reset to defaults");
            AvaloniaReflect.Set(reset, "Tag", ResetTag + g.Name);
            if (!AvaloniaReflect.HookClick(reset)) return null;
            gu.Reason = Text("", "OrangeRed");
            AvaloniaReflect.Set(gu.Reason, "VerticalAlignment", AvaloniaReflect.EnumValue("Avalonia.Layout.VerticalAlignment", "Center"));
            AvaloniaReflect.Set(gu.Reason, "Margin", AvaloniaReflect.Thickness(16, 0, 0, 0));
            var fl = AvaloniaReflect.List(foot, "Children");
            fl?.Add(reset); fl?.Add(gu.Reason);
            list.Add(foot);
        }
        AvaloniaReflect.Clicked = Clicked;
        if (!AvaloniaReflect.Set(exp, "Content", body)) return null;
        return exp;
    }

    /// <summary>A text line (wrapping; brush: one of Avalonia's named brushes, or null for the theme's).</summary>
    static object Text(string s, string brush)
    {
        var t = AvaloniaReflect.New("TextBlock");
        AvaloniaReflect.Set(t, "Text", s);
        AvaloniaReflect.Set(t, "TextWrapping", AvaloniaReflect.EnumValue("Avalonia.Media.TextWrapping", "Wrap"));
        if (brush != null) AvaloniaReflect.Set(t, "Foreground", AvaloniaReflect.Brush(brush));
        return t;
    }

    /// <summary>One row: a grid [label | gap | control | value] over [the lock reason or note], in the game's
    /// DescriptionControl (the screen's description panel shows the doc) - a ToolTip when that type is missing.</summary>
    static object MakeRow(SettingsModel.Row r, GroupUi g)
    {
        var grid = AvaloniaReflect.New("Grid");
        if (grid == null) return null;
        AvaloniaReflect.Set(grid, "ColumnDefinitions", AvaloniaReflect.Definitions("ColumnDefinitions", "*,24,360,170"));
        AvaloniaReflect.Set(grid, "RowDefinitions", AvaloniaReflect.Definitions("RowDefinitions", "Auto,Auto"));
        AvaloniaReflect.Set(grid, "Margin", AvaloniaReflect.Thickness(0, 3, 0, 3));
        var cells = AvaloniaReflect.List(grid, "Children");
        var label = Text(r.Label, null);
        AvaloniaReflect.AddClass(label, "OptionSubTitle");
        AvaloniaReflect.Set(label, "HorizontalAlignment", AvaloniaReflect.EnumValue("Avalonia.Layout.HorizontalAlignment", "Right"));
        AvaloniaReflect.Set(label, "VerticalAlignment", AvaloniaReflect.EnumValue("Avalonia.Layout.VerticalAlignment", "Center"));
        AvaloniaReflect.Place(label, 0, 0);
        cells?.Add(label);

        string kind = r.Kind;
        object control;
        if (kind == "bool") control = AvaloniaReflect.New(SettingsModel.BoolControl) ?? AvaloniaReflect.New("CheckBox");
        else if (kind == "choice")
        {
            control = AvaloniaReflect.New("ComboBox");
            var items = AvaloniaReflect.List(control, "Items");
            if (items == null) return null;
            foreach (var c in r.Choices) items.Add(c);
            AvaloniaReflect.Set(control, "HorizontalAlignment", AvaloniaReflect.EnumValue("Avalonia.Layout.HorizontalAlignment", "Stretch"));
        }
        else
        {
            control = AvaloniaReflect.New("Slider");
            SettingsModel.Scale(r, out _, out int steps);
            AvaloniaReflect.Set(control, "Minimum", 0.0);
            AvaloniaReflect.Set(control, "Maximum", (double)Math.Max(1, steps));
            AvaloniaReflect.Set(control, "SmallChange", 1.0);
            AvaloniaReflect.Set(control, "LargeChange", (double)Math.Max(1, steps / 10));
            AvaloniaReflect.Set(control, "TickFrequency", 1.0);
            AvaloniaReflect.Set(control, "IsSnapToTickEnabled", true);
        }
        if (control == null) return null;
        AvaloniaReflect.Set(control, "Tag", RowTag + r.Name);
        AvaloniaReflect.Set(control, "VerticalAlignment", AvaloniaReflect.EnumValue("Avalonia.Layout.VerticalAlignment", "Center"));
        AvaloniaReflect.Place(control, 2, 0);
        cells?.Add(control);

        object value = null;
        if (kind == "number")
        {
            value = Text("", null);
            AvaloniaReflect.Set(value, "VerticalAlignment", AvaloniaReflect.EnumValue("Avalonia.Layout.VerticalAlignment", "Center"));
            AvaloniaReflect.Set(value, "Margin", AvaloniaReflect.Thickness(12, 0, 0, 0));
            AvaloniaReflect.Place(value, 3, 0);
            cells?.Add(value);
        }
        var sub = Text(r.Note ?? "", "Gray");
        AvaloniaReflect.Set(sub, "IsVisible", r.Note != null);
        AvaloniaReflect.Place(sub, 2, 1, 2);
        cells?.Add(sub);

        _rowUi.Add(new RowUi { R = r, E = new SettingsModel.Edit(kind == "number"), G = g, Control = control, Value = value, Sub = sub });

        var dc = AvaloniaReflect.New(AvaloniaReflect.GameType("Keen.Game2.Client.UI.Library.Controls.ContentControls.DescriptionControl"));
        if (dc != null && AvaloniaReflect.Set(dc, "Content", grid) && AvaloniaReflect.Set(dc, "DescriptionKey", r.Doc ?? "")) return dc;
        AvaloniaReflect.SetTip(grid, r.Doc);
        return grid;
    }

    // ── the pump (the tab selected: ~4 Hz) ──

    /// <param name="last">The tab is being left or the screen closed: a moved slider applies without waiting.</param>
    static void Pump(bool last = false)
    {
        if (_seenVersion == int.MinValue) { _seenVersion = SettingsVersion; foreach (var u in _rowUi) Show(u); }   // (after a reset)
        bool canEdit = CanEditWorld();
        string worldPath = WorldPath;
        // the player's edits first (applied through the registry; a refusal snaps the control back)...
        foreach (var u in _rowUi)
        {
            var r = u.R;
            string reason = SettingsModel.LockReason(r, canEdit, r.World && IsForced(r.Name), worldPath);
            if (reason != u.LockShown)
            {
                u.LockShown = reason;
                AvaloniaReflect.Set(u.Control, "IsEnabled", reason == null);
                string sub = reason ?? r.Note;
                AvaloniaReflect.Set(u.Sub, "Text", sub ?? "");
                AvaloniaReflect.Set(u.Sub, "Foreground", AvaloniaReflect.Brush(reason != null ? "Goldenrod" : "Gray"));
                AvaloniaReflect.Set(u.Sub, "IsVisible", sub != null);
            }
            double read = ReadControl(u);
            if (u.E.Observe(read, out double c, last))
            {
                double v = SettingsModel.FromControl(r, c);
                string why = reason;
                if (why == null && Set(r.Name, v, out why)) { u.E.Showing(c); Say(u.G, ""); }
                else { Say(u.G, r.Label + ": " + why); Show(u); }
            }
            if (u.Value != null && !double.IsNaN(read) && u.E.Moving)   // (a slider being dragged: its value as it goes)
                AvaloniaReflect.Set(u.Value, "Text", SettingsModel.Format(r, SettingsModel.FromControl(r, read)));
        }
        // ...then what changed in the registry (these edits, the files, the API, a reset) into the controls
        int ver = SettingsVersion;
        if (ver != _seenVersion) { _seenVersion = ver; foreach (var u in _rowUi) Show(u); }
        AvaloniaReflect.Set(_worldHint, "IsVisible", WorldUnsaved);
    }

    /// <summary>The control's value: bool 0/1, choice index, slider index (NaN: none).</summary>
    static double ReadControl(RowUi u)
    {
        switch (u.R.Kind)
        {
            case "bool": return AvaloniaReflect.Get(u.Control, "IsChecked") is bool b ? (b ? 1 : 0) : double.NaN;
            case "choice": return AvaloniaReflect.Get(u.Control, "SelectedIndex") is int i && i >= 0 ? i : double.NaN;
            default: return AvaloniaReflect.Double(u.Control, "Value");
        }
    }

    /// <summary>The setting's current value into its control and value text.</summary>
    static void Show(RowUi u)
    {
        if (!TryGet(u.R.Name, out double v)) return;
        double c = SettingsModel.ToControl(u.R, v);
        if (ReadControl(u) != c)
            switch (u.R.Kind)
            {
                case "bool": AvaloniaReflect.Set(u.Control, "IsChecked", c != 0); break;
                case "choice": AvaloniaReflect.Set(u.Control, "SelectedIndex", (int)c); break;
                default: AvaloniaReflect.Set(u.Control, "Value", c); break;
            }
        u.E.Showing(c);
        if (u.Value != null) AvaloniaReflect.Set(u.Value, "Text", SettingsModel.Format(u.R, v));
    }

    static void Say(GroupUi g, string s) { if (g?.Reason != null) AvaloniaReflect.Set(g.Reason, "Text", s); }

    /// <summary>A button of this mod's section (AvaloniaReflect.Clicked; UI thread): "Reset to defaults".</summary>
    static void Clicked(object button)
    {
        if (!(AvaloniaReflect.Get(button, "Tag") is string tag) || !tag.StartsWith(ResetTag, StringComparison.Ordinal)) return;
        string group = tag.Substring(ResetTag.Length);
        _groupUi.TryGetValue(group, out var g);
        int n = ResetGroup(group, out string why);
        Say(g, why != null ? why : n == 0 ? "Already at the defaults." : n + (n == 1 ? " setting" : " settings") + " back to the default.");
        Log.Default?.Info($"{LogTag} settings tab: '{group}' reset ({n}{(why != null ? ", " + why : "")})");
        _seenVersion = int.MinValue;
    }

    // ── for a harness (the Orbital DevHarness 'settingsui') ──

    static IDisposable _devHandle;

    /// <summary>open: the Options screen (SharedUIComponent.OpenOptionsScreen, on the UI thread); select: our tab; flip
    /// &lt;setting&gt;: change that row's control as a player would (a switch flipped, the next choice, a slider one step) - the
    /// pump applies it while the tab is selected; close: the screen it opened; anything else: the last report (a new one is
    /// made on the UI thread's next check - ask again).</summary>
    public static string Dev(string line)
    {
        var shared = _shared;
        string[] w = (line ?? "").Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
        string what = w.Length > 0 ? w[0].ToLowerInvariant() : "report";
        switch (what)
        {
            case "open":
                if (shared == null) return "no SharedUIComponent yet";
                return AvaloniaReflect.Post(() =>
                {
                    try { _devHandle = shared.GetType().GetMethod("OpenOptionsScreen", Type.EmptyTypes)?.Invoke(shared, null) as IDisposable; Status = _devHandle != null ? "opened by the harness" : "OpenOptionsScreen not found"; }
                    catch (Exception e) { Status = "open failed: " + (e.InnerException ?? e).Message; }
                }) ? "opening the Options screen" : "no UI dispatcher";
            case "select":
                return AvaloniaReflect.Post(() =>
                {
                    var tc = AvaloniaReflect.Field(_screen, "PART_TabControl");
                    if (tc != null && _tab != null) AvaloniaReflect.Set(tc, "SelectedItem", _tab);
                }) ? "selecting the Mods tab" : "no UI dispatcher";
            case "flip":
                if (w.Length < 2) return "settingsui flip <setting>";
                string name = w[1].Trim();
                return AvaloniaReflect.Post(() => Status = Flip(name)) ? "flipping " + name + " (see the status)" : "no UI dispatcher";
            case "close":
                return AvaloniaReflect.Post(() => { try { _devHandle?.Dispose(); } catch { } _devHandle = null; }) ? "closing" : "no UI dispatcher";
            default:
                _reportWanted = true;
                return (Report.Length > 0 ? Report : "(no report yet - ask again)") + " | status: " + Status;
        }
    }

    /// <summary>A row's control changed as a click or a drag would (UI thread).</summary>
    static string Flip(string name)
    {
        foreach (var u in _rowUi)
        {
            if (!string.Equals(u.R.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            double c = ReadControl(u);
            switch (u.R.Kind)
            {
                case "bool": AvaloniaReflect.Set(u.Control, "IsChecked", !(c != 0)); break;
                case "choice": AvaloniaReflect.Set(u.Control, "SelectedIndex", ((int)(double.IsNaN(c) ? 0 : c) + 1) % u.R.Choices.Length); break;
                default:
                    double max = AvaloniaReflect.Double(u.Control, "Maximum");
                    AvaloniaReflect.Set(u.Control, "Value", c + 1 <= max ? c + 1 : c - 1);
                    break;
            }
            return "flipped " + u.R.Name + " (control " + c + " -> " + ReadControl(u) + ")";
        }
        return "flip: no row " + name + (_rowUi.Count == 0 ? " (the tab is not attached)" : "");
    }

    /// <summary>The tab as it is: found or not, its sections (each: rows counted by Tag / rows it says it shows), this mod's
    /// rows against its registry's shown count, selected or not. UI thread.</summary>
    static string MakeReport()
    {
        if (_screen == null) return "no Options screen open";
        if (_tab == null) return "Mods tab: not found";
        var sb = new System.Text.StringBuilder("Mods tab: found; sections:");
        var host = Host(_tab);
        var children = AvaloniaReflect.List(host, "Children");
        int sections = 0;
        if (children != null)
            for (int k = 0; k < children.Count; k++)
            {
                if (!(AvaloniaReflect.Get(children[k], "Tag") is string t) || t == NoteTag || !t.StartsWith(SettingsModel.TabTag + "/", StringComparison.Ordinal)) continue;
                sections++;
                string body = AvaloniaReflect.Get(AvaloniaReflect.Get(children[k], "Content"), "Tag") as string ?? "";
                int eq = body.LastIndexOf("rows=", StringComparison.Ordinal);
                sb.Append(' ').Append(t.Substring(SettingsModel.TabTag.Length + 1)).Append(" rows ")
                  .Append(AvaloniaReflect.CountTagged(children[k], t + "/row/")).Append('/').Append(eq >= 0 ? body.Substring(eq + 5) : "?");
            }
        sb.Append(" (").Append(sections).Append(" sections); ").Append(ModId).Append(": ").Append(_rowUi.Count).Append(" rows built, registry shows ")
          .Append(SettingsModel.Shown(_rows)).Append(" of ").Append(_rows.Count).Append("; selected ").Append(AvaloniaReflect.Get(_tab, "IsSelected"));
        return sb.ToString();
    }
}
