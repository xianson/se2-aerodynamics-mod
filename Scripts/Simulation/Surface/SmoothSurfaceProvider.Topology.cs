#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

// The vertex/edge graph: adjacency, creases, dirty-vertex expansion, the six direction groups.
public partial class SmoothSurfaceProvider
{
    // ─── Adjacency graph ──────────────────────────────────────────

    private void BuildAdjacency()
    {
        _edgeRefCount = _edgeStore ?? new Dictionary<long, int>(LongKey.Comparer); _edgeStore = null;
        _edgeRefCount.EnsureCapacity(_rawFaceKeys.Count * 2);

        for (int i = 0; i < _rawFaceKeys.Count; i++)
        {
            var cell = UnpackCell(_rawFaceKeys[i]);
            AddFaceEdges(cell, _rawFaceDirs[i]);
        }
    }

    private void AddFaceEdges(Vector3I cell, int dir)
    {
        if (_edgeRefCount == null) return;

        long v0 = PackVertex(cell, dir, 0);
        long v1 = PackVertex(cell, dir, 1);
        long v2 = PackVertex(cell, dir, 2);
        long v3 = PackVertex(cell, dir, 3);

        AddEdge(v0, v1); AddEdge(v1, v3); AddEdge(v3, v2); AddEdge(v2, v0);
    }

    private void RemoveFaceEdges(Vector3I cell, int dir)
    {
        if (_edgeRefCount == null) return;

        long v0 = PackVertex(cell, dir, 0);
        long v1 = PackVertex(cell, dir, 1);
        long v2 = PackVertex(cell, dir, 2);
        long v3 = PackVertex(cell, dir, 3);

        RemoveEdge(v0, v1); RemoveEdge(v1, v3); RemoveEdge(v3, v2); RemoveEdge(v2, v0);
    }

    // A vertex key's 20-bit fields (X << 40 | Y << 20 | Z, two's complement): stepped by unpacking, never by adding
    // to the key (a negative coordinate's field would carry into the next one).
    private static int VField(long v, int shift) => ((int)((v >> shift) & 0xFFFFF) << 12) >> 12;
    private static long VPack(int x, int y, int z) => ((long)(x & 0xFFFFF) << 40) | ((long)(y & 0xFFFFF) << 20) | (long)(z & 0xFFFFF);
    private static long VStep(long v, int axis, int d)
    {
        int x = VField(v, 40), y = VField(v, 20), z = VField(v, 0);
        if (axis == 0) x += d; else if (axis == 1) y += d; else z += d;
        return VPack(x, y, z);
    }
    private static long EdgeKey(long lowVertex, int axis) => (lowVertex << 2) | (long)axis;

    /// <summary>The key of the unit edge between two vertices one axis step apart.</summary>
    private static long EdgeKeyOf(long a, long b)
    {
        int dx = VField(b, 40) - VField(a, 40), dy = VField(b, 20) - VField(a, 20), dz = VField(b, 0) - VField(a, 0);
        int axis = dx != 0 ? 0 : dy != 0 ? 1 : 2;
        int d = dx + dy + dz;
        return EdgeKey(d > 0 ? a : b, axis);
    }

