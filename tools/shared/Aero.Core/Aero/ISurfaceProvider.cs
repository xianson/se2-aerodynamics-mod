using System.Numerics;
using Aero.Core.Grids;

namespace Aero.Core.Aero;

/// <summary>
/// Mirrors SE2's CubeGridComponent.BlocksChangedArgs.
/// Batches adds and removes into a single event, matching the game's signal.
///
/// In SE2, this fires from CubeGridComponent.CommitBlockChanges() after
/// blocks have been added/removed from the grid. Both lists may be populated
/// simultaneously (e.g., block replacement). "Merge" in SE2 is just a
/// BlocksChanged with a large AddedBlocks list — there is no separate merge signal.
/// </summary>
public readonly struct BlocksChangedArgs
{
    public readonly IReadOnlyList<Block> AddedBlocks;
    public readonly IReadOnlyList<Block> RemovedBlocks;

    /// <summary>True if all blocks were removed (grid destroyed).</summary>
    public readonly bool AllBlocksRemoved;

    public BlocksChangedArgs(
        IReadOnlyList<Block>? added = null,
        IReadOnlyList<Block>? removed = null,
        bool allBlocksRemoved = false)
    {
        AddedBlocks = added ?? Array.Empty<Block>();
        RemovedBlocks = removed ?? Array.Empty<Block>();
        AllBlocksRemoved = allBlocksRemoved;
    }

    public static BlocksChangedArgs Added(IReadOnlyList<Block> blocks) => new(added: blocks);
    public static BlocksChangedArgs Added(Block block) => new(added: new[] { block });
    public static BlocksChangedArgs Removed(IReadOnlyList<Block> blocks) => new(removed: blocks);
    public static BlocksChangedArgs Removed(Block block) => new(removed: new[] { block });
}

/// <summary>
/// Mirrors SE2's BlockOctreeComponent.BeforeSplitGridSignal argument.
/// Contains the groups of blocks that will depart the grid.
/// Each group becomes a separate new grid entity.
/// </summary>
public readonly struct GridSplitArgs
{
    /// <summary>
    /// Each entry is a set of blocks that will form a new grid.
    /// SE2's signal provides PooledList&lt;HashSet&lt;CubeBlockComponent&gt;&gt;.
    /// </summary>
    public readonly IReadOnlyList<IReadOnlyCollection<Block>> DepartingGroups;

    public GridSplitArgs(IReadOnlyList<IReadOnlyCollection<Block>> departingGroups)
    {
        DepartingGroups = departingGroups;
    }

    /// <summary>All departing blocks across all groups (convenience).</summary>
    public IEnumerable<Block> AllDepartedBlocks
    {
        get
        {
            foreach (var group in DepartingGroups)
                foreach (var block in group)
                    yield return block;
        }
    }

    /// <summary>Total number of blocks departing.</summary>
    public int TotalDepartedCount
    {
        get
        {
            int count = 0;
            foreach (var group in DepartingGroups)
                count += group.Count;
            return count;
        }
    }
}

/// <summary>
/// Provides the aerodynamic surface of a grid — the set of exposed faces
/// that interact with airflow.
///
/// Interface modeled after SE2's CubeGridComponent / BlockOctreeComponent signals:
///
///   Build()           — initial construction or full reset
///   OnBlocksChanged() — mirrors BlocksChangedSignal (batched adds + removes)
///   OnGridSplit()     — mirrors BeforeSplitGridSignal (departed block groups)
///
/// SE2 has NO grid merge signal. When grids "merge" (e.g., welding a connector),
/// the smaller grid's blocks are added to the larger grid via BlocksChangedSignal
/// with a large AddedBlocks list. Use OnBlocksChanged() for this.
///
/// Implementations decide HOW to find exposed faces:
///   - CubeSurfaceProvider: neighbor lookup in cell map (cubes only, fast)
///   - FaceGroupSurfaceProvider: merged coplanar rectangles (fewer faces)
///   - IncrementalCubeSurfaceProvider: O(1) per-block updates
///   - Se2CornerSurfaceProvider: SE2's 8-corner approach (for comparison)
/// </summary>
public interface ISurfaceProvider
{
    /// <summary>Human-readable name (for benchmarks).</summary>
    string Name { get; }

    /// <summary>All exposed surface faces.</summary>
    IReadOnlyList<SurfaceFace> Faces { get; }

    /// <summary>Faces grouped by unique normal direction.</summary>
    IReadOnlyList<NormalGroup> NormalGroups { get; }

    /// <summary>Total exposed face count.</summary>
    int FaceCount { get; }

    /// <summary>Number of unique normal groups.</summary>
    int GroupCount { get; }

    /// <summary>Block size used for the last build (meters).</summary>
    float BlockSize { get; }

    // ─── Lifecycle ────────────────────────────────────────────────

    /// <summary>
    /// Full rebuild from scratch. Call on first use or after bulk changes.
    /// </summary>
    void Build(Grid grid, float blockSize);

    /// <summary>
    /// Blocks were added and/or removed from the grid.
    /// Mirrors SE2's CubeGridComponent.BlocksChangedSignal.
    ///
    /// Called AFTER the blocks have been added to / removed from the Grid.
    /// Both AddedBlocks and RemovedBlocks may be populated simultaneously.
    ///
    /// This is also how "merge" works: the merged grid's blocks appear
    /// as a large AddedBlocks list.
    /// </summary>
    void OnBlocksChanged(Grid grid, in BlocksChangedArgs args);

    /// <summary>
    /// Grid is splitting. Some block groups will depart to new grid entities.
    /// Mirrors SE2's BlockOctreeComponent.BeforeSplitGridSignal.
    ///
    /// Called BEFORE the blocks are removed — the grid still contains them.
    /// After this signal, SE2 removes the blocks (triggering BlocksChangedSignal
    /// with RemovedBlocks), then spawns new grid entities.
    ///
    /// Incremental providers can use the departed block list to efficiently
    /// update rather than doing a full rebuild.
    /// </summary>
    void OnGridSplit(Grid grid, in GridSplitArgs args);
}
