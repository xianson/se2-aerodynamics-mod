#pragma warning disable
using System;
using System.Globalization;
using System.IO;
using System.Text;
namespace AeroMod;

// THE SETTINGS' STORE (AeroSettings): the API (names, info, get, set), the "name = value" text, the world save's load and
// unload, and the two .cfg files. The registry itself - every setting and its metadata - is AeroSettings.cs.
public static partial class AeroSettings
{
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
        // (a value refused against its partner's CURRENT value - OnsetMach under FullMach - is tried again after the
        //  others: loading 6/8 over defaults refused 6, as the saved full 8 was not in yet)
        List<(string name, double v, string val)> retry = null;
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
            else (retry ??= new List<(string, double, string)>()).Add((name, v, val));
        }
        if (retry != null)
            foreach (var (name, v, val) in retry)
            {
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
        // (twice: a default refused against a partner not yet reset - Onset 2 over a world's Full 1 - goes in on the second)
        for (int pass = 0; pass < 2; pass++)
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
        AeroSurfaceSaved.Load(text);
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
        AeroSurfaceSaved.Clear();
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

    /// <summary>Any aero job: cheap (a timestamp compare); the files every ~2 s - looked at OFF the job's thread (2026-10-15,
    /// a profile: 6 ms/s of the simulation's threads was this poll's file system calls - the folder made, each file's
    /// existence and time asked separately - every 2 s from whichever aero job came first). Now one background task at a
    /// time does it (a poll due while one runs is skipped: the next comes 2 s later), with one look per file (FileInfo: there
    /// and when written together) and the folder made only to write into it. A forced poll (a world's settings carrier
    /// added) still reads at once, on its caller's thread, as before.</summary>
    internal static void Poll(bool force = false)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        // (called by every grid's sim tick and every client grid's effects frame - tens of thousands a second on many
        //  threads: each thread looks at the shared due time at most 4 times a second, its own clock first; the shared
        //  cache line read by all of them cost ~5 ms/s in a profile, 2026-10-15)
        if (!force && now < _localNext) return;
        _localNext = now + System.Diagnostics.Stopwatch.Frequency / 4;
        long due = System.Threading.Interlocked.Read(ref _pollAt);
        if (!force && now < due) return;
        if (System.Threading.Interlocked.CompareExchange(ref _pollAt, now + 2 * System.Diagnostics.Stopwatch.Frequency, due) != due && !force) return;
        GameStallWarning.Apply();   // (the game's stall warning: removed unless Hud.GameStallWarning)
        if (_fileFails >= 3) return;
        if (force) { PollFiles(true); return; }
        if (System.Threading.Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;
        try { System.Threading.Tasks.Task.Factory.StartNew(_pollTask, System.Threading.CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.None, System.Threading.Tasks.TaskScheduler.Default); }
        catch { System.Threading.Volatile.Write(ref _polling, 0); }
    }
    static int _polling;
    [ThreadStatic] static long _localNext;
    static readonly object _filesLock = new object();
    static readonly Action _pollTask = () => { try { PollFiles(false); } finally { System.Threading.Volatile.Write(ref _polling, 0); } };

    /// <summary>The files themselves (one poll at a time: the lock - a forced poll may meet a background one).</summary>
    static void PollFiles(bool force)
    {
        lock (_filesLock)
        {
            if (_fileFails >= 3) return;
            try
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                // client: written after an API change has settled (1 s), else read when edited by hand
                long dirty = _clientDirtyAt;
                var client = new FileInfo(ClientPath);   // (whether it is there and when it was written: one look)
                if (dirty != 0 && now - dirty > System.Diagnostics.Stopwatch.Frequency)
                {
                    _clientDirtyAt = 0;
                    Directory.CreateDirectory(Folder);
                    File.WriteAllText(ClientPath, ClientHeader + Text("client", all: true));
                    _clientFileAt = File.GetLastWriteTimeUtc(ClientPath);
                }
                else if (!client.Exists)
                {
                    Directory.CreateDirectory(Folder);
                    File.WriteAllText(ClientPath, ClientHeader + Text("client", all: true));
                    _clientFileAt = File.GetLastWriteTimeUtc(ClientPath);
                }
                else
                {
                    var at = client.LastWriteTimeUtc;
                    if (at != _clientFileAt) { _clientFileAt = at; Apply(File.ReadAllText(ClientPath), "client"); }
                }
                // world: the host's overrides (only where the server runs)
                if (SawServer || force)
                {
                    var world = new FileInfo(WorldPath);
                    if (!world.Exists) { Directory.CreateDirectory(Folder); File.WriteAllText(WorldPath, WorldHeader + Commented(Text("world", all: true))); world.Refresh(); }
                    var at = world.LastWriteTimeUtc;
                    if (at != _worldFileAt && WorldWritable) { _worldFileAt = at; Apply(File.ReadAllText(WorldPath), "world"); }
                }
                FileWhy = "";
            }
            catch (Exception e) { _fileFails++; FileWhy = $"{e.GetType().Name}: {e.Message}" + (_fileFails >= 3 ? " (files given up for this session)" : ""); }
        }
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
