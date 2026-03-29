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
    private const float DampeningThreshold = 0.01f;

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

    // Track which thrusters have dampening overrides so we can clear them next frame
    private static readonly HashSet<int> _dampeningOverrideIndices = new();

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

        // Clear previous dampening overrides now that the main loop has read them
        foreach (int idx in _dampeningOverrideIndices)
        {
            if (idx < thrusters.Count)
            {
                if (GetComponentThrustOverride(thrusters[idx].ThrusterComponent) > 0f)
                    SetComponentThrustOverride(thrusters[idx].ThrusterComponent, 0f);
            }
        }
        _dampeningOverrideIndices.Clear();

        // ── Attitude control (direct torque) ──
        // Apply attitude torque directly via physics API. When dampeners are on,
        // counteracts offset coupling torque as feedforward (same pattern as aero torque).
        // With dampeners off, coupling passes through naturally — asymmetric thrust spins the ship.
        ApplyAttitudeTorqueDirect(thrusters, gridData, gridWt, angularVelocity, comLocal,
            enableDampening, _traceNetOffsetTorque, targetAngVel, aeroTorqueLocal);

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
    /// Attitude controller — applies damping/SAS/feedforward torque directly via physics API.
    /// Counteracts offset coupling torque when dampeners are on so asymmetric thrust
    /// doesn't spin the ship uncontrollably. Still computes per-thruster debug state.
    /// </summary>
    private static void ApplyAttitudeTorqueDirect(
        List<ThrusterInfo> thrusters,
        DEntityContext gridData,
        WorldTransform gridWt,
        Vector3 angularVelocity,
        Vector3 comLocal,
        bool enableDampening,
        Vector3 offsetCouplingTorque,
        Vector3 targetAngVel = default,
        Vector3 aeroTorqueLocal = default)
    {
        Vector3 localAngVel = WorldTransform.TransformDirectionInv(angularVelocity, gridWt);

        bool hasInput = targetAngVel.LengthSquared() > 0.0001f;
        bool hasDamp = enableDampening &&
            localAngVel.LengthSquared() >= DampeningThreshold * DampeningThreshold;
        bool hasAeroFF = aeroTorqueLocal.LengthSquared() > 1f;
        bool hasCoupling = enableDampening && offsetCouplingTorque.LengthSquared() > 1f;

        _traceNetAttitudeTorque = Vector3.Zero;

        if (!hasInput && !hasDamp && !hasAeroFF && !hasCoupling) return;

        // Desired torque: damping + SAS + aero feedforward + coupling feedforward
        Vector3 desiredTorque = Vector3.Zero;

        if (hasDamp)
            desiredTorque -= localAngVel * DampGain;

        if (hasInput)
            desiredTorque += targetAngVel;

        // Feedforward: counter aero torque so it doesn't build angular velocity
        if (hasAeroFF)
            desiredTorque -= aeroTorqueLocal;

        // Feedforward: counter offset thrust coupling torque when dampeners are on
        if (hasCoupling)
            desiredTorque -= offsetCouplingTorque;

        // Clamp to available torque capacity (sum of all thruster torque arms × max thrust)
        // For now, use a simple gain limit based on total grid thrust
        float totalTorqueCapacity = 0f;
        for (int i = 0; i < thrusters.Count; i++)
        {
            Vector3 r = thrusters[i].GridLocalPosition - comLocal;
            float arm = Vector3.Cross(r, thrusters[i].ThrustDirection).Length();
            totalTorqueCapacity += arm * thrusters[i].MaxPower * 0.5f; // 50% attitude budget
        }

        float desiredMag = desiredTorque.Length();
        if (desiredMag > 0.001f && totalTorqueCapacity > 0f)
        {
            // Scale desired torque to not exceed capacity (both in N·m;
            // the dt conversion to angular velocity happens inside ApplyDeltaVAndTorque)
            float scale = MathF.Min(1f, totalTorqueCapacity / desiredMag);
            Vector3 torqueToApply = desiredTorque * scale;

            PhysicsHack.ApplyDeltaVAndTorque(gridData, Vector3.Zero, torqueToApply, DT, gridWt.Orientation);
            _traceNetAttitudeTorque = torqueToApply;
        }

        // Record debug state for all thrusters (for visualization)
        for (int i = 0; i < thrusters.Count; i++)
        {
            var thruster = thrusters[i];
            Vector3 r = thruster.GridLocalPosition - comLocal;
            Vector3 cross = Vector3.Cross(r, thruster.ThrustDirection);
            float torqueLen = cross.Length();
            Vector3 torqueAxis = torqueLen > 0.001f ? cross / torqueLen : Vector3.Zero;

            float dampComponent = hasDamp ? -Vector3.Dot(localAngVel, torqueAxis) * DampGain : 0f;
            float inputComponent = hasInput ? -Vector3.Dot(targetAngVel, torqueAxis) : 0f;

            if (DebugStatesByGrid.TryGetValue(thrusters, out var attDebugStates) && i < attDebugStates.Count)
            {
                var dbg = attDebugStates[i];
                dbg.DTermComponent = dampComponent;
                dbg.PTermComponent = inputComponent;
                dbg.TorqueAxis = torqueAxis;
                dbg.TorqueArm = torqueLen;
                attDebugStates[i] = dbg;
            }

            // Phase 2 trace
            if (_traceActive && i == _traceIndex)
            {
                Log.Default?.Info($"[AERO-TRACE] ATT f={_traceFrame}" +
                    $" angVelW=({angularVelocity.X:F4},{angularVelocity.Y:F4},{angularVelocity.Z:F4})" +
                    $" angVelL=({localAngVel.X:F4},{localAngVel.Y:F4},{localAngVel.Z:F4})" +
                    $" targAV=({targetAngVel.X:F4},{targetAngVel.Y:F4},{targetAngVel.Z:F4})" +
                    $" hasIn={hasInput} hasDmp={hasDamp}" +
                    $" desiredTorque=({desiredTorque.X:F1},{desiredTorque.Y:F1},{desiredTorque.Z:F1})" +
                    $" aeroFF=({aeroTorqueLocal.X:F0},{aeroTorqueLocal.Y:F0},{aeroTorqueLocal.Z:F0})" +
                    $" cplFF=({offsetCouplingTorque.X:F0},{offsetCouplingTorque.Y:F0},{offsetCouplingTorque.Z:F0})" +
                    $" capacity={totalTorqueCapacity:F0}");
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
