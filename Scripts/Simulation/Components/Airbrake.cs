#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// Deployable drag plate — pure drag, no lift.
///
/// Physics:
///   cosAoA = dot(-flowDir, facingDir)      (how much plate faces oncoming air)
///   effectiveness = cosAoA × deployFraction
///   drag = CdDeployed × q × A × effectiveness
///   force = -flowDir × drag
/// </summary>
/// <summary>An airbrake block's live opening (the server's AeroAirbrakeComponent): each leaf's angle from closed (deg).</summary>
public interface IAirbrakeState
{
    float OpeningDeg { get; }
}

public class Airbrake : IAeroBlockComponent, IFaceOverride
{
    /// <summary>The block's opening (the 3x3x3 clamshell); null: DeployFraction as set by the grid.</summary>
    public IAirbrakeState State { get; init; }

    // ── Configuration ──

    /// <summary>Deployed plate normal direction, grid-local unit vector.</summary>
    public Vector3 FacingDirection { get; }

    /// <summary>Plate area in m².</summary>
    public float Area { get; }

    /// <summary>Flat plate drag coefficient when fully deployed.</summary>
    public float CdDeployed { get; set; } = 1.2f;

    // ── IAeroBlockComponent ──

    public Vector3 Position { get; }
    public Vector3I BlockPosition { get; }

    // ── IFaceOverride ──

    private readonly List<Vector3I> _ownedCells;
    public IReadOnlyList<Vector3I> OwnedCells => _ownedCells;

    // ── Mutable input ──

    /// <summary>Deploy fraction, 0 (retracted) to 1 (fully deployed).</summary>
    public float DeployFraction { get; set; }

    // ── Output state (readable after Compute) ──

    /// <summary>Drag force magnitude (N).</summary>
    public float DragForce { get; private set; }

    /// <summary>Overall effectiveness (cosAoA × deployFraction), 0..1.</summary>
    public float Effectiveness { get; private set; }

    /// <summary>Effective Cp for heatmap: CdDeployed × effectiveness.</summary>
    public float EffectiveCp { get; private set; }

    public Airbrake(Vector3 position, Vector3I blockPosition,
        Vector3 facingDirection, float area)
    {
        Position = position;
        BlockPosition = blockPosition;
        FacingDirection = Vector3.Normalize(facingDirection);
        Area = area;
        _ownedCells = new List<Vector3I> { blockPosition };
    }

    public ComponentForceResult Compute(in LocalAeroConditions conditions)
    {
        DragForce = 0;
        Effectiveness = 0;
        EffectiveCp = 0;

        if (conditions.Speed < 0.01f || conditions.Atmosphere.Density < 1e-8)
            return ComponentForceResult.Zero;

        // How much the plate faces oncoming air
        // (either side: FlowDirection is the motion's direction here, so the old sign - against the facing - made a
        //  forward-facing plate drag nothing in forward flight, 2026-10-15)
        float cosAoA = MathF.Abs(Vector3.Dot(conditions.FlowDirection, FacingDirection));

        if (cosAoA <= 0)
            return ComponentForceResult.Zero;

        float effectiveness = cosAoA * DeployFraction;
        float dragMag = CdDeployed * conditions.DynamicPressure * Area * effectiveness;

        Vector3 force = -conditions.FlowDirection * dragMag;

        DragForce = dragMag;
        Effectiveness = effectiveness;
        EffectiveCp = CdDeployed * effectiveness;

        return new ComponentForceResult(force, Position);
    }
}
