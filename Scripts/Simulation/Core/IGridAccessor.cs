#pragma warning disable
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
