#pragma warning disable
using System;
using System.Threading;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.Game2.Simulation.WorldObjects.Shared.Movement;
using Keen.VRage.Core;
using Keen.VRage.Physics.Data;

namespace AeroMod;

/// <summary>
/// REALISTIC THRUST TORQUE (replaces the phantom attitude system). SE2 computes one thrust vector per grid and applies
/// it at the centre of mass: thrust never turns a ship. Here, every frame, after the game's thrust:
///  1. OFFSET TORQUE: the game's actual thrust this frame is shared among the thrusters facing each direction in
///     proportion to their maximum (as the game allocates it), and each share acts at its thruster: torque
///     = sum of r x F. Unbalanced thrust turns the ship. Nothing cancels it.
///  2. THRUSTER ATTITUDE: SE2 flies by a TARGET ORIENTATION (TargetControlData: your controls move it, the gyros
///     steer to it). Thrusters help steer to the same target, restoratively (a rate toward the target, not only
///     rate-nulling), with the capacity the game's own thrust leaves them: real force at real positions, linear
///     force included (the game's dampeners then take out any drift). Unpiloted with dampeners on: they hold the
///     last orientation. Unpiloted, dampeners off: nothing.
/// No phantom torque anywhere: every torque comes from a thruster that could produce it. (The thruster steering
/// is applied as physics impulses: it burns no fuel yet. Its flames: each thruster's share is published for the client
/// copy to draw - see Flames below.)
/// </summary>
public static class ThrustTorque
{
    public static bool Enabled = true;
    /// <summary>Apply the torque and force (off: computed, reported and shown as flames only - for A/B tests).</summary>
    public static bool ApplyForces = true;   // (torque only, the game's net thrust kept - KeepNetThrust)
    /// <summary>rad/s of target rate per rad of error, and the most it asks for.</summary>
    public static float Kp = 1.2f, MaxRate = 0.8f;
    /// <summary>The time it tries to reach the target rate in (s): shorter is stiffer.</summary>
    public const float ResponseTime = 0.3f;
    /// <summary>Below this error and rate the hold rests (no chatter).</summary>
    public const float RestAngle = 0.002f, RestRate = 0.002f;
    /// <summary>Unpiloted damping's rest rate: no attitude to hold still about, so a far finer one - at RestRate a residual
    /// 0.1 deg/s coasted on for good (ThrustTests ControlAuditChecks).</summary>
    public const float DampRestRate = 0.0002f;
    /// <summary>Allocation solver iterations (projected gradient).</summary>
    public const int Iterations = 60;

    /// <summary>Last frame's numbers (for the harness / log).</summary>
    /// <summary>Last frame's numbers: the imbalance as the game shares the thrust (OffsetTorque), what the controller's
    /// sharing changes (RcsTorque, RcsForce), the attitude error, and how much thrust it moved (RcsUse).</summary>
    public struct Report { public Vector3 OffsetTorque, RcsTorque, RcsForce, Error; public float RcsUse, AttitudePriority; public string Mode;
        /// <summary>Flying by the aero-aware law (AeroAware): the air's torque it answered, the gyros' torque it gave, and the
        /// game gyros' own angular velocity change it took back (all local).</summary>
        public bool Aware; public Vector3 AeroTorque, GyroTorque, GameGyroUndone; }

    /// <summary>
    /// AERO-AWARE ATTITUDE (2026-10-12, the user: "fix the controllers so they are aware of exact aero torque but they don't
    /// cheat"). Piloted (a target orientation) in air with the air's forces known, ONE law flies both the thrusters and the
    /// gyros: ThrustAllocator.AttitudeAccel, told the air's torque this frame (AeroGridComponent.LastAeroTorque - the very
    /// torque applied), asks for I x its acceleration - the air's torque; the thrusters give what they can of what the gyros
    /// cannot (the game's thrust kept, as before), the gyros the rest up to their real reach (GyroControl: the game's own
    /// budget; the game's gyro law's impulse this frame taken back - it knows nothing of the air and fought this one).
    /// Nothing is given that the ship's thrusters and gyros could not give: a ship without the authority departs.
    /// Off, in vacuum, unpiloted: the game's gyros and the thruster law as before. (Before: the thrusters steered by
    /// their own law and the game's gyros by theirs - two controllers on one axis - neither knowing the air.)
    /// </summary>
    public static bool AeroAware = true;
    /// <summary>Air thinner than this: not flown by the aero-aware law (kg/m^3).</summary>
    public const float AwareMinDensity = 1e-4f;

