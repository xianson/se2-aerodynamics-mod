#pragma warning disable
using System;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails;
using Keen.VRage.Library.Serialization.Validation;
using Keen.VRage.Library.UI.PropertyInspector;
using Keen.VRage.Library.UI.PropertyInspector.Visibility;

namespace AeroMod;

/// <summary>
/// The Autopilot block's terminal - the autopilot's whole control surface (AeroAutopilotComponent). 2026-10-20 redesign (the user:
/// the old one "doesn't make much sense" - every button labelled twice, eight mode buttons filling the first screen, the mode only a
/// greyed button, the status cut off, "Behaviour" / "Style" jargon): the STATUS first, its whole text wrapped ([Multiline]); ONE
/// Mode dropdown (ApModeChoice: Off, Hover, Hold height, Cruise, Follow route, Return home, Follow ship, Attack) with a line saying
/// what it does; Level Off the one button (a blank row label - the game shows a button's [Label] beside it and its content on it);
/// then ONLY the chosen mode's fields ([ShowInInspectorWhen] on ApPanelLayout's switches): the hover's height and speed; "Fly as";
/// Follow ship's leader and slot; Attack's target, movement, facing (a desire, ApFacing) and aim priority, band, Enemy; the route;
/// the holds; "Keep flying when I leave the seat"; the Safety limits last. Each dropdown has a line under it naming what its choice
/// does, and every choice with its line in its tooltip (MembersDocumentation). The toolbar keeps every mode as an action (the
/// methods here, never shown on the panel - ShowNever; their names are what saved toolbars hold). On / off and power are the game's
/// own panel. Host / single player, as every mod panel (DetailModelPairing). The status line is read when the panel shows it.
/// </summary>
[BlockDetailProvider]
[BlockDetailDisplayMode(DisplayMode.Controls)]
// SE2 4.0 runs the serializer generators on mod scripts: [NoSerialize] keeps them off a mod model (AeroAirbrakeBlockDetailModel).
[Keen.VRage.Library.Serialization.NoSerialize]
public partial class AeroAutopilotBlockDetailModel : BlockDetailModel
{
    // THE ORDER ON THE PANEL (the inspector sorts by it - ModDetailTypeInfo; the [Button] methods placed by name too): the status,
    // the mode, Level Off, the chosen mode's fields top down, unattended, the safety limits last. The switches and the toolbar's
    // actions after (never shown). Tests/AutopilotSim PanelLayoutPack reads this list and checks it mode by mode.
    static readonly Keen.VRage.Library.Serialization.AccessibleTypeInfo _typeInfo = ModDetailTypeInfo.Build<AeroAutopilotBlockDetailModel>(
        "Autopilot: hover, hold a height, cruise, follow a route or a ship, return home, attack - with safety limits.", Tips(),
        nameof(Status),
        nameof(Mode), nameof(ModeHint), nameof(LevelOffNow),
        nameof(HeightM), nameof(OverGround), nameof(HoverSpeed),
        nameof(Style), nameof(StyleHint), nameof(HeadingOn), nameof(HeadingDeg),
        nameof(TargetSrc), nameof(TargetHint), nameof(TargetText), nameof(TargetFilter),
        nameof(AttackMove), nameof(MoveHint), nameof(RangeM), nameof(AbortM), nameof(RerunS), nameof(AltOffsetM), nameof(Evasive), nameof(Aggression),
        nameof(DfFight), nameof(DfHeadOn), nameof(DfRespectGuns), nameof(DfSkill), nameof(DfBingoFuelPct), nameof(DfBingoDamagePct),
        nameof(RgLaw), nameof(RgNavConst), nameof(RgTargetAccelGain), nameof(RgMaxLatG), nameof(RgGravityComp), nameof(RgArmM), nameof(RgTimeoutS),
        nameof(FmRightM), nameof(FmUpM), nameof(FmBackM), nameof(FmCatchUpMs),
        nameof(Facing), nameof(FacingHint), nameof(AimPriority), nameof(MuzzleSpeed), nameof(ConeDeg),
        nameof(DeckM), nameof(CeilingM),
        nameof(Enemy), nameof(EnemyText),
        nameof(Route), nameof(RouteLoop), nameof(LoiterM),
        nameof(AltOn), nameof(AltM), nameof(AltOverGround), nameof(VsMs), nameof(SpeedOn), nameof(SpeedMs), nameof(BankDeg), nameof(MinAglM),
        nameof(Unattended),
        nameof(Protection), nameof(GLimit), nameof(AoaMargin), nameof(FloorM), nameof(LookAhead), nameof(AvoidOn), nameof(AvoidS),
        nameof(ShowHover), nameof(ShowPilot), nameof(ShowFlight), nameof(ShowHolds), nameof(ShowRoute), nameof(ShowLoiter), nameof(ShowTarget),
        nameof(ShowEngage), nameof(ShowAttack), nameof(ShowTargetText), nameof(ShowAim), nameof(ShowFacing), nameof(ShowAimPriority),
        nameof(ShowHeading), nameof(ShowAlt), nameof(ShowAltRef), nameof(ShowSpeed), nameof(ShowEnemy), nameof(ShowAvoidS),
        nameof(ShowTactics), nameof(ShowAggression), nameof(ShowDogfight), nameof(ShowRocket), nameof(ShowFormation), nameof(ShowBand), nameof(ShowTargetFilter), nameof(ShowNever),
        nameof(HoverHold), nameof(HeightHold), nameof(FlightHold), nameof(Navigate), nameof(ReturnHome), nameof(FollowShip), nameof(Engage),
        nameof(LevelOff), nameof(Return), nameof(AutopilotOff),
        nameof(ToggleHoverHold), nameof(ToggleHeightHold), nameof(ToggleFlightHold), nameof(ToggleNavigate), nameof(ToggleFollowShip), nameof(ToggleEngage));
    /// <summary>The game's member-order lookup (MemberReference.DeclarationOrder asks AccessibleTypeIndexer, which finds a type's
    /// info by its nested TypeInfoHolder.Value): without it every member here sorted as -1, in no stable order - the panel came
    /// out shuffled (protection first) with the buttons last, below the fold (in game, 2026-10-19). ModDetailTypeInfo.</summary>
    public static class TypeInfoHolder { public static readonly Keen.VRage.Library.Serialization.AccessibleTypeInfo Value = _typeInfo; }
    public override Keen.VRage.Library.Serialization.AccessibleTypeInfo GetTypeInfo() => _typeInfo;

