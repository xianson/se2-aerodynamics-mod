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
    public Vector3 GridLocalPosition;  // block center in grid-local space (meters): CellCoords.BlockCentre, the game's
    public Vector3 DrawPosition;       // block center for debug draw (the same point since 2026-10-15)
    public Vector3 ThrustDirection;    // unit vector in grid-local space (direction force pushes the ship)
    public float MaxPower;             // Newtons (impulse per frame = MaxPower / 60)
    public ThrustProfile Profile;      // Mach-dependent thrust scaling (null = rocket/flat)
    public Vector3 IntakeDirection;    // unit vector pointing into intake (opposite ThrustDirection for air-breathers)
    public float AttitudeFraction;     // 0..1: fraction of capacity reserved for attitude (from terminal slider)
    public AeroThrustSettingsComponent Settings; // live reference for reading slider changes
    /// <summary>The block's place in its grid (its cell box's Min + Max, 0.25 m cells): the same in the server and client
    /// copies of a grid, at any speed - the key its flame share is published under (ThrustTorque.Publish).</summary>
    public Vector3I Cell;
    /// <summary>The block's cell box (0.25 m cells, inclusive): flow-through engines (AeroGridComponent.EngineBoxes).</summary>
    public Vector3I CellMin, CellMax;
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
public static partial class OffsetThrustJob
{
    private const float DT = 1f / 60f;
    private const float AngularDampeningThreshold = 0.01f;  // Bob's RotationDampeningAggresiveness
    private const float LinearDampeningThreshold = 0.001f; // Bob's MovementDampeningAggresiveness

    private static int _executeLogCooldown;

    // ── Per-thruster trace logging ──
    private static int _traceGridHash;
    /// <summary>
    /// The thrust diagnostics: the per-frame [AERO-TRACE] of a big grid's thrusters, the [AERO-THR] dump and
    /// [AERO-HOLD]. Off: on, they wrote ~100 lines a second into the game's log in ordinary play.
    /// </summary>
    public static bool Verbose = false;
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
    // (thread-safe and weak: every grid's sim job adds here in parallel - a plain Dictionary could be corrupted by two
    //  inserts at once - and a removed grid's entry goes with its thruster list; it was never pruned)
    public static readonly System.Runtime.CompilerServices.ConditionalWeakTable<List<ThrusterInfo>, List<ThrusterDebugState>> DebugStatesByGrid = new();
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
        long tcs = AeroCost.Start();

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
        _traceActive = Verbose && (_traceGridHash == gridHash);
        if (Verbose && !_traceActive && thrusters.Count >= 40 && _traceIndex < 0)
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
        var debugStates = DebugStatesByGrid.GetValue(thrusters, _ => new List<ThrusterDebugState>());
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
        AeroCost.TSetup.Stop(tcs); tcs = AeroCost.Start();
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
        AeroCost.TLoop.Stop(tcs); tcs = AeroCost.Start();
        // ── Attitude control (real differential thrust) ──
        // Differentially throttle thrusters via ThrusterOverrideData ECS tag to produce
        // counter-torque. No phantom forces — all torque comes from real thruster output.
        // With dampeners off, coupling passes through naturally — asymmetric thrust spins the ship.
        Vector3 localAngVel = WorldTransform.TransformDirectionInv(angularVelocity, gridWt);
        ApplyAttitudeViaOverrides(thrusters, comLocal, enableDampening, localAngVel,
            velocityLocal, playerMovement, playerRotation, targetAngVel);
        AeroCost.TAtt.Stop(tcs); tcs = AeroCost.Start();

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
        if (Verbose && _debugLogCooldown == 0)
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
        AeroCost.TWrite.Stop(tcs);
    }
}
