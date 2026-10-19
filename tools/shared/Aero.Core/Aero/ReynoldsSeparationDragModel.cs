using System.Numerics;
using Aero.Core.Atmosphere;

namespace Aero.Core.Aero;

/// <summary>
/// Drag model with Reynolds-dependent flow separation for bluff bodies.
///
/// Extends SeparationDragModel's fixed-geometry separation with Re-dependent
/// separation point location:
///
///   - Low Re (laminar BL, Re_L &lt; ~2×10⁵): early separation, larger wake, higher Cd
///   - High Re (turbulent BL, Re_L &gt; ~5×10⁵): later separation, smaller wake, lower Cd
///   - Critical Re (transition): sharp Cd drop ("drag crisis" on cylinders/spheres)
///
/// For sharp-edged blocky bodies the effect is less dramatic than for smooth
/// bodies (separation is always at the edge), but:
///   1. The reattachment length downstream varies with Re
///   2. At high Re, flow can partially reattach after the leading edge separation
///      bubble, recovering some pressure on downstream faces
///   3. The separated wake base pressure varies with Re
///
/// The model adjusts three parameters based on Re:
///   - AttachedFraction: how far along the body flow stays attached
///   - SeparatedCpFraction: how much pressure remains in the separated zone
///   - CpBase: wake suction strength on rear faces
///
/// Per-group and per-face modes supported, with second-moment torque correction.
/// </summary>
public class ReynoldsSeparationDragModel : AeroDragModelBase
{
    public override string Name => UsePerGroup ? "Re-Sep (grp)" : "Re-Sep (face)";
    public override string Description => "Compressible + Re-dependent separation point";

    /// <summary>Whether Compute() uses per-group (fast) or per-face (accurate torque).</summary>
    public bool UsePerGroup { get; set; } = true;

    // ─── Compressible flow parameters ───────────────────────────────

    /// <summary>Heat capacity ratio. Default 1.4 for air.</summary>
    public double Gamma { get; set; } = 1.4;

    /// <summary>Subsonic bluff body Cp (incompressible). Default 1.1.</summary>
    public float CdBluff { get; set; } = 1.1f;

    /// <summary>Incompressible skin friction. Default 0.005.</summary>
    public float CfSkin { get; set; } = 0.005f;

    /// <summary>Reference length for Reynolds number (m). Default 2.5.</summary>
    public float ReferenceLength { get; set; } = 2.5f;

    /// <summary>Below this Mach: pure subsonic model.</summary>
    public float SubsonicLimit { get; set; } = 0.6f;

    /// <summary>Above this Mach: pure supersonic model.</summary>
    public float SupersonicLimit { get; set; } = 1.2f;

    // ─── Re-dependent separation parameters ─────────────────────────

    /// <summary>
    /// Reynolds number at the start of the drag crisis (laminar→turbulent transition).
    /// Below this: fully laminar separation behavior.
    /// Default 2×10⁵ (sphere/cylinder critical Re for smooth bodies;
    /// sharp-edged bodies transition lower, but we use the body-length Re).
    /// </summary>
    public double ReCriticalLow { get; set; } = 2e5;

    /// <summary>
    /// Reynolds number at the end of the drag crisis.
    /// Above this: fully turbulent separation behavior.
    /// Default 5×10⁵.
    /// </summary>
    public double ReCriticalHigh { get; set; } = 5e5;

    /// <summary>
    /// Attached fraction at low Re (laminar). Flow separates almost immediately
    /// at the leading edge. Default 0.05.
    /// </summary>
    public float AttachedFractionLaminar { get; set; } = 0.05f;

    /// <summary>
    /// Attached fraction at high Re (turbulent). Turbulent BL resists separation
    /// longer, so flow stays attached further along the body. Default 0.30.
    /// </summary>
    public float AttachedFractionTurbulent { get; set; } = 0.30f;

