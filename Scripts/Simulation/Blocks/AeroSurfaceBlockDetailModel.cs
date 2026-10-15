#pragma warning disable
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails;
using Keen.VRage.Library.Serialization.Validation;
using Keen.VRage.Library.UI.PropertyInspector;

namespace AeroMod;

/// <summary>
/// A 3x3x1 control surface's terminal (its AeroSurfaceSettingsComponent): which of the cockpit's axes it helps turn about,
/// how far it deflects, and a fixed deflection (a flap, or trim). On / off and power are the game's own panel
/// (PowerableBlockDetailModel). Host / single player: mod settings do not replicate ([Replicate] / [SyncFromClients] are
/// not whitelisted), and for the same reason none of these is a toolbar action. Opened without the control panel's
/// crash by DetailModelPairing (the model paired with itself as it is built).
/// </summary>
[BlockDetailProvider]
[BlockDetailDisplayMode(DisplayMode.Controls)]   // (the inspector shows only models marked Controls)
public class AeroSurfaceBlockDetailModel : BlockDetailModel
{
    /// <summary>Its members for the inspector (ModDetailTypeInfo: the generators that write this do not run on mods).</summary>
    static readonly Keen.VRage.Library.Serialization.AccessibleTypeInfo _typeInfo = ModDetailTypeInfo.Build<AeroSurfaceBlockDetailModel>(
        "Control surface: the axes it helps with, how far it deflects, a fixed flap / trim.",
        nameof(Pitch), nameof(Yaw), nameof(Roll), nameof(MaxDeflection), nameof(Trim));
    public override Keen.VRage.Library.Serialization.AccessibleTypeInfo GetTypeInfo() => _typeInfo;

    [Keen.VRage.DCS.Annotations.Component]
    private AeroSurfaceSettingsComponent _settings;

    public AeroSurfaceBlockDetailModel()
    {
        // (the game runs this after setting _settings - BlockDetailProviderIndexer's Ctor - and before RegisterObservable)
        DetailModelPairing.Pair(this, _settings);
    }

    [Label("Pitch")]
    public bool Pitch { get => _settings?.Pitch ?? true; set { if (_settings != null) _settings.Pitch = value; } }

    [Label("Yaw")]
    public bool Yaw { get => _settings?.Yaw ?? true; set { if (_settings != null) _settings.Yaw = value; } }

    [Label("Roll")]
    public bool Roll { get => _settings?.Roll ?? true; set { if (_settings != null) _settings.Roll = value; } }

    [Slider(null)]
    [Range<float>(0f, AeroSurfaceSettingsComponent.MaxDeflectionLimit, RangeMode.InIn)]
    [Label("Max Deflection (deg)")]
    [Increment(1f, 5f, 10f)]
    public float MaxDeflection
    {
        get => _settings?.MaxDeflection ?? AeroSurfaceSettingsComponent.DefaultMaxDeflection;
        set { if (_settings != null) _settings.MaxDeflection = value; }
    }

    [Slider(null)]
    [Range<float>(-AeroSurfaceSettingsComponent.MaxDeflectionLimit, AeroSurfaceSettingsComponent.MaxDeflectionLimit, RangeMode.InIn)]
    [Label("Flap / Trim (deg)")]
    [Increment(1f, 5f, 10f)]
    public float Trim
    {
        get => _settings?.Trim ?? 0f;
        set { if (_settings != null) _settings.Trim = value; }
    }
}
