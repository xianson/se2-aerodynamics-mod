using System.Collections;
using System.IO;
using System.Reflection;
using System.Linq;
using AeroMod;

// Offline checks for Reload/ScriptResultsCleanup.cs - the world-switch fix (2026-10-19): the game's script results
// (ScriptCodeProvider._results) keep a mod's entry after its project unloads, so loading a world again in the same process
// throws "An item with the same key has already been added. Key: Simulation". The mod takes its own entry out. Checked here:
// the key finder and the removal on a fake dictionary of the same shape (two mods: each takes only its own, the other's
// stays whole, and the game's next Add then succeeds), the refusals (not the shape, the assembly under two projects, an
// entry that is no longer ours), the base-class field lookup, and - against the INSTALLED game's DLLs - the real field
// names and types the engine half reaches, with the real generic dictionary type filled and cleaned.
// SHARED: written once in the Orbital mod, copied into the Aerodynamics Mod by tools/sync_settings_ui.sh. Exit 0 = all pass.
static class Program
{
    static int _pass, _fail;
    static void Check(bool ok, string what)
    {
        if (ok) { _pass++; Console.WriteLine("  PASS  " + what); }
        else { _fail++; Console.WriteLine("  FAIL  " + what); }
    }

    // (the shape: VRageProjectID is a struct with value equality, CompilationResult a class)
    struct FakeId : IEquatable<FakeId>
    {
        public Guid Guid;
        public FakeId(int n) { Guid = new Guid(n, 0, 0, new byte[8]); }
        public bool Equals(FakeId o) => Guid == o.Guid;
        public override bool Equals(object o) => o is FakeId f && Equals(f);
        public override int GetHashCode() => Guid.GetHashCode();
        public override string ToString() => "P" + Guid.ToString().Substring(0, 8);
    }
    sealed class FakeCompilation { }
    class FakeProviderBase { private readonly object _results = "base"; }
    sealed class FakeProvider : FakeProviderBase { }

    static readonly Assembly ModA = typeof(Program).Assembly;               // (this mod)
    static readonly Assembly ModB = typeof(System.Linq.Enumerable).Assembly; // (the other mod)
    static readonly Assembly Vanilla = typeof(object).Assembly;

    static Dictionary<FakeId, Dictionary<string, (FakeCompilation, Assembly)>> World()
    {
        var r = new Dictionary<FakeId, Dictionary<string, (FakeCompilation, Assembly)>>();
        r[new FakeId(1)] = new() { ["Simulation"] = (new FakeCompilation(), ModA) };
        r[new FakeId(2)] = new() { ["Simulation"] = (new FakeCompilation(), ModB), ["Client"] = (new FakeCompilation(), ModB) };
        r[new FakeId(3)] = new() { ["Simulation"] = (new FakeCompilation(), Vanilla) };
        return r;
    }

