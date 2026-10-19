using System.Numerics;

namespace Aero.Core.Aero;

/// <summary>
/// Hybrid aerodynamic drag model: per-group O(K) forces + Cp-weighted damping tensor.
///
/// Forces and static torque use the fast per-group path (identical to NewtonianDragModel.ComputePerGroup).
/// Rotational damping uses a Cp-weighted tensor built each tick from all faces:
///
///   For each face:
///     cosα = v̂ · n̂
///     w_i  = Cp(cosα, Mach) · A_i    (windward)  or  |CpBase| · A_i  (leeward)
///     p_i  = (r_i - CoM) × n̂_i      (geometric lever arm, cached)
///     D   += w_i · (p_i ⊗ p_i)       (6 mul-adds per face)
///
///   τ_damp = -ρ · v · D · ω
///
/// This is O(N) when rotating but much cheaper than full per-face:
///   - No per-face velocity computation (no v + ω×r)
///   - No per-face force vectors
///   - No per-face torque cross products
///   - Just 1 dot product + 6 multiply-adds per face
///
/// The Cp weighting makes the damping orientation-aware: windward faces (high Cp)
/// contribute more damping than leeward faces (low |CpBase|), so the tensor
/// correctly captures which faces are "in the wind" and prevents over-damping
/// that would block natural reorientation (e.g., capsule flipping heat-shield-forward).
/// </summary>
public class HybridDragModel : AeroDragModelBase
{
    public override string Name => "Hybrid (group + Cp-damped)";
    public override string Description => "O(K) forces, O(N) Cp-weighted rotational damping";

    // ─── Tuning (same defaults as NewtonianDragModel) ─────────────

    public float CpMax { get; set; } = 2.0f;
    public float CdBluff { get; set; } = 1.1f;
    public float CpBase { get; set; } = -0.15f;
    public float CfSkin { get; set; } = 0.005f;
    public float SubsonicLimit { get; set; } = 0.6f;
    public float SupersonicLimit { get; set; } = 1.2f;

    // ─── Cached lever arms ────────────────────────────────────────

    // p_i = (r_i - CoM) × n̂_i — purely geometric, only changes when CoM changes.
    private Vector3[]? _leverArms;
    private Vector3 _leverCoM;
    private bool _leverValid;

    // ─── Compute ──────────────────────────────────────────────────

    public override AeroResult Compute(in AeroContext ctx)
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
        float machF = (float)comMach;

        // ── Per-group forces (O(K)) ──────────────────────────────

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
                cp = ComputeCp(cosAlpha, machF);
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

        // ── Cp-weighted rotational damping (O(N) lightweight) ────

        if (ctx.AngularVelocity.LengthSquared() > 1e-12f)
        {
            EnsureLeverArms(ctx);

            float dxx = 0, dxy = 0, dxz = 0, dyy = 0, dyz = 0, dzz = 0;
            float absCpBase = MathF.Abs(CpBase);

            var faces = ctx.SurfaceCache.Faces;
            for (int fi = 0; fi < faces.Count; fi++)
            {
                var face = faces[fi];
                float cosA = Vector3.Dot(comVHat, face.Normal);
                float weight = cosA > 0
                    ? ComputeCp(cosA, machF) * face.Area
                    : absCpBase * face.Area;

                var p = _leverArms![fi];
                dxx += weight * p.X * p.X;
                dxy += weight * p.X * p.Y;
                dxz += weight * p.X * p.Z;
                dyy += weight * p.Y * p.Y;
                dyz += weight * p.Y * p.Z;
                dzz += weight * p.Z * p.Z;
            }

            float rhoV = (float)ctx.Atmosphere.Density * comSpeed;
            var w = ctx.AngularVelocity;
            totalTorque -= new Vector3(
                rhoV * (dxx * w.X + dxy * w.Y + dxz * w.Z),
                rhoV * (dxy * w.X + dyy * w.Y + dyz * w.Z),
                rhoV * (dxz * w.X + dyz * w.Y + dzz * w.Z));
        }

        return NewtonianDragModel.BuildResult(totalForce, totalTorque, comVHat, frontalArea, comMach, comQ);
    }

    // ─── Cp model (same as Newtonian) ─────────────────────────────

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

    // ─── Lever arm cache ──────────────────────────────────────────

    private void EnsureLeverArms(in AeroContext ctx)
    {
        if (_leverValid && _leverCoM == ctx.CenterOfMass && _leverArms?.Length == ctx.SurfaceCache.Faces.Count)
            return;

        var faces = ctx.SurfaceCache.Faces;
        if (_leverArms == null || _leverArms.Length != faces.Count)
            _leverArms = new Vector3[faces.Count];

        for (int i = 0; i < faces.Count; i++)
        {
            Vector3 r = faces[i].Position - ctx.CenterOfMass;
            _leverArms[i] = Vector3.Cross(r, faces[i].Normal);
        }

        _leverCoM = ctx.CenterOfMass;
        _leverValid = true;
    }
}
