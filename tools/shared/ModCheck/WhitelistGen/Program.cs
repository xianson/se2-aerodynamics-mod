// Rebuilds the SE2 MOD script whitelist with the game's own code and serializes it to
// AllowedSymbols.Mod.txt for Keen's WhitelistDiagnosticAnalyzer (VRS1001) to consume offline.
//
// Nothing is re-implemented: the statics are set exactly as GameApp.AddScripting (4.0.0.3151) sets
// them, the reference set is the real ModCompilationDescriptor's, and the namespaces come from the
// real GameWhitelistProvider<ModWhitelistProvider>.ConfigureWhitelist - the same calls
// AnalyzerDescriptorBuilderExtensions.AddWhitelistAnalyzer makes in game.
//
// After a game update: diff GameApp.AddScripting against the two arrays below.
//
// Usage: dotnet run --project tools/ModCheck/WhitelistGen -- "<Game2 dir>" <out AllowedSymbols.Mod.txt>
using System.Collections.Immutable;
using System.Reflection;
using Keen.Game2.Client.Input;
using Keen.Game2.Simulation;
using Keen.Game2.Simulation.Scripting;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Render;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Utils;
using Keen.VRage.Multiplayer;
using Keen.VRage.Physics;
using Keen.VRage.Scripting.Whitelisting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

if (args.Length < 2) { Console.Error.WriteLine("usage: WhitelistGen <Game2 dir> <out file>"); return 2; }
string game2 = args[0], outPath = args[^1];
AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
{
    var p = Path.Combine(game2, new AssemblyName(e.Name).Name + ".dll");
    return File.Exists(p) ? Assembly.LoadFrom(p) : null;
};
return Run(outPath);

static int Run(string outPath)
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

    Console.WriteLine("whitelist seed assemblies:");
    foreach (var t in GameWhitelistProvider<ModWhitelistProvider>.AllowedAssemblies.Select(t => t.Assembly).Distinct())
        Console.WriteLine($"  {t.GetName().Name}");

    var descriptor = new ModCompilationDescriptor();
    // ModCheck.csproj must reference exactly these (printed so a game update shows the drift).
    Console.WriteLine("compile references (ModCompilationDescriptor.MetadataReferences):");
    foreach (var r in descriptor.MetadataReferences)
        Console.WriteLine($"  {Path.GetFileName(r.Display)}");

    var comp = CSharpCompilation.Create("wl", references: descriptor.MetadataReferences, options: descriptor.CompilationOptions);
    var builder = ScriptWhitelist.CreateBuilder(comp);
    ((IScriptWhitelistProvider)Singleton<ModWhitelistProvider>.Instance).ConfigureWhitelist(builder);
    var wl = builder.Build();
    using (var fs = File.Create(outPath)) wl.Serialize(fs);
    Console.WriteLine($"wrote {outPath} ({new FileInfo(outPath).Length} bytes)");
    return 0;
}
