#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// WHAT THE AUTOPILOT BLOCK'S PANEL SHOWS (engine-free; the terminal's model, Game/AeroAutopilotBlockDetailModel.cs, asks these
/// for its [ShowInInspectorWhen] switches - Tests/AutopilotSim's PanelLayoutPack reads that model's member order and attributes
/// and checks, mode by mode, what a player sees and in which order). 2026-10-20, the redesign the user approved: the STATUS first
/// (its whole text, wrapped), ONE Mode dropdown (Off, Hover, Hold height, Cruise, Follow route, Return home, Follow ship, Attack -
/// ApModeChoice) with a line saying what the chosen mode does, Level Off the one button, then ONLY the chosen mode's fields
/// (the hover's height and speed; Fly as; Follow ship's leader and slot; Attack's target, movement, facing, band; the route; the
/// holds), "keep flying when I leave the seat", and the Safety limits last. Each dropdown has a line under it saying what its
/// choice does (the *Hint texts here; no tooltips - in game a row's tooltip is its label).
/// </summary>
public static class ApPanelLayout
{
    static ApModeChoice C(in ApPanel p) => ApPanel.ChoiceOf(in p);
    static bool Attack(in ApPanel p) => C(in p) == ApModeChoice.Attack;
    static bool Classic(ApBehaviour b) => b != ApBehaviour.Formation && b != ApBehaviour.Dogfight && b != ApBehaviour.RocketGuidance;

    public static bool ShowHover(in ApPanel p) => C(in p) is ApModeChoice.Hover or ApModeChoice.HoldHeight;
    /// <summary>Fly as, the bank limit, the terrain minimum: every mode the flight pilot flies.</summary>
    public static bool ShowPilot(in ApPanel p) => ApPanel.IsPilotMode(p.Mode);
    /// <summary>The heading hold: Cruise.</summary>
    public static bool ShowFlight(in ApPanel p) => C(in p) == ApModeChoice.Cruise;
    /// <summary>The altitude and speed holds' switches: Cruise, Follow route, Return home, Attack (the patrol's) - not Follow ship (the
    /// leader's height and speed).</summary>
    public static bool ShowHolds(in ApPanel p) => ShowPilot(in p) && C(in p) != ApModeChoice.FollowShip;
    /// <summary>The route and its loop: Follow route, Attack (its patrol while there is no target).</summary>
    public static bool ShowRoute(in ApPanel p) => C(in p) is ApModeChoice.FollowRoute or ApModeChoice.Attack;
    /// <summary>The loiter's radius: at the route's end, at home, an attack's patrol.</summary>
    public static bool ShowLoiter(in ApPanel p) => C(in p) is ApModeChoice.FollowRoute or ApModeChoice.ReturnHome or ApModeChoice.Attack;
    /// <summary>The target (the leader of Follow ship, the attack's target).</summary>
    public static bool ShowTarget(in ApPanel p) => C(in p) is ApModeChoice.FollowShip or ApModeChoice.Attack;
    public static bool ShowTargetText(in ApPanel p) => ShowTarget(in p) && (p.TargetSrc == ApTargetSrc.Gps || p.TargetSrc == ApTargetSrc.Grid);
    /// <summary>The target filter: Attack (a leader may be any grid - your own).</summary>
    public static bool ShowTargetFilter(in ApPanel p) => Attack(in p);
    /// <summary>The attack movement and its line.</summary>
    public static bool ShowEngage(in ApPanel p) => Attack(in p);
    /// <summary>Range, height over the target, jinking: the built-in movements (and Auto).</summary>
    public static bool ShowTactics(in ApPanel p) => Attack(in p) && Classic(p.Behaviour);
    /// <summary>The strafing runs' abort distance and re-run time (and Auto, which may pick one).</summary>
    public static bool ShowAttack(in ApPanel p) => Attack(in p) && (p.Behaviour == ApBehaviour.AttackRun || p.Behaviour == ApBehaviour.AttackRunUTurn || p.Behaviour == ApBehaviour.Auto);
    /// <summary>Aggressiveness: the built-in movements and the dogfight.</summary>
    public static bool ShowAggression(in ApPanel p) => Attack(in p) && (Classic(p.Behaviour) || p.Behaviour == ApBehaviour.Dogfight);
    public static bool ShowDogfight(in ApPanel p) => Attack(in p) && p.Behaviour == ApBehaviour.Dogfight;
    public static bool ShowRocket(in ApPanel p) => Attack(in p) && p.Behaviour == ApBehaviour.RocketGuidance;
    public static bool ShowFormation(in ApPanel p) => C(in p) == ApModeChoice.FollowShip;
    /// <summary>The facing and its line: Attack, not a rocket (it has no guns to bear: it is the round).</summary>
    public static bool ShowFacing(in ApPanel p) => Attack(in p) && p.Behaviour != ApBehaviour.RocketGuidance;
    /// <summary>The aim priority: a facing other than the way it flies.</summary>
    public static bool ShowAimPriority(in ApPanel p) => ShowFacing(in p) && p.Facing != ApFacing.Travel;
    /// <summary>The fixed guns' muzzle speed and cone: the nose facing.</summary>
    public static bool ShowAim(in ApPanel p) => ShowFacing(in p) && p.Facing == ApFacing.Nose;
    /// <summary>The hard deck and the combat ceiling: Attack, not a rocket.</summary>
    public static bool ShowBand(in ApPanel p) => Attack(in p) && p.Behaviour != ApBehaviour.RocketGuidance;
    /// <summary>Never: the toolbar's actions (a [Button] method's [HideInInspector] does not hide it on the panel).</summary>
    public const bool ShowNever = false;
    public static bool ShowHeading(in ApPanel p) => ShowFlight(in p) && p.HeadingOn;
    public static bool ShowAlt(in ApPanel p) => ShowHolds(in p) && p.AltOn;
    /// <summary>The altitude's reference (over the ground / level): not Attack's (it flies its own height over the target).</summary>
    public static bool ShowAltRef(in ApPanel p) => ShowAlt(in p) && !Attack(in p);
    public static bool ShowSpeed(in ApPanel p) => ShowHolds(in p) && p.SpeedOn;
    public static bool ShowAvoidS(in ApPanel p) => p.AvoidOn;
    /// <summary>The Enemy switch: in Attack while the world allows it, and always on an enemy ship (so it can be switched back).</summary>
    public static bool ShowEnemy(in ApPanel p, ApEnemyRule rule, bool gridIsEnemy) => (Attack(in p) && rule != ApEnemyRule.Off) || gridIsEnemy;

