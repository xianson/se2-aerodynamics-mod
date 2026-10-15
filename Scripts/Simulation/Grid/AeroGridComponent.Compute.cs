using System;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.BlockOctrees;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace AeroMod;

// The per-frame force computation (TryCompute): rebuild triggers, the surface update, wings, the model, components and patches.
public partial class AeroGridComponent
{
    // ── Compute (called by the sim job, AeroSimJob.cs, on the frames this grid recomputes) ──

    internal void TryCompute(WorldTransform wt, float density, Vector3 linearVelocity, Vector3 angularVelocity, Vector3 centerOfMass, float groundHeight = -1f)
    {
        HasResult = false;

        // ── Attitude hold (runs every frame, even without atmosphere/speed) ──
        UpdateAttitudeHold(wt, angularVelocity);

        if (!_initialized) return;
        // (the save's surface / gear settings onto their blocks - here, before the air's early outs: a parked grid too)
        // (each scene's step its own scene's blocks: the client copies' share on the client's thread - AeroSurfaceSaved)
        if (IsServerScene) AeroSurfaceSaved.ApplyPending(Data.Scene); else AeroSurfaceSaved.ClientStep(Data.Scene);

        // ── Global scheduler tick (first grid each frame drives all rebuilds) ──
        long tsc = AeroCost.Start();
        AeroScheduler.EnsureTicked();
        AeroScheduler.RunMine(this);   // (this grid's own rebuild steps, in its own job)
        AeroCost.Sched.Stop(tsc);

        // A grid's first build is queued even parked or out of the air (behind those that fly - BuildPriority):
        // it used to wait until the grid moved in air, which then flew without aero for the length of a build.
        if (_chunks != null && LocalUpdates) TickLocal();
        if (_quickTable != null) { var qt = _quickTable; _quickTable = null; if (_model.Table == null) _model.InstallForceTable(qt); }
        // (only a grid that could use a table now: a static grid, or one in neither air nor gravity, built a HashSet and
        //  scanned every block each frame while any debris was young)
        if (_model?.Table == null && _seedTries < 120 && OrphanChunks.Any && !IsStatic && (density > 0f || InGravity)) TrySeed(wt);

        if (_dirty && !_staggeredBuildActive && _surface.FaceCount == 0 && _model?.Table == null && (density > 0f || InGravity || EntryWantsTable) && !IsStatic)
        {
            if (_blockSize <= 0) _blockSize = DetectBlockSize();
            _gridAccessor.SetOctree(_octree);
            AeroScheduler.EnqueueRebuild(this);
            _fullRebuildNeeded = false;
            _pendingAddedCells.Clear();
            _pendingRemovedCells.Clear();
            _dirty = false;
        }

        if (density < AeroConfig.MinDensity) return;

        float speed = linearVelocity.Length();
        if (speed < AeroConfig.MinSpeed) return;

        // Ensure block size is known (needed for thruster cache even if grid isn't dirty)
        if (_blockSize <= 0)
            _blockSize = DetectBlockSize();

        // ── Handle dirty state: enqueue full rebuild or do incremental update ──
        // Cooldown: suppress incremental updates while blocks are still actively
        // changing (e.g., ground impact). Cells keep accumulating in _pending lists;
        // a single batched update runs once the cooldown expires.
        long surfaceStart = AeroStats.Timestamp();
        long tfl = AeroCost.Start();
        if (_dirty && _chunks != null && LocalUpdates && !_fullRebuildNeeded)
        {
            // (chunked: damage is handled by local updates - TickLocal - and a whole rebuild once quiet)
            _dirty = false;
            _pendingAddedCells.Clear();
            _pendingRemovedCells.Clear();
        }
        if (_dirty && !_staggeredBuildActive)
        {
            // Full rebuilds bypass cooldown — staggered builder handles its own pacing
            if (_fullRebuildNeeded || _surface.FaceCount == 0)
            {
                _gridAccessor.SetOctree(_octree);
                _blockSize = DetectBlockSize();
                AeroScheduler.EnqueueRebuild(this);
                _fullRebuildNeeded = false;
                _pendingAddedCells.Clear();
                _pendingRemovedCells.Clear();
                _dirty = false;
            }
            else if (_surfaceCooldownTicks > 0 && ++_surfaceWaited < MaxSurfaceWait)
            {
                // Still receiving rapid changes — keep accumulating (but not past MaxSurfaceWait: under steady damage
                // the cooldown never ran out, and the surface never followed the damage at all)
                _surfaceCooldownTicks--;
            }
            else
            {
                // Cooldown expired — flush all accumulated changes
                _gridAccessor.SetOctree(_octree);
                _blockSize = DetectBlockSize();

                int changedCells = _pendingAddedCells.Count + _pendingRemovedCells.Count;
                int faceCount = _surface.RawFaceCount;

                // A big surface always rebuilds in the background: its "incremental" update re-classifies the whole
                // surface and re-reads every cell of the grid on the simulation thread (31-52 ms on the Jetliner),
                // and its wing re-detection takes 70 ms more.
                bool big = faceCount > BigSurfaceFaces || _cellScale > 1;
                if (big && System.Diagnostics.Stopwatch.GetTimestamp() - _lastRebuildStart < System.Diagnostics.Stopwatch.Frequency * BigRebuildGapSeconds)
                {
                    // (a big grid rebuilds at most every BigRebuildGapSeconds: changes keep collecting meanwhile)
                    AeroStats.SetSurf(AeroStats.ElapsedUs(surfaceStart));
                    AeroCost.Flush.Stop(tfl);
                    goto SurfaceDone;
                }
                if (faceCount > 0 && (changedCells > faceCount / 5 || big))
                {
                    // Too many accumulated changes — incremental would fall through
                    // to a synchronous full Build(). Route to staggered builder instead.
                    AeroScheduler.EnqueueRebuild(this);
                }
                else
                {
                    // Small enough for incremental update
                    var args = new BlocksChangedArgs(
                        added: _pendingAddedCells.Count > 0 ? _pendingAddedCells : null,
                        removed: _pendingRemovedCells.Count > 0 ? _pendingRemovedCells : null);
                    _surface.OnBlocksChanged(_gridAccessor, args);
                    _manifold.Classify(_surface);

                    _model.UpdateWings(_gridAccessor, _surface, _blockSize,
                        _pendingAddedCells, _pendingRemovedCells);
                    _faceOverridesDirty = true;

                    _wingAddedCells.AddRange(_pendingAddedCells);
                    _wingRemovedCells.AddRange(_pendingRemovedCells);
                    _wingsDirty = true;
                    _wingCooldownTicks = WingDetectCooldown;
                }

                _pendingAddedCells.Clear();
                _pendingRemovedCells.Clear();
                _dirty = false;
                _surfaceWaited = 0;
            }
        }
        else if (_dirty && _staggeredBuildActive)
        {
            // Changed while a rebuild runs: the changes wait (pending) and are applied after its swap, incrementally
            // or by another rebuild if large. (Restarting threw the whole rebuild away: under steady damage a big
            // ship rebuilt forever, and the garbage drove full collections.)
        }

        AeroStats.SetSurf(AeroStats.ElapsedUs(surfaceStart));
        AeroCost.Flush.Stop(tfl);
        SurfaceDone:

        // ── Deferred full wing detection (correctness pass with ray-march) ──
        long wingStart = AeroStats.Timestamp();
        if (_wingsDirty && !_staggeredBuildActive && _surface.RawFaceCount > BigSurfaceFaces) { _wingsDirty = false; _wingAddedCells.Clear(); _wingRemovedCells.Clear(); }   // (its rebuild detects them)
        if (_wingsDirty && !_staggeredBuildActive)
        {
            if (--_wingCooldownTicks <= 0)
            {
                long twi = AeroCost.Start();
                try {
                _model.InvalidateWings();
                _model.DetectWings(_gridAccessor, _surface, _blockSize);
                _manifold.Classify(_surface);
                _faceOverridesDirty = true;
                _wingsDirty = false;
                _wingAddedCells.Clear();
                _wingRemovedCells.Clear();
                } finally { AeroCost.Wings.Stop(twi); }
            }
        }
        AeroStats.SetWing(AeroStats.ElapsedUs(wingStart));

        // ── Force computation (the force table; else the surface, even during staggered build) ──
        // (a big grid's surface is let go once its table is in: the table alone carries it)
        // (no surface and no table: nothing for the drag model - but self-modelled blocks, kept out of the surface, still
        //  fly by their components: a grid of 3x3x1 control surfaces alone has no faces at all)
        bool noSurface = _surface.FaceCount == 0 && _model?.Table == null;
        if (noSurface && _components.Count == 0) return;

        Vector3 localLinVel = WorldTransform.TransformDirectionInv(linearVelocity, wt);
        Vector3 localAngVel = WorldTransform.TransformDirectionInv(angularVelocity, wt);

        AtmosphereState atmo = _atmosphereBridge.GetState(density);

        _lastVelocityLocal = localLinVel;
        _lastComLocal = centerOfMass;

        var ctx = new AeroContext(
            _gridAccessor,
            _surface,
            localLinVel,
            atmo,
            centerOfMass,
            _blockSize,
            localAngVel,
            groundHeight,
            _manifold,
            GroundUpLocal);

        long dragStart = AeroStats.Timestamp();
        LastResult = noSurface ? default : _model.Compute(ctx);
        AeroStats.SetDrag(AeroStats.ElapsedUs(dragStart));

        // ── Control surface input from player ──
        long csStart = AeroStats.Timestamp();
        float q = (float)(0.5 * atmo.Density * speed * speed);
        UpdateControlSurfaceInputs(centerOfMass, wt, localAngVel, q, LastResult.Torque);
        // the aero-aware attitude law flying: the surfaces deliver ITS torque demand (ThrustTorque publishes it; the gyros
        // and thrusters give the rest) - the mixer's own law stands only when it does not fly (rate input, harness, vacuum)
        if (AllocEnabled && IsServerScene && LastControlTorqueValid && float.IsNaN(ControlSurface.TestInput)) AllocateSurfaces(ctx, centerOfMass);
        PublishFlapAngles(wt);
        AeroStats.SetCtrl(AeroStats.ElapsedUs(csStart));

        // ── Block component forces ──
        long compStart = AeroStats.Timestamp();
        LastComponentTorque = Vector3.Zero;
        LastComponentForce = Vector3.Zero;
        if (_components.Count > 0)
        {
            var (compForce, compTorque) = _components.EvaluateAll(ctx);
            LastComponentTorque = compTorque;
            LastComponentForce = compForce;
            if (SurfaceTorqueLog && IsServerScene)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (now - _surfaceLogAt > System.Diagnostics.Stopwatch.Frequency / 2)
                {
                    _surfaceLogAt = now;
                    Log.Default?.Info($"[AERO-CS-TORQUE] grid={Entity?.GetHashCode()} q={q:F0}Pa comp torque=({compTorque.X:F0},{compTorque.Y:F0},{compTorque.Z:F0})Nm want=({_lastGridAngVel.X:F3},{_lastGridAngVel.Y:F3},{_lastGridAngVel.Z:F3}) w=({localAngVel.X:F3},{localAngVel.Y:F3},{localAngVel.Z:F3}) testInput={ControlSurface.TestInput}");
                }
            }
            var merged = LastResult.Force + compForce;
            var mergedTorque = LastResult.Torque + compTorque;

            float forceDotV = Vector3.Dot(merged, localLinVel / speed);
            Vector3 liftVec = merged - forceDotV * (localLinVel / speed);

            LastResult = new AeroResult(
                merged, mergedTorque,
                MathF.Abs(forceDotV),
                liftVec.Length(),
                LastResult.FrontalArea,
                LastResult.Mach,
                LastResult.DynamicPressure);
        }
        // ── Other mods' drag (AeroApi.SetDrag): airframe air - in the result, not the control surfaces' torque ──
        LastPatchForce = LastPatchTorque = Vector3.Zero;
        List<DragPatch> patches = _dragPatches;
        if (patches.Count > 0)
        {
            var (pf, pt) = EvaluateDragPatches(patches, ctx);
            LastPatchForce = pf; LastPatchTorque = pt;
            var mf = LastResult.Force + pf;
            float fdv = Vector3.Dot(mf, localLinVel / speed);
            LastResult = new AeroResult(mf, LastResult.Torque + pt, MathF.Abs(fdv), (mf - fdv * (localLinVel / speed)).Length(),
                LastResult.FrontalArea, LastResult.Mach, LastResult.DynamicPressure);
        }
        AeroStats.SetComp(AeroStats.ElapsedUs(compStart));

        // ── Override FaceCp for wing + component faces ──
        long faceOverrideStart = AeroStats.Timestamp();
        if (_faceOverridesDirty && _surface != null)
        {
            _model.BuildFaceOverrideIndex(_surface, _components.Components);
            if (_model.InnerModel is DampedShadowedDragModel dsmEx)
            {
                _model.ExcludeWingFaces(dsmEx, _surface.Version);
                var eng = _engines;
                if (DampedShadowedDragModel.FlowThroughEngines && eng != null && eng.Count > 0)
                    dsmEx.SetThroughFaces(DampedShadowedDragModel.ThroughMask(_surface.Faces, _surface.CellSize, eng, out _), _surface.Version);
            }
            _faceOverridesDirty = false;
        }
        if (_model.InnerModel is DampedShadowedDragModel dsmCp)
            _model.OverrideFaceCp(dsmCp, _components.Components);
        AeroStats.SetFaceOvr(AeroStats.ElapsedUs(faceOverrideStart));

        HasResult = true;
    }
}
