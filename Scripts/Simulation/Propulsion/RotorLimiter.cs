#pragma warning disable
using System;

namespace AeroMod;

/// <summary>
/// THE CONTROLLABILITY LIMITER (2026-10-17, the user: "pulling up past the cap stops adding lift instead of spinning") -
/// game-free (Tests/ThrustTests, RotorTests); the game part is RotorCyclic.Limit (ThrustTorque.Apply, every step, per grid,
/// both scenes - each scene's own propulsor components) and AeroPropulsorComponent (it writes the cap into the rotor's
/// ThrustData.MaxThrustPower, the same path as the air's maximum).
/// A main rotor's reaction torque grows faster than its thrust (RotorKinematics.ReactionTorque ~ T^1.5). Pulling up past
/// what the ship can hold against it saturated the anti-torque and spun the ship. So each main rotor's thrust is capped,
/// PREDICTIVELY - from what the ship can hold now, not from a spin seen - to the thrust whose reaction torque is Margin of
/// the yaw authority about its axis that is not needed elsewhere:
///   authority  about the rotor's axis a (the reaction acts along +a, so it is answered along -a):
///              every REVERSIBLE thruster both ways: cap x |arm . a|;
///              every ONE-WAY thruster in the answering sense only: cap x max(0, -arm . a) (an ordinary side thruster mounted
///              the right way round is anti-torque too; one mounted the wrong way gives none - and lift thrusters, other
///              rotors and the rotor itself have no arm about a vertical axis);
///              the GYROS: their reach about a (GyroControl.Reach - the game's own budget, the one the aero-aware law flies);
///              the CONTROL SURFACES (2026-10-17, the user: "any source of torque counts"): what they have left toward -a from
///              their trim (the allocator's reach: AeroGridComponent.SurfacePush / SurfacePull, per axis, a tick old) x
///              ThrustTorque.SurfaceReachShare (the share the attitude law plans with) - speed-dependent: ~0 at hover, a rudder's
///              worth in forward flight. Only fresh reach counts (AeroGridComponent.SurfaceReachFresh: a slowed ship's last
///              fast-flight reach is never kept). Each source is a different actuator, counted once: the thrusters by their
///              caps and arms, the gyros by their budget, the surfaces by their deflection (the airframe's own torque - a fin's
///              weathervaning, wings - is not an actuator and is not counted).
///   elsewhere  the other main rotors whose axes share the direction: the authority is split between them by their reaction
///              at their rating (two alike: half each).
///   cap        RotorKinematics.ThrustForTorque(Margin x authority x share) - the closed-form inverse of the reaction torque.
/// The cap is smoothed (FallSeconds when it drops - authority lost, act soon; RiseSeconds when it grows) and has no feedback
/// path: neither the authority nor the inverse depends on the rotor's own thrust, so it cannot chatter. A ship with no
/// anti-torque at all (no reversible or sideways thruster with an arm, no gyros, no control surface reach) gets a cap of 0: its rotor idles at flat
/// pitch and gives no lift - it would only spin the ship (the block's description says why). Never NaN: a stopped rotor
/// (no reaction) has no cap (+infinity).
/// </summary>
public static class RotorLimiter
{
    /// <summary>Always on in play; off only for A/B tests (harness: aeroset RotorLimiter.Enabled false).</summary>
    public static bool Enabled = true;
    /// <summary>The share of the free authority a rotor's reaction may take: the rest is for steering (yaw, gusts).</summary>
    public static float Margin = 0.8f;
    /// <summary>The cap's first-order smoothing (s): when it drops, and when it rises.</summary>
    public static float FallSeconds = 0.15f, RiseSeconds = 0.6f;

    /// <summary>The yaw authority (N m) about a unit axis (the reaction acts along +axis): the reversible thrusters both ways,
    /// the one-way ones in the answering sense (-axis), the gyros' reach about it. rev: null = none reversible.</summary>
    public static float Authority(Vector3 axis, Vector3[] arms, float[] caps, bool[] rev, int n, Vector3 gyroReach, out float thrusters, out float gyros)
        => Authority(axis, arms, caps, rev, n, gyroReach, Vector3.Zero, Vector3.Zero, out thrusters, out gyros, out _);

    /// <summary>... and the control surfaces' reach left toward + (surfPush) and toward - (surfPull, a magnitude) per local
    /// axis (N m, already x the share planned with): the answering sense -axis takes Pull on an axis where axis is +, Push
    /// where it is -.</summary>
    public static float Authority(Vector3 axis, Vector3[] arms, float[] caps, bool[] rev, int n, Vector3 gyroReach, Vector3 surfPush, Vector3 surfPull,
        out float thrusters, out float gyros, out float surfaces)
    {
        gyros = MathF.Abs(axis.X) * MathF.Max(gyroReach.X, 0f) + MathF.Abs(axis.Y) * MathF.Max(gyroReach.Y, 0f) + MathF.Abs(axis.Z) * MathF.Max(gyroReach.Z, 0f);
        surfaces = SurfaceAuthority(axis, surfPush, surfPull);
        thrusters = 0f;
        for (int i = 0; i < n && i < arms.Length && i < caps.Length; i++)
        {
            float c = caps[i];
            if (!(c > 0f)) continue;
            float t = Vector3.Dot(arms[i], axis);
            if (rev != null && i < rev.Length && rev[i]) thrusters += MathF.Abs(t) * c;
            else if (t < 0f) thrusters -= t * c;
        }
        if (!float.IsFinite(thrusters)) thrusters = 0f;
        if (!float.IsFinite(gyros)) gyros = 0f;
        return thrusters + gyros + surfaces;
    }

