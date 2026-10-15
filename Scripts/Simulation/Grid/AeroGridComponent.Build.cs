using System;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.BlockOctrees;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.GameSystems.OWT;

#pragma warning disable
namespace AeroMod;

// The full rebuild: the scheduler's callbacks, the background build (surface, classify, wings, force table) and the swap.
public partial class AeroGridComponent
{
    // ── Scheduler callbacks (called by AeroScheduler) ──

    /// <summary>
    /// Called by the scheduler when this grid's turn arrives.
    /// Starts (or restarts) the staggered surface build on the back buffer.
    /// </summary>
    internal void BeginStaggeredBuild()
    {
        _blockSize = DetectBlockSize();
        StartBackgroundBuild();
        _staggeredBuildActive = true;
        _rebuildRestartNeeded = false;
        _fullRebuildNeeded = false;
        _pendingAddedCells.Clear();
        _pendingRemovedCells.Clear();
    }

    /// <summary>
    /// Called by the scheduler each tick. Returns true when the build is complete (FinalizeStaggeredBuild swaps it
    /// in). A change while it runs: its result is thrown away and it starts over from a fresh capture.
    /// </summary>
    internal bool TickStaggeredBuild(int cellBudget)
    {
        if (!_staggeredBuildActive) return true;
        if (_finalizeTask == null) { StartBackgroundBuild(); return false; }
        if (!_finalizeTask.IsCompleted) return false;
        if (!_rebuildRestartNeeded) return true;
        _rebuildRestartNeeded = false;
        _pendingAddedCells.Clear();
        _pendingRemovedCells.Clear();
        _blockSize = DetectBlockSize();
        StartBackgroundBuild();
        return false;
    }

