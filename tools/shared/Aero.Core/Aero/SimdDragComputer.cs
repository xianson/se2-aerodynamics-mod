using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Aero.Core.Atmosphere;

namespace Aero.Core.Aero;

/// <summary>
/// Optimized aerodynamic force computation exploiting axis-aligned face normals.
///
/// Since all cube-based surface providers emit faces with normals along ±X, ±Y, ±Z:
///   1. Cp is pre-computed per direction (6 values, not per face)
///   2. Force has only one non-zero component → no Vector3 multiply
///   3. Cross product for torque reduces from 6 to 2 multiplies per face
///   4. Skin friction tangent is pre-computed per direction
///
/// These optimizations eliminate ~70% of per-face FP operations compared to
/// <see cref="NewtonianDragModel.ComputePerFace"/>.
/// </summary>
public static class SimdDragComputer
{
    private static readonly Vector3[] DirNormals =
    {
        Vector3.UnitX, -Vector3.UnitX,
        Vector3.UnitY, -Vector3.UnitY,
        Vector3.UnitZ, -Vector3.UnitZ,
    };

    /// <summary>Pre-computed per-direction aerodynamic parameters.</summary>
    private struct DirParams
    {
        public float CosAlpha;
        public float ForceMul;     // -Cp * q: multiply by area for force magnitude
        public float FricMul;      // CfSkin * q: multiply by area for friction magnitude
        public Vector3 FricDir;    // normalized tangent direction (pre-computed)
        public bool IsFrontFacing;
    }

    /// <summary>
    /// Compute per-face forces using axis-aligned optimizations.
    /// Drop-in replacement for NewtonianDragModel.ComputePerFace.
    /// </summary>
    public static AeroResult ComputePerFace(
        ISurfaceProvider provider,
        Vector3 velocity,
        AtmosphereState atmo,
        Vector3 centerOfMass,
        bool[]? visibility = null,
        float cpMax = 2.0f,
        float cdBluff = 1.1f,
        float cpBase = -0.15f,
        float cfSkin = 0.005f,
        float subsonicLimit = 0.6f,
        float supersonicLimit = 1.2f)
    {
        float speed = velocity.Length();
        if (speed < 0.01f || atmo.Density < 1e-8)
            return default;

        Vector3 vHat = velocity / speed;
        double q = atmo.GetDynamicPressure(speed);
        float qf = (float)q;
        double mach = atmo.SpeedOfSound > 0 ? speed / atmo.SpeedOfSound : 0;

        // Pre-compute per-direction parameters (6 total, not per-face)
        Span<DirParams> dirs = stackalloc DirParams[6];
        PrecomputeDirections(dirs, vHat, qf, (float)mach,
            cpMax, cdBluff, cpBase, cfSkin, subsonicLimit, supersonicLimit);

        float comX = centerOfMass.X, comY = centerOfMass.Y, comZ = centerOfMass.Z;
        float totalFx = 0, totalFy = 0, totalFz = 0;
        float totalTx = 0, totalTy = 0, totalTz = 0;
        float frontalArea = 0;

        var faces = provider.Faces;
        int count = faces.Count;

        // Fast path: direct span access for List<SurfaceFace>
        if (faces is List<SurfaceFace> faceList)
        {
            var span = CollectionsMarshal.AsSpan(faceList);
            ComputeLoop(span, dirs, visibility, comX, comY, comZ,
                ref totalFx, ref totalFy, ref totalFz,
                ref totalTx, ref totalTy, ref totalTz,
                ref frontalArea);
        }
        else
        {
            // Fallback: IReadOnlyList (rare)
            for (int i = 0; i < count; i++)
            {
                var face = faces[i];
                ProcessFace(face, i, dirs, visibility, comX, comY, comZ,
                    ref totalFx, ref totalFy, ref totalFz,
                    ref totalTx, ref totalTy, ref totalTz,
                    ref frontalArea);
            }
        }

        Vector3 totalForce = new(totalFx, totalFy, totalFz);
        Vector3 totalTorque = new(totalTx, totalTy, totalTz);

        float forceDotV = Vector3.Dot(totalForce, vHat);
        Vector3 liftVec = totalForce - forceDotV * vHat;

        return new AeroResult(
            totalForce, totalTorque,
            MathF.Abs(forceDotV),
            liftVec.Length(),
            frontalArea,
            mach, q);
    }

