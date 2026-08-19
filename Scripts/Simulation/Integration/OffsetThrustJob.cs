#pragma warning disable
using System;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.BlockOctrees;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement;

namespace AeroMod;

/// <summary>
/// Cached info about a thruster on the grid.
/// </summary>
public struct ThrusterInfo
{
    public Entity ThrusterEntity;
    public Component ThrusterComponent; // ThrusterComponent instance (for reading/writing override)
    public Vector3 GridLocalPosition;  // block center in grid-local space (meters) — physics formula
    public Vector3 DrawPosition;       // block center for debug draw (cell-center formula, matches aero components)
    public Vector3 ThrustDirection;    // unit vector in grid-local space (direction force pushes the ship)
    public float MaxPower;             // Newtons (impulse per frame = MaxPower / 60)
    public ThrustProfile Profile;      // Mach-dependent thrust scaling (null = rocket/flat)
    public Vector3 IntakeDirection;    // unit vector pointing into intake (opposite ThrustDirection for air-breathers)
    public float AttitudeFraction;     // 0..1: fraction of capacity reserved for attitude (from terminal slider)
    public AeroThrustSettingsComponent Settings; // live reference for reading slider changes
}

/// <summary>
/// Cached info about a gyroscope on the grid.
/// </summary>
public struct GyroInfo
{
    public Entity GyroEntity;
    public Component BlockComponent;   // PowerableBlockComponent (for Enabled toggle)
    public float MaxTorque;            // N·m
}

/// <summary>
/// Per-thruster debug snapshot, written each physics frame for debug draw.
/// </summary>
public struct ThrusterDebugState
{
    public float VanillaThrust;     // N (before profile scaling)
    public float ScaledThrust;      // N (after Mach profile)
    public float AttitudeOverride;  // 0..1 fraction commanded for attitude
    public float DTermComponent;    // D-term contribution (rate nulling)
    public float PTermComponent;    // P-term contribution (SAS attitude)
    public Vector3 TorqueAxis;      // normalized torque axis (r × dir / |r × dir|)
    public float TorqueArm;         // |r × dir| in meters
    public bool IsActive;           // thruster fired this frame
}

/// <summary>
/// Offset thrust torque: vanilla applies all thrust at CoM (no torque).
/// We add the coupling torque (r × F) that would exist if thrust acted
/// at the thruster's block position. Pure angular impulse — vanilla's
/// linear thrust is left untouched. Mach scaling delta applied separately.
/// </summary>
public static class OffsetThrustJob
{
    private const float DT = 1f / 60f;
    private const float AngularDampeningThreshold = 0.01f;  // Bob's RotationDampeningAggresiveness
    private const float LinearDampeningThreshold = 0.001f; // Bob's MovementDampeningAggresiveness

    private static int _executeLogCooldown;

    // ── Per-thruster trace logging ──
    private static int _traceGridHash;
    private static int _traceIndex = -1;
    private static int _traceFrame;
    private static int _dampDiagCooldown;
    private static int _traceLockedCount = int.MaxValue;
    private static int _traceSkipCount = 0; // skip first N grids before locking
    private static bool _traceActive;
    // Net torque accumulators (written by Execute offset loop, read for NET log)
    /// <summary>Total coupling torque from offset loop (all thrusters, current frame).</summary>
    public static Vector3 NetOffsetCouplingTorque { get; private set; }
    private static Vector3 _traceNetOffsetTorque;
    /// <summary>Coupling torque from gravity-hover-only thrust (constant bias).
    /// Apply phantom -HoverCouplingTorque to cancel it while preserving attitude differential.</summary>
    public static Vector3 HoverCouplingTorque { get; private set; }
    private static Vector3 _traceNetAttitudeTorque;
    private static Vector3 _netOverrideThrust; // accumulated thrust from damping/attitude overrides (local frame, Newtons)

    // (Per-thruster blending manages its own overrides — no tracking needed)

    /// <summary>Per-grid debug states, keyed by thruster list reference. Written by Execute, read by draw.</summary>
    public static readonly Dictionary<List<ThrusterInfo>, List<ThrusterDebugState>> DebugStatesByGrid = new();
    private static int _debugLogCooldown;


