using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace VrbReader;

/// <summary>
/// The game's assemblies come from the installed game, never from copies next to this exe (the build does not copy
/// them: Private=false). Registered as a module initializer, i.e. before Main touches a single game type.
/// Game folder: env SE2_GAME_DIR, else the SE2Dir the build was made with (VrbReader.csproj).
/// </summary>
static class GameAssemblies
{
    public static readonly string GameDir = Environment.GetEnvironmentVariable("SE2_GAME_DIR") is { Length: > 0 } env ? env
        : typeof(GameAssemblies).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "SE2Dir")?.Value
          ?? @"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers2\Game2";

    [ModuleInitializer]
    internal static void Init()
    {
        if (!Directory.Exists(GameDir))
            Console.Error.WriteLine($"VrbReader: no game at {GameDir} (set SE2_GAME_DIR or build with -p:SE2Dir=...)");
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            var p = Path.Combine(GameDir, name.Name + ".dll");
            return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
        };
    }
}
