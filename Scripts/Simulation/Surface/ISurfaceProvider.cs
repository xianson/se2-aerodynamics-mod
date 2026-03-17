#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// SE2's signal gives CubeBlockComponent — the adapter converts to cells.
/// </summary>
public readonly struct BlocksChangedArgs
{
    public readonly IReadOnlyList<Vector3I> AddedCells;
    public readonly IReadOnlyList<Vector3I> RemovedCells;

    /// <summary>True if all blocks were removed (grid destroyed).</summary>
    public readonly bool AllBlocksRemoved;

    public BlocksChangedArgs(
        IReadOnlyList<Vector3I>? added = null,
        IReadOnlyList<Vector3I>? removed = null,
        bool allBlocksRemoved = false)
    {
        AddedCells = added ?? new List<Vector3I>();
        RemovedCells = removed ?? new List<Vector3I>();
        AllBlocksRemoved = allBlocksRemoved;
    }

    public static BlocksChangedArgs Added(IReadOnlyList<Vector3I> cells) => new(added: cells);
    public static BlocksChangedArgs Removed(IReadOnlyList<Vector3I> cells) => new(removed: cells);
}

/// <summary>
/// Provides the aerodynamic surface of a grid — the set of exposed faces
/// that interact with airflow.
///
/// Implementations decide HOW to find exposed faces:
///   - CubeSurfaceProvider: neighbor lookup in cell map (cubes only, fast)
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

    /// <summary>Incremented on every structural change (build, incremental update, finalize).
    /// Used by downstream caches (SoA, visArea) to detect stale data.</summary>
    int Version { get; }

    // ─── Lifecycle ────────────────────────────────────────────────

    /// <summary>Full rebuild from scratch.</summary>
    void Build(IGridAccessor grid, float blockSize);

    /// <summary>Blocks were added and/or removed from the grid.</summary>
    void OnBlocksChanged(IGridAccessor grid, in BlocksChangedArgs args);
}
