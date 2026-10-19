namespace Aero.Core.Components;

/// <summary>
/// Definition ID strings for custom aero block definitions.
/// Must match the Manipulator.Guid in the corresponding _BlockDefinition.partialdef files.
/// </summary>
public static class AeroBlockGuids
{
    // -- Control Surfaces --
    public const string ControlSurface_1x1 = "ae200001-ae00-c501-0001-000000000001";

    // -- Airbrakes --
    public const string Airbrake_1x1       = "ae200001-ae00-ab01-0001-000000000001";

    // -- Atmospheric Scoops --
    public const string Scoop_1x1          = "ae200001-ae00-5c01-0001-000000000001";

    // -- Helicopter Rotors --
    public const string HelicopterRotor_1x1    = "ae200001-ae00-4801-0001-000000000001"; // CCW (standard)
    public const string HelicopterRotorCW_1x1  = "ae200001-ae00-4801-0001-000000000002"; // CW (counter-rotating)
}
