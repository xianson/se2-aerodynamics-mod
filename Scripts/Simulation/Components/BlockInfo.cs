#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// Pure-math snapshot of a block's placement for aero component construction.
/// No SE2 type references — built by BlockComponentFactory from CubeBlockComponent data.
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

    /// <summary>The block's size along the grid's X, Y, Z (m): its occupied cells' bounds.</summary>
    public readonly Vector3 Extents;

    /// <summary>The block's AABB Min + Max (cells): the key both copies of a grid share for it (flames, flaps).</summary>
    public Vector3I CellKey { get; init; }
    /// <summary>The block's terminal settings, if it has any (the 3x3x1 control surfaces).</summary>
    public IControlSurfaceSettings Settings { get; init; }
    /// <summary>The block's retractable gear, if it is one (its drag follows the leg).</summary>
    public IGearState Gear { get; init; }
    /// <summary>The block's airbrake opening, if it is one (the 3x3x3 clamshell).</summary>
    public IAirbrakeState Airbrake { get; init; }

    public BlockInfo(Vector3 position, Vector3I blockPosition,
        Vector3 forward, Vector3 up, float blockSize, Vector3 extents)
    {
        Position = position;
        BlockPosition = blockPosition;
        Forward = forward;
        Up = up;
        BlockSize = blockSize;
        Extents = extents;
    }

    /// <summary>The block's area seen along <paramref name="normal"/> (an axis direction, e.g. Up for a control surface's
    /// planform, Forward for an airbrake's plate): the product of its two other extents. (It was a constant 30 m2 for every
    /// block until 2026-10-12, about five times a large block's 6.25 m2 face.)</summary>
    public float AreaFacing(Vector3 normal)
    {
        float ax = MathF.Abs(normal.X), ay = MathF.Abs(normal.Y), az = MathF.Abs(normal.Z);
        return ax >= ay && ax >= az ? Extents.Y * Extents.Z : ay >= az ? Extents.X * Extents.Z : Extents.X * Extents.Y;
    }
}
