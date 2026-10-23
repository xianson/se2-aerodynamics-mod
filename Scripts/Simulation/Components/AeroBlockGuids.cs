#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
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
    /// <summary>The 3x3x1 control surfaces (own models: a flap subpart on a stretched hinge), large 7.5 m / small 1.5 m.</summary>
    public static readonly Guid ControlSurface_3x3x1_Large = new("ae200001-ae00-c502-0001-000000000001");
    public static readonly Guid ControlSurface_3x3x1_Small = new("ae200001-ae00-c503-0001-000000000001");
    /// <summary>Their client prefabs (Manipulator.Guid of the _Client.partialdef): AeroFlapComponent goes in there.</summary>
    public static readonly System.Collections.Generic.HashSet<Guid> FlapClientPrefabs = new()
    {
        new("ae200001-ae00-c502-0012-000000000001"), new("ae200001-ae00-c503-0012-000000000001"),
    };

    /// <summary>Their server prefabs (Manipulator.Guid of the _Server.partialdef): AeroSurfaceSettingsComponent goes in there.</summary>
    public static readonly System.Collections.Generic.HashSet<Guid> SurfaceServerPrefabs = new()
    {
        new("ae200001-ae00-c502-0011-000000000001"), new("ae200001-ae00-c503-0011-000000000001"),
    };

    /// <summary>The retractable gears (0.5 / 1 / 1.5 / 2.5 m wheels, and the pad-foot ones; AeroGearVariants): their server prefabs (AeroGearComponent)
    /// and client prefabs (AeroGearAnimComponent) - Manipulator.Guid of their _Server / _Client partialdefs.</summary>
    public static readonly System.Collections.Generic.HashSet<Guid> GearServerPrefabs = new()
    {
        new("ae200001-ae00-9e01-0011-000000000001"), new("ae200001-ae00-9e02-0011-000000000001"), new("ae200001-ae00-9e03-0011-000000000001"), new("ae200001-ae00-9e04-0011-000000000001"),
        // (the pad-foot gears, 2026-10-20: gen_padgear_defs.py)
        new("ae200001-ae00-9e21-0011-000000000001"), new("ae200001-ae00-9e22-0011-000000000001"), new("ae200001-ae00-9e23-0011-000000000001"), new("ae200001-ae00-9e24-0011-000000000001"),
    };
    public static readonly System.Collections.Generic.HashSet<Guid> GearClientPrefabs = new()
    {
        new("ae200001-ae00-9e01-0012-000000000001"), new("ae200001-ae00-9e02-0012-000000000001"), new("ae200001-ae00-9e03-0012-000000000001"), new("ae200001-ae00-9e04-0012-000000000001"),
        new("ae200001-ae00-9e21-0012-000000000001"), new("ae200001-ae00-9e22-0012-000000000001"), new("ae200001-ae00-9e23-0012-000000000001"), new("ae200001-ae00-9e24-0012-000000000001"),
    };

    // ── Airbrakes ──
    public static readonly Guid Airbrake_1x1       = new("ae200001-ae00-ab01-0001-000000000001");
    /// <summary>The 3x3x3 clamshell airbrakes (2026-10-14; tools/meshgen/gen_airbrake_defs.py): their blocks, and their server
    /// prefabs (AeroAirbrakeComponent) and client prefabs (AeroAirbrakeAnimComponent).</summary>
    public static readonly Guid Airbrake_3x3x3_Large = new("ae200001-ae00-ab02-0001-000000000001");
    public static readonly Guid Airbrake_3x3x3_Small = new("ae200001-ae00-ab03-0001-000000000001");
    public static readonly System.Collections.Generic.HashSet<Guid> AirbrakeServerPrefabs = new()
        { new("ae200001-ae00-ab02-0011-000000000001"), new("ae200001-ae00-ab03-0011-000000000001") };
    public static readonly System.Collections.Generic.HashSet<Guid> AirbrakeClientPrefabs = new()
        { new("ae200001-ae00-ab02-0012-000000000001"), new("ae200001-ae00-ab03-0012-000000000001") };

    // ── The Autopilot block (2026-10-19; tools/meshgen/gen_autopilot_defs.py): its server prefabs (AeroAutopilotComponent - the
    //    terminal's panel and the saved state). Large: on the vanilla Event Controller's model; Small: the vanilla Timer's. ──
    public static readonly Guid Autopilot_Large = new("ae200001-ae00-a901-0001-000000000001");
    public static readonly Guid Autopilot_Small = new("ae200001-ae00-a902-0001-000000000001");
    public static readonly System.Collections.Generic.HashSet<Guid> AutopilotServerPrefabs = new()
        { new("ae200001-ae00-a901-0011-000000000001"), new("ae200001-ae00-a902-0011-000000000001") };

    // ── Atmospheric Scoops ──
    public static readonly Guid Scoop_1x1          = new("ae200001-ae00-5c01-0001-000000000001");
}
