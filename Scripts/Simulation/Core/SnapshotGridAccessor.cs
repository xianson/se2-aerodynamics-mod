#pragma warning disable
namespace AeroMod;

/// <summary>
/// The grid's occupied cells, copied: a background rebuild reads this instead of the live octree (which the
/// simulation changes under it). Built from the staged cell list of a surface build.
/// </summary>
public sealed class SnapshotGridAccessor : IGridAccessor
{
    private readonly HashSet<Vector3I> _cells;
    public SnapshotGridAccessor(IReadOnlyList<Vector3I> cells) { _cells = new HashSet<Vector3I>(cells); }
    public bool IsCellOccupied(Vector3I position) => _cells.Contains(position);
    public IEnumerable<Vector3I> EnumerateOccupiedCells() => _cells;
    public int CellCount => _cells.Count;
}
