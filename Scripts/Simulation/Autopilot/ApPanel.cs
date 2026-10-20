#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>Which height a height hold keeps: over the ground under the ship (AGL), or along the gravity from where it was
/// engaged (a level altitude, steady over rough ground).</summary>
public enum ApHeightRef : byte { Ground, Inertial }

/// <summary>The Autopilot block's mode choice (its panel). Later modes append (the saved number is this).</summary>
public enum ApMode : byte
{
    Off = 0, HoverHold = 1, HeightHold = 2,
    /// <summary>The flight holds (2026-10-19): heading (or wings level), altitude with a climb / descent rate, speed - each on or off.</summary>
    FlightHold = 3,
    /// <summary>GPS navigation: the route (once, then a loiter at its end; or round and round), the terrain kept under it.</summary>
    Navigate = 4,
    /// <summary>The target's behaviours (From the Depths' attack modes): patrol / loiter until there is a target, then engage it.</summary>
    Engage = 5,
}

/// <summary>How the ship flies (auto-detected from its thrust and lift; the panel may set it): bank-to-turn on its wings, lean the
/// lift axis toward the motion (a helicopter's tilt movement), or translate on thrusters in any direction, the nose free.</summary>
public enum ApStyle : byte { Auto = 0, Airplane = 1, Helicopter = 2, SixAxis = 3 }

/// <summary>The behaviour relative to the target (From the Depths' set; Auto: the arbiter picks the best for the ship and the
/// situation). Appended only (the saved number is this).</summary>
public enum ApBehaviour : byte
{
    Auto = 0, AttackRun = 1, AttackRunUTurn = 2, Broadside = 3, Circle = 4, HoverAbove = 5, HoverBelow = 6, PointAt = 7, Standoff = 8,
    PursuitLead = 9, PursuitPure = 10, PursuitLag = 11,
    /// <summary>(internal: the arbiter's energy recovery - fly away fast, then turn back in)</summary>
    Extend = 12,
    /// <summary>(2026-10-19) THE DOGFIGHT SLOT (ApDogfight.cs - the author's): air combat against another aircraft. Its decision
    /// logic is not written yet: chosen, it refuses ("not written yet"); Auto never picks it until ApDogfight.Implemented.</summary>
    Dogfight = 13,
    /// <summary>(2026-10-19) THE ROCKET-GUIDANCE SLOT (ApRocketGuidance.cs - the author's): an unpiloted rocket grid steered onto
    /// the target. Not written yet: as Dogfight.</summary>
    RocketGuidance = 14,
    /// <summary>(2026-10-19) FORMATION: the target (the leader - any grid the target source picks) followed in a slot right / up /
    /// back in its own frame (from its observed motion), its speed and heading matched, catch-up and overshoot handled.</summary>
    Formation = 15,
}

/// <summary>The dogfight slot's preferred fight (a value the author's logic reads: ApDogfightParams.Fight). Appended only.</summary>
public enum ApFightPref : byte { Auto = 0, Angles = 1, Energy = 2 }

/// <summary>The rocket-guidance slot's law (a value the author's logic switches on: ApRocketParams.Law). Appended only.</summary>
public enum ApGuidanceLaw : byte { ProNav = 0, WhipNav = 1, HybridNav = 2, QuadraticIntercept = 3, AugmentedPN = 4, Pursuit = 5 }

/// <summary>Where the target comes from: the grid's own targeting (what the pilot locked; else the nearest the turrets see - vanilla
/// targeting), a GPS marker or coordinates, a grid by name, or another mod (AeroApi).</summary>
public enum ApTargetSrc : byte { Targeting = 0, Gps = 1, Grid = 2, Api = 3 }

/// <summary>(2026-10-20) FACING - a desire, not a movement (the user: "the facing is like a desire and it's gotta fly how it's gotta
/// fly; you compromise a little to put the guns on target"): which face of the ship the attack wants on the target, blended into
/// the movement's attitude by ApPanel.AimPriority within each style's caps (ApPilot.Aim). Movement always wins where they conflict;
/// the protection still clips. Replaces the Point At / Broadside behaviours and the Aim Nose at Target switch. Appended only.</summary>
public enum ApFacing : byte
{
    /// <summary>The way it flies (the movement's own attitude; no aim).</summary>
    Travel = 0,
    /// <summary>The nose - the fixed guns' axis - on the target's lead point.</summary>
    Nose = 1,
    /// <summary>The left side at the target (turrets on that side: a broadside); a circle is flown the way that keeps it there.</summary>
    SideLeft = 2,
    /// <summary>The right side at the target.</summary>
    SideRight = 3,
}

