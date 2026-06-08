#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// Classifies surface faces as hull (exterior) or cavity (sealed interior)
/// using connected component analysis on the face adjacency graph.
///
/// All exposed faces form closed 2-manifold surfaces. The outer hull is one
/// manifold; each sealed internal cavity is another. The largest connected
/// component by face count is the hull. Cavity faces can be skipped in the
/// drag loop and wing detection.
///
/// Face adjacency: each quad face has 4 edges. For each edge, the neighbor
/// is either the continuation face (same normal, adjacent cell) or the corner
/// face (perpendicular normal, turning the corner). This is resolved via
/// O(1) dictionary lookups in the surface provider's _faceIndex.
/// </summary>
public class ManifoldClassifier
{
    private int[] _parent;
    private int[] _rank;
    private bool[] _isHull;
    private int _hullRoot = -1;
    private int _faceCount;
    private bool _classified;

    /// <summary>True if face at this index is part of the hull (exterior).</summary>
    public bool IsHull(int faceIndex)
    {
        if (!_classified || faceIndex < 0 || faceIndex >= _faceCount)
            return true; // default to hull if not classified
        return _isHull[faceIndex];
    }

    /// <summary>Number of faces classified as hull.</summary>
    public int HullFaceCount { get; private set; }

    /// <summary>Number of faces classified as cavity (internal).</summary>
    public int CavityFaceCount { get; private set; }

    /// <summary>Whether classification has been run.</summary>
    public bool IsClassified => _classified;

    /// <summary>
    /// Classify all faces in the surface provider as hull or cavity.
    /// Call after surface build/finalize.
    /// </summary>
    public void Classify(SmoothSurfaceProvider surface)
    {
        _faceCount = surface.RawFaceCount;
        if (_faceCount == 0)
        {
            _classified = true;
            HullFaceCount = 0;
            CavityFaceCount = 0;
            return;
        }

        // Init union-find
        if (_parent == null || _parent.Length < _faceCount)
        {
            _parent = new int[_faceCount];
            _rank = new int[_faceCount];
            _isHull = new bool[_faceCount];
        }
        for (int i = 0; i < _faceCount; i++)
        {
            _parent[i] = i;
            _rank[i] = 0;
        }

        // Build connected components via face edge-adjacency
        for (int i = 0; i < _faceCount; i++)
        {
            surface.GetFaceCellDir(i, out var cell, out int dir);

            // Get 4 edge-neighbor face indices
            Span<int> neighbors = stackalloc int[4];
            GetEdgeNeighbors(surface, cell, dir, neighbors);

            for (int e = 0; e < 4; e++)
            {
                if (neighbors[e] >= 0)
                    Union(i, neighbors[e]);
            }
        }

        // Find largest component = hull
        // Count faces per root
        var rootCounts = new Dictionary<int, int>();
        for (int i = 0; i < _faceCount; i++)
        {
            int root = Find(i);
            rootCounts.TryGetValue(root, out int count);
            rootCounts[root] = count + 1;
        }

        _hullRoot = -1;
        int maxCount = 0;
        foreach (var kv in rootCounts)
        {
            if (kv.Value > maxCount)
            {
                maxCount = kv.Value;
                _hullRoot = kv.Key;
            }
        }

        // Classify
        HullFaceCount = 0;
        CavityFaceCount = 0;
        for (int i = 0; i < _faceCount; i++)
        {
            bool hull = Find(i) == _hullRoot;
            _isHull[i] = hull;
            if (hull) HullFaceCount++;
            else CavityFaceCount++;
        }

        _classified = true;

        if (CavityFaceCount > 0)
            Log.Default?.Info($"[AERO] ManifoldClassifier: {HullFaceCount} hull, {CavityFaceCount} cavity faces culled");
    }

    /// <summary>
    /// Invalidate classification (call when topology changes significantly).
    /// </summary>
    public void Invalidate()
    {
        _classified = false;
        _hullRoot = -1;
    }

    // ═══════════════════════════════════════════════════════════════
    // Face edge-neighbor computation
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// For face (cell, dir), find the 4 edge-neighbor face indices.
    /// Each edge has two candidates: continuation (same dir, adjacent cell)
    /// and corner (perpendicular dir, cell offset by normal).
    /// Returns -1 for edges with no neighbor.
    /// </summary>
    private static void GetEdgeNeighbors(SmoothSurfaceProvider surface, Vector3I cell, int dir, Span<int> neighbors)
    {
        // The 4 edge directions for each face normal.
        // dir: 0=+X, 1=-X, 2=+Y, 3=-Y, 4=+Z, 5=-Z
        // For a face with normal along axis A, the 4 edges are along the other 2 axes (±B, ±C).

        // Get the two tangent axes and normal offset
        GetTangentAxes(dir, out var tangent0, out var tangent1, out var normalOffset);

        // 4 edges: +tangent0, -tangent0, +tangent1, -tangent1
        neighbors[0] = FindEdgeNeighbor(surface, cell, dir, tangent0, normalOffset);
        neighbors[1] = FindEdgeNeighbor(surface, cell, dir, -tangent0, normalOffset);
        neighbors[2] = FindEdgeNeighbor(surface, cell, dir, tangent1, normalOffset);
        neighbors[3] = FindEdgeNeighbor(surface, cell, dir, -tangent1, normalOffset);
    }