    /// <summary>A vertex's neighbours along the surface's edges (at most 6), and those edges' keys.</summary>
    private int Neighbors(long v, Span<long> neighbors, Span<long> edges)
    {
        int n = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            long up = EdgeKey(v, axis);
            if (_edgeRefCount.ContainsKey(up)) { neighbors[n] = VStep(v, axis, 1); edges[n] = up; n++; }
            long lowV = VStep(v, axis, -1);
            long down = EdgeKey(lowV, axis);
            if (_edgeRefCount.ContainsKey(down)) { neighbors[n] = lowV; edges[n] = down; n++; }
        }
        return n;
    }

    private void AddEdge(long a, long b)
    {
        ref int count = ref CollectionsMarshal.GetValueRefOrAddDefault(_edgeRefCount, EdgeKeyOf(a, b), out _);
        count++;
    }

    private void RemoveEdge(long a, long b)
    {
        long key = EdgeKeyOf(a, b);
        if (!_edgeRefCount.TryGetValue(key, out int count))
            return;

        if (count <= 1)
        {
            _edgeRefCount.Remove(key);
            _creaseEdges?.Remove(key);
        }
        else
        {
            _edgeRefCount[key] = count - 1;
        }
    }

    // ─── Crease detection ─────────────────────────────────────────

    private void DetectAllCreases()
    {
        _creaseEdges = _creaseStore ?? new HashSet<long>(LongKey.Comparer); _creaseStore = null;
        _creaseEdges.Clear();
        if (_edgeRefCount == null) return;

        float cosThreshold = MathF.Cos(CreaseAngleDegrees * MathF.PI / 180f);

        // Every edge once
        foreach (var kvp in _edgeRefCount)
        {
            long key = kvp.Key;
            long vA = key >> 2;
            long vB = VStep(vA, (int)(key & 3), 1);
            if (IsCreaseEdge(vA, vB, cosThreshold))
                _creaseEdges.Add(key);
        }
    }

    private void UpdateCreasesAt(HashSet<long> vertices)
    {
        float cosThreshold = MathF.Cos(CreaseAngleDegrees * MathF.PI / 180f);
        Span<long> nb = stackalloc long[6];
        Span<long> ek = stackalloc long[6];
        foreach (long v in vertices)
        {
            int nn = Neighbors(v, nb, ek);
            for (int k = 0; k < nn; k++)
            {
                if (IsCreaseEdge(v, nb[k], cosThreshold)) _creaseEdges.Add(ek[k]);
                else _creaseEdges.Remove(ek[k]);
            }
        }
    }

    private bool IsCreaseEdge(long va, long vb, float cosThreshold)
    {
        if (!_vertexToFaceIndex.TryGetValue(va, out var facesA) ||
            !_vertexToFaceIndex.TryGetValue(vb, out var facesB))
            return false;

        // Every pair of the faces on the edge (2, or 4 where cells meet only along it): a crease if any pair bends
        // past the threshold. (The first two found decided before - their order is the order faces were added, so a
        // surface updated by damage and a fresh build of the same shape could disagree.)
        int dirMask = 0;
        for (int ai = 0; ai < facesA.Count; ai++)
        {
            int fi = facesA.Get(ai);
            if (!facesB.Contains(fi)) continue;
            if (fi >= _rawFaceDirs.Count) continue;
            dirMask |= 1 << _rawFaceDirs[fi];
        }
        for (int d1 = 0; d1 < 6; d1++)
        {
            if ((dirMask & (1 << d1)) == 0) continue;
            for (int d2 = d1 + 1; d2 < 6; d2++)
                if ((dirMask & (1 << d2)) != 0 && Vector3.Dot(DirNormals[d1], DirNormals[d2]) <= cosThreshold)
                    return true;
        }
        return false;
    }

    // ─── Dirty vertex expansion ──────────────────────────────────

    private HashSet<long> _frontierA = new(LongKey.Comparer);
    private HashSet<long> _frontierB = new(LongKey.Comparer);

    private HashSet<long> ExpandDirtyVertices(HashSet<long> dirty, int hops)
    {
        if (_edgeRefCount == null || hops <= 0) return dirty;
        Span<long> nb = stackalloc long[6];
        Span<long> ek = stackalloc long[6];

        var expanded = new HashSet<long>(dirty, LongKey.Comparer);

        _frontierA.Clear();
        foreach (long v in dirty) _frontierA.Add(v);

        for (int h = 0; h < hops; h++)
        {
            _frontierB.Clear();
            foreach (long v in _frontierA)
            {
                int nn = Neighbors(v, nb, ek);
                for (int k = 0; k < nn; k++)
                {
                    if (expanded.Add(nb[k]))
                        _frontierB.Add(nb[k]);
                }
            }
            if (_frontierB.Count == 0) break;
            // Swap
            (_frontierA, _frontierB) = (_frontierB, _frontierA);
        }

        return expanded;
    }

    // ─── Groups (persistent accumulators) ────────────────────────

    private readonly List<float> _grpArea = new() { 0f, 0f, 0f, 0f, 0f, 0f };
    private readonly List<Vector3> _grpWeightedPos = new() { Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero };
    private readonly List<Vector3> _grpNormalSum = new() { Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero };
    private readonly List<int> _grpCount = new() { 0, 0, 0, 0, 0, 0 };
    private readonly List<float> _grpSxx = new() { 0f, 0f, 0f, 0f, 0f, 0f }, _grpSyy = new() { 0f, 0f, 0f, 0f, 0f, 0f }, _grpSzz = new() { 0f, 0f, 0f, 0f, 0f, 0f };
    private readonly List<float> _grpSxy = new() { 0f, 0f, 0f, 0f, 0f, 0f }, _grpSxz = new() { 0f, 0f, 0f, 0f, 0f, 0f }, _grpSyz = new() { 0f, 0f, 0f, 0f, 0f, 0f };

    private void RebuildGroups()
    {
        for (int d = 0; d < 6; d++)
        {
            _grpArea[d] = 0f; _grpCount[d] = 0;
            _grpSxx[d] = 0f; _grpSyy[d] = 0f; _grpSzz[d] = 0f;
            _grpSxy[d] = 0f; _grpSxz[d] = 0f; _grpSyz[d] = 0f;
            _grpWeightedPos[d] = Vector3.Zero; _grpNormalSum[d] = Vector3.Zero;
        }

        for (int i = 0; i < _faces.Count; i++)
        {
            var face = _faces[i];
            int dir = _rawFaceDirs[i];

            _grpArea[dir] += face.Area;
            _grpWeightedPos[dir] += face.Position * face.Area;
            _grpNormalSum[dir] += face.Normal;
            _grpCount[dir]++;
            _grpSxx[dir] += face.Area * face.Position.X * face.Position.X;
            _grpSyy[dir] += face.Area * face.Position.Y * face.Position.Y;
            _grpSzz[dir] += face.Area * face.Position.Z * face.Position.Z;
            _grpSxy[dir] += face.Area * face.Position.X * face.Position.Y;
            _grpSxz[dir] += face.Area * face.Position.X * face.Position.Z;
            _grpSyz[dir] += face.Area * face.Position.Y * face.Position.Z;
        }

        FinalizeGroups();
    }

    private void FinalizeGroups()
    {
        _groups.Clear();

        for (int d = 0; d < 6; d++)
        {
            if (_grpCount[d] == 0) continue;

            float a = _grpArea[d];
            var centroid = _grpWeightedPos[d] / a;

            Vector3 avgNormal = _grpNormalSum[d];
            float avgLen = avgNormal.Length();
            Vector3 groupNormal = avgLen > 1e-6f ? avgNormal / avgLen : DirNormals[d];

            float sxx = _grpSxx[d] - a * centroid.X * centroid.X;
            float syy = _grpSyy[d] - a * centroid.Y * centroid.Y;
            float szz = _grpSzz[d] - a * centroid.Z * centroid.Z;
            float sxy = _grpSxy[d] - a * centroid.X * centroid.Y;
            float sxz = _grpSxz[d] - a * centroid.X * centroid.Z;
            float syz = _grpSyz[d] - a * centroid.Y * centroid.Z;

            _groups.Add(new NormalGroup(
                groupNormal, a, centroid, _grpCount[d],
                sxx, syy, szz, sxy, sxz, syz));
        }
    }
}
