using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace __MOD__;

/// <summary>
/// Avalonia and the game's screens BY REFLECTION, for the settings tab (SettingsTab.cs). Scripts cannot reference Avalonia
/// or VRage.UI (the mod compile has neither), so every control is made with Activator.CreateInstance on the type found by
/// name in the loaded Avalonia.Controls / Avalonia.Base, and every property is set through a cached PropertyInfo - the
/// patterns of the Orbital mod's UiThread / MapView / GameUi, in one place both mods can carry.
/// Everything here fails closed: a missing type or member gives null / false (never a throw to the caller), and Why keeps
/// the first thing that was missing. Every touch of a control must run on the UI thread (Post).
/// SHARED: written once in the Aerodynamics Mod's tools/shared/modsrc/files/ (this path under it), copied into both mods by
/// tools/shared/modsrc/sync.sh (only the namespace line differs). Edit it THERE, not in a mod's copy.
/// </summary>
public static class AvaloniaReflect
{
    /// <summary>The first lookup that failed ("" none).</summary>
    public static string Why = "";
    static void Miss(string what) { if (Why.Length == 0) Why = what; }

    static readonly object _lock = new object();
    static bool _looked; static Assembly _base, _controls;
    static object _dispatcher; static MethodInfo _post; static object[] _postDefaults;