    // ── the lines under the dropdowns: what the choice does (one line each) ──

    public static string ModeHint(ApModeChoice c) => c switch
    {
        ApModeChoice.Hover => "Holds this spot and height; your keys move it.",
        ApModeChoice.HoldHeight => "Holds the height; you steer.",
        ApModeChoice.Cruise => "Holds a heading, altitude and speed.",
        ApModeChoice.FollowRoute => "Flies your GPS points in order, then circles the last.",
        ApModeChoice.ReturnHome => "Flies back to where you started it and circles there.",
        ApModeChoice.FollowShip => "Flies in formation with another ship.",
        ApModeChoice.Attack => "Fights the target: moves as set, turns the chosen face to it.",
        _ => "The autopilot is off.",
    };

    public static string StyleHint(ApStyle s) => s switch
    {
        ApStyle.Airplane => "Banks to turn on its wings; needs airspeed.",
        ApStyle.Helicopter => "Leans its lift to move; turns by yaw.",
        ApStyle.SixAxis => "Moves on thrusters in any direction; the nose is free.",
        _ => "Picks plane, helicopter or thruster ship from its blocks.",
    };

    public static string MoveHint(ApMove m) => m switch
    {
        ApMove.StrafingRun => "Fly over it, extend, come back.",
        ApMove.TurnBackRun => "Fly over it, turn straight back, again.",
        ApMove.Circle => "Circle it at the range.",
        ApMove.KeepDistance => "Hold the range; back off when it closes.",
        ApMove.HoverOver => "Sit above it.",
        ApMove.HoverUnder => "Sit below it.",
        ApMove.ChaseLead => "Chase it, aiming ahead of it (gun lead).",
        ApMove.ChaseDirect => "Chase it straight at it.",
        ApMove.ChaseTrailing => "Chase it from behind its tail.",
        ApMove.Dogfight => "Air combat logic (the dogfight slot).",
        ApMove.Rocket => "This ship is the rocket: steer into the target.",
        _ => "Picks the movement for the ship and the fight.",
    };

    public static string FacingHint(ApFacing f) => f switch
    {
        ApFacing.Nose => "Fixed guns: the nose on the target's lead, as far as the flight allows.",
        ApFacing.SideLeft => "Turrets on the left: the left side to the target.",
        ApFacing.SideRight => "Turrets on the right: the right side to the target.",
        _ => "Faces where it flies; no aiming.",
    };

