using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Aero.Core.Grids;

namespace Aero.Core.Aero;

/// <summary>
/// Performance-optimized per-face drag model with shadow occlusion and streamlining.
///
/// Full-fidelity equivalent of DampedShadowedDragModel, but:
///   - SOA layout for face data — cache-friendly sequential access
///   - SIMD: Vector256 processes 8 faces per iteration (AVX2 / fallback)
///   - rsqrt + Newton-Raphson — avoids expensive sqrt/divide
///   - Branchless Cp — clamped smoothstep with SIMD masking
///   - Shadow occlusion via DDA ray marching (cached per flow direction)
///   - Visibility baked into area array — one fewer SIMD load per iteration
///   - Streamlining: d'Alembert pressure recovery on shallow-angle faces
///   - sin²α shortcut for skin friction — avoids redundant tangent length computation
///   - Scalar fallback for non-AVX2 hardware
/// </summary>
public class FastDragModel : AeroDragModelBase
{
    public override string Name => "Fast Per-Face";
    public override string Description => "SIMD8 + SOA + shadow + streamlining per-face";

    // ─── Tuning (same defaults as DampedShadowedDragModel) ──────────

    public float CpMax { get; set; } = 2.0f;
    public float CdBluff { get; set; } = 1.1f;
    public float CpBase { get; set; } = -0.15f;
    public float CfSkin { get; set; } = 0.005f;
    public float SubsonicLimit { get; set; } = 0.6f;
    public float SupersonicLimit { get; set; } = 1.2f;

    /// <summary>
    /// Streamlining factor [0,1]. At 1.0 (full streamlined), faces at shallow angles
    /// to the flow get ~8% of Newtonian Cp (modeling pressure recovery on tapered aft sections).
    /// At 0.0, disabled (bluff body). Default: 0.0.
    /// </summary>
    public float Streamlining { get; set; } = 0f;

    // ─── Shadow map ─────────────────────────────────────────────────

    private readonly ShadowMap _shadowMap = new();

    /// <summary>Access the shadow map for inspection/configuration.</summary>
    public ShadowMap ShadowMap => _shadowMap;

    /// <summary>Shadow direction threshold in radians (~5° default).</summary>
    public float DirectionThreshold
    {
        get => _shadowMap.DirectionThreshold;
        set => _shadowMap.DirectionThreshold = value;
    }

    /// <summary>
    /// Notify the shadow map that blocks changed.
    /// </summary>
    public void OnBlocksChanged(Grid grid, ISurfaceProvider provider,
        Vector3 flowDirection, in BlocksChangedArgs args)
    {
        _shadowMap.OnBlocksChanged(grid, provider, flowDirection, args);
    }

    // ─── SOA cache (padded to multiple of 8) ──────────────────────

    private float[] _px = [], _py = [], _pz = [];
    private float[] _nx = [], _ny = [], _nz = [];
    private float[] _area = [];    // raw face area
    private float[] _visArea = []; // area × visibility (baked for SIMD — one fewer load)
    private int _soaCount;
    private int _soaPadded; // rounded up to multiple of 8
    private int _lastShadowVersion = -1;

    private void EnsureSOA(IReadOnlyList<SurfaceFace> faces)
    {
        int n = faces.Count;
        if (_soaCount == n && _px.Length >= _soaPadded) return;

        int padded = (n + 7) & ~7; // round up to multiple of 8
        _soaPadded = padded;

        // Allocate padded arrays (extra slots zeroed — contributes zero force)
        _px = new float[padded]; _py = new float[padded]; _pz = new float[padded];
        _nx = new float[padded]; _ny = new float[padded]; _nz = new float[padded];
        _area = new float[padded];    // padding slots have area=0, so zero contribution
        _visArea = new float[padded]; // will be filled by BakeVisibility

        if (faces is List<SurfaceFace> list)
        {
            var span = CollectionsMarshal.AsSpan(list);
            for (int i = 0; i < n; i++)
            {
                ref readonly var f = ref span[i];
                _px[i] = f.Position.X; _py[i] = f.Position.Y; _pz[i] = f.Position.Z;
                _nx[i] = f.Normal.X;   _ny[i] = f.Normal.Y;   _nz[i] = f.Normal.Z;
                _area[i] = f.Area;
                _visArea[i] = f.Area; // default: fully visible (vis=1)
            }
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                var f = faces[i];
                _px[i] = f.Position.X; _py[i] = f.Position.Y; _pz[i] = f.Position.Z;
                _nx[i] = f.Normal.X;   _ny[i] = f.Normal.Y;   _nz[i] = f.Normal.Z;
                _area[i] = f.Area;
                _visArea[i] = f.Area;
            }
        }

