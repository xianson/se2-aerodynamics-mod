#pragma warning disable
using System;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails;
using Keen.VRage.Library.Serialization.Validation;
using Keen.VRage.Library.UI.PropertyInspector;
using Keen.VRage.Library.UI.PropertyInspector.Visibility;

namespace AeroMod;

/// <summary>
/// The Autopilot block's terminal - the autopilot's whole control surface (AeroAutopilotComponent): the mode buttons (Hover hold,
/// Height hold, Flight hold, Navigate, Engage, Level off, Return, Off - toolbar actions too, with hidden Toggle ones: [Button]
/// methods are what the toolbar takes from a mod panel), the status line, and ONLY the fields of the mode chosen
/// ([ShowInInspectorWhen] on the Show* switches below): the hover's height and speed; the flight style and holds; the route; the
/// behaviour's own numbers (the attack run's abort and re-run only for an attack run, the angle only for a broadside), the target's
/// source, text and filter, the Enemy switch (Engage, or an enemy ship), the aim; then the protection's limits. On / off and power are the game's own panel. Host / single player, as
/// every mod panel (DetailModelPairing). The status line is read when the panel shows it (a button press refreshes it).
/// </summary>
[BlockDetailProvider]
[BlockDetailDisplayMode(DisplayMode.Controls)]
// SE2 4.0 runs the serializer generators on mod scripts: [NoSerialize] keeps them off a mod model (AeroAirbrakeBlockDetailModel).
[Keen.VRage.Library.Serialization.NoSerialize]
public partial class AeroAutopilotBlockDetailModel : BlockDetailModel
{
    // THE ORDER ON THE PANEL (the inspector sorts by it - ModDetailTypeInfo; the [Button] methods placed by name too): the status,
    // the mode buttons, the chosen mode's fields top down, what it does unattended, the protection last. The switches and the
    // toolbar's hidden toggles after (never shown). Tests/AutopilotSim PanelLayoutPack reads this list and checks it mode by mode.
    static readonly Keen.VRage.Library.Serialization.AccessibleTypeInfo _typeInfo = ModDetailTypeInfo.Build<AeroAutopilotBlockDetailModel>(
        "Autopilot: hover, height and flight holds, GPS navigation and target behaviours for this ship, with envelope protection.",
        nameof(Status),
        nameof(HoverHold), nameof(HeightHold), nameof(FlightHold), nameof(Navigate), nameof(Engage), nameof(LevelOff), nameof(Return), nameof(AutopilotOff),
        nameof(HeightM), nameof(OverGround), nameof(HoverSpeed),
        nameof(Style), nameof(HeadingOn), nameof(HeadingDeg), nameof(AltOn), nameof(AltM), nameof(AltOverGround), nameof(VsMs),
        nameof(SpeedOn), nameof(SpeedMs), nameof(BankDeg), nameof(MinAglM),
        nameof(Route), nameof(RouteLoop), nameof(LoiterM),
        nameof(Behaviour), nameof(RangeM), nameof(AbortM), nameof(RerunS), nameof(AngleDeg), nameof(AltOffsetM), nameof(Evasive), nameof(Aggression),
        nameof(DfFight), nameof(DfHeadOn), nameof(DfRespectGuns), nameof(DfSkill), nameof(DfBingoFuelPct), nameof(DfBingoDamagePct),
        nameof(RgLaw), nameof(RgNavConst), nameof(RgTargetAccelGain), nameof(RgMaxLatG), nameof(RgGravityComp), nameof(RgArmM), nameof(RgTimeoutS),
        nameof(FmRightM), nameof(FmUpM), nameof(FmBackM), nameof(FmCatchUpMs), nameof(DeckM), nameof(CeilingM),
        nameof(TargetSrc), nameof(TargetText), nameof(TargetFilter), nameof(AimOn), nameof(MuzzleSpeed), nameof(ConeDeg),
        nameof(Enemy), nameof(EnemyText),
        nameof(Unattended),
        nameof(Protection), nameof(GLimit), nameof(AoaMargin), nameof(FloorM), nameof(LookAhead), nameof(AvoidOn), nameof(AvoidS),
        nameof(CanHover), nameof(CanHeight), nameof(CanFlight), nameof(CanNavigate), nameof(CanEngage), nameof(CanOff),
        nameof(ShowHover), nameof(ShowPilot), nameof(ShowFlight), nameof(ShowRoute), nameof(ShowEngage), nameof(ShowAttack), nameof(ShowBroadside),
        nameof(ShowTargetText), nameof(ShowAim), nameof(ShowHeading), nameof(ShowAlt), nameof(ShowAltRef), nameof(ShowSpeed), nameof(ShowEnemy), nameof(ShowAvoidS),
        nameof(ShowTactics), nameof(ShowAggression), nameof(ShowDogfight), nameof(ShowRocket), nameof(ShowFormation), nameof(ShowBand), nameof(ShowTargetFilter), nameof(ShowAimSw), nameof(ShowNever),
        nameof(ToggleHoverHold), nameof(ToggleHeightHold), nameof(ToggleFlightHold), nameof(ToggleNavigate), nameof(ToggleEngage));
    /// <summary>The game's member-order lookup (MemberReference.DeclarationOrder asks AccessibleTypeIndexer, which finds a type's
    /// info by its nested TypeInfoHolder.Value): without it every member here sorted as -1, in no stable order - the panel came
    /// out shuffled (protection first) with the buttons last, below the fold (in game, 2026-10-19). ModDetailTypeInfo.</summary>
    public static class TypeInfoHolder { public static readonly Keen.VRage.Library.Serialization.AccessibleTypeInfo Value = _typeInfo; }
    public override Keen.VRage.Library.Serialization.AccessibleTypeInfo GetTypeInfo() => _typeInfo;

