using System;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.BlockOctrees;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.GameSystems.OWT;

#pragma warning disable
namespace AeroMod;

/// <summary>
/// Per-grid aerodynamics component. Builds surface, detects wings.
/// Force application deferred until VRage.Physics access is resolved.
/// </summary>
[WhenSimulated]
public partial class AeroGridComponent : Component, IInSceneListener
{
    [Keen.VRage.DCS.Annotations.Component]
    private readonly CubeGridComponent _grid;

    [Keen.VRage.DCS.Annotations.Component]
    private readonly BlockOctreeComponent _octree;

    // ── Aero pipeline state ──

    private Se2GridAccessor _gridAccessor;
    private SmoothSurfaceProvider _surface;       // active surface (used for force computation)
    private SmoothSurfaceProvider _buildSurface;  // back buffer (used during staggered rebuild)
    private LiftingSurfaceModel _model;
    private float _blockSize;
    private bool _dirty;
    private bool _fullRebuildNeeded;
    private bool _initialized;

    // ── Staggered rebuild ──
    private bool _staggeredBuildActive;
    private const int CellsPerTick = 5000;

    // ── Deferred full wing re-detection ──
    private bool _wingsDirty;
    private int _wingCooldownTicks;
    private const int WingDetectCooldown = 60; // ~1s at 60Hz — full detection with ray-march

    // ── Accumulated changes for incremental wing update ──
    private List<Vector3I> _wingAddedCells = new();
    private List<Vector3I> _wingRemovedCells = new();

    // ── Batched block change accumulation ──
    private List<Vector3I> _pendingAddedCells = new();
    private List<Vector3I> _pendingRemovedCells = new();

    // Shared across all grids
    private static AtmosphereBridge _atmosphereBridge;

    /// <summary>Last computed result.</summary>
    internal AeroResult LastResult;
    internal bool HasResult;

    // ── Lifecycle ──

    void IInSceneListener.OnAddedToScene()
    {
        Log.Default?.Info("[AERO] AeroGridComponent.OnAddedToScene()");

        _gridAccessor = new Se2GridAccessor(_octree);
        _surface = new SmoothSurfaceProvider();
        _buildSurface = new SmoothSurfaceProvider();

        var innerDrag = new DampedShadowedDragModel();
        _model = new LiftingSurfaceModel(innerDrag, liftModel: new CompressibleWingModel());

        _atmosphereBridge ??= new AtmosphereBridge();

        // Attach ObservedWorldTransform so the draw job triggers on transform changes
        ObservedWorldTransform.AttachTo(Data, Data);

        // Enable debug draw globally (off by default)
        Keen.VRage.Core.GlobalDebugSettings.Default.EnabledDebugDraw = true;

        // Defer heavy work (surface build, wing detection) to first compute call
        _dirty = true;
        _fullRebuildNeeded = true;
        _initialized = true;
    }

    void IInSceneListener.OnBeforeRemovedFromScene()
    {
        _initialized = false;
        ObservedWorldTransform.DetachFrom(Data, Data);
    }

    // ── Block change signal ──

    [CubeGridComponent.BlocksChangedSignal]
    private void OnBlocksChanged(CubeGridComponent.BlocksChangedArgs blockData)
    {
        if (blockData.IsParallelInit) return;

        if (blockData.AllBlocksRemoved)
        {
            _dirty = true;
            _fullRebuildNeeded = true;
            _pendingAddedCells.Clear();
            _pendingRemovedCells.Clear();
            return;
        }

        // Accumulate changed cells for batched incremental update
        foreach (var block in blockData.RemovedBlocks)
        {
            if (block == null) continue;
            foreach (var cellGroup in block.GetTransformedOccupiedCellGroups())
            {
                var min = cellGroup.Min;
                var max = cellGroup.Max;
                for (int x = min.X; x <= max.X; x++)
                    for (int y = min.Y; y <= max.Y; y++)
                        for (int z = min.Z; z <= max.Z; z++)
                            _pendingRemovedCells.Add(new Vector3I(x, y, z));
            }
        }

        foreach (var block in blockData.AddedBlocks)
        {
            if (block == null) continue;
            foreach (var cellGroup in block.GetTransformedOccupiedCellGroups())
            {
                var min = cellGroup.Min;
                var max = cellGroup.Max;
                for (int x = min.X; x <= max.X; x++)
                    for (int y = min.Y; y <= max.Y; y++)
                        for (int z = min.Z; z <= max.Z; z++)
                            _pendingAddedCells.Add(new Vector3I(x, y, z));
            }
        }

        _dirty = true;
    }

    // ── Compute (called from debug draw for now) ──

