#pragma warning disable
namespace AeroMod;

/// <summary>
/// A hash for packed long keys (cells, faces, vertices, columns). .NET hashes a long as low ^ high 32 bits: the
/// packings here put coordinates where those halves cancel (a face key hashed by X ^ Z, a column key by Y ^ Z), so a
/// big grid's dictionaries were long collision chains - Red Ship's wing detection took 27 s in one loop over its cells.
/// </summary>
public sealed class LongKey : IEqualityComparer<long>
{
    public static readonly LongKey Comparer = new();

    public bool Equals(long a, long b) => a == b;

    public int GetHashCode(long x)
    {
        // splitmix64's finalizer: every input bit reaches every output bit
        ulong z = (ulong)x;
        z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9UL;
        z = (z ^ (z >> 27)) * 0x94d049bb133111ebUL;
        return (int)(z ^ (z >> 31));
    }
}
