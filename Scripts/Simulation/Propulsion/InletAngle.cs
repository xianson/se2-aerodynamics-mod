#pragma warning disable
using System;

namespace AeroMod;

/// <summary>
/// THE INLET-ANGLE THRUST FACTOR (2026-10-17, the user: "ramjet needs to be very intolerant, jet tolerant, turboprop dgaf";
/// docs/PROPULSOR_SPECS.md "Inlet angle") - game-free (Tests/ThrustTests InletChecks). A factor on an air-breathing engine's
/// thrust, multiplied on top of its Mach curve, from theta: the angle between the engine's INTAKE (its push direction - air
/// comes in at the front, the exhaust leaves at the back) and the grid's TRUE AIRSPEED vector (its velocity less the wind,
/// AeroGridComponent.LastAirVel) in the block's frame. theta 0: flying straight along the push, the air straight into the
/// intake; theta 180: the air comes in at the exhaust.
///   Shape(theta)  1 up to FullDeg, a smoothstep down to Residual by StallDeg (compressor stall / spill), Residual up to
///                 FlameoutDeg0, a smoothstep to Behind by FlameoutDeg1 (air from behind: the jet 30 %, the ramjet 0). C1, no steps.
///   Weight(v)     the effect fades in with the true airspeed: none below the profile's FadeNone, full from its FadeFull (jet 100 / 200 m/s; ramjet 20 / 60 m/s),
///                 a smoothstep between - a hovering or taxiing ship has no meaningful "sideways air" (and theta is noise).
///   factor        1 - Weight(v) x (1 - Shape(theta)).
/// A profile that UNSTARTS (the ramjet) has a state (Step): past UnstartDeg its inlet unstarts - thrust drops to Residual
/// within DropTime; it stays unstarted until theta has been back inside RestartDeg (hysteresis: RestartDeg &lt; UnstartDeg)
/// for RelightDelay seconds, then spools back up over SpoolTime. Below FadeNone the flow counts as straight (it relights).
/// Who: the vanilla atmospheric thrusters fly Jet (AeroThrustSettingsComponent.Jet.cs, on their MaxThrustPower with the Jet
/// Mach curve - no extra force); the ramjet block (not built yet) will call Ramjet with Step. Turboprops, rotors, fans,
/// hydrogen and ion: no factor (Of returns null). Reports: the factor applied now is in JetFactor (AeroApi.GetThrusters
/// effectiveness); the design probe assumes straight-in air (factor 1).
/// Replaces ThrustProfile's old Evaluate(mach, velocityHat, intakeDirection) (it lowered the Mach sideways - wrong physics -
/// and took the flame direction for the intake, so it read straight flight as reverse flow).
/// </summary>
public static class InletAngle
{
    /// <summary>Always on in play; off for A/B tests (harness: aeroset InletAngle.Enabled false) - the factor is 1 (the Mach
    /// curve alone stays on its own knob, JetCurve.Enabled).</summary>
    public static bool Enabled = true;
    /// <summary>The default low-speed fade (true airspeed, m/s): no effect at or below FadeNone, the full effect from FadeFull
    /// (each profile carries its own: the jet's bites only at real speed - the user, 2026-10-17).</summary>
    public const float FadeNone = 20f, FadeFull = 60f;

    /// <summary>One engine family's tolerance (degrees, fractions, seconds).</summary>
    public sealed class Profile
    {
        public readonly string Name;
        /// <summary>Full thrust within FullDeg; down to Residual by StallDeg; Residual to FlameoutDeg0; Behind from FlameoutDeg1
        /// (air from behind: 0 for a hard flameout).</summary>
        public readonly float FullDeg, StallDeg, Residual, FlameoutDeg0, FlameoutDeg1, Behind;
        /// <summary>This profile's low-speed fade (true airspeed, m/s): none at or below FadeNone, full from FadeFull.</summary>
        public readonly float FadeNone, FadeFull;
        /// <summary>The unstart (Step): it unstarts past UnstartDeg, relights after RelightDelay s back inside RestartDeg; the
        /// thrust falls to Residual in DropTime s and spools back up in SpoolTime s.</summary>
        public readonly bool Unstarts;
        public readonly float UnstartDeg, RestartDeg, RelightDelay, DropTime, SpoolTime;