    /// <summary>
    /// A full rebuild, off the simulation thread. All the simulation thread does is capture each block's
    /// occupied-cell boxes (one per block); the worker expands them into cells, builds the spare surface from that
    /// snapshot, finishes it (adjacency, creases, normals, groups), classifies it into the spare classifier and
    /// detects wings. The simulation keeps using the active surface, classifier and wings until the swap. (On the
    /// Jetliner the build froze the server 630 ms in one frame; staggered over frames it still cost 18 ms to start
    /// and 2 ms a frame.) Classified BEFORE wing detection, which filters cavity faces by it.
    /// </summary>
    private void StartBackgroundBuild()
    {
        long tb = System.Diagnostics.Stopwatch.GetTimestamp();   // (the big-build log line reads it: always)
        var boxes = new List<(Vector3I, Vector3I)>();
        foreach (var block in _octree.GetAllCubeBlocks())
        {
            if (block == null || BlockComponentFactory.IsSelfModelled(block)) continue;
            foreach (var g in block.GetTransformedOccupiedCellGroups()) boxes.Add((g.Min, g.Max));
        }
        // the air-breathing engines (flow-through: their intakes and exhausts are no walls) - the thruster list first, if
        // the blocks changed it (the build would otherwise take the engines of the shape before)
        if (_thrusterCacheDirty && _blockSize > 0)
        {
            OffsetThrustJob.RebuildThrusterCache(_octree, _blockSize, _thrusterCache, Entity);
            _thrusterCacheDirty = false; _rcsGeomCount = -1; ThrustersRebuilt();
        }
        var engines = EngineBoxes();
        _engines = engines;
        AeroCost.Begin.Stop(tb);
        AeroExport.Write($"{Entity?.DebugName}_{LastMass / 1000f:F0}t", boxes, _blockSize, LastMass);
        var surface = _buildSurface;
        var manifold = _buildManifold;
        var detector = _model.Detector;
        float blockSize = _blockSize;
        // The centre of mass the table's rotation map (and PredictHullForces' torque) is taken about: the grid's own, now.
        // (It was the last one seen in air - (0,0,0) for every table built before the grid met air: in space, or on
        //  entering gravity. Turning was then modelled about the grid's origin - 72 m from the Hercules' centre of mass - and
        //  the air's response to rotation was nonsense. Tests/ThrustTests on dumped ships)
        var tableCom = PhysicsHack.TryGetMassProperties(Data, out _, out var massCom) ? massCom : _lastComLocal;
        int cellScale = SnapshotGridAccessor.CellScale(blockSize);
        var cellGeo = SnapshotGridAccessor.CellGeometry(cellScale);
        _cellScale = cellScale;
        _builtTable = null; _builtChunks = null;
        _builtWings = null; _builtShadow = null;
        var spareShadow = _spareShadow; _spareShadow = null;
        // (its own thread, not a thread-pool one: a rebuild runs ~1 s on a big grid, and the game's own async work
        // - physics queries, ray casts - waits on the pool: back-to-back rebuilds starved it, 0.3-0.6 s stalls)
        _lastRebuildStart = System.Diagnostics.Stopwatch.GetTimestamp();
        // A big grid's build allocates hundreds of MB: meanwhile the runtime should not stop the game for a blocking
        // full collection (it did, 5 s, on Red Ship's first build) - concurrent ones only (AeroGc).
        long cellEstimate = 0;
        foreach (var (lo, hi) in boxes) cellEstimate += (long)(hi.X - lo.X + 1) * (hi.Y - lo.Y + 1) * (hi.Z - lo.Z + 1);
        bool bigBuild = cellEstimate > AeroGc.BigBuildCells;
        if (bigBuild) { AeroGc.Enter(); Log.Default?.Info($"[AERO] big build start: grid {Entity?.DebugName}, ~{cellEstimate} cells (capture {AeroCost.Ms(tb):F0} ms)"); }
        _finalizeTask = AeroWork.Enqueue(() => CachedPriority, () =>
        {
          // (AeroWork: a hundred grids arriving at once queue for its workers)
          try
          {
            // below the game's own threads: a rebuild must never compete with a frame
            try { System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Lowest; } catch { }
            long t = AeroCost.Start(), tStart = t;
            var sw = System.Diagnostics.Stopwatch.StartNew(); var ms = new double[6];
            // a shape seen before (a reloaded world, a pasted blueprint, a copy): its table from disk, no build
            var phys = TablePhysics;
            string cacheKey = AeroTableCache.Enabled ? AeroTableCache.Key(boxes, blockSize, cellScale, phys, engines) : null;
            if (cacheKey != null && AeroTableCache.TryLoad(cacheKey, phys, out var cTable, out var cChunks, out var cWings))
            {
                foreach (var (a, b) in boxes) cChunks.CountBlock(cChunks.KeyOf(new Vector3(a.X + b.X + 1, a.Y + b.Y + 1, a.Z + b.Z + 1) * 0.125f), 1); cChunks.SealCounts();
                surface.ReleaseAll();   // (the swap puts this surface live: empty, as a big grid's is after its build)
                _builtWings = cWings; _builtShadow = null; _builtChunks = cChunks; _builtTable = cTable;
                FromCache = true;
                _rebuildNote = $"from the table cache in {sw.Elapsed.TotalMilliseconds:F0} ms ({cChunks.ChunkCount} chunks, {cWings.Count} wings)";
                Log.Default?.Info($"[AERO] grid {Entity?.DebugName}: {_rebuildNote}");
                return;
            }
            FromCache = false;
            long m0 = GC.GetAllocatedBytesForCurrentThread(), m;
            // large-block grids at 0.5 m (majority of their 0.25 m cells): 4.5x fewer faces, forces within ~8%
            var snapshot = cellScale > 1 ? new SnapshotGridAccessor(boxes).CoarsenMajority(cellScale) : new SnapshotGridAccessor(boxes);
            surface.CellSize = cellGeo.size; surface.CellOffset = cellGeo.offset;
            long aSnap = (m = GC.GetAllocatedBytesForCurrentThread()) - m0; m0 = m; ms[0] = sw.Elapsed.TotalMilliseconds;
            surface.BeginBuild(snapshot, blockSize);
            while (!surface.AddCellBatch(int.MaxValue)) { }
            long aFaces = (m = GC.GetAllocatedBytesForCurrentThread()) - m0; m0 = m; ms[1] = sw.Elapsed.TotalMilliseconds;
            AeroCost.Batch.Stop(t); t = AeroCost.Start();
            surface.FinalizeBuild();
            long aFin = (m = GC.GetAllocatedBytesForCurrentThread()) - m0; m0 = m; ms[2] = sw.Elapsed.TotalMilliseconds;
            AeroCost.FinSurface.Stop(t); t = AeroCost.Start();
            manifold.Classify(surface);
            long aCls = (m = GC.GetAllocatedBytesForCurrentThread()) - m0; m0 = m; ms[3] = sw.Elapsed.TotalMilliseconds;
            AeroCost.FinClassify.Stop(t); t = AeroCost.Start();
            if (detector is ConnectedComponentWingDetector cc) { cc.Manifold = manifold; cc.ManifoldSurface = surface; cc.CellSize = cellGeo.size; cc.CellOffset = cellGeo.offset; }
            detector.Invalidate();
            _builtWings = detector.Detect(snapshot, surface, blockSize);
            long aWing = (m = GC.GetAllocatedBytesForCurrentThread()) - m0; m0 = m; ms[4] = sw.Elapsed.TotalMilliseconds;
            AeroCost.FinWings.Stop(t); t = AeroCost.Start();
            var shadow = spareShadow ?? new PrecomputedShadowMap();
            shadow.Manifold = manifold;
            // (no 26-direction precompute: the force table bakes its own occlusion; the face loop - only before a
            //  first table - fills the map as it goes)
            _builtShadow = shadow;
            long aShd = (m = GC.GetAllocatedBytesForCurrentThread()) - m0; ms[5] = sw.Elapsed.TotalMilliseconds;
            // the force table: the faces' forces for every direction (per frame a lookup, whatever the grid's size)
            var builder = RentTableBuilder();
            try
            {
                var tmp = new LiftingSurfaceModel(builder, liftModel: new CompressibleWingModel());
                tmp.InstallWings(_builtWings ?? new List<LiftingSurface>());
                tmp.BuildFaceOverrideIndex(surface, new List<IAeroBlockComponent>());
                tmp.ExcludeWingFaces(builder, surface.Version);
                if (DampedShadowedDragModel.FlowThroughEngines && engines.Count > 0)
                    builder.SetThroughFaces(DampedShadowedDragModel.ThroughMask(surface.Faces, surface.CellSize, engines, out _), surface.Version);
                // a grid with no forces yet gets a coarse table at once (TickLocal installs it), the full one after
                if (_model.Table == null)
                {
                    var tq = System.Diagnostics.Stopwatch.StartNew();
                    _quickTable = builder.BuildForceTable(snapshot, surface, manifold, tableCom, n: 4, nj: 2);
                    QuickMs = tq.Elapsed.TotalMilliseconds;
                    if (bigBuild || surface.FaceCount > BigSurfaceFaces) Log.Default?.Info($"[AERO] quick table for grid {Entity?.DebugName}: {QuickMs:F0} ms, {sw.Elapsed.TotalMilliseconds:F0} ms after the build began");
                }
                var chunks = new ChunkedTable(8, ChunkSize, new FacePhysics(builder));
                _builtTable = builder.BuildForceTable(snapshot, surface, manifold, tableCom, chunks: chunks);
                // (saved before the counts and before anything shares it; small grids build in a moment, not kept)
                if (cacheKey != null && sw.Elapsed.TotalMilliseconds > CacheAboveMs) AeroTableCache.Save(cacheKey, _builtTable, chunks, _builtWings);
                foreach (var (a, b) in boxes) chunks.CountBlock(chunks.KeyOf(new Vector3(a.X + b.X + 1, a.Y + b.Y + 1, a.Z + b.Z + 1) * 0.125f), 1); chunks.SealCounts();
                _builtChunks = chunks;
            }
            finally { ReturnTableBuilder(builder); }
            ms[5] = sw.Elapsed.TotalMilliseconds;
            _rebuildNote = $"{surface.FaceCount} faces from {snapshot.CellCount} cells ({boxes.Count} boxes): snapshot {ms[0]:F0} ms {aSnap / 1048576.0:F1} MB, faces {ms[1] - ms[0]:F0} ms {aFaces / 1048576.0:F1} MB, finalize {ms[2] - ms[1]:F0} ms {aFin / 1048576.0:F1} MB, classify {ms[3] - ms[2]:F0} ms {aCls / 1048576.0:F1} MB, wings {ms[4] - ms[3]:F0} ms {aWing / 1048576.0:F1} MB, shadow+table {ms[5] - ms[4]:F0} ms {aShd / 1048576.0:F1} MB (table {_builtTable?.BuildMs ?? 0:F0} ms)";
            AeroCost.FinShadow.Stop(t);
            AeroCost.RebuildAlloc = $"rebuild alloc MB: snapshot {aSnap / 1048576.0:F1} faces {aFaces / 1048576.0:F1} finalize {aFin / 1048576.0:F1} classify {aCls / 1048576.0:F1} wings {aWing / 1048576.0:F1} shadow {aShd / 1048576.0:F1} ({surface.FaceCount} faces)";
          }
          finally { if (bigBuild) AeroGc.Exit(); }
        });
    }

