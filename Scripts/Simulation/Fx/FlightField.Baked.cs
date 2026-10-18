#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using Keen.VRage.Core;
namespace AeroMod;

/// <summary>
/// THE BAKED FIELD (the user, 2026-10-09: "we need to interpolate position and orientation between the prebaked
/// orientations", "no lag"). The plasma and the vapour sheets are both drawn along the ship's OUTLINE as the air sees it.
/// Instead of laying that outline out for one flow direction and throwing it away past AxisTol (a re-layout: the
/// emitters parked, the pool handing them back wiped, the field stepping ~2.5 times a second while turning):
///  - ANCHORS: fixed points on the hull (its surface points, BakeSpacing apart) - every emitter belongs to one anchor
///    and one channel (0 plasma front, 1 plasma tail, 2 vapour sheet), for the whole flight;
///  - BAKED DIRECTIONS: the vertices of a geodesic sphere (BakeLevel 3: 642, ~8.5 deg apart); per direction, the
///    anchors on the outline seen along it, each with its strength (the old layout's weight) and spray direction.
///    Baked a few per frame from the start (nearest the live flow first), and at once for any the live flow needs;
///  - EVERY FRAME, NO EASING: the live flow's triangle of baked directions, blended by barycentric weight - each
///    anchor's strength and spray direction interpolated. As the ship turns, emitters along the outline fade in and out
///    and turn their spray continuously: the outline glides over the hull, never relaid out.
/// Emitters are made only for anchors that light (at strength 0: no pop), kept while lit, parked after BakeIdle s dark.
/// </summary>
internal sealed partial class FlightField
{
    public static bool BakedOn = true;
    public static int BakeLevel = 3;
    /// <summary>Anchor spacing on the hull (m), and the bake's projection cell (x spacing).</summary>
    public static float BakeSpacing = 0.6f, BakeCell = 1.0f;
    /// <summary>Per frame: the background bake's budget (ms); an emitter dark this long goes back to the pool (s).</summary>
    public static float BakeBudgetMs = 2f, BakeIdle = 2f;
    /// <summary>The vapour sheets' rate: x their strength (the effect's rate rises with it up to half its time - the
    /// sheets ran at ~0.4 of it; "higher particle spawn rate", the user 2026-10-09).</summary>
    public static float VapourGain = 4f;

