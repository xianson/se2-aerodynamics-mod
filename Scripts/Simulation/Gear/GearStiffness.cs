#pragma warning disable
using System;

namespace AeroMod;

/// <summary>
/// THE SUSPENSION'S STIFFNESS CAP (2026-10-20, every gear): the wheel constraint's SuspensionStrengthFactor scales the solver's
/// tau (SolverTau, 0.6) - the effective tau is their product, and past ~1.5-2 the suspension goes unstable (it pumps energy in).
/// The terminal's slider (0..1) x StiffnessScale is capped so the effective tau never passes MaxEffectiveTau. The spring this
/// gives grows with the WHEEL's mass (k ~ strength x tau x m_wheel / dt^2): a light wheel under a heavy ship bottoms out, so the
/// pad-foot gears' wheels are made heavy (their definitions' WheelMass, gen_padgear_defs.py).
/// </summary>
public static class GearStiffness
{
    public const float SolverTau = 0.6f, MaxEffectiveTau = 1.67f;
    /// <summary>The highest strength factor written (2.78).</summary>
    public const float MaxStrength = MaxEffectiveTau / SolverTau;

    /// <summary>The strength factor for this slider (0..1) and scale, capped.</summary>
    public static float Strength(float slider, float scale) => Math.Clamp(Math.Max(0f, slider) * Math.Max(0f, scale), 0f, MaxStrength);

    /// <summary>The spring rate it gives (N/m): strength x tau x the wheel's (and pad's) mass / dt^2.</summary>
    public static float SpringRate(float strength, float mass, float dt) => strength * SolverTau * mass / (dt * dt);

    /// <summary>The static sag under a load (m) at that spring rate.</summary>
    public static float Sag(float loadKg, float springRate, float g = 9.81f) => springRate > 0f ? loadKg * g / springRate : float.PositiveInfinity;
}
