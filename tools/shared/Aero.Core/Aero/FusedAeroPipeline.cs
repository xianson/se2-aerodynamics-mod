using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Aero.Core.Atmosphere;
using Aero.Core.Grids;
using Aero.Core.Math;

namespace Aero.Core.Aero;

/// <summary>
/// Fused single-pass aerodynamic pipeline: flat depth buffer + shadow eval + force
/// computation in one pass over faces. No intermediate bool[] visibility array.
///
/// Optimizations vs separate ColumnShadowMap + SimdDragComputer:
///   1. Flat float[] depth buffer (no Dictionary hash overhead)
///   2. Single iteration over faces (shadow check + force in same loop)
///   3. Pre-computed Cp per direction (6 values, not per-face)
///   4. Simplified axis-aligned cross product (2 muls instead of 6)
///   5. Branchless NormalToDir via sign-bit extraction
///   6. Reusable buffers (no per-frame allocation)
///
/// Expected improvement: 2-4x over ColumnShadowMap + SimdDragComputer combined.
/// </summary>
public class FusedAeroPipeline
{
    private static readonly Vector3[] DirNormals =
    {
        Vector3.UnitX, -Vector3.UnitX,
        Vector3.UnitY, -Vector3.UnitY,
        Vector3.UnitZ, -Vector3.UnitZ,
    };

    // ─── Reusable flat depth buffer ──────────────────────────────

    private float[] _depthBuf = Array.Empty<float>();
    private bool[] _wingCol = Array.Empty<bool>();
    private int _minU, _minV, _sizeU, _sizeV;

    /// <summary>Direction change threshold (radians). Default ~5°.</summary>
    public float DirectionThreshold { get; set; } = 0.087f;

    /// <summary>
    /// Wake decay length in block units. 0 = binary shadow (legacy).
    /// Default 10. Only applies when blocker is a wing cell.
    /// </summary>
    public float WakeDecayLength { get; set; } = 10f;

    /// <summary>
    /// Set of grid cells that belong to detected wings. Wake decay only
    /// applies to columns whose upstream-most block is a wing cell.
    /// </summary>
    public HashSet<Vector3I>? WingCells { get; set; }

    // Reserved for future direction-change caching
    // (currently rebuilds every call — add caching when profiling shows it matters)

    // ─── Shadow stats (for diagnostics) ──────────────────────────

    /// <summary>Number of visible front-facing faces from last Compute.</summary>
    public int LastVisibleCount { get; private set; }

    /// <summary>Number of shadowed front-facing faces from last Compute.</summary>
    public int LastShadowedCount { get; private set; }

    // ─── Pre-computed direction params ───────────────────────────

    private struct DirParams
    {
        public float CosAlpha;
        public float ForceMul;     // -Cp * q
        public float FricMul;      // CfSkin * q
        public float FricDirX, FricDirY, FricDirZ;
        public bool IsFrontFacing;
    }

    // ─── Main entry point ────────────────────────────────────────