    /// <summary>
    /// The thrust the game's thrusters give this frame (grid-local, N), the GRAVITY HOLD INCLUDED. The game's
    /// ActiveThrustData.ComputedThrustPerFrame leaves the hold out: ThrustComponent.ComputeThrust cancels gravity by
    /// shrinking the grid's gravity in place and only puts the thrust beyond it there - a hovering ship read ~0 N: no
    /// hover offset torque, almost no steering (KeepNetThrust held ~0 net), and at rest its flames went dark (audit
    /// 2026-10-10). Its VoluntaryThrustData is the whole command (hold + dampening + input) as a fraction of each
    /// direction's regular maximum (ThrustComponent.UnscaleThrust): x that maximum, clamped to it, + the overrides.
    /// </summary>
    internal static Vector3 GameThrust(DEntityContext data, bool server)
    {
        if (data.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement.VoluntaryThrustData>(out var vt) && data.TryGet<Keen.Game2.Simulation.WorldObjects.Shared.Movement.MaxThrustData>(out var mt))
        {
            Vector3 v = vt.VoluntaryThrust;
            Vector3 f = mt.Regular.Clamp(v * mt.Regular.SignSelectAbs(in v));
            // (the client copy's overrides are the flames this mod draws - not thrust)
            if (server && data.TryGet<Keen.Game2.Simulation.WorldObjects.Shared.Movement.OverriddenThrustData>(out var ov)) f += ov.DirectionalThrust;
            return f.IsValid() ? f : Vector3.Zero;
        }
        return data.TryGet<ActiveThrustData>(out var atd) ? atd.ComputedThrustPerFrame * 60f : Vector3.Zero;
    }

    /// <summary>A thruster's maximum now, the game's own rule (GridMovementCollectorComponent.OnThrusterChanged): 0 when
    /// disabled (off, unpowered, damaged - DisabledTag), else its ThrustData.MaxThrustPower, x a player's power override
    /// (server only: the client copy's overrides are this mod's flames). -1: not readable (the cached maximum stands).</summary>
    internal static float LiveMax(Entity thruster, bool server)
    {
        var d = thruster?.Data;
        // (a thruster removed - the ship cut, a block ground off - is not alive: its data threw, 2026-10-10)
        if (d == null || !PhysicsHack.Alive(d.Value) || !d.Value.TryGet<Keen.Game2.Simulation.WorldObjects.Movement.ThrustData>(out var td)) return -1f;
        if (d.Value.Has<Keen.Game2.Simulation.WorldObjects.CubeBlocks.DisabledTag>()) return 0f;
        float m = td.MaxThrustPower;
        if (server && d.Value.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement.ThrusterOverrideData>(out var o)) m *= Math.Max(0f, o.OverridePower);
        return m;
    }

