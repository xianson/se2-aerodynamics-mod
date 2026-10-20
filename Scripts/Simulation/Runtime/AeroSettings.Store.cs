#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
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

    /// <summary>A setting's UI metadata (Setting.Ui): label, group, tier ("basic", "advanced", "hidden"), unit, step, and the
    /// choices' labels (a copy; null: a number).</summary>
    public static bool UiInfo(string name, out string label, out string group, out string tier, out string unit, out double step, out string[] choices)
    {
        label = group = tier = unit = null; step = 0; choices = null;
        if (name == null || !_by.TryGetValue(name, out var s)) return false;
        label = s.Label; group = s.Group; unit = s.Unit; step = s.Step;
        tier = s.Tier == Tier.Basic ? "basic" : s.Tier == Tier.Advanced ? "advanced" : "hidden";
        choices = s.Choices == null ? null : (string[])s.Choices.Clone();
        return true;
    }

    public static bool TryGet(string name, out double value)
    {
        value = 0;
        if (name == null || !_by.TryGetValue(name, out var s) || s.Type == "text") return false;   // (a text has no number: TryGetText)
        value = s.Get();
        return true;
    }

    /// <summary>A text setting's value now (false: no such setting, or not a text one).</summary>
    public static bool TryGetText(string name, out string value)
    {
        value = null;
        if (name == null || !_by.TryGetValue(name, out var s) || s.Type != "text") return false;
        value = s.GetText() ?? "";
        return true;
    }

    /// <summary>Change a text setting (from the API or a script). False, with why: no such text setting, its rule, a world one
    /// where the server does not run or the host file forces it.</summary>
    public static bool SetText(string name, string value, out string why) => SetText(name, value, out why, fromStorage: false);

    static bool SetText(string name, string value, out string why, bool fromStorage)
    {
        why = null;
        if (name == null || !_by.TryGetValue(name, out var s)) { why = "no setting " + name; return false; }
        if (s.Type != "text") { why = "not a text setting (SetSetting)"; return false; }
        value = (value ?? "").Trim();
        if (value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0) { why = "one line only"; return false; }
        if (s.Scope == "world" && !fromStorage)
        {
            if (!CanEditWorld()) { why = "world settings are set by the host / server"; return false; }
            if (IsForced(s.Name)) { why = "forced by the host file " + WorldPath; return false; }
        }
        lock (_lock)
        {
            bool same = string.Equals(s.GetText(), value, StringComparison.Ordinal);
            if (same && string.Equals(s.StoredText, value, StringComparison.Ordinal)) return true;
            if (!same && s.CheckText != null && (why = s.CheckText(value)) != null) return false;
            if (!same) { s.SetText(value); System.Threading.Interlocked.Increment(ref _version); }
            s.StoredText = s.GetText() ?? "";
            if (s.Scope == "client" && !fromStorage) _clientDirtyAt = System.Diagnostics.Stopwatch.GetTimestamp();
            if (s.Scope == "world" && !fromStorage) _worldUnsaved = true;
        }
        Log.Default?.Info($"[AERO] setting {s.Name} = {value}{(fromStorage ? " (loaded)" : "")}");
        return true;
    }

    /// <summary>A setting off its default (the live value or the player's): a text compares its text.</summary>
    static bool OffDefault(Setting s) => s.Type == "text"
        ? !string.Equals(s.GetText(), s.DefaultText, StringComparison.Ordinal) || !string.Equals(s.StoredText, s.DefaultText, StringComparison.Ordinal)
        : s.Get() != s.Default || s.Stored != s.Default;

    /// <summary>A setting back to its default through the store.</summary>
    static bool Reset(Setting s, out string why, bool fromStorage)
        => s.Type == "text" ? SetText(s.Name, s.DefaultText, out why, fromStorage) : Set(s.Name, s.Default, out why, fromStorage);

    /// <summary>Change a setting (from the API or a script). False, with why, when it cannot be.</summary>
    public static bool Set(string name, double value, out string why) => Set(name, value, out why, fromStorage: false);

    static bool Set(string name, double value, out string why, bool fromStorage)
    {
        why = null;
        if (name == null || !_by.TryGetValue(name, out var s)) { why = "no setting " + name; return false; }
        if (s.Type == "text") { why = "a text setting (SetSettingText)"; return false; }
        if (double.IsNaN(value) || double.IsInfinity(value)) { why = "not a number"; return false; }
        if (s.Type != "float") value = Math.Round(value);
        if (s.Type == "bool" && value != 0 && value != 1) { why = "a bool is 0 or 1"; return false; }
        if (value < s.Min || value > s.Max) { why = $"out of range {s.Min.ToString(Inv)}..{s.Max.ToString(Inv)}"; return false; }
        if (s.Scope == "world" && !fromStorage)
        {
            if (!CanEditWorld()) { why = "world settings are set by the host / server"; return false; }
            if (IsForced(s.Name)) { why = "forced by the host file " + WorldPath; return false; }
        }
        lock (_lock)
        {
            // (the live field may hold a direct write - a harness's - that this value equals: it still becomes the player's)
            bool same = s.Get() == value;
            if (same && s.Stored == value) return true;
            if (!same && s.Check != null && (why = s.Check(value)) != null) return false;
            if (!same)
            {
                s.Set(value);
                System.Threading.Interlocked.Increment(ref _version);
                if (s.Rebuild) System.Threading.Interlocked.Increment(ref AeroSwitch.Generation);   // (every grid rebuilds: AeroSimJob)
            }
            s.Stored = s.Get();   // (what the field took: the files' text as it always was)
            if (s.Scope == "client" && !fromStorage) _clientDirtyAt = System.Diagnostics.Stopwatch.GetTimestamp();
            if (s.Scope == "world" && !fromStorage) _worldUnsaved = true;
        }
        Log.Default?.Info($"[AERO] setting {s.Name} = {Fmt(s, value)}{(fromStorage ? " (loaded)" : "")}");
        return true;
    }

    // ── who may change what, and groups (settings DESIGN.md W3) ──

    /// <summary>World settings may be changed from here (a settings screen greys them out otherwise; Set refuses). THE one
    /// place for "who may": admin checks go here once SE2 has real multiplayer.</summary>
    public static bool CanEditWorld() => WorldWritable && IsLocalHostOrSinglePlayer();

    /// <summary>This process hosts the world. SE2 2.4: always - its server runs in the client's process (the networking is
    /// a mock) and has no admins a mod can see. A real dedicated server or remote client changes only this.</summary>
    static bool IsLocalHostOrSinglePlayer() => true;

    /// <summary>The names the host file (world.cfg) set at its last apply: they win over the save, so a screen shows them
    /// locked and Set refuses them ("forced by the host file"). Swapped whole (read on any thread).</summary>
    static volatile HashSet<string> _forced = new(StringComparer.OrdinalIgnoreCase);
    public static bool IsForced(string name) => name != null && _forced.Contains(name);

    /// <summary>The host file's text applied over the world's values; the names it set become the forced ones.</summary>
    internal static void ApplyHostFile(string text)
    {
        var forced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Apply(text, "world", forced);
        _forced = forced;
    }

    /// <summary>A world setting changed (the API, a screen) since the world was last saved: it goes into the save carrier on
    /// the world's next save (nothing is written now). A settings screen's "saved with this world" hint.</summary>
    public static bool WorldUnsaved => _worldUnsaved;
    static volatile bool _worldUnsaved;
    /// <summary>The save carrier serialized the world scope.</summary>
    internal static void WorldSaved() => _worldUnsaved = false;

    /// <summary>Every setting of a group (and scope; null: either) back to its default, through Set (so a world group
    /// needs CanEditWorld, and a forced name stays). Returns how many changed; why: the first refusal, or no such group.</summary>
    public static int ResetGroup(string scope, string group, out string why)
    {
        why = null;
        int n = 0; bool any = false;
        // (twice: a default refused against a partner not yet reset - Entry.OnsetMach under a lowered FullMach - goes in on the second)
        for (int pass = 0; pass < 2; pass++)
            foreach (var s in _all)
            {
                if (!string.Equals(s.Group, group, StringComparison.OrdinalIgnoreCase) || (scope != null && s.Scope != scope)) continue;
                any = true;
                if (!OffDefault(s)) continue;
                if (Reset(s, out string w, fromStorage: false)) n++;
                else if (pass == 1) why ??= s.Name + ": " + w;
            }
        if (!any) why = "no group " + group + (scope != null ? " in " + scope : "");
        return n;
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
            if (s.Scope != scope || s.Derived) continue;   // (a derived one - the assists' preset - is its members)
            if (s.Type == "text")
            {
                string t = s.StoredText ?? "";
                if (!all && string.Equals(t, s.DefaultText, StringComparison.Ordinal)) continue;
                if (all) sb.Append("# ").Append(s.Doc).Append(" (text, default ").Append(s.DefaultText.Length > 0 ? s.DefaultText : "empty").Append(")\n");
                sb.Append(s.Name).Append(" = ").Append(t).Append('\n');
                continue;
            }
            double v = s.Stored;   // (the player's value, never a harness's direct write to the field)
            if (!all && v == s.Default) continue;
            if (all) sb.Append("# ").Append(s.Doc).Append(" (").Append(s.Type).Append(", default ").Append(Fmt(s, s.Default))
                       .Append(", ").Append(Fmt(s, s.Min)).Append("..").Append(Fmt(s, s.Max)).Append(s.Rebuild ? ", rebuilds every grid" : "").Append(")\n");
            sb.Append(s.Name).Append(" = ").Append(Fmt(s, v)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Apply "name = value" lines of a scope (others ignored). Returns the count applied; applied (if given)
    /// collects their names.</summary>
    internal static int Apply(string text, string scope, HashSet<string> applied = null)
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
            if (s.Type == "text")
            {
                if (SetText(name, val, out string twhy, fromStorage: true)) { n++; applied?.Add(s.Name); }
                else Log.Default?.Info($"[AERO] setting {name} = {val} refused: {twhy}");
                continue;
            }
            if (!Parse(val, out double v)) { Log.Default?.Info($"[AERO] setting {name}: '{val}' is not a value"); continue; }
            if (Set(name, v, out string why, fromStorage: true)) { n++; applied?.Add(s.Name); }
            else (retry ??= new List<(string, double, string)>()).Add((name, v, val));
        }
        if (retry != null)
            foreach (var (name, v, val) in retry)
            {
                if (Set(name, v, out string why, fromStorage: true)) { n++; applied?.Add(_by[name].Name); }
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
            foreach (var s in _all) if (s.Scope == scope && OffDefault(s)) Reset(s, out _, fromStorage: true);
    }

    // ── the world save (the carriers: AeroWorldSettingsComponent on the server planets, AeroWorldBodyComponent on the
    //    players' server bodies - Save/WorldCarriers.cs decides which text is applied and when the world is over) ──

    internal const string SaveKey = "aero-settings:";

    /// <summary>The world's settings carriers (2026-10-19: planetless worlds - the bodies carry the text too; a body's text
    /// wins over a planet's; the world's end is its last carrier leaving while the session unloads, or another session).</summary>
    internal static readonly WorldCarriers Carriers = new WorldCarriers(
        (text, kind) => WorldLoaded(text, legacyLines: kind == WorldCarriers.Kind.Planet),
        (why, unloading) => { Log.Default?.Info($"[AERO] world reset: {why}"); WorldDefaults(); WorldHooks?.Invoke(); },
        () => _worldUnsaved);

    /// <summary>The per-world state beyond the settings, dropped at the world's end (set by the game side: the API's structure
    /// events, quick launch's ask, the autopilot's toasts - AeroWorldSettingsComponent.Hooks).</summary>
    internal static Action WorldHooks;

    /// <summary>A carrier's saved key (`aero-settings:` + the text) out of its builder's map; "" if none.</summary>
    internal static string SavedText(IEnumerable<string> keys)
    {
        if (keys != null) foreach (var k in keys) if (k != null && k.StartsWith(SaveKey) && k.Length > SaveKey.Length) return k.Substring(SaveKey.Length);
        return "";
    }

    /// <summary>The world's settings from a saved text (Carriers picks which; the host's overrides re-read over it).
    /// `legacyLines`: a planet's text - saves before 2026-10-15 also held the blocks' lines there (AeroSurfaceSaved).</summary>
    internal static void WorldLoaded(string text, bool legacyLines = true)
    {
        if (string.IsNullOrEmpty(text)) return;
        ResetScope("world");
        int n = Apply(text, "world");
        if (legacyLines) AeroSurfaceSaved.Load(text);
        Log.Default?.Info($"[AERO] world settings loaded: {n} ({(legacyLines ? "a planet's" : "a body's")} carrier)");
        _worldUnsaved = false;
        _worldFileAt = DateTime.MinValue;   // (the host's overrides again, over the save)
    }

    /// <summary>A carrier joined its session's scene (the server side is here).</summary>
    internal static void CarrierAdded(object session) { SawServer = true; Carriers.Added(session); Poll(force: true); }

    /// <summary>A carrier leaves its session's scene; the last one while the session unloads ends the world (Carriers).</summary>
    internal static void CarrierRemoved(object session, bool unloading) => Carriers.Removed(session, unloading);

    /// <summary>The world is over: world settings back to defaults for the next world.</summary>
    internal static void WorldDefaults()
    {
        ResetScope("world");
        AeroSurfaceSaved.Clear();
        _worldFileAt = DateTime.MinValue;
        _forced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // (the next world's apply of the host file sets them again)
        _worldUnsaved = false;
        System.Threading.Volatile.Write(ref _readWorld, null);   // (nor a read of it still waiting for a tick)
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
    /// and when written together) and the folder made only to write into it. What it read is applied HERE, on the next job
    /// that polls (2026-10-15, a review: the task applied it itself, so the setters ran on a pool thread - they touch the
    /// jobs' state; the Orbital Mod's poll does the same). A forced poll (a world's settings carrier added) still reads and
    /// applies at once, on its caller's thread, as before.</summary>
    internal static void Poll(bool force = false)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        // (called by every grid's sim tick and every client grid's effects frame - tens of thousands a second on many
        //  threads: each thread looks at the shared due time at most 4 times a second, its own clock first; the shared
        //  cache line read by all of them cost ~5 ms/s in a profile, 2026-10-15)
        if (!force && now < _localNext) return;
        _localNext = now + System.Diagnostics.Stopwatch.Frequency / 4;
        ApplyRead();   // (the last background read, if any)
        long due = System.Threading.Interlocked.Read(ref _pollAt);
        if (!force && now < due) return;
        if (System.Threading.Interlocked.CompareExchange(ref _pollAt, now + 2 * System.Diagnostics.Stopwatch.Frequency, due) != due && !force) return;
        GameStallWarning.Apply();   // (the game's stall warning: removed unless Hud.GameStallWarning)
        if (_fileFails >= 3) return;
        if (force) { PollFiles(true); ApplyRead(); return; }
        if (System.Threading.Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;
        try { System.Threading.Tasks.Task.Factory.StartNew(_pollTask, System.Threading.CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.None, System.Threading.Tasks.TaskScheduler.Default); }
        catch { System.Threading.Volatile.Write(ref _polling, 0); }
    }
    static int _polling;
    [ThreadStatic] static long _localNext;
    static readonly object _filesLock = new object();
    static readonly Action _pollTask = () => { try { PollFiles(false); } finally { System.Threading.Volatile.Write(ref _polling, 0); } };
    /// <summary>A file's text read by the poll, waiting for a job to apply it (null: none).</summary>
    static string _readClient, _readWorld;

    /// <summary>A job's poll: the text the background poll read, applied (the client file's values; the host file's, which
    /// become the forced names).</summary>
    static void ApplyRead()
    {
        if (System.Threading.Volatile.Read(ref _readClient) == null && System.Threading.Volatile.Read(ref _readWorld) == null) return;
        string client = System.Threading.Interlocked.Exchange(ref _readClient, null);
        if (client != null) Apply(client, "client");
        string world = System.Threading.Interlocked.Exchange(ref _readWorld, null);
        if (world != null) ApplyHostFile(world);
    }

    /// <summary>The files themselves (one poll at a time: the lock - a forced poll may meet a background one). Writes the
    /// client file when an API change has settled (1 s) or it is missing; otherwise reads what was edited, for ApplyRead.</summary>
    static void PollFiles(bool force)
    {
        lock (_filesLock)
        {
            if (_fileFails >= 3) return;
            try
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                // client: written after an API change has settled (1 s), else read when edited by hand
                long dirty = System.Threading.Interlocked.Read(ref _clientDirtyAt);
                var client = new FileInfo(ClientPath);   // (whether it is there and when it was written: one look)
                if (dirty != 0 && now - dirty > System.Diagnostics.Stopwatch.Frequency) WriteClient(dirty);
                else if (!client.Exists) WriteClient(0);   // (0: a pending change stays marked)
                else
                {
                    var at = client.LastWriteTimeUtc;
                    if (at != _clientFileAt) { _clientFileAt = at; System.Threading.Volatile.Write(ref _readClient, File.ReadAllText(ClientPath)); }
                }
                // world: the host's overrides (only where the server runs)
                if (SawServer || force)
                {
                    var world = new FileInfo(WorldPath);
                    if (!world.Exists) { Directory.CreateDirectory(Folder); File.WriteAllText(WorldPath, WorldHeader + Commented(Text("world", all: true))); world.Refresh(); }
                    var at = world.LastWriteTimeUtc;
                    if (at != _worldFileAt && WorldWritable) { _worldFileAt = at; System.Threading.Volatile.Write(ref _readWorld, File.ReadAllText(WorldPath)); }
                }
                FileWhy = "";
            }
            catch (Exception e) { _fileFails++; FileWhy = $"{e.GetType().Name}: {e.Message}" + (_fileFails >= 3 ? " (files given up for this session)" : ""); }
        }
    }

    /// <summary>The client file written; the change marked at `dirty` (its time, read at the poll's start) cleared.</summary>
    static void WriteClient(long dirty)
    {
        // (cleared only if still that mark - a Set since marked a later time, and the next poll writes it again: the clear
        //  was unconditional, and a Set landing between its text being taken and the clear was left out of the file
        //  until another change, 2026-10-15; the exchange is also a full fence, so the text below is read after it)
        System.Threading.Interlocked.CompareExchange(ref _clientDirtyAt, 0, dirty);
        Directory.CreateDirectory(Folder);
        File.WriteAllText(ClientPath, ClientHeader + Text("client", all: true));
        _clientFileAt = File.GetLastWriteTimeUtc(ClientPath);
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
