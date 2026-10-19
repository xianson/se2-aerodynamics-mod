using System.Numerics;

namespace Aero.Core.Sim;

/// <summary>
/// Full 6DOF rigid body state: position, orientation, linear and angular velocity.
/// 13 scalar DOFs: pos(3) + quat(4) + linVel(3) + angVel(3).
/// </summary>
public struct RigidBodyState
{
    /// <summary>Center of mass position in world frame (meters).</summary>
    public Vector3 Position;

    /// <summary>Orientation quaternion (body-to-world rotation).</summary>
    public Quaternion Orientation;

    /// <summary>Linear velocity of CoM in world frame (m/s).</summary>
    public Vector3 LinearVelocity;

    /// <summary>Angular velocity in body frame (rad/s). Euler equations keep ω in body frame.</summary>
    public Vector3 AngularVelocity;

    /// <summary>Speed magnitude (m/s).</summary>
    public readonly float Speed => LinearVelocity.Length();

    /// <summary>Angular speed magnitude (rad/s).</summary>
    public readonly float AngularSpeed => AngularVelocity.Length();

    /// <summary>Transform a direction from body frame to world frame.</summary>
    public readonly Vector3 BodyToWorld(Vector3 bodyDir)
        => Vector3.Transform(bodyDir, Orientation);

    /// <summary>Transform a direction from world frame to body frame.</summary>
    public readonly Vector3 WorldToBody(Vector3 worldDir)
        => Vector3.Transform(worldDir, Quaternion.Conjugate(Orientation));

    /// <summary>World-space velocity at a point offset from CoM (in world frame).</summary>
    public readonly Vector3 VelocityAtPoint(Vector3 worldOffset)
    {
        Vector3 worldOmega = Vector3.Transform(AngularVelocity, Orientation);
        return LinearVelocity + Vector3.Cross(worldOmega, worldOffset);
    }

    public override readonly string ToString()
        => $"pos=({Position.X:F1},{Position.Y:F1},{Position.Z:F1}) " +
           $"v={Speed:F1}m/s ω={AngularSpeed:F2}rad/s";
}
