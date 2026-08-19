#pragma warning disable
using System;
using System.Reflection;
using Keen.Game2.Simulation.GameSystems.Physicss;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.Game2.Simulation.WorldObjects.Shared.Movement;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.GameSystems.Gravity;
using Keen.VRage.Core.Game.GameSystems.Queries;
using Keen.VRage.Physics;
using Keen.VRage.Physics.Data;
using Keen.VRage.Physics.Queries;

namespace AeroMod;

/// <summary>
/// Direct physics/simulation access for the aero mod.
///
/// This used to be 2000 lines of reflection, opening with "That assembly is not whitelisted for
/// mod scripts". Since SE2 2.4.0.77 added typeof(IPhysics) to GameApp.SetupScripting's
/// AllowedAssemblies, Keen.VRage.Physics.* IS whitelisted, and the Game2.Simulation types this
/// also reached for were whitelisted all along via the UpdateTime seed. Everything here is now
/// a direct typed call through the public generic DEntityContext API and the engine's own
/// RigidBodyDataFunctions helpers.
///
/// The three members the engine genuinely does not expose live in <see cref="EngineOverrides"/>.
/// Prefer <see cref="AeroPhysics"/> for applying aerodynamic forces.
/// </summary>
public static class PhysicsHack
{
    private static bool _initialized;
    private static bool _available;
    private static bool _groundSystemInitialized;
    private static IPhysics _physics;
    private static bool _gravityFixed;

    public static bool Available
    {
        get
        {
            if (!_initialized)
                Initialize();
            return _available;
        }
    }

    /// <summary>
    /// DEntityContext.TryGet throws NullReferenceException from deep inside
    /// Scene.TryGetDataPointer when the entity has been detached or its scene torn down --
    /// it is only "try" with respect to the component being absent, not to the entity being
    /// dead. The old reflection path hid this behind a blanket catch; these wrappers keep that
    /// tolerance now that the calls are direct.
    /// </summary>
    private static bool SafeTryGet<T>(DEntityContext data, out T value) where T : unmanaged
    {
        try { return data.TryGet(out value); }
        catch { value = default; return false; }
    }

    private static bool SafeHas<T>(DEntityContext data) where T : unmanaged
    {
        try { return data.Has<T>(); }
        catch { return false; }
    }

    private static bool SafeSet<T>(DEntityContext data, T value) where T : unmanaged
    {
        try { data.Set(value); return true; }
        catch { return false; }
    }

    private static bool SafeTryRemove<T>(DEntityContext data) where T : unmanaged
    {
        try { return data.TryRemove<T>(); }
        catch { return false; }
    }

    /// <summary>Write pointer access, tolerant of a dead entity. Returns false if unavailable.</summary>
    private static bool SafeGetWorldTransform(DEntityContext data, out WorldTransform wt)
    {
        try { wt = data.GetWorldTransform(); return true; }
        catch { wt = default; return false; }
    }

    /// <summary>
    /// TryGetWritePtr wrapped so a detached entity yields a null ref rather than throwing.
    /// </summary>
    private static ref T TryWrite<T>(DEntityContext data) where T : unmanaged
    {
        try { return ref data.TryGetWritePtr<T>(); }
        catch { return ref Unsafe.NullRef<T>(); }
    }

    private static void Initialize()
    {
        // Nothing to resolve any more: every type this class touches is directly referenced.
        // Availability is simply whether the entity actually carries RigidBodyData, which each
        // method checks for itself.
        _initialized = true;
        _available = true;
    }

    /// <summary>Read gyro MaxTorque from an entity's MaxTorqueData.</summary>
    public static float TryGetGyroMaxTorque(DEntityContext data)
    {
        return SafeTryGet<MaxTorqueData>(data, out var mt) ? mt.MaxTorque : -1f;
    }

    /// <summary>Read linear and angular velocity from an entity's RigidBodyData.</summary>
    public static bool TryGetVelocity(DEntityContext data, out Vector3 linear, out Vector3 angular)
    {
        if (!SafeTryGet<RigidBodyData>(data, out var rb))
        {
            linear = Vector3.Zero;
            angular = Vector3.Zero;
            return false;
        }
        linear = rb.LinearVelocity;
        angular = rb.AngularVelocity;
        return true;
    }

