#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Threading;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;

namespace AeroMod;

/// <summary>
/// DEV (2026-10-15): the per-block saved data observed and set from the harness (tools/harness/test_blocksave.sh in the
/// Orbital Mod: set, save, relaunch, compare). Counters - counted in the [Init] / [Serializer] methods and the old lines'
/// claims, never per tick: aeroget AeroBlockSave.Restored / Written / LegacyClaimed / Counts / Last. Requests - asked from
/// any thread, answered on a SERVER grid's own step (AeroSimJobCore, one read while nothing is asked; ServerDevReads'
/// pattern - the server blocks are never read or written from the harness's thread):
///   aeroset AeroBlockSave.DevSet "grid|kind|which|value" (AeroBlockSaveDevForm) - then aeroget AeroBlockSave.DevSet: the answer
///   aeroset AeroBlockSave.DevDump "grid" - then aeroget AeroBlockSave.DevDump: every aero block's state on that grid
/// An answer reads "asked..." until the server step has given it.
/// </summary>
internal static partial class AeroBlockSave
{
    // ── counters (per block kind: surface, gear, airbrake, thrust, solid rocket segment, solid rocket nozzle, rotor blades, autopilot) ──
    public const int KSurface = 0, KGear = 1, KAirbrake = 2, KThrust = 3, KSolid = 4, KSolidNozzle = 5, KBlades = 6, KAutopilot = 7;
    static readonly int[] _restored = new int[8], _written = new int[8], _legacy = new int[8];
    static readonly string[] KindNames = { "surface", "gear", "airbrake", "thrust", "solid", "solidnozzle", "blades", "autopilot" };
    static string _last = "none";

    /// <summary>Blocks that took their state from their own saved data, this process (both scenes' thruster copies).</summary>
    public static int Restored => Sum(_restored);
    /// <summary>Keys written by the blocks' serializers (a thruster at the default writes none).</summary>
    public static int Written => Sum(_written);
    /// <summary>Blocks given their state by an old position-matched line (AeroSurfaceSaved.ApplyPending).</summary>
    public static int LegacyClaimed => Sum(_legacy);
    /// <summary>Per kind: "restored s/g/a/t/seg/noz written ... legacy ...".</summary>
    public static string Counts => $"restored {Per(_restored)} written {Per(_written)} legacy {Per(_legacy)}";
    /// <summary>The last count's kind and what it was ("restored aero-gear:1 0 0").</summary>
    public static string Last => Volatile.Read(ref _last);

    static int Sum(int[] a) { int n = 0; for (int i = 0; i < a.Length; i++) n += Volatile.Read(ref a[i]); return n; }
    static string Per(int[] a) { var sb = new System.Text.StringBuilder(); for (int i = 0; i < a.Length; i++) { if (i > 0) sb.Append('/'); sb.Append(Volatile.Read(ref a[i])); } return sb.ToString(); }

    internal static void CountRestored(int kind, string key) { Interlocked.Increment(ref _restored[kind]); Volatile.Write(ref _last, "restored " + key); }
    internal static void CountWritten(int kind, string key) { if (key == null) return; Interlocked.Increment(ref _written[kind]); Volatile.Write(ref _last, "written " + key); }
    internal static void CountLegacy(int kind) { Interlocked.Increment(ref _legacy[kind]); Volatile.Write(ref _last, "legacy " + KindNames[kind]); }

    // ── requests ──
    /// <summary>Non-zero: a set or a dump is waiting for the server step.</summary>
    internal static volatile int DevPending;
    const string Asked = "asked: the server step answers - read it again";
    static readonly List<string> _sets = new();
    static string _setAnswer = "none", _dumpWant, _dumpAnswer = "none";

    /// <summary>aeroset: queue a set request; aeroget: the answers to the requests the last step took (" | " between).</summary>
    public static string DevSet
    {
        get => Volatile.Read(ref _setAnswer);
        set { lock (_sets) { _sets.Add(value); _setAnswer = Asked; } DevPending = 1; }
    }

    /// <summary>aeroset: ask a dump of this grid's aero blocks; aeroget: the last dump.</summary>
    public static string DevDump
    {
        get => Volatile.Read(ref _dumpAnswer);
        set { lock (_sets) { _dumpWant = value; _dumpAnswer = Asked; } DevPending = 1; }
    }

