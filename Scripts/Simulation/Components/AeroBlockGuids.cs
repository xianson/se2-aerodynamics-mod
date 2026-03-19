#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// GUIDs for custom aero block CubeBlockDefinitions.
/// Must match the Manipulator.Guid in the corresponding _BlockDefinition.partialdef files.
/// </summary>
public static class AeroBlockGuids
{
    // ── Control Surfaces ──
    public static readonly Guid ControlSurface_1x1 = new("ae200001-ae00-c501-0001-000000000001");

    // ── Airbrakes ──
    public static readonly Guid Airbrake_1x1       = new("ae200001-ae00-ab01-0001-000000000001");

    // ── Atmospheric Scoops ──
    public static readonly Guid Scoop_1x1          = new("ae200001-ae00-5c01-0001-000000000001");
}
