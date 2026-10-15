#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;

namespace AeroMod;

/// <summary>
/// THE AIR'S UPPER TAIL - pure math, no game (tested offline: Tests/AeroBench, `torque`). SE2's atmosphere generator gives
/// full density inside its constant distance, then LINEARLY less to zero at its affect distance: the air simply stops.
/// Verdure's ends 6 km up; a ship handed over to physics at the planet frame's border (9.45 km, the Orbital Mod) fell
/// 3.5 km in vacuum and then met air thickening 0.11 kg/m3 per km at Mach 3 - 14 g. Here the fade's outer part becomes an
/// exponential tail: from the point where the line's own slope equals the tail's (one scale height inside the affect
/// distance) the density decays e-fold per scale height, on past the affect distance. Same value and slope where they
/// meet (no kink), never less air than the game's, inside it unchanged.
/// </summary>
public static class AtmosphereTail
{
    /// <summary>The tail on (off: the game's linear fade, the air ending at the affect distance).</summary>
    public static bool Enabled = true;
    /// <summary>The scale height as a share of the fade (affect - constant distance): Verdure's ~6 km fade -> 1.5 km.</summary>
    public static float ScaleFraction = 0.25f;
    /// <summary>Beyond this many scale heights past the affect distance: none (the tail is 1e-4 of its start by then).</summary>
    public const float MaxScales = 9f;

    /// <summary>One generator's density at distance d from its centre: its constant distance, affect distance and full
    /// density (kg/m3), with the tail (when Enabled).</summary>
    public static float Density(double d, float constant, float affect, float density)
    {
        if (d <= constant) return density;
        float fade = affect - constant;
        if (fade <= 0f) return 0f;
        float k = density / fade;                       // (the game's slope: kg/m3 per m)
        float linear = d < affect ? (float)(k * (affect - d)) : 0f;
        if (!Enabled) return linear;
        float h = MathF.Max(1f, ScaleFraction * fade);
        double d1 = affect - h;                          // (the tangent point: the line's slope there is the tail's)
        if (d <= d1) return linear;
        if (d - affect > MaxScales * h) return 0f;
        // tangent at d1: value k h, slope -k (= -(k h) / h)
        float tail = (float)(k * h * Math.Exp(-(d - d1) / h));
        return MathF.Max(linear, tail);
    }

    /// <summary>The farthest any of a generator's air reaches (m from its centre).</summary>
    public static float Reach(float constant, float affect) =>
        !Enabled ? affect : affect + MaxScales * MathF.Max(1f, ScaleFraction * (affect - constant));
}