    /// <summary>Raise the session speed cap. See EngineOverrides (reflection holdout).</summary>
    public static void UncapSpeed(object velocityLimitProvider, float newLimit = 1000f)
    {
        EngineOverrides.UncapSpeed(velocityLimitProvider, newLimit);
    }

    /// <summary>
    /// Set GravityMultiplier and MaximumSpeedLinear on the physics session configuration.
    /// The lookup is now a direct DefinitionManager.GetConfiguration&lt;T&gt;() call; only the
    /// private property setters still need reflection (see EngineOverrides).
    /// </summary>
    public static void TryFixGravity(float targetGravity = 1f, float targetSpeed = 1000f)
    {
        if (_gravityFixed) return;
        _gravityFixed = true;

        try
        {
            var config = DefinitionManager.Instance?.GetConfiguration<PhysicsSessionConfiguration>();
            if (config == null)
            {
                Log.Default?.Info("[AERO] TryFixGravity: PhysicsSessionConfiguration unavailable");
                return;
            }

            // WHY THIS REFLECTION STILL EXISTS.
            // The mod ships Content/System/Configurations/PhysicsSessionConfiguration.def, which
            // is the correct data-driven way to set these -- WorldSessionComponent builds the
            // VelocityLimitProvider straight from _definition.Physics.MaximumSpeedLinear, so a
            // working .def would make both this and EngineOverrides.UncapSpeed unnecessary.
            //
            // It does not currently apply. The mod has no contentcache.vrb ("Warning: Mounted
            // path ... did not contain contentcache.vrb, assets might not be properly
            // recognized"), so its .def content is never mounted, and this probe reads the
            // world's values rather than the mod's 1000/1/1000:
            //     CONFIGPROBE before override: MaximumSpeedLinear=300 GravityMultiplier=2
            // Building that cache needs the content builder, which is blocked on the stale Mod
            // SDK (see tools/SE2-UPDATE-RUNBOOK.md). Delete this once the cache builds.
            Log.Default?.Info($"[AERO] CONFIGPROBE before override: MaximumSpeedLinear={config.MaximumSpeedLinear} GravityMultiplier={config.GravityMultiplier} MaximumCharacterSpeedLinear={config.MaximumCharacterSpeedLinear}");

            bool speedOk = EngineOverrides.TrySetConfigProperty(config, "MaximumSpeedLinear", targetSpeed);
            bool gravOk = EngineOverrides.TrySetConfigProperty(config, "GravityMultiplier", targetGravity);
            Log.Default?.Info($"[AERO] Physics config: MaximumSpeedLinear={targetSpeed} ({speedOk}), GravityMultiplier={targetGravity} ({gravOk})");
        }
        catch (Exception ex)
        {
            Log.Default?.Info($"[AERO] TryFixGravity failed: {ex.Message}");
        }
    }



    /// <summary>Read mass and centre of mass from RigidBodyMassProperties.</summary>
    public static bool TryGetMassProperties(DEntityContext data, out float mass, out Vector3 centerOfMass)
    {
        mass = 0f;
        centerOfMass = Vector3.Zero;
        if (!SafeTryGet<RigidBodyMassProperties>(data, out var mp))
            return false;
        mass = mp.InvMass > 1e-10f ? 1f / mp.InvMass : 0f;
        centerOfMass = mp.CenterOfMass;
        return true;
    }

    /// <summary>Read inverse inertia tensor and principal-axis rotation (diagnostics).</summary>
    public static bool TryGetInertiaData(DEntityContext data,
        out Vector3 invInertiaTensor, out Quaternion majorAxisRot)
    {
        invInertiaTensor = Vector3.Zero;
        majorAxisRot = Quaternion.Identity;
        if (!SafeTryGet<RigidBodyMassProperties>(data, out var mp))
            return false;
        invInertiaTensor = mp.InvInertiaTensor;
        majorAxisRot = mp.InertiaMajorAxisRotation;
        return true;
    }

    /// <summary>
    /// Zero out the grid's gyroscope MaxTorque so game gyros don't compete.
    /// Uses Set&lt;MaxTorqueData&gt; via reflection.
    /// </summary>
    private static float _savedMaxTorque = -1f;

    public static bool TryRestoreGyroTorque(DEntityContext data)
    {
        if (_savedMaxTorque < 0f) return false;
        return TrySetGyroTorque(data, _savedMaxTorque, "restored");
    }

