#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// Detects lifting surfaces by finding connected components of exposed faces
/// per normal direction, then classifying by aspect ratio, thickness, and size.
///
/// Algorithm:
///   A. For each of 6 axis normals, collect exposed faces → BFS flood-fill → connected components
///   B. Geometric analysis per component: bounding box, AR, thickness (raycast), sweep, centroid
///   C. Classification: wing if AR ≥ 1.5, t/c ≤ 0.5, faceCount ≥ 4
///   D. Precompute lift coefficients (Helmbold, Oswald)
///
/// Incremental mode: caches wings with cell→wing lookup. On block change, only
/// re-processes wings whose cells overlap the changed region. Skips expensive
/// full-grid ray-march; uses simple neighbor check instead.
/// </summary>
public class ConnectedComponentWingDetector : IWingDetector
{
    /// <summary>Minimum aspect ratio to qualify as a wing.</summary>
    public float MinAspectRatio { get; set; } = 1.5f;

    /// <summary>Maximum thickness-to-chord ratio.</summary>
    public float MaxThicknessRatio { get; set; } = 0.5f;

    /// <summary>Minimum number of faces in a connected component.</summary>
    public int MinFaceCount { get; set; } = 4;

    private static readonly List<Vector3I> PositiveNormals = new List<Vector3I>
    {
        new( 1, 0, 0),
        new( 0, 1, 0),
        new( 0, 0, 1),
    };

    private const float CellSize = 0.25f;

    // ─── Cached state for incremental updates ────────────────────

    private List<LiftingSurface> _cachedWings;
    private Dictionary<Vector3I, int> _cellToWing = new();

    public void Invalidate()
    {
        _cachedWings = null;
        _cellToWing.Clear();
    }

    private void BuildCellLookup()
    {
        _cellToWing.Clear();
        if (_cachedWings == null) return;
        for (int i = 0; i < _cachedWings.Count; i++)
            foreach (var cell in _cachedWings[i].Cells)
                _cellToWing[cell] = i;
    }

    // ─── Full detection ──────────────────────────────────────────

    public List<LiftingSurface> Detect(IGridAccessor grid, ISurfaceProvider surface, float blockSize)
    {
        var results = new List<LiftingSurface>();

        for (int d = 0; d < 3; d++)
        {
            var posNormal = PositiveNormals[d];
            var negNormal = new Vector3I(-posNormal.X, -posNormal.Y, -posNormal.Z);

            var faces = CollectExposedFaces(grid, posNormal);
            var negFaces = CollectExposedFaces(grid, negNormal);
            faces.UnionWith(negFaces);

            if (faces.Count < MinFaceCount) continue;

            var components = FloodFill(faces, posNormal);

            foreach (var component in components)
            {
                if (component.Count < MinFaceCount) continue;

                var wing = AnalyzeComponent(grid, component, posNormal, blockSize);
                if (wing.HasValue)
                    results.Add(wing.Value);
            }
        }

        results = MergeCoplanarWings(results, blockSize);

        _cachedWings = results;
        BuildCellLookup();
        return results;
    }

    // ─── Incremental update ──────────────────────────────────────

    public List<LiftingSurface> Update(IGridAccessor grid, ISurfaceProvider surface, float blockSize,
        IReadOnlyList<Vector3I> addedCells, IReadOnlyList<Vector3I> removedCells)
    {
        if (_cachedWings == null)
            return Detect(grid, surface, blockSize);

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
        var newWings = DetectInRegion(grid, dirtyCells, blockSize);

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
            var exposed = new HashSet<Vector3I>();
            foreach (var cell in regionCells)
            {
                if (!grid.IsCellOccupied(cell + posNormal))
                    exposed.Add(cell);
                if (!grid.IsCellOccupied(cell + negNormal))
                    exposed.Add(cell);
            }

            if (exposed.Count < MinFaceCount) continue;

            var components = FloodFill(exposed, posNormal);

            foreach (var component in components)
            {
                if (component.Count < MinFaceCount) continue;

                var wing = AnalyzeComponent(grid, component, posNormal, blockSize);
                if (wing.HasValue)
                    results.Add(wing.Value);
            }
        }

