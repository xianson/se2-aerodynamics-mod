#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// Surface provider that smooths face normals to approximate the intended surface
/// of a blocky cube grid, analogous to Phong shading in graphics.
///
/// Ported from Aero.Core.Aero.SmoothSurfaceProvider.
///
/// Algorithm:
///   1. Angle-weighted count² vertex normals
///   2. Hemisphere filtering (prevents opposing faces from cancelling)
///   3. Multi-ring Laplacian diffusion (extends smoothing radius)
///   4. Crease preservation (sharp edges preserved above threshold angle)
///   5. Incremental updates (only recomputes affected faces on block changes)
/// </summary>
public class SmoothSurfaceProvider : ISurfaceProvider
{
    private static readonly List<Vector3I> DirOffsets = new List<Vector3I>
    {
        new( 1, 0, 0), new(-1, 0, 0),
        new( 0, 1, 0), new( 0,-1, 0),
        new( 0, 0, 1), new( 0, 0,-1),
    };

    private static readonly List<Vector3> DirNormals = new List<Vector3>
    {
        Vector3.UnitX, -Vector3.UnitX,
        Vector3.UnitY, -Vector3.UnitY,
        Vector3.UnitZ, -Vector3.UnitZ,
    };

    private static readonly List<int> OppositeDir = new List<int> { 1, 0, 3, 2, 5, 4 };

    // ─── Persistent face data (swap-with-last) ─────────────────────
    private readonly List<SurfaceFace> _faces = new();
    private readonly List<NormalGroup> _groups = new();
    private readonly Dictionary<long, int> _faceIndex = new();
    private readonly List<long> _rawFaceKeys = new();
    private readonly List<int> _rawFaceDirs = new();

    // ─── Vertex data ───────────────────────────────────────────────
    private readonly Dictionary<long, List<float>> _vertexDirWeights = new();
    private readonly Dictionary<long, HashSet<int>> _vertexToFaceIndex = new();

    // ─── Adjacency & crease data ───────────────────────────────────
    private Dictionary<long, HashSet<long>> _vertexAdjacency;
    private Dictionary<long, int> _edgeRefCount;
    private HashSet<long> _creaseEdges;

    private float _blockSize;

    /// <summary>Smoothing strength: 0 = blocky, 1 = fully smoothed.</summary>
    public float Smoothing { get; set; } = 1.0f;

    /// <summary>Weight exponent for vertex direction weights. 2 = count² (default).</summary>
    public float WeightExponent { get; set; } = 2.0f;

    /// <summary>Number of neighborhood rings for Laplacian diffusion.</summary>
    public int SmoothingRings { get; set; } = 2;

    /// <summary>Dihedral angle threshold (degrees) for crease edges. 90 = preserve right angles.</summary>
    public float CreaseAngleDegrees { get; set; } = 90f;

    public string Name => "Smooth";
    public IReadOnlyList<SurfaceFace> Faces => _faces;
    public IReadOnlyList<NormalGroup> NormalGroups => _groups;
    public int FaceCount => _faces.Count;
    public int GroupCount => _groups.Count;
    public float BlockSize => _blockSize;
    public int Version { get; private set; }

    /// <summary>True while a staggered build is in progress (between BeginBuild and FinalizeBuild).</summary>
    public bool IsBuilding => _stagingCells != null;

    // ─── Staggered build state ──────────────────────────────────
    private List<Vector3I> _stagingCells;
    private int _stagingOffset;
    private IGridAccessor _stagingGrid;

    // ─── Full rebuild ─────────────────────────────────────────────

    public void Build(IGridAccessor grid, float blockSize)
    {
        _blockSize = blockSize;
        Clear();

        // Phase 1: Find all exposed faces
        foreach (var cell in grid.EnumerateOccupiedCells())
        {
            for (int d = 0; d < 6; d++)
            {
                if (!grid.IsCellOccupied(cell + DirOffsets[d]))
                    AddRawFace(cell, d);
            }
        }

        // Phase 2: Build adjacency if multi-ring
        if (SmoothingRings > 1)
            BuildAdjacency();

        // Phase 2b: Detect creases
        if (CreaseAngleDegrees < 180f)
            DetectAllCreases();

        // Phase 3: Compute smoothed normals
        RecomputeAllFaceNormals();

        // Phase 4: Build groups
        RebuildGroups();
        Version++;
    }

