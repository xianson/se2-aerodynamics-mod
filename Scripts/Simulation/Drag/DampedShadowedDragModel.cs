#pragma warning disable
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace AeroMod;

/// <summary>
/// Shadow + Newtonian drag with per-face rotational velocity (ω×r).
/// Vector256 AVX2 SIMD: 8 faces per iteration, rsqrt+NR, baked visArea,
/// sin²α skin friction shortcut. Scalar fallback for non-AVX2.
/// </summary>
public class DampedShadowedDragModel : IAeroDragModel
{
    public string Name => "Newtonian + Shadow + Damping (AVX2)";

    private readonly ColumnShadowMap _shadowMap = new();

    // ─── Tuning ─────────────────────────────────────────────────────

    public float CpMax { get; set; } = 2.0f;
    public float CdBluff { get; set; } = 1.1f;
    public float CpBase { get; set; } = -0.15f;
    public float CfSkin { get; set; } = 0.005f;
    public float SubsonicLimit { get; set; } = 0.6f;
    public float SupersonicLimit { get; set; } = 1.2f;
    public float Streamlining { get; set; } = 0.4f;

    public float DirectionThreshold
    {
        get => _shadowMap.DirectionThreshold;
        set => _shadowMap.DirectionThreshold = value;
    }

    public ColumnShadowMap ShadowMap => _shadowMap;

    /// <summary>Per-face Cp from the last Compute() call (for debug draw).</summary>
    public List<float> FaceCp => _cpOut;

    // ─── SoA lists (padded to multiple of 8) ─────────────────────────

    private List<float> _px = new(), _py = new(), _pz = new();
    private List<float> _nx = new(), _ny = new(), _nz = new();
    private List<float> _area = new();
    private List<float> _visArea = new(); // area × visibility (baked)
    private List<float> _cpOut = new();
    private int _faceCount;
    private int _padded;
    private int _lastShadowVersion = -1;
    private int _lastSurfaceVersion = -1;

    private void EnsureSoA(IReadOnlyList<SurfaceFace> faces, int surfaceVersion)
    {
        int n = faces.Count;
        int padded = (n + 7) & ~7;

        if (_faceCount == n && _px.Count >= padded && _lastSurfaceVersion == surfaceVersion)
            return;

        _lastSurfaceVersion = surfaceVersion;
        _faceCount = n;
        _padded = padded;

        // Only allocate if lists are too small; reuse existing when possible
        if (_px.Count < padded)
        {
            _px = new List<float>(padded); _py = new List<float>(padded); _pz = new List<float>(padded);
            _nx = new List<float>(padded); _ny = new List<float>(padded); _nz = new List<float>(padded);
            _area = new List<float>(padded);
            _visArea = new List<float>(padded);
            _cpOut = new List<float>(padded);
            for (int j = 0; j < padded; j++)
            {
                _px.Add(0f); _py.Add(0f); _pz.Add(0f);
                _nx.Add(0f); _ny.Add(0f); _nz.Add(0f);
                _area.Add(0f); _visArea.Add(0f); _cpOut.Add(0f);
            }
        }
        else
        {
            // Zero out padding zone (old data from larger face set)
            for (int j = n; j < padded; j++)
            {
                _px[j] = 0f; _py[j] = 0f; _pz[j] = 0f;
                _nx[j] = 0f; _ny[j] = 0f; _nz[j] = 0f;
                _area[j] = 0f; _visArea[j] = 0f; _cpOut[j] = 0f;
            }
        }

        for (int i = 0; i < n; i++)
        {
            var f = faces[i];
            _px[i] = f.Position.X; _py[i] = f.Position.Y; _pz[i] = f.Position.Z;
            _nx[i] = f.Normal.X;   _ny[i] = f.Normal.Y;   _nz[i] = f.Normal.Z;
            _area[i] = f.Area;
            _visArea[i] = f.Area;
        }

        _lastShadowVersion = -1;
    }

