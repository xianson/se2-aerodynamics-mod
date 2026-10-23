#pragma warning disable
using System;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails;
using Keen.VRage.Library.UI.PropertyInspector;
using Keen.VRage.Library.UI.PropertyInspector.Visibility;
using Keen.VRage.Library.Serialization.Validation;

namespace AeroMod;

/// <summary>
/// A solid rocket NOZZLE's terminal (2026-10-17): Thrust Vectoring and its cone. (2026-10-23, the user: "get rid of ignite, just
/// have on/off" - the block's own switch ignites it: AeroSolidNozzleComponent; the Ignite button is gone.) (2026-10-23, the user: "remove all the shit stuff that is not actually a
/// control": the read-only burning / power level / segments / propellant / burn-left fields are gone - they showed as switches and
/// input boxes.) Host / single player, as every mod panel (DetailModelPairing). The game's own thruster panels stay:
/// its on / off does nothing (put back); its thrust override, BEFORE ignition, sets the power level (50..100 %, unset 100 %) -
/// once lit the mod owns it (no throttle, no shutdown; the attitude allocator's steer is written there). THRUST VECTORING (2026-10-20):
/// its switch and its cone (0.5..15 deg, shown while on) - lit and on, the ship's attitude control swivels its thrust within the
/// cone (ThrustVectorGame); changed any time, saved with the block.
/// </summary>
[BlockDetailProvider]
[BlockDetailDisplayMode(DisplayMode.Controls)]
// SE2 4.0 runs the serializer generators on mod scripts: a partial BlockDetailModel gets a generated named-object
// serializer whose code needs System.ObjectModel (INotifyPropertyChanged), which mod compiles do not reference (CS0012).
// [NoSerialize] turns those generators off for the model - mod models were never serialized (DetailModelPairing).
[Keen.VRage.Library.Serialization.NoSerialize]
public partial class AeroSolidNozzleBlockDetailModel : BlockDetailModel
{
    static readonly Keen.VRage.Library.Serialization.AccessibleTypeInfo _typeInfo = ModDetailTypeInfo.Build<AeroSolidNozzleBlockDetailModel>(
        "Solid rocket nozzle: turn it on to ignite - it burns the segments stacked on its top face until they are empty. Thrust Override before ignition sets its power (50-100 %).",
        nameof(GimbalOn), nameof(GimbalDeg));
    public override Keen.VRage.Library.Serialization.AccessibleTypeInfo GetTypeInfo() => _typeInfo;
    /// <summary>The panel's order as listed (the game finds a type's member order here - ModDetailTypeInfo).</summary>
    public static class TypeInfoHolder { public static readonly Keen.VRage.Library.Serialization.AccessibleTypeInfo Value = _typeInfo; }

    [Keen.VRage.DCS.Annotations.Component]
    private AeroSolidNozzleComponent _nozzle;

    public AeroSolidNozzleBlockDetailModel() { DetailModelPairing.Pair(this, _nozzle); }

    /// <summary>Thrust vectoring on (the gimbal; AeroSolidNozzleComponent.GimbalOn).</summary>
    [Label("Thrust Vectoring")]
    public bool GimbalOn
    {
        get => _nozzle != null && _nozzle.GimbalOn;
        set { if (_nozzle != null) _nozzle.GimbalOn = value; OnPropertyChanged(nameof(GimbalOn)); }
    }

    /// <summary>The most it swivels off its axis (deg; AeroSolidNozzleComponent.GimbalDeg).</summary>
    [Slider(null)] [Range<float>(GimbalLimits.MinDeg, GimbalLimits.MaxDeg, RangeMode.InIn)] [Label("Gimbal Cone (deg)")] [Increment(0.5f, 1f, 5f)]
    [ShowInInspectorWhen("GimbalOn", ShowWhen.True)]
    public float GimbalDeg
    {
        get => _nozzle?.GimbalDeg ?? GimbalLimits.DefaultDeg;
        set { if (_nozzle != null) _nozzle.GimbalDeg = GimbalLimits.Cone(value); OnPropertyChanged(nameof(GimbalDeg)); }
    }
}

/// <summary>
/// A solid rocket SEGMENT's terminal (2026-10-17): its description only (2026-10-23: the read-only "in a motor stack" and propellant
/// fields are gone - not controls). The game's own tank panel shows too (its mode is held at Auto by the mod: AeroSolidSegmentComponent).
/// </summary>
[BlockDetailProvider]
[BlockDetailDisplayMode(DisplayMode.Controls)]
[Keen.VRage.Library.Serialization.NoSerialize]
public partial class AeroSolidSegmentBlockDetailModel : BlockDetailModel
{
    static readonly Keen.VRage.Library.Serialization.AccessibleTypeInfo _typeInfo = ModDetailTypeInfo.Build<AeroSolidSegmentBlockDetailModel>(
        "Solid rocket segment: propellant for the nozzle it is stacked on. Cannot be refilled.");
    public override Keen.VRage.Library.Serialization.AccessibleTypeInfo GetTypeInfo() => _typeInfo;
    /// <summary>The panel's order as listed (the game finds a type's member order here - ModDetailTypeInfo).</summary>
    public static class TypeInfoHolder { public static readonly Keen.VRage.Library.Serialization.AccessibleTypeInfo Value = _typeInfo; }

    [Keen.VRage.DCS.Annotations.Component]
    private AeroSolidSegmentComponent _segment;

    public AeroSolidSegmentBlockDetailModel() { DetailModelPairing.Pair(this, _segment); }
}
