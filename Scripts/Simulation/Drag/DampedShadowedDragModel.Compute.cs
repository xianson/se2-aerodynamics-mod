#pragma warning disable
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace AeroMod;

// The per-frame face loop (a grid without its force table yet): Compute, the AVX2 and scalar groups, base pressure.
public partial class DampedShadowedDragModel
{
    // ─── Compute ────────────────────────────────────────────────────

    /// <summary>The face loop (a grid without its force table yet) computes exactly what the table would - SumFaces, its
    /// rotation included (ExactTableValue). Off: the older per-frame loop (its own rules: full base suction on every face
    /// facing away, sides classed by the travel alone - no rotation damping in straight flight, and forces that jumped when
    /// the table arrived). Tests/AeroBench: torque.</summary>
    public static bool FaceLoopMatchesTable = true;
    private Vector3 _loopBakeDir; private int _loopBakeVersion = int.MinValue; private ForceTable _oneEntry; private readonly TableWork _loopWork = new TableWork();

    public AeroResult Compute(in AeroContext ctx)
    {
        if (FaceLoopMatchesTable)
        {
            if (ctx.Speed < 0.01f || ctx.Atmosphere.Density < 1e-8) return default;
            EnsureSoA(ctx.SurfaceCache.Faces, ctx.SurfaceCache.Version, ctx.Manifold);
            _shadowMap.Manifold = ctx.Manifold;
            // (what the air reaches, baked again only when the travel has turned 0.5 deg or the surface changed: the bake
            //  was half the frame on Red Ship - 7.0 ms against the older loop's 3.7)
            var vHat0 = ctx.Velocity / ctx.Speed;
            bool bake = _loopBakeVersion != _lastSurfaceVersion || Vector3.Dot(vHat0, _loopBakeDir) < 0.99996f;
            if (bake) { _loopBakeDir = vHat0; _loopBakeVersion = _lastSurfaceVersion; }
            return ExactTableValue(ctx, null, bake, _loopWork);   // (its own buffers: a table building in the background bakes into _work0)
        }
        float speed = ctx.Speed;
        if (speed < 0.01f || ctx.Atmosphere.Density < 1e-8)
            return default;

        var cache = ctx.SurfaceCache;
        Vector3 vHat = ctx.Velocity / speed;

        long t0 = AeroStats.Timestamp();
        _shadowMap.Manifold = ctx.Manifold;
        // The air's direction past the grid: against its motion. (It was given the motion itself: the map then saw
        // the REAR faces as the ones the air reaches and shadowed every front face - the faces' pressure drag, the
        // bulk of a bluff ship's drag, was all but gone: 8 kN on the cruising Jetliner.)
        _shadowMap.Update(ctx.GridAccessor, cache, -ctx.Velocity);

        EnsureSoA(cache.Faces, cache.Version, ctx.Manifold);

        if (_lastShadowVersion != _shadowMap.Version)
            BakeVisibility();
        AeroStats.SetShadow(AeroStats.ElapsedUs(t0));

        float totalFx = 0, totalFy = 0, totalFz = 0;
        float totalTx = 0, totalTy = 0, totalTz = 0;
        float frontalArea = 0;

        long t1 = AeroStats.Timestamp();

        // Pre-compute shared constants
        float halfRho = (float)(0.5 * ctx.Atmosphere.Density);
        float invSoS = ctx.Atmosphere.SpeedOfSound > 0 ? 1f / (float)ctx.Atmosphere.SpeedOfSound : 0;
        float baseCp = Streamlining > 0f ? CpBase * (1f - 0.7f * Streamlining) : CpBase;
        bool hasOmega = ctx.AngularVelocity.X != 0 || ctx.AngularVelocity.Y != 0 || ctx.AngularVelocity.Z != 0;
        bool hasSkin = CfSkin > 0;
        bool hasStreamlining = Streamlining > 0f;

        // Determine which directions are front-facing vs rear-facing
        for (int d = 0; d < 6; d++)
        {
            int count = _dirCount[d];
            if (count == 0) continue;

            float cosDir = Vector3.Dot(vHat, DirNormals[d]);

            if (PressureRecovery)
            {
                // (recovery: every face by its own slope - the per-face path; the rear groups' flat base pressure and the
                //  vector path know only suction)
                ComputeGroupScalar(ctx, d, cosDir > 0.001f, baseCp, halfRho, invSoS,
                    hasOmega, hasSkin, hasStreamlining,
                    ref totalFx, ref totalFy, ref totalFz,
                    ref totalTx, ref totalTy, ref totalTz,
                    ref frontalArea);
            }
            else if (cosDir > 0.001f)
            {
                // Front-facing group — full force computation
                if (Vector256.IsHardwareAccelerated && _dirPadded[d] >= 8)
                    ComputeGroupAvx2(ctx, d, cosDir, halfRho, invSoS, baseCp,
                        hasOmega, hasSkin, hasStreamlining,
                        ref totalFx, ref totalFy, ref totalFz,
                        ref totalTx, ref totalTy, ref totalTz,
                        ref frontalArea);
                else
                    ComputeGroupScalar(ctx, d, true, baseCp, halfRho, invSoS,
                        hasOmega, hasSkin, hasStreamlining,
                        ref totalFx, ref totalFy, ref totalFz,
                        ref totalTx, ref totalTy, ref totalTz,
                        ref frontalArea);
            }
            else
            {
                // Rear-facing group — just apply base pressure (cheap)
                ApplyBasePressure(ctx, d, baseCp, halfRho, hasOmega,
                    ref totalFx, ref totalFy, ref totalFz,
                    ref totalTx, ref totalTy, ref totalTz);
            }
        }

        // Wave drag: transonic drag rise from shock formation
        if (invSoS > 0)
        {
            float mach0 = speed * invSoS;
            // (the shape's own streamlining along the flow, as ForceTable.ShapeStreamlining: this drag against a flat face's
            //  of the same frontal area - it was the fixed Streamlining for every grid)
            float st = Streamlining;
            float qNow = halfRho * speed * speed;
            if (ForceTable.ShapeStreamliningOn && frontalArea > 1e-3f && qNow > 0f)
            {
                float dragNow = -(totalFx * vHat.X + totalFy * vHat.Y + totalFz * vHat.Z);
                st = ForceTable.StreamliningOf(dragNow / (qNow * frontalArea));
            }
            LastStreamlining = st;
            double mcrit = 0.7 + 0.1 * st;
            double peak = ForceTable.WavePeak(st);
            float waveMul = (float)CompressibleFlow.WaveDragMultiplier(mach0, mcrit, peak);
            totalFx *= waveMul; totalFy *= waveMul; totalFz *= waveMul;
            totalTx *= waveMul; totalTy *= waveMul; totalTz *= waveMul;
            frontalArea *= waveMul;
        }

        AeroStats.SetForce(AeroStats.ElapsedUs(t1));

        var totalForce = new Vector3(totalFx, totalFy, totalFz);
        var totalTorque = new Vector3(totalTx, totalTy, totalTz);

        // Write _cpOut in original face order
        for (int d = 0; d < 6; d++)
        {
            int start = _dirStart[d];
            int count = _dirCount[d];
            for (int j = 0; j < count; j++)
            {
                int oi = _origIndex[start + j];
                if (oi >= 0) _cpOut[oi] = _cpGrouped[start + j];
            }
        }

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

    // ─── Front-facing AVX2 group ─────────────────────────────────────

    private void ComputeGroupAvx2(in AeroContext ctx, int dir, float cosDir,
        float halfRho, float invSoS, float baseCp,
        bool hasOmega, bool hasSkin, bool hasStreamlining,
        ref float totalFx, ref float totalFy, ref float totalFz,
        ref float totalTx, ref float totalTy, ref float totalTz,
        ref float frontalArea)
    {
        int start = _dirStart[dir];
        int padded = _dirPadded[dir];

        var velXV = Vector256.Create(ctx.Velocity.X);
        var velYV = Vector256.Create(ctx.Velocity.Y);
        var velZV = Vector256.Create(ctx.Velocity.Z);
        var wxV = Vector256.Create(ctx.AngularVelocity.X);
        var wyV = Vector256.Create(ctx.AngularVelocity.Y);
        var wzV = Vector256.Create(ctx.AngularVelocity.Z);
        var comXV = Vector256.Create(ctx.CenterOfMass.X);
        var comYV = Vector256.Create(ctx.CenterOfMass.Y);
        var comZV = Vector256.Create(ctx.CenterOfMass.Z);
        var halfRhoV = Vector256.Create(halfRho);
        var baseCpV = Vector256.Create(baseCp);
        var cfSkinV = Vector256.Create(CfSkin);

        // Pre-compute Mach blend factor from CoM speed (constant across group)
        float comSpeed = ctx.Velocity.Length();
        float mach0 = comSpeed * invSoS;
        float t0 = (mach0 - SubsonicLimit) * (SupersonicLimit > SubsonicLimit ? 1f / (SupersonicLimit - SubsonicLimit) : 0f);
        t0 = t0 < 0 ? 0 : (t0 > 1 ? 1 : t0);
        t0 = t0 * t0 * (3f - 2f * t0);
        // Per-face Cp = cdBluff*cosA + (cpMax*cosA² - cdBluff*cosA)*t0
        //             = cosA * (cdBluff + (cpMax*cosA - cdBluff)*t0)
        //             = cosA * (cdBluff*(1-t0) + cpMax*cosA*t0)
        var cdBlend = Vector256.Create(CdBluff * (1f - t0));
        var cpBlend = Vector256.Create(CpMax * t0);
        var streamV = Vector256.Create(Streamlining);

        var zeroV = Vector256<float>.Zero;
        var oneV = Vector256.Create(1f);
        var halfV = Vector256.Create(0.5f);
        var onePointFiveV = Vector256.Create(1.5f);
        var epsV = Vector256.Create(1e-4f);
        var tEpsV = Vector256.Create(1e-12f);
        var minFracV = Vector256.Create(0.08f);
        var frac92V = Vector256.Create(0.92f);
        var negOneV = Vector256.Create(-1f);

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
        ref float cpRef = ref MemoryMarshal.GetReference(CollectionsMarshal.AsSpan(_cpGrouped));

        for (int i = 0; i < padded; i += 8)
        {
            int si = start + i;
            var px = Vector256.LoadUnsafe(ref Unsafe.Add(ref pxRef, si));
            var py = Vector256.LoadUnsafe(ref Unsafe.Add(ref pyRef, si));
            var pz = Vector256.LoadUnsafe(ref Unsafe.Add(ref pzRef, si));
            var nx = Vector256.LoadUnsafe(ref Unsafe.Add(ref nxRef, si));
            var ny = Vector256.LoadUnsafe(ref Unsafe.Add(ref nyRef, si));
            var nz = Vector256.LoadUnsafe(ref Unsafe.Add(ref nzRef, si));
            var vai = Vector256.LoadUnsafe(ref Unsafe.Add(ref vaRef, si));

            var rx = px - comXV;
            var ry = py - comYV;
            var rz = pz - comZV;

            // Per-face velocity: v + ω×r (FMA)
            Vector256<float> vx, vy, vz;
            if (Fma.IsSupported)
            {
                vx = Fma.MultiplyAddNegated(wzV, ry, Fma.MultiplyAdd(wyV, rz, velXV));
                vy = Fma.MultiplyAddNegated(wxV, rz, Fma.MultiplyAdd(wzV, rx, velYV));
                vz = Fma.MultiplyAddNegated(wyV, rx, Fma.MultiplyAdd(wxV, ry, velZV));
            }
            else
            {
                vx = velXV + (wyV * rz - wzV * ry);
                vy = velYV + (wzV * rx - wxV * rz);
                vz = velZV + (wxV * ry - wyV * rx);
            }

            // speedSq (FMA chain)
            Vector256<float> speedSq;
            if (Fma.IsSupported)
                speedSq = Fma.MultiplyAdd(vz, vz, Fma.MultiplyAdd(vy, vy, vx * vx));
            else
                speedSq = vx * vx + vy * vy + vz * vz;

            var safeSpeedSq = Vector256.Max(speedSq, epsV);

            // rsqrt + NR (FMA)
            var est = Avx.ReciprocalSqrt(safeSpeedSq);
            Vector256<float> invSpeed;
            if (Fma.IsSupported)
                invSpeed = est * Fma.MultiplyAddNegated(halfV * safeSpeedSq, est * est, onePointFiveV);
            else
                invSpeed = est * (onePointFiveV - halfV * safeSpeedSq * est * est);

            // vhat + cosAlpha (FMA)
            var vhx = vx * invSpeed;
            var vhy = vy * invSpeed;
            var vhz = vz * invSpeed;

            Vector256<float> cosA;
            if (Fma.IsSupported)
                cosA = Fma.MultiplyAdd(vhz, nz, Fma.MultiplyAdd(vhy, ny, vhx * nx));
            else
                cosA = vhx * nx + vhy * ny + vhz * nz;

            // q = ½ρv²
            var q = halfRhoV * speedSq;

            // Cp with pre-computed Mach blend: cosA * (cdBlend + cpBlend * cosA)
            Vector256<float> cpWindward;
            if (SubsonicSquareLaw)
                cpWindward = cosA * cosA * (cdBlend + cpBlend);
            else if (Fma.IsSupported)
                cpWindward = cosA * Fma.MultiplyAdd(cpBlend, cosA, cdBlend);
            else
                cpWindward = cosA * (cdBlend + cpBlend * cosA);

            // Streamlining (not with the square law: it is what the fudge stood in for)
            if (hasStreamlining && !SubsonicSquareLaw)
            {
                var cosA2 = cosA * cosA;
                Vector256<float> factor;
                if (Fma.IsSupported)
                    factor = Fma.MultiplyAdd(frac92V, cosA2, minFracV);
                else
                    factor = minFracV + frac92V * cosA2;
                cpWindward = cpWindward * (oneV - streamV * (oneV - factor));
            }

            // Front/rear select + validity
            var windward = Vector256.GreaterThan(cosA, zeroV);
            var cp = Vector256.ConditionalSelect(windward, cpWindward, baseCpV);
            var valid = Vector256.GreaterThanOrEqual(speedSq, epsV);
            cp = Vector256.ConditionalSelect(valid, cp, zeroV);

            cp.StoreUnsafe(ref Unsafe.Add(ref cpRef, si));

            accArea += Vector256.ConditionalSelect(windward & valid, vai * cosA, zeroV);

            // Force = -Cp * q * visArea * normal
            var pf = negOneV * cp * q * vai;
            var fx = nx * pf;
            var fy = ny * pf;
            var fz = nz * pf;

            // Skin friction (single rsqrt eliminated — use tangent directly)
            if (hasSkin)
            {
                // tangent = vhat - cosA*normal (unnormalized, length = sinA)
                // friction force = cfSkin * q * area * tangent_hat
                //                = cfSkin * q * area * tangent / sinA
                //                = cfSkin * q * area / sinA * (vhat - cosA*n)
                // sinA = sqrt(1 - cosA²), use rsqrt(1 - cosA²) for 1/sinA

                Vector256<float> cosA2;
                if (Fma.IsSupported)
                    cosA2 = Fma.MultiplyAddNegated(cosA, cosA, oneV);
                else
                    cosA2 = oneV - cosA * cosA;
                var sinValid = Vector256.GreaterThan(cosA2, tEpsV);
                var safeSinSq = Vector256.Max(cosA2, epsV);

                var sinEst = Avx.ReciprocalSqrt(safeSinSq);
                Vector256<float> invSinA;
                if (Fma.IsSupported)
                    invSinA = sinEst * Fma.MultiplyAddNegated(halfV * safeSinSq, sinEst * sinEst, onePointFiveV);
                else
                    invSinA = sinEst * (onePointFiveV - halfV * safeSinSq * sinEst * sinEst);

                var fric = cfSkinV * q * vai * invSinA;
                var fricMask = windward & sinValid & valid;

                // tangent = vhat - cosA * normal (FMA)
                Vector256<float> tx, ty, tz;
                if (Fma.IsSupported)
                {
                    tx = Fma.MultiplySubtract(cosA, nx, vhx);   // (cosA n - v: friction opposes the motion)
                    ty = Fma.MultiplySubtract(cosA, ny, vhy);
                    tz = Fma.MultiplySubtract(cosA, nz, vhz);
                }
                else
                {
                    tx = cosA * nx - vhx;   // (friction opposes the motion: -tangent - it was +, pushing every hull along its travel)
                    ty = cosA * ny - vhy;
                    tz = cosA * nz - vhz;
                }

                if (Fma.IsSupported)
                {
                    fx += Vector256.ConditionalSelect(fricMask, Fma.MultiplyAdd(tx, fric, zeroV), zeroV);
                    fy += Vector256.ConditionalSelect(fricMask, Fma.MultiplyAdd(ty, fric, zeroV), zeroV);
                    fz += Vector256.ConditionalSelect(fricMask, Fma.MultiplyAdd(tz, fric, zeroV), zeroV);
                }
                else
                {
                    fx += Vector256.ConditionalSelect(fricMask, tx * fric, zeroV);
                    fy += Vector256.ConditionalSelect(fricMask, ty * fric, zeroV);
                    fz += Vector256.ConditionalSelect(fricMask, tz * fric, zeroV);
                }
            }

            fx = Vector256.ConditionalSelect(valid, fx, zeroV);
            fy = Vector256.ConditionalSelect(valid, fy, zeroV);
            fz = Vector256.ConditionalSelect(valid, fz, zeroV);

            // Accumulate force + torque (FMA for cross product)
            accFx += fx; accFy += fy; accFz += fz;
            if (Fma.IsSupported)
            {
                accTx = Fma.MultiplyAdd(ry, fz, Fma.MultiplyAddNegated(rz, fy, accTx));
                accTy = Fma.MultiplyAdd(rz, fx, Fma.MultiplyAddNegated(rx, fz, accTy));
                accTz = Fma.MultiplyAdd(rx, fy, Fma.MultiplyAddNegated(ry, fx, accTz));
            }
            else
            {
                accTx += ry * fz - rz * fy;
                accTy += rz * fx - rx * fz;
                accTz += rx * fy - ry * fx;
            }
        }

        totalFx += Vector256.Sum(accFx);
        totalFy += Vector256.Sum(accFy);
        totalFz += Vector256.Sum(accFz);
        totalTx += Vector256.Sum(accTx);
        totalTy += Vector256.Sum(accTy);
        totalTz += Vector256.Sum(accTz);
        frontalArea += Vector256.Sum(accArea);
    }

    // ─── Rear-facing base pressure (cheap — no shadow, no Mach blend) ──

    private void ApplyBasePressure(in AeroContext ctx, int dir, float baseCp, float halfRho,
        bool hasOmega,
        ref float totalFx, ref float totalFy, ref float totalFz,
        ref float totalTx, ref float totalTy, ref float totalTz)
    {
        int start = _dirStart[dir];
        int count = _dirCount[dir];
        float comX = ctx.CenterOfMass.X, comY = ctx.CenterOfMass.Y, comZ = ctx.CenterOfMass.Z;
        float velX = ctx.Velocity.X, velY = ctx.Velocity.Y, velZ = ctx.Velocity.Z;
        float wx = ctx.AngularVelocity.X, wy = ctx.AngularVelocity.Y, wz = ctx.AngularVelocity.Z;

        for (int j = 0; j < count; j++)
        {
            int gi = start + j;
            float vai = _visArea[gi];
            if (vai < 1e-6f) { _cpGrouped[gi] = 0; continue; }

            float pxi = _px[gi], pyi = _py[gi], pzi = _pz[gi];
            float nxi = _nx[gi], nyi = _ny[gi], nzi = _nz[gi];

            float rx = pxi - comX, ry = pyi - comY, rz = pzi - comZ;

            float vx, vy, vz;
            if (hasOmega)
            {
                vx = velX + (wy * rz - wz * ry);
                vy = velY + (wz * rx - wx * rz);
                vz = velZ + (wx * ry - wy * rx);
            }
            else { vx = velX; vy = velY; vz = velZ; }

            float speedSq = vx * vx + vy * vy + vz * vz;
            if (speedSq < 1e-4f) { _cpGrouped[gi] = 0; continue; }

            _cpGrouped[gi] = baseCp;

            float q = halfRho * speedSq;
            float pf = -baseCp * q * vai;
            float fx = nxi * pf, fy = nyi * pf, fz = nzi * pf;

            totalFx += fx; totalFy += fy; totalFz += fz;
            totalTx += ry * fz - rz * fy;
            totalTy += rz * fx - rx * fz;
            totalTz += rx * fy - ry * fx;
        }
    }

    // ─── Scalar fallback for front-facing group ──────────────────────

    private void ComputeGroupScalar(in AeroContext ctx, int dir, bool frontFacing,
        float baseCp, float halfRho, float invSoS,
        bool hasOmega, bool hasSkin, bool hasStreamlining,
        ref float totalFx, ref float totalFy, ref float totalFz,
        ref float totalTx, ref float totalTy, ref float totalTz,
        ref float frontalArea)
    {
        int start = _dirStart[dir];
        int count = _dirCount[dir];
        float comX = ctx.CenterOfMass.X, comY = ctx.CenterOfMass.Y, comZ = ctx.CenterOfMass.Z;
        float velX = ctx.Velocity.X, velY = ctx.Velocity.Y, velZ = ctx.Velocity.Z;
        float wx = ctx.AngularVelocity.X, wy = ctx.AngularVelocity.Y, wz = ctx.AngularVelocity.Z;
        float cfSkin = CfSkin, cpMax = CpMax, cdBluff = CdBluff;
        float subLim = SubsonicLimit;
        float invMachRange = SupersonicLimit > SubsonicLimit ? 1f / (SupersonicLimit - SubsonicLimit) : 0;
        float streamlining = Streamlining;

        for (int j = 0; j < count; j++)
        {
            int gi = start + j;
            float pxi = _px[gi], pyi = _py[gi], pzi = _pz[gi];
            float nxi = _nx[gi], nyi = _ny[gi], nzi = _nz[gi];
            float vai = _visArea[gi];

            float rx = pxi - comX, ry = pyi - comY, rz = pzi - comZ;

            float vx, vy, vz;
            if (hasOmega)
            {
                vx = velX + (wy * rz - wz * ry);
                vy = velY + (wz * rx - wx * rz);
                vz = velZ + (wx * ry - wy * rx);
            }
            else { vx = velX; vy = velY; vz = velZ; }

            float speedSq = vx * vx + vy * vy + vz * vz;
            if (speedSq < 0.0001f) { _cpGrouped[gi] = 0; continue; }

            float invSpd = RsqrtNR(speedSq);
            float spd = speedSq * invSpd;
            float vhx = vx * invSpd, vhy = vy * invSpd, vhz = vz * invSpd;

            float cosA = vhx * nxi + vhy * nyi + vhz * nzi;
            float q = halfRho * speedSq;

            float cp;
            if (cosA > 0)
            {
                if (vai < 1e-6f) { _cpGrouped[gi] = 0; continue; }

                float mach = spd * invSoS;
                float cpSub = SubsonicSquareLaw ? cdBluff * cosA * cosA : cdBluff * cosA;
                float cpSup = cpMax * cosA * cosA;
                float t = (mach - subLim) * invMachRange;
                t = t < 0 ? 0 : (t > 1 ? 1 : t);
                t = t * t * (3f - 2f * t);
                cp = cpSub + (cpSup - cpSub) * t;

                if (hasStreamlining && !SubsonicSquareLaw)
                {
                    float recovery = cosA * cosA;
                    float factor = 0.08f + 0.92f * recovery;
                    cp *= 1f - streamlining * (1f - factor);
                }

                frontalArea += vai * cosA;
            }
            else
            {
                cp = PressureRecovery ? LeeCp(cosA, baseCp, cdBluff) : baseCp;
            }

            _cpGrouped[gi] = cp;

            float pf = -cp * q * vai;
            float fx = nxi * pf, fy = nyi * pf, fz = nzi * pf;

            if (cosA > 0 && hasSkin)
            {
                float sinSq = 1f - cosA * cosA;
                if (sinSq > 1e-12f)
                {
                    float invSinA = RsqrtNR(sinSq);
                    float tx = cosA * nxi - vhx;   // (friction opposes the motion: -tangent - it was +, pushing every hull along its travel)
                    float ty = cosA * nyi - vhy;
                    float tz = cosA * nzi - vhz;
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
