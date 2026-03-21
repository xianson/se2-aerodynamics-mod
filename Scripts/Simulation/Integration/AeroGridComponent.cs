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

    // ── Face override index (for Cp heatmap) ──
    private bool _faceOverridesDirty = true;

    // ── Cascaded flight controller state ──
    private Vector3 _rateIntegral = Vector3.Zero;  // inner loop integrator (per-axis)

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

    /// <summary>Block-level aero component registry.</summary>
    public AeroComponentRegistry Components => _components;

    // ── Lifecycle ──

    void IInSceneListener.OnAddedToScene()
    {
        Log.Default?.Info("[AERO] AeroGridComponent.OnAddedToScene()");

        _gridAccessor = new Se2GridAccessor(_octree);
        _surface = new SmoothSurfaceProvider();
        _buildSurface = new SmoothSurfaceProvider();

        var innerDrag = new DampedShadowedDragModel();
        _model = new LiftingSurfaceModel(innerDrag, liftModel: new CompressibleWingModel());
        _components = new AeroComponentRegistry();
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
    }

    // ── Compute (called from debug draw for now) ──

    internal void TryCompute(WorldTransform wt, float density, Vector3 linearVelocity, Vector3 angularVelocity, Vector3 centerOfMass)
    {
        HasResult = false;

        if (!_initialized) return;

        // ── Global scheduler tick (first grid each frame drives all rebuilds) ──
        AeroScheduler.EnsureTicked();

        if (density < AeroConfig.MinDensity) return;

        float speed = linearVelocity.Length();
        if (speed < AeroConfig.MinSpeed) return;

        // ── Handle dirty state: enqueue full rebuild or do incremental update ──
        if (_dirty && !_staggeredBuildActive)
        {
            _gridAccessor.SetOctree(_octree);
            _blockSize = DetectBlockSize();

            if (_fullRebuildNeeded || _surface.FaceCount == 0)
            {
                // Enqueue for scheduler-managed staggered rebuild
                AeroScheduler.EnqueueRebuild(this);
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
                _faceOverridesDirty = true;

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
        else if (_dirty && _staggeredBuildActive)
        {
            // Topology changed mid-build — flag for restart on next scheduler tick
            _rebuildRestartNeeded = true;
            _dirty = false;
            _pendingAddedCells.Clear();
            _pendingRemovedCells.Clear();
        }

        // ── Deferred full wing detection (correctness pass with ray-march) ──
        if (_wingsDirty && !_staggeredBuildActive)
        {
            if (--_wingCooldownTicks <= 0)
            {
                _model.InvalidateWings();
                _model.DetectWings(_gridAccessor, _surface, _blockSize);
                _faceOverridesDirty = true;
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

        // ── Control surface input from player ──
        float q = (float)(0.5 * atmo.Density * speed * speed);
        UpdateControlSurfaceInputs(centerOfMass, wt, localAngVel, q, LastResult.Torque);

        // ── Block component forces ──
        if (_components.Count > 0)
        {
            var (compForce, compTorque) = _components.EvaluateAll(ctx);
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

        // ── Override FaceCp for wing + component faces ──
        if (_faceOverridesDirty && _surface != null)
        {
            _model.BuildFaceOverrideIndex(_surface, _components.Components);
            _faceOverridesDirty = false;
        }
        if (_model.InnerModel is DampedShadowedDragModel dsmCp)
            _model.OverrideFaceCp(dsmCp, _components.Components);

        HasResult = true;
    }

    // ── Helpers ──

    /// <summary>
    /// Map player angular control input to control surface deflections.
    /// Each surface's effectiveness axis = cross(posFromCoM, hingeAxis).normalized
    /// DeflectionInput = dot(effectivenessAxis, targetAngularVelocity), clamped [-1,+1].
    /// </summary>
    private int _csLogCooldown;
    private int _csLogOnceCountdown = 300; // log once after 5s regardless of input

    private void UpdateControlSurfaceInputs(Vector3 centerOfMass, WorldTransform wt,
        Vector3 localAngVel, float dynamicPressure, Vector3 bodyTorque)
    {
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

            // ── OUTER LOOP: Attitude → Desired angular rate ──
            // Orientation error in local frame (same as GridGyroscopesComponent.ComputeTorqueTarget)
            Quaternion errorQuat = Quaternion.Inverse(gridOrientation) * targetData.TargetOrientation;
            Vector3 eulerError = -errorQuat.ConvertToEuler(); // radians, local frame

            // Desired angular rate proportional to attitude error, clamped.
            //
            // Axis mapping: keyboard/euler convention is (Pitch, Yaw, Roll) = (X, Y, Z)
            // but the effectiveness space is (Roll, Yaw, Pitch) = (X, Y, Z).
            // Ailerons are differential on X, elevators collective on Z, rudder on Y.
            //
            // So: euler.X (pitch error) → desiredRate.Z (elevator axis)
            //     euler.Y (yaw error)   → desiredRate.Y (rudder axis)
            //     keyboard roll (TAV.Z) → desiredRate.X (aileron axis)
            const float Kouter = 3.0f;    // rad/s per rad of error
            const float maxRate = 1.5f;    // max desired angular rate (rad/s)

            // Roll: direct rate command from AngularControlData (keyboard Q/E)
            // Maps to X axis where ailerons respond differentially
            float rollDesired = 0f;
            if (hasAngularData)
            {
                float rollInput = MathF.Max(-1f, MathF.Min(1f, angularData.TargetAngularVelocity.Z));
                rollDesired = rollInput * maxRate;
            }

            Vector3 desiredRate = new Vector3(
                rollDesired,                                                      // X = roll (ailerons)
                MathF.Max(-maxRate, MathF.Min(maxRate, eulerError.Y * Kouter)),   // Y = yaw (rudder)
                MathF.Max(-maxRate, MathF.Min(maxRate, eulerError.X * Kouter)));  // Z = pitch (elevators)

            desiredRate.Y *= targetData.PerAxisDampeningMultiplier.Y;

            // ── INNER LOOP: Rate error → deflection command ──
            // P controller on angular rate error
            Vector3 rateError = desiredRate - localAngVel;

            const float Kp_rate = 10.0f;  // proportional on rate error (high to catch instabilities early)

            // targetAngVel here is the deflection command signal (not a velocity)
            // It will be projected onto each surface's effectiveness axis in STEP 5
            targetAngVel = rateError * Kp_rate;

            // ── FEEDFORWARD: counter body aero torque ──
            // bodyTorque is in local frame from the body aero model (drag, lift on hull).
            // Negate it so surfaces preemptively oppose destabilizing moments.
            // Applied AFTER geometric normalization in STEP 4b, so we add it there instead.
            // (stored for use in STEP 4b)

            if (shouldLog)
            {
                Log.Default?.Info($"[AERO-CS] STEP3 RETICLE eulerError=({eulerError.X:F5},{eulerError.Y:F5},{eulerError.Z:F5}) rad");
                Log.Default?.Info($"[AERO-CS] STEP3 RETICLE desiredRate=({desiredRate.X:F5},{desiredRate.Y:F5},{desiredRate.Z:F5})");
                Log.Default?.Info($"[AERO-CS] STEP3 RETICLE localAngVel=({localAngVel.X:F5},{localAngVel.Y:F5},{localAngVel.Z:F5})");
                Log.Default?.Info($"[AERO-CS] STEP3 RETICLE rateError=({rateError.X:F5},{rateError.Y:F5},{rateError.Z:F5})");
                Log.Default?.Info($"[AERO-CS] STEP3 RETICLE bodyTorque=({bodyTorque.X:F1},{bodyTorque.Y:F1},{bodyTorque.Z:F1})");
                Log.Default?.Info($"[AERO-CS] STEP3 RETICLE command=({targetAngVel.X:F5},{targetAngVel.Y:F5},{targetAngVel.Z:F5})");
            }
        }
        else if (hasAngularData)
        {
            inputMode = "KEYBOARD";

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
                Vector3 eff = Vector3.Cross(ld, r);
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

        // Normalize reticle command by geometric authority
        // After this, command=1 means "full deflection on this axis"
        if (inputMode == "RETICLE")
        {
            targetAngVel = new Vector3(
                totalAuthority.X > 0.01f ? targetAngVel.X / totalAuthority.X : targetAngVel.X,
                totalAuthority.Y > 0.01f ? targetAngVel.Y / totalAuthority.Y : targetAngVel.Y,
                totalAuthority.Z > 0.01f ? targetAngVel.Z / totalAuthority.Z : targetAngVel.Z);

            // Feedforward: oppose body aero torque, scaled by surface torque authority
            // Result is in deflection units: bodyTorque / torqueAuthority = fraction of max deflection needed
            const float Kff = 1.0f;  // 0-1: how much of the body torque to counter (1 = full cancel)
            Vector3 ffTrim = new Vector3(
                torqueAuthority.X > 1f ? -bodyTorque.X / torqueAuthority.X * Kff : 0f,
                torqueAuthority.Y > 1f ? -bodyTorque.Y / torqueAuthority.Y * Kff : 0f,
                torqueAuthority.Z > 1f ? -bodyTorque.Z / torqueAuthority.Z * Kff : 0f);
            targetAngVel += ffTrim;

            if (shouldLog)
            {
                Log.Default?.Info($"[AERO-CS] STEP4b geoAuthority=({totalAuthority.X:F3},{totalAuthority.Y:F3},{totalAuthority.Z:F3})");
                Log.Default?.Info($"[AERO-CS] STEP4b torqueAuth=({torqueAuthority.X:F0},{torqueAuthority.Y:F0},{torqueAuthority.Z:F0})");
                Log.Default?.Info($"[AERO-CS] STEP4b ffTrim=({ffTrim.X:F5},{ffTrim.Y:F5},{ffTrim.Z:F5})");
                Log.Default?.Info($"[AERO-CS] STEP4b finalCmd=({targetAngVel.X:F5},{targetAngVel.Y:F5},{targetAngVel.Z:F5})");
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // STEP 5: Per-surface loop
        // ══════════════════════════════════════════════════════════════════════
        for (int i = 0; i < _components.Components.Count; i++)
        {
            if (_components.Components[i] is Airbrake ab)
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
                //     Positive deflection rotates chord about hinge (Rodrigues), tilting
                //     surfNormal AWAY from liftDir → AoA and lift DECREASE.
                //     Torque from decreased lift = cross(r, -ΔF) = -cross(r, liftDir).
                //     So effectiveness = cross(liftDir, r): positive input → torque in this direction.
                Vector3 effectiveness = Vector3.Cross(liftDir, r);
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
                    // Reticle mode: cascaded controller already computed the
                    // deflection command in STEP 3 (inner PI loop on rate error).
                    // Just project onto this surface's effectiveness axis.
                    command = Vector3.Dot(effNorm, targetAngVel);
                    damping = 0f;
                    scaledInput = command;
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

        _model.InvalidateWings();
        _model.DetectWings(_gridAccessor, _surface, _blockSize);

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
