using System.Numerics;

namespace Aero.Core.Aero;

/// <summary>
/// Incompressible wing lift model based on thin airfoil theory with:
///   - Helmbold finite-wing correction (via precomputed CLAlpha)
///   - Kirchhoff stall model (smooth CL decay past α_stall)
///   - Prandtl biplane mutual interference (O(N²) for N wings)
///   - Induced drag from CL²/(π·e·AR)
///
/// This model uses precomputed CLAlpha and ignores Mach effects.
/// For compressible (Mach-dependent) lift, use CompressibleWingModel.
///
/// Anti-cheese:
///   - Low AR → low CL_alpha (Helmbold penalizes square plates)
///   - Stacking plates → mutual downwash reduces effective AoA, Oswald e drops
///   - Post-stall → Kirchhoff decay, no free lift at 90°
/// </summary>
public class ThinAirfoilWingModel : IWingLiftModel
{
    /// <summary>Minimum speed to compute wing forces (m/s).</summary>
    public float MinSpeed { get; set; } = 1f;

    public WingForceResult[] ComputeWingForces(ReadOnlySpan<LiftingSurface> wings, in AeroContext ctx)
    {
        int n = wings.Length;
        if (n == 0) return Array.Empty<WingForceResult>();

        float comSpeed = ctx.Velocity.Length();
        if (comSpeed < MinSpeed || ctx.Atmosphere.Density < 1e-8)
            return new WingForceResult[n]; // all zero

        float rho = (float)ctx.Atmosphere.Density;

        // Step 1: Compute raw AoA for each wing
        var alphas = new float[n];
        var localSpeeds = new float[n];
        var vHats = new Vector3[n];

        for (int i = 0; i < n; i++)
        {
            ref readonly var w = ref wings[i];
            var v = ctx.VelocityAtPoint(w.AeroCenter);
            float speed = v.Length();
            localSpeeds[i] = speed;
            if (speed < MinSpeed)
            {
                alphas[i] = 0;
                vHats[i] = Vector3.UnitX;
                continue;
            }
            vHats[i] = v / speed;

            // AoA = angle between airstream and chord plane
            // Airstream = -velocity (flow opposes body motion)
            // positive when flow comes from below normal (from below for +Y wing)
            float vDotN = -Vector3.Dot(v, w.Normal);
            var vInPlane = v - Vector3.Dot(v, w.Normal) * w.Normal;
            float vPlaneSpeed = vInPlane.Length();
            alphas[i] = MathF.Atan2(vDotN, vPlaneSpeed + 1e-6f);
        }

        // Step 2: Prandtl biplane interference — mutual downwash
        // Precompute pairwise influence coefficients, then iterate to convergence.
        var alphaEff = new float[n];
        var efficiency = new float[n];
        Array.Copy(alphas, alphaEff, n);
        for (int i = 0; i < n; i++) efficiency[i] = 1f;

        var oswaldMod = new float[n];
        for (int i = 0; i < n; i++) oswaldMod[i] = wings[i].OswaldE;

        // Precompute influence matrix: influence[i,j] = downwash coefficient of wing j on wing i
        // Δα_i = -influence[i,j] × CL_j (from Helmbold: CL_j ≈ CLAlpha_j × α_j)
        var influence = new float[n, n];

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                if (i == j) continue;
                ref readonly var wi = ref wings[i];
                ref readonly var wj = ref wings[j];

                float normalDot = MathF.Abs(Vector3.Dot(wi.Normal, wj.Normal));
                if (normalDot < 0.9f) continue;

                var delta = wj.Centroid - wi.Centroid;
                float gap = MathF.Abs(Vector3.Dot(delta, wi.Normal));
                float overlap = ComputePlanformOverlap(wi, wj);
                if (overlap <= 0) continue;

                float maxSpan = MathF.Max(wi.Span, wj.Span);
                if (gap >= maxSpan) continue;

                float gbRatio = gap / maxSpan;
                float sigma = gbRatio / (1f + gbRatio);

                float arJ = MathF.Max(wj.AspectRatio, 0.1f);
                // Influence coefficient: how much wing j's CL reduces wing i's AoA
                influence[i, j] = overlap * (1f - sigma) / (MathF.PI * arJ);

                // Reduce Oswald efficiency (only once per pair, use upper triangle)
                if (j > i)
                {
                    float eFactor = sigma + (1f - sigma) * 0.5f;
                    oswaldMod[i] *= eFactor;
                    oswaldMod[j] *= eFactor;
                }
            }
        }

        // Iterative solve: α_eff_i = α_i - Σ_j influence[i,j] × CLAlpha_j × α_eff_j
        // Fixed-point iteration (converges in 3-5 steps for typical cases)
        for (int iter = 0; iter < 8; iter++)
        {
            float maxChange = 0;
            for (int i = 0; i < n; i++)
            {
                float downwash = 0;
                for (int j = 0; j < n; j++)
                {
                    if (influence[i, j] == 0) continue;
                    float clJ = wings[j].CLAlpha * alphaEff[j];
                    downwash += influence[i, j] * clJ;
                }
                float newAlpha = alphas[i] - downwash;
                maxChange = MathF.Max(maxChange, MathF.Abs(newAlpha - alphaEff[i]));
                alphaEff[i] = newAlpha;
            }
            if (maxChange < 1e-5f) break; // converged
        }

        // Clamp results
        for (int i = 0; i < n; i++)
        {
            oswaldMod[i] = MathF.Max(0.3f, MathF.Min(0.95f, oswaldMod[i]));
            if (float.IsNaN(alphaEff[i]) || float.IsInfinity(alphaEff[i]))
                alphaEff[i] = 0;
            alphaEff[i] = MathF.Max(-MathF.PI / 2f, MathF.Min(MathF.PI / 2f, alphaEff[i]));
        }

        // Step 3: Compute forces per wing
        var results = new WingForceResult[n];

        for (int i = 0; i < n; i++)
        {
            ref readonly var w = ref wings[i];
            float speed = localSpeeds[i];
            if (speed < MinSpeed) continue;

            float alpha = alphaEff[i];
            float absAlpha = MathF.Abs(alpha);
            float signAlpha = alpha >= 0 ? 1f : -1f;

            // CL via Kirchhoff stall model
            float cl;
            if (absAlpha <= w.AlphaStall)
            {
                // Pre-stall: linear
                cl = w.CLAlpha * alpha;
            }
            else
            {
                // Kirchhoff separation function: f = ((1 + √f0) / 2)² where f0 decays past stall
                float stallExcess = (absAlpha - w.AlphaStall) / (w.AlphaStall + 0.01f);
                float f0 = MathF.Exp(-2f * stallExcess); // separation fraction decays exponentially
                float sqrtF0 = MathF.Sqrt(f0);
                float kirchhoffFactor = 0.25f * (1f + sqrtF0) * (1f + sqrtF0);

                float clAttached = w.CLAlpha * w.AlphaStall * signAlpha;
                float clFlatPlate = MathF.Sin(2f * alpha); // deep stall limit

                // Blend: Kirchhoff at moderate post-stall, flat-plate at deep stall
                float blendToFlat = MathF.Min(1f, stallExcess * 0.5f);
                cl = clAttached * kirchhoffFactor * (1f - blendToFlat) + clFlatPlate * blendToFlat;
            }

            // Dynamic pressure
            float q = 0.5f * rho * speed * speed;

            // Lift force direction: component of normal perpendicular to flow
            var vHat = vHats[i];
            var liftDir = w.Normal - Vector3.Dot(w.Normal, vHat) * vHat;
            float liftDirLen = liftDir.Length();
            if (liftDirLen > 1e-6f)
                liftDir /= liftDirLen;
            else
                liftDir = w.Normal; // fallback (shouldn't happen in practice)

            float liftMag = q * w.PlanformArea * cl;
            var liftForce = liftDir * liftMag;

            // Induced drag: CDi = CL²/(π·e·AR)
            float e = oswaldMod[i];
            float cdi = w.AspectRatio > 0.1f ? cl * cl / (MathF.PI * e * w.AspectRatio) : 0;

            // Profile drag: Cd0 ≈ 0.010 for voxel wings
            float cd0 = 0.010f;
            float totalCd = cdi + cd0;
            float dragMag = q * w.PlanformArea * totalCd;
            var inducedDrag = vHat * dragMag; // along flow direction

            results[i] = new WingForceResult(
                liftForce, inducedDrag, w.AeroCenter,
                cl, cdi, alpha, efficiency[i]);
        }

        return results;
    }

    /// <summary>
    /// Compute approximate planform overlap fraction between two wings.
    /// Projects both onto their shared planform plane and estimates overlap.
    /// </summary>
    private static float ComputePlanformOverlap(in LiftingSurface a, in LiftingSurface b)
    {
        // Project centroids onto span and chord axes (using wing a's axes since normals are parallel)
        var delta = b.Centroid - a.Centroid;
        float spanSep = MathF.Abs(Vector3.Dot(delta, a.SpanAxis));
        float chordSep = MathF.Abs(Vector3.Dot(delta, a.ChordAxis));

        // Overlap in span direction
        float spanOverlap = MathF.Max(0, (a.Span + b.Span) * 0.5f - spanSep);
        float maxSpan = MathF.Max(a.Span, b.Span);
        float spanFrac = maxSpan > 0 ? spanOverlap / maxSpan : 0;

        // Overlap in chord direction
        float chordOverlap = MathF.Max(0, (a.MeanChord + b.MeanChord) * 0.5f - chordSep);
        float maxChord = MathF.Max(a.MeanChord, b.MeanChord);
        float chordFrac = maxChord > 0 ? chordOverlap / maxChord : 0;

        return spanFrac * chordFrac;
    }
}
