#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace AeroMod;

/// <summary>
/// Thin abstraction over grid cell storage.
/// Only used during build/rebuild (not per-frame hot path), so virtual dispatch is fine.
/// The sole SE2-coupled implementation is Se2GridAccessor; everything else is pure math.
/// </summary>
public interface IGridAccessor
{
    /// <summary>Whether a cell at the given grid position is occupied by a block.</summary>
    bool IsCellOccupied(Vector3I position);

    /// <summary>Enumerate all occupied cell positions in the grid.</summary>
    IEnumerable<Vector3I> EnumerateOccupiedCells();

    /// <summary>Total number of occupied cells.</summary>
    int CellCount { get; }
}
