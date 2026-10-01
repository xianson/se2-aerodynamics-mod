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

    private static int _logCooldown;

    // Cached work arrays — reused across frames to avoid per-frame allocations
    private float[] _alphas = Array.Empty<float>();
    private float[] _localSpeeds = Array.Empty<float>();
    private Vector3[] _vHats = Array.Empty<Vector3>();
    private float[] _clAlphas = Array.Empty<float>();
    private float[] _machPerps = Array.Empty<float>();
    private float[] _alphaStalls = Array.Empty<float>();
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
        _clAlphas = new float[n];
        _machPerps = new float[n];
        _alphaStalls = new float[n];
        _alphaEff = new float[n];
        _efficiency = new float[n];
        _oswaldMod = new float[n];
        // Influence is n*n, allocated separately
    }

    private void EnsureInfluenceArray(int n)
    {
        int nn = n * n;
        if (_influence.Length >= nn) return;
        _influence = new float[nn];
        _influenceValid = false;
    }

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

        // Step 1: Compute raw AoA and runtime CLAlpha for each wing
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
                _clAlphas[i] = w.CLAlpha;
                _alphaStalls[i] = w.AlphaStall;
                continue;
            }
            _vHats[i] = v / speed;

            // AoA
            float vDotN = -Vector3.Dot(v, w.Normal);
            var vInPlane = v - Vector3.Dot(v, w.Normal) * w.Normal;
            float vPlaneSpeed = vInPlane.Length();
            _alphas[i] = MathF.Atan2(vDotN, vPlaneSpeed + 1e-6f);

            // Runtime CLAlpha from Mach regime
            float mach = speedOfSound > 0 ? speed / speedOfSound : 0;
            float machPerp = mach * MathF.Cos(w.SweepAngle);
            _machPerps[i] = machPerp;
            _clAlphas[i] = ComputeCLAlpha(machPerp, w.AspectRatio, w.SweepAngle,
                                          SubsonicBlendLimit, SupersonicBlendLimit);

            // Runtime stall angle, floored at 3 degrees
            _alphaStalls[i] = _clAlphas[i] > 0.01f
                ? MathF.Max(3f * MathF.PI / 180f, w.CLMax / _clAlphas[i])
                : MathF.PI / 4f;
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

        // Iterative solve using runtime clAlphas
        for (int iter = 0; iter < 8; iter++)
        {
            float maxChange = 0;
            for (int i = 0; i < n; i++)
            {
                float downwash = 0;
                for (int j = 0; j < n; j++)
                {
                    if (_influence[i * n + j] == 0) continue;
                    float clJ = _clAlphas[j] * _alphaEff[j];
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
            float clAlpha = _clAlphas[i];
            float alphaStall = _alphaStalls[i];

            // CL via Kirchhoff stall model using runtime clAlpha and alphaStall
            float cl;
            // Pressure drag of separated flow: none attached; past stall it grows to a flat plate's (normal force
            // 2 sin a: drag 2 sin^2 a), with the same blend as the lift. (The wing owns its skins - the face model
            // leaves them out - so without it a stalled wing kept an L/D of 17 at 36 degrees.)
            float cdSep = 0f;
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
                float sinA = MathF.Sin(absAlpha);
                cdSep = 2f * sinA * sinA * MathF.Max(blendToFlat, 1f - kirchhoffFactor);
                cl = clAttached * kirchhoffFactor * (1f - blendToFlat) + clFlatPlate * blendToFlat;
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
            // φ = (h / b)² / (1 + (h / b)²), where h = ground height, b = span
            // CDi_eff = CDi * φ  (approaches 0 at h→0, 1 at h→∞)
            // Also boosts effective CL slightly (reduced downwash = more effective AoA)
            float groundFactor = 1f;
            if (ctx.GroundHeight >= 0f && w.Span > 0.1f)
            {
                float hb = ctx.GroundHeight / w.Span;
                groundFactor = hb * hb / (1f + hb * hb);
                groundFactor = MathF.Max(0.1f, groundFactor); // never fully eliminate drag
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
            Vector3 cpShiftAoA = w.ChordAxis * (w.MeanChord * 0.25f * cpBlend);

            // ── Mach tuck: CoP shifts aft at transonic (uses machPerp for sweep) ──
            float machTuck = MachTuckFactor(_machPerps[i]);
            Vector3 cpShiftMach = w.ChordAxis * (w.MeanChord * machTuck);

            Vector3 applicationPoint = w.AeroCenter + cpShiftAoA + cpShiftMach;

            _resultsList[i] = new WingForceResult(
                liftForce, inducedDrag, applicationPoint,
                cl, cdi, alpha, _efficiency[i]);
        }

        // Throttled wing diagnostics (~1/sec)
        if (--_logCooldown <= 0 && n > 0)
        {
            _logCooldown = 60;
            float mach = speedOfSound > 0 ? comSpeed / speedOfSound : 0;
            for (int i = 0; i < n; i++)
            {
                ref readonly var w = ref wings[i];
                var wf = _resultsList[i];
                float sweepDeg = w.SweepAngle * 180f / MathF.PI;
                float alphaDeg = _alphas[i] * 180f / MathF.PI;
                float tuck = MachTuckFactor(_machPerps[i]);
                Log.Default?.Info(
                    $"[AERO-WING] W{i} M={mach:F2} Mperp={_machPerps[i]:F2} sweep={sweepDeg:F1}° " +
                    $"AoA={alphaDeg:F1}° CL={wf.CL:F3} CLa={_clAlphas[i]:F3} " +
                    $"L={wf.LiftForce.Length():F0}N Di={wf.InducedDrag.Length():F0}N " +
                    $"AR={w.AspectRatio:F1} S={w.PlanformArea:F1}m² span={w.Span:F1}m " +
                    $"tuck={tuck:F3} chord={w.MeanChord:F2}m " +
                    $"n=({w.Normal.X:F3},{w.Normal.Y:F3},{w.Normal.Z:F3}) " +
                    $"cen=({w.Centroid.X:F1},{w.Centroid.Y:F1},{w.Centroid.Z:F1})");
            }
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

    /// <summary>
    /// CoP rearward shift fraction as a function of Mach perpendicular to leading edge.
    /// Swept wings naturally delay onset because machPerp = M∞ × cos(Λ).
    /// Returns fraction of chord to shift CoP aft (0 = no shift, 0.22 = 22% chord).
    /// </summary>
    private static float MachTuckFactor(float machPerp)
    {
        if (machPerp < 0.7f) return 0f;

        if (machPerp < 1.0f)
        {
            float t = (machPerp - 0.7f) / 0.3f;
            return 0.20f * t * t;
        }

        if (machPerp < 1.3f)
        {
            float t = (machPerp - 1.0f) / 0.3f;
            return 0.20f + 0.02f * t;
        }

        return 0.22f;
    }
}
