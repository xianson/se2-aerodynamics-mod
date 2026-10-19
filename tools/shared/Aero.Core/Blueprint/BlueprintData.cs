namespace Aero.Core.Blueprint;

/// <summary>
/// Parsed SE2 blueprint — the grid entity with all its child blocks.
/// </summary>
public class BlueprintData
{
    /// <summary>Display name from container-info metadata.</summary>
    public string Name { get; set; } = "";

    /// <summary>Game version that created this blueprint.</summary>
    public string GameVersion { get; set; } = "";

    /// <summary>Block count from metadata.</summary>
    public int MetaBlockCount { get; set; }

    /// <summary>PCU from metadata.</summary>
    public int MetaPCU { get; set; }

    /// <summary>The root grid entity.</summary>
    public GridEntity Grid { get; set; } = new();
}

/// <summary>
/// A CubeGrid entity containing blocks.
/// </summary>
public class GridEntity
{
    /// <summary>Grid definition GUID.</summary>
    public string DefinitionId { get; set; } = "";

    /// <summary>Display name if set in CubeGridObjectBuilder.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Whether dampeners are on.</summary>
    public bool IsDampening { get; set; }

    /// <summary>All blocks in this grid.</summary>
    public List<BlockEntity> Blocks { get; } = new();
}

/// <summary>
/// A single block within a grid.
/// </summary>
public class BlockEntity
{
    /// <summary>Sequential object ID from the blueprint.</summary>
    public int ObjectId { get; set; }

    /// <summary>Block definition GUID (identifies the block type — armor cube, thruster, cockpit, etc.).</summary>
    public string DefinitionId { get; set; } = "";

    /// <summary>Position relative to grid origin (from ChildTransformComponentObjectBuilder).</summary>
    public Vector3D Position { get; set; }

    /// <summary>Orientation quaternion (from ChildTransformComponentObjectBuilder).</summary>
    public QuaternionD Orientation { get; set; }

    /// <summary>Block color (RGBA from CubeBlockObjectBuilder or PowerableBlockObjectBuilder).</summary>
    public Vector4D Color { get; set; }

    /// <summary>Build progress [0..1].</summary>
    public float BuildProgress { get; set; } = 1f;

    /// <summary>Health integrity [0..1].</summary>
    public float HealthIntegrity { get; set; } = 1f;

    /// <summary>
    /// The block's functional type — what kind of CubeBlock-derived ObjectBuilder it has.
    /// E.g. "CubeBlockObjectBuilder", "PowerableBlockObjectBuilder", "ThrusterObjectBuilder"
    /// </summary>
    public string BlockType { get; set; } = "CubeBlockObjectBuilder";

    /// <summary>Whether this is a functional block that's enabled.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Display name if the block has one (functional blocks).</summary>
    public string? DisplayName { get; set; }

    /// <summary>Raw key-value component data for any components we don't explicitly parse.</summary>
    public Dictionary<string, object?> ExtraComponents { get; } = new();
}

public record struct Vector3D(double X, double Y, double Z)
{
    public override string ToString() => $"({X:F3}, {Y:F3}, {Z:F3})";
}

public record struct Vector4D(double X, double Y, double Z, double W)
{
    public override string ToString() => $"({X:F3}, {Y:F3}, {Z:F3}, {W:F3})";
}

public record struct QuaternionD(double X, double Y, double Z, double W)
{
    public static QuaternionD Identity => new(0, 0, 0, 1);
    public override string ToString() => $"({X:F4}, {Y:F4}, {Z:F4}, {W:F4})";
}