    /// <summary>The tooltips (the game shows a member's documentation on hover): every dropdown's choices with their lines.</summary>
    static System.Collections.Generic.Dictionary<string, string> Tips() => new()
    {
        [nameof(Mode)] = ApPanelLayout.Tooltip(ApPanelLayout.Name, ApPanelLayout.ModeHint, (ApModeChoice[])Enum.GetValues(typeof(ApModeChoice))),
        [nameof(Style)] = ApPanelLayout.Tooltip(ApPanelLayout.Name, ApPanelLayout.StyleHint, (ApStyle[])Enum.GetValues(typeof(ApStyle))),
        [nameof(AttackMove)] = ApPanelLayout.Tooltip(ApPanelLayout.Name, ApPanelLayout.MoveHint, (ApMove[])Enum.GetValues(typeof(ApMove))),
        [nameof(Facing)] = ApPanelLayout.Tooltip(ApPanelLayout.Name, ApPanelLayout.FacingHint, (ApFacing[])Enum.GetValues(typeof(ApFacing))),
        [nameof(TargetSrc)] = ApPanelLayout.Tooltip(ApPanelLayout.Name, ApPanelLayout.TargetHint, (ApTargetSrc[])Enum.GetValues(typeof(ApTargetSrc))),
        [nameof(AimPriority)] = "How much the flight gives way to turn the chosen face onto the target. 0: the movement only. 1: as far as the ship can while it still flies the movement.",
        [nameof(LevelOffNow)] = "Recover: wings level, then cruise on the heading, altitude and speed it has now.",
        [nameof(Unattended)] = "On: the autopilot keeps flying when nobody is in the seat. Off: it lets go when you leave.",
        [nameof(Protection)] = "Keeps the flight inside the ship's limits: the g limit, a margin from the stall, the terrain floor.",
    };

    [Keen.VRage.DCS.Annotations.Component]
    private AeroAutopilotComponent _ap;