    // what the game's LoadScriptingProjectAsync does with a project's compile (ScriptCodeProvider.cs:84-90)
    static bool GameAdds(Dictionary<FakeId, Dictionary<string, (FakeCompilation, Assembly)>> r, FakeId id, Assembly a)
    {
        try
        {
            if (!r.TryGetValue(id, out var v)) { v = new(); r[id] = v; }
            v.Add("Simulation", (new FakeCompilation(), a));
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    static int Main()
    {
        Bug();
        Find();
        TwoMods();
        Refusals();
        Fields();
        RealGame();
        Console.WriteLine($"== {_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    static void Bug()
    {
        Console.WriteLine("[the bug, reproduced on the fake]");
        var r = World();
        Check(!GameAdds(r, new FakeId(1), ModA), "a second compile of a project still in the results throws (the 'Key: Simulation' load failure)");
    }

    static void Find()
    {
        Console.WriteLine("[the key]");
        var r = World();
        Check(Equals(ScriptResultsCleanup.FindOwnKey(r, ModA, out _), new FakeId(1)), "this mod's assembly: its own project's key");
        Check(Equals(ScriptResultsCleanup.FindOwnKey(r, ModB, out _), new FakeId(2)), "the other mod's (two scripting projects under it): its key");
        Check(ScriptResultsCleanup.FindOwnKey(r, typeof(Console).Assembly, out string why) == null && why != null, "an assembly in no entry: none (" + why + ")");
        Check(ScriptResultsCleanup.FindOwnKey(new Dictionary<FakeId, Dictionary<string, (FakeCompilation, Assembly)>>(), ModA, out _) == null, "empty results: none");
    }

    static void TwoMods()
    {
        Console.WriteLine("[both mods: each takes its own]");
        var r = World();
        object a = ScriptResultsCleanup.FindOwnKey(r, ModA, out _);
        Check(ScriptResultsCleanup.RemoveOwn(r, a, ModA, out _), "this mod's entry removed");
        Check(!r.ContainsKey(new FakeId(1)) && r.Count == 2, "only that one");
        Check(r[new FakeId(2)].Count == 2 && r[new FakeId(2)]["Client"].Item2 == ModB, "the other mod's entry whole (both its scripting projects)");
        Check(r.ContainsKey(new FakeId(3)), "a third project's entry untouched");
        Check(GameAdds(r, new FakeId(1), ModA), "the next load's compile of this mod: added, no throw");
        Check(!GameAdds(r, new FakeId(2), ModB), "the other mod, not cleaned yet: still throws (each mod must clean its own)");
        object b = ScriptResultsCleanup.FindOwnKey(r, ModB, out _);
        Check(ScriptResultsCleanup.RemoveOwn(r, b, ModB, out _), "the other mod's own cleanup: removed");
        Check(GameAdds(r, new FakeId(2), ModB) && r.Count == 3, "then its compile is added too; this mod's fresh entry kept");
        Check(!ScriptResultsCleanup.RemoveOwn(r, b, Vanilla, out string why) && r.ContainsKey(new FakeId(2)), "a fresh entry of another assembly under the key: left alone (" + why + ")");
        r.Remove(new FakeId(1));
        Check(!ScriptResultsCleanup.RemoveOwn(r, a, ModA, out why) && why == "already gone", "already gone (the game cleaned up itself): nothing, no throw");
    }

    static void Refusals()
    {
        Console.WriteLine("[fails closed]");
        var r = World();
        r[new FakeId(4)] = new() { ["Simulation"] = (new FakeCompilation(), ModA) };
        Check(ScriptResultsCleanup.FindOwnKey(r, ModA, out string why) == null && why.Contains("2 projects"), "the assembly under two projects: no guess (" + why + ")");
        Check(ScriptResultsCleanup.FindOwnKey(null, ModA, out why) == null && why != null, "null results: none (" + why + ")");
        Check(ScriptResultsCleanup.FindOwnKey("not a dictionary", ModA, out why) == null && why != null, "not a dictionary: none (" + why + ")");
        Check(ScriptResultsCleanup.FindOwnKey(r, null, out _) == null, "no own assembly: none");
        var odd = new Dictionary<FakeId, string> { [new FakeId(1)] = "x" };
        Check(ScriptResultsCleanup.FindOwnKey(odd, ModA, out _) == null, "inner values not dictionaries: none, no throw");
        Check(!ScriptResultsCleanup.RemoveOwn("x", new FakeId(1), ModA, out _) && !ScriptResultsCleanup.RemoveOwn(r, null, ModA, out _), "remove: not a dictionary / no key - nothing");
        var plain = new Dictionary<FakeId, Dictionary<string, Assembly>> { [new FakeId(5)] = new() { ["Simulation"] = ModA } };
        Check(Equals(ScriptResultsCleanup.FindOwnKey(plain, ModA, out _), new FakeId(5)), "an inner value that is the assembly itself (a future shape): found");
    }

    static void Fields()
    {
        Console.WriteLine("[fields]");
        var f = ScriptResultsCleanup.FindField(typeof(FakeProvider), "_results");
        Check(f != null && Equals(f.GetValue(new FakeProvider()), "base"), "a base class's private field found from the derived type");
        Check(ScriptResultsCleanup.FindField(typeof(FakeProvider), "_nope") == null, "a missing field: null");
    }

    // ── the installed game ──

    static string Game2 => Environment.GetEnvironmentVariable("SE2_GAME2") ?? @"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers2\Game2";

    static void RealGame()
    {
        Console.WriteLine("[the installed game: " + Game2 + "]");
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string p = Path.Combine(Game2, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };
        Assembly scripting, core;
        try
        {
            scripting = Assembly.LoadFrom(Path.Combine(Game2, "VRage.Scripting.dll"));
            core = Assembly.LoadFrom(Path.Combine(Game2, "VRage.Core.dll"));
        }
        catch (Exception e) { Check(false, "the game's VRage.Scripting / VRage.Core load: " + e.GetType().Name + " " + e.Message + " (set SE2_GAME2)"); return; }

        // the provider: GameScriptCodeProvider (the Mod and DLC provider, GameApp.AddScripting) -> its base's private _results
        var gscp = scripting.GetType("Keen.VRage.Scripting.Project.GameScriptCodeProvider");
        Check(gscp != null, "Keen.VRage.Scripting.Project.GameScriptCodeProvider exists");
        var rf = gscp == null ? null : ScriptResultsCleanup.FindField(gscp, "_results");
        Check(rf != null, "its _results field is found from the derived type (declared on " + rf?.DeclaringType?.Name + ")");
        if (rf == null) return;
        var ft = rf.FieldType;
        Console.WriteLine("        _results: " + Nice(ft));
        bool shape = ft.IsGenericType && ft.GetGenericTypeDefinition() == typeof(Dictionary<,>)
                     && ft.GetGenericArguments()[0].FullName == "Keen.VRage.Core.Project.VRageProjectID";
        Type inner = shape ? ft.GetGenericArguments()[1] : null;
        shape &= inner != null && inner.IsGenericType && inner.GetGenericTypeDefinition() == typeof(Dictionary<,>) && inner.GetGenericArguments()[0] == typeof(string);
        Type tuple = shape ? inner.GetGenericArguments()[1] : null;
        shape &= tuple != null && tuple.IsGenericType && tuple.GetGenericArguments().Contains(typeof(Assembly)) && typeof(System.Runtime.CompilerServices.ITuple).IsAssignableFrom(tuple);
        Check(shape, "_results is Dictionary<VRageProjectID, Dictionary<string, (..., Assembly)>> (the tuple an ITuple)");

        // the project manager: the protected _codeProviders, readable as IReadOnlyDictionary<ProjectType, IProjectCodeProvider>
        var pm = core.GetType("Keen.VRage.Core.EngineComponents.ProjectManagerEngineComponent");
        var ipm = core.GetType("Keen.VRage.Core.EngineComponents.IProjectManager");
        var pt = core.GetType("Keen.VRage.Core.Project.ProjectType");
        var icp = core.GetType("Keen.VRage.Core.Project.IProjectCodeProvider");
        Check(pm != null && ipm != null && ipm.IsAssignableFrom(pm), "ProjectManagerEngineComponent is the engine's IProjectManager");
        var cpf = pm == null ? null : ScriptResultsCleanup.FindField(pm, "_codeProviders");
        bool cpOk = cpf != null && pt != null && icp != null
                    && typeof(IReadOnlyDictionary<,>).MakeGenericType(pt, icp).IsAssignableFrom(cpf.FieldType);
        Check(cpOk, "its _codeProviders reads as IReadOnlyDictionary<ProjectType, IProjectCodeProvider> (" + (cpf == null ? "missing" : Nice(cpf.FieldType)) + ")");
        Check(pt != null && Enum.GetNames(pt).Contains("Mod"), "ProjectType.Mod exists");
        Check(icp != null && gscp != null && icp.IsAssignableFrom(gscp), "GameScriptCodeProvider is an IProjectCodeProvider");
        var ev = ipm?.GetEvent("BeforeProjectsUnloaded");
        var vp = core.GetType("Keen.VRage.Core.Project.VRageProject");
        Check(ev != null && vp != null && ev.EventHandlerType == typeof(Action<>).MakeGenericType(typeof(IEnumerable<>).MakeGenericType(vp)),
              "IProjectManager.BeforeProjectsUnloaded is Action<IEnumerable<VRageProject>>");
        var data = vp?.GetProperty("Data");
        var pid = data?.PropertyType.GetProperty("ProjectID");
        Check(pid != null && pid.PropertyType.FullName == "Keen.VRage.Core.Project.VRageProjectID", "VRageProject.Data.ProjectID is a VRageProjectID");

        // the real generic type, filled the way the game fills it, cleaned the way the mod cleans it
        if (!shape) return;
        try
        {
            var idT = ft.GetGenericArguments()[0];
            var idEq = idT.GetMethod("Equals", new[] { typeof(object) });
            Check(idEq != null && idEq.DeclaringType == idT, "VRageProjectID overrides Equals(object) (the boxed key compare)");
            object Id(int n) { var id = Activator.CreateInstance(idT); idT.GetProperty("Guid").SetValue(id, new Guid(n, 0, 0, new byte[8])); return id; }
            var results = (IDictionary)Activator.CreateInstance(ft);
            object Entry(Assembly a)
            {
                var d = (IDictionary)Activator.CreateInstance(inner);
                d["Simulation"] = Activator.CreateInstance(tuple, new object[] { null, a });
                return d;
            }
            results[Id(1)] = Entry(ModA);
            results[Id(2)] = Entry(ModB);
            object key = ScriptResultsCleanup.FindOwnKey(results, ModA, out string why);
            Check(key != null && key.Equals(Id(1)), "real type: this mod's key found (" + (key ?? why) + ")");
            Check(ScriptResultsCleanup.RemoveOwn(results, key, ModA, out why) && results.Count == 1 && results.Contains(Id(2)), "real type: only this mod's entry removed");
        }
        catch (Exception e) { Check(false, "real type: " + e.GetType().Name + " " + e.Message); }
    }

    static string Nice(Type t) => !t.IsGenericType ? t.Name : t.Name.Split('`')[0] + "<" + string.Join(", ", t.GetGenericArguments().Select(Nice)) + ">";
}