    /// <summary>
    /// Compute per-face forces with angular velocity (rotational damping).
    /// Uses per-face velocity accounting for rotation about CoM.
    /// </summary>
    public static AeroResult ComputePerFaceRotational(
        ISurfaceProvider provider,
        Vector3 velocity,
        Vector3 angularVelocity,
        AtmosphereState atmo,
        Vector3 centerOfMass,
        bool[]? visibility = null,
        float cpMax = 2.0f,
        float cdBluff = 1.1f,
        float cpBase = -0.15f,
        float cfSkin = 0.005f,
        float subsonicLimit = 0.6f,
        float supersonicLimit = 1.2f)
    {
        float speed = velocity.Length();
        if (speed < 0.01f && angularVelocity.LengthSquared() < 1e-8f)
            return default;
        if (atmo.Density < 1e-8)
            return default;

        Vector3 comVHat = speed > 0.01f ? velocity / speed : Vector3.UnitX;
        double comMach = atmo.SpeedOfSound > 0 ? speed / atmo.SpeedOfSound : 0;
        double comQ = atmo.GetDynamicPressure(speed);

        float comX = centerOfMass.X, comY = centerOfMass.Y, comZ = centerOfMass.Z;
        float totalFx = 0, totalFy = 0, totalFz = 0;
        float totalTx = 0, totalTy = 0, totalTz = 0;
        float frontalArea = 0;

        var faces = provider.Faces;
        int count = faces.Count;

        for (int i = 0; i < count; i++)
        {
            var face = faces is List<SurfaceFace> list
                ? CollectionsMarshal.AsSpan(list)[i]
                : faces[i];

            if (visibility != null && i < visibility.Length && !visibility[i])
            {
                // Check if front-facing before skipping
                int dCheck = NormalToDir(face.Normal);
                float cosCheck = -Vector3.Dot(comVHat, DirNormals[dCheck]);
                if (cosCheck > 0) continue;
            }

            // Per-face velocity with rotation
            Vector3 r = face.Position - centerOfMass;
            Vector3 faceVel = velocity + Vector3.Cross(angularVelocity, r);
            float faceSpeed = faceVel.Length();
            if (faceSpeed < 0.01f) continue;

            Vector3 faceVHat = faceVel / faceSpeed;
            float faceQ = (float)atmo.GetDynamicPressure(faceSpeed);
            float faceMach = atmo.SpeedOfSound > 0
                ? faceSpeed / (float)atmo.SpeedOfSound : 0;

            int d = NormalToDir(face.Normal);
            float cosAlpha = -Vector3.Dot(faceVHat, DirNormals[d]);

            float cp;
            if (cosAlpha > 0)
            {
                cp = ComputeCp(cosAlpha, faceMach, cpMax, cdBluff, subsonicLimit, supersonicLimit);
                frontalArea += face.Area * cosAlpha;
            }
            else
            {
                cp = cpBase;
            }

            float fMag = -cp * faceQ * face.Area;
            float rx = r.X, ry = r.Y, rz = r.Z;

            // Axis-aligned force + simplified cross product
            switch (d)
            {
                case 0: totalFx += fMag; totalTy += rz * fMag; totalTz -= ry * fMag; break;
                case 1: totalFx -= fMag; totalTy -= rz * fMag; totalTz += ry * fMag; break;
                case 2: totalFy += fMag; totalTx -= rz * fMag; totalTz += rx * fMag; break;
                case 3: totalFy -= fMag; totalTx += rz * fMag; totalTz -= rx * fMag; break;
                case 4: totalFz += fMag; totalTx += ry * fMag; totalTy -= rx * fMag; break;
                case 5: totalFz -= fMag; totalTx -= ry * fMag; totalTy += rx * fMag; break;
            }

            // Skin friction
            if (cosAlpha > 0 && cfSkin > 0)
            {
                Vector3 tangent = faceVHat - Vector3.Dot(faceVHat, DirNormals[d]) * DirNormals[d];
                float tLen = tangent.Length();
                if (tLen > 1e-6f)
                {
                    float fricF = cfSkin * faceQ * face.Area;
                    float ffx = tangent.X / tLen * fricF;
                    float ffy = tangent.Y / tLen * fricF;
                    float ffz = tangent.Z / tLen * fricF;

                    totalFx += ffx; totalFy += ffy; totalFz += ffz;
                    totalTx += ry * ffz - rz * ffy;
                    totalTy += rz * ffx - rx * ffz;
                    totalTz += rx * ffy - ry * ffx;
                }
            }
        }

        Vector3 totalForce = new(totalFx, totalFy, totalFz);
        Vector3 totalTorque = new(totalTx, totalTy, totalTz);

        float forceDotV = Vector3.Dot(totalForce, comVHat);
        Vector3 liftVec = totalForce - forceDotV * comVHat;

        return new AeroResult(
            totalForce, totalTorque,
            MathF.Abs(forceDotV),
            liftVec.Length(),
            frontalArea,
            comMach, comQ);
    }