    public static bool TryZeroGyroTorque(DEntityContext data)
    {
        if (!SafeTryGet<MaxTorqueData>(data, out var mt))
            return false;
        if (mt.MaxTorque == 0f)
            return true;
        if (_savedMaxTorque < 0f)
            _savedMaxTorque = mt.MaxTorque;
        float was = mt.MaxTorque;
        if (!TrySetGyroTorque(data, 0f, "zeroed"))
            return false;
        Log.Default?.Info($"[AERO] Physics: zeroed MaxTorque (was {was:F0})");
        return true;
    }

    private static bool TrySetGyroTorque(DEntityContext data, float value, string label)
    {
        ref MaxTorqueData mt = ref TryWrite<MaxTorqueData>(data);
        if (Unsafe.IsNullRef(in mt))
            return false;
        mt.MaxTorque = value;
        return true;
    }

    /// <summary>
    /// Apply linear velocity delta to the entity's RigidBodyData.
    /// </summary>
    public static bool ApplyDeltaV(DEntityContext data, Vector3 deltaV)
    {
        return ApplyDeltaVAndTorque(data, deltaV, Vector3.Zero);
    }

    /// <summary>
    /// Apply linear and angular velocity deltas.
    ///
    /// Prefer <see cref="AeroPhysics.ApplyForceAndTorque"/>: it takes a force and moment and
    /// converts them with the engine's own impulse helpers. This overload survives for callers
    /// that already hold a velocity delta, and now routes through RigidBodyDataFunctions rather
    /// than re-deriving I^-1 by hand.
    /// </summary>
    public static bool ApplyDeltaVAndTorque(DEntityContext data, Vector3 deltaV, Vector3 torqueLocal, float dt = 1f / 60f, Quaternion? gridOrientation = null)
    {
        if (!IsFinite(deltaV) || !IsFinite(torqueLocal))
            return false;

        ref RigidBodyData rb = ref TryWrite<RigidBodyData>(data);
        if (Unsafe.IsNullRef(in rb))
            return false;

        if (deltaV.LengthSquared() > 0f)
            rb.LinearVelocity += deltaV;

        if (torqueLocal.LengthSquared() > 1e-6f
            && SafeTryGet<RigidBodyMassProperties>(data, out var mass))
        {
            if (SafeGetWorldTransform(data, out var wtLocal))
                rb.ApplyAngularImpulseLocal(in mass, in wtLocal, torqueLocal * dt);
        }
        return true;
    }

    internal static bool IsFinite(Vector3 v) =>
        !float.IsNaN(v.X) && !float.IsNaN(v.Y) && !float.IsNaN(v.Z) &&
        !float.IsInfinity(v.X) && !float.IsInfinity(v.Y) && !float.IsInfinity(v.Z);

    // ═══════════════════════════════════════════════════════════════
    // Thruster data access
    // ═══════════════════════════════════════════════════════════════

    /// <summary>ThrustData is a directly referenced type now, so access is always available.</summary>
    public static bool ThrusterAccessAvailable => true;

    public static bool GroundSystemReady => _groundSystemInitialized;

    /// <summary>Read a thruster's max power and Base6Directions.Direction (as int).</summary>
    public static bool TryGetThrustData(DEntityContext data, out float maxPower, out int direction)
    {
        maxPower = 0f;
        direction = 0;
        if (!SafeTryGet<ThrustData>(data, out var td))
            return false;
        maxPower = td.MaxThrustPower;
        direction = (int)td.Direction;
        return true;
    }

    /// <summary>True if the thruster currently carries the IsThrusting tag.</summary>
    public static bool IsEntityThrusting(DEntityContext data)
    {
        return SafeHas<IsThrusting>(data);
    }

    /// <summary>ThrusterOverrideData.OverridePower, or -1 when no override is set.</summary>
    public static float GetThrustOverride(DEntityContext data)
    {
        return SafeTryGet<ThrusterOverrideData>(data, out var o) ? o.OverridePower : -1f;
    }

    /// <summary>Set ThrusterOverrideData; this drives the game's thrust visuals.</summary>
    public static bool TrySetThrustOverride(DEntityContext data, float overridePower)
    {
        return SafeSet(data, new ThrusterOverrideData { OverridePower = overridePower });
    }