/// <summary>(2026-10-20) THE PANEL'S MODE CHOICE - one dropdown (ApPanel.ChoiceOf / Choose map it onto the saved Mode, ReturnHome and
/// Behaviour: Follow ship is Engage with the formation, Return home is Navigate with the return asked). Never saved itself.</summary>
public enum ApModeChoice : byte { Off = 0, Hover = 1, HoldHeight = 2, Cruise = 3, FollowRoute = 4, ReturnHome = 5, FollowShip = 6, Attack = 7 }

/// <summary>(2026-10-20) THE PANEL'S ATTACK MOVEMENT - the behaviours a player picks (ApPanel.MoveOf / BhOf map them onto ApBehaviour,
/// the saved number): no Point At / Broadside (now Keep distance / Circle it with a Facing), no Extend (the arbiter's), no Formation
/// (its own mode, Follow ship).</summary>
public enum ApMove : byte
{
    Auto = 0, StrafingRun = 1, TurnBackRun = 2, Circle = 3, KeepDistance = 4, HoverOver = 5, HoverUnder = 6,
    ChaseLead = 7, ChaseDirect = 8, ChaseTrailing = 9, Dogfight = 10, Rocket = 11,
}

/// <summary>Which targets the block engages, by the game's own relation between the owners (ApRelations: the turrets' rule,
/// TargetingGridComponent.IsEligibleTarget). Every source is filtered (a GPS point or an API point has no owner: always taken).
/// Appended only (the saved number is this).</summary>
public enum ApTargetFilter : byte
{
    /// <summary>What the game's turrets would shoot at (IRelations: Hostile).</summary>
    Hostile = 0,
    /// <summary>... and neutral: not hostile, not yours, not your faction's (another player, a neutral NPC).</summary>
    HostileNeutral = 1,
    /// <summary>Anything but this ship itself (a named grid may be your own).</summary>
    Everything = 2,
}

/// <summary>
/// THE AUTOPILOT BLOCK'S PANEL (engine-free): what the player sets on the block - the mode, the flight style, the holds, the route,
/// the behaviour and its tactical numbers, the target source, the aim, the protection's limits, what it does unattended - its
/// limits (ApLimits.Of) and goals (ApModes / ApPilot), saved with the block (AeroBlockSaveForm.Autopilot). Engine-free and
/// dependency-light: Tests/BlockSaveTests compiles it with ApConfig.cs. Only tactical numbers - never a turning circle, a gain or
/// a top speed: those come from the ship's physics (FlightPerf, the attitude law's reach).
/// </summary>
public struct ApPanel
{
    public ApMode Mode;
    /// <summary>The height held (m): over the ground, or over the ground where it was engaged (HeightRef). Engaging catches the
    /// height the ship has (written back here); a new number flies it there.</summary>
    public float HeightM;
    public ApHeightRef HeightRef;
    public float HoverSpeed;
    public bool Protection;
    public float GLimit, AoaMarginDeg, FloorM;
    public bool LookAhead;
    /// <summary>Keep the mode with nobody seated (within the world's Autopilot.Unattended).</summary>
    public bool Unattended;

