#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// Deflectable lift/drag surface — aileron, elevator, rudder, canard.
/// Produces lift via thin airfoil theory with Kirchhoff stall, plus
/// Mach-dependent CLAlpha from CompressibleWingModel.
///
/// Physics:
///   1. Rotate chord about hinge by deflection (Rodrigues)
///   2. Compute AoA between deflected chord and local flow
///   3. CLAlpha from CompressibleWingModel (Mach-corrected)
///   4. CL via Kirchhoff stall model (same as wing models)
///   5. Lift + induced drag + parasitic drag
/// </summary>
public class ControlSurface : IAeroBlockComponent, IFaceOverride
{
    // ── Configuration ──

    /// <summary>Rotation axis, grid-local unit vector.</summary>
    public Vector3 HingeAxis { get; }

    /// <summary>Undeflected chord direction (LE→TE), unit vector.</summary>
    public Vector3 ChordDirection { get; }

    /// <summary>Planform area in m².</summary>
    public float Area { get; }

    /// <summary>Maximum deflection angle in degrees.</summary>
    public float MaxDeflection { get; set; } = 10f;

    /// <summary>Parasitic drag coefficient.</summary>
    public float Cd0 { get; set; } = 0.012f;

    /// <summary>Sweep angle in radians (0 for unswept).</summary>
    public float SweepAngle { get; set; } = 0f;

    /// <summary>Aspect ratio (span / chord) for the lift slope and induced drag: 4 for the old 1x1 block (a guess),
    /// 1 for the square 3x3x1 surfaces.</summary>
    public float AspectRatio { get; set; } = 4f;

    // ── Derived (precomputed) ──

    private const float OswaldE = 0.85f;

    // ── IAeroBlockComponent ──

    public Vector3 Position { get; }
    public Vector3I BlockPosition { get; }

    /// <summary>Set for a surface with a moving flap model (the 3x3x1 blocks): its block's AABB Min + Max, under which
    /// the server publishes DeflectionAngle for the client's flap (AeroFlapComponent).</summary>
    public Vector3I? FlapKey { get; init; }

    // ── IFaceOverride ──

    private readonly List<Vector3I> _ownedCells;
    public IReadOnlyList<Vector3I> OwnedCells => _ownedCells;

    // ── Mutable input ──

    /// <summary>DEV (harness: aeroset ControlSurface.TestInput 1): every surface held at this input instead of the
    /// mixer's (a visual check of the flap models); NaN = off, the default.</summary>
    public static float TestInput = float.NaN;

    /// <summary>The rate-steering law (a pilot's rotation rates, no reticle): a surface's input from its effectiveness
    /// (unit torque direction of +input), the rotation wanted and the rotation now (rad/s, grid frame). The command, scaled
    /// down at high dynamic pressure, plus damping of the rate ERROR. (Damping the rate itself - before 2026-10-12 - made a
    /// surface push against a turn held at the asked rate: on the Tu-134 at 180 m/s, -240 kN m of roll against a 0.5 rad/s
    /// roll command; the gyros won. On the error, a held turn is assisted and a released one still damped.)</summary>
    public static float RateCommand(Vector3 effNorm, Vector3 want, Vector3 now, float gainScale, float kp = RateKp, float kd = RateKd)
        => Vector3.Dot(effNorm, want) * kp * gainScale - Vector3.Dot(effNorm, now - want) * kd;
    public const float RateKp = 1.0f, RateKd = 1.5f;

