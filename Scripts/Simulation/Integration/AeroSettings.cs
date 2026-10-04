#pragma warning disable
using System;
using System.Globalization;
using System.IO;
using System.Text;
using Keen.Game2.Simulation.GameSystems;
namespace AeroMod;

/// <summary>
/// THE AERO MOD'S SETTINGS: one list of named values over the mod's static tuning fields (no reflection: each setting
/// is a getter and a setter on its field), in two scopes:
///  - WORLD (server): the same for every player - the aero strength, entry heating, performance budgets. Saved WITH
///    THE WORLD (AeroWorldSettingsComponent on the server planets: the engine's EntityNameSessionComponentObjectBuilder,
///    the only builder a mod can use - see the Orbital Mod's SavedState). Optional host overrides in
///    %APPDATA%\SpaceEngineers2\ModSettings\AerodynamicsMod.world.cfg (uncommented lines win over the save).
///  - CLIENT (this PC): the visual effects. Saved in %APPDATA%\SpaceEngineers2\ModSettings\AerodynamicsMod.client.cfg.
/// Both files are polled (~2 s): edit them while playing. Every change takes effect at once - the fields are read each
/// tick or frame - and a setting marked Rebuild bumps AeroSwitch.Generation (every grid rebuilds from scratch).
/// Reached by other mods through AeroApi (GetSettings, TryGetSettingInfo, TryGetSetting, SetSetting,
/// GetSettingsVersion); documented in docs/API.md.
///
/// Multiplayer: SE2 2.4 runs its server in the same process as the client (its networking is a mock), so the world
/// values ARE the server's. A process that only ever saw a client scene (a future remote client) refuses world sets.
/// </summary>
public static class AeroSettings
{
    internal sealed class Setting
    {
        public string Name, Type, Scope, Doc;
        public double Default, Min, Max;
        public bool Rebuild;
        public Func<double> Get;
        public Action<double> Set;
        public Func<double, string> Check;   // (a cross-setting rule: why not, or null)
    }

    static readonly List<Setting> _all = new();
    static readonly Dictionary<string, Setting> _by = new(StringComparer.OrdinalIgnoreCase);
    static readonly object _lock = new();
    static int _version;
    /// <summary>Bumped on every change (API, file, world load).</summary>
    public static int Version => _version;

    /// <summary>Seen in this process: a server scene (aero simulated here) / a client scene (effects drawn here).</summary>
    internal static volatile bool SawServer, SawClient;
    /// <summary>World settings can be set here: the server runs in this process (or nothing is known yet).</summary>
    public static bool WorldWritable => SawServer || !SawClient;