        public Profile(string name, float fullDeg, float stallDeg, float residual, float flameoutDeg0, float flameoutDeg1,
                       bool unstarts = false, float unstartDeg = 180f, float restartDeg = 180f, float relightDelay = 0f, float dropTime = 0.15f, float spoolTime = 0.5f,
                       float behind = 0f, float fadeNone = InletAngle.FadeNone, float fadeFull = InletAngle.FadeFull)
        {
            Name = name; FullDeg = fullDeg; StallDeg = stallDeg; Residual = residual; FlameoutDeg0 = flameoutDeg0; FlameoutDeg1 = flameoutDeg1;
            Behind = behind; FadeNone = fadeNone; FadeFull = fadeFull;
            Unstarts = unstarts; UnstartDeg = unstartDeg; RestartDeg = restartDeg; RelightDelay = relightDelay; DropTime = dropTime; SpoolTime = spoolTime;
        }
    }

    /// <summary>A jet (the vanilla atmospheric thrusters): TOLERANT - full within 28 deg; a soft compressor stall to 60 % by
    /// 65 deg; air from behind to 30 % (100 .. 125 deg). Softened and fading in only at real speed (none up to 100 m/s,
    /// full from 200 m/s ~ Mach 0.6) - the user, 2026-10-17: the specced 15 % / flameout broke VTOL lift and retro braking.
    /// Stateless (smooth: no chatter to have).</summary>
    public static readonly Profile Jet = new("Jet", 28f, 65f, 0.6f, 100f, 125f, behind: 0.3f, fadeNone: 100f, fadeFull: 200f);
    /// <summary>A ramjet: VERY INTOLERANT - full within 9 deg (a 5 % sag at 10); past 10 deg the inlet UNSTARTS: 2 % within
    /// 0.15 s; it relights 1.5 s after the air is back within 6 deg and spools up over 0.5 s.</summary>
    public static readonly Profile Ramjet = new("Ramjet", 9f, 16f, 0.02f, 90f, 120f, unstarts: true, unstartDeg: 10f, restartDeg: 6f, relightDelay: 1.5f, dropTime: 0.15f, spoolTime: 0.5f);

    /// <summary>The inlet profile that goes with a thrust-vs-Mach curve (by its name): the jet family (Jet, Turbojet,
    /// Turbofan) - Jet; Ramjet and Scramjet - Ramjet; anything else (Turboprop, Propeller, DuctedFan, HelicopterRotor,
    /// Rocket, none) - null: no factor.</summary>
    public static Profile Of(ThrustProfile curve) => ByName(curve?.Name);
    public static Profile ByName(string name) => name switch
    {
        "Jet" or "Turbojet" or "Turbofan" => Jet,
        "Ramjet" or "Scramjet" => Ramjet,
        _ => null,
    };

    static float Smooth(float t) { t = Math.Clamp(t, 0f, 1f); return t * t * (3f - 2f * t); }

    /// <summary>The low-speed fade weight at a true airspeed (m/s): 0 up to FadeNone, 1 from FadeFull. NaN: 0.</summary>
    public static float Weight(float airspeed) => Weight(null, airspeed);
    /// <summary>The fade weight with this profile's own fade (null: the default).</summary>
    public static float Weight(Profile p, float airspeed)
    {
        float lo = p != null ? p.FadeNone : FadeNone, hi = p != null ? p.FadeFull : FadeFull;
        return !(airspeed > lo) ? 0f : Smooth((airspeed - lo) / (hi - lo));
    }