    internal void TryCompute(WorldTransform wt, float density, Vector3 linearVelocity, Vector3 angularVelocity, Vector3 centerOfMass)
    {
        HasResult = false;

        if (!_initialized) return;
        if (density < AeroConfig.MinDensity) return;

        float speed = linearVelocity.Length();
        if (speed < AeroConfig.MinSpeed) return;

        // ── Staggered rebuild: continue processing cell batches ──
        if (_staggeredBuildActive)
        {
            if (_dirty)
            {
                // Topology changed mid-build — restart the staggered build
                _buildSurface.AbortBuild();
                _gridAccessor.SetOctree(_octree);
                _blockSize = DetectBlockSize();
                _buildSurface.BeginBuild(_gridAccessor, _blockSize);
                _dirty = false;
                _fullRebuildNeeded = false;
                _pendingAddedCells.Clear();
                _pendingRemovedCells.Clear();
            }

            bool done = _buildSurface.AddCellBatch(CellsPerTick);
            if (done)
            {
                _buildSurface.FinalizeBuild();

                // Swap: buildSurface becomes active, old active becomes next build buffer
                (_surface, _buildSurface) = (_buildSurface, _surface);
                _staggeredBuildActive = false;

                _model.InvalidateWings();
                _model.DetectWings(_gridAccessor, _surface, _blockSize);
            }
            // Fall through — use _surface (old data) for force computation this tick
        }
        else if (_dirty)
        {
            _gridAccessor.SetOctree(_octree);
            _blockSize = DetectBlockSize();

            if (_fullRebuildNeeded || _surface.FaceCount == 0)
            {
                // Start staggered full rebuild
                _buildSurface.BeginBuild(_gridAccessor, _blockSize);
                _staggeredBuildActive = true;
                _fullRebuildNeeded = false;
            }
            else
            {
                // Incremental surface update (immediate, single tick)
                var args = new BlocksChangedArgs(
                    added: _pendingAddedCells.Count > 0 ? _pendingAddedCells : null,
                    removed: _pendingRemovedCells.Count > 0 ? _pendingRemovedCells : null);
                _surface.OnBlocksChanged(_gridAccessor, args);

                // Incremental wing update (cheap — only re-processes affected wings)
                _model.UpdateWings(_gridAccessor, _surface, _blockSize,
                    _pendingAddedCells, _pendingRemovedCells);

                // Accumulate for deferred full detection (ray-march correctness pass)
                _wingAddedCells.AddRange(_pendingAddedCells);
                _wingRemovedCells.AddRange(_pendingRemovedCells);
                _wingsDirty = true;
                _wingCooldownTicks = WingDetectCooldown;
            }

            _pendingAddedCells.Clear();
            _pendingRemovedCells.Clear();
            _dirty = false;
        }

        // ── Deferred full wing detection (correctness pass with ray-march) ──
        if (_wingsDirty && !_staggeredBuildActive)
        {
            if (--_wingCooldownTicks <= 0)
            {
                _model.InvalidateWings();
                _model.DetectWings(_gridAccessor, _surface, _blockSize);
                _wingsDirty = false;
                _wingAddedCells.Clear();
                _wingRemovedCells.Clear();
            }
        }

        // ── Force computation (always uses _surface, even during staggered build) ──
        if (_surface.FaceCount == 0) return;

        Vector3 localLinVel = WorldTransform.TransformDirectionInv(linearVelocity, wt);
        Vector3 localAngVel = WorldTransform.TransformDirectionInv(angularVelocity, wt);

        AtmosphereState atmo = _atmosphereBridge.GetState(density);

        _lastVelocityLocal = localLinVel;
        _lastComLocal = centerOfMass;
        _lastDensity = density;

        var ctx = new AeroContext(
            _gridAccessor,
            _surface,
            localLinVel,
            atmo,
            centerOfMass,
            _blockSize,
            localAngVel);

        LastResult = _model.Compute(ctx);
        HasResult = true;
    }

    // ── Helpers ──

    private float DetectBlockSize()
    {
        var blocks = _octree.GetAllCubeBlocks();
        foreach (var block in blocks)
        {
            if (block == null) continue;
            foreach (var cellGroup in block.GetTransformedOccupiedCellGroups())
            {
                var extent = cellGroup.Max - cellGroup.Min + Vector3I.One;
                int maxExtent = Math.Max(extent.X, Math.Max(extent.Y, extent.Z));
                return maxExtent >= 5 ? AeroConfig.LargeBlockSize : AeroConfig.SmallBlockSize;
            }
        }
        return AeroConfig.LargeBlockSize;
    }
}
