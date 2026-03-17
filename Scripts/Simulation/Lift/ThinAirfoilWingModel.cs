#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// Wing lift model based on thin airfoil theory with:
///   - Helmbold finite-wing correction (via precomputed CLAlpha)
///   - Kirchhoff stall model (smooth CL decay past α_stall)
///   - Prandtl-Glauert compressibility correction
///   - Prandtl biplane mutual interference (O(N²) for N wings)
///   - Induced drag from CL²/(π·e·AR)
/// </summary>
public class ThinAirfoilWingModel : IWingLiftModel
{
    /// <summary>Minimum speed to compute wing forces (m/s).</summary>
    public float MinSpeed { get; set; } = 1f;

    public List<WingForceResult> ComputeWingForces(ReadOnlySpan<LiftingSurface> wings, in AeroContext ctx)
    {
        int n = wings.Length;
        if (n == 0) return new List<WingForceResult>();

        float comSpeed = ctx.Velocity.Length();
        if (comSpeed < MinSpeed || ctx.Atmosphere.Density < 1e-8)
        {
            var empty = new List<WingForceResult>(n);
            for (int ei = 0; ei < n; ei++) empty.Add(default);
            return empty;
        }

        float rho = (float)ctx.Atmosphere.Density;
        float speedOfSound = (float)ctx.Atmosphere.SpeedOfSound;

        // Step 1: Compute raw AoA for each wing
        var alphas = new List<float>(n);
        var localSpeeds = new List<float>(n);
        var vHats = new List<Vector3>(n);
        for (int ei = 0; ei < n; ei++) { alphas.Add(0f); localSpeeds.Add(0f); vHats.Add(Vector3.Zero); }

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

            float vDotN = -Vector3.Dot(v, w.Normal);
            var vInPlane = v - Vector3.Dot(v, w.Normal) * w.Normal;
            float vPlaneSpeed = vInPlane.Length();
            alphas[i] = MathF.Atan2(vDotN, vPlaneSpeed + 1e-6f);
        }

        // Step 2: Prandtl biplane interference — mutual downwash
        var alphaEff = new List<float>(n);
        var efficiency = new List<float>(n);
        for (int ei = 0; ei < n; ei++) { alphaEff.Add(alphas[ei]); efficiency.Add(1f); }

        var oswaldMod = new List<float>(n);
        for (int ei = 0; ei < n; ei++) oswaldMod.Add(wings[ei].OswaldE);

        // Flat list for n×n influence matrix: influence[i,j] = influence[i*n+j]
        var influence = new List<float>(n * n);
        for (int ei = 0; ei < n * n; ei++) influence.Add(0f);

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
                influence[i * n + j] = overlap * (1f - sigma) / (MathF.PI * arJ);