        return results;
    }

    // ─── Merge coplanar wings ────────────────────────────────────

    private static List<LiftingSurface> MergeCoplanarWings(List<LiftingSurface> wings, float blockSize)
    {
        float maxGapM = 12f * CellSize;

        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < wings.Count && !changed; i++)
            {
                for (int j = i + 1; j < wings.Count && !changed; j++)
                {
                    var a = wings[i];
                    var b = wings[j];

                    if (Vector3.Dot(a.Normal, b.Normal) < 0.99f) continue;

                    float aMinS = float.MaxValue, aMaxS = float.MinValue;
                    foreach (var c in a.Cells)
                    {
                        float s = (c.X + 0.5f) * CellSize * a.SpanAxis.X
                                + (c.Y + 0.5f) * CellSize * a.SpanAxis.Y
                                + (c.Z + 0.5f) * CellSize * a.SpanAxis.Z;
                        aMinS = MathF.Min(aMinS, s); aMaxS = MathF.Max(aMaxS, s);
                    }
                    float bMinS = float.MaxValue, bMaxS = float.MinValue;
                    foreach (var c in b.Cells)
                    {
                        float s = (c.X + 0.5f) * CellSize * b.SpanAxis.X
                                + (c.Y + 0.5f) * CellSize * b.SpanAxis.Y
                                + (c.Z + 0.5f) * CellSize * b.SpanAxis.Z;
                        bMinS = MathF.Min(bMinS, s); bMaxS = MathF.Max(bMaxS, s);
                    }

                    float gap = MathF.Max(aMinS, bMinS) - MathF.Min(aMaxS, bMaxS);
                    if (gap > maxGapM) continue;

                    float normalSep = MathF.Abs(Vector3.Dot(a.Centroid - b.Centroid, a.Normal));
                    if (normalSep > CellSize * 6f) continue; // ~1.5 blocks

                    float chordSep = MathF.Abs(Vector3.Dot(a.Centroid - b.Centroid, a.ChordAxis));
                    float maxChord = MathF.Max(a.MeanChord, b.MeanChord);
                    if (chordSep > maxChord * 2f) continue;

                    var allCells = new List<Vector3I>(a.Cells.Count + b.Cells.Count);
                    allCells.AddRange(a.Cells);
                    allCells.AddRange(b.Cells);

                    float totalArea = a.PlanformArea + b.PlanformArea;
                    float fullSpan = MathF.Max(aMaxS, bMaxS) - MathF.Min(aMinS, bMinS) + CellSize;

                    var centroid = (a.Centroid * a.PlanformArea + b.Centroid * b.PlanformArea) / totalArea;
                    var aeroCenter = (a.AeroCenter * a.PlanformArea + b.AeroCenter * b.PlanformArea) / totalArea;
                    float tc = (a.ThicknessRatio * a.PlanformArea + b.ThicknessRatio * b.PlanformArea) / totalArea;
                    float sweep = (a.SweepAngle * a.PlanformArea + b.SweepAngle * b.PlanformArea) / totalArea;

                    var merged = new LiftingSurface(
                        a.Normal, a.SpanAxis, a.ChordAxis,
                        totalArea, fullSpan, tc, sweep,
                        centroid, aeroCenter, a.FaceCount + b.FaceCount,
                        allCells);

                    wings[i] = merged;
                    wings.RemoveAt(j);
                    changed = true;
                }
            }
        }

        return wings;
    }

    // ─── Exposed face collection (full grid scan with ray-march) ─

    private static HashSet<Vector3I> CollectExposedFaces(IGridAccessor grid, Vector3I normal)
    {
        int normalAxis = normal.X != 0 ? 0 : normal.Y != 0 ? 1 : 2;

        // Compute grid bounding box for ray termination
        int bMin = int.MaxValue, bMax = int.MinValue;
        foreach (var cell in grid.EnumerateOccupiedCells())
        {
            int coord = normalAxis == 0 ? cell.X : normalAxis == 1 ? cell.Y : cell.Z;
            bMin = Math.Min(bMin, coord);
            bMax = Math.Max(bMax, coord);
        }

        var faces = new HashSet<Vector3I>();
        foreach (var cell in grid.EnumerateOccupiedCells())
        {
            if (grid.IsCellOccupied(cell + normal)) continue; // not exposed

            // Ray-march along normal to check if this is exterior
            bool exterior = true;
            var probe = cell + normal + normal;
            while (true)
            {
                int coord = normalAxis == 0 ? probe.X : normalAxis == 1 ? probe.Y : probe.Z;
                if (coord < bMin || coord > bMax) break;
                if (grid.IsCellOccupied(probe)) { exterior = false; break; }
                probe += normal;
            }

            if (exterior)
                faces.Add(cell);
        }
        return faces;
    }

    // ─── BFS flood-fill ──────────────────────────────────────────

    private static List<List<Vector3I>> FloodFill(HashSet<Vector3I> faces, Vector3I normal)
    {
        var adjacencyDirs = GetPlanformAdjacency(normal);
        var visited = new HashSet<Vector3I>();
        var components = new List<List<Vector3I>>();

        foreach (var face in faces)
        {
            if (!visited.Add(face)) continue;

            var component = new List<Vector3I>();
            var queue = new Queue<Vector3I>();
            queue.Enqueue(face);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                component.Add(current);

                foreach (var dir in adjacencyDirs)
                {
                    var neighbor = current + dir;
                    if (faces.Contains(neighbor) && visited.Add(neighbor))
                        queue.Enqueue(neighbor);
                }
            }

            components.Add(component);
        }

        return components;
    }

    private static List<Vector3I> GetPlanformAdjacency(Vector3I normal)
    {
        if (normal.X != 0)
            return new List<Vector3I> { new Vector3I(0, 1, 0), new Vector3I(0, -1, 0), new Vector3I(0, 0, 1), new Vector3I(0, 0, -1) };
        if (normal.Y != 0)
            return new List<Vector3I> { new Vector3I(1, 0, 0), new Vector3I(-1, 0, 0), new Vector3I(0, 0, 1), new Vector3I(0, 0, -1) };
        return new List<Vector3I> { new Vector3I(1, 0, 0), new Vector3I(-1, 0, 0), new Vector3I(0, 1, 0), new Vector3I(0, -1, 0) };
    }

    // ─── Component analysis ──────────────────────────────────────

    private LiftingSurface? AnalyzeComponent(IGridAccessor grid, List<Vector3I> cells, Vector3I normal, float blockSize)
    {
        GetPlanformAxes(normal, out var axis1, out var axis2, out var spanVec, out var chordVec);

        int min1 = int.MaxValue, max1 = int.MinValue;
        int min2 = int.MaxValue, max2 = int.MinValue;

        foreach (var cell in cells)
        {
            int c1 = ProjectOnAxis(cell, axis1);
            int c2 = ProjectOnAxis(cell, axis2);
            min1 = Math.Min(min1, c1); max1 = Math.Max(max1, c1);
            min2 = Math.Min(min2, c2); max2 = Math.Max(max2, c2);
        }

        int extent1 = max1 - min1 + 1;
        int extent2 = max2 - min2 + 1;

        bool axis1IsSpan = extent1 >= extent2;
        if (!axis1IsSpan)
        {
            (axis1, axis2) = (axis2, axis1);
            (spanVec, chordVec) = (chordVec, spanVec);
        }

        // Strip analysis: identify fuselage core
        var cellsBySpan = new Dictionary<int, List<Vector3I>>();
        foreach (var cell in cells)
        {
            int s = ProjectOnAxis(cell, axis1);
            if (!cellsBySpan.TryGetValue(s, out var list))
            {
                list = new List<Vector3I>();
                cellsBySpan[s] = list;
            }
            list.Add(cell);
        }

        var chordExtent = new Dictionary<int, int>();
        foreach (var (s, spanCells) in cellsBySpan)
        {
            int minC = int.MaxValue, maxC = int.MinValue;
            foreach (var cell in spanCells)
            {
                int c = ProjectOnAxis(cell, axis2);
                minC = Math.Min(minC, c);
                maxC = Math.Max(maxC, c);
            }
            chordExtent[s] = maxC - minC + 1;
        }

        var chordValues = chordExtent.Values.ToList();
        chordValues.Sort();
        int medianChordCells = chordValues[chordValues.Count / 2];

        int effectiveCellCount = 0;
        foreach (var (s, spanCells) in cellsBySpan)
        {
            int cap = medianChordCells + 2;
            effectiveCellCount += Math.Min(spanCells.Count, cap);
        }

        var wingCells = cells;
        if (wingCells.Count < MinFaceCount) return null;

        int sMin = int.MaxValue, sMax = int.MinValue;
        foreach (var cell in wingCells)
        {
            int s = ProjectOnAxis(cell, axis1);
            sMin = Math.Min(sMin, s); sMax = Math.Max(sMax, s);
        }

        int spanCellCount = sMax - sMin + 1;
        float span = spanCellCount * CellSize;
        float planformArea = effectiveCellCount * CellSize * CellSize;
        float meanChord = span > 0 ? planformArea / span : 0;
        float ar = span > 0 ? span * span / planformArea : 0;

        if (ar < MinAspectRatio) return null;

        // Thickness measurement
        int nMin = int.MaxValue, nMax = int.MinValue;
        foreach (var cell in grid.EnumerateOccupiedCells())
        {
            int coord = ProjectOnAxis(cell, normal);
            nMin = Math.Min(nMin, coord);
            nMax = Math.Max(nMax, coord);
        }

        var columnThickness = new Dictionary<(int, int), int>();
        foreach (var cell in wingCells)
        {
            int s = ProjectOnAxis(cell, axis1);
            int c2val = ProjectOnAxis(cell, axis2);
            var key = (s, c2val);

            if (!columnThickness.ContainsKey(key))
            {
                int localMin = ProjectOnAxis(cell, normal);
                int localMax = localMin;

                var probe = cell + normal;
                int probeCoord = ProjectOnAxis(probe, normal);
                while (probeCoord <= nMax)
                {
                    if (grid.IsCellOccupied(probe))
                        localMax = probeCoord;
                    probe += normal;
                    probeCoord++;
                }

                probe = cell - normal;
                probeCoord = ProjectOnAxis(probe, normal);
                while (probeCoord >= nMin)
                {
                    if (grid.IsCellOccupied(probe))
                        localMin = probeCoord;
                    probe -= normal;
                    probeCoord--;
                }

                columnThickness[key] = localMax - localMin + 1;
            }
        }
        float totalThickness = 0;
        foreach (var t in columnThickness.Values) totalThickness += t;
        float meanThicknessCells = columnThickness.Count > 0 ? totalThickness / columnThickness.Count : 0;
        float meanThickness = meanThicknessCells * CellSize;
        float tc = meanChord > 0 ? meanThickness / meanChord : 0;

        if (tc > MaxThicknessRatio) return null;

        // Centroid (in grid-local meters, cell centers match face coordinate space)
        var centroid = Vector3.Zero;
        foreach (var cell in wingCells)
        {
            centroid += new Vector3(
                (cell.X + 0.5f) * CellSize,
                (cell.Y + 0.5f) * CellSize,
                (cell.Z + 0.5f) * CellSize);
        }
        centroid /= wingCells.Count;

        // Sweep
        float sweepAngle = ComputeSweep(wingCells, axis1, axis2, blockSize);

        // Aero center: quarter-chord from leading edge
        var normalF = new Vector3(normal.X, normal.Y, normal.Z);
        var aeroCenter = centroid - chordVec * (0.25f * meanChord);

        return new LiftingSurface(
            normalF, spanVec, chordVec,
            planformArea, span, tc, sweepAngle,
            centroid, aeroCenter, wingCells.Count,
            wingCells);
    }

    private static void GetPlanformAxes(Vector3I normal,
        out Vector3I axis1, out Vector3I axis2,
        out Vector3 axis1Vec, out Vector3 axis2Vec)
    {
        if (normal.Y != 0)
        {
            axis1 = new Vector3I(0, 0, 1);
            axis2 = new Vector3I(1, 0, 0);
            axis1Vec = Vector3.UnitZ;
            axis2Vec = Vector3.UnitX;
        }
        else if (normal.X != 0)
        {
            axis1 = new Vector3I(0, 0, 1);
            axis2 = new Vector3I(0, 1, 0);
            axis1Vec = Vector3.UnitZ;
            axis2Vec = Vector3.UnitY;
        }
        else
        {
            axis1 = new Vector3I(1, 0, 0);
            axis2 = new Vector3I(0, 1, 0);
            axis1Vec = Vector3.UnitX;
            axis2Vec = Vector3.UnitY;
        }
    }

    private static int ProjectOnAxis(Vector3I cell, Vector3I axis)
        => cell.X * axis.X + cell.Y * axis.Y + cell.Z * axis.Z;

    private static float ComputeSweep(List<Vector3I> cells, Vector3I spanAxis, Vector3I chordAxis, float blockSize)
    {
        var leBySpan = new Dictionary<int, int>();
        foreach (var cell in cells)
        {
            int s = ProjectOnAxis(cell, spanAxis);
            int c = ProjectOnAxis(cell, chordAxis);
            if (!leBySpan.TryGetValue(s, out var minC) || c < minC)
                leBySpan[s] = c;
        }

        if (leBySpan.Count < 2) return 0;

        double sumS = 0, sumC = 0, sumSS = 0, sumSC = 0;
        int n = 0;
        foreach (var (s, c) in leBySpan)
        {
            sumS += s; sumC += c;
            sumSS += (double)s * s; sumSC += (double)s * c;
            n++;
        }
        double meanS = sumS / n;
        double meanC = sumC / n;
        double varS = sumSS / n - meanS * meanS;

        if (varS < 0.01) return 0;

        double slope = (sumSC / n - meanS * meanC) / varS;
        return MathF.Atan(MathF.Abs((float)slope));
    }
}