    private void BakeVisibility()
    {
        var visFactor = _shadowMap.VisibilityFactor;
        int n = _faceCount;
        int visLen = visFactor.Count;
        for (int i = 0; i < n; i++)
        {
            float v = i < visLen ? visFactor[i] : 1f;
            _visArea[i] = _area[i] * v;
        }
        _lastShadowVersion = _shadowMap.Version;
    }

    // ─── Compute ────────────────────────────────────────────────────

    public AeroResult Compute(in AeroContext ctx)
    {
        float speed = ctx.Speed;
        if (speed < 0.01f || ctx.Atmosphere.Density < 1e-8)
            return default;

        var cache = ctx.SurfaceCache;
        Vector3 vHat = ctx.Velocity / speed;

        _shadowMap.Update(ctx.GridAccessor, cache, ctx.Velocity);

        EnsureSoA(cache.Faces, cache.Version);

        if (_lastShadowVersion != _shadowMap.Version)
            BakeVisibility();

        float totalFx, totalFy, totalFz;
        float totalTx, totalTy, totalTz;
        float frontalArea;

        if (Vector256.IsHardwareAccelerated && _padded >= 8)
            ComputeAvx2(ctx, speed, vHat,
                out totalFx, out totalFy, out totalFz,
                out totalTx, out totalTy, out totalTz,
                out frontalArea);
        else
            ComputeScalar(ctx, speed, vHat,
                out totalFx, out totalFy, out totalFz,
                out totalTx, out totalTy, out totalTz,
                out frontalArea);

        var totalForce = new Vector3(totalFx, totalFy, totalFz);
        var totalTorque = new Vector3(totalTx, totalTy, totalTz);

        float forceDotV = Vector3.Dot(totalForce, vHat);
        Vector3 liftVec = totalForce - forceDotV * vHat;

        double q = ctx.Atmosphere.GetDynamicPressure(speed);
        double mach = ctx.Atmosphere.SpeedOfSound > 0 ? speed / ctx.Atmosphere.SpeedOfSound : 0;

        return new AeroResult(
            totalForce, totalTorque,
            MathF.Abs(forceDotV),
            liftVec.Length(),
            frontalArea,
            mach, q);
    }

    // ─── AVX2 path: 8 faces per iteration ───────────────────────────

