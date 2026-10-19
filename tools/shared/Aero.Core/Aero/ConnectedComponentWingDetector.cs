using System.Numerics;
using Aero.Core.Grids;
using Aero.Core.Math;

namespace Aero.Core.Aero;

/// <summary>
/// Detects lifting surfaces by finding connected components of exposed faces
/// per normal direction, then classifying by aspect ratio, thickness, and size.
///
/// Algorithm:
///   A. For each of 6 axis normals, collect exposed faces → BFS flood-fill → connected components
///   B. Geometric analysis per component: bounding box, AR, thickness (raycast), sweep, centroid
///   C. Classification: wing if AR ≥ 1.5, t/c ≤ 0.5, faceCount ≥ 4
///   D. Precompute lift coefficients (Helmbold, Oswald)
/// </summary>
public class ConnectedComponentWingDetector : IWingDetector
{
    /// <summary>Minimum aspect ratio to qualify as a wing. AR &lt; 1.5 is a plate, not a wing.</summary>
    public float MinAspectRatio { get; set; } = 1.5f;

    /// <summary>Maximum thickness-to-chord ratio.</summary>
    public float MaxThicknessRatio { get; set; } = 0.5f;

    /// <summary>Minimum number of faces in a connected component.</summary>
    public int MinFaceCount { get; set; } = 4;

    /// <summary>
    /// Maximum adjacency search distance in cells for flood-fill.
    /// Set to 1 for dense cell grids (every cell filled), or higher for sparse
    /// grids where blocks are spaced apart (e.g. 10 for 2.5m blocks on 0.25m cells).
    /// </summary>
    public int MaxAdjacencyDistance { get; set; } = 1;

    // Only positive axis directions — each wing is detected once.
    // A flat plate has +Y and -Y exposed faces, but both belong to the same wing
    // (the wing generates lift along the ±Y axis). We use +Y as the canonical normal
    // and collect cells that have an exposed face in EITHER +N or -N direction.
    private static readonly Vector3I[] PositiveNormals =
    {
        new( 1, 0, 0), // X axis
        new( 0, 1, 0), // Y axis
        new( 0, 0, 1), // Z axis
    };

    public LiftingSurface[] Detect(Grid grid, ISurfaceProvider surface, float blockSize)
    {
        var results = new List<LiftingSurface>();

        // Single-pass: collect all exposed faces using projection maps (min/max per column)
        var allExposed = CollectAllExposedFaces(grid, out var gridBBox);

        for (int d = 0; d < 3; d++)
        {
            if (!allExposed.TryGetValue(d, out var faces) || faces.Count < MinFaceCount)
                continue;

            var components = FloodFill(faces, PositiveNormals[d]);

            foreach (var component in components)
            {
                if (component.Count < MinFaceCount) continue;

                var wing = AnalyzeComponent(grid, component, PositiveNormals[d], blockSize);
                if (wing.HasValue)
                    results.Add(wing.Value);
            }
        }

        // ── Merge symmetric wing halves ──
        // Real wings span tip-to-tip across the fuselage. The flood-fill splits
        // them at the fuselage gap. Merge pairs with the same normal whose
        // centroids are symmetric about the span=0 plane.
        results = MergeCoplanarWings(results, blockSize);

        return results.ToArray();
    }