                if (j > i)
                {
                    float eFactor = sigma + (1f - sigma) * 0.5f;
                    oswaldMod[i] *= eFactor;
                    oswaldMod[j] *= eFactor;
                }
            }
        }

        // Fixed-point iteration
        for (int iter = 0; iter < 8; iter++)
        {
            float maxChange = 0;
            for (int i = 0; i < n; i++)
            {
                float downwash = 0;
                for (int j = 0; j < n; j++)
                {
                    if (influence[i * n + j] == 0) continue;
                    float clJ = wings[j].CLAlpha * alphaEff[j];
                    downwash += influence[i * n + j] * clJ;
                }
                float newAlpha = alphas[i] - downwash;
                maxChange = MathF.Max(maxChange, MathF.Abs(newAlpha - alphaEff[i]));
                alphaEff[i] = newAlpha;
            }
            if (maxChange < 1e-5f) break;
        }

        // Clamp
        for (int i = 0; i < n; i++)
        {
            oswaldMod[i] = MathF.Max(0.3f, MathF.Min(0.95f, oswaldMod[i]));
            if (float.IsNaN(alphaEff[i]) || float.IsInfinity(alphaEff[i]))
                alphaEff[i] = 0;
            alphaEff[i] = MathF.Max(-MathF.PI / 2f, MathF.Min(MathF.PI / 2f, alphaEff[i]));
        }

        // Step 3: Compute forces per wing
        var results = new List<WingForceResult>(n);
        for (int ei = 0; ei < n; ei++) results.Add(default);

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
                cl = w.CLAlpha * alpha;
            }
            else
            {
                float stallExcess = (absAlpha - w.AlphaStall) / (w.AlphaStall + 0.01f);
                float f0 = MathF.Exp(-2f * stallExcess);
                float sqrtF0 = MathF.Sqrt(f0);
                float kirchhoffFactor = 0.25f * (1f + sqrtF0) * (1f + sqrtF0);

                float clAttached = w.CLAlpha * w.AlphaStall * signAlpha;
                float clFlatPlate = MathF.Sin(2f * alpha);

                float blendToFlat = MathF.Min(1f, stallExcess * 0.5f);
                cl = clAttached * kirchhoffFactor * (1f - blendToFlat) + clFlatPlate * blendToFlat;
            }

            // Prandtl-Glauert compressibility correction
            float mach = speedOfSound > 0 ? speed / speedOfSound : 0;
            if (mach > 0.01f && mach < 0.85f)
            {
                float beta = MathF.Sqrt(1f - mach * mach);
                cl /= beta;
            }
            else if (mach >= 1.05f)
            {
                float beta = MathF.Sqrt(mach * mach - 1f);
                cl /= beta;
            }
            else if (mach >= 0.85f)
            {
                float t = (mach - 0.85f) / 0.2f;
                t = t * t * (3f - 2f * t);
                float subBeta = MathF.Sqrt(MathF.Max(0.01f, 1f - 0.85f * 0.85f));
                float supBeta = MathF.Sqrt(1.05f * 1.05f - 1f);
                float correction = 1f / subBeta + (1f / supBeta - 1f / subBeta) * t;
                cl *= correction;
            }

            float q = 0.5f * rho * speed * speed;

            // Lift force direction
            var vHat = vHats[i];
            var liftDir = w.Normal - Vector3.Dot(w.Normal, vHat) * vHat;
            float liftDirLen = liftDir.Length();
            if (liftDirLen > 1e-6f)
                liftDir /= liftDirLen;
            else
                liftDir = w.Normal;

            float liftMag = q * w.PlanformArea * cl;
            var liftForce = liftDir * liftMag;

            // Induced drag: CDi = CL²/(π·e·AR)
            float e = oswaldMod[i];
            float cdi = w.AspectRatio > 0.1f ? cl * cl / (MathF.PI * e * w.AspectRatio) : 0;

            float cd0 = 0.010f;
            float totalCd = cdi + cd0;
            float dragMag = q * w.PlanformArea * totalCd;
            var inducedDrag = vHat * dragMag;

            results[i] = new WingForceResult(
                liftForce, inducedDrag, w.AeroCenter,
                cl, cdi, alpha, efficiency[i]);
        }

        return results;
    }

    private static float ComputePlanformOverlap(in LiftingSurface a, in LiftingSurface b)
    {
        var delta = b.Centroid - a.Centroid;
        float spanSep = MathF.Abs(Vector3.Dot(delta, a.SpanAxis));
        float chordSep = MathF.Abs(Vector3.Dot(delta, a.ChordAxis));

        float spanOverlap = MathF.Max(0, (a.Span + b.Span) * 0.5f - spanSep);
        float maxSpan = MathF.Max(a.Span, b.Span);
        float spanFrac = maxSpan > 0 ? spanOverlap / maxSpan : 0;

        float chordOverlap = MathF.Max(0, (a.MeanChord + b.MeanChord) * 0.5f - chordSep);
        float maxChord = MathF.Max(a.MeanChord, b.MeanChord);
        float chordFrac = maxChord > 0 ? chordOverlap / maxChord : 0;

        return spanFrac * chordFrac;
    }
}
