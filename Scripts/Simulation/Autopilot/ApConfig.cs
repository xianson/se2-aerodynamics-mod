#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// THE AUTOPILOT'S SETTINGS. The world's (AeroSettings, the "Autopilot" group: the host's caps - Enabled, Unattended, MaxLeanDeg,
/// ClimbRate) and the client's readout; the rest are the DEFAULTS of a new Autopilot block's panel (Blocks/AeroAutopilotBlock*: each
/// block keeps its own, saved with it) and what the offline sim flies with. Engine-free: Tests/AutopilotSim and Tests/SettingsTests
/// compile it. The flying numbers (gains, lean for a given push, how fast it tilts) are never settings: they come from what the mod
/// measures of the ship (FlightDirector) - only limits and choices are.
/// </summary>
public static class Autopilot
{
    // ── world (the host's caps) ──
    /// <summary>Autopilot modes may be engaged in this world (off: every engaged one lets go, "autopilot not allowed").</summary>
    public static bool Enabled = true;
    /// <summary>A ship may keep its engaged mode with nobody seated (each block's "Unattended" chooses within it). Off: it lets go.</summary>
    public static bool Unattended = true;
    /// <summary>Hover hold: the most the ship leans to move or hold its place (deg).</summary>
    public static float MaxLeanDeg = 20f;
    /// <summary>Hover / height hold: the climb and descent rate your up / down keys ask for, and the most it climbs to a new height (m/s).</summary>
    public static float ClimbRate = 4f;

    // ── a new block's panel defaults (and the sim's) ──
    /// <summary>The envelope protection on every autopilot command (stall angle, g, the predicted loss of control, terrain
    /// floor, low energy). Off: the commands as guidance makes them (the thrust and lean limits always hold).</summary>
    public static bool Protection = true;
    /// <summary>Hover hold: the speed your movement keys ask for (m/s), and the most it flies back to its held place at.</summary>
    public static float HoverSpeed = 10f;
    /// <summary>Protection: the most load (g) an autopilot command may pull.</summary>
    public static float GLimit = 3f;
    /// <summary>Protection: the commanded attitude stays this many degrees short of the best-lift angle (the stall).</summary>
    public static float AoaMarginDeg = 3f;
    /// <summary>Protection: the lowest the autopilot lets the ship sink to over the ground (m); 0 off.</summary>
    public static float FloorM = 15f;
    /// <summary>Protection: the look ahead along the path (the time to impact: climb / pull up in time). Off: the floor alone.</summary>
    public static bool LookAhead = true;
    /// <summary>A height hold keeps the height over the ground under the ship (the terrain's shape followed); off: the height
    /// along the gravity from where it was caught (steady over rough ground).</summary>
    public static bool HoldAboveGround = true;

    // ── client (this PC) ──
    /// <summary>The engaged mode and its protections in the cockpit's speed readout ("312 m/s · AP HOVER POS ALT").</summary>
    public static bool Readout = true;
}

/// <summary>The autopilot's fixed tuning shared by the game and the offline sim (one number, one place).</summary>
public static class ApTuning
{
    /// <summary>The share of the rotors' cyclic reach ThrustTorque's aware law plans its braking with while the autopilot flies
    /// (Tests/AutopilotSim: without it a large tilt was paced by the gyros and thrusters alone, and a single-tail helicopter's
    /// sideways flight went into a limit cycle; 0.25..0.5 all hold).</summary>
    public const float CyclicPlanShare = 0.35f;
}