    /// <summary>
    /// Collect exterior exposed faces per axis using projection maps.
    /// A cell is exterior-exposed on axis A if it's at the min or max of its column
    /// along A AND the adjacent cell in that direction is empty.
    /// This prevents interior cavity faces from being included.
    /// </summary>
    private static Dictionary<int, HashSet<Vector3I>> CollectAllExposedFaces(
        Grid grid, out (int MinX, int MaxX, int MinY, int MaxY, int MinZ, int MaxZ) bbox)
    {
        int bMinX = int.MaxValue, bMaxX = int.MinValue;
        int bMinY = int.MaxValue, bMaxY = int.MinValue;
        int bMinZ = int.MaxValue, bMaxZ = int.MinValue;

        // Pass 1: Build projection maps (min/max coordinate along each axis per 2D column)
        var colMinX = new Dictionary<long, int>();
        var colMaxX = new Dictionary<long, int>();
        var colMinY = new Dictionary<long, int>();
        var colMaxY = new Dictionary<long, int>();
        var colMinZ = new Dictionary<long, int>();
        var colMaxZ = new Dictionary<long, int>();

        foreach (var block in grid.Blocks.Values)
        {
            foreach (var cell in block.EnumerateCells())
            {
                bMinX = System.Math.Min(bMinX, cell.X); bMaxX = System.Math.Max(bMaxX, cell.X);
                bMinY = System.Math.Min(bMinY, cell.Y); bMaxY = System.Math.Max(bMaxY, cell.Y);
                bMinZ = System.Math.Min(bMinZ, cell.Z); bMaxZ = System.Math.Max(bMaxZ, cell.Z);

                long kx = ((long)cell.Y << 32) | (uint)cell.Z;
                if (!colMinX.TryGetValue(kx, out int mnx) || cell.X < mnx) colMinX[kx] = cell.X;
                if (!colMaxX.TryGetValue(kx, out int mxx) || cell.X > mxx) colMaxX[kx] = cell.X;

                long ky = ((long)cell.X << 32) | (uint)cell.Z;
                if (!colMinY.TryGetValue(ky, out int mny) || cell.Y < mny) colMinY[ky] = cell.Y;
                if (!colMaxY.TryGetValue(ky, out int mxy) || cell.Y > mxy) colMaxY[ky] = cell.Y;

                long kz = ((long)cell.X << 32) | (uint)cell.Y;
                if (!colMinZ.TryGetValue(kz, out int mnz) || cell.Z < mnz) colMinZ[kz] = cell.Z;
                if (!colMaxZ.TryGetValue(kz, out int mxz) || cell.Z > mxz) colMaxZ[kz] = cell.Z;
            }
        }

        bbox = (bMinX, bMaxX, bMinY, bMaxY, bMinZ, bMaxZ);

        // Pass 2: A face is exterior if the cell is at the min or max of its column
        var result = new Dictionary<int, HashSet<Vector3I>>
        {
            { 0, new HashSet<Vector3I>() },
            { 1, new HashSet<Vector3I>() },
            { 2, new HashSet<Vector3I>() },
        };

        foreach (var block in grid.Blocks.Values)
        {
            foreach (var cell in block.EnumerateCells())
            {
                long kx = ((long)cell.Y << 32) | (uint)cell.Z;
                if ((cell.X == colMinX[kx] && grid.GetBlockAt(new Vector3I(cell.X - 1, cell.Y, cell.Z)) == null)
                 || (cell.X == colMaxX[kx] && grid.GetBlockAt(new Vector3I(cell.X + 1, cell.Y, cell.Z)) == null))
                    result[0].Add(cell);

                long ky = ((long)cell.X << 32) | (uint)cell.Z;
                if ((cell.Y == colMinY[ky] && grid.GetBlockAt(new Vector3I(cell.X, cell.Y - 1, cell.Z)) == null)
                 || (cell.Y == colMaxY[ky] && grid.GetBlockAt(new Vector3I(cell.X, cell.Y + 1, cell.Z)) == null))
                    result[1].Add(cell);

                long kz = ((long)cell.X << 32) | (uint)cell.Y;
                if ((cell.Z == colMinZ[kz] && grid.GetBlockAt(new Vector3I(cell.X, cell.Y, cell.Z - 1)) == null)
                 || (cell.Z == colMaxZ[kz] && grid.GetBlockAt(new Vector3I(cell.X, cell.Y, cell.Z + 1)) == null))
                    result[2].Add(cell);
            }
        }

        return result;
    }

