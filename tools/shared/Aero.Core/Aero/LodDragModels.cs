using System.Numerics;
using Aero.Core.Atmosphere;

namespace Aero.Core.Aero;

/// <summary>
/// LOD 2: Axis-decomposed drag. Precomputed CdA per axis direction.
/// No face iteration, no Cp computation, no trig — just 3 multiplies.
///
/// F_axis = -½ρ · CdA · v_component · |v_component|
///
/// Naturally produces lift at off-axis angles from the per-axis decomposition.
/// No torque (appropriate for distant/NPC ships).
/// </summary>
public class AxisCdDragModel : AeroDragModelBase
{
    public override string Name => "Axis CdA (LOD2)";
    public override string Description => "Baked per-axis drag, O(1)";

    /// <summary>Effective drag area (Cd × frontal area) for flow in each direction (m²).</summary>
    public float CdA_PosX, CdA_NegX;
    public float CdA_PosY, CdA_NegY;
    public float CdA_PosZ, CdA_NegZ;

    public override AeroResult Compute(in AeroContext ctx)
    {
        float speed = ctx.Velocity.Length();
        if (speed < 0.01f) return default;
        float rhoHalf = (float)(0.5 * ctx.Atmosphere.Density);
        if (rhoHalf < 1e-9f) return default;

        var v = ctx.Velocity;

        // Per-axis drag: F = -½ρ · CdA · v · |v| per component
        Vector3 F = new(
            -rhoHalf * (v.X > 0 ? CdA_PosX : CdA_NegX) * v.X * MathF.Abs(v.X),
            -rhoHalf * (v.Y > 0 ? CdA_PosY : CdA_NegY) * v.Y * MathF.Abs(v.Y),
            -rhoHalf * (v.Z > 0 ? CdA_PosZ : CdA_NegZ) * v.Z * MathF.Abs(v.Z));

        Vector3 vHat = ctx.Velocity / speed;
        float dragMag = -Vector3.Dot(F, vHat);
        Vector3 liftVec = F + dragMag * vHat;
        double mach = ctx.Atmosphere.SpeedOfSound > 0 ? speed / ctx.Atmosphere.SpeedOfSound : 0;

        return new AeroResult(F, Vector3.Zero, MathF.Abs(dragMag), liftVec.Length(),
            0, mach, ctx.Atmosphere.GetDynamicPressure(speed));
    }

    /// <summary>
    /// Bake CdA values by running a full model at a reference speed in each axis direction.
    /// </summary>
    public static AxisCdDragModel BakeFrom(AeroDragModelBase fullModel, ISurfaceProvider surface,
        Grids.Grid grid, AtmosphereState atmo, Vector3 com, float blockSize, float speed)
    {
        var model = new AxisCdDragModel();
        float q = (float)atmo.GetDynamicPressure(speed);
        if (q < 1e-6f) return model;

        Vector3[] dirs = { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY,
                           -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };

        for (int i = 0; i < 6; i++)
        {
            var ctx = new AeroContext(grid, surface, dirs[i] * speed, atmo, com, blockSize);
            float cda = fullModel.Compute(ctx).DragMagnitude / q;

            switch (i)
            {
                case 0: model.CdA_PosX = cda; break;
                case 1: model.CdA_NegX = cda; break;
                case 2: model.CdA_PosY = cda; break;
                case 3: model.CdA_NegY = cda; break;
                case 4: model.CdA_PosZ = cda; break;
                case 5: model.CdA_NegZ = cda; break;
            }
        }

        return model;
    }

    /// <summary>
    /// Quick bake from surface groups directly (no full model needed).
    /// Uses a flat Cd for all faces.
    /// </summary>
    public static AxisCdDragModel FromSurface(ISurfaceProvider surface, float cd = 1.1f)
    {
        var model = new AxisCdDragModel();
        foreach (var grp in surface.NormalGroups)
        {
            float cda = cd * grp.TotalArea;
            if (grp.Normal.X > 0.5f) model.CdA_PosX = cda;
            else if (grp.Normal.X < -0.5f) model.CdA_NegX = cda;
            else if (grp.Normal.Y > 0.5f) model.CdA_PosY = cda;
            else if (grp.Normal.Y < -0.5f) model.CdA_NegY = cda;
            else if (grp.Normal.Z > 0.5f) model.CdA_PosZ = cda;
            else if (grp.Normal.Z < -0.5f) model.CdA_NegZ = cda;
        }
        return model;
    }
}

/// <summary>
/// LOD 3: Scalar drag. Single CdA value, force opposes velocity.
/// Absolute minimum computation: F = -½ρv²·CdA·v̂.
/// No lift, no torque, no directionality.
/// </summary>
public class ScalarDragModel : AeroDragModelBase
{
    public override string Name => "Scalar CdA (LOD3)";
    public override string Description => "Single drag area, O(1)";

    /// <summary>Effective drag area Cd × A (m²). Isotropic.</summary>
    public float CdA;

    /// <summary>
    /// Apply transonic wave drag multiplier. Costs one branch + exp but
    /// gives correct drag rise near Mach 1. Set false for max speed.
    /// </summary>
    public bool ApplyWaveDrag { get; set; } = false;

    public override AeroResult Compute(in AeroContext ctx)
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
    public static ScalarDragModel BakeFrom(AeroDragModelBase fullModel, ISurfaceProvider surface,
        Grids.Grid grid, AtmosphereState atmo, Vector3 com, float blockSize, float speed)
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
        // Each face direction covers one side; average over 3 axis pairs
        return new ScalarDragModel { CdA = cd * totalArea / 6f };
    }
}