    // ── 2026-10-19: flight in the air ──
    public ApStyle Style;
    /// <summary>The flight holds: heading (deg, 0 north, clockwise) on / off (off: wings level), altitude (m, in HeightRef) on / off,
    /// the climb / descent rate to a new altitude (m/s), the speed (m/s through the air; a helicopter's: over the ground) on / off,
    /// the most bank (deg).</summary>
    public float HeadingDeg; public bool HeadingOn;
    public float AltM; public bool AltOn; public float VsMs;
    public float SpeedMs; public bool SpeedOn;
    public float BankDeg;
    /// <summary>The route: GPS marker names or coordinates, ';' / ',' / new line between (ApRoute.Parse); round and round or once
    /// (then a loiter at its end); the loiter's radius (m); the lowest it flies over the terrain along the path (m).</summary>
    public string Route; public bool RouteLoop; public float LoiterM; public float MinAglM;
    /// <summary>The behaviour and its tactical numbers: the main distance (m: the attack run's begin, the standoff, the orbit's
    /// radius, the broadside range, the pursuit's gun range), the attack run's abort distance (m) and its re-run time (s), the
    /// broadside angle (deg: 0 nose on, 90 circling), the height over (under) the target (m), the aggressiveness (0..1), jinking.</summary>
    public ApBehaviour Behaviour; public float RangeM, AbortM, RerunS, AngleDeg, AltOffsetM, Aggression; public bool Evasive;
    /// <summary>The target's source, and its text (a GPS name / coordinates, a grid's name).</summary>
    public ApTargetSrc TargetSrc; public string TargetText;
    /// <summary>Nose on the target's lead point (fixed forward guns): on / off, the muzzle speed (m/s; 0: the grid's own guns'), the
    /// "in cone" angle (deg). (2026-10-20: AimOn is Facing == Nose - kept in step by Clamped, saved for older readers and the slots.)</summary>
    public bool AimOn; public float MuzzleSpeed, ConeDeg;
    /// <summary>(2026-10-20) The facing (a desire), how much the flight compromises for it (0: the path only .. 1: as much as the style's
    /// caps allow), and the attack movement kept while Follow ship holds Behaviour (Formation) - Attack picks it up again.</summary>
    public ApFacing Facing; public float AimPriority; public ApBehaviour AttackBh;
    /// <summary>Where a mode was first engaged (the return point; world), and the return asked (Navigate flies there and loiters).</summary>
    public bool HasHome; public double HomeX, HomeY, HomeZ; public bool ReturnHome;
    /// <summary>Which targets it engages (every source).</summary>
    public ApTargetFilter TargetFilter;
    /// <summary>The ENEMY toggle (2026-10-19): this block made its grid a hostile NPC's (the game's own NPC owner - its turrets and
    /// Engage fight players); the grid's owner before (an identity's number; 0 none known), given back when it is switched off.</summary>
    public bool Enemy; public long EnemyPrevOwner;
    /// <summary>Collision avoidance (ApAvoid) on every mode, and its look-ahead (s: the closest approach along the velocity now).</summary>
    public bool AvoidOn; public float AvoidS;

    // ── 2026-10-19: the combat slots' parameters (ApDogfightParams / ApRocketParams carry them to the author's logic; nothing in
    //    the core acts on them but the altitude band - DeckM / CeilingM - which every Engage behaviour's height keeps) ──
    /// <summary>Dogfight: the preferred fight, head-on passes allowed, the hard deck (m over the ground; 0 off) and the combat
    /// ceiling (m in the pilot's altimeter; 0 off), bingo at this fuel (%) / this much of the ship's integrity lost (%; 0 off),
    /// how far it respects the enemy's guns (0..1), its skill (0..1). (Its aggressiveness is the panel's Aggression.)</summary>
    public ApFightPref DfFight; public bool DfHeadOn; public float DeckM, CeilingM, DfBingoFuelPct, DfBingoDamagePct, DfRespectGuns, DfSkill;
    /// <summary>Rocket guidance: the law, the navigation constant, the target-acceleration gain, the most lateral g (also clipped by
    /// the protection), gravity compensation, the arm distance (m), the let-go / self-destruct timeout (s; 0 off).</summary>
    public ApGuidanceLaw RgLaw; public float RgNavConst, RgTargetAccelGain, RgMaxLatG; public bool RgGravityComp; public float RgArmM, RgTimeoutS;
    /// <summary>Formation: the slot right / up / back of the leader in its own frame (m), the most speed over the leader's it
    /// closes a gap at (m/s).</summary>
    public float FmRightM, FmUpM, FmBackM, FmCatchUpMs;
    /// <summary>The slots' own saved state (ApDogfightState.Save / ApRocketState.Save - "" none; written back by the link).</summary>
    public string DfSaved, RgSaved;

    public const float HeightMax = 20000f, HoverSpeedMin = 1f, HoverSpeedMax = 50f, GLimitMin = 1.5f, GLimitMax = 15f,
        AoaMarginMin = 0f, AoaMarginMax = 15f, FloorMin = 0f, FloorMax = 500f;
    public const float AltMax = 50000f, VsMin = 0.5f, VsMax = 200f, SpeedMax = 3000f, BankMin = 5f, BankMax = 80f, LoiterMin = 10f, LoiterMax = 20000f,
        MinAglMax = 3000f, RangeMin = 5f, RangeMax = 50000f, AbortMin = 5f, AbortMax = 20000f, RerunMin = 5f, RerunMax = 600f, AltOffsetMax = 10000f,
        MuzzleMax = 20000f, ConeMin = 0.1f, ConeMax = 45f;
    public const int TextMax = 2000;
    public const float AvoidSMin = 1f, AvoidSMax = 15f;
    public const float DeckMax = 5000f, CeilingMax = 50000f, NavConstMin = 1f, NavConstMax = 8f, AccelGainMax = 3f, LatGMin = 1f, LatGMax = 100f,
        ArmMax = 20000f, TimeoutMax = 3600f, SlotMax = 2000f, CatchUpMax = 500f;

