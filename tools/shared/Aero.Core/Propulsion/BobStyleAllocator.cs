using System.Numerics;

namespace Aero.Core.Propulsion;

/// <summary>
/// Per-thruster dot-product allocator inspired by BobSurvival's RCS mod for SE1.
///
/// Each thruster independently computes its throttle by dotting the demand vectors
/// against its own effectiveness. No centralized optimization — the projection IS
/// the allocation. Rotation and translation are blended per-thruster via
/// RotationThrustLimiter (equivalent to Bob's Terminal_RotationThrustLimiter).
///
/// Key behavior (matching SE1 mod exactly):
///   1. Compute movement = Dot(inputDir, thrusterDir) — per-thruster
///   2. Compute rotation = Dot(rotationInput, torqueArm) — per-thruster
///   3. If movement == 0 for THIS thruster, linear damping takes over
///   4. If rotation == 0 for THIS thruster, angular damping takes over
///   5. Blend: throttle = rotation * limiter + movement * (1 - limiter)
///
/// Inputs are normalized directions ([-1,1] per axis) matching SE1's MoveIndicator
/// and RotationIndicator, not forces in Newtons.
/// </summary>
public class BobStyleAllocator : ThrustAllocatorBase
{
    public override string Name => "BobStyle";
    public override string Description => "Per-thruster dot-product allocation (SE1 RCS mod style).";

    /// <summary>
    /// Fraction of throttle capacity reserved for rotation (0..1). Default 0.5.
    /// Translation gets (1 - RotationThrustLimiter).
    /// Matches Bob's Terminal_RotationThrustLimiter.
    /// </summary>
    public float RotationThrustLimiter { get; set; } = 0.5f;

    /// <summary>
    /// Angular velocity deadzone in rad/s. Below this, angular damping is inactive.
    /// Matches Bob's RotationDampeningAggresiveness (0.01).
    /// </summary>
    public float AngularDampeningDeadzone { get; set; } = 0.01f;

    /// <summary>
    /// Linear velocity deadzone in m/s. Below this, linear damping is inactive.
    /// Matches Bob's MovementDampeningAggresiveness (0.001).
    /// </summary>
    public float LinearDampeningDeadzone { get; set; } = 0.001f;

    public override ThrustResult Allocate(ThrusterInfo[] thrusters, Vector3 centerOfMass, in ThrustContext ctx)
    {
        int n = thrusters.Length;
        if (n == 0) return ThrustResult.Empty;

        var throttles = new float[n];
        Vector3 producedForce = Vector3.Zero;
        Vector3 producedTorque = Vector3.Zero;

        // Normalize inputs to [-1,1] direction vectors (like SE1's MoveIndicator/RotationIndicator)
        Vector3 moveDir = Vector3.Zero;
        if (ctx.InputForce.LengthSquared() > 1e-8f)
            moveDir = Vector3.Normalize(ctx.InputForce);

        Vector3 rotDir = Vector3.Zero;
        if (ctx.InputTorque.LengthSquared() > 1e-8f)
            rotDir = Vector3.Normalize(ctx.InputTorque);

        for (int i = 0; i < n; i++)
        {
            ref readonly var t = ref thrusters[i];
            Vector3 dir = t.Direction;
            Vector3 arm = Vector3.Cross(t.Position - centerOfMass, dir);

            float movement = 0f;
            float rotation = 0f;

            if (!ctx.DampenersEnabled)
            {
                // Dampeners off: player force input only, no rotation, no damping
                if (moveDir.LengthSquared() > 1e-8f)
                    movement = MathHelper.Clamp(Vector3.Dot(moveDir, dir), -1f, 1f);
            }
            else
            {
                // Step 1: movement from player input (per-thruster projection)
                if (moveDir.LengthSquared() > 1e-8f)
                    movement = MathHelper.Clamp(Vector3.Dot(moveDir, dir), -1f, 1f);

                // Step 2: rotation from player input (per-thruster projection)
                if (rotDir.LengthSquared() > 1e-8f)
                    rotation = MathHelper.Clamp(Vector3.Dot(rotDir, arm), -1f, 1f);

                // Step 3: angular damping — only when THIS thruster's rotation == 0
                if (rotation == 0f && ctx.AngularVelocity.Length() > AngularDampeningDeadzone)
                {
                    Vector3 dampAngular = -ctx.AngularVelocity;
                    dampAngular = DeadzoneFilter(dampAngular, AngularDampeningDeadzone);
                    rotation = MathHelper.Clamp(Vector3.Dot(dampAngular, arm), -1f, 1f);
                }

                // Step 4: linear damping — only when THIS thruster's movement == 0
                if (movement == 0f && ctx.Velocity.Length() > LinearDampeningDeadzone)
                {
                    Vector3 dampLinear = -ctx.Velocity;
                    dampLinear = DeadzoneFilter(dampLinear, LinearDampeningDeadzone);
                    // Normalize so velocity magnitude maps to [0,1] range like Bob's decomposed axes
                    float speed = dampLinear.Length();
                    if (speed > 1e-6f)
                    {
                        Vector3 dampDir = dampLinear / speed;
                        // Scale by speed but clamp — faster = more damping throttle
                        movement = MathHelper.Clamp(Vector3.Dot(dampDir, dir) * speed, -1f, 1f);
                    }
                }
            }

            // Step 5: blend rotation and movement via limiter
            float throttle;
            if (!ctx.DampenersEnabled)
            {
                throttle = MathHelper.Clamp(movement, 0f, 1f);
            }
            else
            {
                float combined = (rotation * RotationThrustLimiter) + (movement * (1f - RotationThrustLimiter));
                throttle = MathHelper.Clamp(combined, 0f, 1f);
            }

            throttles[i] = throttle;

            float thrust = throttle * t.MaxThrust;
            Vector3 forceEff = dir;
            Vector3 torqueEff = arm;
            producedForce += forceEff * thrust;
            producedTorque += torqueEff * thrust;
        }

        return new ThrustResult(throttles, producedForce, producedTorque);
    }

    /// <summary>
    /// Zero out vector components below deadzone threshold.
    /// Matches Bob's per-axis deadzone filtering.
    /// </summary>
    private static Vector3 DeadzoneFilter(Vector3 v, float deadzone)
    {
        return new Vector3(
            MathF.Abs(v.X) < deadzone ? 0f : v.X,
            MathF.Abs(v.Y) < deadzone ? 0f : v.Y,
            MathF.Abs(v.Z) < deadzone ? 0f : v.Z);
    }
}

/// <summary>
/// MathHelper utilities matching SE1 VRageMath.MathHelper behavior.
/// </summary>
internal static class MathHelper
{
    public static float Clamp(float value, float min, float max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }
}