    [Keen.VRage.DCS.Annotations.Component]
    private AeroAutopilotComponent _ap;

    public AeroAutopilotBlockDetailModel() { DetailModelPairing.Pair(this, _ap); }

    ApPanel P => _ap?.Panel ?? ApPanel.Defaults;
    void Edit(AeroAutopilotComponent.PanelEdit e) { _ap?.Edit(e); Changed(); }

    [Label("Status")]
    [ReadOnlyInInspector]
    public string Status => _ap?.Status ?? "";

    // ── what is shown: the fields of the mode chosen only ──
    [HideInInspector] public bool CanHover => _ap != null && P.Mode != ApMode.HoverHold;
    [HideInInspector] public bool CanHeight => _ap != null && P.Mode != ApMode.HeightHold;
    [HideInInspector] public bool CanFlight => _ap != null && P.Mode != ApMode.FlightHold;
    [HideInInspector] public bool CanNavigate => _ap != null && P.Mode != ApMode.Navigate;
    [HideInInspector] public bool CanEngage => _ap != null && P.Mode != ApMode.Engage;
    [HideInInspector] public bool CanOff => _ap != null && P.Mode != ApMode.Off;
    [HideInInspector] public bool ShowHover => ApPanelLayout.ShowHover(P);
    [HideInInspector] public bool ShowPilot => ApPanelLayout.ShowPilot(P);
    [HideInInspector] public bool ShowFlight => ApPanelLayout.ShowFlight(P);
    [HideInInspector] public bool ShowRoute => ApPanelLayout.ShowRoute(P);
    [HideInInspector] public bool ShowEngage => ApPanelLayout.ShowEngage(P);
    [HideInInspector] public bool ShowAttack => ApPanelLayout.ShowAttack(P);
    [HideInInspector] public bool ShowBroadside => ApPanelLayout.ShowBroadside(P);
    [HideInInspector] public bool ShowTargetText => ApPanelLayout.ShowTargetText(P);
    [HideInInspector] public bool ShowAim => ApPanelLayout.ShowAim(P);
    [HideInInspector] public bool ShowHeading => ApPanelLayout.ShowHeading(P);
    [HideInInspector] public bool ShowAlt => ApPanelLayout.ShowAlt(P);
    [HideInInspector] public bool ShowAltRef => ApPanelLayout.ShowAltRef(P);
    [HideInInspector] public bool ShowSpeed => ApPanelLayout.ShowSpeed(P);
    [HideInInspector] public bool ShowAvoidS => ApPanelLayout.ShowAvoidS(P);
    [HideInInspector] public bool ShowEnemy => _ap != null && ApPanelLayout.ShowEnemy(P, Autopilot.EnemyRule, _ap.GridIsEnemy);
    [HideInInspector] public bool ShowTactics => ApPanelLayout.ShowTactics(P);
    [HideInInspector] public bool ShowAggression => ApPanelLayout.ShowAggression(P);
    [HideInInspector] public bool ShowDogfight => ApPanelLayout.ShowDogfight(P);
    [HideInInspector] public bool ShowRocket => ApPanelLayout.ShowRocket(P);
    [HideInInspector] public bool ShowFormation => ApPanelLayout.ShowFormation(P);
    [HideInInspector] public bool ShowBand => ApPanelLayout.ShowBand(P);
    [HideInInspector] public bool ShowTargetFilter => ApPanelLayout.ShowTargetFilter(P);
    [HideInInspector] public bool ShowAimSw => ApPanelLayout.ShowAimSw(P);
    /// <summary>The toolbar's hidden toggles: the game shows every public button method on the panel (HideInInspector is not read
    /// for a method - TerminalInspectorViewModel.InjectInspectable), so a switch that is never true keeps them off it (the toolbar
    /// lists them all the same: BlockDetailProviderIndexer.IsButtonEligible). In game, 2026-10-20: they showed at the bottom.</summary>
    [HideInInspector] public bool ShowNever => ApPanelLayout.ShowNever;

