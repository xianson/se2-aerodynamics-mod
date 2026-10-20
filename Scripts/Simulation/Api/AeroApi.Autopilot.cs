#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

// THE PUBLIC API, VERSIONS 11-12: the autopilot (Autopilot/: hover hold, height hold, envelope protection; 12: the flight pilot -
// its target, its aim, its mode) and the flight performance.
// Reads of mod-owned fields only (the server grid's flight director, the stall evaluation's published box) - any thread, no
// allocation, no exception handling on the way. The ask is queued: the grid's next attitude step engages or lets go.
public static partial class AeroApi
{
    /// <summary>(version 11) The grid's autopilot: the goals it holds (bits: 1 attitude, 2 hover, 4 position, 8 height, 16 an
    /// external goal source), the protections in force this tick (bits: 1 lean limit, 2 thrust limit, 4 stall limit, 8 g limit,
    /// 16 control limit - the predicted loss of control, 32 overspeed, 64 terrain floor, 128 full thrust - low energy, 256 terrain
    /// ahead, 512 collision avoidance), why it last let go or refused (0 none; 15: the Autopilot block went dark - see docs/API.md), and its readout ("AP HOVER POS AGL · FLOOR", "" off).
    /// False: no autopilot on this grid (never engaged) or no server grid found for it.</summary>
    public static bool TryGetAutopilot(Keen.VRage.DCS.Components.Entity grid, out int goals, out int protections, out int reason, out string readout)
    {
        goals = protections = reason = 0; readout = "";
        var aero = DesignProbe.ServerAero(grid);
        var fd = aero?.Fd;
        if (fd == null) return false;
        goals = (int)fd.Goals; protections = (int)fd.Prot; reason = (int)fd.Reason; readout = fd.Readout ?? "";
        return true;
    }

    /// <summary>(version 11) Ask the grid's autopilot for exactly these goals (the bits of TryGetAutopilot; 0: every goal off) -
    /// what an Autopilot block's Hover Hold button asks is 2 | 8, Height Hold 8 - the world's Autopilot.* settings and the autopilot's
    /// own checks applying (it refuses with a reason: TryGetAutopilot). An Autopilot block on the grid keeps its own panel: choosing a
    /// mode there (or the block going dark) replaces what was asked here. Queued for the grid's next simulation step (server). False:
    /// no server grid found for it (a client grid not yet paired - ask again a tick later).</summary>
    public static bool RequestAutopilot(Keen.VRage.DCS.Components.Entity grid, int goals)
    {
        var aero = DesignProbe.ServerAero(grid);
        if (aero == null || !aero.IsServerScene) return false;
        System.Threading.Volatile.Write(ref aero.ApAsk, Math.Clamp(goals, 0, 0xFF));
        return true;
    }

    /// <summary>(version 12) The target the grid's Autopilot block engages when its Target Source is "Api": an entity (a grid, a
    /// block - its top grid's velocity is used). False: no server grid found for it.</summary>
    public static bool SetAutopilotTarget(Keen.VRage.DCS.Components.Entity grid, Keen.VRage.DCS.Components.Entity target)
    {
        var aero = DesignProbe.ServerAero(grid);
        if (aero == null || !aero.IsServerScene) return false;
        var t = aero.ApTargetsMade; t.ApiPoint = false; t.ApiEntity = target;
        return true;
    }

    /// <summary>(version 12) ... or a point (world) moving at a velocity (world m/s) - extrapolated by the autopilot until set again.</summary>
    public static bool SetAutopilotTargetPoint(Keen.VRage.DCS.Components.Entity grid, Vector3D position, Vector3 velocity)
    {
        var aero = DesignProbe.ServerAero(grid);
        if (aero == null || !aero.IsServerScene) return false;
        var t = aero.ApTargetsMade; t.ApiEntity = null; t.ApiPos = position; t.ApiVel = velocity; t.ApiPoint = true;
        return true;
    }

    /// <summary>(version 12) No API target (the block then waits - patrols its route or loiters).</summary>
    public static bool ClearAutopilotTarget(Keen.VRage.DCS.Components.Entity grid)
    {
        var aero = DesignProbe.ServerAero(grid);
        if (aero == null || !aero.IsServerScene) return false;
        var t = aero.ApTargetsMade; t.ApiPoint = false; t.ApiEntity = null;
        return true;
    }

    /// <summary>(version 12) THE AIM, for another mod's weapons logic (the autopilot never fires): the angle (deg) between the fixed
    /// guns' axis (or the nose) and the target's lead point, whether that is inside the block's aim cone (or the miss inside the
    /// target's size) and the target within the guns' range, the range (m) and the projectile's time of flight (s). False: no
    /// target being aimed at (not in Engage, no target, aim off, or the behaviour does not aim now).</summary>
    public static bool TryGetAutopilotAim(Keen.VRage.DCS.Components.Entity grid, out float errorDeg, out bool inCone, out bool inRange, out float range, out float timeOfFlight)
    {
        errorDeg = float.NaN; inCone = inRange = false; range = timeOfFlight = 0f;
        var p = DesignProbe.ServerAero(grid)?.ApPilotNow;
        if (p == null || !p.AimOn || !float.IsFinite(p.AimErrDeg)) return false;
        errorDeg = p.AimErrDeg; inCone = p.InCone; inRange = p.InRange; range = p.T.Range; timeOfFlight = p.Tof;
        return true;
    }

