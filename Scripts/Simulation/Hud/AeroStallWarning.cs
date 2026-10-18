using Keen.Game2.Simulation.WorldObjects.CubeGrids.Movement;
using Keen.Game2.Simulation.WorldObjects.Shared.Movement;
using Keen.VRage.DCS.Annotations;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace AeroMod;

/// <summary>
/// The cockpit's THRUST OVERLOAD warning (the game's stall warning) counts THRUSTERS only: ThrustStallData.GravityHoldRatio is
/// raw upward thrust over the weight to hold (GridMovementCollectorComponent.ReportThrustStall) - the wings' lift never
/// enters it, so anything flying on its wings with thrusters too weak to hover was warned in level flight. While aero sees
/// real lift (at least StallLiftShare of the weight) the warning is off - the ratio written as float.MaxValue, the game's own
/// "nothing to warn about" - right after the game's report, on the server (it replicates to the cockpit from there). With no
/// lift the game's own value stands (the user, 2026-10-09: "disable it unless there's no lift").
/// </summary>
public partial class AeroGridComponent
{
    /// <summary>Lift (as a share of the weight) from which the thrust-overload warning is off.</summary>
    public static float StallLiftShare = 0.1f;
    public static bool StallFromLift = true;
    /// <summary>The thrust-overload ratio this wrote last (honest dampeners' scaling): the game's report left standing is not scaled again.</summary>
    float _stallScaled = float.NaN;

    [After(typeof(GridMovementCollectorComponent.ThrusterUpdate.ReportStall))]
    private class OnAeroStall : JobGroup;

    [OnAeroStall]
    [MustHave(typeof(AeroGridComponent))]
    private static void AeroStallJob(AeroGridComponent aero)
    {
        if (!aero.IsServerScene) return;
        var data = aero.Data;
        if (!data.TryGet<ThrustStallData>(out var stall) || stall.GravityHoldRatio == float.MaxValue) return;
        if (StallFromLift && aero.HasResult)
        {
            float g = PhysicsHack.GetGravityDirection(data).Length();
            float weight = aero.LastMass * g * PhysicsHack.GravityScale;   // (the real weight: the engine's multiplier - the lift carries all of it)
            if (weight > 0f && aero.LastResult.LiftMagnitude >= StallLiftShare * weight) { data.Set(new ThrustStallData { GravityHoldRatio = float.MaxValue }); return; }
        }
        // (honest dampeners - HonestDampeners: the thrust must hold the planet's weight x the engine's multiplier, so the game's
        //  ratio over the planet's weight alone is that many times too generous - the warning at its real 1. The game's report
        //  writes its own value again when it moves 0.01 off this one; this follows it, right after - scaled once: a value it left
        //  standing is this one's, never divided again. Only while the game's warning is shown - Hud.GameStallWarning, off by
        //  default: the game rewriting it every tick then, it replicates every tick)
        float gm = PhysicsHack.GravityScale;
        if (!GameStallWarning.On || !HonestHold.Enabled || !AeroSwitch.Enabled || gm == 1f || !(gm > 0f) || stall.GravityHoldRatio == aero._stallScaled) return;
        aero._stallScaled = stall.GravityHoldRatio / gm;
        data.Set(new ThrustStallData { GravityHoldRatio = aero._stallScaled });
    }
}
