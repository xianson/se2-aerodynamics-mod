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
// SE2 4.0 runs the serializer generators on mod scripts: a partial BlockDetailModel gets a generated named-object
// serializer whose code needs System.ObjectModel (INotifyPropertyChanged), which mod compiles do not reference (CS0012).
// [NoSerialize] turns those generators off for the model - mod models were never serialized (DetailModelPairing).
[Keen.VRage.Library.Serialization.NoSerialize]
public partial class AeroAirbrakeBlockDetailModel : BlockDetailModel
{
    static readonly Keen.VRage.Library.Serialization.AccessibleTypeInfo _typeInfo = ModDetailTypeInfo.Build<AeroAirbrakeBlockDetailModel>(
        "Airbrake: opens with the parking brake (P); how far.", nameof(MaxOpening));
    public override Keen.VRage.Library.Serialization.AccessibleTypeInfo GetTypeInfo() => _typeInfo;
    /// <summary>The panel's order as listed (the game finds a type's member order here - ModDetailTypeInfo).</summary>
    public static class TypeInfoHolder { public static readonly Keen.VRage.Library.Serialization.AccessibleTypeInfo Value = _typeInfo; }

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
