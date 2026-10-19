using Aero.Core.Math;
using Aero.Core.Octree;

namespace Aero.Core.Grids;

/// <summary>
/// A grid of blocks with spatial indexing via octree.
/// Supports add, remove, split (on disconnect), and merge operations.
/// </summary>
public class Grid
{
    private static int _nextGridId;

    public int Id { get; }
    public BlockOctree Octree { get; } = new();

    /// <summary>All blocks in this grid, keyed by block ID.</summary>
    private readonly Dictionary<int, Block> _blocks = new();

    /// <summary>Map from grid cell position → block occupying it.</summary>
    private readonly Dictionary<Vector3I, Block> _cellMap = new();

    public IReadOnlyDictionary<int, Block> Blocks => _blocks;
    public int BlockCount => _blocks.Count;

    public Grid()
    {
        Id = Interlocked.Increment(ref _nextGridId);
    }

    // ─── Add ───────────────────────────────────────────────────────

    /// <summary>
    /// Add a block to this grid. Throws if any occupied cell is already taken.
    /// </summary>
    public void AddBlock(Block block)
    {
        // Check for overlap
        foreach (var cell in block.EnumerateCells())
        {
            if (_cellMap.ContainsKey(cell))
                throw new InvalidOperationException(
                    $"Cell {cell} already occupied by block {_cellMap[cell].Id}");
        }

        _blocks[block.Id] = block;
        block.Grid = this;

        foreach (var cell in block.EnumerateCells())
            _cellMap[cell] = block;

        Octree.Insert(block.Id, block.OccupiedCells);
    }

    /// <summary>
    /// Add multiple blocks at once.
    /// </summary>
    public void AddBlocks(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
            AddBlock(block);
    }

    // ─── Remove ────────────────────────────────────────────────────

    /// <summary>
    /// Remove a block from this grid by ID.
    /// Returns the removed block, or null if not found.
    /// </summary>
    public Block? RemoveBlock(int blockId)
    {
        if (!_blocks.TryGetValue(blockId, out var block))
            return null;

        _blocks.Remove(blockId);
        block.Grid = null;

        foreach (var cell in block.EnumerateCells())
            _cellMap.Remove(cell);

        Octree.Remove(block.OccupiedCells);
        return block;
    }

    // ─── Query ─────────────────────────────────────────────────────

    /// <summary>Get the block at a grid position, or null.</summary>
    public Block? GetBlockAt(Vector3I position) =>
        _cellMap.TryGetValue(position, out var block) ? block : null;

    /// <summary>Get all blocks whose cells intersect the given range.</summary>
    public IReadOnlySet<Block> GetBlocksIn(BoundingBoxI range)
    {
        var results = new HashSet<Block>();
        for (int x = range.Min.X; x <= range.Max.X; x++)
            for (int y = range.Min.Y; y <= range.Max.Y; y++)
                for (int z = range.Min.Z; z <= range.Max.Z; z++)
                    if (_cellMap.TryGetValue(new Vector3I(x, y, z), out var block))
                        results.Add(block);
        return results;
    }

    // ─── Adjacency ─────────────────────────────────────────────────

    private static readonly Vector3I[] Directions =
    {
        new(1, 0, 0), new(-1, 0, 0),
        new(0, 1, 0), new(0, -1, 0),
        new(0, 0, 1), new(0, 0, -1),
    };

    /// <summary>
    /// Get all blocks that are face-adjacent to the given block (share a face on the grid).
    /// </summary>
    public HashSet<Block> GetAdjacentBlocks(Block block)
    {
        var neighbors = new HashSet<Block>();
        foreach (var cell in block.EnumerateCells())
        {
            foreach (var dir in Directions)
            {
                var neighbor = cell + dir;
                if (_cellMap.TryGetValue(neighbor, out var adj) && adj != block)
                    neighbors.Add(adj);
            }
        }
        return neighbors;
    }

    // ─── Split ─────────────────────────────────────────────────────

    /// <summary>
    /// Check if removing a block would split the grid into disconnected groups.
    /// Does NOT actually remove the block.
    /// </summary>
    public List<HashSet<Block>> FindDisconnectedGroups(int blockIdToRemove)
    {
        if (!_blocks.TryGetValue(blockIdToRemove, out var removedBlock))
            return new List<HashSet<Block>>();

        // Find all blocks adjacent to the removed block — these are the "seed" blocks
        // that might end up in separate groups.
        var seeds = GetAdjacentBlocks(removedBlock);
        if (seeds.Count <= 1)
            return new List<HashSet<Block>>(); // 0-1 neighbors → no split possible

        // BFS/flood-fill from each seed, skipping the removed block
        var visited = new HashSet<int>();
        var groups = new List<HashSet<Block>>();

        foreach (var seed in seeds)
        {
            if (visited.Contains(seed.Id))
                continue;

            var group = new HashSet<Block>();
            var queue = new Queue<Block>();
            queue.Enqueue(seed);
            visited.Add(seed.Id);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                group.Add(current);

                foreach (var cell in current.EnumerateCells())
                {
                    foreach (var dir in Directions)
                    {
                        var neighbor = cell + dir;
                        if (_cellMap.TryGetValue(neighbor, out var adj) &&
                            adj.Id != blockIdToRemove &&
                            visited.Add(adj.Id))
                        {
                            queue.Enqueue(adj);
                        }
                    }
                }
            }

            groups.Add(group);
        }