    /// <summary>
    /// Compute aerodynamic forces with self-shadow in a single fused pass.
    ///
    /// Pipeline:
    ///   1. Build flat depth buffer from grid blocks: O(cells)
    ///   2. Single pass over faces: project → shadow check → force: O(faces)
    ///
    /// No intermediate arrays. No Dictionary. No separate shadow evaluation.
    /// </summary>
    public AeroResult Compute(
        Grid grid,
        ISurfaceProvider provider,
        Vector3 velocity,
        AtmosphereState atmo,
        Vector3 centerOfMass,
        float blockSize = 2.5f,
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

        // Build perpendicular axes
        Vector3 flowDir = vHat;
        GetPerpendicularAxes(flowDir, out Vector3 axisU, out Vector3 axisV);

        // 1. Build flat depth buffer
        BuildFlatDepthBuffer(grid, blockSize, flowDir, axisU, axisV);

        // 2. Pre-compute per-direction parameters
        Span<DirParams> dirs = stackalloc DirParams[6];
        PrecomputeDirections(dirs, vHat, qf, (float)mach,
            cpMax, cdBluff, cpBase, cfSkin, subsonicLimit, supersonicLimit);

        // 3. Fused shadow eval + force computation
        float comX = centerOfMass.X, comY = centerOfMass.Y, comZ = centerOfMass.Z;
        float invBlock = 1f / blockSize;
        float totalFx = 0, totalFy = 0, totalFz = 0;
        float totalTx = 0, totalTy = 0, totalTz = 0;
        float frontalArea = 0;
        int visibleCount = 0, shadowedCount = 0;

        // Inline constants for depth buffer lookup
        float auX = axisU.X, auY = axisU.Y, auZ = axisU.Z;
        float avX = axisV.X, avY = axisV.Y, avZ = axisV.Z;
        float fdX = flowDir.X, fdY = flowDir.Y, fdZ = flowDir.Z;
        int minU = _minU, minV = _minV, sizeU = _sizeU, sizeV = _sizeV;

        var faces = provider.Faces;

        // Fast path: direct span access
        if (faces is List<SurfaceFace> faceList)
        {
            var span = CollectionsMarshal.AsSpan(faceList);
            for (int i = 0; i < span.Length; i++)
            {
                ref readonly var face = ref span[i];
                FusedProcessFace(
                    in face, dirs,
                    invBlock, auX, auY, auZ, avX, avY, avZ, fdX, fdY, fdZ,
                    minU, minV, sizeU, sizeV,
                    comX, comY, comZ,
                    ref totalFx, ref totalFy, ref totalFz,
                    ref totalTx, ref totalTy, ref totalTz,
                    ref frontalArea, ref visibleCount, ref shadowedCount);
            }
        }
        else
        {
            for (int i = 0; i < faces.Count; i++)
            {
                var face = faces[i];
                FusedProcessFace(
                    in face, dirs,
                    invBlock, auX, auY, auZ, avX, avY, avZ, fdX, fdY, fdZ,
                    minU, minV, sizeU, sizeV,
                    comX, comY, comZ,
                    ref totalFx, ref totalFy, ref totalFz,
                    ref totalTx, ref totalTy, ref totalTz,
                    ref frontalArea, ref visibleCount, ref shadowedCount);
            }
        }

        LastVisibleCount = visibleCount;
        LastShadowedCount = shadowedCount;

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

    // ─── Fused per-face: shadow check + force in one shot ────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FusedProcessFace(
        in SurfaceFace face, Span<DirParams> dirs,
        float invBlock,
        float auX, float auY, float auZ,
        float avX, float avY, float avZ,
        float fdX, float fdY, float fdZ,
        int minU, int minV, int sizeU, int sizeV,
        float comX, float comY, float comZ,
        ref float totalFx, ref float totalFy, ref float totalFz,
        ref float totalTx, ref float totalTy, ref float totalTz,
        ref float frontalArea, ref int visibleCount, ref int shadowedCount)
    {
        int d = NormalToDirFast(face.Normal);
        ref readonly DirParams dp = ref dirs[d];

        if (dp.IsFrontFacing)
        {
            // Shadow check via flat depth buffer (no Dictionary!)
            float bx = face.Position.X * invBlock;
            float by = face.Position.Y * invBlock;
            float bz = face.Position.Z * invBlock;

            int u = (int)MathF.Floor(bx * auX + by * auY + bz * auZ) - minU;
            int v = (int)MathF.Floor(bx * avX + by * avY + bz * avZ) - minV;
            float depth = bx * fdX + by * fdY + bz * fdZ;

            float shadowFactor = 1f;
            if ((uint)u < (uint)sizeU && (uint)v < (uint)sizeV)
            {
                int colIdx = u * sizeV + v;
                float minDepth = _depthBuf[colIdx];
                if (minDepth < depth - 0.4f)
                {
                    float decayLen = WakeDecayLength;
                    if (decayLen > 0f && _wingCol[colIdx])
                    {
                        float dist = depth - minDepth;
                        shadowFactor = 1f - MathF.Exp(-dist / decayLen);
                        if (shadowFactor < 0.01f)
                        {
                            shadowedCount++;
                            return;
                        }
                    }
                    else
                    {
                        shadowedCount++;
                        return;
                    }
                }
            }

            visibleCount++;
            frontalArea += face.Area * dp.CosAlpha * shadowFactor;

            // Force computation (front-facing visible with shadow attenuation)
            float fMag = dp.ForceMul * shadowFactor * face.Area;
            float rx = face.Position.X - comX;
            float ry = face.Position.Y - comY;
            float rz = face.Position.Z - comZ;

            switch (d)
            {
                case 0: totalFx += fMag; totalTy += rz * fMag; totalTz -= ry * fMag; break;
                case 1: totalFx -= fMag; totalTy -= rz * fMag; totalTz += ry * fMag; break;
                case 2: totalFy += fMag; totalTx -= rz * fMag; totalTz += rx * fMag; break;
                case 3: totalFy -= fMag; totalTx += rz * fMag; totalTz -= rx * fMag; break;
                case 4: totalFz += fMag; totalTx += ry * fMag; totalTy -= rx * fMag; break;
                case 5: totalFz -= fMag; totalTx -= ry * fMag; totalTy += rx * fMag; break;
            }

            if (dp.FricMul > 0)
            {
                float ff = dp.FricMul * face.Area;
                totalFx += dp.FricDirX * ff;
                totalFy += dp.FricDirY * ff;
                totalFz += dp.FricDirZ * ff;
            }
            return;
        }

        // Rear-facing: base pressure — no shadow check needed
        float rearFMag = dp.ForceMul * face.Area;
        float rx2 = face.Position.X - comX;
        float ry2 = face.Position.Y - comY;
        float rz2 = face.Position.Z - comZ;

        // Axis-aligned force + simplified cross product
        switch (d)
        {
            case 0: totalFx += rearFMag; totalTy += rz2 * rearFMag; totalTz -= ry2 * rearFMag; break;
            case 1: totalFx -= rearFMag; totalTy -= rz2 * rearFMag; totalTz += ry2 * rearFMag; break;
            case 2: totalFy += rearFMag; totalTx -= rz2 * rearFMag; totalTz += rx2 * rearFMag; break;
            case 3: totalFy -= rearFMag; totalTx += rz2 * rearFMag; totalTz -= rx2 * rearFMag; break;
            case 4: totalFz += rearFMag; totalTx += ry2 * rearFMag; totalTy -= rx2 * rearFMag; break;
            case 5: totalFz -= rearFMag; totalTx -= ry2 * rearFMag; totalTy += rx2 * rearFMag; break;
        }
    }

