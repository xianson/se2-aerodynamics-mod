using System.Numerics;
using Aero.Core.Grids;

namespace Aero.Core.Aero;

/// <summary>
/// Composition-based surface provider that delegates to
/// <see cref="AdaptiveSurfaceProvider"/> for fast incremental tracking
/// and lazily provides merged face output via <see cref="FaceGroupSurfaceProvider"/>.
///
/// Design trade-offs:
///   <see cref="Faces"/> → raw (unmerged) faces from Adaptive — full face list
///       compatible with shadow maps that need per-face indexing.
///   <see cref="NormalGroups"/> → incrementally tracked from Adaptive — O(6), no rebuild.
///   <see cref="MergedFaces"/> → lazily rebuilt merged faces — fewer faces for
///       per-face force computation where face count dominates cost.
///
/// Use when you need both fast incremental updates AND the option of
/// merged faces for specific operations (e.g., per-face shadowed force
/// computation on large flat grids where merging reduces face count 10-100x).
/// </summary>
public class HybridSurfaceProvider : ISurfaceProvider
{
    private readonly AdaptiveSurfaceProvider _adaptive = new();
    private readonly FaceGroupSurfaceProvider _faceGroup = new();
    private bool _mergeDirty = true;
    private float _blockSize;
    private Grid? _lastGrid;

    public string Name => "Hybrid (adaptive + lazy merge)";

    /// <summary>
    /// Raw (unmerged) faces — one per exposed cell face. Compatible with
    /// shadow maps that index into this list by face index.
    /// </summary>
    public IReadOnlyList<SurfaceFace> Faces => _adaptive.Faces;

    /// <summary>
    /// Incrementally maintained normal groups (running sums, O(6)).
    /// </summary>
    public IReadOnlyList<NormalGroup> NormalGroups => _adaptive.NormalGroups;

    public int FaceCount => _adaptive.FaceCount;
    public int GroupCount => _adaptive.GroupCount;
    public float BlockSize => _blockSize;

    /// <summary>
    /// Adaptive provider's rebuild threshold (fraction of grid triggering full rebuild).
    /// </summary>
    public float RebuildThreshold
    {
        get => _adaptive.RebuildThreshold;
        set => _adaptive.RebuildThreshold = value;
    }

    // ─── Merged face access (lazy) ───────────────────────────────

    /// <summary>
    /// Lazily merged faces via greedy rectangle merging. Triggers a full
    /// FaceGroupSurfaceProvider rebuild when first accessed after a change.
    ///
    /// Use for per-face force computation where face count dominates cost.
    /// A 10x10 flat plate: 100 raw faces → 1 merged face.
    /// </summary>
    public IReadOnlyList<SurfaceFace> MergedFaces
    {
        get
        {
            EnsureMerged();
            return _faceGroup.Faces;
        }
    }

    /// <summary>Number of merged faces (triggers merge if dirty).</summary>
    public int MergedFaceCount
    {
        get
        {
            EnsureMerged();
            return _faceGroup.FaceCount;
        }
    }

    /// <summary>
    /// Normal groups from the merged provider (may differ slightly from
    /// Adaptive's groups due to centroid being computed from merged rectangle
    /// centers rather than individual face centers).
    /// </summary>
    public IReadOnlyList<NormalGroup> MergedNormalGroups
    {
        get
        {
            EnsureMerged();
            return _faceGroup.NormalGroups;
        }
    }

    /// <summary>
    /// Face count reduction ratio: MergedFaceCount / FaceCount.
    /// Lower is better. Triggers merge to compute.
    /// </summary>
    public float MergeRatio => FaceCount > 0 ? (float)MergedFaceCount / FaceCount : 1f;

    // ─── ISurfaceProvider lifecycle ──────────────────────────────

    public void Build(Grid grid, float blockSize)
    {
        _blockSize = blockSize;
        _lastGrid = grid;
        _adaptive.Build(grid, blockSize);
        _mergeDirty = true;
    }

    public void OnBlocksChanged(Grid grid, in BlocksChangedArgs args)
    {
        _lastGrid = grid;
        _adaptive.OnBlocksChanged(grid, args);
        _mergeDirty = true;
    }

    public void OnGridSplit(Grid grid, in GridSplitArgs args)
    {
        _lastGrid = grid;
        _adaptive.OnGridSplit(grid, args);
        _mergeDirty = true;
    }

    // ─── Internals ───────────────────────────────────────────────

    private void EnsureMerged()
    {
        if (_mergeDirty && _lastGrid != null)
        {
            _faceGroup.Build(_lastGrid, _blockSize);
            _mergeDirty = false;
        }
    }
}