    // ── the modes ──
    [Label("Hover Hold")] [Button(null)] [WritableInInspectorWhen("CanHover", WritableWhen.True)]
    public void HoverHold() { _ap?.SetMode(ApMode.HoverHold); Changed(); }

    [Label("Height Hold")] [Button(null)] [WritableInInspectorWhen("CanHeight", WritableWhen.True)]
    public void HeightHold() { _ap?.SetMode(ApMode.HeightHold); Changed(); }

    [Label("Flight Hold")] [Button(null)] [WritableInInspectorWhen("CanFlight", WritableWhen.True)]
    public void FlightHold() { _ap?.SetMode(ApMode.FlightHold); Changed(); }

    [Label("Navigate")] [Button(null)] [WritableInInspectorWhen("CanNavigate", WritableWhen.True)]
    public void Navigate() { _ap?.SetMode(ApMode.Navigate); Changed(); }

    [Label("Engage")] [Button(null)] [WritableInInspectorWhen("CanEngage", WritableWhen.True)]
    public void Engage() { _ap?.SetMode(ApMode.Engage); Changed(); }

    /// <summary>Level off / recover: flight hold, every hold caught from the flight now.</summary>
    [Label("Level Off")] [Button(null)]
    public void LevelOff() { _ap?.LevelOff(); Changed(); }

    /// <summary>Back to where a mode was first engaged, a loiter there; again: the route resumed.</summary>
    [Label("Return")] [Button(null)]
    public void Return() { _ap?.ToggleReturn(); Changed(); }

    [Label("Autopilot Off")] [Button(null)] [WritableInInspectorWhen("CanOff", WritableWhen.True)]
    public void AutopilotOff() { _ap?.SetMode(ApMode.Off); Changed(); }

    [Label("Toggle Hover Hold")] [Button(null)] [HideInInspector] [ShowInInspectorWhen("ShowNever", ShowWhen.True)]
    public void ToggleHoverHold() { _ap?.ToggleMode(ApMode.HoverHold); Changed(); }

    [Label("Toggle Height Hold")] [Button(null)] [HideInInspector] [ShowInInspectorWhen("ShowNever", ShowWhen.True)]
    public void ToggleHeightHold() { _ap?.ToggleMode(ApMode.HeightHold); Changed(); }

    [Label("Toggle Flight Hold")] [Button(null)] [HideInInspector] [ShowInInspectorWhen("ShowNever", ShowWhen.True)]
    public void ToggleFlightHold() { _ap?.ToggleMode(ApMode.FlightHold); Changed(); }

    [Label("Toggle Navigate")] [Button(null)] [HideInInspector] [ShowInInspectorWhen("ShowNever", ShowWhen.True)]
    public void ToggleNavigate() { _ap?.ToggleMode(ApMode.Navigate); Changed(); }

    [Label("Toggle Engage")] [Button(null)] [HideInInspector] [ShowInInspectorWhen("ShowNever", ShowWhen.True)]
    public void ToggleEngage() { _ap?.ToggleMode(ApMode.Engage); Changed(); }

    // ── the flight style (the pilot modes) ──
    /// <summary>Auto: from the ship (wings - airplane; one lift axis - helicopter; thrust every way - six-axis).</summary>
    [Label("Flight Style")] [ShowInInspectorWhen("ShowPilot", ShowWhen.True)]
    public ApStyle Style { get => P.Style; set => Edit((ref ApPanel p) => p.Style = value); }

    // ── hover / height hold ──
    /// <summary>The height held: engaging catches the ship's own (shown here); a new number flies it there.</summary>
    [Slider(null)] [Range<float>(0f, 5000f, RangeMode.InIn)] [Label("Height (m)")] [Increment(1f, 10f, 100f)] [ShowInInspectorWhen("ShowHover", ShowWhen.True)]
    public float HeightM { get => P.HeightM; set { _ap?.SetHeight(value); Changed(); } }

