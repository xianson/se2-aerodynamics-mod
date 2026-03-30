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
    private readonly BlockOctreeComponent _octree;

    // ── Aero pipeline state ──

    private Se2GridAccessor _gridAccessor;
    private SmoothSurfaceProvider _surface;       // active surface (used for force computation)
    private SmoothSurfaceProvider _buildSurface;  // back buffer (used during staggered rebuild)
    private LiftingSurfaceModel _model;
    private ManifoldClassifier _manifold;
    private AeroComponentRegistry _components;
    private BlockComponentFactory _factory;
    private float _blockSize;
    private float _cachedBlockSize = -1;
    private bool _dirty;
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
    private Quaternion _holdOrientation = Quaternion.Identity; // orientation to hold when unpiloted
    private bool _holdOrientationValid;                        // true once captured

    // ── Orientation settling test ──
    internal int _diagPhase = 100;       // 0=stabilize, 1=running tests, 2=summary, 4=level flight, 5=CS only, 6=thrust-only attitude, 100+=done
    // NOTE: set to 0 to run full SAS settling tests, 4 to skip straight to CS testing, 6 for thrust-only
    private int _diagFrames = 0;
    private bool _diagGyrosKilled;
    private bool _diagGyrosDisabledViaTerminal;
    internal bool DiagActive => _diagPhase < 100;

    private int _settleTestIndex;
    private int _settleSubPhase;     // 0=kick, 1=settle, 2=brake
    private int _settleHoldFrames;   // consecutive frames within tolerance
    private List<(string name, bool passed, float errorDeg, float timeSec)> _settleResults = new();
    private Quaternion _fixedTargetQ;  // stored target for phase 4 (re-applied every frame)
    private Vector3 _fixedHeadingDir;  // desired horizontal forward direction (world space)
    private Vector3D _testStartPosition; // saved position for teleporting back between tests
    private Vector3 _testStartVelocity;  // saved velocity for restoring between tests

    // ── Accumulated changes for incremental wing update ──
    private List<Vector3I> _wingAddedCells = new();
    private List<Vector3I> _wingRemovedCells = new();

    // ── Batched block change accumulation ──
    private List<Vector3I> _pendingAddedCells = new();
    private List<Vector3I> _pendingRemovedCells = new();

    // ── Offset thrust (RCS) ──
    private List<ThrusterInfo> _thrusterCache = new();
    private bool _thrusterCacheDirty = true;

    // ── Gyroscope control ──
    internal List<GyroInfo> _gyroCache = new();
    private bool _gyroCacheDirty = true;

    // ── Ground effect ──
    /// <summary>Height above ground in meters. -1 = unknown.</summary>
    internal float GroundHeight = -1f;

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

        // Defer heavy work (surface build, wing detection) to first compute call
        _dirty = true;
        _fullRebuildNeeded = true;
        _initialized = true;
    }

    void IInSceneListener.OnBeforeRemovedFromScene()
    {
        _initialized = false;
        AeroScheduler.Remove(this);
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
            _thrusterCacheDirty = true;
            _gyroCacheDirty = true;
            _cachedBlockSize = -1;
            _pendingAddedCells.Clear();
            _pendingRemovedCells.Clear();
            _components.Clear();
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
        _thrusterCacheDirty = true;
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
        AeroScheduler.EnsureTicked();

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
            else if (_surfaceCooldownTicks > 0)
            {
                // Still receiving rapid changes — keep accumulating
                _surfaceCooldownTicks--;
            }
            else
            {
                // Cooldown expired — flush all accumulated changes
                _gridAccessor.SetOctree(_octree);
                _blockSize = DetectBlockSize();

                int changedCells = _pendingAddedCells.Count + _pendingRemovedCells.Count;
                int faceCount = _surface.RawFaceCount;

                if (faceCount > 0 && changedCells > faceCount / 5)
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
            }
        }
        else if (_dirty && _staggeredBuildActive)
        {
            // Topology changed mid-build — flag for restart on next scheduler tick
            _rebuildRestartNeeded = true;
            _dirty = false;
            _pendingAddedCells.Clear();
            _pendingRemovedCells.Clear();
        }

        AeroStats.SetSurf(AeroStats.ElapsedUs(surfaceStart));

        // ── Deferred full wing detection (correctness pass with ray-march) ──
        long wingStart = AeroStats.Timestamp();
        if (_wingsDirty && !_staggeredBuildActive)
        {
            if (--_wingCooldownTicks <= 0)
            {
                _model.InvalidateWings();
                _model.DetectWings(_gridAccessor, _surface, _blockSize);
                _manifold.Classify(_surface);
                _faceOverridesDirty = true;
                _wingsDirty = false;
                _wingAddedCells.Clear();
                _wingRemovedCells.Clear();
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
            _faceOverridesDirty = false;
        }
        if (_model.InnerModel is DampedShadowedDragModel dsmCp)
            _model.OverrideFaceCp(dsmCp, _components.Components);
        AeroStats.SetFaceOvr(AeroStats.ElapsedUs(faceOverrideStart));

        HasResult = true;
    }

    // ── Self-test (math-only, no physics) ──

    private bool _selfTestDone;

    private void RunSelfTest(WorldTransform wt, Vector3 localAngVel)
    {
        if (_selfTestDone) return;
        _selfTestDone = true;
        return; // disabled — re-enable for diagnostics

        Log.Default?.Info("[AERO-TEST] ═══ MATH SELF-TEST ═══");
        int passed = 0, failed = 0;

        Quaternion q = wt.Orientation;

        // Test 1: Round-trip transform (local → world → local = identity)
        Vector3 toWorld = Vector3.Transform(localAngVel, q);
        Vector3 backToLocal = Vector3.Transform(toWorld, Quaternion.Inverse(q));
        float roundTripError = (backToLocal - localAngVel).Length();
        LogTest(ref passed, ref failed, 1, "Round-trip transform",
            roundTripError < 1e-4f, $"error={roundTripError:E3}");

        // Test 2: Damping sign — negation in local opposes world velocity
        Vector3 worldAngVel = Vector3.Transform(localAngVel, q);
        Vector3 dampLocal = -localAngVel;
        Vector3 dampWorld = Vector3.Transform(dampLocal, q);
        float dotProduct = Vector3.Dot(dampWorld, worldAngVel);
        LogTest(ref passed, ref failed, 2, "Damping opposes velocity",
            dotProduct <= 0.001f || worldAngVel.LengthSquared() < 1e-10f,
            $"dot={dotProduct:F6} (should be ≤0)");

        // Tests 3-5: Per-axis damping maps correctly
        for (int axis = 0; axis < 3; axis++)
        {
            Vector3 testLocal = Vector3.Zero;
            if (axis == 0) testLocal.X = 1f;
            else if (axis == 1) testLocal.Y = 1f;
            else testLocal.Z = 1f;

            Vector3 dampedWorld = Vector3.Transform(-testLocal, q);
            Vector3 originalWorld = Vector3.Transform(testLocal, q);
            float axisDot = Vector3.Dot(dampedWorld, originalWorld);
            string axisName = axis == 0 ? "X" : (axis == 1 ? "Y" : "Z");
            LogTest(ref passed, ref failed, 3 + axis, $"Damping axis {axisName}",
                axisDot < -0.99f, $"dot={axisDot:F6} (should be -1.0)");
        }

        // Test 6: Euler error direction (informational)
        Vector3 testEuler = new Vector3(0.1f, 0f, 0f);
        Vector3 attWorld = Vector3.Transform(testEuler, q);
        Log.Default?.Info($"[AERO-TEST] Test 6: Euler +X(0.1) → worldTorque=({attWorld.X:F5},{attWorld.Y:F5},{attWorld.Z:F5})");

        // Test 7: PhysicsHack available
        bool physOk = PhysicsHack.Available;
        LogTest(ref passed, ref failed, 7, "PhysicsHack available", physOk, "");

        // Test 8: Grid orientation is unit quaternion
        float qLen = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        LogTest(ref passed, ref failed, 8, "Orientation is unit quaternion",
            MathF.Abs(qLen - 1f) < 0.01f, $"|q|={qLen:F6}");

        Log.Default?.Info($"[AERO-TEST] ═══ RESULTS: {passed}/{passed + failed} PASSED ═══");
    }

    private void LogTest(ref int passed, ref int failed, int num, string name, bool pass, string detail)
    {
        if (pass) { passed++; Log.Default?.Info($"[AERO-TEST] Test {num}: {name} — PASS ✓ {detail}"); }
        else { failed++; Log.Default?.Info($"[AERO-TEST] Test {num}: {name} — FAIL ✗ {detail}"); }
    }

    // ── Orientation settling test ──
    //
    // Verifies that SAS + control surfaces can settle the vehicle to the
    // target orientation from perturbations of up to +/-15 deg pitch and roll.
    //
    // For each test case:
    //   1. KICK:   Apply torque pulse until euler error reaches target angle
    //   2. SETTLE: Let SAS PD controller + aero surfaces recover
    //   3. BRAKE:  Stop rotation before next test
    //
    // Pass: euler error < 2 deg and angular vel < 0.02 rad/s held for 1 second.

    private readonly struct SettleTestCase
    {
        public readonly string Name;
        public readonly Vector3 KickDir;
        public readonly float TargetDeg;
        public SettleTestCase(string n, Vector3 d, float t) { Name = n; KickDir = d; TargetDeg = t; }
    }

    private static Vector3 NormVec(float x, float y, float z)
    {
        float len = MathF.Sqrt(x * x + y * y + z * z);
        return new Vector3(x / len, y / len, z / len);
    }

    private static readonly SettleTestCase[] SettleTests =
    {
        // Pure pitch (rotation about X axis — game convention: X=pitch, Y=yaw, Z=roll)
        new("Pitch +10",  new Vector3(1, 0, 0),  10f),
        new("Pitch -10",  new Vector3(-1, 0, 0), 10f),
        new("Pitch +15",  new Vector3(1, 0, 0),  15f),
        new("Pitch -15",  new Vector3(-1, 0, 0), 15f),
        // Pure yaw (rotation about Y axis)
        new("Yaw +10",    new Vector3(0, 1, 0),  10f),
        new("Yaw -10",    new Vector3(0, -1, 0), 10f),
        new("Yaw +15",    new Vector3(0, 1, 0),  15f),
        new("Yaw -15",    new Vector3(0, -1, 0), 15f),
        // Pure roll (rotation about Z axis)
        new("Roll +10",   new Vector3(0, 0, 1),  10f),
        new("Roll -10",   new Vector3(0, 0, -1), 10f),
        new("Roll +15",   new Vector3(0, 0, 1),  15f),
        new("Roll -15",   new Vector3(0, 0, -1), 15f),
        // Combined pitch + roll
        new("P+10 R+10",  NormVec(1, 0, 1),  14f),
        new("P-10 R-10",  NormVec(-1, 0, -1), 14f),
        // Combined pitch + yaw
        new("P+10 Y+10",  NormVec(1, 1, 0),  14f),
        new("P-10 Y-10",  NormVec(-1, -1, 0), 14f),
        // Combined all three
        new("P+10 Y+5 R+5", NormVec(2, 1, 1), 12f),
        new("P-5 Y+10 R-5", NormVec(-1, 2, -1), 12f),
    };

    private Vector3 ComputeDiagSasTorque(Vector3 localAngVel, Vector3 eulerError,
        Vector3 targetAngVel, WorldTransform wt)
    {
        const float KickTorque = 8_000_000f;
        const int KickMaxFrames = 120;       // 2s max kick
        const float BrakeGain = 3_000_000f;
        const int BrakeMinFrames = 120;      // 2s min brake
        const int SettleMaxFrames = 600;     // 10s max settle
        const float SettleErrorDeg = 2f;
        const float SettleAngSpeedMax = 0.02f;
        const int SettleHoldRequired = 60;   // 1s at 60Hz
        const float Rad2Deg = 180f / MathF.PI;

        // SAS gains (must match real SAS)
        const float SasDamping = 2_000_000f;
        const float SasAttitude = 1_000_000f;
        const float SasMaxTorque = 10_000_000f;
        const float MaxEulerCmd = 0.5f;

        // ── Kill game gyros once (only during active diagnostics) ──
        if (!_diagGyrosKilled && DiagActive)
        {
            _diagGyrosKilled = true;
            bool killed = PhysicsHack.TryZeroGyroTorque(Data);
            Log.Default?.Info($"[SETTLE] Gyro kill: {(killed ? "OK" : "FAILED")}");
        }

        float errorDeg = eulerError.Length() * Rad2Deg;

        // ══════════════════════════════════════════════════════════
        // Phase 0: Stabilize — brake to ~zero before starting tests
        // ══════════════════════════════════════════════════════════
        if (_diagPhase == 0)
        {
            _diagFrames++;

            if (_diagFrames == 1)
            {
                Log.Default?.Info("[SETTLE] ══════════════════════════════════════════════");
                Log.Default?.Info("[SETTLE] ORIENTATION SETTLING TEST");
                Log.Default?.Info($"[SETTLE] {SettleTests.Length} test cases, +/-15 deg max perturbation");
                Log.Default?.Info($"[SETTLE] Pass: error < {SettleErrorDeg} deg and w < {SettleAngSpeedMax} rad/s for {SettleHoldRequired / 60f:F1}s");
                Log.Default?.Info("[SETTLE] ══════════════════════════════════════════════");

                if (PhysicsHack.TryGetInertiaData(Data, out Vector3 invI, out _))
                {
                    Vector3 I = new Vector3(
                        invI.X > 1e-12f ? 1f / invI.X : 0f,
                        invI.Y > 1e-12f ? 1f / invI.Y : 0f,
                        invI.Z > 1e-12f ? 1f / invI.Z : 0f);
                    Log.Default?.Info($"[SETTLE] Inertia=({I.X:F0},{I.Y:F0},{I.Z:F0}) kg*m^2");
                }
                PhysicsHack.TryGetMassProperties(Data, out float mass, out _);
                Log.Default?.Info($"[SETTLE] Mass={mass:F0}kg");
                Log.Default?.Info($"[SETTLE] Initial euler error={errorDeg:F1} deg");
                Log.Default?.Info("[SETTLE] Stabilizing...");

                _settleTestIndex = 0;
                _settleSubPhase = 0;
                _settleHoldFrames = 0;
                _settleResults.Clear();
            }

            if (_diagFrames % 60 == 0)
                Log.Default?.Info($"[SETTLE] stab f={_diagFrames} w={localAngVel.Length():F4} err={errorDeg:F1} deg");

            if (localAngVel.LengthSquared() < 0.0001f && _diagFrames > 60)
            {
                Log.Default?.Info("[SETTLE] Stabilized. Starting tests.");
                _diagPhase = 1;
                _diagFrames = 0;
            }

            return BrakingTorque(localAngVel, BrakeGain);
        }

        // ══════════════════════════════════════════════════════════
        // Phase 1: Run test cases (kick -> settle -> brake loop)
        // ══════════════════════════════════════════════════════════
        if (_diagPhase == 1)
        {
            if (_settleTestIndex >= SettleTests.Length)
            {
                _diagPhase = 2;
                _diagFrames = 0;
            }
            else
            {
                var test = SettleTests[_settleTestIndex];
                _diagFrames++;

                // ── Sub-phase 0: KICK ──
                if (_settleSubPhase == 0)
                {
                    if (_diagFrames == 1)
                    {
                        Log.Default?.Info($"[SETTLE] ── TEST {_settleTestIndex + 1}/{SettleTests.Length}: {test.Name} ──");
                        Log.Default?.Info($"[SETTLE] KICK target={test.TargetDeg:F0} deg dir=({test.KickDir.X:F2},{test.KickDir.Y:F2},{test.KickDir.Z:F2})");
                    }

                    if (_diagFrames % 30 == 0)
                        Log.Default?.Info($"[SETTLE] KICK f={_diagFrames} error={errorDeg:F1} deg w={localAngVel.Length():F4}");

                    if (errorDeg >= test.TargetDeg || _diagFrames > KickMaxFrames)
                    {
                        Log.Default?.Info($"[SETTLE] KICK done: error={errorDeg:F1} deg after {_diagFrames} frames");
                        Log.Default?.Info($"[SETTLE]   euler=({eulerError.X * Rad2Deg:F2},{eulerError.Y * Rad2Deg:F2},{eulerError.Z * Rad2Deg:F2}) deg");
                        Log.Default?.Info($"[SETTLE]   w=({localAngVel.X:F4},{localAngVel.Y:F4},{localAngVel.Z:F4}) |w|={localAngVel.Length():F4}");

                        if (_diagFrames > KickMaxFrames)
                            Log.Default?.Info($"[SETTLE]   WARNING: kick timed out, only reached {errorDeg:F1} deg");

                        _settleSubPhase = 1;
                        _settleHoldFrames = 0;
                        _diagFrames = 0;
                        return Vector3.Zero;
                    }

                    return test.KickDir * KickTorque;
                }

                // ── Sub-phase 1: SETTLE ──
                if (_settleSubPhase == 1)
                {
                    if (_diagFrames % 120 == 0)
                    {
                        Log.Default?.Info($"[SETTLE] SETTLE t={_diagFrames / 60f:F1}s error={errorDeg:F2} deg w={localAngVel.Length():F4}");
                        Log.Default?.Info($"[SETTLE]   euler=({eulerError.X * Rad2Deg:F2},{eulerError.Y * Rad2Deg:F2},{eulerError.Z * Rad2Deg:F2}) deg");
                    }

                    bool withinTolerance = errorDeg < SettleErrorDeg &&
                                           localAngVel.Length() < SettleAngSpeedMax;

                    if (withinTolerance)
                    {
                        _settleHoldFrames++;
                        if (_settleHoldFrames >= SettleHoldRequired)
                        {
                            float settleTime = _diagFrames / 60f;
                            Log.Default?.Info($"[SETTLE] >> PASS: {test.Name} settled in {settleTime:F2}s (error={errorDeg:F2} deg w={localAngVel.Length():F4})");
                            _settleResults.Add((test.Name, true, errorDeg, settleTime));
                            _settleSubPhase = 2;
                            _diagFrames = 0;
                            return BrakingTorque(localAngVel, BrakeGain);
                        }
                    }
                    else
                    {
                        _settleHoldFrames = 0;
                    }

                    if (_diagFrames >= SettleMaxFrames)
                    {
                        float settleTime = _diagFrames / 60f;
                        Log.Default?.Info($"[SETTLE] >> FAIL: {test.Name} did not settle in {settleTime:F1}s (error={errorDeg:F2} deg w={localAngVel.Length():F4})");
                        Log.Default?.Info($"[SETTLE]   euler=({eulerError.X * Rad2Deg:F2},{eulerError.Y * Rad2Deg:F2},{eulerError.Z * Rad2Deg:F2}) deg");
                        _settleResults.Add((test.Name, false, errorDeg, settleTime));
                        _settleSubPhase = 2;
                        _diagFrames = 0;
                        return BrakingTorque(localAngVel, BrakeGain);
                    }

                    // Apply SAS PD torque (same formula as real SAS lines 930-948)
                    Vector3 sasDamp = -localAngVel * SasDamping;
                    Vector3 clampedEuler = new Vector3(
                        MathF.Max(-MaxEulerCmd, MathF.Min(MaxEulerCmd, eulerError.X)),
                        MathF.Max(-MaxEulerCmd, MathF.Min(MaxEulerCmd, eulerError.Y)),
                        MathF.Max(-MaxEulerCmd, MathF.Min(MaxEulerCmd, eulerError.Z)));
                    Vector3 sasAtt = clampedEuler * SasAttitude;
                    Vector3 sas = sasDamp + sasAtt;
                    return new Vector3(
                        MathF.Max(-SasMaxTorque, MathF.Min(SasMaxTorque, sas.X)),
                        MathF.Max(-SasMaxTorque, MathF.Min(SasMaxTorque, sas.Y)),
                        MathF.Max(-SasMaxTorque, MathF.Min(SasMaxTorque, sas.Z)));
                }

                // ── Sub-phase 2: BRAKE ──
                if (_settleSubPhase == 2)
                {
                    if (_diagFrames >= BrakeMinFrames && localAngVel.LengthSquared() < 0.001f)
                    {
                        _settleTestIndex++;
                        _settleSubPhase = 0;
                        _diagFrames = 0;
                    }
                    return BrakingTorque(localAngVel, BrakeGain);
                }
            }
        }

        // ══════════════════════════════════════════════════════════
        // Phase 2: Summary
        // ══════════════════════════════════════════════════════════
        if (_diagPhase == 2)
        {
            int passed = 0, failed = 0;
            Log.Default?.Info("[SETTLE] ══════════════════════════════════════════════");
            Log.Default?.Info("[SETTLE] RESULTS");
            Log.Default?.Info("[SETTLE] ──────────────────────────────────────────────");
            foreach (var (name, ok, err, time) in _settleResults)
            {
                string status = ok ? "PASS" : "FAIL";
                Log.Default?.Info($"[SETTLE]  {status}  {name,-16} error={err:F2} deg  time={time:F2}s");
                if (ok) passed++; else failed++;
            }
            Log.Default?.Info("[SETTLE] ──────────────────────────────────────────────");
            Log.Default?.Info($"[SETTLE] {passed}/{passed + failed} PASSED" +
                (failed > 0 ? $"  ({failed} FAILED)" : ""));
            Log.Default?.Info("[SETTLE] ══════════════════════════════════════════════");

            _diagPhase = 4;
            _diagFrames = 0;
        }

        // ══════════════════════════════════════════════════════════
        // Phase 3: Target tracking observation
        // Let the SAS track the reticle target and log everything
        // to see if TargetOrientation is interpreted correctly.
        // ══════════════════════════════════════════════════════════
        if (_diagPhase == 3)
        {
            _diagFrames++;

            if (_diagFrames == 1)
            {
                Log.Default?.Info("[SETTLE] ══════════════════════════════════════════════");
                Log.Default?.Info("[SETTLE] TARGET TRACKING OBSERVATION (10s)");
                Log.Default?.Info("[SETTLE] SAS active, logging target vs actual orientation");
                Log.Default?.Info("[SETTLE] ══════════════════════════════════════════════");
            }

            // Log every second (60 frames)
            if (_diagFrames % 60 == 0 || _diagFrames == 1)
            {
                float errDeg = eulerError.Length() * Rad2Deg;

                // Euler error components in degrees
                float euX = eulerError.X * Rad2Deg;
                float euY = eulerError.Y * Rad2Deg;
                float euZ = eulerError.Z * Rad2Deg;

                // Grid body axes in world frame
                Quaternion gq = wt.Orientation;
                Vector3 bodyFwd = Vector3.Transform(-Vector3.UnitX, gq);   // grid forward
                Vector3 bodyUp = Vector3.Transform(Vector3.UnitY, gq);     // grid up
                Vector3 bodyRight = Vector3.Transform(Vector3.UnitZ, gq);  // grid right

                // Target body axes in world frame
                Quaternion tq = Quaternion.Identity;
                if (Data.TryGet<TargetControlData>(out var tcd))
                {
                    tq = tcd.TargetOrientation;

                    Vector3 targetFwd = Vector3.Transform(-Vector3.UnitX, tq);
                    Vector3 targetUp = Vector3.Transform(Vector3.UnitY, tq);

                    // Angle between grid forward and target forward
                    float fwdDot = Vector3.Dot(bodyFwd, targetFwd);
                    float fwdAngle = MathF.Acos(MathF.Min(1f, MathF.Max(-1f, fwdDot))) * Rad2Deg;

                    // Angle between grid up and target up
                    float upDot = Vector3.Dot(bodyUp, targetUp);
                    float upAngle = MathF.Acos(MathF.Min(1f, MathF.Max(-1f, upDot))) * Rad2Deg;

                    // How level is body-up vs world-up
                    float bodyUpY = bodyUp.Y;  // 1.0 = perfectly level
                    float targetUpY = targetUp.Y;

                    // Velocity direction vs body forward (AoA proxy)
                    Vector3 vel = _lastVelocityLocal;
                    float speed = vel.Length();
                    Vector3 worldVel = Vector3.Transform(vel, gq);
                    float aoaDeg = 0f;
                    if (speed > 1f)
                    {
                        Vector3 velDir = worldVel / speed;
                        float velFwdDot = Vector3.Dot(velDir, bodyFwd);
                        aoaDeg = MathF.Acos(MathF.Min(1f, MathF.Max(-1f, velFwdDot))) * Rad2Deg;
                    }

                    // Cockpit orientation info
                    Quaternion cockpitRel = tcd.RelativeCockpitOrientation;

                    Log.Default?.Info($"[SETTLE] ── t={_diagFrames / 60f:F1}s ──");
                    Log.Default?.Info($"[SETTLE] eulerErr=({euX:F2},{euY:F2},{euZ:F2}) deg  |err|={errDeg:F2} deg");
                    Log.Default?.Info($"[SETTLE] w=({localAngVel.X:F4},{localAngVel.Y:F4},{localAngVel.Z:F4}) |w|={localAngVel.Length():F4}");
                    Log.Default?.Info($"[SETTLE] gridQ=({gq.X:F5},{gq.Y:F5},{gq.Z:F5},{gq.W:F5})");
                    Log.Default?.Info($"[SETTLE] targetQ=({tq.X:F5},{tq.Y:F5},{tq.Z:F5},{tq.W:F5})");
                    Log.Default?.Info($"[SETTLE] cockpitRelQ=({cockpitRel.X:F5},{cockpitRel.Y:F5},{cockpitRel.Z:F5},{cockpitRel.W:F5})");
                    Log.Default?.Info($"[SETTLE] bodyFwd=({bodyFwd.X:F4},{bodyFwd.Y:F4},{bodyFwd.Z:F4})  targetFwd=({targetFwd.X:F4},{targetFwd.Y:F4},{targetFwd.Z:F4})  fwdAngle={fwdAngle:F2} deg");
                    Log.Default?.Info($"[SETTLE] bodyUp=({bodyUp.X:F4},{bodyUp.Y:F4},{bodyUp.Z:F4})  targetUp=({targetUp.X:F4},{targetUp.Y:F4},{targetUp.Z:F4})  upAngle={upAngle:F2} deg");
                    Log.Default?.Info($"[SETTLE] bodyUp.Y={bodyUpY:F4} targetUp.Y={targetUpY:F4}  (1.0=level)");
                    Log.Default?.Info($"[SETTLE] speed={speed:F1} AoA={aoaDeg:F2} deg");

                    // Decompose cockpit relative orientation
                    Vector3 cockpitFwd = Vector3.Transform(-Vector3.UnitX, cockpitRel);
                    Vector3 cockpitUp = Vector3.Transform(Vector3.UnitY, cockpitRel);
                    Log.Default?.Info($"[SETTLE] cockpitFwd(local)=({cockpitFwd.X:F4},{cockpitFwd.Y:F4},{cockpitFwd.Z:F4})  cockpitUp(local)=({cockpitUp.X:F4},{cockpitUp.Y:F4},{cockpitUp.Z:F4})");

                    // What would target be if we accounted for cockpit rotation?
                    Quaternion adjustedTarget = tcd.TargetOrientation * Quaternion.Inverse(cockpitRel);
                    Quaternion adjustedError = Quaternion.Inverse(gq) * adjustedTarget;
                    Vector3 adjEuler = adjustedError.ConvertToEuler();
                    float adjErrDeg = adjEuler.Length() * Rad2Deg;
                    Log.Default?.Info($"[SETTLE] IF adjusted for cockpit: adjEulerErr=({adjEuler.X * Rad2Deg:F2},{adjEuler.Y * Rad2Deg:F2},{adjEuler.Z * Rad2Deg:F2}) deg  |adjErr|={adjErrDeg:F2} deg");
                }
            }

            if (_diagFrames >= 600)
            {
                Log.Default?.Info("[SETTLE] ══════════════════════════════════════════════");
                Log.Default?.Info("[SETTLE] TARGET TRACKING OBSERVATION COMPLETE");
                Log.Default?.Info("[SETTLE] ══════════════════════════════════════════════");
                _diagPhase = 4;
                _diagFrames = 0;
            }

            // Apply normal SAS PD torque while observing
            Vector3 sasDamp2 = -localAngVel * SasDamping;
            Vector3 clampedEuler2 = new Vector3(
                MathF.Max(-MaxEulerCmd, MathF.Min(MaxEulerCmd, eulerError.X)),
                MathF.Max(-MaxEulerCmd, MathF.Min(MaxEulerCmd, eulerError.Y)),
                MathF.Max(-MaxEulerCmd, MathF.Min(MaxEulerCmd, eulerError.Z)));
            Vector3 sasAtt2 = clampedEuler2 * SasAttitude;
            Vector3 sas2 = sasDamp2 + sasAtt2;
            return new Vector3(
                MathF.Max(-SasMaxTorque, MathF.Min(SasMaxTorque, sas2.X)),
                MathF.Max(-SasMaxTorque, MathF.Min(SasMaxTorque, sas2.Y)),
                MathF.Max(-SasMaxTorque, MathF.Min(SasMaxTorque, sas2.Z)));
        }

        // ══════════════════════════════════════════════════════════
        // Phase 4: Programmatic target — level flight along velocity
        // Set ONCE on frame 1: grid -X along horizontal velocity, Y up.
        // Then hold fixed and let SAS settle.
        // ══════════════════════════════════════════════════════════
        if (_diagPhase == 4)
        {
            _diagFrames++;

            // Frame 1: compute target, store it
            if (_diagFrames == 1)
            {
                Quaternion gq = wt.Orientation;
                Vector3 vel = _lastVelocityLocal;
                float speed = vel.Length();
                Vector3 worldVel = Vector3.Transform(vel, gq);

                // "Up" is opposite gravity, not world Y
                Vector3 gravDir = PhysicsHack.GetGravityDirection(Data);
                Vector3 worldUp = -gravDir; // away from planet
                float upLen = worldUp.Length();
                if (upLen > 0.01f)
                    worldUp /= upLen; // normalize — gravity vector is NOT unit length
                else
                    worldUp = Vector3.UnitY; // fallback if no gravity

                // Project velocity onto plane perpendicular to gravity
                Vector3 horizVel = worldVel - Vector3.Dot(worldVel, worldUp) * worldUp;
                float horizSpeed = horizVel.Length();

                Log.Default?.Info("[SETTLE] ══════════════════════════════════════════════");
                Log.Default?.Info("[SETTLE] LEVEL FLIGHT TARGET TEST (15s)");
                Log.Default?.Info("[SETTLE] Setting target: -X along horiz velocity, Y along -gravity");
                Log.Default?.Info("[SETTLE] Re-applied every frame (game overwrites TargetOrientation)");
                Log.Default?.Info($"[SETTLE] worldVel=({worldVel.X:F2},{worldVel.Y:F2},{worldVel.Z:F2}) speed={speed:F1}");
                Log.Default?.Info($"[SETTLE] horizVel=({horizVel.X:F2},{horizVel.Y:F2},{horizVel.Z:F2}) horizSpeed={horizSpeed:F1}");
                Log.Default?.Info($"[SETTLE] gravDir=({gravDir.X:F4},{gravDir.Y:F4},{gravDir.Z:F4}) worldUp=({worldUp.X:F4},{worldUp.Y:F4},{worldUp.Z:F4})");
                Log.Default?.Info("[SETTLE] ══════════════════════════════════════════════");

                _fixedTargetQ = gq; // fallback: current orientation

                if (horizSpeed > 5f)
                {
                    _fixedHeadingDir = horizVel / horizSpeed;  // store heading for per-frame recompute
                    Log.Default?.Info($"[SETTLE] headingDir=({_fixedHeadingDir.X:F4},{_fixedHeadingDir.Y:F4},{_fixedHeadingDir.Z:F4})");
                }
                else
                {
                    // Fallback: use current grid forward projected onto horizon
                    Vector3 bodyFwd = Vector3.Transform(-Vector3.UnitX, gq);
                    Vector3 hFwd = bodyFwd - Vector3.Dot(bodyFwd, worldUp) * worldUp;
                    _fixedHeadingDir = hFwd.LengthSquared() > 0.001f ? Vector3.Normalize(hFwd) : bodyFwd;
                }
            }

            // Recompute target quaternion every frame from stored heading + current gravity
            {
                Vector3 gravDirNow = PhysicsHack.GetGravityDirection(Data);
                Vector3 upNow = -gravDirNow;
                float upNowLen = upNow.Length();
                if (upNowLen > 0.01f)
                    upNow /= upNowLen;
                else
                    upNow = Vector3.UnitY;

                // Re-project heading onto current horizon plane (gravity may have rotated)
                Vector3 fwd = _fixedHeadingDir - Vector3.Dot(_fixedHeadingDir, upNow) * upNow;
                float fwdLen = fwd.Length();
                if (fwdLen > 0.001f)
                {
                    fwd /= fwdLen;
                    Vector3 right = Vector3.Cross(upNow, fwd);
                    float rightLen = right.Length();
                    if (rightLen > 0.001f)
                    {
                        right /= rightLen;
                        Vector3 up = Vector3.Cross(fwd, right);
                        _fixedTargetQ = QuatFromAxes(-fwd, up, right);
                    }
                }
            }

            // Re-apply our target every frame (game's cockpit handler overwrites it)
            if (Data.TryGet<TargetControlData>(out var tcdSet))
            {
                tcdSet.TargetOrientation = _fixedTargetQ;
                Data.Set(tcdSet);
            }

            // Log every second
            if (Data.TryGet<TargetControlData>(out var tcdObs))
            {
                Quaternion gq4 = wt.Orientation;
                Quaternion errQ4 = Quaternion.Inverse(gq4) * tcdObs.TargetOrientation;
                Vector3 eu4 = errQ4.ConvertToEuler();
                float errDeg4 = eu4.Length() * Rad2Deg;

                if (_diagFrames % 60 == 0 || _diagFrames == 1)
                {
                    Vector3 bodyFwd4 = Vector3.Transform(-Vector3.UnitX, gq4);
                    Vector3 bodyUp4 = Vector3.Transform(Vector3.UnitY, gq4);
                    float speed4 = _lastVelocityLocal.Length();
                    Vector3 worldVel4 = Vector3.Transform(_lastVelocityLocal, gq4);
                    float aoa4 = 0f;
                    if (speed4 > 1f)
                    {
                        Vector3 vd = worldVel4 / speed4;
                        aoa4 = MathF.Acos(MathF.Min(1f, MathF.Max(-1f, Vector3.Dot(vd, bodyFwd4)))) * Rad2Deg;
                    }

                    Log.Default?.Info($"[SETTLE] ── t={_diagFrames / 60f:F1}s ──");
                    Log.Default?.Info($"[SETTLE] eulerErr=({eu4.X * Rad2Deg:F2},{eu4.Y * Rad2Deg:F2},{eu4.Z * Rad2Deg:F2}) deg  |err|={errDeg4:F2} deg");
                    Log.Default?.Info($"[SETTLE] w=({localAngVel.X:F4},{localAngVel.Y:F4},{localAngVel.Z:F4}) |w|={localAngVel.Length():F4}");
                    Log.Default?.Info($"[SETTLE] speed={speed4:F1}  bodyFwd=({bodyFwd4.X:F4},{bodyFwd4.Y:F4},{bodyFwd4.Z:F4})  bodyUp.Y={bodyUp4.Y:F4}");
                    Log.Default?.Info($"[SETTLE] AoA={aoa4:F2} deg");
                }

                if (_diagFrames >= 600)
                {
                    Log.Default?.Info("[SETTLE] ══════════════════════════════════════════════");
                    Log.Default?.Info("[SETTLE] LEVEL FLIGHT ESTABLISHED — switching to control surfaces only");
                    Log.Default?.Info("[SETTLE] ══════════════════════════════════════════════");
                    _diagPhase = 5;
                    _diagFrames = 0;
                    _settleTestIndex = 0;
                    _settleSubPhase = 0;
                    _settleHoldFrames = 0;
                }

                // Apply SAS PD torque toward the fixed target
                Vector3 sd = -localAngVel * SasDamping;
                Vector3 ce = new Vector3(
                    MathF.Max(-MaxEulerCmd, MathF.Min(MaxEulerCmd, eu4.X)),
                    MathF.Max(-MaxEulerCmd, MathF.Min(MaxEulerCmd, eu4.Y)),
                    MathF.Max(-MaxEulerCmd, MathF.Min(MaxEulerCmd, eu4.Z)));
                Vector3 sa = ce * SasAttitude;
                Vector3 st = sd + sa;
                return new Vector3(
                    MathF.Max(-SasMaxTorque, MathF.Min(SasMaxTorque, st.X)),
                    MathF.Max(-SasMaxTorque, MathF.Min(SasMaxTorque, st.Y)),
                    MathF.Max(-SasMaxTorque, MathF.Min(SasMaxTorque, st.Z)));
            }

            return Vector3.Zero;
        }

        // ══════════════════════════════════════════════════════════
        // Phase 5: Control surfaces only — kick + settle tests
        // For each test case:
        //   1. Hard-reset orientation to level flight (force quaternion)
        //   2. Apply torque kick to reach target perturbation angle
        //   3. Let control surfaces (no SAS) try to settle back
        //   4. Pass/fail after 8s timeout
        // ══════════════════════════════════════════════════════════
        if (_diagPhase == 5)
        {
            const int CSSettleMaxFrames = 900;    // 15s max settle
            const float CSSettleErrorDeg = 3f;
            const float CSSettleAngSpeedMax = 0.05f;
            const int CSSettleHoldRequired = 60;  // 1s hold
            const float CSTestSpeed = 200f;       // m/s — consistent for all tests

            // Initialize target orientation on first entry (skipped if jumping to phase 5 directly)
            if (_fixedTargetQ.W == 0 && _fixedTargetQ.X == 0 && _fixedTargetQ.Y == 0 && _fixedTargetQ.Z == 0)
            {
                _fixedTargetQ = wt.Orientation;
                if (Data.TryGet<TargetControlData>(out var tcdInit5))
                    _fixedTargetQ = tcdInit5.TargetOrientation != default ? tcdInit5.TargetOrientation : wt.Orientation;
                Log.Default?.Info($"[CS-TEST] Initialized _fixedTargetQ from current orientation");
            }

            // Re-apply our fixed target every frame
            if (Data.TryGet<TargetControlData>(out var tcd5))
            {
                tcd5.TargetOrientation = _fixedTargetQ;
                Data.Set(tcd5);
            }

            _diagFrames++;

            if (_diagFrames == 1 && _settleTestIndex == 0 && _settleSubPhase == 0)
            {
                Log.Default?.Info("[CS-TEST] ══════════════════════════════════════════════");
                Log.Default?.Info("[CS-TEST] CONTROL SURFACES SETTLING TEST");
                Log.Default?.Info($"[CS-TEST] {SettleTests.Length} test cases, SAS torque = ZERO");
                Log.Default?.Info($"[CS-TEST] Pass: error < {CSSettleErrorDeg} deg and w < {CSSettleAngSpeedMax} rad/s for {CSSettleHoldRequired / 60f:F1}s");
                Log.Default?.Info($"[CS-TEST] Timeout: {CSSettleMaxFrames / 60f:F0}s per test");
                Log.Default?.Info("[CS-TEST] ══════════════════════════════════════════════");
                _settleResults.Clear();
            }

            // Done with all tests?
            if (_settleTestIndex >= SettleTests.Length)
            {
                // Print results
                int passed = 0, failed = 0;
                Log.Default?.Info("[CS-TEST] ══════════════════════════════════════════════");
                Log.Default?.Info("[CS-TEST] RESULTS");
                Log.Default?.Info("[CS-TEST] ──────────────────────────────────────────────");
                foreach (var (name, ok, err, time) in _settleResults)
                {
                    string status = ok ? "PASS" : "FAIL";
                    Log.Default?.Info($"[CS-TEST]  {status}  {name,-16} error={err:F2} deg  time={time:F2}s");
                    if (ok) passed++; else failed++;
                }
                Log.Default?.Info("[CS-TEST] ──────────────────────────────────────────────");
                Log.Default?.Info($"[CS-TEST] {passed}/{passed + failed} PASSED" +
                    (failed > 0 ? $"  ({failed} FAILED)" : ""));
                Log.Default?.Info("[CS-TEST] ══════════════════════════════════════════════");

                // Restore gyro and hand controls back to player
                bool restored = PhysicsHack.TryRestoreGyroTorque(Data);
                Log.Default?.Info($"[CS-TEST] Gyro restore: {(restored ? "OK" : "FAILED")}");
                Log.Default?.Info("[CS-TEST] Controls returned to player.");

                _diagPhase = 100;
                _diagFrames = 0;
                return Vector3.Zero;
            }

            var csTest = SettleTests[_settleTestIndex];
            Quaternion gq5 = wt.Orientation;
            Quaternion errQ5 = Quaternion.Inverse(gq5) * _fixedTargetQ;
            Vector3 eu5 = errQ5.ConvertToEuler();
            float errDeg5 = eu5.Length() * Rad2Deg;

            // ── Sub-phase 0: TELEPORT to perturbed orientation with zero angular velocity ──
            if (_settleSubPhase == 0)
            {
                if (_diagFrames == 1)
                {
                    Log.Default?.Info($"[CS-TEST] ── TEST {_settleTestIndex + 1}/{SettleTests.Length}: {csTest.Name} ── teleporting to perturbation...");

                    // Compute perturbed orientation: rotate _fixedTargetQ by test angle about KickDir
                    float perturbRad = csTest.TargetDeg * MathF.PI / 180f;
                    Quaternion perturbQ = Quaternion.CreateFromAxisAngle(csTest.KickDir, perturbRad);
                    Quaternion perturbedOrientation = _fixedTargetQ * perturbQ;

                    bool ok = PhysicsHack.TrySetOrientation(Data, perturbedOrientation);
                    Log.Default?.Info($"[CS-TEST] Teleport to {csTest.TargetDeg:F0} deg perturbation: {(ok ? "OK" : "FAILED")}");
                }

                // Frame 3: set velocity to fixed-speed forward (-X in local → world) + zero angvel
                // Use LEVEL orientation for velocity (not perturbed) — the perturbation is attitude only,
                // the wind should still come from the flight direction
                if (_diagFrames == 3)
                {
                    Vector3 worldFwd = Vector3.Transform(-Vector3.UnitX, _fixedTargetQ);
                    PhysicsHack.TrySetVelocity(Data, worldFwd * CSTestSpeed);
                }

                // Frame 5: set again in case physics recomputed from teleport
                if (_diagFrames == 5)
                {
                    Vector3 worldFwd5 = Vector3.Transform(-Vector3.UnitX, _fixedTargetQ);
                    PhysicsHack.TrySetVelocity(Data, worldFwd5 * CSTestSpeed);
                }

                // Wait for physics to settle after teleport + angvel zero
                if (_diagFrames >= 10)
                {
                    Log.Default?.Info($"[CS-TEST] Perturbation set (err={errDeg5:F1} deg w={localAngVel.Length():F4}). Settling with CS only...");
                    _settleSubPhase = 1;  // move to settle
                    _settleHoldFrames = 0;
                    _diagFrames = 0;
                    return Vector3.Zero;
                }

                return Vector3.Zero;
            }

            // ── Sub-phase 1: SETTLE (CS only, no SAS) ──
            if (_settleSubPhase == 1)
            {
                // Maintain airspeed: apply forward thrust to compensate drag
                // Without this, ship decelerates and CS lose authority
                {
                    float curSpeed = _lastVelocityLocal.Length();
                    if (curSpeed > 10f && curSpeed < CSTestSpeed)
                    {
                        float deficit = CSTestSpeed - curSpeed;
                        // Apply deltaV in world forward direction (level -X)
                        Vector3 worldFwdMaint = Vector3.Transform(-Vector3.UnitX, _fixedTargetQ);
                        PhysicsHack.ApplyDeltaV(Data, worldFwdMaint * deficit * (1f / 60f));
                    }
                }

                // Shadow SAS: compute what SAS would command (without applying)
                Vector3 shadowSasDamp = -localAngVel * SasDamping;
                Vector3 clampedEulerCS = new Vector3(
                    MathF.Max(-MaxEulerCmd, MathF.Min(MaxEulerCmd, eu5.X)),
                    MathF.Max(-MaxEulerCmd, MathF.Min(MaxEulerCmd, eu5.Y)),
                    MathF.Max(-MaxEulerCmd, MathF.Min(MaxEulerCmd, eu5.Z)));
                Vector3 shadowSasAtt = clampedEulerCS * SasAttitude;
                Vector3 shadowSas = shadowSasDamp + shadowSasAtt;
                shadowSas = new Vector3(
                    MathF.Max(-SasMaxTorque, MathF.Min(SasMaxTorque, shadowSas.X)),
                    MathF.Max(-SasMaxTorque, MathF.Min(SasMaxTorque, shadowSas.Y)),
                    MathF.Max(-SasMaxTorque, MathF.Min(SasMaxTorque, shadowSas.Z)));

                if (_diagFrames % 60 == 0 || _diagFrames == 1)
                {
                    // Log CS states
                    int csCount = 0;
                    float totalLift = 0f, totalDrag = 0f;
                    for (int ci = 0; ci < _components.Components.Count; ci++)
                    {
                        if (_components.Components[ci] is ControlSurface csLog)
                        {
                            totalLift += csLog.CurrentLift;
                            totalDrag += csLog.CurrentDrag;
                            Log.Default?.Info($"[CS-TEST] CS#{csCount} defl={csLog.DeflectionInput:F3} AoA={csLog.EffectiveAoA:F1} deg L={csLog.CurrentLift:F0}N D={csLog.CurrentDrag:F0}N");
                            csCount++;
                        }
                    }
                    float speed5 = _lastVelocityLocal.Length();
                    Log.Default?.Info($"[CS-TEST] SETTLE t={_diagFrames / 60f:F1}s error={errDeg5:F2} deg w={localAngVel.Length():F4} spd={speed5:F1} totalL={totalLift:F0}N");
                    Log.Default?.Info($"[CS-TEST] compTorque=({LastComponentTorque.X:F0},{LastComponentTorque.Y:F0},{LastComponentTorque.Z:F0}) compForce=({LastComponentForce.X:F0},{LastComponentForce.Y:F0},{LastComponentForce.Z:F0})");
                    Log.Default?.Info($"[CS-TEST] shadowSAS=({shadowSas.X:F0},{shadowSas.Y:F0},{shadowSas.Z:F0}) sasDamp=({shadowSasDamp.X:F0},{shadowSasDamp.Y:F0},{shadowSasDamp.Z:F0}) sasAtt=({shadowSasAtt.X:F0},{shadowSasAtt.Y:F0},{shadowSasAtt.Z:F0})");
                    Log.Default?.Info($"[CS-TEST] localAngVel=({localAngVel.X:F4},{localAngVel.Y:F4},{localAngVel.Z:F4}) eulerErr=({eu5.X * Rad2Deg:F2},{eu5.Y * Rad2Deg:F2},{eu5.Z * Rad2Deg:F2})");
                }

                bool withinTol = errDeg5 < CSSettleErrorDeg &&
                                 localAngVel.Length() < CSSettleAngSpeedMax;

                if (withinTol)
                {
                    _settleHoldFrames++;
                    if (_settleHoldFrames >= CSSettleHoldRequired)
                    {
                        float settleTime = _diagFrames / 60f;
                        Log.Default?.Info($"[CS-TEST] >> PASS: {csTest.Name} settled in {settleTime:F2}s (error={errDeg5:F2} deg)");
                        _settleResults.Add((csTest.Name, true, errDeg5, settleTime));
                        _settleSubPhase = 0;  // back to reset for next test
                        _settleTestIndex++;
                        _diagFrames = 0;
                        return Vector3.Zero;
                    }
                }
                else
                {
                    _settleHoldFrames = 0;
                }

                if (_diagFrames >= CSSettleMaxFrames)
                {
                    float settleTime = _diagFrames / 60f;
                    Log.Default?.Info($"[CS-TEST] >> FAIL: {csTest.Name} did not settle in {settleTime:F1}s (error={errDeg5:F2} deg w={localAngVel.Length():F4})");
                    _settleResults.Add((csTest.Name, false, errDeg5, settleTime));
                    _settleSubPhase = 0;  // back to reset for next test
                    _settleTestIndex++;
                    _diagFrames = 0;
                    return Vector3.Zero;
                }

                // No SAS torque — control surfaces only
                return Vector3.Zero;
            }

            return Vector3.Zero;
        }

        // ────────────────────────────────────────────────────
        // Phase 6: THRUST-ONLY ATTITUDE TEST
        // Gyros disabled via terminal, SAS drives thrusters only
        // ────────────────────────────────────────────────────
        if (_diagPhase == 6)
        {
            const int ThrustSettleMaxFrames = 900;    // 15s max
            const float ThrustSettleErrorDeg = 5f;
            const float ThrustSettleAngSpeedMax = 0.1f;
            const int ThrustSettleHoldRequired = 60;  // 1s hold
            const int CruiseDelayFrames = 300;        // 5s cruise before tests start

            // Boost to 250 m/s and maintain, then start tests after 5s
            if (!_diagGyrosDisabledViaTerminal && _diagFrames < CruiseDelayFrames)
            {
                _diagFrames++;
                // Compute horizon-forward + 5° up: grid +X projected flat, then pitched up
                Vector3 gridFwd = WorldTransform.TransformDirection(Vector3.UnitX, wt);
                float horizLen = MathF.Sqrt(gridFwd.X * gridFwd.X + gridFwd.Z * gridFwd.Z);
                Vector3 horizFwd = horizLen > 0.01f
                    ? new Vector3(gridFwd.X / horizLen, 0f, gridFwd.Z / horizLen)
                    : new Vector3(1f, 0f, 0f);
                // 15° climb: sin(15°) ≈ 0.259, cos(15°) ≈ 0.966
                Vector3 climbDir = horizFwd * 0.966f + Vector3.UnitY * 0.259f;
                PhysicsHack.TrySetVelocity(Data, climbDir * 500f);
                if (_diagFrames == 1)
                    Log.Default?.Info("[THRUST-TEST] Boosting to 250 m/s horizon-forward, 5s cruise...");
                if (_diagFrames % 60 == 0)
                    Log.Default?.Info($"[THRUST-TEST] Cruise: {_diagFrames / 60}s / 5s at {LastSpeed:F0} m/s");
                return Vector3.Zero;
            }

            // First entry after cruise: save position, initialize target orientation, disable gyros
            if (!_diagGyrosDisabledViaTerminal)
            {
                _diagFrames = 0; // reset for test timing
                _testStartPosition = wt.Position;
                _testStartVelocity = LastLinVel;
                Log.Default?.Info($"[THRUST-TEST] Saved start pos=({wt.Position.X:F0},{wt.Position.Y:F0},{wt.Position.Z:F0}) vel={LastSpeed:F0} m/s");
                // Capture current orientation as target (level flight)
                _fixedTargetQ = wt.Orientation;
                if (Data.TryGet<TargetControlData>(out var tcdInit))
                    _fixedTargetQ = tcdInit.TargetOrientation;

                if (_gyroCache.Count > 0)
                {
                    OffsetThrustJob.SetGyrosEnabled(_gyroCache, false);
                    Log.Default?.Info($"[THRUST-TEST] Gyros disabled via terminal ({_gyroCache.Count} gyros)");
                }
                else
                {
                    Log.Default?.Info("[THRUST-TEST] WARNING: No gyros cached — rebuild may be pending");
                }

                // Also zero game gyro torque as backup
                PhysicsHack.TryZeroGyroTorque(Data);
                _diagGyrosDisabledViaTerminal = true;
            }

            // Re-apply target orientation
            if (Data.TryGet<TargetControlData>(out var tcd6))
            {
                tcd6.TargetOrientation = _fixedTargetQ;
                Data.Set(tcd6);
            }

            // Keep forward thrusters firing at full power during tests
            for (int t = 0; t < _thrusterCache.Count; t++)
            {
                var ti = _thrusterCache[t];
                // Forward thrusters push +X
                if (ti.ThrustDirection.X > 0.9f)
                    OffsetThrustJob.ForceOverride(ti.ThrusterComponent, 1.0f);
            }

            _diagFrames++;

            if (_diagFrames == 1 && _settleTestIndex == 0 && _settleSubPhase == 0)
            {
                Log.Default?.Info("[THRUST-TEST] ══════════════════════════════════════════════");
                Log.Default?.Info("[THRUST-TEST] THRUST-ONLY ATTITUDE CONTROL TEST");
                Log.Default?.Info($"[THRUST-TEST] {SettleTests.Length} tests, gyros OFF, SAS → thrusters");
                Log.Default?.Info($"[THRUST-TEST] Pass: error < {ThrustSettleErrorDeg} deg and w < {ThrustSettleAngSpeedMax} rad/s for {ThrustSettleHoldRequired / 60f:F1}s");
                Log.Default?.Info($"[THRUST-TEST] Timeout: {ThrustSettleMaxFrames / 60f:F0}s per test");
                Log.Default?.Info("[THRUST-TEST] ══════════════════════════════════════════════");
                _settleResults.Clear();
            }

            // Done with all tests?
            if (_settleTestIndex >= SettleTests.Length)
            {
                int passed6 = 0, failed6 = 0;
                Log.Default?.Info("[THRUST-TEST] ══════════════════════════════════════════════");
                Log.Default?.Info("[THRUST-TEST] RESULTS");
                Log.Default?.Info("[THRUST-TEST] ──────────────────────────────────────────────");
                foreach (var (name, ok, err, time) in _settleResults)
                {
                    string status = ok ? "PASS" : "FAIL";
                    Log.Default?.Info($"[THRUST-TEST]  {status}  {name,-16} error={err:F2} deg  time={time:F2}s");
                    if (ok) passed6++; else failed6++;
                }
                Log.Default?.Info("[THRUST-TEST] ──────────────────────────────────────────────");
                Log.Default?.Info($"[THRUST-TEST] {passed6}/{passed6 + failed6} PASSED" +
                    (failed6 > 0 ? $"  ({failed6} FAILED)" : ""));
                Log.Default?.Info("[THRUST-TEST] ══════════════════════════════════════════════");

                // Re-enable gyros
                if (_diagGyrosDisabledViaTerminal && _gyroCache.Count > 0)
                {
                    OffsetThrustJob.SetGyrosEnabled(_gyroCache, true);
                    _diagGyrosDisabledViaTerminal = false;
                    Log.Default?.Info("[THRUST-TEST] Gyros re-enabled");
                }
                bool restored6 = PhysicsHack.TryRestoreGyroTorque(Data);
                Log.Default?.Info($"[THRUST-TEST] Gyro torque restore: {(restored6 ? "OK" : "FAILED")}");

                _diagPhase = 100;
                _diagFrames = 0;
                return Vector3.Zero;
            }

            var thrTest = SettleTests[_settleTestIndex];
            Quaternion gq6 = wt.Orientation;
            Quaternion errQ6 = Quaternion.Inverse(gq6) * _fixedTargetQ;
            Vector3 eu6 = errQ6.ConvertToEuler();
            float errDeg6 = eu6.Length() * Rad2Deg;

            // Sub-phase 0: teleport to perturbed orientation
            if (_settleSubPhase == 0)
            {
                if (_diagFrames == 1)
                {
                    Log.Default?.Info($"[THRUST-TEST] ── TEST {_settleTestIndex + 1}/{SettleTests.Length}: {thrTest.Name} ── teleporting...");
                    // Restore position and velocity to start point
                    PhysicsHack.TrySetPosition(Data, _testStartPosition);
                    PhysicsHack.TrySetVelocity(Data, _testStartVelocity);
                    // Apply perturbed orientation
                    float perturbRad6 = thrTest.TargetDeg * MathF.PI / 180f;
                    Quaternion perturbQ6 = Quaternion.CreateFromAxisAngle(thrTest.KickDir, perturbRad6);
                    Quaternion perturbedQ6 = _fixedTargetQ * perturbQ6;
                    PhysicsHack.TrySetOrientation(Data, perturbedQ6);
                    PhysicsHack.TryZeroAngularVelocity(Data);
                }
                // Wait 2 frames for physics to process
                if (_diagFrames >= 3)
                {
                    _settleSubPhase = 1;
                    _diagFrames = 0;
                    _settleHoldFrames = 0;
                }
                return Vector3.Zero;
            }

            // Sub-phase 1: settle via thrust-only (SAS torque → thrusters, no gyros)
            if (_settleSubPhase == 1)
            {
                if (_diagFrames % 60 == 0)
                {
                    var sas = ComputeThrustSas(localAngVel, eu6);
                    Log.Default?.Info($"[THRUST-TEST] t={_diagFrames / 60f:F1}s err={errDeg6:F2} deg w={localAngVel.Length():F4} " +
                        $"SAS=({sas.X:F0},{sas.Y:F0},{sas.Z:F0})");
                }

                // Check convergence
                if (errDeg6 < ThrustSettleErrorDeg && localAngVel.Length() < ThrustSettleAngSpeedMax)
                {
                    _settleHoldFrames++;
                    if (_settleHoldFrames >= ThrustSettleHoldRequired)
                    {
                        float settleTime6 = _diagFrames / 60f;
                        Log.Default?.Info($"[THRUST-TEST] >> PASS: {thrTest.Name} settled in {settleTime6:F2}s (error={errDeg6:F2} deg)");
                        _settleResults.Add((thrTest.Name, true, errDeg6, settleTime6));
                        _settleSubPhase = 0;
                        _settleTestIndex++;
                        _diagFrames = 0;
                        // Return full SAS torque — this drives the thrusters via attitudeTorqueLocal
                        return ComputeThrustSas(localAngVel, eu6);
                    }
                }
                else
                {
                    _settleHoldFrames = 0;
                }

                // Timeout
                if (_diagFrames >= ThrustSettleMaxFrames)
                {
                    float settleTime6 = _diagFrames / 60f;
                    Log.Default?.Info($"[THRUST-TEST] >> FAIL: {thrTest.Name} did not settle in {settleTime6:F1}s (error={errDeg6:F2} deg)");
                    _settleResults.Add((thrTest.Name, false, errDeg6, settleTime6));
                    _settleSubPhase = 0;
                    _settleTestIndex++;
                    _diagFrames = 0;
                    return Vector3.Zero;
                }

                // Drive SAS normally — the torque feeds into thrusters via attitudeTorqueLocal
                return ComputeThrustSas(localAngVel, eu6);
            }

            return Vector3.Zero;
        }

        // Phase 100+: done, normal SAS resumes
        return Vector3.Zero;
    }

    /// <summary>SAS PD controller for thrust-only attitude control (no gyros).
    /// Gains scale with ship inertia so behavior is consistent across ship sizes.
    /// Target: angular acceleration ~3 rad/s² at max euler error.</summary>
    private Vector3 ComputeThrustSas(Vector3 localAngVel, Vector3 eulerError)
    {
        // Estimate MOI from block count.
        // Calibrated: 14K-block jet needs ~167M kg·m² inertia estimate.
        // Formula: MOI ≈ blockCount^1.5 * k, where k tuned to match.
        // 14784^1.5 ≈ 1.8M, k ≈ 90 → 162M ✓
        int blockCount = _octree?.GetAllCubeBlocks().Length ?? 1000;
        float inertiaEst = MathF.Pow((float)blockCount, 1.5f) * 90f;
        if (inertiaEst < 100_000f) inertiaEst = 100_000f;

        // PD gains: torque = inertia * (Kp * error + Kd * rate)
        // Kp=4 → 4 rad/s² at 1 rad error, Kd=4 → slight overdamp for stability
        float damping = 4f * inertiaEst;
        float attitude = 4f * inertiaEst;
        float maxTorque = 15f * inertiaEst;
        const float MaxEuler = 0.5f;

        Vector3 damp = -localAngVel * damping;
        Vector3 clampedEuler = new Vector3(
            Math.Clamp(eulerError.X, -MaxEuler, MaxEuler),
            Math.Clamp(eulerError.Y, -MaxEuler, MaxEuler),
            Math.Clamp(eulerError.Z, -MaxEuler, MaxEuler));
        Vector3 att = clampedEuler * attitude;
        Vector3 total = damp + att;
        return new Vector3(
            Math.Clamp(total.X, -maxTorque, maxTorque),
            Math.Clamp(total.Y, -maxTorque, maxTorque),
            Math.Clamp(total.Z, -maxTorque, maxTorque));
    }

    private static Quaternion QuatFromAxes(Vector3 xAxis, Vector3 yAxis, Vector3 zAxis)
    {
        // Build quaternion from rotation matrix columns (X, Y, Z axes in world)
        float m00 = xAxis.X, m01 = yAxis.X, m02 = zAxis.X;
        float m10 = xAxis.Y, m11 = yAxis.Y, m12 = zAxis.Y;
        float m20 = xAxis.Z, m21 = yAxis.Z, m22 = zAxis.Z;
        float trace = m00 + m11 + m22;

        Quaternion q;
        if (trace > 0f)
        {
            float s = MathF.Sqrt(trace + 1f) * 2f;
            q = new Quaternion((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s);
        }
        else if (m00 > m11 && m00 > m22)
        {
            float s = MathF.Sqrt(1f + m00 - m11 - m22) * 2f;
            q = new Quaternion(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
        }
        else if (m11 > m22)
        {
            float s = MathF.Sqrt(1f + m11 - m00 - m22) * 2f;
            q = new Quaternion((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s);
        }
        else
        {
            float s = MathF.Sqrt(1f + m22 - m00 - m11) * 2f;
            q = new Quaternion((m02 + m20) / s, (m12 + m21) / s, 0.25f * s, (m10 - m01) / s);
        }
        return Quaternion.Normalize(q);
    }

    private static Vector3 WorldAngVelFromLocal(Vector3 localAngVel, WorldTransform wt)
        => Vector3.Transform(localAngVel, wt.Orientation);

    private static Vector3 BrakingTorque(Vector3 localAngVel, float gain)
    {
        Vector3 t = -localAngVel * gain;
        const float maxBrake = 10_000_000f;
        return new Vector3(
            MathF.Max(-maxBrake, MathF.Min(maxBrake, t.X)),
            MathF.Max(-maxBrake, MathF.Min(maxBrake, t.Y)),
            MathF.Max(-maxBrake, MathF.Min(maxBrake, t.Z)));
    }

    private static string DominantAxis(Vector3 v)
    {
        float ax = MathF.Abs(v.X), ay = MathF.Abs(v.Y), az = MathF.Abs(v.Z);
        if (ax > ay && ax > az) return v.X > 0 ? "+X" : "-X";
        if (ay > ax && ay > az) return v.Y > 0 ? "+Y" : "-Y";
        return v.Z > 0 ? "+Z" : "-Z";
    }

    // ── Helpers ──

    /// <summary>
    /// Map player angular control input to control surface deflections.
    /// Each surface's effectiveness axis = cross(posFromCoM, hingeAxis).normalized
    /// <summary>
    /// <summary>
    /// Attitude hold: when no pilot is present, hold the current orientation.
    /// Runs every frame before early returns so it works even without atmosphere.
    /// Sets _lastGridAngVel and SasTorque when no pilot input is detected.
    /// </summary>
    private void UpdateAttitudeHold(WorldTransform wt, Vector3 angularVelocity)
    {
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

        const float Kp = 5.0f;
        const float Kd = 0.5f;
        const float maxCmd = 5.0f;

        Vector3 attitudeCmd = eulerError * Kp;
        attitudeCmd = new Vector3(
            MathF.Max(-maxCmd, MathF.Min(maxCmd, attitudeCmd.X)),
            MathF.Max(-maxCmd, MathF.Min(maxCmd, attitudeCmd.Y)),
            MathF.Max(-maxCmd, MathF.Min(maxCmd, attitudeCmd.Z)));

        _lastGridAngVel = attitudeCmd - localAngVel * Kd;

        if (_simFrameCount % 120 == 0)
            Log.Default?.Info($"[AERO-HOLD] euler=({eulerError.X:F4},{eulerError.Y:F4},{eulerError.Z:F4})" +
                $" angVel=({localAngVel.X:F4},{localAngVel.Y:F4},{localAngVel.Z:F4})" +
                $" cmd=({_lastGridAngVel.X:F4},{_lastGridAngVel.Y:F4},{_lastGridAngVel.Z:F4})");

        // SAS torque for the hidden stability system
        if (!DiagActive)
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

    /// DeflectionInput = dot(effectivenessAxis, targetAngularVelocity), clamped [-1,+1].
    /// </summary>
    private int _csLogCooldown;
    private int _csLogOnceCountdown = 300; // log once after 5s regardless of input

    private void UpdateControlSurfaceInputs(Vector3 centerOfMass, WorldTransform wt,
        Vector3 localAngVel, float dynamicPressure, Vector3 bodyTorque)
    {
        // ══════════════════════════════════════════════════════════════════════
        // SAS: runs regardless of control surface count (Bug 1 fix)
        // ══════════════════════════════════════════════════════════════════════
        if (_diagPhase == 5) SasTorque = Vector3.Zero;

        bool hasTargetDataEarly = Data.TryGet<TargetControlData>(out var targetDataEarly);
        bool hasAngularDataEarly = Data.TryGet<AngularControlData>(out var angularDataEarly);
        if (hasTargetDataEarly)
        {
            // Run self-test on first frame
            RunSelfTest(wt, localAngVel);

            // Compute orientation error
            Quaternion errorQuat = Quaternion.Inverse(wt.Orientation) * targetDataEarly.TargetOrientation;
            Vector3 eulerErr = errorQuat.ConvertToEuler();

            // Run torque probe diagnostic (applies real torques, logs results)
            SasTorque = ComputeDiagSasTorque(localAngVel, eulerErr,
                hasAngularDataEarly ? angularDataEarly.TargetAngularVelocity : Vector3.Zero, wt);
        }

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
            // Skip if torque probe diagnostic is still running
            if (!DiagActive)
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

            if (_diagPhase == 5) SasTorque = Vector3.Zero; // no SAS in keyboard mode

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
            if (_diagPhase == 5) SasTorque = Vector3.Zero;
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
        if (_staggeredBuildActive)
        {
            // Restart: topology changed mid-build
            _buildSurface.AbortBuild();
        }

        _gridAccessor.SetOctree(_octree);
        _blockSize = DetectBlockSize();
        _buildSurface.BeginBuild(_gridAccessor, _blockSize);
        _staggeredBuildActive = true;
        _rebuildRestartNeeded = false;
        _fullRebuildNeeded = false;
        _pendingAddedCells.Clear();
        _pendingRemovedCells.Clear();
    }

    /// <summary>
    /// Called by the scheduler each tick with this grid's share of the global cell budget.
    /// Returns true when the build is complete.
    /// </summary>
    internal bool TickStaggeredBuild(int cellBudget)
    {
        if (!_staggeredBuildActive) return true;

        // If topology changed mid-build, restart
        if (_rebuildRestartNeeded)
        {
            _buildSurface.AbortBuild();
            _gridAccessor.SetOctree(_octree);
            _blockSize = DetectBlockSize();
            _buildSurface.BeginBuild(_gridAccessor, _blockSize);
            _rebuildRestartNeeded = false;
            _pendingAddedCells.Clear();
            _pendingRemovedCells.Clear();
        }

        return _buildSurface.AddCellBatch(cellBudget);
    }

    /// <summary>
    /// Called by the scheduler when TickStaggeredBuild returns true.
    /// Swaps buffers and runs wing detection.
    /// </summary>
    internal void FinalizeStaggeredBuild()
    {
        _buildSurface.FinalizeBuild();

        // Swap: buildSurface becomes active, old active becomes next build buffer
        (_surface, _buildSurface) = (_buildSurface, _surface);
        _staggeredBuildActive = false;

        // Update detector's surface reference after swap
        if (_model.Detector is ConnectedComponentWingDetector ccwd2)
            ccwd2.ManifoldSurface = _surface;

        _model.InvalidateWings();
        _model.DetectWings(_gridAccessor, _surface, _blockSize);
        _manifold.Classify(_surface);

        // Rebuild all block-level aero components from scratch
        _components.Clear();
        _factory.CreateAll(_octree, _blockSize, _components);
        _faceOverridesDirty = true;
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