    public AeroAutopilotBlockDetailModel() { DetailModelPairing.Pair(this, _ap); }

    ApPanel P => _ap?.Panel ?? ApPanel.Defaults;
    void Edit(AeroAutopilotComponent.PanelEdit e) { _ap?.Edit(e); Changed(); }

    [Label("Status")]
    [Multiline]
    [ReadOnlyInInspector]
    public string Status => _ap?.Status ?? "";

    // ── the mode: one dropdown, a line saying what it does, Level Off ──
    [Label("Mode")]
    public ApModeChoice Mode { get => ApPanel.ChoiceOf(P); set { _ap?.SetChoice(value); Changed(); } }

    [Label(" ")] [Multiline] [ReadOnlyInInspector]
    public string ModeHint => ApPanelLayout.ModeHint(ApPanel.ChoiceOf(P));

    /// <summary>Level off / recover - the one button (its row label blank: the game shows the label beside a button and its content
    /// on it). Not a toolbar action itself: the toolbar's is LevelOff, below.</summary>
    [Label(" ")] [Button("Level Off")] [NoToolbarAction]
    public void LevelOffNow() { _ap?.LevelOff(); Changed(); }

    // ── what is shown: the fields of the mode chosen only ──
    [HideInInspector] public bool ShowHover => ApPanelLayout.ShowHover(P);
    [HideInInspector] public bool ShowPilot => ApPanelLayout.ShowPilot(P);
    [HideInInspector] public bool ShowFlight => ApPanelLayout.ShowFlight(P);
    [HideInInspector] public bool ShowHolds => ApPanelLayout.ShowHolds(P);
    [HideInInspector] public bool ShowRoute => ApPanelLayout.ShowRoute(P);
    [HideInInspector] public bool ShowLoiter => ApPanelLayout.ShowLoiter(P);
    [HideInInspector] public bool ShowTarget => ApPanelLayout.ShowTarget(P);
    [HideInInspector] public bool ShowEngage => ApPanelLayout.ShowEngage(P);
    [HideInInspector] public bool ShowAttack => ApPanelLayout.ShowAttack(P);
    [HideInInspector] public bool ShowTargetText => ApPanelLayout.ShowTargetText(P);
    [HideInInspector] public bool ShowAim => ApPanelLayout.ShowAim(P);
    [HideInInspector] public bool ShowFacing => ApPanelLayout.ShowFacing(P);
    [HideInInspector] public bool ShowAimPriority => ApPanelLayout.ShowAimPriority(P);
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
    /// <summary>The toolbar's actions: the game shows every public button method on the panel (HideInInspector is not read for a
    /// method - TerminalInspectorViewModel.InjectInspectable), so a switch that is never true keeps them off it (the toolbar lists
    /// them all the same: BlockDetailProviderIndexer.IsButtonEligible). In game, 2026-10-20: the toggles showed at the bottom.</summary>
    [HideInInspector] public bool ShowNever => ApPanelLayout.ShowNever;

    // ── hover / hold height ──
    /// <summary>The height held: engaging catches the ship's own (shown here); a new number flies it there.</summary>
    [Slider(null)] [Range<float>(0f, 5000f, RangeMode.InIn)] [Label("Height (m)")] [Increment(1f, 10f, 100f)] [ShowInInspectorWhen("ShowHover", ShowWhen.True)]
    public float HeightM { get => P.HeightM; set { _ap?.SetHeight(value); Changed(); } }

    /// <summary>On: the height over the ground under the ship (the terrain followed). Off: a level altitude (over the ground where
    /// the mode was engaged).</summary>
    [Label("Height Over Ground")] [ShowInInspectorWhen("ShowHover", ShowWhen.True)]
    public bool OverGround { get => P.HeightRef == ApHeightRef.Ground; set { _ap?.SetHeightRef(value ? ApHeightRef.Ground : ApHeightRef.Inertial); Changed(); } }

    [Slider(null)] [Range<float>(ApPanel.HoverSpeedMin, ApPanel.HoverSpeedMax, RangeMode.InIn)] [Label("Hover Speed (m/s)")] [Increment(1f, 5f, 10f)] [ShowInInspectorWhen("ShowHover", ShowWhen.True)]
    public float HoverSpeed { get => P.HoverSpeed; set => _ap?.SetHoverSpeed(value); }

