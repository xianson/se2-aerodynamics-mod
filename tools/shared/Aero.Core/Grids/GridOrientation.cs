using Aero.Core.Blueprint;
using Aero.Core.Math;

namespace Aero.Core.Grids;

/// <summary>
/// Utility for converting SE2 block orientation quaternions to grid-aligned
/// axis remappings. SE2 only uses 24 possible orientations (90° increments).
/// </summary>
public static class GridOrientation
{
    /// <summary>
    /// Given a block's cell dimensions and its orientation quaternion,
    /// compute the min/max cell offsets relative to the block's anchor position.
    /// The block in local space occupies (0,0,0) to (cx-1, cy-1, cz-1).
    /// After rotation, it may extend in negative directions.
    /// </summary>
    public static (Vector3I min, Vector3I max) GetRotatedExtent(
        int cx, int cy, int cz, QuaternionD q)
    {
        // Build rotation matrix from quaternion
        double xx = q.X * q.X, yy = q.Y * q.Y, zz = q.Z * q.Z;
        double xy = q.X * q.Y, xz = q.X * q.Z, yz = q.Y * q.Z;
        double wx = q.W * q.X, wy = q.W * q.Y, wz = q.W * q.Z;

        // Rotation matrix: column i = where local axis i maps in world space
        // Column 0 (local X → world):
        int m00 = Snap(1 - 2 * (yy + zz));
        int m10 = Snap(2 * (xy + wz));
        int m20 = Snap(2 * (xz - wy));
        // Column 1 (local Y → world):
        int m01 = Snap(2 * (xy - wz));
        int m11 = Snap(1 - 2 * (xx + zz));
        int m21 = Snap(2 * (yz + wx));
        // Column 2 (local Z → world):
        int m02 = Snap(2 * (xz + wy));
        int m12 = Snap(2 * (yz - wx));
        int m22 = Snap(1 - 2 * (xx + yy));

        // Local extent: (0,0,0) to (lx, ly, lz)
        int lx = cx - 1, ly = cy - 1, lz = cz - 1;

        // For each world axis, the range is:
        //   world_i = m[i,0]*localX + m[i,1]*localY + m[i,2]*localZ
        // where localX ∈ [0, lx], localY ∈ [0, ly], localZ ∈ [0, lz]
        // min = sum of min(0, m*l), max = sum of max(0, m*l)
        int minX = System.Math.Min(0, m00 * lx) + System.Math.Min(0, m01 * ly) + System.Math.Min(0, m02 * lz);
        int maxX = System.Math.Max(0, m00 * lx) + System.Math.Max(0, m01 * ly) + System.Math.Max(0, m02 * lz);

        int minY = System.Math.Min(0, m10 * lx) + System.Math.Min(0, m11 * ly) + System.Math.Min(0, m12 * lz);
        int maxY = System.Math.Max(0, m10 * lx) + System.Math.Max(0, m11 * ly) + System.Math.Max(0, m12 * lz);

        int minZ = System.Math.Min(0, m20 * lx) + System.Math.Min(0, m21 * ly) + System.Math.Min(0, m22 * lz);
        int maxZ = System.Math.Max(0, m20 * lx) + System.Math.Max(0, m21 * ly) + System.Math.Max(0, m22 * lz);

        return (new Vector3I(minX, minY, minZ), new Vector3I(maxX, maxY, maxZ));
    }

    private static int Snap(double v)
    {
        if (v > 0.5) return 1;
        if (v < -0.5) return -1;
        return 0;
    }
}