    /// <summary>
    /// Run offset thrust correction for one grid.
    /// Call once per physics frame AFTER vanilla thrust has been applied.
    ///
    /// Mach-dependent thrust scaling: for air-breathing profiles, the vanilla
    /// thrust is treated as the "rated" (static) value. We compute the profile
    /// scale factor, then apply a delta impulse = (scaled - vanilla) so the net
    /// thrust matches the profile curve. Rockets (null profile) pass through 1:1.
    /// </summary>
    public static void Execute(
        List<ThrusterInfo> thrusters,
        DEntityContext gridData,
        Entity gridEntity,
        WorldTransform gridWt,
        Vector3 angularVelocity,
        bool enableDampening,
        float mach = 0f,
        Vector3 velocityLocalHat = default,
        Vector3 velocityLocal = default,
        Vector3 gravityLocal = default,
        float mass = 0f,
        Vector3 targetAngVel = default,
        Vector3 aeroTorqueLocal = default,
        bool skipOffsetLoop = false)
    {
        if (thrusters.Count == 0) return;
        if (!PhysicsHack.ThrusterAccessAvailable) return;

        // Read raw player input from grid entity (WASD → Movement, mouse → Rotation)
        Vector3 playerMovement = Vector3.Zero;
        Vector3 playerRotation = Vector3.Zero;
        if (gridData.TryGet<ControlData>(out var controlData))
        {
            playerMovement = controlData.Movement;
            playerRotation = controlData.Rotation;
        }
        _executeLogCooldown = Math.Max(0, _executeLogCooldown - 1);

        // Auto-lock trace: skip first grid, lock onto second active grid
        int gridHash = thrusters.GetHashCode();
        _traceActive = (_traceGridHash == gridHash);
        if (!_traceActive && thrusters.Count >= 40 && _traceIndex < 0)
        {
            // Only lock if at least one thruster is actually firing
            bool hasActive = false;
            for (int j = 0; j < thrusters.Count; j++)
            {
                if (PhysicsHack.GetThrustOverride(thrusters[j].ThrusterEntity.Data) > 0f ||
                    IsComponentThrusting(thrusters[j].ThrusterComponent))
                { hasActive = true; break; }
            }
            if (hasActive)
            {
                if (_traceSkipCount > 0)
                {
                    _traceSkipCount--;
                    Log.Default?.Info($"[AERO-TRACE] SKIPPED grid={gridHash:X8} count={thrusters.Count} (skipsLeft={_traceSkipCount})");
                }
                else
                {
                    _traceGridHash = gridHash;
                    _traceIndex = 0;
                    _traceFrame = 0;
                    _traceLockedCount = thrusters.Count;
                    _traceActive = true;
                    Log.Default?.Info($"[AERO-TRACE] LOCKED grid={gridHash:X8} thruster=0 count={thrusters.Count}");
                }
            }
        }
        if (_traceActive) _traceFrame++;
        _traceNetOffsetTorque = Vector3.Zero;
        Vector3 hoverCoupling = Vector3.Zero;

        // Reset per-grid debug states for this frame
        if (!DebugStatesByGrid.TryGetValue(thrusters, out var debugStates))
        {
            debugStates = new List<ThrusterDebugState>();
            DebugStatesByGrid[thrusters] = debugStates;
        }
        while (debugStates.Count < thrusters.Count)
            debugStates.Add(default);
        while (debugStates.Count > thrusters.Count)
            debugStates.RemoveAt(debugStates.Count - 1);
        for (int i = 0; i < debugStates.Count; i++)
            debugStates[i] = default;

        // Read CoM in local space (needed for coupling torque computation)
        PhysicsHack.TryGetMassProperties(gridData, out _, out Vector3 comLocal);

        // Precompute gravity capacity for hover coupling (same formula as attitude baseline)
        bool hasGravityOL = gravityLocal.LengthSquared() > 0.1f;
        float totalGravCapacityOL = 0f;
        float weightForceOL = 0f;
        if (hasGravityOL)
        {
            for (int j = 0; j < thrusters.Count; j++)
            {
                float al = Vector3.Dot(-gravityLocal, -thrusters[j].ThrustDirection);
                if (al > 0f) totalGravCapacityOL += al * thrusters[j].MaxPower;
            }
            weightForceOL = gravityLocal.Length() * mass;
        }

        // Skip offset loop during Phase 7 test — isolate attitude controller from coupling
        if (skipOffsetLoop) goto AttitudeOnly;

        for (int i = 0; i < thrusters.Count; i++)
        {
            var thruster = thrusters[i];

            // ── Read actual thrust state via ThrusterComponent ──
            float overridePower = Math.Max(0f, PhysicsHack.GetThrustOverride(thruster.ThrusterEntity.Data));

            // Determine actual thrust force this frame (vanilla value)
            // ThrustOverride > 0 means override active (0-1 normalized).
            // Otherwise, check IsThrusting data tag.
            float vanillaThrust = 0f;
            if (overridePower > 0f)
            {
                vanillaThrust = thruster.MaxPower * overridePower;
            }
            else if (IsComponentThrusting(thruster.ThrusterComponent))
            {
                vanillaThrust = thruster.MaxPower;
            }

            if (vanillaThrust < 0.01f)
                continue;

            // Throttled diagnostic: log first active thrust detection
            if (_executeLogCooldown == 0)
            {
                float cosIntake = Vector3.Dot(velocityLocalHat, thruster.IntakeDirection);
                float logScale = thruster.Profile != null
                    ? (float)thruster.Profile.Evaluate(mach, velocityLocalHat, thruster.IntakeDirection) : 1f;
                float logOverride = Math.Max(0f, PhysicsHack.GetThrustOverride(thruster.ThrusterEntity.Data));
                Log.Default?.Info($"[AERO] OffsetThrust active: [{i}] actual={vanillaThrust:F0}N override={logOverride:F3} max={thruster.MaxPower:F0}N " +
                    $"dir=({thruster.ThrustDirection.X:F1},{thruster.ThrustDirection.Y:F1},{thruster.ThrustDirection.Z:F1}) " +
                    $"mach={mach:F2} cosIntake={cosIntake:F3} scale={logScale:F3}" +
                    (thruster.Profile != null ? $" [{thruster.Profile.Name}]" : ""));
                _executeLogCooldown = 600;
            }

            // ── Mach-dependent thrust scaling ──
            // Vanilla applies full rated thrust. We scale to the profile curve
            // and apply the delta so the net matches the profile.
            float profileScale = 1f;
            if (thruster.Profile != null)
            {
                profileScale = (float)thruster.Profile.Evaluate(
                    mach, velocityLocalHat, thruster.IntakeDirection);
            }
            float scaledThrust = vanillaThrust * profileScale;
            if (float.IsNaN(scaledThrust) || float.IsInfinity(scaledThrust)) continue;

            // Record debug state for this thruster
            var dbg = debugStates[i];
            dbg.VanillaThrust = vanillaThrust;
            dbg.ScaledThrust = scaledThrust;
            dbg.IsActive = true;
            debugStates[i] = dbg;

            // ── Offset torque + Mach scaling ──
            // Vanilla applies thrust at CoM (linear only, no torque).
            // We add the coupling torque that would exist if thrust acted at block position.
            // For Mach-scaled profiles, also apply the linear delta (scaled - vanilla).
            Vector3 forceDir = -thruster.ThrustDirection;
            Vector3 localForce = forceDir * scaledThrust;

            // Coupling torque from offset position
            Vector3 rOffset = thruster.GridLocalPosition - comLocal;
            Vector3 couplingTorqueI = Vector3.Cross(rOffset, localForce);

            // NaN guard
            if (float.IsNaN(couplingTorqueI.X)) continue;

            // Apply pure torque (no linear change — vanilla's thrust stays intact)
            PhysicsHack.ApplyTorqueImpulse(gridData, couplingTorqueI * DT, gridWt);

            // Mach scaling: apply linear delta if profile != 1.0
            if (Math.Abs(profileScale - 1f) > 0.001f)
            {
                Vector3 deltaImpulse = WorldTransform.TransformDirection(
                    forceDir * (scaledThrust - vanillaThrust) * DT, gridWt);
                if (!float.IsNaN(deltaImpulse.X))
                    PhysicsHack.ApplyLinearImpulse(gridData, deltaImpulse);
            }
            _traceNetOffsetTorque += couplingTorqueI;

            // Compute hover-only coupling: what torque would this thruster create
            // if it only fired for gravity hover (no attitude adjustment)?
            // Uses same proportional gravity share as the attitude controller baseline.
            Vector3 fd = -thruster.ThrustDirection;
            float hoverAlign = Vector3.Dot(-gravityLocal, fd);
            if (hoverAlign > 0f && totalGravCapacityOL > 1f)
            {
                float gravThrottle = Math.Clamp(
                    (hoverAlign * weightForceOL) / totalGravCapacityOL, 0f, 1f);
                Vector3 hoverForce = fd * (gravThrottle * thruster.MaxPower * profileScale);
                hoverCoupling += Vector3.Cross(rOffset, hoverForce);
            }

            // Phase 1 trace: every variable for tracked thruster
            if (_traceActive && i == _traceIndex)
            {
                bool isThrustingFlag = IsComponentThrusting(thruster.ThrusterComponent);
                float traceOverride = Math.Max(0f, PhysicsHack.GetThrustOverride(thruster.ThrusterEntity.Data));
                Log.Default?.Info($"[AERO-TRACE] OFFSET f={_traceFrame}" +
                    $" pos=({thruster.GridLocalPosition.X:F2},{thruster.GridLocalPosition.Y:F2},{thruster.GridLocalPosition.Z:F2})" +
                    $" dir=({thruster.ThrustDirection.X:F2},{thruster.ThrustDirection.Y:F2},{thruster.ThrustDirection.Z:F2})" +
                    $" com=({comLocal.X:F2},{comLocal.Y:F2},{comLocal.Z:F2})" +
                    $" r=({rOffset.X:F2},{rOffset.Y:F2},{rOffset.Z:F2})" +
                    $" override={traceOverride:F3} isThrusting={isThrustingFlag}" +
                    $" actual={vanillaThrust:F0} profScale={profileScale:F3} scaled={scaledThrust:F0}" +
                    $" localF=({localForce.X:F0},{localForce.Y:F0},{localForce.Z:F0})" +
                    $" coupling=({couplingTorqueI.X:F0},{couplingTorqueI.Y:F0},{couplingTorqueI.Z:F0})");
            }
        }

        // Per-thruster blending sets all overrides every frame — no clearing needed
        HoverCouplingTorque = hoverCoupling;
        NetOffsetCouplingTorque = _traceNetOffsetTorque;

        AttitudeOnly:
        // ── Attitude control (real differential thrust) ──
        // Differentially throttle thrusters via ThrusterOverrideData ECS tag to produce
        // counter-torque. No phantom forces — all torque comes from real thruster output.
        // With dampeners off, coupling passes through naturally — asymmetric thrust spins the ship.
        Vector3 localAngVel = WorldTransform.TransformDirectionInv(angularVelocity, gridWt);
        ApplyAttitudeViaOverrides(thrusters, comLocal, enableDampening, localAngVel,
            velocityLocal, playerMovement, playerRotation, targetAngVel);

        // Write accumulated override thrust to grid-level OverriddenThrustData
        // This is what ThrustComponent.ComputeThrust reads to produce actual force.
        // Write directly — forceDir opposes velocity, which is what we want.
        // No per-thruster tags = collector won't overwrite us.
        bool ovrOk;
        if (_netOverrideThrust.LengthSquared() > 0.01f)
            ovrOk = PhysicsHack.TrySetOverriddenThrust(gridEntity, gridData, _netOverrideThrust);
        else
            ovrOk = PhysicsHack.TrySetOverriddenThrust(gridEntity, gridData, Vector3.Zero);

        // Readback verification (once per second)
        _dampDiagCooldown = Math.Max(0, _dampDiagCooldown - 1);
        if (_dampDiagCooldown == 0 && _netOverrideThrust.LengthSquared() > 0.01f)
        {
            Vector3 readback = PhysicsHack.GetOverriddenThrust(gridData);
            // Also check ActiveThrustData on the GRID entity (not thruster)
            float gridActiveThrust = PhysicsHack.GetGridActiveThrust(gridData);
            Log.Default?.Info($"[OVR-DIAG] wrote=({_netOverrideThrust.X:F0},{_netOverrideThrust.Y:F0},{_netOverrideThrust.Z:F0})" +
                $" readback=({readback.X:F0},{readback.Y:F0},{readback.Z:F0}) ok={ovrOk}" +
                $" gridActiveThrust={gridActiveThrust:F0}");
            _dampDiagCooldown = 60;
        }

        // Phase 3 trace: net torque summary
        if (_traceActive)
        {
            Vector3 residual = _traceNetOffsetTorque + _traceNetAttitudeTorque;
            Log.Default?.Info($"[AERO-TRACE] NET f={_traceFrame}" +
                $" offset=({_traceNetOffsetTorque.X:F0},{_traceNetOffsetTorque.Y:F0},{_traceNetOffsetTorque.Z:F0})" +
                $" att=({_traceNetAttitudeTorque.X:F0},{_traceNetAttitudeTorque.Y:F0},{_traceNetAttitudeTorque.Z:F0})" +
                $" residual=({residual.X:F0},{residual.Y:F0},{residual.Z:F0})" +
                $" |res|={residual.Length():F0}");
        }

        // ── Throttled debug log ──
        _debugLogCooldown = Math.Max(0, _debugLogCooldown - 1);
        if (_debugLogCooldown == 0)
        {
            _debugLogCooldown = 120; // every 2 seconds
            for (int i = 0; i < debugStates.Count && i < thrusters.Count; i++)
            {
                var dbg = debugStates[i];
                var t = thrusters[i];
                Log.Default?.Info(
                    $"[AERO-THR] T{i} active={dbg.IsActive} " +
                    $"vanilla={dbg.VanillaThrust:F0}N scaled={dbg.ScaledThrust:F0}N max={t.MaxPower:F0}N " +
                    $"pos=({t.GridLocalPosition.X:F1},{t.GridLocalPosition.Y:F1},{t.GridLocalPosition.Z:F1}) " +
                    $"dir=({t.ThrustDirection.X:F1},{t.ThrustDirection.Y:F1},{t.ThrustDirection.Z:F1}) " +
                    $"arm={dbg.TorqueArm:F1}m att={dbg.AttitudeOverride:P0} " +
                    $"D={dbg.DTermComponent:F3} P={dbg.PTermComponent:F3} " +
                    $"frac={t.AttitudeFraction:F2}");
            }
        }
    }

