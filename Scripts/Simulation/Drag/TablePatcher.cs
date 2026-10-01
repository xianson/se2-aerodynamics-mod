#pragma warning disable
using System;

namespace AeroMod;

/// <summary>
/// Damage (and building), at once: the faces a block change made and unmade, straight into a force table (the
/// background rebuild, seconds later on a big grid, brings the exact result and fades in). Side by side over each
/// changed box, against the grid as it is after the change: across a side whose neighbour cell is empty a face came
/// (added) or went (removed); where the neighbour is solid, the face it turns toward the box went (added) or came
/// (removed); neighbours changed in the same event had none either way. Each side is one patch - a dozen per block -
/// over every table direction.
/// </summary>
public static class TablePatcher
{
    const float Cell = 0.25f, Half = 0.125f, FaceArea = Cell * Cell;

    /// <returns>Patches applied.</returns>
    public static int PatchBoxes(ForceTable table, DampedShadowedDragModel dsm, IGridAccessor grid, List<(Vector3I min, Vector3I max)> boxes, bool removed, List<(Vector3 n, Vector3 p, float area)> log = null)
    {
        if (table == null || boxes.Count == 0) return 0;
        int patches = 0;
        for (int bi = 0; bi < boxes.Count; bi++)
        {
            var (min, max) = boxes[bi];
            for (int side = 0; side < 6; side++)
            {
                int axis = side >> 1, sgn = (side & 1) == 0 ? 1 : -1;
                var ns = axis == 0 ? new Vector3I(sgn, 0, 0) : axis == 1 ? new Vector3I(0, sgn, 0) : new Vector3I(0, 0, sgn);
                var lo = min; var hi = max;   // the box's layer of cells on this side
                if (axis == 0) { lo.X = hi.X = sgn > 0 ? max.X : min.X; }
                else if (axis == 1) { lo.Y = hi.Y = sgn > 0 ? max.Y : min.Y; }
                else { lo.Z = hi.Z = sgn > 0 ? max.Z : min.Z; }
                int nOpen = 0, nSolid = 0; var sumOpen = Vector3.Zero; var sumSolid = Vector3.Zero;
                for (int x = lo.X; x <= hi.X; x++)
                    for (int y = lo.Y; y <= hi.Y; y++)
                        for (int z = lo.Z; z <= hi.Z; z++)
                        {
                            var c = new Vector3I(x, y, z);
                            var m = c + ns;
                            if (InBoxes(boxes, m)) continue;
                            var centre = new Vector3(c.X * Cell + ns.X * Half, c.Y * Cell + ns.Y * Half, c.Z * Cell + ns.Z * Half);
                            if (grid.IsCellOccupied(m)) { nSolid++; sumSolid += centre; }
                            else { nOpen++; sumOpen += centre; }
                        }
                var n = new Vector3(ns.X, ns.Y, ns.Z);
                // only the part of each that meets the outside air counts (the table holds the hull, not rooms):
                //   open side  - its faces looked out through the open neighbour: outward along +ns
                //   solid side - the neighbour faces into the box's place, open now (removed) or covered (added):
                //                it meets the air if, after a removal, the hole is open to the outside past the box
                float fOpen = nOpen > 0 ? ExteriorFraction(grid, lo, hi, ns, 1, boxes) : 0f;
                float fSolid = nSolid > 0 && removed ? ExteriorFraction(grid, lo, hi, -ns, 1 + Extent(min, max, axis), boxes) : nSolid > 0 ? fOpen : 0f;
                if (nOpen > 0 && fOpen > 0) { Apply(table, dsm, n, sumOpen / nOpen, (removed ? -1f : 1f) * nOpen * FaceArea * fOpen, log); patches++; }
                if (nSolid > 0 && fSolid > 0) { Apply(table, dsm, -n, sumSolid / nSolid, (removed ? 1f : -1f) * nSolid * FaceArea * fSolid, log); patches++; }
            }
        }
        return patches;
    }

    /// <summary>How far (cells) a ray looks for anything solid before it calls a face's air the open sky: 40 m.</summary>
    const int ReachCells = 160;

    static int Extent(Vector3I min, Vector3I max, int axis) => axis == 0 ? max.X - min.X : axis == 1 ? max.Y - min.Y : max.Z - min.Z;

    /// <summary>The fraction of a side's layer whose air is the open sky: from 5 cells of the layer (corners and
    /// centre), a ray along dir - starting `skip` cells out (past the changed box, for the faces opposite) - meets no
    /// solid cell (changed boxes count as empty) within ReachCells.</summary>
    static float ExteriorFraction(IGridAccessor grid, Vector3I lo, Vector3I hi, Vector3I dir, int skip, List<(Vector3I min, Vector3I max)> boxes)
    {
        Span<Vector3I> starts = stackalloc Vector3I[5];
        starts[0] = lo; starts[1] = hi; starts[2] = new Vector3I(lo.X, hi.Y, lo.Z == hi.Z ? lo.Z : hi.Z);
        starts[3] = new Vector3I(hi.X, lo.Y, lo.Z); starts[4] = new Vector3I((lo.X + hi.X) / 2, (lo.Y + hi.Y) / 2, (lo.Z + hi.Z) / 2);
        int open = 0;
        for (int k = 0; k < 5; k++)
        {
            var c = starts[k] + dir * skip;
            bool blocked = false;
            for (int step = 0; step < ReachCells; step++, c += dir)
                if (grid.IsCellOccupied(c) && !InBoxes(boxes, c)) { blocked = true; break; }
            if (!blocked) open++;
        }
        return open / 5f;
    }

    static void Apply(ForceTable table, DampedShadowedDragModel dsm, Vector3 n, Vector3 p, float area, List<(Vector3, Vector3, float)> log)
    {
        dsm.AddPatch(table, n, p, area);
        log?.Add((n, p, area));
    }

    static bool InBoxes(List<(Vector3I min, Vector3I max)> boxes, Vector3I c)
    {
        for (int i = 0; i < boxes.Count; i++)
        {
            var (a, b) = boxes[i];
            if (c.X >= a.X && c.X <= b.X && c.Y >= a.Y && c.Y <= b.Y && c.Z >= a.Z && c.Z <= b.Z) return true;
        }
        return false;
    }
}
