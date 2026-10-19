using System.Numerics;
using Aero.Core.Atmosphere;
using Aero.Core.Grids;

namespace Aero.Core.Aero;

/// <summary>
/// Panel-method drag model with upstream shadow filtering.
///
/// Wraps NewtonianDragModel and adds a ShadowMap pass: faces occluded
/// by upstream blocks along the flow direction get zero pressure contribution.
/// Shadowed rear-facing faces still get base pressure (wake suction is
/// independent of upstream occlusion).
///
/// The ShadowMap is cached and only re-evaluated when:
///   - Flow direction changes by more than ~5°
///   - Blocks are added/removed (via OnBlocksChanged)
/// </summary>
public class ShadowedDragModel : AeroDragModelBase
{
    public override string Name => "Newtonian + Shadow";
    public override string Description => "Panel method with upstream occlusion via DDA ray march";

    private readonly ShadowMap _shadowMap = new();

    // ─── Tuning (delegates to inner model params) ─────────────────

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

    /// <summary>Shadow direction threshold in radians (~5° default).</summary>
    public float DirectionThreshold
    {
        get => _shadowMap.DirectionThreshold;
        set => _shadowMap.DirectionThreshold = value;
    }

    /// <summary>Access the shadow map for inspection/benchmarking.</summary>
    public ShadowMap ShadowMap => _shadowMap;

    // ─── Compute ──────────────────────────────────────────────────

    public override AeroResult Compute(in AeroContext ctx)
    {
        float speed = ctx.Speed;
        if (speed < 0.01f || ctx.Atmosphere.Density < 1e-8)
            return default;

        var cache = ctx.SurfaceCache;
        Vector3 vHat = ctx.Velocity / speed;
        double q = ctx.Atmosphere.GetDynamicPressure(speed);
        float qf = (float)q;
        double mach = ctx.Atmosphere.SpeedOfSound > 0 ? speed / ctx.Atmosphere.SpeedOfSound : 0;

        // Update shadow map (no-op if direction hasn't changed enough)
        _shadowMap.Update(ctx.Grid, cache, ctx.Velocity);

        Vector3 totalForce = Vector3.Zero;
        Vector3 totalTorque = Vector3.Zero;
        float frontalArea = 0;

        var faces = cache.Faces;
        var visFactor = _shadowMap.VisibilityFactor;

        for (int i = 0; i < faces.Count; i++)
        {
            var face = faces[i];
            float cosAlpha = Vector3.Dot(vHat, face.Normal);

            if (cosAlpha > 0)
            {
                // Front-facing: get shadow attenuation factor [0,1]
                float shadowFactor = (i < visFactor.Length) ? visFactor[i] : 1f;
                if (shadowFactor < 0.01f)
                    continue; // fully shadowed — skip

                float cp = ComputeCp(cosAlpha, (float)mach) * shadowFactor;
                frontalArea += face.Area * cosAlpha * shadowFactor;

                Vector3 F = face.Normal * (-cp * qf * face.Area);

                if (CfSkin > 0)
                {
                    Vector3 tangent = vHat - Vector3.Dot(vHat, face.Normal) * face.Normal;
                    float tangentLen = tangent.Length();
                    if (tangentLen > 1e-6f)
                        F += (tangent / tangentLen) * (CfSkin * qf * face.Area);
                }

                totalForce += F;
                totalTorque += Vector3.Cross(face.Position - ctx.CenterOfMass, F);
            }
            else
            {
                // Rear-facing: base pressure (wake suction)
                // Applied regardless of shadow — wake suction acts on all rear faces
                Vector3 F = face.Normal * (-CpBase * qf * face.Area);
                totalForce += F;
                totalTorque += Vector3.Cross(face.Position - ctx.CenterOfMass, F);
            }
        }

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
    /// Notify the shadow map that blocks changed. Call this after updating
    /// the surface provider but before the next Compute().
    /// </summary>
    public void OnBlocksChanged(Grid grid, ISurfaceProvider provider,
        Vector3 flowDirection, in BlocksChangedArgs args)
    {
        _shadowMap.OnBlocksChanged(grid, provider, flowDirection, args);
    }

    // ─── Cp model (same as NewtonianDragModel) ────────────────────

    private float ComputeCp(float cosAlpha, float mach)
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
}