    // ─── Staggered full rebuild (spread across ticks) ───────────

    /// <summary>
    /// Begin a staggered rebuild. Materializes all occupied cells into a buffer
    /// but does NOT clear the active face data — old faces remain usable for
    /// force computation until FinalizeBuild() is called.
    /// </summary>
    public void BeginBuild(IGridAccessor grid, float blockSize)
    {
        _blockSize = blockSize;

        // Materialize all cells up front (one octree pass, relatively cheap)
        _stagingCells = new List<Vector3I>();
        foreach (var cell in grid.EnumerateOccupiedCells())
            _stagingCells.Add(cell);
        _stagingOffset = 0;
        _stagingGrid = grid;

        // Clear internal structures but NOT _faces — those are still serving force computation
        _rawFaceKeys.Clear();
        _rawFaceDirs.Clear();
        _faceIndex.Clear();
        _vertexDirWeights.Clear();
        _vertexToFaceIndex.Clear();
        _vertexAdjacency = null;
        _edgeRefCount = null;
        _creaseEdges = null;
    }

    /// <summary>
    /// Process up to <paramref name="maxCells"/> cells from the staging buffer.
    /// Returns true when all cells have been processed (ready for FinalizeBuild).
    /// </summary>
    public bool AddCellBatch(int maxCells)
    {
        if (_stagingCells == null || _stagingGrid == null)
            return true;

        int end = Math.Min(_stagingOffset + maxCells, _stagingCells.Count);
        for (int i = _stagingOffset; i < end; i++)
        {
            var cell = _stagingCells[i];
            for (int d = 0; d < 6; d++)
            {
                if (!_stagingGrid.IsCellOccupied(cell + DirOffsets[d]))
                    AddRawFace(cell, d);
            }
        }
        _stagingOffset = end;
        return _stagingOffset >= _stagingCells.Count;
    }

    /// <summary>
    /// Finalize the staggered build: adjacency, creases, normals, groups.
    /// After this call, _faces is overwritten with the new surface data.
    /// </summary>
    public void FinalizeBuild()
    {
        _stagingCells = null;
        _stagingGrid = null;

        if (SmoothingRings > 1)
            BuildAdjacency();
        if (CreaseAngleDegrees < 180f)
            DetectAllCreases();
        RecomputeAllFaceNormals();
        RebuildGroups();
        Version++;
    }

    /// <summary>Abort a staggered build in progress (e.g., grid topology changed mid-build).</summary>
    public void AbortBuild()
    {
        _stagingCells = null;
        _stagingGrid = null;
    }

    // ─── Incremental updates ─────────────────────────────────────

    public void OnBlocksChanged(IGridAccessor grid, in BlocksChangedArgs args)
    {
        if (args.AllBlocksRemoved)
        {
            Clear();
            return;
        }

        // If too many changes or first build, do full rebuild
        int changedCells = args.AddedCells.Count + args.RemovedCells.Count;
        if (_rawFaceKeys.Count == 0 || changedCells > _rawFaceKeys.Count / 5)
        {
            Build(grid, _blockSize);
            return;
        }

        var dirtyVertices = new HashSet<long>();

        // Process removals
        for (int ri = 0; ri < args.RemovedCells.Count; ri++)
        {
            var cell = args.RemovedCells[ri];
            // Remove all faces of this cell
            for (int d = 0; d < 6; d++)
            {
                long key = PackCellDir(cell, d);
                if (_faceIndex.ContainsKey(key))
                {
                    for (int v = 0; v < 4; v++)
                        dirtyVertices.Add(PackVertex(cell, d, v));
                    RemoveRawFace(cell, d);
                }
            }
            // Expose neighbor faces
            for (int d = 0; d < 6; d++)
            {
                var neighborCell = cell + DirOffsets[d];
                if (grid.IsCellOccupied(neighborCell))
                {
                    int oppositeD = OppositeDir[d];
                    AddRawFace(neighborCell, oppositeD);
                    for (int v = 0; v < 4; v++)
                        dirtyVertices.Add(PackVertex(neighborCell, oppositeD, v));
                }
            }
        }

        // Process additions
        for (int ai = 0; ai < args.AddedCells.Count; ai++)
        {
            var cell = args.AddedCells[ai];
            for (int d = 0; d < 6; d++)
            {
                var neighborCell = cell + DirOffsets[d];
                if (!grid.IsCellOccupied(neighborCell))
                {
                    AddRawFace(cell, d);
                    for (int v = 0; v < 4; v++)
                        dirtyVertices.Add(PackVertex(cell, d, v));
                }
                else
                {
                    int oppositeD = OppositeDir[d];
                    long nkey = PackCellDir(neighborCell, oppositeD);
                    if (_faceIndex.ContainsKey(nkey))
                    {
                        for (int v = 0; v < 4; v++)
                            dirtyVertices.Add(PackVertex(neighborCell, oppositeD, v));
                        RemoveRawFace(neighborCell, oppositeD);
                    }
                }
            }
        }

        // Expand dirty set for multi-ring diffusion
        if (SmoothingRings > 1 && _vertexAdjacency != null)
            dirtyVertices = ExpandDirtyVertices(dirtyVertices, SmoothingRings - 1);

        RecomputeDirtyFaces(dirtyVertices);
        RebuildGroups();
        Version++;
    }

