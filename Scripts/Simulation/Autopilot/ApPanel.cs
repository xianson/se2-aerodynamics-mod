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
}

/// <summary>Where the target comes from: the grid's own targeting (what the pilot locked; else the nearest the turrets see - vanilla
/// targeting), a GPS marker or coordinates, a grid by name, or another mod (AeroApi).</summary>
public enum ApTargetSrc : byte { Targeting = 0, Gps = 1, Grid = 2, Api = 3 }

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
    /// "in cone" angle (deg).</summary>
    public bool AimOn; public float MuzzleSpeed, ConeDeg;
    /// <summary>Where a mode was first engaged (the return point; world), and the return asked (Navigate flies there and loiters).</summary>
    public bool HasHome; public double HomeX, HomeY, HomeZ; public bool ReturnHome;
    /// <summary>Which targets it engages (every source).</summary>
    public ApTargetFilter TargetFilter;
    /// <summary>The ENEMY toggle (2026-10-19): this block made its grid a hostile NPC's (the game's own NPC owner - its turrets and
    /// Engage fight players); the grid's owner before (an identity's number; 0 none known), given back when it is switched off.</summary>
    public bool Enemy; public long EnemyPrevOwner;
    /// <summary>Collision avoidance (ApAvoid) on every mode, and its look-ahead (s: the closest approach along the velocity now).</summary>
    public bool AvoidOn; public float AvoidS;

    public const float HeightMax = 20000f, HoverSpeedMin = 1f, HoverSpeedMax = 50f, GLimitMin = 1.5f, GLimitMax = 15f,
        AoaMarginMin = 0f, AoaMarginMax = 15f, FloorMin = 0f, FloorMax = 500f;
    public const float AltMax = 50000f, VsMin = 0.5f, VsMax = 200f, SpeedMax = 3000f, BankMin = 5f, BankMax = 80f, LoiterMin = 10f, LoiterMax = 20000f,
        MinAglMax = 3000f, RangeMin = 5f, RangeMax = 50000f, AbortMin = 5f, AbortMax = 20000f, RerunMin = 5f, RerunMax = 600f, AltOffsetMax = 10000f,
        MuzzleMax = 20000f, ConeMin = 0.1f, ConeMax = 45f;
    public const int TextMax = 2000;
    public const float AvoidSMin = 1f, AvoidSMax = 15f;

    /// <summary>A new block's panel: off, the static defaults (Autopilot.*).</summary>
    public static ApPanel Defaults => new ApPanel
    {
        Mode = ApMode.Off, HeightM = 0f, HeightRef = Autopilot.HoldAboveGround ? ApHeightRef.Ground : ApHeightRef.Inertial,
        HoverSpeed = Autopilot.HoverSpeed, Protection = Autopilot.Protection, GLimit = Autopilot.GLimit, AoaMarginDeg = Autopilot.AoaMarginDeg,
        FloorM = Autopilot.FloorM, LookAhead = Autopilot.LookAhead, Unattended = true,
        Style = ApStyle.Auto, HeadingDeg = 0f, HeadingOn = true, AltM = 0f, AltOn = true, VsMs = 10f, SpeedMs = 0f, SpeedOn = true, BankDeg = 45f,
        Route = "", RouteLoop = false, LoiterM = 600f, MinAglM = 60f,
        Behaviour = ApBehaviour.Auto, RangeM = 800f, AbortM = 250f, RerunS = 40f, AngleDeg = 90f, AltOffsetM = 100f, Aggression = 0.5f, Evasive = false,
        TargetSrc = ApTargetSrc.Targeting, TargetText = "", AimOn = true, MuzzleSpeed = 0f, ConeDeg = 3f,
        AvoidOn = true, AvoidS = 4f,
    };

    /// <summary>Every number in its range, every choice known (a hand-edited or older save).</summary>
    public ApPanel Clamped()
    {
        var p = this;
        if (p.Mode > ApMode.Engage) p.Mode = ApMode.Off;
        if (p.HeightRef > ApHeightRef.Inertial) p.HeightRef = ApHeightRef.Ground;
        if (p.Style > ApStyle.SixAxis) p.Style = ApStyle.Auto;
        if (p.Behaviour > ApBehaviour.PursuitLag) p.Behaviour = ApBehaviour.Auto;
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
        if (!(double.IsFinite(p.HomeX) && double.IsFinite(p.HomeY) && double.IsFinite(p.HomeZ))) { p.HasHome = false; p.HomeX = p.HomeY = p.HomeZ = 0; }
        return p;
    }
    static float Fin(float v, float def, float lo, float hi) => float.IsFinite(v) ? Math.Clamp(v, lo, hi) : def;
    static string Text(string s) => s == null ? "" : s.Length > TextMax ? s.Substring(0, TextMax) : s;

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
