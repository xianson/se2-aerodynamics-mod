#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

// The flood-fill path: exposed faces by projection, connected components, each component's planform analysis.
public partial class ConnectedComponentWingDetector
{
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
    private List<HashSet<Vector3I>> CollectAllExposedFaces(
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
        var colMinX = _s.MinX; var colMaxX = _s.MaxX; var colMinY = _s.MinY;
        var colMaxY = _s.MaxY; var colMinZ = _s.MinZ; var colMaxZ = _s.MaxZ;

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
        var result = _s.Exposed;   // (X, Y, Z axes)
        foreach (var e in result) e.Clear();

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

    /// <summary>The connected components, one at a time, in one reused list (copy one to keep it).</summary>
    /// <summary>STEPPED WINGS (2026-10-10): the flood also steps one cell across the plane to a diagonal neighbour, when
    /// both columns there are thin (MaxStepThickness cells or fewer along the normal) - a wing drawn in cells steps
    /// (a one-cell leading and trailing edge on a two-cell wing; a slope-block wing): flooded one layer at a time it fell
    /// apart (a clean airliner's half wing found as 25 of its 47 m2, a Tu-134's in stacked layers). A fuselage's columns
    /// are thick: no wing floods into one.</summary>
    public static bool StepConnect;
    public static List<string> Rejected;   // (the bench's: why components were not wings)
    public static int MaxStepThickness = 4;

    /// <summary>The column's occupied extent along n about c, looking ScanCells each way - not just the cells touching
    /// it: a hull is hollow, its skin one or two cells thick (the flood ran through a Tu-134's fuselage skin from wing
    /// root to wing root, and the merged 'wing' took the fuselage's height for its thickness).</summary>
    private static int ThicknessAt(IGridAccessor grid, Vector3I c, Vector3I n, int cap)
    {
        const int ScanCells = 16;
        int hi = 0, lo = 0;
        var p = c;
        for (int k = 1; k <= ScanCells; k++) { p += n; if (grid.IsCellOccupied(p)) hi = k; }
        p = c;
        for (int k = 1; k <= ScanCells; k++) { p -= n; if (grid.IsCellOccupied(p)) lo = k; }
        return hi + lo + 1;
    }

    private static IEnumerable<List<Vector3I>> FloodFill(HashSet<Vector3I> faces, Vector3I normal, Scratch sc, IGridAccessor grid = null)
    {
        var adjacencyDirs = GetPlanformAdjacency(normal);
        var visited = sc.Visited; visited.Clear();
        var component = sc.Component;
        var queue = sc.Queue;

        foreach (var face in faces)
        {
            if (!visited.Add(face)) continue;

            component.Clear();
            queue.Clear();
            queue.Enqueue(face);
            int seedH = face.X * normal.X + face.Y * normal.Y + face.Z * normal.Z;   // (diagonal steps stay within MaxStepThickness of it: a wing is a band, not a tail cone)

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                component.Add(current);

                bool thinHere = false, thinKnown = false;
                foreach (var dir in adjacencyDirs)
                {
                    var neighbor = current + dir;
                    if (faces.Contains(neighbor)) { if (visited.Add(neighbor)) queue.Enqueue(neighbor); continue; }
                    if (!StepConnect || grid == null) continue;
                    for (int sg = -1; sg <= 1; sg += 2)
                    {
                        var diag = neighbor + normal * sg;
                        if (!faces.Contains(diag) || visited.Contains(diag)) continue;
                        if (Math.Abs(diag.X * normal.X + diag.Y * normal.Y + diag.Z * normal.Z - seedH) > MaxStepThickness) continue;
                        if (!thinKnown) { thinHere = ThicknessAt(grid, current, normal, MaxStepThickness) <= MaxStepThickness; thinKnown = true; }
                        if (!thinHere || ThicknessAt(grid, diag, normal, MaxStepThickness) > MaxStepThickness) continue;
                        visited.Add(diag); queue.Enqueue(diag);
                    }
                }
            }

            yield return component;
        }
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
        if (StepConnect && cells.Count >= MinFaceCount)
        {
            // (trim what is not wing: cells whose column across the plane is thick - more than MaxStepThickness and
            //  twice the component's median - a fuselage's skin the wing floods onto at its root, an engine under it)
            var th = new Dictionary<(int, int), int>();
            GetPlanformAxes(normal, out var ta1, out var ta2, out _, out _);
            var per = new int[cells.Count];
            for (int i = 0; i < cells.Count; i++)
            {
                var key = (ProjectOnAxis(cells[i], ta1), ProjectOnAxis(cells[i], ta2));
                if (!th.TryGetValue(key, out int t)) th[key] = t = ThicknessAt(grid, cells[i], normal, MaxStepThickness);
                per[i] = t;
            }
            var sorted = new List<int>(th.Values); sorted.Sort();
            int lim = Math.Max(MaxStepThickness, 2 * sorted[sorted.Count / 2]);
            var kept = new List<Vector3I>(cells.Count);
            for (int i = 0; i < cells.Count; i++) if (per[i] <= lim) kept.Add(cells[i]);
            if (kept.Count < cells.Count)
            {
                if (kept.Count < MinFaceCount) return null;
                cells = kept;
            }
        }
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
        _s.ClearSpans();
        var cellsBySpan = _s.BySpan;
        foreach (var cell in cells)
        {
            int s = ProjectOnAxis(cell, axis1);
            if (!cellsBySpan.TryGetValue(s, out var list))
            {
                list = _s.SpanLists.Count > 0 ? _s.SpanLists.Pop() : new List<Vector3I>();
                cellsBySpan[s] = list;
            }
            list.Add(cell);
        }

        var chordExtent = _s.ChordExtent; chordExtent.Clear();
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

        var chordValues = _s.ChordValues; chordValues.Clear(); chordValues.AddRange(chordExtent.Values);
        chordValues.Sort();
        int medianChordCells = chordValues[chordValues.Count / 2];

        int effectiveCellCount = 0;
        foreach (var (s, spanCells) in cellsBySpan)
        {
            int cap = medianChordCells + 2;
            int cnt = spanCells.Count;
            if (StepConnect)
            {
                // (distinct chord stations: a stepped wing's component holds its top and its bottom cells - counted
                //  as cells, a jetliner's tailplane came out twice its planform)
                var seen = new HashSet<int>();
                foreach (var cell in spanCells) seen.Add(ProjectOnAxis(cell, axis2));
                cnt = seen.Count;
            }
            effectiveCellCount += Math.Min(cnt, cap);
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

        if (_lowArPass ? ar < LowAspectRatio || ar >= MinAspectRatio || planformArea < LowArMinArea : ar < MinAspectRatio)
        {
            if (!_lowArPass) { _rejectedAr = true; Rejected?.Add($"n {normal} cells {cells.Count} S {planformArea:F1} span {span:F1} AR {ar:F2}: aspect ratio"); }
            return null;
        }

        // Thickness measurement — use pre-computed grid bbox
        int nMin, nMax;
        if (normal.X != 0) { nMin = gridBBox.MinX; nMax = gridBBox.MaxX; }
        else if (normal.Y != 0) { nMin = gridBBox.MinY; nMax = gridBBox.MaxY; }
        else { nMin = gridBBox.MinZ; nMax = gridBBox.MaxZ; }

        var columnThickness = _s.ColumnThickness; columnThickness.Clear();
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
        if (StepConnect && columnThickness.Count > 0)
        {
            // (the MEDIAN column: a wing runs through its fuselage and over its engines - those few columns are the
            //  fuselage's height; their mean took a 737's whole main wing to t/c 0.75 and threw it out)
            var tl = new List<int>(columnThickness.Values); tl.Sort();
            meanThicknessCells = tl[tl.Count / 2];
        }
        float meanThickness = meanThicknessCells * CellSize;
        float tc = meanChord > 0 ? meanThickness / meanChord : 0;

        if (tc > MaxThicknessRatio) { Rejected?.Add($"n {normal} cells {cells.Count} S {planformArea:F1} span {span:F1} AR {ar:F2} t/c {tc:F2}: too thick"); return null; }

        // Centroid (in grid-local meters, cell centers match face coordinate space)
        var centroid = Vector3.Zero;
        foreach (var cell in wingCells)
        {
            centroid += new Vector3(
                (cell.X + CellOffset) * CellSize,
                (cell.Y + CellOffset) * CellSize,
                (cell.Z + CellOffset) * CellSize);
        }
        centroid /= wingCells.Count;

        // Sweep
        float sweepAngle = ComputeSweep(wingCells, axis1, axis2, blockSize, _s.LeBySpan);

        // Aero center: quarter-chord from leading edge
        var normalF = new Vector3(normal.X, normal.Y, normal.Z);
        var aeroCenter = centroid - chordVec * (0.25f * meanChord);

        return new LiftingSurface(
            normalF, spanVec, chordVec,
            planformArea, span, tc, sweepAngle,
            centroid, aeroCenter, wingCells.Count,
            new List<Vector3I>(wingCells));   // (the component list is reused: the wing keeps a copy)
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

    private static float ComputeSweep(List<Vector3I> cells, Vector3I spanAxis, Vector3I chordAxis, float blockSize, Dictionary<int, int> leBySpan)
    {
        leBySpan.Clear();
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