    /// <summary>
    /// Merge coplanar wing components that are close along the span axis.
    /// Two wings separated by a fuselage gap are physically one lifting surface.
    /// Merges greedily: any two wings with the same normal, overlapping chord extent,
    /// and a span gap smaller than maxGapCells are combined.
    /// </summary>
    private static List<LiftingSurface> MergeCoplanarWings(List<LiftingSurface> wings, float blockSize)
    {
        // Maximum gap in span direction (in meters) to merge across.
        // A fuselage of radius 3 at blockSize 2.5 = 7.5m body width + margin.
        float maxGapM = 12f * blockSize;

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

                    // Same normal
                    if (Vector3.Dot(a.Normal, b.Normal) < 0.99f) continue;

                    // Check span gap: project all cells onto span axis, find gap
                    float aMinS = float.MaxValue, aMaxS = float.MinValue;
                    foreach (var c in a.Cells)
                    {
                        float s = (c.X + 0.5f) * blockSize * a.SpanAxis.X
                                + (c.Y + 0.5f) * blockSize * a.SpanAxis.Y
                                + (c.Z + 0.5f) * blockSize * a.SpanAxis.Z;
                        aMinS = MathF.Min(aMinS, s); aMaxS = MathF.Max(aMaxS, s);
                    }
                    float bMinS = float.MaxValue, bMaxS = float.MinValue;
                    foreach (var c in b.Cells)
                    {
                        float s = (c.X + 0.5f) * blockSize * b.SpanAxis.X
                                + (c.Y + 0.5f) * blockSize * b.SpanAxis.Y
                                + (c.Z + 0.5f) * blockSize * b.SpanAxis.Z;
                        bMinS = MathF.Min(bMinS, s); bMaxS = MathF.Max(bMaxS, s);
                    }

                    // Gap = distance between the closest span edges
                    float gap = MathF.Max(aMinS, bMinS) - MathF.Min(aMaxS, bMaxS);
                    if (gap > maxGapM) continue; // too far apart in span

                    // Check coplanarity: wings offset along the normal axis are stacked,
                    // not split by a fuselage. Stacked wings should stay separate for
                    // Prandtl interference to tame their lift.
                    float normalSep = MathF.Abs(Vector3.Dot(a.Centroid - b.Centroid, a.Normal));
                    if (normalSep > blockSize * 1.5f) continue; // stacked, not coplanar

                    // Check chord overlap: centroids shouldn't be too far apart in chord direction
                    float chordSep = MathF.Abs(Vector3.Dot(a.Centroid - b.Centroid, a.ChordAxis));
                    float maxChord = MathF.Max(a.MeanChord, b.MeanChord);
                    if (chordSep > maxChord * 2f) continue; // different chord stations

                    // Merge
                    var allCells = new Vector3I[a.Cells.Length + b.Cells.Length];
                    a.Cells.CopyTo(allCells, 0);
                    b.Cells.CopyTo(allCells, a.Cells.Length);

                    float totalArea = a.PlanformArea + b.PlanformArea;
                    float fullSpan = MathF.Max(aMaxS, bMaxS) - MathF.Min(aMinS, bMinS) + blockSize;

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

    /// <summary>
    /// Collect all grid cells that have an exterior exposed face with the given normal direction.
    /// A face is exposed if the adjacent cell in the normal direction is empty.
    /// A face is exterior if a ray along the normal escapes the grid bounding box
    /// without hitting another block (interior cavity faces hit the opposite wall).
    /// </summary>
    private static HashSet<Vector3I> CollectExposedFaces(Grid grid, Vector3I normal)
    {
        // Compute grid bounding box for ray termination
        int bMin = int.MaxValue, bMax = int.MinValue;
        int normalAxis = normal.X != 0 ? 0 : normal.Y != 0 ? 1 : 2;
        foreach (var block in grid.Blocks.Values)
        {
            foreach (var cell in block.EnumerateCells())
            {
                int coord = normalAxis == 0 ? cell.X : normalAxis == 1 ? cell.Y : cell.Z;
                bMin = System.Math.Min(bMin, coord);
                bMax = System.Math.Max(bMax, coord);
            }
        }

        var faces = new HashSet<Vector3I>();
        foreach (var block in grid.Blocks.Values)
        {
            foreach (var cell in block.EnumerateCells())
            {
                if (grid.GetBlockAt(cell + normal) != null) continue; // not exposed

                // Ray-march along normal to check if this is exterior
                bool exterior = true;
                var probe = cell + normal + normal;
                while (true)
                {
                    int coord = normalAxis == 0 ? probe.X : normalAxis == 1 ? probe.Y : probe.Z;
                    if (coord < bMin || coord > bMax) break; // escaped — exterior
                    if (grid.GetBlockAt(probe) != null) { exterior = false; break; } // hit wall — interior
                    probe += normal;
                }

                if (exterior)
                    faces.Add(cell);
            }
        }
        return faces;
    }

    /// <summary>
    /// BFS flood-fill to find connected components in the planform plane.
    /// Searches up to MaxAdjacencyDistance cells in each planform direction.
    /// </summary>
    private List<List<Vector3I>> FloodFill(HashSet<Vector3I> faces, Vector3I normal)
    {
        var adjacencyDirs = GetPlanformAdjacency(normal);
        int maxDist = MaxAdjacencyDistance;

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
                    // Search up to maxDist cells in this direction
                    for (int step = 1; step <= maxDist; step++)
                    {
                        var neighbor = new Vector3I(
                            current.X + dir.X * step,
                            current.Y + dir.Y * step,
                            current.Z + dir.Z * step);
                        if (faces.Contains(neighbor) && visited.Add(neighbor))
                        {
                            queue.Enqueue(neighbor);
                            break; // found nearest neighbor in this direction
                        }
                    }
                }
            }

