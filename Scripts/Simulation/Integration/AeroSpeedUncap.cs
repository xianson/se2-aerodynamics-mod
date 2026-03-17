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

        // Uncap speed to 1000 m/s
        PhysicsHack.UncapSpeed(vlp, 1000f);

        // Fix gravity: find PhysicsSessionConfiguration and set multiplier to 1
        PhysicsHack.TryFixGravity();

        _physicsFixed = true;
    }
}