        // Add blocks not adjacent to the removed block — they must be in one of the
        // groups already (connected through other paths) or they are isolated.
        // Actually, the BFS from seeds will have found them already via connectivity.
        // But we should include any blocks that weren't neighbors of the removed block
        // but are still in the grid — they connect to one of the seed groups.

        // Any remaining blocks not visited are blocks that aren't connected to any
        // neighbor of the removed block — they form their own group.
        foreach (var block in _blocks.Values)
        {
            if (block.Id != blockIdToRemove && !visited.Contains(block.Id))
            {
                // Isolated block(s) — BFS from this one too
                var group = new HashSet<Block>();
                var queue = new Queue<Block>();
                queue.Enqueue(block);
                visited.Add(block.Id);

                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    group.Add(current);

                    foreach (var cell in current.EnumerateCells())
                    {
                        foreach (var dir in Directions)
                        {
                            var neighbor = cell + dir;
                            if (_cellMap.TryGetValue(neighbor, out var adj) &&
                                adj.Id != blockIdToRemove &&
                                visited.Add(adj.Id))
                            {
                                queue.Enqueue(adj);
                            }
                        }
                    }
                }

                groups.Add(group);
            }
        }

        return groups;
    }

    /// <summary>
    /// Remove a block and split the grid if the remaining blocks are disconnected.
    /// Returns a list of new grids created from the split (the largest group stays in this grid).
    /// Returns empty list if no split occurred.
    /// </summary>
    public List<Grid> RemoveBlockAndSplit(int blockId)
    {
        var groups = FindDisconnectedGroups(blockId);

        // Actually remove the block
        RemoveBlock(blockId);

        if (groups.Count <= 1)
            return new List<Grid>(); // No split

        // Keep the largest group in this grid, move others to new grids
        var sorted = groups.OrderByDescending(g => g.Count).ToList();

        var newGrids = new List<Grid>();
        // Skip the first (largest) group — it stays
        for (int i = 1; i < sorted.Count; i++)
        {
            var newGrid = new Grid();
            foreach (var block in sorted[i])
            {
                RemoveBlock(block.Id);
                newGrid.AddBlock(block);
            }
            newGrids.Add(newGrid);
        }

        return newGrids;
    }

    // ─── Merge ─────────────────────────────────────────────────────

    /// <summary>
    /// Merge another grid into this one, transferring all its blocks.
    /// The other grid becomes empty.
    /// </summary>
    public void Merge(Grid other)
    {
        if (other == this)
            throw new InvalidOperationException("Cannot merge a grid with itself");

        var blocksToMove = other._blocks.Values.ToList();
        foreach (var block in blocksToMove)
        {
            other.RemoveBlock(block.Id);
            AddBlock(block);
        }
    }

    /// <summary>
    /// Check if this grid is face-adjacent to another grid at any cell.
    /// </summary>
    public bool IsAdjacentTo(Grid other)
    {
        // Check if any cell in this grid is face-adjacent to any cell in the other grid
        foreach (var cell in _cellMap.Keys)
        {
            foreach (var dir in Directions)
            {
                var neighbor = cell + dir;
                if (other._cellMap.ContainsKey(neighbor))
                    return true;
            }
        }
        return false;
    }

    // ─── Explosion ────────────────────────────────────────────────

    /// <summary>
    /// Result of an explosion applied to a grid.
    /// </summary>
    public class ExplosionResult
    {
        /// <summary>Blocks destroyed by the blast.</summary>
        public List<Block> DestroyedBlocks { get; init; } = new();

        /// <summary>New grids created from disconnected fragments (largest group stays in original grid).</summary>
        public List<Grid> Fragments { get; init; } = new();

        /// <summary>Whether the explosion caused the grid to split.</summary>
        public bool DidSplit => Fragments.Count > 0;

        /// <summary>Whether the explosion destroyed the entire grid.</summary>
        public bool TotalDestruction { get; init; }
    }

    /// <summary>
    /// Apply an explosion centered at a grid cell position with a given radius.
    /// Removes all blocks whose cells fall within the blast sphere, then splits
    /// the grid if disconnected groups result.
    /// </summary>
    /// <param name="center">Explosion center in grid cell coordinates.</param>
    /// <param name="radius">Blast radius in grid cells (Euclidean distance).</param>
    /// <returns>Result containing destroyed blocks and any fragment grids.</returns>
    public ExplosionResult Explode(Vector3I center, float radius)
    {
        float r2 = radius * radius;

        // Find all blocks with any cell inside the blast sphere
        var toDestroy = new HashSet<Block>();
        foreach (var block in _blocks.Values)
        {
            foreach (var cell in block.EnumerateCells())
            {
                float dx = cell.X - center.X;
                float dy = cell.Y - center.Y;
                float dz = cell.Z - center.Z;
                if (dx * dx + dy * dy + dz * dz <= r2)
                {
                    toDestroy.Add(block);
                    break;
                }
            }
        }

        if (toDestroy.Count == 0)
            return new ExplosionResult();

        bool totalDestruction = toDestroy.Count == _blocks.Count;

        // Remove destroyed blocks
        var destroyed = new List<Block>(toDestroy.Count);
        foreach (var block in toDestroy)
        {
            RemoveBlock(block.Id);
            destroyed.Add(block);
        }

        if (totalDestruction || _blocks.Count == 0)
            return new ExplosionResult { DestroyedBlocks = destroyed, TotalDestruction = true };

        // Check for splits
        var fragments = new List<Grid>();
        if (!IsConnected())
        {
            // BFS to find all groups
            var visited = new HashSet<int>();
            var groups = new List<List<Block>>();

            foreach (var block in _blocks.Values)
            {
                if (visited.Contains(block.Id)) continue;

                var group = new List<Block>();
                var queue = new Queue<Block>();
                queue.Enqueue(block);
                visited.Add(block.Id);

                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    group.Add(current);

                    foreach (var cell in current.EnumerateCells())
                    {
                        foreach (var dir in Directions)
                        {
                            if (_cellMap.TryGetValue(cell + dir, out var adj) && visited.Add(adj.Id))
                                queue.Enqueue(adj);
                        }
                    }
                }

                groups.Add(group);
            }

            if (groups.Count > 1)
            {
                // Sort by size descending — largest stays
                groups.Sort((a, b) => b.Count.CompareTo(a.Count));

                for (int i = 1; i < groups.Count; i++)
                {
                    var newGrid = new Grid();
                    foreach (var block in groups[i])
                    {
                        RemoveBlock(block.Id);
                        newGrid.AddBlock(block);
                    }
                    fragments.Add(newGrid);
                }
            }
        }

        return new ExplosionResult { DestroyedBlocks = destroyed, Fragments = fragments };
    }

    /// <summary>
    /// Apply a random explosion. Picks a random block as the center,
    /// uses the given radius. Good for stress testing.
    /// </summary>
    public ExplosionResult ExplodeRandom(Random rng, float radius)
    {
        if (_blocks.Count == 0)
            return new ExplosionResult();

        // Pick a random block
        var blocks = _blocks.Values.ToArray();
        var target = blocks[rng.Next(blocks.Length)];

        // Use the center of the block's first cell
        var cells = target.EnumerateCells().ToArray();
        var center = cells[rng.Next(cells.Length)];

        return Explode(center, radius);
    }

    /// <summary>
    /// Apply multiple random explosions in sequence. Returns aggregate results.
    /// </summary>
    public (List<Block> allDestroyed, List<Grid> allFragments) ExplodeMultiple(
        Random rng, int count, float minRadius, float maxRadius)
    {
        var allDestroyed = new List<Block>();
        var allFragments = new List<Grid>();

        for (int i = 0; i < count && _blocks.Count > 0; i++)
        {
            float radius = minRadius + (float)(rng.NextDouble() * (maxRadius - minRadius));
            var result = ExplodeRandom(rng, radius);
            allDestroyed.AddRange(result.DestroyedBlocks);
            allFragments.AddRange(result.Fragments);
        }

        return (allDestroyed, allFragments);
    }

    // ─── Connectivity ──────────────────────────────────────────────

    /// <summary>
    /// Check if all blocks in the grid form a single connected component.
    /// </summary>
    public bool IsConnected()
    {
        if (_blocks.Count <= 1)
            return true;

        var first = _blocks.Values.First();
        var visited = new HashSet<int>();
        var queue = new Queue<Block>();
        queue.Enqueue(first);
        visited.Add(first.Id);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var cell in current.EnumerateCells())
            {
                foreach (var dir in Directions)
                {
                    var neighbor = cell + dir;
                    if (_cellMap.TryGetValue(neighbor, out var adj) && visited.Add(adj.Id))
                        queue.Enqueue(adj);
                }
            }
        }

        return visited.Count == _blocks.Count;
    }

    public override string ToString() => $"Grid({Id}, blocks={BlockCount})";
}