    /// <summary>theta (degrees, 0..180) between an intake (its push direction, any length) and the true airspeed vector (the
    /// grid's air velocity, the same frame). 0 when either is zero.</summary>
    public static float AngleDeg(Vector3 intake, Vector3 airVel)
    {
        float li = intake.Length(), lv = airVel.Length();
        if (!(li > 1e-6f) || !(lv > 1e-6f)) return 0f;
        float c = Vector3.Dot(intake, airVel) / (li * lv);
        if (!float.IsFinite(c)) return 0f;
        return MathF.Acos(Math.Clamp(c, -1f, 1f)) * (180f / MathF.PI);
    }

    /// <summary>The profile's shape at theta (degrees): 1 .. Residual .. 0 (no fade, no state).</summary>
    public static float Shape(Profile p, float thetaDeg)
    {
        if (p == null || !float.IsFinite(thetaDeg)) return 1f;
        float th = MathF.Abs(thetaDeg);
        if (th <= p.FullDeg) return 1f;
        if (th < p.StallDeg) return 1f - (1f - p.Residual) * Smooth((th - p.FullDeg) / (p.StallDeg - p.FullDeg));
        if (th <= p.FlameoutDeg0) return p.Residual;
        return p.Residual + (p.Behind - p.Residual) * Smooth((th - p.FlameoutDeg0) / (p.FlameoutDeg1 - p.FlameoutDeg0));
    }

    /// <summary>The stateless factor (a jet): 1 - Weight(v) x (1 - Shape(theta)). 1 with no profile or Enabled off.</summary>
    public static float Factor(Profile p, float thetaDeg, float airspeed)
    {
        if (p == null || !Enabled) return 1f;
        return 1f - Weight(p, airspeed) * (1f - Shape(p, thetaDeg));
    }

    /// <summary>An unstarting inlet's state: unstarted now, the seconds the flow has been straight since it unstarted, and the
    /// inlet's level (1 started and spooled, Residual unstarted; ramps between).</summary>
    public struct State { public bool Unstarted; public float Clock, Level; public int Unstarts; }
    public static State NewState() => new State { Level = 1f };

    /// <summary>One step of an inlet (dt seconds): the factor now. A profile that does not unstart is Factor (no state kept).
    /// Unstarts past UnstartDeg (above FadeNone); relights after RelightDelay s with theta below RestartDeg (or the airspeed
    /// below FadeNone); the level falls to Residual in DropTime s and rises back in SpoolTime s. Factor off (Enabled false or
    /// no profile): 1, and the state reset (started).</summary>
    public static float Step(Profile p, ref State s, float thetaDeg, float airspeed, float dt)
    {
        if (p == null || !Enabled) { s = NewState(); return 1f; }
        if (!p.Unstarts) return Factor(p, thetaDeg, airspeed);
        float w = Weight(p, airspeed);
        float th = float.IsFinite(thetaDeg) ? MathF.Abs(thetaDeg) : 0f;
        if (!(dt > 0f) || !float.IsFinite(dt)) dt = 0f;
        if (!s.Unstarted)
        {
            if (w > 0f && th > p.UnstartDeg) { s.Unstarted = true; s.Clock = 0f; s.Unstarts++; }
        }
        else
        {
            if (w <= 0f || th < p.RestartDeg) s.Clock += dt; else s.Clock = 0f;
            if (s.Clock >= p.RelightDelay) { s.Unstarted = false; s.Clock = 0f; }
        }
        float target = s.Unstarted ? p.Residual : 1f;
        if (s.Level > target) s.Level = MathF.Max(target, s.Level - dt * (1f - p.Residual) / MathF.Max(p.DropTime, 1e-3f));
        else if (s.Level < target) s.Level = MathF.Min(target, s.Level + dt * (1f - p.Residual) / MathF.Max(p.SpoolTime, 1e-3f));
        float raw = MathF.Min(Shape(p, th), s.Level);
        return 1f - w * (1f - raw);
    }
}