    /// <summary>(version 12) The flight pilot: its mode (3 flight hold, 4 navigate, 5 engage - ApMode), the style it flies (1 airplane,
    /// 2 helicopter, 3 six-axis), the behaviour active (ApBehaviour: 1 attack run .. 11 pursuit lag, 12 extend; 13 dogfight, 14 rocket guidance, 15 formation - version 13), a target held, and
    /// the next waypoint's index (-1: loitering). False: no pilot mode flying.</summary>
    public static bool TryGetAutopilotPilot(Keen.VRage.DCS.Components.Entity grid, out int mode, out int style, out int behaviour, out bool hasTarget, out int waypoint)
    {
        mode = style = behaviour = 0; hasTarget = false; waypoint = -1;
        var aero = DesignProbe.ServerAero(grid);
        var p = aero?.ApPilotNow;
        if (p == null || aero.Fd == null || (aero.Fd.Goals & ApGoal.External) == 0) return false;
        mode = (int)p.Mode; style = (int)p.Style; behaviour = (int)p.ActiveBh; hasTarget = p.T.Valid; waypoint = p.Loitering ? -1 : p.Wp;
        return true;
    }

    /// <summary>(version 13) The behaviour flying in Engage beyond the numbers TryGetAutopilotPilot gives: the active behaviour
    /// (13 dogfight, 14 rocket guidance, 15 formation - ApBehaviour - or a built-in one's number), the combat slot's own status text
    /// (ApSlotOut.Status - "" none, or the slot's logic not written yet), and the formation's slot error (m; its part along the
    /// leader's path, + behind the slot; both NaN when not in formation). False: not in Engage with a target.</summary>
    public static bool TryGetAutopilotSlot(Keen.VRage.DCS.Components.Entity grid, out int behaviour, out string status, out float slotError, out float slotAlong)
    {
        behaviour = 0; status = ""; slotError = slotAlong = float.NaN;
        var aero = DesignProbe.ServerAero(grid);
        var p = aero?.ApPilotNow;
        if (p == null || aero.Fd == null || (aero.Fd.Goals & ApGoal.External) == 0 || p.Mode != ApMode.Engage || !p.T.Valid) return false;
        behaviour = (int)p.ActiveBh;
        status = ApPilot.IsSlot(p.ActiveBh) ? p.SlotText ?? "" : "";
        if (p.ActiveBh == ApBehaviour.Formation) { slotError = p.Formation.Err; slotAlong = p.Formation.Along; }
        return true;
    }

    /// <summary>(version 12) The grid's Autopilot block (the one that flies; else the first): whether the grid is an ENEMY now (the
    /// game's hostile NPC owns it - by the block's Enemy switch or otherwise; its turrets and Engage fight players) and its target
    /// filter (ApTargetFilter: 0 hostile only, 1 hostile + neutral, 2 everything - the game's own relation, as the turrets judge).
    /// The switch itself is the block's panel (its world rule and its owner checks); another mod changes ownership through the
    /// game (OwnershipSessionComponent.TryTransferToNPC). False: no Autopilot block on the grid.</summary>
    public static bool TryGetAutopilotEnemy(Keen.VRage.DCS.Components.Entity grid, out bool enemy, out int targetFilter)
    {
        enemy = false; targetFilter = 0;
        var aero = DesignProbe.ServerAero(grid);
        var b = aero?.ApCtlNow;
        if (b == null) { AeroAutopilotComponent[] all = aero?.ApBlocks; if (all == null || all.Length == 0) return false; b = all[0]; }
        enemy = b.GridIsEnemy; targetFilter = (int)b.Panel.TargetFilter;
        return true;
    }

    /// <summary>(version 11) The FLIGHT PERFORMANCE of a flown grid now, from the mod's own whole-ship lift and drag over the angle

    /// of attack (the stall evaluation's sweep, a few times a second; Mach and density in) and its thrust: the most load the lift
    /// gives within the stall margin and Autopilot.GLimit (instantaneous, g) and the most its full thrust sustains (g), the level
    /// turn rates at those (rad/s), the drag's deceleration now (m/s2), full thrust less the drag along the path (m/s2), and the
    /// most nose-up the flow may be turned before the stall margin (deg). False: not evaluated (slower than 20 m/s, thin air, not
    /// flown - the evaluation runs for a seated or autopilot-flown grid).</summary>
    public static bool TryGetFlightPerformance(Keen.VRage.DCS.Components.Entity grid, out float nInstant, out float nSustained, out float turnRateInstant,
        out float turnRateSustained, out float dragDecel, out float excessAccel, out float alphaMaxDeg)
    {
        nInstant = nSustained = turnRateInstant = turnRateSustained = dragDecel = excessAccel = alphaMaxDeg = 0f;
        var aero = DesignProbe.ServerAero(grid);
        var box = aero?.PerfBoxNow;
        if (box == null || !box.P.Valid || System.Diagnostics.Stopwatch.GetTimestamp() - box.Stamp > 2 * System.Diagnostics.Stopwatch.Frequency) return false;
        var p = box.P;
        nInstant = p.NInst; nSustained = p.NSust; turnRateInstant = p.TurnRateInst; turnRateSustained = p.TurnRateSust;
        dragDecel = p.DragDecelNow; excessAccel = p.ExcessAccel; alphaMaxDeg = p.AlphaMax / ApMath.Deg;
        return true;
    }
}