    /// <summary>A new block's panel: off, the static defaults (Autopilot.*).</summary>
    public static ApPanel Defaults => new ApPanel
    {
        Mode = ApMode.Off, HeightM = 0f, HeightRef = Autopilot.HoldAboveGround ? ApHeightRef.Ground : ApHeightRef.Inertial,
        HoverSpeed = Autopilot.HoverSpeed, Protection = Autopilot.Protection, GLimit = Autopilot.GLimit, AoaMarginDeg = Autopilot.AoaMarginDeg,
        FloorM = Autopilot.FloorM, LookAhead = Autopilot.LookAhead, Unattended = true,
        Style = ApStyle.Auto, HeadingDeg = 0f, HeadingOn = true, AltM = 0f, AltOn = true, VsMs = 10f, SpeedMs = 0f, SpeedOn = true, BankDeg = 45f,
        Route = "", RouteLoop = false, LoiterM = 600f, MinAglM = 60f,
        Behaviour = ApBehaviour.Auto, RangeM = 800f, AbortM = 250f, RerunS = 40f, AngleDeg = 90f, AltOffsetM = 100f, Aggression = 0.5f, Evasive = false,
        TargetSrc = ApTargetSrc.Targeting, TargetText = "", AimOn = false, MuzzleSpeed = 0f, ConeDeg = 3f,
        Facing = ApFacing.Travel, AimPriority = 0.5f, AttackBh = ApBehaviour.Auto,
        AvoidOn = true, AvoidS = 4f,
        DfFight = ApFightPref.Auto, DfHeadOn = false, DeckM = 0f, CeilingM = 0f, DfBingoFuelPct = 10f, DfBingoDamagePct = 40f, DfRespectGuns = 0.5f, DfSkill = 0.7f,
        RgLaw = ApGuidanceLaw.ProNav, RgNavConst = 3f, RgTargetAccelGain = 1.5f, RgMaxLatG = 20f, RgGravityComp = true, RgArmM = 50f, RgTimeoutS = 60f,
        FmRightM = 30f, FmUpM = 0f, FmBackM = 30f, FmCatchUpMs = 30f, DfSaved = "", RgSaved = "",
    };

