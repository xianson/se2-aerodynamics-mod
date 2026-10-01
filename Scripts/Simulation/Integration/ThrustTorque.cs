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
/// is applied as physics impulses: it shows no flame and burns no fuel yet.)
/// </summary>
public static class ThrustTorque
{
    public static bool Enabled = true;
    /// <summary>rad/s of target rate per rad of error, and the most it asks for.</summary>
    public const float Kp = 1.2f, MaxRate = 0.8f;
    /// <summary>The time it tries to reach the target rate in (s): shorter is stiffer.</summary>
    public const float ResponseTime = 0.3f;
    /// <summary>Below this error and rate the hold rests (no chatter).</summary>
    public const float RestAngle = 0.002f, RestRate = 0.002f;
    /// <summary>Allocation solver iterations (projected gradient).</summary>
    public const int Iterations = 60;

    /// <summary>Last frame's numbers (for the harness / log).</summary>
    /// <summary>Last frame's numbers: the imbalance as the game shares the thrust (OffsetTorque), what the controller's
    /// sharing changes (RcsTorque, RcsForce), the attitude error, and how much thrust it moved (RcsUse).</summary>
    public struct Report { public Vector3 OffsetTorque, RcsTorque, RcsForce, Error; public float RcsUse; public string Mode; }

    public static Report Apply(AeroGridComponent aero, in WorldTransform wt, Vector3 angVelWorld, float dt)
    {
        var rep = new Report { Mode = "off" };
        var thrusters = aero._thrusterCache;
        if (aero.IsServerScene) System.Threading.Interlocked.Increment(ref ServerCalls); else System.Threading.Interlocked.Increment(ref ClientCalls);
        if (!Enabled || thrusters.Count == 0) return rep;
        var data = aero.Data;
        if (!data.TryGet<RigidBodyMassProperties>(out var mass) || mass.InvMass <= 0f) return rep;
        Vector3 com = mass.CenterOfMass;
        int n = thrusters.Count;
        if (aero._rcsArm.Length != n)
        {
            aero._rcsArm = new Vector3[n]; aero._rcsDir = new Vector3[n]; aero._rcsCap = new float[n]; aero._rcsF = new float[n]; aero._thrustUse = new float[n];
        }
        var arms = aero._rcsArm; var dirs = aero._rcsDir; var caps = aero._rcsCap; var f = aero._rcsF; var f0 = aero._thrustUse;

        // ── 1. The game's thrust this frame (grid-local, N), as the game shares it: by direction, by capacity ──
        Vector3 gameF = data.TryGet<ActiveThrustData>(out var atd) ? atd.ComputedThrustPerFrame * 60f : Vector3.Zero;
        // At rest - nothing pushing, not moving, not turning, no pilot steering: nothing to do (a parked ship cost
        // as much as a flying one, every frame)
        if (gameF.LengthSquared() < 1f && aero.LastSpeed < 0.05f && angVelWorld.LengthSquared() < 4e-6f && !data.Has<TargetControlData>())
        {
            rep.Mode = "rest";
            Publish(aero, wt, false);
            return rep;
        }
        // the thrusters' geometry: only when they or the centre of mass change (it was recomputed every frame)
        var cap = aero._rcsCapDir;
        if (aero._rcsGeomCount != n || (aero._rcsGeomCom - com).LengthSquared() > 1e-4f)
        {
            aero._rcsGeomCount = n; aero._rcsGeomCom = com;
            System.Array.Clear(cap, 0, 6);
            float sq = 0f;
            for (int i = 0; i < n; i++)
            {
                var t = thrusters[i];
                dirs[i] = -t.ThrustDirection;                                      // the way it pushes the ship
                arms[i] = Vector3.Cross(t.GridLocalPosition - com, dirs[i]);       // torque per newton
                caps[i] = Math.Max(0f, t.MaxPower);
                cap[DirIndex(dirs[i])] += caps[i];
                sq += (t.GridLocalPosition - com).LengthSquared();
            }
            aero._rcsArmSq = sq;
        }
        float armSq = aero._rcsArmSq;
        Vector3 offsetTorque = Vector3.Zero;
        for (int i = 0; i < n; i++)
        {
            float along = Vector3.Dot(gameF, dirs[i]);
            int k = DirIndex(dirs[i]);
            f0[i] = along > 0f && cap[k] > 0f ? Math.Min(caps[i], along * caps[i] / cap[k]) : 0f;
            offsetTorque += arms[i] * f0[i];
        }
        rep.OffsetTorque = offsetTorque;   // the imbalance as the game shares it

        // ── 2. With an attitude target, a flight controller shares it instead ──
        Vector3 torque = offsetTorque, extraForce = Vector3.Zero;
        Vector3 w = WorldTransform.TransformDirectionInv(angVelWorld, wt);
        if (aero.AttitudeTarget(wt, out Quaternion target, out rep.Mode))
        {
            Quaternion err = Quaternion.Normalize(Quaternion.Inverse(wt.Orientation) * target);
            if (err.W < 0f) err = new Quaternion(-err.X, -err.Y, -err.Z, -err.W);   // the short way round
            Vector3 axis = new Vector3(err.X, err.Y, err.Z);
            float s = axis.Length();
            Vector3 e = s > 1e-6f ? axis * (2f * MathF.Atan2(s, err.W) / s) : Vector3.Zero;   // axis-angle, local
            rep.Error = e;
            Vector3 wDes = e * Kp;
            float wl = wDes.Length();
            if (wl > MaxRate) wDes *= MaxRate / wl;
            bool resting = e.Length() <= RestAngle && w.Length() <= RestRate;
            Vector3 tauTarget = resting ? Vector3.Zero : Inertia(mass, (wDes - w) / ResponseTime);
            // Each thruster's force f_i (0..max) so that the torque is the one wanted and the total force stays the
            // game's (its translation, dampening and gravity compensation): minimise
            //   |sum a_i f_i - tauTarget|^2 + mu |sum d_i f_i - gameF|^2
            // (mu: the mean arm squared, so force and torque weigh alike). Unbalanced hover thrust is rebalanced by
            // throttling some thrusters down and others up; spare thrusters add couples. Projected gradient from the
            // game's own sharing.
            float mu = n > 0 ? Math.Max(armSq / n, 1f) : 1f;
            // Warm start: last frame's sharing (targets move slowly, so the iterations add up over frames), else the
            // game's. Accelerated (FISTA): rebalancing at constant total force lies along the problem's weak
            // directions, where plain gradient steps crawl.
            var prev = aero._rcsPrev; var y = aero._rcsY;
            if (prev.Length != n || !aero._rcsWarm) { aero._rcsPrev = prev = new float[n]; aero._rcsY = y = new float[n]; for (int i = 0; i < n; i++) f[i] = f0[i]; }
            else for (int i = 0; i < n; i++) f[i] = Math.Clamp(f[i], 0f, caps[i]);
            for (int i = 0; i < n; i++) { prev[i] = f[i]; y[i] = f[i]; }
            float lmax = LargestEigen(arms, dirs, mu, n);
            float step = lmax > 0f ? 1f / lmax : 0f;
            float tk = 1f;
            for (int it = 0; it < Iterations && step > 0f; it++)
            {
                Vector3 tr = -tauTarget, fr = -gameF;
                for (int i = 0; i < n; i++) { tr += arms[i] * y[i]; fr += dirs[i] * y[i]; }
                float tk1 = (1f + MathF.Sqrt(1f + 4f * tk * tk)) / 2f, mom = (tk - 1f) / tk1;
                for (int i = 0; i < n; i++)
                {
                    float fi = Math.Clamp(y[i] - step * (Vector3.Dot(arms[i], tr) + mu * Vector3.Dot(dirs[i], fr)), 0f, caps[i]);
                    y[i] = Math.Clamp(fi + mom * (fi - prev[i]), 0f, caps[i]);
                    prev[i] = fi; f[i] = fi;
                }
                tk = tk1;
            }
            aero._rcsWarm = true;
            torque = Vector3.Zero; Vector3 total = Vector3.Zero;
            float moved = 0f, sumCap = 0f;
            for (int i = 0; i < n; i++) { torque += arms[i] * f[i]; total += dirs[i] * f[i]; moved += Math.Abs(f[i] - f0[i]); sumCap += caps[i]; }
            extraForce = total - gameF;   // (the game applies gameF itself; what the controller's sharing adds)
            rep.RcsUse = sumCap > 0f ? moved / sumCap : 0f;
        }
        else aero._rcsWarm = false;
        rep.RcsTorque = torque - offsetTorque; rep.RcsForce = extraForce;
        // The flames: published for the client copy (which runs no simulation) to show.
        Publish(aero, wt, rep.Mode == "pilot target" || rep.Mode == "hold");
        return rep;
    }

