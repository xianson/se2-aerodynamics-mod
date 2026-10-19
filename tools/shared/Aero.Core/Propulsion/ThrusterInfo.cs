using System.Numerics;

namespace Aero.Core.Propulsion;

/// <summary>
/// Describes a single thruster mounted on a grid.
/// Position and direction are in body-frame (grid-local) coordinates.
/// </summary>
public struct ThrusterInfo
{
    /// <summary>Thruster position in body-frame meters (from grid origin).</summary>
    public Vector3 Position;

    /// <summary>Unit vector: direction the thrust force pushes the ship (body-frame).</summary>
    public Vector3 Direction;

    /// <summary>Maximum thrust output in Newtons.</summary>
    public float MaxThrust;

    public ThrusterInfo(Vector3 position, Vector3 direction, float maxThrust)
    {
        Position = position;
        Direction = Vector3.Normalize(direction);
        MaxThrust = maxThrust;
    }

    public override string ToString()
        => $"pos=({Position.X:F1},{Position.Y:F1},{Position.Z:F1}) dir=({Direction.X:F2},{Direction.Y:F2},{Direction.Z:F2}) Tmax={MaxThrust:F0}N";
}
