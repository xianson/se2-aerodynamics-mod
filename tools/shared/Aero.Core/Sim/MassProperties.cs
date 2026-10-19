using System.Numerics;
using Aero.Core.Grids;

namespace Aero.Core.Sim;

/// <summary>
/// Mass properties of a rigid body: total mass, center of mass,
/// and inertia tensor (with its inverse for angular acceleration).
///
/// Computed from a grid's block distribution assuming uniform density per block.
/// </summary>
public readonly struct MassProperties
{
    /// <summary>Total mass in kg.</summary>
    public readonly float TotalMass;

    /// <summary>Center of mass in grid-local coordinates (meters from grid origin).</summary>
    public readonly Vector3 CenterOfMass;

    /// <summary>
    /// Inertia tensor about CoM, in grid-local frame.
    /// Stored as a symmetric 3×3 matrix packed in 6 floats:
    ///   [Ixx, Iyy, Izz, Ixy, Ixz, Iyz]
    /// </summary>
    public readonly float Ixx, Iyy, Izz, Ixy, Ixz, Iyz;

    /// <summary>Inverse inertia tensor about CoM (precomputed for angular accel).</summary>
    public readonly float InvIxx, InvIyy, InvIzz, InvIxy, InvIxz, InvIyz;

    public MassProperties(float totalMass, Vector3 centerOfMass,
        float ixx, float iyy, float izz, float ixy, float ixz, float iyz)
    {
        TotalMass = totalMass;
        CenterOfMass = centerOfMass;
        Ixx = ixx; Iyy = iyy; Izz = izz;
        Ixy = ixy; Ixz = ixz; Iyz = iyz;

        // Invert symmetric 3×3
        (InvIxx, InvIyy, InvIzz, InvIxy, InvIxz, InvIyz) =
            InvertSymmetric3x3(ixx, iyy, izz, ixy, ixz, iyz);
    }

    /// <summary>
    /// Compute angular acceleration from torque: α = I⁻¹ · τ
    /// </summary>
    public readonly Vector3 AngularAcceleration(Vector3 torque)
    {
        return new Vector3(
            InvIxx * torque.X + InvIxy * torque.Y + InvIxz * torque.Z,
            InvIxy * torque.X + InvIyy * torque.Y + InvIyz * torque.Z,
            InvIxz * torque.X + InvIyz * torque.Y + InvIzz * torque.Z);
    }

    /// <summary>
    /// Compute mass properties from a grid with uniform block mass.
    /// Each 1×1×1 cell gets the same mass regardless of block type.
    /// </summary>
    public static MassProperties FromGrid(Grid grid, float blockSize, float massPerBlock)
    {
        if (grid.BlockCount == 0)
            return new MassProperties(0, Vector3.Zero, 1, 1, 1, 0, 0, 0);

        // First pass: count cells, find center of mass
        int cellCount = 0;
        Vector3 comSum = Vector3.Zero;

        foreach (var block in grid.Blocks.Values)
        {
            foreach (var cell in block.EnumerateCells())
            {
                // Cell center in meters
                float cx = (cell.X + 0.5f) * blockSize;
                float cy = (cell.Y + 0.5f) * blockSize;
                float cz = (cell.Z + 0.5f) * blockSize;
                comSum += new Vector3(cx, cy, cz);
                cellCount++;
            }
        }

        float totalMass = cellCount * massPerBlock;
        Vector3 com = comSum / cellCount;

        // Second pass: inertia tensor about CoM
        // For a cube of side s and mass m, about its own center:
        //   I_cube = (1/6) * m * s²
        // Parallel axis theorem adds m * d² for each axis
        float cubeSelfInertia = massPerBlock * blockSize * blockSize / 6.0f;

        float ixx = 0, iyy = 0, izz = 0;
        float ixy = 0, ixz = 0, iyz = 0;

        foreach (var block in grid.Blocks.Values)
        {
            foreach (var cell in block.EnumerateCells())
            {
                float dx = (cell.X + 0.5f) * blockSize - com.X;
                float dy = (cell.Y + 0.5f) * blockSize - com.Y;
                float dz = (cell.Z + 0.5f) * blockSize - com.Z;

                // Parallel axis: I_total = I_cube_self + m*(d_perp²)
                ixx += cubeSelfInertia + massPerBlock * (dy * dy + dz * dz);
                iyy += cubeSelfInertia + massPerBlock * (dx * dx + dz * dz);
                izz += cubeSelfInertia + massPerBlock * (dx * dx + dy * dy);

                // Products of inertia (negative by convention)
                ixy -= massPerBlock * dx * dy;
                ixz -= massPerBlock * dx * dz;
                iyz -= massPerBlock * dy * dz;
            }
        }

        return new MassProperties(totalMass, com, ixx, iyy, izz, ixy, ixz, iyz);
    }

    private static (float, float, float, float, float, float)
        InvertSymmetric3x3(float a, float b, float c, float d, float e, float f)
    {
        // Matrix:  [a d e]
        //          [d b f]
        //          [e f c]
        float det = a * (b * c - f * f)
                  - d * (d * c - f * e)
                  + e * (d * f - b * e);

        if (MathF.Abs(det) < 1e-20f)
        {
            // Degenerate — return identity-like inverse
            return (1, 1, 1, 0, 0, 0);
        }

        float invDet = 1.0f / det;

        float ia = (b * c - f * f) * invDet;
        float ib = (a * c - e * e) * invDet;
        float ic = (a * b - d * d) * invDet;
        float id = (e * f - d * c) * invDet;
        float ie = (d * f - b * e) * invDet;
        float iF = (d * e - a * f) * invDet;

        return (ia, ib, ic, id, ie, iF);
    }

    /// <summary>
    /// Add a point mass at the given position (meters). Returns new MassProperties
    /// with combined CoM and inertia (parallel axis theorem applied correctly).
    /// </summary>
    public MassProperties WithBallast(float ballastMass, Vector3 ballastPos)
    {
        float mTotal = TotalMass + ballastMass;
        if (mTotal < 1e-6f) return this;

        // New combined CoM
        Vector3 newCom = (CenterOfMass * TotalMass + ballastPos * ballastMass) / mTotal;

        // Shift original inertia from old CoM to new CoM (parallel axis)
        Vector3 dOld = CenterOfMass - newCom;
        float nIxx = Ixx + TotalMass * (dOld.Y * dOld.Y + dOld.Z * dOld.Z);
        float nIyy = Iyy + TotalMass * (dOld.X * dOld.X + dOld.Z * dOld.Z);
        float nIzz = Izz + TotalMass * (dOld.X * dOld.X + dOld.Y * dOld.Y);
        float nIxy = Ixy - TotalMass * dOld.X * dOld.Y;
        float nIxz = Ixz - TotalMass * dOld.X * dOld.Z;
        float nIyz = Iyz - TotalMass * dOld.Y * dOld.Z;

        // Add ballast point mass inertia about new CoM
        Vector3 dBal = ballastPos - newCom;
        nIxx += ballastMass * (dBal.Y * dBal.Y + dBal.Z * dBal.Z);
        nIyy += ballastMass * (dBal.X * dBal.X + dBal.Z * dBal.Z);
        nIzz += ballastMass * (dBal.X * dBal.X + dBal.Y * dBal.Y);
        nIxy -= ballastMass * dBal.X * dBal.Y;
        nIxz -= ballastMass * dBal.X * dBal.Z;
        nIyz -= ballastMass * dBal.Y * dBal.Z;

        return new MassProperties(mTotal, newCom, nIxx, nIyy, nIzz, nIxy, nIxz, nIyz);
    }

    public override string ToString()
        => $"mass={TotalMass:F1}kg CoM=({CenterOfMass.X:F2},{CenterOfMass.Y:F2},{CenterOfMass.Z:F2}) " +
           $"I=({Ixx:F0},{Iyy:F0},{Izz:F0})";
}
