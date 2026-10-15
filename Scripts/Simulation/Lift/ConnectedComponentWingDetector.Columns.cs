#pragma warning disable
using System;
namespace AeroMod;

// The planform-column detector (DetectColumns): slabs per column, their pieces and pairs, each piece's wing.
public partial class ConnectedComponentWingDetector
{
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
}
