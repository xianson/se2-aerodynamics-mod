#pragma warning disable
using System;
namespace AeroMod;

// Face normals: per-vertex normals by multi-ring Laplacian diffusion, the faces' smoothed normals.
public partial class SmoothSurfaceProvider
{
    // ─── Normal computation ────────────────────────────────────────

    private void RecomputeAllFaceNormals()
    {
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

        float faceArea = CellSize * CellSize;

        var dirtyFaceIndices = new HashSet<int>();
        foreach (long vkey in dirtyVertices)
        {
            if (_vertexToFaceIndex.TryGetValue(vkey, out var faceSet))
            {
                for (int fi = 0; fi < faceSet.Count; fi++)
                    dirtyFaceIndices.Add(faceSet.Get(fi));
            }
        }

        Dictionary<long, Vector3> vertexNormals = null;
        if (SmoothingRings > 1)
            vertexNormals = ComputeDiffusedVertexNormals(dirtyVertices);

        // Sync _faces size
        while (_faces.Count < _rawFaceKeys.Count)
            _faces.Add(default);
        while (_faces.Count > _rawFaceKeys.Count)
            _faces.RemoveAt(_faces.Count - 1);

        // Only iterate dirty faces instead of all faces
        foreach (int i in dirtyFaceIndices)
        {
            if (i >= _rawFaceKeys.Count) continue;
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

        // (the face's share of the SMOOTHED surface: a staircase's cell faces - its fronts and its treads - each pushed along the
        //  smoothed slope's normal with its whole square-on area counted the slope about 1.4 times over at 45 deg, and a
        //  sloped nose dragged like a flat wall (AeroBench noses: 45 deg wedge Cd 0.96 against the box's 1.10). Projected onto
        //  the smoothed normal they sum to the slope's true area; an unsmoothed face (a box's side) keeps all of it.)
        float share = Vector3.Dot(originalNormal, finalNormal);
        return new SurfaceFace(pos, finalNormal, faceArea * (share > 0.05f ? share : 0.05f), originalNormal);
    }

    private float ApplyWeight(float w)
    {
        if (WeightExponent == 2f) return w * w;
        if (WeightExponent == 1f) return w;
        return MathF.Pow(w, WeightExponent);
    }

    // ─── Multi-ring Laplacian diffusion ────────────────────────────

    private Dictionary<long, Vector3> ComputeDiffusedVertexNormals()
        => ComputeDiffusedVertexNormals(null);

    /// <summary>
    /// Compute diffused vertex normals. When dirtyVertices is non-null, only computes
    /// normals for the dirty set expanded by SmoothingRings hops (localized diffusion).
    /// </summary>
    private Dictionary<long, Vector3> ComputeDiffusedVertexNormals(HashSet<long> dirtyVertices)
    {
        // Determine which vertices to process
        Dictionary<long, W6> source;
        int capacity;

        if (dirtyVertices != null)
        {
            // Expand dirty set to cover diffusion neighborhood
            // The faces recomputed touch the dirty vertices; their far corners are 2 hops away (a quad's diagonal),
            // and each of the SmoothingRings - 1 passes reaches one hop further: raw normals are needed out to
            // SmoothingRings + 1 hops for those corners to come out exactly as a full build's. (SmoothingRings hops
            // left the window's edge short of neighbours: damaged surfaces drifted from a fresh build's normals.)
            var expanded = _edgeRefCount != null && SmoothingRings > 1
                ? ExpandDirtyVertices(dirtyVertices, SmoothingRings + 1)
                : dirtyVertices;

            capacity = expanded.Count;
            var filtered = new Dictionary<long, W6>(capacity, LongKey.Comparer);
            foreach (long vkey in expanded)
            {
                if (_vertexDirWeights.TryGetValue(vkey, out var weights))
                    filtered[vkey] = weights;
            }
            source = filtered;
        }
        else
        {
            capacity = _vertexDirWeights.Count;
            source = _vertexDirWeights;
        }

        bool full = dirtyVertices == null;   // (the full pass reuses two dictionaries; the local pass is small)
        var vertexNormals = full ? (_normA ??= new Dictionary<long, Vector3>(capacity, LongKey.Comparer)) : new Dictionary<long, Vector3>(capacity, LongKey.Comparer);
        if (full) vertexNormals.Clear();
        foreach (var kvp in source)
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

        if (_edgeRefCount == null || SmoothingRings <= 1)
            return vertexNormals;

        Span<long> nb = stackalloc long[6];
        Span<long> ek = stackalloc long[6];

        for (int ring = 1; ring < SmoothingRings; ring++)
        {
            var newNormals = full
                ? (ReferenceEquals(vertexNormals, _normA) ? (_normB ??= new Dictionary<long, Vector3>(vertexNormals.Count, LongKey.Comparer)) : _normA)
                : new Dictionary<long, Vector3>(vertexNormals.Count, LongKey.Comparer);
            if (full) newNormals.Clear();
            foreach (var kvp in vertexNormals)
            {
                var key = kvp.Key;
                var normal = kvp.Value;
                Vector3 sum = normal;
                int count = 1;

                int nn = Neighbors(key, nb, ek);
                for (int k = 0; k < nn; k++)
                {
                    if (_creaseEdges != null && _creaseEdges.Contains(ek[k])) continue;
                    if (vertexNormals.TryGetValue(nb[k], out var neighborNormal))
                    {
                        sum = sum + neighborNormal;
                        count++;
                    }
                }

                float len = sum.Length();
                newNormals[key] = len > 1e-6f ? sum / len : normal;
            }
            vertexNormals = newNormals;
        }

        return vertexNormals;
    }
}
