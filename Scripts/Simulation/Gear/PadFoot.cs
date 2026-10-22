#pragma warning disable
using System;

namespace AeroMod;

/// <summary>
/// A PAD-FOOT gear's foot (2026-10-21, the user: "go back to wheel suspension ... a separate, physics-off landing gear entity where the
/// invisible wheel is. the landing gear and hinge IK to the wheel contact surface normal"): a vanilla landing-gear mesh as a CLIENT
/// SUBPART of the gear block (no collider), hinged at the ankle pin (GearLayout.FootPoint) and turned to the ground the suspension's
/// HIDDEN wheel touches (FootIk; AeroGearAnimComponent). The wheel is the vanilla wheel of the housing's size, never drawn
/// (AeroHiddenWheelComponent), its bottom on the foot's sole (tools/meshgen/gen_padgear_defs.py, padgear_spec.json).
/// </summary>
public sealed class PadFootSpec
{
    /// <summary>The vanilla landing-gear model it shows and its scale (a placeholder reuse: no kitbash).</summary>
    public string Model; public float Scale = 1f;
    /// <summary>The foot's height (m): its sole is this far below the ankle pin.</summary>
    public float Height;
    /// <summary>The hidden wheel's mass (kg; its WheelColliderDefinition's), the weight one gear is sized for (kg) and the suspension's
    /// stroke (m, the housing's line of travel).</summary>
    public float WheelMass, RatedLoad, Stroke;
}