    public static string TargetHint(ApTargetSrc t) => t switch
    {
        ApTargetSrc.Gps => "A GPS point by name, or coordinates.",
        ApTargetSrc.Grid => "A ship or station by its name.",
        ApTargetSrc.Api => "Set by another mod.",
        _ => "What your cockpit locked; else what your turrets see.",
    };

    /// <summary>The dropdowns' choice names as the game shows them ("&lt;Type&gt;_&lt;Value&gt;" -> the en-US text: the texts file holds the
    /// same - Tests/AutopilotSim TextsPack compares them).</summary>
    public static readonly System.Collections.Generic.Dictionary<string, string> Names = new()
    {
        ["ApModeChoice_Off"] = "Off", ["ApModeChoice_Hover"] = "Hover", ["ApModeChoice_HoldHeight"] = "Hold height", ["ApModeChoice_Cruise"] = "Cruise",
        ["ApModeChoice_FollowRoute"] = "Follow route", ["ApModeChoice_ReturnHome"] = "Return home", ["ApModeChoice_FollowShip"] = "Follow ship", ["ApModeChoice_Attack"] = "Attack",
        ["ApStyle_Auto"] = "Detect", ["ApStyle_Airplane"] = "Plane", ["ApStyle_Helicopter"] = "Helicopter", ["ApStyle_SixAxis"] = "Thruster ship",
        ["ApMove_Auto"] = "Auto", ["ApMove_StrafingRun"] = "Strafing run", ["ApMove_TurnBackRun"] = "Turn-back run", ["ApMove_Circle"] = "Circle it",
        ["ApMove_KeepDistance"] = "Keep distance", ["ApMove_HoverOver"] = "Hover over", ["ApMove_HoverUnder"] = "Hover under",
        ["ApMove_ChaseLead"] = "Chase - gun lead", ["ApMove_ChaseDirect"] = "Chase - direct", ["ApMove_ChaseTrailing"] = "Chase - trailing",
        ["ApMove_Dogfight"] = "Dogfight", ["ApMove_Rocket"] = "Rocket",
        ["ApFacing_Travel"] = "Direction of travel", ["ApFacing_Nose"] = "Nose at target", ["ApFacing_SideLeft"] = "Side at target - left", ["ApFacing_SideRight"] = "Side at target - right",
        ["ApTargetSrc_Targeting"] = "Locked target", ["ApTargetSrc_Gps"] = "GPS", ["ApTargetSrc_Grid"] = "Ship by name", ["ApTargetSrc_Api"] = "Other mod",
    };

    /// <summary>A choice's name (Names; the value's own name when missing).</summary>
    public static string Name<T>(T v) where T : struct, Enum => Names.TryGetValue(typeof(T).Name + "_" + v, out var n) ? n : v.ToString();

    /// <summary>A switch by its name (the model's property names - the offline layout check evaluates the model's attributes with
    /// it). False for a name it does not know (the check fails on it).</summary>
    public static bool Cond(string name, in ApPanel p, ApEnemyRule rule, bool gridIsEnemy, out bool known)
    {
        known = true;
        switch (name)
        {
            case "ShowHover": return ShowHover(in p);
            case "ShowPilot": return ShowPilot(in p);
            case "ShowFlight": return ShowFlight(in p);
            case "ShowHolds": return ShowHolds(in p);
            case "ShowRoute": return ShowRoute(in p);
            case "ShowLoiter": return ShowLoiter(in p);
            case "ShowTarget": return ShowTarget(in p);
            case "ShowEngage": return ShowEngage(in p);
            case "ShowAttack": return ShowAttack(in p);
            case "ShowTargetText": return ShowTargetText(in p);
            case "ShowAim": return ShowAim(in p);
            case "ShowFacing": return ShowFacing(in p);
            case "ShowAimPriority": return ShowAimPriority(in p);
            case "ShowHeading": return ShowHeading(in p);
            case "ShowAlt": return ShowAlt(in p);
            case "ShowAltRef": return ShowAltRef(in p);
            case "ShowSpeed": return ShowSpeed(in p);
            case "ShowAvoidS": return ShowAvoidS(in p);
            case "ShowEnemy": return ShowEnemy(in p, rule, gridIsEnemy);
            case "ShowTactics": return ShowTactics(in p);
            case "ShowAggression": return ShowAggression(in p);
            case "ShowDogfight": return ShowDogfight(in p);
            case "ShowRocket": return ShowRocket(in p);
            case "ShowFormation": return ShowFormation(in p);
            case "ShowBand": return ShowBand(in p);
            case "ShowTargetFilter": return ShowTargetFilter(in p);
            case "ShowNever": return ShowNever;
        }
        known = false;
        return false;
    }
}
