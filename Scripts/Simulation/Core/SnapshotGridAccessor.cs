#pragma warning disable
namespace AeroMod;

/// <summary>
/// The grid's occupied cells, copied: a background rebuild reads this instead of the live octree (which the
/// simulation changes under it). Stored as a bit per cell over the grid's bounding box: a big grid has millions of
/// quarter-metre cells, and a HashSet of them (tens of MB per rebuild) drove full garbage collections that paused
/// the game ~0.4 s.
/// </summary>
public sealed class SnapshotGridAccessor : IGridAccessor
{
    private readonly ulong[] _bits;
    private readonly Vector3I _min, _size;
    private readonly int _count;

    /// <summary>From the blocks' occupied-cell boxes (inclusive min/max), one per block.</summary>
    public SnapshotGridAccessor(IReadOnlyList<(Vector3I min, Vector3I max)> boxes)
    {
        if (boxes.Count == 0) { _bits = System.Array.Empty<ulong>(); return; }
        Vector3I lo = boxes[0].min, hi = boxes[0].max;
        foreach (var (a, b) in boxes) { lo = Vector3I.Min(lo, a); hi = Vector3I.Max(hi, b); }
        _min = lo;
        _size = hi - lo + Vector3I.One;
        long total = (long)_size.X * _size.Y * _size.Z;
        _bits = new ulong[(total + 63) / 64];
        int count = 0;
        foreach (var (a, b) in boxes)
            for (int x = a.X; x <= b.X; x++)
                for (int y = a.Y; y <= b.Y; y++)
                    for (int z = a.Z; z <= b.Z; z++)
                    {
                        long i = Index(x, y, z);
                        ulong m = 1UL << (int)(i & 63);
                        if ((_bits[i >> 6] & m) == 0) { _bits[i >> 6] |= m; count++; }
                    }
        _count = count;
    }

    private long Index(int x, int y, int z) => ((long)(x - _min.X) * _size.Y + (y - _min.Y)) * _size.Z + (z - _min.Z);

    public bool IsCellOccupied(Vector3I p)
    {
        if (_bits.Length == 0) return false;
        int x = p.X - _min.X, y = p.Y - _min.Y, z = p.Z - _min.Z;
        if ((uint)x >= (uint)_size.X || (uint)y >= (uint)_size.Y || (uint)z >= (uint)_size.Z) return false;
        long i = ((long)x * _size.Y + y) * _size.Z + z;
        return (_bits[i >> 6] & (1UL << (int)(i & 63))) != 0;
    }

    public IEnumerable<Vector3I> EnumerateOccupiedCells()
    {
        for (int w = 0; w < _bits.Length; w++)
        {
            ulong word = _bits[w];
            while (word != 0)
            {
                int b = System.Numerics.BitOperations.TrailingZeroCount(word);
                word &= word - 1;
                long i = ((long)w << 6) + b;
                int z = (int)(i % _size.Z); long r = i / _size.Z;
                int y = (int)(r % _size.Y); int x = (int)(r / _size.Y);
                yield return new Vector3I(x + _min.X, y + _min.Y, z + _min.Z);
            }
        }
    }

    public int CellCount => _count;

    /// <summary>Cells per surface cell, by block size: large-block grids (2.5 m) at 0.5 m (2 of the grid's 0.25 m
    /// cells; block boundaries fall on it exactly); smaller ones at the grid's own cells.</summary>
    public static int CellScale(float blockSize) => blockSize >= 2.4f ? LargeGridCellScale : 1;
    public static int LargeGridCellScale = 2;   // (5 - 1.25 m - lost the shape: forces 55% off; 2: ~8%)

    static int FloorDiv(int a, int k) => a >= 0 ? a / k : -((-a + k - 1) / k);

    /// <summary>The boxes in cells k times larger (a coarse cell is solid where any of its cells is).</summary>
    public static List<(Vector3I min, Vector3I max)> Coarsen(IReadOnlyList<(Vector3I min, Vector3I max)> boxes, int k)
    {
        var r = new List<(Vector3I, Vector3I)>(boxes.Count);
        foreach (var (a, b) in boxes)
            r.Add((new Vector3I(FloorDiv(a.X, k), FloorDiv(a.Y, k), FloorDiv(a.Z, k)), new Vector3I(FloorDiv(b.X, k), FloorDiv(b.Y, k), FloorDiv(b.Z, k))));
        return r;
    }

    /// <summary>The coarse cells (k x k x k of these) more than half solid: shapes keep their size and slopes stay
    /// slopes (where any solid cell counted, a slope block filled out to a box: +34% frontal area on Red Ship).</summary>
    public SnapshotGridAccessor CoarsenMajority(int k)
    {
        var counts = new Dictionary<Vector3I, int>();
        foreach (var c in EnumerateOccupiedCells())
        {
            var q = new Vector3I(FloorDiv(c.X, k), FloorDiv(c.Y, k), FloorDiv(c.Z, k));
            counts.TryGetValue(q, out int n); counts[q] = n + 1;
        }
        int need = k * k * k / 2 + 1;
        var boxes = new List<(Vector3I, Vector3I)>();
        foreach (var kv in counts) if (kv.Value >= need) boxes.Add((kv.Key, kv.Key));
        return new SnapshotGridAccessor(boxes);
    }

    /// <summary>A surface cell's size (m) and centre offset (cells) at scale k.</summary>
    public static (float size, float offset) CellGeometry(int k) => (0.25f * k, (k - 1) / (2f * k));
}
