#pragma warning disable
using System;
using System.Reflection;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Movement;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.Game2.Simulation.WorldObjects.Shared.Movement;
using Keen.VRage.Core;
using Keen.VRage.DCS.Accessors;
using Keen.VRage.Physics.Data;

namespace AeroMod;

/// <summary>
/// THE GAME'S GYROS UNDER THE MOD'S ATTITUDE LAW (2026-10-12, the user: "fix the controllers so they are aware of exact
/// aero torque but they don't cheat"). The game's gyro controller (GridGyroscopesComponent.ComputeTorqueTarget) steers a
/// piloted grid to its target orientation with a law that knows nothing of the air: it has no input for an outside torque
/// and its output (ActiveGridGyroscopeData) is private. So in air, piloted, the mod's law flies the gyros instead
/// (ThrustTorque.Apply, after the game's grid systems):
///  - Taken back: the angular velocity the game's gyros gave this frame - the impulse its law computed (read from its
///    ActiveGridGyroscopeData) through the game's own ApplyAngularImpulseTarget, undone exactly.
///  - Given: the mod's law's gyro torque, held to what the game's gyros can give (Reach: its own budget - the smaller of
///    MaximumTorqueLimiterCoefficient x mass^2 and MaxTorque x TorqueDampenerPowerMultiplier, x the pilot's per-axis
///    multiplier, the grid's bounds weighting - the game's ComputeTorqueTarget's numbers at full error).
/// The gyros' MaxTorque is never written (it is replicated, and at 0 the cockpit hides the aim reticle).
/// </summary>
public static class GyroControl
{
    static bool _resolved; static MethodInfo _tryGet; static FieldInfo _computed;
    /// <summary>Resolved and readable (the game's private ActiveGridGyroscopeData and its one field).</summary>
    public static bool Ready { get { Resolve(); return _tryGet != null && _computed != null; } }

    static void Resolve()
    {
        if (_resolved) return;
        _resolved = true;
        try
        {
            var t = typeof(GridGyroscopesComponent).GetNestedType("ActiveGridGyroscopeData", BindingFlags.NonPublic);
            _computed = t?.GetField("ComputedGyroscopePerFrame", BindingFlags.Public | BindingFlags.Instance);
            if (t != null)
                foreach (var m in typeof(DEntityContext).GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (m.Name == "TryGet" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1) { _tryGet = m.MakeGenericMethod(t); break; }
        }
        catch { _tryGet = null; _computed = null; }
        Log.Default?.Info($"[AERO] GyroControl: game gyro impulse readable={_tryGet != null && _computed != null}");
    }

    [ThreadStatic] static object[] _args;
    /// <summary>The angular impulse the game's gyros applied this frame (local; zero: none).</summary>
    public static Vector3 GameImpulse(DEntityContext data)
    {
        Resolve();
        if (_tryGet == null || _computed == null) return Vector3.Zero;
        var a = _args ??= new object[1];
        a[0] = null;
        try
        {
            if (!(bool)_tryGet.Invoke(data, a) || a[0] == null) return Vector3.Zero;
            return (Vector3)_computed.GetValue(a[0]);
        }
        catch { return Vector3.Zero; }
    }

    /// <summary>The angular velocity change (local) the game's ApplyAngularImpulseTarget made of that impulse: Δω for it,
    /// blended toward the impulse's own direction by the grid's smallest bounds weight.</summary>
    public static Vector3 GameDeltaW(in RigidBodyMassProperties mass, in BoundingBoxData bounds, Vector3 impulse)
    {
        float n2 = impulse.Length();
        if (n2 <= 0f) return Vector3.Zero;
        Vector3 v = Vector3.Transform(mass.InvInertiaMajorAxisRotation * impulse * mass.InvInertiaTensor, mass.InertiaMajorAxisRotation);
        float n1 = v.Length();
        return Vector3.Lerp(v, impulse * (n1 / n2), Weights(in bounds).Min());
    }

    /// <summary>Take back what the game's gyros gave this frame. Returns the local Δω removed.</summary>
    public static Vector3 Undo(DEntityContext data, in WorldTransform wt)
    {
        Vector3 imp = GameImpulse(data);
        if (imp == Vector3.Zero) return Vector3.Zero;
        if (!data.TryGet<RigidBodyMassProperties>(out var mass) || !data.TryGet<BoundingBoxData>(out var bb)) return Vector3.Zero;
        Vector3 dw = GameDeltaW(in mass, in bb, imp);
        if (!dw.IsValid()) return Vector3.Zero;
        ref RigidBodyData rb = ref data.TryGetWritePtr<RigidBodyData>();
        if (Unsafe.IsNullRef(in rb)) return Vector3.Zero;
        rb.AngularVelocity -= WorldTransform.TransformDirection(dw, wt);
        return dw;
    }

    /// <summary>The most torque (N m, per local axis) the game's gyros give this grid: ComputeTorqueTarget at full error.</summary>
    public static Vector3 Reach(DEntityContext data, in RigidBodyMassProperties mass, in TargetControlData target)
    {
        if (!data.TryGet<MaxTorqueData>(out var mt) || mt.MaxTorque <= 0f || !data.TryGet<MovementData>(out var md) || !data.TryGet<BoundingBoxData>(out var bb)) return Vector3.Zero;
        var rot = md.Definition.Value.TargetGyroRotation;
        float m = mass.InvMass > 0f ? 1f / mass.InvMass : 0f;
        float num = MathF.Min(rot.MaximumTorqueLimiterCoefficient * m * m, mt.MaxTorque * rot.TorqueDampenerPowerMultiplier);
        if (!(num > 0f)) return Vector3.Zero;
        Vector3 v = new Vector3(num) * target.PerAxisDampeningMultiplier;
        Vector3 w = Weights(in bb);
        return Vector3.Lerp(v * w, v, MathHelper.Saturate((w.Min() - 0.1f) / 0.15f));
    }

    /// <summary>The game's ComputeGridBoundsWeights (gamma 1.5): 1 on a cube, less about a long grid's short axes.</summary>
    static Vector3 Weights(in BoundingBoxData b)
    {
        Vector3 s = b.BoundingBox.Size;
        if (!(s.X > 0f && s.Y > 0f && s.Z > 0f)) return Vector3.One;
        float c = (float)Math.Pow(s.X * s.Y * s.Z, 1.0 / 3.0);
        return Vector3.Min(new Vector3((float)Math.Pow(c / s.X, 1.5), (float)Math.Pow(c / s.Y, 1.5), (float)Math.Pow(c / s.Z, 1.5)), Vector3.One);
    }
}
