#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace AeroMod;

/// <summary>
/// Pluggable wing detection — finds lifting surfaces from grid geometry.
/// Called at build-time; results cached until grid changes.
/// </summary>
public interface IWingDetector
{
    /// <summary>Full detection from scratch.</summary>
    List<LiftingSurface> Detect(IGridAccessor grid, ISurfaceProvider surface, float blockSize);

    /// <summary>
    /// Incremental update: re-detect only wings affected by the changed cells.
    /// Falls back to full Detect if no cached state exists.
    /// </summary>
    List<LiftingSurface> Update(IGridAccessor grid, ISurfaceProvider surface, float blockSize,
        IReadOnlyList<Vector3I> addedCells, IReadOnlyList<Vector3I> removedCells);

    /// <summary>Clear cached state (forces next call to do full detection).</summary>
    void Invalidate();
}
