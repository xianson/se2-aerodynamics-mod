using System.Numerics;
using Aero.Core.Atmosphere;

namespace Aero.Core.Aero;

/// <summary>
/// Compressible flow tier. Each tier adds features on top of the previous.
/// </summary>
public enum CompressibleTier
{
    /// <summary>Rayleigh Pitot CpMax, Karman-Tsien subsonic correction, wave drag multiplier.</summary>
    Tier1,

    /// <summary>Tier1 + Mach/Re-dependent skin friction (Schlichting + White).</summary>
    Tier2,

    /// <summary>Tier2 + Mach-dependent base pressure, oblique shock Cp (LUT), soft Cd cap.</summary>
    Tier3,
}

/// <summary>
/// Drag model with compressible flow corrections wired in from CompressibleFlow.cs.
///
/// Builds on the same panel-method approach as NewtonianDragModel but replaces
/// the hand-tuned Cp/Cf/CpBase with physics-based compressible relations.
///
/// Tier 1: Rayleigh Pitot CpMax(M,gamma), Karman-Tsien correction, wave drag multiplier
/// Tier 2: + Mach/Reynolds-dependent skin friction (Schlichting + White compressibility)
/// Tier 3: + Mach-dependent base pressure, oblique shock Cp via LUT, tanh soft cap
/// </summary>
public class CompressibleDragModel : AeroDragModelBase
{
    public CompressibleTier Tier { get; set; } = CompressibleTier.Tier1;

    public override string Name => $"Compressible T{(int)Tier + 1}";
    public override string Description => Tier switch
    {
        CompressibleTier.Tier1 => "Rayleigh CpMax + Karman-Tsien + wave drag",
        CompressibleTier.Tier2 => "T1 + Mach-dependent Cf(Re,M)",
        CompressibleTier.Tier3 => "T2 + base pressure(M) + oblique shock LUT + soft cap",
        _ => "Compressible flow",
    };

    /// <summary>Whether Compute() uses per-group (fast) or per-face (accurate torque).</summary>
    public bool UsePerGroup { get; set; } = true;

    // ─── Parameters ─────────────────────────────────────────────────

    /// <summary>Heat capacity ratio. Default 1.4 for air.</summary>
    public double Gamma { get; set; } = 1.4;

    /// <summary>Subsonic bluff body Cp (incompressible). Default 1.1.</summary>
    public float CdBluff { get; set; } = 1.1f;

    /// <summary>Base pressure on rear faces. Default -0.15. Only used in Tier1/Tier2.</summary>
    public float CpBase { get; set; } = -0.15f;

    /// <summary>Incompressible skin friction. Default 0.005. Used directly in Tier1.</summary>
    public float CfSkin { get; set; } = 0.005f;

    /// <summary>Reference length for Reynolds number (m). Default 2.5 (one block).</summary>
    public float ReferenceLength { get; set; } = 2.5f;

    /// <summary>Below this Mach: pure subsonic model.</summary>
    public float SubsonicLimit { get; set; } = 0.6f;

    /// <summary>Above this Mach: pure supersonic model.</summary>
    public float SupersonicLimit { get; set; } = 1.2f;

    /// <summary>
    /// Enable position-dependent Cp for physically correct CoP shift with AoA.
    /// Per-face mode only. Modulates Cp across each face group so upstream faces
    /// get higher pressure than downstream faces, shifting CoP forward of geometric center.
    /// Default true.
    /// </summary>
    public bool EnablePressureGradient { get; set; } = true;

    // ─── Cached grid geometry (for pressure gradient) ───────────────

    private Vector3 _gridCenter;
    private Vector3 _gridHalfSize;
    private bool _gridGeomCached;

    // ─── Oblique shock LUT (Tier 3) ─────────────────────────────────

    private float[]? _obliqueCpLut;
    private double _lutGamma;
    private const float LutMachMin = 1.2f;
    private const float LutMachMax = 10.0f;
    private const float LutMachStep = 0.1f;
    private const int LutMachBins = (int)((LutMachMax - LutMachMin) / LutMachStep) + 1; // 89
    private const float LutThetaMax = 50f * MathF.PI / 180f;
    private const float LutThetaStep = 1f * MathF.PI / 180f; // 1 degree
    private const int LutThetaBins = (int)(50f / 1f) + 1; // 51

