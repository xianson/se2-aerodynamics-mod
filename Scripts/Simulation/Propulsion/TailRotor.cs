#pragma warning disable
using System;

namespace AeroMod;

/// <summary>
/// The Reversible Rotor block's (PropulsorKinds.TailRotor - its C# kind name kept) KIND-SPECIFIC drawing, game-free
/// (Tests/RotorTests). Its thrust is a reversible thruster's (ReversibleThrustComponent, ReversibleThrust): nothing here acts.
/// The split head / blades / blur are drawn as a rotor's (AeroRotorAnimComponent) but with no collective, no cyclic and no
/// coning: every blade at +/- PitchDeg by the side its push is on (ReversibleThrustComponent.PushSide).
/// </summary>
public static class TailRotor
{
    /// <summary>The blades' drawn pitch (deg at 75 % radius) - no collective: the sign is the side it pushes.</summary>
    public static float PitchDeg = 8f;
    /// <summary>Its blade model's own pitch at 75 % radius (rotor_anim_spec.json baked_pitch75_deg).</summary>
    public const float BakedPitch75Deg = 4.778f;

    /// <summary>The blades' drawn pitch (rad, leading edge up = thrust along the block's Up) for its push's side.</summary>
    public static float Pitch(int sign) => (sign < 0 ? -PitchDeg : PitchDeg) * MathF.PI / 180f;
}
