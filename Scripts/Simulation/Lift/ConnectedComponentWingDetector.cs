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

    /// <summary>
    /// Optional manifold classifier for filtering out internal cavity faces.
    /// Set before calling Detect/Update. When set, only hull faces are considered
    /// as wing candidates, preventing phantom wings on large interior walls.
    /// </summary>
    public ManifoldClassifier Manifold { get; set; }

    /// <summary>
    /// Surface provider reference for manifold face-index lookups.
    /// Set alongside Manifold.
    /// </summary>
    public SmoothSurfaceProvider ManifoldSurface { get; set; }

    /// <summary>Check if a face at (cell, normalDir) is a cavity face that should be excluded.</summary>
    private bool IsCavityFace(Vector3I cell, Vector3I normalDir)
    {
        if (Manifold == null || !Manifold.IsClassified || ManifoldSurface == null)
            return false;

        // Convert normal vector to direction index
        int dir;
        if (normalDir.X == 1) dir = 0;
        else if (normalDir.X == -1) dir = 1;
        else if (normalDir.Y == 1) dir = 2;
        else if (normalDir.Y == -1) dir = 3;
        else if (normalDir.Z == 1) dir = 4;
        else dir = 5; // -Z

        int faceIdx = ManifoldSurface.GetFaceIndex(cell, dir);
        if (faceIdx < 0) return false; // face not in surface provider (shouldn't happen)
        return !Manifold.IsHull(faceIdx);
    }

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

        // Single-pass: collect all exposed faces and per-axis bounding box
        var allExposed = CollectAllExposedFaces(grid, out var gridBBox);

        for (int d = 0; d < 3; d++)
        {
            var posNormal = PositiveNormals[d];

            if (!allExposed.TryGetValue(d, out var faces) || faces.Count < MinFaceCount)
                continue;

            var components = FloodFill(faces, posNormal);

            foreach (var component in components)
            {
                if (component.Count < MinFaceCount) continue;

                var wing = AnalyzeComponent(grid, component, posNormal, blockSize, gridBBox);
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
            // If manifold classifier is available, also require the face to be hull (not cavity)
            var exposed = new HashSet<Vector3I>();
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

    private static void ComputeSpanExtents(LiftingSurface wing, out float minS, out float maxS)
    {
        minS = float.MaxValue;
        maxS = float.MinValue;
        foreach (var c in wing.Cells)
        {
            float s = (c.X + 0.5f) * CellSize * wing.SpanAxis.X
                    + (c.Y + 0.5f) * CellSize * wing.SpanAxis.Y
                    + (c.Z + 0.5f) * CellSize * wing.SpanAxis.Z;
            minS = MathF.Min(minS, s);
            maxS = MathF.Max(maxS, s);
        }
    }

    private static List<LiftingSurface> MergeCoplanarWings(List<LiftingSurface> wings, float blockSize)
    {
        float maxGapM = 12f * CellSize;

        // Pre-compute span extents for each wing
        var spanMin = new List<float>(wings.Count);
        var spanMax = new List<float>(wings.Count);
        for (int k0 = 0; k0 < wings.Count; k0++) { spanMin.Add(0f); spanMax.Add(0f); }
        for (int k = 0; k < wings.Count; k++)
        {
            ComputeSpanExtents(wings[k], out float sMin, out float sMax);
            spanMin[k] = sMin;
            spanMax[k] = sMax;
        }

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

                    float nDot = Vector3.Dot(a.Normal, b.Normal);
                    if (nDot < 0.99f) continue;

                    // Project both wings onto a's span axis for consistent comparison
                    float aMinS = spanMin[i], aMaxS = spanMax[i];
                    // Re-project b onto a's span axis
                    float bMinS2 = float.MaxValue, bMaxS2 = float.MinValue;
                    foreach (var c in b.Cells)
                    {
                        float s = (c.X + 0.5f) * CellSize * a.SpanAxis.X
                                + (c.Y + 0.5f) * CellSize * a.SpanAxis.Y
                                + (c.Z + 0.5f) * CellSize * a.SpanAxis.Z;
                        bMinS2 = MathF.Min(bMinS2, s);
                        bMaxS2 = MathF.Max(bMaxS2, s);
                    }

                    float gap = MathF.Max(aMinS, bMinS2) - MathF.Min(aMaxS, bMaxS2);
                    float maxSpan = MathF.Max(aMaxS - aMinS, bMaxS2 - bMinS2);
                    float gapLimit = MathF.Max(maxGapM, maxSpan * 0.20f);
                    if (gap > gapLimit)
                    {
                        if (a.PlanformArea > 100f && b.PlanformArea > 100f)
                            Log.Default?.Info($"[AERO-MERGE] W{i}+W{j} REJECT gap={gap:F2}>{gapLimit:F2} aS=[{aMinS:F1},{aMaxS:F1}] bS=[{bMinS2:F1},{bMaxS2:F1}] spanAx=({a.SpanAxis.X:F3},{a.SpanAxis.Y:F3},{a.SpanAxis.Z:F3})");
                        continue;
                    }

                    float normalSep = MathF.Abs(Vector3.Dot(a.Centroid - b.Centroid, a.Normal));
                    if (normalSep > CellSize * 6f)
                    {
                        if (a.PlanformArea > 100f && b.PlanformArea > 100f)
                            Log.Default?.Info($"[AERO-MERGE] W{i}+W{j} REJECT normalSep={normalSep:F2}>{CellSize * 6f:F2}");
                        continue;
                    }

                    float chordSep = MathF.Abs(Vector3.Dot(a.Centroid - b.Centroid, a.ChordAxis));
                    float maxChord = MathF.Max(a.MeanChord, b.MeanChord);
                    if (chordSep > maxChord * 2f)
                    {
                        if (a.PlanformArea > 100f && b.PlanformArea > 100f)
                            Log.Default?.Info($"[AERO-MERGE] W{i}+W{j} REJECT chordSep={chordSep:F2}>{maxChord * 2f:F2}");
                        continue;
                    }

                    Log.Default?.Info(
                        $"[AERO-MERGE] W{i}+W{j} MERGING " +
                        $"nDot={nDot:F4} gap={gap:F2} gapLim={gapLimit:F2} " +
                        $"normSep={normalSep:F2} chordSep={chordSep:F2}/{maxChord * 2f:F2} " +
                        $"aS=[{aMinS:F1},{aMaxS:F1}] bS=[{bMinS2:F1},{bMaxS2:F1}] " +
                        $"aSpan=({a.SpanAxis.X:F3},{a.SpanAxis.Y:F3},{a.SpanAxis.Z:F3})");

                    var allCells = new List<Vector3I>(a.Cells.Count + b.Cells.Count);
                    allCells.AddRange(a.Cells);
                    allCells.AddRange(b.Cells);

                    float totalArea = a.PlanformArea + b.PlanformArea;
                    float fullSpan = MathF.Max(aMaxS, bMaxS2) - MathF.Min(aMinS, bMinS2) + CellSize;

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
                    // Update span extents for merged wing
                    spanMin[i] = MathF.Min(aMinS, bMinS2);
                    spanMax[i] = MathF.Max(aMaxS, bMaxS2);

                    wings.RemoveAt(j);
                    // Shift span arrays: move last element into j's slot
                    int last = wings.Count; // after RemoveAt, Count is one less
                    if (j < last)
                    {
                        spanMin[j] = spanMin[last];
                        spanMax[j] = spanMax[last];
                    }
                    changed = true;
                }
            }
        }

        return wings;
    }

    // ─── Exposed face collection (single-pass projection) ────────

    private struct GridBBox
    {
        public int MinX, MaxX, MinY, MaxY, MinZ, MaxZ;
    }

    /// <summary>
    /// Single-pass collection of all exposed faces across all 3 axis pairs.
    /// Uses projection maps (min/max per column) instead of ray-marching.
    /// Returns Dictionary keyed by axis index (0=X, 1=Y, 2=Z), each containing
    /// cells exposed on either side of that axis.
    /// </summary>
    private Dictionary<int, HashSet<Vector3I>> CollectAllExposedFaces(
        IGridAccessor grid, out GridBBox bbox)
    {
        bbox = new GridBBox
        {
            MinX = int.MaxValue, MaxX = int.MinValue,
            MinY = int.MaxValue, MaxY = int.MinValue,
            MinZ = int.MaxValue, MaxZ = int.MinValue,
        };

        // Pass 1: Build projection maps (min/max coordinate along each axis per 2D column)
        // For X-axis: column key = (Y,Z), store min/max X
        // For Y-axis: column key = (X,Z), store min/max Y
        // For Z-axis: column key = (X,Y), store min/max Z
        var colMinX = new Dictionary<long, int>();
        var colMaxX = new Dictionary<long, int>();
        var colMinY = new Dictionary<long, int>();
        var colMaxY = new Dictionary<long, int>();
        var colMinZ = new Dictionary<long, int>();
        var colMaxZ = new Dictionary<long, int>();

        foreach (var cell in grid.EnumerateOccupiedCells())
        {
            bbox.MinX = Math.Min(bbox.MinX, cell.X); bbox.MaxX = Math.Max(bbox.MaxX, cell.X);
            bbox.MinY = Math.Min(bbox.MinY, cell.Y); bbox.MaxY = Math.Max(bbox.MaxY, cell.Y);
            bbox.MinZ = Math.Min(bbox.MinZ, cell.Z); bbox.MaxZ = Math.Max(bbox.MaxZ, cell.Z);

            // X-axis columns: keyed by (Y, Z)
            long kx = ((long)cell.Y << 32) | (uint)cell.Z;
            if (!colMinX.TryGetValue(kx, out int mnx) || cell.X < mnx) colMinX[kx] = cell.X;
            if (!colMaxX.TryGetValue(kx, out int mxx) || cell.X > mxx) colMaxX[kx] = cell.X;

            // Y-axis columns: keyed by (X, Z)
            long ky = ((long)cell.X << 32) | (uint)cell.Z;
            if (!colMinY.TryGetValue(ky, out int mny) || cell.Y < mny) colMinY[ky] = cell.Y;
            if (!colMaxY.TryGetValue(ky, out int mxy) || cell.Y > mxy) colMaxY[ky] = cell.Y;

            // Z-axis columns: keyed by (X, Y)
            long kz = ((long)cell.X << 32) | (uint)cell.Y;
            if (!colMinZ.TryGetValue(kz, out int mnz) || cell.Z < mnz) colMinZ[kz] = cell.Z;
            if (!colMaxZ.TryGetValue(kz, out int mxz) || cell.Z > mxz) colMaxZ[kz] = cell.Z;
        }

        // Pass 2: A face is exterior if the cell is at the min or max of its column
        // AND the adjacent cell in the normal direction is empty
        var result = new Dictionary<int, HashSet<Vector3I>>
        {
            { 0, new HashSet<Vector3I>() }, // X-axis
            { 1, new HashSet<Vector3I>() }, // Y-axis
            { 2, new HashSet<Vector3I>() }, // Z-axis
        };

        foreach (var cell in grid.EnumerateOccupiedCells())
        {
            // X-axis: check if at min/max of its (Y,Z) column
            long kx = ((long)cell.Y << 32) | (uint)cell.Z;
            if (cell.X == colMinX[kx] && !grid.IsCellOccupied(new Vector3I(cell.X - 1, cell.Y, cell.Z))
                && !IsCavityFace(cell, new Vector3I(-1, 0, 0)))
                result[0].Add(cell);
            if (cell.X == colMaxX[kx] && !grid.IsCellOccupied(new Vector3I(cell.X + 1, cell.Y, cell.Z))
                && !IsCavityFace(cell, new Vector3I(1, 0, 0)))
                result[0].Add(cell);

            // Y-axis: check if at min/max of its (X,Z) column
            long ky = ((long)cell.X << 32) | (uint)cell.Z;
            if (cell.Y == colMinY[ky] && !grid.IsCellOccupied(new Vector3I(cell.X, cell.Y - 1, cell.Z))
                && !IsCavityFace(cell, new Vector3I(0, -1, 0)))
                result[1].Add(cell);
            if (cell.Y == colMaxY[ky] && !grid.IsCellOccupied(new Vector3I(cell.X, cell.Y + 1, cell.Z))
                && !IsCavityFace(cell, new Vector3I(0, 1, 0)))
                result[1].Add(cell);

            // Z-axis: check if at min/max of its (X,Y) column
            long kz = ((long)cell.X << 32) | (uint)cell.Y;
            if (cell.Z == colMinZ[kz] && !grid.IsCellOccupied(new Vector3I(cell.X, cell.Y, cell.Z - 1))
                && !IsCavityFace(cell, new Vector3I(0, 0, -1)))
                result[2].Add(cell);
            if (cell.Z == colMaxZ[kz] && !grid.IsCellOccupied(new Vector3I(cell.X, cell.Y, cell.Z + 1))
                && !IsCavityFace(cell, new Vector3I(0, 0, 1)))
                result[2].Add(cell);
        }

        return result;
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

    private static readonly List<Vector3I> AdjX = new() { new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1) };
    private static readonly List<Vector3I> AdjY = new() { new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1) };
    private static readonly List<Vector3I> AdjZ = new() { new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0) };

    private static List<Vector3I> GetPlanformAdjacency(Vector3I normal)
    {
        if (normal.X != 0) return AdjX;
        if (normal.Y != 0) return AdjY;
        return AdjZ;
    }

    // ─── Component analysis ──────────────────────────────────────

    /// <summary>Overload without pre-computed bbox (used by DetectInRegion).</summary>
    private LiftingSurface? AnalyzeComponent(IGridAccessor grid, List<Vector3I> cells, Vector3I normal, float blockSize)
    {
        // Compute bbox on demand
        var bbox = new GridBBox
        {
            MinX = int.MaxValue, MaxX = int.MinValue,
            MinY = int.MaxValue, MaxY = int.MinValue,
            MinZ = int.MaxValue, MaxZ = int.MinValue,
        };
        foreach (var cell in grid.EnumerateOccupiedCells())
        {
            bbox.MinX = Math.Min(bbox.MinX, cell.X); bbox.MaxX = Math.Max(bbox.MaxX, cell.X);
            bbox.MinY = Math.Min(bbox.MinY, cell.Y); bbox.MaxY = Math.Max(bbox.MaxY, cell.Y);
            bbox.MinZ = Math.Min(bbox.MinZ, cell.Z); bbox.MaxZ = Math.Max(bbox.MaxZ, cell.Z);
        }
        return AnalyzeComponent(grid, cells, normal, blockSize, bbox);
    }

    private LiftingSurface? AnalyzeComponent(IGridAccessor grid, List<Vector3I> cells, Vector3I normal, float blockSize, GridBBox gridBBox)
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

        // Thickness measurement — use pre-computed grid bbox
        int nMin, nMax;
        if (normal.X != 0) { nMin = gridBBox.MinX; nMax = gridBBox.MaxX; }
        else if (normal.Y != 0) { nMin = gridBBox.MinY; nMax = gridBBox.MaxY; }
        else { nMin = gridBBox.MinZ; nMax = gridBBox.MaxZ; }

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
