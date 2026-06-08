#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// Helicopter rotor — produces collective thrust along the rotor axis,
/// cyclic torque for orientation control, passive torque reaction,
/// ground effect thrust boost, and autorotation in unpowered descent.
///
/// Physics model:
///   Thrust:       T = ratedThrust * (rho div rho_ref) * collectivePitch * groundEffect * ETL
///   Reaction:     Q = -SpinSign * T * R * (0.07 div FOM)   (Newton's 3rd law)
///   Ground effect: T_ge = T * (1 + k div (16*(h div D)^2))  Cheeseman-Bennett, h &lt;= 2D
///   Autorotation: In unpowered descent through the disc, windmilling blades
///                 produce ~40-60% of normal hover thrust via momentum exchange.
///   Cyclic:       PD controller on pitch/roll error.
///   Yaw:          PD controller on yaw error.
///
/// Multi-rotor torque cancellation:
///   Each rotor has SpinSign (+1 = CCW, -1 = CW from above).
///   Opposite-spin rotors cancel reaction torque through the component sum.
/// </summary>
public class HelicopterRotor : IAeroBlockComponent
{
    // ═══════════════════════════════════════════════════════════════════
    //  Configuration
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Rotor disc axis in grid-local space (unit vector, typically Up).</summary>
    public Vector3 DiscAxis { get; }

    /// <summary>Rated thrust at sea-level density and full collective (N).</summary>
    public float RatedThrust { get; set; }

    /// <summary>Rotor disc radius (m). Affects torque reaction and ground effect height.</summary>
    public float DiscRadius { get; set; }

    /// <summary>
    /// Spin direction: +1 = CCW from above (standard), -1 = CW.
    /// Opposite-spin rotors cancel reaction torque.
    /// </summary>
    public float SpinSign { get; set; } = 1f;

    /// <summary>
    /// Figure of merit (0.6–1.0). Lower = more torque reaction per unit thrust.
    /// </summary>
    public float FigureOfMerit { get; set; } = 0.75f;

    /// <summary>Sea-level reference density (kg/m³).</summary>
    public float SeaLevelDensity { get; set; } = 1.225f;

    /// <summary>Maximum cyclic torque per axis (N·m).</summary>
    public float MaxCyclicTorque { get; set; } = 5_000_000f;

    /// <summary>Maximum yaw authority (N·m).</summary>
    public float MaxYawTorque { get; set; } = 2_000_000f;

    /// <summary>Cyclic attitude gain (N·m per radian).</summary>
    public float CyclicAttitudeGain { get; set; } = 1_000_000f;

    /// <summary>Cyclic damping gain (N·m per rad/s).</summary>
    public float CyclicDampingGain { get; set; } = 2_000_000f;

    /// <summary>Yaw attitude gain (N·m per radian).</summary>
    public float YawAttitudeGain { get; set; } = 500_000f;

    /// <summary>Yaw damping gain (N·m per rad/s).</summary>
    public float YawDampingGain { get; set; } = 1_500_000f;

    /// <summary>ETL thrust bonus at optimal speed (fraction, e.g. 0.12 = +12%).</summary>
    public float TranslationalLiftBonus { get; set; } = 0.12f;

    /// <summary>Forward speed (m/s) at which ETL peaks.</summary>
    public float TranslationalLiftSpeed { get; set; } = 30f;

    /// <summary>
    /// Autorotation efficiency: fraction of hover thrust recoverable in ideal
    /// unpowered descent. Real helicopters: 0.4–0.6 depending on disc loading.
    /// </summary>
    public float AutorotationEfficiency { get; set; } = 0.5f;

    // ═══════════════════════════════════════════════════════════════════
    //  IAeroBlockComponent
    // ═══════════════════════════════════════════════════════════════════

    public Vector3 Position { get; }
    public Vector3I BlockPosition { get; }

    // ═══════════════════════════════════════════════════════════════════
    //  Mutable inputs (set per-frame by AeroGridComponent)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Collective pitch: 0 = idle, 1 = full thrust.</summary>
    public float CollectivePitch { get; set; }

    /// <summary>Target orientation (world-space quaternion).</summary>
    public Quaternion TargetOrientation { get; set; } = Quaternion.Identity;

    /// <summary>Current grid orientation (world-space quaternion).</summary>
    public Quaternion CurrentOrientation { get; set; } = Quaternion.Identity;

    /// <summary>Angular velocity in grid-local space (rad/s).</summary>
    public Vector3 LocalAngularVelocity { get; set; }

    /// <summary>Whether orientation hold is active.</summary>
    public bool OrientationHoldActive { get; set; }

    // ═══════════════════════════════════════════════════════════════════
    //  Output state
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Current thrust magnitude (N), after all modifiers.</summary>
    public float CurrentThrust { get; private set; }

    /// <summary>Density scale factor.</summary>
    public float DensityScale { get; private set; }

    /// <summary>Translational lift factor (1.0 = no ETL bonus).</summary>
    public float TranslationalLiftFactor { get; private set; }

