#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// Detected wing geometry — pure data, no physics.
/// Computed at build-time by an IWingDetector and cached until the grid changes.
/// ORIENTATION-FREE (2026-10-18, fix a-wing-edge): the detector cannot know which way the ship flies - the chord axis is
/// a positive grid axis whichever edge leads. So a wing holds both edges' sweeps, its mid-chord centroid and how far its
/// quarter chord lies from it; the lift model picks the leading edge per tick by the air (CompressibleWingModel, lead:
/// +1 the wing moves toward +ChordAxis - its leading edge the high end, -1 toward -ChordAxis). Before, the low end always
/// led: a ship flying toward +ChordAxis had its lift at 3/4 chord, its Mach tuck forward and its sweep from the
/// trailing edge.
/// </summary>
public readonly struct LiftingSurface
{
    // ─── Geometry (build-time) ─────────────────────────────────────

    /// <summary>Lift-generating normal (±X/±Y/±Z).</summary>
    public readonly Vector3 Normal;

    /// <summary>Span direction unit vector.</summary>
    public readonly Vector3 SpanAxis;

    /// <summary>Chord direction unit vector: a positive grid axis, unsigned - which end leads is the flow's (see lead).</summary>
    public readonly Vector3 ChordAxis;

    /// <summary>Planform area in m².</summary>
    public readonly float PlanformArea;

    /// <summary>Span in meters.</summary>
    public readonly float Span;

    /// <summary>Mean chord = Area/Span in meters.</summary>
    public readonly float MeanChord;

    /// <summary>Aspect ratio = Span²/Area.</summary>
    public readonly float AspectRatio;

    /// <summary>Mean thickness-to-chord ratio.</summary>
    public readonly float ThicknessRatio;

    /// <summary>Sweep (rad, unsigned) of the edge at the low end of ChordAxis: the leading edge's when the wing moves
    /// toward -ChordAxis.</summary>
    public readonly float SweepLo;

    /// <summary>Sweep (rad, unsigned) of the edge at the high end of ChordAxis: the leading edge's when the wing moves
    /// toward +ChordAxis.</summary>
    public readonly float SweepHi;

    /// <summary>Orientation-free summary sweep (rad): the mean of the two edges' (~ the mid-chord line's) - for the
    /// precomputed coefficients below and the logs. The force path uses the leading edge's (SweepFor).</summary>
    public readonly float SweepAngle;

    /// <summary>Area-weighted centroid (the mid chord), grid-local meters.</summary>
    public readonly Vector3 Centroid;

    /// <summary>The quarter chord's distance (m) from the centroid along the chord axis, toward the leading edge: 0.25 x
    /// the mean chord (a merged wing: its pieces' area-weighted).</summary>
    public readonly float AcOffset;

    /// <summary>Number of exposed faces in this wing.</summary>
    public readonly int FaceCount;

    /// <summary>Grid cells belonging to this wing. Used for shadow wake decay.</summary>
    public readonly List<Vector3I> Cells;

    // ─── Precomputed aero coefficients ─────────────────────────────

    /// <summary>Lift curve slope (per radian) from Helmbold equation with sweep correction.</summary>
    public readonly float CLAlpha;

    /// <summary>Maximum lift coefficient before stall.</summary>
    public readonly float CLMax;

    /// <summary>Stall angle of attack in radians.</summary>
    public readonly float AlphaStall;

    /// <summary>Oswald span efficiency factor.</summary>
    public readonly float OswaldE;

    public LiftingSurface(
        Vector3 normal, Vector3 spanAxis, Vector3 chordAxis,
        float planformArea, float span, float thicknessRatio, float sweepLo, float sweepHi,
        Vector3 centroid, float acOffset, int faceCount,
        List<Vector3I>? cells = null)
    {
        Normal = normal;
        SpanAxis = spanAxis;
        ChordAxis = chordAxis;
        PlanformArea = planformArea;
        Span = span;
        MeanChord = span > 0 ? planformArea / span : 0;
        AspectRatio = span > 0 ? span * span / planformArea : 0;
        ThicknessRatio = thicknessRatio;
        SweepLo = sweepLo;
        SweepHi = sweepHi;
        float sweepAngle = SweepAngle = 0.5f * (sweepLo + sweepHi);
        Centroid = centroid;
        AcOffset = acOffset;
        FaceCount = faceCount;
        Cells = cells ?? new List<Vector3I>();

        // Precompute aero coefficients
        float ar = AspectRatio;

        // Helmbold: CL_alpha = 2π·AR / (2 + √(4 + AR²)) × cos(sweep)
        CLAlpha = 2f * MathF.PI * ar / (2f + MathF.Sqrt(4f + ar * ar))
                  * MathF.Cos(sweepAngle);

        // CL_max: penalize thick sections
        CLMax = 1.2f * (1f - 1.5f * MathF.Max(0f, thicknessRatio - 0.12f));
        CLMax = MathF.Max(CLMax, 0.2f);

        // Alpha_stall = CL_max / CL_alpha
        AlphaStall = CLAlpha > 0.01f ? CLMax / CLAlpha : MathF.PI / 4f;

        // Oswald e = 1.78(1 - 0.045·AR^0.68) - 0.64, clamped [0.3, 0.95]
        OswaldE = 1.78f * (1f - 0.045f * MathF.Pow(ar, 0.68f)) - 0.64f;
        OswaldE = MathF.Max(0.5f, MathF.Min(0.95f, OswaldE));
    }

    /// <summary>The quarter-chord point (grid-local m) for a flow with this lead (+1 the high end of ChordAxis leads,
    /// -1 the low end; between: blended through the mid chord - see CompressibleWingModel.LeadBlend).</summary>
    public Vector3 AeroCenterFor(float lead) => Centroid + ChordAxis * (lead * AcOffset);

    /// <summary>The leading edge's sweep (rad) for a flow with this lead (blended between the edges as AeroCenterFor).</summary>
    public float SweepFor(float lead) => SweepLo + (SweepHi - SweepLo) * (0.5f + 0.5f * lead);

    /// <summary>The chord direction toward the trailing edge for a flow with this lead (0: +ChordAxis, the old default).</summary>
    public Vector3 TrailingFor(float lead) => lead > 0f ? -ChordAxis : ChordAxis;

    public override string ToString() =>
        $"Wing(AR={AspectRatio:F1}, S={PlanformArea:F1}m², span={Span:F1}m, t/c={ThicknessRatio:F2}, " +
        $"sweep={SweepLo * 180f / MathF.PI:F0}/{SweepHi * 180f / MathF.PI:F0}°, CLα={CLAlpha:F2}, faces={FaceCount})";

}