    /// <summary>
    /// Pure Bob RCS pattern with raw vanilla inputs.
    ///
    /// Each thruster independently:
    ///   1. movement = Dot(-movementInput, forceDir) — player WASD
    ///   2. rotation = Dot(-rotationInput, torqueArm) — player mouse/roll
    ///   3. If movement == 0, linear damping: Dot(-velocity, forceDir)
    ///   4. If rotation == 0, angular damping: Dot(-angVel, torqueArm)
    ///   5. throttle = rotation * limiter + movement * (1 - limiter)
    /// </summary>
    private static void ApplyAttitudeViaOverrides(
        List<ThrusterInfo> thrusters,
        Vector3 comLocal,
        bool enableDampening,
        Vector3 localAngVel,
        Vector3 velocityLocal,
        Vector3 playerMovement,
        Vector3 playerRotation,
        Vector3 targetAngVel = default)
    {
        int n = thrusters.Count;
        _traceNetAttitudeTorque = Vector3.Zero;
        _netOverrideThrust = Vector3.Zero;

        bool hasPlayerMove = playerMovement.LengthSquared() > 0.0001f;
        bool hasPlayerRot = playerRotation.LengthSquared() > 0.0001f;
        bool hasAngVel = localAngVel.Length() > AngularDampeningThreshold;
        bool hasLinVel = velocityLocal.Length() > LinearDampeningThreshold;

        // Throttled debug: log inputs every 2 seconds
        if (_traceActive && _traceFrame % 120 == 0)
        {
            Log.Default?.Info($"[AERO-INPUT] damp={enableDampening}" +
                $" move=({playerMovement.X:F2},{playerMovement.Y:F2},{playerMovement.Z:F2})" +
                $" rot=({playerRotation.X:F2},{playerRotation.Y:F2},{playerRotation.Z:F2})" +
                $" |w|={localAngVel.Length():F4} |v|={velocityLocal.Length():F2}");
        }

        // If dampeners off and no player input, nothing to do
        if (!enableDampening && !hasPlayerMove && !hasPlayerRot) return;

        int overrideCount = 0;
        Vector3 producedTorque = Vector3.Zero;

        for (int i = 0; i < n; i++)
        {
            var t = thrusters[i];
            Vector3 forceDir = -t.ThrustDirection;
            Vector3 r = t.GridLocalPosition - comLocal;
            Vector3 torqueArm = Vector3.Cross(r, forceDir);
            float arm = torqueArm.Length();

            // Live-read attitude fraction from terminal slider (falls back to cached value)
            float limiter = t.Settings != null ? t.Settings.AttitudeFraction : t.AttitudeFraction;

            float movement = 0f;
            float rotation = 0f;
            bool movementFromPlayer = false;

            // ── Step 1: movement from player WASD ──
            if (hasPlayerMove)
            {
                movement = Math.Clamp(Vector3.Dot(-playerMovement, forceDir), -1f, 1f);
                if (movement != 0f) movementFromPlayer = true;
            }

            // ── Step 2: rotation from player mouse/roll ──
            if (hasPlayerRot && arm > 0.001f)
                rotation = Math.Clamp(Vector3.Dot(-playerRotation, torqueArm), -1f, 1f);

            if (enableDampening)
            {
                // ── Step 3: if THIS thruster's movement == 0, linear damping ──
                if (movement == 0f && hasLinVel)
                {
                    float rawMov = Vector3.Dot(-velocityLocal, forceDir);
                    movement = Math.Abs(rawMov) < LinearDampeningThreshold ? 0f
                        : Math.Clamp(rawMov, -1f, 1f);
                }

                // ── Step 4: if THIS thruster's rotation == 0, attitude command + angular damping ──
                // SE1 Bob pattern: attitude command and rate-nulling are INDEPENDENT per-thruster.
                // Combine: project attitude command onto torqueArm, PLUS rate-nulling damping.
                // This avoids the cross-axis coupling that happens when D-term is in the command.
                if (rotation == 0f && arm > 0.001f)
                {
                    float attRot = 0f;
                    float dampRot = 0f;

                    // Attitude command (P-only from harness/attitude hold)
                    if (targetAngVel.LengthSquared() > AngularDampeningThreshold * AngularDampeningThreshold)
                    {
                        float rawAtt = Vector3.Dot(targetAngVel, torqueArm);
                        attRot = Math.Abs(rawAtt) < AngularDampeningThreshold ? 0f
                            : Math.Clamp(rawAtt, -1f, 1f);
                    }

                    // Rate-nulling damping (always active, independent of attitude command)
                    if (hasAngVel)
                    {
                        float rawDamp = Vector3.Dot(-localAngVel, torqueArm);
                        dampRot = Math.Abs(rawDamp) < AngularDampeningThreshold ? 0f
                            : Math.Clamp(rawDamp, -1f, 1f);
                    }

                    // Blend: attitude drives toward target, damping prevents overshoot
                    rotation = Math.Clamp(attRot + dampRot, -1f, 1f);
                }
            }

            // ── Step 5: Bob blend ──
            // When only one axis has demand, give it full authority.
            // Only blend when both translation and rotation want the thruster.
            float throttle;
            if (rotation <= 0f)
                throttle = Math.Clamp(movement, 0f, 1f);
            else if (movement <= 0f)
                throttle = Math.Clamp(rotation, 0f, 1f);
            else
                throttle = Math.Clamp(rotation * limiter + movement * (1f - limiter), 0f, 1f);

            // Both non-positive → release to vanilla (override=0)
            float overrideValue;
            if (movement <= 0f && rotation <= 0f)
                overrideValue = 0f;
            else
                overrideValue = throttle > 0.0001f ? throttle : 0.0001f;

            // Temporary debug: log first 4 thrusters every 60 frames
            if (i < 4 && _traceFrame % 60 == 1)
            {
                Log.Default?.Info($"[BOB-DBG] T{i} mov={movement:F4} rot={rotation:F4} thr={throttle:F4} ovr={overrideValue:F4}" +
                    $" arm={arm:F1} lim={limiter:F2}" +
                    $" tgtAV=({targetAngVel.X:F3},{targetAngVel.Y:F3},{targetAngVel.Z:F3})" +
                    $" locAV=({localAngVel.X:F4},{localAngVel.Y:F4},{localAngVel.Z:F4})" +
                    $" damp={enableDampening}");
            }

            // NOTE: Per-thruster ThrusterOverrideData tags intentionally NOT set here.
            // The collector recomputes OverriddenThrustData from per-thruster tags with wrong
            // direction mapping. Instead, we write OverriddenThrustData directly on the grid.
            overrideCount++;
            if (throttle > 0.001f)
            {
                producedTorque += torqueArm * (throttle * t.MaxPower);
                // Only accumulate damping/attitude thrust into OverriddenThrustData.
                // Player input thrust is already handled by ComputeThrust via ControlData.
                if (!movementFromPlayer)
                    _netOverrideThrust += forceDir * (throttle * t.MaxPower);
            }

            // Record debug state
            Vector3 torqueAxis = arm > 0.001f ? torqueArm / arm : Vector3.Zero;
            if (DebugStatesByGrid.TryGetValue(thrusters, out var attDebugStates) && i < attDebugStates.Count)
            {
                var dbg = attDebugStates[i];
                dbg.DTermComponent = rotation;
                dbg.PTermComponent = movement;
                dbg.TorqueAxis = torqueAxis;
                dbg.TorqueArm = arm;
                dbg.AttitudeOverride = throttle;
                attDebugStates[i] = dbg;
            }
        }
        _traceNetAttitudeTorque = producedTorque;

        // Dampening diagnostic: log once per second when dampeners are active and velocity is significant
        _dampDiagCooldown = Math.Max(0, _dampDiagCooldown - 1);
        if (enableDampening && hasLinVel && _dampDiagCooldown == 0)
        {
            int posOverrides = 0;
            int actualFiring = 0;
            float maxOvr = 0f;
            float totalActual = 0f;
            for (int j = 0; j < n; j++)
            {
                float ovr = Math.Max(0f, PhysicsHack.GetThrustOverride(thrusters[j].ThrusterEntity.Data));
                if (ovr > 0.001f) posOverrides++;
                if (ovr > maxOvr) maxOvr = ovr;
                float actual = GetActualThrust(thrusters[j].ThrusterComponent);
                if (actual > 0.01f) { actualFiring++; totalActual += actual; }
            }
            Log.Default?.Info($"[DAMP-DIAG] |v|={velocityLocal.Length():F1} vel=({velocityLocal.X:F1},{velocityLocal.Y:F1},{velocityLocal.Z:F1})" +
                $" damp={enableDampening} n={n} posOvr={posOverrides} maxOvr={maxOvr:F3}" +
                $" actualFiring={actualFiring} totalActualN={totalActual:F0}" +
                $" ovrThrust=({_netOverrideThrust.X:F0},{_netOverrideThrust.Y:F0},{_netOverrideThrust.Z:F0})" +
                $" |w|={localAngVel.Length():F4}");
            _dampDiagCooldown = 60;
        }

        // Trace logging
        if (_traceActive && _traceIndex >= 0 && _traceIndex < n)
        {
            var tt = thrusters[_traceIndex];
            Vector3 fd = -tt.ThrustDirection;
            Vector3 ta = Vector3.Cross(tt.GridLocalPosition - comLocal, fd);

            float mov = hasPlayerMove ? Math.Clamp(Vector3.Dot(-playerMovement, fd), -1f, 1f) : 0f;
            if (mov == 0f && hasLinVel) mov = Math.Clamp(Vector3.Dot(-velocityLocal, fd), -1f, 1f);
            float rot = hasPlayerRot ? Math.Clamp(Vector3.Dot(-playerRotation, ta), -1f, 1f) : 0f;
            if (rot == 0f && ta.Length() > 0.001f)
            {
                if (targetAngVel.LengthSquared() > AngularDampeningThreshold * AngularDampeningThreshold)
                    rot = Math.Clamp(Vector3.Dot(targetAngVel, ta), -1f, 1f);
                else if (hasAngVel)
                    rot = Math.Clamp(Vector3.Dot(-localAngVel, ta), -1f, 1f);
            }
            float trLim = tt.Settings != null ? tt.Settings.AttitudeFraction : tt.AttitudeFraction;
            float trThrottle = Math.Clamp(rot * trLim + mov * (1f - trLim), 0f, 1f);

            Log.Default?.Info($"[AERO-TRACE] ATT f={_traceFrame}" +
                $" vel=({velocityLocal.X:F1},{velocityLocal.Y:F1},{velocityLocal.Z:F1})" +
                $" angVel=({localAngVel.X:F4},{localAngVel.Y:F4},{localAngVel.Z:F4})" +
                $" thr[{_traceIndex}] mov={mov:F3} rot={rot:F3}" +
                $" thr={trThrottle:F3} lim={trLim:F2} overrides={overrideCount}");
        }
    }