    /// <summary>Drop ThrusterOverrideData, returning the thruster to normal control.</summary>
    public static bool TryRemoveThrustOverride(DEntityContext data)
    {
        return SafeTryRemove<ThrusterOverrideData>(data);
    }

    // Resolved SetData<OverriddenThrustData> on Component base class
    private static MethodInfo _setDataOnComponent;
    private static Type _thrustCompType;

    /// <summary>
    /// Write grid-level OverriddenThrustData, which ComputeThrust reads.
    ///
    /// Prefers ThrustComponent.SetData so the value lands in the component's scene pool where
    /// ComputeThrust looks; DEntityContext.Set is the fallback. SetData is PROTECTED on
    /// Component, so this is one of the three reflection holdouts -- see EngineOverrides.
    /// </summary>
    public static bool TrySetOverriddenThrust(Entity gridEntity, DEntityContext gridData, Vector3 directionalThrust)
    {
        var overridden = new OverriddenThrustData { DirectionalThrust = directionalThrust };

        if (EngineOverrides.TrySetComponentData<ThrustComponent, OverriddenThrustData>(gridEntity, overridden))
            return true;

        return SafeSet(gridData, overridden);
    }

    /// <summary>Magnitude of ActiveThrustData.ComputedThrustPerFrame on the GRID entity.</summary>
    public static float GetGridActiveThrust(DEntityContext gridData)
    {
        return SafeTryGet<ActiveThrustData>(gridData, out var atd)
            ? atd.ComputedThrustPerFrame.Length()
            : 0f;
    }

    /// <summary>
    /// Write synthetic ControlData (test harness player-input simulation) onto a grid.
    /// Uses the protected Component.SetData -- see EngineOverrides.
    /// </summary>
    public static bool TrySetControlData(Entity gridEntity, Vector3 movement, Vector3 rotation)
    {
        var cd = new ControlData { Movement = movement, Rotation = rotation };
        return EngineOverrides.TrySetComponentData<ThrustComponent, ControlData>(gridEntity, cd);
    }

    private static MethodInfo _setDataControlMethod;



    /// <summary>OverriddenThrustData.DirectionalThrust on a grid, or zero when absent.</summary>
    public static Vector3 GetOverriddenThrust(DEntityContext gridData)
    {
        return SafeTryGet<OverriddenThrustData>(gridData, out var o) ? o.DirectionalThrust : Vector3.Zero;
    }

    public static bool TryGetWorldTransform(DEntityContext data, out WorldTransform wt)
    {
        return SafeGetWorldTransform(data, out wt);
    }

    private static MethodInfo _setWorldTransformMethod;

    /// <summary>
    /// Set orientation while keeping position. Does NOT zero angular velocity -- call
    /// TryZeroAngularVelocity on a later frame, after physics has processed the teleport.
    /// </summary>
    public static bool TrySetOrientation(DEntityContext data, Quaternion targetOrientation)
    {
        if (!SafeGetWorldTransform(data, out var wt)) return false;
        var newWt = new WorldTransform(wt.Position, targetOrientation);
        try { data.SetWorldTransform(in newWt); return true; } catch { return false; }
    }

    /// <summary>Teleport to a new position, preserving orientation.</summary>
    public static bool TrySetPosition(DEntityContext data, Vector3D newPosition)
    {
        if (!SafeGetWorldTransform(data, out var wt)) return false;
        var newWt = new WorldTransform(newPosition, wt.Orientation);
        try { data.SetWorldTransform(in newWt); return true; } catch { return false; }
    }

    public static bool TryZeroAngularVelocity(DEntityContext data)
    {
        return TrySetAngularVelocity(data, Vector3.Zero);
    }

    public static bool TrySetAngularVelocity(DEntityContext data, Vector3 angVel)
    {
        if (!IsFinite(angVel)) return false;
        ref RigidBodyData rb = ref TryWrite<RigidBodyData>(data);
        if (Unsafe.IsNullRef(in rb)) return false;
        rb.AngularVelocity = angVel;
        return true;
    }

    public static bool TrySetLinearVelocity(DEntityContext data, Vector3 linearVel)
    {
        if (!IsFinite(linearVel)) return false;
        ref RigidBodyData rb = ref TryWrite<RigidBodyData>(data);
        if (Unsafe.IsNullRef(in rb)) return false;
        rb.LinearVelocity = linearVel;
        return true;
    }

