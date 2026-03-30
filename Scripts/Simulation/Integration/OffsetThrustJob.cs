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

    // (Per-thruster blending manages its own overrides — no tracking needed)

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
        Vector3 gravityLocal = default,
        float mass = 0f,
        Vector3 targetAngVel = default,
        Vector3 aeroTorqueLocal = default,
        bool skipOffsetLoop = false)
    {
        if (thrusters.Count == 0) return;
        if (!PhysicsHack.ThrusterAccessAvailable) return;
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

        // Per-thruster blending sets all overrides every frame — no clearing needed
        HoverCouplingTorque = hoverCoupling;
        NetOffsetCouplingTorque = _traceNetOffsetTorque;

        AttitudeOnly:
        // ── Attitude control (real differential thrust) ──
        // Differentially throttle thrusters via SetComponentThrustOverride to produce
        // counter-torque. No phantom forces — all torque comes from real thruster output.
        // With dampeners off, coupling passes through naturally — asymmetric thrust spins the ship.
        Vector3 localAngVel = WorldTransform.TransformDirectionInv(angularVelocity, gridWt);
        ApplyAttitudeViaOverrides(thrusters, comLocal, enableDampening, localAngVel,
            velocityLocal, gravityLocal, mass, _traceNetOffsetTorque, targetAngVel, aeroTorqueLocal);

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
    /// Per-thruster independent blending (SE1 RealRCS pattern).
    /// Each thruster independently computes movement (linear dampening + gravity) and
    /// rotation (angular dampening + SAS + aero FF), then blends them using AttitudeFraction.
    /// No centralized allocator — inherently force-balanced because translation and rotation
    /// share each thruster's budget.
    /// </summary>
    private static void ApplyAttitudeViaOverrides(
        List<ThrusterInfo> thrusters,
        Vector3 comLocal,
        bool enableDampening,
        Vector3 localAngVel,
        Vector3 velocityLocal,
        Vector3 gravityLocal,
        float mass,
        Vector3 offsetCouplingTorque,
        Vector3 targetAngVel = default,
        Vector3 aeroTorqueLocal = default)
    {
        int n = thrusters.Count;
        _traceNetAttitudeTorque = Vector3.Zero;

        bool hasInput = targetAngVel.LengthSquared() > 0.0001f;
        bool hasAngVel = localAngVel.LengthSquared() >= DampeningThreshold * DampeningThreshold;
        bool hasLinVel = velocityLocal.LengthSquared() > 0.25f; // > 0.5 m/s
        bool hasAeroFF = aeroTorqueLocal.LengthSquared() > 1f;
        bool hasGravity = gravityLocal.LengthSquared() > 0.1f;

        // If dampeners off and no SAS input, nothing to do
        if (!enableDampening && !hasInput) return;

        // Gravity compensation: constant force to hover (vanilla pattern: -gravity * mass)
        // Normalized per-thruster: project onto forceDir, scale by (mass / MaxPower)
        // so a thruster aligned with anti-gravity gets throttle ≈ gravityForce / totalThrust

        const float AngDampGain = 0.5f;  // proportional up to ~2 rad/s
        const float SasGain = 0.5f;      // proportional up to ~2 rad/s command

        // Precompute total gravity-opposing thrust capacity so each thruster gets
        // its share of the load. Without this, each thruster computes
        // (mass * g / MaxPower) which is ~10x too large when 25 thrusters share the load.
        float totalGravCapacity = 0f;
        if (hasGravity)
        {
            for (int j = 0; j < n; j++)
            {
                float align = Vector3.Dot(-gravityLocal, -thrusters[j].ThrustDirection);
                if (align > 0f)
                    totalGravCapacity += align * thrusters[j].MaxPower;
            }
        }
        // Weight force magnitude along gravity direction
        float weightForce = hasGravity ? gravityLocal.Length() * mass : 0f;

        int overrideCount = 0;
        Vector3 producedTorque = Vector3.Zero;

        for (int i = 0; i < n; i++)
        {
            var t = thrusters[i];
            Vector3 forceDir = -t.ThrustDirection; // direction this thruster pushes
            Vector3 r = t.GridLocalPosition - comLocal;
            Vector3 torqueVec = Vector3.Cross(r, forceDir);
            float arm = torqueVec.Length();
            Vector3 torqueAxis = arm > 0.001f ? torqueVec / arm : Vector3.Zero;

            // ── Baseline: gravity compensation + linear dampening ──
            // Each thruster gets its proportional share of the weight based on
            // alignment with anti-gravity, distributed across total capacity.
            float baseline = 0f;
            if (enableDampening)
            {
                // Gravity compensation: share of weight proportional to alignment
                if (hasGravity && totalGravCapacity > 1f)
                {
                    float align = Vector3.Dot(-gravityLocal, forceDir);
                    float gravComp = align > 0f ? (align * weightForce) / totalGravCapacity : 0f;
                    baseline += Math.Clamp(gravComp, 0f, 1f);
                }

                // Linear dampening: fire if this thruster opposes velocity
                if (hasLinVel)
                    baseline += Math.Clamp(Vector3.Dot(-velocityLocal, forceDir), -1f, 1f);
            }

            // ── Rotation component: oppose angular velocity + SAS + aero FF ──
            // Uses normalized torqueAxis with explicit gain scaling for proportional
            // response. Without scaling, Dot(angVel, torqueVec) always saturates at ±1
            // giving bang-bang control that causes oscillation.
            float rotation = 0f;
            if (arm > 0.001f)
            {
                // Angular dampening: project angVel onto torque axis, scale for proportional response
                if (enableDampening && hasAngVel)
                    rotation = Vector3.Dot(-localAngVel, torqueAxis) * AngDampGain;

                // SAS hold input: project targetAngVel onto torque axis
                if (hasInput)
                    rotation += Vector3.Dot(targetAngVel, torqueAxis) * SasGain;

                // Aero torque feedforward (N·m, normalize by torque capacity)
                if (hasAeroFF)
                {
                    float torqueCapacity = arm * t.MaxPower;
                    rotation += Vector3.Dot(-aeroTorqueLocal, torqueAxis) / torqueCapacity;
                }

                rotation = Math.Clamp(rotation, -1f, 1f);
            }

            // ── Additive: baseline + rotation adjustment ──
            // Gravity/dampening provides the base throttle. Rotation is added on top,
            // scaled by AttitudeFraction. This ensures gravity hover is maintained while
            // attitude correction is applied differentially.
            // CRITICAL: we control ALL thrusters (never return to vanilla) to prevent
            // vanilla from firing "wrong" thrusters at full power and overwhelming our
            // attitude correction with coupling torque.
            float limiter = t.AttitudeFraction; // 0..1, default 0.5
            float throttle = Math.Clamp(baseline + rotation * limiter, 0f, 1f);

            // Set override on ALL thrusters. Use minimum 0.001 for thrusters we want off
            // to prevent vanilla from independently firing them (override=0 means vanilla control).
            float overrideValue = throttle > 0.001f ? throttle : 0.001f;
            SetComponentThrustOverride(t.ThrusterComponent, overrideValue);
            overrideCount++;
            if (throttle > 0.001f)
                producedTorque += torqueVec * (throttle * t.MaxPower);

            // Record debug state
            if (DebugStatesByGrid.TryGetValue(thrusters, out var attDebugStates) && i < attDebugStates.Count)
            {
                var dbg = attDebugStates[i];
                dbg.DTermComponent = hasAngVel ? Vector3.Dot(-localAngVel, torqueAxis) * AngDampGain : 0f;
                dbg.PTermComponent = hasInput ? Vector3.Dot(targetAngVel, torqueAxis) * SasGain : 0f;
                dbg.TorqueAxis = torqueAxis;
                dbg.TorqueArm = arm;
                dbg.AttitudeOverride = throttle;
                attDebugStates[i] = dbg;
            }
        }
        _traceNetAttitudeTorque = producedTorque;

        // Trace logging for tracked thruster
        if (_traceActive && _traceIndex >= 0 && _traceIndex < n)
        {
            var tt = thrusters[_traceIndex];
            Vector3 fd = -tt.ThrustDirection;
            Vector3 tv = Vector3.Cross(tt.GridLocalPosition - comLocal, fd);
            float ta = tv.Length();
            Vector3 tax = ta > 0.001f ? tv / ta : Vector3.Zero;

            float damp = hasLinVel ? Math.Clamp(Vector3.Dot(-velocityLocal, fd), -1f, 1f) : 0f;
            float gravAlign = Vector3.Dot(-gravityLocal, fd);
            float grav = (hasGravity && totalGravCapacity > 1f && gravAlign > 0f)
                ? Math.Clamp(gravAlign * weightForce / totalGravCapacity, 0f, 1f) : 0f;
            float rot = hasAngVel ? Vector3.Dot(-localAngVel, tax) * AngDampGain : 0f;
            float sas = hasInput ? Vector3.Dot(targetAngVel, tax) * SasGain : 0f;
            float trBase = grav + damp;
            float trThrottle = Math.Clamp(trBase + rot * tt.AttitudeFraction, 0f, 1f);

            Log.Default?.Info($"[AERO-TRACE] ATT f={_traceFrame}" +
                $" velL=({velocityLocal.X:F1},{velocityLocal.Y:F1},{velocityLocal.Z:F1})" +
                $" angVelL=({localAngVel.X:F4},{localAngVel.Y:F4},{localAngVel.Z:F4})" +
                $" gravL=({gravityLocal.X:F1},{gravityLocal.Y:F1},{gravityLocal.Z:F1})" +
                $" targAV=({targetAngVel.X:F3},{targetAngVel.Y:F3},{targetAngVel.Z:F3})" +
                $" thr[{_traceIndex}] damp={damp:F3} grav={grav:F3} rot={rot:F3} sas={sas:F3}" +
                $" base={trBase:F3} thr={trThrottle:F3}" +
                $" lim={tt.AttitudeFraction:F2} overrides={overrideCount}");
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