    /// <summary>Every number in its range, every choice known (a hand-edited or older save).</summary>
    public ApPanel Clamped()
    {
        var p = this;
        if (p.Mode > ApMode.Engage) p.Mode = ApMode.Off;
        if (p.HeightRef > ApHeightRef.Inertial) p.HeightRef = ApHeightRef.Ground;
        if (p.Style > ApStyle.SixAxis) p.Style = ApStyle.Auto;
        if (p.Behaviour > ApBehaviour.Formation || p.Behaviour == ApBehaviour.Extend) p.Behaviour = ApBehaviour.Auto;   // (Extend: the arbiter's own, never chosen)
        if (p.Facing > ApFacing.SideRight) p.Facing = ApFacing.Travel;
        Migrate(ref p);
        if (p.AttackBh > ApBehaviour.Formation || p.AttackBh == ApBehaviour.Extend || p.AttackBh == ApBehaviour.Formation) p.AttackBh = ApBehaviour.Auto;
        if (p.Behaviour != ApBehaviour.Formation) p.AttackBh = p.Behaviour;   // (the movement chosen; Follow ship keeps the last one)
        p.AimOn = p.Facing == ApFacing.Nose;
        p.AimPriority = float.IsFinite(p.AimPriority) ? Math.Clamp(p.AimPriority, 0f, 1f) : 0.5f;
        if (p.TargetSrc > ApTargetSrc.Api) p.TargetSrc = ApTargetSrc.Targeting;
        if (p.TargetFilter > ApTargetFilter.Everything) p.TargetFilter = ApTargetFilter.Hostile;
        p.HeightM = float.IsFinite(p.HeightM) ? Math.Clamp(p.HeightM, 0f, HeightMax) : 0f;
        p.HoverSpeed = Fin(p.HoverSpeed, Autopilot.HoverSpeed, HoverSpeedMin, HoverSpeedMax);
        p.GLimit = Fin(p.GLimit, Autopilot.GLimit, GLimitMin, GLimitMax);
        p.AoaMarginDeg = Fin(p.AoaMarginDeg, Autopilot.AoaMarginDeg, AoaMarginMin, AoaMarginMax);
        p.FloorM = Fin(p.FloorM, Autopilot.FloorM, FloorMin, FloorMax);
        var d = Defaults;
        p.HeadingDeg = float.IsFinite(p.HeadingDeg) ? ((p.HeadingDeg % 360f) + 360f) % 360f : 0f;
        p.AltM = Fin(p.AltM, 0f, 0f, AltMax); p.VsMs = Fin(p.VsMs, d.VsMs, VsMin, VsMax); p.SpeedMs = Fin(p.SpeedMs, 0f, 0f, SpeedMax);
        p.BankDeg = Fin(p.BankDeg, d.BankDeg, BankMin, BankMax);
        p.LoiterM = Fin(p.LoiterM, d.LoiterM, LoiterMin, LoiterMax); p.MinAglM = Fin(p.MinAglM, d.MinAglM, 0f, MinAglMax);
        p.RangeM = Fin(p.RangeM, d.RangeM, RangeMin, RangeMax); p.AbortM = Fin(p.AbortM, d.AbortM, AbortMin, AbortMax);
        p.RerunS = Fin(p.RerunS, d.RerunS, RerunMin, RerunMax); p.AngleDeg = Fin(p.AngleDeg, d.AngleDeg, 0f, 180f);
        p.AltOffsetM = Fin(p.AltOffsetM, d.AltOffsetM, -AltOffsetMax, AltOffsetMax); p.Aggression = Fin(p.Aggression, d.Aggression, 0f, 1f);
        p.MuzzleSpeed = Fin(p.MuzzleSpeed, 0f, 0f, MuzzleMax); p.ConeDeg = Fin(p.ConeDeg, d.ConeDeg, ConeMin, ConeMax);
        p.Route = Text(p.Route); p.TargetText = Text(p.TargetText);
        p.AvoidS = Fin(p.AvoidS, d.AvoidS, AvoidSMin, AvoidSMax);
        if (p.DfFight > ApFightPref.Energy) p.DfFight = ApFightPref.Auto;
        if (p.RgLaw > ApGuidanceLaw.Pursuit) p.RgLaw = ApGuidanceLaw.ProNav;
        p.DeckM = Fin(p.DeckM, 0f, 0f, DeckMax); p.CeilingM = Fin(p.CeilingM, 0f, 0f, CeilingMax);
        p.DfBingoFuelPct = Fin(p.DfBingoFuelPct, d.DfBingoFuelPct, 0f, 100f); p.DfBingoDamagePct = Fin(p.DfBingoDamagePct, d.DfBingoDamagePct, 0f, 100f);
        p.DfRespectGuns = Fin(p.DfRespectGuns, d.DfRespectGuns, 0f, 1f); p.DfSkill = Fin(p.DfSkill, d.DfSkill, 0f, 1f);
        p.RgNavConst = Fin(p.RgNavConst, d.RgNavConst, NavConstMin, NavConstMax); p.RgTargetAccelGain = Fin(p.RgTargetAccelGain, d.RgTargetAccelGain, 0f, AccelGainMax);
        p.RgMaxLatG = Fin(p.RgMaxLatG, d.RgMaxLatG, LatGMin, LatGMax); p.RgArmM = Fin(p.RgArmM, d.RgArmM, 0f, ArmMax); p.RgTimeoutS = Fin(p.RgTimeoutS, d.RgTimeoutS, 0f, TimeoutMax);
        p.FmRightM = Fin(p.FmRightM, d.FmRightM, -SlotMax, SlotMax); p.FmUpM = Fin(p.FmUpM, d.FmUpM, -SlotMax, SlotMax); p.FmBackM = Fin(p.FmBackM, d.FmBackM, -SlotMax, SlotMax);
        p.FmCatchUpMs = Fin(p.FmCatchUpMs, d.FmCatchUpMs, 0f, CatchUpMax);
        p.DfSaved = Text(p.DfSaved); p.RgSaved = Text(p.RgSaved);
        if (!(double.IsFinite(p.HomeX) && double.IsFinite(p.HomeY) && double.IsFinite(p.HomeZ))) { p.HasHome = false; p.HomeX = p.HomeY = p.HomeZ = 0; }
        return p;
    }
    static float Fin(float v, float def, float lo, float hi) => float.IsFinite(v) ? Math.Clamp(v, lo, hi) : def;
    static string Text(string s) => s == null ? "" : s.Length > TextMax ? s.Substring(0, TextMax) : s;