    /// <summary>On: the height over the ground under the ship (the terrain followed). Off: a level altitude (over the ground where
    /// the mode was engaged).</summary>
    [Label("Height Over Ground")] [ShowInInspectorWhen("ShowHover", ShowWhen.True)]
    public bool OverGround { get => P.HeightRef == ApHeightRef.Ground; set { _ap?.SetHeightRef(value ? ApHeightRef.Ground : ApHeightRef.Inertial); Changed(); } }

    [Slider(null)] [Range<float>(ApPanel.HoverSpeedMin, ApPanel.HoverSpeedMax, RangeMode.InIn)] [Label("Hover Speed (m/s)")] [Increment(1f, 5f, 10f)] [ShowInInspectorWhen("ShowHover", ShowWhen.True)]
    public float HoverSpeed { get => P.HoverSpeed; set => _ap?.SetHoverSpeed(value); }

    // ── the holds ──
    [Label("Heading Hold")] [ShowInInspectorWhen("ShowFlight", ShowWhen.True)]
    public bool HeadingOn { get => P.HeadingOn; set => Edit((ref ApPanel p) => p.HeadingOn = value); }

    [Slider(null)] [Range<float>(0f, 359f, RangeMode.InIn)] [Label("Heading (deg)")] [Increment(1f, 5f, 45f)] [ShowInInspectorWhen("ShowHeading", ShowWhen.True)]
    public float HeadingDeg { get => P.HeadingDeg; set => Edit((ref ApPanel p) => p.HeadingDeg = value); }

    [Label("Altitude Hold")] [ShowInInspectorWhen("ShowPilot", ShowWhen.True)]
    public bool AltOn { get => P.AltOn; set => Edit((ref ApPanel p) => p.AltOn = value); }

    [Slider(null)] [Range<float>(0f, 20000f, RangeMode.InIn)] [Label("Altitude (m)")] [Increment(10f, 100f, 1000f)] [ShowInInspectorWhen("ShowAlt", ShowWhen.True)]
    public float AltM { get => P.AltM; set => Edit((ref ApPanel p) => p.AltM = value); }

    /// <summary>The altitude's reference (the same setting as the hover's Height Over Ground): over the ground under the ship, or level.</summary>
    [Label("Altitude Over Ground")] [ShowInInspectorWhen("ShowAltRef", ShowWhen.True)]
    public bool AltOverGround { get => OverGround; set => OverGround = value; }

    [Slider(null)] [Range<float>(ApPanel.VsMin, 100f, RangeMode.InIn)] [Label("Climb Rate (m/s)")] [Increment(0.5f, 5f, 20f)] [ShowInInspectorWhen("ShowAlt", ShowWhen.True)]
    public float VsMs { get => P.VsMs; set => Edit((ref ApPanel p) => p.VsMs = value); }

    [Label("Speed Hold")] [ShowInInspectorWhen("ShowPilot", ShowWhen.True)]
    public bool SpeedOn { get => P.SpeedOn; set => Edit((ref ApPanel p) => p.SpeedOn = value); }

    /// <summary>Through the air (an airplane); over the ground, and the most it chases at (a helicopter, six-axis). 0: as engaged.</summary>
    [Slider(null)] [Range<float>(0f, 1000f, RangeMode.InIn)] [Label("Speed (m/s)")] [Increment(1f, 10f, 50f)] [ShowInInspectorWhen("ShowSpeed", ShowWhen.True)]
    public float SpeedMs { get => P.SpeedMs; set => Edit((ref ApPanel p) => p.SpeedMs = value); }

    [Slider(null)] [Range<float>(ApPanel.BankMin, ApPanel.BankMax, RangeMode.InIn)] [Label("Bank Limit (deg)")] [Increment(1f, 5f, 15f)] [ShowInInspectorWhen("ShowPilot", ShowWhen.True)]
    public float BankDeg { get => P.BankDeg; set => Edit((ref ApPanel p) => p.BankDeg = value); }

    // ── the route ──
    /// <summary>GPS marker names or coordinates ("x y z"), separated by ';' or new lines.</summary>
    [Label("Route (GPS names)")] [ShowInInspectorWhen("ShowRoute", ShowWhen.True)]
    public string Route { get => P.Route; set => Edit((ref ApPanel p) => p.Route = value ?? ""); }

    [Label("Loop Route")] [ShowInInspectorWhen("ShowRoute", ShowWhen.True)]
    public bool RouteLoop { get => P.RouteLoop; set => Edit((ref ApPanel p) => p.RouteLoop = value); }