    /// <summary>The inputs (u, -1..+1) for surfaces whose torque per unit input is a[i], giving the torque wanted: least
    /// squares over the surfaces still free (u_i = a_i . x, (sum a a^T + lambda I) x = rest), those past +-1 then held at
    /// the limit and the rest re-solved for what remains (an active set: a plain clamp lost what a saturated surface could
    /// not give, though the others could). False: no authority at all.</summary>
    public static bool Allocate(IReadOnlyList<Vector3> a, Vector3 want, float[] u)
    {
        // two candidates, the one nearer the demand kept: the active set (meets any demand within reach), and the plain
        // least-squares inputs scaled down until the largest is +-1 (the demand's direction exactly, never worse than nothing:
        // past reach the active set's greedy holds could miss sideways)
        int n = a.Count;
        Span<float> s = n <= 64 ? stackalloc float[n] : new float[n];
        if (!ActiveSet(a, want, u, n, 0)) return false;
        ActiveSet(a, want, s, n, 1);   // (one pass: the plain least squares, unclamped)
        float peak = 0f; for (int i = 0; i < n; i++) peak = MathF.Max(peak, MathF.Abs(s[i]));
        if (peak > 1f) for (int i = 0; i < n; i++) s[i] /= peak;
        Vector3 ga = Vector3.Zero, gs = Vector3.Zero;
        for (int i = 0; i < n; i++) { ga += a[i] * u[i]; gs += a[i] * s[i]; }
        if ((gs - want).LengthSquared() < (ga - want).LengthSquared()) for (int i = 0; i < n; i++) u[i] = s[i];
        // then accelerated projected gradient (FISTA) on |sum u a - want|^2 within +-1 - convex, so it closes on the best inputs
        // the surfaces have (the greedy holds above are not always the best set); step 1 / the largest eigenvalue of sum a a^T
        float m00 = 0, m01 = 0, m02 = 0, m11 = 0, m12 = 0, m22 = 0;
        for (int i = 0; i < n; i++) { var v = a[i]; m00 += v.X * v.X; m01 += v.X * v.Y; m02 += v.X * v.Z; m11 += v.Y * v.Y; m12 += v.Y * v.Z; m22 += v.Z * v.Z; }
        var e = new Vector3(1f, 0.7f, 0.4f); float lmax = 0f;
        for (int k = 0; k < 24; k++)
        {
            var me = new Vector3(m00 * e.X + m01 * e.Y + m02 * e.Z, m01 * e.X + m11 * e.Y + m12 * e.Z, m02 * e.X + m12 * e.Y + m22 * e.Z);
            lmax = me.Length(); if (!(lmax > 0f)) break; e = me / lmax;
        }
        if (lmax > 0f)
        {
            float step = 1f / (lmax * 1.02f), t = 1f;
            Span<float> y = n <= 64 ? stackalloc float[n] : new float[n];
            Span<float> prev = n <= 64 ? stackalloc float[n] : new float[n];
            for (int i = 0; i < n; i++) y[i] = prev[i] = u[i];
            for (int it = 0; it < RefineSteps; it++)
            {
                Vector3 got = Vector3.Zero; for (int i = 0; i < n; i++) got += a[i] * y[i];
                Vector3 miss = got - want;
                if (miss.LengthSquared() < 1e-6f * want.LengthSquared() + 1e-4f) break;   // (met to 0.1 %: typical ticks stop early)
                for (int i = 0; i < n; i++) u[i] = Math.Clamp(y[i] - step * Vector3.Dot(a[i], miss), -1f, 1f);
                float tn = 0.5f * (1f + MathF.Sqrt(1f + 4f * t * t)), mom = (t - 1f) / tn; t = tn;
                float moved = 0f;
                for (int i = 0; i < n; i++) { moved = MathF.Max(moved, MathF.Abs(u[i] - prev[i])); y[i] = u[i] + mom * (u[i] - prev[i]); prev[i] = u[i]; }
                if (moved < 1e-5f) break;   // (settled: a demand past reach is never met - this ends it)
            }
        }
        return true;
    }
    /// <summary>The refinement's step cap (poorly conditioned surfaces - torque directions near a plane - need hundreds).</summary>
    public static int RefineSteps = 1000;