    static AeroSettings()
    {
        // ── WORLD ──
        B("Aero.Enabled", "world", false, () => AeroSwitch.Enabled, v => AeroSwitch.Enabled = v,
            "The whole aero simulation: forces, thrust offsets, block tracking. Off: the game's own flight; on again: every grid rebuilds.");
        F("Aero.DragMultiplier", "world", 0, 10, false, () => AeroTuning.DragMultiplier, v => AeroTuning.DragMultiplier = (float)v,
            "The aerodynamic force along the motion (drag), times this.");
        F("Aero.LiftMultiplier", "world", 0, 10, false, () => AeroTuning.LiftMultiplier, v => AeroTuning.LiftMultiplier = (float)v,
            "The aerodynamic force across the motion (lift: wings, control surfaces, the hull's own), times this.");
        B("Aero.ThrustTorque", "world", false, () => ThrustTorque.Enabled, v => ThrustTorque.Enabled = v,
            "Thrusters off the centre of mass turn the ship (and steer to the pilot's target). Off: the older phantom attitude hold.");
        I("Control.HingeSurfaces", "world", 0, 1, false, () => HingeSurfaces.Mode, v => HingeSurfaces.Mode = (int)v,
            "Vanilla hinges carrying flaps as control surfaces, driven from the pilot's input (fly-by-wire mixing). 0 none (a player's hinges are theirs), 1 every hinge on a piloted grid.");
        F("Control.HingeMaxDeflection", "world", 1, 60, false, () => HingeSurfaces.MaxDeflection, v => HingeSurfaces.MaxDeflection = (float)v,
            "Hinge control surfaces: degrees at full command (the hinge's own limits clamp too).");
        I("Model.LargeGridCellScale", "world", 1, 4, true, () => SnapshotGridAccessor.LargeGridCellScale, v => SnapshotGridAccessor.LargeGridCellScale = (int)v,
            "Large-grid surface resolution: 1 = 0.25 m cells (finest, slowest), 2 = 0.5 m, 4 = 1 m. Rebuilds every grid.");
        F("Entry.OnsetMach", "world", 0.5, 25, false, () => AeroEntryFx.OnsetMach, SetOnsetMach,
            "Entry heating (the plasma) starts at this Mach number.").Check = v => v + 0.05 >= FullMach() ? "must stay under Entry.FullMach" : null;
        F("Entry.FullMach", "world", 0.6, 30, false, FullMach, SetFullMach,
            "Entry heating is full at this Mach number.").Check = v => v <= AeroEntryFx.OnsetMach + 0.05 ? "must stay over Entry.OnsetMach" : null;
        F("Entry.HeatLag", "world", 0.05, 30, false, () => AeroEntryFx.HeatLag, v => AeroEntryFx.HeatLag = (float)v,
            "Seconds the hull takes to heat up and cool down (the glow lingers after slowing).");
        F("Perf.FrameBudgetMs", "world", 0.1, 16, false, () => AeroFrameBudget.BudgetMs, v => AeroFrameBudget.BudgetMs = v,
            "The most aero may take of a simulation frame (ms); past it, grids not piloted wait a few frames.");
        I("Perf.Workers", "world", 1, 16, false, () => AeroWork.Workers, v => AeroWork.Workers = (int)v,
            "Background cores aero may keep busy (hull and force-table builds).");
        B("Perf.TableCache", "world", false, () => AeroTableCache.Enabled, v => AeroTableCache.Enabled = v,
            "Keep built force tables for reuse (a grid seen before builds in milliseconds).");
        I("Perf.TableCacheMB", "world", 16, 4096, false, () => (int)(AeroTableCache.MaxBytes >> 20), v => AeroTableCache.MaxBytes = (long)v << 20,
            "The force-table cache's size (MB).");
        B("Perf.LocalUpdates", "world", false, () => AeroGridComponent.LocalUpdates, v => AeroGridComponent.LocalUpdates = v,
            "Update only the changed part of a hull when blocks change (off: rebuild the whole grid).");
        I("Perf.ChunksPerUpdate", "world", 1, 64, false, () => AeroGridComponent.ChunksPerUpdate, v => AeroGridComponent.ChunksPerUpdate = (int)v,
            "Hull chunks updated per pass after a block change.");
        F("Perf.MinSpeedForUpdates", "world", 0, 1000, false, () => AeroGridComponent.MinSpeedForUpdates, v => AeroGridComponent.MinSpeedForUpdates = (float)v,
            "Below this speed (m/s) a damaged or edited hull waits before updating its forces.");
        F("Perf.QuietBeforeFullRebuild", "world", 0.5, 120, false, () => AeroGridComponent.QuietBeforeFullRebuild, v => AeroGridComponent.QuietBeforeFullRebuild = v,
            "Seconds without block changes before a full rebuild cleans up after local updates.");

        // ── CLIENT ──
        B("Fx.Effects", "client", false, () => AeroEntryFx.AllEffects, v => AeroEntryFx.AllEffects = v,
            "Every aero visual effect (plasma, vapour, tip trails, the plasma light). Off: none at all.");
        B("Fx.FlightField", "client", false, () => FlightField.On, v => FlightField.On = v,
            "The full flight effects (off: the older single plasma at the nose, no vapour or trails).");
        B("Fx.Plasma", "client", false, () => FlightField.PlasmaOn, v => FlightField.PlasmaOn = v, "Entry plasma sparks over the hull.");
        B("Fx.Vapour", "client", false, () => FlightField.VapourOn, v => FlightField.VapourOn = v, "Transonic vapour along the hull's edges.");
        B("Fx.Tips", "client", false, () => FlightField.TipsOn, v => FlightField.TipsOn = v, "Wing-tip vapour trails.");
        B("Fx.SpotLight", "client", false, () => FlightField.SpotOn, v => FlightField.SpotOn = v, "The plasma's light on the hull (one shadowed spot light per glowing grid).");
        I("Fx.MaxGrids", "client", 1, 32, false, () => FlightField.FlightMax, v => FlightField.FlightMax = (int)v,
            "Grids with flight effects at once (the nearest to the camera).");
        F("Fx.Range", "client", 100, 50000, false, () => FlightField.FlightRange, v => FlightField.FlightRange = (float)v,
            "Distance (m) past which a grid shows no flight effects.");
        I("Fx.SpawnPerTick", "client", 1, 200, false, () => FlightField.SpawnPerTick, v => FlightField.SpawnPerTick = (int)v,
            "Effects started per frame while a grid's effects build up (higher: faster, more hitching).");
    }

