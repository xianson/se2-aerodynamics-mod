#pragma warning disable
using System;

namespace AeroMod;

/// <summary>
/// THE CYCLIC'S TRIM (2026-10-18) - game-free (Tests/ThrustTests TiltHeliChecks); the game part is ThrustTorque.Apply (the order)
/// and RotorCyclic (the reach per rotor, the tilt applied).
/// In game an unpiloted 3x helicopter (dampeners on) pitched over to 66 deg at a steady rate and fell at 12.5 m/s2 on 26 % of
/// its rotor: the dampeners' hold is per grid axis (the game's), so a tipped ship asks its rotor only for gravity's share along
/// it. The tip-over: the cyclic was asked only for the attitude law's torque (the damping), the thruster solve for the rest of
/// it with the net force kept - and a lone rotor's lift off the centre of mass is no torque the solve can move (every newton of
/// it is needed for the net force): its OFFSET TORQUE stood, nobody's to answer.
/// So, while the rotors' axes agree (one plane of cyclic):
///   Plan     before the solve: the in-plane part of (the ask - the thrusters' torque at the game's share, the offset torque
///            and the lit solid rockets' - trim) - the cyclic's. Within its reach (Project): the solve is not asked for any
///            in-plane torque but its leaning to the game's share - it is solved with the in-plane torque weighed InPlaneWeight
///            (ProjectArms / ProjectTorque), so it gives the axial torque (yaw: the tails) and keeps near the game's share
///            otherwise; beyond the reach: the plan is the reach and the solve (on the full arms) the excess.
///   Give     after the solve: the in-plane part of (the ask - the thrusters' torque as solved), up to the reach - their side
///            effects (a tail's pitch or roll arm) answered too.
/// (Planned on the last solve - 730e48a - the solve held in-plane what it had given the tick before: the split froze, a
/// reversible tail with an in-plane arm stayed pinned against the dampeners' demand, and the "hover" ran off sideways at
/// 5 m/s2. Projected, the solve never holds in-plane torque the cyclic can reach.)
/// Not trimmed (Resting): a ship at rest that something else holds up - on its gear, parked, spooling up - its offset torque is
/// the ground's (a trim there only tilted the disc into a sideways force). Mixed rotor axes, a structure steering cap below 1:
/// the plan given as planned, no pick-up after the solve (the cap limits the thrusters' steering; the cyclic does not take
/// back what it removed).
/// </summary>
public static class RotorTrim
{
    /// <summary>Off: the cyclic answers the attitude law's torque only (the offset torque left standing - A/B tests).</summary>
    public static bool Enabled = true;
    /// <summary>Resting: slower than this (m/s) and turning slower than RestTurn (rad/s), with the thrust and the gravity the
    /// body gets out of balance by more than RestImbalance (m/s2: something holds it).</summary>
    public static float RestSpeed = 0.2f, RestTurn = 0.01f, RestImbalance = 0.5f;

    /// <summary>One rotor's cyclic reach (N m, about the in-plane axes): its thrust x (the hub's height above the centre of mass
    /// along its axis x sin(the most tilt) + the hub moment's share x its radius).</summary>
    public static float Reach(float thrust, float hubHeight, float radius, float sinMaxTilt, float hubMoment)
        => MathF.Max(thrust, 0f) * (MathF.Max(hubHeight, 0f) * sinMaxTilt + hubMoment * radius);

    /// <summary>The tilt's force (local N) for the in-plane torque given (the tilt's share of the reach: the thrust tilted;
    /// in-plane force = torque x axis / h). Zero for a hub at or below 0.25 m.</summary>
    public static Vector3 TiltForce(Vector3 given, Vector3 axis, float thrust, float hubHeight, float reach, float sinMaxTilt)
    {
        if (!(hubHeight > 0.25f) || !(reach > 0f)) return Vector3.Zero;
        float tiltShare = hubHeight * sinMaxTilt * MathF.Max(thrust, 0f) / reach;
        return Vector3.Cross(given * tiltShare, axis) / hubHeight;
    }

    /// <summary>The part of t in the plane normal to the unit axis.</summary>
    public static Vector3 InPlane(Vector3 t, Vector3 axis) => t - axis * Vector3.Dot(t, axis);

    /// <summary>t's in-plane part, at most reach long.</summary>
    public static Vector3 Clip(Vector3 t, Vector3 axis, float reach)
    {
        Vector3 p = InPlane(t, axis); float l = p.Length();
        return l > reach && l > 0f ? p * (MathF.Max(reach, 0f) / l) : p;
    }

    /// <summary>At rest and held up by something else (see the class): no trim.</summary>
    public static bool Resting(float speed, float turnRate, float imbalance)
        => speed < RestSpeed && turnRate < RestTurn && imbalance > RestImbalance;

    /// <summary>The cyclic's plan (local N m) before the solve - ask: what the actuators are asked for (the reaction out), own:
    /// the thrusters' torque at the game's share (+ the lit rockets'), trim: answer it. project: the plan holds all of the
    /// in-plane need - solve on ProjectArms.</summary>
    public static Vector3 Plan(Vector3 ask, Vector3 own, Vector3 axis, float reach, bool trim, out bool project)
    {
        Vector3 p = InPlane(trim && Enabled ? ask - own : ask, axis);
        float l = p.Length();
        project = trim && Enabled && l <= reach;
        return l > reach && l > 0f ? p * (MathF.Max(reach, 0f) / l) : p;
    }

    /// <summary>The cyclic's torque given after the solve (local N m): solved - the thrusters' torque as solved (+ the lit rockets'
    /// at their thrust now); pickUp false (no trim, a structure cap, mixed axes): the plan as it is.</summary>
    public static Vector3 Give(Vector3 ask, Vector3 solved, Vector3 plan, Vector3 axis, float reach, bool pickUp)
        => pickUp && Enabled ? Clip(ask - solved, axis, reach) : plan;

    /// <summary>The weight of the in-plane torque in the solve while the cyclic holds it (ProjectArms / ProjectTorque): low, not
    /// zero - at 0 the reversible tails (their net force weighed RevForceWeight) were free to take any force for a sliver of
    /// yaw arm, a tail pinned at its maximum; at 0.2 the solve still leans to the game's share in-plane (the target's in-plane
    /// part is the share's torque) and the cyclic takes the rest.</summary>
    public static float InPlaneWeight = 0.2f;

    /// <summary>A torque with its in-plane part x InPlaneWeight (the solve's space while the cyclic holds the plane).</summary>
    public static Vector3 ProjectTorque(Vector3 t, Vector3 axis)
    {
        Vector3 a = axis * Vector3.Dot(t, axis);
        return a + (t - a) * InPlaneWeight;
    }

    /// <summary>Each thruster's torque arm in the solve's space (ProjectTorque, into proj, n of them).</summary>
    public static void ProjectArms(Vector3[] arms, Vector3 axis, Vector3[] proj, int n)
    {
        for (int i = 0; i < n; i++) proj[i] = ProjectTorque(arms[i], axis);
    }
}