    // ═══════════════════════════════════════════════════════════════
    // Thruster cache building
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// GUID → ThrustProfile mapping for custom thruster blocks.
    /// Register entries before the first RebuildThrusterCache call.
    /// Vanilla SE2 thrusters (hydrogen, ion) get null profile (= rocket, flat 1.0).
    /// </summary>
    public static readonly Dictionary<Guid, ThrustProfile> ProfileByGuid = new();

    static OffsetThrustJob()
    {
        // Atmospheric thrusters = Turbofan (air-breathing, thrust drops at transonic)
        var atmo = ThrustProfiles.Turbofan;
        ProfileByGuid[new Guid("8dfddd91-eb3a-4979-8b16-2fdf43061486")] = atmo; // Atmo 100
        ProfileByGuid[new Guid("272859d7-957d-41ba-9f9c-8f84cddb5cb3")] = atmo; // Atmo 250
        ProfileByGuid[new Guid("8a005c16-0a05-444d-ab26-786653199b52")] = atmo; // Atmo 500
        ProfileByGuid[new Guid("bfbbff3e-7396-428a-a12c-3f32ec14c712")] = atmo; // Atmo 1000
        // Hydrogen & Ion = Rocket (no Mach penalty, null profile by default)
    }

    // Cached reflection for reading block definition GUID

