using System.Text.Json;

namespace Aero.Core.Blueprint;

/// <summary>
/// Block size and shape information resolved from game data.
/// </summary>
public readonly struct BlockInfo
{
    /// <summary>Block dimensions in 0.25m grid cells.</summary>
    public readonly int CellsX, CellsY, CellsZ;

    /// <summary>Shape category (Cube, Slope, Corner, Half, etc.) or null for functional blocks.</summary>
    public readonly string? ArmorShape;

    /// <summary>Whether this is an armor block (vs functional block like thruster, battery).</summary>
    public readonly bool IsArmor;

    /// <summary>Display name from the file path.</summary>
    public readonly string Name;

    public BlockInfo(int cx, int cy, int cz, string name, bool isArmor = false, string? armorShape = null)
    {
        CellsX = cx; CellsY = cy; CellsZ = cz;
        Name = name;
        IsArmor = isArmor;
        ArmorShape = armorShape;
    }

    /// <summary>Size in meters.</summary>
    public (float X, float Y, float Z) SizeMeters => (CellsX * 0.25f, CellsY * 0.25f, CellsZ * 0.25f);

    public override string ToString() => $"{Name} {CellsX}x{CellsY}x{CellsZ}";
}

/// <summary>
/// Scans SE2 game data to build a mapping from block definition GUIDs to
/// their physical dimensions in grid cells.
///
/// For functional blocks (thrusters, batteries, etc.): reads the BoundingBox
/// from the .mwm model file in Models/NonFractured/.
///
/// IMPORTANT -- WHERE THE MODELS LIVE. As of SE2 2.4.0.77 the *game install*
/// ships compiled .vrm models (VR3B binary archives) and contains ZERO .mwm, so
/// scanning the game's GameData/Vanilla/Content resolves armor only and silently
/// drops every functional block to a 1x1x1 fallback. The Mod SDK still ships
/// .mwm in the same layout, so that is the root to scan. Use
/// <see cref="FindContentRoot"/> rather than hardcoding a path, and check
/// <see cref="FunctionalResolved"/> -- a zero there means the scan found no models.
///
/// For armor blocks: derives size from the folder structure (/50/ = 2 cells,
/// /250/ = 10 cells per axis for cubes; other shapes similarly).
///
/// The blueprint's block Definition GUID matches the PrefabBindingDefinition
/// GUID in the base .def file (the one without underscores in its name).
/// </summary>
public class BlockDefinitionCatalog
{
    private readonly Dictionary<string, BlockInfo> _blocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of block definitions loaded.</summary>
    public int Count => _blocks.Count;

    /// <summary>
    /// Look up a block by its definition GUID (as used in blueprints).
    /// Returns null if the GUID is unknown.
    /// </summary>
    public BlockInfo? GetBlock(string guid) =>
        _blocks.TryGetValue(guid, out var info) ? info : null;

    /// <summary>All loaded block definitions.</summary>
    public IReadOnlyDictionary<string, BlockInfo> All => _blocks;

    /// <summary>Armor blocks resolved by the last scan (derived from folder structure).</summary>
    public int ArmorResolved { get; private set; }

    /// <summary>Functional blocks resolved by the last scan (needs a readable model file).</summary>
    public int FunctionalResolved { get; private set; }

    /// <summary>
    /// Functional blocks skipped because no model file was found next to their .def.
    /// A large number here means the content root ships .vrm rather than .mwm.
    /// </summary>
    public int FunctionalMissingModel { get; private set; }

    /// <summary>The content root the last scan used.</summary>
    public string? ScannedRoot { get; private set; }

    /// <summary>Human-readable summary of the last scan.</summary>
    public string ScanSummary =>
        $"{Count} definitions from {ScannedRoot ?? "(nothing)"}: " +
        $"{ArmorResolved} armor, {FunctionalResolved} functional, " +
        $"{FunctionalMissingModel} functional skipped (no model file)";

    /// <summary>
    /// Candidate SE2 content roots, best first. The Mod SDK comes first because it is
    /// the only one that still ships .mwm models; the game install is kept as a fallback
    /// so armor at least resolves if the SDK is not present.
    /// </summary>
    public static readonly string[] CandidateContentRoots =
    {
        @"D:\SteamLibrary\steamapps\common\Space Engineers 2 - Mod SDK\GameData\Vanilla\Content",
        @"C:\Program Files (x86)\Steam\steamapps\common\Space Engineers 2 - Mod SDK\GameData\Vanilla\Content",
        @"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers2\GameData\Vanilla\Content",
    };

    /// <summary>
    /// Pick a content root that actually contains .mwm models, falling back to the first
    /// directory that exists. Override with the AERO_SE2_CONTENT environment variable.
    /// Returns null if nothing usable is found.
    /// </summary>
    public static string? FindContentRoot()
    {
        var overridden = Environment.GetEnvironmentVariable("AERO_SE2_CONTENT");
        if (!string.IsNullOrWhiteSpace(overridden) && Directory.Exists(overridden))
            return overridden;

        string? firstExisting = null;
        foreach (var root in CandidateContentRoots)
        {
            if (!Directory.Exists(root)) continue;
            firstExisting ??= root;
            // Cheap probe: does this root ship models we can actually read?
            if (Directory.EnumerateFiles(root, "*.mwm", SearchOption.AllDirectories).Any())
                return root;
        }
        return firstExisting;
    }

