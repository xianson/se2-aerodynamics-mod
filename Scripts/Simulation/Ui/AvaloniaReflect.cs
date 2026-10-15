using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

#pragma warning disable
namespace AeroMod;

/// <summary>
/// Avalonia and the game's screens BY REFLECTION, for the settings tab (SettingsTab.cs). Scripts cannot reference Avalonia
/// or VRage.UI (the mod compile has neither), so every control is made with Activator.CreateInstance on the type found by
/// name in the loaded Avalonia.Controls / Avalonia.Base, and every property is set through a cached PropertyInfo - the
/// patterns of the Orbital mod's UiThread / MapView / GameUi, in one place both mods can carry.
/// Everything here fails closed: a missing type or member gives null / false (never a throw to the caller), and Why keeps
/// the first thing that was missing. Every touch of a control must run on the UI thread (Post).
/// SHARED: copied into the Aerodynamics Mod by tools/sync_settings_ui.sh (only the namespace line differs). Edit it here.
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
        if (!(Field(shared, "_activeScreenHandles") is IList handles)) return null;
        try
        {
            for (int k = handles.Count - 1; k >= 0; k--)
            {
                var v = Get(handles[k], "ScreenView");
                if (v != null && v.GetType().Name == typeName) return v;
            }
        }
        catch { }   // (the list changed under us: next read)
        return null;
    }

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
    static readonly string[] Lists = { "Children", "Items" };
}
