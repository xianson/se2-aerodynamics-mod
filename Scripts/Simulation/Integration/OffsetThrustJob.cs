#pragma warning disable
using System;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.BlockOctrees;
using Keen.VRage.Core;

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
/// Offset thrust control: cancel vanilla's CoM-only linear impulse and
/// re-apply at thruster block position so offset thrusters create torque.
///
/// Pattern from SE1 BobSurvival/RealRCSThrusterLogic.cs lines 460-471:
///   grid.Physics.AddForce(-force, CoM);   // cancel linear
///   grid.Physics.AddForce(+force, blockPos); // re-apply at offset → torque
/// </summary>
public static class OffsetThrustJob
{
    private const float DT = 1f / 60f;
    private const float DampeningThreshold = 0.001f;

    private const float DampGain = 0.5f;      // D-term scaling (proportional zone up to ~2 rad/s)
    private const float IntegralGain = 0.3f;  // accumulation rate per frame
    private const float IntegralMax = 0.3f;   // max integral contribution (before attFrac scaling)

    private static int _executeLogCooldown;

    // ── Per-thruster trace logging ──
    private static int _traceGridHash;
    private static int _traceIndex = -1;
    private static int _traceFrame;
    private static int _traceLockedCount = int.MaxValue;
    private static int _traceSkipCount = 1; // skip first N grids before locking
    private static bool _traceActive;
    // Net torque accumulators (written by Execute offset loop, read for NET log)
    private static Vector3 _traceNetOffsetTorque;
    private static Vector3 _traceNetAttitudeTorque;

    // Per-grid: track which thrusters have attitude overrides so we can clear them next frame
    private static readonly Dictionary<List<ThrusterInfo>, HashSet<int>> _attitudeOverridesByGrid = new();

    /// <summary>Per-grid debug states, keyed by thruster list reference. Written by Execute, read by draw.</summary>
    public static readonly Dictionary<List<ThrusterInfo>, List<ThrusterDebugState>> DebugStatesByGrid = new();
    private static int _debugLogCooldown;