    [Slider(null)] [Range<float>(ApPanel.LoiterMin, 5000f, RangeMode.InIn)] [Label("Loiter Radius (m)")] [Increment(10f, 100f, 500f)] [ShowInInspectorWhen("ShowRoute", ShowWhen.True)]
    public float LoiterM { get => P.LoiterM; set => Edit((ref ApPanel p) => p.LoiterM = value); }

    [Slider(null)] [Range<float>(0f, 1000f, RangeMode.InIn)] [Label("Terrain Minimum (m)")] [Increment(5f, 25f, 100f)] [ShowInInspectorWhen("ShowPilot", ShowWhen.True)]
    public float MinAglM { get => P.MinAglM; set => Edit((ref ApPanel p) => p.MinAglM = value); }

    // ── the behaviour ──
    [Label("Behaviour")] [ShowInInspectorWhen("ShowEngage", ShowWhen.True)]
    public ApBehaviour Behaviour { get => P.Behaviour; set => Edit((ref ApPanel p) => p.Behaviour = value == ApBehaviour.Extend ? ApBehaviour.Auto : value); }

    /// <summary>The attack run's begin distance, the standoff range, the orbit radius, the broadside range, the gun range of a pursuit.</summary>
    [Slider(null)] [Range<float>(ApPanel.RangeMin, 10000f, RangeMode.InIn)] [Label("Range (m)")] [Increment(10f, 100f, 500f)] [ShowInInspectorWhen("ShowTactics", ShowWhen.True)]
    public float RangeM { get => P.RangeM; set => Edit((ref ApPanel p) => p.RangeM = value); }

    [Slider(null)] [Range<float>(ApPanel.AbortMin, 5000f, RangeMode.InIn)] [Label("Abort Distance (m)")] [Increment(10f, 50f, 250f)] [ShowInInspectorWhen("ShowAttack", ShowWhen.True)]
    public float AbortM { get => P.AbortM; set => Edit((ref ApPanel p) => p.AbortM = value); }

    [Slider(null)] [Range<float>(ApPanel.RerunMin, 300f, RangeMode.InIn)] [Label("Re-run Time (s)")] [Increment(1f, 5f, 30f)] [ShowInInspectorWhen("ShowAttack", ShowWhen.True)]
    public float RerunS { get => P.RerunS; set => Edit((ref ApPanel p) => p.RerunS = value); }

    /// <summary>0 nose on, 90 circling (the target abeam).</summary>
    [Slider(null)] [Range<float>(0f, 180f, RangeMode.InIn)] [Label("Broadside Angle (deg)")] [Increment(1f, 5f, 15f)] [ShowInInspectorWhen("ShowBroadside", ShowWhen.True)]
    public float AngleDeg { get => P.AngleDeg; set => Edit((ref ApPanel p) => p.AngleDeg = value); }

    /// <summary>Over the target (+) or under it (-): the attack run's, the orbit's, the hover above / below's height.</summary>
    [Slider(null)] [Range<float>(-2000f, 2000f, RangeMode.InIn)] [Label("Height Over Target (m)")] [Increment(5f, 25f, 100f)] [ShowInInspectorWhen("ShowTactics", ShowWhen.True)]
    public float AltOffsetM { get => P.AltOffsetM; set => Edit((ref ApPanel p) => p.AltOffsetM = value); }

    [Label("Evasive (jink)")] [ShowInInspectorWhen("ShowTactics", ShowWhen.True)]
    public bool Evasive { get => P.Evasive; set => Edit((ref ApPanel p) => p.Evasive = value); }

    [Slider(null)] [Range<float>(0f, 1f, RangeMode.InIn)] [Label("Aggressiveness")] [Increment(0.05f, 0.1f, 0.25f)] [ShowInInspectorWhen("ShowAggression", ShowWhen.True)]
    public float Aggression { get => P.Aggression; set => Edit((ref ApPanel p) => p.Aggression = value); }

    // ── (2026-10-19) the DOGFIGHT slot's values (ApDogfightParams - the author's ApDogfight reads them; the core acts on none but
    //    the band below) ──
    [Label("Preferred Fight")] [ShowInInspectorWhen("ShowDogfight", ShowWhen.True)]
    public ApFightPref DfFight { get => P.DfFight; set => Edit((ref ApPanel p) => p.DfFight = value); }

    [Label("Head-on Passes")] [ShowInInspectorWhen("ShowDogfight", ShowWhen.True)]
    public bool DfHeadOn { get => P.DfHeadOn; set => Edit((ref ApPanel p) => p.DfHeadOn = value); }