    // ── how it flies (the flight pilot's modes) ──
    [Label("Fly as")] [ShowInInspectorWhen("ShowPilot", ShowWhen.True)]
    public ApStyle Style { get => P.Style; set => Edit((ref ApPanel p) => p.Style = value); }

    [Label(" ")] [Multiline] [ReadOnlyInInspector] [ShowInInspectorWhen("ShowPilot", ShowWhen.True)]
    public string StyleHint => ApPanelLayout.StyleHint(P.Style);

    [Label("Heading Hold")] [ShowInInspectorWhen("ShowFlight", ShowWhen.True)]
    public bool HeadingOn { get => P.HeadingOn; set => Edit((ref ApPanel p) => p.HeadingOn = value); }

    [Slider(null)] [Range<float>(0f, 359f, RangeMode.InIn)] [Label("Heading (deg)")] [Increment(1f, 5f, 45f)] [ShowInInspectorWhen("ShowHeading", ShowWhen.True)]
    public float HeadingDeg { get => P.HeadingDeg; set => Edit((ref ApPanel p) => p.HeadingDeg = value); }

    // ── the target (Follow ship's leader, Attack's target) ──
    [Label("Target")] [ShowInInspectorWhen("ShowTarget", ShowWhen.True)]
    public ApTargetSrc TargetSrc { get => P.TargetSrc; set => Edit((ref ApPanel p) => p.TargetSrc = value); }

    [Label(" ")] [Multiline] [ReadOnlyInInspector] [ShowInInspectorWhen("ShowTarget", ShowWhen.True)]
    public string TargetHint => ApPanelLayout.TargetHint(P.TargetSrc);

    [Label("Target (GPS / ship name)")] [ShowInInspectorWhen("ShowTargetText", ShowWhen.True)]
    public string TargetText { get => P.TargetText; set => Edit((ref ApPanel p) => p.TargetText = value ?? ""); }

    /// <summary>Which targets it takes, by the game's own relation (the turrets' rule): hostile only; also neutral (another
    /// player, a neutral NPC); everything but this ship.</summary>
    [Label("Target Filter")] [ShowInInspectorWhen("ShowTargetFilter", ShowWhen.True)]
    public ApTargetFilter TargetFilter { get => P.TargetFilter; set => Edit((ref ApPanel p) => p.TargetFilter = value); }

    // ── Attack: the movement ──
    [Label("Attack movement")] [ShowInInspectorWhen("ShowEngage", ShowWhen.True)]
    public ApMove AttackMove
    {
        get { var p = P; return ApPanel.MoveOf(p.Behaviour == ApBehaviour.Formation ? p.AttackBh : p.Behaviour); }
        set => Edit((ref ApPanel p) => { p.Behaviour = ApPanel.BhOf(value); p.AttackBh = p.Behaviour; });
    }

    [Label(" ")] [Multiline] [ReadOnlyInInspector] [ShowInInspectorWhen("ShowEngage", ShowWhen.True)]
    public string MoveHint => ApPanelLayout.MoveHint(AttackMove);

    /// <summary>The strafing run's begin distance, the range kept, the circle's radius, the chase's gun range.</summary>
    [Slider(null)] [Range<float>(ApPanel.RangeMin, 10000f, RangeMode.InIn)] [Label("Range (m)")] [Increment(10f, 100f, 500f)] [ShowInInspectorWhen("ShowTactics", ShowWhen.True)]
    public float RangeM { get => P.RangeM; set => Edit((ref ApPanel p) => p.RangeM = value); }

    [Slider(null)] [Range<float>(ApPanel.AbortMin, 5000f, RangeMode.InIn)] [Label("Abort Distance (m)")] [Increment(10f, 50f, 250f)] [ShowInInspectorWhen("ShowAttack", ShowWhen.True)]
    public float AbortM { get => P.AbortM; set => Edit((ref ApPanel p) => p.AbortM = value); }

    [Slider(null)] [Range<float>(ApPanel.RerunMin, 300f, RangeMode.InIn)] [Label("Re-run Time (s)")] [Increment(1f, 5f, 30f)] [ShowInInspectorWhen("ShowAttack", ShowWhen.True)]
    public float RerunS { get => P.RerunS; set => Edit((ref ApPanel p) => p.RerunS = value); }

