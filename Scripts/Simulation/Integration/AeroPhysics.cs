#pragma warning disable
using System;
using Keen.Game2.Simulation;
using Keen.VRage.Core;
using Keen.VRage.Physics.Components;
using Keen.VRage.Physics.Data;

namespace AeroMod;

/// <summary>
/// Real physics access for aero force application.
///
/// SE2 2.4.0.77 added typeof(IPhysics) to GameApp.SetupScripting's AllowedAssemblies, so
/// Keen.VRage.Physics.* is now whitelisted for mod scripts. Before that, PhysicsHack had to
/// reach RigidBodyData through reflection, and the force path did a boxed
/// read-modify-write of the velocity fields with hand-rolled inertia math.
///
/// What this replaces, and why the old way was wrong:
///
///   * It re-implemented the engine's angular math. ApplyDeltaVAndTorque computed
///     deltaOmega = R * (I^-1 * (R^-1 * tau)) * dt itself, mirroring the principal-axis
///     rotation by hand. The engine already ships that exact computation in
///     RigidBodyDataFunctions.ApplyAngularImpulseLocal; any divergence between the two was a
///     silent physics bug.
///   * It boxed on every access. FieldInfo.GetValue/SetValue box each Vector3, and this runs
///     per grid per tick. TryGetWritePtr gives a ref straight into component storage.
///   * It round-tripped through Data.Set&lt;RigidBodyData&gt;(copy) instead of writing in place.
///   * It never checked motion type, so it wrote velocity onto static/keyframed bodies that
///     are not supposed to respond to impulses. The engine's own TryApplyImpulse checks both
///     RigidBodyComponent.CurrentMotionType and IPhysicsMotionProvider.Motion first.
///
/// Note SE2 has no force accumulator: the engine's own impulse helpers mutate
/// LinearVelocity/AngularVelocity directly (see RigidBodyDataFunctions). So an impulse model
/// is correct here; the point is to use the ENGINE's implementation rather than a parallel one.
/// Force -> impulse is force * dt, with dt fixed at UpdateTime.SECONDS_PER_STEP (the sim runs
/// at a fixed 60 Hz; UPDATE_STEPS_PER_SECOND is a compile-time constant).
/// </summary>
public static class AeroPhysics
{
    /// <summary>Fixed simulation step. The sim tick rate is a compile-time constant in SE2.</summary>
    public const float Dt = UpdateTime.SECONDS_PER_STEP;

    /// <summary>
    /// True if this entity's body responds to impulses at all. Static and keyframed bodies
    /// must not be pushed -- writing velocity onto them desyncs render from simulation.
    /// </summary>
    public static bool IsDynamic(Entity entity)
    {
        try
        {
            var rbc = entity.TryGet<RigidBodyComponent>();
            if (rbc != null && rbc.CurrentMotionType != BodyArgs.Motion.Dynamic)
                return false;

            var motion = entity.AsInterface<IPhysicsMotionProvider>();
            if (motion != null && motion.Motion != BodyArgs.Motion.Dynamic)
                return false;

            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Apply an aerodynamic force (world space, newtons) and moment (grid-local, newton-metres)
    /// about the centre of mass, for one simulation step.
    /// </summary>
    public static bool ApplyForceAndTorque(Entity entity, in WorldTransform wt,
                                           Vector3 worldForce, Vector3 localTorque,
                                           float dt = Dt)
    {
        if (!IsFinite(worldForce) || !IsFinite(localTorque))
            return false;
        if (!IsDynamic(entity))
            return false;

        try
        {
            var data = entity.Data;
            ref RigidBodyData rb = ref data.TryGetWritePtr<RigidBodyData>();
            if (Unsafe.IsNullRef(in rb))
                return false;
            if (!data.TryGet<RigidBodyMassProperties>(out var mass))
                return false;

            if (worldForce.LengthSquared() > 0f)
                rb.ApplyLinearImpulse(in mass, worldForce * dt);

            if (localTorque.LengthSquared() > 0f)
                rb.ApplyAngularImpulseLocal(in mass, in wt, localTorque * dt);

            return true;
        }
        catch
        {
            // Entity detached mid-tick: Scene.TryGetDataPointer throws rather than returning null.
            return false;
        }
    }



    /// <summary>Read velocities without reflection.</summary>
    public static bool TryGetVelocity(Entity entity, out Vector3 linear, out Vector3 angular)
    {
        linear = Vector3.Zero;
        angular = Vector3.Zero;
        RigidBodyData rb;
        try { if (!entity.Data.TryGet<RigidBodyData>(out rb)) return false; }
        catch { return false; }
        linear = rb.LinearVelocity;
        angular = rb.AngularVelocity;
        return true;
    }

    private static bool IsFinite(Vector3 v) =>
        !float.IsNaN(v.X) && !float.IsNaN(v.Y) && !float.IsNaN(v.Z) &&
        !float.IsInfinity(v.X) && !float.IsInfinity(v.Y) && !float.IsInfinity(v.Z);
}