    // ─── Raw face management (swap-with-last) ─────────────────────

    private static long PackCellDir(Vector3I cell, int dir)
    {
        return ((long)(cell.X & 0xFFFF) << 36)
             | ((long)(cell.Y & 0xFFFF) << 20)
             | ((long)(cell.Z & 0xFFFF) << 4)
             | (long)(dir & 0xF);
    }

    private static Vector3I UnpackCell(long key)
    {
        int cx = (int)((key >> 36) & 0xFFFF);
        int cy = (int)((key >> 20) & 0xFFFF);
        int cz = (int)((key >> 4) & 0xFFFF);
        if (cx >= 0x8000) cx -= 0x10000;
        if (cy >= 0x8000) cy -= 0x10000;
        if (cz >= 0x8000) cz -= 0x10000;
        return new Vector3I(cx, cy, cz);
    }

    private void AddRawFace(Vector3I cell, int dir)
    {
        long key = PackCellDir(cell, dir);
        if (_faceIndex.ContainsKey(key))
            return;

        int index = _rawFaceKeys.Count;
        _rawFaceKeys.Add(key);
        _rawFaceDirs.Add(dir);
        _faceIndex[key] = index;

        for (int v = 0; v < 4; v++)
        {
            long vkey = PackVertex(cell, dir, v);
            if (!_vertexDirWeights.TryGetValue(vkey, out var weights))
            {
                weights = new List<float> { 0, 0, 0, 0, 0, 0 };
                _vertexDirWeights[vkey] = weights;
            }
            weights[dir] = weights[dir] + MathF.PI / 2f;

            if (!_vertexToFaceIndex.TryGetValue(vkey, out var faceSet))
            {
                faceSet = new HashSet<int>();
                _vertexToFaceIndex[vkey] = faceSet;
            }
            faceSet.Add(index);
        }

        if (_vertexAdjacency != null)
            AddFaceEdges(cell, dir);
    }

    private void RemoveRawFace(Vector3I cell, int dir)
    {
        long key = PackCellDir(cell, dir);
        if (!_faceIndex.TryGetValue(key, out int index))
            return;

        if (_vertexAdjacency != null)
            RemoveFaceEdges(cell, dir);

        for (int v = 0; v < 4; v++)
        {
            long vkey = PackVertex(cell, dir, v);
            if (_vertexDirWeights.TryGetValue(vkey, out var weights))
            {
                weights[dir] = weights[dir] - MathF.PI / 2f;
                if (weights[dir] < 1e-6f) weights[dir] = 0f;
            }
            if (_vertexToFaceIndex.TryGetValue(vkey, out var faceSet))
            {
                faceSet.Remove(index);
                if (faceSet.Count == 0)
                {
                    _vertexToFaceIndex.Remove(vkey);
                    _vertexDirWeights.Remove(vkey);
                }
            }
        }

        // Swap-with-last removal
        int lastIndex = _rawFaceKeys.Count - 1;
        if (index != lastIndex)
        {
            var movedKey = _rawFaceKeys[lastIndex];
            int movedDir = _rawFaceDirs[lastIndex];
            _rawFaceKeys[index] = movedKey;
            _rawFaceDirs[index] = movedDir;
            _faceIndex[movedKey] = index;

            var movedCell = UnpackCell(movedKey);
            for (int v = 0; v < 4; v++)
            {
                long vkey = PackVertex(movedCell, movedDir, v);
                if (_vertexToFaceIndex.TryGetValue(vkey, out var faceSet))
                {
                    faceSet.Remove(lastIndex);
                    faceSet.Add(index);
                }
            }
        }

        _rawFaceKeys.RemoveAt(lastIndex);
        _rawFaceDirs.RemoveAt(lastIndex);
        _faceIndex.Remove(key);
    }

