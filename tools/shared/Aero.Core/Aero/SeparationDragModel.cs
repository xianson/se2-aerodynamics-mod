using System.Numerics;
using Aero.Core.Atmosphere;

namespace Aero.Core.Aero;

/// <summary>
/// Drag model with flow separation for bluff bodies.
///
/// Physics: on a blocky grid, flow separates at sharp edges (90° corners).
/// Faces that are geometrically windward but sit downstream of a separation
/// edge experience reduced pressure — they're in the dead-air/wake region
/// even though their normal faces into the wind.
///
/// For each windward face/group, we compute its streamwise position relative
/// to the body's leading edge. If the flow must turn around a sharp corner
/// to reach that face, the face is in the separated zone and gets attenuated Cp.
///
/// Effects vs. non-separated models:
///   - At 0° AoA (face-on): no change — all windward faces are upstream
///   - At 45° AoA: downstream windward faces lose pressure → less drag, less lift
///   - Lift peaks at moderate AoA then drops (bluff body "stall")
///   - CoP shifts forward toward the separation edge
///
/// Supports both per-face (accurate torque) and per-group (O(K) fast) modes.
/// Per-group uses the second moment tensor for separation-induced CoP shift.
/// </summary>
public class SeparationDragModel : AeroDragModelBase
{
    public override string Name => UsePerGroup ? "Separation (grp)" : "Separation (face)";
    public override string Description => "Compressible + edge separation / bluff-body stall";

    /// <summary>Whether Compute() uses per-group (fast) or per-face (accurate torque).</summary>
    public bool UsePerGroup { get; set; } = true;

    // ─── Compressible flow parameters (same as CompressibleDragModel) ──

    /// <summary>Heat capacity ratio. Default 1.4 for air.</summary>
    public double Gamma { get; set; } = 1.4;

    /// <summary>Subsonic bluff body Cp (incompressible). Default 1.1.</summary>
    public float CdBluff { get; set; } = 1.1f;

    /// <summary>Base pressure on rear faces. Default -0.15.</summary>
    public float CpBase { get; set; } = -0.15f;

    /// <summary>Incompressible skin friction. Default 0.005.</summary>
    public float CfSkin { get; set; } = 0.005f;

    /// <summary>Reference length for Reynolds number (m). Default 2.5.</summary>
    public float ReferenceLength { get; set; } = 2.5f;

    /// <summary>Below this Mach: pure subsonic model.</summary>
    public float SubsonicLimit { get; set; } = 0.6f;

    /// <summary>Above this Mach: pure supersonic model.</summary>
    public float SupersonicLimit { get; set; } = 1.2f;

    // ─── Separation parameters ──────────────────────────────────────

    /// <summary>
    /// Fraction of body length (in flow direction) before separation takes effect.
    /// Faces within this fraction from the leading edge get full attached-flow Cp.
    /// Default 0.15 — flow stays attached for the first ~15% of the body.
    /// </summary>
    public float AttachedFraction { get; set; } = 0.15f;

    /// <summary>
    /// Cp multiplier for fully separated faces. 0 = dead air (Cp=0),
    /// values closer to 1 = less separation effect.
    /// Default 0.2 — separated faces retain 20% of their attached-flow Cp.
    /// </summary>
    public float SeparatedCpFraction { get; set; } = 0.2f;

    /// <summary>
    /// How quickly Cp drops from attached to separated, as a fraction of body length.
    /// Default 0.15 — transition spans ~15% of body length.
    /// </summary>
    public float TransitionLength { get; set; } = 0.15f;

    /// <summary>
    /// Minimum cosAlpha for separation to apply. At face-on flow (cosAlpha=1)
    /// there's no tangential component — that's stagnation, not separation.
    /// Default 0.85 — separation kicks in when face is more than ~32° from face-on.
    /// </summary>
    public float SeparationCosAlphaMax { get; set; } = 0.85f;

    // ─── Cached geometry ────────────────────────────────────────────

