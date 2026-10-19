using System.Numerics;
using Aero.Core.Grids;
using Aero.Core.Math;

namespace Aero.Core.Aero;

/// <summary>
/// Detected wing geometry — pure data, no physics.
/// Computed at build-time by an IWingDetector and cached until the grid changes.
/// </summary>
public readonly struct LiftingSurface
{
    // ─── Geometry (build-time) ─────────────────────────────────────

    /// <summary>Lift-generating normal (±X/±Y/±Z).</summary>
    public readonly Vector3 Normal;

    /// <summary>Span direction unit vector.</summary>
    public readonly Vector3 SpanAxis;

    /// <summary>Chord direction unit vector (toward trailing edge).</summary>
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

    /// <summary>Leading edge sweep angle in radians.</summary>
    public readonly float SweepAngle;

    /// <summary>Area-weighted centroid, grid-local meters.</summary>
    public readonly Vector3 Centroid;

    /// <summary>Quarter-chord point, grid-local meters.</summary>
    public readonly Vector3 AeroCenter;

    /// <summary>Number of exposed faces in this wing.</summary>
    public readonly int FaceCount;

    /// <summary>Grid cells belonging to this wing. Used for shadow wake decay.</summary>
    public readonly Vector3I[] Cells;

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
        float planformArea, float span, float thicknessRatio, float sweepAngle,
        Vector3 centroid, Vector3 aeroCenter, int faceCount,
        Vector3I[]? cells = null)
    {
        Normal = normal;
        SpanAxis = spanAxis;
        ChordAxis = chordAxis;
        PlanformArea = planformArea;
        Span = span;
        MeanChord = span > 0 ? planformArea / span : 0;
        AspectRatio = span > 0 ? span * span / planformArea : 0;
        ThicknessRatio = thicknessRatio;
        SweepAngle = sweepAngle;
        Centroid = centroid;
        AeroCenter = aeroCenter;
        FaceCount = faceCount;
        Cells = cells ?? Array.Empty<Vector3I>();

        // Precompute aero coefficients
        float ar = AspectRatio;

        // Helmbold: CL_alpha = 2π·AR / (2 + √(4 + AR²)) × cos(sweep)
        CLAlpha = 2f * MathF.PI * ar / (2f + MathF.Sqrt(4f + ar * ar))
                  * MathF.Cos(sweepAngle);

        // CL_max: penalize thick sections
        CLMax = 1.2f * (1f - 1.5f * MathF.Max(0f, thicknessRatio - 0.12f));
        CLMax = MathF.Max(CLMax, 0.2f); // floor

        // Alpha_stall = CL_max / CL_alpha
        AlphaStall = CLAlpha > 0.01f ? CLMax / CLAlpha : MathF.PI / 4f;

        // Oswald e = 1.78(1 - 0.045·AR^0.68) - 0.64, clamped [0.3, 0.95]
        OswaldE = 1.78f * (1f - 0.045f * MathF.Pow(ar, 0.68f)) - 0.64f;
        OswaldE = MathF.Max(0.5f, MathF.Min(0.95f, OswaldE));
    }

    public override string ToString() =>
        $"Wing(AR={AspectRatio:F1}, S={PlanformArea:F1}m², span={Span:F1}m, t/c={ThicknessRatio:F2}, " +
        $"sweep={SweepAngle * 180f / MathF.PI:F0}°, CLα={CLAlpha:F2}, faces={FaceCount})";

    /// <summary>
    /// Collect all cells from multiple wings into a single set.
    /// Pass the result to shadow maps via their WingCells property.
    /// </summary>
    public static HashSet<Vector3I> CollectWingCells(ReadOnlySpan<LiftingSurface> wings)
    {
        int total = 0;
        foreach (ref readonly var w in wings)
            total += w.Cells.Length;
        var set = new HashSet<Vector3I>(total);
        foreach (ref readonly var w in wings)
            foreach (var c in w.Cells)
                set.Add(c);
        return set;
    }
}

/// <summary>
/// Per-wing force output from the lift model.
/// </summary>
public readonly struct WingForceResult
{
    /// <summary>Lift force perpendicular to flow (Newtons).</summary>
    public readonly Vector3 LiftForce;

    /// <summary>Induced drag force along flow direction (Newtons).</summary>
    public readonly Vector3 InducedDrag;

    /// <summary>Where forces act (aero center, grid-local meters).</summary>
    public readonly Vector3 ApplicationPoint;

    /// <summary>Lift coefficient.</summary>
    public readonly float CL;

    /// <summary>Induced drag coefficient.</summary>
    public readonly float CDi;

    /// <summary>Effective angle of attack after interference (radians).</summary>
    public readonly float AlphaEffective;

    /// <summary>Interference reduction factor (1.0 = solo wing).</summary>
    public readonly float Efficiency;

    public WingForceResult(Vector3 liftForce, Vector3 inducedDrag, Vector3 applicationPoint,
        float cl, float cdi, float alphaEffective, float efficiency)
    {
        LiftForce = liftForce;
        InducedDrag = inducedDrag;
        ApplicationPoint = applicationPoint;
        CL = cl;
        CDi = cdi;
        AlphaEffective = alphaEffective;
        Efficiency = efficiency;
    }
}

/// <summary>
/// Pluggable wing detection — finds lifting surfaces from grid geometry.
/// Called at build-time; results cached until grid changes.
/// </summary>
public interface IWingDetector
{
    LiftingSurface[] Detect(Grid grid, ISurfaceProvider surface, float blockSize);
}

/// <summary>
/// Pluggable wing aero model — computes lift + induced drag + interference.
/// Called every frame with the detected wings and current flight conditions.
/// </summary>
public interface IWingLiftModel
{
    WingForceResult[] ComputeWingForces(ReadOnlySpan<LiftingSurface> wings, in AeroContext ctx);
}
