#pragma warning disable
using System;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails;
using Keen.VRage.Library.Serialization.Validation;
using Keen.VRage.Library.UI.PropertyInspector;

namespace AeroMod;

/// <summary>
/// A 3x3x3 airbrake's terminal (its AeroAirbrakeComponent): how far the leaves open with the parking brake on (0..35 deg,
/// inside the block's box). On / off and power are the game's own panel. Host / single player, as every mod panel
/// (AeroSurfaceBlockDetailModel).
/// </summary>
[BlockDetailProvider]
[BlockDetailDisplayMode(DisplayMode.Controls)]
public class AeroAirbrakeBlockDetailModel : BlockDetailModel
{
    static readonly Keen.VRage.Library.Serialization.AccessibleTypeInfo _typeInfo = ModDetailTypeInfo.Build<AeroAirbrakeBlockDetailModel>(
        "Airbrake: opens with the parking brake (P); how far.", nameof(MaxOpening));
    public override Keen.VRage.Library.Serialization.AccessibleTypeInfo GetTypeInfo() => _typeInfo;

    [Keen.VRage.DCS.Annotations.Component]
    private AeroAirbrakeComponent _brake;

    public AeroAirbrakeBlockDetailModel() { DetailModelPairing.Pair(this, _brake); }

    [Slider(null)]
    [Range<float>(0f, AeroAirbrakeComponent.MaxOpeningLimit, RangeMode.InIn)]
    [Label("Max Opening (deg)")]
    [Increment(1f, 5f, 10f)]
    public float MaxOpening
    {
        get => _brake?.MaxOpening ?? AeroAirbrakeComponent.DefaultMaxOpening;
        set { if (_brake != null) _brake.MaxOpening = value; }
    }
}