    /// <summary>Over the target (+) or under it (-): the strafing run's, the circle's, the hover over / under's height.</summary>
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

    /// <summary>The most lateral load the guidance may ask (the safety limits clip further).</summary>
    [Slider(null)] [Range<float>(ApPanel.LatGMin, 60f, RangeMode.InIn)] [Label("Max Lateral g")] [Increment(0.5f, 1f, 5f)] [ShowInInspectorWhen("ShowRocket", ShowWhen.True)]
    public float RgMaxLatG { get => P.RgMaxLatG; set => Edit((ref ApPanel p) => p.RgMaxLatG = value); }

    [Label("Gravity Compensation")] [ShowInInspectorWhen("ShowRocket", ShowWhen.True)]
    public bool RgGravityComp { get => P.RgGravityComp; set => Edit((ref ApPanel p) => p.RgGravityComp = value); }

    [Slider(null)] [Range<float>(0f, 5000f, RangeMode.InIn)] [Label("Arm Distance (m)")] [Increment(5f, 25f, 100f)] [ShowInInspectorWhen("ShowRocket", ShowWhen.True)]
    public float RgArmM { get => P.RgArmM; set => Edit((ref ApPanel p) => p.RgArmM = value); }

    /// <summary>Self-destruct / let go after this long (0: off).</summary>
    [Slider(null)] [Range<float>(0f, 600f, RangeMode.InIn)] [Label("Let-go Timeout (s)")] [Increment(1f, 10f, 60f)] [ShowInInspectorWhen("ShowRocket", ShowWhen.True)]
    public float RgTimeoutS { get => P.RgTimeoutS; set => Edit((ref ApPanel p) => p.RgTimeoutS = value); }

    // ── FOLLOW SHIP: the slot in the leader's frame (the leader: the target) ──
    [Slider(null)] [Range<float>(-500f, 500f, RangeMode.InIn)] [Label("Slot Right (m)")] [Increment(1f, 5f, 25f)] [ShowInInspectorWhen("ShowFormation", ShowWhen.True)]
    public float FmRightM { get => P.FmRightM; set => Edit((ref ApPanel p) => p.FmRightM = value); }

    [Slider(null)] [Range<float>(-200f, 200f, RangeMode.InIn)] [Label("Slot Up (m)")] [Increment(1f, 5f, 25f)] [ShowInInspectorWhen("ShowFormation", ShowWhen.True)]
    public float FmUpM { get => P.FmUpM; set => Edit((ref ApPanel p) => p.FmUpM = value); }

    [Slider(null)] [Range<float>(-500f, 1000f, RangeMode.InIn)] [Label("Slot Back (m)")] [Increment(1f, 5f, 25f)] [ShowInInspectorWhen("ShowFormation", ShowWhen.True)]
    public float FmBackM { get => P.FmBackM; set => Edit((ref ApPanel p) => p.FmBackM = value); }

    /// <summary>The most speed over the leader's a gap is closed at.</summary>
    [Slider(null)] [Range<float>(0f, 200f, RangeMode.InIn)] [Label("Catch-up Speed (m/s)")] [Increment(1f, 5f, 25f)] [ShowInInspectorWhen("ShowFormation", ShowWhen.True)]
    public float FmCatchUpMs { get => P.FmCatchUpMs; set => Edit((ref ApPanel p) => p.FmCatchUpMs = value); }

    // ── Attack: the facing (a desire) ──
    [Label("Facing")] [ShowInInspectorWhen("ShowFacing", ShowWhen.True)]
    public ApFacing Facing { get => P.Facing; set => Edit((ref ApPanel p) => p.Facing = value); }

    [Label(" ")] [Multiline] [ReadOnlyInInspector] [ShowInInspectorWhen("ShowFacing", ShowWhen.True)]
    public string FacingHint => ApPanelLayout.FacingHint(P.Facing);

    [Slider(null)] [Range<float>(0f, 1f, RangeMode.InIn)] [Label("Aim priority")] [Increment(0.05f, 0.1f, 0.25f)] [ShowInInspectorWhen("ShowAimPriority", ShowWhen.True)]
    public float AimPriority { get => P.AimPriority; set => Edit((ref ApPanel p) => p.AimPriority = value); }

