#pragma warning disable
using System;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails;
using Keen.VRage.Library.UI.PropertyInspector;
using Keen.VRage.Library.UI.PropertyInspector.Visibility;
using Keen.VRage.Library.Serialization.Validation;

namespace AeroMod;

/// <summary>
/// A solid rocket NOZZLE's terminal (2026-10-17): Ignite (a [Button]: also a toolbar action - as the gear's Toggle), greyed out
/// while it cannot light (burning, spent, no motor); and its motor, read only - burning, power level (%), segments, propellant
/// left (%), burn left (s). Host / single player, as every mod panel (DetailModelPairing). The game's own thruster panels stay:
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
        "Solid rocket nozzle: set its power level (50-100 %) with the thrust override BEFORE ignition, then ignite it - it burns at that level until its stack of segments is empty (lit, the flight controller may vary it 50-100 % to turn the ship). Thrust Vectoring: lit, the flight controller swivels its thrust within the cone to turn the ship.",
        nameof(CanIgnite), nameof(Burning), nameof(PowerLevel), nameof(Segments), nameof(Propellant), nameof(BurnTimeLeft), nameof(GimbalOn), nameof(GimbalDeg));
    public override Keen.VRage.Library.Serialization.AccessibleTypeInfo GetTypeInfo() => _typeInfo;
    /// <summary>The panel's order as listed (the game finds a type's member order here - ModDetailTypeInfo).</summary>
    public static class TypeInfoHolder { public static readonly Keen.VRage.Library.Serialization.AccessibleTypeInfo Value = _typeInfo; }

    [Keen.VRage.DCS.Annotations.Component]
    private AeroSolidNozzleComponent _nozzle;

    public AeroSolidNozzleBlockDetailModel() { DetailModelPairing.Pair(this, _nozzle); }

    [HideInInspector]
    public bool CanIgnite => _nozzle != null && _nozzle.CanIgnite;

    [Label("Ignite")]
    [Button(null)]
    [WritableInInspectorWhen("CanIgnite", WritableWhen.True)]
    public void Ignite() { _nozzle?.Ignite(); OnPropertyChanged(nameof(CanIgnite)); OnPropertyChanged(nameof(Burning)); }

    [Label("Burning")]
    public bool Burning => _nozzle != null && _nozzle.Burning;

    [Label("Power level (%) - set with thrust override before ignition")]
    public float PowerLevel => _nozzle != null ? 100f * _nozzle.PowerLevel : 100f;

    [Label("Segments in its stack")]
    public float Segments => _nozzle?.StackCount ?? 0;

    [Label("Propellant (%)")]
    public float Propellant => _nozzle != null && _nozzle.StackFull > 0f ? 100f * _nozzle.StackPropellant / _nozzle.StackFull : 0f;

    [Label("Burn time left (s)")]
    public float BurnTimeLeft => _nozzle?.BurnTimeLeft ?? 0f;

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
/// A solid rocket SEGMENT's terminal (2026-10-17), read only: its propellant left (% - its tank's charge, the game's) and whether it is
/// in a motor stack (a segment that is not - not on a nozzle's top face, or after a gap, a wrong-size, a turned or a broken segment -
/// does nothing). The game's own tank panel shows too (its mode is held at Auto by the mod: AeroSolidSegmentComponent).
/// </summary>
[BlockDetailProvider]
[BlockDetailDisplayMode(DisplayMode.Controls)]
[Keen.VRage.Library.Serialization.NoSerialize]
public partial class AeroSolidSegmentBlockDetailModel : BlockDetailModel
{
    static readonly Keen.VRage.Library.Serialization.AccessibleTypeInfo _typeInfo = ModDetailTypeInfo.Build<AeroSolidSegmentBlockDetailModel>(
        "Solid rocket segment: a tank of solid propellant for the nozzle it is stacked on (not in a motor stack: inert). It cannot be refilled, switched off or set to charge.",
        nameof(InMotorStack), nameof(Propellant));
    public override Keen.VRage.Library.Serialization.AccessibleTypeInfo GetTypeInfo() => _typeInfo;
    /// <summary>The panel's order as listed (the game finds a type's member order here - ModDetailTypeInfo).</summary>
    public static class TypeInfoHolder { public static readonly Keen.VRage.Library.Serialization.AccessibleTypeInfo Value = _typeInfo; }

    [Keen.VRage.DCS.Annotations.Component]
    private AeroSolidSegmentComponent _segment;

    public AeroSolidSegmentBlockDetailModel() { DetailModelPairing.Pair(this, _segment); }

    [Label("In a motor stack (else inert)")]
    public bool InMotorStack => _segment != null && _segment.InStack;

    [Label("Propellant (%)")]
    public float Propellant => _segment != null ? 100f * _segment.Fraction : 0f;
}
