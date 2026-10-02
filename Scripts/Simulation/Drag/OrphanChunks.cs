#pragma warning disable
using System;

namespace AeroMod;

/// <summary>
/// Pieces that break off: the game spawns a split-off piece with its parent's very transform and grid coordinates
/// (CubeGridSplitterComponent serializes the parent with the piece's blocks only). So the shares of the force table
/// its blocks left behind in the parent are its own forces, exactly: the parent posts them here as they leave its
/// table; a grid with no table yet, standing where they were posted, takes the ones in its chunks - forces from its
/// first frame, while its own build runs (no rotation damping from them: they carry none).
/// </summary>
public static class OrphanChunks
{
    sealed class Post
    {
        public long Stamp; public Vector3D Pos; public Quaternion Rot; public int N; public float ChunkSize;
        public List<(long key, float[] share)> Shares = new();
    }
    static readonly List<Post> _posts = new();
    const double MaxAgeSeconds = 3.0, MaxDistance = 2.0;

    public static void Add(Vector3D parentPos, Quaternion parentRot, int n, float chunkSize, List<(long key, float[] share)> shares)
    {
        if (shares.Count == 0) return;
        var p = new Post { Stamp = System.Diagnostics.Stopwatch.GetTimestamp(), Pos = parentPos, Rot = parentRot, N = n, ChunkSize = chunkSize };
        p.Shares.AddRange(shares);
        lock (_posts)
        {
            Prune();
            _posts.Add(p);
        }
    }

    static void Prune()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp(), max = (long)(System.Diagnostics.Stopwatch.Frequency * MaxAgeSeconds);
        _posts.RemoveAll(p => now - p.Stamp > max);
    }

    public static bool Any { get { lock (_posts) return _posts.Count > 0; } }

    /// <summary>A table from the posted shares in `keys` (chunks this grid has blocks in), posted where this grid
    /// stands; the shares taken are removed. Null if none.</summary>
    public static ForceTable Take(Vector3D gridPos, Quaternion gridRot, HashSet<long> keys, int n, int nj)
    {
        ForceTable table = null;
        lock (_posts)
        {
            Prune();
            foreach (var p in _posts)
            {
                if (p.N != n || (p.Pos - gridPos).Length() > MaxDistance) continue;
                if (MathF.Abs(Quaternion.Dot(p.Rot, gridRot)) < 0.999f) continue;
                for (int i = p.Shares.Count - 1; i >= 0; i--)
                {
                    var (key, share) = p.Shares[i];
                    if (!keys.Contains(key)) continue;
                    table ??= new ForceTable(n, nj);
                    int slots = 6 * (n + 1) * (n + 1);
                    for (int s = 0; s < slots; s++)
                    {
                        int face = s / ((n + 1) * (n + 1)), r = s % ((n + 1) * (n + 1));
                        var e = table.At(face, r / (n + 1), r % (n + 1));
                        for (int k = 0; k < ForceTable.Stride; k++) e[k] += share[s * ForceTable.Stride + k];
                    }
                    p.Shares.RemoveAt(i);
                }
            }
            _posts.RemoveAll(p => p.Shares.Count == 0);
        }
        return table;
    }
}
