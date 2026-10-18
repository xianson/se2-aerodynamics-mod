#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.Game2.Simulation.WorldObjects.Shared.Movement;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.GameSystems.Gravity;
using Keen.VRage.Physics.Data;

namespace AeroMod;

/// <summary>
/// HONEST DAMPENERS - the engine side (the law: HonestHold.cs; 2026-10-18). Called first in each grid's sim job
/// (AeroGridComponent.OnAeroSim: During GridUpdateOrder.UpdateSystems.Update, After ThrustComponent.OnComputeThrust - so after
/// the game's ComputeThrust has taken its hold off CustomGravity and ApplyThrust has put its impulse in, and before the
/// Physics step, whose PhysicsUpdate.OnCollectDataForDynamicBodies hands CustomGravity to Havok, CollectRigidBodyGravity:
/// UpdateCustomGravity marked it changed this tick, and the write below marks it again). The structure's contact balance
/// (StructureContacts, later in the same job) reads the CustomGravity written here, the attitude job (ThrustTorque, after
/// UpdateSystems) the thrust change (HonestDelta) - so they see the step the physics will take.
/// SERVER only: the client copy's CustomGravity is the server's, replicated (GravityGeneratorComponent.SyncCustomGravity) -
/// corrected there again it would count the hold twice. MovementData.SuperDampeners in both scenes (each runs its own thrust law).
/// </summary>
public static class HonestDampeners
{
    /// <summary>Diagnostics (harness aeroget): the grids' MovementData.SuperDampeners writes (cleared, or set back). Per grid:
    /// AeroGridComponent.HonestHoldN / HonestShortN (no shared word written per tick by the parallel grid jobs).</summary>
    public static int SuperWrites;

    internal static void Step(AeroGridComponent aero, in WorldTransform wt)
    {
        aero.HonestDelta = Vector3.Zero; aero.HonestHoldN = 0f; aero.HonestShortN = 0f;
        var data = aero.Data;
        bool on = HonestHold.Enabled && AeroSwitch.Enabled;
        // ── super dampeners: the grid's own flag cleared (the game's law then brakes with the thrusters' real maxima), set back
        //    when the setting goes off (only a flag this cleared: the world option was on) ──
        if ((on || aero._superCleared) && data.TryGet<MovementData>(out var md0))
        {
            bool want = HonestHold.SuperDampeners(aero._superCleared || md0.SuperDampeners, on);
            if (md0.SuperDampeners != want)
            {
                // (read first, written only on a change: a write pointer marks the data changed)
                ref MovementData md = ref data.TryGetWritePtr<MovementData>();
                if (!Unsafe.IsNullRef(in md)) { md.SuperDampeners = want; aero._superCleared = !want; System.Threading.Interlocked.Increment(ref SuperWrites); }
            }
            else if (!on) aero._superCleared = false;
        }
        if (!on || !aero.IsServerScene) return;
        float gm = PhysicsHack.GravityScale;
        // (the hold only with dampeners - GameFeatureConfig.ThrustAlwaysDampensGravity is a debug-screen switch, off - and only
        //  where the game held this tick: its thrust ran (ActiveThrustData) and moved CustomGravity off the planet's - a grid
        //  docked to a station, without thrusters or without thrust on the hold's side never held, and is left as it is)
        if (gm == 1f || !data.Has<DampeningData>() || !data.TryGet<ActiveThrustData>(out var atd)
            || !data.TryGet<CustomGravity>(out var cg) || !data.TryGet<GravityEffectData>(out var ge)) return;
        Vector3 g = ge.GravitySum;
        if ((cg.Value - g).LengthSquared() < 1e-8f) return;
        if (!data.TryGet<RigidBodyMassProperties>(out var mp) || !(mp.InvMass > 0f) || !data.TryGet<MaxThrustData>(out var mt)) return;
        float m = 1f / mp.InvMass;
        Vector3 move = data.TryGet<ControlData>(out var cd) ? cd.Movement : Vector3.Zero;
        Vector3 ov = data.TryGet<OverriddenThrustData>(out var od) ? od.DirectionalThrust : Vector3.Zero;
        Vector3 hold = HonestHold.GameHold(WorldTransform.TransformDirectionInv(g, wt), m, move);
        Vector3 game = atd.ComputedThrustPerFrame * 60f - ov;   // (the game's dampening and input thrust, as it applied it)
        Vector3 thrust = HonestHold.Thrust(hold, gm, game, mt.Regular.Positive, mt.Regular.Negative);
        if (!thrust.IsValid()) return;
        ref CustomGravity w = ref data.TryGetWritePtr<CustomGravity>();
        if (Unsafe.IsNullRef(in w)) return;
        w.Value = HonestHold.Gravity(g, WorldTransform.TransformDirection(thrust - game, wt), m, gm);
        // (what the thrusters give beyond the game's own booked command - ThrustTorque shares it onto them, as the rest)
        aero.HonestDelta = thrust - ThrustTorque.GameThrust(data, false);
        aero.HonestHoldN = (hold * gm).Length(); aero.HonestShortN = (hold * gm + game - thrust).Length();
    }
}

public partial class AeroGridComponent
{
    /// <summary>The honest hold's thrust beyond the game's booked command this tick (grid-local N; zero when not held):
    /// HonestDampeners.Step, before the attitude job adds it to the game's thrust it shares.</summary>
    internal Vector3 HonestDelta;
    /// <summary>The hold the dampeners asked for this tick (N: the planet's weight x the multiplier, as the input left it) and
    /// what the thrusters' maxima could not give of it with the rest of the thrust (N: the ship sinks by that / m).</summary>
    internal float HonestHoldN, HonestShortN;
    /// <summary>This grid's MovementData.SuperDampeners was cleared by the honest setting (set back when it goes off).</summary>
    internal bool _superCleared;
}
