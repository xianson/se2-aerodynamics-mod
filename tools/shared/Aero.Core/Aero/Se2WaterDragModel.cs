using System.Numerics;
using Aero.Core.Grids;
using Aero.Core.Math;

namespace Aero.Core.Aero;

/// <summary>
/// Faithful reimplementation of SE2's water drag model.
///
/// From GridHitReceiverComponent.ReceiveWaterHit + WaterBuoyancy.GetDrag:
///   - Iterates ALL blocks (no exposure check)
///   - 8 corners per block AABB
///   - "Normal" = normalize(corner - blockCenter)  ← always diagonal
///   - Drag = -v̂ × (v²/2) × (area × dot(v̂, n̂)) × power
///   - No fluid density (ρ) in the formula
///   - Submersion fraction scales area (we assume fully submerged = 1.0)
///   - Force applied at each corner position (produces torque)
///
/// This is here so we can compare it against our panel method and quantify
/// exactly how wrong it is.
/// </summary>
public class Se2WaterDragModel : AeroDragModelBase
{
    public override string Name => "SE2 Water Drag";
    public override string Description => "All blocks × 8 corners, diagonal normals, no density";

    /// <summary>The "Drag" multiplier from WaterHit. Tuning knob with no physical basis.</summary>
    public float Power { get; set; } = 1.0f;

    /// <summary>Submersion fraction [0,1]. 1.0 = fully submerged.</summary>
    public float SubmersionFraction { get; set; } = 1.0f;

    public override AeroResult Compute(in AeroContext ctx)
    {
        return Compute(ctx.Grid, ctx.Velocity, ctx.CenterOfMass, ctx.BlockSize);
    }

    public AeroResult Compute(Grid grid, Vector3 velocity, Vector3 centerOfMass, float blockSize = 2.5f)
    {
        float speed2 = velocity.LengthSquared();
        if (speed2 < 0.0001f)
            return default;

        float speed = MathF.Sqrt(speed2);
        Vector3 vHat = velocity / speed;

        float blockSurfaceArea = 6f * blockSize * blockSize;
        float areaPerCorner = (blockSurfaceArea / 8f) * SubmersionFraction;

        Vector3 totalForce = Vector3.Zero;
        Vector3 totalTorque = Vector3.Zero;

        foreach (var block in grid.Blocks.Values)
        {
            foreach (var cell in block.EnumerateCells())
            {
                float cx = (cell.X + 0.5f) * blockSize;
                float cy = (cell.Y + 0.5f) * blockSize;
                float cz = (cell.Z + 0.5f) * blockSize;
                var center = new Vector3(cx, cy, cz);

                for (int i = 0; i < 8; i++)
                {
                    float cornerX = (cell.X + (i & 1)) * blockSize;
                    float cornerY = (cell.Y + ((i >> 1) & 1)) * blockSize;
                    float cornerZ = (cell.Z + ((i >> 2) & 1)) * blockSize;
                    var corner = new Vector3(cornerX, cornerY, cornerZ);

                    var areaNormal = Vector3.Normalize(corner - center);

                    float dot = Vector3.Dot(vHat, areaNormal);
                    if (dot > 0)
                    {
                        float projArea = areaPerCorner * dot;
                        Vector3 F = -vHat * (speed2 / 2f) * projArea * Power;

                        totalForce += F;
                        totalTorque += Vector3.Cross(corner - centerOfMass, F);
                    }
                }
            }
        }

        float forceDotV = Vector3.Dot(totalForce, vHat);
        Vector3 liftVec = totalForce - forceDotV * vHat;

        return new AeroResult(
            totalForce,
            totalTorque,
            MathF.Abs(forceDotV),
            liftVec.Length(),
            0, 0,
            speed2 / 2.0);
    }

    /// <summary>
    /// Break down per-block contribution for analysis.
    /// </summary>
    public (int surfaceBlocks, int interiorBlocks, float surfaceForce, float interiorForce)
        AnalyzeInteriorContribution(Grid grid, Vector3 velocity, float blockSize = 2.5f)
    {
        float speed2 = velocity.LengthSquared();
        if (speed2 < 0.0001f)
            return (0, 0, 0, 0);

        float speed = MathF.Sqrt(speed2);
        Vector3 vHat = velocity / speed;
        float blockSurfaceArea = 6f * blockSize * blockSize;
        float areaPerCorner = (blockSurfaceArea / 8f) * SubmersionFraction;

        int surfaceBlocks = 0, interiorBlocks = 0;
        float surfaceForce = 0, interiorForce = 0;

        foreach (var block in grid.Blocks.Values)
        {
            foreach (var cell in block.EnumerateCells())
            {
                bool isSurface = false;
                for (int d = 0; d < 6; d++)
                {
                    var offset = d switch
                    {
                        0 => new Vector3I(1, 0, 0),
                        1 => new Vector3I(-1, 0, 0),
                        2 => new Vector3I(0, 1, 0),
                        3 => new Vector3I(0, -1, 0),
                        4 => new Vector3I(0, 0, 1),
                        _ => new Vector3I(0, 0, -1),
                    };
                    if (grid.GetBlockAt(cell + offset) == null)
                    {
                        isSurface = true;
                        break;
                    }
                }

                float cellForce = 0;
                var center = new Vector3(
                    (cell.X + 0.5f) * blockSize,
                    (cell.Y + 0.5f) * blockSize,
                    (cell.Z + 0.5f) * blockSize);

                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3(
                        (cell.X + (i & 1)) * blockSize,
                        (cell.Y + ((i >> 1) & 1)) * blockSize,
                        (cell.Z + ((i >> 2) & 1)) * blockSize);
                    var areaNormal = Vector3.Normalize(corner - center);
                    float dot = Vector3.Dot(vHat, areaNormal);
                    if (dot > 0)
                        cellForce += (speed2 / 2f) * areaPerCorner * dot;
                }

                if (isSurface)
                {
                    surfaceBlocks++;
                    surfaceForce += cellForce;
                }
                else
                {
                    interiorBlocks++;
                    interiorForce += cellForce;
                }
            }
        }

        return (surfaceBlocks, interiorBlocks, surfaceForce, interiorForce);
    }
}