    /// <summary>The surfaces' reach answering a reaction along +axis (N m): toward -axis on each local axis, by how much axis
    /// lies along it. Never NaN, never negative.</summary>
    public static float SurfaceAuthority(Vector3 axis, Vector3 surfPush, Vector3 surfPull)
    {
        float s = SurfaceOne(axis.X, surfPush.X, surfPull.X) + SurfaceOne(axis.Y, surfPush.Y, surfPull.Y) + SurfaceOne(axis.Z, surfPush.Z, surfPull.Z);
        return float.IsFinite(s) ? s : 0f;
    }
    static float SurfaceOne(float a, float push, float pull) => a > 0f ? a * MathF.Max(pull, 0f) : -a * MathF.Max(push, 0f);

    /// <summary>Every main rotor's thrust cap target (N, the actual thrust; +infinity: none) - rotor k is thruster rIdx[k]
    /// (its axis dirs[rIdx[k]]), tip radius radius[k] (m), governed rpm rpm[k] (0..1), rating rated[k] (N: only for the
    /// split between rotors); rho the air's density (kg/m^3). authority[k] (N m), budget[k] (N m: the reaction allowed),
    /// target[k] out. No allocation.</summary>
    public static void Targets(int m, int[] rIdx, float[] radius, float[] rpm, float[] rated, Vector3[] arms, Vector3[] dirs, float[] caps,
        bool[] rev, int n, Vector3 gyroReach, float rho, float[] authority, float[] budget, float[] target)
        => Targets(m, rIdx, radius, rpm, rated, arms, dirs, caps, rev, n, gyroReach, Vector3.Zero, Vector3.Zero, rho, authority, budget, target);

    /// <summary>... with the control surfaces' reach (surfPush / surfPull: see Authority; zero - none, at hover).</summary>
    public static void Targets(int m, int[] rIdx, float[] radius, float[] rpm, float[] rated, Vector3[] arms, Vector3[] dirs, float[] caps,
        bool[] rev, int n, Vector3 gyroReach, Vector3 surfPush, Vector3 surfPull, float rho, float[] authority, float[] budget, float[] target)
    {
        for (int k = 0; k < m; k++)
        {
            int i = rIdx[k];
            if (i < 0 || i >= n || i >= dirs.Length) { authority[k] = budget[k] = 0f; target[k] = float.PositiveInfinity; continue; }
            Vector3 a = dirs[i];
            authority[k] = Authority(a, arms, caps, rev, n, gyroReach, surfPush, surfPull, out _, out _, out _);
            // the split: this rotor's reaction at its rating over every rotor's along the same way (itself included)
            float own = RotorKinematics.ReactionTorque(MathF.Max(rated[k], 0f), radius[k], 1f, rho), all = 0f;
            for (int j = 0; j < m; j++)
            {
                int ij = rIdx[j];
                if (ij < 0 || ij >= n || ij >= dirs.Length) continue;
                float c = Vector3.Dot(dirs[ij], a);
                if (c > 0f) all += c * RotorKinematics.ReactionTorque(MathF.Max(rated[j], 0f), radius[j], 1f, rho);
            }
            float share = all > 0f && own > 0f ? Math.Clamp(own / all, 0f, 1f) : 1f;
            budget[k] = Margin * authority[k] * share;
            target[k] = RotorKinematics.ThrustForTorque(budget[k], radius[k], rpm[k], rho);
        }
    }

    /// <summary>One step of a rotor's smoothed cap (N; +infinity: none) toward its target: at once from none (or to none),
    /// else first order - FallSeconds down, RiseSeconds up.</summary>
    public static float Step(float cap, float target, float dt)
    {
        if (!float.IsFinite(target)) return float.PositiveInfinity;
        target = MathF.Max(target, 0f);
        if (!float.IsFinite(cap) || cap < 0f) return target;
        float tau = target < cap ? FallSeconds : RiseSeconds;
        return cap + (target - cap) * (tau > 0f ? MathF.Min(1f, MathF.Max(dt, 0f) / tau) : 1f);
    }

    // ── the readout (server: the last grid that flew main rotors - its first rotor), for tests and aeroget RotorLimiter.Readout ──
    /// <summary>The cap (N, actual thrust; +infinity: none), the rotor's maximum without it (N), the authority (N m) and its
    /// parts, the reaction allowed (N m), the reaction torque now (N m, all the grid's rotors), its rotors.</summary>
    public static float LastCap = float.PositiveInfinity, LastUncapped, LastAuthority, LastThrusters, LastGyros, LastSurfaces, LastBudget, LastTorque;
    public static int LastRotors;
    /// <summary>The numbers above as one line (built when read - nothing is formatted per step).</summary>
    public static string Readout => LastRotors == 0 ? "no main rotors limited yet"
        : $"cap {(float.IsFinite(LastCap) ? LastCap.ToString("F0") + " N" : "none")} (max without it {LastUncapped:F0} N) | authority {LastAuthority:F0} N m (thrusters {LastThrusters:F0}, gyros {LastGyros:F0}, surfaces {LastSurfaces:F0}) x {Margin:F2} -> reaction allowed {LastBudget:F0} N m | reaction now {LastTorque:F0} N m | rotors {LastRotors}{(Enabled ? "" : " | LIMITER OFF")}";
}
