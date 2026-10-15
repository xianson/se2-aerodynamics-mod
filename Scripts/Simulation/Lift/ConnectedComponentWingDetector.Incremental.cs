#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

// The incremental update after block changes: the affected wings and their region detected again.
public partial class ConnectedComponentWingDetector
{
    // ─── Incremental update ──────────────────────────────────────

    public List<LiftingSurface> Update(IGridAccessor grid, ISurfaceProvider surface, float blockSize,
        IReadOnlyList<Vector3I> addedCells, IReadOnlyList<Vector3I> removedCells)
    {
        if (_cachedWings == null || ColumnMode)
            return Detect(grid, surface, blockSize);   // (ColumnMode: a whole detection is a sort and a sweep - cheaper than the bookkeeping)

        // Find which cached wings are affected by the change
        var affected = new HashSet<int>();

        // Check changed cells and their 6-neighbors against wing lookup
        for (int ci = 0; ci < removedCells.Count; ci++)
            MarkAffected(removedCells[ci], affected);
        for (int ci = 0; ci < addedCells.Count; ci++)
            MarkAffected(addedCells[ci], affected);

        if (affected.Count == 0 && addedCells.Count == 0)
            return _cachedWings; // no wings touched, no new cells

        // Collect dirty pool: all cells from affected wings + changed cells
        var dirtyCells = new HashSet<Vector3I>();
        foreach (int wi in affected)
            foreach (var cell in _cachedWings[wi].Cells)
                dirtyCells.Add(cell);
        for (int i = 0; i < addedCells.Count; i++)
            dirtyCells.Add(addedCells[i]);

        // Remove cells that were destroyed
        for (int i = 0; i < removedCells.Count; i++)
            dirtyCells.Remove(removedCells[i]);

        // Only keep cells that are actually occupied
        dirtyCells.RemoveWhere(c => !grid.IsCellOccupied(c));

        // Also pull in occupied neighbors of changed cells (might form new wings)
        var neighbors = new List<Vector3I>();
        for (int ci = 0; ci < addedCells.Count; ci++)
            CollectOccupiedNeighbors(grid, addedCells[ci], neighbors);
        for (int ci = 0; ci < removedCells.Count; ci++)
            CollectOccupiedNeighbors(grid, removedCells[ci], neighbors);
        foreach (var n in neighbors)
        {
            if (_cellToWing.TryGetValue(n, out int nwi) && !affected.Contains(nwi))
            {
                // Neighbor belongs to an unaffected wing — pull that wing in too
                affected.Add(nwi);
                foreach (var cell in _cachedWings[nwi].Cells)
                    dirtyCells.Add(cell);
            }
            else
            {
                dirtyCells.Add(n);
            }
        }

        // Re-detect wings in the dirty region only (simple neighbor check, no ray-march)
        List<LiftingSurface> newWings;
        Acquire();
        try { newWings = DetectInRegion(grid, dirtyCells, blockSize); }
        finally { Release(); }

        // Build result: unaffected wings + newly detected wings
        var result = new List<LiftingSurface>();
        for (int i = 0; i < _cachedWings.Count; i++)
            if (!affected.Contains(i))
                result.Add(_cachedWings[i]);
        result.AddRange(newWings);

        result = MergeCoplanarWings(result, blockSize);

        _cachedWings = result;
        BuildCellLookup();
        return result;
    }

    private void MarkAffected(Vector3I cell, HashSet<int> affected)
    {
        if (_cellToWing.TryGetValue(cell, out int wi))
            affected.Add(wi);

        // Check 6-neighbors (planform adjacency in all directions)
        if (_cellToWing.TryGetValue(new Vector3I(cell.X + 1, cell.Y, cell.Z), out wi)) affected.Add(wi);
        if (_cellToWing.TryGetValue(new Vector3I(cell.X - 1, cell.Y, cell.Z), out wi)) affected.Add(wi);
        if (_cellToWing.TryGetValue(new Vector3I(cell.X, cell.Y + 1, cell.Z), out wi)) affected.Add(wi);
        if (_cellToWing.TryGetValue(new Vector3I(cell.X, cell.Y - 1, cell.Z), out wi)) affected.Add(wi);
        if (_cellToWing.TryGetValue(new Vector3I(cell.X, cell.Y, cell.Z + 1), out wi)) affected.Add(wi);
        if (_cellToWing.TryGetValue(new Vector3I(cell.X, cell.Y, cell.Z - 1), out wi)) affected.Add(wi);
    }

    private static void CollectOccupiedNeighbors(IGridAccessor grid, Vector3I cell, List<Vector3I> result)
    {
        TryAdd(grid, new Vector3I(cell.X + 1, cell.Y, cell.Z), result);
        TryAdd(grid, new Vector3I(cell.X - 1, cell.Y, cell.Z), result);
        TryAdd(grid, new Vector3I(cell.X, cell.Y + 1, cell.Z), result);
        TryAdd(grid, new Vector3I(cell.X, cell.Y - 1, cell.Z), result);
        TryAdd(grid, new Vector3I(cell.X, cell.Y, cell.Z + 1), result);
        TryAdd(grid, new Vector3I(cell.X, cell.Y, cell.Z - 1), result);
    }

    private static void TryAdd(IGridAccessor grid, Vector3I cell, List<Vector3I> result)
    {
        if (grid.IsCellOccupied(cell))
            result.Add(cell);
    }

    /// <summary>
    /// Run wing detection on a limited set of cells.
    /// Uses simple neighbor check (no ray-march) — the full Detect() with ray-march
    /// runs after the cooldown timer expires and corrects any interior-face issues.
    /// </summary>
    private List<LiftingSurface> DetectInRegion(IGridAccessor grid, HashSet<Vector3I> regionCells, float blockSize)
    {
        var results = new List<LiftingSurface>();
        if (regionCells.Count < MinFaceCount) return results;

        for (int d = 0; d < 3; d++)
        {
            var posNormal = PositiveNormals[d];
            var negNormal = new Vector3I(-posNormal.X, -posNormal.Y, -posNormal.Z);

            // Simple exposure check: cell is in region AND neighbor in normal direction is empty
            // If manifold classifier is available, also require the face to be hull (not cavity)
            var exposed = _s.Exposed[0];
            exposed.Clear();
            foreach (var cell in regionCells)
            {
                if (!grid.IsCellOccupied(cell + posNormal))
                {
                    if (!IsCavityFace(cell, posNormal))
                        exposed.Add(cell);
                }
                if (!grid.IsCellOccupied(cell + negNormal))
                {
                    if (!IsCavityFace(cell, negNormal))
                        exposed.Add(cell);
                }
            }

            if (exposed.Count < MinFaceCount) continue;

            foreach (var component in FloodFill(exposed, posNormal, _s, grid))
            {
                if (component.Count < MinFaceCount) continue;

                var wing = AnalyzeComponent(grid, component, posNormal, blockSize);
                if (wing.HasValue)
                    results.Add(wing.Value);
            }
        }

        return results;
    }
}
