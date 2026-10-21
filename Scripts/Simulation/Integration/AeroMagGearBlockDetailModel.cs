#pragma warning disable
using System;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails;
using Keen.VRage.Library.UI.PropertyInspector;
using Keen.VRage.Library.UI.PropertyInspector.Visibility;

namespace AeroMod;

/// <summary>
/// The magnetic gear's magnet panel (2026-10-20; beside the gear's own Extend / Retract / Airshock panel - AeroGearBlockDetailModel):
/// Lock and Unlock (as the parking brake, P), Auto-Lock, and what the magnet does now. Host / single player, as every mod panel.
/// </summary>
[BlockDetailProvider]
[BlockDetailDisplayMode(DisplayMode.Controls)]
[Keen.VRage.Library.Serialization.NoSerialize]
public partial class AeroMagGearBlockDetailModel : BlockDetailModel
{
    static readonly Keen.VRage.Library.Serialization.AccessibleTypeInfo _typeInfo = ModDetailTypeInfo.Build<AeroMagGearBlockDetailModel>(
        "Magnetic gear: its pad locks like a vanilla landing gear - Lock or P, or Auto-Lock on touchdown; never onto its own ship. Unlock before retracting.",
        nameof(Locked), nameof(AutoLock), nameof(PadOn));
    public override Keen.VRage.Library.Serialization.AccessibleTypeInfo GetTypeInfo() => _typeInfo;
    /// <summary>The panel's order as listed (the game finds a type's member order here - ModDetailTypeInfo).</summary>
    public static class TypeInfoHolder { public static readonly Keen.VRage.Library.Serialization.AccessibleTypeInfo Value = _typeInfo; }

    [Keen.VRage.DCS.Annotations.Component]
    private AeroMagGearComponent _mag;

    public AeroMagGearBlockDetailModel() { DetailModelPairing.Pair(this, _mag); }

    [HideInInspector]
    public bool CanLock => _mag != null && _mag.PadJoined && !_mag.Locked;
    [HideInInspector]
    public bool CanUnlock => _mag != null && _mag.Locked;

    [Label("Lock")]
    [Button(null)]
    [WritableInInspectorWhen("CanLock", WritableWhen.True)]
    public void Lock() { if (_mag != null) _mag.Ask = 1; Changed(); }

    [Label("Unlock")]
    [Button(null)]
    [WritableInInspectorWhen("CanUnlock", WritableWhen.True)]
    public void Unlock() { if (_mag != null) _mag.Ask = 2; Changed(); }

    [Label("Locked")]
    public bool Locked => _mag != null && _mag.Locked;

    /// <summary>Lock on touchdown (the vanilla landing gear's Auto-Lock - onto anything but its own ship).</summary>
    [Label("Auto-Lock")]
    public bool AutoLock { get => _mag?.AutoLock ?? true; set { if (_mag != null) _mag.AutoLock = value; OnPropertyChanged(nameof(AutoLock)); } }

    [Label("Pad attached")]
    public bool PadOn => _mag != null && _mag.PadJoined;

    void Changed() { OnPropertyChanged(nameof(CanLock)); OnPropertyChanged(nameof(CanUnlock)); OnPropertyChanged(nameof(Locked)); }
}
