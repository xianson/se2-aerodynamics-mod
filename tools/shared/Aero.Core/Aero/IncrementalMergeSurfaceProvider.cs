using System.Numerics;
using Aero.Core.Grids;
using Aero.Core.Math;

namespace Aero.Core.Aero;

/// <summary>
/// Surface provider combining incremental face tracking with lazy per-plane
/// greedy rectangle merging.
///
/// Raw face add/remove is O(1) per face. Normal groups are maintained via
/// running sums (no rebuild scan). The merged face list is rebuilt lazily —
/// only planes whose cells changed since last access are re-merged.
///
/// Typical steady-state cost:
///   Block add/remove: O(6) for face updates, O(0) for groups
///   Faces access: O(dirty_plane_cells × log) for re-merge of affected planes
///   NormalGroups access: O(1) materialization from running sums
///
/// Falls back to full rebuild when changed cells exceed RebuildThreshold.
/// </summary>
public class IncrementalMergeSurfaceProvider : ISurfaceProvider
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

    private static readonly (int PlaneAxis, int RowAxis, int ColAxis)[] AxisMapping =
    {
        (0, 1, 2), (0, 1, 2), // +X / -X
        (1, 0, 2), (1, 0, 2), // +Y / -Y
        (2, 0, 1), (2, 0, 1), // +Z / -Z
    };

    // ─── Per-plane cell tracking ──────────────────────────────────
    // Key: packed (dir, planeCoord). Value: set of packed (row, col).
    private readonly Dictionary<long, HashSet<long>> _planeCells = new();
    private readonly HashSet<long> _dirtyPlanes = new();

    // Cached merged faces per plane
    private readonly Dictionary<long, List<SurfaceFace>> _mergedByPlane = new();

    // Flattened merged output
    private readonly List<SurfaceFace> _mergedFaces = new();
    private bool _mergedDirty = true;

    // ─── Incremental group tracking ──────────────────────────────
    private struct GroupAccum
    {
        public float TotalArea;
        public Vector3 WeightedPosition;
        public int Count;
    }

    private readonly GroupAccum[] _groupData = new GroupAccum[6];
    private readonly List<NormalGroup> _groups = new();
    private bool _groupsDirty = true;

    // ─── Config ──────────────────────────────────────────────────
    private float _blockSize;
    private int _totalCells;

    /// <summary>
    /// If changed cells exceed this fraction of total cells,
    /// fall back to full rebuild. Default 0.15 (15%).
    /// </summary>
    public float RebuildThreshold { get; set; } = 0.15f;

    // ─── ISurfaceProvider ────────────────────────────────────────

    public string Name => "Incremental Merge";

    public IReadOnlyList<SurfaceFace> Faces
    {
        get
        {
            if (_mergedDirty) RebuildMergedFaces();
            return _mergedFaces;
        }
    }

    public IReadOnlyList<NormalGroup> NormalGroups
    {
        get
        {
            if (_groupsDirty) MaterializeGroups();
            return _groups;
        }
    }

    public int FaceCount
    {
        get
        {
            if (_mergedDirty) RebuildMergedFaces();
            return _mergedFaces.Count;
        }
    }

    public int GroupCount => NormalGroups.Count;
    public float BlockSize => _blockSize;

    /// <summary>Number of raw (unmerged) exposed faces tracked.</summary>
    public int RawFaceCount
    {
        get
        {
            int count = 0;
            foreach (var cells in _planeCells.Values)
                count += cells.Count;
            return count;
        }
    }

    // ─── Full rebuild ────────────────────────────────────────────

    public void Build(Grid grid, float blockSize)
    {
        _blockSize = blockSize;
        _totalCells = CountCells(grid);

        _planeCells.Clear();
        _dirtyPlanes.Clear();
        _mergedByPlane.Clear();
        _mergedFaces.Clear();
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

                    AddFaceTracked(cell, d, faceArea);
                }
            }
        }

        // Mark all planes dirty for initial merge
        foreach (var planeKey in _planeCells.Keys)
            _dirtyPlanes.Add(planeKey);

        _mergedDirty = true;
        _groupsDirty = true;
    }

    // ─── OnBlocksChanged ─────────────────────────────────────────

    public void OnBlocksChanged(Grid grid, in BlocksChangedArgs args)
    {
        if (args.AllBlocksRemoved)
        {
            _planeCells.Clear();
            _dirtyPlanes.Clear();
            _mergedByPlane.Clear();
            _mergedFaces.Clear();
            _groups.Clear();
            Array.Clear(_groupData);
            _mergedDirty = false;
            _groupsDirty = false;
            _totalCells = 0;
            return;
        }

        int changedCells = EstimateCellCount(args.RemovedBlocks)
                         + EstimateCellCount(args.AddedBlocks);

        if (_totalCells > 0 && changedCells > _totalCells * RebuildThreshold)
        {
            Build(grid, _blockSize);
            return;
        }

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

        _mergedDirty = true;
        _groupsDirty = true;
    }

    // ─── OnGridSplit ─────────────────────────────────────────────

    public void OnGridSplit(Grid grid, in GridSplitArgs args)
    {
        int departedCells = 0;
        foreach (var group in args.DepartingGroups)
            foreach (var block in group)
                foreach (var _ in block.EnumerateCells())
                    departedCells++;

        if (_totalCells > 0 && departedCells > _totalCells * RebuildThreshold)
        {
            _totalCells -= departedCells;
            Build(grid, _blockSize);
            return;
        }

        var departed = new List<Block>();
        foreach (var group in args.DepartingGroups)
            foreach (var block in group)
                departed.Add(block);

        if (departed.Count > 0)
        {
            ProcessRemovals(grid, departed);
            _totalCells -= departedCells;
            _mergedDirty = true;
            _groupsDirty = true;
        }
    }

    // ─── Incremental processing ──────────────────────────────────

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
                        AddFaceTracked(cell, d, faceArea);
                    }
                    else
                    {
                        RemoveFaceTracked(neighborCell, OppositeDir[d], faceArea);
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
                    RemoveFaceTracked(cell, d, faceArea);

                // Neighbors now have exposed faces toward this empty cell
                for (int d = 0; d < 6; d++)
                {
                    var neighborCell = cell + DirOffsets[d];
                    if (grid.GetBlockAt(neighborCell) != null)
                    {
                        int od = OppositeDir[d];
                        AddFaceTracked(neighborCell, od, faceArea);
                    }
                }
            }
        }
    }

    // ─── Face + plane tracking ───────────────────────────────────

    private void AddFaceTracked(Vector3I cell, int dir, float faceArea)
    {
        var (planeAxis, rowAxis, colAxis) = AxisMapping[dir];
        int planeCoord = GetAxis(cell, planeAxis);
        int row = GetAxis(cell, rowAxis);
        int col = GetAxis(cell, colAxis);

        long planeKey = PackPlane(dir, planeCoord);
        long cellKey = PackRC(row, col);

        if (!_planeCells.TryGetValue(planeKey, out var cells))
        {
            cells = new HashSet<long>();
            _planeCells[planeKey] = cells;
        }

        if (!cells.Add(cellKey))
            return; // already tracked

        // Update group running totals
        var pos = CellFaceCenter(cell, DirOffsets[dir], _blockSize);
        ref var g = ref _groupData[dir];
        g.TotalArea += faceArea;
        g.WeightedPosition += pos * faceArea;
        g.Count++;

        _dirtyPlanes.Add(planeKey);
    }

    private void RemoveFaceTracked(Vector3I cell, int dir, float faceArea)
    {
        var (planeAxis, rowAxis, colAxis) = AxisMapping[dir];
        int planeCoord = GetAxis(cell, planeAxis);
        int row = GetAxis(cell, rowAxis);
        int col = GetAxis(cell, colAxis);

        long planeKey = PackPlane(dir, planeCoord);
        long cellKey = PackRC(row, col);

        if (!_planeCells.TryGetValue(planeKey, out var cells) || !cells.Remove(cellKey))
            return; // wasn't tracked

        // Update group running totals
        var pos = CellFaceCenter(cell, DirOffsets[dir], _blockSize);
        ref var g = ref _groupData[dir];
        g.TotalArea -= faceArea;
        g.WeightedPosition -= pos * faceArea;
        g.Count--;

        _dirtyPlanes.Add(planeKey);

        if (cells.Count == 0)
            _planeCells.Remove(planeKey);
    }

    // ─── Lazy merge ──────────────────────────────────────────────

    private void RebuildMergedFaces()
    {
        // Re-merge only dirty planes
        foreach (var planeKey in _dirtyPlanes)
        {
            if (_planeCells.TryGetValue(planeKey, out var cells) && cells.Count > 0)
            {
                UnpackPlane(planeKey, out int dir, out int planeCoord);
                _mergedByPlane[planeKey] = MergePlane(dir, planeCoord, cells);
            }
            else
            {
                _mergedByPlane.Remove(planeKey);
            }
        }
        _dirtyPlanes.Clear();

        // Flatten all plane caches into the output list
        _mergedFaces.Clear();
        foreach (var faces in _mergedByPlane.Values)
            _mergedFaces.AddRange(faces);

        _mergedDirty = false;
    }

    private List<SurfaceFace> MergePlane(int dir, int planeCoord, HashSet<long> packedCells)
    {
        var (planeAxis, rowAxis, colAxis) = AxisMapping[dir];
        var faceDir = DirOffsets[dir];
        var normal = DirNormals[dir];
        float faceArea = _blockSize * _blockSize;

        // Unpack cells for merging
        var positions = new List<(int row, int col)>(packedCells.Count);
        foreach (var packed in packedCells)
        {
            UnpackRC(packed, out int r, out int c);
            positions.Add((r, c));
        }

        var rectangles = GreedyMerge2D(positions);
        var result = new List<SurfaceFace>(rectangles.Count);

        foreach (var rect in rectangles)
        {
            int cellCount = rect.Width * rect.Height;
            float area = cellCount * faceArea;

            float centerRow = rect.MinRow + (rect.Width - 1) * 0.5f;
            float centerCol = rect.MinCol + (rect.Height - 1) * 0.5f;

            float cellPlane = planeCoord + 0.5f;
            float cellRow = centerRow + 0.5f;
            float cellCol = centerCol + 0.5f;

            var pos = new float[3];
            pos[planeAxis] = cellPlane * _blockSize
                + GetAxis(faceDir, planeAxis) * 0.5f * _blockSize;
            pos[rowAxis] = cellRow * _blockSize;
            pos[colAxis] = cellCol * _blockSize;

            result.Add(new SurfaceFace(
                new Vector3(pos[0], pos[1], pos[2]),
                normal,
                area));
        }

        return result;
    }

    // ─── Greedy 2D merge (pooled allocations) ─────────────────────

    // Pooled buffers to avoid per-merge allocation
    [ThreadStatic] private static HashSet<long>? _occupiedPool;
    [ThreadStatic] private static HashSet<long>? _visitedPool;
    [ThreadStatic] private static List<MergedRect>? _resultPool;

    private static List<MergedRect> GreedyMerge2D(List<(int row, int col)> positions)
    {
        var occupied = _occupiedPool ??= new HashSet<long>();
        var visited = _visitedPool ??= new HashSet<long>();
        var result = _resultPool ??= new List<MergedRect>();

        occupied.Clear();
        visited.Clear();
        result.Clear();

        foreach (var (r, c) in positions)
            occupied.Add(PackRC(r, c));

        positions.Sort((a, b) =>
        {
            int cmp = a.row.CompareTo(b.row);
            return cmp != 0 ? cmp : a.col.CompareTo(b.col);
        });

        foreach (var (startRow, startCol) in positions)
        {
            long startKey = PackRC(startRow, startCol);
            if (visited.Contains(startKey))
                continue;

            int width = 1;
            while (occupied.Contains(PackRC(startRow + width, startCol)) &&
                   !visited.Contains(PackRC(startRow + width, startCol)))
                width++;

            int height = 1;
            bool canExtend = true;
            while (canExtend)
            {
                int nextCol = startCol + height;
                for (int r = 0; r < width; r++)
                {
                    long key = PackRC(startRow + r, nextCol);
                    if (!occupied.Contains(key) || visited.Contains(key))
                    {
                        canExtend = false;
                        break;
                    }
                }
                if (canExtend) height++;
            }

            for (int r = 0; r < width; r++)
                for (int c = 0; c < height; c++)
                    visited.Add(PackRC(startRow + r, startCol + c));

            result.Add(new MergedRect(startRow, startCol, width, height));
        }

        // Return a copy — pool keeps the originals for reuse
        return new List<MergedRect>(result);
    }

    // ─── Groups ──────────────────────────────────────────────────

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

    // ─── Packing helpers ─────────────────────────────────────────

    private static long PackPlane(int dir, int planeCoord)
        => ((long)dir << 32) | (uint)planeCoord;

    private static void UnpackPlane(long key, out int dir, out int planeCoord)
    {
        dir = (int)(key >> 32);
        planeCoord = (int)(uint)key;
    }

    private static long PackRC(int row, int col)
        => ((long)row << 32) | (uint)col;

    private static void UnpackRC(long key, out int row, out int col)
    {
        row = (int)(key >> 32);
        col = (int)(uint)key;
    }

    private static int GetAxis(Vector3I v, int axis) => axis switch
    {
        0 => v.X, 1 => v.Y, 2 => v.Z,
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };

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

    private readonly struct MergedRect
    {
        public readonly int MinRow, MinCol, Width, Height;

        public MergedRect(int minRow, int minCol, int width, int height)
        {
            MinRow = minRow; MinCol = minCol;
            Width = width; Height = height;
        }
    }
}
