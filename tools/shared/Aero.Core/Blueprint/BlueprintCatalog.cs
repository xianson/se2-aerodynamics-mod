namespace Aero.Core.Blueprint;

/// <summary>
/// Known blueprint paths for testing and analysis.
/// All paths point to SE2 grid JSON files on this machine.
///
/// Usage:
///   var bp = BlueprintParser.ParseFile(BlueprintCatalog.Grasshopper.Path);
///   // or iterate all:
///   foreach (var entry in BlueprintCatalog.All)
///       if (entry.Exists) { ... }
/// </summary>
public static class BlueprintCatalog
{
    // ─── Local blueprints ───────────────────────────────────────────

    /// <summary>
    /// Grasshopper — user's custom small ship. ~156 blocks.
    /// </summary>
    public static readonly BlueprintEntry Grasshopper = new(
        name: "Grasshopper",
        path: @"C:\Users\slob\AppData\Roaming\SpaceEngineers2\AppData\Blueprints\Grasshopper\grid.json.bak.json",
        source: BlueprintSource.Local,
        estimatedBlocks: 156,
        description: "Small ship, user-built");

    // ─── Workshop blueprints ────────────────────────────────────────

    /// <summary>
    /// Workshop 3442307782 — large ship, ~67K blocks.
    /// </summary>
    public static readonly BlueprintEntry Workshop_3442307782 = new(
        name: "Workshop Large A",
        path: @"C:\Program Files (x86)\Steam\steamapps\workshop\content\1133870\3442307782\grid.json",
        source: BlueprintSource.Workshop,
        workshopId: 3442307782,
        estimatedBlocks: 67_000,
        description: "Large ship with named block groups (Bridge Lighting, Cap Rm Lighting)");

    /// <summary>
    /// Workshop 3593395872 — massive ship, ~383K blocks. 1.06 GB JSON.
    /// Warning: parsing this will take significant memory and time.
    /// </summary>
    public static readonly BlueprintEntry Workshop_3593395872 = new(
        name: "Workshop Massive (Cargo Transport)",
        path: @"C:\Program Files (x86)\Steam\steamapps\workshop\content\1133870\3593395872\grid.json",
        source: BlueprintSource.Workshop,
        workshopId: 3593395872,
        estimatedBlocks: 383_000,
        description: "Massive cargo transport ship. Named groups: 'cargo transport', 'main cargo'. WARNING: ~1GB JSON");

    /// <summary>
    /// Workshop 3599670638 — large ship, ~69K blocks.
    /// </summary>
    public static readonly BlueprintEntry Workshop_3599670638 = new(
        name: "Workshop Large B",
        path: @"C:\Program Files (x86)\Steam\steamapps\workshop\content\1133870\3599670638\grid.json",
        source: BlueprintSource.Workshop,
        workshopId: 3599670638,
        estimatedBlocks: 69_000,
        description: "Large ship with multiple block groups");

    /// <summary>
    /// Workshop 3625214497 — binary-only (.vrb), NOT parseable with current parser.
    /// </summary>
    public static readonly BlueprintEntry Workshop_3625214497 = new(
        name: "Workshop Binary Only",
        path: @"C:\Program Files (x86)\Steam\steamapps\workshop\content\1133870\3625214497\grid.json.vrb",
        source: BlueprintSource.Workshop,
        workshopId: 3625214497,
        estimatedBlocks: 0,
        description: "Binary-only blueprint (.vrb) — needs VRB parser");

    // ─── Collections ────────────────────────────────────────────────

    /// <summary>All known blueprints (including binary-only).</summary>
    public static IReadOnlyList<BlueprintEntry> All { get; } = new[]
    {
        Grasshopper,
        Workshop_3442307782,
        Workshop_3593395872,
        Workshop_3599670638,
        Workshop_3625214497,
    };

    /// <summary>Blueprints that have parseable JSON (excludes binary-only).</summary>
    public static IReadOnlyList<BlueprintEntry> Parseable { get; } = new[]
    {
        Grasshopper,
        Workshop_3442307782,
        Workshop_3593395872,
        Workshop_3599670638,
    };

    /// <summary>Small blueprints suitable for unit tests (&lt;1000 blocks).</summary>
    public static IReadOnlyList<BlueprintEntry> Small { get; } = new[]
    {
        Grasshopper,
    };

    /// <summary>Large blueprints suitable for benchmarks (&gt;10K blocks).</summary>
    public static IReadOnlyList<BlueprintEntry> Large { get; } = new[]
    {
        Workshop_3442307782,
        Workshop_3599670638,
    };
}

public enum BlueprintSource
{
    Local,
    Workshop,
}

public class BlueprintEntry
{
    public string Name { get; }
    public string Path { get; }
    public BlueprintSource Source { get; }
    public long WorkshopId { get; }
    public int EstimatedBlocks { get; }
    public string Description { get; }

    public bool Exists => File.Exists(Path);
    public bool IsJson => Path.EndsWith(".json");

    public BlueprintEntry(string name, string path, BlueprintSource source,
        long workshopId = 0, int estimatedBlocks = 0, string description = "")
    {
        Name = name;
        Path = path;
        Source = source;
        WorkshopId = workshopId;
        EstimatedBlocks = estimatedBlocks;
        Description = description;
    }

    public override string ToString()
        => $"{Name} ({EstimatedBlocks:N0} blocks, {Source}{(Exists ? "" : " MISSING")})";
}
