#pragma warning disable
using Keen.Game2.Simulation.WorldObjects.CubeGrids.BlockOctrees;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;

namespace AeroMod;

/// <summary>
/// IGridAccessor implementation wrapping SE2's BlockOctreeComponent.
/// This is the ONLY file that depends on SE2 grid types.
/// </summary>
public class Se2GridAccessor : IGridAccessor
{
    private BlockOctreeComponent _octree;
    private HashSet<Vector3I>? _occupiedCells;

    public Se2GridAccessor(BlockOctreeComponent octree)
    {
        _octree = octree;
    }

    public void SetOctree(BlockOctreeComponent octree)
    {
        _octree = octree;
        _occupiedCells?.Clear();
    }

    public bool IsCellOccupied(Vector3I position)
    {
        return GetOccupiedCells().Contains(position);
    }

    public IEnumerable<Vector3I> EnumerateOccupiedCells()
    {
        return GetOccupiedCells();
    }

    public int CellCount => GetOccupiedCells().Count;

    private HashSet<Vector3I> GetOccupiedCells()
    {
        if (_occupiedCells != null && _occupiedCells.Count > 0)
            return _occupiedCells;

        _occupiedCells ??= new HashSet<Vector3I>();
        var blocks = _octree.GetAllCubeBlocks();
        foreach (var block in blocks)
        {
            if (block == null)
                continue;
            foreach (var cellGroup in block.GetTransformedOccupiedCellGroups())
            {
                var min = cellGroup.Min;
                var max = cellGroup.Max;
                for (int x = min.X; x <= max.X; x++)
                    for (int y = min.Y; y <= max.Y; y++)
                        for (int z = min.Z; z <= max.Z; z++)
                            _occupiedCells.Add(new Vector3I(x, y, z));
            }
        }

        return _occupiedCells;
    }
}
