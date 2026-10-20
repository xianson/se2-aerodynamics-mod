using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.Library.Memory;
using Keen.VRage.Library.Reflection;
using Keen.VRage.Library.Serialization;
using Keen.VRage.Library.Serialization.Binary;
using Keen.VRage.Library.Serialization.Binary.Archive;
using Keen.VRage.Library.Utils;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.Serialization;
using Aero.Core.Aero;
using Aero.Core.Atmosphere;
using Aero.Core.Blueprint;
using Aero.Core.Grids;
using Aero.Core.Sim;
using VrbReader;
using Block = Aero.Core.Grids.Block;
using Grid = Aero.Core.Grids.Grid;
using Vector3I = Aero.Core.Math.Vector3I;

string SE2Dir = GameAssemblies.GameDir;
string GameDataDir = Path.Combine(Path.GetDirectoryName(SE2Dir.TrimEnd('\\', '/'))!, "GameData", "Vanilla", "Content");

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: VrbReader <blueprint-dir-or-vrb> [--json] [--sim]");
    Console.Error.WriteLine("       VrbReader --occupancy [contentcache.vrb]");
    Console.Error.WriteLine("  --json       Output block data as JSON to stdout");
    Console.Error.WriteLine("  --sim        Run flight sim and generate HTML report+playback");
    Console.Error.WriteLine("  --occupancy  Every block model's occupied cells (the game's content cache) as JSON to stdout");
    return 1;
}
bool occupancyMode = args[0] == "--occupancy";
if (occupancyMode)
    args = new[] { args.Length > 1 ? args[1] : Path.Combine(GameDataDir, "contentcache.vrb") };

string path = args[0];
bool outputJson = args.Contains("--json");
bool runSim = args.Contains("--sim");
string vrbPath;
string? blueprintDir = null;

if (Directory.Exists(path))
{
    blueprintDir = path;
    vrbPath = Path.Combine(path, "grid.json.vrb");
    if (!File.Exists(vrbPath)) { Console.Error.WriteLine($"No grid.json.vrb in {path}"); return 1; }
}
else if (File.Exists(path))
    vrbPath = path;
else
{ Console.Error.WriteLine($"Not found: {path}"); return 1; }

var sw = Stopwatch.StartNew();
Console.Error.WriteLine($"Reading: {vrbPath} ({new FileInfo(vrbPath).Length:N0} bytes)");