    /// <summary>
    /// Cp fraction in the separated zone at low Re. Laminar separation gives
    /// a wider, lower-energy wake → less pressure recovery. Default 0.10.
    /// </summary>
    public float SeparatedCpLaminar { get; set; } = 0.10f;

    /// <summary>
    /// Cp fraction in the separated zone at high Re. Turbulent mixing energizes
    /// the wake → some pressure recovery. Default 0.35.
    /// </summary>
    public float SeparatedCpTurbulent { get; set; } = 0.35f;

    /// <summary>
    /// Base pressure at low Re. Laminar wake has weak suction. Default -0.10.
    /// </summary>
    public float CpBaseLaminar { get; set; } = -0.10f;

    /// <summary>
    /// Base pressure at high Re. Turbulent wake has stronger suction. Default -0.20.
    /// </summary>
    public float CpBaseTurbulent { get; set; } = -0.20f;

    /// <summary>
    /// Transition length (fraction of body). Fixed — doesn't vary much with Re
    /// for sharp-edged bodies. Default 0.12.
    /// </summary>
    public float TransitionLength { get; set; } = 0.12f;

    /// <summary>
    /// Max cosAlpha for separation to apply. Same as SeparationDragModel.
    /// Default 0.85.
    /// </summary>
    public float SeparationCosAlphaMax { get; set; } = 0.85f;

    // ─── Per-frame Re-interpolated values ───────────────────────────

    private float _attachedFraction;
    private float _separatedCpFraction;
    private float _cpBase;

    // ─── Cached geometry ────────────────────────────────────────────

    private Vector3 _gridCenter;
    private Vector3 _gridHalfSize;
    private float _bodyLength; // max bounding box extent
    private bool _geomCached;

    public void InvalidateCache() => _geomCached = false;

    private void EnsureGeometry(in AeroContext ctx)
    {
        if (_geomCached) return;
        _geomCached = true;

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
        var size = max - min;
        _bodyLength = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
    }

    /// <summary>
    /// Compute the laminar-turbulent blend factor from Reynolds number.
    /// 0 = fully laminar, 1 = fully turbulent.
    /// Uses smoothstep over the critical Re range.
    /// </summary>
    private float ComputeReBlend(double re)
    {
        if (re <= ReCriticalLow) return 0f;
        if (re >= ReCriticalHigh) return 1f;

        float t = (float)((re - ReCriticalLow) / (ReCriticalHigh - ReCriticalLow));
        return t * t * (3f - 2f * t); // smoothstep
    }

    /// <summary>
    /// Update the Re-dependent separation parameters for this frame.
    /// Called once per Compute() with the body-length Reynolds number.
    /// </summary>
    private void UpdateReParameters(in AtmosphereState atmo, float speed)
    {
        // Body-length Reynolds number: use the longest bounding box dimension
        float refLen = _bodyLength > 0.1f ? _bodyLength : ReferenceLength;
        double re = atmo.GetReynoldsPerMeter(speed) * refLen;

        float blend = ComputeReBlend(re);

        _attachedFraction = AttachedFractionLaminar + (AttachedFractionTurbulent - AttachedFractionLaminar) * blend;
        _separatedCpFraction = SeparatedCpLaminar + (SeparatedCpTurbulent - SeparatedCpLaminar) * blend;
        _cpBase = CpBaseLaminar + (CpBaseTurbulent - CpBaseLaminar) * blend;
    }

    // ─── Dispatch ───────────────────────────────────────────────────

    public override AeroResult Compute(in AeroContext ctx)
    {
        EnsureGeometry(ctx);
        return UsePerGroup ? ComputePerGroup(ctx) : ComputePerFace(ctx);
    }

    // ─── Per-face ───────────────────────────────────────────────────

