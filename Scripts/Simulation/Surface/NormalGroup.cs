#pragma warning disable
namespace AeroMod;

/// <summary>
/// Faces grouped by normal direction for O(K) per-frame force computation.
/// </summary>
public readonly struct NormalGroup
{
    public readonly Vector3 Normal;
    public readonly float TotalArea;

    /// <summary>Area-weighted centroid of all faces in this group (meters from grid origin).</summary>
    public readonly Vector3 Centroid;

    public readonly int FaceCount;

    /// <summary>
    /// Area-weighted second moment of face positions about the centroid (m⁴).
    /// Symmetric 3×3 tensor: S = Σ A_i · (r_i - centroid) ⊗ (r_i - centroid).
    /// Used for O(K) pressure gradient torque correction.
    /// </summary>
    public readonly float Sxx, Syy, Szz, Sxy, Sxz, Syz;

    public NormalGroup(Vector3 normal, float totalArea, Vector3 centroid, int faceCount)
    {
        Normal = normal;
        TotalArea = totalArea;
        Centroid = centroid;
        FaceCount = faceCount;
    }

    public NormalGroup(Vector3 normal, float totalArea, Vector3 centroid, int faceCount,
        float sxx, float syy, float szz, float sxy, float sxz, float syz)
    {
        Normal = normal;
        TotalArea = totalArea;
        Centroid = centroid;
        FaceCount = faceCount;
        Sxx = sxx; Syy = syy; Szz = szz;
        Sxy = sxy; Sxz = sxz; Syz = syz;
    }

    /// <summary>S · v (matrix-vector multiply with the second moment tensor).</summary>
    public Vector3 SecondMomentMul(Vector3 v) => new(
        Sxx * v.X + Sxy * v.Y + Sxz * v.Z,
        Sxy * v.X + Syy * v.Y + Syz * v.Z,
        Sxz * v.X + Syz * v.Y + Szz * v.Z);

    /// <summary>Whether this group has precomputed second moments.</summary>
    public bool HasSecondMoment => Sxx != 0 || Syy != 0 || Szz != 0;
}
