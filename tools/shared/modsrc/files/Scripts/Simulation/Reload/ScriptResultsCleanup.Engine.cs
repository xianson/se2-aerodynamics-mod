using System;
using System.Collections.Generic;
using Keen.VRage.Core.EngineComponents;
using Keen.VRage.Core.Project;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Diagnostics;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace __MOD__;

/// <summary>
/// The engine half of the world-switch fix (ScriptResultsCleanup.cs says why). <see cref="Ensure"/> runs once per load of
/// this assembly (from the mod's definition post-processor - the game instantiates it on every world load, client and
/// server, before any entity exists): it finds the game's script results (the engine's IProjectManager -> the protected
/// <c>ProjectManagerEngineComponent._codeProviders[ProjectType.Mod]</c> -> the private <c>ScriptCodeProvider._results</c>),
/// finds this assembly's entry there and subscribes to the public <c>IProjectManager.BeforeProjectsUnloaded</c>.
///
/// Why that event: it fires inside <c>ProjectManagerEngineComponent.PopProjects</c>, exactly when the game drops THIS
/// project - after the session is gone (the world data unloads with no session alive), before the next world's projects
/// compile (GameDataLoaderComponent unloads the old world data first) - and only then: a quick load of the same world
/// keeps its projects (and its entry, which the next session's ConfigureSession reads) and never fires it. The handler is
/// this (old) copy's code, still loaded: the game never unloads a mod assembly, so nothing has to wait for the next
/// load's new code. Another batch's unload (our project not in it) is ignored; ours: unsubscribe, run
/// <see cref="OwnProjectUnloading"/>, take our entry out (only if it is still ours).
///
/// Fails closed: anything missing (a future game renames a field, or cleans up itself) logs ONE line and does nothing -
/// the game then behaves exactly as without the fix. The handler never throws into the game's unload. Teardown only: one
/// lookup per load, nothing per frame.
/// Reflection: docs/REFLECTION_AND_WHITELIST.md.
/// SHARED: written once in the Aerodynamics Mod's tools/shared/modsrc/files/ (this path under it), copied into both mods by
/// tools/shared/modsrc/sync.sh; edit it THERE.
/// </summary>
internal static partial class ScriptResultsCleanup
{
    /// <summary>What it did last (harness / log): "watching ...", "removed ...", "off: ...".</summary>
    public static string Status = "not started";

    /// <summary>Raised once, when the game unloads this mod's project (a world left for the menu or another world): unhook
    /// engine-level handlers this copy installed, which the next load's copy cannot reach (it is another assembly).</summary>
    internal static event Action OwnProjectUnloading;

    static int _started;
    static IProjectManager _pm;
    static object _results, _key;
    static Action<IEnumerable<VRageProject>> _handler;

    /// <summary>Once per load of this assembly; any thread (later calls: one volatile read).</summary>
    internal static void Ensure()
    {
        if (System.Threading.Volatile.Read(ref _started) != 0 || System.Threading.Interlocked.Exchange(ref _started, 1) != 0) return;
        string why = Watch();
        Say(why == null ? "watching (the entry of project " + _key + " goes when the game unloads it)" : "off: " + why, why != null);
    }

    static string Watch()
    {
        try
        {
            var pm = Keen.VRage.Library.Utils.Singleton<Keen.VRage.Core.VRageCore>.Instance?.Engine?.AsInterface<IProjectManager>();
            if (pm == null) return "no project manager on the engine";
            var cpf = FindField(pm.GetType(), "_codeProviders");
            if (cpf == null) return pm.GetType().Name + "._codeProviders not found";
            if (!(cpf.GetValue(pm) is IReadOnlyDictionary<ProjectType, IProjectCodeProvider> providers)) return "_codeProviders is not a ProjectType -> IProjectCodeProvider map";
            if (!providers.TryGetValue(ProjectType.Mod, out var provider) || provider == null) return "no Mod code provider";
            var rf = FindField(provider.GetType(), "_results");
            if (rf == null) return provider.GetType().Name + "._results not found";
            object results = rf.GetValue(provider);
            object key = FindOwnKey(results, typeof(ScriptResultsCleanup).Assembly, out string why);
            if (key == null) return why;
            _pm = pm; _results = results; _key = key; _handler = OnBeforeProjectsUnloaded;
            pm.BeforeProjectsUnloaded += _handler;
            return null;
        }
        catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
    }

    static void OnBeforeProjectsUnloaded(IEnumerable<VRageProject> projects)
    {
        try
        {
            bool ours = false;
            if (projects != null)
                foreach (var p in projects)
                    if (p?.Data != null && _key.Equals(p.Data.ProjectID)) { ours = true; break; }
            if (!ours) return;   // (another batch: keep watching)
            _pm.BeforeProjectsUnloaded -= _handler;
            try { OwnProjectUnloading?.Invoke(); }
            catch (Exception e) { Say("an unload hook threw: " + e.GetType().Name + ": " + e.Message, true); }
            bool removed = RemoveOwn(_results, _key, typeof(ScriptResultsCleanup).Assembly, out string why);
            Say(removed ? "removed this mod's compiled entry (project " + _key + "): the next world compiles it fresh" : "nothing removed: " + why, false);
        }
        catch (Exception e) { Say("failed: " + e.GetType().Name + ": " + e.Message, true); }
    }

    static void Say(string what, bool warn)
    {
        Status = what;
        string line = "[" + typeof(ScriptResultsCleanup).Assembly.GetName().Name + "] world switch: " + what;
        if (warn) Log.Default?.Warning(line); else Log.Default?.Info(line);
    }
}
