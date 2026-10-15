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
public partial class ConnectedComponentWingDetector : IWingDetector
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

}
