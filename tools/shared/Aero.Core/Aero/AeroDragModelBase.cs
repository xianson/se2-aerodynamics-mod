using System.Numerics;
using Aero.Core.Atmosphere;
using Aero.Core.Grids;

namespace Aero.Core.Aero;

/// <summary>
/// Everything a drag model might need to compute forces.
/// Models take what they need and ignore the rest.
/// </summary>
public readonly struct AeroContext
{
    /// <summary>The block grid (for models that iterate blocks directly).</summary>
    public readonly Grid Grid;

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

    /// <summary>
    /// Height above ground in meters (-1 if unknown/no ground).
    /// Used for ground effect: reduces induced drag when close to surface.
    /// </summary>
    public readonly float GroundHeight;

    public AeroContext(Grid grid, ISurfaceProvider surfaceCache, Vector3 velocity,
        AtmosphereState atmosphere, Vector3 centerOfMass, float blockSize = 2.5f,
        Vector3 angularVelocity = default, float groundHeight = -1f)
    {
        Grid = grid;
        SurfaceCache = surfaceCache;
        Velocity = velocity;
        AngularVelocity = angularVelocity;
        Atmosphere = atmosphere;
        CenterOfMass = centerOfMass;
        BlockSize = blockSize;
        GroundHeight = groundHeight;
    }

    /// <summary>Compute velocity at a point offset from CoM, accounting for rotation.</summary>
    public Vector3 VelocityAtPoint(Vector3 point)
        => Velocity + Vector3.Cross(AngularVelocity, point - CenterOfMass);

    public float Speed => Velocity.Length();
}

/// <summary>
/// Abstract base for all aerodynamic drag models.
/// Subclasses implement Compute() with whatever physics they want —
/// the benchmark and game integration treat them uniformly.
/// </summary>
public abstract class AeroDragModelBase
{
    /// <summary>Human-readable model name (for reports).</summary>
    public abstract string Name { get; }

    /// <summary>Short description of the approach.</summary>
    public abstract string Description { get; }

    /// <summary>Compute aerodynamic forces and torques for the given context.</summary>
    public abstract AeroResult Compute(in AeroContext ctx);
}