    private void EnsureObliqueLut()
    {
        if (_obliqueCpLut != null && _lutGamma == Gamma)
            return;

        _lutGamma = Gamma;
        _obliqueCpLut = new float[LutMachBins * LutThetaBins];

        for (int mi = 0; mi < LutMachBins; mi++)
        {
            double m = LutMachMin + mi * LutMachStep;
            double cpMax = CompressibleFlow.ModifiedNewtonianCpMax(m, Gamma);

            for (int ti = 0; ti < LutThetaBins; ti++)
            {
                double theta = ti * LutThetaStep;
                double beta = CompressibleFlow.ObliqueShockBeta(m, theta, Gamma);

                float cp;
                if (double.IsNaN(beta))
                {
                    // Detached shock — use modified Newtonian
                    double sinT = System.Math.Sin(theta);
                    cp = (float)(cpMax * sinT * sinT);
                }
                else
                {
                    double m1n = m * System.Math.Sin(beta);
                    double p2p1 = CompressibleFlow.NormalShockPressureRatio(m1n, Gamma);
                    cp = (float)(2.0 / (Gamma * m * m) * (p2p1 - 1.0));
                    if (cp < 0) cp = 0;
                }

                _obliqueCpLut[mi * LutThetaBins + ti] = cp;
            }
        }
    }

    /// <summary>
    /// Look up oblique shock Cp from the pre-built table with bilinear interpolation.
    /// Falls back to modified Newtonian at the table boundaries.
    /// </summary>
    private float LookupObliqueCp(float mach, float cosAlpha)
    {
        float theta = MathF.Asin(cosAlpha);

        // Clamp to table range
        float mi = (mach - LutMachMin) / LutMachStep;
        float ti = theta / LutThetaStep;

        int mi0 = (int)mi;
        int ti0 = (int)ti;

        if (mi0 < 0) mi0 = 0;
        if (mi0 >= LutMachBins - 1) mi0 = LutMachBins - 2;
        if (ti0 < 0) ti0 = 0;
        if (ti0 >= LutThetaBins - 1) ti0 = LutThetaBins - 2;

        float mf = mi - mi0;
        float tf = ti - ti0;
        mf = System.Math.Clamp(mf, 0f, 1f);
        tf = System.Math.Clamp(tf, 0f, 1f);

        int idx00 = mi0 * LutThetaBins + ti0;
        int idx01 = idx00 + 1;
        int idx10 = (mi0 + 1) * LutThetaBins + ti0;
        int idx11 = idx10 + 1;

        float cp00 = _obliqueCpLut![idx00];
        float cp01 = _obliqueCpLut[idx01];
        float cp10 = _obliqueCpLut[idx10];
        float cp11 = _obliqueCpLut[idx11];

        float cp0 = cp00 + (cp01 - cp00) * tf;
        float cp1 = cp10 + (cp11 - cp10) * tf;
        return cp0 + (cp1 - cp0) * mf;
    }

    private void EnsureGridGeometry(in AeroContext ctx)
    {
        if (_gridGeomCached) return;
        _gridGeomCached = true;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var faces = ctx.SurfaceCache.Faces;
        for (int i = 0; i < faces.Count; i++)
        {
            var pos = faces[i].Position;
            min = Vector3.Min(min, pos);
            max = Vector3.Max(max, pos);
        }
        _gridCenter = 0.5f * (min + max);
        _gridHalfSize = 0.5f * (max - min);
    }

    /// <summary>
    /// Invalidate cached grid geometry (call after grid changes).
    /// </summary>
    public void InvalidateCache() => _gridGeomCached = false;

    /// <summary>
    /// Pressure gradient coefficient vs Mach.
    ///
    /// Physical basis: stagnation pressure concentrates near the upstream edge
    /// of each windward surface. The gradient is strongest at transonic (shock
    /// formation creates sharp leading-edge pressure peak) and decays toward
    /// Newtonian at hypersonic (uniform pressure on impact face).
    ///
    /// Subsonic ~0.35, transonic peak ~0.50, supersonic decay toward 0.
    /// </summary>
    private static float PressureGradientCoeff(float mach)
    {
        if (mach <= 0.6f) return 0.35f;
        if (mach <= 1.0f) return 0.35f + 0.15f * (mach - 0.6f) / 0.4f;
        if (mach <= 1.5f) return 0.50f - 0.15f * (mach - 1.0f) / 0.5f;
        return 0.35f * MathF.Exp(-0.5f * (mach - 1.5f));
    }

