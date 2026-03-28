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

    /// <summary>
    /// Pure torque in body-frame N·m, added directly (not derived from offset).
    /// Used by components that produce torque independently of thrust position
    /// (e.g. helicopter rotor cyclic, reaction wheels).
    /// </summary>
    public readonly Vector3 DirectTorque;

    public ComponentForceResult(Vector3 force, Vector3 applicationPoint)
    {
        Force = force;
        ApplicationPoint = applicationPoint;
        DirectTorque = Vector3.Zero;
    }

    public ComponentForceResult(Vector3 force, Vector3 applicationPoint, Vector3 directTorque)
    {
        Force = force;
        ApplicationPoint = applicationPoint;
        DirectTorque = directTorque;
    }

    /// <summary>Compute torque about a given center of mass (offset + direct).</summary>
    public Vector3 TorqueAbout(Vector3 com)
        => Vector3.Cross(ApplicationPoint - com, Force) + DirectTorque;

    /// <summary>Zero force result.</summary>
    public static readonly ComponentForceResult Zero = new(Vector3.Zero, Vector3.Zero);
}