    // ─── Inner loop (span path) ──────────────────────────────────

    private static void ComputeLoop(
        Span<SurfaceFace> faces, Span<DirParams> dirs,
        bool[]? visibility,
        float comX, float comY, float comZ,
        ref float totalFx, ref float totalFy, ref float totalFz,
        ref float totalTx, ref float totalTy, ref float totalTz,
        ref float frontalArea)
    {
        for (int i = 0; i < faces.Length; i++)
        {
            ref readonly var face = ref faces[i];
            ProcessFace(face, i, dirs, visibility, comX, comY, comZ,
                ref totalFx, ref totalFy, ref totalFz,
                ref totalTx, ref totalTy, ref totalTz,
                ref frontalArea);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ProcessFace(
        in SurfaceFace face, int index,
        Span<DirParams> dirs, bool[]? visibility,
        float comX, float comY, float comZ,
        ref float totalFx, ref float totalFy, ref float totalFz,
        ref float totalTx, ref float totalTy, ref float totalTz,
        ref float frontalArea)
    {
        int d = NormalToDir(face.Normal);
        ref readonly DirParams dp = ref dirs[d];

        if (dp.IsFrontFacing)
        {
            if (visibility != null && index < visibility.Length && !visibility[index])
                return;
            frontalArea += face.Area * dp.CosAlpha;
        }

        float fMag = dp.ForceMul * face.Area;

        float rx = face.Position.X - comX;
        float ry = face.Position.Y - comY;
        float rz = face.Position.Z - comZ;

        // Axis-aligned force + simplified cross product (2 muls instead of 6)
        //   d=0 (+X): F=(fMag,0,0) → T=(0, rz*fMag, -ry*fMag)
        //   d=1 (-X): F=(-fMag,0,0) → T=(0, -rz*fMag, ry*fMag)
        //   d=2 (+Y): F=(0,fMag,0) → T=(-rz*fMag, 0, rx*fMag)
        //   d=3 (-Y): F=(0,-fMag,0) → T=(rz*fMag, 0, -rx*fMag)
        //   d=4 (+Z): F=(0,0,fMag) → T=(ry*fMag, -rx*fMag, 0)
        //   d=5 (-Z): F=(0,0,-fMag) → T=(-ry*fMag, rx*fMag, 0)
        switch (d)
        {
            case 0: totalFx += fMag; totalTy += rz * fMag; totalTz -= ry * fMag; break;
            case 1: totalFx -= fMag; totalTy -= rz * fMag; totalTz += ry * fMag; break;
            case 2: totalFy += fMag; totalTx -= rz * fMag; totalTz += rx * fMag; break;
            case 3: totalFy -= fMag; totalTx += rz * fMag; totalTz -= rx * fMag; break;
            case 4: totalFz += fMag; totalTx += ry * fMag; totalTy -= rx * fMag; break;
            case 5: totalFz -= fMag; totalTx -= ry * fMag; totalTy += rx * fMag; break;
        }

        // Skin friction (pre-computed tangent direction)
        if (dp.IsFrontFacing && dp.FricMul > 0)
        {
            float fricF = dp.FricMul * face.Area;
            float ffx = dp.FricDir.X * fricF;
            float ffy = dp.FricDir.Y * fricF;
            float ffz = dp.FricDir.Z * fricF;

            totalFx += ffx; totalFy += ffy; totalFz += ffz;
            totalTx += ry * ffz - rz * ffy;
            totalTy += rz * ffx - rx * ffz;
            totalTz += rx * ffy - ry * ffx;
        }
    }

    // ─── Pre-computation ─────────────────────────────────────────

    private static void PrecomputeDirections(
        Span<DirParams> dirs, Vector3 vHat, float q, float mach,
        float cpMax, float cdBluff, float cpBase, float cfSkin,
        float subsonicLimit, float supersonicLimit)
    {
        for (int d = 0; d < 6; d++)
        {
            float cosA = -Vector3.Dot(vHat, DirNormals[d]);
            dirs[d].CosAlpha = cosA;
            dirs[d].IsFrontFacing = cosA > 0;

            if (cosA > 0)
            {
                float cp = ComputeCp(cosA, mach, cpMax, cdBluff, subsonicLimit, supersonicLimit);
                dirs[d].ForceMul = -cp * q;
            }
            else
            {
                dirs[d].ForceMul = -cpBase * q;
            }

            // Pre-compute friction direction
            if (cosA > 0 && cfSkin > 0)
            {
                Vector3 tangent = vHat - Vector3.Dot(vHat, DirNormals[d]) * DirNormals[d];
                float tLen = tangent.Length();
                if (tLen > 1e-6f)
                {
                    dirs[d].FricDir = tangent / tLen;
                    dirs[d].FricMul = cfSkin * q;
                }
            }
        }
    }

    // ─── Cp model ────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ComputeCp(float cosAlpha, float mach,
        float cpMax, float cdBluff, float subsonicLimit, float supersonicLimit)
    {
        if (mach >= supersonicLimit)
            return cpMax * cosAlpha * cosAlpha;

        if (mach <= subsonicLimit)
            return cdBluff * cosAlpha;

        float t = (mach - subsonicLimit) / (supersonicLimit - subsonicLimit);
        t = t * t * (3f - 2f * t);

        float cpSub = cdBluff * cosAlpha;
        float cpSup = cpMax * cosAlpha * cosAlpha;
        return cpSub + (cpSup - cpSub) * t;
    }

    // ─── Normal → direction index ────────────────────────────────

    /// <summary>
    /// Branchless axis-aligned normal to direction index.
    /// 3 comparisons (max-of-3) instead of 5-comparison cascade.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int NormalToDir(Vector3 normal)
    {
        float ax = MathF.Abs(normal.X);
        float ay = MathF.Abs(normal.Y);
        float az = MathF.Abs(normal.Z);

        int axis;
        float sign;
        if (ax > ay)
        {
            if (ax > az) { axis = 0; sign = normal.X; }
            else { axis = 2; sign = normal.Z; }
        }
        else
        {
            if (ay > az) { axis = 1; sign = normal.Y; }
            else { axis = 2; sign = normal.Z; }
        }

        return axis * 2 + (sign < 0 ? 1 : 0);
    }
}