    static void Look()
    {
        if (_looked) return;
        lock (_lock)
        {
            if (_looked) return;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n = a.GetName().Name;
                if (n == "Avalonia.Base") _base = a;
                else if (n == "Avalonia.Controls") _controls = a;
            }
            // (Dispatcher.UIThread.Post(Action, DispatcherPriority = default): its defaults filled once - UiThread's way)
            var dt = _base?.GetType("Avalonia.Threading.Dispatcher");
            _dispatcher = dt?.GetProperty("UIThread")?.GetValue(null);
            foreach (var m in dt?.GetMethods() ?? Array.Empty<MethodInfo>())
            {
                var ps = m.GetParameters();
                if (m.Name != "Post" || ps.Length == 0 || ps[0].ParameterType != typeof(Action)) continue;
                var defs = new object[ps.Length];
                for (int k = 1; k < ps.Length; k++)
                    defs[k] = ps[k].HasDefaultValue && ps[k].DefaultValue != null ? ps[k].DefaultValue : ps[k].ParameterType.IsValueType ? Activator.CreateInstance(ps[k].ParameterType) : null;
                _postDefaults = defs; _post = m;
                break;
            }
            if (_base == null) Miss("no Avalonia.Base loaded");
            else if (_controls == null) Miss("no Avalonia.Controls loaded");
            else if (_post == null || _dispatcher == null) Miss("no Dispatcher.UIThread.Post");
            _looked = true;
        }
    }

    /// <summary>Run on Avalonia's UI thread (false: no dispatcher).</summary>
    /// <summary>Avalonia's DispatcherTimer.Run(Func&lt;bool&gt;, TimeSpan, priority = default): f on the UI thread every
    /// interval while it returns true, the returned IDisposable stops it. It runs while the GAME IS PAUSED (2026-10-23, game
    /// 4.0.0.3159: the pause menu pauses the session, so nothing a session update posts runs while Options is open). Null
    /// (and Why) when not found or it threw.</summary>
    public static IDisposable UiTimer(Func<bool> f, TimeSpan every)
    {
        Look();
        var t = _base?.GetType("Avalonia.Threading.DispatcherTimer");
        MethodInfo run = null;
        foreach (var m in t?.GetMethods(BindingFlags.Public | BindingFlags.Static) ?? Array.Empty<MethodInfo>())
        {
            var ps = m.GetParameters();
            if (m.Name == "Run" && ps.Length >= 2 && ps[0].ParameterType == typeof(Func<bool>) && ps[1].ParameterType == typeof(TimeSpan)) { run = m; break; }
        }
        if (run == null) { Miss(t == null ? "no Avalonia.Threading.DispatcherTimer" : "no DispatcherTimer.Run(Func<bool>, TimeSpan)"); return null; }
        var pi = run.GetParameters(); var args = new object[pi.Length]; args[0] = f; args[1] = every;
        for (int k = 2; k < pi.Length; k++)
            args[k] = pi[k].HasDefaultValue && pi[k].DefaultValue != null ? pi[k].DefaultValue : pi[k].ParameterType.IsValueType ? Activator.CreateInstance(pi[k].ParameterType) : null;
        try { return run.Invoke(null, args) as IDisposable; }
        catch (Exception e) { Miss("DispatcherTimer.Run threw " + (e.InnerException ?? e).GetType().Name); return null; }
    }

    public static bool Post(Action a)
    {
        Look();
        if (_dispatcher == null || _post == null) return false;
        var args = (object[])_postDefaults.Clone(); args[0] = a;
        try { _post.Invoke(_dispatcher, args); return true; } catch { return false; }
    }

    // ── types ──

    static readonly Dictionary<string, Type> _types = new Dictionary<string, Type>();

    /// <summary>Avalonia.Controls.&lt;name&gt; (TabItem, Slider, ...), or null.</summary>
    public static Type Control(string name) => TypeIn(_controls, "Avalonia.Controls." + name);
    /// <summary>A type of Avalonia.Base by full name (Avalonia.Thickness, Avalonia.Media.Brushes, ...), or null.</summary>
    public static Type BaseType(string fullName) => TypeIn(_base, fullName);

    static Type TypeIn(Assembly a, string fullName)
    {
        Look();
        lock (_types)
        {
            if (_types.TryGetValue(fullName, out var t)) return t;
            t = a?.GetType(fullName);
            if (t == null) Miss("no type " + fullName);
            _types[fullName] = t;
            return t;
        }
    }

    /// <summary>A game type by full name, from any loaded assembly (looked for once per name; null if absent).</summary>
    public static Type GameType(string fullName)
    {
        lock (_types)
        {
            if (_types.TryGetValue(fullName, out var t)) return t;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { t = a.GetType(fullName); if (t != null) break; }
            if (t == null) Miss("no type " + fullName);
            _types[fullName] = t;
            return t;
        }
    }

    /// <summary>A new control of that Avalonia.Controls type (null: no such type, or its constructor threw).</summary>
    public static object New(string controlName) => New(Control(controlName));
    public static object New(Type t)
    {
        if (t == null) return null;
        try { return Activator.CreateInstance(t); } catch (Exception e) { Miss("new " + t.Name + ": " + (e.InnerException ?? e).Message); return null; }
    }

    // ── members ──

    static readonly Dictionary<(Type, string), PropertyInfo> _props = new Dictionary<(Type, string), PropertyInfo>();
    static readonly Dictionary<(Type, string), FieldInfo> _fields = new Dictionary<(Type, string), FieldInfo>();

    /// <summary>A public instance property, the most derived of that name (GetProperty threw AmbiguousMatchException for
    /// properties redeclared down a hierarchy - UiThread's lesson); cached, null if none.</summary>
    public static PropertyInfo Prop(Type t, string name)
    {
        if (t == null) return null;
        lock (_props)
        {
            if (_props.TryGetValue((t, name), out var p)) return p;
            for (var x = t; x != null && p == null; x = x.BaseType)
                p = x.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            _props[(t, name)] = p;
            return p;
        }
    }

    public static object Get(object o, string name)
    {
        if (o == null) return null;
        try { return Prop(o.GetType(), name)?.GetValue(o); } catch { return null; }
    }

    /// <summary>Set a property (false: no such writable property, or it threw - the first such is in Why).</summary>
    public static bool Set(object o, string name, object value)
    {
        if (o == null) return false;
        var p = Prop(o.GetType(), name);
        if (p == null || !p.CanWrite) { Miss(o.GetType().Name + "." + name + " not settable"); return false; }
        try { p.SetValue(o, value); return true; }
        catch (Exception e) { Miss(o.GetType().Name + "." + name + ": " + (e.InnerException ?? e).Message); return false; }
    }

    /// <summary>An instance field of any visibility (the type's or a base's), or null.</summary>
    public static object Field(object o, string name)
    {
        if (o == null) return null;
        var t = o.GetType();
        FieldInfo f;
        lock (_fields)
        {
            if (!_fields.TryGetValue((t, name), out f))
            {
                for (var x = t; x != null && f == null; x = x.BaseType)
                    f = x.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                _fields[(t, name)] = f;
            }
        }
        try { return f?.GetValue(o); } catch { return null; }
    }

    /// <summary>A collection property as IList (Panel.Children, ItemsControl.Items, StyledElement.Classes), or null.</summary>
    public static IList List(object o, string name) => Get(o, name) as IList;

    public static void AddClass(object c, string cls) { try { List(c, "Classes")?.Add(cls); } catch { } }

    public static double Double(object o, string name) => Get(o, name) is double d ? d : double.NaN;

    // ── values ──

    /// <summary>A boxed Avalonia.Thickness (a Margin).</summary>
    public static object Thickness(double l, double t, double r, double b)
    {
        var tt = BaseType("Avalonia.Thickness");
        try { return tt == null ? null : Activator.CreateInstance(tt, l, t, r, b); } catch { return null; }
    }

    /// <summary>An enum value of Avalonia.Base by its type's full name (Avalonia.Layout.HorizontalAlignment, "Left").</summary>
    public static object EnumValue(string typeFullName, string value)
    {
        var t = BaseType(typeFullName);
        try { return t == null ? null : System.Enum.Parse(t, value); } catch { return null; }
    }

    /// <summary>One of Avalonia.Media.Brushes' named brushes (OrangeRed, Gray), or null.</summary>
    public static object Brush(string name)
    {
        try { return BaseType("Avalonia.Media.Brushes")?.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null); } catch { return null; }
    }

    /// <summary>new Avalonia.Controls.ColumnDefinitions(spec) / RowDefinitions(spec) ("*,24,380,150").</summary>
    public static object Definitions(string kind, string spec)
    {
        var t = Control(kind);
        try { return t == null ? null : Activator.CreateInstance(t, spec); } catch { return null; }
    }

    // ── the game's theme (its resources and style classes, not our colours) ──

    static MethodInfo _tryFind;
    /// <summary>A resource by key as the control would see it (ResourceNodeExtensions.TryFindResource: up its logical tree
    /// to the screen's and the application's dictionaries - "ValueWidth", "Warning", ...), or null.</summary>
    public static object Resource(object anchor, string key)
    {
        if (anchor == null) return null;
        try
        {
            if (_tryFind == null)
                foreach (var m in BaseType("Avalonia.Controls.ResourceNodeExtensions")?.GetMethods(BindingFlags.Public | BindingFlags.Static) ?? Array.Empty<MethodInfo>())
                {
                    var ps = m.GetParameters();
                    if (m.Name == "TryFindResource" && ps.Length == 3 && ps[2].ParameterType.IsByRef) { _tryFind = m; break; }
                }
            if (_tryFind == null || !_tryFind.GetParameters()[0].ParameterType.IsInstanceOfType(anchor)) return null;
            var args = new object[] { anchor, key, null };
            return _tryFind.Invoke(null, args) is bool ok && ok ? args[2] : null;
        }
        catch { return null; }
    }

    /// <summary>The theme's brush of that key (a brush, or a Color made a SolidColorBrush - the game's dictionaries hold
    /// both), else Avalonia's named brush (fallback; null: none).</summary>
    public static object ThemeBrush(object anchor, string key, string fallback)
    {
        var v = Resource(anchor, key);
        var ib = BaseType("Avalonia.Media.IBrush");
        if (v != null && ib != null && ib.IsInstanceOfType(v)) return v;
        if (v != null && v.GetType().FullName == "Avalonia.Media.Color")
            try { return Activator.CreateInstance(BaseType("Avalonia.Media.SolidColorBrush"), v, 1.0); } catch { }
        return fallback == null ? null : Brush(fallback);
    }

    /// <summary>A GridLength resource as a ColumnDefinitions spec piece ("516", "0.65*", "Auto"), else fallback.</summary>
    public static string LengthSpec(object anchor, string key, string fallback)
    {
        var v = Resource(anchor, key);
        if (v == null || v.GetType().Name != "GridLength") return fallback;
        return SettingsModel.LengthSpec(Get(v, "Value") is double d ? d : double.NaN, Get(v, "GridUnitType")?.ToString(), fallback);
    }

    /// <summary>A Thickness resource ("Margin-Large-Horizontal"), else the fallback thickness.</summary>
    public static object ThicknessOr(object anchor, string key, double l, double t, double r, double b)
    {
        var v = Resource(anchor, key);
        return v != null && v.GetType().FullName == "Avalonia.Thickness" ? v : Thickness(l, t, r, b);
    }

    /// <summary>Column k of a Grid set to a fixed width in pixels (ColumnDefinition.Width = new GridLength(px)). False: no
    /// such column, or GridLength missing.</summary>
    public static bool ColumnWidth(object grid, int k, double px)
    {
        if (!(List(grid, "ColumnDefinitions") is IList defs) || k >= defs.Count) return false;
        var gl = Control("GridLength");
        try { return gl != null && Set(defs[k], "Width", Activator.CreateInstance(gl, px)); } catch { return false; }
    }

    static ConstructorInfo _typeface, _formatted;
    static object _ltr;
    /// <summary>How wide s is in that TextBlock's font as its styles resolved it (FontFamily / FontStyle / FontWeight /
    /// FontStretch / FontSize -> new Typeface -> new FormattedText(..).Width). NaN: unreadable (the caller estimates).</summary>
    public static double TextWidth(object textBlock, string s)
    {
        if (textBlock == null || string.IsNullOrEmpty(s)) return double.NaN;
        try
        {
            if (_formatted == null)
            {
                Type tf = BaseType("Avalonia.Media.Typeface"), ft = BaseType("Avalonia.Media.FormattedText");
                Type fam = BaseType("Avalonia.Media.FontFamily"), sty = BaseType("Avalonia.Media.FontStyle"), wei = BaseType("Avalonia.Media.FontWeight"),
                     str = BaseType("Avalonia.Media.FontStretch"), dir = BaseType("Avalonia.Media.FlowDirection"), brush = BaseType("Avalonia.Media.IBrush");
                if (tf == null || ft == null || fam == null || sty == null || wei == null || str == null || dir == null || brush == null) return double.NaN;
                _typeface = tf.GetConstructor(new[] { fam, sty, wei, str });
                _ltr = System.Enum.Parse(dir, "LeftToRight");
                _formatted = ft.GetConstructor(new[] { typeof(string), typeof(System.Globalization.CultureInfo), dir, tf, typeof(double), brush });
                if (_typeface == null || _formatted == null) { _formatted = null; Miss("no Typeface / FormattedText constructor"); return double.NaN; }
            }
            object family = Get(textBlock, "FontFamily"), style = Get(textBlock, "FontStyle"), weight = Get(textBlock, "FontWeight"), stretch = Get(textBlock, "FontStretch");
            double size = Double(textBlock, "FontSize");
            if (family == null || style == null || weight == null || stretch == null || !(size > 0)) return double.NaN;
            var face = _typeface.Invoke(new[] { family, style, weight, stretch });
            var text = _formatted.Invoke(new[] { s, System.Globalization.CultureInfo.CurrentUICulture, _ltr, face, (object)size, null });
            return Double(text, "WidthIncludingTrailingWhitespace");
        }
        catch { return double.NaN; }
    }

    static MethodInfo _translate;
    /// <summary>Where visual's top-left is, in x, inside relativeTo (VisualExtensions.TranslatePoint; both laid out and in
    /// one tree). NaN: not laid out, not related, or the method is missing.</summary>
    public static double OffsetX(object visual, object relativeTo)
    {
        if (visual == null || relativeTo == null) return double.NaN;
        try
        {
            _translate ??= BaseType("Avalonia.VisualExtensions")?.GetMethod("TranslatePoint", BindingFlags.Public | BindingFlags.Static);
            var pt = BaseType("Avalonia.Point");
            if (_translate == null || pt == null) return double.NaN;
            var p = _translate.Invoke(null, new[] { visual, Activator.CreateInstance(pt, 0.0, 0.0), relativeTo });   // (a Point?: boxed Point or null)
            return p == null ? double.NaN : Double(p, "X");
        }
        catch { return double.NaN; }
    }

    /// <summary>A public static property's value (Avalonia.Media.TextTrimming.CharacterEllipsis), or null.</summary>
    public static object StaticValue(string baseTypeFullName, string name)
    {
        try { return BaseType(baseTypeFullName)?.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null); } catch { return null; }
    }

    // ── a template part hidden (the game's Slider template shows its raw Value: PART_NumericUpDown) ──

    static readonly List<string> _hideParts = new List<string>();
    static MethodInfo _onTemplate;

    /// <summary>Hide that named part of the control's template each time the template is applied (TemplatedControl.
    /// TemplateApplied; the args' NameScope.Find(part).IsVisible = false). False: no such event.</summary>
    public static bool HidePart(object control, string part)
    {
        try
        {
            lock (_hideParts) if (!_hideParts.Contains(part)) _hideParts.Add(part);
            var ev = control?.GetType().GetEvent("TemplateApplied", BindingFlags.Public | BindingFlags.Instance);
            if (ev == null) { Miss("no TemplateApplied"); return false; }
            _onTemplate ??= typeof(AvaloniaReflect).GetMethod(nameof(OnTemplate), BindingFlags.NonPublic | BindingFlags.Static);
            var d = Delegate.CreateDelegate(ev.EventHandlerType, _onTemplate, false);
            if (d == null) { Miss("TemplateApplied handler type " + ev.EventHandlerType.Name); return false; }
            ev.AddEventHandler(control, d);
            return true;
        }
        catch (Exception e) { Miss("TemplateApplied: " + (e.InnerException ?? e).Message); return false; }
    }

    static void OnTemplate(object sender, EventArgs e)
    {
        try
        {
            var scope = Get(e, "NameScope");
            var find = scope?.GetType().GetMethod("Find", new[] { typeof(string) });
            if (find == null) return;
            lock (_hideParts)
                foreach (string p in _hideParts)
                    if (find.Invoke(scope, new object[] { p }) is object part) Set(part, "IsVisible", false);
        }
        catch { }
    }

    /// <summary>A public instance method with no parameters (ScrollToHome, BringIntoView); false: none, or it threw.</summary>
    public static bool Call(object o, string name)
    {
        try
        {
            var m = o?.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (m == null) return false;
            m.Invoke(o, null);
            return true;
        }
        catch { return false; }
    }

    /// <summary>ControlExtensions.BringIntoView(c): its ScrollViewer scrolls to show it. False: none, or it threw.</summary>
    public static bool BringIntoView(object c)
    {
        try
        {
            foreach (var m in Control("ControlExtensions")?.GetMethods(BindingFlags.Public | BindingFlags.Static) ?? Array.Empty<MethodInfo>())
                if (m.Name == "BringIntoView" && m.GetParameters().Length == 1 && c != null && m.GetParameters()[0].ParameterType.IsInstanceOfType(c))
                { m.Invoke(null, new[] { c }); return true; }
        }
        catch { }
        return false;
    }

    /// <summary>A boxed Avalonia.Vector (a ScrollViewer.Offset).</summary>
    public static object Vector(double x, double y)
    {
        var t = BaseType("Avalonia.Vector");
        try { return t == null ? null : Activator.CreateInstance(t, x, y); } catch { return null; }
    }

    static MethodInfo _setColumn, _setRow, _setColumnSpan, _setTip;
    /// <summary>Grid.SetColumn / SetRow / SetColumnSpan on a child.</summary>
    public static void Place(object c, int column, int row, int columnSpan = 1)
    {
        if (c == null) return;
        var g = Control("Grid");
        try
        {
            _setColumn ??= Static(g, "SetColumn"); _setRow ??= Static(g, "SetRow"); _setColumnSpan ??= Static(g, "SetColumnSpan");
            _setColumn?.Invoke(null, new object[] { c, column });
            _setRow?.Invoke(null, new object[] { c, row });
            if (columnSpan > 1) _setColumnSpan?.Invoke(null, new object[] { c, columnSpan });
        }
        catch (Exception e) { Miss("Grid.Set*: " + (e.InnerException ?? e).Message); }
    }

    /// <summary>ToolTip.SetTip(control, text).</summary>
    public static void SetTip(object c, string tip)
    {
        if (c == null || string.IsNullOrEmpty(tip)) return;
        try { (_setTip ??= Static(Control("ToolTip"), "SetTip"))?.Invoke(null, new object[] { c, tip }); } catch { }
    }

    static MethodInfo Static(Type t, string name)
    {
        if (t == null) return null;
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            if (m.Name == name && m.GetParameters().Length == 2) return m;
        Miss("no " + t.Name + "." + name);
        return null;
    }

    // ── a button's Click ──

    /// <summary>Every hooked button's click lands here, with the button (its Tag says which). UI thread.</summary>
    public static Action<object> Clicked;
    static MethodInfo _onClick;
    static void OnClick(object sender, EventArgs e) { try { Clicked?.Invoke(sender); } catch { } }

    /// <summary>Hook a Button's Click (an EventHandler&lt;RoutedEventArgs&gt;: a delegate of that type made over OnClick, whose
    /// (object, EventArgs) takes it by contravariance). False: no Click event.</summary>
    public static bool HookClick(object button)
    {
        try
        {
            var ev = button?.GetType().GetEvent("Click", BindingFlags.Public | BindingFlags.Instance);
            if (ev == null) { Miss("no Button.Click"); return false; }
            _onClick ??= typeof(AvaloniaReflect).GetMethod(nameof(OnClick), BindingFlags.NonPublic | BindingFlags.Static);
            var d = Delegate.CreateDelegate(ev.EventHandlerType, _onClick, false);
            if (d == null) { Miss("Click handler type " + ev.EventHandlerType.Name); return false; }
            ev.AddEventHandler(button, d);
            return true;
        }
        catch (Exception e) { Miss("Click: " + (e.InnerException ?? e).Message); return false; }
    }

    // ── the game's screens ──

    /// <summary>The game's SharedUIComponent (the screens' owner) from the session's services, or null. Game thread.</summary>
    public static object SharedUi(Session session)
    {
        var t = GameType("Keen.Game2.Client.UI.Library.SharedUIComponent");
        if (session == null || t == null) return null;
        try { return session.TryGet(t); } catch { return null; }
    }

    /// <summary>The open screen view of that type name (OptionsScreen) among SharedUIComponent's active screens
    /// (private _activeScreenHandles: List&lt;ScreenHandle&gt;, each handle's ScreenView), or null. UI thread.</summary>
    public static object ActiveScreen(object shared, string typeName)
    {
        // (HEALTH, 2026-10-23: a missing or retyped field read as "no screen open" and the tab vanished with no word - the
        //  game 4.0.0.3159. Now: ScreensWhy names the field's problem, and Screens lists the open screens' types)
        var f = Field(shared, "_activeScreenHandles");
        if (!(f is IList handles)) { ScreensWhy = f == null ? "SharedUIComponent._activeScreenHandles not found" : "SharedUIComponent._activeScreenHandles is " + f.GetType().Name + ", not a list"; return null; }
        ScreensWhy = null;
        object found = null; _screenNames.Clear();
        try
        {
            for (int k = handles.Count - 1; k >= 0; k--)
            {
                var h = handles[k];
                var v = Get(h, "ScreenView");
                _screenNames.Append(v != null ? v.GetType().Name : h == null ? "null" : h.GetType().Name + ".ScreenView=null").Append(' ');
                if (found == null && v != null && v.GetType().Name == typeName) found = v;
            }
        }
        catch { }   // (the list changed under us: next read)
        Screens = _screenNames.ToString();
        return found;
    }
    static readonly System.Text.StringBuilder _screenNames = new System.Text.StringBuilder();
    /// <summary>HEALTH: the open screens' view types at the last ActiveScreen (newest first), and why none could be read
    /// (null: the list was read).</summary>
    public static string Screens = "", ScreensWhy;

    /// <summary>The first item of a list whose Tag is that string, or null.</summary>
    public static object Tagged(IList items, string tag)
    {
        if (items == null) return null;
        for (int k = 0; k < items.Count; k++)
        {
            var it = items[k];
            if (it != null && Get(it, "Tag") as string == tag) return it;
        }
        return null;
    }

    /// <summary>How many controls under o (through Content, Child, Children and Items) carry a Tag starting so.</summary>
    public static int CountTagged(object o, string prefix, int depth = 12)
    {
        if (o == null || depth < 0 || o is string) return 0;
        int n = Get(o, "Tag") is string t && t.StartsWith(prefix, StringComparison.Ordinal) ? 1 : 0;
        n += CountTagged(Get(o, "Content"), prefix, depth - 1) + CountTagged(Get(o, "Child"), prefix, depth - 1);
        foreach (string list in Lists)
            if (List(o, list) is IList l)
                for (int k = 0; k < l.Count; k++) n += CountTagged(l[k], prefix, depth - 1);
        return n;
    }
    /// <summary>The first control under o (as CountTagged walks) whose Tag ends so (ignoring case), or null.</summary>
    public static object FindTagEnding(object o, string suffix, int depth = 12)
    {
        if (o == null || depth < 0 || o is string) return null;
        if (Get(o, "Tag") is string t && t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return o;
        var hit = FindTagEnding(Get(o, "Content"), suffix, depth - 1) ?? FindTagEnding(Get(o, "Child"), suffix, depth - 1);
        if (hit != null) return hit;
        foreach (string list in Lists)
            if (List(o, list) is IList l)
                for (int k = 0; k < l.Count; k++) if ((hit = FindTagEnding(l[k], suffix, depth - 1)) != null) return hit;
        return null;
    }
    static readonly string[] Lists = { "Children", "Items" };
}
