#pragma warning disable
namespace AeroMod;

/// <summary>
/// Force output from a single aero block component.
/// </summary>
public readonly struct ComponentForceResult
{
    /// <summary>Force in body-frame Newtons.</summary>
    public readonly Vector3 Force;

    /// <summary>Application point in grid-local meters.</summary>
    public readonly Vector3 ApplicationPoint;

    public ComponentForceResult(Vector3 force, Vector3 applicationPoint)
    {
        Force = force;
        ApplicationPoint = applicationPoint;
    }

    /// <summary>Compute torque about a given center of mass.</summary>
    public Vector3 TorqueAbout(Vector3 com)
        => Vector3.Cross(ApplicationPoint - com, Force);

    /// <summary>Zero force result.</summary>
    public static readonly ComponentForceResult Zero = new(Vector3.Zero, Vector3.Zero);
}
