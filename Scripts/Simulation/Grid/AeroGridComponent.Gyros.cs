#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement;
using Keen.VRage.Core;
using Keen.VRage.DCS.Annotations;

namespace AeroMod;

/// <summary>
/// THE GYRO REBALANCE's game part (GyroBalance: the scale, the saturation store, the write rule). Server, every
/// GyroBalance.Period ticks, after the attitude job (it reads the torque the gyros gave this tick) and before the physics:
/// each of the grid's gyros (the gyro cache) gets GyroscopeData.MaxTorque = its definition's x GyroBalance.Factor - the
/// game's own number (GyroscopeComponent.Init writes the definition's there, and nothing else writes it), which its
/// collector sums into the grid's MaxTorqueData next tick (GridMovementCollectorComponent.OnGyroscopeChanged, in
/// UpdateSystems.Collect - done for this tick by now) and replicates; every gyro law then spends that. Always from the
/// definition, never from the value now, so it cannot compound; aero off: the definition's written back. (GyroscopeData is
/// server-only - GyroscopeComponent is [ServerOnly] - the client copy reads the replicated MaxTorqueData.)
/// </summary>
public partial class AeroGridComponent
{
    [After(typeof(OnAttitude))]
    [Before(typeof(Keen.Game2.Simulation.Physics))]
    private class OnGyroBalance : JobGroup;

    [OnGyroBalance]
    [MustHave(typeof(AeroGridComponent))]
    private static void GyroBalanceJob(AeroGridComponent aero)
    {
        if (!aero.IsServerScene || ++aero._gyroTick < GyroBalance.Period) return;
        aero._gyroTick = 0;
        aero.GyroBalanceStep(GyroBalance.Period * AeroPhysics.Dt);
    }

    int _gyroTick;
    /// <summary>The gyros' stored momentum (N m s, grid-local: GyroBalance.Step), its fill (0..1), the share of their
    /// ratings they give now (scale x fade; 1 with aero off), and the rated sum it was last taken against.</summary>
    internal Vector3 GyroStore;
    internal float GyroFill, GyroFactor = 1f, GyroFade = 1f;
    float _gyroRated;

    void GyroBalanceStep(float dt)
    {
        var cache = _gyroCache;
        int n = cache.Count;
        if (n == 0) { GyroStore = Vector3.Zero; GyroFill = 0f; GyroFade = 1f; GyroFactor = 1f; _gyroRated = 0f; return; }
        bool on = AeroSwitch.Enabled;
        float rated = 0f;
        for (int i = 0; i < n; i++) if (cache[i].MaxTorque > 0f) rated += cache[i].MaxTorque;
        GyroBalance.Lost(ref GyroStore, _gyroRated, rated);   // (gyros gone - a cut, a block ground off: their share went with them)
        _gyroRated = rated;
        // the store: the torque the gyros gave - the aero-aware law's own (it flew them this tick), else the game's law's
        // impulse this frame (GyroControl.GameImpulse) - against their full torque now: the grid's MaxTorqueData (the
        // working gyros only - one switched off counts 0 there) with the fade it was written at taken out
        if (on && GyroBalance.Saturation)
        {
            float live = Data.TryGet<Keen.Game2.Simulation.WorldObjects.Shared.Movement.MaxTorqueData>(out var mt) ? mt.MaxTorque : 0f;
            float full = GyroControl.Budget(Data, GyroBalance.Unfaded(live, GyroFade));
            Vector3 tau = ThrustTorque.Enabled && LastGyroAware ? LastGyroTorque : GyroControl.GameImpulse(Data) / AeroPhysics.Dt;
            GyroFill = GyroBalance.Step(ref GyroStore, tau, full, GyroBalance.SaturationSeconds, dt);
        }
        else { GyroStore = Vector3.Zero; GyroFill = 0f; }
        GyroFade = on && GyroBalance.Saturation ? GyroBalance.Fade(GyroFill) : 1f;
        GyroFactor = GyroBalance.Factor(on, GyroFill);
        for (int i = 0; i < n; i++)
        {
            var g = cache[i];
            var d = g.GyroEntity?.Data;
            // (a gyro removed or its grid cut is not alive: reading its data would throw - 200 ms each, se2-exceptions-cost)
            if (d == null || !(g.MaxTorque > 0f) || !GridPhysics.Alive(d.Value)) continue;
            var dv = d.Value;
            if (!dv.TryGet<GyroscopeData>(out var gd)) continue;
            if (!GyroBalance.Target(g.MaxTorque, gd.MaxTorque, GyroFactor, out float write)) continue;
            gd.MaxTorque = write;
            dv.Set(gd);
        }
    }
}