    static bool ActiveSet(IReadOnlyList<Vector3> a, Vector3 want, Span<float> u, int n, int plain)
    {
        Span<bool> held = n <= 64 ? stackalloc bool[n] : new bool[n];
        for (int i = 0; i < n; i++) { u[i] = 0f; held[i] = false; }
        bool any = false;
        for (int pass = 0; pass <= n; pass++)
        {
            Vector3 rest = want;
            for (int i = 0; i < n; i++) if (held[i]) rest -= a[i] * u[i];
            float m00 = 0, m01 = 0, m02 = 0, m11 = 0, m12 = 0, m22 = 0; int free = 0;
            for (int i = 0; i < n; i++)
            {
                if (held[i]) continue;
                var v = a[i]; free++;
                m00 += v.X * v.X; m01 += v.X * v.Y; m02 += v.X * v.Z; m11 += v.Y * v.Y; m12 += v.Y * v.Z; m22 += v.Z * v.Z;
            }
            if (free == 0) break;
            float lam = 1e-4f * (m00 + m11 + m22) + 1e-6f;
            m00 += lam; m11 += lam; m22 += lam;
            float c00 = m11 * m22 - m12 * m12, c01 = m02 * m12 - m01 * m22, c02 = m01 * m12 - m02 * m11;
            float det = m00 * c00 + m01 * c01 + m02 * c02;
            if (!(MathF.Abs(det) > 1e-12f)) break;
            any = true;
            float c11 = m00 * m22 - m02 * m02, c12 = m01 * m02 - m00 * m12, c22 = m00 * m11 - m01 * m01;
            var x = new Vector3(c00 * rest.X + c01 * rest.Y + c02 * rest.Z, c01 * rest.X + c11 * rest.Y + c12 * rest.Z,
                                c02 * rest.X + c12 * rest.Y + c22 * rest.Z) / det;
            bool over = false;
            for (int i = 0; i < n; i++)
            {
                if (held[i]) continue;
                float v = Vector3.Dot(a[i], x);
                if (plain == 0 && MathF.Abs(v) > 1f) { u[i] = MathF.Sign(v); held[i] = true; over = true; }
                else u[i] = v;
            }
            if (!over) break;
        }
        return any;
    }

    /// <summary>Deflection input from autopilot/player, -1..+1.</summary>
    public float DeflectionInput { get; set; }

    /// <summary>The block's size along the grid axes (m): its span, chord and thickness are this along the hinge, the chord
    /// and their normal (zero: unknown - a lone plate of AspectRatio).</summary>
    public Vector3 Extents { get; init; }
    /// <summary>What it sits in, measured from the grid's cells (ControlSurfaceGeometry.Measure; null: a lone plate of
    /// AspectRatio) - its host's chord ahead of each edge, the section's span, the end plates.</summary>
    public SurfaceSurroundings Surroundings { get; set; }
    /// <summary>The section it flew as at its last Compute (diagnostics: harness / logs).</summary>
    public float LastHostChord { get; private set; }
    public float LastAspectRatio { get; private set; }
    public float LastTau { get; private set; } = 1f;

    /// <summary>The block's terminal settings (in game: its AeroSurfaceSettingsComponent; null: every axis, as built).</summary>
    public IControlSurfaceSettings Settings { get; set; }

    // ── Output state (readable after Compute) ──

    /// <summary>Current lift force magnitude (N).</summary>
    public float CurrentLift { get; private set; }

    /// <summary>Current drag force magnitude (N).</summary>
    public float CurrentDrag { get; private set; }

    /// <summary>Effective angle of attack (degrees).</summary>
    public float EffectiveAoA { get; private set; }

    /// <summary>Effective Cp for heatmap: |CL| (same as wings).</summary>
    public float EffectiveCp { get; private set; }

    /// <summary>The deflection now (rad, about HingeAxis, right-handed): what the flap's model is turned by.</summary>
    public float DeflectionAngle => -DeflectionInput * MaxDeflection * MathF.PI / 180f;

    public ControlSurface(Vector3 position, Vector3I blockPosition,
        Vector3 hingeAxis, Vector3 chordDirection, float area)
    {
        Position = position;
        BlockPosition = blockPosition;
        HingeAxis = Vector3.Normalize(hingeAxis);
        ChordDirection = Vector3.Normalize(chordDirection);
        Area = area;
        _ownedCells = new List<Vector3I> { blockPosition };
    }

