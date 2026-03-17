#pragma warning disable
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

    public WingForceResult(Vector3 liftForce, Vector3 inducedDrag, Vector3 applicationPoint,
        float cl, float cdi, float alphaEffective, float efficiency)
    {
        LiftForce = liftForce;
        InducedDrag = inducedDrag;
        ApplicationPoint = applicationPoint;
        CL = cl;
        CDi = cdi;
        AlphaEffective = alphaEffective;
        Efficiency = efficiency;
    }
}