    // ─── Normal computation ────────────────────────────────────────

    private void RecomputeAllFaceNormals()
    {
        const float CellSize = 0.25f;
        float faceArea = CellSize * CellSize;

        Dictionary<long, Vector3> vertexNormals = null;
        if (SmoothingRings > 1)
            vertexNormals = ComputeDiffusedVertexNormals();

        _faces.Clear();

        for (int i = 0; i < _rawFaceKeys.Count; i++)
        {
            var cell = UnpackCell(_rawFaceKeys[i]);
            int dir = _rawFaceDirs[i];
            _faces.Add(ComputeFaceNormal(cell, dir, faceArea, vertexNormals));
        }
    }

    private void RecomputeDirtyFaces(HashSet<long> dirtyVertices)
    {
        if (dirtyVertices.Count == 0) return;

        const float CellSize = 0.25f;
        float faceArea = CellSize * CellSize;

        var dirtyFaceIndices = new HashSet<int>();
        foreach (long vkey in dirtyVertices)
        {
            if (_vertexToFaceIndex.TryGetValue(vkey, out var faceSet))
            {
                foreach (int fi in faceSet)
                    dirtyFaceIndices.Add(fi);
            }
        }

        Dictionary<long, Vector3> vertexNormals = null;
        if (SmoothingRings > 1)
            vertexNormals = ComputeDiffusedVertexNormals();

        // Sync _faces size
        while (_faces.Count < _rawFaceKeys.Count)
            _faces.Add(default);
        while (_faces.Count > _rawFaceKeys.Count)
            _faces.RemoveAt(_faces.Count - 1);

        for (int i = 0; i < _rawFaceKeys.Count; i++)
        {
            if (!dirtyFaceIndices.Contains(i))
                continue;
            var cell = UnpackCell(_rawFaceKeys[i]);
            int dir = _rawFaceDirs[i];
            _faces[i] = ComputeFaceNormal(cell, dir, faceArea, vertexNormals);
        }
    }

    private SurfaceFace ComputeFaceNormal(Vector3I cell, int dir, float faceArea,
        Dictionary<long, Vector3> diffusedVertexNormals)
    {
        Vector3 originalNormal = DirNormals[dir];
        Vector3 pos = CellFaceCenter(cell, DirOffsets[dir]);

        Vector3 smoothNormal = Vector3.Zero;
        for (int v = 0; v < 4; v++)
        {
            long vkey = PackVertex(cell, dir, v);

            Vector3 vertexN;
            if (diffusedVertexNormals != null && diffusedVertexNormals.TryGetValue(vkey, out var diffused))
            {
                vertexN = Vector3.Dot(originalNormal, diffused) >= 0 ? diffused : Vector3.Zero;
            }
            else
            {
                if (!_vertexDirWeights.TryGetValue(vkey, out var vw))
                    continue;

                vertexN = Vector3.Zero;
                for (int d = 0; d < 6; d++)
                {
                    if (vw[d] > 0 && Vector3.Dot(originalNormal, DirNormals[d]) >= 0)
                        vertexN = vertexN + DirNormals[d] * ApplyWeight(vw[d]);
                }
            }

            float vLen = vertexN.Length();
            if (vLen > 1e-6f)
                smoothNormal = smoothNormal + vertexN / vLen;
        }

        float sLen = smoothNormal.Length();
        if (sLen > 1e-6f)
            smoothNormal = smoothNormal / sLen;
        else
            smoothNormal = originalNormal;

        Vector3 finalNormal;
        if (Smoothing >= 1f)
            finalNormal = smoothNormal;
        else if (Smoothing <= 0f)
            finalNormal = originalNormal;
        else
            finalNormal = Vector3.Normalize(
                originalNormal * (1f - Smoothing) + smoothNormal * Smoothing);

        return new SurfaceFace(pos, finalNormal, faceArea);
    }

