using System.Numerics;

namespace Aero.Core.Propulsion;

/// <summary>
/// Two-phase thrust allocator with projection-weighted throttle assignment.
///
/// Dampener OFF behavior:
///   Pure translation only — thrusters respond to player inputForce at full throttle range.
///   No rate nulling, no attitude correction. Coupling torque from offset thrusters is
///   an uncorrected side-effect.
///
/// Dampener ON behavior (two-phase):
///   Phase 1 — Translation: allocate up to (1 - AttitudeFraction) for force demand.
///             Linear damping is fully suppressed per-axis when player input is present.
///   Phase 2 — Attitude: use remaining capacity for torque demand, correcting coupling
///             from Phase 1. Angular damping is always active and can oppose player
///             torque input (they "fight", potentially resulting in zero net command).
/// </summary>
public class TwoPhaseAllocator : ThrustAllocatorBase
{
    public override string Name => "TwoPhase";
    public override string Description => "Two-phase (translate then attitude) with projection-weighted throttles.";

    /// <summary>Fraction of each thruster reserved for attitude control (0..1). Default 0.5.</summary>
    public float AttitudeFraction { get; set; } = 0.5f;

    /// <summary>
    /// Linear damping gain. Damping force = -velocity * mass * LinearDampingGain.
    /// Higher = more aggressive stopping. Typical: 1..5.
    /// </summary>
    public float LinearDampingGain { get; set; } = 2f;

    /// <summary>
    /// Angular damping gain. Damping torque = -omega_i * I_ii * AngularDampingGain.
    /// </summary>
    public float AngularDampingGain { get; set; } = 3f;

    /// <summary>Total mass (kg). Set before calling Allocate.</summary>
    public float Mass { get; set; } = 1000f;

    /// <summary>Diagonal inertia (Ixx, Iyy, Izz). Set before calling Allocate.</summary>
    public Vector3 Inertia { get; set; } = new(500, 500, 500);

    public override ThrustResult Allocate(ThrusterInfo[] thrusters, Vector3 centerOfMass, in ThrustContext ctx)
    {
        int n = thrusters.Length;
        if (n == 0) return ThrustResult.Empty;

        // Precompute effectiveness vectors
        var forceEff = new Vector3[n];
        var torqueEff = new Vector3[n];
        var throttles = new float[n];

        for (int i = 0; i < n; i++)
        {
            forceEff[i] = thrusters[i].Direction;
            torqueEff[i] = Vector3.Cross(thrusters[i].Position - centerOfMass, thrusters[i].Direction);
        }

        if (!ctx.DampenersEnabled)
        {
            if (ctx.InputForce.LengthSquared() > 1e-8f)
                AllocateForObjective(thrusters, throttles, ctx.InputForce, forceEff, 1f);
        }
        else
        {
            Vector3 desiredForce = ctx.InputForce;
            float dampScale = Mass * LinearDampingGain;
            Vector3 rawDamp = ctx.Velocity * dampScale;
            desiredForce -= new Vector3(
                BlendDamping(ctx.InputForce.X, rawDamp.X),
                BlendDamping(ctx.InputForce.Y, rawDamp.Y),
                BlendDamping(ctx.InputForce.Z, rawDamp.Z));

            Vector3 desiredTorque = ctx.InputTorque - new Vector3(
                ctx.AngularVelocity.X * Inertia.X * AngularDampingGain,
                ctx.AngularVelocity.Y * Inertia.Y * AngularDampingGain,
                ctx.AngularVelocity.Z * Inertia.Z * AngularDampingGain);

            float transCeiling = 1f - AttitudeFraction;

            if (desiredForce.LengthSquared() > 1e-8f)
                AllocateForObjective(thrusters, throttles, desiredForce, forceEff, transCeiling);

            Vector3 translationTorque = Vector3.Zero;
            for (int i = 0; i < n; i++)
            {
                float thrust = throttles[i] * thrusters[i].MaxThrust;
                translationTorque += torqueEff[i] * thrust;
            }

            Vector3 attitudeDemand = desiredTorque - translationTorque;
            if (attitudeDemand.LengthSquared() > 1e-8f)
                AllocateForObjective(thrusters, throttles, attitudeDemand, torqueEff, 1f);
        }

        // Compute produced totals
        Vector3 producedForce = Vector3.Zero;
        Vector3 producedTorque = Vector3.Zero;
        for (int i = 0; i < n; i++)
        {
            float thrust = throttles[i] * thrusters[i].MaxThrust;
            producedForce += forceEff[i] * thrust;
            producedTorque += torqueEff[i] * thrust;
        }

        return new ThrustResult(throttles, producedForce, producedTorque);
    }

    private static float BlendDamping(float input, float damp)
    {
        if (MathF.Abs(input) > 1e-8f) return 0f;
        return damp;
    }

    private static void AllocateForObjective(ThrusterInfo[] thrusters, float[] throttles,
        Vector3 desired, Vector3[] effectiveness, float throttleCeiling)
    {
        int n = thrusters.Length;
        float desiredMag = desired.Length();
        if (desiredMag < 1e-6f) return;
        Vector3 desiredDir = desired / desiredMag;

        Span<float> proj = n <= 64 ? stackalloc float[n] : new float[n];
        float maxProj = 0f;

        for (int i = 0; i < n; i++)
        {
            float available = throttleCeiling - throttles[i];
            if (available <= 1e-6f) { proj[i] = 0f; continue; }

            float p = Vector3.Dot(effectiveness[i], desiredDir);
            if (p <= 0f) { proj[i] = 0f; continue; }

            proj[i] = p;
            if (p > maxProj) maxProj = p;
        }

        if (maxProj < 1e-6f) return;

        float maxAchievable = 0f;
        for (int i = 0; i < n; i++)
        {
            if (proj[i] <= 0f) continue;
            float available = throttleCeiling - throttles[i];
            float w = proj[i] / maxProj;
            maxAchievable += w * available * thrusters[i].MaxThrust * proj[i];
        }

        if (maxAchievable < 1e-6f) return;

        float scale = MathF.Min(desiredMag / maxAchievable, 1f);

        for (int i = 0; i < n; i++)
        {
            if (proj[i] <= 0f) continue;
            float available = throttleCeiling - throttles[i];
            float w = proj[i] / maxProj;
            throttles[i] += scale * w * available;
        }
    }
}
