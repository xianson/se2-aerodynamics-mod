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

    // ── Derived (precomputed) ──

    private const float CLAlphaIncomp = 2f * MathF.PI; // thin airfoil 2D
    private const float AR = 4f;                        // estimate for single-block surface
    private const float AlphaStallDefault = 15f * MathF.PI / 180f; // 0.2618 rad
    private const float OswaldE = 0.85f;

    // ── IAeroBlockComponent ──

    public Vector3 Position { get; }
    public Vector3I BlockPosition { get; }

    // ── IFaceOverride ──

    private readonly List<Vector3I> _ownedCells;
    public IReadOnlyList<Vector3I> OwnedCells => _ownedCells;

    // ── Mutable input ──

    /// <summary>Deflection input from autopilot/player, -1..+1.</summary>
    public float DeflectionInput { get; set; }

    // ── Output state (readable after Compute) ──

    /// <summary>Current lift force magnitude (N).</summary>
    public float CurrentLift { get; private set; }

    /// <summary>Current drag force magnitude (N).</summary>
    public float CurrentDrag { get; private set; }

    /// <summary>Effective angle of attack (degrees).</summary>
    public float EffectiveAoA { get; private set; }

    /// <summary>Effective Cp for heatmap: |CL| (same as wings).</summary>
    public float EffectiveCp { get; private set; }

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

    public ComponentForceResult Compute(in LocalAeroConditions conditions)
    {
        CurrentLift = 0;
        CurrentDrag = 0;
        EffectiveAoA = 0;
        EffectiveCp = 0;

        if (conditions.Speed < 0.01f || conditions.Atmosphere.Density < 1e-8)
            return ComponentForceResult.Zero;

        // 1. Rotate ChordDirection about HingeAxis by DeflectionInput * MaxDeflection (Rodrigues)
        //    Negate so that positive DeflectionInput → trailing-edge-down → positive AoA → positive lift
        float deflRad = -DeflectionInput * MaxDeflection * MathF.PI / 180f;
        Vector3 deflectedChord = Rodrigues(ChordDirection, HingeAxis, deflRad);

        // 2. Surface normal = cross(hinge, deflectedChord), normalized
        Vector3 surfNormal = Vector3.Normalize(Vector3.Cross(HingeAxis, deflectedChord));

        // 3. AoA: angle between incoming air (-velocity) and chord plane
        Vector3 airflow = -conditions.Velocity;
        float alpha = MathF.Atan2(
            Vector3.Dot(airflow, surfNormal),
            Vector3.Dot(airflow, deflectedChord));

        float absAlpha = MathF.Abs(alpha);
        float signAlpha = alpha >= 0 ? 1f : -1f;

        // 4. Mach-corrected CLAlpha from CompressibleWingModel
        float machPerp = conditions.Mach * MathF.Cos(SweepAngle);
        float clAlpha = CompressibleWingModel.ComputeCLAlpha(
            machPerp, AR, SweepAngle, 0.85f, 1.15f);

        // Runtime stall angle (floored at 3°)
        float clMax = clAlpha * AlphaStallDefault; // approximate CLmax
        float alphaStall = clAlpha > 0.01f
            ? MathF.Max(3f * MathF.PI / 180f, clMax / clAlpha)
            : MathF.PI / 4f;

        // 5. CL via Kirchhoff stall model (verbatim from wing models)
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

        // 6. Forces
        float q = conditions.DynamicPressure;
        float liftMag = cl * q * Area;
        float cdi = AR > 0.1f ? cl * cl / (MathF.PI * OswaldE * AR) : 0;
        float dragMag = (Cd0 + cdi) * q * Area;

        // 7. Lift direction: component of surfNormal perpendicular to velocity
        Vector3 vHat = conditions.FlowDirection;
        Vector3 liftDir = surfNormal - Vector3.Dot(surfNormal, vHat) * vHat;
        float liftDirLen = liftDir.Length();
        if (liftDirLen > 1e-6f)
            liftDir /= liftDirLen;
        else
            liftDir = surfNormal;

        // 8. Total force = lift + drag
        Vector3 force = liftDir * liftMag - vHat * dragMag;

        // Update output state
        CurrentLift = liftMag;
        CurrentDrag = dragMag;
        EffectiveAoA = alpha * 180f / MathF.PI;
        EffectiveCp = MathF.Abs(cl);

        return new ComponentForceResult(force, Position);
    }

    /// <summary>Rodrigues' rotation formula: rotate v about axis k by angle theta.</summary>
    private static Vector3 Rodrigues(Vector3 v, Vector3 k, float theta)
    {
        float cos = MathF.Cos(theta);
        float sin = MathF.Sin(theta);
        return v * cos + Vector3.Cross(k, v) * sin + k * Vector3.Dot(k, v) * (1f - cos);
    }
}
