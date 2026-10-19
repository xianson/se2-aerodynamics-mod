using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Aero.Core.Math;

namespace Aero.Core.Octree;

/// <summary>
/// Block octree parallel to SE2's BlockOctreeComponent.
/// Stores block references in an expandable octree over integer grid coordinates.
///
/// Key design decisions matching SE2:
///   - Root is always at index 0 in a FreeList
///   - 8 children allocated contiguously
///   - Leaf nodes store encoded block+shape data inline (3 bytes)
///   - Boundary starts at [-4, 3] and doubles when blocks placed outside
///   - Child ordering matches SE2's GetChildBoundary (see octant layout)
/// </summary>
public class BlockOctree
{
    // ─── Node types ────────────────────────────────────────────────

    /// <summary>
    /// Packed 4-byte node matching SE2's BlockOctreeNode layout.
    /// Bytes 0-2: LeafData or encoded FirstChild pointer
    /// Byte 3: NodeData flags
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Pack = 1, Size = 4)]
    public struct OctreeNode
    {
        [FieldOffset(0)] private int _packed; // low 24 bits = data, high 8 bits = flags

        /// <summary>Raw 24-bit data field (block shape when leaf, child pointer when internal).</summary>
        public int Data24
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _packed & 0x00FFFFFF;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => _packed = (_packed & unchecked((int)0xFF000000)) | (value & 0x00FFFFFF);
        }

        /// <summary>Flags byte (bit 6 = IsLeaf, bit 7 = HasConnectedChildren, bits 0-5 = direction connectivity).</summary>
        public byte Flags
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (byte)((_packed >> 24) & 0xFF);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => _packed = (_packed & 0x00FFFFFF) | (value << 24);
        }

        public bool IsLeaf
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (Flags & (1 << 6)) != 0;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                if (value) Flags |= (byte)(1 << 6);
                else Flags &= unchecked((byte)~(1 << 6));
            }
        }

        public bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Data24 == 0;
        }

        /// <summary>
        /// For internal nodes: index of first child in FreeList.
        /// SE2 encoding: stored = (firstChild >> 3) + 1, so firstChild = (stored - 1) << 3 | 1
        /// </summary>
        public int FirstChild
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                int stored = Data24;
                stored--;
                stored <<= 3;
                return stored | 1;
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                int stored = value;
                stored >>= 3;
                stored++;
                Data24 = stored;
            }
        }

        /// <summary>Leaf data (block shape encoding) — only valid when IsLeaf.</summary>
        public int LeafData
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Data24;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => Data24 = value;
        }

        public static OctreeNode CreateEmptyLeaf()
        {
            var node = new OctreeNode();
            node.IsLeaf = true;
            return node;
        }

        public override string ToString() =>
            IsLeaf ? $"Leaf(data={LeafData}, empty={IsEmpty})" : $"Internal(firstChild={FirstChild})";
    }

    /// <summary>
    /// Encoded block + shape reference matching SE2's BlockShape.
    /// For small values: bits [0..15] = block+1, bits [16..22] = shape
    /// For large values: bit 23 set, bits [0..22] = index into large index table
    /// </summary>
    public readonly record struct BlockRef
    {
        private readonly int _encoded;

        public int BlockIndex => IsLargeIndex ? -1 : (_encoded & 0xFFFF) - 1;
        public int ShapeIndex => IsLargeIndex ? -1 : (_encoded >> 16) & 0x7F;
        public bool IsLargeIndex => (_encoded & 0x800000) != 0;
        public int LargeTableIndex => _encoded & 0x7FFFFF;
        public bool IsEmpty => _encoded == 0;
        public int Encoded => _encoded;

        private BlockRef(int encoded) => _encoded = encoded;

        public static BlockRef Create(int blockIndex, int shapeIndex, List<(int block, int shape)> largeTable)
        {
            if (shapeIndex < 128 && blockIndex < 65535)
                return new BlockRef((shapeIndex << 16) | (blockIndex + 1));
            // Large index: store in external table, encode table index with bit 23 set
            int tableIndex = largeTable.Count;
            largeTable.Add((blockIndex, shapeIndex));
            return new BlockRef(0x800000 | tableIndex);
        }

        public static BlockRef Create(int blockIndex, int shapeIndex = 0)
        {
            if (shapeIndex < 128 && blockIndex < 65535)
                return new BlockRef((shapeIndex << 16) | (blockIndex + 1));
            // Fallback: use a unique negative-offset encoding that won't collide with 0 (empty)
            // Encode as: bit 23 set + lower 23 bits = blockIndex (supports up to ~8M blocks)
            return new BlockRef(0x800000 | (blockIndex & 0x7FFFFF));
        }

        public static BlockRef FromEncoded(int encoded) => new(encoded);

        public override string ToString() => IsEmpty ? "Empty" : $"Block({BlockIndex}, Shape={ShapeIndex})";
    }

    // ─── Fields ────────────────────────────────────────────────────

    private readonly FreeList<OctreeNode> _nodes;
    private readonly OctreeNode _emptyLeaf;
    private int _blockCount;

    /// <summary>
    /// Maps encoded leaf data → occupied cells.
    /// SE2 gets this from CubeBlockComponent; we store it explicitly since we don't have the game's ECS.
    /// </summary>
    private readonly Dictionary<int, BoundingBoxI> _blockBounds = new();

    /// <summary>Current spatial boundary of the octree (inclusive).</summary>
    public BoundingBoxI Boundary { get; private set; }

    /// <summary>Number of blocks stored in the octree.</summary>
    public int BlockCount => _blockCount;

    /// <summary>Total nodes allocated in the tree.</summary>
    public int NodeCount => _nodes.AllocatedCount;

    // ─── Construction ──────────────────────────────────────────────

    public BlockOctree()
    {
        _emptyLeaf = OctreeNode.CreateEmptyLeaf();
        _nodes = new FreeList<OctreeNode>(16, _emptyLeaf);

        // SE2 starts with boundary [-4, 3] (8x8x8)
        Boundary = new BoundingBoxI(new Vector3I(-4), new Vector3I(3));

        // Root node at index 0
        _nodes.Allocate(_emptyLeaf);
    }

    // ─── Public API ────────────────────────────────────────────────

    /// <summary>
    /// Insert a block occupying the given grid cell range.
    /// </summary>
    public void Insert(int blockIndex, BoundingBoxI occupiedCells, int shapeIndex = 0)
    {
        ExpandBoundary(occupiedCells.Min, occupiedCells.Max);

        int leafData = BlockRef.Create(blockIndex, shapeIndex).Encoded;
        _blockBounds[leafData] = occupiedCells;
        UpdateBlocks(0, Boundary, occupiedCells, leafData, UpdateType.Insert, forceSplit: false);
        _blockCount++;
    }

    /// <summary>
    /// Remove a block from the given grid cell range.
    /// </summary>
    public void Remove(BoundingBoxI occupiedCells)
    {
        UpdateBlocks(0, Boundary, occupiedCells, 0, UpdateType.Remove, forceSplit: false);
        _blockCount--;
    }

    /// <summary>
    /// Query all distinct block refs whose occupied cells intersect the given range.
    /// </summary>
    public List<BlockRef> Query(BoundingBoxI queryRange)
    {
        var results = new HashSet<int>();
        QueryRange(0, Boundary, queryRange, results, exitAny: false);
        return results.Select(BlockRef.FromEncoded).ToList();
    }

    /// <summary>
    /// Look up a single grid position. Returns null if empty.
    /// </summary>
    public BlockRef? TryGet(Vector3I position)
    {
        var range = new BoundingBoxI(position, position);
        var results = new HashSet<int>();
        QueryRange(0, Boundary, range, results, exitAny: true);
        return results.Count > 0 ? BlockRef.FromEncoded(results.First()) : null;
    }

    /// <summary>
    /// Check if an area has no blocks.
    /// </summary>
    public bool IsAreaEmpty(BoundingBoxI queryRange)
    {
        var results = new HashSet<int>();
        QueryRange(0, Boundary, queryRange, results, exitAny: true);
        return results.Count == 0;
    }

    /// <summary>
    /// Get the node at the given index (for inspection/debugging).
    /// </summary>
    public OctreeNode GetNode(int index) => _nodes[index];

    // ─── Tree traversal ────────────────────────────────────────────

    private enum UpdateType { Insert, Remove }

    private void UpdateBlocks(int nodeIndex, BoundingBoxI nodeBounds, BoundingBoxI opBounds, int leafData, UpdateType type, bool forceSplit)
    {
        ref OctreeNode node = ref _nodes[nodeIndex];

        if (node.IsEmpty && type != UpdateType.Insert)
            return;

        // Root always forces subdivision
        if (nodeIndex == 0)
            forceSplit = true;

        // Insert into empty leaf — just set the data
        if (type == UpdateType.Insert && !forceSplit)
        {
            if (node.IsEmpty)
            {
                _nodes[nodeIndex].LeafData = leafData;
                return;
            }

            // Collision with existing leaf — need to re-insert the existing data after subdividing
            if (node.IsLeaf)
            {
                int existingData = node.LeafData;
                var existingBounds = _blockBounds[existingData];
                _nodes[nodeIndex] = _emptyLeaf;
                // Re-insert the existing block with its own bounds, forced to subdivide
                UpdateBlocks(nodeIndex, nodeBounds, existingBounds, existingData, UpdateType.Insert, forceSplit: true);
            }
        }

        // Ensure this node is subdivided for Insert
        if (type == UpdateType.Insert)
        {
            SubdivideIfLeaf(nodeIndex);
        }
        else if (node.IsLeaf)
        {
            return; // Remove on a leaf that doesn't match — nothing to do
        }

        int firstChild = _nodes[nodeIndex].FirstChild;

        for (int i = 0; i < 8; i++)
        {
            var childBounds = GetChildBoundary(nodeBounds, i);
            var intersection = BoundingBoxI.Intersection(opBounds, childBounds);
            if (!intersection.IsValid())
                continue;

            int childIdx = firstChild + i;

            if (type == UpdateType.Remove && _nodes[childIdx].IsLeaf)
            {
                // Remove from leaf
                _nodes[childIdx] = _emptyLeaf;
            }
            else if (intersection == childBounds)
            {
                // Operation bounds fully cover this child
                if (type == UpdateType.Insert)
                {
                    _nodes[childIdx] = _emptyLeaf;
                    _nodes[childIdx].IsLeaf = true;
                    _nodes[childIdx].LeafData = leafData;
                }
            }
            else
            {
                // Partial overlap — recurse
                UpdateBlocks(childIdx, childBounds, intersection, leafData, type, forceSplit: false);
            }
        }

        // After removal, try to collapse empty children
        if (type == UpdateType.Remove)
            TryCollapse(nodeIndex);
    }

    private void QueryRange(int nodeIndex, BoundingBoxI nodeBounds, BoundingBoxI queryRange, HashSet<int> results, bool exitAny)
    {
        var node = _nodes[nodeIndex];
        if (node.IsEmpty)
            return;

        if (node.IsLeaf)
        {
            // Leaf contains data — it's a hit if we got here (bounds already checked by caller)
            results.Add(node.LeafData);
            return;
        }

        // Internal node — recurse into children
        int firstChild = node.FirstChild;
        for (int i = 0; i < 8; i++)
        {
            var childBounds = GetChildBoundary(nodeBounds, i);
            var intersection = BoundingBoxI.Intersection(queryRange, childBounds);
            if (!intersection.IsValid())
                continue;

            QueryRange(firstChild + i, childBounds, intersection, results, exitAny);

            if (exitAny && results.Count > 0)
                return;
        }
    }

    // ─── Subdivision ───────────────────────────────────────────────

    private void SubdivideIfLeaf(int nodeIndex)
    {
        if (!_nodes[nodeIndex].IsLeaf)
            return;
        Subdivide(nodeIndex);
    }

    private void Subdivide(int nodeIndex)
    {
        Debug.Assert(_nodes[nodeIndex].IsEmpty, "Cannot subdivide non-empty leaf");

        // Allocate 8 contiguous children — must be contiguous for FirstChild encoding
        int first = _nodes.AllocateContiguous(8, _emptyLeaf);

        _nodes[nodeIndex].IsLeaf = false;
        _nodes[nodeIndex].FirstChild = first;
    }

    private void TryCollapse(int nodeIndex)
    {
        var node = _nodes[nodeIndex];
        if (node.IsLeaf)
            return;

        int firstChild = node.FirstChild;
        bool allEmpty = true;
        for (int i = 0; i < 8; i++)
        {
            if (!_nodes[firstChild + i].IsEmpty)
            {
                allEmpty = false;
                break;
            }
        }

        if (allEmpty)
        {
            // Free all 8 children and convert back to empty leaf
            for (int i = 0; i < 8; i++)
                _nodes.Free(firstChild + i);
            _nodes[nodeIndex] = _emptyLeaf;
        }
    }

    // ─── Boundary expansion (matches SE2 exactly) ──────────────────

    private void ExpandBoundary(Vector3I minPos, Vector3I maxPos)
    {
        while (minPos.X < Boundary.Min.X || minPos.Y < Boundary.Min.Y || minPos.Z < Boundary.Min.Z)
        {
            // Expand toward negative: new min = old min - size
            Boundary = new BoundingBoxI(
                Boundary.Min - Boundary.GetSizeIncludingMax(),
                Boundary.Max);
            CreateNewRoot(1); // old root goes into octant 1 (positive corner)
        }

        while (maxPos.X > Boundary.Max.X || maxPos.Y > Boundary.Max.Y || maxPos.Z > Boundary.Max.Z)
        {
            // Expand toward positive: new max = old max + size
            Boundary = new BoundingBoxI(
                Boundary.Min,
                Boundary.Max + Boundary.GetSizeIncludingMax());
            CreateNewRoot(7); // old root goes into octant 7 (negative corner)
        }
    }

    private void CreateNewRoot(int oldRootChildOffset)
    {
        var oldRoot = _nodes[0];

        // Create a new subdivided root
        var newRoot = _emptyLeaf;
        _nodes[0] = newRoot;
        Subdivide(0);

        // Place old root data into the appropriate child
        _nodes[_nodes[0].FirstChild + oldRootChildOffset] = oldRoot;
    }

    // ─── Child boundary computation (matches SE2 octant layout) ────

    /// <summary>
    /// SE2 octant layout:
    ///   0: -X +Y +Z (back-left-up)
    ///   1: +X +Y +Z (back-right-up)
    ///   2: +X -Y +Z (back-right-down)
    ///   3: -X -Y +Z (back-left-down)
    ///   4: -X +Y -Z (front-left-up)
    ///   5: +X +Y -Z (front-right-up)
    ///   6: +X -Y -Z (front-right-down)
    ///   7: -X -Y -Z (front-left-down)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BoundingBoxI GetChildBoundary(in BoundingBoxI parent, int childOffset)
    {
        var center = parent.GetCenterFloored();
        BoundingBoxI child;

        switch (childOffset)
        {
            case 0: // -X +Y +Z
                child.Min.X = parent.Min.X;   child.Min.Y = center.Y + 1;    child.Min.Z = center.Z + 1;
                child.Max.X = center.X;        child.Max.Y = parent.Max.Y;    child.Max.Z = parent.Max.Z;
                break;
            case 1: // +X +Y +Z
                child.Min = center + Vector3I.One;
                child.Max = parent.Max;
                break;
            case 2: // +X -Y +Z
                child.Min.X = center.X + 1;    child.Min.Y = parent.Min.Y;    child.Min.Z = center.Z + 1;
                child.Max.X = parent.Max.X;     child.Max.Y = center.Y;        child.Max.Z = parent.Max.Z;
                break;
            case 3: // -X -Y +Z
                child.Min.X = parent.Min.X;     child.Min.Y = parent.Min.Y;    child.Min.Z = center.Z + 1;
                child.Max.X = center.X;          child.Max.Y = center.Y;        child.Max.Z = parent.Max.Z;
                break;
            case 4: // -X +Y -Z
                child.Min.X = parent.Min.X;     child.Min.Y = center.Y + 1;    child.Min.Z = parent.Min.Z;
                child.Max.X = center.X;          child.Max.Y = parent.Max.Y;    child.Max.Z = center.Z;
                break;
            case 5: // +X +Y -Z
                child.Min.X = center.X + 1;     child.Min.Y = center.Y + 1;    child.Min.Z = parent.Min.Z;
                child.Max.X = parent.Max.X;      child.Max.Y = parent.Max.Y;    child.Max.Z = center.Z;
                break;
            case 6: // +X -Y -Z
                child.Min.X = center.X + 1;     child.Min.Y = parent.Min.Y;    child.Min.Z = parent.Min.Z;
                child.Max.X = parent.Max.X;      child.Max.Y = center.Y;        child.Max.Z = center.Z;
                break;
            case 7: // -X -Y -Z
                child.Min = parent.Min;
                child.Max = center;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(childOffset));
        }
        return child;
    }
}
