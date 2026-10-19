#pragma warning disable
using System;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails;
using Keen.VRage.Library.UI.PropertyInspector;
using Keen.VRage.Library.UI.PropertyInspector.Visibility;

namespace AeroMod;

/// <summary>
/// A retractable gear's terminal, as the vanilla door's (2026-10-13, the user): Extend and Retract buttons, the one that
/// does nothing now greyed out ([WritableInInspectorWhen], DoorBlockDetailModel's CanOpen / CanClose). Toggle is a
/// toolbar action only ([Button] methods are toolbar actions - the one kind of mod control the toolbar takes without
/// [SyncFromClients]). Power and on/off, damping and stiffness are the vanilla panels. Host / single player, as every
/// mod panel (see AeroSurfaceBlockDetailModel).
/// </summary>
[BlockDetailProvider]
[BlockDetailDisplayMode(DisplayMode.Controls)]
// SE2 4.0 runs the serializer generators on mod scripts: a partial BlockDetailModel gets a generated named-object
// serializer whose code needs System.ObjectModel (INotifyPropertyChanged), which mod compiles do not reference (CS0012).
// [NoSerialize] turns those generators off for the model - mod models were never serialized (DetailModelPairing).
[Keen.VRage.Library.Serialization.NoSerialize]
public partial class AeroGearBlockDetailModel : BlockDetailModel
{
    static readonly Keen.VRage.Library.Serialization.AccessibleTypeInfo _typeInfo = ModDetailTypeInfo.Build<AeroGearBlockDetailModel>(
        "Retractable gear: extend or retract it; Airshock softens the touchdown.", nameof(CanExtend), nameof(CanRetract), nameof(Airshock));
    public override Keen.VRage.Library.Serialization.AccessibleTypeInfo GetTypeInfo() => _typeInfo;

    [Keen.VRage.DCS.Annotations.Component]
    private AeroGearComponent _gear;

    public AeroGearBlockDetailModel() { DetailModelPairing.Pair(this, _gear); }

    /// <summary>Extend does something: the gear is (going) up.</summary>
    [HideInInspector]
    public bool CanExtend => _gear != null && !_gear.Seq.Down;

    /// <summary>Retract does something: the gear is (going) down.</summary>
    [HideInInspector]
    public bool CanRetract => _gear != null && _gear.Seq.Down;

    [Label("Extend Gear")]
    [Button(null)]
    [WritableInInspectorWhen("CanExtend", WritableWhen.True)]
    public void ExtendGear() { _gear?.Command(true); Changed(); }

    [Label("Retract Gear")]
    [Button(null)]
    [WritableInInspectorWhen("CanRetract", WritableWhen.True)]
    public void RetractGear() { _gear?.Command(false); Changed(); }

    [Label("Toggle Gear")]
    [Button(null)]
    [HideInInspector]
    public void ToggleGear() { _gear?.Toggle(); Changed(); }

    /// <summary>SE1's Airshock: the suspension goes stiff and fully damped just before touchdown (AeroGearComponent.Airshock).</summary>
    [Label("Airshock")]
    public bool Airshock { get => _gear?.AirshockEnabled ?? true; set { if (_gear != null) _gear.AirshockEnabled = value; } }

    void Changed() { OnPropertyChanged(nameof(CanExtend)); OnPropertyChanged(nameof(CanRetract)); }
}
