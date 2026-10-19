using System.Numerics;
using Aero.Core.Atmosphere;
using Aero.Core.Grids;

namespace Aero.Core.Aero;

/// <summary>
/// Shadow + Newtonian drag with per-face rotational velocity (ω×r).
///
/// Like ShadowedDragModel but computes local velocity at each face center
/// instead of using CoM velocity everywhere. This gives proper rotational
/// aerodynamic damping: faces moving faster due to rotation see more drag,
/// producing a restoring torque that damps angular motion.
///
/// Without this, a spinning grid experiences no rotational drag because
/// all faces see the same CoM velocity regardless of their position.
/// </summary>
public class DampedShadowedDragModel : AeroDragModelBase
{
    public override string Name => "Newtonian + Shadow + Damping";
    public override string Description => "Panel method with shadow occlusion and per-face rotational velocity";

    private readonly ShadowMap _shadowMap = new();

    // ─── Tuning ─────────────────────────────────────────────────────

    /// <summary>Stagnation pressure coefficient. Default 2.0.</summary>
    public float CpMax { get; set; } = 2.0f;

    /// <summary>Subsonic bluff body Cd. Default 1.1.</summary>
    public float CdBluff { get; set; } = 1.1f;

    /// <summary>Base pressure (rear faces). Default -0.15.</summary>
    public float CpBase { get; set; } = -0.15f;

    /// <summary>Skin friction. Default 0.005.</summary>
    public float CfSkin { get; set; } = 0.005f;

    /// <summary>Below this Mach: subsonic model.</summary>
    public float SubsonicLimit { get; set; } = 0.6f;

    /// <summary>Above this Mach: Newtonian model.</summary>
    public float SupersonicLimit { get; set; } = 1.2f;

    /// <summary>
    /// Streamlining factor [0,1]. At 1.0 (full streamlined), faces at shallow angles
    /// to the flow get ~8% of Newtonian Cp (modeling pressure recovery on tapered aft sections).
    /// Only stagnation-region faces retain full Cp. At 0.0, disabled (bluff body).
    /// Default: 0.0 (bluff body — set to 1.0 for aircraft/streamlined shapes).
    /// </summary>
    public float Streamlining { get; set; } = 0f;

    /// <summary>Shadow direction threshold in radians (~5° default).</summary>
    public float DirectionThreshold
    {
        get => _shadowMap.DirectionThreshold;
        set => _shadowMap.DirectionThreshold = value;
    }

    /// <summary>Access the shadow map for inspection/benchmarking.</summary>
    public ShadowMap ShadowMap => _shadowMap;

    // ─── Compute ────────────────────────────────────────────────────

    public override AeroResult Compute(in AeroContext ctx)
    {
        float speed = ctx.Speed;
        if (speed < 0.01f || ctx.Atmosphere.Density < 1e-8)
            return default;

        var cache = ctx.SurfaceCache;
        Vector3 vHat = ctx.Velocity / speed;

        // Shadow map uses CoM flow direction (good enough for occlusion)
        _shadowMap.Update(ctx.Grid, cache, ctx.Velocity);

        Vector3 totalForce = Vector3.Zero;
        Vector3 totalTorque = Vector3.Zero;
        float frontalArea = 0;

        var faces = cache.Faces;
        var visFactor = _shadowMap.VisibilityFactor;
        Vector3 omega = ctx.AngularVelocity;
        Vector3 com = ctx.CenterOfMass;
        bool hasRotation = omega.LengthSquared() > 1e-10f;

        for (int i = 0; i < faces.Count; i++)
        {
            var face = faces[i];

            // Per-face velocity: v_com + ω × (r - com)
            Vector3 localVel = hasRotation
                ? ctx.Velocity + Vector3.Cross(omega, face.Position - com)
                : ctx.Velocity;

            float localSpeed = localVel.Length();
            if (localSpeed < 0.01f) continue;

            Vector3 localVHat = localVel / localSpeed;
            double localQ = ctx.Atmosphere.GetDynamicPressure(localSpeed);
            float localQf = (float)localQ;
            double localMach = ctx.Atmosphere.SpeedOfSound > 0
                ? localSpeed / ctx.Atmosphere.SpeedOfSound : 0;

            float cosAlpha = Vector3.Dot(localVHat, face.Normal);

            if (cosAlpha > 0)
            {
                // Front-facing: apply shadow attenuation
                float shadowFactor = (i < visFactor.Length) ? visFactor[i] : 1f;
                if (shadowFactor < 0.01f)
                    continue;

                float cp = ComputeCp(cosAlpha, (float)localMach) * shadowFactor;

                // Frontal area uses CoM flow direction for consistency
                float cosAlphaCoM = Vector3.Dot(vHat, face.Normal);
                if (cosAlphaCoM > 0)
                    frontalArea += face.Area * cosAlphaCoM * shadowFactor;

                Vector3 F = face.Normal * (-cp * localQf * face.Area);

                if (CfSkin > 0)
                {
                    Vector3 tangent = localVHat - Vector3.Dot(localVHat, face.Normal) * face.Normal;
                    float tangentLen = tangent.Length();
                    if (tangentLen > 1e-6f)
                        F += (tangent / tangentLen) * (CfSkin * localQf * face.Area);
                }

                totalForce += F;
                totalTorque += Vector3.Cross(face.Position - com, F);
            }
            else
            {
                // Rear-facing: base pressure (wake suction)
                // Streamlined aft: less base suction (30% of bluff base pressure)
                float baseCp = Streamlining > 0f
                    ? CpBase * (1f - 0.7f * Streamlining)
                    : CpBase;
                Vector3 F = face.Normal * (-baseCp * localQf * face.Area);
                totalForce += F;
                totalTorque += Vector3.Cross(face.Position - com, F);
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

    /// <summary>
    /// Notify the shadow map that blocks changed.
    /// </summary>
    public void OnBlocksChanged(Grid grid, ISurfaceProvider provider,
        Vector3 flowDirection, in BlocksChangedArgs args)
    {
        _shadowMap.OnBlocksChanged(grid, provider, flowDirection, args);
    }

    // ─── Cp model ───────────────────────────────────────────────────

    private float ComputeCp(float cosAlpha, float mach)
    {
        float cp;
        if (mach >= SupersonicLimit)
            cp = CpMax * cosAlpha * cosAlpha;
        else if (mach <= SubsonicLimit)
            cp = CdBluff * cosAlpha;
        else
        {
            float t = (mach - SubsonicLimit) / (SupersonicLimit - SubsonicLimit);
            t = t * t * (3f - 2f * t);
            cp = CdBluff * cosAlpha + (CpMax * cosAlpha * cosAlpha - CdBluff * cosAlpha) * t;
        }

        // Streamlining: faces at shallow incidence (sides of fuselage) get reduced Cp
        // because pressure recovery on the aft body cancels forward pressure (d'Alembert).
        // Only stagnation region (cosAlpha close to 1) retains full Cp.
        if (Streamlining > 0f)
        {
            float recovery = cosAlpha * cosAlpha; // 0 at shallow, 1 at stagnation
            float minFraction = 0.08f; // ~8% of Cp survives on shallow-angle faces
            float factor = minFraction + (1f - minFraction) * recovery;
            cp *= 1f - Streamlining * (1f - factor);
        }

        return cp;
    }
}
