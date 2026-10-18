#pragma warning disable
using System;
using Keen.VRage.Core;

namespace AeroMod;

/// <summary>
/// HELICOPTER ROTOR CONTROL (2026-10-16, the user: "real rotor controls"). The rotor block is a thruster pushing along its
/// Up, so COLLECTIVE is the game's own up / down thrust (Space / C, the dampeners' hover hold - AeroPropulsorComponent
/// shapes its maximum). CYCLIC and the ANTI-TORQUE are actuators of ThrustTorque's attitude law, like the gyros and the
/// control surfaces - not a law of their own:
///   cyclic       tilting the disc tilts its thrust T: about the centre of mass that is T x h x sin(tilt) (h: the hub's
///                height above the centre of mass along the rotor axis) plus the hub's own moment (offset hinges:
///                HubMoment x T x R). Reach per rotor = T x (max(h, 0) x sin(MaxTilt) + HubMoment x R) about the two in-plane
///                axes. The tilt's share also moves the ship: its thrust's in-plane part, T x sin(tilt) = torque / h.
///   reaction     (2026-10-16, the user: "you should need to counter the rotation") the rotor's REACTION TORQUE acts on the
///                ship: the engine turns the rotor clockwise seen from above (RotorKinematics), so the ship is turned the
///                other way about the rotor axis by Q = P / omega - the power to hold the thrust (ideal induced power
///                x InducedFactor + the blades' profile power) over the governed rpm (ReactionTorque; ThrustTorque adds it to
///                the thrusters' offset torque and the attitude law asks the actuators to answer it). It must be countered:
///                a tail rotor (any thruster pushing sideways at an arm), gyros, control surfaces, or a second rotor - there
///                is no built-in anti-torque (the old YawArm / Reaction-off fallback was removed 2026-10-17).
///                Its size is calibrated to a real helicopter's (RotorKinematics.ReactionScale), and the CONTROLLABILITY LIMITER
///                (Limit, RotorLimiter) caps each rotor's thrust so its reaction stays within what the grid can hold.
///                (The Reversible Rotor block is NOT one of these rotors - PropulsorKinds.HasCyclic: no cyclic, no reaction;
///                a reversible thruster - ReversibleThrustComponent - steered by the thruster allocator after the cyclic.)
/// T is the thrust the game gives that rotor THIS frame (its share): no lift, no cyclic - a rotor at idle on the ground has
/// next to no authority, as a real one. Nothing is given beyond the reach: a heavy ship with a small rotor cannot turn fast.
/// Called from ThrustTorque.Apply with the torque the law wants (steered: the whole need, before the gyros and thrusters;
/// a rate pilot: the rate error x inertia); returns what the rotors give and applies it (with the tilt's force).
/// </summary>
public static class RotorCyclic
{
    public static bool Enabled = true;
    /// <summary>The cyclic's most disc tilt (deg), the hub moment as a fraction of thrust x radius.</summary>
    public static float MaxTiltDeg = 10f, HubMoment = 0.15f;
    /// <summary>The blade tips' radius over the table's RotorRadius: 1 since the approved model (rotor_rig.py, 2026-10-16), whose
    /// table radius IS the tip radius (6.6 / 11.0 m; the blockout's table held 0.879 x its tip: 1.1377 then). The power and
    /// torque laws are RotorKinematics' (Power, ReactionTorque).</summary>
    public static float TipOverTable = 1f;
    /// <summary>Harness / tests: the last frame's numbers (any grid - the last one that flew rotors).</summary>
    public static string Last = "";