    /// <summary>0: the grid's own fixed guns' projectile speed.</summary>
    [Slider(null)] [Range<float>(0f, 5000f, RangeMode.InIn)] [Label("Muzzle Speed (m/s)")] [Increment(10f, 50f, 250f)] [ShowInInspectorWhen("ShowAim", ShowWhen.True)]
    public float MuzzleSpeed { get => P.MuzzleSpeed; set => Edit((ref ApPanel p) => p.MuzzleSpeed = value); }

    [Slider(null)] [Range<float>(ApPanel.ConeMin, 20f, RangeMode.InIn)] [Label("Aim Cone (deg)")] [Increment(0.1f, 0.5f, 2f)] [ShowInInspectorWhen("ShowAim", ShowWhen.True)]
    public float ConeDeg { get => P.ConeDeg; set => Edit((ref ApPanel p) => p.ConeDeg = value); }

    // ── the altitude band (every attack movement's height keeps it; the dogfight slot reads it too) ──
    [Slider(null)] [Range<float>(0f, ApPanel.DeckMax, RangeMode.InIn)] [Label("Hard Deck (m over ground)")] [Increment(5f, 25f, 100f)] [ShowInInspectorWhen("ShowBand", ShowWhen.True)]
    public float DeckM { get => P.DeckM; set => Edit((ref ApPanel p) => p.DeckM = value); }

    [Slider(null)] [Range<float>(0f, 20000f, RangeMode.InIn)] [Label("Combat Ceiling (m)")] [Increment(10f, 100f, 1000f)] [ShowInInspectorWhen("ShowBand", ShowWhen.True)]
    public float CeilingM { get => P.CeilingM; set => Edit((ref ApPanel p) => p.CeilingM = value); }

    /// <summary>ENEMY: this ship becomes the game's hostile NPC's - its turrets and its Attack fight players. Off: back to its owner.
    /// Who may: the world's Autopilot.EnemyRule; an enemy ship's panel: creative or an admin only.</summary>
    [Label("Enemy (hostile NPC)")] [ShowInInspectorWhen("ShowEnemy", ShowWhen.True)]
    public bool Enemy { get => _ap != null && _ap.GridIsEnemy; set { _ap?.AskEnemy(value); Changed(); } }

    [Label("Enemy Status")] [ReadOnlyInInspector] [ShowInInspectorWhen("ShowEnemy", ShowWhen.True)]
    public string EnemyText => _ap?.EnemyText ?? "";

    // ── the route (Follow route; Attack's patrol while there is no target) ──
    /// <summary>GPS marker names or coordinates ("x y z"), separated by ';' or new lines.</summary>
    [Label("Route (GPS names)")] [ShowInInspectorWhen("ShowRoute", ShowWhen.True)]
    public string Route { get => P.Route; set => Edit((ref ApPanel p) => p.Route = value ?? ""); }

    [Label("Loop Route")] [ShowInInspectorWhen("ShowRoute", ShowWhen.True)]
    public bool RouteLoop { get => P.RouteLoop; set => Edit((ref ApPanel p) => p.RouteLoop = value); }

    [Slider(null)] [Range<float>(ApPanel.LoiterMin, 5000f, RangeMode.InIn)] [Label("Loiter Radius (m)")] [Increment(10f, 100f, 500f)] [ShowInInspectorWhen("ShowLoiter", ShowWhen.True)]
    public float LoiterM { get => P.LoiterM; set => Edit((ref ApPanel p) => p.LoiterM = value); }

    // ── the holds ──
    [Label("Altitude Hold")] [ShowInInspectorWhen("ShowHolds", ShowWhen.True)]
    public bool AltOn { get => P.AltOn; set => Edit((ref ApPanel p) => p.AltOn = value); }

    [Slider(null)] [Range<float>(0f, 20000f, RangeMode.InIn)] [Label("Altitude (m)")] [Increment(10f, 100f, 1000f)] [ShowInInspectorWhen("ShowAlt", ShowWhen.True)]
    public float AltM { get => P.AltM; set => Edit((ref ApPanel p) => p.AltM = value); }

