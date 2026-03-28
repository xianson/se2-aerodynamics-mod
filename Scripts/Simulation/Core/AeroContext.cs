#pragma warning disable
namespace AeroMod;

/// <summary>
/// Everything a drag model might need to compute forces.
/// Models take what they need and ignore the rest.
/// </summary>
public readonly struct AeroContext
{
    /// <summary>Grid cell accessor (for models that need grid queries).</summary>
    public readonly IGridAccessor GridAccessor;

    /// <summary>Pre-computed exposed surface faces (for panel-method models).</summary>
    public readonly ISurfaceProvider SurfaceCache;

    /// <summary>Grid linear velocity at CoM in body/grid-local frame (m/s).</summary>
    public readonly Vector3 Velocity;

    /// <summary>Grid angular velocity in body frame (rad/s).</summary>
    public readonly Vector3 AngularVelocity;

    /// <summary>Atmospheric properties at the grid's location.</summary>
    public readonly AtmosphereState Atmosphere;

    /// <summary>Center of mass in grid-local frame (m).</summary>
    public readonly Vector3 CenterOfMass;

    /// <summary>Block size in meters (2.5 for SE2 large grid).</summary>
    public readonly float BlockSize;

    /// <summary>Pre-computed speed (magnitude of Velocity).</summary>
    public readonly float Speed;

    /// <summary>
    /// Height above ground in meters (-1 if unknown/no ground).
    /// Used for ground effect: reduces induced drag when close to surface.
    /// </summary>
    public readonly float GroundHeight;

    /// <summary>
    /// Hull/cavity face classifier (null if not available).
    /// When set, drag models skip faces where IsHull(i) == false.
    /// </summary>
    public readonly ManifoldClassifier Manifold;

    public AeroContext(IGridAccessor gridAccessor, ISurfaceProvider surfaceCache, Vector3 velocity,
        AtmosphereState atmosphere, Vector3 centerOfMass, float blockSize = 2.5f,
        Vector3 angularVelocity = default, float groundHeight = -1f,
        ManifoldClassifier manifold = null)
    {
        GridAccessor = gridAccessor;
        SurfaceCache = surfaceCache;
        Velocity = velocity;
        AngularVelocity = angularVelocity;
        Atmosphere = atmosphere;
        CenterOfMass = centerOfMass;
        BlockSize = blockSize;
        Speed = velocity.Length();
        GroundHeight = groundHeight;
        Manifold = manifold;
    }

    /// <summary>Compute velocity at a point offset from CoM, accounting for rotation.</summary>
    public Vector3 VelocityAtPoint(Vector3 point)
        => Velocity + Vector3.Cross(AngularVelocity, point - CenterOfMass);
}