    [Slider(null)] [Range<float>(0f, 1f, RangeMode.InIn)] [Label("Respect Enemy Guns")] [Increment(0.05f, 0.1f, 0.25f)] [ShowInInspectorWhen("ShowDogfight", ShowWhen.True)]
    public float DfRespectGuns { get => P.DfRespectGuns; set => Edit((ref ApPanel p) => p.DfRespectGuns = value); }

    [Slider(null)] [Range<float>(0f, 1f, RangeMode.InIn)] [Label("Skill")] [Increment(0.05f, 0.1f, 0.25f)] [ShowInInspectorWhen("ShowDogfight", ShowWhen.True)]
    public float DfSkill { get => P.DfSkill; set => Edit((ref ApPanel p) => p.DfSkill = value); }

    /// <summary>Break off and fly home under this much fuel (0: off).</summary>
    [Slider(null)] [Range<float>(0f, 100f, RangeMode.InIn)] [Label("Bingo Fuel (%)")] [Increment(1f, 5f, 10f)] [ShowInInspectorWhen("ShowDogfight", ShowWhen.True)]
    public float DfBingoFuelPct { get => P.DfBingoFuelPct; set => Edit((ref ApPanel p) => p.DfBingoFuelPct = value); }

    /// <summary>Break off with this much of the ship's integrity lost since the engage (0: off).</summary>
    [Slider(null)] [Range<float>(0f, 100f, RangeMode.InIn)] [Label("Bingo Damage (%)")] [Increment(1f, 5f, 10f)] [ShowInInspectorWhen("ShowDogfight", ShowWhen.True)]
    public float DfBingoDamagePct { get => P.DfBingoDamagePct; set => Edit((ref ApPanel p) => p.DfBingoDamagePct = value); }

    // ── the ROCKET-GUIDANCE slot's values (ApRocketParams) ──
    [Label("Guidance Law")] [ShowInInspectorWhen("ShowRocket", ShowWhen.True)]
    public ApGuidanceLaw RgLaw { get => P.RgLaw; set => Edit((ref ApPanel p) => p.RgLaw = value); }

    [Slider(null)] [Range<float>(ApPanel.NavConstMin, ApPanel.NavConstMax, RangeMode.InIn)] [Label("Navigation Constant")] [Increment(0.1f, 0.5f, 1f)] [ShowInInspectorWhen("ShowRocket", ShowWhen.True)]
    public float RgNavConst { get => P.RgNavConst; set => Edit((ref ApPanel p) => p.RgNavConst = value); }

    [Slider(null)] [Range<float>(0f, ApPanel.AccelGainMax, RangeMode.InIn)] [Label("Target Accel Gain")] [Increment(0.05f, 0.25f, 0.5f)] [ShowInInspectorWhen("ShowRocket", ShowWhen.True)]
    public float RgTargetAccelGain { get => P.RgTargetAccelGain; set => Edit((ref ApPanel p) => p.RgTargetAccelGain = value); }

    /// <summary>The most lateral load the guidance may ask (the protection clips further).</summary>
    [Slider(null)] [Range<float>(ApPanel.LatGMin, 60f, RangeMode.InIn)] [Label("Max Lateral g")] [Increment(0.5f, 1f, 5f)] [ShowInInspectorWhen("ShowRocket", ShowWhen.True)]
    public float RgMaxLatG { get => P.RgMaxLatG; set => Edit((ref ApPanel p) => p.RgMaxLatG = value); }

    [Label("Gravity Compensation")] [ShowInInspectorWhen("ShowRocket", ShowWhen.True)]
    public bool RgGravityComp { get => P.RgGravityComp; set => Edit((ref ApPanel p) => p.RgGravityComp = value); }

    [Slider(null)] [Range<float>(0f, 5000f, RangeMode.InIn)] [Label("Arm Distance (m)")] [Increment(5f, 25f, 100f)] [ShowInInspectorWhen("ShowRocket", ShowWhen.True)]
    public float RgArmM { get => P.RgArmM; set => Edit((ref ApPanel p) => p.RgArmM = value); }

    /// <summary>Self-destruct / let go after this long (0: off).</summary>
    [Slider(null)] [Range<float>(0f, 600f, RangeMode.InIn)] [Label("Let-go Timeout (s)")] [Increment(1f, 10f, 60f)] [ShowInInspectorWhen("ShowRocket", ShowWhen.True)]
    public float RgTimeoutS { get => P.RgTimeoutS; set => Edit((ref ApPanel p) => p.RgTimeoutS = value); }