    /// <summary>The altitude's reference (the same setting as the hover's Height Over Ground): over the ground under the ship, or level.</summary>
    [Label("Altitude Over Ground")] [ShowInInspectorWhen("ShowAltRef", ShowWhen.True)]
    public bool AltOverGround { get => OverGround; set => OverGround = value; }

    [Slider(null)] [Range<float>(ApPanel.VsMin, 100f, RangeMode.InIn)] [Label("Climb Rate (m/s)")] [Increment(0.5f, 5f, 20f)] [ShowInInspectorWhen("ShowAlt", ShowWhen.True)]
    public float VsMs { get => P.VsMs; set => Edit((ref ApPanel p) => p.VsMs = value); }

    [Label("Speed Hold")] [ShowInInspectorWhen("ShowHolds", ShowWhen.True)]
    public bool SpeedOn { get => P.SpeedOn; set => Edit((ref ApPanel p) => p.SpeedOn = value); }

    /// <summary>Through the air (a plane); over the ground, and the most it chases at (a helicopter, a thruster ship). 0: as engaged.</summary>
    [Slider(null)] [Range<float>(0f, 1000f, RangeMode.InIn)] [Label("Speed (m/s)")] [Increment(1f, 10f, 50f)] [ShowInInspectorWhen("ShowSpeed", ShowWhen.True)]
    public float SpeedMs { get => P.SpeedMs; set => Edit((ref ApPanel p) => p.SpeedMs = value); }

    [Slider(null)] [Range<float>(ApPanel.BankMin, ApPanel.BankMax, RangeMode.InIn)] [Label("Bank Limit (deg)")] [Increment(1f, 5f, 15f)] [ShowInInspectorWhen("ShowPilot", ShowWhen.True)]
    public float BankDeg { get => P.BankDeg; set => Edit((ref ApPanel p) => p.BankDeg = value); }

    [Slider(null)] [Range<float>(0f, 1000f, RangeMode.InIn)] [Label("Terrain Minimum (m)")] [Increment(5f, 25f, 100f)] [ShowInInspectorWhen("ShowPilot", ShowWhen.True)]
    public float MinAglM { get => P.MinAglM; set => Edit((ref ApPanel p) => p.MinAglM = value); }

    [Label("Keep flying when I leave the seat")]
    public bool Unattended { get => P.Unattended; set => _ap?.SetUnattended(value); }

    // ── the safety limits ──
    [Label("Safety limits")]
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
    /// would come too near (every mode; through the same safety limits).</summary>
    [Label("Collision Avoidance")]
    public bool AvoidOn { get => P.AvoidOn; set => Edit((ref ApPanel p) => p.AvoidOn = value); }

    /// <summary>How far ahead it looks for a closest approach (s).</summary>
    [Slider(null)] [Range<float>(ApPanel.AvoidSMin, ApPanel.AvoidSMax, RangeMode.InIn)] [Label("Avoidance Look-Ahead (s)")] [Increment(0.5f, 1f, 2f)] [ShowInInspectorWhen("ShowAvoidS", ShowWhen.True)]
    public float AvoidS { get => P.AvoidS; set => Edit((ref ApPanel p) => p.AvoidS = value); }

    // ── THE TOOLBAR'S ACTIONS (never on the panel: ShowNever). Their method names are what saved toolbars hold - kept from the old
    //    panel's buttons (HoverHold .. AutopilotOff, the Toggle* ones); the labels are the new names. ──
    [Label("Hover")] [Button(null)] [ShowInInspectorWhen("ShowNever", ShowWhen.True)]
    public void HoverHold() { _ap?.SetChoice(ApModeChoice.Hover); Changed(); }

    [Label("Hold height")] [Button(null)] [ShowInInspectorWhen("ShowNever", ShowWhen.True)]
    public void HeightHold() { _ap?.SetChoice(ApModeChoice.HoldHeight); Changed(); }

    [Label("Cruise")] [Button(null)] [ShowInInspectorWhen("ShowNever", ShowWhen.True)]
    public void FlightHold() { _ap?.SetChoice(ApModeChoice.Cruise); Changed(); }

    [Label("Follow route")] [Button(null)] [ShowInInspectorWhen("ShowNever", ShowWhen.True)]
    public void Navigate() { _ap?.SetChoice(ApModeChoice.FollowRoute); Changed(); }

