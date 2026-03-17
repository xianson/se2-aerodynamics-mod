#pragma warning disable
namespace AeroMod;

/// <summary>
/// Result of an aerodynamic force computation.
/// </summary>
public readonly struct AeroResult
{
    /// <summary>Total aerodynamic force in Newtons (world frame).</summary>
    public readonly Vector3 Force;

    /// <summary>Total torque about center of mass in N·m (world frame).</summary>
    public readonly Vector3 Torque;

    /// <summary>Drag magnitude (force component opposing velocity), Newtons.</summary>
    public readonly float DragMagnitude;

    /// <summary>Lift magnitude (force component perpendicular to velocity), Newtons.</summary>
    public readonly float LiftMagnitude;

    /// <summary>Total projected frontal area in m².</summary>
    public readonly float FrontalArea;

    /// <summary>Mach number at this condition.</summary>
    public readonly double Mach;

    /// <summary>Dynamic pressure q = ½ρv² in Pascals.</summary>
    public readonly double DynamicPressure;

    public AeroResult(Vector3 force, Vector3 torque, float dragMag, float liftMag,
        float frontalArea, double mach, double dynamicPressure)
    {
        Force = force;
        Torque = torque;
        DragMagnitude = dragMag;
        LiftMagnitude = liftMag;
        FrontalArea = frontalArea;
        Mach = mach;
        DynamicPressure = dynamicPressure;
    }

    public override string ToString() =>
        $"D={DragMagnitude:F1}N L={LiftMagnitude:F1}N A={FrontalArea:F1}m² M={Mach:F2}";
}
