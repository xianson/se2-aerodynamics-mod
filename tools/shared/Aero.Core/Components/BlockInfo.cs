using System.Numerics;
using Aero.Core.Grids;
using Aero.Core.Math;

namespace Aero.Core.Components;

/// <summary>
/// Pure-math snapshot of a block's placement for aero component construction.
/// </summary>
public readonly struct BlockInfo
{
    /// <summary>Block center in grid-local meters.</summary>
    public readonly Vector3 Position;

    /// <summary>Grid cell position (integer, for block-keyed removal).</summary>
    public readonly Vector3I BlockPosition;

    /// <summary>Block's forward direction (grid-local unit vector).</summary>
    public readonly Vector3 Forward;

    /// <summary>Block's up direction (grid-local unit vector).</summary>
    public readonly Vector3 Up;

    /// <summary>Block edge length in meters (2.5 large, 0.5 small).</summary>
    public readonly float BlockSize;

    /// <summary>Planform area of the control surface in m2 (single cell face).</summary>
    public readonly float FaceArea;

    public BlockInfo(Vector3 position, Vector3I blockPosition,
        Vector3 forward, Vector3 up, float blockSize)
    {
        Position = position;
        BlockPosition = blockPosition;
        Forward = forward;
        Up = up;
        BlockSize = blockSize;
        // Control surface planform area — one block face worth of lifting surface.
        // Real aircraft: aileron ~1-2 m2 on a 10m wing. A 2.5m block ~ 1 m2.
        FaceArea = 30.0f;
    }

    /// <summary>
    /// Build a BlockInfo from a standalone Block.
    /// </summary>
    public static BlockInfo FromBlock(Block block, float blockSize)
    {
        Vector3 position = new Vector3(
            (block.Min.X + block.Max.X) * 0.5f * blockSize,
            (block.Min.Y + block.Max.Y) * 0.5f * blockSize,
            (block.Min.Z + block.Max.Z) * 0.5f * blockSize);

        return new BlockInfo(position, block.Min, block.Forward, block.Up, blockSize);
    }
}