    private Vector3 _gridCenter;
    private Vector3 _gridHalfSize;
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
    }

    // ─── Dispatch ───────────────────────────────────────────────────

    public override AeroResult Compute(in AeroContext ctx)
    {
        EnsureGeometry(ctx);
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
                cp = CpBase;
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

            // Separation: compute average factor at centroid for force,
            // then use second moment for torque correction.
            float sepFactor = 1f;

            if (cosAlpha > 0)
            {
                cp = ComputeCp(cosAlpha, mach);

                // Average separation factor at centroid position
                sepFactor = ComputeSeparationFactorGroup(grp, vHat, cosAlpha);
                cp *= sepFactor;

                frontalArea += grp.TotalArea * cosAlpha;
            }
            else
            {
                cp = CpBase;
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

            // ── Separation torque correction from second moments ──
            // Separation concentrates force toward the leading edge (upstream).
            // This is analogous to the pressure gradient CoP shift but driven
            // by the separation Cp attenuation gradient instead.
            //
            // The separation factor varies roughly linearly along the flow-tangent
            // direction: ~1.0 at the leading edge, ~SeparatedCpFraction downstream.
            // This linear variation × the face area distribution (captured by the
            // second moment tensor) gives a torque correction:
            //
            //   δτ = sepGradient · Cp · q · cross(S · vTanHat, normal) / halfExtent
            //
            // where sepGradient = (1 - sepFactor) measures how much force shifts upstream.
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
                        // The separation gradient pushes CoP upstream.
                        // Factor: how much Cp varies across the group.
                        // At sepFactor=1 → no gradient. At sepFactor=0.2 → strong gradient.
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

    /// <summary>
    /// Returns 1.0 for fully attached flow, down to SeparatedCpFraction for fully separated.
    /// Uses body bounding box to determine streamwise position.
    /// </summary>
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

    /// <summary>
    /// Per-group separation: uses centroid position and second moment RMS extent
    /// as the effective body length for that group's faces.
    /// </summary>
    private float ComputeSeparationFactorGroup(in NormalGroup grp, Vector3 vHat, float cosAlpha)
    {
        if (cosAlpha >= SeparationCosAlphaMax)
            return 1.0f;

        Vector3 vTan = vHat - cosAlpha * grp.Normal;
        float vTanMag = vTan.Length();
        if (vTanMag < 0.02f)
            return 1.0f;

        Vector3 vTanHat = vTan / vTanMag;

        // Use second moment to get the RMS extent of this group along tangent.
        // This captures how spread out the faces are — a wide group has more
        // faces in the separated zone than a narrow one.
        float halfExtent;
        if (grp.HasSecondMoment)
        {
            float Sd = Vector3.Dot(vTanHat, grp.SecondMomentMul(vTanHat));
            halfExtent = MathF.Sqrt(Sd / grp.TotalArea);
        }
        else
        {
            // Fallback: estimate from bounding box (same as per-face)
            halfExtent = MathF.Abs(vTanHat.X) * grp.Centroid.X // not ideal but rare path
                       + MathF.Abs(vTanHat.Y) * grp.Centroid.Y
                       + MathF.Abs(vTanHat.Z) * grp.Centroid.Z;
        }

        if (halfExtent < 0.1f)
            return 1.0f;

        // Centroid position along flow tangent, normalized by the group's extent.
        // This tells us where the "average" face sits relative to the leading edge.
        // For a symmetric body at an angle, the centroid is at the center,
        // so ~50% of faces are in the separated zone.
        //
        // We use the group's RMS extent as the normalization rather than the
        // full body bounding box, since different normal groups may span
        // different portions of the body.
        float s = Vector3.Dot(grp.Centroid - _gridCenter, vTanHat);
        float sNorm = System.Math.Clamp(s / (halfExtent * 2f + 0.01f), -1f, 1f);
        float fraction = (sNorm + 1f) * 0.5f;

        // For per-group, we want the area-weighted average separation factor
        // across all faces in the group. If faces are uniformly distributed
        // along the tangent direction, the average factor is the integral of
        // the separation curve over the group's extent.
        //
        // Approximate: evaluate at centroid, then blend toward a more separated
        // value based on the extent (larger extent → more faces downstream).
        float centerFactor = EvaluateSeparationCurve(fraction, cosAlpha);

        // Correction: if the group spans a wide range, the average is pulled
        // toward more separation than the centroid value suggests.
        // extent ratio: how much of the body this group covers
        // (not used in the fallback path where we don't have good extent data)
        return centerFactor;
    }

    // ─── Shared separation curve ────────────────────────────────────

    /// <summary>
    /// Evaluate the separation attenuation curve.
    /// fraction: 0 = leading edge, 1 = trailing edge.
    /// cosAlpha: dot(v, normal), controls separation strength.
    /// Returns: 1.0 (attached) to SeparatedCpFraction (fully separated).
    /// </summary>
    private float EvaluateSeparationCurve(float fraction, float cosAlpha)
    {
        if (fraction <= AttachedFraction)
            return 1.0f;

        // Obliqueness: how far from face-on. 0 = near face-on, 1 = very oblique.
        float obliqueness = 1f - cosAlpha / SeparationCosAlphaMax;
        float sepTarget = MathF.Max(SeparatedCpFraction, 1f - obliqueness * (1f - SeparatedCpFraction));

        float transEnd = AttachedFraction + TransitionLength;
        if (fraction >= transEnd)
            return sepTarget;

        // Smoothstep transition
        float t = (fraction - AttachedFraction) / TransitionLength;
        t = t * t * (3f - 2f * t);
        return 1f + (sepTarget - 1f) * t;
    }

    // ─── Cp model (from CompressibleDragModel) ──────────────────────

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