    public static bool TrySetVelocity(DEntityContext data, Vector3 linearVel, Vector3 angularVel)
    {
        if (!IsFinite(linearVel) || !IsFinite(angularVel)) return false;
        ref RigidBodyData rb = ref TryWrite<RigidBodyData>(data);
        if (Unsafe.IsNullRef(in rb)) return false;
        rb.LinearVelocity = linearVel;
        rb.AngularVelocity = angularVel;
        return true;
    }

    // ═══════════════════════════════════════════════════════════════
    // Offset impulse (ApplyImpulseAt equivalent)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Apply an impulse at a world point. Linear and angular response both fall out of the
    /// geometry -- this is RigidBodyDataFunctions.ApplyImpulseAt, the engine's own routine.
    /// </summary>
    public static bool ApplyImpulseAt(DEntityContext data, Vector3 impulse, Vector3D worldPosition, WorldTransform wt = default)
    {
        if (!IsFinite(impulse)) return false;
        ref RigidBodyData rb = ref TryWrite<RigidBodyData>(data);
        if (Unsafe.IsNullRef(in rb)) return false;
        if (!SafeTryGet<RigidBodyMassProperties>(data, out var mass)) return false;
        if (wt.Equals(default(WorldTransform)) && !SafeGetWorldTransform(data, out wt)) return false;
        rb.ApplyImpulseAt(in mass, in wt, worldPosition, impulse);
        return true;
    }

    /// <summary>Apply an angular impulse expressed in grid-local space.</summary>
    public static bool ApplyTorqueImpulse(DEntityContext data, Vector3 localTorqueImpulse, WorldTransform wt = default)
    {
        if (!IsFinite(localTorqueImpulse)) return false;
        ref RigidBodyData rb = ref TryWrite<RigidBodyData>(data);
        if (Unsafe.IsNullRef(in rb)) return false;
        if (!SafeTryGet<RigidBodyMassProperties>(data, out var mass)) return false;
        if (wt.Equals(default(WorldTransform)) && !SafeGetWorldTransform(data, out wt)) return false;
        rb.ApplyAngularImpulseLocal(in mass, in wt, localTorqueImpulse);
        return true;
    }

    /// <summary>Apply a world-space linear impulse at the centre of mass.</summary>
    public static bool ApplyLinearImpulse(DEntityContext data, Vector3 impulse)
    {
        if (!IsFinite(impulse)) return false;
        ref RigidBodyData rb = ref TryWrite<RigidBodyData>(data);
        if (Unsafe.IsNullRef(in rb)) return false;
        if (!SafeTryGet<RigidBodyMassProperties>(data, out var mass)) return false;
        rb.ApplyLinearImpulse(in mass, impulse);
        return true;
    }

    // ═══════════════════════════════════════════════════════════════
    // Gravity & ground height (raycast)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Local gravity vector (not normalised) from GravityEffectData.</summary>
    public static Vector3 GetGravityDirection(DEntityContext data)
    {
        return SafeTryGet<GravityEffectData>(data, out var g) ? g.GravitySum : Vector3.Zero;
    }

    /// <summary>
    /// Resolve IPhysics for the ground/forward probes. Session services are reachable via the
    /// public GameEntityExtensions helpers, so this no longer walks Scene.UserObject ->
    /// Session.EntitySerializer.TryResolveSessionService by reflection.
    /// </summary>
    public static void InitGroundSystem(Entity entity)
    {
        if (_groundSystemInitialized) return;
        _groundSystemInitialized = true;

        try
        {
            _physics = entity?.GetSession()?.TryGet<IPhysics>();
            Log.Default?.Info(_physics != null
                ? "[AERO] Physics: IPhysics resolved for ray probes"
                : "[AERO] Physics: IPhysics not available in session");
        }
        catch (Exception ex)
        {
            Log.Default?.Info($"[AERO] Physics: ground system init failed: {ex.Message}");
        }
    }

    // ── Async ray probe state ──
    private const float GroundRayLength = 200f;
    private static Task<Buffer<SweepQueryHit>> _pendingRay;
    private static bool _rayInFlight;
    private static float _lastGroundDist = -1f;
    private static int _raycastCooldown;

    private static Task<Buffer<SweepQueryHit>> _pendingFwd;
    private static bool _fwdInFlight;
    private static float _lastFwdDist = float.NaN;
    private static float _lastFwdMaxDist;
    private static int _fwdCooldown;

