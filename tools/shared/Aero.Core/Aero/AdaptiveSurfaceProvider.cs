using System.Numerics;
using Aero.Core.Grids;
using Aero.Core.Math;

namespace Aero.Core.Aero;

/// <summary>
/// Advanced incremental surface provider with:
///
///   1. Incremental NormalGroup tracking — running sums per normal are adjusted
///      on each face add/remove. No full-scan RebuildGroups() needed.
///
///   2. Adaptive strategy — small changes (few blocks) use incremental path,
///      large changes (splits, bulk adds) fall back to full rebuild when the
///      changed cell count exceeds a threshold relative to grid size.
///
///   3. Efficient split handling — uses departed block list to remove only
///      affected faces rather than rescanning the entire grid.
///
/// Data structures:
///   _faces     — dense face list (swap-remove for O(1) deletion)
///   _faceKeys  — parallel to _faces, stores (cell, dir) key per slot
///   _faceIndex — maps (cell, dir) → index in _faces
///   _groupData — 6-element array tracking running totals per normal direction
/// </summary>
public class AdaptiveSurfaceProvider : ISurfaceProvider
{
    private static readonly Vector3I[] DirOffsets =
    {
        new( 1, 0, 0), new(-1, 0, 0),
        new( 0, 1, 0), new( 0,-1, 0),
        new( 0, 0, 1), new( 0, 0,-1),
    };

    private static readonly Vector3[] DirNormals =
    {
        Vector3.UnitX, -Vector3.UnitX,
        Vector3.UnitY, -Vector3.UnitY,
        Vector3.UnitZ, -Vector3.UnitZ,
    };

    private static readonly int[] OppositeDir = { 1, 0, 3, 2, 5, 4 };

    // ─── Face storage ─────────────────────────────────────────────

    private readonly List<SurfaceFace> _faces = new();
    private readonly List<(Vector3I cell, int dir)> _faceKeys = new();
    private readonly Dictionary<(Vector3I cell, int dir), int> _faceIndex = new();

    // ─── Incremental group tracking ───────────────────────────────

    private struct GroupAccum
    {
        public float TotalArea;
        public Vector3 WeightedPosition;  // sum of (position × area)
        public int Count;
    }

    private readonly GroupAccum[] _groupData = new GroupAccum[6];
    private readonly List<NormalGroup> _groups = new();
    private bool _groupsDirty = true;

    // ─── Configuration ────────────────────────────────────────────

    private float _blockSize;
    private int _totalCells;  // total occupied cells in the grid (for threshold)

    /// <summary>
    /// If the number of changed cells exceeds this fraction of total cells,
    /// fall back to full rebuild instead of incremental. Default 0.15 (15%).
    /// </summary>
    public float RebuildThreshold { get; set; } = 0.15f;

    // ─── ISurfaceProvider ─────────────────────────────────────────

    public string Name => "Adaptive (incremental)";
    public IReadOnlyList<SurfaceFace> Faces => _faces;

    public IReadOnlyList<NormalGroup> NormalGroups
    {
        get
        {
            if (_groupsDirty)
                MaterializeGroups();
            return _groups;
        }
    }

    public int FaceCount => _faces.Count;
    public int GroupCount => NormalGroups.Count;
    public float BlockSize => _blockSize;

    // ─── Full rebuild ─────────────────────────────────────────────

    public void Build(Grid grid, float blockSize)
    {
        _blockSize = blockSize;
        _totalCells = CountCells(grid);

        _faces.Clear();
        _faceIndex.Clear();
        _faceKeys.Clear();
        Array.Clear(_groupData);

        float faceArea = blockSize * blockSize;

        foreach (var block in grid.Blocks.Values)
        {
            foreach (var cell in block.EnumerateCells())
            {
                for (int d = 0; d < 6; d++)
                {
                    if (grid.GetBlockAt(cell + DirOffsets[d]) != null)
                        continue;

                    var pos = CellFaceCenter(cell, DirOffsets[d], blockSize);
                    AddFaceTracked(cell, d, new SurfaceFace(pos, DirNormals[d], faceArea));
                }
            }
        }

        _groupsDirty = true;
    }

    // ─── OnBlocksChanged ──────────────────────────────────────────

    public void OnBlocksChanged(Grid grid, in BlocksChangedArgs args)
    {
        if (args.AllBlocksRemoved)
        {
            _faces.Clear();
            _faceKeys.Clear();
            _faceIndex.Clear();
            Array.Clear(_groupData);
            _groups.Clear();
            _groupsDirty = false;
            _totalCells = 0;
            return;
        }

        // Estimate the number of affected cells
        int changedCells = EstimateCellCount(args.RemovedBlocks) + EstimateCellCount(args.AddedBlocks);

        if (_totalCells > 0 && changedCells > _totalCells * RebuildThreshold)
        {
            // Bulk change — full rebuild is cheaper
            _totalCells += EstimateCellCount(args.AddedBlocks) - EstimateCellCount(args.RemovedBlocks);
            Build(grid, _blockSize);
            return;
        }

        // Incremental path
        if (args.RemovedBlocks.Count > 0)
        {
            ProcessRemovals(grid, args.RemovedBlocks);
            _totalCells -= EstimateCellCount(args.RemovedBlocks);
        }

        if (args.AddedBlocks.Count > 0)
        {
            ProcessAdds(grid, args.AddedBlocks);
            _totalCells += EstimateCellCount(args.AddedBlocks);
        }

        _groupsDirty = true;
    }

    // ─── OnGridSplit ──────────────────────────────────────────────