    // ─── Dispatch ───────────────────────────────────────────────────

    public override AeroResult Compute(in AeroContext ctx)
    {
        if (Tier >= CompressibleTier.Tier3)
            EnsureObliqueLut();
        if (EnablePressureGradient && !UsePerGroup)
            EnsureGridGeometry(ctx);
        // Per-group gradient uses precomputed second moments — no geometry cache needed.

        return UsePerGroup ? ComputePerGroup(ctx) : ComputePerFace(ctx);
    }

    // ─── Per-face (accurate torque + rotational damping) ────────────

    public AeroResult ComputePerFace(in AeroContext ctx)
    {
        float comSpeed = ctx.Velocity.Length();
        if (comSpeed < 0.01f && ctx.AngularVelocity.LengthSquared() < 1e-8f)
            return default;
        if (ctx.Atmosphere.Density < 1e-8)
            return default;

        Vector3 comVHat = comSpeed > 0.01f ? ctx.Velocity / comSpeed : Vector3.UnitX;
        double comMach = ctx.Atmosphere.SpeedOfSound > 0 ? comSpeed / ctx.Atmosphere.SpeedOfSound : 0;
        double comQ = ctx.Atmosphere.GetDynamicPressure(comSpeed);

        Vector3 totalForce = Vector3.Zero;
        Vector3 totalTorque = Vector3.Zero;
        float frontalArea = 0;

        var faces = ctx.SurfaceCache.Faces;
        for (int i = 0; i < faces.Count; i++)
        {
            ref readonly var face = ref faces is List<SurfaceFace> list
                ? ref GetListItem(list, i)
                : ref GetFallback(faces, i);

            Vector3 faceVel = ctx.VelocityAtPoint(face.Position);
            float faceSpeed = faceVel.Length();
            if (faceSpeed < 0.01f) continue;

            Vector3 faceVHat = faceVel / faceSpeed;
            float faceQ = (float)ctx.Atmosphere.GetDynamicPressure(faceSpeed);
            float faceMach = ctx.Atmosphere.SpeedOfSound > 0
                ? faceSpeed / (float)ctx.Atmosphere.SpeedOfSound : 0;

            float cosAlpha = Vector3.Dot(faceVHat, face.Normal);
            float cp;

            if (cosAlpha > 0)
            {
                cp = ComputeCp(cosAlpha, faceMach);

                // Pressure gradient: modulate Cp based on face position along
                // flow tangent. Upstream faces get higher Cp (stagnation shifts
                // toward leading edge), downstream faces get lower Cp.
                if (EnablePressureGradient)
                {
                    Vector3 vTan = faceVHat - cosAlpha * face.Normal;
                    float vTanMag = vTan.Length();
                    if (vTanMag > 0.02f)
                    {
                        Vector3 vTanHat = vTan / vTanMag;
                        // Half-extent of body bounding box in the tangent direction
                        float halfExtent = MathF.Abs(vTanHat.X) * _gridHalfSize.X
                                         + MathF.Abs(vTanHat.Y) * _gridHalfSize.Y
                                         + MathF.Abs(vTanHat.Z) * _gridHalfSize.Z;
                        if (halfExtent > 0.1f)
                        {
                            // Signed position: negative = upstream, positive = downstream
                            float s = Vector3.Dot(face.Position - _gridCenter, vTanHat);
                            float sNorm = System.Math.Clamp(s / halfExtent, -1f, 1f);
                            float beta = PressureGradientCoeff(faceMach) * vTanMag;
                            cp *= (1f - beta * sNorm);
                        }
                    }
                }

                frontalArea += face.Area * cosAlpha;
            }
            else
            {
                cp = ComputeBasePressure(faceMach);
            }

            Vector3 F = face.Normal * (-cp * faceQ * face.Area);

            if (cosAlpha > 0)
            {
                float cf = ComputeCf(faceMach, ctx.Atmosphere, faceSpeed);
                if (cf > 0)
                {
                    Vector3 tangent = faceVHat - Vector3.Dot(faceVHat, face.Normal) * face.Normal;
                    float tangentLen = tangent.Length();
                    if (tangentLen > 1e-6f)
                        F += (tangent / tangentLen) * (cf * faceQ * face.Area);
                }
            }

            totalForce += F;
            totalTorque += Vector3.Cross(face.Position - ctx.CenterOfMass, F);
        }

        return BuildResult(totalForce, totalTorque, comVHat, frontalArea, comMach, comQ);
    }

