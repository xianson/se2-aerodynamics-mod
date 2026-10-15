#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace AeroMod;

/// <summary>A retractable gear's state as the air sees it (in game: AeroGearComponent).</summary>
public interface IGearState
{
    /// <summary>How far the leg is out: 0 stowed .. 1 down.</summary>
    float Extension { get; }
    /// <summary>The bay doors: 0 shut .. 1 open.</summary>
    float DoorsOpen { get; }
}

/// <summary>
/// A retractable gear's drag: the wheel (frontal area diameter x width, Cd 1.0) and the strut (leg length x a fifth of a
/// diameter, Cd 1.2) out in the air as far as the leg is, plus the open bay (half a wheel's area, Cd 0.5) while the doors
/// are open - against the airflow, at the block. Nothing stowed with the doors shut (the block is hull, in the voxel
/// surface like any other).
/// </summary>
public sealed class GearDrag : IAeroBlockComponent
{
    public Vector3 Position { get; }
    public Vector3I BlockPosition { get; }
    public IGearState Gear { get; init; }
    public float WheelRadius { get; init; } = 1.25f;
    public float LegLength { get; init; } = 4.25f;
    public float LastDrag { get; private set; }

    public GearDrag(Vector3 position, Vector3I blockPosition) { Position = position; BlockPosition = blockPosition; }

    /// <summary>Drag area (m2 x Cd) at this extension and door opening.</summary>
    public float CdA(float extension, float doors)
    {
        float d = 2f * WheelRadius, w = 0.72f * d;   // (the vanilla wheels: width ~0.72 of the diameter)
        return extension * (1.0f * d * w + 1.2f * LegLength * 0.2f * d) + doors * 0.5f * 0.5f * d * w;
    }

    public ComponentForceResult Compute(in LocalAeroConditions conditions)
    {
        LastDrag = 0f;
        var g = Gear;
        if (g == null || conditions.Speed < 0.01f) return ComponentForceResult.Zero;
        float cda = CdA(System.Math.Clamp(g.Extension, 0f, 1f), System.Math.Clamp(g.DoorsOpen, 0f, 1f));
        if (cda <= 0f) return ComponentForceResult.Zero;
        LastDrag = conditions.DynamicPressure * cda;
        return new ComponentForceResult(-conditions.FlowDirection * LastDrag, Position);
    }
}
