#pragma warning disable
using System;

namespace AeroMod;

/// <summary>
/// THE VANILLA ATMOSPHERIC THRUSTERS' MACH CURVE (2026-10-17, the user's speed roles: "atmos king of transonic-ish", "a lil
/// more respeck on mach 1's name") - game-free (Tests/ThrustTests); the game part is AeroThrustSettingsComponent.Jet.cs (every
/// vanilla thruster carries that component). In normal play each VANILLA atmospheric thruster's real maximum - its
/// ThrustData.MaxThrustPower, the number the game's movement shares out and ThrustTorque reads (LiveMax) - is reshaped every
/// few ticks to its rating x ThrustProfiles.Jet at the grid's Mach (AeroGridComponent.LastMach), the way AeroPropulsorComponent
/// reshapes the propulsors: the game's own thrust, no extra force. The game multiplies that maximum by its air-density
/// efficiency (GetThrustEfficiency) as always - this curve has no density in it, so nothing is applied twice.
/// Who: thrust class Atmospheric, NOT a propulsor block (they are vanilla atmospheric thrusters underneath with their own
/// curves: AeroPropulsorComponent). Hydrogen and ion: flat (rockets), never touched.
/// When: Enabled, aero on (AeroSwitch) and the thrust-torque path on (ThrustTorque.Enabled - off: the game's own thrust
/// untouched). Off, or the curve lifted: the rating is written back (Step: restore).
/// Who wrote it: the game writes the rating (load, power returned) or 0 (unpowered): a value that is not ours is taken as the
/// new rating, a game 0 is left alone until the game writes the rating back (as AeroPropulsorComponent). The curve is always
/// applied to the rating, never to the value now - it cannot compound.
/// The INLET ANGLE (InletAngle.Jet, 2026-10-17) is the second factor in the same write: Step's factor is the Mach curve's x
/// the inlet's (AeroThrustSettingsComponent.Jet.cs), so Unshaped takes both out.
/// </summary>
public static class JetCurve
{
    /// <summary>Always on in play; off for A/B tests (harness: aeroset JetCurve.Enabled false) - the Mach curve lifted (the
    /// ratings written back unless the inlet angle - InletAngle.Enabled, its own knob - still shapes them).</summary>
    public static bool Enabled = true;
    /// <summary>A write when the maximum moved more than this fraction of the rating (each write re-sums the grid's thrust).</summary>
    public const float WriteTolerance = 0.005f;
    /// <summary>The curve's update period (ticks): Mach changes slowly.</summary>
    public const int Period = 3;

    /// <summary>The highest factor written (x the rating): the supersonic RAM RISE (2026-10-17, the Mach wall made reachable)
    /// takes the curve above its rating past ~Mach 0.97 (no transonic pinch since 2026-10-18) - ThrustProfiles.JetRamPeak at its top. The game's power draw is its
    /// definition's, flat (ThrusterComponent: ResourcesRequiredToThrust), so the rise is more thrust per kW, as a real jet's
    /// ram pressure gives; the propulsors write above their ratings too (AeroPropulsorComponent: density up to x1.25).</summary>
    public const float MaxFactor = 2f;

    /// <summary>The thrust fraction at a Mach (ThrustProfiles.Jet; 1 at rest, above 1 in the ram rise, never NaN, 0 past
    /// its envelope).</summary>
    public static float Factor(float mach)
    {
        if (!(mach > 0f)) return 1f;
        float f = (float)ThrustProfiles.Jet.Evaluate(mach);
        return float.IsFinite(f) ? Math.Clamp(f, 0f, MaxFactor) : 1f;
    }

    /// <summary>The curve applies to this thruster: an atmospheric-class thruster that is not one of our propulsor blocks.</summary>
    public static bool Applies(string thrustClass, bool propulsor)
        => !propulsor && thrustClass != null && thrustClass.IndexOf("Atmospheric", StringComparison.Ordinal) >= 0;

    /// <summary>One thruster's state: its rating (the game's value), what was last written (-1: nothing yet), a game zero
    /// standing, the factor applied now (1: none - the curve lifted or not yet), whether the curve is on it now.</summary>
    public struct State { public float Rated, Written; public bool GameZero, Applied; public float Factor; }
    public static State NewState() => new State { Written = -1f, Factor = 1f };

    /// <summary>One update: cur, the thruster's MaxThrustPower now; active, the curve on; factor, Factor(Mach). True: write
    /// 'write' as its MaxThrustPower (the shaped maximum, or the rating restored).</summary>
    public static bool Step(ref State s, float cur, bool active, float factor, out float write)
    {
        write = cur;
        // the game wrote it (not what we wrote): its rating, or a zero we leave alone
        if (s.Written < 0f || MathF.Abs(cur - s.Written) > 1e-3f * MathF.Max(1f, s.Written))
        {
            if (cur > 0f) { s.Rated = cur; s.GameZero = false; }
            else { s.GameZero = true; s.Written = 0f; s.Applied = false; s.Factor = 1f; return false; }
            s.Written = cur;
        }
        if (s.GameZero || !(s.Rated > 0f)) { s.Applied = false; s.Factor = 1f; return false; }
        float f = active && float.IsFinite(factor) ? Math.Clamp(factor, 0f, MaxFactor) : 1f;
        s.Applied = active; s.Factor = f;
        float want = s.Rated * f;
        // (back to the rating exactly when lifted - no tolerance left standing; and a zero factor (past the envelope, an
        //  inlet flamed out) gives exactly 0 - not a remnant within the tolerance)
        if (active ? MathF.Abs(want - cur) <= WriteTolerance * s.Rated && (f > 0f || cur == 0f) : cur == want) return false;
        s.Written = want; write = want;
        return true;
    }

    /// <summary>A live maximum (N) taken back to the rating: the shaped share removed (live / Factor) - what a sea-level,
    /// Mach-0 report wants (DesignProbe). At a factor of 0 (past the envelope) the rating itself. (A factor above 1 - the
    /// ram rise - is taken out too: it was "Factor >= 1 - nothing to take out" while the curve could not pass its rating.)</summary>
    public static float Unshaped(in State s, float live)
    {
        if (!s.Applied || s.Factor == 1f) return live;
        if (s.Factor > 1e-4f) return live / s.Factor;
        return live > 0f ? live : s.Rated;
    }
}