    // ── the geodesic sphere (shared) ──
    static Vector3[] _ico; static int[] _icoF; static List<int>[] _icoVF; static int _icoLevel = -1;
    static void Ico(int level)
    {
        if (_icoLevel == level && _ico != null) return;
        float t = (1f + MathF.Sqrt(5f)) / 2f;
        var v = new List<Vector3>
        {
            new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0), new(0, -1, t), new(0, 1, t),
            new(0, -1, -t), new(0, 1, -t), new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
        };
        for (int i = 0; i < v.Count; i++) v[i] = Vector3.Normalize(v[i]);
        var f = new List<int> { 0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11, 1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8,
                                3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9, 4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1 };
        for (int l = 0; l < level; l++)
        {
            var mid = new Dictionary<long, int>();
            int Mid(int a, int b)
            {
                long k = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (mid.TryGetValue(k, out int m)) return m;
                v.Add(Vector3.Normalize(v[a] + v[b])); mid[k] = v.Count - 1; return v.Count - 1;
            }
            var nf = new List<int>(f.Count * 4);
            for (int i = 0; i < f.Count; i += 3)
            {
                int a = f[i], b = f[i + 1], c = f[i + 2], ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                nf.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
            }
            f = nf;
        }
        _ico = v.ToArray(); _icoF = f.ToArray();
        _icoVF = new List<int>[_ico.Length];
        for (int i = 0; i < _ico.Length; i++) _icoVF[i] = new List<int>(6);
        for (int i = 0; i < _icoF.Length; i += 3) { _icoVF[_icoF[i]].Add(i); _icoVF[_icoF[i + 1]].Add(i); _icoVF[_icoF[i + 2]].Add(i); }
        _icoLevel = level;
    }

    /// <summary>The baked directions' triangle around f and its barycentric weights.</summary>
    static void IcoTri(Vector3 f, out int v0, out int v1, out int v2, out float b0, out float b1, out float b2)
    {
        int best = 0; float bd = float.MinValue;
        for (int i = 0; i < _ico.Length; i++) { float d = Vector3.Dot(_ico[i], f); if (d > bd) { bd = d; best = i; } }
        v0 = v1 = v2 = best; b0 = 1f; b1 = b2 = 0f;
        float bestMin = float.MinValue;
        foreach (int fi in _icoVF[best])
        {
            Vector3 A = _ico[_icoF[fi]], B = _ico[_icoF[fi + 1]], C = _ico[_icoF[fi + 2]];
            float det = Vector3.Dot(A, Vector3.Cross(B, C));
            if (MathF.Abs(det) < 1e-9f) continue;
            float x = Vector3.Dot(f, Vector3.Cross(B, C)) / det, y = Vector3.Dot(A, Vector3.Cross(f, C)) / det, z = Vector3.Dot(A, Vector3.Cross(B, f)) / det;
            float mn = MathF.Min(x, MathF.Min(y, z));
            if (mn > bestMin)
            {
                bestMin = mn; float s = x + y + z;
                x = MathF.Max(0f, x / s); y = MathF.Max(0f, y / s); z = MathF.Max(0f, z / s); s = x + y + z;
                v0 = _icoF[fi]; v1 = _icoF[fi + 1]; v2 = _icoF[fi + 2]; b0 = x / s; b1 = y / s; b2 = z / s;
            }
        }
    }

    // ── this field's anchors and bake ──
    private Vector3[] _anc, _ancN; private bool[] _ancHasN; private Vector3[] _ancJit;
    private Vector3 _ancC; private float _ancR;
    private object _ancFor;
    /// <summary>Per baked direction: its entries (slot = anchor x 3 + channel, strength, spray direction); null: not yet.</summary>
    private (int[] slot, float[] w, Vector3[] dir)[] _bake;
    private int _bakedCount => _bd?.Count ?? 0;

    /// <summary>A hull's bake, SHARED: by every field of a hull with the same points (count and checksum) - a field lost
    /// and made again, the aero model handing over a new chunk table, two ships alike - kept for good (it was baked again
    /// from nothing each time: 6 times in one 20 s ramp, 92 s each).</summary>
    private sealed class BakeData
    {
        public Vector3[] Anc, AncN, Jit; public bool[] HasN; public Vector3 C; public float R;
        public (int[] slot, float[] w, Vector3[] dir)[] Bake; public int Count;
        /// <summary>The knobs it was baked with (BakeLevel: Bake's length is that sphere's; BakeSpacing, BakeCell).</summary>
        public int Level; public float Spacing, Cell;
        public bool Current => Level == BakeLevel && Spacing == BakeSpacing && Cell == BakeCell;
    }
    private static readonly Dictionary<(int, long), BakeData> _bakeCache = new();
    private static long _bkWin, _bkUsed;
    public static int BakeCacheMax = 16;
    private BakeData _bd; private (int, long) _bdKey;
    private static (int, long) HullKey(List<Vector3> pts)
    {
        long h = pts.Count;
        foreach (var p in pts) h = h * 31 + (long)MathF.Round(p.X * 4f) * 73856093L ^ (long)MathF.Round(p.Y * 4f) * 19349663L ^ (long)MathF.Round(p.Z * 4f) * 83492791L;
        // (and the bake's knobs: keyed by the hull alone, a BakeLevel change found the old level's bake - its directions
        //  indexed by the new sphere's: IndexOutOfRange, DropAll every frame, no plasma at all, 2026-10-15)
        h = h * 31 + BakeLevel; h = h * 31 + BitConverter.SingleToInt32Bits(BakeSpacing); h = h * 31 + BitConverter.SingleToInt32Bits(BakeCell);
        return (pts.Count, h);
    }
    /// <summary>The rebuild bump the shared bakes were made under (FlightField.Rebuild clears them: the bake's other knobs).</summary>
    private static int _bakeCacheFor;
    /// <summary>(FlightField.Rebuild) this field's bake forgotten - made again, from the shared cache cleared once per bump.</summary>
    private void BakedForget()
    {
        BakedDrop();
        lock (_bakeCache) if (_bakeCacheFor != Rebuild) { _bakeCacheFor = Rebuild; _bakeCache.Clear(); }
        _anc = null; _ancFor = null; _bd = null;
    }
    public static int RecBakes, RecBakeNow;
    private string _bakeWhy;

    private bool BakeAnchors()
    {
        var pts = HullPoints();
        if (pts == null) { _bakeWhy = _hullWhy ?? "no hull"; return false; }
        _ancFor = _server?.FxChunks;
        Ico(BakeLevel);   // (the shared sphere at this level: a cached bake's directions are its)
        var key = HullKey(pts);
        if (_bd != null && key.Equals(_bdKey)) return true;   // (a new chunk table of the same hull: kept)
        BakedDrop();
        BakeData bd;
        lock (_bakeCache)
        {
            if (!_bakeCache.TryGetValue(key, out bd))
            {
                if (_bakeCache.Count >= BakeCacheMax) _bakeCache.Clear();
                bd = NewBake(pts);
                _bakeCache[key] = bd;
            }
        }
        _bd = bd; _bdKey = key;
        _anc = bd.Anc; _ancN = bd.AncN; _ancHasN = bd.HasN; _ancJit = bd.Jit; _ancC = bd.C; _ancR = bd.R; _bake = bd.Bake;
        _slotH = new object[_anc.Length * 3]; _slotDark = new long[_anc.Length * 3]; _slotQ = new Quaternion[_anc.Length * 3];
        _accW = new float[_anc.Length * 3]; _accD = new Vector3[_anc.Length * 3];
        _bakeWhy = null;
        return true;
    }

    private static BakeData NewBake(List<Vector3> pts)
    {
        float s = Math.Max(0.1f, BakeSpacing);
        var vox = new Dictionary<(int, int, int), (Vector3 sum, int n)>();
        foreach (var p in pts)
        {
            var k = ((int)MathF.Floor(p.X / s), (int)MathF.Floor(p.Y / s), (int)MathF.Floor(p.Z / s));
            vox.TryGetValue(k, out var e); vox[k] = (e.sum + p, e.n + 1);
        }
        var bd = new BakeData { Anc = new Vector3[vox.Count] };
        int i = 0; Vector3 c = Vector3.Zero;
        foreach (var e in vox.Values) { bd.Anc[i] = e.sum / e.n; c += bd.Anc[i]; i++; }
        bd.C = c / Math.Max(1, bd.Anc.Length);
        float r = 0f; foreach (var a in bd.Anc) r = MathF.Max(r, (a - bd.C).Length()); bd.R = Math.Max(1f, r);
        bd.AncN = new Vector3[bd.Anc.Length]; bd.HasN = new bool[bd.Anc.Length]; bd.Jit = new Vector3[bd.Anc.Length];
        var rng = new Random(7);
        for (int k = 0; k < bd.Anc.Length; k++) bd.Jit[k] = new Vector3((float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1);
        Ico(BakeLevel);
        bd.Bake = new (int[], float[], Vector3[])[_ico.Length];
        bd.Level = BakeLevel; bd.Spacing = BakeSpacing; bd.Cell = BakeCell;
        return bd;
    }

    private Vector3 AnchorNormal(int k)
    {
        if (_ancHasN[k]) return _ancN[k];
        long tN = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return AnchorNormal0(k); } finally { BkNormal += AeroCost.Ms(tN); }
    }
    private Vector3 AnchorNormal0(int k)
    {
        Vector3 outw = _anc[k] - _ancC; outw = outw.LengthSquared() > 1e-6f ? Vector3.Normalize(outw) : Vector3.UnitY;
        _ancN[k] = HullNormal(_anc[k], outw) ?? outw; _ancHasN[k] = true;
        return _ancN[k];
    }

    /// <summary>One direction baked: the anchors on the outline seen along d, their strengths and spray directions.</summary>
    public static double BkCells, BkChunk, BkLoop, BkNormal, BkPressure;
    public static string BakeProfile => $"bake ms total: cells {BkCells:F0} chunkdrag {BkChunk:F0} outline {BkLoop:F0} (normals {BkNormal:F0}, pressure {BkPressure:F0})";
    private void Bake(int vi)
    {
        long tA = System.Diagnostics.Stopwatch.GetTimestamp();
        Vector3 d = _ico[vi];
        Vector3 a = Vector3.Normalize(Vector3.Cross(d, MathF.Abs(d.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY)), b = Vector3.Cross(d, a);
        float cell = Math.Max(0.1f, BakeSpacing * BakeCell);
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue, zFront = float.MinValue;
        foreach (var p in _anc) { float x = Vector3.Dot(p, a), y = Vector3.Dot(p, b); x0 = MathF.Min(x0, x); y0 = MathF.Min(y0, y); x1 = MathF.Max(x1, x); y1 = MathF.Max(y1, y); zFront = MathF.Max(zFront, Vector3.Dot(p, d)); }
        int W = (int)((x1 - x0) / cell) + 3, H = (int)((y1 - y0) / cell) + 3;
        x0 -= cell; y0 -= cell;
        var cells = new Dictionary<int, List<int>>();
        for (int k = 0; k < _anc.Length; k++)
        {
            int i = (int)((Vector3.Dot(_anc[k], a) - x0) / cell), j = (int)((Vector3.Dot(_anc[k], b) - y0) / cell);
            int key = i * H + j;
            if (!cells.TryGetValue(key, out var l)) cells[key] = l = new List<int>(4);
            l.Add(k);
        }
        // the mask, closed by one cell (anchors BakeSpacing apart leave pinholes that read as outline)
        var occ = new bool[W, H];
        foreach (var key in cells.Keys) occ[key / H, key % H] = true;
        var dil = new bool[W, H];
        for (int i = 0; i < W; i++) for (int j = 0; j < H; j++)
            if (occ[i, j]) for (int di = -1; di <= 1; di++) for (int dj = -1; dj <= 1; dj++) { int ii = i + di, jj = j + dj; if (ii >= 0 && jj >= 0 && ii < W && jj < H) dil[ii, jj] = true; }
        var mask = new bool[W, H];
        for (int i = 0; i < W; i++) for (int j = 0; j < H; j++)
        {
            if (!dil[i, j]) continue;
            bool all = true;
            for (int di = -1; di <= 1 && all; di++) for (int dj = -1; dj <= 1; dj++) { int ii = i + di, jj = j + dj; if (ii < 0 || jj < 0 || ii >= W || jj >= H || !dil[ii, jj]) { all = false; break; } }
            mask[i, j] = all || occ[i, j];
        }
        bool M(int i, int j) => i >= 0 && j >= 0 && i < W && j < H && mask[i, j];
        // the weights' pressure (the chunks' drag along d) and the front's middle (the spray's "outward")
        BkCells += AeroCost.Ms(tA); long tB = System.Diagnostics.Stopwatch.GetTimestamp();
        ChunkDrag(d);
        BkChunk += AeroCost.Ms(tB); long tC = System.Diagnostics.Stopwatch.GetTimestamp();
        Vector3 stag = Vector3.Zero; int sc = 0;
        foreach (var l in cells.Values) { int best = -1; float bz = float.MinValue; foreach (int k in l) { float z = Vector3.Dot(_anc[k], d); if (z > bz) { bz = z; best = k; } } stag += _anc[best]; sc++; }
        stag = sc > 0 ? stag / sc : _ancC; _stagMid = stag;
        float R = 0.5f * MathF.Max(x1 - x0, y1 - y0);
        var slots = new List<int>(); var ws = new List<float>(); var dirs = new List<Vector3>();
        float wMax = 1e-6f; var raw = new List<float>();
        foreach (var kv in cells)
        {
            int i = kv.Key / H, j = kv.Key % H;
            if (M(i - 1, j) && M(i + 1, j) && M(i, j - 1) && M(i, j + 1)) continue;   // (inside: not the outline)
            var l = kv.Value;
            l.Sort((p, q) => Vector3.Dot(_anc[q], d).CompareTo(Vector3.Dot(_anc[p], d)));   // front first
            int front = l[0];
            float zf = Vector3.Dot(_anc[front], d), back = zf;
            for (int m = 1; m < l.Count; m++) { float z = Vector3.Dot(_anc[l[m]], d); if (back - z > ChordGap) break; back = z; }
            float zMid = zf - ChordShift * (zf - back);
            int midK = front; float md = float.MaxValue;
            foreach (int k in l) { float z = Vector3.Dot(_anc[k], d); if (z < back - 1e-3f) continue; float e = MathF.Abs(z - zMid); if (e < md) { md = e; midK = k; } }
            // strength: the old layout's weight (local pressure x the surface's slope), normalised below
            Vector3 n = AnchorNormal(front);
            float cos = MathF.Abs(Vector3.Dot(n, d));
            float w = LocalPressure(_anc[front]) * (1f - CpMix + CpMix * cos * cos);
            bool isFront = zFront - zf < FrontDepth * R;
            slots.Add(front * 3 + (isFront ? 0 : 1)); raw.Add(w); dirs.Add(Spray(front, d, stag, PlasmaDownMix, true));
            Vector3 n2 = AnchorNormal(midK);
            float cos2 = MathF.Abs(Vector3.Dot(n2, d));
            slots.Add(midK * 3 + 2); raw.Add(LocalPressure(_anc[midK]) * (1f - CpMix + CpMix * cos2 * cos2)); dirs.Add(Spray(midK, d, stag, VapourDownMix, false));
            wMax = MathF.Max(wMax, w);
        }
        BkLoop += AeroCost.Ms(tC);
        float rate = BakeSpacing / Math.Max(0.1f, SegLen);   // (per emitter: the old outline's SegLen / LineDensity spacing)
        for (int m = 0; m < raw.Count; m++)
        {
            float w = MathF.Pow(Math.Clamp(raw[m] / wMax, 0f, 1f), WeightGamma);
            ws.Add(w < WeightMin ? 0f : w * rate);
        }
        _bake[vi] = (slots.ToArray(), ws.ToArray(), dirs.ToArray());
        _bd.Count++; RecBakes++;
    }

    /// <summary>The bake the live flow needs now (its triangle: never a frame behind), then the rest, nearest first,
    /// within the budget - on the shared bake (its lock held).</summary>
    private void BakeNeeded(Vector3 f)
    {
        IcoTri(f, out int v0, out int v1, out int v2, out _, out _, out _);
        // the three it needs: now (never a frame behind); then the rest, nearest first, within the budget
        if (_bake[v0].slot == null) { Bake(v0); RecBakeNow++; }
        if (_bake[v1].slot == null) { Bake(v1); RecBakeNow++; }
        if (_bake[v2].slot == null) { Bake(v2); RecBakeNow++; }
        if (_bakedCount < _ico.Length)
        {
            // (the budget is ALL fields' together, per frame: six grids of one ship baking at once each took their own 2 ms -
            //  frames of 17 ms)
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp(), fq = System.Diagnostics.Stopwatch.Frequency, budget;
            lock (_bakeCache)
            {
                if (t0 - _bkWin > fq / 60) { _bkWin = t0; _bkUsed = 0; }
                budget = (long)(fq * BakeBudgetMs / 1000.0) - _bkUsed;
            }
            while (budget > 0 && _bakedCount < _ico.Length && System.Diagnostics.Stopwatch.GetTimestamp() - t0 < budget)
            {
                int next = -1; float nd = float.MinValue;
                for (int i = 0; i < _ico.Length; i++) if (_bake[i].slot == null) { float dd = Vector3.Dot(_ico[i], f); if (dd > nd) { nd = dd; next = i; } }
                if (next < 0) break;
                Bake(next);
            }
            lock (_bakeCache) _bkUsed += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        }
    }

    /// <summary>The spray at anchor k for flow d: downwind (mix) plus along the surface, outward from the front's middle.</summary>
    private Vector3 Spray(int k, Vector3 d, Vector3 stag, float downMix, bool clamp)
    {
        Vector3 rad = _anc[k] - stag; rad -= d * Vector3.Dot(rad, d);
        Vector3 radU = rad.LengthSquared() > 1e-4f ? Vector3.Normalize(rad) : Vector3.Normalize(Vector3.Cross(d, Vector3.UnitY) + new Vector3(1e-3f, 0, 0));
        Vector3 n = AnchorNormal(k);
        Vector3 along = radU - Vector3.Dot(radU, n) * n;
        along -= d * Vector3.Dot(along, d);
        if (along.LengthSquared() > 1f) along = Vector3.Normalize(along);
        return Vector3.Normalize(downMix * -d + (1f - downMix) * along);
    }

    // ── the emitters and the per-frame blend ──
    private object[] _slotH; private long[] _slotDark; private Quaternion[] _slotQ;
    private float[] _accW; private Vector3[] _accD;
    private readonly List<int> _touched = new(); private readonly HashSet<int> _live = new();
    private readonly List<int> _drop = new();   // (the slots leaving _live this frame: one list kept - subsonic, every touched slot left every frame, a fresh list grown each time)
    private Vector3 _bakedSpotMid; private float _bakedSpotFront; private bool _bakedSpotOk;

    /// <summary>Every frame (no easing): the live flow's baked triangle blended onto the emitters.</summary>
    public static double RecBakedMaxMs, RecBakedSumMs; public static int RecBakedFrames;
    private void BakedFrame(Vector3 f, float fdt)
    {
        if (!BakedOn || f == default) return;
        long tf0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try { BakedFrameCore(f, fdt); }
        finally
        {
            double ms = AeroCost.Ms(tf0);
            RecBakedSumMs += ms; RecBakedFrames++; if (ms > RecBakedMaxMs) RecBakedMaxMs = ms;
        }
    }
    private void BakedFrameCore(Vector3 f, float fdt)
    {
        if (_anc == null || !ReferenceEquals(_ancFor, _server?.FxChunks) || !_bd.Current) { if (!BakeAnchors()) return; }
        lock (_bd) BakeNeeded(f);
        IcoTri(f, out int v0, out int v1, out int v2, out float b0, out float b1, out float b2);
        // blend
        foreach (int s in _touched) { _accW[s] = 0f; _accD[s] = Vector3.Zero; }
        _touched.Clear();
        void Add(int vi, float bw)
        {
            if (bw <= 0f) return;
            var (sl, w, dr) = _bake[vi];
            for (int m = 0; m < sl.Length; m++)
            {
                int s = sl[m];
                if (_accW[s] == 0f && _accD[s] == Vector3.Zero) _touched.Add(s);
                _accW[s] += bw * w[m]; _accD[s] += bw * dr[m];
            }
        }
        Add(v0, b0); Add(v1, b1); Add(v2, b2);
        // the live lift (the vapour's suction side), per frame
        Vector3 L = LiftTest > 0f ? Vector3.UnitY - f * Vector3.Dot(Vector3.UnitY, f) : _liftDir;
        float coef = LiftTest > 0f ? LiftTest : _liftCoef;
        bool suck = L.LengthSquared() > 1e-6f && coef > 0f; if (suck) L = Vector3.Normalize(L);
        float kS = Math.Min(1f, LiftSens * coef);
        long now = System.Diagnostics.Stopwatch.GetTimestamp(); long idle = (long)(System.Diagnostics.Stopwatch.Frequency * BakeIdle);
        float jt = MathF.Tan(PlasmaJitter * MathF.PI / 180f), jtV = MathF.Tan(VapourJitter * MathF.PI / 180f);
        Vector3 spotSum = Vector3.Zero; float spotW = 0f, spotFront = float.MinValue;
        foreach (int s in _touched) _live.Add(s);
        var drop = _drop; drop.Clear();
        foreach (int s in _live)
        {
            int k = s / 3, ch = s % 3;
            float w = _accW[s];
            if (ch == 2 && suck && w > 0f) w *= (1f - kS) + kS * Math.Clamp(0.5f + 1.5f * Vector3.Dot(AnchorNormal(k), L), 0f, 1f);
            float str = ch < 2 ? (PlasmaOn ? _entryS * w : 0f) : (VapourOn ? _edgeI * w * VapourGain : 0f);
            if (ch < 2 && w > 0f) { Vector3 p = _anc[k]; spotSum += (p - f * Vector3.Dot(p, f)) * w; spotW += w; spotFront = MathF.Max(spotFront, Vector3.Dot(p, f)); }
            var h = _slotH[s];
            if (str > 0.002f) _slotDark[s] = 0;
            else if (_slotDark[s] == 0) _slotDark[s] = now;
            if (h == null)
            {
                if (str <= 0.002f) { drop.Add(s); continue; }
                h = SpawnSlot(s, f);
                if (h == null) { drop.Add(s); continue; }
                _slotH[s] = h;
            }
            else if (_slotDark[s] != 0 && now - _slotDark[s] > idle)
            {
                Park(h); _slotH[s] = null; drop.Add(s); continue;
            }
            if (w > 0f && _accD[s].LengthSquared() > 1e-8f)
            {
                Vector3 dir = Vector3.Normalize(_accD[s]);
                Vector3 r = _ancJit[k]; r -= dir * Vector3.Dot(r, dir);
                dir = Vector3.Normalize(dir + r * (ch < 2 ? jt : jtV));
                var q = Quaternion.CreateFromTwoVectors(Vector3.UnitZ, dir);
                if (MathF.Abs(Quaternion.Dot(q, _slotQ[s])) < 0.999996f)   // (moved over ~0.3 deg)
                {
                    try { (_updTransform ??= h.GetType().GetMethod("UpdateTransform", AnyInst, null, new[] { typeof(RelativeTransform) }, null)).Invoke(h, new object[] { new RelativeTransform(SlotAt(s), q) }); _slotQ[s] = q; }
                    catch (Exception e) { _puffWhy = "baked move: " + Inner(e); }
                }
            }
            FixTime(h, Math.Clamp(str, 0.00001f, 0.999f), FixTimeFine);
        }
        foreach (int s in drop) _live.Remove(s);
        drop.Clear();
        _bakedSpotOk = spotW > 0f; if (_bakedSpotOk) { _bakedSpotMid = spotSum / spotW; _bakedSpotFront = spotFront; }
    }

    private Vector3 SlotAt(int s)
    {
        int k = s / 3; var n = AnchorNormal(k);
        return s % 3 == 2 ? _anc[k] + n * SnapLift : _anc[k] + n * (SurfaceOff * _ancR);
    }

    private object SpawnSlot(int s, Vector3 f)
    {
        int ch = s % 3;
        Guid e = ch == 2 ? (EdgeCollide == 0 ? new Guid("5d0c7a3e-0105-4000-8000-000000000001") : VapourEdge) : (Recipe ? RecipeGuid(ch == 1) : RateGuid(TexKind(ch == 1), StreakField));
        void FillVapour(object prm)
        {
            var t = prm.GetType();
            if (VapourSpawn > 0f) t.GetField("EmitterSizeMultiplier").SetValue(prm, VapourSpawn);
            t.GetField("EmitterScaleMultiplier").SetValue(prm, Math.Max(0.1f, VapourEdgeScale));
            t.GetField("VelocityMultiplier").SetValue(prm, Math.Max(0.01f, VapourSpeed));
        }
        var q = Quaternion.CreateFromTwoVectors(Vector3.UnitZ, -f);
        _spawnTip = true;   // (not swung: the bake places it)
        object h;
        try { h = SpawnEffect(_client, e, new RelativeTransform(SlotAt(s), q), out object prm, out _puffWhy, ch == 2 ? FillVapour : FillSparks); if (h != null && prm != null) SetParamsIfChanged(h, prm); }
        finally { _spawnTip = false; }
        if (h == null) return null;
        _slotQ[s] = q;
        FixTime(h, 0.00001f, 0f);   // (born dark: its strength comes in from 0)
        return h;
    }

    /// <summary>The baked emitters parked (the field over, or the hull changed).</summary>
    private void BakedDrop()
    {
        if (_slotH != null) for (int s = 0; s < _slotH.Length; s++) if (_slotH[s] != null) { Park(_slotH[s]); _slotH[s] = null; }
        _live.Clear(); _touched.Clear();
    }

    public string BakedStatus => _anc == null ? "baked: " + (_bakeWhy ?? "not started") : $"baked: {_anc.Length} anchors, {_bakedCount}/{_ico.Length} directions, {_live.Count} live";
}
