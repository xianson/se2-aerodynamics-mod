#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;

namespace AeroMod;

/// <summary>
/// THE PAD FOOT'S ANKLE (2026-10-21, the user: "go back to wheel suspension ... a separate, physics-off landing gear entity where the
/// invisible wheel is. the landing gear and hinge IK to the wheel contact surface normal"; "the landing gear foot should retract in so
/// it's flat like the wheel is when retracted"). Pure maths (no engine types): the foot is a visual subpart hinged at the ankle (the rod's
/// bottom, GearLayout.DeployedHub), turned about the hinge axis (the leg's fold axis, GearLayout.HingeAxis) so its sole lies on the
/// ground the hidden wheel touches. Everything in the block's frame.
/// </summary>
public static class FootIk
{
    /// <summary>The most the ankle turns either way (rad).</summary>
    public const float MaxAngle = 35f * MathF.PI / 180f;
    /// <summary>How fast the ankle follows its target (1/s; a first-order follow - no snap when a contact comes or goes).</summary>
    public const float Rate = 12f;

    /// <summary>The ankle angle (rad, about axis, right-handed) that turns the foot's up (unit, its sole's normal at angle 0) toward the
    /// ground normal n (unit; zero: no ground) within MaxAngle - only the part of n across the hinge counts (a hinge turns one way).
    /// Zero when there is no ground or it is beyond a quarter turn (the foot cannot reach it).</summary>
    public static float Target(Vector3 up, Vector3 n, Vector3 axis)
    {
        if (n.LengthSquared() < 1e-6f || axis.LengthSquared() < 1e-6f) return 0f;
        Vector3 a = Vector3.Normalize(axis);
        Vector3 u = up - a * Vector3.Dot(up, a), g = n - a * Vector3.Dot(n, a);
        if (u.LengthSquared() < 1e-8f || g.LengthSquared() < 1e-8f) return 0f;
        u = Vector3.Normalize(u); g = Vector3.Normalize(g);
        float c = Vector3.Dot(u, g);
        if (c <= 0f) return 0f;
        float angle = MathF.Atan2(Vector3.Dot(Vector3.Cross(u, g), a), c);
        return Math.Clamp(angle, -MaxAngle, MaxAngle);
    }

    /// <summary>The ankle's target this frame: the ground's (Target) while the gear is fully down and its wheel touches; else 0 - in the
    /// air, retracting, stowed: the foot sits square on its rod, so it folds flat into the well with the leg (as the wheel does).</summary>
    public static float Want(Vector3 up, Vector3 n, Vector3 axis, float progress)
        => progress >= 0.999f ? Target(up, n, axis) : 0f;

    /// <summary>The ankle angle after dt (s) following want (first order, rate Rate; dt clamped to 0.1 s).</summary>
    public static float Follow(float now, float want, float dt)
    {
        float k = 1f - MathF.Exp(-Rate * Math.Clamp(dt, 0f, 0.1f));
        return now + (want - now) * k;
    }

    /// <summary>The ankle's turn (about axis, at the hinge) as a quaternion.</summary>
    public static Quaternion Turn(Vector3 axis, float angle)
        => axis.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angle);
}
