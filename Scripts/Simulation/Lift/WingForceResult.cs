#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace AeroMod;

/// <summary>
/// Per-wing force output from the lift model.
/// </summary>
public readonly struct WingForceResult
{
    /// <summary>Lift force perpendicular to flow (Newtons).</summary>
    public readonly Vector3 LiftForce;

    /// <summary>The wing's whole drag force along the flow (N): drag due to lift, profile, separation and wave drag.</summary>
    public readonly Vector3 InducedDrag;

    /// <summary>Where forces act (aero center, grid-local meters).</summary>
    public readonly Vector3 ApplicationPoint;

    /// <summary>Lift coefficient.</summary>
    public readonly float CL;

    /// <summary>Drag-due-to-lift coefficient: CL^2/(pi e AR), toward CL^2/CLa as the leading-edge suction goes in
    /// supersonic flight (CompressibleWingModel.SupersonicLiftDrag, 2026-10-18).</summary>
    public readonly float CDi;

    /// <summary>Effective angle of attack after interference (radians).</summary>
    public readonly float AlphaEffective;

    /// <summary>Interference reduction factor (1.0 = solo wing).</summary>
    public readonly float Efficiency;

    /// <summary>Which chord edge led this tick (2026-10-18, fix a-wing-edge): +1 the high end of the wing's ChordAxis
    /// (it moved toward +ChordAxis), -1 the low end, between: blended (near-spanwise flow - CompressibleWingModel.LeadBlend);
    /// 0 also when not computed.</summary>
    public readonly float Lead;

    /// <summary>The wing's own wave drag coefficient this tick (2026-10-18, fix a-wing-wave: thickness and sweep, less the
    /// hull's charge on its edge faces - CompressibleWingModel.WingWaveDrag); in InducedDrag's force with CDi and the rest.</summary>
    public readonly float CDw;

    public WingForceResult(Vector3 liftForce, Vector3 inducedDrag, Vector3 applicationPoint,
        float cl, float cdi, float alphaEffective, float efficiency, float lead = 0f, float cdw = 0f)
    {
        CDw = cdw;
        Lead = lead;
        LiftForce = liftForce;
        InducedDrag = inducedDrag;
        ApplicationPoint = applicationPoint;
        CL = cl;
        CDi = cdi;
        AlphaEffective = alphaEffective;
        Efficiency = efficiency;
    }
}