    /// <summary>The thrusters of this grid that are MAIN rotors - the cyclic and the reaction torque (PropulsorKinds.HasCyclic:
    /// the Reversible Rotor is not one) - as indices into its thruster list, refreshed with the list; with them the governed
    /// propulsors (PropulsorKinds.Governed: main and reversible rotors, turboprops) whose state Publish sends to their client
    /// animation. (A component not described yet - its first tick - is looked at again on the next list.)</summary>
    static int[] Rotors(AeroGridComponent aero, int n)
    {
        if (aero._rotorGen == aero._thrusterGen && aero._rotorIdx != null) return aero._rotorIdx;
        var thrusters = aero._thrusterCache; var l = new System.Collections.Generic.List<int>();
        var comps = new System.Collections.Generic.List<AeroPropulsorComponent>();
        var sl = new System.Collections.Generic.List<int>(); var sc = new System.Collections.Generic.List<AeroPropulsorComponent>();
        var sk = new System.Collections.Generic.List<int>();
        for (int i = 0; i < n && i < thrusters.Count; i++)
        {
            var e = thrusters[i].ThrusterEntity;
            if (e == null || !PhysicsHack.Alive(e.Data)) continue;
            var p = e.TryGet<AeroPropulsorComponent>();
            if (p == null) continue;
            // (the kind from the block's table row: the component's own Kind is set on its first tick, maybe after this)
            var row = PropulsorBlocks.RowOf(e);
            var kind = row != null ? row.Kind : p.Kind;
            int c = -1;
            if (AeroPropulsorComponent.HasCyclic(kind)) { c = l.Count; l.Add(i); comps.Add(p); }
            if (AeroPropulsorComponent.Governed(kind)) { sl.Add(i); sc.Add(p); sk.Add(c); }
        }
        aero._rotorIdx = l.ToArray(); aero._rotorComp = comps; aero._rotorGen = aero._thrusterGen;
        aero._spinIdx = sl.ToArray(); aero._spinComp = sc; aero._spinCyc = sk.ToArray();
        aero._rotorCyc = new float[l.Count]; aero._rotorH = new float[l.Count]; aero._rotorTilt = new Vector3[l.Count];
        aero._rotorLoadF = new Vector3[l.Count]; aero._rotorLoadT = new Vector3[l.Count]; aero._rotorLoadQ = new Vector3[l.Count];
        aero._limR = new float[l.Count]; aero._limRpm = new float[l.Count]; aero._limRated = new float[l.Count];
        aero._limAuth = new float[l.Count]; aero._limBudget = new float[l.Count]; aero._limTarget = new float[l.Count];
        return aero._rotorIdx;
    }

    /// <summary>The grid has rotors (cheap after the first call per thruster list).</summary>
    internal static bool Has(AeroGridComponent aero, int n) => Enabled && Rotors(aero, n).Length > 0;