    /// <summary>
    /// Scan all blocks on the grid and build a cache of thruster info.
    /// Call on grid init and when blocks change.
    ///
    /// Detection strategy: iterate Entity.Components on each block to find
    /// ThrusterComponent by type. This bypasses the tag-based Entity.TryGet
    /// and DEntityContext.TryGet which fail when invoked via reflection
    /// (DEntityContext is a struct; boxing breaks the archetype-based
    /// ref-returning data lookup).
    /// </summary>
    public static void RebuildThrusterCache(
        BlockOctreeComponent octree,
        float blockSize,
        List<ThrusterInfo> outThrusters,
        Entity gridEntity = null)
    {
        outThrusters.Clear();
        EnsureThrusterReflectionResolved();

        if (_thrusterCompType == null)
        {
            Log.Default?.Info("[AERO] OffsetThrust: ThrusterComponent type not resolved, skipping");
            return;
        }

        // Strategy: iterate grid's HierarchyComponent.Children to find entities
        // with ThrusterComponent. The block octree only stores structural
        // CubeBlockComponents — functional components like ThrusterComponent
        // live on the same entity but aren't findable via the octree's type.
        // HierarchyComponent.Children contains ALL child entities.
        int totalChildren = 0, thrustBlocks = 0;

        if (gridEntity != null)
        {
            var hierarchy = gridEntity.TryGet<HierarchyComponent>();
            if (hierarchy != null && hierarchy.Children != null)
            {
                foreach (var childEntity in hierarchy.Children)
                {
                    if (childEntity == null) continue;
                    totalChildren++;

                    // Find ThrusterComponent — try both approaches:
                    // 1. Entity.TryGet(tag) — uses CompositionData lookup
                    // 2. FindComponentByType — iterates Entity.Components via reflection
                    var tag = DefaultTag.Get(_thrusterCompType);
                    Component thrusterComp = childEntity.TryGet(tag);
                    thrusterComp ??= PhysicsHack.FindComponentByType(childEntity, _thrusterCompType);
                    if (thrusterComp == null) continue;
                    thrustBlocks++;

                    // Get CubeBlockComponent from this entity for orientation/position
                    var block = childEntity.TryGet<CubeBlockComponent>();
                    if (block == null) continue;

                    float maxPower = 0;
                    int directionInt = 0;

                    if (_thrusterDefField != null)
                    {
                        var def = _thrusterDefField.GetValue(thrusterComp);
                        if (def != null)
                        {
                            if (def is ThrusterDefinition td)
                                maxPower = td.ThrustPower;
                            if (def is ThrusterDefinition td2)
                            {
                                var rawDir = (object)td2.ThrustDirection;
                                // Unbox enum to its underlying type first, then convert
                                directionInt = Convert.ToInt32(rawDir);
                                var orientedDir = block.BlockOrientation.TransformDirection(
                                    (Base6Directions.Direction)directionInt);
                                directionInt = (int)orientedDir;
                            }
                        }
                    }

                    if (maxPower <= 0f) continue;

                    var aabb = block.AABB;
                    Vector3 gridCenter = new Vector3(
                        (aabb.Min.X + aabb.Max.X + 1) * 0.5f,
                        (aabb.Min.Y + aabb.Max.Y + 1) * 0.5f,
                        (aabb.Min.Z + aabb.Max.Z + 1) * 0.5f);
                    Vector3 localPos = gridCenter * blockSize;

                    const float CellSize = 0.25f;
                    Vector3 drawPos = new Vector3(
                        (aabb.Min.X + aabb.Max.X) * 0.5f * CellSize,
                        (aabb.Min.Y + aabb.Max.Y) * 0.5f * CellSize,
                        (aabb.Min.Z + aabb.Max.Z) * 0.5f * CellSize);

                    Vector3 thrustDir = DirectionToVector(directionInt);

                    // Classify by ThrustClass from ThrusterDefinition
                    ThrustProfile profile = null;
                    string thrustClass = GetThrustClass(thrusterComp);
                    if (thrustClass != null && thrustClass.Contains("Atmospheric"))
                        profile = ThrustProfiles.Turbofan;

                    // Read per-thruster attitude fraction from injected component
                    var aeroSettings = childEntity.TryGet<AeroThrustSettingsComponent>();
                    float attFrac = 0.5f; // hardcoded default — terminal slider can override later

                    outThrusters.Add(new ThrusterInfo
                    {
                        ThrusterEntity = childEntity,
                        ThrusterComponent = thrusterComp,
                        GridLocalPosition = localPos,
                        DrawPosition = drawPos,
                        ThrustDirection = thrustDir,
                        MaxPower = maxPower,
                        Profile = profile,
                        IntakeDirection = thrustDir, // intake faces into airflow = same direction as thrust
                        AttitudeFraction = attFrac,
                        Settings = aeroSettings,
                    });
                }
            }
        }

        int airBreathing = 0;
        for (int i = 0; i < outThrusters.Count; i++)
            if (outThrusters[i].Profile != null) airBreathing++;

        Log.Default?.Info($"[AERO] OffsetThrust: scanned {totalChildren} hierarchy children, {thrustBlocks} with ThrusterComponent, cached {outThrusters.Count} thrusters ({airBreathing} air-breathing)");
    }