    private float ApplyWeight(float w)
    {
        if (WeightExponent == 2f) return w * w;
        if (WeightExponent == 1f) return w;
        return MathF.Pow(w, WeightExponent);
    }

    // ─── Multi-ring Laplacian diffusion ────────────────────────────

    private Dictionary<long, Vector3> ComputeDiffusedVertexNormals()
    {
        var vertexNormals = new Dictionary<long, Vector3>(_vertexDirWeights.Count);
        foreach (var kvp in _vertexDirWeights)
        {
            var vw = kvp.Value;
            Vector3 n = Vector3.Zero;
            for (int d = 0; d < 6; d++)
            {
                if (vw[d] > 0)
                    n = n + DirNormals[d] * ApplyWeight(vw[d]);
            }
            float len = n.Length();
            vertexNormals[kvp.Key] = len > 1e-6f ? n / len : Vector3.Zero;
        }

        if (_vertexAdjacency == null || SmoothingRings <= 1)
            return vertexNormals;

        for (int ring = 1; ring < SmoothingRings; ring++)
        {
            var newNormals = new Dictionary<long, Vector3>(vertexNormals.Count);
            foreach (var kvp in vertexNormals)
            {
                var key = kvp.Key;
                var normal = kvp.Value;
                Vector3 sum = normal;
                int count = 1;

                if (_vertexAdjacency.TryGetValue(key, out var neighbors))
                {
                    foreach (long neighbor in neighbors)
                    {
                        if (_creaseEdges != null)
                        {
                            long edgeKey = key < neighbor
                                ? PackEdge(key, neighbor)
                                : PackEdge(neighbor, key);
                            if (_creaseEdges.Contains(edgeKey)) continue;
                        }

                        if (vertexNormals.TryGetValue(neighbor, out var neighborNormal))
                        {
                            sum = sum + neighborNormal;
                            count++;
                        }
                    }
                }

                float len = sum.Length();
                newNormals[key] = len > 1e-6f ? sum / len : normal;
            }
            vertexNormals = newNormals;
        }

        return vertexNormals;
    }

    // ─── Adjacency graph ──────────────────────────────────────────

    private void BuildAdjacency()
    {
        _vertexAdjacency = new Dictionary<long, HashSet<long>>();
        _edgeRefCount = new Dictionary<long, int>();

        for (int i = 0; i < _rawFaceKeys.Count; i++)
        {
            var cell = UnpackCell(_rawFaceKeys[i]);
            AddFaceEdges(cell, _rawFaceDirs[i]);
        }
    }

    private void AddFaceEdges(Vector3I cell, int dir)
    {
        if (_vertexAdjacency == null || _edgeRefCount == null) return;

        long v0 = PackVertex(cell, dir, 0);
        long v1 = PackVertex(cell, dir, 1);
        long v2 = PackVertex(cell, dir, 2);
        long v3 = PackVertex(cell, dir, 3);

        AddEdge(v0, v1); AddEdge(v1, v3); AddEdge(v3, v2); AddEdge(v2, v0);
    }

    private void RemoveFaceEdges(Vector3I cell, int dir)
    {
        if (_vertexAdjacency == null || _edgeRefCount == null) return;

        long v0 = PackVertex(cell, dir, 0);
        long v1 = PackVertex(cell, dir, 1);
        long v2 = PackVertex(cell, dir, 2);
        long v3 = PackVertex(cell, dir, 3);

        RemoveEdge(v0, v1); RemoveEdge(v1, v3); RemoveEdge(v3, v2); RemoveEdge(v2, v0);
    }

    private static long PackEdge(long a, long b)
    {
        return a ^ (b * unchecked((long)0x9E3779B97F4A7C15UL));
    }

    private void AddEdge(long a, long b)
    {
        long canonical = a < b ? PackEdge(a, b) : PackEdge(b, a);
        _edgeRefCount.TryGetValue(canonical, out int count);
        _edgeRefCount[canonical] = count + 1;

        if (!_vertexAdjacency.TryGetValue(a, out var setA))
        {
            setA = new HashSet<long>();
            _vertexAdjacency[a] = setA;
        }
        setA.Add(b);

        if (!_vertexAdjacency.TryGetValue(b, out var setB))
        {
            setB = new HashSet<long>();
            _vertexAdjacency[b] = setB;
        }
        setB.Add(a);
    }

