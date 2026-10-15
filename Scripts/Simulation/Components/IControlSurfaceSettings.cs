#pragma warning disable
namespace AeroMod;

/// <summary>
/// A control surface block's own settings - its terminal (AeroSurfaceSettingsComponent in game; none offline, where
/// every surface works on every axis). Read by the grid's control every step.
/// </summary>
public interface IControlSurfaceSettings
{
    /// <summary>Built, switched on and powered: else the surface trails at neutral and the control leaves it out.</summary>
    bool Working { get; }
    /// <summary>The cockpit's axes this surface may help turn about (its terminal's toggles).</summary>
    bool Pitch { get; }
    bool Yaw { get; }
    bool Roll { get; }
    /// <summary>The most it deflects either way (deg).</summary>
    float MaxDeflection { get; }
    /// <summary>A fixed deflection it holds (deg, +: trailing edge down) - a flap or trim; the control works about it.</summary>
    float Trim { get; }
}