    /// <summary>
    /// The surface's air (2026-10-13: a section of its own, measured, independent of wing detection). With the air along the
    /// chord one way or the other (placed either way round: the air decides which edge leads), the section is the host
    /// structure ahead of the leading edge plus this surface (SurfaceSurroundings; none: a lone plate):
    ///  - its OWN area lifts at its own angle of attack alpha0 (the block is out of the voxel surface, so this is its share
    ///    of the wing it is part of);
    ///  - its DEFLECTION turns the whole section: tau(flap share of the chord) x the deflection, over the section strip
    ///    ahead of and including it (host area + own area) - the host's own lift stays the air model's;
    ///  - lift slope from the section's aspect ratio (span / chord, x2 per end plate; Mach as the wings), stall later the
    ///    lower it is; past stall the Kirchhoff curve of the wing models;
    ///  - drag: Cd0 on its area, induced on the strip, and the deflection's own form drag (0.9 E^1.38 sin^2 delta);
    ///  - the deflection's lift acts at the strip's centre (half a host chord ahead): an extra moment.
    /// No host, no row, no plates: an all-moving plate - its own lift at alpha0 + delta, as before.
    /// </summary>
    public ComponentForceResult Compute(in LocalAeroConditions conditions)
    {
        CurrentLift = 0;
        CurrentDrag = 0;
        EffectiveAoA = 0;
        EffectiveCp = 0;

        if (conditions.Speed < 0.01f || conditions.Atmosphere.Density < 1e-8)
            return ComponentForceResult.Zero;

        Vector3 airflow = -conditions.Velocity;
        bool alongC = Vector3.Dot(airflow, ChordDirection) >= 0f;
        Vector3 chord = alongC ? ChordDirection : -ChordDirection;   // (LE -> TE, with the air)

        // 1. the section: the host ahead of the leading edge (air along +C: the leading edge is the -C one, [1])
        float cf = Extents != Vector3.Zero ? MathF.Abs(Vector3.Dot(Extents, ChordDirection)) : MathF.Sqrt(Area / MathF.Max(0.1f, AspectRatio));
        var g = Surroundings;
        int lead = alongC ? 1 : 0;
        float ch = g != null ? g.HostChord[lead] : 0f;
        float c = cf + ch;
        float ar = g != null ? Math.Clamp(g.SectionSpan[lead] / c * (1 + g.EndPlates[lead]), 0.3f, 12f) : AspectRatio;
        float E = cf / c;
        float tau = ControlSurfaceGeometry.FlapEffectiveness(E);
        float strip = Area / E;   // (the section strip: span x (host + own chord))
        LastHostChord = ch; LastAspectRatio = ar; LastTau = tau;

        // 2. angles: alpha0 the air's on the undeflected section, dAlpha what the deflection adds (+input: + alpha)
        Vector3 n0 = Vector3.Normalize(Vector3.Cross(HingeAxis, chord));
        float alpha0 = MathF.Atan2(Vector3.Dot(airflow, n0), Vector3.Dot(airflow, chord));
        Vector3 dChord = Rodrigues(chord, HingeAxis, DeflectionAngle);
        Vector3 nD = Vector3.Normalize(Vector3.Cross(HingeAxis, dChord));
        float dAlpha = WrapPi(MathF.Atan2(Vector3.Dot(airflow, nD), Vector3.Dot(airflow, dChord)) - alpha0);
        float alphaE = alpha0 + tau * dAlpha;

        // 3. lift slope (Mach, the section's aspect ratio) and stall
        float clAlpha = CompressibleWingModel.ComputeCLAlpha(conditions.Mach * MathF.Cos(SweepAngle), ar, SweepAngle, 0.85f, 1.15f);
        float alphaStall = StallAngle(ar);
        float cl = Kirchhoff(alphaE, clAlpha, alphaStall);
        float f = MathF.Abs(alphaE) > 1e-4f && clAlpha > 0.01f ? cl / (clAlpha * alphaE) : 1f;   // (1 while attached)

        // 4. forces
        float q = conditions.DynamicPressure;
        float liftOwn = q * f * clAlpha * alpha0 * Area;
        float liftDefl = q * f * clAlpha * tau * dAlpha * strip;
        // ground effect (GroundEffect: all lift, doubled coming down to land): its height, its strip's span (b = sqrt(AR S))
        float phiG = 1f, geStrength = 1f + conditions.Landing;
        if (conditions.GroundHeight >= 0f)
        {
            float hG = conditions.GroundHeight + (conditions.GroundUp.LengthSquared() > 0.5f ? Vector3.Dot(Position, conditions.GroundUp) : 0f);
            phiG = GroundEffect.Phi(hG, MathF.Sqrt(MathF.Max(ar, 0.1f) * strip));
            float boost = GroundEffect.LiftBoost(phiG, geStrength);
            liftOwn *= boost; liftDefl *= boost;
        }
        float lift = liftOwn + liftDefl;
        float clStrip = lift / (q * strip);
        float cdi = ar > 0.1f ? clStrip * clStrip / (MathF.PI * OswaldE * ar) : 0f;
        if (phiG < 1f) cdi *= MathF.Pow(phiG, geStrength);
        float sd = MathF.Sin(dAlpha);
        float drag = q * (Cd0 * Area + cdi * strip + 0.9f * MathF.Pow(E, 1.38f) * sd * sd * strip);

        Vector3 vHat = conditions.FlowDirection;
        Vector3 liftDir = n0 - Vector3.Dot(n0, vHat) * vHat;
        float liftDirLen = liftDir.Length();
        liftDir = liftDirLen > 1e-6f ? liftDir / liftDirLen : n0;
        Vector3 force = liftDir * lift - vHat * drag;
        // (the deflection's lift at the strip's centre - half a host chord ahead of this block's centre)
        Vector3 moment = ch > 0f ? Vector3.Cross(-chord * (0.5f * ch), liftDir * liftDefl) : Vector3.Zero;

        CurrentLift = lift;
        CurrentDrag = drag;
        EffectiveAoA = alphaE * 180f / MathF.PI;
        EffectiveCp = MathF.Abs(clStrip);
        return new ComponentForceResult(force, Position, moment);
    }

