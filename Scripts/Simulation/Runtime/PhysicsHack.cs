#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
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
    private static volatile bool _groundSystemInitialized;
    private static volatile IPhysics _physics;
    public static IPhysics Physics => _physics;

    /// <summary>
    /// A grid is a SUB-PART when something it is jointed to is heavier, or static (a thruster pod on a hinge, a
    /// ship docked to a station): its orientation is the joint's, not its own to hold.
    /// </summary>
    public static bool IsSubPart(Entity grid, float myMass)
    {
        var phys = _physics;
        if (phys == null || grid == null || !phys.HasBody(grid.DEntity)) return false;
        var scene = grid.Data.Scene;
        using (var buf = new Keen.VRage.Library.Memory.Buffer<Keen.VRage.DCS.Accessors.DEntity>(Keen.VRage.Library.Memory.Allocator.Pool, "AeroSubPart"))
        {
            if (!phys.GetDirectlyConnectedBodies(grid.DEntity, buf)) return false;
            var en = ((Keen.VRage.Library.Memory.BufferReference<Keen.VRage.DCS.Accessors.DEntity>)buf).GetEnumerator();
            bool sub = false;
            while (en.MoveNext())
            {
                var ctx = new DEntityContext(scene, en.Current);
                if (!Alive(ctx)) continue;
                if (!ctx.TryGet<RigidBodyMassProperties>(out var mp)) continue;
                if (mp.InvMass <= 0f || 1f / mp.InvMass > myMass) { sub = true; break; }
            }
            en.Dispose();
            return sub;
        }
    }
    private static bool _gravityFixed;


    /// <summary>
    /// DEntityContext.TryGet throws NullReferenceException from deep inside
    /// Scene.TryGetDataPointer when the entity has been detached or its scene torn down --
    /// it is only "try" with respect to the component being absent, not to the entity being
    /// dead. The old reflection path hid this behind a blanket catch; these wrappers keep that
    /// tolerance now that the calls are direct.
    /// </summary>
    /// <summary>
    /// Whether an entity is still alive, without throwing. Reading a destroyed entity's data throws, and the game
    /// makes every exception cost ~200 ms even when caught (measured: a cached thruster destroyed in combat froze
    /// the server 360-440 ms a time on the Red Ship). The Safe* accessors check this first; their try/catch stays
    /// for an entity that dies in between.
    /// </summary>
    public static bool Alive(DEntityContext data)
    {
        var scene = data.Scene;
        return scene != null && scene.IsEntityAlive(data.Entity);
    }

    private static bool SafeTryGet<T>(DEntityContext data, out T value) where T : unmanaged
    {
        if (!Alive(data)) { value = default; return false; }
        try { return data.TryGet(out value); }
        catch { System.Threading.Interlocked.Increment(ref Caught); value = default; return false; }
    }

    /// <summary>Exceptions swallowed by the Safe* accessors (a dead entity's data throws), for AeroCost.</summary>
    public static int Caught;

    private static bool SafeHas<T>(DEntityContext data) where T : unmanaged
    {
        if (!Alive(data)) return false;
        try { return data.Has<T>(); }
        catch { System.Threading.Interlocked.Increment(ref Caught); return false; }
    }

    private static bool SafeSet<T>(DEntityContext data, T value) where T : unmanaged
    {
        if (!Alive(data)) return false;
        try { data.Set(value); return true; }
        catch { System.Threading.Interlocked.Increment(ref Caught); return false; }
    }

    private static bool SafeTryRemove<T>(DEntityContext data) where T : unmanaged
    {
        if (!Alive(data)) return false;
        try { return data.TryRemove<T>(); }
        catch { System.Threading.Interlocked.Increment(ref Caught); return false; }
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

    /// <summary>The world's PhysicsSessionConfiguration.GravityMultiplier (1 when unreadable): the engine scales the
    /// gravity it applies by it, on top of GravityEffectData.GravitySum.</summary>
    public static float GravityMultiplier()
    {
        try { return DefinitionManager.Instance?.GetConfiguration<PhysicsSessionConfiguration>()?.GravityMultiplier ?? 1f; }
        catch { return 1f; }
    }

    /// <summary>
    /// Once: log the physics session configuration the world runs with (CONFIGPROBE). It used to set
    /// GravityMultiplier and MaximumSpeedLinear by reflection (private setters); the mod's own
    /// PhysicsSessionConfiguration.def sets them now, so this only reads.
    /// </summary>
    public static void TryFixGravity()
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

            // The mod ships Content/System/Configurations/PhysicsSessionConfiguration.def, the data-driven way to set
            // these: WorldSessionComponent builds the VelocityLimitProvider straight from _definition.Physics.MaximumSpeedLinear.
            // Until its contentcache.vrb built, the .def never mounted and the probe read the world's values
            // (CONFIGPROBE before override: MaximumSpeedLinear=300 GravityMultiplier=2), so the values were forced by reflection.
            Log.Default?.Info($"[AERO] CONFIGPROBE before override: MaximumSpeedLinear={config.MaximumSpeedLinear} GravityMultiplier={config.GravityMultiplier} MaximumCharacterSpeedLinear={config.MaximumCharacterSpeedLinear}");
            // (2026-10-01: the cache builds now - D:/aero/tools/build_mod_content.py - and the probe reads the
            // mod's 1000 / 1. The reflection setters, unused since, went in the 2026-10-15 reorg.)
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

    /// <summary>TryGetMassProperties and TryGetInertiaData in one read (the same RigidBodyMassProperties: one alive check -
    /// the scene's shared lock word - and one lookup, not two; the sim job reads both every full step). False: neither read,
    /// mass and centre of mass zero, the inertia outputs as TryGetInertiaData leaves them.</summary>
    public static bool TryGetMassAndInertia(DEntityContext data, out float mass, out Vector3 centerOfMass,
        out Vector3 invInertiaTensor, out Quaternion majorAxisRot)
    {
        mass = 0f; centerOfMass = Vector3.Zero;
        invInertiaTensor = Vector3.Zero; majorAxisRot = Quaternion.Identity;
        if (!SafeTryGet<RigidBodyMassProperties>(data, out var mp))
            return false;
        mass = mp.InvMass > 1e-10f ? 1f / mp.InvMass : 0f;
        centerOfMass = mp.CenterOfMass;
        invInertiaTensor = mp.InvInertiaTensor;
        majorAxisRot = mp.InertiaMajorAxisRotation;
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
    /// (Per grid, 2026-10-15: one static float - the first grid ever zeroed, kept across world loads, never given back: the
    ///  game keeps MaxTorque as a sum of gyro deltas and never recomputes it, so a zeroed grid kept 0 for good.)
    private static readonly System.Collections.Generic.Dictionary<Keen.VRage.DCS.Accessors.DEntity, float> _savedMaxTorque = new();


    public static bool TryZeroGyroTorque(DEntityContext data)
    {
        if (!SafeTryGet<MaxTorqueData>(data, out var mt))
            return false;
        if (mt.MaxTorque == 0f)
            return true;
        lock (_savedMaxTorque) _savedMaxTorque.TryAdd(data.Entity, mt.MaxTorque);
        float was = mt.MaxTorque;
        if (!TrySetGyroTorque(data, 0f, "zeroed"))
            return false;
        Log.Default?.Info($"[AERO] Physics: zeroed MaxTorque (was {was:F0})");
        return true;
    }

    /// <summary>A grid's MaxTorque given back as TryZeroGyroTorque found it, plus whatever gyros changed since (the game
    /// adds their deltas to the zero). False: not zeroed by us. (AeroGridComponent leaving its scene; a harness teardown.)</summary>
    public static bool RestoreGyroTorque(DEntityContext data)
    {
        float saved;
        lock (_savedMaxTorque) if (!_savedMaxTorque.Remove(data.Entity, out saved)) return false;
        if (!SafeTryGet<MaxTorqueData>(data, out var mt)) return false;
        if (!TrySetGyroTorque(data, mt.MaxTorque + saved, "restored")) return false;
        Log.Default?.Info($"[AERO] Physics: restored MaxTorque ({mt.MaxTorque + saved:F0})");
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

    internal static bool IsFinite(Vector3 v) =>
        !float.IsNaN(v.X) && !float.IsNaN(v.Y) && !float.IsNaN(v.Z) &&
        !float.IsInfinity(v.X) && !float.IsInfinity(v.Y) && !float.IsInfinity(v.Z);

    // ═══════════════════════════════════════════════════════════════
    // Thruster data access
    // ═══════════════════════════════════════════════════════════════

    /// <summary>ThrustData is a directly referenced type now, so access is always available.</summary>
    public static bool ThrusterAccessAvailable => true;

    public static bool GroundSystemReady => _groundSystemInitialized;


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


    /// <summary>
    /// Write grid-level OverriddenThrustData, which ComputeThrust reads.
    ///
    /// Prefers ThrustComponent.SetData so the value lands in the component's scene pool where
    /// ComputeThrust looks; DEntityContext.Set is the fallback. SetData is PROTECTED on
    /// Component, so this is the reflection holdout -- see EngineOverrides.
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




    /// <summary>OverriddenThrustData.DirectionalThrust on a grid, or zero when absent.</summary>
    public static Vector3 GetOverriddenThrust(DEntityContext gridData)
    {
        return SafeTryGet<OverriddenThrustData>(gridData, out var o) ? o.DirectionalThrust : Vector3.Zero;
    }


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
    static volatile object _physicsSession;
    static readonly object _groundLock = new();
    /// <summary>The ray probes' physics: the SERVER scene's (forces are computed there; a client grid's DEntity is not that
    /// world's), re-resolved when the session changes (a world reload kept the old one).</summary>
    public static void InitGroundSystem(Entity entity)
    {
        var session = entity?.GetSession();
        if (session == null || (_groundSystemInitialized && ReferenceEquals(session, _physicsSession))) return;
        // (the physics published before the session and the flag, under a lock - 2026-10-15: the flag and session went first,
        //  so a parallel grid's job returned above and cast its ray on the old session's physics, or on none)
        lock (_groundLock)
        {
            if (_groundSystemInitialized && ReferenceEquals(session, _physicsSession)) return;
            IPhysics phys = null;
            try
            {
                phys = session.TryGet<IPhysics>();
                Log.Default?.Info(phys != null
                    ? "[AERO] Physics: IPhysics resolved for ray probes"
                    : "[AERO] Physics: IPhysics not available in session");
            }
            catch (Exception ex)
            {
                Log.Default?.Info($"[AERO] Physics: ground system init failed: {ex.Message}");
            }
            _physics = phys;
            _physicsSession = session;
            _groundSystemInitialized = true;
        }
    }

    // ── Ray probe state ──
    // 2 km (was 200 m: above it the probe said "no ground", so the flight rig never saw its altitude).
    private const float GroundRayLength = 2000f;

    /// <summary>
    /// One grid's ground probe. Per grid: the probe was static, shared by every aero grid, so each grid's
    /// result overwrote the others' (a parked grid's 0 m became a flying one's altitude).
    /// </summary>
    public sealed class GroundProbe
    {
        internal float Last = -1f;
        internal int Cooldown;
        // (the ray's hit list and ignore list: native heap, grown once and reused by every cast - no allocation per ray.
        //  Only this grid's sim job touches them. Released when the grid leaves the scene: Release)
        internal Keen.VRage.Library.Memory.Buffer<SweepQueryHit> Hits;
        internal Keen.VRage.Library.Memory.Buffer<Keen.VRage.DCS.Accessors.DEntity> Ignore;

        /// <summary>Free the ray buffers (the grid left the scene; a later cast grows them again).</summary>
        internal void Release() { Hits.Dispose(); Ignore.Dispose(); }
    }

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


    /// <summary>The ground ray ignores the grid itself and what is attached to it (harness: aeroset PhysicsHack.GroundIgnoreSelf 0).</summary>
    public static bool GroundIgnoreSelf = true;
    /// <summary>Diagnostics (harness aeroget): ground rays cast, answered with a hit, answered with none, failed (none since the
    /// ray went synchronous, 2026-10-15: kept for the harness); the last reading.</summary>
    public static int GroundCasts, GroundHits, GroundMisses, GroundFails;
    public static float GroundLast = -1f;

    /// <summary>
    /// Distance to ground along gravity, via a synchronous physics ray. Casts every few frames and
    /// returns the cached value in between. -1 means "no ground found".
    /// </summary>
    /// <remarks>
    /// Synchronous since 2026-10-15: CastRayAsync boxed an AsyncStateMachineTask per cast (52 KB/s of garbage in a profile,
    /// alloc6) and answered a tick late. The sync CastRay is what the async one ran (at SimulationOutputsCollected); here it runs
    /// in the grid's sim job, GridUpdateOrder.UpdateSystems, which is ordered before the Physics step, so the world is not
    /// stepping - the game's own jobs cast sync rays the same way (character alignment, auto-targeting, thruster landing fx).
    /// The hit and ignore lists are the probe's, reused: no GC and no native allocation per ray (stack spans were tried: a list
    /// that outgrew one spilled to the thread's bump allocator, which asserts on a thread that never set it up). No try/catch
    /// (hot path); the ignore list is gathered and used in this one call now, where the async one kept it alive across ticks.
    /// </remarks>
    public static float GetGroundDistance(GroundProbe probe, Vector3D worldPosition, Vector3 gravityDir, Entity grid = null)
    {
        var physics = _physics;   // (read once: a session change re-resolves it from another grid's job)
        if (physics == null || probe == null) return -1f;

        if (--probe.Cooldown <= 0)
        {
            // Sample faster when close to the ground.
            probe.Cooldown = (probe.Last >= 0f && probe.Last < 50f) ? 3 : 10;

            float len = gravityDir.Length();
            if (len < 0.01f) return probe.Last;

            float d = CastGround(physics, probe, worldPosition, gravityDir / len * GroundRayLength, grid);
            probe.Last = d;
            GroundLast = d;
            GroundCasts++;
            if (d >= 0f) GroundHits++; else GroundMisses++;
        }
        return probe.Last;
    }

    /// <summary>One closest-hit ray into the probe's own buffers; the hit distance, or -1 for nothing hit.</summary>
    private static float CastGround(IPhysics physics, GroundProbe probe, Vector3D origin, Vector3 ray, Entity grid)
    {
        probe.Hits.Clear();   // (CastRay appends; the closest-hit merge assumes it started empty)
        var args = new RayCastArgs(in origin, ray);
        // (the ray starts at the grid's origin, inside its hull: without this it hit the ship's own belly and
        //  read a metre or two - ground effect on at any height. The grid and its attached parts are ignored)
        if (grid != null && GroundIgnoreSelf)
        {
            probe.Ignore.Clear();
            if (physics.HasBody(grid.DEntity)) physics.GetDirectlyConnectedBodies(grid.DEntity, probe.Ignore);
            probe.Ignore.Add(grid.DEntity);
            args.IgnoredEntities = probe.Ignore;   // (a copy, made after the last grow: never args.Dispose, the probe owns it)
        }
        physics.CastRay(probe.Hits, in args, CollisionPreset.Closest);
        return probe.Hits.Count > 0 ? (float)(probe.Hits[0].Fraction * GroundRayLength) : -1f;
    }

    /// <summary>
    /// Distance to the nearest obstacle along fwdDir, via an async physics ray.
    /// Returns -1 until the first cast completes; maxDist when the ray reaches nothing.
    /// </summary>
    public static float GetForwardDistance(Vector3D worldPosition, Vector3 fwdDir, float maxDist)
    {
        var physics = _physics;
        if (physics == null) return -1f;

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
                _pendingFwd = physics.CastRayAsync(in args, CollisionPreset.Closest);
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
