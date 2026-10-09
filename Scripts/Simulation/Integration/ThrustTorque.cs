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
    /// <summary>Allocation solver iterations (projected gradient).</summary>
    public const int Iterations = 60;

    /// <summary>Last frame's numbers (for the harness / log).</summary>
    /// <summary>Last frame's numbers: the imbalance as the game shares the thrust (OffsetTorque), what the controller's
    /// sharing changes (RcsTorque, RcsForce), the attitude error, and how much thrust it moved (RcsUse).</summary>
    public struct Report { public Vector3 OffsetTorque, RcsTorque, RcsForce, Error; public float RcsUse, AttitudePriority; public string Mode; }

    public static Report Apply(AeroGridComponent aero, in WorldTransform wt, Vector3 angVelWorld, float dt)
    {
        var rep = new Report { Mode = "off" };
        var thrusters = aero._thrusterCache;
        if (aero.IsServerScene) System.Threading.Interlocked.Increment(ref ServerCalls); else System.Threading.Interlocked.Increment(ref ClientCalls);
        if (!Enabled || thrusters.Count == 0) { Unpublish(aero); return rep; }   // (the game's own flames again)
        var data = aero.Data;
        if (!data.TryGet<RigidBodyMassProperties>(out var mass) || mass.InvMass <= 0f) { Unpublish(aero); return rep; }
        Vector3 com = mass.CenterOfMass;
        int n = thrusters.Count;
        if (aero._rcsArm.Length != n)
        {
            aero._rcsArm = new Vector3[n]; aero._rcsDir = new Vector3[n]; aero._rcsCap = new float[n]; aero._rcsF = new float[n]; aero._thrustUse = new float[n];
            aero._rcsNom = new float[n]; aero._rcsClass = new Keen.Game2.Simulation.GameSystems.Movement.ThrustClassData[n]; aero._rcsHasClass = new bool[n];
        }
        var arms = aero._rcsArm; var dirs = aero._rcsDir; var caps = aero._rcsCap; var f = aero._rcsF; var f0 = aero._thrustUse;

        // ── 1. The game's thrust this frame (grid-local, N), as the game shares it: by direction, by capacity ──
        Vector3 gameF = data.TryGet<ActiveThrustData>(out var atd) ? atd.ComputedThrustPerFrame * 60f : Vector3.Zero;
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
                nom[i] = Math.Max(0f, t.MaxPower);
                sq += (t.GridLocalPosition - com).LengthSquared();
            }
            aero._rcsArmSq = sq;
        }
        // CAPACITIES: each thruster's maximum x its class's efficiency in the air the grid is in (the game's own
        // GetThrustEfficiency, on the grid's AirData - what its MaxThrustData, so gameF, is made of). Nominal maxima shared
        // the game's thrust onto atmospheric thrusters in vacuum, and a flame share (force / capacity) came out x efficiency
        // where the game multiplies by efficiency again: the flames showed u x eff^2, too dark. Only when the air changes.
        float air = data.TryGet<Keen.Game2.Simulation.GameSystems.RangedAffectGenerators.Atmosphere.AirData>(out var ad) ? ad.Density : 0f;
        if (MathF.Abs(air - aero._rcsGeomAir) > 1e-4f)
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
        if (steered)
        {
            Vector3 accel = ThrustAllocator.RateCommand(wt.Orientation, target, w, Kp, MaxRate, ResponseTime, RestAngle, RestRate, out rep.Error);
            Vector3 tauTarget = accel == Vector3.Zero ? Vector3.Zero : Inertia(mass, accel);
            // Each thruster's force so the torque is the one wanted and the net force stays the game's (ThrustAllocator.Solve).
            // Warm start: last frame's sharing (targets move slowly, so the iterations add up over frames), else the game's.
            var prev = aero._rcsPrev; var y = aero._rcsY;
            if (prev.Length != n || !aero._rcsWarm) { aero._rcsPrev = prev = new float[n]; aero._rcsY = y = new float[n]; for (int i = 0; i < n; i++) f[i] = f0[i]; }
            ThrustAllocator.Solve(tauTarget, gameF, arms, dirs, caps, f0, f, prev, y, armSq, n, Iterations, out rep.AttitudePriority);
            aero._rcsWarm = true;
            torque = Vector3.Zero; Vector3 total = Vector3.Zero;
            float moved = 0f, sumCap = 0f;
            for (int i = 0; i < n; i++) { torque += arms[i] * f[i]; total += dirs[i] * f[i]; moved += Math.Abs(f[i] - f0[i]); sumCap += caps[i]; }
            extraForce = total - gameF;   // (0 while the game's thrust is kept; with attitude priority, the thrust given up - applied below)
            rep.RcsUse = sumCap > 0f ? moved / sumCap : 0f;
        }
        else aero._rcsWarm = false;
        rep.RcsTorque = torque - offsetTorque; rep.RcsForce = extraForce;
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
        return rep;
    }

    /// <summary>Each thruster's thrust class (ThrustData.ThrustClass -> the game's ThrustClassesConfiguration): on a new
    /// thruster list only. None found: efficiency 1 (the nominal maximum, as before).</summary>
    static Keen.Game2.Simulation.GameSystems.Movement.ThrustClassesConfiguration _classes;
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
