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
    private int _cellCount;

    public Se2GridAccessor(BlockOctreeComponent octree)
    {
        _octree = octree;
        _cellCount = -1; // lazy
    }

    public void SetOctree(BlockOctreeComponent octree)
    {
        _octree = octree;
        _cellCount = -1;
    }

    public bool IsCellOccupied(Vector3I position)
    {
        return _octree.TryGetCubeBlock(position) != null;
    }

    public IEnumerable<Vector3I> EnumerateOccupiedCells()
    {
        // Copy Span to List first — Span cannot cross yield boundary (CS4007)
        var blocks = _octree.GetAllCubeBlocks();
        var blockList = new List<CubeBlockComponent>(blocks.Length);
        foreach (var block in blocks)
        {
            if (block != null)
                blockList.Add(block);
        }

        foreach (var block in blockList)
        {
            foreach (var cellGroup in block.GetTransformedOccupiedCellGroups())
            {
                var min = cellGroup.Min;
                var max = cellGroup.Max;
                for (int x = min.X; x <= max.X; x++)
                    for (int y = min.Y; y <= max.Y; y++)
                        for (int z = min.Z; z <= max.Z; z++)
                        {
                            var pos = new Vector3I(x, y, z);
                            // Cell groups are bounding boxes — slopes/corners
                            // don't fill every cell. Verify with the octree,
                            // and ensure the cell belongs to THIS block (not an
                            // adjacent block whose cell falls in our bbox).
                            var occupant = _octree.TryGetCubeBlock(pos);
                            if (occupant != null && occupant == block)
                                yield return pos;
                        }
            }
        }
    }

    public int CellCount
    {
        get
        {
            if (_cellCount < 0)
            {
                int count = 0;
                foreach (var _ in EnumerateOccupiedCells())
                    count++;
                _cellCount = count;
            }
            return _cellCount;
        }
    }
}
