#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
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
/// The grid's MaxTorqueData is never written (it is replicated, the game adds and subtracts into it, and at 0 the cockpit
/// hides the aim reticle); the gyros' own maxima are scaled at the source instead (GyroBalance, AeroGridComponent.Gyros.cs).
/// </summary>
public static class GyroControl
{
    static bool _resolved; static ImpulseReader _reader;
    /// <summary>Resolved and readable (the game's private ActiveGridGyroscopeData and its one field).</summary>
    public static bool Ready { get { Resolve(); return _reader != null; } }

    /// <summary>The game's private ActiveGridGyroscopeData read typed (2026-10-18: it was a MethodInfo.Invoke of TryGet - the
    /// struct boxed as its out argument, the bool and the Vector3 result boxed, three objects a read, every tick per piloted
    /// grid and every GyroBalance.Period ticks per grid with gyros): our own ImpulseReader&lt;G&gt; made generic on it once
    /// (MakeGenericType, as AeroFlameComponent's EfficiencyReader), a direct TryGet&lt;G&gt; and Unsafe.As on its one Vector3
    /// field (checked first: one instance field, ComputedGyroscopePerFrame, a Vector3 at offset 0 of a 12-byte struct).</summary>
    abstract class ImpulseReader { public abstract Vector3 Read(DEntityContext data); public abstract bool Fits { get; } }
    sealed class ImpulseReader<G> : ImpulseReader where G : unmanaged
    {
        public override bool Fits => System.Runtime.CompilerServices.Unsafe.SizeOf<G>() == System.Runtime.CompilerServices.Unsafe.SizeOf<Vector3>();
        public override Vector3 Read(DEntityContext data)
            => data.TryGet<G>(out var v) ? System.Runtime.CompilerServices.Unsafe.As<G, Vector3>(ref v) : Vector3.Zero;
    }

    static void Resolve()
    {
        if (_resolved) return;
        _resolved = true;
        try
        {
            var t = typeof(GridGyroscopesComponent).GetNestedType("ActiveGridGyroscopeData", BindingFlags.NonPublic);
            var fs = t?.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (t != null && t.IsValueType && fs != null && fs.Length == 1 && fs[0].Name == "ComputedGyroscopePerFrame" && fs[0].FieldType == typeof(Vector3))
            {
                var r = (ImpulseReader)Activator.CreateInstance(typeof(ImpulseReader<>).MakeGenericType(t));
                _reader = r.Fits ? r : null;
            }
        }
        catch { _reader = null; }
        if (AeroLog.Detail) Log.Default?.Info($"[AERO] GyroControl: game gyro impulse readable={_reader != null}");
    }

    /// <summary>The angular impulse the game's gyros applied this frame (local; zero: none). No allocation.</summary>
    public static Vector3 GameImpulse(DEntityContext data)
    {
        Resolve();
        var r = _reader;
        if (r == null) return Vector3.Zero;
        Vector3 v = r.Read(data);
        return v.IsValid() ? v : Vector3.Zero;
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
        => Reach(data, in mass, target.PerAxisDampeningMultiplier);

    /// <summary>... with this per-axis multiplier (the pilot's: TargetControlData.PerAxisDampeningMultiplier - 1, 1 and the
    /// roll boost, so Vector3.One is the plain reach: RotorLimiter's authority, piloted or not).</summary>
    public static Vector3 Reach(DEntityContext data, in RigidBodyMassProperties mass, Vector3 perAxis)
    {
        if (!data.TryGet<MaxTorqueData>(out var mt) || mt.MaxTorque <= 0f || !data.TryGet<MovementData>(out var md) || !data.TryGet<BoundingBoxData>(out var bb)) return Vector3.Zero;
        var rot = md.Definition.Value.TargetGyroRotation;
        float m = mass.InvMass > 0f ? 1f / mass.InvMass : 0f;
        float num = MathF.Min(rot.MaximumTorqueLimiterCoefficient * m * m, mt.MaxTorque * rot.TorqueDampenerPowerMultiplier);
        if (!(num > 0f)) return Vector3.Zero;
        Vector3 v = new Vector3(num) * perAxis;
        Vector3 w = Weights(in bb);
        return Vector3.Lerp(v * w, v, MathHelper.Saturate((w.Min() - 0.1f) / 0.15f));
    }

    /// <summary>The target law's full torque (N m) for gyros summing maxTorque (N m) on this grid: ComputeTorqueTarget's
    /// min(MaximumTorqueLimiterCoefficient x mass^2, MaxTorque x TorqueDampenerPowerMultiplier) - GyroBalance's store size.
    /// 0: not readable.</summary>
    public static float Budget(DEntityContext data, float maxTorque)
    {
        if (!(maxTorque > 0f) || !data.TryGet<MovementData>(out var md) || !data.TryGet<RigidBodyMassProperties>(out var mass)) return 0f;
        var rot = md.Definition.Value.TargetGyroRotation;
        float m = mass.InvMass > 0f ? 1f / mass.InvMass : 0f;
        float b = MathF.Min(rot.MaximumTorqueLimiterCoefficient * m * m, maxTorque * rot.TorqueDampenerPowerMultiplier);
        return float.IsFinite(b) && b > 0f ? b : 0f;
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
