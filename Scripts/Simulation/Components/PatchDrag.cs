#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace AeroMod;

/// <summary>One drag patch's force (AeroApi.SetDrag; pure): q at the point x CdA, against the point's travel - for a flat
/// plate (normal not zero) scaled by how squarely the air meets it.</summary>
public static class PatchDrag
{
    /// <param name="pointVelocity">the point's velocity through the air (grid-local m/s)</param>
    public static Vector3 Force(Vector3 pointVelocity, float density, float cdA, Vector3 normal)
    {
        float s2 = pointVelocity.LengthSquared();
        if (s2 < 1e-4f || !(density > 0f) || !(cdA > 0f)) return Vector3.Zero;
        float s = System.MathF.Sqrt(s2);
        Vector3 dir = pointVelocity / s;
        float facing = normal == Vector3.Zero ? 1f : System.MathF.Abs(Vector3.Dot(normal, dir));
        return -dir * (0.5f * density * s2 * cdA * facing);
    }
}