    /// <summary>(2026-10-20) The movements that were also a facing, as the movement and the facing now: Point At - Keep distance with the
    /// nose at the target; Broadside - Circle it with a side at the target (its angle: under 25 deg the nose - the old broadside aimed
    /// the nose there -, to 90 the right side, past 90 the left; the old one took the side it was already turning). Idempotent.</summary>
    public static void Migrate(ref ApPanel p)
    {
        if (p.Behaviour == ApBehaviour.PointAt) { p.Behaviour = ApBehaviour.Standoff; p.Facing = ApFacing.Nose; }
        else if (p.Behaviour == ApBehaviour.Broadside)
        {
            p.Behaviour = ApBehaviour.Circle;
            float a = float.IsFinite(p.AngleDeg) ? p.AngleDeg : 90f;
            p.Facing = a < 25f ? ApFacing.Nose : a <= 90f ? ApFacing.SideRight : ApFacing.SideLeft;
        }
        if (p.AttackBh == ApBehaviour.PointAt) p.AttackBh = ApBehaviour.Standoff;
        else if (p.AttackBh == ApBehaviour.Broadside) p.AttackBh = ApBehaviour.Circle;
    }

    /// <summary>The panel's mode choice of a saved panel: a return asked (Navigate / Engage) - Return home; Engage with the formation -
    /// Follow ship; Engage - Attack.</summary>
    public static ApModeChoice ChoiceOf(in ApPanel p) => p.Mode switch
    {
        ApMode.HoverHold => ApModeChoice.Hover,
        ApMode.HeightHold => ApModeChoice.HoldHeight,
        ApMode.FlightHold => ApModeChoice.Cruise,
        ApMode.Navigate => p.ReturnHome ? ApModeChoice.ReturnHome : ApModeChoice.FollowRoute,
        ApMode.Engage => p.ReturnHome ? ApModeChoice.ReturnHome : p.Behaviour == ApBehaviour.Formation ? ApModeChoice.FollowShip : ApModeChoice.Attack,
        _ => ApModeChoice.Off,
    };

    /// <summary>A mode choice onto the saved panel: the mode, the return (only Return home asks it), the formation (Follow ship; the
    /// attack movement kept in AttackBh, given back by Attack).</summary>
    public static void Choose(ref ApPanel p, ApModeChoice c)
    {
        p.ReturnHome = c == ApModeChoice.ReturnHome;
        switch (c)
        {
            case ApModeChoice.Hover: p.Mode = ApMode.HoverHold; break;
            case ApModeChoice.HoldHeight: p.Mode = ApMode.HeightHold; break;
            case ApModeChoice.Cruise: p.Mode = ApMode.FlightHold; break;
            case ApModeChoice.FollowRoute: case ApModeChoice.ReturnHome: p.Mode = ApMode.Navigate; break;
            case ApModeChoice.FollowShip:
                if (p.Behaviour != ApBehaviour.Formation) p.AttackBh = p.Behaviour;
                p.Mode = ApMode.Engage; p.Behaviour = ApBehaviour.Formation; break;
            case ApModeChoice.Attack:
                if (p.Behaviour == ApBehaviour.Formation) p.Behaviour = p.AttackBh == ApBehaviour.Formation ? ApBehaviour.Auto : p.AttackBh;
                p.Mode = ApMode.Engage; break;
            default: p.Mode = ApMode.Off; break;
        }
    }

    /// <summary>The panel's attack movement of a behaviour (the old Point At / Broadside as their movements now).</summary>
    public static ApMove MoveOf(ApBehaviour b) => b switch
    {
        ApBehaviour.AttackRun => ApMove.StrafingRun, ApBehaviour.AttackRunUTurn => ApMove.TurnBackRun, ApBehaviour.Circle => ApMove.Circle,
        ApBehaviour.Broadside => ApMove.Circle, ApBehaviour.Standoff => ApMove.KeepDistance, ApBehaviour.PointAt => ApMove.KeepDistance,
        ApBehaviour.HoverAbove => ApMove.HoverOver, ApBehaviour.HoverBelow => ApMove.HoverUnder, ApBehaviour.PursuitLead => ApMove.ChaseLead,
        ApBehaviour.PursuitPure => ApMove.ChaseDirect, ApBehaviour.PursuitLag => ApMove.ChaseTrailing, ApBehaviour.Dogfight => ApMove.Dogfight,
        ApBehaviour.RocketGuidance => ApMove.Rocket, _ => ApMove.Auto,
    };

