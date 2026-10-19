using System.Numerics;
using Aero.Core.Math;

namespace Aero.Core.Grids;

/// <summary>
/// A block placed in a grid. Occupies one or more grid cells.
/// </summary>
public class Block
{
    public int Id { get; }

    /// <summary>Block definition GUID (identifies type — armor cube, thruster, etc.).</summary>
    public string DefinitionId { get; }

    /// <summary>Minimum corner of occupied cells (inclusive).</summary>
    public Vector3I Min { get; }

    /// <summary>Maximum corner of occupied cells (inclusive).</summary>
    public Vector3I Max { get; }

    /// <summary>Block's forward direction (grid-local unit vector).</summary>
    public Vector3 Forward { get; }

    /// <summary>Block's up direction (grid-local unit vector).</summary>
    public Vector3 Up { get; }

    /// <summary>The grid this block belongs to (set by Grid on add/remove).</summary>
    public Grid? Grid { get; internal set; }

    public BoundingBoxI OccupiedCells => new(Min, Max);

    /// <summary>Size in grid cells per axis.</summary>
    public Vector3I Size => new(Max.X - Min.X + 1, Max.Y - Min.Y + 1, Max.Z - Min.Z + 1);

    public Block(int id, string definitionId, Vector3I position)
        : this(id, definitionId, position, position) { }

    public Block(int id, string definitionId, Vector3I min, Vector3I max)
        : this(id, definitionId, min, max, new Vector3(0, 0, -1), Vector3.UnitY) { }

    public Block(int id, string definitionId, Vector3I min, Vector3I max,
        Vector3 forward, Vector3 up)
    {
        Id = id;
        DefinitionId = definitionId;
        Min = min;
        Max = max;
        Forward = forward;
        Up = up;
    }

    /// <summary>
    /// Enumerate all grid cells this block occupies.
    /// </summary>
    public IEnumerable<Vector3I> EnumerateCells()
    {
        for (int x = Min.X; x <= Max.X; x++)
            for (int y = Min.Y; y <= Max.Y; y++)
                for (int z = Min.Z; z <= Max.Z; z++)
                    yield return new Vector3I(x, y, z);
    }

    public override string ToString() => $"Block({Id}, def={DefinitionId}, [{Min}-{Max}])";
}
