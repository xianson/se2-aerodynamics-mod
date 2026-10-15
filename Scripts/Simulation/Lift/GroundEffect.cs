#pragma warning disable
using System;

namespace AeroMod;

/// <summary>
/// GROUND EFFECT for every kind of lift (2026-10-15, the user: "ground effect for all lift, please", "double ground effect
/// ... only if we're landing"). One ground factor for all - McCormick's phi = (16 h/b)^2 / (1 + (16 h/b)^2), h the lifting
/// part's height, b its span (fading out about a span up) - used as:
///   induced drag x phi^(1 + landing)        (the wing model has the first phi: LiftingSurfaceModel adds the rest)
///   lift x (1 + LiftGain x strength x (1 - phi))
/// strength: the hull's lift and the control surfaces 1 + landing; the wings (whose model already turns phi into lift
/// through their aspect ratio - AR / phi) only the landing share. landing: 0 level or climbing .. 1 sinking at LandingSink
/// m/s or more toward the ground - so the effect doubles on the way down to land.
/// </summary>
public static class GroundEffect
{
    /// <summary>The lift added at full ground effect (1 - phi = 1), per unit strength: 0.44 gives a wing at h/b 0.05 (phi 0.39)
    /// about the +27 % McCormick's model gave it, so the landing double is ~ +54 %.</summary>
    public static float LiftGain = 0.44f;
    /// <summary>The sink rate (m/s toward the ground) at which the landing double is full.</summary>
    public static float LandingSink = 2f;

    /// <summary>The ground factor of a part at height h (m) with span b (m): 1 far from the ground or unknown.</summary>
    public static float Phi(float h, float span)
    {
        if (!CompressibleWingModel.GroundEffect || h < 0f || span <= 0.1f) return 1f;
        h = MathF.Max(0.05f, h);
        float x = 16f * h / span;
        if (x > 60f) return 1f;
        return MathF.Max(0.1f, x * x / (1f + x * x));
    }

    /// <summary>0 .. 1: how much the grid is coming down to land - its sink toward the ground (velocity: the grid's motion,
    /// up: away from the ground, both grid-local).</summary>
    public static float Landing(Vector3 velocity, Vector3 up)
    {
        if (up.LengthSquared() < 0.5f || !(LandingSink > 0f)) return 0f;
        return Math.Clamp(-Vector3.Dot(velocity, up) / LandingSink, 0f, 1f);
    }

    /// <summary>The lift multiplier at this ground factor and strength.</summary>
    public static float LiftBoost(float phi, float strength) => 1f + LiftGain * strength * (1f - phi);
}