    /// <summary>Server, a grid's own step: whatever was asked (scene: the calling server grid's - only its blocks are read).</summary>
    internal static void DevStep(Keen.VRage.DCS.Scenes.Scene scene)
    {
        if (DevPending == 0) return;
        DevPending = 0;
        string[] sets; string dump;
        lock (_sets) { sets = _sets.ToArray(); _sets.Clear(); dump = _dumpWant; _dumpWant = null; }
        // (dev only, once per request - never per tick: an answer instead of a stuck "asked")
        if (sets.Length > 0)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var s in sets)
            {
                if (sb.Length > 0) sb.Append(" | ");
                try { sb.Append(ApplySet(scene, s)); } catch (Exception e) { sb.Append("failed: ").Append(e.Message); }
            }
            Volatile.Write(ref _setAnswer, sb.ToString());
        }
        if (dump != null)
        {
            string a;
            try { a = Dump(scene, dump); } catch (Exception e) { a = "failed: " + e.Message; }
            Volatile.Write(ref _dumpAnswer, a);
        }
    }

    // a block of one kind on the wanted grid, with its cell (server step only: its own scene's entities)
    struct Hit { public object C; public int X, Y, Z; public object Grid; }

    static bool OnGrid(Entity e, string want, out object grid, out int x, out int y, out int z)
    {
        grid = null; x = y = z = 0;
        var top = e.GetTopLevelParent();
        var cg = top?.TryGet<CubeGridComponent>();
        if (cg == null) return false;
        string name; try { name = cg.DisplayName.EvaluateText(); } catch { return false; }
        if (!AeroBlockSaveDevForm.NameMatches(name, want)) return false;
        var block = e.TryGet<CubeBlockComponent>();
        if (block == null) return false;
        var bb = block.AABB; x = bb.Min.X; y = bb.Min.Y; z = bb.Min.Z; grid = top;
        return true;
    }

    /// <summary>The wanted grid's blocks of one kind, in cell order.</summary>
    static List<Hit> Blocks(Keen.VRage.DCS.Scenes.Scene scene, string kind, string want)
    {
        var hits = new List<Hit>();
        switch (kind)
        {
            case "surface":
                lock (AeroSurfaceSettingsComponent.All)
                    foreach (var s in AeroSurfaceSettingsComponent.All)
                        if (s.Entity != null && ReferenceEquals(s.Entity.Data.Scene, scene) && OnGrid(s.Entity, want, out var g, out int x, out int y, out int z)) hits.Add(new Hit { C = s, X = x, Y = y, Z = z, Grid = g });
                break;
            case "gear":
                lock (AeroGearComponent.All)
                    foreach (var gc in AeroGearComponent.All)
                        if (gc.Entity != null && ReferenceEquals(gc.Entity.Data.Scene, scene) && OnGrid(gc.Entity, want, out var g, out int x, out int y, out int z)) hits.Add(new Hit { C = gc, X = x, Y = y, Z = z, Grid = g });
                break;
            case "airbrake":
                lock (AeroAirbrakeComponent.All)
                    foreach (var b in AeroAirbrakeComponent.All)
                        if (b.Entity != null && ReferenceEquals(b.Entity.Data.Scene, scene) && OnGrid(b.Entity, want, out var g, out int x, out int y, out int z)) hits.Add(new Hit { C = b, X = x, Y = y, Z = z, Grid = g });
                break;
            case "autopilot":
                lock (AeroAutopilotComponent.All)
                    foreach (var ap in AeroAutopilotComponent.All)
                        if (ap.Entity != null && ReferenceEquals(ap.Entity.Data.Scene, scene) && OnGrid(ap.Entity, want, out var g, out int x, out int y, out int z)) hits.Add(new Hit { C = ap, X = x, Y = y, Z = z, Grid = g });
                break;
            default:
                // (both scenes' copies in this list: the server step's own only - the copy the save writes)
                lock (AeroThrustSettingsComponent.All)
                    foreach (var th in AeroThrustSettingsComponent.All)
                        if (th.Entity != null && ReferenceEquals(th.Entity.Data.Scene, scene) && th.Server && OnGrid(th.Entity, want, out var g, out int x, out int y, out int z)) hits.Add(new Hit { C = th, X = x, Y = y, Z = z, Grid = g });
                break;
        }
        hits.Sort((a, b) => AeroBlockSaveDevForm.CompareCells(a.X, a.Y, a.Z, b.X, b.Y, b.Z));
        return hits;
    }

    static string Token(Hit h) => h.C switch
    {
        AeroSurfaceSettingsComponent s => AeroBlockSaveDevForm.SurfaceToken(h.X, h.Y, h.Z, s.MaxDeflection, s.Trim, s.Pitch, s.Yaw, s.Roll, s.RestoredFromBlockData),
        AeroGearComponent g => AeroBlockSaveDevForm.GearToken(h.X, h.Y, h.Z, g.Seq.Down, g.AirshockEnabled, g.RestoredFromBlockData),
        AeroAirbrakeComponent b => AeroBlockSaveDevForm.AirbrakeToken(h.X, h.Y, h.Z, b.MaxOpening, b.RestoredFromBlockData),
        AeroThrustSettingsComponent t => AeroBlockSaveDevForm.ThrustToken(h.X, h.Y, h.Z, t.AttitudeFraction, t.RestoredFromBlockData),
        AeroAutopilotComponent ap => $"autopilot@{h.X},{h.Y},{h.Z}={AeroBlockSaveForm.Autopilot(ap.Panel).Substring(AeroBlockSaveForm.AutopilotKey.Length)}{(ap.RestoredFromBlockData ? " (restored)" : "")}",
        _ => "?",
    };

    static string ApplySet(Keen.VRage.DCS.Scenes.Scene scene, string text)
    {
        if (!AeroBlockSaveDevForm.TryParseSet(text, out var r, out string why)) return "bad request '" + text + "': " + why;
        var hits = Blocks(scene, AeroBlockSaveDevForm.BlockKind(r.Kind), r.Grid);
        int at = -1;
        if (r.ByCell) { for (int i = 0; i < hits.Count; i++) if (hits[i].X == r.X && hits[i].Y == r.Y && hits[i].Z == r.Z) { at = i; break; } }
        else if (r.Index < hits.Count) at = r.Index;
        if (at < 0) return $"no {AeroBlockSaveDevForm.BlockKind(r.Kind)} {(r.ByCell ? $"at {r.X},{r.Y},{r.Z}" : "#" + r.Index)} on '{r.Grid}' ({hits.Count} of that kind)";
        var h = hits[at]; string before = Token(h);
        switch (r.Kind)
        {
            case AeroBlockSaveDevForm.Kind.SurfaceTrim: ((AeroSurfaceSettingsComponent)h.C).Trim = r.Value; break;
            case AeroBlockSaveDevForm.Kind.SurfaceMax: ((AeroSurfaceSettingsComponent)h.C).MaxDeflection = r.Value; break;
            case AeroBlockSaveDevForm.Kind.Gear: ((AeroGearComponent)h.C).Command(r.Flag); break;   // (as the terminal / G: the motion runs)
            case AeroBlockSaveDevForm.Kind.Airshock: ((AeroGearComponent)h.C).AirshockEnabled = r.Flag; break;
            case AeroBlockSaveDevForm.Kind.AirbrakeMax: ((AeroAirbrakeComponent)h.C).MaxOpening = r.Value; break;
            case AeroBlockSaveDevForm.Kind.Thrust: ((AeroThrustSettingsComponent)h.C).AttitudeFraction = r.Value; break;
        }
        return "set " + before + " -> " + Token(h).Substring(Token(h).IndexOf('=') + 1);
    }

    static string Dump(Keen.VRage.DCS.Scenes.Scene scene, string want)
    {
        var sb = new System.Text.StringBuilder();
        var grids = new HashSet<object>(); int n = 0;
        foreach (var kind in KindNames)
            foreach (var h in Blocks(scene, kind, want)) { sb.Append(' ').Append(Token(h)); grids.Add(h.Grid); n++; }
        return $"dump '{want}': {n} block(s) on {grids.Count} grid(s):" + sb;
    }
}