    /// <summary>
    /// Fire a ray and return the hit distance, or -1 for "completed, nothing hit".
    /// Returns false while the cast is still in flight.
    /// </summary>
    private static bool TryHarvest(ref Task<Buffer<SweepQueryHit>> task, ref bool inFlight,
                                   float rayLength, out float distance)
    {
        distance = -1f;
        if (!inFlight) return false;
        if (!task.TryGetResult(out var hits)) return false;

        inFlight = false;
        if (hits.Count > 0)
            distance = (float)(hits[0].Fraction * rayLength);
        hits.Dispose();
        return true;
    }

    // ── Async raycast state ──
    private static object _pendingRayTask;   // Task<Buffer<SweepQueryHit>> in flight
    // Cached PropertyInfo for task/buffer result reading (resolved on first completed task)
    private static PropertyInfo _taskIsCompletedProp;
    private static PropertyInfo _taskResultProp;
    private static PropertyInfo _bufferCountProp;
    private static PropertyInfo _bufferIndexerProp;
    private static bool _taskPropsResolved;

    /// <summary>
    /// Distance to ground along gravity, via an async physics ray. Fires every few frames and
    /// returns the cached value in between. -1 means "no ground found".
    /// </summary>
    public static float GetGroundDistance(Vector3D worldPosition, Vector3 gravityDir)
    {
        if (_physics == null) return -1f;

        if (TryHarvest(ref _pendingRay, ref _rayInFlight, GroundRayLength, out float d))
            _lastGroundDist = d;

        if (!_rayInFlight && --_raycastCooldown <= 0)
        {
            // Sample faster when close to the ground.
            _raycastCooldown = (_lastGroundDist >= 0f && _lastGroundDist < 50f) ? 3 : 10;

            float len = gravityDir.Length();
            if (len < 0.01f) return _lastGroundDist;

            try
            {
                var args = new RayCastArgs(in worldPosition, gravityDir / len * GroundRayLength);
                _pendingRay = _physics.CastRayAsync(in args, CollisionPreset.Closest);
                _rayInFlight = true;
            }
            catch (Exception ex)
            {
                Log.Default?.Info($"[AERO] Ground ray failed: {ex.Message}");
            }
        }
        return _lastGroundDist;
    }

    /// <summary>
    /// Distance to the nearest obstacle along fwdDir, via an async physics ray.
    /// Returns -1 until the first cast completes; maxDist when the ray reaches nothing.
    /// </summary>
    public static float GetForwardDistance(Vector3D worldPosition, Vector3 fwdDir, float maxDist)
    {
        if (_physics == null) return -1f;

        if (TryHarvest(ref _pendingFwd, ref _fwdInFlight, _lastFwdMaxDist, out float d))
            _lastFwdDist = d < 0f ? _lastFwdMaxDist : d;   // clear path == full range

        if (!_fwdInFlight && --_fwdCooldown <= 0)
        {
            _fwdCooldown = 6;
            float len = fwdDir.Length();
            if (len < 0.01f) return MapFwd();

            _lastFwdMaxDist = maxDist;
            try
            {
                var args = new RayCastArgs(in worldPosition, fwdDir / len * maxDist);
                _pendingFwd = _physics.CastRayAsync(in args, CollisionPreset.Closest);
                _fwdInFlight = true;
            }
            catch (Exception ex)
            {
                Log.Default?.Info($"[AERO] Forward ray failed: {ex.Message}");
            }
        }
        return MapFwd();
    }

    private static float MapFwd() => float.IsNaN(_lastFwdDist) ? -1f : _lastFwdDist;

    // Component iteration used to live here as a REFLECTION HOLDOUT: Entity.Components is
    // ImmutableArray<Component> and VRS1001 bans that type in scripts, so finding a sibling
    // component by runtime Type meant reflecting over the array.
    //
    // It is gone. Every caller either already knew its component type at compile time, or could
    // capture the lookup generically at registration (BlockComponentFactory now stores
    // Func<Entity, Component> = e => e.TryGet<T>()). Entity.TryGet<T>() is public and typed, and
    // was verified in game to agree with both the tag lookup and the old scan:
    //     [AERO] LOOKUPPROBE tag=True generic=True scan=True
    // Do not reintroduce a Type-keyed scan -- add a generic overload instead.

}