    /// <summary>
    /// Called by the scheduler when TickStaggeredBuild returns true.
    /// Swaps buffers and runs wing detection.
    /// </summary>
    internal void FinalizeStaggeredBuild()
    {
        if (_finalizeTask != null)
        {
            var task = _finalizeTask;
            _finalizeTask = null;
            if (task.IsFaulted)
            {
                Log.Default?.Info($"[AERO] background rebuild failed ({task.Error?.Message}); rebuilding in one go");
                _buildSurface.CellSize = _surface.CellSize = 0.25f; _buildSurface.CellOffset = _surface.CellOffset = 0f; _cellScale = 1;
            }
            else
            {
                // The swap (simulation thread): the finished surface, its classification and its wings go live.
                long ts = AeroCost.Start();
                if (_buildSurface.FaceCount > BigSurfaceFaces) Log.Default?.Info($"[AERO] big rebuild of grid {Entity?.DebugName} ({LastMass / 1000f:F0} t): {_rebuildNote} | wings: {ConnectedComponentWingDetector.LastProfile} | finalize: {_buildSurface.LastFinalizeProfile}");
                (_surface, _buildSurface) = (_buildSurface, _surface);
                (_manifold, _buildManifold) = (_buildManifold, _manifold);
                _staggeredBuildActive = false;
                if (_model.Detector is ConnectedComponentWingDetector cc) { cc.Manifold = _manifold; cc.ManifoldSurface = _surface; }
                _model.InstallWings(_builtWings);
                _builtWings = null;
                if (_model.InnerModel is DampedShadowedDragModel dsm && _builtShadow != null) _spareShadow = dsm.InstallShadowMap(_builtShadow);
                if (_builtTable != null)
                {
                    _model.InstallForceTable(_builtTable);
                    _chunks = _builtChunks; _builtChunks = null; _chunkGen++;
                    // a big grid flies on its table: its surfaces and per-face arrays go (~30 MB for Red Ship; damage
                    // rebuilds it whole anyway)
                    if (_cellScale > 1 || _surface.FaceCount > BigSurfaceFaces || FromCache)
                    {
                        _lastFaces = _surface.FaceCount;
                        _surface.ReleaseAll(); _buildSurface.ReleaseAll();
                        if (_model.InnerModel is DampedShadowedDragModel rdsm) rdsm.ReleaseFaces();
                        _spareShadow = null;
                    }
                }
                _builtTable = null;
                _builtShadow = null;
                _components.Clear();
                _factory.CreateAll(_octree, _blockSize, _components);
                MeasureSurfaces();
                _faceOverridesDirty = true;
                AeroCost.FinComponents.Stop(ts);
                return;
            }
            // (failed: the old single-frame path below, from a fresh staging)
            _gridAccessor.SetOctree(_octree);
            _buildSurface.BeginBuild(_gridAccessor, _blockSize);
            while (!_buildSurface.AddCellBatch(int.MaxValue)) { }
        }
        long tf = AeroCost.Start();
        _buildSurface.FinalizeBuild();
        AeroCost.FinSurface.Stop(tf); tf = AeroCost.Start();

        // Swap: buildSurface becomes active, old active becomes next build buffer
        (_surface, _buildSurface) = (_buildSurface, _surface);
        _staggeredBuildActive = false;

        // Classify first: wing detection filters cavity faces by the classification of THIS surface.
        _manifold.Classify(_surface);
        AeroCost.FinClassify.Stop(tf); tf = AeroCost.Start();
        if (_model.Detector is ConnectedComponentWingDetector ccwd2) { ccwd2.Manifold = _manifold; ccwd2.ManifoldSurface = _surface; }
        _model.InvalidateWings();
        _model.DetectWings(_gridAccessor, _surface, _blockSize);
        AeroCost.FinWings.Stop(tf); tf = AeroCost.Start();

        // Rebuild all block-level aero components from scratch
        _components.Clear();
        _factory.CreateAll(_octree, _blockSize, _components);
        MeasureSurfaces();
        _faceOverridesDirty = true;
        AeroCost.FinComponents.Stop(tf);
    }

