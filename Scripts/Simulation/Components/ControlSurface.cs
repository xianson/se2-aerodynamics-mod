#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// Deflectable lift/drag surface — aileron, elevator, rudder, canard.
/// Produces lift via thin airfoil theory (Mach-dependent CLAlpha from CompressibleWingModel) and, past its stall, a flat
/// plate's normal force (Section) with a floor on the deflection's authority (PostStallAuthorityFloor).
///
/// Physics:
///   1. Rotate chord about hinge by deflection (Rodrigues)
///   2. Compute AoA between deflected chord and local flow
///   3. CLAlpha from CompressibleWingModel (Mach-corrected)
///   4. CL: linear to the stall, then blended to the flat plate (CN = CD90 sin a / (0.56 + 0.44 sin a), CL = CN cos a,
///      CD = CN sin a)
///   5. Lift + induced drag + parasitic drag (+ the plate's pressure drag past the stall)
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
    /// <summary>Its last Compute flew attached (both the section's and the deflected section's angle under the stall, or no air).</summary>
    public bool LastAttached { get; private set; } = true;

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
    ///    lower it is; past stall (2026-10-19) a flat plate's normal force (Section), the deflection's change floored at
    ///    PostStallAuthorityFloor of its attached value;
    ///  - drag: Cd0 on its area, induced on the strip, and the deflection's own form drag (0.9 E^1.38 sin^2 delta) - past the
    ///    stall the plate's pressure drag CN sin a in their place;
    ///  - the deflection's force acts at the strip's centre (half a host chord ahead): an extra moment (past the stall its drag
    ///    change too).
    /// No host, no row, no plates: an all-moving plate - its own lift at alpha0 + delta, as before.
    /// </summary>
    public ComponentForceResult Compute(in LocalAeroConditions conditions)
    {
        CurrentLift = 0;
        CurrentDrag = 0;
        EffectiveAoA = 0;
        EffectiveCp = 0;
        LastAttached = true;

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
        float alphaStall = FlightAssists.Stall(StallAngle(ar));
        float q = conditions.DynamicPressure;
        // ground effect (GroundEffect: all lift, doubled coming down to land): its height, its strip's span (b = sqrt(AR S))
        float phiG = 1f, geStrength = 1f + conditions.Landing, boost = 1f;
        if (conditions.GroundHeight >= 0f)
        {
            float hG = conditions.GroundHeight + (conditions.GroundUp.LengthSquared() > 0.5f ? Vector3.Dot(Position, conditions.GroundUp) : 0f);
            phiG = GroundEffect.Phi(hG, MathF.Sqrt(MathF.Max(ar, 0.1f) * strip));
            boost = GroundEffect.LiftBoost(phiG, geStrength);
        }
        Vector3 vHat = conditions.FlowDirection;
        Vector3 liftDir = n0 - Vector3.Dot(n0, vHat) * vHat;
        float liftDirLen = liftDir.Length();
        liftDir = liftDirLen > 1e-6f ? liftDir / liftDirLen : n0;

        float lift, drag, liftDefl, dragDefl;
        LastAttached = MathF.Abs(alphaE) <= alphaStall && MathF.Abs(alpha0) <= alphaStall;
        if (LastAttached)
        {
            // 4a. ATTACHED (the section and the deflected section both under the stall): thin airfoil, as before 2026-10-19
            float liftOwn = q * clAlpha * alpha0 * Area * boost;
            liftDefl = q * clAlpha * tau * dAlpha * strip * boost;
            lift = liftOwn + liftDefl;
            float clStrip = lift / (q * strip);
            float cdi = ar > 0.1f ? clStrip * clStrip / (MathF.PI * OswaldE * ar) : 0f;
            if (phiG < 1f) cdi *= MathF.Pow(phiG, geStrength);
            float sd = MathF.Sin(dAlpha);
            drag = q * (Cd0 * Area + cdi * strip + 0.9f * MathF.Pow(E, 1.38f) * sd * sd * strip);
            dragDefl = 0f;   // (the deflection's moment: its lift only, as before)
        }
        else
        {
            // 4b. PAST THE STALL (2026-10-19, measured on dumped ships: Tests/AeroBench surfaces - the old Kirchhoff + sin 2a curve
            //     left a 30-50 deg dead zone, a plate's normal force ~0.03 at 90 deg against ~1.17 real, and the deflection's share
            //     scaled to nothing by the secant). The section as a flat plate: its own area at alpha0, the deflection the change
            //     from alpha0 to alphaE over the strip - both from Section (the attached lift held at the stall, blended to the
            //     plate's normal force, split CL = CN cos a, CD = CN sin a). The deflection's change is floored at
            //     PostStallAuthorityFloor of the attached one (x the assist's SurfaceAuthority), along its own direction.
            Section(alpha0, clAlpha, alphaStall, ar, out float cl0, out float cd0p, out float w0);
            Section(alphaE, clAlpha, alphaStall, ar, out float clE, out float cdEp, out float wE);
            float dCl = clE - cl0, dCd = cdEp - cd0p;
            float refD = clAlpha * tau * MathF.Abs(dAlpha), dMag = MathF.Sqrt(dCl * dCl + dCd * dCd);
            float floorD = PostStallAuthorityFloor * refD;
            if (dMag < floorD)
            {
                if (dMag > 1e-6f) { float k = floorD / dMag; dCl *= k; dCd *= k; }
                else { dCl = MathF.Sign(dAlpha) * floorD; dCd = 0f; }   // (no change of its own to keep: along the attached one's)
            }
            float wMax = MathF.Max(w0, wE), auth = 1f + (FlightAssists.SurfaceAuthority - 1f) * wMax;
            dCl *= auth; dCd *= auth;
            float liftOwn = q * cl0 * Area * boost;
            liftDefl = q * dCl * strip * boost;
            lift = liftOwn + liftDefl;
            // (induced drag on the attached share of the lift only - the plate's CN holds all its pressure drag; so does the
            //  deflection's own form drag)
            float clStrip = lift / (q * strip), att = 1f - wMax;
            float cdi = ar > 0.1f ? att * clStrip * clStrip / (MathF.PI * OswaldE * ar) : 0f;
            if (phiG < 1f) cdi *= MathF.Pow(phiG, geStrength);
            float sd = MathF.Sin(dAlpha);
            dragDefl = q * dCd * strip;
            drag = q * (Cd0 * Area + cdi * strip + att * 0.9f * MathF.Pow(E, 1.38f) * sd * sd * strip + cd0p * Area) + dragDefl;
        }

        // 5. forces: lift across the air, drag with it (vHat is the motion: the air's way is -vHat)
        Vector3 force = liftDir * lift - vHat * drag;
        // (the deflection's force at the strip's centre - half a host chord ahead of this block's centre)
        Vector3 moment = ch > 0f ? Vector3.Cross(-chord * (0.5f * ch), liftDir * liftDefl - vHat * dragDefl) : Vector3.Zero;

        CurrentLift = lift;
        CurrentDrag = drag;
        EffectiveAoA = alphaE * 180f / MathF.PI;
        EffectiveCp = MathF.Abs(lift / (q * strip));
        return new ComponentForceResult(force, Position, moment);
    }

    /// <summary>The stall angle (rad) for an aspect ratio: 15 deg at 4 and up, rising to 30 deg at 1 and below (low aspect
    /// plates keep their lift far longer - tip vortices).</summary>
    public static float StallAngle(float ar) => (15f + 15f * Math.Clamp((4f - ar) / 3f, 0f, 1f)) * MathF.PI / 180f;

    /// <summary>The share of its attached-flow deflection authority a control surface keeps past its stall, at least (the
    /// deflection's change of the section force against the attached one's, clAlpha tau delta). 0.35: in separated flow a
    /// trailing-edge flap still moves the wake's pressure - Hoerner and Borst, Fluid-Dynamic Lift (1985) ch. 5-6: a flap's
    /// lift increment past the section's maximum lift falls to roughly a third of its attached value; NASA high-angle-of-attack
    /// control data (Wentz, flat and cambered panels to 90 deg) - elevator and rudder effectiveness mostly 30-50 % of the
    /// low-angle value there. The low end of that: never more than the measurements give. A flat plate's own change usually
    /// exceeds it (an AR 1 plate's CN at 45-90 deg is 0.6-0.8 of its attached slope); it binds for high-aspect sections and in
    /// the stall break. ONE place: the model and the tests read it here.</summary>
    public const float PostStallAuthorityFloor = 0.35f;

    /// <summary>The blend from the attached lift (held at the stall) to the flat plate: over this past the stall angle at least
    /// (rad), or half the stall angle.</summary>
    public const float StallBand = 10f * MathF.PI / 180f;

    /// <summary>A flat plate's normal force coefficient at 90 deg (CD90) for an aspect ratio - Lindenburg (ECN-C--03-025, 2003),
    /// fitted to Hoerner's plates: 1.17 square (AR 1), 1.19 at 5, 1.28 at 10, 1.98 two-dimensional. A plate of AR 1/x is the
    /// plate of x turned.</summary>
    public static float Cd90(float ar)
    {
        float a = MathF.Max(MathF.Max(ar, 1f / MathF.Max(ar, 1e-3f)), 1f);
        return 1.98f - 0.81f * (1f - MathF.Exp(-20f / a));
    }

    /// <summary>A flat plate's normal force coefficient at angle a (rad; any sign, |a| to pi): CD90 sin a / (0.56 + 0.44 sin a)
    /// (Lindenburg's fit to Hoerner) - rising monotonically to CD90 at 90 deg.</summary>
    public static float PlateCn(float a, float ar)
    {
        float s = MathF.Sin(MathF.Abs(a));
        return MathF.Sign(a) * Cd90(ar) * s / (0.56f + 0.44f * s);
    }

    /// <summary>
    /// The section's lift and pressure-drag coefficients at angle a (rad, any sign, |a| up to pi), and the plate's share w (0
    /// attached .. 1 the flat plate). Under the stall: the thin airfoil (clAlpha a, no pressure drag - the attached drag is
    /// Compute's). Past it: the attached lift held at the stall, blended smoothly (smoothstep over StallBand, or half the stall
    /// angle) to the flat plate's normal force decomposed - CL = CN cos a, CD = |CN| sin|a| - so past the blend the force on
    /// the section only grows toward 90 deg (no dead zone, no reversal; a high-aspect section keeps its real stall break).
    /// FlightAssists.PostStallLift keeps more of the lift (never above the stall's).
    /// </summary>
    public static void Section(float a, float clAlpha, float alphaStall, float ar, out float cl, out float cdPlate, out float w)
    {
        float abs = MathF.Abs(a);
        if (abs <= alphaStall) { cl = clAlpha * a; cdPlate = 0f; w = 0f; return; }
        float band = MathF.Max(StallBand, 0.5f * alphaStall);
        float t = Math.Clamp((abs - alphaStall) / band, 0f, 1f);
        w = t * t * (3f - 2f * t);
        float cn = PlateCn(a, ar);
        float clStall = clAlpha * alphaStall;
        cl = (1f - w) * clStall * MathF.Sign(a) + w * cn * MathF.Cos(a);
        cl = FlightAssists.Retain(cl, clStall);
        cdPlate = w * MathF.Abs(cn) * MathF.Sin(abs);
    }

    /// <summary>The inputs a surface's torque is measured at (Measure): its ends, its base and half-way to each end.</summary>
    public const int Knots = 5;

    /// <summary>
    /// A surface's torque about com at Knots inputs - -1, half-way to its base b (trim / flap), b, half-way to +1, +1 - in the
    /// conditions given: the allocator's and the reach's measure (2026-10-19) - the torque at inputs it may really be given, not
    /// a slope about the trim. Past the stall the torque is far from linear in the input - the most may lie between the base
    /// and the end, or one side may push the other side's way - and the +-1/2 secant about the trim pushed the wrong way there
    /// (Tests/SurfaceStallTests). dT: each knot's torque less the base's; du: each knot's input less the base (ascending, du[2]
    /// = 0). Returns the base's torque. Its input is put back as it was. Three Computes (the base and the ends) while all three
    /// fly attached - the torque is then linear in the input and the half-way knots are interpolated - else five.
    /// </summary>
    public static Vector3 Measure(ControlSurface cs, in LocalAeroConditions c, Vector3 com, float b, Span<Vector3> dT, Span<float> du)
    {
        float keep = cs.DeflectionInput;
        b = Math.Clamp(b, -1f, 1f);
        cs.DeflectionInput = b; Vector3 tb = cs.Compute(c).TorqueAbout(com);
        bool attached = cs.LastAttached;
        float lo = -1f - b, hi = 1f - b;
        du[0] = lo; du[1] = 0.5f * lo; du[2] = 0f; du[3] = 0.5f * hi; du[4] = hi;
        dT[2] = Vector3.Zero;
        // (the ends first; trimmed to an end: no room that way)
        if (MathF.Abs(lo) < 1e-6f) dT[0] = Vector3.Zero; else { cs.DeflectionInput = -1f; dT[0] = cs.Compute(c).TorqueAbout(com) - tb; attached &= cs.LastAttached; }
        if (MathF.Abs(hi) < 1e-6f) dT[4] = Vector3.Zero; else { cs.DeflectionInput = 1f; dT[4] = cs.Compute(c).TorqueAbout(com) - tb; attached &= cs.LastAttached; }
        if (attached) { dT[1] = 0.5f * dT[0]; dT[3] = 0.5f * dT[4]; }
        else
        {
            if (MathF.Abs(du[1]) < 1e-6f) dT[1] = Vector3.Zero; else { cs.DeflectionInput = b + du[1]; dT[1] = cs.Compute(c).TorqueAbout(com) - tb; }
            if (MathF.Abs(du[3]) < 1e-6f) dT[3] = Vector3.Zero; else { cs.DeflectionInput = b + du[3]; dT[3] = cs.Compute(c).TorqueAbout(com) - tb; }
        }
        cs.DeflectionInput = keep;
        return tb;
    }

    /// <summary>A surface's torque change (relative to its base) at input change x off the base, piecewise linear through its
    /// measured knots (dT / du at offset o, Knots of them; beyond the ends: the end's).</summary>
    public static Vector3 AtKnots(ReadOnlySpan<Vector3> dT, ReadOnlySpan<float> du, int o, float x)
    {
        if (x <= du[o]) return dT[o];
        for (int k = o; k < o + Knots - 1; k++)
        {
            float a = du[k], b = du[k + 1];
            if (x <= b) return b - a > 1e-6f ? dT[k] + (dT[k + 1] - dT[k]) * ((x - a) / (b - a)) : dT[k + 1];
        }
        return dT[o + Knots - 1];
    }

    /// <summary>
    /// After Allocate (over each surface's full-throw slope): the inputs refined against the MEASURED torques - each surface's
    /// piecewise linear through its knots (Measure) - by coordinate passes: each surface in turn, the others held, the input on
    /// any segment leaving the least miss of the demand (the base itself a candidate). It starts from Allocate's inputs or from
    /// none, whichever misses less, and a step never adds miss: it never ends worse than no input at all, and at its end no one
    /// surface works against the demand (taking it back to its base would never leave less). u: the inputs relative to the base
    /// (in and out); dT / du: n x Knots, as Measure gives them. Allocation-free; n x passes x Knots small products.
    /// </summary>
    public static void Refine(ReadOnlySpan<Vector3> dT, ReadOnlySpan<float> du, Vector3 want, float[] u, int n, int passes = RefinePasses)
    {
        Vector3 got = Vector3.Zero;
        for (int i = 0; i < n; i++) { int o = i * Knots; u[i] = Math.Clamp(u[i], du[o], du[o + Knots - 1]); got += AtKnots(dT, du, o, u[i]); }
        if ((got - want).LengthSquared() > want.LengthSquared()) { for (int i = 0; i < n; i++) u[i] = 0f; got = Vector3.Zero; }
        for (int p = 0; p < passes; p++)
        {
            bool moved = false;
            float missBefore = (got - want).LengthSquared();
            for (int i = 0; i < n; i++)
            {
                int o = i * Knots;
                Vector3 mine = AtKnots(dT, du, o, u[i]);
                Vector3 rest = want - (got - mine);
                float best = 0f, bestMiss = rest.LengthSquared();
                Vector3 bestT = Vector3.Zero;
                for (int k = o; k < o + Knots - 1; k++)
                {
                    float len = du[k + 1] - du[k];
                    if (!(len > 1e-6f)) continue;
                    Vector3 P = dT[k], S = dT[k + 1] - dT[k];
                    float ss = S.LengthSquared();
                    float t = ss > 0f ? Math.Clamp(Vector3.Dot(rest - P, S) / ss, 0f, 1f) : 0f;
                    Vector3 at = P + S * t;
                    float m = (rest - at).LengthSquared();
                    if (m < bestMiss) { bestMiss = m; best = du[k] + len * t; bestT = at; }
                }
                if (MathF.Abs(best - u[i]) > 1e-5f) moved = true;
                u[i] = best;
                got = want - rest + bestT;
            }
            // (settled: nothing moved, or the pass took off under 0.01 % of the miss - a poorly conditioned set creeps for many)
            if (!moved || missBefore - (got - want).LengthSquared() <= 1e-4f * missBefore) break;
        }
    }
    /// <summary>Refine's pass cap (most settle in 2-3: it stops when no input moves or the miss stops falling).</summary>
    public const int RefinePasses = 32;

    /// <summary>
    /// The keyboard mixer's input for one surface (2026-10-19): the rate law (RateCommand) on each side of its base with that
    /// side's MEASURED torque direction - up: its torque at +1 less at the base, down: at -1 less at the base. The side whose
    /// command asks for it is taken (both: the stronger; neither: the base - 0). The old geometric r x (hinge x chord) with a
    /// fixed sign reversed past ~45 deg (a flat plate's deflection turns its force round) and steered against the pilot.
    /// Returns the input to add to the base (unclamped: the caller clamps).
    /// </summary>
    public static float MixerInput(Vector3 up, Vector3 down, Vector3 want, Vector3 now, float gainScale)
    {
        float lu = up.Length(), ld = down.Length();
        float cu = lu > 1e-6f ? RateCommand(up / lu, want, now, gainScale) : 0f;      // (+ asks for +input)
        float cd = ld > 1e-6f ? RateCommand(-down / ld, want, now, gainScale) : 0f;   // (- asks for -input: down's way is -1's)
        bool usePlus = cu > 0f, useMinus = cd < 0f;
        if (usePlus && useMinus) return cu >= -cd ? cu : cd;
        return usePlus ? cu : useMinus ? cd : 0f;
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