            components.Add(component);
        }

        return components;
    }

    /// <summary>
    /// Returns the 4 adjacency directions in the planform plane (perpendicular to normal).
    /// </summary>
    private static Vector3I[] GetPlanformAdjacency(Vector3I normal)
    {
        if (normal.X != 0) // ±X normal → planform in Y-Z
            return new[] { new Vector3I(0, 1, 0), new Vector3I(0, -1, 0), new Vector3I(0, 0, 1), new Vector3I(0, 0, -1) };
        if (normal.Y != 0) // ±Y normal → planform in X-Z
            return new[] { new Vector3I(1, 0, 0), new Vector3I(-1, 0, 0), new Vector3I(0, 0, 1), new Vector3I(0, 0, -1) };
        // ±Z normal → planform in X-Y
        return new[] { new Vector3I(1, 0, 0), new Vector3I(-1, 0, 0), new Vector3I(0, 1, 0), new Vector3I(0, -1, 0) };
    }

    /// <summary>
    /// Analyze a connected component: compute span, chord, AR, thickness, sweep, centroid.
    /// Includes fuselage stripping: span stations where chord is much larger than median
    /// are identified as fuselage and excluded from wing geometry.
    /// Returns null if it doesn't qualify as a wing.
    /// </summary>
    private LiftingSurface? AnalyzeComponent(Grid grid, List<Vector3I> cells, Vector3I normal, float blockSize)
    {
        // Project cells onto planform plane
        GetPlanformAxes(normal, out var axis1, out var axis2, out var spanVec, out var chordVec);

        // Bounding box in planform coords
        int min1 = int.MaxValue, max1 = int.MinValue;
        int min2 = int.MaxValue, max2 = int.MinValue;

        foreach (var cell in cells)
        {
            int c1 = ProjectOnAxis(cell, axis1);
            int c2 = ProjectOnAxis(cell, axis2);
            min1 = System.Math.Min(min1, c1); max1 = System.Math.Max(max1, c1);
            min2 = System.Math.Min(min2, c2); max2 = System.Math.Max(max2, c2);
        }

        int extent1 = max1 - min1 + 1;
        int extent2 = max2 - min2 + 1;

        // Span = longer extent, chord = shorter
        bool axis1IsSpan = extent1 >= extent2;

        // Swap axes if needed so axis1=span, axis2=chord
        if (!axis1IsSpan)
        {
            (axis1, axis2) = (axis2, axis1);
            (spanVec, chordVec) = (chordVec, spanVec);
        }

        // ── Strip analysis: identify fuselage core and compute wing-only geometry ──
        // Group cells by span coordinate
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

        // Compute chord extent at each span station
        var chordExtent = new Dictionary<int, int>();
        foreach (var (s, spanCells) in cellsBySpan)
        {
            int minC = int.MaxValue, maxC = int.MinValue;
            foreach (var cell in spanCells)
            {
                int c = ProjectOnAxis(cell, axis2);
                minC = System.Math.Min(minC, c);
                maxC = System.Math.Max(maxC, c);
            }
            chordExtent[s] = maxC - minC + 1;
        }

        // Find median chord — stations with chord much larger than median are fuselage.
        // But instead of removing entire stations, we use median chord for AR/area computation
        // while keeping all cells for centroid/aero center. This way the fuselage
        // doesn't inflate the planform area but the wing span is preserved.
        var chordValues = chordExtent.Values.ToList();
        chordValues.Sort();
        int medianChordCells = chordValues[chordValues.Count / 2];

        // Effective wing area: use actual cell count at wing stations, but cap
        // fuselage stations to median chord. This prevents the fuselage from
        // inflating planform area while keeping all wing cells.
        int effectiveCellCount = 0;
        var wingCells = cells; // use all cells for geometry
        foreach (var (s, extent) in chordExtent)
        {
            effectiveCellCount += System.Math.Min(extent, medianChordCells + 2) * 1;
            // For multi-cell stations, count actual cells up to the capped extent
        }
        // Better: count actual cells but cap per-station contribution
        effectiveCellCount = 0;
        foreach (var (s, spanCells) in cellsBySpan)
        {
            int cap = medianChordCells + 2; // allow small margin
            effectiveCellCount += System.Math.Min(spanCells.Count, cap);
        }

        if (wingCells.Count < MinFaceCount) return null;

        // Compute geometry — span from all cells, area from effective (fuselage-capped) count
        int sMin = int.MaxValue, sMax = int.MinValue;
        foreach (var cell in wingCells)
        {
            int s = ProjectOnAxis(cell, axis1);
            sMin = System.Math.Min(sMin, s); sMax = System.Math.Max(sMax, s);
        }

        int spanCellCount = sMax - sMin + 1;
        float span = spanCellCount * blockSize;
        float planformArea = effectiveCellCount * blockSize * blockSize;
        float meanChord = span > 0 ? planformArea / span : 0;
        float ar = span > 0 ? span * span / planformArea : 0;

        // Classification gate: AR
        if (ar < MinAspectRatio) return null;

        // Thickness: measure full body depth through the grid at each planform column.
        // Walk in both normal directions from the cell, crossing gaps (hollow interior),
        // to find the total extent of the grid body. This correctly identifies fuselage
        // shell strips (body depth = fuselage diameter) vs real wings (body depth = wing thickness).
        int nMin = int.MaxValue, nMax = int.MinValue;
        foreach (var block in grid.Blocks.Values)
        {
            foreach (var c in block.EnumerateCells())
            {
                int coord = ProjectOnAxis(c, normal);
                nMin = System.Math.Min(nMin, coord);
                nMax = System.Math.Max(nMax, coord);
            }
        }

        var columnThickness = new Dictionary<(int, int), int>();
        foreach (var cell in wingCells)
        {
            int s = ProjectOnAxis(cell, axis1);
            int c2val = ProjectOnAxis(cell, axis2);
            var key = (s, c2val);

            if (!columnThickness.ContainsKey(key))
            {
                // Find min and max occupied positions along the normal axis at this planform column
                int localMin = ProjectOnAxis(cell, normal);
                int localMax = localMin;

                var probe = cell + normal;
                int probeCoord = ProjectOnAxis(probe, normal);
                while (probeCoord <= nMax)
                {
                    if (grid.GetBlockAt(probe) != null)
                        localMax = probeCoord;
                    probe += normal;
                    probeCoord++;
                }

                probe = cell - normal;
                probeCoord = ProjectOnAxis(probe, normal);
                while (probeCoord >= nMin)
                {
                    if (grid.GetBlockAt(probe) != null)
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
        float meanThickness = meanThicknessCells * blockSize;
        float tc = meanChord > 0 ? meanThickness / meanChord : 0;

        // Classification gate: t/c
        if (tc > MaxThicknessRatio) return null;

        // Centroid (average of wing cell positions in world coords)
        var centroid = Vector3.Zero;
        foreach (var cell in wingCells)
        {
            centroid += new Vector3(
                (cell.X + 0.5f) * blockSize,
                (cell.Y + 0.5f) * blockSize,
                (cell.Z + 0.5f) * blockSize);
        }
        centroid /= wingCells.Count;

        // Sweep: linear fit of leading edge positions
        float sweepAngle = ComputeSweep(wingCells, axis1, axis2, blockSize);

        // Aero center: quarter-chord from leading edge centroid
        var leadingEdgeBySpan = new Dictionary<int, int>();
        foreach (var cell in wingCells)
        {
            int s = ProjectOnAxis(cell, axis1);
            int c = ProjectOnAxis(cell, axis2);
            if (!leadingEdgeBySpan.TryGetValue(s, out var minC) || c < minC)
                leadingEdgeBySpan[s] = c;
        }
        float meanLeadingEdge = 0;
        foreach (var (_, minC) in leadingEdgeBySpan)
            meanLeadingEdge += minC;
        if (leadingEdgeBySpan.Count > 0)
            meanLeadingEdge /= leadingEdgeBySpan.Count;

        var normalF = new Vector3(normal.X, normal.Y, normal.Z);
        // Quarter-chord point: centroid shifted toward LE by 25% of mean chord
        var aeroCenter = centroid - chordVec * (0.25f * meanChord);

        return new LiftingSurface(
            normalF, spanVec, chordVec,
            planformArea, span, tc, sweepAngle,
            centroid, aeroCenter, wingCells.Count,
            wingCells.ToArray());
    }

    /// <summary>
    /// Determine planform axes and their float-vector equivalents.
    /// Convention: for ±Y normal, span is Z, chord is X (matching airplane convention).
    /// </summary>
    private static void GetPlanformAxes(Vector3I normal,
        out Vector3I axis1, out Vector3I axis2,
        out Vector3 axis1Vec, out Vector3 axis2Vec)
    {
        if (normal.Y != 0) // ±Y: planform in X-Z
        {
            axis1 = new Vector3I(0, 0, 1); // span candidate = Z
            axis2 = new Vector3I(1, 0, 0); // chord candidate = X
            axis1Vec = Vector3.UnitZ;
            axis2Vec = Vector3.UnitX;
        }
        else if (normal.X != 0) // ±X: planform in Y-Z
        {
            axis1 = new Vector3I(0, 0, 1); // span candidate = Z
            axis2 = new Vector3I(0, 1, 0); // chord candidate = Y
            axis1Vec = Vector3.UnitZ;
            axis2Vec = Vector3.UnitY;
        }
        else // ±Z: planform in X-Y
        {
            axis1 = new Vector3I(1, 0, 0); // span candidate = X
            axis2 = new Vector3I(0, 1, 0); // chord candidate = Y
            axis1Vec = Vector3.UnitX;
            axis2Vec = Vector3.UnitY;
        }
    }

    private static int ProjectOnAxis(Vector3I cell, Vector3I axis)
        => cell.X * axis.X + cell.Y * axis.Y + cell.Z * axis.Z;

    /// <summary>
    /// Compute leading edge sweep angle via linear regression of LE positions vs span.
    /// </summary>
    private static float ComputeSweep(List<Vector3I> cells, Vector3I spanAxis, Vector3I chordAxis, float blockSize)
    {
        // Collect leading edge (min chord coord) at each span station
        var leBySpan = new Dictionary<int, int>();
        foreach (var cell in cells)
        {
            int s = ProjectOnAxis(cell, spanAxis);
            int c = ProjectOnAxis(cell, chordAxis);
            if (!leBySpan.TryGetValue(s, out var minC) || c < minC)
                leBySpan[s] = c;
        }

        if (leBySpan.Count < 2) return 0;

        // Linear regression: LE_chord = a + b * span
        double sumS = 0, sumC = 0, sumSS = 0, sumSC = 0;
        int n = 0;
        foreach (var (s, c) in leBySpan)
        {
            sumS += s;
            sumC += c;
            sumSS += (double)s * s;
            sumSC += (double)s * c;
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
