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

        // The mod's PhysicsSessionConfiguration loads now (its contentcache is built, 2026-10-01): 1000 m/s and
        // gravity 1 come from the definition, and 1000 is the cap the Orbital Mod's reentry is built on. So no
        // override any more (it set 99999 by reflection); only the probe, to see what the world runs with.
        PhysicsHack.TryFixGravity(apply: false);

        _physicsFixed = true;
    }
}
