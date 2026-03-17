#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// Mach-dependent wing lift model that computes CLAlpha at runtime based on regime:
///   - Subsonic: Compressible Helmbold (Diederich form) using M_perp
///   - Supersonic: Ackeret 2D + finite-wing tip correction
///   - Transonic: Hermite smoothstep blend avoiding the M=1 singularity
///
/// Shares stall/interference/drag machinery with ThinAirfoilWingModel but applies
/// compressibility to the lift slope rather than post-hoc correcting CL.
/// </summary>
public class CompressibleWingModel : IWingLiftModel
{
    public float MinSpeed { get; set; } = 1f;
    public float SubsonicBlendLimit { get; set; } = 0.85f;
    public float SupersonicBlendLimit { get; set; } = 1.15f;

    /// <summary>
    /// Subsonic compressible lift slope (Diederich/Helmbold form).
    /// At M=0, sweep=0 reduces to standard Helmbold: 2π·AR / (2 + √(4 + AR²)).
    /// </summary>
    internal static float SubsonicCLAlpha(float machPerp, float ar, float sweepAngle)
    {
        float beta2 = 1f - machPerp * machPerp;
        if (beta2 < 0.01f) beta2 = 0.01f;
        float cosSweep = MathF.Cos(sweepAngle);
        float cosSweep2 = cosSweep * cosSweep;
        float discriminant = ar * ar * beta2 / cosSweep2 + 4f;
        return 2f * MathF.PI * ar / (2f + MathF.Sqrt(discriminant));
    }

    /// <summary>
    /// Supersonic lift slope: Ackeret 2D + finite-wing tip loss.
    /// </summary>
    internal static float SupersonicCLAlpha(float machPerp, float ar)
    {
        float mach2m1 = machPerp * machPerp - 1f;
        if (mach2m1 < 0.01f) mach2m1 = 0.01f;
        float sqrtM = MathF.Sqrt(mach2m1);
        float cla2d = 4f / sqrtM;
        float tipFactor = MathF.Max(0f, 1f - 1f / (2f * ar * sqrtM));
        return cla2d * tipFactor;
    }

    /// <summary>
    /// Compute CLAlpha for any Mach regime with transonic blending.
    /// </summary>
    internal static float ComputeCLAlpha(float machPerp, float ar, float sweepAngle,
                                          float subLimit, float supLimit)
    {
        if (machPerp < subLimit)
            return SubsonicCLAlpha(machPerp, ar, sweepAngle);
        if (machPerp > supLimit)
            return SupersonicCLAlpha(machPerp, ar);

        float t = (machPerp - subLimit) / (supLimit - subLimit);
        t = t * t * (3f - 2f * t);
        float claSub = SubsonicCLAlpha(subLimit, ar, sweepAngle);
        float claSup = SupersonicCLAlpha(supLimit, ar);
        return claSub + (claSup - claSub) * t;
    }

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

        // Step 1: Compute raw AoA and runtime CLAlpha for each wing
        var alphas = new List<float>(n);
        var localSpeeds = new List<float>(n);
        var vHats = new List<Vector3>(n);
        var clAlphas = new List<float>(n);
        var alphaStalls = new List<float>(n);
        for (int ei = 0; ei < n; ei++)
        {
            alphas.Add(0f);
            localSpeeds.Add(0f);
            vHats.Add(Vector3.Zero);
            clAlphas.Add(0f);
            alphaStalls.Add(0f);
        }

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
                clAlphas[i] = w.CLAlpha;
                alphaStalls[i] = w.AlphaStall;
                continue;
            }
            vHats[i] = v / speed;

            // AoA
            float vDotN = -Vector3.Dot(v, w.Normal);
            var vInPlane = v - Vector3.Dot(v, w.Normal) * w.Normal;
            float vPlaneSpeed = vInPlane.Length();
            alphas[i] = MathF.Atan2(vDotN, vPlaneSpeed + 1e-6f);

            // Runtime CLAlpha from Mach regime
            float mach = speedOfSound > 0 ? speed / speedOfSound : 0;
            float machPerp = mach * MathF.Cos(w.SweepAngle);
            clAlphas[i] = ComputeCLAlpha(machPerp, w.AspectRatio, w.SweepAngle,
                                          SubsonicBlendLimit, SupersonicBlendLimit);

            // Runtime stall angle, floored at 3 degrees
            alphaStalls[i] = clAlphas[i] > 0.01f
                ? MathF.Max(3f * MathF.PI / 180f, w.CLMax / clAlphas[i])
                : MathF.PI / 4f;
        }

        // Step 2: Prandtl biplane interference — mutual downwash
        var alphaEff = new List<float>(n);
        var efficiency = new List<float>(n);
        for (int ei = 0; ei < n; ei++)
        {
            alphaEff.Add(alphas[ei]);
            efficiency.Add(1f);
        }

        var oswaldMod = new List<float>(n);
        for (int ei = 0; ei < n; ei++) oswaldMod.Add(wings[ei].OswaldE);

        // Flat list for n×n influence matrix
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

        // Iterative solve using runtime clAlphas
        for (int iter = 0; iter < 8; iter++)
        {
            float maxChange = 0;
            for (int i = 0; i < n; i++)
            {
                float downwash = 0;
                for (int j = 0; j < n; j++)
                {
                    if (influence[i * n + j] == 0) continue;
                    float clJ = clAlphas[j] * alphaEff[j];
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
            float clAlpha = clAlphas[i];
            float alphaStall = alphaStalls[i];

            // CL via Kirchhoff stall model using runtime clAlpha and alphaStall
            float cl;
            if (absAlpha <= alphaStall)
            {
                cl = clAlpha * alpha;
            }
            else
            {
                float stallExcess = (absAlpha - alphaStall) / (alphaStall + 0.01f);
                float f0 = MathF.Exp(-2f * stallExcess);
                float sqrtF0 = MathF.Sqrt(f0);
                float kirchhoffFactor = 0.25f * (1f + sqrtF0) * (1f + sqrtF0);

                float clAttached = clAlpha * alphaStall * signAlpha;
                float clFlatPlate = MathF.Sin(2f * alpha);

                float blendToFlat = MathF.Min(1f, stallExcess * 0.5f);
                cl = clAttached * kirchhoffFactor * (1f - blendToFlat) + clFlatPlate * blendToFlat;
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