    /// <summary>The stall angle (rad) for an aspect ratio: 15 deg at 4 and up, rising to 30 deg at 1 and below (low aspect
    /// plates keep their lift far longer - tip vortices).</summary>
    public static float StallAngle(float ar) => (15f + 15f * Math.Clamp((4f - ar) / 3f, 0f, 1f)) * MathF.PI / 180f;

    /// <summary>CL at an angle of attack: linear to the stall angle, then the wing models' Kirchhoff decay blending to a flat
    /// plate (floored at 15 % - a control surface keeps some bite in deep stall).</summary>
    public static float Kirchhoff(float alpha, float clAlpha, float alphaStall)
    {
        float absAlpha = MathF.Abs(alpha);
        if (absAlpha <= alphaStall) return clAlpha * alpha;
        float stallExcess = (absAlpha - alphaStall) / (alphaStall + 0.01f);
        float sqrtF0 = MathF.Sqrt(MathF.Exp(-2f * stallExcess));
        float kirchhoffFactor = MathF.Max(0.15f, 0.25f * (1f + sqrtF0) * (1f + sqrtF0));
        float clAttached = clAlpha * alphaStall * MathF.Sign(alpha);
        float blendToFlat = MathF.Min(1f, stallExcess * 0.5f);
        return clAttached * kirchhoffFactor * (1f - blendToFlat) + MathF.Sin(2f * alpha) * blendToFlat;
    }

    static float WrapPi(float a)
    {
        while (a > MathF.PI) a -= 2f * MathF.PI;
        while (a < -MathF.PI) a += 2f * MathF.PI;
        return a;
    }

    /// <summary>Rodrigues' rotation formula: rotate v about axis k by angle theta.</summary>
    private static Vector3 Rodrigues(Vector3 v, Vector3 k, float theta)
    {
        float cos = MathF.Cos(theta);
        float sin = MathF.Sin(theta);
        return v * cos + Vector3.Cross(k, v) * sin + k * Vector3.Dot(k, v) * (1f - cos);
    }
}