try
{
    // ══════════════════════════════════════════════════════════════════
    //  BOOTSTRAP VRAGE RUNTIME
    // ══════════════════════════════════════════════════════════════════
    if (!BumpAllocator.Instance.Initialized)
        BumpAllocator.Instance.Initialize();

    var assemblies = new[] {
        "VRage.Library.dll", "VRage.Core.dll", "VRage.Core.Game.dll",
        "VRage.DCS.dll", "VRage.Game.dll", "Game2.Simulation.dll", "VRage.Physics.dll",
        "VRage.Scripting.dll"
    }.Select(d => Path.Combine(SE2Dir, d)).Where(File.Exists).Select(Assembly.LoadFrom).ToList();
    if (occupancyMode)
    {
        // (the content cache holds every asset kind's blobs - animation, render, audio...: their types must resolve. Not
        //  the whole folder: the editor / content pipeline need assemblies the game does not ship, WinForms, ...)
        foreach (var d in OccupancyAssemblies)
        {
            var p = Path.Combine(SE2Dir, d);
            if (File.Exists(p)) assemblies.Add(Assembly.LoadFrom(p));
        }
    }
    MetadataManager.Instance.PushContext(assemblies);
    // (the game's file system, bare: a toolbar tile's icon is a ResourceHandle, registered in the content cache as it is
    //  read - without one every blueprint with a toolbar failed. Only the cache: nothing is read from disk)
    if (!Keen.VRage.Library.Filesystem.FileSystem.HasValue)
    {
        // (virtual roots: nothing on disk is mounted or written)
        Keen.VRage.Library.Filesystem.FileSystem.Init(forceSynchronousIO: true);
        var fsi = Keen.VRage.Library.Utils.Singleton<Keen.VRage.Library.Filesystem.FileSystem>.Instance;
        fsi.InitContent(new Keen.VRage.Library.Filesystem.RootPathSetup());
        fsi.InitAppData(new Keen.VRage.Library.Filesystem.RootPathSetup());
        fsi.InitTemp(new Keen.VRage.Library.Filesystem.RootPathSetup(), skipInitAppDataTemp: true);
    }

    if (occupancyMode) return Occupancy.Dump(vrbPath);

    // ══════════════════════════════════════════════════════════════════
    //  DESERIALIZE VRB
    // ══════════════════════════════════════════════════════════════════
    using var fs = File.OpenRead(vrbPath);
    var hdr = BinaryArchiveParser.TryReadHeader(fs, out _);
    if (hdr == null) { Console.Error.WriteLine("Invalid VRB"); return 1; }
    var header = hdr.Value;

    fs.Position = 0;
    var builder = ServiceLocatorContext.Builder.Create();
    builder.Add(new BinarySerializationParameters { MainChunkCompression = header.MainChunk.CompressionType });
    builder.Add(new EntityProxySerializationContext());
    builder.Add(new StubDefinitionContext());
    // the game's data migrations for a blueprint saved by an older game (EntityContainerDeserializer adds them; without, an
    // older field layout misread - the Diamondback's toolbar). VRB_VERSION picks the version to migrate from.
    {
        var mv = Environment.GetEnvironmentVariable("VRB_VERSION");
        if (!string.IsNullOrEmpty(mv)) builder.Add(Keen.VRage.Library.Serialization.Migrations.CompositeMigrationsContext.GetDataMigrations(Version.Parse(mv)));
    }

    // (as the game: a member it cannot read is skipped and reported, not fatal - the Diamondback's toolbar icons)
    builder = Keen.VRage.Library.Serialization.Validation.ErrorRecovery.DeferredErrorHandlingSerializationContext.AddContexts(builder);
    using var ctx = new SerializationContext(fs, "grid.json.vrb", builder);
    // Skip unknown properties from older blueprint versions (game patches remove fields)
    ctx.UnknownPropertyHandler = (context, type, name) => { };
    // (as the game reads it - EntityContainerDeserializer.TryReadFile: SerializationHelper.DeserializeAny; the hand-driven
    //  parser misread the Diamondback's toolbars. VRB_PARSER=1: the old way)
    EntityBundle entityBundle;
    if (Environment.GetEnvironmentVariable("VRB_PARSER") == "1")
    {
        var parser = new BinaryArchiveParser(ctx);
        var h2 = parser.ReadHeader();
        var bundles = new List<BundleInfo>();
        parser.ReadBundleTable(in h2, bundles);
        var typeModel = parser.ReadTypeModel(in h2);
        entityBundle = parser.DeserializeMainChunk<EntityBundle>(typeModel, in h2);
    }
    else entityBundle = SerializationHelper.DeserializeAny<EntityBundle>(ctx, out _)!;

    Console.Error.WriteLine($"Deserialized in {sw.Elapsed.TotalSeconds:F1}s: {entityBundle.Builders.Count} entities");

    // ══════════════════════════════════════════════════════════════════
    //  EXTRACT BLOCKS FROM HIERARCHY
    // ══════════════════════════════════════════════════════════════════
    var gridOb = entityBundle.Builders[0];
    if (gridOb == null) { Console.Error.WriteLine("No root entity"); return 1; }

    HierarchyComponentObjectBuilder? hierarchy = null;
    string? gridName = null;
    for (int j = 0; j < gridOb.ObjectBuilders.Count; j++)
    {
        var val = gridOb.ObjectBuilders[j];
        if (val is HierarchyComponentObjectBuilder h) hierarchy = h;
        if (val is Keen.Game2.Simulation.WorldObjects.CubeGrids.CubeGridObjectBuilder cg)
            gridName = cg.DisplayName.ToString();
    }
    if (hierarchy == null) { Console.Error.WriteLine("No hierarchy"); return 1; }

    // Parse container-info for name
    string bpName = gridName ?? "Blueprint";
    if (blueprintDir != null)
    {
        var ciPath = Path.Combine(blueprintDir, ".container-info");
        if (File.Exists(ciPath))
        {
            try
            {
                var ciDoc = JsonDocument.Parse(File.ReadAllText(ciPath));
                var root = ciDoc.RootElement;
                if (root.TryGetProperty("$Value", out var v) && v.TryGetProperty("Meta", out var meta) &&
                    meta.TryGetProperty("UserInfo", out var ui) && ui.TryGetProperty("DisplayName", out var dn) &&
                    dn.TryGetProperty("RawText", out var raw))
                    bpName = raw.GetString() ?? bpName;
            }
            catch { }
        }
    }

    // Convert to BlueprintData block list
    var bpBlocks = new List<BlockEntity>();
    for (int i = 0; i < hierarchy.Children.Count; i++)
    {
        var blockOb = hierarchy.Children[i];
        if (blockOb == null) continue;

        var block = new BlockEntity
        {
            ObjectId = i,
            DefinitionId = blockOb.Definition?.Guid.ToString() ?? "",
            Orientation = QuaternionD.Identity
        };

        if (blockOb.ObjectBuilders != null)
        {
            for (int k = 0; k < blockOb.ObjectBuilders.Count; k++)
            {
                if (blockOb.ObjectBuilders[k] is ChildTransformComponentObjectBuilder ct)
                {
                    var t = ct.TransformWithEulerHint.Transform;
                    block.Position = new Vector3D(t.Position.X, t.Position.Y, t.Position.Z);
                    block.Orientation = new QuaternionD(
                        t.Orientation.X, t.Orientation.Y, t.Orientation.Z, t.Orientation.W);
                }
            }
        }
        bpBlocks.Add(block);
    }

    Console.Error.WriteLine($"Extracted: {bpBlocks.Count} blocks from \"{bpName}\"");

    // ══════════════════════════════════════════════════════════════════
    //  JSON OUTPUT
    // ══════════════════════════════════════════════════════════════════
    if (outputJson)
    {
        using var writer = new Utf8JsonWriter(Console.OpenStandardOutput(), new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WriteString("name", bpName);
        writer.WriteNumber("blockCount", bpBlocks.Count);
        writer.WriteStartArray("blocks");
        foreach (var b in bpBlocks)
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", b.ObjectId);
            writer.WriteString("def", b.DefinitionId);
            writer.WriteStartArray("pos");
            writer.WriteNumberValue(Math.Round(b.Position.X, 4));
            writer.WriteNumberValue(Math.Round(b.Position.Y, 4));
            writer.WriteNumberValue(Math.Round(b.Position.Z, 4));
            writer.WriteEndArray();
            writer.WriteStartArray("orient");
            writer.WriteNumberValue(Math.Round(b.Orientation.X, 6));
            writer.WriteNumberValue(Math.Round(b.Orientation.Y, 6));
            writer.WriteNumberValue(Math.Round(b.Orientation.Z, 6));
            writer.WriteNumberValue(Math.Round(b.Orientation.W, 6));
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        Console.Out.WriteLine();
    }

    // ══════════════════════════════════════════════════════════════════
    //  FLIGHT SIMULATION
    // ══════════════════════════════════════════════════════════════════
    if (runSim)
    {
        Console.Error.WriteLine("\n── Building Grid ──");
        sw.Restart();

        // Load block definition catalog for multi-cell block sizes
        var catalog = new BlockDefinitionCatalog();
        if (Directory.Exists(GameDataDir))
        {
            catalog.ScanGameData(GameDataDir);
            Console.Error.WriteLine($"Block catalog: {catalog.Count} definitions");
        }

        // Each SE2 block entity = one 0.25m cell.
        var grid = new Grid();
        float blockSize = 0.25f;
        int id = 0, skipped = 0;
        foreach (var bpBlock in bpBlocks)
        {
            int px = (int)Math.Round(bpBlock.Position.X / blockSize);
            int py = (int)Math.Round(bpBlock.Position.Y / blockSize);
            int pz = (int)Math.Round(bpBlock.Position.Z / blockSize);

            try { grid.AddBlock(new Block(id++, bpBlock.DefinitionId, new Vector3I(px, py, pz))); }
            catch (InvalidOperationException) { skipped++; }
        }
        Console.Error.WriteLine($"Grid: {grid.BlockCount} cells ({skipped} overlap) in {sw.Elapsed.TotalSeconds:F1}s");

        // Surface cache
        sw.Restart();
        var surface = new SmoothSurfaceProvider { SmoothingRings = 2, CreaseAngleDegrees = 180f, WeightExponent = 2f };
        surface.Build(grid, blockSize);
        Console.Error.WriteLine($"Surface: {surface.FaceCount} faces in {sw.Elapsed.TotalSeconds:F1}s");

        // Each cell = one 0.25m armor block (~0.5kg for light armor cell)
        float massPerCell = 0.5f;
        var massBase = MassProperties.FromGrid(grid, blockSize, massPerCell);
        // Ballast to bring CoM forward for stability
        var mass = massBase.WithBallast(massBase.TotalMass * 0.15f,
            new Vector3(massBase.CenterOfMass.X + 5f * blockSize, massBase.CenterOfMass.Y, massBase.CenterOfMass.Z));
        Console.Error.WriteLine($"Mass: {mass.TotalMass / 1000:F0}t CoM: ({mass.CenterOfMass.X:F1}, {mass.CenterOfMass.Y:F1}, {mass.CenterOfMass.Z:F1})");

        // Drag model with lift
        var drag = new DampedShadowedDragModel { CdBluff = 0.4f, CpBase = -0.05f, CfSkin = 0.003f, Streamlining = 1.0f };
        // Blocks are 2.5m apart on a 0.25m grid = 10 cells spacing.
        // Flood-fill needs to bridge that gap.
        var detector = new ConnectedComponentWingDetector { MaxAdjacencyDistance = 12 };
        Console.Error.WriteLine($"Wing detector: MinAR={detector.MinAspectRatio} MaxTC={detector.MaxThicknessRatio} MinFaces={detector.MinFaceCount}");
        var liftModel = new LiftingSurfaceModel(drag, detector);
        liftModel.DetectWings(grid, surface, blockSize);
        if (liftModel.Wings is { Length: > 0 })
            drag.ShadowMap.WingCells = LiftingSurface.CollectWingCells(liftModel.Wings);
        Console.Error.WriteLine($"Wings: {liftModel.Wings?.Length ?? 0}");
        if (liftModel.Wings != null)
            foreach (var w in liftModel.Wings)
                Console.Error.WriteLine($"  span={w.Span:F1}m area={w.PlanformArea:F1}m² AR={w.AspectRatio:F2} t/c={w.ThicknessRatio:F2}");

        // Atmosphere
        var atmo = AtmosphereModel.Earth();

        // ── Run flight sim: glide from 5km at 250 m/s ──
        Console.Error.WriteLine("\n── Running flight sim: 60s glide, 250 m/s, 5km alt ──");
        sw.Restart();

        float initAoA = 3f * MathF.PI / 180f;
        var sim = new FlightSim(grid, surface, liftModel, atmo, mass, blockSize)
        {
            Gravity = 9.81f,
            StopAtGround = true,
            RecordInterval = 10, // every 0.1s at dt=0.01
        };
        sim.State = new RigidBodyState
        {
            Position = new Vector3(0, 5000, 0),
            LinearVelocity = new Vector3(-250, 0, 0),
            Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -initAoA),
            AngularVelocity = Vector3.Zero,
        };

        var history = sim.Run(60.0, 0.01);
        Console.Error.WriteLine($"Sim completed in {sw.Elapsed.TotalSeconds:F1}s: {history.Count} records");

        var last = history[^1];
        Console.Error.WriteLine($"Final: t={last.Time:F1}s alt={last.Altitude:F0}m v={last.State.Speed:F1}m/s M={last.Mach:F2}");

        // ── Generate HTML outputs ──
        string safeName = new string(bpName.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray());
        if (string.IsNullOrEmpty(safeName)) safeName = "blueprint";

        string reportPath = Path.Combine(Directory.GetCurrentDirectory(), $"flight_{safeName}.html");
        string playbackPath = Path.Combine(Directory.GetCurrentDirectory(), $"playback_{safeName}.html");

        FlightReport.Generate(history, reportPath, $"{bpName} — Glide Sim");
        Console.Error.WriteLine($"Report: {reportPath}");

        FlightPlayback.Generate(grid, surface, history, playbackPath, blockSize, $"{bpName} — Playback");
        Console.Error.WriteLine($"Playback: {playbackPath}");
    }

    if (!outputJson && !runSim)
    {
        // Default: just print summary
        var defCounts = bpBlocks.GroupBy(b => b.DefinitionId).OrderByDescending(g => g.Count()).ToList();
        Console.Error.WriteLine($"Unique definitions: {defCounts.Count}");
        foreach (var g in defCounts.Take(10))
            Console.Error.WriteLine($"  {g.Key}: {g.Count()} blocks");
        if (defCounts.Count > 10)
            Console.Error.WriteLine($"  ... +{defCounts.Count - 10} more");
    }

    Console.Error.WriteLine("Done!");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"\nError: {ex.GetType().Name}: {ex.Message}");
    Console.Error.WriteLine(ex.StackTrace);
    var inner = ex.InnerException;
    while (inner != null)
    {
        Console.Error.WriteLine($"\nCaused by: {inner.GetType().Name}: {inner.Message}");
        Console.Error.WriteLine(inner.StackTrace);
        inner = inner.InnerException;
    }
    return 1;
}

static partial class Program
{
    /// <summary>--occupancy: the assemblies the content cache's blob types live in (beyond the blueprint reader's).</summary>
    static readonly string[] OccupancyAssemblies = {
        "VRage.Animation.dll", "VRage.Animation.Client.dll", "VRage.Render.dll", "VRage.Audio.dll", "VRage.Client.dll",
        "VRage.Game.Client.dll", "VRage.Physics.Client.dll", "VRage.UI.dll", "VRage.UI.Shared.dll", "VRage.Voxels.dll",
        "VRage.Multiplayer.dll", "VRage.AI.dll", "Game2.Game.dll", "Game2.Client.dll",
    };
}