    /// <summary>Per-grid integral state for I-term. Same indexing as thruster cache.</summary>
    private static readonly Dictionary<List<ThrusterInfo>, List<float>> _integralState = new();

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
        WorldTransform gridWt,
        Vector3 angularVelocity,
        bool enableDampening,
        float mach = 0f,
        Vector3 velocityLocalHat = default,
        Vector3 velocityLocal = default,
        float mass = 0f,
        Vector3 targetAngVel = default,
        Vector3 aeroTorqueLocal = default)
    {
        if (thrusters.Count == 0) return;
        if (!PhysicsHack.ThrusterAccessAvailable) return;
        _executeLogCooldown = Math.Max(0, _executeLogCooldown - 1);

        // Auto-lock trace: skip first grid, lock onto second active grid
        int gridHash = thrusters.GetHashCode();
        _traceActive = (_traceGridHash == gridHash);
        if (!_traceActive && thrusters.Count > 2 && _traceIndex < 0)
        {
            // Only lock if at least one thruster is actually firing
            bool hasActive = false;
            for (int j = 0; j < thrusters.Count; j++)
            {
                if (GetComponentThrustOverride(thrusters[j].ThrusterComponent) > 0f ||
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

        for (int i = 0; i < thrusters.Count; i++)
        {
            var thruster = thrusters[i];

            // ── Read actual thrust state via ThrusterComponent ──
            float overridePower = GetComponentThrustOverride(thruster.ThrusterComponent);

            // Determine actual thrust force this frame (vanilla value)
            // ThrustOverride > 0 means override active (0-1 normalized).
            // Otherwise, check IsThrusting data tag via DEntityContext (may fail due to boxing).
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
                Log.Default?.Info($"[AERO] OffsetThrust active: [{i}] thrust={vanillaThrust:F0}N " +
                    $"dir=({thruster.ThrustDirection.X:F1},{thruster.ThrustDirection.Y:F1},{thruster.ThrustDirection.Z:F1}) " +
                    $"intake=({thruster.IntakeDirection.X:F1},{thruster.IntakeDirection.Y:F1},{thruster.IntakeDirection.Z:F1}) " +
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

            // ── Dual-force pattern ──
            // ThrustDirection is the exhaust direction; force pushes the ship opposite
            Vector3 forceDir = -thruster.ThrustDirection;
            Vector3 localForce = forceDir * scaledThrust;

            // Convert to world-space impulse
            Vector3 worldImpulse = WorldTransform.TransformDirection(localForce * DT, gridWt);

            // Vanilla impulse to cancel (applied at CoM by game in force direction)
            Vector3 vanillaLocalForce = forceDir * vanillaThrust;
            Vector3 vanillaImpulse = WorldTransform.TransformDirection(vanillaLocalForce * DT, gridWt);

            // NaN guard — don't write bad values to physics
            if (float.IsNaN(worldImpulse.X) || float.IsNaN(vanillaImpulse.X)) continue;

            // Block center in world space
            Vector3D blockWorldPos = WorldTransform.Transform((Vector3D)thruster.GridLocalPosition, gridWt);

            // Step 1: Cancel the vanilla linear impulse at CoM
            PhysicsHack.CancelLinearImpulse(gridData, vanillaImpulse);

            // Step 2: Re-apply scaled thrust at the thruster's offset position
            PhysicsHack.ApplyImpulseAt(gridData, worldImpulse, blockWorldPos, gridWt);

            // Accumulate net offset coupling torque
            Vector3 rOffset = thruster.GridLocalPosition - comLocal;
            Vector3 couplingTorqueI = Vector3.Cross(rOffset, localForce);
            _traceNetOffsetTorque += couplingTorqueI;

            // Phase 1 trace: every variable for tracked thruster
            if (_traceActive && i == _traceIndex)
            {
                bool isThrustingFlag = IsComponentThrusting(thruster.ThrusterComponent);
                Log.Default?.Info($"[AERO-TRACE] OFFSET f={_traceFrame}" +
                    $" pos=({thruster.GridLocalPosition.X:F2},{thruster.GridLocalPosition.Y:F2},{thruster.GridLocalPosition.Z:F2})" +
                    $" dir=({thruster.ThrustDirection.X:F2},{thruster.ThrustDirection.Y:F2},{thruster.ThrustDirection.Z:F2})" +
                    $" com=({comLocal.X:F2},{comLocal.Y:F2},{comLocal.Z:F2})" +
                    $" r=({rOffset.X:F2},{rOffset.Y:F2},{rOffset.Z:F2})" +
                    $" override={overridePower:F3} isThrusting={isThrustingFlag}" +
                    $" van={vanillaThrust:F0} profScale={profileScale:F3} scaled={scaledThrust:F0}" +
                    $" localF=({localForce.X:F0},{localForce.Y:F0},{localForce.Z:F0})" +
                    $" coupling=({couplingTorqueI.X:F0},{couplingTorqueI.Y:F0},{couplingTorqueI.Z:F0})");
            }
        }

        // Clear previous attitude overrides now that the main loop has read them
        if (_attitudeOverridesByGrid.TryGetValue(thrusters, out var prevOverrides))
        {
            foreach (int idx in prevOverrides)
            {
                if (idx < thrusters.Count)
                {
                    if (GetComponentThrustOverride(thrusters[idx].ThrusterComponent) > 0f)
                        SetComponentThrustOverride(thrusters[idx].ThrusterComponent, 0f);
                }
            }
            prevOverrides.Clear();
        }

        // ── Attitude control (real differential thrust) ──
        // Differentially throttle thrusters via SetComponentThrustOverride to produce
        // counter-torque. No phantom forces — all torque comes from real thruster output.
        // With dampeners off, coupling passes through naturally — asymmetric thrust spins the ship.
        Vector3 localAngVel = WorldTransform.TransformDirectionInv(angularVelocity, gridWt);
        ApplyAttitudeViaOverrides(thrusters, comLocal, enableDampening, localAngVel,
            velocityLocal, mass, _traceNetOffsetTorque, targetAngVel, aeroTorqueLocal);

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
    /// Two-phase thrust allocator via real SetComponentThrustOverride.
    /// Phase 1: Translational dampening — fire thrusters to stop linear velocity.
    /// Phase 2: Attitude — differential thrust for torque (coupling FF + damping + SAS + aero FF).
    /// All forces come from actual thruster output. Overrides take effect next frame.
    /// </summary>
    private static void ApplyAttitudeViaOverrides(
        List<ThrusterInfo> thrusters,
        Vector3 comLocal,
        bool enableDampening,
        Vector3 localAngVel,
        Vector3 velocityLocal,
        float mass,
        Vector3 offsetCouplingTorque,
        Vector3 targetAngVel = default,
        Vector3 aeroTorqueLocal = default)
    {
        int n = thrusters.Count;
        _traceNetAttitudeTorque = Vector3.Zero;

        // Get or create per-grid override tracking
        if (!_attitudeOverridesByGrid.TryGetValue(thrusters, out var overrideSet))
        {
            overrideSet = new HashSet<int>();
            _attitudeOverridesByGrid[thrusters] = overrideSet;
        }

        // Per-thruster throttle state: starts at 0, built up by Phase 1 and Phase 2
        Span<float> throttle = n <= 64 ? stackalloc float[n] : new float[n];
        // Force effectiveness: direction each thruster pushes (opposite exhaust)
        Span<Vector3> forceDir = n <= 64 ? stackalloc Vector3[n] : new Vector3[n];
        // Torque effectiveness: Cross(r, forceDir) — torque per Newton
        Span<Vector3> torqueEff = n <= 64 ? stackalloc Vector3[n] : new Vector3[n];

        for (int i = 0; i < n; i++)
        {
            var t = thrusters[i];
            forceDir[i] = -t.ThrustDirection;
            torqueEff[i] = Vector3.Cross(t.GridLocalPosition - comLocal, forceDir[i]);
        }

        float transCeiling = enableDampening ? 1f : 1f; // no cap for now; attitude uses remaining

        // ════════════════════════════════════════════════════════════════
        // PHASE 1: Translational dampening
        // Fire thrusters opposite to velocity to stop linear motion.
        // ════════════════════════════════════════════════════════════════
        // Phase 1: Translation is handled by vanilla dampeners.
        // Read vanilla's throttle state as a floor — Phase 2 only adds above this.
        bool hasLinearDamp = enableDampening && velocityLocal.LengthSquared() > 0.1f;
        Span<float> vanFloor = n <= 64 ? stackalloc float[n] : new float[n];
        for (int i = 0; i < n; i++)
        {
            float ovr = GetComponentThrustOverride(thrusters[i].ThrusterComponent);
            float vanThrottle = ovr > 0f ? ovr : (IsComponentThrusting(thrusters[i].ThrusterComponent) ? 1f : 0f);
            vanFloor[i] = vanThrottle;
            throttle[i] = vanThrottle; // start from vanilla's allocation
        }

        // ════════════════════════════════════════════════════════════════
        // PHASE 2: Attitude (rotational damping + coupling FF + SAS + aero FF)
        // Use remaining throttle capacity for torque production.
        // ════════════════════════════════════════════════════════════════
        bool hasInput = targetAngVel.LengthSquared() > 0.0001f;
        bool hasRotDamp = enableDampening &&
            localAngVel.LengthSquared() >= DampeningThreshold * DampeningThreshold;
        bool hasAeroFF = aeroTorqueLocal.LengthSquared() > 1f;
        bool hasCoupling = enableDampening && offsetCouplingTorque.LengthSquared() > 1f;

        // Compute coupling torque from Phase 1 allocation
        Vector3 phase1Torque = Vector3.Zero;
        for (int i = 0; i < n; i++)
        {
            if (throttle[i] > 0.001f)
                phase1Torque += torqueEff[i] * (throttle[i] * thrusters[i].MaxPower);
        }

        // No coupling feedforward — with real differential thrust, counteracting
        // coupling creates uncontrolled force side-effects that push the ship around.
        // Instead, let coupling happen naturally and rely on rotational damping.
        //
        // Angular velocity commands (damping, SAS) are in rad/s. Scale by mass to
        // approximate torque demand: torque ≈ mass * charLength² * angVel / dt.
        // Using mass * 100 as a rough proxy for I/dt.
        float angVelToTorque = mass * 100f;
        Vector3 desiredTorque = Vector3.Zero;
        if (hasRotDamp) desiredTorque -= localAngVel * DampGain * angVelToTorque;
        if (hasInput) desiredTorque += targetAngVel * angVelToTorque;
        if (hasAeroFF) desiredTorque -= aeroTorqueLocal;

        float desiredMag = desiredTorque.Length();
        if (desiredMag > 0.001f)
        {
            Vector3 desiredDir = desiredTorque / desiredMag;

            // Phase 2 only ADDS throttle — never reduces Phase 1 translation allocation
            float maxAchievable = 0f;
            Span<float> tProj = n <= 64 ? stackalloc float[n] : new float[n];
            for (int i = 0; i < n; i++)
            {
                float p = Vector3.Dot(torqueEff[i], desiredDir);
                tProj[i] = p > 0f ? p : 0f; // only positive contributors
                if (tProj[i] > 0f)
                {
                    float available = 1f - throttle[i]; // headroom above Phase 1
                    if (available > 0.001f)
                        maxAchievable += tProj[i] * available * thrusters[i].MaxPower;
                }
            }

            if (maxAchievable > 1f)
            {
                float tScale = MathF.Min(desiredMag / maxAchievable, 1f);
                for (int i = 0; i < n; i++)
                {
                    if (tProj[i] <= 0f) continue;
                    float available = 1f - throttle[i];
                    throttle[i] += tScale * available;
                    throttle[i] = MathF.Min(throttle[i], 1f);
                }
            }
        }

        // ════════════════════════════════════════════════════════════════
        // PHASE 3: Force nulling
        // Phase 2 attitude thrusters create net linear force as a side-effect.
        // Fire additional thrusters to cancel that force, like real RCS pairs.
        // Uses force effectiveness (not torque) to find opposing thrusters.
        // ════════════════════════════════════════════════════════════════
        Vector3 attForce = Vector3.Zero;
        for (int i = 0; i < n; i++)
        {
            float delta = throttle[i] - vanFloor[i];
            if (delta > 0.001f)
                attForce += forceDir[i] * (delta * thrusters[i].MaxPower);
        }

        float attForceMag = attForce.Length();
        if (attForceMag > 100f) // only bother if > 100 N
        {
            Vector3 cancelDir = -attForce / attForceMag;

            float maxCancelForce = 0f;
            Span<float> fProj = n <= 64 ? stackalloc float[n] : new float[n];
            for (int i = 0; i < n; i++)
            {
                float p = Vector3.Dot(forceDir[i], cancelDir);
                fProj[i] = p > 0f ? p : 0f;
                if (fProj[i] > 0f)
                {
                    float available = 1f - throttle[i];
                    if (available > 0.001f)
                        maxCancelForce += fProj[i] * available * thrusters[i].MaxPower;
                }
            }

            if (maxCancelForce > 1f)
            {
                float fScale = MathF.Min(attForceMag / maxCancelForce, 1f);
                for (int i = 0; i < n; i++)
                {
                    if (fProj[i] <= 0f) continue;
                    float available = 1f - throttle[i];
                    throttle[i] += fScale * available;
                    throttle[i] = MathF.Min(throttle[i], 1f);
                }
            }

            if (_traceActive)
            {
                int p3candidates = 0;
                for (int i = 0; i < n; i++) if (fProj[i] > 0f) p3candidates++;
                Log.Default?.Info($"[AERO-TRACE] P3 f={_traceFrame}" +
                    $" attF=({attForce.X:F0},{attForce.Y:F0},{attForce.Z:F0})" +
                    $" cancelDir=({cancelDir.X:F2},{cancelDir.Y:F2},{cancelDir.Z:F2})" +
                    $" maxCancel={maxCancelForce:F0} candidates={p3candidates}");
            }
        }

        // ════════════════════════════════════════════════════════════════
        // Apply overrides ONLY where allocation changed throttle above vanilla floor.
        // Leave vanilla-controlled thrusters alone so dampening isn't disrupted.
        // ════════════════════════════════════════════════════════════════
        Vector3 producedTorque = Vector3.Zero;
        for (int i = 0; i < n; i++)
        {
            float delta = throttle[i] - vanFloor[i];
            if (delta < 0.001f) continue; // no change or reduced — skip

            SetComponentThrustOverride(thrusters[i].ThrusterComponent, throttle[i]);
            overrideSet.Add(i);

            // Only count the DELTA torque from our additions, not vanilla's contribution
            producedTorque += torqueEff[i] * (delta * thrusters[i].MaxPower);
        }
        _traceNetAttitudeTorque = producedTorque;

        // Record debug state for all thrusters
        for (int i = 0; i < n; i++)
        {
            float torqueLen = torqueEff[i].Length();
            Vector3 torqueAxis = torqueLen > 0.001f ? torqueEff[i] / torqueLen : Vector3.Zero;

            float dampComponent = hasRotDamp ? -Vector3.Dot(localAngVel, torqueAxis) * DampGain : 0f;
            float inputComponent = hasInput ? -Vector3.Dot(targetAngVel, torqueAxis) : 0f;

            if (DebugStatesByGrid.TryGetValue(thrusters, out var attDebugStates) && i < attDebugStates.Count)
            {
                var dbg = attDebugStates[i];
                dbg.DTermComponent = dampComponent;
                dbg.PTermComponent = inputComponent;
                dbg.TorqueAxis = torqueAxis;
                dbg.TorqueArm = torqueLen;
                dbg.AttitudeOverride = throttle[i];
                attDebugStates[i] = dbg;
            }

            // Phase 2 trace
            if (_traceActive && i == _traceIndex)
            {
                // Compute Phase 1 force total for logging
                Vector3 p1Force = Vector3.Zero;
                int p1Count = 0;
                for (int j = 0; j < n; j++)
                {
                    // Phase 1 throttle is anything set before Phase 2
                    // Can't separate easily, so just log total override force
                }
                Log.Default?.Info($"[AERO-TRACE] ATT f={_traceFrame}" +
                    $" angVelL=({localAngVel.X:F4},{localAngVel.Y:F4},{localAngVel.Z:F4})" +
                    $" velL=({velocityLocal.X:F1},{velocityLocal.Y:F1},{velocityLocal.Z:F1})" +
                    $" mass={mass:F0}" +
                    $" targAV=({targetAngVel.X:F4},{targetAngVel.Y:F4},{targetAngVel.Z:F4})" +
                    $" hasIn={hasInput} hasDmp={hasRotDamp} hasLinDmp={hasLinearDamp}" +
                    $" desiredTorque=({desiredTorque.X:F0},{desiredTorque.Y:F0},{desiredTorque.Z:F0})" +
                    $" produced=({producedTorque.X:F0},{producedTorque.Y:F0},{producedTorque.Z:F0})" +
                    $" cplFF=({offsetCouplingTorque.X:F0},{offsetCouplingTorque.Y:F0},{offsetCouplingTorque.Z:F0})" +
                    $" p1cpl=({phase1Torque.X:F0},{phase1Torque.Y:F0},{phase1Torque.Z:F0})" +
                    $" overrides={overrideSet.Count}");

                // Log per-thruster Phase 1 allocation for tracked thruster
                var tt = thrusters[_traceIndex];
                Log.Default?.Info($"[AERO-TRACE] P1DBG f={_traceFrame}" +
                    $" thr[{_traceIndex}] throttle={throttle[_traceIndex]:F3}" +
                    $" fd=({forceDir[_traceIndex].X:F2},{forceDir[_traceIndex].Y:F2},{forceDir[_traceIndex].Z:F2})" +
                    $" maxPow={tt.MaxPower:F0}");

                // Log force from our additions (delta above vanilla) — after force nulling
                Vector3 totalAttForce = Vector3.Zero;
                int overrideCount = 0;
                for (int j = 0; j < n; j++)
                {
                    float d = throttle[j] - vanFloor[j];
                    if (d > 0.001f)
                    {
                        totalAttForce += forceDir[j] * (d * thrusters[j].MaxPower);
                        overrideCount++;
                    }
                }
                Log.Default?.Info($"[AERO-TRACE] FORCE f={_traceFrame}" +
                    $" preNull=({attForce.X:F0},{attForce.Y:F0},{attForce.Z:F0}) |pre|={attForceMag:F0}" +
                    $" postNull=({totalAttForce.X:F0},{totalAttForce.Y:F0},{totalAttForce.Z:F0}) |post|={totalAttForce.Length():F0}" +
                    $" nOverrides={overrideCount}");
            }
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
    private static System.Reflection.PropertyInfo _blockDefProp;
    private static System.Reflection.PropertyInfo _blockGuidProp;

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
                            if (_thrusterMaxPowerProp != null)
                                maxPower = (float)_thrusterMaxPowerProp.GetValue(def);
                            if (_thrusterDirProp != null)
                            {
                                var rawDir = _thrusterDirProp.GetValue(def);
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
    /// Read block definition GUID via reflection (same pattern as BlockComponentFactory).
    /// </summary>
    private static Guid? GetBlockDefinitionGuid(CubeBlockComponent block)
    {
        _blockDefProp ??= typeof(CubeBlockComponent).GetProperty("Definition",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        var def = _blockDefProp?.GetValue(block);
        if (def == null) return null;

        _blockGuidProp ??= def.GetType().GetProperty("Guid",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        if (_blockGuidProp != null)
            return (Guid)_blockGuidProp.GetValue(def);

        return null;
    }

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
    private static System.Reflection.PropertyInfo _thrusterMaxPowerProp;
    private static System.Reflection.PropertyInfo _thrusterDirProp;
    private static System.Reflection.PropertyInfo _thrusterClassProp;
    private static System.Reflection.PropertyInfo _thrustOverrideProp; // ThrusterComponent.ThrustOverride (float, get/set)
    private static System.Reflection.MethodInfo _hasIsThrustingMethod; // Component.HasData<IsThrusting>()
    private static bool _thrusterReflectionResolved;

    private static void EnsureThrusterReflectionResolved()
    {
        if (_thrusterReflectionResolved) return;
        _thrusterReflectionResolved = true;

        try
        {
            _thrusterCompType = Type.GetType(
                "Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement.ThrusterComponent, Game2.Simulation",
                throwOnError: false);

            if (_thrusterCompType != null)
            {
                // ThrusterComponent has private field _definition (ThrusterDefinition)
                _thrusterDefField = _thrusterCompType.GetField("_definition",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (_thrusterDefField != null)
                {
                    var defType = _thrusterDefField.FieldType;
                    _thrusterMaxPowerProp = defType.GetProperty("ThrustPower",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    _thrusterDirProp = defType.GetProperty("ThrustDirection",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    _thrusterClassProp = defType.GetProperty("ThrustClass",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                }

                // ThrusterComponent.ThrustOverride (public float property)
                _thrustOverrideProp = _thrusterCompType.GetProperty("ThrustOverride",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                // Component.HasData<IsThrusting>() — protected, call via reflection on the
                // Component class instance (no DEntityContext boxing needed).
                var isThrustingType = Type.GetType(
                    "Keen.Game2.Simulation.WorldObjects.Movement.IsThrusting, Game2.Simulation",
                    throwOnError: false);
                if (isThrustingType != null)
                {
                    // Find the protected HasData<T>() on Component base class
                    foreach (var m in typeof(Component).GetMethods(
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                    {
                        if (m.Name == "HasData" && m.IsGenericMethodDefinition
                            && m.GetParameters().Length == 0)
                        {
                            _hasIsThrustingMethod = m.MakeGenericMethod(isThrustingType);
                            break;
                        }
                    }
                }

                Log.Default?.Info($"[AERO] ThrusterComponent resolved: type={_thrusterCompType != null} def={_thrusterDefField != null} maxPower={_thrusterMaxPowerProp != null} dir={_thrusterDirProp != null} override={_thrustOverrideProp != null} isThrusting={_hasIsThrustingMethod != null}");
            }
            else
            {
                Log.Default?.Info("[AERO] ThrusterComponent type not found in Game2.Simulation");
            }
        }
        catch (Exception ex)
        {
            Log.Default?.Info($"[AERO] ThrusterComponent resolution failed: {ex.Message}");
        }
    }

    /// <summary>Check if thruster is actively firing via Component.HasData&lt;IsThrusting&gt;().</summary>
    private static bool IsComponentThrusting(Component thrusterComp)
    {
        if (_hasIsThrustingMethod == null || thrusterComp == null) return false;
        try { return (bool)_hasIsThrustingMethod.Invoke(thrusterComp, null); }
        catch { return false; }
    }

    /// <summary>Read ThrustOverride from ThrusterComponent (0 = no override).</summary>
    private static float GetComponentThrustOverride(Component thrusterComp)
    {
        if (_thrustOverrideProp == null || thrusterComp == null) return 0f;
        try { return (float)_thrustOverrideProp.GetValue(thrusterComp); }
        catch { return 0f; }
    }

    /// <summary>Set ThrustOverride on ThrusterComponent.</summary>
    private static void SetComponentThrustOverride(Component thrusterComp, float value)
    {
        if (_thrustOverrideProp == null || thrusterComp == null) return;
        if (float.IsNaN(value) || float.IsInfinity(value)) value = 0f;
        try { _thrustOverrideProp.SetValue(thrusterComp, value); }
        catch { }
    }

    /// <summary>Public wrapper to force a thrust override from outside (e.g. test harness).</summary>
    public static void ForceOverride(Component thrusterComp, float value)
        => SetComponentThrustOverride(thrusterComp, value);

    /// <summary>Read ThrustClass (StringId) from ThrusterDefinition on a ThrusterComponent.</summary>
    private static string GetThrustClass(Component thrusterComp)
    {
        if (_thrusterDefField == null || _thrusterClassProp == null) return null;
        try
        {
            var def = _thrusterDefField.GetValue(thrusterComp);
            if (def == null) return null;
            var tc = _thrusterClassProp.GetValue(def);
            return tc?.ToString();
        }
        catch { return null; }
    }

    // ═══════════════════════════════════════════════════════════════
    // Gyroscope detection and control
    // ═══════════════════════════════════════════════════════════════

    private static Type _gyroCompType;
    private static System.Reflection.FieldInfo _gyroDefField;
    private static System.Reflection.PropertyInfo _gyroMaxTorqueProp;
    private static Type _powerableBlockType;
    private static System.Reflection.PropertyInfo _enabledProp; // PowerableBlockComponent.Enabled
    private static bool _gyroReflectionResolved;

    private static void EnsureGyroReflectionResolved()
    {
        if (_gyroReflectionResolved) return;
        _gyroReflectionResolved = true;

        try
        {
            _gyroCompType = Type.GetType(
                "Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement.GyroscopeComponent, Game2.Simulation",
                throwOnError: false);

            if (_gyroCompType != null)
            {
                _gyroDefField = _gyroCompType.GetField("_definition",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (_gyroDefField != null)
                {
                    var defType = _gyroDefField.FieldType;
                    _gyroMaxTorqueProp = defType.GetProperty("MaxTorque",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                }
            }

            // PowerableBlockComponent.Enabled — used to toggle gyros (and any powerable block)
            _powerableBlockType = Type.GetType(
                "Keen.Game2.Simulation.WorldObjects.CubeBlocks.PowerableBlockComponent, Game2.Simulation",
                throwOnError: false);
            if (_powerableBlockType != null)
            {
                _enabledProp = _powerableBlockType.GetProperty("Enabled",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            }

            Log.Default?.Info($"[AERO] GyroscopeComponent resolved: type={_gyroCompType != null} " +
                $"def={_gyroDefField != null} maxTorque={_gyroMaxTorqueProp != null} " +
                $"powerable={_powerableBlockType != null} enabled={_enabledProp != null}");
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
            if (_gyroDefField != null && _gyroMaxTorqueProp != null)
            {
                try
                {
                    var def = _gyroDefField.GetValue(gyroComp);
                    if (def != null)
                        maxTorque = (float)_gyroMaxTorqueProp.GetValue(def);
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
        if (_enabledProp == null) return;
        int toggled = 0;
        for (int i = 0; i < gyros.Count; i++)
        {
            if (gyros[i].BlockComponent == null) continue;
            try
            {
                bool current = (bool)_enabledProp.GetValue(gyros[i].BlockComponent);
                if (current != enabled)
                {
                    _enabledProp.SetValue(gyros[i].BlockComponent, enabled);
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
        if (_enabledProp == null || gyros.Count == 0) return true;
        try { return (bool)_enabledProp.GetValue(gyros[0].BlockComponent); }
        catch { return true; }
    }
}
