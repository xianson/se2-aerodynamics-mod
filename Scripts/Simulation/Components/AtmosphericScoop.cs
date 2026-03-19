#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// Atmospheric resource collector — scoops gas proportional to mass flow rate.
///
/// Physics:
///   cosN = cos(AoA)^n                      (AoA attenuation)
///   mdot = rho × v × A × cosN             (mass flow rate)
///   collection = mdot × efficiency         (collected mass rate)
///   drag = CdScoop × q × A × cosN         (intake drag)
///   force = -flowDir × drag
/// </summary>
public class AtmosphericScoop : IAeroBlockComponent, IFaceOverride
{
    // ── Configuration ──

    /// <summary>Scoop opening normal direction, grid-local unit vector.</summary>
    public Vector3 FacingDirection { get; }

    /// <summary>Scoop opening area in m².</summary>
    public float ScoopArea { get; }

    /// <summary>Collection efficiency, 0..1.</summary>
    public float CollectionEfficiency { get; set; } = 0.7f;

    /// <summary>AoA attenuation exponent. Higher = narrower collection cone.</summary>
    public float AoaExponent { get; set; } = 2f;

    /// <summary>Scoop drag coefficient.</summary>
    public float CdScoop { get; set; } = 0.8f;

    // ── IAeroBlockComponent ──

    public Vector3 Position { get; }
    public Vector3I BlockPosition { get; }

    // ── IFaceOverride ──

    private readonly List<Vector3I> _ownedCells;
    public IReadOnlyList<Vector3I> OwnedCells => _ownedCells;

    // ── Output state (readable after Compute) ──

    /// <summary>Total air mass flow rate (kg/s).</summary>
    public float MassFlowRate { get; private set; }

    /// <summary>Collected mass rate (kg/s) = mdot × efficiency.</summary>
    public float CollectionRate { get; private set; }

    /// <summary>Cumulative collected mass (kg). Double for precision over long flights.</summary>
    public double CumulativeCollected { get; private set; }

    /// <summary>Drag force magnitude (N).</summary>
    public float DragForce { get; private set; }

    /// <summary>Effective Cp for heatmap: CdScoop × cosAoA^n.</summary>
    public float EffectiveCp { get; private set; }

    public AtmosphericScoop(Vector3 position, Vector3I blockPosition,
        Vector3 facingDirection, float scoopArea)
    {
        Position = position;
        BlockPosition = blockPosition;
        FacingDirection = Vector3.Normalize(facingDirection);
        ScoopArea = scoopArea;
        _ownedCells = new List<Vector3I> { blockPosition };
    }

    public ComponentForceResult Compute(in LocalAeroConditions conditions)
    {
        MassFlowRate = 0;
        CollectionRate = 0;
        DragForce = 0;
        EffectiveCp = 0;

        if (conditions.Speed < 0.01f || conditions.Atmosphere.Density < 1e-8)
            return ComponentForceResult.Zero;

        // How much the scoop faces oncoming air
        float cosAoA = Vector3.Dot(-conditions.FlowDirection, FacingDirection);

        if (cosAoA <= 0)
            return ComponentForceResult.Zero;

        float rho = (float)conditions.Atmosphere.Density;
        float v = conditions.Speed;

        // AoA-attenuated factor
        float cosN = MathF.Pow(cosAoA, AoaExponent);

        // Mass flow rate
        float mdot = rho * v * ScoopArea * cosN;
        MassFlowRate = mdot;

        // Collection
        float collection = mdot * CollectionEfficiency;
        CollectionRate = collection;
        CumulativeCollected += collection * AeroConfig.Dt;

        // Drag
        float dragMag = CdScoop * conditions.DynamicPressure * ScoopArea * cosN;
        DragForce = dragMag;
        EffectiveCp = CdScoop * cosN;

        Vector3 force = -conditions.FlowDirection * dragMag;

        return new ComponentForceResult(force, Position);
    }

    /// <summary>Reset the cumulative collection counter.</summary>
    public void ResetCumulative() => CumulativeCollected = 0;
}