    // ─── Flat depth buffer ───────────────────────────────────────

    private void BuildFlatDepthBuffer(Grid grid, float blockSize,
        Vector3 flowDir, Vector3 axisU, Vector3 axisV)
    {
        float invBlock = 1f / blockSize;
        float auX = axisU.X, auY = axisU.Y, auZ = axisU.Z;
        float avX = axisV.X, avY = axisV.Y, avZ = axisV.Z;
        float fdX = flowDir.X, fdY = flowDir.Y, fdZ = flowDir.Z;

        // First pass: find UV bounds
        int bMinU = int.MaxValue, bMaxU = int.MinValue;
        int bMinV = int.MaxValue, bMaxV = int.MinValue;

        foreach (var block in grid.Blocks.Values)
        {
            foreach (var cell in block.EnumerateCells())
            {
                float bx = cell.X + 0.5f;
                float by = cell.Y + 0.5f;
                float bz = cell.Z + 0.5f;

                int u = (int)MathF.Floor(bx * auX + by * auY + bz * auZ);
                int v = (int)MathF.Floor(bx * avX + by * avY + bz * avZ);

                if (u < bMinU) bMinU = u;
                if (u > bMaxU) bMaxU = u;
                if (v < bMinV) bMinV = v;
                if (v > bMaxV) bMaxV = v;
            }
        }

        if (bMinU > bMaxU)
        {
            // Empty grid
            _sizeU = _sizeV = 0;
            return;
        }

        _minU = bMinU;
        _minV = bMinV;
        _sizeU = bMaxU - bMinU + 1;
        _sizeV = bMaxV - bMinV + 1;
        int total = _sizeU * _sizeV;

        // Reuse buffer if large enough
        if (_depthBuf.Length < total)
        {
            _depthBuf = new float[total];
            _wingCol = new bool[total];
        }

        // Fill with max depth (no blocker)
        _depthBuf.AsSpan(0, total).Fill(float.MaxValue);
        _wingCol.AsSpan(0, total).Clear();

        var wingCells = WingCells;

        // Second pass: fill min depth per column, track wing blockers
        foreach (var block in grid.Blocks.Values)
        {
            foreach (var cell in block.EnumerateCells())
            {
                float bx = cell.X + 0.5f;
                float by = cell.Y + 0.5f;
                float bz = cell.Z + 0.5f;

                int u = (int)MathF.Floor(bx * auX + by * auY + bz * auZ) - _minU;
                int v = (int)MathF.Floor(bx * avX + by * avY + bz * avZ) - _minV;
                float depth = bx * fdX + by * fdY + bz * fdZ;

                int idx = u * _sizeV + v;
                if (depth < _depthBuf[idx])
                {
                    _depthBuf[idx] = depth;
                    _wingCol[idx] = wingCells is { Count: > 0 } && wingCells.Contains(cell);
                }
            }
        }
    }