    /// <summary>Ground effect factor (1.0 = no bonus, >1 = boosted).</summary>
    public float GroundEffectFactor { get; private set; }

    /// <summary>Autorotation thrust (N). Non-zero only in unpowered descent.</summary>
    public float AutorotationThrust { get; private set; }

    /// <summary>Cyclic torque applied (grid-local N·m).</summary>
    public Vector3 CyclicTorque { get; private set; }

    /// <summary>Yaw torque from PD controller (N·m about disc axis).</summary>
    public float YawTorqueApplied { get; private set; }

    /// <summary>Passive torque reaction (N·m about disc axis).</summary>
    public float ReactionTorque { get; private set; }

    // ═══════════════════════════════════════════════════════════════════
    //  Constructor
    // ═══════════════════════════════════════════════════════════════════

    public HelicopterRotor(Vector3 position, Vector3I blockPosition,
        Vector3 discAxis, float ratedThrust, float discRadius,
        float spinSign = 1f)
    {
        Position = position;
        BlockPosition = blockPosition;
        DiscAxis = Vector3.Normalize(discAxis);
        RatedThrust = ratedThrust;
        DiscRadius = discRadius;
        SpinSign = spinSign;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Compute
    // ═══════════════════════════════════════════════════════════════════

    public ComponentForceResult Compute(in LocalAeroConditions conditions)
    {
        CurrentThrust = 0f;
        DensityScale = 0f;
        TranslationalLiftFactor = 1f;
        GroundEffectFactor = 1f;
        AutorotationThrust = 0f;
        CyclicTorque = Vector3.Zero;
        YawTorqueApplied = 0f;
        ReactionTorque = 0f;

        float rho = (float)conditions.Atmosphere.Density;
        if (rho < 1e-6f)
            return ComponentForceResult.Zero;

        DensityScale = rho / SeaLevelDensity;

        // ══════════════════════════════════════════════════════════════
        //  THRUST
        // ══════════════════════════════════════════════════════════════

        float thrust = RatedThrust * DensityScale * CollectivePitch;

        // ── Translational lift (ETL) ──
        if (conditions.Speed > 1f)
        {
            float speedRatio = conditions.Speed / TranslationalLiftSpeed;
            float etl = TranslationalLiftBonus * speedRatio * MathF.Exp(1f - speedRatio);
            TranslationalLiftFactor = 1f + etl;
            thrust *= TranslationalLiftFactor;
        }

        // ── Ground effect ──
        // Cheeseman-Bennett model: T_IGE/T_OGE = 1 / (1 - (R/4h)^2)
        // Simplified to avoid singularity: bonus = k / (16*(h/D)^2 + k)
        // where k controls the peak bonus magnitude.
        // At h=0 (on ground): ~25% boost. At h=2D: ~1.5% boost (negligible).
        float groundHeight = conditions.GroundHeight;
        if (groundHeight >= 0f)
        {
            float discDiameter = DiscRadius * 2f;
            float hOverD = groundHeight / discDiameter;

            if (hOverD < 2f) // only compute within 2 disc diameters
            {
                // k=0.25 gives ~25% boost at ground level, ~6% at h=D
                const float k = 0.25f;
                float denom = 16f * hOverD * hOverD + k;
                GroundEffectFactor = 1f + k / denom;
                thrust *= GroundEffectFactor;
            }
        }

        // ── Autorotation ──
        // When collective is low/zero and the grid is descending through the disc,
        // the upward airflow through the rotor disc keeps blades windmilling,
        // producing thrust from the kinetic energy of the descent.
        //
        // Conditions: low collective + descent velocity component along disc axis
        // The faster the descent, the more energy available — up to a limit.
        //
        // T_auto = ratedThrust * rho_scale * efficiency * descentFactor
        // descentFactor ramps from 0 at Vd=0 to 1.0 at ideal autorotation descent rate.
        // Ideal descent rate ≈ sqrt(T_hover / (2 * rho * A)) ≈ induced velocity * 1.7
        float autoThrust = 0f;
        if (CollectivePitch < 0.3f)
        {
            // Descent velocity: component of velocity along negative disc axis
            // (disc axis points up; descent = velocity has negative component along disc axis)
            float descentRate = -Vector3.Dot(conditions.Velocity, DiscAxis);

            if (descentRate > 1f) // must be descending through the disc
            {
                // Ideal autorotation descent rate (momentum theory)
                // v_ideal ≈ 1.7 * v_hover = 1.7 * sqrt(T / (2*rho*A))
                float discArea = MathF.PI * DiscRadius * DiscRadius;
                float hoverThrust = RatedThrust * DensityScale;
                float vHover = rho > 0.01f
                    ? MathF.Sqrt(hoverThrust / (2f * rho * discArea))
                    : 10f;
                float vIdeal = 1.7f * vHover;

                // Descent factor: ramps up to 1.0 at ideal rate, slight falloff beyond
                // (too fast = flow through disc becomes turbulent)
                float descentRatio = descentRate / MathF.Max(1f, vIdeal);
                float descentFactor;
                if (descentRatio <= 1f)
                {
                    // Ramp up: smooth curve from 0 to 1
                    descentFactor = descentRatio * descentRatio * (3f - 2f * descentRatio); // smoothstep
                }
                else
                {
                    // Beyond ideal: gradual falloff (turbulent wake)
                    float excess = descentRatio - 1f;
                    descentFactor = 1f / (1f + excess * excess);
                }

                // Scale with how little collective is applied (full collective = no autorotation)
                float collectiveAttenuation = 1f - CollectivePitch / 0.3f;

                autoThrust = hoverThrust * AutorotationEfficiency * descentFactor * collectiveAttenuation;

                // Ground effect also applies to autorotation thrust
                autoThrust *= GroundEffectFactor;

                AutorotationThrust = autoThrust;
            }
        }

        float totalThrust = thrust + autoThrust;
        CurrentThrust = totalThrust;
        Vector3 thrustForce = DiscAxis * totalThrust;

        // ══════════════════════════════════════════════════════════════
        //  TORQUE REACTION
        // ══════════════════════════════════════════════════════════════
        // Only the powered portion produces reaction torque.
        // Autorotation is driven by airflow, not the engine — no reaction on airframe.
        float torqueCoeff = 0.07f / FigureOfMerit;
        float reactionMag = thrust * DiscRadius * torqueCoeff; // powered thrust only
        ReactionTorque = -SpinSign * reactionMag;

        Vector3 torque = DiscAxis * ReactionTorque;

        // ══════════════════════════════════════════════════════════════
        //  CYCLIC + YAW
        // ══════════════════════════════════════════════════════════════
        if (OrientationHoldActive)
        {
            Quaternion errorQuat = Quaternion.Inverse(CurrentOrientation) * TargetOrientation;
            Vector3 eulerError = errorQuat.ConvertToEuler();

            float yawError = Vector3.Dot(eulerError, DiscAxis);
            Vector3 cyclicError = eulerError - DiscAxis * yawError;

            float yawRate = Vector3.Dot(LocalAngularVelocity, DiscAxis);
            Vector3 cyclicRate = LocalAngularVelocity - DiscAxis * yawRate;

            // Cyclic authority scales with collective + density
            // Autorotation gives partial cyclic authority (disc is still spinning)
            float effectiveCollective = CollectivePitch;
            if (autoThrust > 0f && RatedThrust > 0f)
                effectiveCollective = MathF.Max(effectiveCollective, 0.5f * autoThrust / (RatedThrust * DensityScale));

            float cyclicAuthority = MathF.Min(1f, effectiveCollective + 0.3f);
            cyclicAuthority *= MathF.Min(1f, DensityScale);

            const float maxEulerCmd = 0.5f;
            Vector3 clampedCyclicErr = new Vector3(
                Clamp(cyclicError.X, -maxEulerCmd, maxEulerCmd),
                Clamp(cyclicError.Y, -maxEulerCmd, maxEulerCmd),
                Clamp(cyclicError.Z, -maxEulerCmd, maxEulerCmd));

            Vector3 cyclicPD = clampedCyclicErr * CyclicAttitudeGain - cyclicRate * CyclicDampingGain;
            cyclicPD *= cyclicAuthority;

            cyclicPD = new Vector3(
                Clamp(cyclicPD.X, -MaxCyclicTorque, MaxCyclicTorque),
                Clamp(cyclicPD.Y, -MaxCyclicTorque, MaxCyclicTorque),
                Clamp(cyclicPD.Z, -MaxCyclicTorque, MaxCyclicTorque));

            float cyclicAlongDisc = Vector3.Dot(cyclicPD, DiscAxis);
            cyclicPD -= DiscAxis * cyclicAlongDisc;

            CyclicTorque = cyclicPD;
            torque += cyclicPD;

            // Yaw PD
            float yawCmd = Clamp(yawError, -maxEulerCmd, maxEulerCmd) * YawAttitudeGain
                         - yawRate * YawDampingGain;
            yawCmd *= MathF.Min(1f, DensityScale);
            yawCmd = Clamp(yawCmd, -MaxYawTorque, MaxYawTorque);

            YawTorqueApplied = yawCmd;
            torque += DiscAxis * yawCmd;
        }
        else
        {
            // Damping only — reaction torque still applies
            float yawRate = Vector3.Dot(LocalAngularVelocity, DiscAxis);
            Vector3 cyclicRate = LocalAngularVelocity - DiscAxis * yawRate;

            float dampAuth = MathF.Min(1f, DensityScale);
            Vector3 damping = -cyclicRate * CyclicDampingGain * 0.3f * dampAuth;
            float yawDamp = -yawRate * YawDampingGain * 0.3f * dampAuth;

            float dAlongDisc = Vector3.Dot(damping, DiscAxis);
            damping -= DiscAxis * dAlongDisc;

            torque += damping + DiscAxis * yawDamp;
        }

        return new ComponentForceResult(thrustForce, Position, torque);
    }

    private static float Clamp(float v, float min, float max)
        => MathF.Max(min, MathF.Min(max, v));
}
