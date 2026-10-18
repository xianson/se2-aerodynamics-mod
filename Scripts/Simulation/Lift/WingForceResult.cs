#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace AeroMod;

/// <summary>
/// Per-wing force output from the lift model.
/// </summary>
public readonly struct WingForceResult
{
    /// <summary>Lift force perpendicular to flow (Newtons).</summary>
    public readonly Vector3 LiftForce;

    /// <summary>Induced drag force along flow direction (Newtons).</summary>
    public readonly Vector3 InducedDrag;

    /// <summary>Where forces act (aero center, grid-local meters).</summary>
    public readonly Vector3 ApplicationPoint;

    /// <summary>Lift coefficient.</summary>
    public readonly float CL;

    /// <summary>Induced drag coefficient.</summary>
    public readonly float CDi;

    /// <summary>Effective angle of attack after interference (radians).</summary>
    public readonly float AlphaEffective;

    /// <summary>Interference reduction factor (1.0 = solo wing).</summary>
    public readonly float Efficiency;

    /// <summary>Which chord edge led this tick (2026-10-18, fix a-wing-edge): +1 the high end of the wing's ChordAxis
    /// (it moved toward +ChordAxis), -1 the low end, between: blended (near-spanwise flow - CompressibleWingModel.LeadBlend);
    /// 0 also when not computed.</summary>
    public readonly float Lead;

    public WingForceResult(Vector3 liftForce, Vector3 inducedDrag, Vector3 applicationPoint,
        float cl, float cdi, float alphaEffective, float efficiency, float lead = 0f)
    {
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
