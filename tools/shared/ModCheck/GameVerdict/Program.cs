// GameVerdict: the game's own verdict on a mod's scripts, offline, without launching the game.
//
// Replays the in-game mod compile (SE2 4.0.0.3151) with the game's code: GameApp.AddScripting statics ->
// the real ModCompilationDescriptor (references, parse/compile options incl. reportSuppressedDiagnostics,
// whitelist built by the real GameWhitelistProvider, Keen's analyzers + generators incl. the KCG KN* set)
// -> the body of ScriptCompiler.CompileAsync (copied; only the parallel scheduling is left out) -> the real
// DiagnosticBag.HasErrors, which counts Error diagnostics even when a #pragma suppressed them.
//
// Usage: GameVerdict <mod dir> [bundle name] [--all] [--dump-whitelist=<file>]
//   exit 0 = the game compiles the mod, 1 = the game rejects it (ScriptLoadingException), 2 = usage.
//   --all also lists every warning (suppressed ones included).
//   --dump-whitelist writes the AllowedSymbols text the game fed its analyzer (must equal AllowedSymbols.Mod.txt).
//   --dump-generated=<dir> writes the sources Keen's generators add to the mod (what they make of its types).
//   --analyzers lists every analyzer the game attaches, with its diagnostics' ids and default severities.
// After a game update: diff GameApp.AddScripting and ScriptCompiler.CompileAsync against Run below.
using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Keen.Game2.Client.Input;
using Keen.Game2.Simulation;
using Keen.Game2.Simulation.Scripting;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Render;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Reflection;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer;
using Keen.VRage.Physics;
using Keen.VRage.Scripting;
using Keen.VRage.Scripting.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

const string Game2Dir = @"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers2\Game2";
string game2 = Environment.GetEnvironmentVariable("SE2_GAME2") ?? Game2Dir;
AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
{
    var p = Path.Combine(game2, new AssemblyName(e.Name).Name + ".dll");
    return File.Exists(p) ? Assembly.LoadFrom(p) : null;
};
var pos = args.Where(a => !a.StartsWith("--")).ToArray();
if (pos.Length < 1) { Console.Error.WriteLine("usage: GameVerdict <mod dir> [bundle name] [--all]"); return 2; }
string dump = args.FirstOrDefault(a => a.StartsWith("--dump-whitelist="))?.Substring("--dump-whitelist=".Length);
string dumpGen = args.FirstOrDefault(a => a.StartsWith("--dump-generated="))?.Substring("--dump-generated=".Length);
return Run(pos[0], pos.Length > 1 ? pos[1] : "ModCheckBundle", args.Contains("--all"), dump, dumpGen, args.Contains("--analyzers"));