    /// <summary>
    /// Scan the SE2 game content directory and build the GUID → BlockInfo map.
    /// </summary>
    public void ScanGameData(string contentRoot)
    {
        _blocks.Clear();
        ArmorResolved = FunctionalResolved = FunctionalMissingModel = 0;
        ScannedRoot = contentRoot;

        // Scan Armors directory
        var armorsDir = Path.Combine(contentRoot, "Armors");
        if (Directory.Exists(armorsDir))
            ScanArmorBlocks(armorsDir);

        // Scan Blocks directory (functional blocks)
        var blocksDir = Path.Combine(contentRoot, "Blocks");
        if (Directory.Exists(blocksDir))
            ScanFunctionalBlocks(blocksDir);
    }

    private void ScanArmorBlocks(string armorsDir)
    {
        // Blueprint blocks reference the EntityCompositeDefinition GUID,
        // which is in the *_ServerComposition.def file.
        foreach (var defFile in Directory.EnumerateFiles(armorsDir, "*_ServerComposition.def", SearchOption.AllDirectories))
        {
            var guid = ExtractGuid(defFile);
            if (guid == null) continue;

            // Derive name from filename: "ArmorCubeLight50_ServerComposition" → "ArmorCubeLight50"
            var fileName = Path.GetFileNameWithoutExtension(defFile);
            var name = fileName.Replace("_ServerComposition", "");

            // Parse shape and size from path: /Armors/{Shape}/{Size}/{Variant}/
            var relPath = Path.GetRelativePath(armorsDir, defFile).Replace('\\', '/');
            var parts = relPath.Split('/');

            string? shape = parts.Length > 0 ? parts[0] : null;
            int sizeCm = 0;
            for (int i = 1; i < parts.Length; i++)
            {
                if (int.TryParse(parts[i], out int s))
                {
                    sizeCm = s;
                    break;
                }
            }

            if (sizeCm == 0) continue;

            // Each armor entity in the blueprint is a single grid cell.
            // The "50"/"250" path component indicates the grid class (small/large),
            // not the number of cells per block.
            _blocks[guid] = new BlockInfo(1, 1, 1, name, isArmor: true, armorShape: shape);
            ArmorResolved++;
        }
    }

    private void ScanFunctionalBlocks(string blocksDir)
    {
        // Blueprint blocks reference the EntityCompositeDefinition GUID,
        // which is in the *_ServerComposition.def file.
        // The .mwm is in Models/NonFractured/ relative to the block's directory.
        foreach (var defFile in Directory.EnumerateFiles(blocksDir, "*_ServerComposition.def", SearchOption.AllDirectories))
        {
            var guid = ExtractGuid(defFile);
            if (guid == null) continue;

            var fileName = Path.GetFileNameWithoutExtension(defFile);
            var name = fileName.Replace("_ServerComposition", "");

            // Look for .mwm in Models/NonFractured/ relative to this .def's directory
            var defDir = Path.GetDirectoryName(defFile)!;
            var modelsDir = Path.Combine(defDir, "Models", "NonFractured");

            if (!Directory.Exists(modelsDir))
            {
                FunctionalMissingModel++;
                continue;
            }

            // Find the main .mwm (not LOD, not subpart)
            string? mwmPath = null;
            foreach (var mwm in Directory.EnumerateFiles(modelsDir, "*.mwm"))
            {
                var mwmName = Path.GetFileNameWithoutExtension(mwm);
                if (mwmName.Contains("LOD", StringComparison.OrdinalIgnoreCase)) continue;
                if (mwmName.Contains("_S", StringComparison.Ordinal) &&
                    mwmName.IndexOf("_S") + 2 < mwmName.Length &&
                    char.IsDigit(mwmName[mwmName.IndexOf("_S") + 2])) continue;
                mwmPath = mwm;
                break;
            }

            if (mwmPath == null)
            {
                // Most likely this root ships .vrm (compiled) instead of .mwm.
                FunctionalMissingModel++;
                continue;
            }

            var dims = MwmBoundingBoxReader.ReadCellDimensions(mwmPath);
            if (dims == null)
            {
                FunctionalMissingModel++;
                continue;
            }

            _blocks[guid] = new BlockInfo(dims.Value.X, dims.Value.Y, dims.Value.Z, name);
            FunctionalResolved++;
        }
    }

    /// <summary>
    /// Extract the "Guid" field from a .def JSON file.
    /// </summary>
    private static string? ExtractGuid(string defPath)
    {
        try
        {
            var json = File.ReadAllText(defPath);
            var doc = JsonDocument.Parse(json);
            var value = doc.RootElement.GetProperty("$Value");
            if (value.TryGetProperty("Guid", out var guidProp))
                return guidProp.GetString();
        }
        catch { }
        return null;
    }
}