    // ─── Per-group (O(K), fast) ─────────────────────────────────────

    public AeroResult ComputePerGroup(in AeroContext ctx)
    {
        float speed = ctx.Velocity.Length();
        if (speed < 0.01f || ctx.Atmosphere.Density < 1e-8)
            return default;

        Vector3 vHat = ctx.Velocity / speed;
        double q = ctx.Atmosphere.GetDynamicPressure(speed);
        float qf = (float)q;
        float mach = ctx.Atmosphere.SpeedOfSound > 0 ? speed / (float)ctx.Atmosphere.SpeedOfSound : 0;

        float cf = ComputeCf(mach, ctx.Atmosphere, speed);

        Vector3 totalForce = Vector3.Zero;
        Vector3 totalTorque = Vector3.Zero;
        float frontalArea = 0;

        var groups = ctx.SurfaceCache.NormalGroups;
        for (int i = 0; i < groups.Count; i++)
        {
            var grp = groups[i];
            float cosAlpha = Vector3.Dot(vHat, grp.Normal);
            float cp;

            if (cosAlpha > 0)
            {
                cp = ComputeCp(cosAlpha, mach);
                frontalArea += grp.TotalArea * cosAlpha;
            }
            else
            {
                cp = ComputeBasePressure(mach);
            }

            Vector3 F = grp.Normal * (-cp * qf * grp.TotalArea);

            if (cosAlpha > 0 && cf > 0)
            {
                Vector3 tangent = vHat - Vector3.Dot(vHat, grp.Normal) * grp.Normal;
                float tangentLen = tangent.Length();
                if (tangentLen > 1e-6f)
                    F += (tangent / tangentLen) * (cf * qf * grp.TotalArea);
            }

            totalForce += F;
            totalTorque += Vector3.Cross(grp.Centroid - ctx.CenterOfMass, F);

            // Pressure gradient torque correction from precomputed second moments.
            // δτ = gradient · |vTan| · Cp · q · cross(S · vTanHat, normal) / halfExtent
            if (EnablePressureGradient && cosAlpha > 0 && grp.HasSecondMoment)
            {
                Vector3 vTan = vHat - cosAlpha * grp.Normal;
                float vTanMag = vTan.Length();
                if (vTanMag > 0.02f)
                {
                    Vector3 vTanHat = vTan / vTanMag;
                    Vector3 Sv = grp.SecondMomentMul(vTanHat);
                    float Sd = Vector3.Dot(vTanHat, Sv); // projected scalar moment (m⁴)
                    float halfExtent = MathF.Sqrt(Sd / grp.TotalArea); // RMS extent (m)
                    if (halfExtent > 0.1f)
                    {
                        float gradCoeff = PressureGradientCoeff(mach) * vTanMag;
                        totalTorque += Vector3.Cross(Sv, grp.Normal)
                                     * (gradCoeff * cp * qf / halfExtent);
                    }
                }
            }
        }

        return BuildResult(totalForce, totalTorque, vHat, frontalArea, mach, q);
    }

    // ═══════════════════════════════════════════════════════════════
    //  PRESSURE COEFFICIENT — Mach-dependent, tiered
    // ═══════════════════════════════════════════════════════════════

