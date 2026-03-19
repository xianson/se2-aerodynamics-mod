#pragma warning disable
using System;
using Keen.Game2.Simulation.GameSystems.Movement;
using Keen.VRage.Core.Systems;
using Keen.VRage.DCS.Annotations;

namespace AeroMod;

/// <summary>
/// Separate job to uncap the speed limit and fix gravity.
/// Uses IVelocityLimitProvider as its own JobContext (separate from draw job).
/// </summary>
public partial class AeroGridComponent
{
    private static bool _physicsFixed;

    [After(typeof(RenderSubmissionBegin))]
    private class OnSpeedUncap : JobGroup;

    [OnSpeedUncap]
    [MustHave(typeof(AeroGridComponent))]
    private static void SpeedUncapJob(IVelocityLimitProvider vlp)
    {
        if (_physicsFixed) return;

        // Uncap speed via VelocityLimitProvider
        PhysicsHack.UncapSpeed(vlp, 99999f);

        // Override PhysicsSessionConfiguration: uncap MaximumSpeedLinear, set GravityMultiplier
        PhysicsHack.TryFixGravity(targetGravity: 1f, targetSpeed: 99999f);

        _physicsFixed = true;
    }
}
