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
}