    private void RemoveEdge(long a, long b)
    {
        long canonical = a < b ? PackEdge(a, b) : PackEdge(b, a);
        if (!_edgeRefCount.TryGetValue(canonical, out int count))
            return;

        if (count <= 1)
        {
            _edgeRefCount.Remove(canonical);
            if (_vertexAdjacency.TryGetValue(a, out var setA))
            {
                setA.Remove(b);
                if (setA.Count == 0) _vertexAdjacency.Remove(a);
            }
            if (_vertexAdjacency.TryGetValue(b, out var setB))
            {
                setB.Remove(a);
                if (setB.Count == 0) _vertexAdjacency.Remove(b);
            }
            _creaseEdges?.Remove(canonical);
        }
        else
        {
            _edgeRefCount[canonical] = count - 1;
        }
    }

    // ─── Crease detection ─────────────────────────────────────────

    private void DetectAllCreases()
    {
        _creaseEdges = new HashSet<long>();
        if (_edgeRefCount == null) return;

        float cosThreshold = MathF.Cos(CreaseAngleDegrees * MathF.PI / 180f);

        foreach (var kvp in _edgeRefCount)
        {
            // We need to check all edges, but we only stored packed keys
            // For crease detection, we check via vertex-face adjacency
        }

        // Walk all face pairs sharing edges via vertex adjacency
        if (_vertexAdjacency == null) return;

        var checkedEdges = new HashSet<long>();
        foreach (var kvp in _vertexAdjacency)
        {
            long vA = kvp.Key;
            foreach (long vB in kvp.Value)
            {
                long edgeKey = vA < vB ? PackEdge(vA, vB) : PackEdge(vB, vA);
                if (!checkedEdges.Add(edgeKey)) continue;

                if (IsCreaseEdge(vA, vB, cosThreshold))
                    _creaseEdges.Add(edgeKey);
            }
        }
    }

    private bool IsCreaseEdge(long va, long vb, float cosThreshold)
    {
        if (!_vertexToFaceIndex.TryGetValue(va, out var facesA) ||
            !_vertexToFaceIndex.TryGetValue(vb, out var facesB))
            return false;

        Vector3 normal1 = default;
        Vector3 normal2 = default;
        int found = 0;

        foreach (int fi in facesA)
        {
            if (!facesB.Contains(fi)) continue;
            if (fi >= _rawFaceDirs.Count) continue;

            int dir = _rawFaceDirs[fi];
            if (found == 0) { normal1 = DirNormals[dir]; found = 1; }
            else if (found == 1) { normal2 = DirNormals[dir]; found = 2; break; }
        }

        if (found < 2) return false;
        return Vector3.Dot(normal1, normal2) <= cosThreshold;
    }

    // ─── Dirty vertex expansion ──────────────────────────────────

    private HashSet<long> ExpandDirtyVertices(HashSet<long> dirty, int hops)
    {
        if (_vertexAdjacency == null || hops <= 0) return dirty;

        var expanded = new HashSet<long>(dirty);
        var frontier = new HashSet<long>(dirty);

        for (int h = 0; h < hops; h++)
        {
            var nextFrontier = new HashSet<long>();
            foreach (long v in frontier)
            {
                if (!_vertexAdjacency.TryGetValue(v, out var neighbors)) continue;
                foreach (long n in neighbors)
                {
                    if (expanded.Add(n))
                        nextFrontier.Add(n);
                }
            }
            frontier = nextFrontier;
            if (frontier.Count == 0) break;
        }

        return expanded;
    }

    // ─── Groups ───────────────────────────────────────────────────

