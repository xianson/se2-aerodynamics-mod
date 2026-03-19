#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// Ram air intake — captures airflow, producing intake drag and measurable mass flow.
///
/// Physics:
///   mdot = rho * v * A * cos(AoA)^n        (mass flow rate)
///   TotalPressure = Pstatic + q * eta       (with ram recovery)
///   Force = -mdot * v * flowDir             (intake drag = captured momentum)
/// </summary>
public class AirIntake : IAeroBlockComponent, IFaceOverride
{
    // ── Configuration ──

    /// <summary>Unit direction the intake faces (grid-local, normalized).</summary>
    public Vector3 FacingDirection { get; }

    /// <summary>Capture area in m².</summary>
    public float CaptureArea { get; }

    /// <summary>AoA exponent for off-axis flow attenuation. Higher = narrower capture cone.</summary>
    public float AoaExponent { get; set; } = 2f;

    /// <summary>Maximum half-angle (degrees) beyond which no air is captured.</summary>
    public float MaxHalfAngle { get; set; } = 60f;

    /// <summary>Ram pressure recovery factor (0–1). 1 = perfect isentropic recovery.</summary>
    public float RamRecovery { get; set; } = 0.9f;

    // ── IAeroBlockComponent ──

    public Vector3 Position { get; }
    public Vector3I BlockPosition { get; }

    // ── IFaceOverride ──

    private readonly List<Vector3I> _ownedCells;
    public IReadOnlyList<Vector3I> OwnedCells => _ownedCells;

    // ── Output state (readable after Compute) ──

    /// <summary>Mass flow rate through the intake (kg/s).</summary>
    public float MassFlowRate { get; private set; }

    /// <summary>Total pressure at intake face (Pa).</summary>
    public float TotalPressure { get; private set; }

    /// <summary>Ram pressure ratio: (Pstatic + q*eta) / Pstatic.</summary>
    public float RamPressureRatio { get; private set; }

    /// <summary>Effective Cp for heatmap: cosAoA^n (momentum capture coefficient).</summary>
    public float EffectiveCp { get; private set; }

    // ── Precomputed ──
    private readonly float _cosMaxHalf;

    public AirIntake(Vector3 position, Vector3I blockPosition, Vector3 facingDirection, float captureArea)
    {
        Position = position;
        BlockPosition = blockPosition;
        FacingDirection = Vector3.Normalize(facingDirection);
        CaptureArea = captureArea;
        _ownedCells = new List<Vector3I> { blockPosition };
        _cosMaxHalf = MathF.Cos(60f * MathF.PI / 180f);
    }

    public ComponentForceResult Compute(in LocalAeroConditions conditions)
    {
        MassFlowRate = 0;
        TotalPressure = 0;
        RamPressureRatio = 0;
        EffectiveCp = 0;

        if (conditions.Speed < 0.01f || conditions.Atmosphere.Density < 1e-8)
            return ComponentForceResult.Zero;

        // Angle between intake facing and incoming flow
        // Intake captures air flowing INTO it: dot(facing, -flowDir)
        // i.e., air moving opposite to facing direction enters the intake
        float cosAoA = Vector3.Dot(FacingDirection, -conditions.FlowDirection);

        // Recompute cos limit in case MaxHalfAngle was changed
        float cosLimit = MathF.Cos(MaxHalfAngle * MathF.PI / 180f);

        if (cosAoA <= cosLimit)
            return ComponentForceResult.Zero;

        float rho = (float)conditions.Atmosphere.Density;
        float v = conditions.Speed;

        // Mass flow rate: rho * v * A * cos(AoA)^n
        float cosPow = MathF.Pow(cosAoA, AoaExponent);
        float mdot = rho * v * CaptureArea * cosPow;
        MassFlowRate = mdot;
        EffectiveCp = cosPow; // cosAoA^n

        // Total pressure: Pstatic + q * eta * recovery
        float pStatic = (float)conditions.Atmosphere.Pressure;
        float eta = cosPow; // efficiency follows same AoA falloff
        TotalPressure = pStatic + conditions.DynamicPressure * eta * RamRecovery;
        RamPressureRatio = pStatic > 0 ? TotalPressure / pStatic : 0;

        // Intake drag: captured air momentum = mdot * v in flow direction
        Vector3 force = -mdot * v * conditions.FlowDirection;

        return new ComponentForceResult(force, Position);
    }
}
