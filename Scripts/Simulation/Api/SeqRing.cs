#pragma warning disable
namespace AeroMod;

/// <summary>
/// A bounded event queue with sequence numbers, for the public API's polled events (AeroApi.ReadStructureEvents): each
/// item added gets the next number (from 0, only up); the newest Capacity are kept, older ones overwritten. A reader keeps
/// the number it read up to and asks again from there; a gap (its number under First) is events it missed. Engine-free
/// (Tests/ApiTests). Every member under Lock - the caller takes it around a whole read (At is unlocked).
/// </summary>
internal sealed class SeqRing<T>
{
    readonly T[] _items;
    long _next;
    public readonly object Lock = new object();

    public SeqRing(int capacity) { _items = new T[capacity < 1 ? 1 : capacity]; }

    public int Capacity => _items.Length;
    /// <summary>The number the next item gets (= how many were ever added).</summary>
    public long Next { get { lock (Lock) return _next; } }
    /// <summary>The oldest number still kept (under Lock).</summary>
    public long First => _next > _items.Length ? _next - _items.Length : 0;

    /// <summary>Add an item: its number.</summary>
    public long Add(in T item)
    {
        lock (Lock)
        {
            _items[(int)(_next % _items.Length)] = item;
            return _next++;
        }
    }

    /// <summary>The item numbered seq - First &lt;= seq &lt; Next, the caller holding Lock.</summary>
    public ref readonly T At(long seq) => ref _items[(int)(seq % _items.Length)];

    /// <summary>The unlocked count (under Lock).</summary>
    public long NextUnlocked => _next;
}
