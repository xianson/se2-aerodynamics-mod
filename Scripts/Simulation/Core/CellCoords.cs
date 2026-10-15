#pragma warning disable
namespace AeroMod;

/// <summary>
/// Grid cells to grid-local metres, as the game has them (CubeGridCoords): cell i is CENTRED at i x 0.25 m and spans
/// (i -+ 0.5) x 0.25 m; a block's AABB is in cells, max inclusive; its centre is CubeGridCoords.GridToLocal(AABB).Center.
/// </summary>
public static class CellCoords
{
    public const float Cell = 0.25f;

    /// <summary>A block's centre (grid-local m) from its cell box (min, max inclusive) - the game's GridToLocal(AABB).Center.</summary>
    public static Vector3 BlockCentre(Vector3I min, Vector3I max)
        => new Vector3(min.X + max.X, min.Y + max.Y, min.Z + max.Z) * (0.5f * Cell);
}