    // ── FORMATION: the slot in the leader's frame (the leader: the target source's grid) ──
    [Slider(null)] [Range<float>(-500f, 500f, RangeMode.InIn)] [Label("Slot Right (m)")] [Increment(1f, 5f, 25f)] [ShowInInspectorWhen("ShowFormation", ShowWhen.True)]
    public float FmRightM { get => P.FmRightM; set => Edit((ref ApPanel p) => p.FmRightM = value); }

    [Slider(null)] [Range<float>(-200f, 200f, RangeMode.InIn)] [Label("Slot Up (m)")] [Increment(1f, 5f, 25f)] [ShowInInspectorWhen("ShowFormation", ShowWhen.True)]
    public float FmUpM { get => P.FmUpM; set => Edit((ref ApPanel p) => p.FmUpM = value); }

    [Slider(null)] [Range<float>(-500f, 1000f, RangeMode.InIn)] [Label("Slot Back (m)")] [Increment(1f, 5f, 25f)] [ShowInInspectorWhen("ShowFormation", ShowWhen.True)]
    public float FmBackM { get => P.FmBackM; set => Edit((ref ApPanel p) => p.FmBackM = value); }

    /// <summary>The most speed over the leader's a gap is closed at.</summary>
    [Slider(null)] [Range<float>(0f, 200f, RangeMode.InIn)] [Label("Catch-up Speed (m/s)")] [Increment(1f, 5f, 25f)] [ShowInInspectorWhen("ShowFormation", ShowWhen.True)]
    public float FmCatchUpMs { get => P.FmCatchUpMs; set => Edit((ref ApPanel p) => p.FmCatchUpMs = value); }

    // ── the altitude band (every Engage behaviour's height keeps it; the dogfight slot reads it too) ──
    [Slider(null)] [Range<float>(0f, ApPanel.DeckMax, RangeMode.InIn)] [Label("Hard Deck (m over ground)")] [Increment(5f, 25f, 100f)] [ShowInInspectorWhen("ShowBand", ShowWhen.True)]
    public float DeckM { get => P.DeckM; set => Edit((ref ApPanel p) => p.DeckM = value); }

    [Slider(null)] [Range<float>(0f, 20000f, RangeMode.InIn)] [Label("Combat Ceiling (m)")] [Increment(10f, 100f, 1000f)] [ShowInInspectorWhen("ShowBand", ShowWhen.True)]
    public float CeilingM { get => P.CeilingM; set => Edit((ref ApPanel p) => p.CeilingM = value); }

    // ── the target and the aim ──
    /// <summary>Targeting: what the cockpit locked, else what the turrets see. GPS: a marker's name or coordinates. Grid: a grid's
    /// name. API: another mod's.</summary>
    [Label("Target Source")] [ShowInInspectorWhen("ShowEngage", ShowWhen.True)]
    public ApTargetSrc TargetSrc { get => P.TargetSrc; set => Edit((ref ApPanel p) => p.TargetSrc = value); }

    [Label("Target (GPS / grid name)")] [ShowInInspectorWhen("ShowTargetText", ShowWhen.True)]
    public string TargetText { get => P.TargetText; set => Edit((ref ApPanel p) => p.TargetText = value ?? ""); }

    /// <summary>Which targets it takes, by the game's own relation (the turrets' rule): hostile only; also neutral (another
    /// player, a neutral NPC); everything but this ship.</summary>
    [Label("Target Filter")] [ShowInInspectorWhen("ShowTargetFilter", ShowWhen.True)]
    public ApTargetFilter TargetFilter { get => P.TargetFilter; set => Edit((ref ApPanel p) => p.TargetFilter = value); }

    /// <summary>ENEMY: this ship becomes the game's hostile NPC's - its turrets and its Engage fight players. Off: back to its owner.
    /// Who may: the world's Autopilot.EnemyRule; an enemy ship's panel: creative or an admin only.</summary>
    [Label("Enemy (hostile NPC)")] [ShowInInspectorWhen("ShowEnemy", ShowWhen.True)]
    public bool Enemy { get => _ap != null && _ap.GridIsEnemy; set { _ap?.AskEnemy(value); Changed(); } }

    [Label("Enemy Status")] [ReadOnlyInInspector] [ShowInInspectorWhen("ShowEnemy", ShowWhen.True)]
    public string EnemyText => _ap?.EnemyText ?? "";

    /// <summary>The nose (the fixed guns' axis) on the target's lead point - the protection still clips.</summary>
    [Label("Aim Nose at Target")] [ShowInInspectorWhen("ShowAimSw", ShowWhen.True)]
    public bool AimOn { get => P.AimOn; set => Edit((ref ApPanel p) => p.AimOn = value); }

