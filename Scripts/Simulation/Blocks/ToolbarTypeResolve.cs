#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Reflection;
using System.Threading;

namespace AeroMod;

/// <summary>
/// THE TOOLBAR'S MOD PANEL BUTTONS (2026-10-21; engine-free - Tests/SceneTests runs it). A panel button put on a toolbar
/// (DetailModelButtonToolbarAction) keeps its model's type by FULL NAME (DetailModelToolbarActionBase._detailModelType) and
/// finds it again with BlockToolbarServerComponent.TryGetDetailModelType: Type.GetType(name) - no assembly, so only the game's
/// own assembly and the core library are searched and a mod's type is never found: the model and its method null, the action's
/// label blank, Valid false, TryRun nothing (every Aero panel button on a toolbar, in game 2026-10-20 - "appanel actions": blank).
/// The runtime asks AppDomain.TypeResolve when Type.GetType finds nothing: this answers THIS assembly for a name in our namespace
/// that this assembly has (any other name: not ours to answer - null). The Orbital Mod does the same for its own namespace.
/// NEWEST COPY WINS: the game never unloads a mod assembly, and the first handler that answers wins, so a stale copy (world A's,
/// still loaded) would hand the next world's toolbar world A's type. Each copy writes itself into one AppDomain slot when it
/// hooks and answers only while it is the slot's; the world-switch fix (Reload/ScriptResultsCleanup: OwnProjectUnloading) unhooks
/// it when the game unloads this project (EntityInjector wires both). Resolved once per action (the game caches the type).
/// Reflection: docs/REFLECTION_AND_WHITELIST.md (§3.9).
/// </summary>
public static class ToolbarTypeResolve
{
    /// <summary>The names answered: this mod's namespace.</summary>
    public const string Prefix = "AeroMod.";
    /// <summary>The AppDomain slot naming the copy that answers (the newest hooked).</summary>
    public const string Slot = "AeroMod.ToolbarTypeResolve.Owner";
    static readonly Assembly Own = typeof(ToolbarTypeResolve).Assembly;
    static ResolveEventHandler _handler;
    static int _hooked;
    [ThreadStatic] static bool _inside;
    /// <summary>Names answered / passed on (diagnostics, the harness).</summary>
    public static int Answered, Declined;
    public static string Status = "not hooked";

    /// <summary>Once per load of this assembly (any thread; later calls nothing). True the first time (the caller wires the unhook).</summary>
    public static bool Hook()
    {
        if (Interlocked.Exchange(ref _hooked, 1) != 0) return false;
        try
        {
            var h = new ResolveEventHandler(OnTypeResolve);
            AppDomain.CurrentDomain.SetData(Slot, Own);   // (this copy answers from now: the newest)
            AppDomain.CurrentDomain.TypeResolve += h;
            _handler = h;
            Status = "hooked (" + Own.GetName().Name + ")";
        }
        catch (Exception e) { Status = "off: " + e.GetType().Name + ": " + e.Message; }
        return true;
    }

    /// <summary>The game unloads this project (OwnProjectUnloading): the handler out, the slot cleared if it is still ours.</summary>
    public static void Unhook()
    {
        var h = Interlocked.Exchange(ref _handler, null);
        if (h == null) return;
        try
        {
            AppDomain.CurrentDomain.TypeResolve -= h;
            if (ReferenceEquals(AppDomain.CurrentDomain.GetData(Slot), Own)) AppDomain.CurrentDomain.SetData(Slot, null);
            Status = "unhooked";
        }
        catch (Exception e) { Status = "unhook: " + e.GetType().Name + ": " + e.Message; }
    }

    static Assembly OnTypeResolve(object sender, ResolveEventArgs e)
    {
        string n = e?.Name;
        if (n == null || !n.StartsWith(Prefix, StringComparison.Ordinal) || _inside) return null;
        _inside = true;
        try
        {
            // (a newer copy of this mod hooked since: its answer, not ours; a name this assembly lacks: none)
            if (!ReferenceEquals(AppDomain.CurrentDomain.GetData(Slot), Own) || Own.GetType(n, false) == null) { Interlocked.Increment(ref Declined); return null; }
            Interlocked.Increment(ref Answered);
            return Own;
        }
        catch { return null; }   // (never a throw into the game's type lookup)
        finally { _inside = false; }
    }
}