    private float ComputeCp(float cosAlpha, float mach)
    {
        // CpMax from Rayleigh Pitot formula (all tiers)
        float cpMax = (float)CompressibleFlow.ModifiedNewtonianCpMax(mach, Gamma);

        // ── Supersonic ──────────────────────────────────────────
        if (mach >= SupersonicLimit)
        {
            float cp = cpMax * cosAlpha * cosAlpha;

            // Tier 3: oblique shock Cp from pre-built LUT
            if (Tier >= CompressibleTier.Tier3 && cosAlpha < 0.7f && cosAlpha > 0.05f)
            {
                float cpOblique = LookupObliqueCp(mach, cosAlpha);
                if (cpOblique > 0)
                    cp = cpOblique;
            }

            if (Tier >= CompressibleTier.Tier3)
                cp = (float)CompressibleFlow.SoftCapCd(cp);

            return cp;
        }

        // ── Subsonic ────────────────────────────────────────────
        float cpIncomp = CdBluff * cosAlpha;
        float cpSub;

        // Karman-Tsien compressibility correction (all tiers)
        if (mach > 0.3f && mach < 0.95f)
        {
            double corrected = CompressibleFlow.KarmanTsien(cpIncomp, mach);
            cpSub = double.IsNaN(corrected) ? cpIncomp : (float)corrected;
        }
        else
        {
            cpSub = cpIncomp;
        }

        // Wave drag multiplier through transonic (all tiers)
        cpSub *= (float)CompressibleFlow.WaveDragMultiplier(mach);

        if (mach <= SubsonicLimit)
        {
            if (Tier >= CompressibleTier.Tier3)
                cpSub = (float)CompressibleFlow.SoftCapCd(cpSub);
            return cpSub;
        }

        // ── Transonic blend (Hermite smoothstep) ────────────────
        float t = (mach - SubsonicLimit) / (SupersonicLimit - SubsonicLimit);
        t = t * t * (3f - 2f * t);

        float cpSup = cpMax * cosAlpha * cosAlpha;
        float result = cpSub + (cpSup - cpSub) * t;

        if (Tier >= CompressibleTier.Tier3)
            result = (float)CompressibleFlow.SoftCapCd(result);

        return result;
    }

    // ═══════════════════════════════════════════════════════════════
    //  SKIN FRICTION — Tier 2+: Mach/Re dependent
    // ═══════════════════════════════════════════════════════════════

    private float ComputeCf(float mach, in AtmosphereState atmo, float speed)
    {
        if (Tier < CompressibleTier.Tier2)
            return CfSkin;

        // Reynolds number based on reference length
        double re = atmo.GetReynoldsPerMeter(speed) * ReferenceLength;
        if (re < 1e3) re = 1e3;

        // Turbulent flat plate: Cf = 0.455 / (log10(Re))^2.58 (Schlichting)
        double cf = 0.455 / System.Math.Pow(System.Math.Log10(re), 2.58);

        // Compressibility reduction (White's approximation):
        // Cf_comp = Cf_incomp / (1 + 0.144 * M^2)^0.65
        if (mach > 0.3f)
            cf /= System.Math.Pow(1.0 + 0.144 * mach * mach, 0.65);

        return (float)cf;
    }

    // ═══════════════════════════════════════════════════════════════
    //  BASE PRESSURE — Tier 3: Mach-dependent
    // ═══════════════════════════════════════════════════════════════

    private float ComputeBasePressure(float mach)
    {
        if (Tier < CompressibleTier.Tier3)
            return CpBase;

        // Bell curve: minimum at M=1 (strongest wake suction)
        // Subsonic ~-0.14, transonic ~-0.34, supersonic recovers
        double dM = mach - 1.0;
        return (float)(-0.14 - 0.2 * System.Math.Exp(-3.0 * dM * dM));
    }

    // ─── Helpers ────────────────────────────────────────────────────

    private static AeroResult BuildResult(
        Vector3 totalForce, Vector3 totalTorque, Vector3 vHat,
        float frontalArea, double mach, double q)
    {
        float forceDotV = Vector3.Dot(totalForce, vHat);
        Vector3 liftVec = totalForce - forceDotV * vHat;

        return new AeroResult(
            totalForce, totalTorque,
            MathF.Abs(forceDotV),
            liftVec.Length(),
            frontalArea,
            mach, q);
    }

    private static ref readonly SurfaceFace GetListItem(List<SurfaceFace> list, int index)
        => ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list)[index];

    private static SurfaceFace _fallback;
    private static ref readonly SurfaceFace GetFallback(IReadOnlyList<SurfaceFace> list, int index)
    {
        _fallback = list[index];
        return ref _fallback;
    }
}