        _soaCount = n;
        _lastShadowVersion = -1; // force visibility rebake
    }

    /// <summary>Bake visibility × area into the visArea array. Only called when shadow changes.</summary>
    private void BakeVisibility()
    {
        var visFactor = _shadowMap.VisibilityFactor;
        int n = _soaCount;
        int visLen = visFactor.Length;
        ref float aRef = ref MemoryMarshal.GetArrayDataReference(_area);
        ref float vaRef = ref MemoryMarshal.GetArrayDataReference(_visArea);
        for (int i = 0; i < n; i++)
        {
            float v = i < visLen ? visFactor[i] : 1f;
            Unsafe.Add(ref vaRef, i) = Unsafe.Add(ref aRef, i) * v;
        }
        _lastShadowVersion = _shadowMap.Version;
    }

    // ─── Compute ──────────────────────────────────────────────────

    public override AeroResult Compute(in AeroContext ctx)
    {
        float comSpeed = ctx.Velocity.Length();
        if (comSpeed < 0.01f && ctx.AngularVelocity.LengthSquared() < 1e-8f)
            return default;
        if (ctx.Atmosphere.Density < 1e-8)
            return default;

        EnsureSOA(ctx.SurfaceCache.Faces);

        // Update shadow map; only rebake visArea when shadow actually changed
        _shadowMap.Update(ctx.Grid, ctx.SurfaceCache, ctx.Velocity);
        if (_lastShadowVersion != _shadowMap.Version)
            BakeVisibility();

        float totalFx, totalFy, totalFz;
        float totalTx, totalTy, totalTz;
        float frontalArea;

        if (Vector256.IsHardwareAccelerated && _soaPadded >= 8)
            ComputeSimd(ctx, comSpeed, out totalFx, out totalFy, out totalFz,
                out totalTx, out totalTy, out totalTz, out frontalArea);
        else
            ComputeScalar(ctx, comSpeed, out totalFx, out totalFy, out totalFz,
                out totalTx, out totalTy, out totalTz, out frontalArea);

        Vector3 comVHat = comSpeed > 0.01f ? ctx.Velocity / comSpeed : Vector3.UnitX;
        double comMach = ctx.Atmosphere.SpeedOfSound > 0 ? comSpeed / ctx.Atmosphere.SpeedOfSound : 0;
        double comQ = ctx.Atmosphere.GetDynamicPressure(comSpeed);

        return NewtonianDragModel.BuildResult(
            new Vector3(totalFx, totalFy, totalFz),
            new Vector3(totalTx, totalTy, totalTz),
            comVHat, frontalArea, comMach, comQ);
    }

    // ─── SIMD path: 8 faces per iteration ─────────────────────────

    private void ComputeSimd(in AeroContext ctx, float comSpeed,
        out float totalFx, out float totalFy, out float totalFz,
        out float totalTx, out float totalTy, out float totalTz,
        out float frontalArea)
    {
        int n = _soaPadded;

        // Broadcast constants
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
        // Base pressure adjusted for streamlining (streamlined aft → less suction)
        float cpBaseVal = Streamlining > 0f ? CpBase * (1f - 0.7f * Streamlining) : CpBase;
        var cpBaseV = Vector256.Create(cpBaseVal);
        var cfSkinV = Vector256.Create(CfSkin);
        var cpMaxV = Vector256.Create(CpMax);
        var cdBluffV = Vector256.Create(CdBluff);
        var subLimV = Vector256.Create(SubsonicLimit);
        var invMachRangeV = Vector256.Create(SupersonicLimit > SubsonicLimit ? 1f / (SupersonicLimit - SubsonicLimit) : 0f);
        bool hasOmega = ctx.AngularVelocity.X != 0 || ctx.AngularVelocity.Y != 0 || ctx.AngularVelocity.Z != 0;
        bool hasSkin = CfSkin > 0;
        bool hasStreamlining = Streamlining > 0f;
        var streamV = Vector256.Create(Streamlining);
        var minFracV = Vector256.Create(0.08f);
        var frac92V = Vector256.Create(0.92f);

        var zeroV = Vector256<float>.Zero;
        var oneV = Vector256.Create(1f);
        var halfV = Vector256.Create(0.5f);
        var onePointFiveV = Vector256.Create(1.5f);
        var threeV = Vector256.Create(3f);
        var twoV = Vector256.Create(2f);
        var epsV = Vector256.Create(1e-4f);
        var tEpsV = Vector256.Create(1e-12f);

        // Accumulators
        var accFx = zeroV; var accFy = zeroV; var accFz = zeroV;
        var accTx = zeroV; var accTy = zeroV; var accTz = zeroV;
        var accArea = zeroV;

        ref float pxRef = ref MemoryMarshal.GetArrayDataReference(_px);
        ref float pyRef = ref MemoryMarshal.GetArrayDataReference(_py);
        ref float pzRef = ref MemoryMarshal.GetArrayDataReference(_pz);
        ref float nxRef = ref MemoryMarshal.GetArrayDataReference(_nx);
        ref float nyRef = ref MemoryMarshal.GetArrayDataReference(_ny);
        ref float nzRef = ref MemoryMarshal.GetArrayDataReference(_nz);
        ref float vaRef = ref MemoryMarshal.GetArrayDataReference(_visArea);

        for (int i = 0; i < n; i += 8)
        {
            // Load 8 faces — 7 arrays (visArea bakes vis×area, saving one load)
            var px = Vector256.LoadUnsafe(ref Unsafe.Add(ref pxRef, i));
            var py = Vector256.LoadUnsafe(ref Unsafe.Add(ref pyRef, i));
            var pz = Vector256.LoadUnsafe(ref Unsafe.Add(ref pzRef, i));
            var nx = Vector256.LoadUnsafe(ref Unsafe.Add(ref nxRef, i));
            var ny = Vector256.LoadUnsafe(ref Unsafe.Add(ref nyRef, i));
            var nz = Vector256.LoadUnsafe(ref Unsafe.Add(ref nzRef, i));
            var vai = Vector256.LoadUnsafe(ref Unsafe.Add(ref vaRef, i)); // area × visibility

            // r = pos - CoM
            var rx = px - comXV;
            var ry = py - comYV;
            var rz = pz - comZV;

            // faceVel = vel + ω × r
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

            // speedSq
            var speedSq = vx * vx + vy * vy + vz * vz;

            // Clamp speedSq to avoid NaN (padding slots have area=0 so zero contribution)
            var safeSpeedSq = Vector256.Max(speedSq, epsV);

            // rsqrt + Newton-Raphson: ~8 cycles vs ~26 for div+sqrt
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

            // speed = speedSq * invSpeed ≈ sqrt(speedSq)
            var speed = safeSpeedSq * invSpeed;

            // vHat
            var vhx = vx * invSpeed;
            var vhy = vy * invSpeed;
            var vhz = vz * invSpeed;

            // cosAlpha
            var cosA = vhx * nx + vhy * ny + vhz * nz;

            // q = ½ρv²
            var q = halfRhoV * speedSq;

            // Branchless Cp: compute both sub and sup, blend
            var mach = speed * invSoSV;
            var cpSub = cdBluffV * cosA;
            var cpSup = cpMaxV * cosA * cosA;
            var t = (mach - subLimV) * invMachRangeV;
            t = Vector256.Max(zeroV, Vector256.Min(oneV, t)); // clamp [0,1]
            t = t * t * (threeV - twoV * t); // smoothstep
            var cpWindward = cpSub + (cpSup - cpSub) * t;

            // Streamlining: reduce Cp on shallow-angle faces (d'Alembert recovery)
            if (hasStreamlining)
            {
                var recovery = cosA * cosA;
                var factor = minFracV + frac92V * recovery;
                cpWindward = cpWindward * (oneV - streamV * (oneV - factor));
            }

            // cp = cosA > 0 ? cpWindward : cpBase
            // Shadow occlusion is baked into vai (visArea), so cpWindward×vai naturally zeros shadowed faces
            var windward = Vector256.GreaterThan(cosA, zeroV);
            var cp = Vector256.ConditionalSelect(windward, cpWindward, cpBaseV);

            // frontalArea accumulation (only windward; vai already includes shadow attenuation)
            accArea += Vector256.ConditionalSelect(windward, vai * cosA, zeroV);

            // Pressure force: F = -Cp × q × visArea × normal
            var pf = zeroV - cp * q * vai;
            var fx = nx * pf;
            var fy = ny * pf;
            var fz = nz * pf;

            // Skin friction: use sin²α = 1 - cos²α to avoid redundant tangent-length computation
            if (hasSkin)
            {
                var sinSq = oneV - cosA * cosA;
                var sinValid = Vector256.GreaterThan(sinSq, tEpsV);
                var safeSinSq = Vector256.Max(sinSq, epsV);

                // rsqrt(sin²α) for normalizing tangent direction
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

                // tangent direction (unnormalized, |t| = sinα)
                var tx = vhx - cosA * nx;
                var ty = vhy - cosA * ny;
                var tz = vhz - cosA * nz;

                // friction force = Cf × q × visArea × invSinA × tangent
                var fric = cfSkinV * q * vai * invSinA;
                var fricMask = windward & sinValid;
                fx += Vector256.ConditionalSelect(fricMask, tx * fric, zeroV);
                fy += Vector256.ConditionalSelect(fricMask, ty * fric, zeroV);
                fz += Vector256.ConditionalSelect(fricMask, tz * fric, zeroV);
            }

            // Mask out invalid faces (speedSq < threshold — mostly just padding)
            var valid = Vector256.GreaterThanOrEqual(speedSq, epsV);
            fx = Vector256.ConditionalSelect(valid, fx, zeroV);
            fy = Vector256.ConditionalSelect(valid, fy, zeroV);
            fz = Vector256.ConditionalSelect(valid, fz, zeroV);

            // Accumulate force
            accFx += fx; accFy += fy; accFz += fz;

            // Accumulate torque = r × F
            accTx += ry * fz - rz * fy;
            accTy += rz * fx - rx * fz;
            accTz += rx * fy - ry * fx;
        }

        // Horizontal reduction
        totalFx = Vector256.Sum(accFx);
        totalFy = Vector256.Sum(accFy);
        totalFz = Vector256.Sum(accFz);
        totalTx = Vector256.Sum(accTx);
        totalTy = Vector256.Sum(accTy);
        totalTz = Vector256.Sum(accTz);
        frontalArea = Vector256.Sum(accArea);
    }

    // ─── Scalar fallback ──────────────────────────────────────────

    private void ComputeScalar(in AeroContext ctx, float comSpeed,
        out float totalFx, out float totalFy, out float totalFz,
        out float totalTx, out float totalTy, out float totalTz,
        out float frontalArea)
    {
        int n = _soaCount;

        float velX = ctx.Velocity.X, velY = ctx.Velocity.Y, velZ = ctx.Velocity.Z;
        float wx = ctx.AngularVelocity.X, wy = ctx.AngularVelocity.Y, wz = ctx.AngularVelocity.Z;
        float comX = ctx.CenterOfMass.X, comY = ctx.CenterOfMass.Y, comZ = ctx.CenterOfMass.Z;
        float halfRho = (float)(0.5 * ctx.Atmosphere.Density);
        float invSoS = ctx.Atmosphere.SpeedOfSound > 0 ? 1f / (float)ctx.Atmosphere.SpeedOfSound : 0;
        float cpBase = Streamlining > 0f ? CpBase * (1f - 0.7f * Streamlining) : CpBase;
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

        ref float pxRef = ref MemoryMarshal.GetArrayDataReference(_px);
        ref float pyRef = ref MemoryMarshal.GetArrayDataReference(_py);
        ref float pzRef = ref MemoryMarshal.GetArrayDataReference(_pz);
        ref float nxRef = ref MemoryMarshal.GetArrayDataReference(_nx);
        ref float nyRef = ref MemoryMarshal.GetArrayDataReference(_ny);
        ref float nzRef = ref MemoryMarshal.GetArrayDataReference(_nz);
        ref float vaRef = ref MemoryMarshal.GetArrayDataReference(_visArea);

        for (int i = 0; i < n; i++)
        {
            float pxi = Unsafe.Add(ref pxRef, i);
            float pyi = Unsafe.Add(ref pyRef, i);
            float pzi = Unsafe.Add(ref pzRef, i);
            float nxi = Unsafe.Add(ref nxRef, i);
            float nyi = Unsafe.Add(ref nyRef, i);
            float nzi = Unsafe.Add(ref nzRef, i);
            float vai = Unsafe.Add(ref vaRef, i); // area × visibility

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

            float invSpeed = RsqrtNR(speedSq);
            float speed = speedSq * invSpeed;
            float vhx = vx * invSpeed, vhy = vy * invSpeed, vhz = vz * invSpeed;

            float cosA = vhx * nxi + vhy * nyi + vhz * nzi;
            float q = halfRho * speedSq;

            float cp;
            if (cosA > 0)
            {
                // Shadow occlusion: skip fully shadowed faces (vai ≈ 0)
                if (vai < 1e-6f) continue;

                float mach = speed * invSoS;
                float cpSub = cdBluff * cosA;
                float cpSup = cpMax * cosA * cosA;
                float t = (mach - subLim) * invMachRange;
                t = t < 0 ? 0 : (t > 1 ? 1 : t);
                t = t * t * (3f - 2f * t);
                cp = cpSub + (cpSup - cpSub) * t;

                // Streamlining: reduce Cp on shallow-angle faces
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
                cp = cpBase;
            }

            // Force uses vai (area × visibility) — shadow attenuation baked in
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
