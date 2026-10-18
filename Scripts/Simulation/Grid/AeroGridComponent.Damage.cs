using System;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace AeroMod;

// Block changes and damage: the change signal, the local chunk updates (TickLocal) and seeding a broken-off piece.
public partial class AeroGridComponent
{
    // ── Block change signal ──

    [CubeGridComponent.BlocksChangedSignal]
    private void OnBlocksChanged(CubeGridComponent.BlocksChangedArgs blockData)
    {
        long t0 = AeroCost.Start();
        OnBlocksChangedCore(blockData);
        AeroCost.Blocks.Stop(t0);
    }

    private void OnBlocksChangedCore(CubeGridComponent.BlocksChangedArgs blockData)
    {
        StructBlockVersion++;   // (the structure's rebuild: StructureServer)
        if (blockData.IsParallelInit) return;
        if (!AeroSwitch.Enabled) return;   // (on again: a full rebuild, see AeroSimJob)

        if (blockData.AllBlocksRemoved)
        {
            _dirty = true;
            _fullRebuildNeeded = true;
            _thrusterCacheDirty = true;
            _gyroCacheDirty = true;
            _cachedBlockSize = -1;
            _pendingAddedCells.Clear();
            _pendingRemovedCells.Clear();
            _components.Clear();
            return;
        }

        // Accumulate changed cells for batched incremental update
        // The thruster cache is rebuilt only when a thruster came or went: rebuilding it scans every child of the
        // grid (5000 on the Red Ship), and it ran on EVERY block change (up to 37 ms a time).
        bool thrustersChanged = false;
        _patchRemoved.Clear(); _patchAdded.Clear();
        foreach (var block in blockData.RemovedBlocks)
        {
            if (block == null) continue;
            if (!thrustersChanged)
            {
                // (a set of the thrusters, refreshed when the cache is: a scan of all of them per removed block added
                //  up when a split moved thousands of blocks at once)
                if (_thrusterSetCount != _thrusterCache.Count) { _thrusterSet.Clear(); foreach (var t in _thrusterCache) if (t.ThrusterEntity != null) _thrusterSet.Add(t.ThrusterEntity); _thrusterSetCount = _thrusterCache.Count; }
                if (block.Entity != null && _thrusterSet.Contains(block.Entity)) thrustersChanged = true;
            }
            bool inSurface = !BlockComponentFactory.IsSelfModelled(block);   // (self-modelled: never in the surface)
            foreach (var cellGroup in block.GetTransformedOccupiedCellGroups())
            {
                var min = cellGroup.Min;
                var max = cellGroup.Max;
                if (inSurface) _patchRemoved.Add((min, max));
                // per block, not per cell (an impact is hundreds of blocks of 1000 cells in one tick: it was ~0.2 s)
                if (_components.RemoveInBox(min, max) > 0) _faceOverridesDirty = true;
                if (inSurface && _model?.HasWings == true)
                {
                    int k = _cellScale;
                    var q0 = new Vector3I(SnapshotGridAccessor.FloorDiv(min.X, k), SnapshotGridAccessor.FloorDiv(min.Y, k), SnapshotGridAccessor.FloorDiv(min.Z, k)); var q1 = new Vector3I(SnapshotGridAccessor.FloorDiv(max.X, k), SnapshotGridAccessor.FloorDiv(max.Y, k), SnapshotGridAccessor.FloorDiv(max.Z, k));
                    for (int x = q0.X; x <= q1.X; x++) for (int y = q0.Y; y <= q1.Y; y++) for (int z = q0.Z; z <= q1.Z; z++) _model.RemoveWingCell(new Vector3I(x, y, z));
                }
                if (inSurface && (_chunks == null || !LocalUpdates))   // (the cells, for the cell-by-cell surface update: chunked grids need none)
                    for (int x = min.X; x <= max.X; x++)
                        for (int y = min.Y; y <= max.Y; y++)
                            for (int z = min.Z; z <= max.Z; z++)
                                _pendingRemovedCells.Add(new Vector3I(x, y, z));
            }
        }

        foreach (var block in blockData.AddedBlocks)
        {
            if (block == null) continue;
            if (!thrustersChanged && block.Entity?.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement.ThrusterComponent>() != null) thrustersChanged = true;

            // Try creating an aero component for this block (custom aero blocks)
            if (_blockSize > 0)
            {
                var aeroComp = _factory.TryCreate(block, _blockSize);
                if (aeroComp != null)
                {
                    _components.Add(aeroComp);
                    _faceOverridesDirty = true;
                }
            }

            if (!BlockComponentFactory.IsSelfModelled(block))   // (its component models it whole: not in the surface)
            foreach (var cellGroup in block.GetTransformedOccupiedCellGroups())
            {
                var min = cellGroup.Min;
                var max = cellGroup.Max;
                _patchAdded.Add((min, max));
                if (_chunks == null || !LocalUpdates)
                for (int x = min.X; x <= max.X; x++)
                    for (int y = min.Y; y <= max.Y; y++)
                        for (int z = min.Z; z <= max.Z; z++)
                            _pendingAddedCells.Add(new Vector3I(x, y, z));
            }
        }

        long tp = AeroCost.Start();
        if (_chunks != null && LocalUpdates)
        {
            // the chunks the changed blocks reach (2 m around): recomputed in the background (TickLocal)
            const float M = 2f;
            foreach (var list in new List<List<(Vector3I, Vector3I)>> { _patchRemoved, _patchAdded })
                foreach (var (a, b) in list)
                {
                    _tmpDirty.Clear();
                    _chunks.KeysIn(new Vector3(a.X, a.Y, a.Z) * 0.25f - new Vector3(M), new Vector3(b.X + 1, b.Y + 1, b.Z + 1) * 0.25f + new Vector3(M), _tmpDirty);
                    foreach (var k in _tmpDirty) { _localDirty.Add(k); _localWeight.TryGetValue(k, out int w); _localWeight[k] = w + 1; }
                }
            _lastDamage = System.Diagnostics.Stopwatch.GetTimestamp();
            _fullAfterQuiet = true;
            // whole chunks emptied (an impact): out of the forces this frame - gathered here, applied once per frame
            // (TickLocal: an impact is hundreds of block events in one tick)
            foreach (var (a, b) in _patchRemoved) { long key = _chunks.KeyOf(new Vector3(a.X + b.X + 1, a.Y + b.Y + 1, a.Z + b.Z + 1) * 0.125f); _chunks.CountBlock(key, -1); _dropKeys.Add(key); }
            foreach (var (a, b) in _patchAdded) _chunks.CountBlock(_chunks.KeyOf(new Vector3(a.X + b.X + 1, a.Y + b.Y + 1, a.Z + b.Z + 1) * 0.125f), 1);
        }
        AeroCost.Patch.Stop(tp);

        _dirty = true;
        if (thrustersChanged) _thrusterCacheDirty = true;
        _surfaceCooldownTicks = SurfaceCooldown;
    }

