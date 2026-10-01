using System;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.BlockOctrees;
using Keen.Game2.Simulation.WorldObjects.Movement;
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
    internal readonly BlockOctreeComponent _octree;

    // ── Aero pipeline state ──

    private Se2GridAccessor _gridAccessor;
    private SmoothSurfaceProvider _surface;       // active surface (used for force computation)
    private SmoothSurfaceProvider _buildSurface;  // back buffer (used during staggered rebuild)
    private LiftingSurfaceModel _model;
    private ManifoldClassifier _manifold;
    // A rebuild in the background (StartBackgroundBuild): the spare classifier it classifies into, the
    // wings it detects, and the task. The active surface, classifier and wings serve the simulation until the swap.
    private ManifoldClassifier _buildManifold;
    private System.Threading.Tasks.Task _finalizeTask;
    private List<LiftingSurface> _builtWings;
    private PrecomputedShadowMap _builtShadow, _spareShadow;
    private AeroComponentRegistry _components;
    private BlockComponentFactory _factory;
    private float _blockSize;
    private float _cachedBlockSize = -1;
    private bool _dirty;
    private int _surfaceWaited;
    /// <summary>The longest (frames) block changes wait for the surface, however steady the damage.</summary>
    private const int MaxSurfaceWait = 60;
    /// <summary>Above this many faces, every surface change is a background rebuild.</summary>
    private const int BigSurfaceFaces = 20000;
    private const double BigRebuildGapSeconds = 2.0;
    private long _lastRebuildStart;
    internal int _switchGen;

    /// <summary>Everything rebuilt from scratch (after aero was switched off and on).</summary>
    internal void ForceFullRebuild()
    {
        _dirty = true; _fullRebuildNeeded = true; _thrusterCacheDirty = true; _gyroCacheDirty = true; _cachedBlockSize = -1;
        _pendingAddedCells.Clear(); _pendingRemovedCells.Clear(); _components.Clear();
    }
    private bool _fullRebuildNeeded;
    private bool _initialized;

    // ── Staggered rebuild (managed by AeroScheduler) ──
    private bool _staggeredBuildActive;
    private bool _rebuildRestartNeeded; // topology changed mid-build

    // ── Deferred full wing re-detection ──
    private bool _wingsDirty;
    private int _wingCooldownTicks;
    private const int WingDetectCooldown = 60; // ~1s at 60Hz — full detection with ray-march

    // ── Surface update cooldown (suppress rapid incremental updates during impacts) ──
    private int _surfaceCooldownTicks;
    private const int SurfaceCooldown = 60; // ~1s at 60Hz

    // ── Face override index (for Cp heatmap) ──
    private bool _faceOverridesDirty = true;

    // ── Cascaded flight controller state ──
    private Vector3 _rateIntegral = Vector3.Zero;  // inner loop integrator (per-axis)

    // ── SAS (hidden stability augmentation) ──
    // Direct torque applied to physics, independent of aero surfaces.
    internal Vector3 SasTorque;  // local frame, computed per frame
    internal Vector3 EulerError; // local frame, orientation error in radians
    internal bool SuppressPhantomTorque; // test harness: disable SAS + phantom attitude torque
    internal bool HarnessControlsAttitude; // test harness: skip UpdateAttitudeHold, harness drives _lastGridAngVel + SasTorque
    internal Quaternion _holdOrientation = Quaternion.Identity; // orientation to hold when unpiloted
    internal bool _holdOrientationValid;                        // true once captured

    // ── Accumulated changes for incremental wing update ──
    private List<Vector3I> _wingAddedCells = new();
    private List<Vector3I> _wingRemovedCells = new();

    // ── Batched block change accumulation ──
    private List<Vector3I> _pendingAddedCells = new();
    private List<Vector3I> _pendingRemovedCells = new();

    // ── Offset thrust (RCS) ──
    internal List<ThrusterInfo> _thrusterCache = new();
    private bool _thrusterCacheDirty = true;

    // ── Gyroscope control ──
    internal List<GyroInfo> _gyroCache = new();
    private bool _gyroCacheDirty = true;

    // ── Ground effect ──
    /// <summary>Height above ground in meters. -1 = unknown.</summary>
    internal float GroundHeight = -1f;
    internal readonly PhysicsHack.GroundProbe Ground = new PhysicsHack.GroundProbe();

    // ── Cached physics state (written by sim job @ 60Hz, read by draw job) ──
    internal Vector3 LastLinVel;
    internal Vector3 LastAngVel;
    internal Vector3 LastCoM;
    internal float LastMass;
    internal float LastDensity;
    internal float LastSpeed;
    internal Vector3 LastInvInertia;      // (1/Ixx, 1/Iyy, 1/Izz) principal axes
    internal Quaternion LastInertiaMajorAxisRot = Quaternion.Identity; // principal → body rotation

    // Shared across all grids
    private static AtmosphereBridge _atmosphereBridge;

    /// <summary>Last computed result.</summary>
    internal AeroResult LastResult;
    internal float[] _thrustUse = Array.Empty<float>();
    internal Vector3[] _rcsArm = Array.Empty<Vector3>(), _rcsDir = Array.Empty<Vector3>();
    internal float[] _rcsCap = Array.Empty<float>(), _rcsF = Array.Empty<float>(), _rcsPrev = Array.Empty<float>(), _rcsY = Array.Empty<float>();
    internal bool _rcsWarm;
    internal bool IsServerScene = true;
    static readonly System.Collections.Generic.HashSet<string> _scenesSeen = new();
    internal float[] _flameShown = Array.Empty<float>();   // the override each client thruster shows (-1: none)
    private Quaternion _rcsHold; private bool _rcsHoldValid; private int _holdStall;

    /// <summary>
    /// The orientation the thrusters steer to (ThrustTorque): the pilot's target (TargetControlData, the one the
    /// game's gyros steer to); unpiloted with dampeners on, the orientation it was left at; else none. A rate
    /// command without a target (AngularControlData input) is followed by the game's gyros: no target here.
    /// </summary>
    internal bool AttitudeTarget(in WorldTransform wt, out Quaternion target, out string mode)
    {
        target = wt.Orientation;
        if (HarnessControlsAttitude) { mode = "harness"; return false; }
        if (--_subPartCheck <= 0)
        {
            _subPartCheck = 120;
            try { _isSubPart = PhysicsHack.IsSubPart(Entity, LastMass); } catch { _isSubPart = false; }
        }
        if (_isSubPart) { _rcsHoldValid = false; mode = "sub-part"; return false; }
        if (Data.TryGet<TargetControlData>(out var tc) && tc.TargetOrientation.IsValidAndRotationIsNormalized())
        {
            target = tc.TargetOrientation; _rcsHold = target; _rcsHoldValid = true; mode = "pilot target"; return true;
        }
        if ((Data.TryGet<AngularControlData>(out var ac) && ac.TargetAngularVelocity.LengthSquared() > 1e-6f)
            || (Data.TryGet<ControlData>(out var cd) && cd.Rotation.LengthSquared() > 1e-4f))
        {
            _rcsHoldValid = false; mode = "rate input"; return false;
        }
        if (Data.Has<DampeningData>())
        {
            if (!_rcsHoldValid) { _rcsHold = wt.Orientation; _rcsHoldValid = true; _holdStall = 0; }
            // A hold the ship cannot reach (resting on terrain, wedged): off by over 2 degrees yet barely turning or
            // moving for 3 s, it takes the orientation it rests in, instead of pushing the ground forever. (Not in flight:
            // a flying ship slowly losing to the air re-anchored every 3 s, and the hold ratcheted away with it.)
            float errAngle = 2f * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(wt.Orientation, _rcsHold)), 0f, 1f));
            if (errAngle > 0.035f && LastAngVel.Length() < 0.01f && LastSpeed < 1f) { if (++_holdStall > 180) { _rcsHold = wt.Orientation; _holdStall = 0; } }
            else _holdStall = 0;
            target = _rcsHold; mode = "hold"; return true;
        }
        _rcsHoldValid = false; mode = "free"; return false;
    }
    internal ThrustTorque.Report LastThrust;
    internal int _clearedOverride;
    private int _subPartCheck; private bool _isSubPart;
    /// <summary>For AeroCost's 'top grid' line.</summary>
    internal int FacesNow => _surface?.FaceCount ?? -1;
    internal bool Rebuilding => _staggeredBuildActive;
    internal string ShadowNote => _model?.InnerModel is DampedShadowedDragModel d ? $"shadow visible={d.ShadowMap.VisibleCount} shadowed={d.ShadowMap.ShadowedCount}" : "";
    internal bool HasResult;

    /// <summary>Component (CS/wing) torque from last frame, separate from body torque.</summary>
    internal Vector3 LastComponentTorque;
    /// <summary>Component (CS/wing) force from last frame, separate from body force.</summary>
    internal Vector3 LastComponentForce;

    /// <summary>Block-level aero component registry.</summary>
    public AeroComponentRegistry Components => _components;

    /// <summary>Face hull/cavity classifier for internal surface culling.</summary>
    public ManifoldClassifier Manifold => _manifold;

    // ── Lifecycle ──

    void IInSceneListener.OnAddedToScene()
    {
        Log.Default?.Info("[AERO] AeroGridComponent.OnAddedToScene()");

        _gridAccessor = new Se2GridAccessor(_octree);
        _surface = new SmoothSurfaceProvider();
        _buildSurface = new SmoothSurfaceProvider();

        var innerDrag = new DampedShadowedDragModel();
        _model = new LiftingSurfaceModel(innerDrag, liftModel: new CompressibleWingModel());
        _manifold = new ManifoldClassifier();
        _buildManifold = new ManifoldClassifier();
        _components = new AeroComponentRegistry();

        // Wire manifold into wing detector for cavity face filtering
        if (_model.Detector is ConnectedComponentWingDetector ccwd)
        {
            ccwd.Manifold = _manifold;
            ccwd.ManifoldSurface = _surface;
        }
        _factory = new BlockComponentFactory();
        DefaultMappings.Register(_factory);

        _atmosphereBridge ??= new AtmosphereBridge();

        // Attach ObservedWorldTransform so the draw job triggers on transform changes
        ObservedWorldTransform.AttachTo(Data, Data);

        // Enable debug draw globally (off by default)
        Keen.VRage.Core.GlobalDebugSettings.Default.EnabledDebugDraw = true;

        // The client copy shows thruster flames for the controller's sharing (ThrustTorque.ClientFlames). The game
        // names its scenes "Server" and "Client" (WorldSessionComponent); in single player both sessions have the
        // game server service, so that cannot tell them apart.
        try { IsServerScene = !string.Equals(Data.Scene?.DebugName, "Client", StringComparison.Ordinal); } catch { IsServerScene = true; }
        lock (_scenesSeen) if (_scenesSeen.Add(Data.Scene?.DebugName ?? "null")) Log.Default?.Info($"[AERO] grid scene '{Data.Scene?.DebugName}' (server={IsServerScene}, session {Entity.GetSession()?.GetHashCode()})");

        // Defer heavy work (surface build, wing detection) to first compute call
        _dirty = true;
        _fullRebuildNeeded = true;
        _initialized = true;
        AeroTestHarness.TrackGrid(this);
    }

    void IInSceneListener.OnBeforeRemovedFromScene()
    {
        _initialized = false;
        AeroTestHarness.UntrackGrid(this);
        AeroScheduler.Remove(this);
        ObservedWorldTransform.DetachFrom(Data, Data);
    }

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
        foreach (var block in blockData.RemovedBlocks)
        {
            if (block == null) continue;
            if (!thrustersChanged)
                for (int ti = 0; ti < _thrusterCache.Count; ti++)
                    if (ReferenceEquals(_thrusterCache[ti].ThrusterEntity, block.Entity)) { thrustersChanged = true; break; }
            foreach (var cellGroup in block.GetTransformedOccupiedCellGroups())
            {
                var min = cellGroup.Min;
                var max = cellGroup.Max;
                for (int x = min.X; x <= max.X; x++)
                    for (int y = min.Y; y <= max.Y; y++)
                        for (int z = min.Z; z <= max.Z; z++)
                        {
                            var pos = new Vector3I(x, y, z);
                            _pendingRemovedCells.Add(pos);
                            if (_components.RemoveBlock(pos) > 0)
                                _faceOverridesDirty = true;
                        }
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
        if (thrustersChanged) _thrusterCacheDirty = true;
        _surfaceCooldownTicks = SurfaceCooldown;
    }

    // ── Compute (called from debug draw for now) ──

    internal void TryCompute(WorldTransform wt, float density, Vector3 linearVelocity, Vector3 angularVelocity, Vector3 centerOfMass, float groundHeight = -1f)
    {
        HasResult = false;

        // ── Attitude hold (runs every frame, even without atmosphere/speed) ──
        UpdateAttitudeHold(wt, angularVelocity);

        if (!_initialized) return;

        // ── Global scheduler tick (first grid each frame drives all rebuilds) ──
        long tsc = AeroCost.Start();
        AeroScheduler.EnsureTicked();
        AeroCost.Sched.Stop(tsc);

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
                bool big = faceCount > BigSurfaceFaces;
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
            localAngVel,
            groundHeight,
            _manifold);

        long dragStart = AeroStats.Timestamp();
        LastResult = _model.Compute(ctx);
        AeroStats.SetDrag(AeroStats.ElapsedUs(dragStart));

        // ── Control surface input from player ──
        long csStart = AeroStats.Timestamp();
        float q = (float)(0.5 * atmo.Density * speed * speed);
        UpdateControlSurfaceInputs(centerOfMass, wt, localAngVel, q, LastResult.Torque);
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
        AeroStats.SetComp(AeroStats.ElapsedUs(compStart));

        // ── Override FaceCp for wing + component faces ──
        long faceOverrideStart = AeroStats.Timestamp();
        if (_faceOverridesDirty && _surface != null)
        {
            _model.BuildFaceOverrideIndex(_surface, _components.Components);
            if (_model.InnerModel is DampedShadowedDragModel dsmEx) _model.ExcludeWingFaces(dsmEx, _surface.Version);
            _faceOverridesDirty = false;
        }
        if (_model.InnerModel is DampedShadowedDragModel dsmCp)
            _model.OverrideFaceCp(dsmCp, _components.Components);
        AeroStats.SetFaceOvr(AeroStats.ElapsedUs(faceOverrideStart));

        HasResult = true;
    }


    /// <summary>
    /// Attitude hold: when no pilot is present, hold the current orientation.
    /// Runs every frame before early returns so it works even without atmosphere.
    /// Sets _lastGridAngVel and SasTorque when no pilot input is detected.
    /// </summary>
    private void UpdateAttitudeHold(WorldTransform wt, Vector3 angularVelocity)
    {
        // Test harness drives attitude directly — don't overwrite its values
        if (HarnessControlsAttitude) return;

        // Check if a pilot is providing input
        bool hasTargetData = Data.TryGet<TargetControlData>(out _);
        bool hasAngularData = Data.TryGet<AngularControlData>(out _);

        // AngularControlData persists on entity even without a pilot (stale ECS component).
        // Check if there's actual nonzero input, not just component existence.
        bool hasRealInput = hasTargetData;
        if (!hasRealInput && hasAngularData)
        {
            var angData = Data.Get<AngularControlData>();
            hasRealInput = angData.TargetAngularVelocity.LengthSquared() > 0.0001f;
        }

        if (hasRealInput)
        {
            _holdOrientationValid = false;
            return;
        }

        // No pilot — hold orientation
        Quaternion gridOrientation = wt.Orientation;
        if (!_holdOrientationValid)
        {
            _holdOrientation = gridOrientation;
            _holdOrientationValid = true;
            Log.Default?.Info("[AERO-HOLD] Captured hold orientation (no pilot)");
        }
        else
        {
            // Re-capture if error is too large (stale target from long ago)
            Quaternion checkErr = Quaternion.Inverse(gridOrientation) * _holdOrientation;
            Vector3 checkEuler = checkErr.ConvertToEuler();
            if (checkEuler.LengthSquared() > 1f) // > ~1 radian total error
            {
                _holdOrientation = gridOrientation;
                Log.Default?.Info("[AERO-HOLD] Re-captured hold orientation (error too large)");
            }
        }

        Vector3 localAngVel = WorldTransform.TransformDirectionInv(angularVelocity, wt);

        Quaternion errorQuat = Quaternion.Inverse(gridOrientation) * _holdOrientation;
        Vector3 eulerError = errorQuat.ConvertToEuler();
        EulerError = eulerError;

        // Output targetAngVel in rad/s — the per-thruster system uses
        // dot(targetAngVel, torqueVec) where torqueVec = Cross(r, forceDir)
        // (unnormalized, arm length included). This matches SE1 RealRCS pattern:
        // thrusters further from CoM naturally get more authority.
        const float HoldKp = 3.0f;   // rad/s per rad error
        const float HoldKd = 1.5f;   // damping
        const float HoldMax = 2.0f;  // max command rad/s

        const float HoldMaxEuler = 1.0f;  // ~57 degrees max euler contribution
        Vector3 holdEulerClamped = new Vector3(
            Math.Clamp(eulerError.X, -HoldMaxEuler, HoldMaxEuler),
            Math.Clamp(eulerError.Y, -HoldMaxEuler, HoldMaxEuler),
            Math.Clamp(eulerError.Z, -HoldMaxEuler, HoldMaxEuler));

        Vector3 attitudeCmd = holdEulerClamped * HoldKp - localAngVel * HoldKd;
        _lastGridAngVel = new Vector3(
            Math.Clamp(attitudeCmd.X, -HoldMax, HoldMax),
            Math.Clamp(attitudeCmd.Y, -HoldMax, HoldMax),
            Math.Clamp(attitudeCmd.Z, -HoldMax, HoldMax));

        if (OffsetThrustJob.Verbose && _simFrameCount % 120 == 0)
            Log.Default?.Info($"[AERO-HOLD] euler=({eulerError.X:F4},{eulerError.Y:F4},{eulerError.Z:F4})" +
                $" angVel=({localAngVel.X:F4},{localAngVel.Y:F4},{localAngVel.Z:F4})" +
                $" cmd=({_lastGridAngVel.X:F4},{_lastGridAngVel.Y:F4},{_lastGridAngVel.Z:F4})");

        // SAS torque for the hidden stability system
        {
            const float SasDamping = 2000000f;
            const float SasAttitude = 1000000f;
            const float SasMaxTorque = 10000000f;

            Vector3 sasDamp = -localAngVel * SasDamping;
            Vector3 clampedEuler = new Vector3(
                MathF.Max(-0.5f, MathF.Min(0.5f, eulerError.X)),
                MathF.Max(-0.5f, MathF.Min(0.5f, eulerError.Y)),
                MathF.Max(-0.5f, MathF.Min(0.5f, eulerError.Z)));
            Vector3 sasAtt = clampedEuler * SasAttitude;

            SasTorque = sasDamp + sasAtt;
            SasTorque = new Vector3(
                MathF.Max(-SasMaxTorque, MathF.Min(SasMaxTorque, SasTorque.X)),
                MathF.Max(-SasMaxTorque, MathF.Min(SasMaxTorque, SasTorque.Y)),
                MathF.Max(-SasMaxTorque, MathF.Min(SasMaxTorque, SasTorque.Z)));
        }
    }

    private int _csLogCooldown;
    private int _csLogOnceCountdown = 300; // log once after 5s regardless of input

    private void UpdateControlSurfaceInputs(Vector3 centerOfMass, WorldTransform wt,
        Vector3 localAngVel, float dynamicPressure, Vector3 bodyTorque)
    {
        // ══════════════════════════════════════════════════════════════════════
        // SAS: runs regardless of control surface count (Bug 1 fix)
        // ══════════════════════════════════════════════════════════════════════
        if (_components.Count == 0) return;

        // ══════════════════════════════════════════════════════════════════════
        // STEP 0: Determine logging trigger
        // ══════════════════════════════════════════════════════════════════════
        // Log on input, or once after startup to dump all vectors
        bool hasInput = false;

        // Peek at input state for log decision
        if (Data.Has<TargetControlData>() || Data.Has<AngularControlData>())
        {
            if (Data.TryGet<AngularControlData>(out var peekAng))
                hasInput = peekAng.TargetAngularVelocity.LengthSquared() > 0.001f;
            if (Data.Has<TargetControlData>())
                hasInput = true; // reticle always counts as input
        }

        bool inputLog = _csLogCooldown <= 0 && hasInput;
        bool startupLog = _csLogOnceCountdown > 0 && --_csLogOnceCountdown == 0;
        bool shouldLog = inputLog || startupLog;
        if (inputLog) _csLogCooldown = 120;
        _csLogCooldown--;

        // ══════════════════════════════════════════════════════════════════════
        // STEP 1: World transform decomposition
        // ══════════════════════════════════════════════════════════════════════
        Quaternion gridOrientation = wt.Orientation;
        Vector3 gridPosition = (Vector3)wt.Position;

        if (shouldLog)
        {
            Log.Default?.Info($"[AERO-CS] ═══ FULL TRACE START ═══");
            Log.Default?.Info($"[AERO-CS] STEP1 wt.Position=({gridPosition.X:F2},{gridPosition.Y:F2},{gridPosition.Z:F2})");
            Log.Default?.Info($"[AERO-CS] STEP1 wt.Orientation=({gridOrientation.X:F5},{gridOrientation.Y:F5},{gridOrientation.Z:F5},{gridOrientation.W:F5}) |q|={MathF.Sqrt(gridOrientation.X*gridOrientation.X + gridOrientation.Y*gridOrientation.Y + gridOrientation.Z*gridOrientation.Z + gridOrientation.W*gridOrientation.W):F6}");
        }

        // ══════════════════════════════════════════════════════════════════════
        // STEP 2: Read raw input data from ECS
        // ══════════════════════════════════════════════════════════════════════
        Vector3 targetAngVel = Vector3.Zero;
        string inputMode = "NONE";

        bool hasTargetData = Data.TryGet<TargetControlData>(out var targetData);
        bool hasAngularData = Data.TryGet<AngularControlData>(out var angularData);

        if (shouldLog)
        {
            // Log gyro capability
            float gyroMaxTorque = PhysicsHack.TryGetGyroMaxTorque(Data);
            if (gyroMaxTorque > 0)
                Log.Default?.Info($"[AERO-CS] STEP2 gyro MaxTorque={gyroMaxTorque:F0} N·m");

            Log.Default?.Info($"[AERO-CS] STEP2 hasTargetControlData={hasTargetData} hasAngularControlData={hasAngularData}");

            if (hasTargetData)
            {
                var tq = targetData.TargetOrientation;
                Log.Default?.Info($"[AERO-CS] STEP2 TargetOrientation=({tq.X:F5},{tq.Y:F5},{tq.Z:F5},{tq.W:F5}) |q|={MathF.Sqrt(tq.X*tq.X + tq.Y*tq.Y + tq.Z*tq.Z + tq.W*tq.W):F6}");
                Log.Default?.Info($"[AERO-CS] STEP2 RelativeCockpitOrientation=({targetData.RelativeCockpitOrientation.X:F5},{targetData.RelativeCockpitOrientation.Y:F5},{targetData.RelativeCockpitOrientation.Z:F5},{targetData.RelativeCockpitOrientation.W:F5})");
            }

            if (hasAngularData)
            {
                var raw = angularData.TargetAngularVelocity;
                Log.Default?.Info($"[AERO-CS] STEP2 AngularControlData.TargetAngularVelocity=({raw.X:F5},{raw.Y:F5},{raw.Z:F5}) |v|={raw.Length():F5}");
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // STEP 3: Compute target angular velocity from input
        // ══════════════════════════════════════════════════════════════════════
        if (hasTargetData)
        {
            inputMode = "RETICLE";
            _holdOrientationValid = false; // re-capture when pilot exits

            // ── OUTER LOOP: Attitude → Desired angular rate ──
            // Orientation error in local frame (same as GridGyroscopesComponent.ComputeTorqueTarget)
            Quaternion errorQuat = Quaternion.Inverse(gridOrientation) * targetData.TargetOrientation;
            Vector3 eulerError = errorQuat.ConvertToEuler(); // radians, local frame (matches game convention)
            EulerError = eulerError; // expose for thruster attitude controller

            // Simple PD controller: proportional on attitude error, derivative on angular rate.
            // ConvertToEuler returns (Pitch, Yaw, Roll) = (X, Y, Z) — same frame as
            // effectiveness vectors and localAngVel. No axis remapping needed.
            const float Kp = 5.0f;    // proportional gain on attitude error (rad)
            const float Kd = 0.5f;    // derivative gain on angular rate (in PD cmd, surface adds more)

            // Roll: use keyboard rate command when available
            Vector3 attitudeCmd = eulerError * Kp;
            if (hasAngularData)
            {
                float rollInput = MathF.Max(-1f, MathF.Min(1f, angularData.TargetAngularVelocity.Z));
                attitudeCmd.Z = rollInput * Kp;  // keyboard overrides attitude roll
            }

            // Clamp per axis
            const float maxCmd = 5.0f;
            attitudeCmd = new Vector3(
                MathF.Max(-maxCmd, MathF.Min(maxCmd, attitudeCmd.X)),
                MathF.Max(-maxCmd, MathF.Min(maxCmd, attitudeCmd.Y * targetData.PerAxisDampeningMultiplier.Y)),
                MathF.Max(-maxCmd, MathF.Min(maxCmd, attitudeCmd.Z)));

            // targetAngVel = PD command (will be projected per-surface in STEP 5)
            targetAngVel = attitudeCmd - localAngVel * Kd;

            // ── FEEDFORWARD: counter body aero torque ──
            // bodyTorque is in local frame from the body aero model (drag, lift on hull).
            // Negate it so surfaces preemptively oppose destabilizing moments.
            // Applied AFTER geometric normalization in STEP 4b, so we add it there instead.
            // (stored for use in STEP 4b)

            // ── SAS: PD orientation tracker ──
            {
                const float SasDamping = 2000000f;   // N·m per rad/s — oppose rotation
                const float SasAttitude = 1000000f;   // N·m per rad — track reticle
                const float SasMaxTorque = 10000000f; // clamp per axis

                Vector3 sasDamp = -localAngVel * SasDamping;

                const float maxEulerCmd = 0.5f;
                Vector3 clampedEuler = new Vector3(
                    MathF.Max(-maxEulerCmd, MathF.Min(maxEulerCmd, eulerError.X)),
                    MathF.Max(-maxEulerCmd, MathF.Min(maxEulerCmd, eulerError.Y)),
                    MathF.Max(-maxEulerCmd, MathF.Min(maxEulerCmd, eulerError.Z)));
                Vector3 sasAtt = clampedEuler * SasAttitude;

                SasTorque = sasDamp + sasAtt;

                SasTorque = new Vector3(
                    MathF.Max(-SasMaxTorque, MathF.Min(SasMaxTorque, SasTorque.X)),
                    MathF.Max(-SasMaxTorque, MathF.Min(SasMaxTorque, SasTorque.Y)),
                    MathF.Max(-SasMaxTorque, MathF.Min(SasMaxTorque, SasTorque.Z)));
            }

            if (shouldLog)
            {
                Log.Default?.Info($"[AERO-CS] STEP3 RETICLE eulerError=({eulerError.X:F5},{eulerError.Y:F5},{eulerError.Z:F5}) rad");
                Log.Default?.Info($"[AERO-CS] STEP3 RETICLE attitudeCmd=({attitudeCmd.X:F5},{attitudeCmd.Y:F5},{attitudeCmd.Z:F5})");
                Log.Default?.Info($"[AERO-CS] STEP3 RETICLE localAngVel=({localAngVel.X:F5},{localAngVel.Y:F5},{localAngVel.Z:F5})");
                Log.Default?.Info($"[AERO-CS] STEP3 RETICLE bodyTorque=({bodyTorque.X:F1},{bodyTorque.Y:F1},{bodyTorque.Z:F1})");
                Log.Default?.Info($"[AERO-CS] STEP3 RETICLE SAS=({SasTorque.X:F0},{SasTorque.Y:F0},{SasTorque.Z:F0})");
                Log.Default?.Info($"[AERO-CS] STEP3 RETICLE PD_cmd=({targetAngVel.X:F5},{targetAngVel.Y:F5},{targetAngVel.Z:F5})");
            }
        }
        else if (hasAngularData && angularData.TargetAngularVelocity.LengthSquared() > 0.0001f)
        {
            inputMode = "KEYBOARD";
            _holdOrientationValid = false; // re-capture when pilot exits

            Vector3 raw = angularData.TargetAngularVelocity;
            targetAngVel = new Vector3(
                MathF.Max(-1f, MathF.Min(1f, raw.X)),
                MathF.Max(-1f, MathF.Min(1f, raw.Y)),
                MathF.Max(-1f, MathF.Min(1f, raw.Z)));

            if (shouldLog)
            {
                Log.Default?.Info($"[AERO-CS] STEP3 KEYBOARD raw=({raw.X:F5},{raw.Y:F5},{raw.Z:F5})");
                Log.Default?.Info($"[AERO-CS] STEP3 targetAngVel_clamped=({targetAngVel.X:F5},{targetAngVel.Y:F5},{targetAngVel.Z:F5})");
            }
        }
        else
        {
            inputMode = "HOLD";
            // _lastGridAngVel and SasTorque already set by UpdateAttitudeHold()
            // which runs before early returns in TryCompute
            targetAngVel = _lastGridAngVel;
        }

        _lastGridAngVel = targetAngVel;

        // ══════════════════════════════════════════════════════════════════════
        // STEP 4: Speed-dependent gain
        // ══════════════════════════════════════════════════════════════════════
        const float QRef = 5000f;
        float gainScale = QRef / MathF.Max(QRef, dynamicPressure);

        if (shouldLog)
        {
            var localVel = _lastVelocityLocal;
            float speed = localVel.Length();
            Vector3 flowDir = speed > 0.1f ? localVel / speed : Vector3.Zero;

            Log.Default?.Info($"[AERO-CS] STEP4 inputMode={inputMode} targetAngVel=({targetAngVel.X:F5},{targetAngVel.Y:F5},{targetAngVel.Z:F5})");
            Log.Default?.Info($"[AERO-CS] STEP4 localAngVel(actual)=({localAngVel.X:F5},{localAngVel.Y:F5},{localAngVel.Z:F5})");
            Log.Default?.Info($"[AERO-CS] STEP4 localVelocity=({localVel.X:F2},{localVel.Y:F2},{localVel.Z:F2}) speed={speed:F1}");
            Log.Default?.Info($"[AERO-CS] STEP4 flowDir=({flowDir.X:F4},{flowDir.Y:F4},{flowDir.Z:F4})");
            Log.Default?.Info($"[AERO-CS] STEP4 dynPressure={dynamicPressure:F1} QRef={QRef:F0} gainScale={gainScale:F5}");
            Log.Default?.Info($"[AERO-CS] STEP4 CoM=({centerOfMass.X:F3},{centerOfMass.Y:F3},{centerOfMass.Z:F3}) blockSize={_blockSize} components={_components.Count}");
        }

        // ══════════════════════════════════════════════════════════════════════
        // STEP 4b: Compute geometric authority per axis (for auto-scaling)
        // ══════════════════════════════════════════════════════════════════════
        // Sum |dot(effNorm, axis)| for each surface on each axis.
        // This counts how many surfaces contribute to each axis, weighted by alignment.
        // No area/q — those affect force magnitude but the controller just sets deflection [-1,1].
        // Result: command=1 → all surfaces deflect to ~1 on that axis.
        Vector3 totalAuthority = Vector3.Zero;     // geometric (for normalizing inner loop)
        Vector3 torqueAuthority = Vector3.Zero;   // physical (N·m per unit deflection, for feedforward)
        for (int i = 0; i < _components.Components.Count; i++)
        {
            if (_components.Components[i] is ControlSurface csAuth)
            {
                Vector3 r = csAuth.Position - centerOfMass;
                Vector3 ld = Vector3.Cross(csAuth.HingeAxis, csAuth.ChordDirection);
                Vector3 eff = Vector3.Cross(r, ld);
                float el = eff.Length();
                if (el < 0.01f) continue;
                Vector3 en = eff / el;
                totalAuthority += new Vector3(
                    MathF.Abs(en.X),
                    MathF.Abs(en.Y),
                    MathF.Abs(en.Z));
                // Torque authority: how much torque (N·m) full deflection produces per axis
                // ≈ effLen * area * q * clAlpha_estimate * maxDeflection_rad
                float torquePerDefl = el * csAuth.Area * dynamicPressure * 6.0f * (10f * MathF.PI / 180f);
                torqueAuthority += new Vector3(
                    MathF.Abs(en.X) * torquePerDefl,
                    MathF.Abs(en.Y) * torquePerDefl,
                    MathF.Abs(en.Z) * torquePerDefl);
            }
        }

        // No authority normalization — let the dot product in STEP 5 naturally
        // distribute commands to surfaces based on their effectiveness alignment.
        if (inputMode == "RETICLE" && shouldLog)
        {
            Log.Default?.Info($"[AERO-CS] STEP4b PD cmd=({targetAngVel.X:F5},{targetAngVel.Y:F5},{targetAngVel.Z:F5})");
        }

        // ══════════════════════════════════════════════════════════════════════
        // STEP 5: Per-surface loop
        // ══════════════════════════════════════════════════════════════════════
        for (int i = 0; i < _components.Components.Count; i++)
        {
            if (_components.Components[i] is HelicopterRotor rotor)
            {
                // Feed orientation data to rotor for cyclic/yaw PD control
                rotor.CurrentOrientation = gridOrientation;
                rotor.LocalAngularVelocity = localAngVel;
                rotor.OrientationHoldActive = hasTargetData;
                if (hasTargetData)
                    rotor.TargetOrientation = targetData.TargetOrientation;

                // Feed collective pitch from matching thruster state.
                // The rotor block IS a thruster — find it by block position.
                rotor.CollectivePitch = 0f;
                for (int t = 0; t < _thrusterCache.Count; t++)
                {
                    var ti = _thrusterCache[t];
                    // Match by position proximity (same block)
                    float dx = ti.GridLocalPosition.X - rotor.Position.X;
                    float dy = ti.GridLocalPosition.Y - rotor.Position.Y;
                    float dz = ti.GridLocalPosition.Z - rotor.Position.Z;
                    if (dx * dx + dy * dy + dz * dz > _blockSize * _blockSize)
                        continue;

                    // Read thrust state: IsThrusting or override
                    var td = ti.ThrusterEntity.Data;
                    float overridePower = PhysicsHack.GetThrustOverride(td);
                    if (overridePower > 0f)
                        rotor.CollectivePitch = overridePower;
                    else if (PhysicsHack.IsEntityThrusting(td))
                        rotor.CollectivePitch = 1f;
                    break;
                }
            }
            else if (_components.Components[i] is Airbrake ab)
            {
                ab.DeployFraction = 1f;
            }
            else if (_components.Components[i] is ControlSurface cs)
            {
                // 5a. Moment arm from CoM to surface position
                Vector3 r = cs.Position - centerOfMass;

                // 5b. Lift direction = cross(hinge, chord)
                //     This is the direction of force when the surface deflects
                Vector3 liftDir = Vector3.Cross(cs.HingeAxis, cs.ChordDirection);
                float liftDirLen = liftDir.Length();

                // 5c. Torque axis from positive deflection.
                //     Torque = Cross(r, F). Positive DeflectionInput → deflRad = -input*maxDefl
                //     → positive AoA → positive lift → torque = Cross(r, liftDir*lift).
                //     effectiveness = Cross(r, liftDir): torque direction from positive lift.
                //     But positive input → positive lift, so effectiveness IS the torque direction of +input.
                Vector3 effectiveness = Vector3.Cross(r, liftDir);
                float effLen = effectiveness.Length();

                if (effLen < 0.01f)
                {
                    cs.DeflectionInput = 0;
                    if (shouldLog)
                    {
                        Log.Default?.Info($"[AERO-CS] CS#{i} SKIPPED effLen={effLen:F4} pos=({cs.Position.X:F3},{cs.Position.Y:F3},{cs.Position.Z:F3}) blockPos=({cs.BlockPosition.X},{cs.BlockPosition.Y},{cs.BlockPosition.Z})");
                        Log.Default?.Info($"[AERO-CS] CS#{i} SKIPPED hinge=({cs.HingeAxis.X:F3},{cs.HingeAxis.Y:F3},{cs.HingeAxis.Z:F3}) chord=({cs.ChordDirection.X:F3},{cs.ChordDirection.Y:F3},{cs.ChordDirection.Z:F3})");
                        Log.Default?.Info($"[AERO-CS] CS#{i} SKIPPED liftDir=({liftDir.X:F3},{liftDir.Y:F3},{liftDir.Z:F3}) r=({r.X:F3},{r.Y:F3},{r.Z:F3}) eff=({effectiveness.X:F5},{effectiveness.Y:F5},{effectiveness.Z:F5})");
                    }
                    continue;
                }

                // 5d. Normalize effectiveness to unit vector
                Vector3 effNorm = effectiveness / effLen;

                // 5e. Control law — mode-dependent
                float scaledInput;
                float command;
                float damping;

                if (inputMode == "RETICLE")
                {
                    // Reticle mode: PD command already includes damping.
                    // Use actual torque direction (Cross(r, liftDir)) for projection,
                    // but ALSO add direct damping per-surface to fight angular velocity.
                    command = Vector3.Dot(effNorm, targetAngVel);
                    // Add strong per-surface damping to prevent overshoot
                    float angVelProjection = Vector3.Dot(effNorm, localAngVel);
                    damping = angVelProjection * 3.0f;
                    scaledInput = command - damping;
                }
                else
                {
                    // Keyboard mode: PD controller with speed-dependent gain
                    const float Kp = 1.0f;
                    const float Kd = 1.5f;
                    command = Vector3.Dot(effNorm, targetAngVel) * Kp;
                    damping = Vector3.Dot(effNorm, localAngVel) * Kd;
                    scaledInput = command * gainScale - damping;
                }
                float clampedInput = MathF.Max(-1f, MathF.Min(1f, scaledInput));
                cs.DeflectionInput = clampedInput;

                if (shouldLog)
                {
                    // 5f. Compute derived values for logging (Rodrigues deflection, negated to match Compute)
                    float deflRad = -cs.DeflectionInput * cs.MaxDeflection * MathF.PI / 180f;
                    float cosD = MathF.Cos(deflRad);
                    float sinD = MathF.Sin(deflRad);
                    Vector3 deflChord = cs.ChordDirection * cosD +
                        Vector3.Cross(cs.HingeAxis, cs.ChordDirection) * sinD +
                        cs.HingeAxis * Vector3.Dot(cs.HingeAxis, cs.ChordDirection) * (1f - cosD);
                    Vector3 deflSurfNormal = Vector3.Cross(cs.HingeAxis, deflChord);
                    float deflSurfNormLen = deflSurfNormal.Length();
                    if (deflSurfNormLen > 1e-6f) deflSurfNormal /= deflSurfNormLen;

                    // Flow at this point
                    float localSpeed = _lastVelocityLocal.Length();
                    Vector3 localFlowDir = localSpeed > 0.1f ? _lastVelocityLocal / localSpeed : Vector3.Zero;

                    // AoA decomposition
                    float dotChordFlow = Vector3.Dot(-localFlowDir, deflChord);
                    float dotNormalFlow = Vector3.Dot(-localFlowDir, deflSurfNormal);
                    float aoaRad = MathF.Atan2(dotNormalFlow, dotChordFlow);

                    // Individual dot products for debugging effectiveness
                    float dotEffTarget = Vector3.Dot(effNorm, targetAngVel);
                    float dotEffAngVel = Vector3.Dot(effNorm, localAngVel);

                    // Cross product verification (matches code: cross(liftDir, r))
                    Vector3 torqueFromLift = Vector3.Cross(liftDir, r);

                    Log.Default?.Info($"[AERO-CS] CS#{i} GEOM blockPos=({cs.BlockPosition.X},{cs.BlockPosition.Y},{cs.BlockPosition.Z}) pos=({cs.Position.X:F3},{cs.Position.Y:F3},{cs.Position.Z:F3})");
                    Log.Default?.Info($"[AERO-CS] CS#{i} GEOM hinge=({cs.HingeAxis.X:F3},{cs.HingeAxis.Y:F3},{cs.HingeAxis.Z:F3}) chord=({cs.ChordDirection.X:F3},{cs.ChordDirection.Y:F3},{cs.ChordDirection.Z:F3}) dot(h,c)={Vector3.Dot(cs.HingeAxis, cs.ChordDirection):F5}");
                    Log.Default?.Info($"[AERO-CS] CS#{i} ARM CoM=({centerOfMass.X:F3},{centerOfMass.Y:F3},{centerOfMass.Z:F3}) r=({r.X:F3},{r.Y:F3},{r.Z:F3}) |r|={r.Length():F3}");
                    Log.Default?.Info($"[AERO-CS] CS#{i} EFF liftDir=({liftDir.X:F3},{liftDir.Y:F3},{liftDir.Z:F3}) eff_raw=cross(liftDir,r)=({torqueFromLift.X:F5},{torqueFromLift.Y:F5},{torqueFromLift.Z:F5}) |eff|={effLen:F5}");
                    Log.Default?.Info($"[AERO-CS] CS#{i} EFF effNorm=({effNorm.X:F5},{effNorm.Y:F5},{effNorm.Z:F5})");
                    Log.Default?.Info($"[AERO-CS] CS#{i} PD dot(eff,target)={dotEffTarget:F5} cmd={command:F5} dot(eff,angVel)={dotEffAngVel:F5} damp={damping:F5} mode={inputMode}");
                    Log.Default?.Info($"[AERO-CS] CS#{i} PD scaled={scaledInput:F5}->clamped={clampedInput:F5}");
                    Log.Default?.Info($"[AERO-CS] CS#{i} DEFL input={cs.DeflectionInput:F5} deg={cs.DeflectionInput * cs.MaxDeflection:F2} maxDefl={cs.MaxDeflection:F1} deflRad={deflRad:F5}");
                    Log.Default?.Info($"[AERO-CS] CS#{i} DEFL deflChord=({deflChord.X:F5},{deflChord.Y:F5},{deflChord.Z:F5}) surfNorm=({deflSurfNormal.X:F5},{deflSurfNormal.Y:F5},{deflSurfNormal.Z:F5})");
                    Log.Default?.Info($"[AERO-CS] CS#{i} FLOW vel=({_lastVelocityLocal.X:F2},{_lastVelocityLocal.Y:F2},{_lastVelocityLocal.Z:F2}) spd={localSpeed:F1} flowDir=({localFlowDir.X:F4},{localFlowDir.Y:F4},{localFlowDir.Z:F4})");
                    Log.Default?.Info($"[AERO-CS] CS#{i} AOA dot(-flow,chord)={dotChordFlow:F5} dot(-flow,norm)={dotNormalFlow:F5} atan2={aoaRad:F5}rad ({aoaRad * 180f / MathF.PI:F2}°)");
                    Log.Default?.Info($"[AERO-CS] CS#{i} FORCE AoA={cs.EffectiveAoA:F2}° CL={cs.EffectiveCp:F5} L={cs.CurrentLift:F1}N D={cs.CurrentDrag:F1}N area={cs.Area:F3}m²");
                }
            }
        }

        if (shouldLog)
        {
            Log.Default?.Info($"[AERO-CS] ═══ FULL TRACE END ═══");
        }
    }

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
        long tb = AeroCost.Start();
        var boxes = new List<(Vector3I, Vector3I)>();
        foreach (var block in _octree.GetAllCubeBlocks())
        {
            if (block == null) continue;
            foreach (var g in block.GetTransformedOccupiedCellGroups()) boxes.Add((g.Min, g.Max));
        }
        AeroCost.Begin.Stop(tb);
        var surface = _buildSurface;
        var manifold = _buildManifold;
        var detector = _model.Detector;
        float blockSize = _blockSize;
        _builtWings = null; _builtShadow = null;
        var spareShadow = _spareShadow; _spareShadow = null;
        // (its own thread, not a thread-pool one: a rebuild runs ~1 s on a big grid, and the game's own async work
        // - physics queries, ray casts - waits on the pool: back-to-back rebuilds starved it, 0.3-0.6 s stalls)
        _lastRebuildStart = System.Diagnostics.Stopwatch.GetTimestamp();
        _finalizeTask = System.Threading.Tasks.Task.Factory.StartNew(() =>
        {
            // below the game's own threads: a rebuild must never compete with a frame
            try { System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.BelowNormal; } catch { }
            long t = AeroCost.Start();
            long m0 = GC.GetAllocatedBytesForCurrentThread(), m;
            var snapshot = new SnapshotGridAccessor(boxes);
            long aSnap = (m = GC.GetAllocatedBytesForCurrentThread()) - m0; m0 = m;
            surface.BeginBuild(snapshot, blockSize);
            while (!surface.AddCellBatch(int.MaxValue)) { }
            long aFaces = (m = GC.GetAllocatedBytesForCurrentThread()) - m0; m0 = m;
            AeroCost.Batch.Stop(t); t = AeroCost.Start();
            surface.FinalizeBuild();
            long aFin = (m = GC.GetAllocatedBytesForCurrentThread()) - m0; m0 = m;
            AeroCost.FinSurface.Stop(t); t = AeroCost.Start();
            manifold.Classify(surface);
            long aCls = (m = GC.GetAllocatedBytesForCurrentThread()) - m0; m0 = m;
            AeroCost.FinClassify.Stop(t); t = AeroCost.Start();
            if (detector is ConnectedComponentWingDetector cc) { cc.Manifold = manifold; cc.ManifoldSurface = surface; }
            detector.Invalidate();
            _builtWings = detector.Detect(snapshot, surface, blockSize);
            long aWing = (m = GC.GetAllocatedBytesForCurrentThread()) - m0; m0 = m;
            AeroCost.FinWings.Stop(t); t = AeroCost.Start();
            var shadow = spareShadow ?? new PrecomputedShadowMap();
            shadow.Manifold = manifold;
            shadow.PrecomputeAll(snapshot, surface);
            _builtShadow = shadow;
            long aShd = (m = GC.GetAllocatedBytesForCurrentThread()) - m0;
            AeroCost.FinShadow.Stop(t);
            AeroCost.RebuildAlloc = $"rebuild alloc MB: snapshot {aSnap / 1048576.0:F1} faces {aFaces / 1048576.0:F1} finalize {aFin / 1048576.0:F1} classify {aCls / 1048576.0:F1} wings {aWing / 1048576.0:F1} shadow {aShd / 1048576.0:F1} ({surface.FaceCount} faces)";
        }, System.Threading.CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.LongRunning, System.Threading.Tasks.TaskScheduler.Default);
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
                Log.Default?.Info($"[AERO] background rebuild failed ({task.Exception?.GetBaseException().Message}); rebuilding in one go");
            else
            {
                // The swap (simulation thread): the finished surface, its classification and its wings go live.
                long ts = AeroCost.Start();
                (_surface, _buildSurface) = (_buildSurface, _surface);
                (_manifold, _buildManifold) = (_buildManifold, _manifold);
                _staggeredBuildActive = false;
                if (_model.Detector is ConnectedComponentWingDetector cc) { cc.Manifold = _manifold; cc.ManifoldSurface = _surface; }
                _model.InstallWings(_builtWings);
                _builtWings = null;
                if (_model.InnerModel is DampedShadowedDragModel dsm && _builtShadow != null) _spareShadow = dsm.InstallShadowMap(_builtShadow);
                _builtShadow = null;
                _components.Clear();
                _factory.CreateAll(_octree, _blockSize, _components);
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
        _faceOverridesDirty = true;
        AeroCost.FinComponents.Stop(tf);
    }

    private float DetectBlockSize()
    {
        if (_cachedBlockSize >= 0)
            return _cachedBlockSize;

        var blocks = _octree.GetAllCubeBlocks();
        foreach (var block in blocks)
        {
            if (block == null) continue;
            foreach (var cellGroup in block.GetTransformedOccupiedCellGroups())
            {
                var extent = cellGroup.Max - cellGroup.Min + Vector3I.One;
                int maxExtent = Math.Max(extent.X, Math.Max(extent.Y, extent.Z));
                _cachedBlockSize = maxExtent >= 5 ? AeroConfig.LargeBlockSize : AeroConfig.SmallBlockSize;
                return _cachedBlockSize;
            }
        }
        _cachedBlockSize = AeroConfig.LargeBlockSize;
        return _cachedBlockSize;
    }
}