    // ── the entry heating's Mach pair (stored as heat: OnsetHeat (M / OnsetMach)^3; FullMach is where it is full) ──
    static double FullMach() => AeroEntryFx.OnsetMach * Math.Cbrt(AeroEntryFx.FullHeat / AeroEntryFx.OnsetHeat);
    static void SetFullMach(double m) => AeroEntryFx.FullHeat = (float)(AeroEntryFx.OnsetHeat * Math.Pow(m / AeroEntryFx.OnsetMach, 3));
    static void SetOnsetMach(double m) { double full = FullMach(); AeroEntryFx.OnsetMach = (float)m; SetFullMach(full); }   // (the full point stays put)

    static Setting Add(string name, string type, string scope, double min, double max, bool rebuild, Func<double> get, Action<double> set, string doc)
    {
        var s = new Setting { Name = name, Type = type, Scope = scope, Min = min, Max = max, Rebuild = rebuild, Get = get, Set = set, Doc = doc };
        s.Default = get();   // (the field's own initial value)
        _all.Add(s); _by[name] = s;
        return s;
    }
    static Setting B(string n, string scope, bool rebuild, Func<bool> get, Action<bool> set, string doc) => Add(n, "bool", scope, 0, 1, rebuild, () => get() ? 1 : 0, v => set(v != 0), doc);
    static Setting I(string n, string scope, double min, double max, bool rebuild, Func<int> get, Action<double> set, string doc) => Add(n, "int", scope, min, max, rebuild, () => get(), set, doc);
    static Setting F(string n, string scope, double min, double max, bool rebuild, Func<double> get, Action<double> set, string doc) => Add(n, "float", scope, min, max, rebuild, get, set, doc);
    static Setting F(string n, string scope, double min, double max, bool rebuild, Func<float> get, Action<double> set, string doc) => Add(n, "float", scope, min, max, rebuild, () => get(), set, doc);

    // ── the API ──

    public static int Names(List<string> names)
    {
        if (names == null) return 0;
        names.Clear();
        foreach (var s in _all) names.Add(s.Name);
        return names.Count;
    }

    public static bool Info(string name, out string type, out double def, out double min, out double max, out string scope, out bool rebuild, out string doc)
    {
        type = scope = doc = null; def = min = max = 0; rebuild = false;
        if (name == null || !_by.TryGetValue(name, out var s)) return false;
        type = s.Type; def = s.Default; min = s.Min; max = s.Max; scope = s.Scope; rebuild = s.Rebuild; doc = s.Doc;
        return true;
    }

    public static bool TryGet(string name, out double value)
    {
        value = 0;
        if (name == null || !_by.TryGetValue(name, out var s)) return false;
        value = s.Get();
        return true;
    }

    /// <summary>Change a setting (from the API or a script). False, with why, when it cannot be.</summary>
    public static bool Set(string name, double value, out string why) => Set(name, value, out why, fromStorage: false);

