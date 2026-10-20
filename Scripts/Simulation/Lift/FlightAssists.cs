#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// FLIGHT ASSISTS (2026-10-19, world settings group "Flight assists"): opt-in help for players who want it. Every value's
/// default is REALISTIC - the mod's physics unchanged - and the presets (Assists.Preset) set them together:
///  - StallProtection (honest: no physics change): the autopilot's envelope protection (EnvelopeGuard - alpha prot, the g
///    limit, the predicted loss of control) clips the PILOT's own commanded attitude, like a fly-by-wire airliner's.
///  - StallAngleScale, PostStallLift, SurfaceAuthority ("changes physics"): wings and control surfaces stall later, keep
///    more lift past their stall, and the control surfaces keep more of their bite in separated flow (on top of the flat-plate
///    model and its authority floor, ControlSurface.PostStallAuthorityFloor).
///  - OffsetThrustScale ("unrealistic: removes real torque"): the UNCOMMANDED part of the thrusters' offset torque - what is
///    left of it after the attitude law's own answer (Uncommanded) - times this. The torque the law asks the thrusters for is
///    never touched. The honest answer to offset thrust already exists (ThrustTorque cancels it with real actuators while they
///    have the reach); this is the explicit physics knob for when they do not.
/// Wing drop: the model has no separate asymmetric-stall mechanism (each wing stalls at its own local angle of attack, roll
/// rate included), so there is nothing to damp - no knob.
/// Engine-free (the offline suites compile it); read every tick, written only by the settings registry.
/// </summary>
public static class FlightAssists
{
    /// <summary>The pilot's commanded attitude governed by the envelope protection (fly-by-wire). Off: realistic.</summary>
    public static bool StallProtection = false;
    /// <summary>Every wing's and control surface's stall angle, times this (1: realistic).</summary>
    public static float StallAngleScale = 1f;
    /// <summary>The lift a wing or control surface keeps past its stall, times this - never above its lift at the stall
    /// (1: realistic).</summary>
    public static float PostStallLift = 1f;
    /// <summary>A control surface's deflection authority past its stall, times this (1: realistic).</summary>
    public static float SurfaceAuthority = 1f;
    /// <summary>The thrusters' uncommanded offset torque, times this (1: realistic; 0: none).</summary>
    public static float OffsetThrustScale = 1f;

    /// <summary>The presets (Assists.Preset): 0 custom (the values as they are), 1 realistic, 2 assisted, 3 forgiving.</summary>
    public const int Custom = 0, Realistic = 1, Assisted = 2, Forgiving = 3;
    public static readonly string[] PresetNames = { "Custom", "Realistic", "Assisted", "Forgiving" };

    /// <summary>One preset's values: protection, stall angle, post-stall lift, surface authority, offset thrust, gyro strength.</summary>
    public readonly struct Values
    {
        public readonly bool Protection; public readonly float StallAngle, PostStallLift, SurfaceAuthority, OffsetThrust, Gyro;
        public Values(bool p, float s, float l, float a, float o, float g) { Protection = p; StallAngle = s; PostStallLift = l; SurfaceAuthority = a; OffsetThrust = o; Gyro = g; }
    }

    /// <summary>The preset's values (Custom: none - false). Realistic is every field's default (Gyros.TorqueScale's 0.25 is the
    /// mod's balance: GyroBalance). Assisted: the honest fly-by-wire and stronger gyros, the air untouched. Forgiving: on top,
    /// a gentler stall, more surface bite past it and half the uncommanded offset torque.</summary>
    public static bool Preset(int p, out Values v)
    {
        switch (p)
        {
            case Realistic: v = new Values(false, 1f, 1f, 1f, 1f, 0.25f); return true;
            case Assisted: v = new Values(true, 1f, 1f, 1f, 1f, 0.5f); return true;
            case Forgiving: v = new Values(true, 1.3f, 1.5f, 2f, 0.5f, 1f); return true;
            default: v = default; return false;
        }
    }

    /// <summary>The preset the values now are (Custom when none matches; gyro: the scale now).</summary>
    public static int Matching(float gyro)
    {
        for (int p = Realistic; p <= Forgiving; p++)
        {
            Preset(p, out var v);
            if (v.Protection == StallProtection && Near(v.StallAngle, StallAngleScale) && Near(v.PostStallLift, PostStallLift)
                && Near(v.SurfaceAuthority, SurfaceAuthority) && Near(v.OffsetThrust, OffsetThrustScale) && Near(v.Gyro, gyro)) return p;
        }
        return Custom;
        static bool Near(float a, float b) => MathF.Abs(a - b) < 1e-4f;
    }

    /// <summary>A stall angle (rad) with the assist's scale.</summary>
    public static float Stall(float alphaStall) => alphaStall * StallAngleScale;

    /// <summary>A post-stall lift coefficient with the assist's retention: |cl| x PostStallLift, never above the lift at the
    /// stall (clStall, a magnitude) unless it already was.</summary>
    public static float Retain(float cl, float clStall)
    {
        float m = PostStallLift;
        if (m == 1f) return cl;
        float a = MathF.Abs(cl), cap = MathF.Max(a, clStall);
        return MathF.CopySign(MathF.Min(a * m, cap), cl);
    }

    /// <summary>
    /// The UNCOMMANDED part of the thrusters' offset torque (grid-local N m), per axis: offset - the torque the game's own
    /// sharing of its thrust gives (ThrustTorque Report.OffsetTorque, the thrusters' part); delivered - the thrusters' torque as
    /// applied (the controller's sharing when it solved, else the offset itself); asked - the torque the attitude law asked
    /// the thrusters for (0 when it did not steer). On each axis it is the miss (delivered - asked) where that miss runs WITH
    /// the offset (the offset the thrusters could not cancel), at most the offset there. A miss against the offset - the law
    /// asked more than they could give - is the law's own shortfall, never counted: removing it would add torque nobody has.
    /// So: a met ask (or no offset) - zero; no ask - the whole offset; an offset beyond the thrusters' reach - what is left.
    /// </summary>
    public static Vector3 Uncommanded(Vector3 offset, Vector3 delivered, Vector3 asked)
    {
        Vector3 miss = delivered - asked;
        return new Vector3(Axis(offset.X, miss.X), Axis(offset.Y, miss.Y), Axis(offset.Z, miss.Z));
        static float Axis(float o, float m) => o > 0f ? Math.Clamp(m, 0f, o) : o < 0f ? Math.Clamp(m, o, 0f) : 0f;
    }

    /// <summary>The thrusters' torque to apply with the assist (OffsetThrustScale under 1: the uncommanded part scaled).</summary>
    public static Vector3 ScaleOffset(Vector3 delivered, Vector3 offset, Vector3 asked)
    {
        float s = OffsetThrustScale;
        if (!(s < 1f)) return delivered;
        return delivered - (1f - MathF.Max(0f, s)) * Uncommanded(offset, delivered, asked);
    }
}