    /// <summary>
    /// Find the neighbor face across one edge of face (cell, dir).
    /// edgeStep: direction along the edge (one of ±tangent axes).
    /// normalOffset: direction the face normal points (for corner detection).
    /// </summary>
    private static int FindEdgeNeighbor(SmoothSurfaceProvider surface, Vector3I cell, int dir, Vector3I edgeStep, Vector3I normalOffset)
    {
        // Candidate 1: continuation — same normal, adjacent cell along edge
        Vector3I contCell = cell + edgeStep;
        int contIdx = surface.GetFaceIndex(contCell, dir);
        if (contIdx >= 0)
            return contIdx;

        // Candidate 2: convex corner — face turns outward
        // The neighbor cell is above the current face (cell + normalOffset),
        // and its face normal points back along -edgeStep
        Vector3I cornerCell = cell + normalOffset;
        int cornerDir = VectorToDir(-edgeStep);
        if (cornerDir >= 0)
        {
            int cornerIdx = surface.GetFaceIndex(cornerCell, cornerDir);
            if (cornerIdx >= 0)
                return cornerIdx;
        }

        // Candidate 3: concave corner — face turns inward
        // The neighbor wraps into the concavity: one step along edge, one step back from normal
        Vector3I concaveCell = cell + edgeStep - normalOffset;
        int concaveDir = VectorToDir(normalOffset);
        if (concaveDir >= 0)
        {
            int concaveIdx = surface.GetFaceIndex(concaveCell, concaveDir);
            if (concaveIdx >= 0)
                return concaveIdx;
        }

        return -1; // no neighbor (shouldn't happen on a closed manifold)
    }

    /// <summary>
    /// Get the two tangent axis vectors and normal offset for a face direction.
    /// </summary>
    private static void GetTangentAxes(int dir, out Vector3I tangent0, out Vector3I tangent1, out Vector3I normalOffset)
    {
        switch (dir)
        {
            case 0: // +X
                tangent0 = new Vector3I(0, 1, 0);
                tangent1 = new Vector3I(0, 0, 1);
                normalOffset = new Vector3I(1, 0, 0);
                break;
            case 1: // -X
                tangent0 = new Vector3I(0, 1, 0);
                tangent1 = new Vector3I(0, 0, 1);
                normalOffset = new Vector3I(-1, 0, 0);
                break;
            case 2: // +Y
                tangent0 = new Vector3I(1, 0, 0);
                tangent1 = new Vector3I(0, 0, 1);
                normalOffset = new Vector3I(0, 1, 0);
                break;
            case 3: // -Y
                tangent0 = new Vector3I(1, 0, 0);
                tangent1 = new Vector3I(0, 0, 1);
                normalOffset = new Vector3I(0, -1, 0);
                break;
            case 4: // +Z
                tangent0 = new Vector3I(1, 0, 0);
                tangent1 = new Vector3I(0, 1, 0);
                normalOffset = new Vector3I(0, 0, 1);
                break;
            default: // -Z (5)
                tangent0 = new Vector3I(1, 0, 0);
                tangent1 = new Vector3I(0, 1, 0);
                normalOffset = new Vector3I(0, 0, -1);
                break;
        }
    }

    /// <summary>Convert a unit Vector3I to a direction index (0-5), or -1 if not axis-aligned.</summary>
    private static int VectorToDir(Vector3I v)
    {
        if (v.X == 1) return 0;
        if (v.X == -1) return 1;
        if (v.Y == 1) return 2;
        if (v.Y == -1) return 3;
        if (v.Z == 1) return 4;
        if (v.Z == -1) return 5;
        return -1;
    }

    // ═══════════════════════════════════════════════════════════════
    // Union-Find
    // ═══════════════════════════════════════════════════════════════

    private int Find(int x)
    {
        while (_parent[x] != x)
        {
            _parent[x] = _parent[_parent[x]]; // path halving
            x = _parent[x];
        }
        return x;
    }

    private void Union(int a, int b)
    {
        int ra = Find(a), rb = Find(b);
        if (ra == rb) return;
        if (_rank[ra] < _rank[rb]) (ra, rb) = (rb, ra);
        _parent[rb] = ra;
        if (_rank[ra] == _rank[rb]) _rank[ra]++;
    }
}