    [Label("Return home")] [Button(null)] [ShowInInspectorWhen("ShowNever", ShowWhen.True)]
    public void ReturnHome() { _ap?.SetChoice(ApModeChoice.ReturnHome); Changed(); }

    [Label("Follow ship")] [Button(null)] [ShowInInspectorWhen("ShowNever", ShowWhen.True)]
    public void FollowShip() { _ap?.SetChoice(ApModeChoice.FollowShip); Changed(); }

    [Label("Attack")] [Button(null)] [ShowInInspectorWhen("ShowNever", ShowWhen.True)]
    public void Engage() { _ap?.SetChoice(ApModeChoice.Attack); Changed(); }

    /// <summary>Level off / recover: cruise, every hold caught from the flight now.</summary>
    [Label("Level Off")] [Button(null)] [ShowInInspectorWhen("ShowNever", ShowWhen.True)]
    public void LevelOff() { _ap?.LevelOff(); Changed(); }

    /// <summary>Back to where a mode was first engaged, a loiter there; again: what it flew before resumed.</summary>
    [Label("Toggle return home")] [Button(null)] [ShowInInspectorWhen("ShowNever", ShowWhen.True)]
    public void Return() { _ap?.ToggleReturn(); Changed(); }

    [Label("Autopilot Off")] [Button(null)] [ShowInInspectorWhen("ShowNever", ShowWhen.True)]
    public void AutopilotOff() { _ap?.SetChoice(ApModeChoice.Off); Changed(); }

    [Label("Toggle hover")] [Button(null)] [ShowInInspectorWhen("ShowNever", ShowWhen.True)]
    public void ToggleHoverHold() { _ap?.ToggleChoice(ApModeChoice.Hover); Changed(); }

    [Label("Toggle hold height")] [Button(null)] [ShowInInspectorWhen("ShowNever", ShowWhen.True)]
    public void ToggleHeightHold() { _ap?.ToggleChoice(ApModeChoice.HoldHeight); Changed(); }

    [Label("Toggle cruise")] [Button(null)] [ShowInInspectorWhen("ShowNever", ShowWhen.True)]
    public void ToggleFlightHold() { _ap?.ToggleChoice(ApModeChoice.Cruise); Changed(); }

    [Label("Toggle follow route")] [Button(null)] [ShowInInspectorWhen("ShowNever", ShowWhen.True)]
    public void ToggleNavigate() { _ap?.ToggleChoice(ApModeChoice.FollowRoute); Changed(); }

    [Label("Toggle follow ship")] [Button(null)] [ShowInInspectorWhen("ShowNever", ShowWhen.True)]
    public void ToggleFollowShip() { _ap?.ToggleChoice(ApModeChoice.FollowShip); Changed(); }

    [Label("Toggle attack")] [Button(null)] [ShowInInspectorWhen("ShowNever", ShowWhen.True)]
    public void ToggleEngage() { _ap?.ToggleChoice(ApModeChoice.Attack); Changed(); }

    static readonly string[] _notify =
    {
        nameof(Status), nameof(Mode), nameof(ModeHint), nameof(StyleHint), nameof(TargetHint), nameof(MoveHint), nameof(FacingHint), nameof(AttackMove),
        nameof(ShowHover), nameof(ShowPilot), nameof(ShowFlight), nameof(ShowHolds), nameof(ShowRoute), nameof(ShowLoiter), nameof(ShowTarget),
        nameof(ShowEngage), nameof(ShowAttack), nameof(ShowTargetText), nameof(ShowAim), nameof(ShowFacing), nameof(ShowAimPriority),
        nameof(ShowHeading), nameof(ShowAlt), nameof(ShowAltRef), nameof(ShowSpeed), nameof(ShowEnemy), nameof(ShowAvoidS),
        nameof(ShowTactics), nameof(ShowAggression), nameof(ShowDogfight), nameof(ShowRocket), nameof(ShowFormation), nameof(ShowBand), nameof(ShowTargetFilter), nameof(ShowNever),
        nameof(Enemy), nameof(EnemyText),
        nameof(HeightM), nameof(OverGround), nameof(AltOverGround), nameof(HeadingDeg), nameof(AltM), nameof(SpeedMs),
    };
    void Changed() { foreach (var n in _notify) OnPropertyChanged(n); }
}