    private void ComputeAvx2(in AeroContext ctx, float speed, Vector3 vHat,
        out float totalFx, out float totalFy, out float totalFz,
        out float totalTx, out float totalTy, out float totalTz,
        out float frontalArea)
    {
        int n = _padded;

        var velXV = Vector256.Create(ctx.Velocity.X);
        var velYV = Vector256.Create(ctx.Velocity.Y);
        var velZV = Vector256.Create(ctx.Velocity.Z);
        var wxV = Vector256.Create(ctx.AngularVelocity.X);
        var wyV = Vector256.Create(ctx.AngularVelocity.Y);
        var wzV = Vector256.Create(ctx.AngularVelocity.Z);
        var comXV = Vector256.Create(ctx.CenterOfMass.X);
        var comYV = Vector256.Create(ctx.CenterOfMass.Y);
        var comZV = Vector256.Create(ctx.CenterOfMass.Z);
        var halfRhoV = Vector256.Create((float)(0.5 * ctx.Atmosphere.Density));
        float invSoS = ctx.Atmosphere.SpeedOfSound > 0 ? 1f / (float)ctx.Atmosphere.SpeedOfSound : 0;
        var invSoSV = Vector256.Create(invSoS);
        float baseCp = Streamlining > 0f ? CpBase * (1f - 0.7f * Streamlining) : CpBase;
        var baseCpV = Vector256.Create(baseCp);
        var cpMaxV = Vector256.Create(CpMax);
        var cdBluffV = Vector256.Create(CdBluff);
        var cfSkinV = Vector256.Create(CfSkin);
        var subLimV = Vector256.Create(SubsonicLimit);
        var invMachRangeV = Vector256.Create(
            SupersonicLimit > SubsonicLimit ? 1f / (SupersonicLimit - SubsonicLimit) : 0f);
        var streamV = Vector256.Create(Streamlining);
        var vhatXV = Vector256.Create(vHat.X);
        var vhatYV = Vector256.Create(vHat.Y);
        var vhatZV = Vector256.Create(vHat.Z);

        bool hasOmega = ctx.AngularVelocity.X != 0 || ctx.AngularVelocity.Y != 0 || ctx.AngularVelocity.Z != 0;
        bool hasSkin = CfSkin > 0;
        bool hasStreamlining = Streamlining > 0f;

        var zeroV = Vector256<float>.Zero;
        var oneV = Vector256.Create(1f);
        var halfV = Vector256.Create(0.5f);
        var onePointFiveV = Vector256.Create(1.5f);
        var twoV = Vector256.Create(2f);
        var threeV = Vector256.Create(3f);
        var epsV = Vector256.Create(1e-4f);
        var tEpsV = Vector256.Create(1e-12f);
        var minFracV = Vector256.Create(0.08f);
        var frac92V = Vector256.Create(0.92f);

        var accFx = zeroV; var accFy = zeroV; var accFz = zeroV;
        var accTx = zeroV; var accTy = zeroV; var accTz = zeroV;
        var accArea = zeroV;

        ref float pxRef = ref MemoryMarshal.GetReference(CollectionsMarshal.AsSpan(_px));
        ref float pyRef = ref MemoryMarshal.GetReference(CollectionsMarshal.AsSpan(_py));
        ref float pzRef = ref MemoryMarshal.GetReference(CollectionsMarshal.AsSpan(_pz));
        ref float nxRef = ref MemoryMarshal.GetReference(CollectionsMarshal.AsSpan(_nx));
        ref float nyRef = ref MemoryMarshal.GetReference(CollectionsMarshal.AsSpan(_ny));
        ref float nzRef = ref MemoryMarshal.GetReference(CollectionsMarshal.AsSpan(_nz));
        ref float vaRef = ref MemoryMarshal.GetReference(CollectionsMarshal.AsSpan(_visArea));
        ref float cpRef = ref MemoryMarshal.GetReference(CollectionsMarshal.AsSpan(_cpOut));

        for (int i = 0; i < n; i += 8)
        {
            var px = Vector256.LoadUnsafe(ref Unsafe.Add(ref pxRef, i));
            var py = Vector256.LoadUnsafe(ref Unsafe.Add(ref pyRef, i));
            var pz = Vector256.LoadUnsafe(ref Unsafe.Add(ref pzRef, i));
            var nx = Vector256.LoadUnsafe(ref Unsafe.Add(ref nxRef, i));
            var ny = Vector256.LoadUnsafe(ref Unsafe.Add(ref nyRef, i));
            var nz = Vector256.LoadUnsafe(ref Unsafe.Add(ref nzRef, i));
            var vai = Vector256.LoadUnsafe(ref Unsafe.Add(ref vaRef, i));

            // r = pos - CoM
            var rx = px - comXV;
            var ry = py - comYV;
            var rz = pz - comZV;

            // Per-face velocity: v + ω×r
            Vector256<float> vx, vy, vz;
            if (hasOmega)
            {
                vx = velXV + (wyV * rz - wzV * ry);
                vy = velYV + (wzV * rx - wxV * rz);
                vz = velZV + (wxV * ry - wyV * rx);
            }
            else
            {
                vx = velXV; vy = velYV; vz = velZV;
            }

            // speedSq, rsqrt+NR
            var speedSq = vx * vx + vy * vy + vz * vz;
            var safeSpeedSq = Vector256.Max(speedSq, epsV);

            Vector256<float> invSpeed;
            if (Avx.IsSupported)
            {
                var est = Avx.ReciprocalSqrt(safeSpeedSq);
                invSpeed = est * (onePointFiveV - halfV * safeSpeedSq * est * est);
            }
            else
            {
                invSpeed = oneV / Vector256.Sqrt(safeSpeedSq);
            }

            var spd = safeSpeedSq * invSpeed;

            // vHat
            var vhx = vx * invSpeed;
            var vhy = vy * invSpeed;
            var vhz = vz * invSpeed;

            // cosAlpha
            var cosA = vhx * nx + vhy * ny + vhz * nz;

            // q = ½ρv²
            var q = halfRhoV * speedSq;

            // Mach
            var mach = spd * invSoSV;

            // Branchless Cp: sub/sup blend
            var cpSub = cdBluffV * cosA;
            var cpSup = cpMaxV * cosA * cosA;
            var t = (mach - subLimV) * invMachRangeV;
            t = Vector256.Max(zeroV, Vector256.Min(oneV, t));
            t = t * t * (threeV - twoV * t);
            var cpWindward = cpSub + (cpSup - cpSub) * t;

            // Streamlining
            if (hasStreamlining)
            {
                var recovery = cosA * cosA;
                var factor = minFracV + frac92V * recovery;
                cpWindward = cpWindward * (oneV - streamV * (oneV - factor));
            }

            // Front/rear select. Shadow baked into vai.
            var windward = Vector256.GreaterThan(cosA, zeroV);
            var cp = Vector256.ConditionalSelect(windward, cpWindward, baseCpV);

            // Zero out padded/zero-speed lanes
            var valid = Vector256.GreaterThanOrEqual(speedSq, epsV);
            cp = Vector256.ConditionalSelect(valid, cp, zeroV);

            // Store Cp for debug draw
            cp.StoreUnsafe(ref Unsafe.Add(ref cpRef, i));

            // Frontal area (windward only, with shadow attenuation)
            accArea += Vector256.ConditionalSelect(windward & valid, vai * cosA, zeroV);

            // Pressure force: F = -Cp × q × visArea × normal
            var pf = zeroV - cp * q * vai;
            var fx = nx * pf;
            var fy = ny * pf;
            var fz = nz * pf;

            // Skin friction: sin²α shortcut
            if (hasSkin)
            {
                var sinSq = oneV - cosA * cosA;
                var sinValid = Vector256.GreaterThan(sinSq, tEpsV);
                var safeSinSq = Vector256.Max(sinSq, epsV);

                Vector256<float> invSinA;
                if (Avx.IsSupported)
                {
                    var est = Avx.ReciprocalSqrt(safeSinSq);
                    invSinA = est * (onePointFiveV - halfV * safeSinSq * est * est);
                }
                else
                {
                    invSinA = oneV / Vector256.Sqrt(safeSinSq);
                }

                var tx = vhx - cosA * nx;
                var ty = vhy - cosA * ny;
                var tz = vhz - cosA * nz;

                var fric = cfSkinV * q * vai * invSinA;
                var fricMask = windward & sinValid & valid;
                fx += Vector256.ConditionalSelect(fricMask, tx * fric, zeroV);
                fy += Vector256.ConditionalSelect(fricMask, ty * fric, zeroV);
                fz += Vector256.ConditionalSelect(fricMask, tz * fric, zeroV);
            }

            // Zero invalid lanes
            fx = Vector256.ConditionalSelect(valid, fx, zeroV);
            fy = Vector256.ConditionalSelect(valid, fy, zeroV);
            fz = Vector256.ConditionalSelect(valid, fz, zeroV);

            // Accumulate force + torque
            accFx += fx; accFy += fy; accFz += fz;
            accTx += ry * fz - rz * fy;
            accTy += rz * fx - rx * fz;
            accTz += rx * fy - ry * fx;
        }

        totalFx = Vector256.Sum(accFx);
        totalFy = Vector256.Sum(accFy);
        totalFz = Vector256.Sum(accFz);
        totalTx = Vector256.Sum(accTx);
        totalTy = Vector256.Sum(accTy);
        totalTz = Vector256.Sum(accTz);
        frontalArea = Vector256.Sum(accArea);
    }

