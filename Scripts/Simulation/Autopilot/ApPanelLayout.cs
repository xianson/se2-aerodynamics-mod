#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// WHAT THE AUTOPILOT BLOCK'S PANEL SHOWS (engine-free; the terminal's model, Game/AeroAutopilotBlockDetailModel.cs, asks these
/// for its [ShowInInspectorWhen] switches - Tests/AutopilotSim's PanelLayoutPack reads that model's member order and attributes
/// and checks, mode by mode, what a player sees and in which order). The panel, top down: the status line, the mode buttons,
/// then ONLY the chosen mode's fields (hover / height: the height, its reference, the hover speed; the pilot modes: the style and
/// holds, the route, the behaviour, the target, the aim), what it does unattended, and the protection's limits last.
/// </summary>
public static class ApPanelLayout
{
    public static bool ShowHover(in ApPanel p) => p.Mode == ApMode.HoverHold || p.Mode == ApMode.HeightHold;
    public static bool ShowPilot(in ApPanel p) => ApPanel.IsPilotMode(p.Mode);
    public static bool ShowFlight(in ApPanel p) => p.Mode == ApMode.FlightHold;
    public static bool ShowRoute(in ApPanel p) => p.Mode == ApMode.Navigate || p.Mode == ApMode.Engage;
    public static bool ShowEngage(in ApPanel p) => p.Mode == ApMode.Engage;
    public static bool ShowAttack(in ApPanel p) => p.Mode == ApMode.Engage && (p.Behaviour == ApBehaviour.AttackRun || p.Behaviour == ApBehaviour.AttackRunUTurn || p.Behaviour == ApBehaviour.Auto);
    public static bool ShowBroadside(in ApPanel p) => p.Mode == ApMode.Engage && p.Behaviour == ApBehaviour.Broadside;
    public static bool ShowTargetText(in ApPanel p) => p.Mode == ApMode.Engage && (p.TargetSrc == ApTargetSrc.Gps || p.TargetSrc == ApTargetSrc.Grid);
    public static bool ShowAim(in ApPanel p) => p.Mode == ApMode.Engage && p.AimOn;
    public static bool ShowHeading(in ApPanel p) => p.Mode == ApMode.FlightHold && p.HeadingOn;
    public static bool ShowAlt(in ApPanel p) => ApPanel.IsPilotMode(p.Mode) && p.AltOn;
    /// <summary>The altitude's reference (over the ground / level): the flight hold's and the route's altitude (Engage flies its
    /// own height over the target - ApPilot ignores the reference there).</summary>
    public static bool ShowAltRef(in ApPanel p) => ShowAlt(in p) && p.Mode != ApMode.Engage;
    public static bool ShowSpeed(in ApPanel p) => ApPanel.IsPilotMode(p.Mode) && p.SpeedOn;
    public static bool ShowAvoidS(in ApPanel p) => p.AvoidOn;
    /// <summary>The Enemy switch: in Engage while the world allows it, and always on an enemy ship (so it can be switched back).</summary>
    public static bool ShowEnemy(in ApPanel p, ApEnemyRule rule, bool gridIsEnemy) => (p.Mode == ApMode.Engage && rule != ApEnemyRule.Off) || gridIsEnemy;

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
            case "ShowRoute": return ShowRoute(in p);
            case "ShowEngage": return ShowEngage(in p);
            case "ShowAttack": return ShowAttack(in p);
            case "ShowBroadside": return ShowBroadside(in p);
            case "ShowTargetText": return ShowTargetText(in p);
            case "ShowAim": return ShowAim(in p);
            case "ShowHeading": return ShowHeading(in p);
            case "ShowAlt": return ShowAlt(in p);
            case "ShowAltRef": return ShowAltRef(in p);
            case "ShowSpeed": return ShowSpeed(in p);
            case "ShowAvoidS": return ShowAvoidS(in p);
            case "ShowEnemy": return ShowEnemy(in p, rule, gridIsEnemy);
        }
        known = false;
        return false;
    }
}
