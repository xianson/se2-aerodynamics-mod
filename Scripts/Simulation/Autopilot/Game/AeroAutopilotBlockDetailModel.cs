#pragma warning disable
using System;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails;
using Keen.VRage.Library.Serialization.Validation;
using Keen.VRage.Library.UI.PropertyInspector;
using Keen.VRage.Library.UI.PropertyInspector.Visibility;

namespace AeroMod;

/// <summary>
/// The Autopilot block's terminal - the autopilot's whole control surface (AeroAutopilotComponent): the mode buttons (Hover hold,
/// Height hold, Off - also toolbar actions, with the hidden Toggle ones: [Button] methods are what the toolbar takes from a mod
/// panel), the status line, the height and its reference, the hover speed, the protection's limits and what it does unattended.
/// On / off and power are the game's own panel. Host / single player, as every mod panel (DetailModelPairing). The status line is
/// read when the panel shows it (a button press refreshes it).
/// </summary>
[BlockDetailProvider]
[BlockDetailDisplayMode(DisplayMode.Controls)]
// SE2 4.0 runs the serializer generators on mod scripts: [NoSerialize] keeps them off a mod model (AeroAirbrakeBlockDetailModel).
[Keen.VRage.Library.Serialization.NoSerialize]
public partial class AeroAutopilotBlockDetailModel : BlockDetailModel
{
    static readonly Keen.VRage.Library.Serialization.AccessibleTypeInfo _typeInfo = ModDetailTypeInfo.Build<AeroAutopilotBlockDetailModel>(
        "Autopilot: hover hold and height hold for this ship, with envelope protection.",
        nameof(Status), nameof(CanHover), nameof(CanHeight), nameof(CanOff), nameof(HeightM), nameof(OverGround), nameof(HoverSpeed),
        nameof(Protection), nameof(GLimit), nameof(AoaMargin), nameof(FloorM), nameof(LookAhead), nameof(Unattended));
    public override Keen.VRage.Library.Serialization.AccessibleTypeInfo GetTypeInfo() => _typeInfo;

    [Keen.VRage.DCS.Annotations.Component]
    private AeroAutopilotComponent _ap;

    public AeroAutopilotBlockDetailModel() { DetailModelPairing.Pair(this, _ap); }

    ApPanel P => _ap?.Panel ?? ApPanel.Defaults;

    [Label("Status")]
    [ReadOnlyInInspector]
    public string Status => _ap?.Status ?? "";

    [HideInInspector] public bool CanHover => _ap != null && P.Mode != ApMode.HoverHold;
    [HideInInspector] public bool CanHeight => _ap != null && P.Mode != ApMode.HeightHold;
    [HideInInspector] public bool CanOff => _ap != null && P.Mode != ApMode.Off;

    [Label("Hover Hold")]
    [Button(null)]
    [WritableInInspectorWhen("CanHover", WritableWhen.True)]
    public void HoverHold() { _ap?.SetMode(ApMode.HoverHold); Changed(); }

    [Label("Height Hold")]
    [Button(null)]
    [WritableInInspectorWhen("CanHeight", WritableWhen.True)]
    public void HeightHold() { _ap?.SetMode(ApMode.HeightHold); Changed(); }

    [Label("Autopilot Off")]
    [Button(null)]
    [WritableInInspectorWhen("CanOff", WritableWhen.True)]
    public void AutopilotOff() { _ap?.SetMode(ApMode.Off); Changed(); }

    [Label("Toggle Hover Hold")]
    [Button(null)]
    [HideInInspector]
    public void ToggleHoverHold() { _ap?.ToggleMode(ApMode.HoverHold); Changed(); }

    [Label("Toggle Height Hold")]
    [Button(null)]
    [HideInInspector]
    public void ToggleHeightHold() { _ap?.ToggleMode(ApMode.HeightHold); Changed(); }

    /// <summary>The height held: engaging catches the ship's own (shown here); a new number flies it there.</summary>
    [Slider(null)]
    [Range<float>(0f, 5000f, RangeMode.InIn)]
    [Label("Height (m)")]
    [Increment(1f, 10f, 100f)]
    public float HeightM { get => P.HeightM; set { _ap?.SetHeight(value); Changed(); } }

    /// <summary>On: the height over the ground under the ship (the terrain followed). Off: a level altitude (over the ground where
    /// the hold was engaged).</summary>
    [Label("Height Over Ground")]
    public bool OverGround { get => P.HeightRef == ApHeightRef.Ground; set { _ap?.SetHeightRef(value ? ApHeightRef.Ground : ApHeightRef.Inertial); Changed(); } }

    [Slider(null)]
    [Range<float>(ApPanel.HoverSpeedMin, ApPanel.HoverSpeedMax, RangeMode.InIn)]
    [Label("Hover Speed (m/s)")]
    [Increment(1f, 5f, 10f)]
    public float HoverSpeed { get => P.HoverSpeed; set => _ap?.SetHoverSpeed(value); }

    [Label("Envelope Protection")]
    public bool Protection { get => P.Protection; set => _ap?.SetProtection(value); }

    [Slider(null)]
    [Range<float>(ApPanel.GLimitMin, ApPanel.GLimitMax, RangeMode.InIn)]
    [Label("G Limit")]
    [Increment(0.1f, 0.5f, 1f)]
    public float GLimit { get => P.GLimit; set => _ap?.SetGLimit(value); }

    [Slider(null)]
    [Range<float>(ApPanel.AoaMarginMin, ApPanel.AoaMarginMax, RangeMode.InIn)]
    [Label("Stall Margin (deg)")]
    [Increment(0.5f, 1f, 5f)]
    public float AoaMargin { get => P.AoaMarginDeg; set => _ap?.SetAoaMargin(value); }

    [Slider(null)]
    [Range<float>(ApPanel.FloorMin, ApPanel.FloorMax, RangeMode.InIn)]
    [Label("Terrain Floor (m)")]
    [Increment(1f, 5f, 25f)]
    public float FloorM { get => P.FloorM; set => _ap?.SetFloor(value); }

    [Label("Terrain Look-Ahead")]
    public bool LookAhead { get => P.LookAhead; set => _ap?.SetLookAhead(value); }

    [Label("Keep Mode Unattended")]
    public bool Unattended { get => P.Unattended; set => _ap?.SetUnattended(value); }

    void Changed()
    {
        OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(CanHover)); OnPropertyChanged(nameof(CanHeight)); OnPropertyChanged(nameof(CanOff));
        OnPropertyChanged(nameof(HeightM)); OnPropertyChanged(nameof(OverGround));
    }
}