static int Run(string modDir, string bundle, bool all, string dumpWhitelist, string dumpGenerated, bool listAnalyzers)
{
    // GameApp.AddScripting (4.0.0.3151), verbatim.
    GameWhitelistProvider<ModWhitelistProvider>.AllowedAssemblies = new Type[11]
    {
        typeof(int), typeof(Enumerable), typeof(ImmutableArray), typeof(MeshBuilder), typeof(StringId),
        typeof(Entity), typeof(Blob), typeof(UpdateTime), typeof(InputGameComponent), typeof(IPhysics),
        typeof(ClientId)
    };
    GameCompilationDescriptor.MetaDatas = new Type[11]
    {
        typeof(MeshBuilder), typeof(StringId), typeof(Entity), typeof(Blob), typeof(UpdateTime),
        typeof(InputGameComponent), typeof(IPhysics), typeof(ClientId), typeof(System.Text.Json.JsonTokenType),
        typeof(System.Collections.Concurrent.ConcurrentDictionary<,>), typeof(System.Dynamic.DynamicObject)
    };

    var descriptor = new ModCompilationDescriptor();
    descriptor.SetAssemblyBundle(new BundleInfo(bundle, new Version(1, 0, 0, 0)));

    // The game compiles a mod's Scripts/Simulation as <bundle>.Simulation.
    string scriptsDir = Path.Combine(modDir, "Scripts", "Simulation");
    if (!Directory.Exists(scriptsDir)) { Console.Error.WriteLine($"no {scriptsDir}"); return 2; }
    var scripts = Directory.EnumerateFiles(scriptsDir, "*.cs", SearchOption.AllDirectories)
        .Select(f => new Script(Path.GetRelativePath(Path.GetDirectoryName(scriptsDir)!, f), File.ReadAllText(f))).ToList();

    // ScriptCompiler.CompileAsync body.
    IEnumerable<CSharpSyntaxTree> trees = scripts.Select(sc => (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(sc.SourceText, descriptor.ParseOptions, sc.HintName, Encoding.UTF32));
    foreach (var pre in descriptor.PreProcessors) trees = pre.Process(descriptor, trees);
    var compilation = CSharpCompilation.Create(bundle + ".Simulation", trees.ToImmutableArray(), descriptor.MetadataReferences, descriptor.CompilationOptions);
    var analyzers = descriptor.GetAnalyzers(compilation);
    if (listAnalyzers)
        foreach (var an in analyzers.Analyzers.OrderBy(x => x.GetType().Assembly.GetName().Name).ThenBy(x => x.GetType().Name))
            Console.WriteLine($"analyzer {an.GetType().Assembly.GetName().Name} {an.GetType().Name}: " +
                string.Join(" ", an.SupportedDiagnostics.Select(d => $"{d.Id}:{d.DefaultSeverity}{(d.IsEnabledByDefault ? "" : "(off)")}")));
    if (dumpWhitelist != null)
        File.WriteAllText(dumpWhitelist, analyzers.AnalyzerOptions.AdditionalFiles
            .Single(f => Path.GetFileName(f.Path).StartsWith("AllowedSymbols")).GetText()!.ToString());
    var driver = CSharpGeneratorDriver.Create(analyzers.SourceGenerators, analyzers.AnalyzerOptions?.AdditionalFiles, descriptor.ParseOptions, analyzers.AnalyzerOptions?.AnalyzerConfigOptionsProvider);
    GeneratorDriver ran = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outComp, out var genDiags);
    compilation = (CSharpCompilation)outComp;
    if (dumpGenerated != null)
        foreach (var gen in ran.GetRunResult().Results.SelectMany(r => r.GeneratedSources))
        {
            Directory.CreateDirectory(dumpGenerated);
            File.WriteAllText(Path.Combine(dumpGenerated, gen.HintName), gen.SourceText.ToString());
        }
    var cwa = compilation.WithAnalyzers(analyzers.Analyzers, new CompilationWithAnalyzersOptions(analyzers.AnalyzerOptions, null, true, false, compilation.Options.ReportSuppressedDiagnostics, null));
    var diags = cwa.GetAllDiagnosticsAsync().GetAwaiter().GetResult();
    var bag = new DiagnosticBag();
    bag.AddRange(diags);
    bag.AddRange(genDiags);

    var list = bag.GetDiagnostics().ToList();
    static string Fmt(Diagnostic d)
    {
        var l = d.Location.GetLineSpan();
        string where = l.Path is { Length: > 0 } ? $"{l.Path}({l.StartLinePosition.Line + 1},{l.StartLinePosition.Character + 1}): " : "";
        return $"{where}{d.Severity.ToString().ToLowerInvariant()} {d.Id}{(d.IsSuppressed ? " [pragma-suppressed]" : "")}: {d.GetMessage()}";
    }
    var errors = list.Where(d => d.Severity == DiagnosticSeverity.Error).Select(Fmt).Distinct().ToList();
    foreach (var e in errors) Console.WriteLine(e);
    if (all)
        foreach (var w in list.Where(d => d.Severity == DiagnosticSeverity.Warning).Select(Fmt).Distinct()) Console.WriteLine(w);
    Console.WriteLine($"GameVerdict: {scripts.Count} files, {analyzers.Analyzers.Length} analyzers, {analyzers.SourceGenerators.Length} generators ({string.Join(", ", analyzers.SourceGenerators.Select(g => g.GetType().Name))}); {errors.Count} error(s); summary by id:");
    foreach (var g in list.Where(d => d.Severity >= DiagnosticSeverity.Warning)
                 .GroupBy(d => (d.Id, d.Severity, d.IsSuppressed)).OrderByDescending(g => g.Key.Severity).ThenBy(g => g.Key.Id))
        Console.WriteLine($"  {g.Key.Severity,-8} {g.Key.Id,-8} {(g.Key.IsSuppressed ? "suppressed" : ""),-10} x{g.Count()}");
    Console.WriteLine(bag.HasErrors
        ? "GameVerdict: FAIL - the game rejects this mod (ScriptLoadingException)"
        : "GameVerdict: PASS - the game compiles this mod");
    return bag.HasErrors ? 1 : 0;
}