    static bool Set(string name, double value, out string why, bool fromStorage)
    {
        why = null;
        if (name == null || !_by.TryGetValue(name, out var s)) { why = "no setting " + name; return false; }
        if (double.IsNaN(value) || double.IsInfinity(value)) { why = "not a number"; return false; }
        if (s.Type != "float") value = Math.Round(value);
        if (s.Type == "bool" && value != 0 && value != 1) { why = "a bool is 0 or 1"; return false; }
        if (value < s.Min || value > s.Max) { why = $"out of range {s.Min.ToString(Inv)}..{s.Max.ToString(Inv)}"; return false; }
        if (s.Scope == "world" && !fromStorage && !WorldWritable) { why = "world settings are set by the host / server"; return false; }
        lock (_lock)
        {
            if (s.Get() == value) return true;
            if (s.Check != null && (why = s.Check(value)) != null) return false;
            s.Set(value);
            System.Threading.Interlocked.Increment(ref _version);
            if (s.Rebuild) System.Threading.Interlocked.Increment(ref AeroSwitch.Generation);   // (every grid rebuilds: AeroSimJob)
            if (s.Scope == "client" && !fromStorage) _clientDirtyAt = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        Log.Default?.Info($"[AERO] setting {s.Name} = {Fmt(s, value)}{(fromStorage ? " (loaded)" : "")}");
        return true;
    }

    // ── text: "name = value" lines (# comments), invariant culture ──

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static string Fmt(Setting s, double v) => s.Type == "bool" ? (v != 0 ? "true" : "false") : v.ToString(s.Type == "int" ? "0" : "R", Inv);

    /// <summary>The scope's settings as text: every one (all = true, with its description), or only those off their
    /// default (for the world save).</summary>
    public static string Text(string scope, bool all)
    {
        var sb = new StringBuilder();
        foreach (var s in _all)
        {
            if (s.Scope != scope) continue;
            double v = s.Get();
            if (!all && v == s.Default) continue;
            if (all) sb.Append("# ").Append(s.Doc).Append(" (").Append(s.Type).Append(", default ").Append(Fmt(s, s.Default))
                       .Append(", ").Append(Fmt(s, s.Min)).Append("..").Append(Fmt(s, s.Max)).Append(s.Rebuild ? ", rebuilds every grid" : "").Append(")\n");
            sb.Append(s.Name).Append(" = ").Append(Fmt(s, v)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Apply "name = value" lines of a scope (others ignored). Returns the count applied.</summary>
    internal static int Apply(string text, string scope)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int n = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string name = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
            if (!_by.TryGetValue(name, out var s) || s.Scope != scope) continue;
            if (!Parse(val, out double v)) { Log.Default?.Info($"[AERO] setting {name}: '{val}' is not a value"); continue; }
            if (Set(name, v, out string why, fromStorage: true)) n++;
            else Log.Default?.Info($"[AERO] setting {name} = {val} refused: {why}");
        }
        return n;
    }

    static bool Parse(string s, out double v)
    {
        switch (s.ToLowerInvariant())
        {
            case "true": case "on": case "yes": v = 1; return true;
            case "false": case "off": case "no": v = 0; return true;
        }
        return double.TryParse(s, NumberStyles.Float, Inv, out v);
    }

    /// <summary>Every setting of a scope back to its default (a new world).</summary>
    internal static void ResetScope(string scope)
    {
        foreach (var s in _all) if (s.Scope == scope && s.Get() != s.Default) Set(s.Name, s.Default, out _, fromStorage: true);
    }

    // ── the world save (AeroWorldSettingsComponent) ──

    internal const string SaveKey = "aero-settings:";
    static int _carriers;
    static bool _worldApplied;

    /// <summary>A server planet loaded with its saved builder: the world's settings (the first non-empty one wins).</summary>
    internal static void WorldLoaded(string text)
    {
        if (_worldApplied || string.IsNullOrEmpty(text)) return;
        _worldApplied = true;
        ResetScope("world");
        int n = Apply(text, "world");
        Log.Default?.Info($"[AERO] world settings loaded: {n}");
        _worldFileAt = DateTime.MinValue;   // (the host's overrides again, over the save)
    }

    internal static void CarrierAdded() { SawServer = true; System.Threading.Interlocked.Increment(ref _carriers); Poll(force: true); }

    /// <summary>The last carrier gone (the world unloaded): world settings back to defaults for the next world.</summary>
    internal static void CarrierRemoved()
    {
        if (System.Threading.Interlocked.Decrement(ref _carriers) > 0) return;
        _worldApplied = false;
        ResetScope("world");
        _worldFileAt = DateTime.MinValue;
    }

    // ── the files (~2 s poll from the aero jobs; never a throw per tick: a failing file is left alone) ──

    static string _dir;
    static DateTime _clientFileAt = DateTime.MinValue, _worldFileAt = DateTime.MinValue;
    static long _pollAt, _clientDirtyAt;
    static int _fileFails;
    /// <summary>Diagnostics: the settings folder, the last file problem.</summary>
    public static string Folder => _dir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceEngineers2", "ModSettings");
    public static string FileWhy = "";
    static string ClientPath => Path.Combine(Folder, "AerodynamicsMod.client.cfg");
    static string WorldPath => Path.Combine(Folder, "AerodynamicsMod.world.cfg");

    /// <summary>Any aero job: cheap (a timestamp compare); the files every ~2 s.</summary>
    internal static void Poll(bool force = false)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp(), due = System.Threading.Interlocked.Read(ref _pollAt);
        if (!force && now < due) return;
        if (System.Threading.Interlocked.CompareExchange(ref _pollAt, now + 2 * System.Diagnostics.Stopwatch.Frequency, due) != due && !force) return;
        if (_fileFails >= 3) return;
        try
        {
            Directory.CreateDirectory(Folder);
            // client: written after an API change has settled (1 s), else read when edited by hand
            long dirty = _clientDirtyAt;
            if (dirty != 0 && now - dirty > System.Diagnostics.Stopwatch.Frequency)
            {
                _clientDirtyAt = 0;
                File.WriteAllText(ClientPath, ClientHeader + Text("client", all: true));
                _clientFileAt = File.GetLastWriteTimeUtc(ClientPath);
            }
            else if (!File.Exists(ClientPath))
            {
                File.WriteAllText(ClientPath, ClientHeader + Text("client", all: true));
                _clientFileAt = File.GetLastWriteTimeUtc(ClientPath);
            }
            else
            {
                var at = File.GetLastWriteTimeUtc(ClientPath);
                if (at != _clientFileAt) { _clientFileAt = at; Apply(File.ReadAllText(ClientPath), "client"); }
            }
            // world: the host's overrides (only where the server runs)
            if (SawServer || force)
            {
                if (!File.Exists(WorldPath)) File.WriteAllText(WorldPath, WorldHeader + Commented(Text("world", all: true)));
                var at = File.GetLastWriteTimeUtc(WorldPath);
                if (at != _worldFileAt && WorldWritable) { _worldFileAt = at; Apply(File.ReadAllText(WorldPath), "world"); }
            }
            FileWhy = "";
        }
        catch (Exception e) { _fileFails++; FileWhy = $"{e.GetType().Name}: {e.Message}" + (_fileFails >= 3 ? " (files given up for this session)" : ""); }
    }

    static string Commented(string text)
    {
        var sb = new StringBuilder();
        foreach (var l in text.Split('\n')) if (l.Length > 0) sb.Append(l[0] == '#' ? l : "# " + l).Append('\n');
        return sb.ToString();
    }

    const string ClientHeader = "# Aerodynamics Mod - this player's settings (visual effects). Saved by the mod; edit while playing:\n# changes apply within ~2 s. name = value; true / false for switches.\n\n";
    const string WorldHeader = "# Aerodynamics Mod - host / server overrides for WORLD settings. Uncomment a line to force that value\n# on every world this machine hosts (it wins over the value saved with the world; applied within ~2 s).\n# The world saves its own values; without overrides here, those count.\n\n";
}

/// <summary>
/// Carries the world's aero settings in the save: injected into server planet compositions (EntityInjector), with the
/// engine's EntityNameSessionComponentObjectBuilder (a string -&gt; Entity map; a mod cannot define its own builder).
/// Key "aero-settings:&lt;text&gt;" -&gt; the planet itself (a value must exist). Every planet writes the same text; the
/// first non-empty one read on load wins.
/// </summary>
public partial class AeroWorldSettingsComponent : Component, IInSceneListener
{
    [Keen.VRage.DCS.Annotations.Init]
    private void InitSettings(EntityNameSessionComponentObjectBuilder ob)
    {
        if (ob?.NamedEntities == null) return;
        foreach (var kv in ob.NamedEntities)
            if (kv.Key.StartsWith(AeroSettings.SaveKey)) { AeroSettings.WorldLoaded(kv.Key.Substring(AeroSettings.SaveKey.Length)); break; }
    }

    [Keen.VRage.DCS.Annotations.Serializer]
    private void SerializeSettings(EntityNameSessionComponentObjectBuilder ob)
    {
        ob.NamedEntities = new Dictionary<string, Keen.VRage.DCS.Components.Entity>();
        string text = AeroSettings.Text("world", all: false);
        if (text.Length > 0 && Entity != null) ob.NamedEntities[AeroSettings.SaveKey + text] = Entity;
    }

    void IInSceneListener.OnAddedToScene() => AeroSettings.CarrierAdded();
    void IInSceneListener.OnBeforeRemovedFromScene() => AeroSettings.CarrierRemoved();
}
