using System.Runtime.CompilerServices;

namespace Aero.Core.Octree;

/// <summary>
/// Growable array with O(1) allocation and free, parallel to SE2's FreeList&lt;T&gt;.
/// Freed slots are recycled via an intrusive free list encoded in the slot indices.
/// Allocated slots are always contiguous when no frees have occurred (important for
/// the octree's 8-child-contiguous invariant).
/// </summary>
public sealed class FreeList<T>
{
    private T[] _items;
    private int _count;           // total slots in use (items.Length used so far)
    private int _allocatedCount;  // number of live (non-free) slots
    private int _freeHead = -1;   // head of free chain, -1 = empty
    private int[] _freeNext;      // per-slot next pointer for free chain (-1 = end, -2 = allocated)
    private readonly T _defaultValue;

    private const int AllocatedMarker = -2;

    public int Count => _count;
    public int AllocatedCount => _allocatedCount;

    public FreeList(int initialCapacity = 16, T defaultValue = default!)
    {
        _items = new T[initialCapacity];
        _freeNext = new int[initialCapacity];
        Array.Fill(_freeNext, AllocatedMarker);
        _defaultValue = defaultValue;
    }

    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _items[index];
    }

    /// <summary>
    /// Allocate a slot and return its index. If the free list is empty, appends at the end.
    /// </summary>
    public int Allocate(in T value)
    {
        int idx;
        if (_freeHead >= 0)
        {
            idx = _freeHead;
            _freeHead = _freeNext[idx];
        }
        else
        {
            if (_count >= _items.Length)
                Grow();
            idx = _count++;
        }

        _items[idx] = value;
        _freeNext[idx] = AllocatedMarker;
        _allocatedCount++;
        return idx;
    }

    public int Allocate() => Allocate(_defaultValue);

    /// <summary>
    /// Allocate N contiguous slots. Always appends at the end (skips the free list)
    /// to guarantee contiguity. This is critical for the octree's 8-child invariant.
    /// </summary>
    public int AllocateContiguous(int count, in T value)
    {
        while (_count + count > _items.Length)
            Grow();

        int first = _count;
        for (int i = 0; i < count; i++)
        {
            _items[_count] = value;
            _freeNext[_count] = AllocatedMarker;
            _count++;
            _allocatedCount++;
        }
        return first;
    }

    /// <summary>
    /// Free a previously allocated slot.
    /// </summary>
    public void Free(int index)
    {
        _items[index] = _defaultValue;
        _freeNext[index] = _freeHead;
        _freeHead = index;
        _allocatedCount--;
    }

    public bool IsAllocated(int index) => index >= 0 && index < _count && _freeNext[index] == AllocatedMarker;

    public Span<T> AsSpan() => _items.AsSpan(0, _count);

    private void Grow()
    {
        int newCap = _items.Length * 2;
        Array.Resize(ref _items, newCap);
        Array.Resize(ref _freeNext, newCap);
        Array.Fill(_freeNext, AllocatedMarker, _count, newCap - _count);
    }
}
