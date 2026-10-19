using System.Numerics;
using Aero.Core.Grids;
using Aero.Core.Math;

namespace Aero.Core.Aero;

/// <summary>
/// Surface provider that smooths face normals to approximate the intended surface
/// of a blocky cube grid, analogous to Phong shading in graphics.
///
/// The algorithm uses vertex normal averaging with several improvements:
///
///   1. <b>Angle-weighted count² weighting</b> — each direction's contribution at a vertex is
///      weighted by the subtended angle sum raised to <see cref="WeightExponent"/>.
///      For cubes all angles are π/2, so this reduces to count-based weighting.
///      Infrastructure ready for slope blocks with varying subtended angles.
///
///   2. <b>Hemisphere filtering</b> — when computing the smoothed normal for a face,
///      only vertex contributions from compatible directions (dot ≥ 0 with the face's
///      original normal) are included. This prevents opposing faces (+Y/-Y on a thin
///      plate) from cancelling each other.
///
///   3. <b>Multi-ring Laplacian diffusion</b> — vertex normals can be diffused across
///      multiple neighborhood rings via <see cref="SmoothingRings"/>, extending the
///      smoothing radius beyond the immediate 1-ring neighborhood.
///
///   4. <b>Crease preservation</b> — edges where the dihedral angle between adjacent faces
///      exceeds <see cref="CreaseAngleDegrees"/> are marked as creases. Diffusion does not
///      cross crease edges, preserving sharp features like wing trailing edges.
///
///   5. <b>Incremental updates</b> — block add/remove only recomputes affected faces and
///      their neighborhood, rather than rebuilding the entire surface.
/// </summary>
public class SmoothSurfaceProvider : ISurfaceProvider
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

    // ─── Persistent face data (swap-with-last pattern) ─────────────
    private readonly List<SurfaceFace> _faces = new();
    private readonly List<NormalGroup> _groups = new();
    private readonly Dictionary<(Vector3I cell, int dir), int> _faceIndex = new();
    private readonly List<(Vector3I cell, int dir)> _faceKeys = new();
    private readonly List<(Vector3I cell, int dir)> _rawFaces = new();
    private readonly List<(Vector3I cell, int dir)> _rawFaceKeys = new();

    // ─── Vertex data ───────────────────────────────────────────────
    private readonly Dictionary<long, float[]> _vertexDirWeights = new();
    private readonly Dictionary<long, HashSet<int>> _vertexToFaceIndex = new();

    // ─── Adjacency & crease data ───────────────────────────────────
    private Dictionary<long, HashSet<long>>? _vertexAdjacency;
    private Dictionary<(long, long), int>? _edgeRefCount;
    private HashSet<(long, long)>? _creaseEdges;

    private float _blockSize;

    /// <summary>
    /// Smoothing strength: 0 = original blocky normals, 1 = fully smoothed.
    /// </summary>
    public float Smoothing { get; set; } = 1.0f;

    /// <summary>
    /// Weight exponent for vertex direction weights. 2 = count² (default), 1 = linear.
    /// </summary>
    public float WeightExponent { get; set; } = 2.0f;

    /// <summary>
    /// Number of neighborhood rings for Laplacian diffusion. 1 = immediate vertices only (default).
    /// Higher values produce smoother results across larger regions.
    /// </summary>
    public int SmoothingRings { get; set; } = 1;

    /// <summary>
    /// Dihedral angle threshold (degrees) above which an edge is marked as a crease.
    /// Diffusion does not cross crease edges. 90 = preserve right-angle edges (default).
    /// Set to 180 to disable crease detection.
    /// </summary>
    public float CreaseAngleDegrees { get; set; } = 90f;

    public string Name
    {
        get
        {
            var parts = new List<string>();
            if (Smoothing < 1f) parts.Add($"s={Smoothing:F1}");
            if (SmoothingRings > 1) parts.Add($"r={SmoothingRings}");
            if (CreaseAngleDegrees < 180f) parts.Add($"c={CreaseAngleDegrees:F0}°");
            return parts.Count > 0 ? $"Smooth ({string.Join(", ", parts)})" : "Smooth";
        }
    }

    public IReadOnlyList<SurfaceFace> Faces => _faces;
    public IReadOnlyList<NormalGroup> NormalGroups => _groups;
    public int FaceCount => _faces.Count;
    public int GroupCount => _groups.Count;
    public float BlockSize => _blockSize;

    // ─── Full rebuild ─────────────────────────────────────────────

    public void Build(Grid grid, float blockSize)
    {
        _blockSize = blockSize;
        Clear();

        // Phase 1: Find all exposed faces → populate _rawFaces
        foreach (var block in grid.Blocks.Values)
        {
            foreach (var cell in block.EnumerateCells())
            {
                for (int d = 0; d < 6; d++)
                {
                    if (grid.GetBlockAt(cell + DirOffsets[d]) == null)
                        AddRawFace(cell, d);
                }
            }
        }

        // Phase 2.5: Build adjacency if multi-ring
        if (SmoothingRings > 1)
            BuildAdjacency();

        // Phase 2.5b: Detect creases if enabled
        if (CreaseAngleDegrees < 180f)
            DetectAllCreases();

        // Phase 3+4: Compute smoothed normals for all faces
        RecomputeAllFaceNormals();

        // Phase 5: Build groups
        RebuildGroups();
    }

    // ─── Incremental updates ─────────────────────────────────────

    public void OnBlocksChanged(Grid grid, in BlocksChangedArgs args)
    {
        if (args.AllBlocksRemoved) { Clear(); return; }

        int changedCells = CountCells(args.AddedBlocks) + CountCells(args.RemovedBlocks);
        if (_rawFaces.Count == 0 || changedCells > _rawFaces.Count / 5)
        {
            Build(grid, _blockSize);
            return;
        }

        var dirtyVertices = new HashSet<long>();
        if (args.RemovedBlocks.Count > 0) ProcessRemovals(grid, args.RemovedBlocks, dirtyVertices);
        if (args.AddedBlocks.Count > 0) ProcessAdds(grid, args.AddedBlocks, dirtyVertices);

        // Rebuild adjacency/creases if needed (incremental maintenance handles most,
        // but crease detection needs face normals which may have changed)
        if (CreaseAngleDegrees < 180f)
            RedetectCreasesForDirtyVertices(dirtyVertices);

        // Expand dirty set for multi-ring diffusion
        if (SmoothingRings > 1 && _vertexAdjacency != null)
            dirtyVertices = ExpandDirtyVertices(dirtyVertices, SmoothingRings - 1);

        RecomputeDirtyFaces(dirtyVertices);
        RebuildGroups();
    }

    public void OnGridSplit(Grid grid, in GridSplitArgs args)
    {
        var departed = args.AllDepartedBlocks.ToList();
        if (departed.Count > 0)
            OnBlocksChanged(grid, new BlocksChangedArgs(removed: departed));
        else
            Build(grid, _blockSize);
    }

    // ─── Raw face management (swap-with-last) ─────────────────────

    private void AddRawFace(Vector3I cell, int dir)
    {
        var key = (cell, dir);
        if (_faceIndex.ContainsKey(key))
            return;

        int index = _rawFaces.Count;
        _rawFaces.Add(key);
        _rawFaceKeys.Add(key);
        _faceIndex[key] = index;

        // Update vertex weights
        for (int v = 0; v < 4; v++)
        {
            long vkey = PackVertex(cell, dir, v);
            if (!_vertexDirWeights.TryGetValue(vkey, out var weights))
            {
                weights = new float[6];
                _vertexDirWeights[vkey] = weights;
            }
            weights[dir] += SubtendedAngle(cell, dir, v);

            if (!_vertexToFaceIndex.TryGetValue(vkey, out var faceSet))
            {
                faceSet = new HashSet<int>();
                _vertexToFaceIndex[vkey] = faceSet;
            }
            faceSet.Add(index);
        }

        // Update adjacency if active
        if (_vertexAdjacency != null)
            AddFaceEdges(cell, dir);
    }

    private void RemoveRawFace(Vector3I cell, int dir)
    {
        var key = (cell, dir);
        if (!_faceIndex.TryGetValue(key, out int index))
            return;

        // Remove adjacency edges before removing face
        if (_vertexAdjacency != null)
            RemoveFaceEdges(cell, dir);

        // Decrement vertex weights and remove from vertex→face map
        for (int v = 0; v < 4; v++)
        {
            long vkey = PackVertex(cell, dir, v);
            if (_vertexDirWeights.TryGetValue(vkey, out var weights))
            {
                weights[dir] -= SubtendedAngle(cell, dir, v);
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
        int lastIndex = _rawFaces.Count - 1;
        if (index != lastIndex)
        {
            var movedKey = _rawFaces[lastIndex];
            _rawFaces[index] = movedKey;
            _rawFaceKeys[index] = movedKey;
            _faceIndex[movedKey] = index;

            // Update vertex→face index for moved face
            var (movedCell, movedDir) = movedKey;
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

        _rawFaces.RemoveAt(lastIndex);
        _rawFaceKeys.RemoveAt(lastIndex);
        _faceIndex.Remove(key);
    }

    // ─── Incremental add/remove processing ────────────────────────

    private void ProcessAdds(Grid grid, IReadOnlyList<Block> added, HashSet<long> dirtyVertices)
    {
        foreach (var block in added)
        {
            foreach (var cell in block.EnumerateCells())
            {
                for (int d = 0; d < 6; d++)
                {
                    var neighborCell = cell + DirOffsets[d];

                    if (grid.GetBlockAt(neighborCell) == null)
                    {
                        // New exposed face
                        AddRawFace(cell, d);
                        for (int v = 0; v < 4; v++)
                            dirtyVertices.Add(PackVertex(cell, d, v));
                    }
                    else
                    {
                        // Neighbor's face toward us is now hidden
                        int oppositeD = OppositeDir[d];
                        if (_faceIndex.ContainsKey((neighborCell, oppositeD)))
                        {
                            for (int v = 0; v < 4; v++)
                                dirtyVertices.Add(PackVertex(neighborCell, oppositeD, v));
                            RemoveRawFace(neighborCell, oppositeD);
                        }
                    }
                }
            }
        }
    }

    private void ProcessRemovals(Grid grid, IReadOnlyList<Block> removed, HashSet<long> dirtyVertices)
    {
        foreach (var block in removed)
        {
            foreach (var cell in block.EnumerateCells())
            {
                // Remove all faces of this cell
                for (int d = 0; d < 6; d++)
                {
                    if (_faceIndex.ContainsKey((cell, d)))
                    {
                        for (int v = 0; v < 4; v++)
                            dirtyVertices.Add(PackVertex(cell, d, v));
                        RemoveRawFace(cell, d);
                    }
                }

                // Expose neighbor faces that were hidden
                for (int d = 0; d < 6; d++)
                {
                    var neighborCell = cell + DirOffsets[d];
                    if (grid.GetBlockAt(neighborCell) != null)
                    {
                        int oppositeD = OppositeDir[d];
                        AddRawFace(neighborCell, oppositeD);
                        for (int v = 0; v < 4; v++)
                            dirtyVertices.Add(PackVertex(neighborCell, oppositeD, v));
                    }
                }
            }
        }
    }

    // ─── Normal computation ────────────────────────────────────────

    private void RecomputeAllFaceNormals()
    {
        float faceArea = _blockSize * _blockSize;

        // Phase 3: Multi-ring diffusion (compute vertex normals)
        Dictionary<long, Vector3>? vertexNormals = null;
        if (SmoothingRings > 1)
            vertexNormals = ComputeDiffusedVertexNormals();

        // Phase 4: Compute smoothed face normals
        _faces.Clear();
        _faceKeys.Clear();

        for (int i = 0; i < _rawFaces.Count; i++)
        {
            var (cell, dir) = _rawFaces[i];
            var face = ComputeFaceNormal(cell, dir, faceArea, vertexNormals);
            _faces.Add(face);
            _faceKeys.Add((cell, dir));
        }
    }

    private void RecomputeDirtyFaces(HashSet<long> dirtyVertices)
    {
        if (dirtyVertices.Count == 0) return;

        float faceArea = _blockSize * _blockSize;

        // Find all faces touching dirty vertices
        var dirtyFaceIndices = new HashSet<int>();
        foreach (long vkey in dirtyVertices)
        {
            if (_vertexToFaceIndex.TryGetValue(vkey, out var faceSet))
            {
                foreach (int fi in faceSet)
                    dirtyFaceIndices.Add(fi);
            }
        }

        // Recompute diffused vertex normals if multi-ring
        Dictionary<long, Vector3>? vertexNormals = null;
        if (SmoothingRings > 1)
            vertexNormals = ComputeDiffusedVertexNormals();

        // Sync _faces list size with _rawFaces (faces may have been added/removed)
        while (_faces.Count < _rawFaces.Count)
        {
            _faces.Add(default);
            _faceKeys.Add(default);
        }
        while (_faces.Count > _rawFaces.Count)
        {
            _faces.RemoveAt(_faces.Count - 1);
            _faceKeys.RemoveAt(_faceKeys.Count - 1);
        }

        // Recompute all faces (dirty ones changed, but indices may have shifted due to swap)
        for (int i = 0; i < _rawFaces.Count; i++)
        {
            var (cell, dir) = _rawFaces[i];
            _faceKeys[i] = (cell, dir);
            if (dirtyFaceIndices.Contains(i) || i >= _faces.Count)
            {
                _faces[i] = ComputeFaceNormal(cell, dir, faceArea, vertexNormals);
            }
        }
    }

    private SurfaceFace ComputeFaceNormal(Vector3I cell, int dir, float faceArea,
        Dictionary<long, Vector3>? diffusedVertexNormals)
    {
        Vector3 originalNormal = DirNormals[dir];
        Vector3 pos = CellFaceCenter(cell, DirOffsets[dir], _blockSize);

        Vector3 smoothNormal = Vector3.Zero;
        for (int v = 0; v < 4; v++)
        {
            long key = PackVertex(cell, dir, v);

            Vector3 vertexN;
            if (diffusedVertexNormals != null && diffusedVertexNormals.TryGetValue(key, out var diffused))
            {
                // Use pre-diffused vertex normal, but still apply hemisphere filter
                vertexN = Vector3.Dot(originalNormal, diffused) >= 0 ? diffused : Vector3.Zero;
            }
            else
            {
                // Compute vertex normal from direction weights with hemisphere filter
                if (!_vertexDirWeights.TryGetValue(key, out float[]? vw))
                    continue;

                vertexN = Vector3.Zero;
                for (int d = 0; d < 6; d++)
                {
                    if (vw[d] > 0 && Vector3.Dot(originalNormal, DirNormals[d]) >= 0)
                        vertexN += DirNormals[d] * ApplyWeight(vw[d]);
                }
            }

            float vLen = vertexN.Length();
            if (vLen > 1e-6f)
                smoothNormal += vertexN / vLen;
        }

        float sLen = smoothNormal.Length();
        if (sLen > 1e-6f)
            smoothNormal /= sLen;
        else
            smoothNormal = originalNormal;

        // Lerp between blocky and smooth
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

    /// <summary>
    /// Subtended angle of a face at one of its vertices. For cubes, always π/2.
    /// Infrastructure for future slope blocks with non-right-angle faces.
    /// </summary>
    private static float SubtendedAngle(Vector3I cell, int dir, int v)
    {
        // All cube faces subtend π/2 at each vertex
        return MathF.PI / 2f;
    }

    // ─── Multi-ring Laplacian diffusion ────────────────────────────

    private Dictionary<long, Vector3> ComputeDiffusedVertexNormals()
    {
        // Compute initial vertex normals from direction weights
        var vertexNormals = new Dictionary<long, Vector3>(_vertexDirWeights.Count);
        foreach (var (key, vw) in _vertexDirWeights)
        {
            Vector3 n = Vector3.Zero;
            for (int d = 0; d < 6; d++)
            {
                if (vw[d] > 0)
                    n += DirNormals[d] * ApplyWeight(vw[d]);
            }
            float len = n.Length();
            vertexNormals[key] = len > 1e-6f ? n / len : Vector3.Zero;
        }

        if (_vertexAdjacency == null || SmoothingRings <= 1)
            return vertexNormals;

        // Laplacian diffusion for additional rings
        for (int ring = 1; ring < SmoothingRings; ring++)
        {
            var newNormals = new Dictionary<long, Vector3>(vertexNormals.Count);
            foreach (var (key, normal) in vertexNormals)
            {
                Vector3 sum = normal;
                int count = 1;

                if (_vertexAdjacency.TryGetValue(key, out var neighbors))
                {
                    foreach (long neighbor in neighbors)
                    {
                        // Skip crease edges
                        if (_creaseEdges != null)
                        {
                            var edge = key < neighbor ? (key, neighbor) : (neighbor, key);
                            if (_creaseEdges.Contains(edge)) continue;
                        }

                        if (vertexNormals.TryGetValue(neighbor, out var neighborNormal))
                        {
                            sum += neighborNormal;
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
        _edgeRefCount = new Dictionary<(long, long), int>();

        for (int i = 0; i < _rawFaces.Count; i++)
        {
            var (cell, dir) = _rawFaces[i];
            AddFaceEdges(cell, dir);
        }
    }

    private void AddFaceEdges(Vector3I cell, int dir)
    {
        if (_vertexAdjacency == null || _edgeRefCount == null)
            return;

        // 4 vertices: 0,1,2,3 → edges: (0,1), (1,3), (3,2), (2,0)
        long v0 = PackVertex(cell, dir, 0);
        long v1 = PackVertex(cell, dir, 1);
        long v2 = PackVertex(cell, dir, 2);
        long v3 = PackVertex(cell, dir, 3);

        AddEdge(v0, v1);
        AddEdge(v1, v3);
        AddEdge(v3, v2);
        AddEdge(v2, v0);
    }

    private void RemoveFaceEdges(Vector3I cell, int dir)
    {
        if (_vertexAdjacency == null || _edgeRefCount == null)
            return;

        long v0 = PackVertex(cell, dir, 0);
        long v1 = PackVertex(cell, dir, 1);
        long v2 = PackVertex(cell, dir, 2);
        long v3 = PackVertex(cell, dir, 3);

        RemoveEdge(v0, v1);
        RemoveEdge(v1, v3);
        RemoveEdge(v3, v2);
        RemoveEdge(v2, v0);
    }

    private void AddEdge(long a, long b)
    {
        var canonical = a < b ? (a, b) : (b, a);
        _edgeRefCount!.TryGetValue(canonical, out int count);
        _edgeRefCount[canonical] = count + 1;

        if (!_vertexAdjacency!.TryGetValue(a, out var setA))
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
        var canonical = a < b ? (a, b) : (b, a);
        if (!_edgeRefCount!.TryGetValue(canonical, out int count))
            return;

        if (count <= 1)
        {
            _edgeRefCount.Remove(canonical);

            if (_vertexAdjacency!.TryGetValue(a, out var setA))
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
        _creaseEdges = new HashSet<(long, long)>();

        if (_edgeRefCount == null) return;

        float cosThreshold = MathF.Cos(CreaseAngleDegrees * MathF.PI / 180f);

        foreach (var (edge, _) in _edgeRefCount)
        {
            if (IsCreaseEdge(edge.Item1, edge.Item2, cosThreshold))
                _creaseEdges.Add(edge);
        }
    }

    private void RedetectCreasesForDirtyVertices(HashSet<long> dirtyVertices)
    {
        if (_creaseEdges == null || _edgeRefCount == null || _vertexAdjacency == null)
            return;

        float cosThreshold = MathF.Cos(CreaseAngleDegrees * MathF.PI / 180f);

        // Re-check edges touching dirty vertices
        foreach (long vkey in dirtyVertices)
        {
            if (!_vertexAdjacency.TryGetValue(vkey, out var neighbors))
                continue;

            foreach (long neighbor in neighbors)
            {
                var edge = vkey < neighbor ? (vkey, neighbor) : (neighbor, vkey);
                if (!_edgeRefCount.ContainsKey(edge)) continue;

                if (IsCreaseEdge(edge.Item1, edge.Item2, cosThreshold))
                    _creaseEdges.Add(edge);
                else
                    _creaseEdges.Remove(edge);
            }
        }
    }

    private bool IsCreaseEdge(long va, long vb, float cosThreshold)
    {
        // Find faces sharing this edge
        if (!_vertexToFaceIndex.TryGetValue(va, out var facesA) ||
            !_vertexToFaceIndex.TryGetValue(vb, out var facesB))
            return false;

        // Faces sharing the edge are those that contain both vertices
        Vector3 normal1 = default;
        Vector3 normal2 = default;
        int found = 0;

        foreach (int fi in facesA)
        {
            if (!facesB.Contains(fi)) continue;
            if (fi >= _rawFaces.Count) continue;

            var (_, dir) = _rawFaces[fi];
            if (found == 0)
            {
                normal1 = DirNormals[dir];
                found = 1;
            }
            else if (found == 1)
            {
                normal2 = DirNormals[dir];
                found = 2;
                break;
            }
        }

        if (found < 2) return false;

        float dot = Vector3.Dot(normal1, normal2);
        return dot <= cosThreshold;
    }

    // ─── Dirty vertex expansion for multi-ring ────────────────────

    private HashSet<long> ExpandDirtyVertices(HashSet<long> dirty, int hops)
    {
        if (_vertexAdjacency == null || hops <= 0)
            return dirty;

        var expanded = new HashSet<long>(dirty);
        var frontier = new HashSet<long>(dirty);

        for (int h = 0; h < hops; h++)
        {
            var nextFrontier = new HashSet<long>();
            foreach (long v in frontier)
            {
                if (!_vertexAdjacency.TryGetValue(v, out var neighbors))
                    continue;
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

        var groupArea = new float[6];
        var groupWeightedPos = new Vector3[6];
        var groupNormalSum = new Vector3[6];
        var groupCount = new int[6];
        var rSxx = new float[6]; var rSyy = new float[6]; var rSzz = new float[6];
        var rSxy = new float[6]; var rSxz = new float[6]; var rSyz = new float[6];

        for (int i = 0; i < _faces.Count; i++)
        {
            var face = _faces[i];
            var (_, dir) = _rawFaces[i];

            groupArea[dir] += face.Area;
            groupWeightedPos[dir] += face.Position * face.Area;
            groupNormalSum[dir] += face.Normal;
            groupCount[dir]++;
            rSxx[dir] += face.Area * face.Position.X * face.Position.X;
            rSyy[dir] += face.Area * face.Position.Y * face.Position.Y;
            rSzz[dir] += face.Area * face.Position.Z * face.Position.Z;
            rSxy[dir] += face.Area * face.Position.X * face.Position.Y;
            rSxz[dir] += face.Area * face.Position.X * face.Position.Z;
            rSyz[dir] += face.Area * face.Position.Y * face.Position.Z;
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
        _faceKeys.Clear();
        _groups.Clear();
        _rawFaces.Clear();
        _rawFaceKeys.Clear();
        _faceIndex.Clear();
        _vertexDirWeights.Clear();
        _vertexToFaceIndex.Clear();
        _vertexAdjacency = null;
        _edgeRefCount = null;
        _creaseEdges = null;
    }

    private static int CountCells(IReadOnlyList<Block> blocks)
    {
        int count = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            var size = b.Size;
            count += size.X * size.Y * size.Z;
        }
        return count;
    }

    private static Vector3I GetFaceVertex(Vector3I cell, int dir, int vertexIndex)
    {
        int a = vertexIndex & 1;
        int b = (vertexIndex >> 1) & 1;

        return dir switch
        {
            0 => new Vector3I(cell.X + 1, cell.Y + a, cell.Z + b),
            1 => new Vector3I(cell.X,     cell.Y + a, cell.Z + b),
            2 => new Vector3I(cell.X + a, cell.Y + 1, cell.Z + b),
            3 => new Vector3I(cell.X + a, cell.Y,     cell.Z + b),
            4 => new Vector3I(cell.X + a, cell.Y + b, cell.Z + 1),
            5 => new Vector3I(cell.X + a, cell.Y + b, cell.Z),
            _ => default,
        };
    }

    private static long PackVertex(Vector3I cell, int dir, int vertexIndex)
    {
        var v = GetFaceVertex(cell, dir, vertexIndex);
        return ((long)(v.X & 0xFFFFF) << 40)
             | ((long)(v.Y & 0xFFFFF) << 20)
             | (long)(v.Z & 0xFFFFF);
    }

    private static Vector3 CellFaceCenter(Vector3I cell, Vector3I faceDir, float blockSize)
    {
        return new Vector3(
            (cell.X + 0.5f) * blockSize + faceDir.X * 0.5f * blockSize,
            (cell.Y + 0.5f) * blockSize + faceDir.Y * 0.5f * blockSize,
            (cell.Z + 0.5f) * blockSize + faceDir.Z * 0.5f * blockSize);
    }
}
