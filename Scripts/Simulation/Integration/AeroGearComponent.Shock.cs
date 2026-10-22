#pragma warning disable
namespace AeroMod;

/// <summary>
/// The gear's side of the wheel shocks (2026-10-20; AeroWheelShockComponent - on every suspension, this gear's too): the gear's
/// AIRSHOCK moved there (WheelAirshock, shared by every wheel); the gear only says when its wheel takes part and lets go of its
/// stiffness while the shocks hold it.
/// </summary>
public partial class AeroGearComponent
{
    /// <summary>The wheel shocks own the suspension's factors now (AeroWheelShockComponent sets it): the gear does not write its
    /// stiffness.</summary>
    internal volatile bool ShockOwned;

    /// <summary>The wheel takes part in the shocks: the gear down, its wheel not held up (no retraction, its spring on); a pad foot:
    /// down, not held up, on its slider (the oleo is its only spring - AeroWheelShockComponent's pad branch).</summary>
    internal bool ShockUsable => Seq.Down && _travel <= 0f && (Variant?.Foot != null ? PadHeld : !_springOff && _wc != null);
}