    /// <summary>
    /// Block definition GUID. HOLDOUT: CubeBlockComponent.Definition is public, but
    /// CubeBlockDefinition derives from MaxHealthComponentDefinition in VRage.Game, which is
    /// NOT a referenced assembly for mod scripts:
    ///   CS0012: The type 'MaxHealthComponentDefinition' is defined in an assembly that is not
    ///   referenced
    /// so the compiler cannot walk the base chain to Definition.Guid. Reading it late-bound is
    /// the only way in until VRage.Game joins GameCompilationDescriptor.MetaDatas.
    /// </summary>
    private static Guid? GetBlockDefinitionGuid(CubeBlockComponent block)
    {
        if (block == null) return null;
        _blockDefProp ??= typeof(CubeBlockComponent).GetProperty("Definition",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        var def = _blockDefProp?.GetValue(block);
        if (def == null) return null;

        _blockGuidProp ??= def.GetType().GetProperty("Guid",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        return _blockGuidProp != null ? (Guid)_blockGuidProp.GetValue(def) : (Guid?)null;
    }

    private static System.Reflection.PropertyInfo _blockDefProp;
    private static System.Reflection.PropertyInfo _blockGuidProp;

    /// <summary>
    /// Convert Base6Directions.Direction (int) to a unit vector.
    /// Forward=0, Backward=1, Left=2, Right=3, Up=4, Down=5
    /// </summary>
    private static Vector3 DirectionToVector(int dir)
    {
        switch (dir)
        {
            case 0: return -Vector3.UnitZ; // Forward
            case 1: return Vector3.UnitZ;  // Backward
            case 2: return -Vector3.UnitX; // Left
            case 3: return Vector3.UnitX;  // Right
            case 4: return Vector3.UnitY;  // Up
            case 5: return -Vector3.UnitY; // Down
            default: return Vector3.Zero;
        }
    }

    // ─── ThrusterComponent reflection resolution ─────────

    private static Type _thrusterCompType;
    private static System.Reflection.FieldInfo _thrusterDefField;
    private static bool _thrusterReflectionResolved;

    /// <summary>
    /// HOLDOUT: ThrusterComponent._definition is a private field with no public accessor.
    /// The type itself is public and every value we want off ThrusterDefinition
    /// (ThrustPower / ThrustDirection / ThrustClass) is a public property, so this resolves the
    /// one field and everything after it is typed.
    /// </summary>
    private static void EnsureThrusterReflectionResolved()
    {
        if (_thrusterReflectionResolved) return;
        _thrusterReflectionResolved = true;
        try
        {
            _thrusterCompType = typeof(ThrusterComponent);
            _thrusterDefField = _thrusterCompType.GetField("_definition",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Log.Default?.Info($"[AERO] ThrusterComponent def field={_thrusterDefField != null}");
        }
        catch (Exception ex)
        {
            Log.Default?.Info($"[AERO] ThrusterComponent resolution failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Whether the thruster is firing. Component.HasData is protected, but Component.Data is a
    /// public DEntityContext, so the public Has&lt;T&gt; gets there without reflection.
    /// </summary>
    private static bool IsComponentThrusting(Component thrusterComp)
    {
        if (thrusterComp == null) return false;
        try { return thrusterComp.Data.Has<IsThrusting>(); }
        catch { return false; }
    }

    /// <summary>
    /// Game-computed thrust for this thruster this frame, in newtons; 0 when the game produced
    /// none. SE2's equivalent of SE1 Thrust.CurrentStrength * ForceMagnitude.
    /// Component.TryGetData is protected, but Component.Data is public, so TryGet works.
    /// </summary>
    private static float GetActualThrust(Component thrusterComp)
    {
        if (thrusterComp == null) return -1f;
        try
        {
            return thrusterComp.Data.TryGet<ActiveThrustData>(out var atd)
                ? atd.ComputedThrustPerFrame.Length()
                : 0f;
        }
        catch { return -1f; }
    }

    /// <summary>Public wrapper to force a thrust override from outside.</summary>
    public static void ForceOverride(ThrusterInfo thruster, float value)
    {
        if (value > 0f)
            PhysicsHack.TrySetThrustOverride(thruster.ThrusterEntity.Data, value);
        else
            PhysicsHack.TryRemoveThrustOverride(thruster.ThrusterEntity.Data);
    }

    /// <summary>Read ThrustClass (StringId) from ThrusterDefinition on a ThrusterComponent.</summary>
    private static string GetThrustClass(Component thrusterComp)
    {
        if (_thrusterDefField == null) return null;
        try
        {
            var def = _thrusterDefField.GetValue(thrusterComp);
            if (def == null) return null;
            var tc = (object)((ThrusterDefinition)def).ThrustClass;
            return tc?.ToString();
        }
        catch { return null; }
    }

    // ═══════════════════════════════════════════════════════════════
    // Gyroscope detection and control
    // ═══════════════════════════════════════════════════════════════

    private static Type _gyroCompType;
    private static System.Reflection.FieldInfo _gyroDefField;
    private static Type _powerableBlockType;
    private static bool _gyroReflectionResolved;

    private static void EnsureGyroReflectionResolved()
    {
        if (_gyroReflectionResolved) return;
        _gyroReflectionResolved = true;

        try
        {
            // Same shape as the thruster: public type, private _definition field, public
            // GyroscopeDefinition.MaxTorque. PowerableBlockComponent.Enabled is public outright.
            _gyroCompType = typeof(GyroscopeComponent);
            _gyroDefField = _gyroCompType.GetField("_definition",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            _powerableBlockType = typeof(PowerableBlockComponent);

            Log.Default?.Info($"[AERO] GyroscopeComponent def field={_gyroDefField != null}");
        }
        catch (Exception ex)
        {
            Log.Default?.Info($"[AERO] GyroscopeComponent resolution failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Scan grid hierarchy for gyroscope blocks. Same pattern as thruster detection.
    /// </summary>
    public static void RebuildGyroCache(Entity gridEntity, List<GyroInfo> outGyros)
    {
        outGyros.Clear();
        EnsureGyroReflectionResolved();
        if (_gyroCompType == null || gridEntity == null) return;

        var hierarchy = gridEntity.TryGet<HierarchyComponent>();
        if (hierarchy == null || hierarchy.Children == null) return;

        var tag = DefaultTag.Get(_gyroCompType);

        foreach (var child in hierarchy.Children)
        {
            if (child == null) continue;

            var gyroComp = child.TryGet(tag);
            gyroComp ??= PhysicsHack.FindComponentByType(child, _gyroCompType);
            if (gyroComp == null) continue;

            // Read MaxTorque from definition
            float maxTorque = 0f;
            if (_gyroDefField != null)
            {
                try
                {
                    var def = _gyroDefField.GetValue(gyroComp);
                    if (def != null)
                        maxTorque = ((GyroscopeDefinition)def).MaxTorque;
                }
                catch { }
            }

            // Find the PowerableBlockComponent on the same entity for Enabled toggle
            Component blockComp = null;
            if (_powerableBlockType != null)
            {
                var blockTag = DefaultTag.Get(_powerableBlockType);
                blockComp = child.TryGet(blockTag);
                blockComp ??= PhysicsHack.FindComponentByType(child, _powerableBlockType);
            }

            outGyros.Add(new GyroInfo
            {
                GyroEntity = child,
                BlockComponent = blockComp,
                MaxTorque = maxTorque,
            });
        }

        if (outGyros.Count > 0)
            Log.Default?.Info($"[AERO] GyroCache: found {outGyros.Count} gyros, totalTorque={outGyros.Sum(g => g.MaxTorque):F0} N·m");
    }

    /// <summary>
    /// Enable or disable all gyros on a grid via PowerableBlockComponent.Enabled.
    /// </summary>
    public static void SetGyrosEnabled(List<GyroInfo> gyros, bool enabled)
    {
        int toggled = 0;
        for (int i = 0; i < gyros.Count; i++)
        {
            if (gyros[i].BlockComponent == null) continue;
            try
            {
                bool current = ((PowerableBlockComponent)gyros[i].BlockComponent).Enabled;
                if (current != enabled)
                {
                    ((PowerableBlockComponent)gyros[i].BlockComponent).Enabled = enabled;
                    toggled++;
                }
            }
            catch { }
        }
        if (toggled > 0)
            Log.Default?.Info($"[AERO] Gyros: {(enabled ? "enabled" : "disabled")} {toggled}/{gyros.Count}");
    }

    /// <summary>Check if gyros are currently enabled.</summary>
    public static bool AreGyrosEnabled(List<GyroInfo> gyros)
    {
        if (gyros.Count == 0) return true;
        try { return ((PowerableBlockComponent)gyros[0].BlockComponent).Enabled; }
        catch { return true; }
    }
}