    private float DetectBlockSize()
    {
        if (_cachedBlockSize >= 0)
            return _cachedBlockSize;

        // The SMALLEST block decides (each block's largest extent, in 0.25 m cells): a large grid's every block spans
        // >= 10 cells (2.5 m) somewhere, a small grid always has ~2-cell (0.5 m) blocks. It used the FIRST block the octree
        // returned: on a small grid a thruster, battery or cockpit (6-10 cells) read it as large - coarse cells, most of
        // its wings lost.
        int smallest = int.MaxValue;
        var blocks = _octree.GetAllCubeBlocks();
        foreach (var block in blocks)
        {
            if (block == null) continue;
            int blockMax = 0;
            foreach (var cellGroup in block.GetTransformedOccupiedCellGroups())
            {
                var extent = cellGroup.Max - cellGroup.Min + Vector3I.One;
                blockMax = Math.Max(blockMax, Math.Max(extent.X, Math.Max(extent.Y, extent.Z)));
            }
            if (blockMax > 0 && blockMax < smallest) { smallest = blockMax; if (smallest < 5) break; }   // (small: settled)
        }
        if (smallest == int.MaxValue) return AeroConfig.LargeBlockSize;   // (no blocks yet: not cached - asked again)
        _cachedBlockSize = smallest >= 5 ? AeroConfig.LargeBlockSize : AeroConfig.SmallBlockSize;
        return _cachedBlockSize;
    }
}