    public void OnGridSplit(Grid grid, in GridSplitArgs args)
    {
        int departedCells = 0;
        foreach (var group in args.DepartingGroups)
            foreach (var block in group)
                foreach (var _ in block.EnumerateCells())
                    departedCells++;

        if (_totalCells > 0 && departedCells > _totalCells * RebuildThreshold)
        {
            // Large split — full rebuild on the remaining grid is cheaper
            _totalCells -= departedCells;
            Build(grid, _blockSize);
            return;
        }

        // Small split — incremental removal of departed blocks
        var departed = new List<Block>();
        foreach (var group in args.DepartingGroups)
            foreach (var block in group)
                departed.Add(block);

        if (departed.Count > 0)
        {
            ProcessRemovals(grid, departed);
            _totalCells -= departedCells;
            _groupsDirty = true;
        }
    }

    // ─── Incremental add/remove ───────────────────────────────────

    private void ProcessAdds(Grid grid, IReadOnlyList<Block> added)
    {
        float faceArea = _blockSize * _blockSize;

        foreach (var block in added)
        {
            foreach (var cell in block.EnumerateCells())
            {
                for (int d = 0; d < 6; d++)
                {
                    var neighborCell = cell + DirOffsets[d];

                    if (grid.GetBlockAt(neighborCell) == null)
                    {
                        // New exposed face on the added block
                        var pos = CellFaceCenter(cell, DirOffsets[d], _blockSize);
                        AddFaceTracked(cell, d, new SurfaceFace(pos, DirNormals[d], faceArea));
                    }
                    else
                    {
                        // Neighbor exists — its face toward this cell is now hidden
                        RemoveFaceTracked(neighborCell, OppositeDir[d]);
                    }
                }
            }
        }
    }

    private void ProcessRemovals(Grid grid, IReadOnlyList<Block> removed)
    {
        float faceArea = _blockSize * _blockSize;

        foreach (var block in removed)
        {
            foreach (var cell in block.EnumerateCells())
            {
                // Remove all faces belonging to this cell
                for (int d = 0; d < 6; d++)
                    RemoveFaceTracked(cell, d);

                // Neighbors now have exposed faces toward this empty cell
                for (int d = 0; d < 6; d++)
                {
                    var neighborCell = cell + DirOffsets[d];

                    if (grid.GetBlockAt(neighborCell) != null)
                    {
                        int od = OppositeDir[d];
                        var pos = CellFaceCenter(neighborCell, DirOffsets[od], _blockSize);
                        AddFaceTracked(neighborCell, od,
                            new SurfaceFace(pos, DirNormals[od], faceArea));
                    }
                }
            }
        }
    }

    // ─── Face management with group tracking ──────────────────────

    private void AddFaceTracked(Vector3I cell, int dir, SurfaceFace face)
    {
        var key = (cell, dir);
        if (_faceIndex.ContainsKey(key))
            return;

        int index = _faces.Count;
        _faces.Add(face);
        _faceKeys.Add(key);
        _faceIndex[key] = index;

        // Update running group totals
        ref var g = ref _groupData[dir];
        g.TotalArea += face.Area;
        g.WeightedPosition += face.Position * face.Area;
        g.Count++;
    }

    private void RemoveFaceTracked(Vector3I cell, int dir)
    {
        var key = (cell, dir);
        if (!_faceIndex.TryGetValue(key, out int index))
            return;

        // Update running group totals BEFORE removing
        var face = _faces[index];
        ref var g = ref _groupData[dir];
        g.TotalArea -= face.Area;
        g.WeightedPosition -= face.Position * face.Area;
        g.Count--;

        // Swap-remove
        int lastIndex = _faces.Count - 1;
        if (index != lastIndex)
        {
            _faces[index] = _faces[lastIndex];
            var movedKey = _faceKeys[lastIndex];
            _faceKeys[index] = movedKey;
            _faceIndex[movedKey] = index;
        }

        _faces.RemoveAt(lastIndex);
        _faceKeys.RemoveAt(lastIndex);
        _faceIndex.Remove(key);
    }

    // ─── Group materialization (lazy) ─────────────────────────────

    private void MaterializeGroups()
    {
        _groups.Clear();
        for (int d = 0; d < 6; d++)
        {
            ref var g = ref _groupData[d];
            if (g.Count == 0) continue;
            _groups.Add(new NormalGroup(
                DirNormals[d],
                g.TotalArea,
                g.WeightedPosition / g.TotalArea,
                g.Count));
        }
        _groupsDirty = false;
    }

    // ─── Helpers ──────────────────────────────────────────────────

    private static int CountCells(Grid grid)
    {
        int count = 0;
        foreach (var block in grid.Blocks.Values)
            foreach (var _ in block.EnumerateCells())
                count++;
        return count;
    }

    private static int EstimateCellCount(IReadOnlyList<Block> blocks)
    {
        int count = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            var size = blocks[i].Size;
            count += size.X * size.Y * size.Z;
        }
        return count;
    }

    private static Vector3 CellFaceCenter(Vector3I cell, Vector3I faceDir, float blockSize)
    {
        float cx = (cell.X + 0.5f) * blockSize;
        float cy = (cell.Y + 0.5f) * blockSize;
        float cz = (cell.Z + 0.5f) * blockSize;

        return new Vector3(
            cx + faceDir.X * 0.5f * blockSize,
            cy + faceDir.Y * 0.5f * blockSize,
            cz + faceDir.Z * 0.5f * blockSize);
    }
}