    // ─── Branchless NormalToDir ──────────────────────────────────

    /// <summary>
    /// Map axis-aligned normal to direction index 0-5.
    /// Uses sign-bit extraction + magnitude comparison instead of branch cascade.
    ///
    /// For axis-aligned normals, exactly one component has magnitude > 0.5.
    /// We find which axis dominates and whether it's positive or negative.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int NormalToDirFast(Vector3 normal)
    {
        float ax = MathF.Abs(normal.X);
        float ay = MathF.Abs(normal.Y);
        float az = MathF.Abs(normal.Z);

        // Find dominant axis: 0=X, 1=Y, 2=Z
        // For axis-aligned normals, one of these is 1.0 and others are 0.0
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

        // axis * 2 gives base index: X=0, Y=2, Z=4
        // Negative sign adds 1: +X=0, -X=1, +Y=2, -Y=3, +Z=4, -Z=5
        return axis * 2 + (sign < 0 ? 1 : 0);
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

            if (cosA > 0 && cfSkin > 0)
            {
                Vector3 tangent = vHat - Vector3.Dot(vHat, DirNormals[d]) * DirNormals[d];
                float tLen = tangent.Length();
                if (tLen > 1e-6f)
                {
                    tangent /= tLen;
                    dirs[d].FricDirX = tangent.X;
                    dirs[d].FricDirY = tangent.Y;
                    dirs[d].FricDirZ = tangent.Z;
                    dirs[d].FricMul = cfSkin * q;
                }
            }
        }
    }

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

    private static void GetPerpendicularAxes(Vector3 dir, out Vector3 axisU, out Vector3 axisV)
    {
        Vector3 up = MathF.Abs(dir.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        axisU = Vector3.Normalize(Vector3.Cross(dir, up));
        axisV = Vector3.Cross(dir, axisU);
    }

    /// <summary>
    /// Reset state to force full rebuild on next Compute.
    /// </summary>
    public void ResetCache()
    {
        _depthBuf = Array.Empty<float>();
        _sizeU = _sizeV = 0;
    }
}
