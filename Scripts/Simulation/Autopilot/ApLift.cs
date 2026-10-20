#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// THE LIFT VECTOR's arithmetic (engine-free, allocation-free): what a fighter pilot commands - where the lift points (a unit
/// vector across the velocity) and how hard he pulls (n, g of the real weight) - from a wanted path acceleration, and back. The
/// flight director flies the result as ApExternalKind.Lift (ApExternal.Lift / Load / Throttle: the lift's roll about the path and
/// the angle of attack for that load from the ship's own lift curve - LiftFlight - every protection on top).
/// </summary>
public static class ApLift
{
    /// <summary>The lift direction (world unit, across vel) and load (g of gravity gVec's size) that give the path acceleration aPerp
    /// (world m/s2, across the velocity) against the gravity gVec (world m/s2, down): the specific force across the path is aPerp less
    /// gVec's part across it. n is capped at nMax, the lift's direction kept (a pilot at the limit keeps his lift vector and pulls
    /// what he has).</summary>
    public static void FromAccel(Vector3 vel, Vector3 aPerp, Vector3 gVec, float nMax, out Vector3 lift, out float n)
    {
        float sp = vel.Length(), g = MathF.Max(gVec.Length(), 1e-3f);
        Vector3 vh = sp > 1e-3f ? vel / sp : Vector3.UnitZ;
        Vector3 f = ApMath.Flat(aPerp, vh) - ApMath.Flat(gVec, vh);
        float fl = f.Length();
        if (fl < 1e-4f) { lift = Across(vh, -gVec); n = 0f; return; }
        lift = f / fl; n = MathF.Min(fl / g, MathF.Max(nMax, 0f));
    }

    /// <summary>The path acceleration (world m/s2, across vel) a lift direction (world unit) and load (g) give against the gravity gVec.</summary>
    public static Vector3 ToAccel(Vector3 vel, Vector3 lift, float n, Vector3 gVec)
    {
        float sp = vel.Length();
        Vector3 vh = sp > 1e-3f ? vel / sp : Vector3.UnitZ;
        return ApMath.Flat(Across(vh, lift) * (n * gVec.Length()) + gVec, vh);
    }

    /// <summary>The lift direction for a bank angle (rad, + right wing down) about the velocity, 0 the lift straight up (against the
    /// gravity's part across the path).</summary>
    public static Vector3 Banked(Vector3 vel, Vector3 gVec, float bank)
    {
        float sp = vel.Length();
        Vector3 vh = sp > 1e-3f ? vel / sp : Vector3.UnitZ;
        Vector3 up = Across(vh, -gVec);
        return Vector3.Normalize(Vector3.Transform(up, Quaternion.CreateFromAxisAngle(vh, bank)));
    }

    /// <summary>A unit vector across v (unit) toward d's part across it (fallback: across v).</summary>
    public static Vector3 Across(Vector3 v, Vector3 d)
    {
        Vector3 a = ApMath.Flat(d, v);
        return a.LengthSquared() > 1e-8f ? Vector3.Normalize(a) : ApMath.Across(v);
    }
}