    public AeroResult ComputePerFace(in AeroContext ctx)
    {
        float comSpeed = ctx.Velocity.Length();
        if (comSpeed < 0.01f && ctx.AngularVelocity.LengthSquared() < 1e-8f)
            return default;
        if (ctx.Atmosphere.Density < 1e-8)
            return default;

        UpdateReParameters(ctx.Atmosphere, comSpeed);

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

            float sepFactor = 1f;
            if (cosAlpha > 0)
            {
                cp = ComputeCp(cosAlpha, faceMach);
                sepFactor = ComputeSeparationFactor(face.Position, faceVHat, face.Normal, cosAlpha);
                cp *= sepFactor;
                frontalArea += face.Area * cosAlpha;
            }
            else
            {
                cp = _cpBase;
            }

            Vector3 F = face.Normal * (-cp * faceQ * face.Area);

            if (cosAlpha > 0)
            {
                float cf = ComputeCf(faceMach, ctx.Atmosphere, faceSpeed) * sepFactor;
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

    // ─── Per-group ──────────────────────────────────────────────────

    public AeroResult ComputePerGroup(in AeroContext ctx)
    {
        float speed = ctx.Velocity.Length();
        if (speed < 0.01f || ctx.Atmosphere.Density < 1e-8)
            return default;

        UpdateReParameters(ctx.Atmosphere, speed);

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
            float sepFactor = 1f;

            if (cosAlpha > 0)
            {
                cp = ComputeCp(cosAlpha, mach);
                sepFactor = ComputeSeparationFactorGroup(grp, vHat, cosAlpha);
                cp *= sepFactor;
                frontalArea += grp.TotalArea * cosAlpha;
            }
            else
            {
                cp = _cpBase;
            }

            Vector3 F = grp.Normal * (-cp * qf * grp.TotalArea);

            if (cosAlpha > 0 && cf > 0)
            {
                float cfSep = cf * sepFactor;
                Vector3 tangent = vHat - Vector3.Dot(vHat, grp.Normal) * grp.Normal;
                float tangentLen = tangent.Length();
                if (tangentLen > 1e-6f)
                    F += (tangent / tangentLen) * (cfSep * qf * grp.TotalArea);
            }

            totalForce += F;
            totalTorque += Vector3.Cross(grp.Centroid - ctx.CenterOfMass, F);

            // Separation torque correction from second moments
            if (cosAlpha > 0 && sepFactor < 0.99f && grp.HasSecondMoment)
            {
                Vector3 vTan = vHat - cosAlpha * grp.Normal;
                float vTanMag = vTan.Length();
                if (vTanMag > 0.02f)
                {
                    Vector3 vTanHat = vTan / vTanMag;
                    Vector3 Sv = grp.SecondMomentMul(vTanHat);
                    float Sd = Vector3.Dot(vTanHat, Sv);
                    float halfExtent = MathF.Sqrt(Sd / grp.TotalArea);
                    if (halfExtent > 0.1f)
                    {
                        float sepGradient = (1f - sepFactor) * 0.5f;
                        totalTorque += Vector3.Cross(Sv, grp.Normal)
                                     * (-sepGradient * cp * qf / halfExtent);
                    }
                }
            }
        }

        return BuildResult(totalForce, totalTorque, vHat, frontalArea, mach, q);
    }

    // ─── Separation factor (per-face) ───────────────────────────────

    private float ComputeSeparationFactor(Vector3 facePos, Vector3 vHat, Vector3 faceNormal, float cosAlpha)
    {
        if (cosAlpha >= SeparationCosAlphaMax)
            return 1.0f;

        Vector3 vTan = vHat - cosAlpha * faceNormal;
        float vTanMag = vTan.Length();
        if (vTanMag < 0.02f)
            return 1.0f;

        Vector3 vTanHat = vTan / vTanMag;

        float halfExtent = MathF.Abs(vTanHat.X) * _gridHalfSize.X
                         + MathF.Abs(vTanHat.Y) * _gridHalfSize.Y
                         + MathF.Abs(vTanHat.Z) * _gridHalfSize.Z;
        if (halfExtent < 0.1f)
            return 1.0f;

        float s = Vector3.Dot(facePos - _gridCenter, vTanHat);
        float sNorm = System.Math.Clamp(s / halfExtent, -1f, 1f);
        float fraction = (sNorm + 1f) * 0.5f;

        return EvaluateSeparationCurve(fraction, cosAlpha);
    }

    // ─── Separation factor (per-group) ──────────────────────────────

    private float ComputeSeparationFactorGroup(in NormalGroup grp, Vector3 vHat, float cosAlpha)
    {
        if (cosAlpha >= SeparationCosAlphaMax)
            return 1.0f;

        Vector3 vTan = vHat - cosAlpha * grp.Normal;
        float vTanMag = vTan.Length();
        if (vTanMag < 0.02f)
            return 1.0f;

        Vector3 vTanHat = vTan / vTanMag;

        float halfExtent;
        if (grp.HasSecondMoment)
        {
            float Sd = Vector3.Dot(vTanHat, grp.SecondMomentMul(vTanHat));
            halfExtent = MathF.Sqrt(Sd / grp.TotalArea);
        }
        else
        {
            halfExtent = MathF.Abs(vTanHat.X) * _gridHalfSize.X
                       + MathF.Abs(vTanHat.Y) * _gridHalfSize.Y
                       + MathF.Abs(vTanHat.Z) * _gridHalfSize.Z;
        }

        if (halfExtent < 0.1f)
            return 1.0f;

        float s = Vector3.Dot(grp.Centroid - _gridCenter, vTanHat);
        float sNorm = System.Math.Clamp(s / (halfExtent * 2f + 0.01f), -1f, 1f);
        float fraction = (sNorm + 1f) * 0.5f;

        return EvaluateSeparationCurve(fraction, cosAlpha);
    }

    // ─── Separation curve (uses Re-interpolated parameters) ─────────

    private float EvaluateSeparationCurve(float fraction, float cosAlpha)
    {
        if (fraction <= _attachedFraction)
            return 1.0f;

        float obliqueness = 1f - cosAlpha / SeparationCosAlphaMax;
        float sepTarget = MathF.Max(_separatedCpFraction, 1f - obliqueness * (1f - _separatedCpFraction));

        float transEnd = _attachedFraction + TransitionLength;
        if (fraction >= transEnd)
            return sepTarget;

        float t = (fraction - _attachedFraction) / TransitionLength;
        t = t * t * (3f - 2f * t);
        return 1f + (sepTarget - 1f) * t;
    }

    // ─── Cp model ───────────────────────────────────────────────────

    private float ComputeCp(float cosAlpha, float mach)
    {
        float cpMax = (float)CompressibleFlow.ModifiedNewtonianCpMax(mach, Gamma);

        if (mach >= SupersonicLimit)
            return cpMax * cosAlpha * cosAlpha;

        float cpIncomp = CdBluff * cosAlpha;
        float cpSub;

        if (mach > 0.3f && mach < 0.95f)
        {
            double corrected = CompressibleFlow.KarmanTsien(cpIncomp, mach);
            cpSub = double.IsNaN(corrected) ? cpIncomp : (float)corrected;
        }
        else
        {
            cpSub = cpIncomp;
        }

        cpSub *= (float)CompressibleFlow.WaveDragMultiplier(mach);

        if (mach <= SubsonicLimit)
            return cpSub;

        float t = (mach - SubsonicLimit) / (SupersonicLimit - SubsonicLimit);
        t = t * t * (3f - 2f * t);
        float cpSup = cpMax * cosAlpha * cosAlpha;
        return cpSub + (cpSup - cpSub) * t;
    }

    private float ComputeCf(float mach, in AtmosphereState atmo, float speed)
    {
        double re = atmo.GetReynoldsPerMeter(speed) * ReferenceLength;
        if (re < 1e3) re = 1e3;

        double cf = 0.455 / System.Math.Pow(System.Math.Log10(re), 2.58);

        if (mach > 0.3f)
            cf /= System.Math.Pow(1.0 + 0.144 * mach * mach, 0.65);

        return (float)cf;
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
