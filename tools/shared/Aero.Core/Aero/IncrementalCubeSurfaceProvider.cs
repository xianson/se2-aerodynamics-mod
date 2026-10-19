using System.Numerics;
using Aero.Core.Grids;
using Aero.Core.Math;

namespace Aero.Core.Aero;

/// <summary>
/// Incrementally-updated surface provider for cube blocks.
///
/// Maintains an indexed face list so that block add/remove only touches
/// the affected cells and their immediate neighbors, rather than rescanning
/// the entire grid.
///
/// Data structures:
///   _faces          — dense list of all exposed faces
///   _faceIndex      — maps (cell, direction) → index in _faces for O(1) lookup
///
/// Face removal uses swap-with-last to keep _faces dense in O(1).
///
/// Normal groups are rebuilt from scratch after each mutation — this is just
/// 6 buckets and is negligible compared to the face-level work.
/// </summary>
public class IncrementalCubeSurfaceProvider : ISurfaceProvider
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

    /// <summary>
    /// Opposite direction index: dir 0 (+X) ↔ dir 1 (-X), etc.
    /// </summary>
    private static readonly int[] OppositeDir = { 1, 0, 3, 2, 5, 4 };

    private readonly List<SurfaceFace> _faces = new();
    private readonly List<NormalGroup> _groups = new();

    /// <summary>
    /// Maps (cell, direction index) to the index in <see cref="_faces"/>.
    /// The direction index identifies which of the 6 axis-aligned faces we mean.
    /// </summary>
    private readonly Dictionary<(Vector3I cell, int dir), int> _faceIndex = new();

    /// <summary>
    /// Reverse map: for each index in <see cref="_faces"/>, what is its key?
    /// Needed so that when we swap-remove we can update <see cref="_faceIndex"/>
    /// for the element that was moved.
    /// </summary>
    private readonly List<(Vector3I cell, int dir)> _faceKeys = new();

    private float _blockSize;

    public string Name => "Cube (incremental)";
    public IReadOnlyList<SurfaceFace> Faces => _faces;
    public IReadOnlyList<NormalGroup> NormalGroups => _groups;
    public int FaceCount => _faces.Count;
    public int GroupCount => _groups.Count;
    public float BlockSize => _blockSize;

    // ─── Full rebuild ─────────────────────────────────────────────

    public void Build(Grid grid, float blockSize)
    {
        _blockSize = blockSize;
        _faces.Clear();
        _faceIndex.Clear();
        _faceKeys.Clear();
        _groups.Clear();

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
                    AddFace(cell, d, new SurfaceFace(pos, DirNormals[d], faceArea));
                }
            }
        }

        RebuildGroups();
    }

    // ─── Unified block change event (mirrors SE2 BlocksChangedSignal) ──

    public void OnBlocksChanged(Grid grid, in BlocksChangedArgs args)
    {
        if (args.AllBlocksRemoved)
        {
            _faces.Clear();
            _faceKeys.Clear();
            _faceIndex.Clear();
            _groups.Clear();
            return;
        }

        // Process removes first (blocks already gone from grid)
        if (args.RemovedBlocks.Count > 0)
            ProcessRemovals(grid, args.RemovedBlocks);

        // Then adds (blocks already in grid)
        if (args.AddedBlocks.Count > 0)
            ProcessAdds(grid, args.AddedBlocks);

        RebuildGroups();
    }

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
                        var pos = CellFaceCenter(cell, DirOffsets[d], _blockSize);
                        AddFace(cell, d, new SurfaceFace(pos, DirNormals[d], faceArea));
                    }
                    else
                    {
                        int oppositeD = OppositeDir[d];
                        RemoveFace(neighborCell, oppositeD);
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
                for (int d = 0; d < 6; d++)
                    RemoveFace(cell, d);

                for (int d = 0; d < 6; d++)
                {
                    var neighborCell = cell + DirOffsets[d];

                    if (grid.GetBlockAt(neighborCell) != null)
                    {
                        int oppositeD = OppositeDir[d];
                        var pos = CellFaceCenter(neighborCell, DirOffsets[oppositeD], _blockSize);
                        AddFace(neighborCell, oppositeD,
                            new SurfaceFace(pos, DirNormals[oppositeD], faceArea));
                    }
                }
            }
        }
    }

    // ─── Grid split: treat departed blocks as removes ──────────

    public void OnGridSplit(Grid grid, in GridSplitArgs args)
    {
        // SE2 fires BeforeSplitGridSignal BEFORE blocks are removed.
        // After this, BlocksChangedSignal fires with RemovedBlocks.
        // We handle it here incrementally using the departed block list.
        var departed = args.AllDepartedBlocks.ToList();
        if (departed.Count > 0)
            OnBlocksChanged(grid, new BlocksChangedArgs(removed: departed));
        else
            Build(grid, _blockSize);
    }

    // ─── Face list management ─────────────────────────────────────

    /// <summary>
    /// Add a face to the dense list and update the index.
    /// If the face already exists (duplicate key), this is a no-op.
    /// </summary>
    private void AddFace((Vector3I cell, int dir) key, SurfaceFace face)
    {
        if (_faceIndex.ContainsKey(key))
            return; // already tracked

        int index = _faces.Count;
        _faces.Add(face);
        _faceKeys.Add(key);
        _faceIndex[key] = index;
    }

    private void AddFace(Vector3I cell, int dir, SurfaceFace face)
        => AddFace((cell, dir), face);

    /// <summary>
    /// Remove a face by its key. Uses swap-with-last for O(1) removal.
    /// No-op if the key is not present.
    /// </summary>
    private void RemoveFace(Vector3I cell, int dir)
    {
        var key = (cell, dir);
        if (!_faceIndex.TryGetValue(key, out int index))
            return;

        int lastIndex = _faces.Count - 1;

        if (index != lastIndex)
        {
            // Move the last element into the vacated slot
            _faces[index] = _faces[lastIndex];
            var movedKey = _faceKeys[lastIndex];
            _faceKeys[index] = movedKey;
            _faceIndex[movedKey] = index;
        }

        _faces.RemoveAt(lastIndex);
        _faceKeys.RemoveAt(lastIndex);
        _faceIndex.Remove(key);
    }

    // ─── Groups ───────────────────────────────────────────────────

    /// <summary>
    /// Rebuild <see cref="NormalGroups"/> from the current face list.
    /// Only 6 possible normals, so this is always fast.
    /// </summary>
    private void RebuildGroups()
    {
        _groups.Clear();

        var accum = new float[6];
        var weighted = new Vector3[6];
        var counts = new int[6];

        foreach (var face in _faces)
        {
            int d = NormalToDir(face.Normal);
            accum[d] += face.Area;
            weighted[d] += face.Position * face.Area;
            counts[d]++;
        }

        for (int d = 0; d < 6; d++)
        {
            if (counts[d] == 0) continue;
            _groups.Add(new NormalGroup(
                DirNormals[d],
                accum[d],
                weighted[d] / accum[d],
                counts[d]));
        }
    }

    /// <summary>
    /// Map an axis-aligned unit normal back to its direction index (0..5).
    /// </summary>
    private static int NormalToDir(Vector3 normal)
    {
        if (normal.X > 0.5f) return 0;
        if (normal.X < -0.5f) return 1;
        if (normal.Y > 0.5f) return 2;
        if (normal.Y < -0.5f) return 3;
        if (normal.Z > 0.5f) return 4;
        return 5;
    }

    // ─── Helpers ──────────────────────────────────────────────────

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
