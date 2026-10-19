#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

// THE PUBLIC API, VERSION 11: the autopilot (Autopilot/: hover hold, height hold, envelope protection) and the flight performance.
// Reads of mod-owned fields only (the server grid's flight director, the stall evaluation's published box) - any thread, no
// allocation, no exception handling on the way. The ask is queued: the grid's next attitude step engages or lets go.
public static partial class AeroApi
{
    /// <summary>(version 11) The grid's autopilot: the goals it holds (bits: 1 attitude, 2 hover, 4 position, 8 height, 16 an
    /// external goal source), the protections in force this tick (bits: 1 lean limit, 2 thrust limit, 4 stall limit, 8 g limit,
    /// 16 control limit - the predicted loss of control, 32 overspeed, 64 terrain floor, 128 full thrust - low energy, 256 terrain
    /// ahead), why it last let go or refused (0 none; see docs/API.md), and its readout ("AP HOVER POS AGL · FLOOR", "" off).
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
    /// what a seated pilot's keys ask (Ctrl+H hover hold is 2 | 8), the world's Autopilot.* settings and the autopilot's own
    /// checks applying (it refuses with a reason: TryGetAutopilot). Queued for the grid's next simulation step (server). False: no
    /// server grid found for it (a client grid not yet paired - ask again a tick later).</summary>
    public static bool RequestAutopilot(Keen.VRage.DCS.Components.Entity grid, int goals)
    {
        var aero = DesignProbe.ServerAero(grid);
        if (aero == null || !aero.IsServerScene) return false;
        System.Threading.Volatile.Write(ref aero.ApAsk, Math.Clamp(goals, 0, 0xFF));
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