    /// <summary>0: the grid's own fixed guns' projectile speed.</summary>
    [Slider(null)] [Range<float>(0f, 5000f, RangeMode.InIn)] [Label("Muzzle Speed (m/s)")] [Increment(10f, 50f, 250f)] [ShowInInspectorWhen("ShowAim", ShowWhen.True)]
    public float MuzzleSpeed { get => P.MuzzleSpeed; set => Edit((ref ApPanel p) => p.MuzzleSpeed = value); }

    [Slider(null)] [Range<float>(ApPanel.ConeMin, 20f, RangeMode.InIn)] [Label("Aim Cone (deg)")] [Increment(0.1f, 0.5f, 2f)] [ShowInInspectorWhen("ShowAim", ShowWhen.True)]
    public float ConeDeg { get => P.ConeDeg; set => Edit((ref ApPanel p) => p.ConeDeg = value); }

    // ── the protection ──
    [Label("Envelope Protection")]
    public bool Protection { get => P.Protection; set => _ap?.SetProtection(value); }

    [Slider(null)] [Range<float>(ApPanel.GLimitMin, ApPanel.GLimitMax, RangeMode.InIn)] [Label("G Limit")] [Increment(0.1f, 0.5f, 1f)]
    public float GLimit { get => P.GLimit; set => _ap?.SetGLimit(value); }

    [Slider(null)] [Range<float>(ApPanel.AoaMarginMin, ApPanel.AoaMarginMax, RangeMode.InIn)] [Label("Stall Margin (deg)")] [Increment(0.5f, 1f, 5f)]
    public float AoaMargin { get => P.AoaMarginDeg; set => _ap?.SetAoaMargin(value); }

    [Slider(null)] [Range<float>(ApPanel.FloorMin, ApPanel.FloorMax, RangeMode.InIn)] [Label("Terrain Floor (m)")] [Increment(1f, 5f, 25f)] [ShowInInspectorWhen("ShowHover", ShowWhen.True)]
    public float FloorM { get => P.FloorM; set => _ap?.SetFloor(value); }

    [Label("Terrain Look-Ahead")]
    public bool LookAhead { get => P.LookAhead; set => _ap?.SetLookAhead(value); }

    /// <summary>Other grids, asteroids and the target: a sidestep or a pull-up when the closest approach along the velocity now
    /// would come too near (every mode; through the same protection).</summary>
    [Label("Collision Avoidance")]
    public bool AvoidOn { get => P.AvoidOn; set => Edit((ref ApPanel p) => p.AvoidOn = value); }

    /// <summary>How far ahead it looks for a closest approach (s).</summary>
    [Slider(null)] [Range<float>(ApPanel.AvoidSMin, ApPanel.AvoidSMax, RangeMode.InIn)] [Label("Avoidance Look-Ahead (s)")] [Increment(0.5f, 1f, 2f)] [ShowInInspectorWhen("ShowAvoidS", ShowWhen.True)]
    public float AvoidS { get => P.AvoidS; set => Edit((ref ApPanel p) => p.AvoidS = value); }

    [Label("Keep Mode Unattended")]
    public bool Unattended { get => P.Unattended; set => _ap?.SetUnattended(value); }

    static readonly string[] _notify =
    {
        nameof(Status), nameof(CanHover), nameof(CanHeight), nameof(CanFlight), nameof(CanNavigate), nameof(CanEngage), nameof(CanOff),
        nameof(ShowHover), nameof(ShowPilot), nameof(ShowFlight), nameof(ShowRoute), nameof(ShowEngage), nameof(ShowAttack), nameof(ShowBroadside),
        nameof(ShowTargetText), nameof(ShowAim), nameof(ShowHeading), nameof(ShowAlt), nameof(ShowAltRef), nameof(ShowSpeed), nameof(ShowEnemy), nameof(ShowAvoidS),
        nameof(ShowTactics), nameof(ShowAggression), nameof(ShowDogfight), nameof(ShowRocket), nameof(ShowFormation), nameof(ShowBand), nameof(ShowTargetFilter), nameof(ShowAimSw), nameof(ShowNever),
        nameof(Enemy), nameof(EnemyText),
        nameof(HeightM), nameof(OverGround), nameof(AltOverGround), nameof(HeadingDeg), nameof(AltM), nameof(SpeedMs),
    };
    void Changed() { foreach (var n in _notify) OnPropertyChanged(n); }
}
