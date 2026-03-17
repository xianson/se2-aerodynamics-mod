#pragma warning disable
namespace AeroMod;

/// <summary>
/// LOD 3: Scalar drag. Single CdA value, force opposes velocity.
/// Absolute minimum computation: F = -½ρv²·CdA·v̂.
/// No lift, no torque, no directionality.
/// </summary>
public class ScalarDragModel : IAeroDragModel
{
    public string Name => "Scalar CdA (LOD3)";

    /// <summary>Effective drag area Cd × A (m²). Isotropic.</summary>
    public float CdA;

    /// <summary>
    /// Apply transonic wave drag multiplier. Costs one branch + exp but
    /// gives correct drag rise near Mach 1. Set false for max speed.
    /// </summary>
    public bool ApplyWaveDrag { get; set; } = false;

    public AeroResult Compute(in AeroContext ctx)
    {
        float speed = ctx.Velocity.Length();
        if (speed < 0.01f) return default;
        float rhoHalf = (float)(0.5 * ctx.Atmosphere.Density);
        if (rhoHalf < 1e-9f) return default;

        float drag = rhoHalf * speed * speed * CdA;

        if (ApplyWaveDrag && ctx.Atmosphere.SpeedOfSound > 0)
        {
            float mach = speed / (float)ctx.Atmosphere.SpeedOfSound;
            drag *= (float)CompressibleFlow.WaveDragMultiplier(mach);
        }

        Vector3 vHat = ctx.Velocity / speed;
        double mach2 = ctx.Atmosphere.SpeedOfSound > 0 ? speed / ctx.Atmosphere.SpeedOfSound : 0;

        return new AeroResult(-drag * vHat, Vector3.Zero, drag, 0,
            CdA, mach2, ctx.Atmosphere.GetDynamicPressure(speed));
    }

    /// <summary>Bake from a full model — average CdA across 6 axis directions.</summary>
    public static ScalarDragModel BakeFrom(IAeroDragModel fullModel, ISurfaceProvider surface,
        IGridAccessor grid, AtmosphereState atmo, Vector3 com, float blockSize, float speed)
    {
        var axis = AxisCdDragModel.BakeFrom(fullModel, surface, grid, atmo, com, blockSize, speed);
        return new ScalarDragModel
        {
            CdA = (axis.CdA_PosX + axis.CdA_NegX +
                   axis.CdA_PosY + axis.CdA_NegY +
                   axis.CdA_PosZ + axis.CdA_NegZ) / 6f,
        };
    }

    /// <summary>Quick bake: average frontal area × Cd.</summary>
    public static ScalarDragModel FromSurface(ISurfaceProvider surface, float cd = 1.1f)
    {
        float totalArea = 0;
        foreach (var grp in surface.NormalGroups)
            totalArea += grp.TotalArea;
        return new ScalarDragModel { CdA = cd * totalArea / 6f };
    }
}
