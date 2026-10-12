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

    /// <summary>LOW-ASPECT PLATES (2026-10-11): a piece turned down for its aspect ratio alone, at LowAspectRatio or more and
    /// LowArMinArea or larger, is a wing too - added after the merge, where no wing already holds its cells, so the main
    /// pass's wings stay exactly as they were (lowering MinAspectRatio itself merged and split real wings and let in
    /// hundreds of 0.2 m2 slivers - AeroBench, every fixture). Why: with pressure recovery and the square law, a flat
    /// fin or plate the detector missed lost ~90% of its lift (a 3 x 3 m plate's slope 0.56 -> 0.07 /rad: lift is
    /// circulation, the wing model's, not a face pressure's). 0: off.</summary>
    public static float LowAspectRatio = 0.5f, LowArMinArea = 2f;
    private bool _lowArPass, _rejectedAr;

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

    /// <summary>Cell size (m) and centre offset (cells): as the surface it reads (SmoothSurfaceProvider).</summary>
    public float CellSize { get; set; } = 0.25f;
    public float CellOffset { get; set; } = 0f;

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

    // ─── Working sets, shared ────────────────────────────────────
    // A detection's sets, maps and lists, kept between detections in a small pool (two: one per background worker)
    // rather than made anew each time: ~28 MB per Red Ship rebuild was these.
    private sealed class Scratch
    {
        public readonly Dictionary<long, int> MinX = new(LongKey.Comparer), MaxX = new(LongKey.Comparer), MinY = new(LongKey.Comparer),
            MaxY = new(LongKey.Comparer), MinZ = new(LongKey.Comparer), MaxZ = new(LongKey.Comparer);
        public readonly List<HashSet<Vector3I>> Exposed = new() { new(), new(), new() };
        public readonly HashSet<Vector3I> Visited = new();
        public readonly Queue<Vector3I> Queue = new();
        public readonly List<Vector3I> Component = new();
        public readonly Dictionary<int, List<Vector3I>> BySpan = new();
        public readonly Stack<List<Vector3I>> SpanLists = new();
        public readonly Dictionary<int, int> ChordExtent = new(), LeBySpan = new();
        public readonly List<int> ChordValues = new();
        public readonly Dictionary<(int, int), int> ColumnThickness = new();
        // (ColumnMode's)
        public long[] Keys = System.Array.Empty<long>(), Keys2 = System.Array.Empty<long>();
        public Vector3I[] Cells = System.Array.Empty<Vector3I>();
        public int[] ColStart = System.Array.Empty<int>(), ColCount = System.Array.Empty<int>();
        public long[] ColKeys = System.Array.Empty<long>(); public int[] ColRef = System.Array.Empty<int>(), ColRefN = System.Array.Empty<int>();
        public int MinA, MinB, RangeA, RangeB, NCols; public bool ColGrid; public int[] Hs = System.Array.Empty<int>();
        // (slabs, one array per field: arrays of mod types are banned, VRS1001)
        public int[] SA = System.Array.Empty<int>(), SB = System.Array.Empty<int>(), SLo = System.Array.Empty<int>(), SHi = System.Array.Empty<int>(), SComp = System.Array.Empty<int>(), SClr = System.Array.Empty<int>();
        public int[] Stack = System.Array.Empty<int>();
        public readonly Dictionary<long, long> ColIdx = new(LongKey.Comparer);   // (column -> first slab << 32 | count)
        public readonly List<int> Comp = new();
        public readonly List<int> Thick = new();
        public readonly List<Vector3I> Tops = new();
        public void Clear()
        {
            MinX.Clear(); MaxX.Clear(); MinY.Clear(); MaxY.Clear(); MinZ.Clear(); MaxZ.Clear();
            foreach (var e in Exposed) e.Clear();
            Visited.Clear(); Queue.Clear(); Component.Clear(); ChordExtent.Clear(); LeBySpan.Clear(); ChordValues.Clear(); ColumnThickness.Clear();
            ClearSpans();
        }
        public void ClearSpans() { foreach (var l in BySpan.Values) { l.Clear(); SpanLists.Push(l); } BySpan.Clear(); }
    }
    private static readonly Stack<Scratch> _scratchPool = new();
    private Scratch _s;
    private void Acquire() { lock (_scratchPool) _s = _scratchPool.Count > 0 ? _scratchPool.Pop() : null; _s ??= new Scratch(); }
    private void Release() { var sc = _s; _s = null; if (sc == null) return; sc.Clear(); lock (_scratchPool) if (_scratchPool.Count < 2) _scratchPool.Push(sc); }

    // ─── Full detection ──────────────────────────────────────────

    public static volatile string LastProfile = "";

    public List<LiftingSurface> Detect(IGridAccessor grid, ISurfaceProvider surface, float blockSize)
    {
        Acquire();
        try { return DetectAll(grid, blockSize); }
        finally { Release(); }
    }

    private List<LiftingSurface> DetectAll(IGridAccessor grid, float blockSize)
    {
        if (ColumnMode) return DetectColumns(grid, blockSize);
        var results = new List<LiftingSurface>();
        var lowAr = new List<LiftingSurface>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double tAnalyze = 0; int comps = 0, analyzed = 0;

        // Single-pass: collect all exposed faces and per-axis bounding box
        var allExposed = CollectAllExposedFaces(grid, out var gridBBox);
        double tCollect = sw.Elapsed.TotalMilliseconds;

        for (int d = 0; d < 3; d++)
        {
            var posNormal = PositiveNormals[d];

            var faces = allExposed[d];
            if (faces.Count < MinFaceCount)
                continue;

            double t1 = sw.Elapsed.TotalMilliseconds;
            foreach (var component in FloodFill(faces, posNormal, _s, grid))
            {
                comps++;
                if (component.Count < MinFaceCount) continue;
                analyzed++;
                _rejectedAr = false;
                var wing = AnalyzeComponent(grid, component, posNormal, blockSize, gridBBox);
                if (wing.HasValue)
                    results.Add(wing.Value);
                else if (_rejectedAr && LowAspectRatio > 0f)
                {
                    _lowArPass = true;
                    try { var low = AnalyzeComponent(grid, component, posNormal, blockSize, gridBBox); if (low.HasValue) lowAr.Add(low.Value); }
                    finally { _lowArPass = false; }
                }
            }
            tAnalyze += sw.Elapsed.TotalMilliseconds - t1;
        }

        double t2 = sw.Elapsed.TotalMilliseconds;
        int before = results.Count;
        results = MergeCoplanarWings(results, blockSize);
        int lowAdded = 0;
        if (lowAr.Count > 0)
        {
            var held = new HashSet<Vector3I>();
            foreach (var w in results) foreach (var c in w.Cells) held.Add(c);
            foreach (var w in lowAr)
            {
                bool free = true;
                foreach (var c in w.Cells) if (held.Contains(c)) { free = false; break; }
                if (!free) continue;
                results.Add(w); lowAdded++;
                foreach (var c in w.Cells) held.Add(c);
            }
        }
        LastProfile = $"collect {tCollect:F0} ms, flood + analyze {tAnalyze:F0} ms ({comps} components, {analyzed} analyzed), merge {sw.Elapsed.TotalMilliseconds - t2:F0} ms ({before} -> {results.Count} wings, {lowAdded} low-aspect plates)";

        _cachedWings = results;
        BuildCellLookup();
        return results;
    }

    // ─── Column mode ─────────────────────────────────────────────

    /// <summary>THE PLANFORM-COLUMN DETECTOR (2026-10-10). Per axis: each column of cells across the plane split into
    /// SLABS (runs of cells, one-cell gaps bridged); a slab is wing material when both its faces are the ship's outside
    /// (the manifold: a hollow hull's skin faces the cabin on one side - no wing) and it is no thicker than MaxSlabCells.
    /// Slabs in neighbouring columns whose heights overlap or touch are one piece - a stepped, layered or slope-block wing
    /// is one surface, however its cells step. Each piece's slabs thicker than SlabTrim x its median (a fuselage or an
    /// engine it runs into) are dropped and the rest flooded again. A piece is a wing by aspect ratio and t/c (the median
    /// slab over the mean chord). Its cells: each column's top and bottom cell (the skins: face ownership, strain, camber).
    /// Cost: one sort of the packed cells per axis, a sweep, a flood over slabs - no per-cell hashing. Replaces the
    /// layer-by-layer flood, whose stepped wings fell apart (a Tu-134 main wing in three stubs, a clean wing missing its
    /// leading and trailing strips) and whose patches doubled solid tailplanes.</summary>
    public static bool ColumnMode;   // (off: on the in-game jetliner it found 373 m2 of main wing in two halves where the layer flood finds 605, and its camber lifted the wrong way (-256 kN at 0 deg) - AeroBench polar jetliner, 2026-10-11)
    public static int MaxSlabCells = 16;
    public static float SlabTrim = 2.5f;

    /// <summary>A wing has open air about it on the scale of its chord: a piece whose median slab clearance (free cells to
    /// the next solid above or below in its column, the nearer) is under ClearFrac x its mean chord is boxed in - a hull's
    /// walls with their cabin between (an unsealed cabin reads as outside air: a Tu-134's fuselage passed as a 92 m2
    /// vertical wing).</summary>
    public static float ClearFrac = 0f;   // (per piece: off - the per-slab ClearMin does it)
    /// <summary>A slab with less free air than this (m) above or below it in its column is no wing material: a hollow
    /// hull's skins face their cabin across 2-4 m (a Tu-134's belly joined its main wing, 174 m2). Biplanes closer than
    /// this lose their facing pairs.</summary>
    public static float ClearMin = 4f;
    /// <summary>Gaps inside a column up to this (m) are bridged: a hollow wing (a jetliner's skins 0.5 m apart) is one
    /// slab, not two boxed in by each other. A cabin (2 m and more) is not bridged.</summary>
    public static float BridgeGap = 1f;
    public static float MinColumnArea = 1f;   // (m2: slivers are no wings)
    private const int FarClear = 1 << 16;
    private int _dropThick, _dropClear, _dropCavity;

    private const int POff = 1 << 20;
    private static long Pack3(int a, int b, int h) => ((long)(a + POff) << 42) | ((long)(b + POff) << 21) | (long)(h + POff);

    private List<LiftingSurface> DetectColumns(IGridAccessor grid, float blockSize)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = new List<LiftingSurface>();
        var sc = _s;
        int slabsTotal = 0; _dropThick = _dropClear = _dropCavity = 0;
        // the cells, once (the accessor's enumeration is an interface walk - three of them cost more than the rest)
        int nc = 0;
        if (sc.Cells.Length < grid.CellCount) sc.Cells = new Vector3I[grid.CellCount + 16];
        var mn = new Vector3I(int.MaxValue, int.MaxValue, int.MaxValue); var mx = new Vector3I(int.MinValue, int.MinValue, int.MinValue);
        foreach (var cell in grid.EnumerateOccupiedCells())
        {
            if (nc == sc.Cells.Length) System.Array.Resize(ref sc.Cells, nc * 2 + 16);
            sc.Cells[nc++] = cell;
            if (cell.X < mn.X) mn.X = cell.X; if (cell.Y < mn.Y) mn.Y = cell.Y; if (cell.Z < mn.Z) mn.Z = cell.Z;
            if (cell.X > mx.X) mx.X = cell.X; if (cell.Y > mx.Y) mx.Y = cell.Y; if (cell.Z > mx.Z) mx.Z = cell.Z;
        }
        if (nc < MinFaceCount) { _cachedWings = results; BuildCellLookup(); return results; }
        double tCells = sw.Elapsed.TotalMilliseconds, tSort = 0, tSweep = 0, tFlood = 0;
        if (sc.Keys.Length < nc) { sc.Keys = new long[nc]; sc.Keys2 = new long[nc]; }
        for (int d = 0; d < 3; d++)
        {
            double t0 = sw.Elapsed.TotalMilliseconds;
            var normal = PositiveNormals[d];
            GetPlanformAxes(normal, out var ax1, out var ax2, out var v1, out var v2);
            int minA = ProjectOnAxis(mn, ax1), minB = ProjectOnAxis(mn, ax2), minH = ProjectOnAxis(mn, normal);
            int rA = ProjectOnAxis(mx, ax1) - minA + 1, rB = ProjectOnAxis(mx, ax2) - minB + 1, rH = ProjectOnAxis(mx, normal) - minH + 1;
            int bH = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)rH), bB = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)rB);
            int bA = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)rA), bits = bA + bB + bH;
            sc.MinA = minA; sc.MinB = minB; sc.RangeA = rA; sc.RangeB = rB;
            long plane = (long)rA * rB;
            sc.ColGrid = plane <= 8_000_000;
            sc.NCols = 0;
            int ns = 0;
            var cellsArr = sc.Cells;
            double t1;
            if (sc.ColGrid)
            {
                // COUNTING SORT by column (the plane fits an array): count, prefix, scatter the heights - one pass each;
                // a column's few heights then sorted in place. (A radix sort of packed keys was 21 ms of Red Ship's 45.)
                int pl = (int)plane;
                if (sc.ColStart.Length < pl) { sc.ColStart = new int[pl]; sc.ColCount = new int[pl]; }
                if (sc.Hs.Length < nc) sc.Hs = new int[nc];
                var cnt = sc.ColCount; var pos = sc.ColStart; var hs = sc.Hs;
                System.Array.Clear(cnt, 0, pl);
                for (int i = 0; i < nc; i++) { var c = cellsArr[i]; cnt[(ProjectOnAxis(c, ax1) - minA) * rB + (ProjectOnAxis(c, ax2) - minB)]++; }
                int sum = 0;
                for (int ci = 0; ci < pl; ci++) { pos[ci] = sum; sum += cnt[ci]; }
                for (int i = 0; i < nc; i++) { var c = cellsArr[i]; hs[pos[(ProjectOnAxis(c, ax1) - minA) * rB + (ProjectOnAxis(c, ax2) - minB)]++] = ProjectOnAxis(c, normal); }
                t1 = sw.Elapsed.TotalMilliseconds; tSort += t1 - t0;
                for (int ci = 0; ci < pl; ci++)
                {
                    int m = cnt[ci];
                    if (m == 0) continue;
                    int st = pos[ci] - m;
                    if (m > 32) System.Array.Sort(hs, st, m);
                    else
                        for (int x = st + 1; x < st + m; x++) { int v = hs[x], y = x - 1; while (y >= st && hs[y] > v) { hs[y + 1] = hs[y]; y--; } hs[y + 1] = v; }
                    int s0 = ns;
                    SweepColumn(sc, ref ns, hs, st, m, ci / rB + minA, ci % rB + minB, ax1, ax2, normal);
                    pos[ci] = s0; cnt[ci] = ns - s0;   // (the same arrays, now the column index: first slab, slab count)
                }
            }
            else
            {
                // (a plane past 8M columns: packed (a, b, h) keys radix-sorted, columns found by binary search)
                var keys = sc.Keys;
                for (int i = 0; i < nc; i++)
                {
                    var c = cellsArr[i];
                    keys[i] = ((long)(ProjectOnAxis(c, ax1) - minA) << (bB + bH)) | ((long)(ProjectOnAxis(c, ax2) - minB) << bH) | (long)(ProjectOnAxis(c, normal) - minH);
                }
                RadixSort(ref sc.Keys, ref sc.Keys2, nc, bits);
                keys = sc.Keys;
                t1 = sw.Elapsed.TotalMilliseconds; tSort += t1 - t0;
                long hMask = (1L << bH) - 1;
                if (sc.Hs.Length < nc) sc.Hs = new int[nc];
                for (int i = 0; i < nc;)
                {
                    long col = keys[i] >> bH;
                    int j = i;
                    for (; j < nc && (keys[j] >> bH) == col; j++) sc.Hs[j] = (int)(keys[j] & hMask) + minH;
                    int a = (int)(col >> bB) + minA, b = (int)(col & ((1L << bB) - 1)) + minB, s0 = ns;
                    SweepColumn(sc, ref ns, sc.Hs, i, j - i, a, b, ax1, ax2, normal);
                    if (ns > s0)
                    {
                        if (sc.NCols == sc.ColKeys.Length) { System.Array.Resize(ref sc.ColKeys, sc.NCols * 2 + 64); System.Array.Resize(ref sc.ColRef, sc.NCols * 2 + 64); System.Array.Resize(ref sc.ColRefN, sc.NCols * 2 + 64); }
                        sc.ColKeys[sc.NCols] = ((long)(a - minA) << 32) | (uint)(b - minB); sc.ColRef[sc.NCols] = s0; sc.ColRefN[sc.NCols++] = ns - s0;
                    }
                    i = j;
                }
            }
            double t2 = sw.Elapsed.TotalMilliseconds; tSweep += t2 - t1;
            slabsTotal += ns;
            if (ns < MinFaceCount) continue;
            // pass 1: pieces, and each piece's slabs too thick for it dropped
            for (int i = 0; i < ns; i++) sc.SComp[i] = -1;
            int comps = 0;
            for (int i = 0; i < ns; i++)
            {
                if (sc.SComp[i] != -1) continue;
                FloodSlabs(sc, i, comps++);
                if (sc.Comp.Count < MinFaceCount) continue;
                sc.Thick.Clear();
                foreach (int si in sc.Comp) sc.Thick.Add(sc.SHi[si] - sc.SLo[si] + 1);
                sc.Thick.Sort();
                float lim = MathF.Max(4f, SlabTrim * sc.Thick[sc.Thick.Count / 4]);   // (the lower quartile: a fin on a fuselage roof is the smaller part of their piece - its median was the roof's)
                foreach (int si in sc.Comp) if (sc.SHi[si] - sc.SLo[si] + 1 > lim) sc.SComp[si] = -2;
            }
            // pass 2: pieces of what is left; then their mirror pairs (the span axis); then each a wing or not
            for (int i = 0; i < ns; i++) if (sc.SComp[i] != -2) sc.SComp[i] = -1;
            comps = 0;
            for (int i = 0; i < ns; i++)
            {
                if (sc.SComp[i] != -1) continue;
                FloodSlabs(sc, i, comps++);
            }
            var pieces = Pieces(sc, ns, comps);
            var span = Pairs(pieces);
            for (int c = 0; c < comps; c++)
            {
                if (pieces[c].Slabs.Count < MinFaceCount) continue;
                sc.Comp.Clear(); sc.Comp.AddRange(pieces[c].Slabs);
                var w = AnalyzeSlabs(sc, normal, ax1, ax2, v1, v2, blockSize, span[c]);
                if (w.HasValue) results.Add(w.Value);
            }
            tFlood += sw.Elapsed.TotalMilliseconds - t2;
        }
        double tDetect = sw.Elapsed.TotalMilliseconds;
        // (no coplanar merge: it fused a wing's halves into one piece with a wrong span - the Tu-134's 25 m wing as 11 m,
        //  AR 1.9; the wing model's systems fly the halves as one wing already)
        LastProfile = $"columns: {nc} cells, {slabsTotal} slabs (dropped: thick {_dropThick}, cavity {_dropCavity}, boxed in {_dropClear}), {tDetect:F1} ms (cells {tCells:F1}, sort {tSort:F1}, sweep {tSweep:F1}, flood+analyze {tFlood:F1}) -> {results.Count} wings";
        _cachedWings = results;
        BuildCellLookup();
        return results;
    }

    /// <summary>One column's sorted heights hs[st..st+m) into slabs.</summary>
    private void SweepColumn(Scratch sc, ref int ns, int[] hs, int st, int m, int a, int b, Vector3I ax1, Vector3I ax2, Vector3I normal)
    {
        int lo = hs[st], hi = lo, prevHi = int.MinValue;
        for (int x = st + 1; x < st + m; x++)
        {
            int h = hs[x];
            if ((h - hi - 1) * CellSize <= BridgeGap) { hi = h; continue; }   // (a hollow wing's inside bridged - BridgeGap)
            AddSlab(sc, ref ns, a, b, lo, hi, ax1, ax2, normal, Math.Min(prevHi == int.MinValue ? FarClear : lo - prevHi - 1, h - hi - 1));
            prevHi = hi; lo = hi = h;
        }
        AddSlab(sc, ref ns, a, b, lo, hi, ax1, ax2, normal, prevHi == int.MinValue ? FarClear : lo - prevHi - 1);
    }

    /// <summary>LSD radix sort of the first n keys (non-negative, below 2^bits), 11-bit digits, into a (b the buffer).</summary>
    private static void RadixSort(ref long[] a, ref long[] b, int n, int bits)
    {
        const int D = 8, R = 1 << D;   // (8-bit digits: 256 scatter streams stay in cache - 11-bit ones were slower on 365k cells)
        Span<int> count = stackalloc int[R];
        long[] src = a, dst = b;
        for (int shift = 0; shift < bits; shift += D)
        {
            count.Clear();
            for (int i = 0; i < n; i++) count[(int)((src[i] >> shift) & (R - 1))]++;
            int sum = 0, used = 0;
            for (int r = 0; r < R; r++) { int t = count[r]; if (t != 0) used++; count[r] = sum; sum += t; }
            if (used <= 1) continue;   // (every key the same in this digit: nothing to move)
            for (int i = 0; i < n; i++) { long v = src[i]; dst[count[(int)((v >> shift) & (R - 1))]++] = v; }
            (src, dst) = (dst, src);
        }
        a = src; b = dst;
    }

    /// <summary>The slabs of column (a, b): first index and count (count 0: none).</summary>
    private static (int start, int count) ColumnAt(Scratch sc, int a, int b)
    {
        int ra = a - sc.MinA, rb = b - sc.MinB;
        if ((uint)ra >= (uint)sc.RangeA || (uint)rb >= (uint)sc.RangeB) return (0, 0);
        if (sc.ColGrid) { int ci = ra * sc.RangeB + rb; return (sc.ColStart[ci], sc.ColCount[ci]); }
        int idx = System.Array.BinarySearch(sc.ColKeys, 0, sc.NCols, ((long)ra << 32) | (uint)rb);
        if (idx < 0) return (0, 0);
        return (sc.ColRef[idx], sc.ColRefN[idx]);
    }

    public static Func<Vector3I, bool> DebugRegion;   // (the bench's: report drops of slabs whose top cell is in it)
    private void AddSlab(Scratch sc, ref int ns, int a, int b, int lo, int hi, Vector3I ax1, Vector3I ax2, Vector3I n, int clr)
    {
        if (DebugRegion != null && DebugRegion(ax1 * a + ax2 * b + n * hi))
            Rejected?.Add($"slab n {n} at {ax1 * a + ax2 * b + n * hi} thick {hi - lo + 1} clear {clr} cavity {IsCavityFace(ax1 * a + ax2 * b + n * hi, n)}/{IsCavityFace(ax1 * a + ax2 * b + n * lo, new Vector3I(-n.X, -n.Y, -n.Z))}");
        if (hi - lo + 1 > MaxSlabCells) { _dropThick++; return; }
        if (clr * CellSize < ClearMin) { _dropClear++; return; }
        var basis = ax1 * a + ax2 * b;
        // both faces the ship's outside (not a hull skin facing its cabin)
        if (IsCavityFace(basis + n * hi, n) || IsCavityFace(basis + n * lo, new Vector3I(-n.X, -n.Y, -n.Z))) { _dropCavity++; return; }
        if (ns == sc.SA.Length)
        {
            int cap = ns * 2 + 64;
            System.Array.Resize(ref sc.SA, cap); System.Array.Resize(ref sc.SB, cap); System.Array.Resize(ref sc.SLo, cap);
            System.Array.Resize(ref sc.SHi, cap); System.Array.Resize(ref sc.SComp, cap); System.Array.Resize(ref sc.SClr, cap);
        }
        sc.SA[ns] = a; sc.SB[ns] = b; sc.SLo[ns] = lo; sc.SHi[ns] = hi; sc.SComp[ns] = -1; sc.SClr[ns] = clr; ns++;
    }

    /// <summary>The piece holding slab 'seed' (Comp -1 slabs only) into sc.Comp, each marked 'id'.</summary>
    private static void FloodSlabs(Scratch sc, int seed, int id)
    {
        sc.Comp.Clear();
        if (sc.Stack.Length < 64) sc.Stack = new int[64];
        int top = 0;
        sc.Stack[top++] = seed; sc.SComp[seed] = id;
        while (top > 0)
        {
            int si = sc.Stack[--top];
            sc.Comp.Add(si);
            int s0A = sc.SA[si], s0B = sc.SB[si], s0Lo = sc.SLo[si], s0Hi = sc.SHi[si];
            for (int nb = 0; nb < 4; nb++)
            {
                int na = s0A + (nb == 0 ? 1 : nb == 1 ? -1 : 0), nbb = s0B + (nb == 2 ? 1 : nb == 3 ? -1 : 0);
                var (st, cnt) = ColumnAt(sc, na, nbb);
                for (int q = st; q < st + cnt; q++)
                {
                    if (sc.SComp[q] != -1 || sc.SLo[q] > s0Hi + 1 || sc.SHi[q] < s0Lo - 1) continue;   // (heights overlap or touch: one surface)
                    sc.SComp[q] = id;
                    if (top == sc.Stack.Length) System.Array.Resize(ref sc.Stack, top * 2);
                    sc.Stack[top++] = q;
                }
            }
        }
    }

    private sealed class Piece { public readonly List<int> Slabs = new(); public int MinA = int.MaxValue, MaxA = int.MinValue, MinB = int.MaxValue, MaxB = int.MinValue; public double H; }

    /// <summary>Each piece's slabs and its bounds across the plane (A, B) and its mean height along the normal.</summary>
    private static List<Piece> Pieces(Scratch sc, int ns, int comps)
    {
        var ps = new List<Piece>(comps);
        for (int c = 0; c < comps; c++) ps.Add(new Piece());
        for (int i = 0; i < ns; i++)
        {
            int c = sc.SComp[i];
            if (c < 0 || c >= comps) continue;
            var p = ps[c]; p.Slabs.Add(i);
            int a = sc.SA[i], b = sc.SB[i];
            if (a < p.MinA) p.MinA = a; if (a > p.MaxA) p.MaxA = a; if (b < p.MinB) p.MinB = b; if (b > p.MaxB) p.MaxB = b;
            p.H += 0.5 * (sc.SLo[i] + sc.SHi[i]);
        }
        foreach (var p in ps) if (p.Slabs.Count > 0) p.H /= p.Slabs.Count;
        return ps;
    }

    /// <summary>MIRROR PAIRS (2026-10-11): two pieces in one plane, of like size (within PairSize) and height (within
    /// PairHeight cells), whose ranges along one axis overlap by PairOverlap of the shorter and along the other do not
    /// meet, are a pair of wing halves: the axis they lie apart along is their span. Per piece: true span A, false span
    /// B, null no pair (its extent decides).</summary>
    public static float PairSize = 1.6f, PairOverlap = 0.7f, PairHeight = 8f;
    private bool?[] Pairs(List<Piece> ps)
    {
        var span = new bool?[ps.Count];
        static float Over(int lo1, int hi1, int lo2, int hi2) => (MathF.Min(hi1, hi2) - MathF.Max(lo1, lo2) + 1f) / MathF.Min(hi1 - lo1 + 1f, hi2 - lo2 + 1f);
        for (int i = 0; i < ps.Count; i++)
        {
            var p = ps[i];
            if (p.Slabs.Count < MinFaceCount) continue;
            for (int j = 0; j < ps.Count && span[i] == null; j++)
            {
                var q = ps[j];
                if (j == i || q.Slabs.Count < MinFaceCount) continue;
                float r = (float)p.Slabs.Count / q.Slabs.Count;
                if (r > PairSize || r < 1f / PairSize || Math.Abs(p.H - q.H) > PairHeight) continue;
                bool apartB = p.MaxB < q.MinB || q.MaxB < p.MinB, apartA = p.MaxA < q.MinA || q.MaxA < p.MinA;
                if (apartB && !apartA && Over(p.MinA, p.MaxA, q.MinA, q.MaxA) >= PairOverlap) span[i] = false;
                else if (apartA && !apartB && Over(p.MinB, p.MaxB, q.MinB, q.MaxB) >= PairOverlap) span[i] = true;
            }
        }
        return span;
    }

    private LiftingSurface? AnalyzeSlabs(Scratch sc, Vector3I normal, Vector3I ax1, Vector3I ax2, Vector3 v1, Vector3 v2, float blockSize, bool? forceSpanA = null)
    {
        var comp = sc.Comp;
        int minA = int.MaxValue, maxA = int.MinValue, minB = int.MaxValue, maxB = int.MinValue;
        foreach (int si in comp) { int sa = sc.SA[si], sb = sc.SB[si]; minA = Math.Min(minA, sa); maxA = Math.Max(maxA, sa); minB = Math.Min(minB, sb); maxB = Math.Max(maxB, sb); }
        // The span axis: the mirror pair's (see Pairs), else the longer extent. (The extent alone: a swept half-wing reaches
        // further along the fuselage than out to its tip - the Jetliner's, 33.5 m against 30 - and each half came out a
        // wing spanning fore and aft: wrong area, sweep, aspect ratio and camber; the ship pitched past 50 deg in flight.
        // Its unbroken runs did not tell either: a block-built 40-deg wing's spanwise runs, ~4 m, are shorter than its chord.)
        bool spanIsA = forceSpanA ?? maxA - minA >= maxB - minB;
        float cs = CellSize;
        int spanCells = spanIsA ? maxA - minA + 1 : maxB - minB + 1;
        float span = spanCells * cs, area = comp.Count * cs * cs;
        float meanChord = area / span, ar = span * span / area;
        if (area < MinColumnArea) return null;
        sc.Thick.Clear();
        foreach (int si in comp) sc.Thick.Add(sc.SClr[si]);
        sc.Thick.Sort();
        if (sc.Thick[sc.Thick.Count / 2] * cs < ClearFrac * meanChord) { Rejected?.Add($"columns n {normal} slabs {comp.Count} S {area:F1} span {span:F1} AR {ar:F2} clearance {sc.Thick[sc.Thick.Count / 2] * cs:F1} m: boxed in"); return null; }
        if (ar < MinAspectRatio) { Rejected?.Add($"columns n {normal} slabs {comp.Count} S {area:F1} span {span:F1} AR {ar:F2}: aspect ratio"); return null; }
        sc.Thick.Clear();
        foreach (int si in comp) sc.Thick.Add(sc.SHi[si] - sc.SLo[si] + 1);
        sc.Thick.Sort();
        float tc = sc.Thick[sc.Thick.Count / 2] * cs / meanChord;
        if (tc > MaxThicknessRatio) { Rejected?.Add($"columns n {normal} slabs {comp.Count} S {area:F1} span {span:F1} AR {ar:F2} t/c {tc:F2}: too thick"); return null; }
        var nF = new Vector3(normal.X, normal.Y, normal.Z);
        var cells = new List<Vector3I>(comp.Count * 2);
        var tops = sc.Tops; tops.Clear();
        var mid = Vector3.Zero;
        foreach (int si in comp)
        {
            int sa = sc.SA[si], sb = sc.SB[si], slo = sc.SLo[si], shi = sc.SHi[si];
            var basis = ax1 * sa + ax2 * sb;
            var topC = basis + normal * shi;
            cells.Add(topC); tops.Add(topC);
            if (shi > slo) cells.Add(basis + normal * slo);
            mid += v1 * sa + v2 * sb + nF * ((slo + shi) * 0.5f);
        }
        mid /= comp.Count;
        var centroid = (mid + new Vector3(CellOffset)) * cs;
        Vector3I spanI = spanIsA ? ax1 : ax2, chordI = spanIsA ? ax2 : ax1;
        Vector3 spanVec = spanIsA ? v1 : v2, chordVec = spanIsA ? v2 : v1;
        float sweep = ComputeSweep(tops, spanI, chordI, blockSize, sc.LeBySpan);
        var aeroCenter = centroid - chordVec * (0.25f * meanChord);
        return new LiftingSurface(nF, spanVec, chordVec, area, span, tc, sweep, centroid, aeroCenter, comp.Count, cells);
    }

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

    // ─── Merge coplanar wings ────────────────────────────────────

    private void ComputeSpanExtents(LiftingSurface wing, out float minS, out float maxS)
    {
        minS = float.MaxValue;
        maxS = float.MinValue;
        foreach (var c in wing.Cells)
        {
            float s = (c.X + CellOffset) * CellSize * wing.SpanAxis.X
                    + (c.Y + CellOffset) * CellSize * wing.SpanAxis.Y
                    + (c.Z + CellOffset) * CellSize * wing.SpanAxis.Z;
            minS = MathF.Min(minS, s);
            maxS = MathF.Max(maxS, s);
        }
    }

    private List<LiftingSurface> MergeCoplanarWings(List<LiftingSurface> wings, float blockSize)
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
                        float s = (c.X + CellOffset) * CellSize * a.SpanAxis.X
                                + (c.Y + CellOffset) * CellSize * a.SpanAxis.Y
                                + (c.Z + CellOffset) * CellSize * a.SpanAxis.Z;
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
