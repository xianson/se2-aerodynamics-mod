#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// Mach-dependent wing lift model that computes CLAlpha at runtime based on regime:
///   - Subsonic: Compressible Helmbold (Diederich form) using M_perp
///   - Supersonic: Ackeret 2D + finite-wing tip correction
///   - Transonic: Hermite smoothstep blend avoiding the M=1 singularity
///
/// Applies compressibility to the lift slope rather than post-hoc correcting CL (the incompressible
/// ThinAirfoilWingModel it grew from was removed 2026-10-15: nothing created it).
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
    private float[] _leads = Array.Empty<float>(), _sweepLE = Array.Empty<float>();

    /// <summary>THE LEADING EDGE BY THE AIR (2026-10-18, fix a-wing-edge): a wing's chord axis is a positive grid axis -
    /// the detector cannot know which way the ship flies, and the low end always led: a ship flying toward +ChordAxis got
    /// its lift at 3/4 chord from its real leading edge, its Mach tuck FORWARD (destabilising) and its sweep from the
    /// trailing edge. Now per wing per tick the lead is the chordwise component of the wing's motion through the air
    /// (+1 toward +ChordAxis: the high end leads; -1: the low end): the aerodynamic centre a quarter chord from THAT edge,
    /// that edge's sweep for M_perp and the lift slope, the camber read from that end, the AoA and Mach CoP shifts
    /// downstream. Near-spanwise flow (pure sideslip, a flat fall, a hover) does not flip it hard: within LeadBlend of the
    /// wing's speed the lead goes linearly through 0 - the force point slides through the mid chord, no jitter, no state
    /// (a flat plate's centre of pressure IS its mid chord broadside on).</summary>
    public static float LeadBlend = 0.1f;

    /// <summary>The lead (-1..1) of wing w moving at v (local m/s) through the air: see LeadBlend.</summary>
    internal static float LeadOf(in LiftingSurface w, Vector3 v)
    {
        float along = Vector3.Dot(v, w.ChordAxis);
        float band = LeadBlend * v.Length();
        if (band <= 1e-6f) return along > 0f ? 1f : along < 0f ? -1f : 0f;
        return Math.Clamp(along / band, -1f, 1f);
    }
    private float[] _arSys = Array.Empty<float>(); private int[] _sys = Array.Empty<int>(); private double _sysSig = double.NaN;

    /// <summary>WING SYSTEMS (2026-10-10, AeroBench polar): the detector hands over a wing in pieces - a Tu-134's main wing
    /// came as its left half and its right half in two stacked layers, each flown as a stub of aspect ratio 2.3-4.8 (lift
    /// slope halved, induced drag doubled, and the layers charged as a biplane against each other). Pieces lying in one
    /// plane (parallel, within SystemGap of each other across it), overlapping fore and aft, and touching or overlapping
    /// along the span (across a fuselage: a gap up to SystemSpanGap of their spans) are one wing: each flies with the
    /// system's tip-to-tip aspect ratio (span^2 / its area), and no downwash between them. False: each piece alone.</summary>
    public static float TestAlpha0;   // (rad, negative: a cambered wing's zero-lift angle - AeroBench only)

    /// <summary>GEOMETRIC CAMBER AND INCIDENCE (2026-10-10): read from the wing's own cells. Per span row: the mean line
    /// (halfway between its top and bottom cells at each chord station), the chord line (the mean line's leading to
    /// trailing end); camber = the mean line's furthest bulge from the chord line toward +Normal over the chord; incidence
    /// = the chord line's tilt across the wing's plane. Thin-airfoil theory: camber m lifts as if the wing met the air
    /// 2m (rad) steeper; incidence adds itself, its sign by which end leads. A flat-bottomed wing with its top stepped
    /// up lifts at zero angle, as a real one does; a slab does not. (Without: every ship flew its whole hull nose-up 8-10
    /// deg to make its lift - an airliner's fuselage dragging across the flow.) Caps: camber 0.08, incidence 10 deg.</summary>
    public static bool GeometricCamber = true;   // (default since 2026-10-11: AeroBench jetliner L/D at 8 deg 4.7 -> 8.4, lift kept; the in-game flight A/B)
    private float[] _camber = Array.Empty<float>(), _slope = Array.Empty<float>();

    /// <summary>A wing's zero-lift angle (rad, about its plane, lift toward +Normal positive) with its leading edge at the
    /// low end of ChordAxis (lo) and at the high end (hi): per span row, a parabola z = a + b xi + c xi^2 fitted by least
    /// squares to the mean line (heights over the chord, xi 0 at the leading edge) - the fit rides over the cells' steps;
    /// thin-airfoil theory for it, alpha_L0 = b + 1.5 c (camber and incidence both). Area-weighted over the rows.</summary>
    internal static (float lo, float hi) CamberOf(in LiftingSurface w)
    {
        var rows = new Dictionary<int, Dictionary<int, (float lo, float hi)>>();
        foreach (var c in w.Cells)
        {
            int sp = (int)MathF.Round(c.X * w.SpanAxis.X + c.Y * w.SpanAxis.Y + c.Z * w.SpanAxis.Z);
            int x = (int)MathF.Round(c.X * w.ChordAxis.X + c.Y * w.ChordAxis.Y + c.Z * w.ChordAxis.Z);
            float h = c.X * w.Normal.X + c.Y * w.Normal.Y + c.Z * w.Normal.Z;
            if (!rows.TryGetValue(sp, out var row)) rows[sp] = row = new Dictionary<int, (float, float)>();
            row[x] = row.TryGetValue(x, out var e) ? (MathF.Min(e.lo, h), MathF.Max(e.hi, h)) : (h, h);
        }
        double sLo = 0, sHi = 0, wSum = 0;
        foreach (var row in rows.Values)
        {
            if (row.Count < 4) continue;
            int x0 = int.MaxValue, x1 = int.MinValue;
            foreach (var x in row.Keys) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); }
            double chord = x1 - x0 + 1;
            // least squares for z(xi) = a + b xi + c xi^2
            double s0 = 0, s1 = 0, s2 = 0, s3 = 0, s4 = 0, t0 = 0, t1 = 0, t2 = 0;
            foreach (var (x, e) in row)
            {
                double xi = (x - x0 + 0.5) / chord, z = (e.lo + e.hi) * 0.5 / chord;
                double xi2 = xi * xi;
                s0 += 1; s1 += xi; s2 += xi2; s3 += xi2 * xi; s4 += xi2 * xi2; t0 += z; t1 += z * xi; t2 += z * xi2;
            }
            // solve [[s0 s1 s2][s1 s2 s3][s2 s3 s4]] [a b c] = [t0 t1 t2]
            double det = s0 * (s2 * s4 - s3 * s3) - s1 * (s1 * s4 - s3 * s2) + s2 * (s1 * s3 - s2 * s2);
            if (Math.Abs(det) < 1e-12) continue;
            double db = s0 * (t1 * s4 - s3 * t2) - t0 * (s1 * s4 - s3 * s2) + s2 * (s1 * t2 - t1 * s2);
            double dc = s0 * (s2 * t2 - t1 * s3) - s1 * (s1 * t2 - t1 * s2) + t0 * (s1 * s3 - s2 * s2);
            double b = db / det, cq = dc / det;
            sLo += (b + 1.5 * cq) * row.Count; sHi += (-b - 0.5 * cq) * row.Count; wSum += row.Count;
        }
        if (wSum <= 0) return (0f, 0f);
        return (Math.Clamp((float)(sLo / wSum), -0.2f, 0.2f), Math.Clamp((float)(sHi / wSum), -0.2f, 0.2f));
    }
    public static bool WingSystems = true;   // (default since 2026-10-11: AeroBench jetliner L/D at 8 deg 4.7 -> 8.4, lift kept; the in-game flight A/B)
    public static float SystemGap = 1.0f, SystemSpanGap = 0.35f;

    private void UpdateSystems(ReadOnlySpan<LiftingSurface> wings)
    {
        int n = wings.Length;
        double sig = n;
        for (int i = 0; i < n; i++) sig += (wings[i].Centroid.X * 1.3 + wings[i].Centroid.Y * 1.7 + wings[i].Centroid.Z * 2.9 + wings[i].PlanformArea) * (i + 1);
        if (_arSys.Length >= n && sig == _sysSig) return;
        _sysSig = sig; _influenceValid = false;   // (the downwash between pieces depends on which are one wing)
        if (_arSys.Length < n) { _arSys = new float[n]; _sys = new int[n]; }
        for (int i = 0; i < n; i++) _sys[i] = i;
        int Find(int i) { while (_sys[i] != i) i = _sys[i] = _sys[_sys[i]]; return i; }
        if (WingSystems)
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    ref readonly var a = ref wings[i]; ref readonly var b = ref wings[j];
                    if (MathF.Abs(Vector3.Dot(a.Normal, b.Normal)) < 0.95f) continue;
                    var d = b.Centroid - a.Centroid;
                    if (MathF.Abs(Vector3.Dot(d, a.Normal)) > SystemGap) continue;
                    if (MathF.Abs(Vector3.Dot(a.SpanAxis, b.SpanAxis)) < 0.9f)
                    {
                        // (a sliver of the same wing detected the other way round - a 3 m2 piece of a Tu-134's right wing
                        //  with its span along the chord: one in the other's planform is the same wing)
                        ref readonly var big = ref a.PlanformArea >= b.PlanformArea ? ref a : ref b;
                        ref readonly var small = ref a.PlanformArea >= b.PlanformArea ? ref b : ref a;
                        var ds = small.Centroid - big.Centroid;
                        if (MathF.Abs(Vector3.Dot(ds, big.SpanAxis)) > big.Span * 0.5f || MathF.Abs(Vector3.Dot(ds, big.ChordAxis)) > big.MeanChord * 0.75f) continue;
                        int r0 = Find(i), r1 = Find(j); if (r0 != r1) _sys[r1] = r0;
                        continue;
                    }
                    float chordSep = MathF.Abs(Vector3.Dot(d, a.ChordAxis));
                    float chordOverlap = (a.MeanChord + b.MeanChord) * 0.5f - chordSep;
                    if (chordOverlap < 0.3f * MathF.Min(a.MeanChord, b.MeanChord)) continue;
                    float spanGap = MathF.Abs(Vector3.Dot(d, a.SpanAxis)) - (a.Span + b.Span) * 0.5f;
                    if (spanGap > SystemSpanGap * (a.Span + b.Span)) continue;
                    int ri = Find(i), rj = Find(j); if (ri != rj) _sys[rj] = ri;
                }
        for (int i = 0; i < n; i++) _sys[i] = Find(i);
        if (_camber.Length < n) { _camber = new float[n]; _slope = new float[n]; }
        for (int i = 0; i < n; i++) (_camber[i], _slope[i]) = GeometricCamber ? CamberOf(wings[i]) : (0f, 0f);
        for (int r = 0; r < n; r++)
        {
            if (_sys[r] != r) continue;
            var ax = wings[r].SpanAxis; float lo = float.MaxValue, hi = float.MinValue, area = 0f;
            for (int i = 0; i < n; i++)
            {
                if (_sys[i] != r) continue;
                float c = Vector3.Dot(wings[i].Centroid, ax), h = wings[i].Span * 0.5f;
                lo = MathF.Min(lo, c - h); hi = MathF.Max(hi, c + h); area += wings[i].PlanformArea;
            }
            float arS = area > 1e-3f ? (hi - lo) * (hi - lo) / area : 0f;
            for (int i = 0; i < n; i++) if (_sys[i] == r) _arSys[i] = MathF.Max(wings[i].AspectRatio, arS);
        }
    }

    /// <summary>The aspect ratio wing i flies with: its system's (UpdateSystems).</summary>
    internal float SystemAR(int i) => i < _arSys.Length ? _arSys[i] : 0f;
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
        _leads = new float[n];
        _sweepLE = new float[n];
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

    /// <summary>WING WAVE DRAG (2026-10-18, fix a-wing-wave, the user: wings had no wave drag of their own - only the hull's
    /// one multiplier by streamlining - so a thick straight slab crossed Mach 1 as cheaply as a thin swept blade). Per wing
    /// per tick, from its thickness ratio, this tick's leading-edge sweep L (the air's: LeadBlend) and its CL:
    ///   drag divergence (Korn):  M_dd cos L = KornKappa - (t/c) / cos L - |CL| / (10 cos^2 L)
    ///   transonic rise (Lock):   dCd = 20 (M - M_crit)^4,  M_crit = M_dd - (0.1/80)^(1/3)   (slope 0.1 at M_dd)
    ///   supersonic (Ackeret, simple sweep theory on the normal section): Cd = WaveK (t/c)^2 cos L / sqrt(Mn^2 - 1),
    ///                            Mn = M cos L - the normal section's 2D wave drag (K (t/c_n)^2 / beta_n) x cos^3 L
    /// The transonic rise is capped at the Ackeret value at beta = WaveBetaCap (transonic similarity: the singularity at
    /// Mn = 1 rounded to the peak ~ tau^(5/3)), and handed over to Ackeret from Mn 1 to sqrt(1 + beta_cap^2) (a smoothstep -
    /// both sides equal the cap there, so no jump). A SUBSONIC leading edge at M > 1 (Mn < 1) gets no Ackeret term - its
    /// normal flow is subsonic (simple sweep theory) - only Lock's rise in M against Korn's swept M_dd, which stays small
    /// while Mn < Mn_dd; the volume's wave drag at M > 1 is the hull's (its streamlining law). WaveK 16/3: a biconvex section (the double wedge,
    /// the minimum for its thickness, is 4; a flat-topped hexagon with ramps over a fraction f of the chord is 2/f).
    /// t/c: the wing's OWN (its columns' depth between its skins over its rows' chord - LiftingSurface.OwnThickness;
    /// WaveThickness), not the detector's ThicknessRatio (a stepped block wing's comes out 0.3-0.5, its pieces' over the
    /// whole grid's cells across them - the F-6's 0.28-0.42 where its own are 0.12-0.19).
    /// THE DOUBLE-COUNT GUARD: a wing's leading-edge, trailing-edge and tip faces are HULL faces (only its skins are left
    /// out - LiftingSurfaceModel.ExcludeWingFaces), so the hull's speed-of-sound law already charges part of its thickness
    /// drag there: what it adds over its subsonic level on them - P x (wave x (r CdBluff (1 - t) + CpMax t) - r CdBluff),
    /// r = EdgeBluffKept the share of the bluff charge its pressure recovery leaves subsonic (0.37 measured on a stepped
    /// bench wing, 1 on a square edge - with 0.4 the estimate is within 5 % of the measured rise in the sleek band, and
    /// above it on square edges: it errs toward adding less), P = their Newtonian frontal area (sum of area x cos^3 over
    /// the windward edge faces, measured at detection from the real surface: LiftingSurface.EdgeLo / EdgeHi), t and wave the
    /// hull's own blend and wave multiplier this tick (ForceTable.NewtonBlend / HullWave, handed over by SetHullState) -
    /// comes off the theory value; the wing adds only what is missing, never less than 0. Measured (AeroBench
    /// wingwaveprobe, Lift/README.md): a square edge's rise is 1-3x the theory value through the transonic band and 2-25x
    /// supersonic (nothing added, a little where the hull's blend lags: 0.009 on a t/c 0.17 slab at Mach 0.9); a stepped
    /// edge (smoothed to a ramp, a sleek hull holding its recovery) is charged almost nothing until Mach 1.6 (the theory
    /// added), less from there, none by Mach 3. False: none (the A/B).</summary>
    public static bool WingWaveDrag = true;
    /// <summary>THE BLOCK WINGS' WAVE-DRAG THICKNESS (2026-10-18, the user: '1 and 2' - soften the pinch, scale the block
    /// wings' wave drag): the wave drag takes a wing's t/c x this - its OWN t/c (WaveThickness) read as that of the real wing
    /// a builder means. Block wings are 12-19 % thick (a cell's depth over a few cells of chord: the F-6's 0.12-0.19) where a
    /// real fighter's are 4-6 %; charged at their own t/c they hit the full transonic wall of a thick 1940s section (the F-6's
    /// drag at Mach 1 1.92x its subsonic, T/W 3.4 to break Mach 1 at sea level). x 0.4: the F-6's wings read as 0.05-0.075,
    /// a real fighter's to a thick-ish modern one. Why a scale on the t/c, not on the drag or on WaveK: the whole theory stays
    /// one consistent (thinner) wing - Korn's onset, Lock's rise, the transonic cap and Ackeret all from the same t/c - and the
    /// ordering is kept exactly: monotonic in t/c, and the supersonic (Ackeret, ~ (t/c)^2) ratio between two wings unchanged
    /// (thin swept vs thick straight at Mach 2: 7.3x before and after - AeroBench wingwave). A thick straight block wing still
    /// diverges first (Korn: M_dd falls with t/c) and pays most; a drag scale would have kept its onset at Mach ~0.72 and
    /// scaled a thin wing's (already realistic) drag by the same factor; a smaller WaveK leaves Lock's rise and the onset
    /// untouched. 1: the real wing of that t/c (the A/B).</summary>
    public static float BlockWingThicknessScale = 0.4f;
    public static float KornKappa = 0.87f, WaveK = 16f / 3f, WaveGammaPlus1 = 2.4f, EdgeBluffKept = 0.4f;
    /// <summary>Where linear theory stops near Mn = 1 (the cap's beta): transonic similarity - Ackeret holds while
    /// beta^2 >> ((gamma + 1) tau)^(2/3), tau the normal section's t/c (tc / cos L) - so beta_cap = ((gamma + 1) tau)^(1/3)
    /// and the peak K tau^2 / beta_cap ~ tau^(5/3), the transonic similarity law (NACA 0012: ~0.12 against ~0.1 measured at
    /// Mach 1). Floored at 0.15.</summary>
    internal static float WaveBetaCap(float tc, float cosSweep) => MathF.Max(0.15f, MathF.Cbrt(WaveGammaPlus1 * MathF.Max(0f, tc) / MathF.Max(0.1f, cosSweep)));
    /// <summary>(0.1/80)^(1/3): M_dd - M_crit in Lock's law (dCd/dM = 0.1 at M_dd).</summary>
    const float LockGap = 0.10772f;

    /// <summary>SUPERSONIC DRAG DUE TO LIFT (2026-10-18, fix a-wing-wave): CDi was CL^2 / (pi e AR) at every Mach - full
    /// leading-edge suction - so supersonic lift came ~3x too cheap. Now CDi = CL^2 [k_sub + (1/CLa - k_sub) s], k_sub =
    /// 1/(pi e AR) (x the ground factor, as before), 1/CLa the drag of lift with NO suction (the force normal to the
    /// plate: CL x alpha; with the supersonic slope 4/beta that is CL^2 sqrt(M^2 - 1)/4 for a supersonic leading edge -
    /// this wing's own runtime CLa, its leading-edge Mach, as the lift), s the suction lost: a smoothstep in M_perp over
    /// the lift slope's own blend band (SubsonicBlendLimit .. SupersonicBlendLimit - the same as ComputeCLAlpha), and for a
    /// SUBSONIC leading edge in supersonic flight at least SubsonicEdgeSuctionLoss of it (smoothstep in the free-stream
    /// Mach over the same band): linear theory keeps some suction on a swept edge but a real one - and a blocky one -
    /// loses much of it. Past the stall the no-suction share fades as the separated flow's flat-plate drag (cdSep - the
    /// same thing, the normal force tilted back) takes over. Never below k_sub. False: CL^2 / (pi e AR) at all Mach.</summary>
    public static bool SupersonicLiftDrag = true;
    public static float SubsonicEdgeSuctionLoss = 0.5f;

    /// <summary>Korn's drag-divergence Mach (free stream) of a section of thickness ratio tc on an edge swept by sweep (rad)
    /// at lift coefficient cl: M_dd = (KornKappa - tc / cos L - |cl| / (10 cos^2 L)) / cos L; its normal component floored at
    /// 0.4 (a slab thicker than the relation knows), cos L at 0.1.</summary>
    internal static float KornMdd(float tc, float sweep, float cl)
    {
        float c = MathF.Max(0.1f, MathF.Cos(sweep));
        float mn = KornKappa - MathF.Max(0f, tc) / c - MathF.Abs(cl) / (10f * c * c);
        return MathF.Max(0.4f, mn) / c;
    }

    /// <summary>The thickness wave drag coefficient (on the planform, free-stream q) of a wing at Mach mach, leading-edge
    /// sweep sweep (rad), thickness ratio tc, lift coefficient cl - theory only (the hull's share comes off in
    /// ComputeWingForces): Lock's rise above Korn's M_dd, capped at the Ackeret peak, handed to Ackeret on the normal Mach.
    /// Finite everywhere (0 for NaN input).</summary>
    internal static float ThicknessWaveCd(float mach, float sweep, float tc, float cl)
    {
        if (!(mach > 0f) || !(tc > 0f) || !float.IsFinite(sweep + cl)) return 0f;
        float c = MathF.Max(0.1f, MathF.Cos(sweep));
        float mCrit = KornMdd(tc, sweep, cl) - LockGap;
        float x = mach - mCrit;
        float lockCd = x > 0f ? 20f * x * x * x * x : 0f;
        float k = WaveK * tc * tc * c;
        float betaCap = WaveBetaCap(tc, c);
        float peak = k / betaCap;
        float trans = MathF.Min(lockCd, peak);
        float mn = mach * c;
        if (mn <= 1f) return trans;
        float b2 = mn * mn - 1f, bCap = betaCap * betaCap;
        float sup = b2 > bCap ? k / MathF.Sqrt(b2) : peak;
        // (the hand-over: Mn 1 .. sqrt(1 + bCap) - both ends equal the cap there)
        float s = Math.Clamp(b2 / bCap, 0f, 1f);
        s = s * s * (3f - 2f * s);
        return trans + (sup - trans) * s;
    }

    /// <summary>The share of leading-edge suction lost (0..1) at this leading-edge Mach and free-stream Mach: see
    /// SupersonicLiftDrag.</summary>
    internal float SuctionLoss(float machPerp, float mach)
    {
        float lo = SubsonicBlendLimit, hi = SupersonicBlendLimit;
        if (!(hi > lo)) return machPerp >= hi ? 1f : 0f;
        float a = Math.Clamp((machPerp - lo) / (hi - lo), 0f, 1f), b = Math.Clamp((mach - lo) / (hi - lo), 0f, 1f);
        a = a * a * (3f - 2f * a); b = b * b * (3f - 2f * b);
        return MathF.Max(a, SubsonicEdgeSuctionLoss * b);
    }

    // -- the hull's state this tick (SetHullState: the double-count guard of WingWaveDrag) --
    float _hullCdBluff, _hullCpMax, _hullT, _hullWave = 1f;

    /// <summary>Hand over this tick's hull state for the wave-drag guard: the face model's CdBluff and CpMax, its
    /// speed-of-sound blend t and wave multiplier (ForceTable.NewtonBlend, HullWave) - LiftingSurfaceModel.HandHullEdges.
    /// Stays until the next call; never set (or CdBluff = CpMax = 0): no hull charge known - the full theory value.</summary>
    public void SetHullState(float cdBluff, float cpMax, float blend, float wave)
    {
        _hullCdBluff = cdBluff; _hullCpMax = cpMax; _hullT = blend; _hullWave = wave;
    }

    /// <summary>The thickness ratio a wing's wave drag takes, x BlockWingThicknessScale: its own (LiftingSurface.OwnThickness); a wing given by hand
    /// (no cells: the bench's) the detector's ThicknessRatio; a detected wing never measured (no live surface at its
    /// detection - a cached small grid's incremental update) 0 - no wave drag of its own rather than one from the detector's
    /// 0.3-0.5 with no guard (its edges are still the hull's).</summary>
    internal static float WaveThickness(in LiftingSurface w) =>
        BlockWingThicknessScale * (w.OwnThickness > 0f ? w.OwnThickness : w.Cells == null || w.Cells.Count == 0 ? w.ThicknessRatio : 0f);

    /// <summary>What the hull charges on a wing's edge faces over their subsonic level this tick (drag area, m2; x q a
    /// force), the edge leading by lead (blended as the force point, LeadBlend).</summary>
    internal float HullEdgeRise(in LiftingSurface w, float lead)
    {
        float pe = w.EdgeLo + (w.EdgeHi - w.EdgeLo) * (0.5f + 0.5f * lead);
        float kept = EdgeBluffKept * _hullCdBluff;
        float over = _hullWave * (kept * (1f - _hullT) + _hullCpMax * _hullT) - kept;
        return over > 0f && pe > 0f ? pe * over : 0f;
    }

    /// <summary>Ground effect: switch (harness aeroset CompressibleWingModel.GroundEffect 0) and the height the
    /// lift boost and drag cut fade out by (spans).</summary>
    public static bool GroundEffect = true;
    float[] _groundPhi = Array.Empty<float>();

    /// <summary>The wing's ground factor (McCormick): phi = (16 h/b)^2 / (1 + (16 h/b)^2), floored at 0.1 - h its own height
    /// above the ground (the grid's, plus its aerodynamic centre's offset along 'up' - ac: this tick's), b its span; 1 far
    /// from the ground or unknown. (Without the 16 it halved induced drag a whole span up; it now fades smoothly - ~1 %
    /// left at h = b.)</summary>
    static float GroundPhi(in AeroContext ctx, in LiftingSurface w, Vector3 ac)
    {
        if (!GroundEffect || ctx.GroundHeight < 0f || w.Span <= 0.1f) return 1f;
        float h = ctx.GroundHeight;
        if (ctx.GroundUpLocal.LengthSquared() > 0.5f) h += Vector3.Dot(ac, ctx.GroundUpLocal);
        h = MathF.Max(0.05f, h);
        float x = 16f * h / w.Span;
        if (x > 60f) return 1f;   // (phi within 0.03 % of 1)
        return MathF.Max(0.1f, x * x / (1f + x * x));   // (the landing double on top: GroundEffect, LiftingSurfaceModel)
    }

    public List<WingForceResult> ComputeWingForces(ReadOnlySpan<LiftingSurface> wings, in AeroContext ctx)
    {
        if (_groundPhi.Length < wings.Length) _groundPhi = new float[wings.Length];
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
        UpdateSystems(wings);

        float rho = (float)ctx.Atmosphere.Density;
        float speedOfSound = (float)ctx.Atmosphere.SpeedOfSound;

        // Step 1: Compute raw AoA and runtime CLAlpha for each wing
        for (int i = 0; i < n; i++)
        {
            ref readonly var w = ref wings[i];
            // (the leading edge from the air at the mid chord, then the air at that edge's quarter chord: LeadBlend)
            float lead = LeadOf(w, ctx.VelocityAtPoint(w.Centroid));
            _leads[i] = lead;
            _sweepLE[i] = w.SweepFor(lead);
            var v = ctx.VelocityAtPoint(w.AeroCenterFor(lead));
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
            _alphas[i] = MathF.Atan2(vDotN, vPlaneSpeed + 1e-6f) - TestAlpha0;   // (TestAlpha0: the bench's camber experiment)
            if (GeometricCamber && i < _camber.Length)
            {
                // (the leading end is the one moving ahead: the chord line's rise toward it is the incidence)
                // (moving toward +ChordAxis - lead +1: its leading edge is the high end; blended through near-spanwise
                //  flow as the force point - it flipped hard at along = 0 before, 2026-10-18)
                float hiShare = 0.5f + 0.5f * lead;
                _alphas[i] -= _camber[i] + (_slope[i] - _camber[i]) * hiShare;   // (_camber: alpha_L0 leading at the low end, _slope: at the high end)
            }

            // Runtime CLAlpha from Mach regime
            float mach = speedOfSound > 0 ? speed / speedOfSound : 0;
            float sweepLE = _sweepLE[i];   // (the leading edge's, by the air: LeadBlend)
            float machPerp = mach * MathF.Cos(sweepLE);
            _machPerps[i] = machPerp;
            // (ground effect: near the ground the wing sees less downwash - as if its aspect ratio were AR / phi -
            //  so its lift slope rises; phi from its own height over its span, capped at 4x)
            float phiG = GroundPhi(ctx, w, w.AeroCenterFor(lead));
            _groundPhi[i] = phiG;
            float arI = _arSys[i];
            _clAlphas[i] = ComputeCLAlpha(machPerp, MathF.Min(arI / phiG, 4f * arI), sweepLE,
                                          SubsonicBlendLimit, SupersonicBlendLimit);

            // Runtime stall angle, floored at 3 degrees (x the flight assist's stall angle scale: 1 realistic)
            _alphaStalls[i] = FlightAssists.Stall(_clAlphas[i] > 0.01f
                ? MathF.Max(3f * MathF.PI / 180f, w.CLMax / _clAlphas[i])
                : MathF.PI / 4f);
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
                    if (_sys[i] == _sys[j]) continue;   // (one wing: its own downwash is in its aspect ratio)
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
            float cdSep = 0f, attached = 1f;   // (attached: the share of the lift still from attached flow - SupersonicLiftDrag)
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
                float sepShare = MathF.Max(blendToFlat, 1f - kirchhoffFactor);
                cdSep = 2f * sinA * sinA * sepShare;
                attached = 1f - sepShare;
                cl = clAttached * kirchhoffFactor * (1f - blendToFlat) + clFlatPlate * blendToFlat;
                cl = FlightAssists.Retain(cl, clAlpha * alphaStall);   // (the flight assist's post-stall lift: 1 realistic)
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
            float kSub = _arSys[i] > 0.1f ? 1f / (MathF.PI * e * _arSys[i]) : 0;

            // (profile drag: two turbulent skins on the chord plus a little form - ~0.006 for an airliner's wing, as
            //  measured - with ReynoldsSkin; the flat 0.010 without)
            float cd0 = DampedShadowedDragModel.ReynoldsSkin ? 2.4f * DampedShadowedDragModel.TurbulentCf(w.MeanChord) : 0.010f;

            // ── Ground effect: reduce induced drag near the surface ──
            // φ = (h / b)² / (1 + (h / b)²), where h = ground height, b = span
            // CDi_eff = CDi * φ  (approaches 0 at h→0, 1 at h→∞)
            // Also boosts effective CL slightly (reduced downwash = more effective AoA)
            kSub *= _groundPhi[i];   // (this wing's own height: GroundPhi)
            float cdi = cl * cl * kSub;

            // (supersonic drag due to lift, 2026-10-18 fix a-wing-wave: the leading-edge suction lost toward CL^2/CLa -
            //  SupersonicLiftDrag; the ground factor stays on the suction part only - CLa already has it through AR/phi)
            float mach = speedOfSound > 0 ? speed / speedOfSound : 0f;
            if (SupersonicLiftDrag && clAlpha > 0.01f && attached > 0f)
            {
                float loss = SuctionLoss(_machPerps[i], mach);
                float extra = 1f / clAlpha - kSub;
                if (loss > 0f && extra > 0f) cdi += cl * cl * extra * loss * attached;
            }

            // (the wing's own wave drag: thickness and sweep, less what the hull already charges on its edge faces -
            //  WingWaveDrag, 2026-10-18 fix a-wing-wave)
            float cdw = 0f;
            if (WingWaveDrag && w.PlanformArea > 1e-3f)
            {
                float th = ThicknessWaveCd(mach, _sweepLE[i], WaveThickness(w), cl);
                if (th > 0f) cdw = MathF.Max(0f, th - HullEdgeRise(w, _leads[i]) / w.PlanformArea);
            }

            float totalCd = cdi + cd0 + cdSep + cdw;
            float dragMag = q * w.PlanformArea * totalCd;
            // (against the motion: vHat is the direction the wing moves - it pushed the wing forward before, ~95 kN
            // of free thrust on the Jetliner, hidden by a drag floor)
            var inducedDrag = -vHat * dragMag;

            // (downstream: toward the trailing edge of this tick's flow - LeadBlend; it was always +ChordAxis, forward
            //  for a ship flying toward +ChordAxis; scaled by the lead, so it fades to the mid chord in near-spanwise flow)
            float lead = _leads[i];
            Vector3 downstream = w.ChordAxis * -lead;

            // ── Center of pressure shift with AoA ──
            float absAlphaFrac = absAlpha / (MathF.PI * 0.5f);
            float cpBlend = absAlphaFrac * absAlphaFrac;
            Vector3 cpShiftAoA = downstream * (w.MeanChord * 0.25f * cpBlend);

            // ── Mach tuck: CoP shifts aft at transonic (uses machPerp for sweep) ──
            float machTuck = MachTuckFactor(_machPerps[i]);
            Vector3 cpShiftMach = downstream * (w.MeanChord * machTuck);

            Vector3 applicationPoint = w.AeroCenterFor(lead) + cpShiftAoA + cpShiftMach;

            _resultsList[i] = new WingForceResult(
                liftForce, inducedDrag, applicationPoint,
                cl, cdi, alpha, _efficiency[i], lead, cdw);
        }

        // Throttled wing diagnostics (~1/sec)
        if (AeroLog.Verbose && --_logCooldown <= 0 && n > 0)   // (a line per wing per second for every winged grid: verbose only)
        {
            _logCooldown = 60;
            float mach = speedOfSound > 0 ? comSpeed / speedOfSound : 0;
            for (int i = 0; i < n; i++)
            {
                ref readonly var w = ref wings[i];
                var wf = _resultsList[i];
                float sweepDeg = _sweepLE[i] * 180f / MathF.PI;   // (the leading edge's, by the air)
                float alphaDeg = _alphas[i] * 180f / MathF.PI;
                float tuck = MachTuckFactor(_machPerps[i]);
                Log.Default?.Info(
                    $"[AERO-WING] W{i} M={mach:F2} Mperp={_machPerps[i]:F2} sweep={sweepDeg:F1}° lead={_leads[i]:F2} " +
                    $"AoA={alphaDeg:F1}° CL={wf.CL:F3} CLa={_clAlphas[i]:F3} " +
                    $"L={wf.LiftForce.Length():F0}N Di={wf.InducedDrag.Length():F0}N CDi={wf.CDi:F4} CDw={wf.CDw:F4} t/c={w.ThicknessRatio:F2} " +
                    $"AR={w.AspectRatio:F1} S={w.PlanformArea:F1}m² span={w.Span:F1}m " +
                    $"tuck={tuck:F3} chord={w.MeanChord:F2}m " +
                    $"n=({w.Normal.X:F3},{w.Normal.Y:F3},{w.Normal.Z:F3}) " +
                    $"cen=({w.Centroid.X:F1},{w.Centroid.Y:F1},{w.Centroid.Z:F1})");
            }
        }

        return _resultsList;
    }

    internal static float ComputePlanformOverlap(in LiftingSurface a, in LiftingSurface b)
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