    /// <summary>The behaviour (the saved number) of a panel movement.</summary>
    public static ApBehaviour BhOf(ApMove m) => m switch
    {
        ApMove.StrafingRun => ApBehaviour.AttackRun, ApMove.TurnBackRun => ApBehaviour.AttackRunUTurn, ApMove.Circle => ApBehaviour.Circle,
        ApMove.KeepDistance => ApBehaviour.Standoff, ApMove.HoverOver => ApBehaviour.HoverAbove, ApMove.HoverUnder => ApBehaviour.HoverBelow,
        ApMove.ChaseLead => ApBehaviour.PursuitLead, ApMove.ChaseDirect => ApBehaviour.PursuitPure, ApMove.ChaseTrailing => ApBehaviour.PursuitLag,
        ApMove.Dogfight => ApBehaviour.Dogfight, ApMove.Rocket => ApBehaviour.RocketGuidance, _ => ApBehaviour.Auto,
    };

    /// <summary>The modes the flight pilot flies (ApPilot, an external goal source over the flight director's core).</summary>
    public static bool IsPilotMode(ApMode m) => m == ApMode.FlightHold || m == ApMode.Navigate || m == ApMode.Engage;
}

/// <summary>
/// The limits and choices one autopilot flies with (ApSense.Lim): the controlling block's panel within the world's caps. A struct
/// of values - no allocation, read on the simulation thread each tick.
/// </summary>
public struct ApLimits
{
    public bool Enabled, Unattended, Protection, LookAhead, HoldAboveGround, Avoid;
    /// <summary>The collision avoidance's look-ahead (s).</summary>
    public float AvoidS;
    public float MaxLeanDeg, HoverSpeed, ClimbRate, GLimit, AoaMarginDeg, FloorM;

    /// <summary>The static defaults (Autopilot.*) - the offline sim's, and a block with no saved panel yet.</summary>
    public static ApLimits Defaults => new ApLimits
    {
        Enabled = Autopilot.Enabled, Unattended = Autopilot.Unattended, Protection = Autopilot.Protection, LookAhead = Autopilot.LookAhead,
        HoldAboveGround = Autopilot.HoldAboveGround, MaxLeanDeg = Autopilot.MaxLeanDeg, HoverSpeed = Autopilot.HoverSpeed,
        ClimbRate = Autopilot.ClimbRate, GLimit = Autopilot.GLimit, AoaMarginDeg = Autopilot.AoaMarginDeg, FloorM = Autopilot.FloorM,
        Avoid = true, AvoidS = 4f,
    };

    /// <summary>A block's panel within the world's caps: the world's switch and its unattended permission, its lean and climb caps;
    /// the panel's numbers clamped to their ranges (ApPanel).</summary>
    public static ApLimits Of(in ApPanel panel)
    {
        var p = panel.Clamped();   // (a NaN or an out-of-range number from a hand-edited save never reaches the core)
        return new ApLimits
        {
            Enabled = Autopilot.Enabled, Unattended = Autopilot.Unattended && p.Unattended, Protection = p.Protection, LookAhead = p.LookAhead,
            HoldAboveGround = p.HeightRef == ApHeightRef.Ground, MaxLeanDeg = Autopilot.MaxLeanDeg, ClimbRate = Autopilot.ClimbRate,
            HoverSpeed = Math.Clamp(p.HoverSpeed, ApPanel.HoverSpeedMin, ApPanel.HoverSpeedMax),
            GLimit = Math.Clamp(p.GLimit, ApPanel.GLimitMin, ApPanel.GLimitMax),
            AoaMarginDeg = Math.Clamp(p.AoaMarginDeg, ApPanel.AoaMarginMin, ApPanel.AoaMarginMax),
            FloorM = Math.Clamp(p.FloorM, ApPanel.FloorMin, ApPanel.FloorMax),
            Avoid = p.AvoidOn, AvoidS = p.AvoidS,
        };
    }
}
