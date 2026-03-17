#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// Panel-method aerodynamic drag model using Mach-dependent Cp.
///
/// Per exposed face:
///   cosα = -v̂ · n̂
///   Cp = f(cosα, Mach)                    — Newtonian / bluff body / blend
///   F_face = -Cp × q × A × n̂             — pressure force (normal to surface)
///   F_fric = Cf × q × A × v̂_tangent      — skin friction (tangent to surface)
///   τ_face = (r_face - CoM) × F_face      — torque contribution
///
/// Two evaluation modes:
///   ComputePerFace  — O(N) per face, natural rotational damping from v + ω×r
///   ComputePerGroup — O(K) per normal group, no damping (fast but tumbles)
/// </summary>
public class NewtonianDragModel : IAeroDragModel
{
    public string Name => "Newtonian Panel";

    /// <summary>Whether Compute() uses per-group (fast) or per-face (accurate torque).</summary>
    public bool UsePerGroup { get; set; } = true;

    // ─── Tuning parameters ────────────────────────────────────────

    /// <summary>Stagnation pressure coefficient (supersonic/Newtonian). Default 2.0.</summary>
    public float CpMax { get; set; } = 2.0f;

    /// <summary>Subsonic bluff body drag coefficient. Default 1.1 (cube face-on ≈ 1.05).</summary>
    public float CdBluff { get; set; } = 1.1f;

    /// <summary>Base pressure on rear-facing surfaces (wake suction). Default -0.15.</summary>
    public float CpBase { get; set; } = -0.15f;

    /// <summary>Skin friction coefficient. Default 0.005.</summary>
    public float CfSkin { get; set; } = 0.005f;

    /// <summary>Below this Mach: pure subsonic model.</summary>
    public float SubsonicLimit { get; set; } = 0.6f;

    /// <summary>Above this Mach: pure Newtonian model.</summary>
    public float SupersonicLimit { get; set; } = 1.2f;

    // ─── Dispatch ──────────────────────────────────────────────

    public AeroResult Compute(in AeroContext ctx)
    {
        return UsePerGroup
            ? ComputePerGroup(ctx)
            : ComputePerFace(ctx);
    }

    // ─── Per-face computation (accurate torque + rotational damping) ──

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
            var face = faces[i];

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
                frontalArea += face.Area * cosAlpha;
            }
            else
            {
                cp = CpBase;
            }

            Vector3 F = face.Normal * (-cp * faceQ * face.Area);

            if (cosAlpha > 0 && CfSkin > 0)
            {
                Vector3 tangent = faceVHat - Vector3.Dot(faceVHat, face.Normal) * face.Normal;
                float tangentLen = tangent.Length();
                if (tangentLen > 1e-6f)
                    F += (tangent / tangentLen) * (CfSkin * faceQ * face.Area);
            }

            totalForce += F;
            totalTorque += Vector3.Cross(face.Position - ctx.CenterOfMass, F);
        }

        return BuildResult(totalForce, totalTorque, comVHat, frontalArea, comMach, comQ);
    }

    // ─── Per-group computation (O(K), no damping) ──────────────────

    public AeroResult ComputePerGroup(in AeroContext ctx)
    {
        float comSpeed = ctx.Velocity.Length();
        if (comSpeed < 0.01f && ctx.AngularVelocity.LengthSquared() < 1e-8f)
            return default;
        if (ctx.Atmosphere.Density < 1e-8)
            return default;

        Vector3 comVHat = comSpeed > 0.01f ? ctx.Velocity / comSpeed : Vector3.UnitX;
        double comMach = ctx.Atmosphere.SpeedOfSound > 0 ? comSpeed / ctx.Atmosphere.SpeedOfSound : 0;
        double comQ = ctx.Atmosphere.GetDynamicPressure(comSpeed);
        float qf = (float)comQ;

        Vector3 totalForce = Vector3.Zero;
        Vector3 totalTorque = Vector3.Zero;
        float frontalArea = 0;

        var groups = ctx.SurfaceCache.NormalGroups;
        for (int i = 0; i < groups.Count; i++)
        {
            var grp = groups[i];
            float cosAlpha = Vector3.Dot(comVHat, grp.Normal);
            float cp;

            if (cosAlpha > 0)
            {
                cp = ComputeCp(cosAlpha, (float)comMach);
                frontalArea += grp.TotalArea * cosAlpha;
            }
            else
            {
                cp = CpBase;
            }

            Vector3 F = grp.Normal * (-cp * qf * grp.TotalArea);

            if (cosAlpha > 0 && CfSkin > 0)
            {
                Vector3 tangent = comVHat - Vector3.Dot(comVHat, grp.Normal) * grp.Normal;
                float tangentLen = tangent.Length();
                if (tangentLen > 1e-6f)
                    F += (tangent / tangentLen) * (CfSkin * qf * grp.TotalArea);
            }

            totalForce += F;
            totalTorque += Vector3.Cross(grp.Centroid - ctx.CenterOfMass, F);
        }

        return BuildResult(totalForce, totalTorque, comVHat, frontalArea, comMach, comQ);
    }

    // ─── Cp model ─────────────────────────────────────────────────

    public float ComputeCp(float cosAlpha, float mach)
    {
        if (mach >= SupersonicLimit)
            return CpMax * cosAlpha * cosAlpha;

        if (mach <= SubsonicLimit)
            return CdBluff * cosAlpha;

        float t = (mach - SubsonicLimit) / (SupersonicLimit - SubsonicLimit);
        t = t * t * (3f - 2f * t);

        float cpSub = CdBluff * cosAlpha;
        float cpSup = CpMax * cosAlpha * cosAlpha;
        return cpSub + (cpSup - cpSub) * t;
    }

    // ─── Helpers ──────────────────────────────────────────────────

    internal static AeroResult BuildResult(
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
}
