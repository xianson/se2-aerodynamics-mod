#pragma warning disable
using System;
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
    long _next, _floor;
    public readonly object Lock = new object();

    public SeqRing(int capacity) { _items = new T[capacity < 1 ? 1 : capacity]; }

    public int Capacity => _items.Length;
    /// <summary>The number the next item gets (= how many were ever added).</summary>
    public long Next { get { lock (Lock) return _next; } }
    /// <summary>The oldest number still kept (under Lock).</summary>
    public long First => Math.Max(_floor, _next > _items.Length ? _next - _items.Length : 0);

    /// <summary>Add an item: its number.</summary>
    public long Add(in T item)
    {
        lock (Lock)
        {
            _items[(int)(_next % _items.Length)] = item;
            return _next++;
        }
    }

    /// <summary>Count an item not kept (its number taken, nothing stored): a reader from before it sees the gap.</summary>
    public long Skip() { lock (Lock) { _floor = ++_next; return _next - 1; } }

    /// <summary>Drop every item kept (their references freed); the numbers go on from Next - a reader's cursor stays valid,
    /// and one from before sees the gap.</summary>
    public void Clear() { lock (Lock) { Array.Clear(_items); _floor = _next; } }

    /// <summary>The item numbered seq - First &lt;= seq &lt; Next, the caller holding Lock.</summary>
    public ref readonly T At(long seq) => ref _items[(int)(seq % _items.Length)];

    /// <summary>The unlocked count (under Lock).</summary>
    public long NextUnlocked => _next;
}

/// <summary>
/// Whether anyone polls a queue (AeroApi's structure events, 2026-10-15 review): open from a reader's call (Touch) until
/// idle s after its last - one reader once (the Orbital mod's alerts, asking unseated) kept recording on for the session,
/// the ring holding up to 512 destroyed grids and blocks. Engine-free (Tests/ApiTests). Any thread.
/// </summary>
internal sealed class ReaderGate
{
    long _readAt; volatile bool _open;

    /// <summary>A reader's call at <paramref name="now"/>: open (again).</summary>
    public void Touch(long now) { System.Threading.Volatile.Write(ref _readAt, now); _open = true; }

    /// <summary>Open at <paramref name="now"/>: a reader called within idleTicks. closedNow: this call found it idle and
    /// closed it - the caller drops what it kept.</summary>
    public bool Open(long now, long idleTicks, out bool closedNow)
    {
        closedNow = false;
        if (!_open) return false;
        if (now - System.Threading.Volatile.Read(ref _readAt) <= idleTicks) return true;
        _open = false; closedNow = true;
        return false;
    }

    public void Close() => _open = false;
    public bool IsOpen => _open;
}