    /// <summary>Give what the rotors can of <paramref name="want"/> (local N m), apply it - the torque and the tilt's force -
    /// and return the torque given. thrust: each thruster's thrust this frame (N, the game's share).</summary>
    internal static Vector3 Give(AeroGridComponent aero, in WorldTransform wt, Vector3 com, Vector3 want, Vector3[] dirs, float[] thrust, int n, float dt, bool apply)
    {
        var idx = Rotors(aero, n); var comps = aero._rotorComp;
        if (idx.Length == 0 || want.LengthSquared() < 1e-6f) return Vector3.Zero;
        float sinT = MathF.Sin(MaxTiltDeg * MathF.PI / 180f);
        // the reaches (summed) and each rotor's share
        float cycSum = 0f;
        var cyc = aero._rotorCyc; var h = aero._rotorH;
        var thrusters = aero._thrusterCache;
        for (int k = 0; k < idx.Length; k++)
        {
            int i = idx[k];
            if (i >= thrusters.Count) { cyc[k] = h[k] = 0f; continue; }
            float T = MathF.Max(thrust[i], 0f), R = comps[k].RotorRadius;
            h[k] = Vector3.Dot(thrusters[i].GridLocalPosition - com, dirs[i]) + comps[k].HubOffset;
            cyc[k] = T * (MathF.Max(h[k], 0f) * sinT + HubMoment * R);
            cycSum += cyc[k];
        }
        if (cycSum <= 0f) return Vector3.Zero;
        Vector3 given = Vector3.Zero, force = Vector3.Zero;
        bool applied = apply && ThrustTorque.ApplyForces;
        var loadF = aero._rotorLoadF; var loadT = aero._rotorLoadT;
        for (int k = 0; k < idx.Length; k++)
        {
            int i = idx[k]; if (i >= thrusters.Count) continue;
            Vector3 givenK = Vector3.Zero, forceK = Vector3.Zero;
            Vector3 a = dirs[i];
            float ax = Vector3.Dot(want, a);
            Vector3 plane = want - a * ax;
            // in-plane: this rotor's share of the want, the whole up to the summed reach
            float pl = plane.Length();
            Vector3 gp = Vector3.Zero;
            if (pl > 0f && cycSum > 0f) gp = plane * (MathF.Min(1f, cycSum / pl) * cyc[k] / cycSum);
            givenK = gp;
            given += givenK;
            // the disc's tilt that gives it (the client draws it - Publish): the thrust leans toward gp x a (the torque of a
            // lean d about the centre of mass is h a x d), by MaxTilt x the share of this rotor's reach used
            float gpl = gp.Length();
            if (apply && gpl > 0f && cyc[k] > 0f && k < aero._rotorTilt.Length)
            {
                var lean = Vector3.Cross(gp, a); float ll = lean.Length();
                if (ll > 0f) aero._rotorTilt[k] = lean / ll * (MathF.Min(1f, gpl / cyc[k]) * MaxTiltDeg * MathF.PI / 180f);
            }
            // the tilt's part of the in-plane torque is the thrust tilted: its in-plane force = torque x a / h
            float hk = MathF.Max(h[k], 0f);
            if (hk > 0.25f && cyc[k] > 0f)
            {
                float tiltShare = hk * sinT * MathF.Max(thrust[i], 0f) / cyc[k];
                forceK = Vector3.Cross(gp * tiltShare, a) / hk;
                force += forceK;
            }
            // (this rotor's part, for the structure at its block: PublishLoads)
            if (applied && k < loadF.Length) { loadF[k] = forceK; loadT[k] = givenK; }
        }
        if (applied && given.LengthSquared() > 0f)
        {
            var forceW = WorldTransform.TransformDirection(force, wt);
            AeroPhysics.ApplyForceAndTorque(aero.Entity, wt, forceW, given, dt);
            // (no contact: the structure's contact balance must not read it as the wheels' or the plates' - NoteKnownImpulse)
            if (aero.IsServerScene) aero.NoteKnownImpulse(forceW * dt);
        }
        Last = $"{(aero.IsServerScene ? "server" : "client")} h {h[0]:F2} m T {thrust[idx[0]]:F0} N reaction {aero._rotorReaction:F0} N m rotors {idx.Length}: want ({want.X:F0}, {want.Y:F0}, {want.Z:F0}) gave ({given.X:F0}, {given.Y:F0}, {given.Z:F0}) N m, tilt force ({force.X:F0}, {force.Y:F0}, {force.Z:F0}) N, reach cyclic {cycSum:F0}, anti-torque {(aero._limAuth.Length > 0 ? aero._limAuth[0] : 0f):F0} N m (reversible / sideways thrusters + gyros + control surfaces: RotorLimiter)";
        if (Log && aero.IsServerScene)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - _logAt > System.Diagnostics.Stopwatch.Frequency / 2) { _logAt = now; Keen.VRage.Library.Diagnostics.Log.Default?.Info("[AERO-ROTOR] " + Last); }
        }
        return given;
    }

    /// <summary>THE CONTROLLABILITY LIMITER (RotorLimiter: the law), every step before the reaction torque (ThrustTorque.Apply,
    /// both scenes - each sets its own components): each main rotor's thrust cap from the yaw authority the grid has about its
    /// axis now (arms, caps: the thrusters' torque per newton and maxima this step; rev: the reversible ones, null none; the
    /// gyros' reach - the game's budget, GyroControl.Reach; the control surfaces' fresh reach - AeroGridComponent.SurfacePush /
    /// SurfacePull, server), smoothed, handed to its AeroPropulsorComponent (TorqueCap: it
    /// writes min(its maximum, the cap) as the rotor's MaxThrustPower). Off (RotorLimiter or the cyclic off): no cap.
    /// No allocation (the arrays are sized with the rotor list).</summary>
    internal static void Limit(AeroGridComponent aero, DEntityContext data, in Keen.VRage.Physics.Data.RigidBodyMassProperties mass, Vector3[] arms, Vector3[] dirs, float[] caps, bool[] rev, int n, float dt)
    {
        var idx = Rotors(aero, n); var comps = aero._rotorComp; int m = idx.Length;
        if (m == 0) return;
        if (!Enabled || !RotorLimiter.Enabled)
        {
            for (int k = 0; k < m; k++) { comps[k].TorqueCap = float.PositiveInfinity; comps[k].CapAge = 0; }
            if (aero.IsServerScene) { RotorLimiter.LastCap = float.PositiveInfinity; RotorLimiter.LastRotors = m; }
            return;
        }
        Vector3 gyro = GyroControl.Reach(data, in mass, Vector3.One);
        // the control surfaces' reach left either way from their trim (the allocator's, server, a tick old - fresh only: a
        // slowed ship keeps no fast-flight reach), x the share the attitude law plans with; none on the client copy (no
        // allocator there: its cap is the conservative one, the server's governs the ship)
        Vector3 sPush = Vector3.Zero, sPull = Vector3.Zero;
        if (aero.SurfaceReachFresh) { sPush = aero.SurfacePush * ThrustTorque.SurfaceReachShare; sPull = aero.SurfacePull * ThrustTorque.SurfaceReachShare; }
        float rho = MathF.Max(aero.LastDensity, 0.01f);   // (as ReactionTorque below)
        var R = aero._limR; var rpm = aero._limRpm; var rated = aero._limRated;
        for (int k = 0; k < m; k++)
        {
            var p = comps[k];
            R[k] = p.RotorRadius * TipOverTable; rpm[k] = p.Rpm; rated[k] = p.Rated * p.Efficiency;   // (the actual thrust's rating)
        }
        RotorLimiter.Targets(m, idx, R, rpm, rated, arms, dirs, caps, rev, n, gyro, sPush, sPull, rho, aero._limAuth, aero._limBudget, aero._limTarget);
        for (int k = 0; k < m; k++)
        {
            var p = comps[k];
            p.TorqueCap = RotorLimiter.Step(p.TorqueCap, aero._limTarget[k], dt); p.CapAge = 0;
        }
        if (aero.IsServerScene)
        {
            int i0 = idx[0];
            RotorLimiter.LastCap = comps[0].TorqueCap; RotorLimiter.LastUncapped = comps[0].Uncapped;
            RotorLimiter.LastAuthority = i0 < n ? RotorLimiter.Authority(dirs[i0], arms, caps, rev, n, gyro, sPush, sPull, out RotorLimiter.LastThrusters, out RotorLimiter.LastGyros, out RotorLimiter.LastSurfaces) : 0f;
            RotorLimiter.LastBudget = aero._limBudget[0]; RotorLimiter.LastTorque = aero._rotorReaction; RotorLimiter.LastRotors = m;
        }
    }

    /// <summary>A grid at rest (ThrustTorque.Apply's early out): its rotors' caps kept as they are (still fresh: the propulsor
    /// lets a cap go only when nothing has set it for AeroPropulsorComponent.CapHoldTicks - the attitude job stopped).</summary>
    internal static void KeepCaps(AeroGridComponent aero, int n)
    {
        if (!Enabled) return;
        var idx = Rotors(aero, n); var comps = aero._rotorComp;
        for (int k = 0; k < idx.Length && k < comps.Count; k++) comps[k].CapAge = 0;
    }

    /// <summary>The rotors' reaction torque on the ship (grid-local N m) at these thrusts (N, each thruster's): each rotor's
    /// Q = P / omega about its axis, the way opposite to its (clockwise from above) turn - +axis.</summary>
    internal static Vector3 ReactionTorque(AeroGridComponent aero, Vector3[] dirs, float[] thrust, int n)
    {
        if (!Enabled) { aero._rotorReaction = 0f; return Vector3.Zero; }
        var idx = Rotors(aero, n); if (idx.Length == 0) return Vector3.Zero;
        var comps = aero._rotorComp; Vector3 q = Vector3.Zero; float qs = 0f;
        float rho = MathF.Max(aero.LastDensity, 0.01f);   // (kg/m^3: the game's air density, as the drag uses it)
        var each = aero._rotorLoadQ;
        for (int k = 0; k < idx.Length; k++)
        {
            if (k < each.Length) each[k] = Vector3.Zero;
            int i = idx[k]; if (i >= n || i >= thrust.Length) continue;
            var p = comps[k];
            float Q = RotorKinematics.ReactionTorque(thrust[i], p.RotorRadius * TipOverTable, p.Rpm, rho);
            if (Q <= 0f) continue;
            q += dirs[i] * Q; qs += Q;
            if (k < each.Length && float.IsFinite(Q)) each[k] = dirs[i] * Q;   // (this rotor's, for the structure at its block: PublishLoads)
        }
        aero._rotorReaction = qs;
        return float.IsFinite(q.X + q.Y + q.Z) ? q : Vector3.Zero;
    }

    /// <summary>
    /// THE ROTORS' LOADS IN THE STRUCTURE (2026-10-17; server, ThrustTorque.Apply's end, after the torque was applied): each main
    /// rotor's cyclic - the tilt's in-plane force and the torque it gave about the centre of mass (Give) - and its REACTION torque
    /// (ReactionTorque) published at its block (AeroGridComponent.PublishBlockLoad): applied to the body at the centre of mass
    /// (AeroPhysics), nothing else in the structure carries them (its axial thrust is the thruster loads' - ThrF; the air's not
    /// at all). The force at the hub (the block's centre + HubOffset along its axis), its torque the rest of what it gave about
    /// the centre of mass (given - (hub - com) x force) plus the reaction: the ship's total force and moment unchanged.
    /// isEngine TRUE: they are the rotor's thrust tilted and its drive's torque - engine loads, off with Structure.ThrustLoads
    /// as its axial thrust is; its rigidity unchanged (the rotor has the game's ThrusterComponent - rigid already, no rebuild).
    /// Cleared after: only a step that applied them publishes them. No allocation (the arrays are the rotor list's).
    /// </summary>
    internal static void PublishLoads(AeroGridComponent aero, Vector3 com, Vector3[] dirs, int n, bool applied)
    {
        var idx = Rotors(aero, n); var comps = aero._rotorComp; var thrusters = aero._thrusterCache;
        var F = aero._rotorLoadF; var T = aero._rotorLoadT; var Q = aero._rotorLoadQ;
        for (int k = 0; k < idx.Length && k < F.Length; k++)
        {
            int i = idx[k];
            Vector3 f = F[k], t = T[k] + Q[k];
            F[k] = Vector3.Zero; T[k] = Vector3.Zero; Q[k] = Vector3.Zero;
            if (!applied || !aero.IsServerScene || i >= thrusters.Count || i >= dirs.Length || (f.LengthSquared() <= 0f && t.LengthSquared() <= 0f)) continue;
            var e = thrusters[i].ThrusterEntity; if (e == null) continue;
            Vector3 hub = thrusters[i].GridLocalPosition + dirs[i] * comps[k].HubOffset;
            aero.PublishBlockLoad(e, f, hub, t - Vector3.Cross(hub - com, f), true);
        }
    }

    /// <summary>Server, every step (ThrustTorque.Apply's end): each rotor's state for its client animation (AeroRotorAnimComponent):
    /// the governed rpm (AeroPropulsorComponent), its thrust this step (use: the thrusters' forces, null: none), its maximum
    /// now, its rating, the disc tilt Give set this step (then cleared: no steering, no tilt), on.
    /// (Every governed propulsor - main and reversible rotors, turboprops: PropulsorKinds.Governed - whether the cyclic is on or not;
    /// the tilt is a main rotor's only. Also each one's load - its thrust / its maximum now - for its server rpm (a
    /// turboprop's rises with it). A reversible one's force is signed: its size counts.)</summary>
    internal static void Publish(AeroGridComponent aero, in WorldTransform wt, float[] use, int n)
    {
        if (!aero.IsServerScene) return;
        Rotors(aero, n);
        var idx = aero._spinIdx; if (idx == null || idx.Length == 0) return;
        var comps = aero._spinComp; var cycOf = aero._spinCyc; var thrusters = aero._thrusterCache; var caps = aero._rcsCap; var tilt = aero._rotorTilt;
        for (int k = 0; k < idx.Length; k++)
        {
            int i = idx[k]; if (i >= thrusters.Count) continue;
            var p = comps[k];
            float t = use != null && i < use.Length ? MathF.Abs(use[i]) : 0f;   // (a reversible thruster's force is signed)
            float max = i < caps.Length ? caps[i] : 0f;
            p.Load = max > 1f ? Math.Clamp(t / max, 0f, 1f) : 0f;
            int c = cycOf[k];
            Vector3 tk = c >= 0 && c < tilt.Length ? tilt[c] : Vector3.Zero;
            ThrustTorque.PublishRotor(aero, wt, thrusters[i].Cell, p.Rpm, t, max, p.Rated, tk, p.SpinOn);
            if (c >= 0 && c < tilt.Length) tilt[c] = Vector3.Zero;
        }
    }

    /// <summary>Harness: twice a second, [AERO-ROTOR] with the last frame's numbers (server).</summary>
    public static bool Log;
    static long _logAt;
}

