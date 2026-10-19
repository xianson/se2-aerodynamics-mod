using System.Numerics;

namespace Aero.Core.Math;

/// <summary>
/// Quaternion utilities matching VRage's Quaternion extensions.
/// </summary>
public static class QuaternionHelpers
{
    /// <summary>
    /// Convert a quaternion to Euler angles (radians).
    /// Matches VRage's Quaternion.ConvertToEuler() exactly:
    ///   X = pitch, Y = yaw, Z = roll.
    /// </summary>
    public static Vector3 ConvertToEuler(this Quaternion q)
    {
        float y = 2f * (q.W * q.Z + q.X * q.Y);
        float x = 1f - 2f * (q.Z * q.Z + q.X * q.X);
        float roll = MathF.Atan2(y, x);

        float y2 = 2f * (q.W * q.Y + q.Z * q.X);
        float x2 = 1f - 2f * (q.X * q.X + q.Y * q.Y);
        float yaw = MathF.Atan2(y2, x2);

        float y3 = MathF.Sqrt(1f + 2f * (q.W * q.X - q.Z * q.Y));
        float x3 = MathF.Sqrt(1f - 2f * (q.W * q.X - q.Z * q.Y));
        float pitch = 2f * MathF.Atan2(y3, x3) - MathF.PI / 2f;

        return new Vector3(pitch, yaw, roll);
    }
}
