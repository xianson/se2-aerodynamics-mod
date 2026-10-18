// modlist <world save dir> [--drop <project guid>]... [--add <project guid> <name>]... - print a world's mod list; with
// --drop / --add, write it again without / with those local mods (2026-10-17, the Blocks mod folded into the Aerodynamics Mod: a world that still lists a local mod the
// game cannot find does not load at all - LocalProjectLocator throws "Couldn't find the project", decompiled
// VRage.Core LocalProjectLocator.cs:204).
// The game reads the list from "modlist", then "modlist.vrb", then "modlist.bak.json" (EntityContainerAccessors
// .TryFindFileAsync), binary or JSON alike (SerializationHelper.DeserializeAny). So --drop never touches modlist.vrb:
// it writes the edited list as JSON to the extension-less "modlist", which wins; the game's next save deletes that
// file and writes modlist.vrb (FileContainerAdapter.OpenWriteStream). Run it on a COPY of a world, with the game closed.
// modlist --defs <mod Content dir>    every definition in its definitionsets.vrb: "set guid partialKind type" per line
// (a compressed binary archive - not greppable; read as the game does: ProjectBasedDefinitionSetManagerComponent
//  .TryLoadDefinitionSetCollection. contentcache.vrb needs the game's FileSystem singleton to read - not done here)
using Keen.VRage.Core.Worlds;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Serialization.Binary.Archive;
using Keen.VRage.Library.Utils;
using Keen.VRage.Library.Memory;
using Keen.VRage.Library.Reflection;
using Keen.VRage.Library.Serialization;
using System.Reflection;

const string SE2Dir = @"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers2\Game2";
if (args.Length < 1) { Console.Error.WriteLine("usage: modlist <world save dir> [--drop <project guid>]... | --defs <Content dir>"); return 2; }
if (!BumpAllocator.Instance.Initialized) BumpAllocator.Instance.Initialize();
MetadataManager.Instance.PushContext(new[] { "VRage.Library.dll", "VRage.Core.dll", "VRage.Core.Game.dll", "VRage.DCS.dll", "VRage.Game.dll",
        "VRage.Physics.dll", "VRage.Scripting.dll", "VRage.Render.dll", "VRage.Audio.dll", "Game2.Simulation.dll", "Game2.Game.dll" }
    .Select(d => Path.Combine(SE2Dir, d)).Where(File.Exists).Select(Assembly.LoadFrom).ToList());
if (args[0] == "--defs" && args.Length == 2)
{
    using var fs = File.OpenRead(Path.Combine(args[1], "definitionsets.vrb"));
    using var ctx = new SerializationContext(fs, "definitionsets.vrb");
    var header = BinaryArchiveParser.TryReadHeader(fs, out var why) ?? throw new Exception("definitionsets.vrb: " + why);
    fs.Position = 0;
    var parser = new BinaryArchiveParser(ctx);
    ctx.Bundles = new SortedSet<BundleInfo>();
    parser.ReadBundleTable(in header, ctx.Bundles);
    var sets = parser.DeserializeMainChunk<DefinitionSetCollection>(parser.ReadTypeModel(in header), in header);
    foreach (var (set, defs) in sets.DefinitionSets)
        foreach (var (guid, d) in defs.Definitions) Console.WriteLine($"{set} {guid} {d.PartialKind} {d.Type?.FullName}");
    return 0;
}
string world = args[0];
var drop = new HashSet<Guid>();
var add = new List<(Guid guid, string name)>();
for (int i = 1; i < args.Length; i++)
    if (args[i] == "--drop" && i + 1 < args.Length) drop.Add(Guid.Parse(args[++i]));
    else if (args[i] == "--add" && i + 2 < args.Length) { add.Add((Guid.Parse(args[i + 1]), args[i + 2])); i += 2; }
    else { Console.Error.WriteLine("unknown argument " + args[i]); return 2; }

string? file = new[] { "modlist", "modlist.vrb", "modlist.bak.json" }.Select(n => Path.Combine(world, n)).FirstOrDefault(File.Exists);
if (file == null && add.Count == 0) { Console.WriteLine("no mod list (the world loads no mods)"); return drop.Count == 0 ? 0 : 1; }
ModList Read(string path)
{
    using var fs = File.OpenRead(path);
    using var ctx = new SerializationContext(fs, Path.GetFileName(path));
    return SerializationHelper.DeserializeAny<ModList>(ctx);
}
void Print(ModList l)
{
    foreach (var r in l.Records ?? Array.Empty<ModList.ModRecord>())
        Console.WriteLine($"  {r.Project.Guid}  service \"{r.Project.ServiceId}\"  version {r.Version?.ToString() ?? "-"}  {r.DisplayName}");
}
var list = file != null ? Read(file) : new ModList { Records = Array.Empty<ModList.ModRecord>() };
Console.WriteLine($"{(file != null ? Path.GetFileName(file) : "(no mod list)")}: {list.Records?.Length ?? 0} mod(s)");
Print(list);
if (drop.Count == 0 && add.Count == 0) return 0;

var keep = (list.Records ?? Array.Empty<ModList.ModRecord>()).Where(r => !drop.Contains(r.Project.Guid)).ToList();
foreach (var (g, n) in add)   // (a local mod: ServiceId "", no ModId - LocalProjectLocator matches all three)
    if (!keep.Any(r => r.Project.Guid == g)) keep.Add(new ModList.ModRecord { Project = new Keen.VRage.Core.Project.VRageProjectID { Guid = g, ServiceId = "" }, DisplayName = n });
if (keep.Count == (list.Records?.Length ?? 0) && add.Count == 0) { Console.WriteLine("nothing to drop: the list is unchanged"); return 0; }
var edited = new ModList { Records = keep.ToArray() };
string outPath = Path.Combine(world, "modlist");
using (var fs = File.Create(outPath))
using (var ctx = new SerializationContext(fs, "modlist"))
    SerializationHelper.Serialize(ctx, in edited, SerializerFormat.Json);
var back = Read(outPath);   // (read back as the game will: the extension-less file comes first)
Console.WriteLine($"wrote {outPath}: {back.Records?.Length ?? 0} mod(s)");
Print(back);
return back.Records?.Length == keep.Count ? 0 : 1;
