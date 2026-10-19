using System.Text.Json;

namespace Aero.Core.Blueprint;

/// <summary>
/// Parses SE2 blueprint files (grid.json.bak.json format).
///
/// SE2 blueprint structure:
///   EntityBundle
///     └─ Builders[0] = grid entity (Definition = grid def GUID)
///         └─ ObjectBuilders (keyed by component GUID):
///             ├─ WorldTransformComponentObjectBuilder — grid transform
///             ├─ HierarchyComponentObjectBuilder — children = blocks
///             │     └─ Children[] — each is a block entity:
///             │         ├─ ChildTransformComponentObjectBuilder — position/orientation
///             │         ├─ CubeBlockObjectBuilder — color, build progress, health
///             │         ├─ PowerableBlockObjectBuilder — functional block variant
///             │         ├─ ThrusterObjectBuilder, SeatObjectBuilder, etc.
///             │         └─ ... other components
///             ├─ CubeGridObjectBuilder — grid name, dampening, etc.
///             └─ GridPhysicsObjectBuilder — velocity, motion type
/// </summary>
public static class BlueprintParser
{
    // Well-known component type suffixes we care about
    private const string ChildTransformType = "ChildTransformComponentObjectBuilder";
    private const string CubeBlockType = "CubeBlockObjectBuilder";
    private const string PowerableBlockType = "PowerableBlockObjectBuilder";
    private const string FunctionalBlockType = "FunctionalBlockObjectBuilder";
    private const string HierarchyType = "HierarchyComponentObjectBuilder";
    private const string CubeGridType = "CubeGridObjectBuilder";
    private const string ThrusterType = "ThrusterObjectBuilder";
    private const string SeatType = "SeatObjectBuilder";

    /// <summary>
    /// Parse a blueprint from a directory containing grid.json.bak.json and optionally .container-info.
    /// </summary>
    public static BlueprintData ParseDirectory(string blueprintDir)
    {
        var result = new BlueprintData();

        // Parse metadata from .container-info if present
        var containerInfoPath = Path.Combine(blueprintDir, ".container-info");
        if (File.Exists(containerInfoPath))
        {
            ParseContainerInfo(containerInfoPath, result);
        }

        // Parse the grid — try .bak.json first, fall back to grid.json
        var gridPath = Path.Combine(blueprintDir, "grid.json.bak.json");
        if (!File.Exists(gridPath))
            gridPath = Path.Combine(blueprintDir, "grid.json");
        if (!File.Exists(gridPath))
            throw new FileNotFoundException("Blueprint grid file not found", gridPath);

        var json = File.ReadAllText(gridPath);
        var doc = JsonDocument.Parse(json);
        ParseEntityBundle(doc.RootElement, result);

        return result;
    }

    /// <summary>
    /// Parse a blueprint directly from a grid JSON string.
    /// </summary>
    public static BlueprintData ParseGridJson(string json)
    {
        var result = new BlueprintData();
        var doc = JsonDocument.Parse(json);
        ParseEntityBundle(doc.RootElement, result);
        return result;
    }

    private static void ParseContainerInfo(string path, BlueprintData result)
    {
        var json = File.ReadAllText(path);
        var doc = JsonDocument.Parse(json);
        var value = doc.RootElement.GetProperty("$Value");
        var meta = value.GetProperty("Meta");

        if (meta.TryGetProperty("UserInfo", out var userInfo) &&
            userInfo.TryGetProperty("DisplayName", out var displayName) &&
            displayName.TryGetProperty("RawText", out var rawText))
        {
            result.Name = rawText.GetString() ?? "";
        }

        if (meta.TryGetProperty("BlockCount", out var blockCount))
            result.MetaBlockCount = blockCount.GetInt32();

        if (meta.TryGetProperty("PCU", out var pcu))
            result.MetaPCU = pcu.GetInt32();

        if (meta.TryGetProperty("GameBuildNumber", out var build))
            result.GameVersion = $"2.0.1.{build.GetInt32()}";
    }

    private static void ParseEntityBundle(JsonElement root, BlueprintData result)
    {
        var value = root.GetProperty("$Value");
        var builders = value.GetProperty("Builders");

        // First builder is the grid entity
        var gridBuilder = builders[0];
        result.Grid.DefinitionId = gridBuilder.GetProperty("Definition").GetString() ?? "";

        var objectBuilders = gridBuilder.GetProperty("ObjectBuilders");
        foreach (var ob in objectBuilders.EnumerateArray())
        {
            if (!ob.TryGetProperty("Value", out var val) || val.ValueKind == JsonValueKind.Null)
                continue;

            var type = GetShortType(val);

            if (type == HierarchyType)
            {
                // Children are the blocks
                ParseGridChildren(val, result.Grid);
            }
            else if (type == CubeGridType)
            {
                if (val.TryGetProperty("DisplayName", out var dn) &&
                    dn.TryGetProperty("RawText", out var raw))
                    result.Grid.DisplayName = raw.GetString();

                if (val.TryGetProperty("IsDampening", out var damp))
                    result.Grid.IsDampening = damp.GetBoolean();
            }
        }
    }