    // ── the PROBE (harness: aeroset ThrustTorque.Probe true): once a second per grid, [AERO-THRUST] - its mode, the game's
    //    thrust as read (gravity hold included), that thrust over the weight it holds (1: hovering), the steering's use ──
    public static bool Probe; public static int ProbeHz = 1;
    static readonly System.Collections.Generic.Dictionary<AeroGridComponent, long> _probeAt = new();
    static void ProbeLog(AeroGridComponent aero, DEntityContext data, in WorldTransform wt, Vector3 gameF, float invMass, in Report rep)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_probeAt) { if (_probeAt.TryGetValue(aero, out var at) && now - at < System.Diagnostics.Stopwatch.Frequency / ProbeHz) return; _probeAt[aero] = now; }
        Vector3 g = WorldTransform.TransformDirectionInv(PhysicsHack.GetGravityDirection(data), wt);
        float weight = invMass > 0f ? g.Length() / invMass : 0f;
        float raw = data.TryGet<ActiveThrustData>(out var atd) ? (atd.ComputedThrustPerFrame * 60f).Length() : 0f;
        string vs = data.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement.VoluntaryThrustData>(out var vt) ? $"{vt.VoluntaryThrust.X:F3},{vt.VoluntaryThrust.Y:F3},{vt.VoluntaryThrust.Z:F3}" : "none";
        string ms = data.TryGet<Keen.Game2.Simulation.WorldObjects.Shared.Movement.MaxThrustData>(out var mt) ? $"+{mt.Regular.Positive.X:F0},{mt.Regular.Positive.Y:F0},{mt.Regular.Positive.Z:F0} -{mt.Regular.Negative.X:F0},{mt.Regular.Negative.Y:F0},{mt.Regular.Negative.Z:F0}" : "none";
        Vector3 rawV = atd.ComputedThrustPerFrame * 60f;
        Keen.VRage.Library.Diagnostics.Log.Default?.Info($"[AERO-THRUST-IN] {aero.Entity?.DebugName} voluntary {vs} regular {ms} rawV {rawV.X:F0},{rawV.Y:F0},{rawV.Z:F0} gameF {gameF.X:F0},{gameF.Y:F0},{gameF.Z:F0} gLocal {g.X:F2},{g.Y:F2},{g.Z:F2} mass {(invMass > 0 ? 1f / invMass : 0):F0}");
        Keen.VRage.Library.Diagnostics.Log.Default?.Info($"[AERO-THRUST] {(aero.IsServerScene ? "server" : "client")} {aero.Entity?.DebugName} mode {rep.Mode} gameF {gameF.Length():F0} N (raw active {raw:F0}) weight {weight:F0} N -> {(weight > 1f ? Vector3.Dot(gameF, -Vector3.Normalize(g)) / weight : 0f):F2} of it held | rcs use {rep.RcsUse:F3} err {rep.Error.Length() * 57.3f:F2} deg | t {now * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1} pos {wt.Position.X:F3} {wt.Position.Y:F3} {wt.Position.Z:F3} vel {aero.LastLinVel.X:F3} {aero.LastLinVel.Y:F3} {aero.LastLinVel.Z:F3}");
    }

    public static Report Apply(AeroGridComponent aero, in WorldTransform wt, Vector3 angVelWorld, float dt)
    {
        var rep = new Report { Mode = "off" };
        var thrusters = aero._thrusterCache;
        if (aero.IsServerScene) System.Threading.Interlocked.Increment(ref ServerCalls); else System.Threading.Interlocked.Increment(ref ClientCalls);
        if (!Enabled) { Unpublish(aero); return rep; }   // (the game's own flames again)
        var data = aero.Data;
        if (!data.TryGet<RigidBodyMassProperties>(out var mass) || mass.InvMass <= 0f) { Unpublish(aero); return rep; }
        // ── 0. Flown by the aero-aware law: the game gyros' turn this frame taken back (they are flown below) ──
        TargetControlData tcd = default;
        bool aware = AeroAware && aero.HasResult && aero.LastDensity > AwareMinDensity && !aero.SuppressPhantomTorque && !aero.HarnessControlsAttitude
            && data.TryGet<TargetControlData>(out tcd) && tcd.TargetOrientation.IsValidAndRotationIsNormalized() && aero.AttitudeTarget(wt, out _, out _);
        Vector3 gyroReach = Vector3.Zero;
        if (aware)
        {
            gyroReach = GyroControl.Reach(data, in mass, in tcd);
            if (gyroReach != Vector3.Zero)
            {
                if (!GyroControl.Ready) aware = false;   // (the game's gyro impulse unreadable: its law flies them, as before)
                else
                {
                    rep.GameGyroUndone = GyroControl.Undo(data, wt);
                    if (data.TryGet<RigidBodyData>(out var rbNow)) angVelWorld = rbNow.AngularVelocity;
                }
            }
            if (aware && thrusters.Count == 0) { rep.Mode = "pilot target (aero-aware, gyros)"; rep.Aware = true; FlyGyros(aero, wt, in mass, angVelWorld, gyroReach, dt, ref rep); return rep; }
        }
        if (thrusters.Count == 0) { Unpublish(aero); return rep; }
        Vector3 com = mass.CenterOfMass;
        int n = thrusters.Count;
        if (aero._rcsArm.Length != n)
        {
            aero._rcsArm = new Vector3[n]; aero._rcsDir = new Vector3[n]; aero._rcsCap = new float[n]; aero._rcsF = new float[n]; aero._thrustUse = new float[n];
            aero._rcsNom = new float[n]; aero._rcsClass = new Keen.Game2.Simulation.GameSystems.Movement.ThrustClassData[n]; aero._rcsHasClass = new bool[n];
        }
        var arms = aero._rcsArm; var dirs = aero._rcsDir; var caps = aero._rcsCap; var f = aero._rcsF; var f0 = aero._thrustUse;

        // ── 1. The game's thrust this frame (grid-local, N), as the game shares it: by direction, by capacity ──
        Vector3 gameF = GameThrust(data, aero.IsServerScene);
        // At rest - nothing pushing, not moving, not turning, no pilot steering: nothing to do (a parked ship cost
        // as much as a flying one, every frame)
        if (gameF.LengthSquared() < 1f && aero.LastSpeed < 0.05f && angVelWorld.LengthSquared() < 4e-6f && !data.Has<TargetControlData>())
        {
            rep.Mode = "rest";
            Publish(aero, wt, null);   // (every flame dark - still ours: handed back to the game's and taken again, they flickered)
            return rep;
        }
        // the thrusters' geometry: only when they or the centre of mass change (it was recomputed every frame)
        var cap = aero._rcsCapDir; var nom = aero._rcsNom;
        bool rebuilt = aero._rcsGeomCount != n;   // (a new thruster list: AeroSimJob resets the count)
        if (rebuilt || (aero._rcsGeomCom - com).LengthSquared() > 1e-4f)
        {
            aero._rcsGeomCount = n; aero._rcsGeomCom = com;
            if (rebuilt) ResolveClasses(aero, n);
            aero._rcsGeomAir = -1f;   // (the capacities, below)
            float sq = 0f;
            for (int i = 0; i < n; i++)
            {
                var t = thrusters[i];
                dirs[i] = t.ThrustDirection;                                       // the way it pushes the ship
                // (ThrustDirection IS the push: the game's MaxThrustData counts a Forward thruster as -Z thrust. Negated, the
                //  game's thrust was shared among the opposite thrusters and the offset torque taken at their positions -
                //  Tests/ThrustTests checks the grouping against the game's capacities on dumped ships)
                arms[i] = Vector3.Cross(t.GridLocalPosition - com, dirs[i]);       // torque per newton
                nom[i] = -1f;   // (its live maximum, below)
                sq += (t.GridLocalPosition - com).LengthSquared();
            }
            aero._rcsArmSq = sq;
        }
        // CAPACITIES: each thruster's maximum x its class's efficiency in the air the grid is in (the game's own
        // GetThrustEfficiency, on the grid's AirData - what its MaxThrustData, so gameF, is made of). Nominal maxima shared
        // the game's thrust onto atmospheric thrusters in vacuum, and a flame share (force / capacity) came out x efficiency
        // where the game multiplies by efficiency again: the flames showed u x eff^2, too dark. Only when the air changes.
        float air = data.TryGet<Keen.Game2.Simulation.GameSystems.RangedAffectGenerators.Atmosphere.AirData>(out var ad) ? ad.Density : 0f;
        // (each thruster's maximum NOW - a thruster switched off, unpowered or damaged counted at its rated power: the game
        //  pushed through the others and this shared it onto the dead one too, steered with it, and lit its flame - audit
        //  2026-10-10)
        bool liveChanged = false;
        for (int i = 0; i < n; i++)
        {
            float m = LiveMax(thrusters[i].ThrusterEntity, aero.IsServerScene);
            if (m < 0f) m = Math.Max(0f, thrusters[i].MaxPower);
            if (m != nom[i]) { nom[i] = m; liveChanged = true; }
        }
        if (liveChanged || MathF.Abs(air - aero._rcsGeomAir) > 1e-4f)
        {
            aero._rcsGeomAir = air;
            System.Array.Clear(cap, 0, 6);
            var cls = aero._rcsClass; var has = aero._rcsHasClass;
            for (int i = 0; i < n; i++)
            {
                float eff = has[i] ? Keen.Game2.Simulation.WorldObjects.CubeGrids.Movement.GridMovementCollectorComponent.GetThrustEfficiency(in cls[i], air, 0f) : 1f;
                caps[i] = nom[i] * eff;
                cap[ThrustAllocator.DirIndex(dirs[i])] += caps[i];
            }
        }
        float armSq = aero._rcsArmSq;
        Vector3 offsetTorque = ThrustAllocator.GameShare(gameF, arms, dirs, caps, cap, f0, n);
        rep.OffsetTorque = offsetTorque;   // the imbalance as the game shares it

        // ── 2. With an attitude target, a flight controller shares it instead ──
        Vector3 torque = offsetTorque, extraForce = Vector3.Zero;
        Vector3 w = WorldTransform.TransformDirectionInv(angVelWorld, wt);
        bool steered = aero.AttitudeTarget(wt, out Quaternion target, out rep.Mode);
        Vector3 tauNeed = Vector3.Zero;
        if (steered)
        {
            Vector3 accel = ThrustAllocator.RateCommand(wt.Orientation, target, w, Kp, MaxRate, ResponseTime, RestAngle, rep.Mode == "damp" ? DampRestRate : RestRate, out rep.Error);
            Vector3 tauTarget = accel == Vector3.Zero ? Vector3.Zero : Inertia(mass, accel);
            if (aware)
            {
                // the aero-aware law: all it needs (the air's torque answered); the thrusters asked for what the gyros cannot give
                tauNeed = AwareNeed(aero, in mass, w, rep.Error, gyroReach + ThrusterReach(aero, n), dt, out rep.AeroTorque);
                tauTarget = tauNeed - Vector3.Clamp(tauNeed, -gyroReach, gyroReach);
                rep.Mode += " (aero-aware)"; rep.Aware = true;
            }
            // Each thruster's force so the torque is the one wanted and the net force stays the game's (ThrustAllocator.Solve).
            // Warm start: last frame's sharing (targets move slowly, so the iterations add up over frames), else the game's.
            var prev = aero._rcsPrev; var y = aero._rcsY;
            if (prev.Length != n || !aero._rcsWarm || aero._rcsWarmGen != aero._thrusterGen) { aero._rcsWarmGen = aero._thrusterGen; aero._rcsPrev = prev = new float[n]; aero._rcsY = y = new float[n]; for (int i = 0; i < n; i++) f[i] = f0[i]; }
            ThrustAllocator.Solve(tauTarget, gameF, arms, dirs, caps, f0, f, prev, y, armSq, n, Iterations, out rep.AttitudePriority);
            for (int i = 0; i < n; i++) if (!float.IsFinite(f[i])) { for (int j = 0; j < n; j++) f[j] = f0[j]; aero._rcsWarm = false; rep.Mode += " (reset: not finite)"; break; }
            aero._rcsWarm = true;
            torque = Vector3.Zero; Vector3 total = Vector3.Zero;
            float moved = 0f, sumCap = 0f;
            for (int i = 0; i < n; i++) { torque += arms[i] * f[i]; total += dirs[i] * f[i]; moved += Math.Abs(f[i] - f0[i]); sumCap += caps[i]; }
            extraForce = total - gameF;   // (0 while the game's thrust is kept; with attitude priority, the thrust given up - applied below)
            rep.RcsUse = sumCap > 0f ? moved / sumCap : 0f;
        }
        else aero._rcsWarm = false;
        rep.RcsTorque = torque - offsetTorque; rep.RcsForce = extraForce;
        // the gyros: what the thrusters' torque left of the need, up to their reach
        if (rep.Aware && gyroReach != Vector3.Zero)
        {
            rep.GyroTorque = Vector3.Clamp(tauNeed - torque, -gyroReach, gyroReach);
            if (ApplyForces && rep.GyroTorque.LengthSquared() > 0f) AeroPhysics.ApplyForceAndTorque(aero.Entity, wt, Vector3.Zero, rep.GyroTorque, dt);
        }
        // (applied: 68bda99 replaced this call with the flame publishing below, and thrust stopped turning ships at all -
        //  offset torque and the controller's steering were computed, reported and shown as flames, never applied)
        // Torque only: the sharing keeps the game's net thrust (KeepNetThrust), so no linear force is ever added. (Applying
        // the solver's force difference cancelled the game's thrust while steering - the engines felt dead.)
        // Torque, and - only when the thrusters had to give up some of the game's thrust to hold attitude (a whole group at
        // full power: ThrustAllocator.MaxThrustLoss) - that thrust change, the thrusters' own (world axes).
        if (ApplyForces && torque.LengthSquared() > 0f)
            AeroPhysics.ApplyForceAndTorque(aero.Entity, wt, rep.AttitudePriority > 0f ? WorldTransform.TransformDirection(extraForce, wt) : Vector3.Zero, torque, dt);
        // The flames: each thruster's share, published for the client copy to show - the controller's sharing while it
        // steers, the game's own otherwise (free, rate input: the gyros turn it), so a flame is never handed back mid-flight.
        Publish(aero, wt, steered ? f : f0);
        if (Probe) ProbeLog(aero, data, wt, gameF, mass.InvMass, rep);
        return rep;
    }

    /// <summary>Each thruster's thrust class (ThrustData.ThrustClass -> the game's ThrustClassesConfiguration): on a new
    /// thruster list only. None found: efficiency 1 (the nominal maximum, as before).</summary>
    static Keen.Game2.Simulation.GameSystems.Movement.ThrustClassesConfiguration _classes;
    /// <summary>A thruster's thrust efficiency in air of this density (its class's curve: the game's GetThrustEfficiency); 1
    /// when its class is not known.</summary>
    internal static float SeaLevelEfficiency(Entity thruster, float density)
    {
        if (_classes == null) { try { _classes = DefinitionManager.Instance?.GetConfiguration<Keen.Game2.Simulation.GameSystems.Movement.ThrustClassesConfiguration>(); } catch { _classes = null; } }
        var d = thruster?.Data;
        if (_classes == null || d == null || !PhysicsHack.Alive(d.Value) || !d.Value.TryGet<ThrustData>(out var td) || !_classes.ThrustClasses.TryGetValue(td.ThrustClass, out var cd)) return 1f;
        return Keen.Game2.Simulation.WorldObjects.CubeGrids.Movement.GridMovementCollectorComponent.GetThrustEfficiency(in cd, density, 0f);
    }

    static void ResolveClasses(AeroGridComponent aero, int n)
    {
        if (_classes == null)
        {
            try { _classes = DefinitionManager.Instance?.GetConfiguration<Keen.Game2.Simulation.GameSystems.Movement.ThrustClassesConfiguration>(); }
            catch { _classes = null; }   // (not loaded yet: tried again on the next new thruster list)
        }
        var thrusters = aero._thrusterCache; var cls = aero._rcsClass; var has = aero._rcsHasClass;
        for (int i = 0; i < n; i++)
        {
            has[i] = false;
            var e = thrusters[i].ThrusterEntity;
            if (_classes == null || e == null || !PhysicsHack.Alive(e.Data)) continue;
            if (e.Data.TryGet<ThrustData>(out var td) && _classes.ThrustClasses.TryGetValue(td.ThrustClass, out var cd)) { cls[i] = cd; has[i] = true; }
        }
    }

    // -- Flames --
    // A thruster's flame is the game's override power when it has one (ThrusterEffectsComponent: OverridePower x
    // efficiency), else its direction's share of the grid's voluntary thrust, alike for every thruster facing that
    // way. The SERVER copy of a grid publishes each thruster's share every frame - force / capacity (capacity already x
    // efficiency, so the game's own x efficiency makes it right), keyed by the thruster block's cells in its grid (the
    // same in both copies, at any speed). Each CLIENT thruster (AeroFlameComponent) finds its grid's record and its own
    // share, eases to it and sets its own override, visual only. Published in every mode while the grid exists (zero at
    // rest): the override is never handed back to the game mid-flight (that snap was the flicker).
    /// <summary>A grid's shares: Index (block cells -> slot) is built once per thruster list and never changed after,
    /// Share written in place each frame - a client reads both without the lock.</summary>
    internal sealed class Table { public int Gen; public float[] Share; public readonly Dictionary<Vector3I, int> Index = new(); }
    /// <summary>One server grid's record: where it is (to match its client copy) and its shares.</summary>
    internal sealed class Published
    {
        public Vector3D Pos; public Vector3 Vel; public Quaternion Orient = Quaternion.Identity;
        public Vector3 BoxMin, BoxMax; public bool HasBox;   // (its local bounds: its client copy has the same - another grid does not)
        public long Stamp; public volatile bool Gone; public volatile Table Table;
    }
    static readonly Dictionary<AeroGridComponent, Published> _published = new();
    static volatile int _publishedCount;
    /// <summary>Thruster flames for the thrusters' shares (harness: aeroflames on|off).</summary>
    public static bool FlamesEnabled = true;
    public static bool AnyPublished => _publishedCount > 0;
    /// <summary>A record this fresh is matched; one staler is still HELD (its last shares shown) up to HoldSeconds - a
    /// late server frame never drops a flame - after which the flame is the game's again.</summary>
    public const double FreshSeconds = 1.0, HoldSeconds = 5.0;
    /// <summary>A grid leaving the scene: its published shares dropped (a grid destroyed while piloted stayed forever).</summary>
    internal static void Forget(AeroGridComponent aero)
    {
        lock (_published) { if (_published.Remove(aero, out var gone)) { gone.Gone = true; _publishedCount = _published.Count; } }
        aero._flamePub = null;
    }
    static void Unpublish(AeroGridComponent aero) { if (aero._flamePub != null) Forget(aero); }
    public static volatile string ClientNote = "-";
    public static int ServerCalls, ClientCalls, FlameLookups, FlameHits, FlameLit;

    /// <summary>The server copy's shares this frame: use[i] / caps[i] (use null: all zero).</summary>
    static void Publish(AeroGridComponent aero, in WorldTransform wt, float[] use)
    {
        // (the client copy runs Apply too, for its own prediction: its publish, 0 m from its own thrusters, won the match)
        if (!aero.IsServerScene) return;
        if (!FlamesEnabled) { Unpublish(aero); return; }
        var p = aero._flamePub;
        if (p == null)
        {
            p = new Published();
            lock (_published) { _published[aero] = p; _publishedCount = _published.Count; }
            aero._flamePub = p;
        }
        var thrusters = aero._thrusterCache; int n = thrusters.Count;
        var tab = p.Table;
        if (tab == null || tab.Gen != aero._thrusterGen || tab.Share.Length != n)
        {
            // (a new thruster list: a new table - a client holding the old one finds it replaced and matches again)
            var t = new Table { Gen = aero._thrusterGen, Share = new float[n] };
            for (int i = 0; i < n; i++) t.Index[thrusters[i].Cell] = i;
            p.Table = tab = t;
        }
        var share = tab.Share; var caps = aero._rcsCap;
        for (int i = 0; i < n; i++)
            share[i] = use != null && i < use.Length && i < caps.Length && caps[i] > 0f ? Math.Clamp(use[i] / caps[i], 0f, 1f) : 0f;
        bool hasBox = aero.Data.TryGet<BoundingBoxData>(out var lb);
        lock (_published)
        {
            p.Pos = wt.Position; p.Orient = wt.Orientation; p.Vel = aero.LastLinVel;
            p.HasBox = hasBox; if (hasBox) { p.BoxMin = lb.BoundingBox.Min; p.BoxMax = lb.BoundingBox.Max; }
            p.Stamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    /// <summary>A client thruster's share this frame (0..1), or -1: none (no record matched, or no place in its grid).
    /// The record matched last time is read without a search or the lock; it is checked again every 0.25 s (every frame
    /// while unmatched after it had one), or at once when it is gone or re-keyed.</summary>
    internal static float FlameShare(AeroFlameComponent flame, long now)
    {
        Interlocked.Increment(ref FlameLookups);
        double freq = System.Diagnostics.Stopwatch.Frequency;
        if (!flame._hasKey)
        {
            var block = flame.Entity?.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent>();
            if (block == null) return -1f;
            var bb = block.AABB; flame._key = bb.Min + bb.Max; flame._hasKey = true;
        }
        var m = flame._match; var tab = flame._table;
        if (m == null ? now >= flame._recheckAt : (m.Gone || m.Table != tab || now >= flame._recheckAt))
        {
            var grid = flame.Entity?.GetTopLevelParent();
            if (grid == null || !Find(grid, flame._key, m, now, out m, out tab, out int idx))
            {
                flame._match = null; flame._table = null;
                flame._recheckAt = flame.Shown >= 0f ? now : now + (long)(freq * 0.25);
                return -1f;
            }
            flame._match = m; flame._table = tab; flame._idx = idx;
            flame._recheckAt = now + (long)(freq * 0.25);
        }
        // (a record published before its table, or re-keyed shorter: no share this frame - it read a null table and crashed
        //  the game, 2026-10-09)
        if (tab == null || tab.Share == null || (uint)flame._idx >= (uint)tab.Share.Length)
        {
            flame._match = null; flame._table = null; flame._recheckAt = now;
            return -1f;
        }
        float s = tab.Share[flame._idx];
        Interlocked.Increment(ref FlameHits);
        if (s > 0.05f) Interlocked.Increment(ref FlameLit);
        return s;
    }

    /// <summary>
    /// The server record for a client grid: its own local bounds (another grid - a pod, a rotor part - has other bounds),
    /// turned the same way (5 deg; 20 for the one kept: the record is a step old while the ship turns), where the record
    /// says it is now (its position + velocity x age: within 20 m + 0.1 s of travel; 20 m more for the one kept), and
    /// with this thruster's block in it. The one kept wins while fresh; else the nearest fresh one; else the one kept is
    /// held while under HoldSeconds old.
    /// </summary>
    static bool Find(Entity grid, Vector3I key, Published kept, long now, out Published match, out Table table, out int idx)
    {
        match = null; table = null; idx = -1;
        var gwt = grid.Data.GetWorldTransform();
        bool cBox = grid.Data.TryGet<BoundingBoxData>(out var cb);
        Vector3 cMin = cBox ? cb.BoundingBox.Min : Vector3.Zero, cMax = cBox ? cb.BoundingBox.Max : Vector3.Zero;
        double freq = System.Diagnostics.Stopwatch.Frequency;
        lock (_published)
        {
            bool Fits(Published p, double extra, double deg, double maxAge, out double dist, out Table t, out int i)
            {
                dist = 0; t = null; i = -1;
                if (p == null || p.Gone) return false;
                double age = (now - p.Stamp) / freq;
                if (age > maxAge) return false;
                t = p.Table;
                if (t == null || !t.Index.TryGetValue(key, out i) || i >= t.Share.Length) return false;
                if (cBox && p.HasBox && ((p.BoxMin - cMin).Length() > 0.5f || (p.BoxMax - cMax).Length() > 0.5f)) return false;
                float dot = MathF.Abs(Quaternion.Dot(p.Orient, gwt.Orientation));
                if (2.0 * Math.Acos(Math.Min(1.0, dot)) > deg * Math.PI / 180.0) return false;
                dist = (p.Pos + (Vector3D)(p.Vel * (float)Math.Max(0.0, age)) - gwt.Position).Length();
                return dist <= 20.0 + 0.1 * p.Vel.Length() + extra;
            }
            if (Fits(kept, 20.0, 20.0, FreshSeconds, out _, out var kt, out int ki)) { match = kept; table = kt; idx = ki; return true; }
            double bestD = double.MaxValue;
            foreach (var p in _published.Values)
            {
                if (p == kept || !Fits(p, 0.0, 5.0, FreshSeconds, out double d, out var t, out int i) || d >= bestD) continue;
                bestD = d; match = p; table = t; idx = i;
            }
            if (match != null) return true;
            if (Fits(kept, 20.0, 20.0, HoldSeconds, out _, out kt, out ki)) { match = kept; table = kt; idx = ki; return true; }   // (stale: held)
            return false;
        }
    }

    /// <summary>The aero-aware law's whole ask (local N m): I x AttitudeAccel - the air's torque. reach: the most torque per
    /// axis the actuators give (gyros + thrusters); the rate read without this frame's air kick (applied before this ran).</summary>
    static Vector3 AwareNeed(AeroGridComponent aero, in RigidBodyMassProperties mass, Vector3 w, Vector3 error, Vector3 reach, float dt, out Vector3 aeroTorque)
    {
        aeroTorque = aero.LastAeroTorque;
        Vector3 d = DeltaW(in mass, aeroTorque);   // (rad/s^2)
        var axisReach = new Vector3(AxisAccel(in mass, Vector3.UnitX, reach.X), AxisAccel(in mass, Vector3.UnitY, reach.Y), AxisAccel(in mass, Vector3.UnitZ, reach.Z));
        Vector3 acc = ThrustAllocator.AttitudeAccel(error, w - d * dt, axisReach, d, RestAngle, RestRate);
        return Inertia(mass, acc) - aeroTorque;
    }

    /// <summary>The angular acceleration (local) a torque gives.</summary>
    static Vector3 DeltaW(in RigidBodyMassProperties m, Vector3 torque)
        => Vector3.Transform(m.InvInertiaMajorAxisRotation * torque * m.InvInertiaTensor, m.InertiaMajorAxisRotation);

    /// <summary>The acceleration a torque of size t gives about a local axis.</summary>
    static float AxisAccel(in RigidBodyMassProperties m, Vector3 axis, float t) => t > 0f ? MathF.Abs(Vector3.Dot(DeltaW(in m, axis * t), axis)) : 0f;

    /// <summary>A rough reach of the thrusters about each local axis (N m): half the summed torque of every thruster's
    /// maximum about it - they turn the ship in pairs, the game's thrust kept. Only the law's braking plan reads it.</summary>
    static Vector3 ThrusterReach(AeroGridComponent aero, int n)
    {
        var arms = aero._rcsArm; var caps = aero._rcsCap; Vector3 r = Vector3.Zero;
        for (int i = 0; i < n && i < arms.Length && i < caps.Length; i++) r += Vector3.Abs(arms[i]) * caps[i];
        return r * 0.5f;
    }

    /// <summary>A grid with gyros and no thrusters, flown by the aero-aware law: the gyros alone.</summary>
    static void FlyGyros(AeroGridComponent aero, in WorldTransform wt, in RigidBodyMassProperties mass, Vector3 angVelWorld, Vector3 gyroReach, float dt, ref Report rep)
    {
        if (gyroReach == Vector3.Zero || !aero.AttitudeTarget(wt, out var target, out _)) return;
        Vector3 w = WorldTransform.TransformDirectionInv(angVelWorld, wt);
        ThrustAllocator.RateCommand(wt.Orientation, target, w, Kp, MaxRate, ResponseTime, RestAngle, RestRate, out rep.Error);
        Vector3 need = AwareNeed(aero, in mass, w, rep.Error, gyroReach, dt, out rep.AeroTorque);
        rep.GyroTorque = Vector3.Clamp(need, -gyroReach, gyroReach);
        if (ApplyForces && rep.GyroTorque.LengthSquared() > 0f) AeroPhysics.ApplyForceAndTorque(aero.Entity, wt, Vector3.Zero, rep.GyroTorque, dt);
    }

    /// <summary>The torque that changes the angular velocity by dw (grid-local) in one second (the inverse of the
    /// game's ComputeDeltaAngularVelocity).</summary>
    static Vector3 Inertia(in RigidBodyMassProperties m, Vector3 dw)
    {
        Vector3 p = m.InvInertiaMajorAxisRotation * dw;
        Vector3 inv = m.InvInertiaTensor;
        var j = new Vector3(inv.X > 0f ? p.X / inv.X : 0f, inv.Y > 0f ? p.Y / inv.Y : 0f, inv.Z > 0f ? p.Z / inv.Z : 0f);
        return Vector3.Transform(j, m.InertiaMajorAxisRotation);
    }
}