    // -- Flames --
    // A thruster's flame is the game's override power when it has one (ThrusterEffectsComponent: OverridePower x
    // efficiency), else its direction's share of the grid's voluntary thrust, alike for every thruster facing that
    // way. The aero simulation runs on the server copy of a grid only, so while its controller shares the thrust it
    // publishes each thruster's share (force / max), keyed by the thruster's place in its grid; each CLIENT thruster
    // (AeroFlameComponent) looks its share up and sets its own override, visual only.
    sealed class Published { public Vector3D Pos; public Dictionary<Vector3I, float> Share = new(); public long Stamp; }
    static readonly Dictionary<AeroGridComponent, Published> _published = new();
    static volatile int _publishedCount;
    /// <summary>Thruster flames for the controller's sharing (harness: aeroflames on|off).</summary>
    public static bool FlamesEnabled = true;
    public static bool AnyPublished => _publishedCount > 0;
    public static volatile string ClientNote = "-";
    public static int ServerCalls, ClientCalls, FlameLookups, FlameHits, FlameLit;

    /// <summary>A position's key in its grid: grid-local, quarter metres (both copies of a grid agree on it).</summary>
    static Vector3I Key(in WorldTransform grid, Vector3D world)
    {
        Vector3 l = WorldTransform.TransformDirectionInv((Vector3)(world - grid.Position), grid);
        return new Vector3I((int)MathF.Round(l.X * 4f), (int)MathF.Round(l.Y * 4f), (int)MathF.Round(l.Z * 4f));
    }