    private static void ParseGridChildren(JsonElement hierarchyOB, GridEntity grid)
    {
        if (!hierarchyOB.TryGetProperty("Children", out var children))
            return;

        foreach (var child in children.EnumerateArray())
        {
            if (!child.TryGetProperty("Value", out var entityVal) ||
                entityVal.ValueKind == JsonValueKind.Null)
                continue;

            var block = ParseBlockEntity(entityVal);
            if (block != null)
                grid.Blocks.Add(block);
        }
    }

    private static BlockEntity? ParseBlockEntity(JsonElement entity)
    {
        var block = new BlockEntity();

        if (entity.TryGetProperty("$ObjectId", out var objId))
            block.ObjectId = objId.GetInt32();

        if (entity.TryGetProperty("Definition", out var def))
            block.DefinitionId = def.GetString() ?? "";

        if (!entity.TryGetProperty("ObjectBuilders", out var builders))
            return null;

        foreach (var ob in builders.EnumerateArray())
        {
            var key = ob.GetProperty("Key").GetString() ?? "";
            if (!ob.TryGetProperty("Value", out var val) || val.ValueKind == JsonValueKind.Null)
                continue;

            var type = GetShortType(val);

            switch (type)
            {
                case ChildTransformType:
                    ParseTransform(val, block);
                    break;

                case CubeBlockType:
                    ParseCubeBlock(val, block);
                    block.BlockType = CubeBlockType;
                    break;

                case PowerableBlockType:
                    ParseCubeBlock(val, block); // shares same base fields
                    ParsePowerable(val, block);
                    block.BlockType = PowerableBlockType;
                    break;

                case FunctionalBlockType:
                    ParseCubeBlock(val, block);
                    block.BlockType = FunctionalBlockType;
                    break;

                case ThrusterType:
                case SeatType:
                    // Store the specific block type for later use
                    block.ExtraComponents[type] = ExtractRawValues(val);
                    break;

                default:
                    // Store unrecognized components for potential future use
                    if (type != null && type != HierarchyType)
                        block.ExtraComponents[type] = null;
                    break;
            }
        }

        return block;
    }

    private static void ParseTransform(JsonElement val, BlockEntity block)
    {
        if (!val.TryGetProperty("TransformWithEulerHint", out var tweh))
            return;
        if (!tweh.TryGetProperty("Transform", out var transform))
            return;

        if (transform.TryGetProperty("Position", out var pos))
            block.Position = ReadVector3(pos);

        if (transform.TryGetProperty("Orientation", out var orient))
            block.Orientation = new QuaternionD(
                orient.GetProperty("X").GetDouble(),
                orient.GetProperty("Y").GetDouble(),
                orient.GetProperty("Z").GetDouble(),
                orient.GetProperty("W").GetDouble());
    }

    private static void ParseCubeBlock(JsonElement val, BlockEntity block)
    {
        if (val.TryGetProperty("Color", out var color) &&
            color.TryGetProperty("Values", out var colorValues))
        {
            block.Color = new Vector4D(
                colorValues.GetProperty("X").GetDouble(),
                colorValues.GetProperty("Y").GetDouble(),
                colorValues.GetProperty("Z").GetDouble(),
                colorValues.GetProperty("W").GetDouble());
        }

        if (val.TryGetProperty("BuildProgress", out var bp))
            block.BuildProgress = (float)bp.GetDouble();

        if (val.TryGetProperty("HealthIntegrity", out var hi))
            block.HealthIntegrity = (float)hi.GetDouble();
    }

    private static void ParsePowerable(JsonElement val, BlockEntity block)
    {
        if (val.TryGetProperty("Enabled", out var en))
            block.Enabled = en.GetBoolean();

        if (val.TryGetProperty("DisplayName", out var dn) &&
            dn.TryGetProperty("RawText", out var raw))
            block.DisplayName = raw.GetString();
    }

    private static Vector3D ReadVector3(JsonElement el) => new(
        el.GetProperty("X").GetDouble(),
        el.GetProperty("Y").GetDouble(),
        el.GetProperty("Z").GetDouble());

    private static string? GetShortType(JsonElement el)
    {
        if (!el.TryGetProperty("$Type", out var typeProp))
            return null;
        var fullType = typeProp.GetString();
        if (fullType == null) return null;
        // "$Type": "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockObjectBuilder"
        // -> "CubeBlockObjectBuilder"
        var lastDot = fullType.LastIndexOf('.');
        return lastDot >= 0 ? fullType[(lastDot + 1)..] : fullType;
    }

    private static Dictionary<string, object?> ExtractRawValues(JsonElement el)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Name == "$Type") continue;
            dict[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => prop.Value.GetRawText()
            };
        }
        return dict;
    }
}