public partial class AeroGridComponent
{
    /// <summary>RotorCyclic: the rotors among the thrusters (indices, components) for thruster list generation _rotorGen.</summary>
    internal int _rotorGen = -1; internal int[] _rotorIdx; internal System.Collections.Generic.List<AeroPropulsorComponent> _rotorComp;
    internal float[] _rotorCyc, _rotorH;
    /// <summary>RotorCyclic: each main rotor's cyclic force and torque about the centre of mass (Give) and reaction torque
    /// (ReactionTorque) this step, grid-local - its structure load (PublishLoads); sized with the rotor list.</summary>
    internal Vector3[] _rotorLoadF = System.Array.Empty<Vector3>(), _rotorLoadT = System.Array.Empty<Vector3>(), _rotorLoadQ = System.Array.Empty<Vector3>();
    /// <summary>RotorCyclic.Publish: the governed propulsors (indices, components) and each one's index among the main rotors
    /// (-1: none - a reversible rotor, a turboprop).</summary>
    internal int[] _spinIdx, _spinCyc; internal System.Collections.Generic.List<AeroPropulsorComponent> _spinComp;
    /// <summary>RotorCyclic: each rotor's disc tilt this step (grid-local lean, length = angle rad), for Publish.</summary>
    internal Vector3[] _rotorTilt = System.Array.Empty<Vector3>();
    /// <summary>RotorCyclic: the rotors' summed reaction torque last step (N m, harness / logs).</summary>
    internal float _rotorReaction;
    /// <summary>RotorCyclic.Limit (RotorLimiter): per main rotor - tip radius, rpm, rating (in), the authority about its axis,
    /// the reaction allowed (N m) and the cap's target (N) (out); sized with the rotor list.</summary>
    internal float[] _limR = System.Array.Empty<float>(), _limRpm = System.Array.Empty<float>(), _limRated = System.Array.Empty<float>(),
        _limAuth = System.Array.Empty<float>(), _limBudget = System.Array.Empty<float>(), _limTarget = System.Array.Empty<float>();
}