    // ─── Scalar fallback ────────────────────────────────────────────

    private void ComputeScalar(in AeroContext ctx, float speed, Vector3 vHat,
        out float totalFx, out float totalFy, out float totalFz,
        out float totalTx, out float totalTy, out float totalTz,
        out float frontalArea)
    {
        int n = _faceCount;

        float velX = ctx.Velocity.X, velY = ctx.Velocity.Y, velZ = ctx.Velocity.Z;
        float wx = ctx.AngularVelocity.X, wy = ctx.AngularVelocity.Y, wz = ctx.AngularVelocity.Z;
        float comX = ctx.CenterOfMass.X, comY = ctx.CenterOfMass.Y, comZ = ctx.CenterOfMass.Z;
        float halfRho = (float)(0.5 * ctx.Atmosphere.Density);
        float invSoS = ctx.Atmosphere.SpeedOfSound > 0 ? 1f / (float)ctx.Atmosphere.SpeedOfSound : 0;
        float baseCp = Streamlining > 0f ? CpBase * (1f - 0.7f * Streamlining) : CpBase;
        float cfSkin = CfSkin;
        float cpMax = CpMax;
        float cdBluff = CdBluff;
        float subLim = SubsonicLimit;
        float invMachRange = SupersonicLimit > SubsonicLimit ? 1f / (SupersonicLimit - SubsonicLimit) : 0;
        float streamlining = Streamlining;
        bool hasSkin = cfSkin > 0;
        bool hasOmega = wx != 0 || wy != 0 || wz != 0;
        bool hasStreamlining = streamlining > 0f;

        totalFx = 0; totalFy = 0; totalFz = 0;
        totalTx = 0; totalTy = 0; totalTz = 0;
        frontalArea = 0;

        for (int i = 0; i < n; i++)
        {
            float pxi = _px[i], pyi = _py[i], pzi = _pz[i];
            float nxi = _nx[i], nyi = _ny[i], nzi = _nz[i];
            float vai = _visArea[i];

            float rx = pxi - comX, ry = pyi - comY, rz = pzi - comZ;

            float vx, vy, vz;
            if (hasOmega)
            {
                vx = velX + (wy * rz - wz * ry);
                vy = velY + (wz * rx - wx * rz);
                vz = velZ + (wx * ry - wy * rx);
            }
            else
            {
                vx = velX; vy = velY; vz = velZ;
            }

            float speedSq = vx * vx + vy * vy + vz * vz;
            if (speedSq < 0.0001f) continue;

            float invSpd = RsqrtNR(speedSq);
            float spd = speedSq * invSpd;
            float vhx = vx * invSpd, vhy = vy * invSpd, vhz = vz * invSpd;

            float cosA = vhx * nxi + vhy * nyi + vhz * nzi;
            float q = halfRho * speedSq;

            float cp;
            if (cosA > 0)
            {
                if (vai < 1e-6f) { _cpOut[i] = 0; continue; }

                float mach = spd * invSoS;
                float cpSub = cdBluff * cosA;
                float cpSup = cpMax * cosA * cosA;
                float t = (mach - subLim) * invMachRange;
                t = t < 0 ? 0 : (t > 1 ? 1 : t);
                t = t * t * (3f - 2f * t);
                cp = cpSub + (cpSup - cpSub) * t;

                if (hasStreamlining)
                {
                    float recovery = cosA * cosA;
                    float factor = 0.08f + 0.92f * recovery;
                    cp *= 1f - streamlining * (1f - factor);
                }

                frontalArea += vai * cosA;
            }
            else
            {
                cp = baseCp;
            }

            _cpOut[i] = cp;

            float pf = -cp * q * vai;
            float fx = nxi * pf, fy = nyi * pf, fz = nzi * pf;

            if (cosA > 0 && hasSkin)
            {
                float sinSq = 1f - cosA * cosA;
                if (sinSq > 1e-12f)
                {
                    float invSinA = RsqrtNR(sinSq);
                    float tx = vhx - cosA * nxi;
                    float ty = vhy - cosA * nyi;
                    float tz = vhz - cosA * nzi;
                    float fric = cfSkin * q * vai * invSinA;
                    fx += tx * fric; fy += ty * fric; fz += tz * fric;
                }
            }

            totalFx += fx; totalFy += fy; totalFz += fz;
            totalTx += ry * fz - rz * fy;
            totalTy += rz * fx - rx * fz;
            totalTz += rx * fy - ry * fx;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float RsqrtNR(float x)
    {
        float est = MathF.ReciprocalSqrtEstimate(x);
        return est * (1.5f - 0.5f * x * est * est);
    }
}