    static void Publish(AeroGridComponent aero, in WorldTransform wt, bool steering)
    {
        lock (_published)
        {
            if (!steering || !FlamesEnabled) { if (_published.Remove(aero)) _publishedCount = _published.Count; return; }
            if (!_published.TryGetValue(aero, out var p)) { _published[aero] = p = new Published(); _publishedCount = _published.Count; }
            var thrusters = aero._thrusterCache; var f = aero._rcsF; var caps = aero._rcsCap;
            p.Pos = wt.Position; p.Stamp = System.Diagnostics.Stopwatch.GetTimestamp();
            p.Share.Clear();
            for (int i = 0; i < thrusters.Count && i < f.Length; i++)
            {
                var e = thrusters[i].ThrusterEntity;
                if (e == null || !PhysicsHack.Alive(e.Data)) continue;
                p.Share[Key(wt, e.Data.GetWorldTransform().Position)] = caps[i] > 0f ? Math.Clamp(f[i] / caps[i], 0f, 1f) : 0f;
            }
        }
    }

    /// <summary>A client thruster's share: the published grid nearest its grid (fresh, within 30 m), its place in it.
    /// -1: none published.</summary>
    public static float ShareAt(in WorldTransform grid, Vector3D thrusterWorld)
    {
        Interlocked.Increment(ref FlameLookups);
        long now = System.Diagnostics.Stopwatch.GetTimestamp(), fresh = System.Diagnostics.Stopwatch.Frequency / 2;
        var key = Key(grid, thrusterWorld);
        lock (_published)
        {
            Published best = null; double bestD = 30.0;
            foreach (var kv in _published)
            {
                var p = kv.Value;
                if (now - p.Stamp > fresh) continue;
                double d = (p.Pos - grid.Position).Length();
                if (d < bestD && p.Share.ContainsKey(key)) { bestD = d; best = p; }
            }
            if (best == null) return -1f;
            Interlocked.Increment(ref FlameHits);
            float s = best.Share[key];
            if (s > 0.05f) Interlocked.Increment(ref FlameLit);
            return s;
        }
    }

    /// <summary>The largest eigenvalue of sum v v^T, v = (arm, sqrt(mu) dir) in R^6 (power iteration): the
    /// gradient step's bound. (The trace is a safe bound too, but far too timid with many thrusters.)</summary>
    static float LargestEigen(Vector3[] arms, Vector3[] dirs, float mu, int n)
    {
        Span<float> G = stackalloc float[36];
        float sm = MathF.Sqrt(mu);
        Span<float> v = stackalloc float[6];   // (once: stackalloc in the loop held stack per thruster until return)
        for (int i = 0; i < n; i++)
        {
            v[0] = arms[i].X; v[1] = arms[i].Y; v[2] = arms[i].Z; v[3] = sm * dirs[i].X; v[4] = sm * dirs[i].Y; v[5] = sm * dirs[i].Z;
            for (int r = 0; r < 6; r++) for (int c = 0; c < 6; c++) G[r * 6 + c] += v[r] * v[c];
        }
        Span<float> x = stackalloc float[6] { 1f, 0.9f, 0.8f, 0.7f, 0.6f, 0.5f };
        Span<float> y = stackalloc float[6];
        float lambda = 0f;
        for (int it = 0; it < 30; it++)
        {
            float norm = 0f;
            for (int r = 0; r < 6; r++) { float acc = 0f; for (int c = 0; c < 6; c++) acc += G[r * 6 + c] * x[c]; y[r] = acc; norm += acc * acc; }
            norm = MathF.Sqrt(norm);
            if (norm <= 0f) return 0f;
            for (int r = 0; r < 6; r++) x[r] = y[r] / norm;
            lambda = norm;
        }
        return lambda * 1.05f;
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

    /// <summary>Which of the six axis directions a unit vector is (the largest component).</summary>
    static int DirIndex(Vector3 d)
    {
        float ax = MathF.Abs(d.X), ay = MathF.Abs(d.Y), az = MathF.Abs(d.Z);
        if (ax >= ay && ax >= az) return d.X >= 0f ? 0 : 1;
        if (ay >= az) return d.Y >= 0f ? 2 : 3;
        return d.Z >= 0f ? 4 : 5;
    }
}