    // -- Damage, at once: the force table patched block by block --
    private readonly List<(Vector3I, Vector3I)> _patchRemoved = new(), _patchAdded = new();


    /// <summary>A piece just broken off: its parent's shares for its chunks, as its forces until its own build.</summary>
    private void TrySeed(WorldTransform wt)
    {
        _seedTries++;
        var keys = new HashSet<long>(LongKey.Comparer);
        var keyer = new ChunkedTable(8, ChunkSize, default);
        foreach (var block in _octree.GetAllCubeBlocks())
        {
            if (block == null) continue;
            foreach (var g in block.GetTransformedOccupiedCellGroups())
                keys.Add(keyer.KeyOf(new Vector3(g.Min.X + g.Max.X + 1, g.Min.Y + g.Max.Y + 1, g.Min.Z + g.Max.Z + 1) * 0.125f));
        }
        var t = OrphanChunks.Take(wt.Position, wt.Orientation, keys, 8, 3, IsServerScene);
        if (t == null) return;
        _model.InstallForceTable(t);
        if (AeroLog.Detail) Log.Default?.Info($"[AERO] grid {Entity?.DebugName} seeded from its parent: {keys.Count} chunks, try {_seedTries}");
    }

    /// <summary>Simulation thread, every frame of a chunked grid: install a finished local update, start the next
    /// one, and once damage has been quiet QuietBeforeFullRebuild seconds queue the whole rebuild.</summary>
    private void TickLocal()
    {
        if (_dropKeys.Count > 0 && _model?.Table != null)
        {
            _orphanBuf.Clear();
            var nt = _chunks.DropEmptied(_dropKeys, _model.Table, _localTask != null, out int dropped, _orphanBuf);
            if (!ReferenceEquals(nt, _model.Table)) _model.InstallForceTable(nt);
            // (what left may be a piece breaking off: posted for it)
            if (_orphanBuf.Count > 0) { var pwt = Data.GetWorldTransform(); OrphanChunks.Add(pwt.Position, pwt.Orientation, _chunks.N, _chunks.ChunkSize, _orphanBuf, IsServerScene); }
            ChunksDropped += dropped;
            _dropKeys.Clear();
        }
        if (_localTask != null)
        {
            if (!_localTask.IsCompleted) return;
            var t = _localTask; _localTask = null;
            if (t.IsFaulted) Log.Default?.Info($"[AERO] local update failed: {t.Error?.Message}");
            else if (_localGen == _chunkGen && _localRes != null && _model?.Table != null) { _model.InstallForceTable(_chunks.Apply(_localRes, _model.Table)); LocalUpdatesDone++; }
            _localRes = null;
            _chunks?.ClearPending();
        }
        // background work only while the air matters: a wreck on the ground (or anything barely moving) keeps its
        // damage noted - the same-frame drops above still apply - and catches up the moment it moves again
        bool airMatters = LastSpeed >= MinSpeedForUpdates && LastDensity > 0f;
        if (!airMatters) return;
        if (_localDirty.Count > 0 && !_staggeredBuildActive && _model?.Table != null)
        {
            // the region: the dirty chunks and a margin; its blocks captured here (the octree is ours only now)
            // the most-changed chunks first, ChunksPerUpdate at a time
            // (and near each other: chunks spread over the grid make the update's footprint the whole grid -
            //  355 ms for 8 scattered chunks on Red Ship; the rest get their own updates)
            var dirty = new HashSet<long>(LongKey.Comparer);
            var ranked = _localDirty.OrderByDescending(k => _localWeight.TryGetValue(k, out int w) ? w : 0).ToList();
            var (s0, s1) = _chunks.BoxOf(ranked[0]); var seedC = (s0 + s1) * 0.5f;
            foreach (var k in ranked)
            {
                var (b0, b1) = _chunks.BoxOf(k);
                if (((b0 + b1) * 0.5f - seedC).Length() > 24f) continue;
                dirty.Add(k);
                if (dirty.Count >= ChunksPerUpdate) break;
            }
            foreach (var k in dirty) { _localDirty.Remove(k); _localWeight.Remove(k); }
            // (each dirty chunk with a 3 m margin, in grid cells: scattered damage must not mean the whole grid)
            var regions = new List<(Vector3I lo, Vector3I hi)>();
            foreach (var k in dirty)
            {
                var (a, b) = _chunks.BoxOf(k);
                a -= new Vector3(3f); b += new Vector3(3f);
                regions.Add((new Vector3I((int)MathF.Floor(a.X / 0.25f), (int)MathF.Floor(a.Y / 0.25f), (int)MathF.Floor(a.Z / 0.25f)),
                             new Vector3I((int)MathF.Ceiling(b.X / 0.25f), (int)MathF.Ceiling(b.Y / 0.25f), (int)MathF.Ceiling(b.Z / 0.25f))));
            }
            var boxes = new List<(Vector3I, Vector3I)>();
            foreach (var block in _octree.GetAllCubeBlocks())
            {
                if (block == null || BlockComponentFactory.IsSelfModelled(block)) continue;
                foreach (var g in block.GetTransformedOccupiedCellGroups())
                    for (int r = 0; r < regions.Count; r++)
                    {
                        var (rl, rh) = regions[r];
                        if (g.Max.X >= rl.X && g.Min.X <= rh.X && g.Max.Y >= rl.Y && g.Min.Y <= rh.Y && g.Max.Z >= rl.Z && g.Min.Z <= rh.Z) { boxes.Add((g.Min, g.Max)); break; }
                    }
            }
            _chunks.Prepare(dirty);
            var chunks = _chunks; int k2 = _cellScale; var geo = SnapshotGridAccessor.CellGeometry(k2); float bs = _blockSize;
            _localGen = _chunkGen;
            _localTask = AeroWork.Enqueue(() => CachedPriority + 2000f, () =>   // (damage on a grid that has forces: before builds)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var snap = k2 > 1 ? new SnapshotGridAccessor(boxes).CoarsenMajority(k2) : new SnapshotGridAccessor(boxes);
                var hide = new List<Vector3>();
                var faces = chunks.FacesFor(snap, dirty, geo.size, geo.offset, bs, hide);
                _localRes = chunks.ComputeLocal(dirty, faces, AeroWork.ThreadsForJob(), hide);
                ChunkedTable.FacesDone(faces);
                LastLocalMs = sw.Elapsed.TotalMilliseconds;
            });
            return;
        }
        // quiet: the whole rebuild (what holes uncovered downstream, the wings as they are now)
        if (_fullAfterQuiet && _localDirty.Count == 0 && !_staggeredBuildActive
            && System.Diagnostics.Stopwatch.GetTimestamp() - _lastDamage > System.Diagnostics.Stopwatch.Frequency * QuietBeforeFullRebuild)
        {
            _fullAfterQuiet = false;
            _gridAccessor.SetOctree(_octree);
            AeroScheduler.EnqueueRebuild(this);
        }
    }
}
