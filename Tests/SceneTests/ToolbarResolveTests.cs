// The toolbar's mod panel buttons (Scripts/Simulation/Blocks/ToolbarTypeResolve.cs, run as it is): the game finds a toolbar
// action's model type with Type.GetType(full name) - no assembly - which never finds a mod type. Here the file is compiled TWICE
// (two copies of the mod, as two world loads leave in one process) with a stand-in model, both loaded, and the game's own call
// made from this assembly: none before the hook; the copy hooked answers for its namespace only; a newer copy wins over a stale
// one still hooked; unhooked, nothing answers. And the wiring: EntityInjector hooks it and unhooks it with the world-switch fix,
// every panel model of the mod is in the namespace answered. And the Autopilot panel told of what the block's step changes (the
// Enemy switch done, a mode or height written back) on the UI thread.
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

static class ToolbarResolveTests
{
    const string Model = "AeroMod.AeroStubBlockDetailModel";

    static Assembly Copy(string src, string name)
    {
        var stub = "namespace AeroMod { public class AeroStubBlockDetailModel { public void HoldPrograde() { } } }";
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
        var comp = CSharpCompilation.Create(name, new[] { CSharpSyntaxTree.ParseText(src), CSharpSyntaxTree.ParseText(stub) }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable));
        using var ms = new MemoryStream();
        var r = comp.Emit(ms);
        if (!r.Success) throw new Exception("ToolbarTypeResolve.cs does not compile alone: " + string.Join("; ", r.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return Assembly.Load(ms.ToArray());
    }

    static void Call(Assembly a, string m) => a.GetType("AeroMod.ToolbarTypeResolve")!.GetMethod(m)!.Invoke(null, null);
    static int Answered(Assembly a) => (int)a.GetType("AeroMod.ToolbarTypeResolve")!.GetField("Answered")!.GetValue(null)!;
    // (the game's own call: BlockToolbarServerComponent.TryGetDetailModelType - Type.GetType(name), from an assembly not the mod's)
    static Type? Game(string name) => Type.GetType(name);

    public static void Run(Action<bool, string> check, string sim)
    {
        string src = File.ReadAllText(Path.Combine(sim, "Blocks", "ToolbarTypeResolve.cs"));
        var a = Copy(src, "AeroModCopyA"); var b = Copy(src, "AeroModCopyB");
        check(Game(Model) == null, "toolbar types: unhooked, the game's Type.GetType finds no mod model (the blank action)");
        Call(a, "Hook");
        var t = Game(Model);
        check(t != null && t.Assembly == a && t.GetMethod("HoldPrograde") != null, $"toolbar types: hooked, the model found in the mod's assembly and its button method ({t?.Assembly.GetName().Name ?? "none"})");
        int before = Answered(a);
        check(Game("Keen.NotOurs.SomeModel") == null && Game("AeroMod.NoSuchModel") == null && Answered(a) == before,
            "toolbar types: another namespace or a name the mod lacks - not answered");
        // (two worlds: world A's copy still hooked - its unload's cleanup did not run - and world B's hooks: B's answers)
        Call(b, "Hook");
        t = Game(Model);
        check(t != null && t.Assembly == b, $"toolbar types: a newer copy wins over a stale one still hooked ({t?.Assembly.GetName().Name ?? "none"})");
        Call(b, "Unhook");
        check(Game(Model) == null, "toolbar types: the newest unhooked (its project unloaded) - the stale copy does not answer for it");
        Call(a, "Unhook"); Call(a, "Unhook");
        check(Game(Model) == null, "toolbar types: all unhooked (twice: harmless) - nothing answers");
        // (the normal world switch: A hooked, unloaded, then C)
        var c = Copy(src, "AeroModCopyC");
        Call(c, "Hook"); t = Game(Model);
        check(t != null && t.Assembly == c, "toolbar types: the next world's copy answers after the old one unhooked");
        Call(c, "Unhook");

        // the wiring
        string inj = File.ReadAllText(Path.Combine(sim, "Injection", "EntityInjector.cs"));
        check(Regex.IsMatch(inj, @"ScriptResultsCleanup\.Ensure\(\);[^\n]*\n(\s*//[^\n]*\n)*\s*if \(ToolbarTypeResolve\.Hook\(\)\) ScriptResultsCleanup\.OwnProjectUnloading \+= ToolbarTypeResolve\.Unhook;"),
            "EntityInjector.PostProcess: hooks the toolbar types on every load and unhooks them with the world-switch fix");
        int models = 0; bool allOurs = true;
        foreach (var f in Directory.GetFiles(sim, "*.cs", SearchOption.AllDirectories))
        {
            string code = File.ReadAllText(f);
            if (!Regex.IsMatch(code, @"class \w+\s*:\s*BlockDetailModel\b")) continue;
            models++;
            if (!Regex.IsMatch(code, @"^namespace AeroMod;", RegexOptions.Multiline)) { allOurs = false; Console.WriteLine("  (not in AeroMod: " + f + ")"); }
        }
        check(models >= 6 && allOurs, $"toolbar types: every panel model ({models}) is in the namespace answered ({"AeroMod."})");

        // THE OPEN PANEL TOLD (2026-10-21): the Autopilot panel re-reads its fields only when told; the block's step tells it when the
        // Enemy switch is done (or refused) and when it writes a mode or height back - on the UI thread, never from the step itself
        string comp = File.ReadAllText(Path.Combine(sim, "Autopilot", "Game", "AeroAutopilotComponent.cs"));
        string model = File.ReadAllText(Path.Combine(sim, "Autopilot", "Game", "AeroAutopilotBlockDetailModel.cs"));
        check(Regex.IsMatch(comp, @"wrote = _enemy\.Step\([^;]*;\s*\}[^
]*
(\s*//[^
]*
)*\s*if \(_enemy\.GridIsEnemy != isEnemy \|\| _enemy\.Text\(\) != said\) PanelRefresh\(\);"),
            "autopilot panel: told when the Enemy switch is done or its words change (EnemyStep)");
        check(Regex.IsMatch(comp, @"if \(o\.ModeChanged \|\| o\.HeightChanged \|\| o\.HoldsChanged\) PanelRefresh\(\);"), "autopilot panel: told when the step writes a mode or a height back (WriteBack)");
        check(Regex.IsMatch(comp, @"void PanelRefresh\(\)[\s\S]{0,200}AvaloniaReflect\.Post\(m\.Refresh\)"), "autopilot panel: told on the UI thread (AvaloniaReflect.Post)");
        check(model.Contains("DetailModelPairing.Pair(this, _ap); _ap?.AttachModel(this);") && Regex.IsMatch(model, @"internal void Refresh\(\) \{ try \{ Changed\(\); \} catch \{ \} \}"),
            "autopilot panel: the model attaches itself as it is built, its Refresh notifies every shown field");
        // (and the block's step hands the WHOLE write-back to the panel - the holds a pilot mode caught included; 2026-10-21 only a mode or
        //  height change was passed on, and Cruise flew the panel's stale ALT)
        string grid = File.ReadAllText(Path.Combine(sim, "Autopilot", "Game", "AeroGridComponent.Autopilot.cs"));
        check(Regex.Matches(grid, @"(?m)^\s*ctl\.WriteBack\(in _apOut\);").Count == 2 && !Regex.IsMatch(grid, @"if \([^)]*_apOut\.[^)]*\) ctl\.WriteBack\(in _apOut\)"),
            "autopilot block: the link's whole write-back (holds, home, slot state too) reaches the panel, before and after the step");
    }
}