    private void RebuildGroups()
    {
        _groups.Clear();

        var groupArea = new List<float> { 0, 0, 0, 0, 0, 0 };
        var groupWeightedPos = new List<Vector3>
        {
            Vector3.Zero, Vector3.Zero, Vector3.Zero,
            Vector3.Zero, Vector3.Zero, Vector3.Zero
        };
        var groupNormalSum = new List<Vector3>
        {
            Vector3.Zero, Vector3.Zero, Vector3.Zero,
            Vector3.Zero, Vector3.Zero, Vector3.Zero
        };
        var groupCount = new List<int> { 0, 0, 0, 0, 0, 0 };
        var rSxx = new List<float> { 0, 0, 0, 0, 0, 0 };
        var rSyy = new List<float> { 0, 0, 0, 0, 0, 0 };
        var rSzz = new List<float> { 0, 0, 0, 0, 0, 0 };
        var rSxy = new List<float> { 0, 0, 0, 0, 0, 0 };
        var rSxz = new List<float> { 0, 0, 0, 0, 0, 0 };
        var rSyz = new List<float> { 0, 0, 0, 0, 0, 0 };

        for (int i = 0; i < _faces.Count; i++)
        {
            var face = _faces[i];
            int dir = _rawFaceDirs[i];

            groupArea[dir] = groupArea[dir] + face.Area;
            groupWeightedPos[dir] = groupWeightedPos[dir] + face.Position * face.Area;
            groupNormalSum[dir] = groupNormalSum[dir] + face.Normal;
            groupCount[dir] = groupCount[dir] + 1;
            rSxx[dir] = rSxx[dir] + face.Area * face.Position.X * face.Position.X;
            rSyy[dir] = rSyy[dir] + face.Area * face.Position.Y * face.Position.Y;
            rSzz[dir] = rSzz[dir] + face.Area * face.Position.Z * face.Position.Z;
            rSxy[dir] = rSxy[dir] + face.Area * face.Position.X * face.Position.Y;
            rSxz[dir] = rSxz[dir] + face.Area * face.Position.X * face.Position.Z;
            rSyz[dir] = rSyz[dir] + face.Area * face.Position.Y * face.Position.Z;
        }

        for (int d = 0; d < 6; d++)
        {
            if (groupCount[d] == 0) continue;

            float a = groupArea[d];
            var centroid = groupWeightedPos[d] / a;

            Vector3 avgNormal = groupNormalSum[d];
            float avgLen = avgNormal.Length();
            Vector3 groupNormal = avgLen > 1e-6f ? avgNormal / avgLen : DirNormals[d];

            float sxx = rSxx[d] - a * centroid.X * centroid.X;
            float syy = rSyy[d] - a * centroid.Y * centroid.Y;
            float szz = rSzz[d] - a * centroid.Z * centroid.Z;
            float sxy = rSxy[d] - a * centroid.X * centroid.Y;
            float sxz = rSxz[d] - a * centroid.X * centroid.Z;
            float syz = rSyz[d] - a * centroid.Y * centroid.Z;

            _groups.Add(new NormalGroup(
                groupNormal, a, centroid, groupCount[d],
                sxx, syy, szz, sxy, sxz, syz));
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────

    private void Clear()
    {
        _faces.Clear();
        _groups.Clear();
        _rawFaceKeys.Clear();
        _rawFaceDirs.Clear();
        _faceIndex.Clear();
        _vertexDirWeights.Clear();
        _vertexToFaceIndex.Clear();
        _vertexAdjacency = null;
        _edgeRefCount = null;
        _creaseEdges = null;
    }

    private static Vector3I GetFaceVertex(Vector3I cell, int dir, int vertexIndex)
    {
        int a = vertexIndex & 1;
        int b = (vertexIndex >> 1) & 1;

        if (dir == 0) return new Vector3I(cell.X + 1, cell.Y + a, cell.Z + b);
        if (dir == 1) return new Vector3I(cell.X,     cell.Y + a, cell.Z + b);
        if (dir == 2) return new Vector3I(cell.X + a, cell.Y + 1, cell.Z + b);
        if (dir == 3) return new Vector3I(cell.X + a, cell.Y,     cell.Z + b);
        if (dir == 4) return new Vector3I(cell.X + a, cell.Y + b, cell.Z + 1);
        return          new Vector3I(cell.X + a, cell.Y + b, cell.Z);
    }

    private static long PackVertex(Vector3I cell, int dir, int vertexIndex)
    {
        var v = GetFaceVertex(cell, dir, vertexIndex);
        return ((long)(v.X & 0xFFFFF) << 40)
             | ((long)(v.Y & 0xFFFFF) << 20)
             | (long)(v.Z & 0xFFFFF);
    }

    private static Vector3 CellFaceCenter(Vector3I cell, Vector3I faceDir)
    {
        const float CellSize = 0.25f;
        const float HalfCell = 0.125f;
        return new Vector3(
            cell.X * CellSize + faceDir.X * HalfCell,
            cell.Y * CellSize + faceDir.Y * HalfCell,
            cell.Z * CellSize + faceDir.Z * HalfCell);
    }
}
