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

    // Cached work arrays — reused across frames to avoid per-frame allocations
    private float[] _alphas = Array.Empty<float>();
    private float[] _localSpeeds = Array.Empty<float>();
    private Vector3[] _vHats = Array.Empty<Vector3>();
    private float[] _alphaEff = Array.Empty<float>();
    private float[] _efficiency = Array.Empty<float>();
    private float[] _oswaldMod = Array.Empty<float>();
    private float[] _influence = Array.Empty<float>();
    private bool _influenceValid;
    private int _influenceN;
    private readonly List<WingForceResult> _resultsList = new();
    private static readonly List<WingForceResult> EmptyResults = new();

    /// <summary>Invalidate cached influence matrix (call when wings change).</summary>
    public void InvalidateInfluence()
    {
        _influenceValid = false;
    }

    private void EnsureArrays(int n)
    {
        if (_alphas.Length >= n) return;
        _alphas = new float[n];
        _localSpeeds = new float[n];
        _vHats = new Vector3[n];
        _alphaEff = new float[n];
        _efficiency = new float[n];
        _oswaldMod = new float[n];
    }

    private void EnsureInfluenceArray(int n)
    {
        int nn = n * n;
        if (_influence.Length >= nn) return;
        _influence = new float[nn];
        _influenceValid = false;
    }

    public List<WingForceResult> ComputeWingForces(ReadOnlySpan<LiftingSurface> wings, in AeroContext ctx)
    {
        int n = wings.Length;
        if (n == 0) return EmptyResults;

        float comSpeed = ctx.Velocity.Length();
        if (comSpeed < MinSpeed || ctx.Atmosphere.Density < 1e-8)
        {
            _resultsList.Clear();
            for (int ei = 0; ei < n; ei++) _resultsList.Add(default);
            return _resultsList;
        }

        EnsureArrays(n);
        EnsureInfluenceArray(n);

        float rho = (float)ctx.Atmosphere.Density;
        float speedOfSound = (float)ctx.Atmosphere.SpeedOfSound;

        // Step 1: Compute raw AoA for each wing
        for (int i = 0; i < n; i++)
        {
            ref readonly var w = ref wings[i];
            var v = ctx.VelocityAtPoint(w.AeroCenter);
            float speed = v.Length();
            _localSpeeds[i] = speed;
            if (speed < MinSpeed)
            {
                _alphas[i] = 0;
                _vHats[i] = Vector3.UnitX;
                continue;
            }
            _vHats[i] = v / speed;

            float vDotN = -Vector3.Dot(v, w.Normal);
            var vInPlane = v - Vector3.Dot(v, w.Normal) * w.Normal;
            float vPlaneSpeed = vInPlane.Length();
            _alphas[i] = MathF.Atan2(vDotN, vPlaneSpeed + 1e-6f);
        }

        // Step 2: Prandtl biplane interference — mutual downwash
        for (int i = 0; i < n; i++)
        {
            _alphaEff[i] = _alphas[i];
            _efficiency[i] = 1f;
        }

        if (!_influenceValid || _influenceN != n)
        {
            // Rebuild influence matrix and oswald modifiers
            for (int i = 0; i < n; i++)
                _oswaldMod[i] = wings[i].OswaldE;

            Array.Clear(_influence, 0, n * n);

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
                    _influence[i * n + j] = overlap * (1f - sigma) / (MathF.PI * arJ);

                    if (j > i)
                    {
                        float eFactor = sigma + (1f - sigma) * 0.5f;
                        _oswaldMod[i] *= eFactor;
                        _oswaldMod[j] *= eFactor;
                    }
                }
            }

            _influenceValid = true;
            _influenceN = n;
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
                    if (_influence[i * n + j] == 0) continue;
                    float clJ = wings[j].CLAlpha * _alphaEff[j];
                    downwash += _influence[i * n + j] * clJ;
                }
                float newAlpha = _alphas[i] - downwash;
                maxChange = MathF.Max(maxChange, MathF.Abs(newAlpha - _alphaEff[i]));
                _alphaEff[i] = newAlpha;
            }
            if (maxChange < 1e-5f) break;
        }

        // Clamp
        for (int i = 0; i < n; i++)
        {
            _oswaldMod[i] = MathF.Max(0.3f, MathF.Min(0.95f, _oswaldMod[i]));
            if (float.IsNaN(_alphaEff[i]) || float.IsInfinity(_alphaEff[i]))
                _alphaEff[i] = 0;
            _alphaEff[i] = MathF.Max(-MathF.PI / 2f, MathF.Min(MathF.PI / 2f, _alphaEff[i]));
        }

        // Step 3: Compute forces per wing
        _resultsList.Clear();
        for (int ei = 0; ei < n; ei++) _resultsList.Add(default);

        for (int i = 0; i < n; i++)
        {
            ref readonly var w = ref wings[i];
            float speed = _localSpeeds[i];
            if (speed < MinSpeed) continue;

            float alpha = _alphaEff[i];
            float absAlpha = MathF.Abs(alpha);
            float signAlpha = alpha >= 0 ? 1f : -1f;

            // CL via Kirchhoff stall model
            float cl;
            // Pressure drag of separated flow: none attached; past stall it grows to a flat plate's (normal force
            // 2 sin a: drag 2 sin^2 a), with the same blend as the lift. (The wing owns its skins - the face model
            // leaves them out - so without it a stalled wing kept an L/D of 17 at 36 degrees.)
            float cdSep = 0f;
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
                float sinA = MathF.Sin(absAlpha);
                cdSep = 2f * sinA * sinA * MathF.Max(blendToFlat, 1f - kirchhoffFactor);
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
            var vHat = _vHats[i];
            var liftDir = w.Normal - Vector3.Dot(w.Normal, vHat) * vHat;
            float liftDirLen = liftDir.Length();
            if (liftDirLen > 1e-6f)
                liftDir /= liftDirLen;
            else
                liftDir = w.Normal;

            float liftMag = q * w.PlanformArea * cl;
            var liftForce = liftDir * liftMag;

            // Induced drag: CDi = CL²/(π·e·AR)
            float e = _oswaldMod[i];
            float cdi = w.AspectRatio > 0.1f ? cl * cl / (MathF.PI * e * w.AspectRatio) : 0;

            float cd0 = 0.010f;

            // ── Ground effect: reduce induced drag near the surface ──
            float groundFactor = 1f;
            if (ctx.GroundHeight >= 0f && w.Span > 0.1f)
            {
                float hb = ctx.GroundHeight / w.Span;
                groundFactor = hb * hb / (1f + hb * hb);
                groundFactor = MathF.Max(0.1f, groundFactor);
            }
            cdi *= groundFactor;

            float totalCd = cdi + cd0 + cdSep;
            float dragMag = q * w.PlanformArea * totalCd;
            // (against the motion: vHat is the direction the wing moves - it pushed the wing forward before, ~95 kN
            // of free thrust on the Jetliner, hidden by a drag floor)
            var inducedDrag = -vHat * dragMag;

            // ── Center of pressure shift with AoA ──
            float absAlphaFrac = absAlpha / (MathF.PI * 0.5f);
            float cpBlend = absAlphaFrac * absAlphaFrac;
            Vector3 cpShift = w.ChordAxis * (w.MeanChord * 0.25f * cpBlend);
            Vector3 applicationPoint = w.AeroCenter + cpShift;

            _resultsList[i] = new WingForceResult(
                liftForce, inducedDrag, applicationPoint,
                cl, cdi, alpha, _efficiency[i]);
        }

        return _resultsList;
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
