#pragma warning disable
using System;

namespace AeroMod;

/// <summary>The face model's per-face terms, for unit dynamic pressure (as DampedShadowedDragModel.SumFaces).</summary>
public readonly struct FacePhysics
{
    public readonly float CdBluff, CpMax, Cf, St, BaseCp;

    public FacePhysics(DampedShadowedDragModel m)
    {
        CdBluff = m.CdBluff; CpMax = m.CpMax; Cf = m.CfSkin; St = m.Streamlining;
        BaseCp = m.Streamlining > 0f ? m.CpBase * (1f - 0.7f * m.Streamlining) : m.CpBase;
    }

    /// <summary>A face (normal n, centre p, reached area a) with the grid moving along v, into acc[o..o+13):
    /// F0 F1 T0 T1 frontal.</summary>
    public void Add(Vector3 v, Vector3 n, Vector3 p, float a, double[] acc, int o)
    {
        float cosA = v.X * n.X + v.Y * n.Y + v.Z * n.Z;
        float cpSub, cpSup;
        if (cosA > 0)
        {
            cpSub = CdBluff * cosA; cpSup = CpMax * cosA * cosA;
            if (St > 0f) { float m = 1f - St * (1f - (0.08f + 0.92f * cosA * cosA)); cpSub *= m; cpSup *= m; }
            acc[o + 12] += a * cosA;
        }
        else
        {
            float ramp = MathF.Min(1f, -cosA * 2f); ramp = ramp * ramp * (3f - 2f * ramp);
            cpSub = cpSup = BaseCp * ramp;
        }
        float sx = 0, sy = 0, sz = 0;
        float sinSq = 1f - cosA * cosA;
        if (Cf > 0 && sinSq > 1e-12f)
        {
            float fric = Cf * a / MathF.Sqrt(sinSq);
            sx = (v.X - cosA * n.X) * fric; sy = (v.Y - cosA * n.Y) * fric; sz = (v.Z - cosA * n.Z) * fric;
        }
        float ax = -cpSub * a * n.X + sx, ay = -cpSub * a * n.Y + sy, az = -cpSub * a * n.Z + sz;
        float bx = -cpSup * a * n.X + sx, by = -cpSup * a * n.Y + sy, bz = -cpSup * a * n.Z + sz;
        acc[o] += ax; acc[o + 1] += ay; acc[o + 2] += az; acc[o + 3] += bx; acc[o + 4] += by; acc[o + 5] += bz;
        acc[o + 6] += p.Y * az - p.Z * ay; acc[o + 7] += p.Z * ax - p.X * az; acc[o + 8] += p.X * ay - p.Y * ax;
        acc[o + 9] += p.Y * bz - p.Z * by; acc[o + 10] += p.Z * bx - p.X * bz; acc[o + 11] += p.X * by - p.Y * bx;
    }
}

/// <summary>
/// A grid's force table split into chunks of space (ChunkSize metres a side): each chunk keeps its own faces' share
/// of every direction, and its faces' centres (they hide what is behind them). Damage then recomputes only the chunks
/// it touched - from a small surface built around them, against every chunk's stored faces for what the air reaches -
/// instead of the whole grid (Red Ship: ~2 s -> tens of ms). The total is the sum of the chunks. What a hole newly
/// uncovers in other chunks downstream waits for the next full build.
/// </summary>
public sealed class ChunkedTable
{
    public readonly int N;
    public readonly float ChunkSize;
    public readonly FacePhysics Phys;
    readonly Dictionary<long, int> _index = new(LongKey.Comparer);
    // Per chunk, its share of every direction ([slot * 13 + k]), kept in 8 bits with a scale per component (13): a
    // share only feeds deltas against the full-precision total, so its rounding (0.4% of the chunk's own largest
    // value) is small beside the grid's; it is a quarter of the memory (Red Ship: ~8 MB -> ~2 MB). Null: zero.
    readonly List<sbyte[]> _q = new();
    readonly List<float[]> _scale = new();
    readonly List<float[]> _raw = new();     // (during a build: plain, until Seal)
    /// <summary>Per chunk: its faces' centres, grid-local, in eighths of a metre (16 bits each: face centres sit on
    /// a 0.125 m lattice, within +-4 km).</summary>
    public readonly List<List<P16>> Occluders = new();
    public readonly struct P16
    {
        public readonly short X, Y, Z;
        public P16(Vector3 p) { X = (short)MathF.Round(p.X * 8f); Y = (short)MathF.Round(p.Y * 8f); Z = (short)MathF.Round(p.Z * 8f); }
        public Vector3 V => new Vector3(X, Y, Z) * 0.125f;
    }

    /// <summary>During a build: chunk c's share, plain (Seal stores them all).</summary>
    public float[] RawShare(int c) => _raw[c] ??= new float[Slots * ForceTable.Stride];

    /// <summary>After a build: every share into its 8-bit form.</summary>
    public void Seal()
    {
        for (int c = 0; c < _raw.Count; c++) { if (_raw[c] != null) SetShare(c, _raw[c]); _raw[c] = null; }
    }

    /// <summary>Chunk c's share, decoded (a new array).</summary>
    public float[] GetShare(int c)
    {
        var r = new float[Slots * ForceTable.Stride];
        var q = _q[c]; var sc = _scale[c];
        if (q == null) return r;
        for (int i = 0; i < r.Length; i++) r[i] = q[i] * sc[i % ForceTable.Stride];
        return r;
    }

    public void SetShare(int c, float[] share)
    {
        const int K = ForceTable.Stride;
        var sc = new float[K];
        for (int i = 0; i < share.Length; i++) { float a = MathF.Abs(share[i]); if (a > sc[i % K]) sc[i % K] = a; }
        bool any = false;
        for (int k = 0; k < K; k++) { if (sc[k] > 0) any = true; sc[k] /= 127f; }
        if (!any) { _q[c] = null; _scale[c] = null; return; }
        var q = new sbyte[share.Length];
        for (int i = 0; i < share.Length; i++) { float s = sc[i % K]; q[i] = s > 0 ? (sbyte)Math.Clamp((int)MathF.Round(share[i] / s), -127, 127) : (sbyte)0; }
        _q[c] = q; _scale[c] = sc;
    }

    public bool HasShare(int c) => _q[c] != null;

    /// <summary>Bytes held (shares and occluders).</summary>
    public long Bytes()
    {
        long b = 0;
        for (int c = 0; c < _q.Count; c++) { if (_q[c] != null) b += _q[c].Length + 13 * 4; b += Occluders[c].Count * 6L; }
        return b;
    }
    /// <summary>Faces another model owns (wing skins: the wing model carries them), by cell and side: left out of
    /// local updates as the full build leaves them out.</summary>
    public readonly HashSet<long> ExcludedFaces = new(LongKey.Comparer);
    /// <summary>The full build's interior faces (rooms, not hull), by cell and side: left out of local updates too
    /// (the local surface has no hull classification; faces made by the damage are not in it).</summary>
    public readonly HashSet<long> CavityFaces = new(LongKey.Comparer);
    public static long FaceKey(Vector3I c, int dir) => ((long)(c.X & 0xFFFF) << 36) | ((long)(c.Y & 0xFFFF) << 20) | ((long)(c.Z & 0xFFFF) << 4) | (long)(dir & 0xF);
    /// <summary>Per chunk: the box of its face centres (per direction, chunks out of line with the damage are skipped).</summary>
    public readonly List<(Vector3 lo, Vector3 hi)> Bounds = new();
    public int Slots => 6 * (N + 1) * (N + 1);
    public int ChunkCount => _q.Count;
    public double LastUpdateMs;
    public string LastProfile = "";

    public ChunkedTable(int n, float chunkSize, FacePhysics phys) { N = n; ChunkSize = chunkSize; Phys = phys; }

    static int Fl(float x) => (int)MathF.Floor(x);
    public long KeyOf(Vector3 p)
    {
        int x = Fl(p.X / ChunkSize), y = Fl(p.Y / ChunkSize), z = Fl(p.Z / ChunkSize);
        return ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
    }

    /// <summary>The chunk's index (made if new).</summary>
    public int IndexOf(long key)
    {
        if (_index.TryGetValue(key, out int i)) return i;
        i = _q.Count;
        _index[key] = i;
        _q.Add(null); _scale.Add(null); _raw.Add(null);
        Occluders.Add(new List<P16>());
        Bounds.Add((new Vector3(float.MaxValue), new Vector3(float.MinValue)));
        return i;
    }

    public bool TryIndex(long key, out int i) => _index.TryGetValue(key, out i);

    /// <summary>A face centre among a chunk's occluders.</summary>
    public void AddOccluder(int ci, Vector3 p)
    {
        Occluders[ci].Add(new P16(p));
        var (lo, hi) = Bounds[ci];
        Bounds[ci] = (Vector3.Min(lo, p), Vector3.Max(hi, p));
    }

    /// <summary>
    /// The damaged shape's faces in the dirty chunks: a surface built over them and a margin (its cut edges fall in
    /// the margin and are dropped, as are faces outside the dirty chunks and wing-owned ones). `region` holds the
    /// grid's cells (at the surface's own scale) around the dirty chunks.
    /// </summary>
    public List<SurfaceFace> FacesFor(IGridAccessor region, HashSet<long> dirty, float cellSize, float cellOffset, float blockSize, List<Vector3> occludeOnly = null)
    {
        var surf = new SmoothSurfaceProvider { CellSize = cellSize, CellOffset = cellOffset };
        surf.Build(region, blockSize);
        var r = new List<SurfaceFace>();
        for (int i = 0; i < surf.FaceCount; i++)
        {
            var f = surf.Faces[i];
            if (!dirty.Contains(KeyOf(f.Position))) continue;
            surf.GetFaceCellDir(i, out var c, out int d);
            long fk = FaceKey(c, d);
            if (CavityFaces.Contains(fk)) continue;
            if (ExcludedFaces.Contains(fk)) { occludeOnly?.Add(f.Position); continue; }   // (a wing: hides, carried by the wing model)
            r.Add(f);
        }
        return r;
    }

    /// <summary>The chunks a box of grid-local space (metres) reaches.</summary>
    public void KeysIn(Vector3 lo, Vector3 hi, HashSet<long> into)
    {
        int x0 = Fl(lo.X / ChunkSize), x1 = Fl(hi.X / ChunkSize), y0 = Fl(lo.Y / ChunkSize), y1 = Fl(hi.Y / ChunkSize), z0 = Fl(lo.Z / ChunkSize), z1 = Fl(hi.Z / ChunkSize);
        for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) for (int z = z0; z <= z1; z++)
            into.Add(((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF));
    }

    /// <summary>How far upstream (along v) a box reaches.</summary>
    static float Upstream(Vector3 lo, Vector3 hi, Vector3 v)
        => (v.X > 0 ? hi.X : lo.X) * v.X + (v.Y > 0 ? hi.Y : lo.Y) * v.Y + (v.Z > 0 ? hi.Z : lo.Z) * v.Z;

    /// <summary>A box's extent across a flow (on e1, e2).</summary>
    static void Proj(Vector3 lo, Vector3 hi, Vector3 e1, Vector3 e2, out float u0, out float u1, out float w0, out float w1)
    {
        var c = (lo + hi) * 0.5f; var h = (hi - lo) * 0.5f;
        float cu = Vector3.Dot(c, e1), cw = Vector3.Dot(c, e2);
        float ru = MathF.Abs(h.X * e1.X) + MathF.Abs(h.Y * e1.Y) + MathF.Abs(h.Z * e1.Z);
        float rw = MathF.Abs(h.X * e2.X) + MathF.Abs(h.Y * e2.Y) + MathF.Abs(h.Z * e2.Z);
        u0 = cu - ru; u1 = cu + ru; w0 = cw - rw; w1 = cw + rw;
    }

    /// <summary>A chunk key's box (metres).</summary>
    public (Vector3 lo, Vector3 hi) BoxOf(long key)
    {
        int Un(long f) => (int)(((f & 0x1FFFFF) << 43) >> 43);
        int x = Un(key >> 42), y = Un(key >> 21), z = Un(key);
        var lo = new Vector3(x, y, z) * ChunkSize;
        return (lo, lo + new Vector3(ChunkSize));
    }

    /// <summary>
    /// The chunks in `dirty` recomputed from `faces` (their new faces: those whose centre lies in them), for every
    /// direction, the air reaching them past every chunk's stored faces; a new total table (the old one's rotation map
    /// kept) with the dirty chunks' old shares out and the new in. Thread-safe against readers of `current` (it is
    /// not touched); not against another update of this ChunkedTable.
    /// </summary>
    /// <summary>The new shares of some chunks: computed in the background, applied on the simulation thread.</summary>
    public sealed class LocalResult
    {
        public List<int> Chunks = new();
        public List<float[]> Shares = new();
        public List<List<Vector3>> Occluders = new();
    }

    /// <summary>Make the dirty chunks' indices (the simulation thread, before a background ComputeLocal).</summary>
    public void Prepare(HashSet<long> dirty) { foreach (var k in dirty) IndexOf(k); }

    /// <summary>Background: the dirty chunks' new shares (Prepare first). Reads the chunks; changes nothing.</summary>
    public LocalResult ComputeLocal(HashSet<long> dirty, List<SurfaceFace> faces, int threads = 3, List<Vector3> occludeOnly = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var dirtyIdx = new List<int>();
        foreach (var k in dirty) if (_index.TryGetValue(k, out int di)) dirtyIdx.Add(di);
        var isDirty = new bool[_q.Count];
        foreach (int i in dirtyIdx) isDirty[i] = true;
        // the new faces, by chunk
        var fp = new List<Vector3>(); var fn = new List<Vector3>(); var fa = new List<float>(); var fc = new List<int>();
        foreach (var f in faces)
        {
            if (!_index.TryGetValue(KeyOf(f.Position), out int ci) || ci >= isDirty.Length || !isDirty[ci]) continue;
            fp.Add(f.Position); fn.Add(f.Normal); fa.Add(f.Area); fc.Add(ci);
        }
        // what hides: the other chunks' faces (per direction only those in line with the new faces), and the new ones
        var newLo = new Vector3(float.MaxValue); var newHi = new Vector3(float.MinValue);
        foreach (var p in fp) { newLo = Vector3.Min(newLo, p); newHi = Vector3.Max(newHi, p); }
        // faces that hide but carry no force here (wing skins), and their chunks
        var hideOnly = new List<Vector3>(); var hideChunk = new List<int>();
        if (occludeOnly != null)
            foreach (var p in occludeOnly)
                if (_index.TryGetValue(KeyOf(p), out int hc) && hc < isDirty.Length && isDirty[hc]) { hideOnly.Add(p); hideChunk.Add(hc); }

        // the dirty chunks' new shares, direction by direction (workers take directions in turn)
        int slots = Slots, nd = dirtyIdx.Count;
        var slotOf = new Dictionary<int, int>(); for (int d = 0; d < nd; d++) slotOf[dirtyIdx[d]] = d;
        var fresh = new List<float[]>(); for (int d = 0; d < nd; d++) fresh.Add(new float[slots * ForceTable.Stride]);
        int next = -1;
        void Worker()
        {
            float[] z = System.Array.Empty<float>();
            var acc = new double[nd * ForceTable.Stride];
            int s;
            while ((s = System.Threading.Interlocked.Increment(ref next)) < slots)
            {
                int face = s / ((N + 1) * (N + 1)), r = s % ((N + 1) * (N + 1));
                var v = ForceTable.Direction(face, r / (N + 1), r % (N + 1), N);
                System.Array.Clear(acc, 0, acc.Length);
                var seed = MathF.Abs(v.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
                var e1 = Vector3.Normalize(Vector3.Cross(v, seed)); var e2 = Vector3.Cross(v, e1);
                const float Pix = 0.5f, Tol = 0.4f;
                if (fp.Count == 0) continue;
                float newMinDepth = float.MaxValue;
                foreach (var p in fp) { float d = Vector3.Dot(p, v); if (d < newMinDepth) newMinDepth = d; }
                // the new faces' footprint across the flow: the depth buffer covers just that
                Proj(newLo, newHi, e1, e2, out float umin, out float umax, out float wmin, out float wmax);
                umin -= Pix; wmin -= Pix; umax += Pix; wmax += Pix;
                umin = MathF.Floor(umin / Pix) * Pix; wmin = MathF.Floor(wmin / Pix) * Pix;   // (the full build's lattice)
                int W = (int)((umax - umin) / Pix) + 1, H = (int)((wmax - wmin) / Pix) + 1;
                if (z.Length < W * H) z = new float[W * H];
                System.Array.Fill(z, float.MinValue, 0, W * H);
                void Raster(Vector3 p)
                {
                    float u = Vector3.Dot(p, e1) - umin, w = Vector3.Dot(p, e2) - wmin;
                    if (u < 0 || w < 0) return;
                    int iu = (int)(u / Pix), iw = (int)(w / Pix);
                    if (iu >= W || iw >= H) return;
                    float d = Vector3.Dot(p, v);
                    ref float zz = ref z[iw * W + iu];
                    if (d > zz) zz = d;
                }
                for (int c = 0; c < Occluders.Count; c++)
                {
                    if (isDirty[c]) continue;
                    var (blo, bhi) = Bounds[c];
                    if (blo.X > bhi.X) continue;
                    Proj(blo, bhi, e1, e2, out float cu0, out float cu1, out float cw0, out float cw1);
                    if (cu1 < umin || cu0 > umax || cw1 < wmin || cw0 > wmax) continue;   // not in line
                    if (Upstream(blo, bhi, v) < newMinDepth - Tol) continue;               // wholly downstream: hides none of them
                    foreach (var p in Occluders[c]) Raster(p.V);
                }
                foreach (var p in fp) Raster(p);
                foreach (var p in hideOnly) Raster(p);
                for (int i = 0; i < fp.Count; i++)
                {
                    var p = fp[i];
                    int iu = (int)((Vector3.Dot(p, e1) - umin) / Pix), iw = (int)((Vector3.Dot(p, e2) - wmin) / Pix);
                    if (iu < 0 || iw < 0 || iu >= W || iw >= H) continue;
                    if (Vector3.Dot(p, v) < z[iw * W + iu] - Tol) continue;   // hidden
                    Phys.Add(v, fn[i], p, fa[i], acc, slotOf[fc[i]] * ForceTable.Stride);
                }
                for (int d = 0; d < nd; d++)
                    for (int k = 0; k < ForceTable.Stride; k++)
                        fresh[d][s * ForceTable.Stride + k] = (float)acc[d * ForceTable.Stride + k];
            }
        }
        double tSetup = sw.Elapsed.TotalMilliseconds;
        var helpers = new List<System.Threading.Tasks.Task>();
        for (int t = 1; t < threads; t++) helpers.Add(System.Threading.Tasks.Task.Factory.StartNew(Worker));
        Worker();
        foreach (var h in helpers) h.Wait();

        double tWork = sw.Elapsed.TotalMilliseconds;
        var result = new LocalResult();
        for (int d = 0; d < nd; d++) { result.Chunks.Add(dirtyIdx[d]); result.Shares.Add(fresh[d]); result.Occluders.Add(new List<Vector3>()); }
        for (int i = 0; i < fp.Count; i++) result.Occluders[slotOf[fc[i]]].Add(fp[i]);
        for (int i = 0; i < hideOnly.Count; i++) result.Occluders[slotOf[hideChunk[i]]].Add(hideOnly[i]);
        LastUpdateMs = sw.Elapsed.TotalMilliseconds;
        LastProfile = $"setup {tSetup:F1} ms, directions {tWork - tSetup:F1} ms ({fp.Count} new faces, {nd} chunks)";
        return result;
    }

    /// <summary>Simulation thread: a background result into the live table - the chunks' shares as they are now
    /// out (a chunk dropped meanwhile is already out: zero), the new in.</summary>
    public ForceTable Apply(LocalResult r, ForceTable live)
    {
        var table = live.Clone();
        int slots = Slots;
        for (int d = 0; d < r.Chunks.Count; d++)
        {
            int c = r.Chunks[d];
            var old = GetShare(c); var nw = r.Shares[d];
            for (int sl = 0; sl < slots; sl++)
            {
                int face = sl / ((N + 1) * (N + 1)), rr = sl % ((N + 1) * (N + 1));
                var e = table.At(face, rr / (N + 1), rr % (N + 1));
                for (int k = 0; k < ForceTable.Stride; k++) e[k] += nw[sl * ForceTable.Stride + k] - old[sl * ForceTable.Stride + k];
            }
            SetShare(c, nw);
            Occluders[c].Clear(); Bounds[c] = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
            foreach (var p in r.Occluders[d]) AddOccluder(c, p);
            foreach (var kv in _index) if (kv.Value == c) { _shareCount[kv.Key] = BlockCount.TryGetValue(kv.Key, out int bc) ? bc : 0; _hullBase.Remove(kv.Key); _hullGone.Remove(kv.Key); break; }
        }
        return table;
    }

    // -- Whole chunks gone (an impact): out of the live table the same frame --
    /// <summary>Blocks per chunk (by block centre); a chunk that empties is dropped at once (DropEmptied).</summary>
    public readonly Dictionary<long, int> BlockCount = new(LongKey.Comparer);
    /// <summary>Per chunk: the block count its share was computed for (a partly destroyed chunk is scaled to what
    /// is left the same frame, until its local update).</summary>
    readonly Dictionary<long, int> _shareCount = new(LongKey.Comparer);
    readonly List<int> _pendingOccClear = new();
    // hull faces each share stands for, and how many of them removed blocks took (a chunk's loss is measured in its
    // hull, not its blocks: a hollow ship's interior blocks carry no force)
    readonly Dictionary<long, int> _hullBase = new(LongKey.Comparer), _hullGone = new(LongKey.Comparer);

    /// <summary>A removed block (box of grid cells): the hull faces of its chunks inside it are gone.</summary>
    public void RemoveHullIn(Vector3I min, Vector3I max, float faceReach = 0.13f)
    {
        // (grid cells are centred at cell x 0.25; a block's faces lie within half a surface cell of its cells)
        var lo = new Vector3(min.X, min.Y, min.Z) * 0.25f - new Vector3(faceReach); var hi = new Vector3(max.X, max.Y, max.Z) * 0.25f + new Vector3(faceReach);
        _tmpKeys.Clear();
        KeysIn(lo, hi, _tmpKeys);
        foreach (var key in _tmpKeys)
        {
            if (!_index.TryGetValue(key, out int c)) continue;
            int n = 0;
            foreach (var q in Occluders[c]) { var p = q.V; if (p.X >= lo.X && p.X <= hi.X && p.Y >= lo.Y && p.Y <= hi.Y && p.Z >= lo.Z && p.Z <= hi.Z) n++; }
            if (n == 0) continue;
            if (!_hullBase.ContainsKey(key)) _hullBase[key] = Occluders[c].Count;
            _hullGone.TryGetValue(key, out int g); _hullGone[key] = g + n;
        }
    }
    readonly HashSet<long> _tmpKeys = new(LongKey.Comparer);

    public void CountBlock(long key, int delta)
    {
        BlockCount.TryGetValue(key, out int n);
        BlockCount[key] = Math.Max(0, n + delta);
    }

    /// <summary>After the full build's counting: every chunk's share stands for its blocks as they are.</summary>
    public void SealCounts() { _shareCount.Clear(); foreach (var kv in BlockCount) _shareCount[kv.Key] = kv.Value; }

    /// <summary>Simulation thread: the chunks among `keys` with no blocks left, out of the live table (one copy for
    /// all). Their shares become zero; their faces stop hiding anything once no background update reads them
    /// (`busy`: one is running; ClearPending after it).</summary>
    public ForceTable DropEmptied(IEnumerable<long> keys, ForceTable live, bool busy, out int dropped, List<(long key, float[] share)> removedShares = null)
    {
        dropped = 0;
        ForceTable table = null;
        int slots = Slots;
        foreach (var key in keys)
        {
            BlockCount.TryGetValue(key, out int n);
            if (!_index.TryGetValue(key, out int c)) continue;
            int was = _shareCount.TryGetValue(key, out int w) ? w : n;
            _hullGone.TryGetValue(key, out int hg);
            if (n > 0 && n >= was && hg == 0) continue;
            // emptied: out; partly: scaled to the hull left (else the blocks left); the local update brings the faces
            float keep;
            if (n <= 0) keep = 0f;
            else if (_hullBase.TryGetValue(key, out int hb) && hb > 0) keep = MathF.Max(0f, 1f - hg / (float)hb);
            else keep = was <= 0 ? 0f : n / (float)was;
            if (_hullBase.TryGetValue(key, out int hb2)) _hullBase[key] = Math.Max(0, hb2 - hg);
            _hullGone[key] = 0;
            if (!HasShare(c)) { _shareCount[key] = n; continue; }
            var old = GetShare(c);
            table ??= live.Clone();
            var scaled = new float[slots * ForceTable.Stride];
            float[] gone = removedShares != null ? new float[slots * ForceTable.Stride] : null;
            for (int sl = 0; sl < slots; sl++)
            {
                int face = sl / ((N + 1) * (N + 1)), rr = sl % ((N + 1) * (N + 1));
                var e = table.At(face, rr / (N + 1), rr % (N + 1));
                for (int k = 0; k < ForceTable.Stride; k++)
                {
                    float o = old[sl * ForceTable.Stride + k];
                    e[k] -= o * (1f - keep);
                    scaled[sl * ForceTable.Stride + k] = o * keep;
                    if (gone != null) gone[sl * ForceTable.Stride + k] = o * (1f - keep);
                }
            }
            SetShare(c, scaled);
            _shareCount[key] = n;
            removedShares?.Add((key, gone));
            if (n <= 0)
            {
                if (busy) _pendingOccClear.Add(c); else { Occluders[c].Clear(); Bounds[c] = (new Vector3(float.MaxValue), new Vector3(float.MinValue)); }
                dropped++;
            }
        }
        return table ?? live;
    }

    /// <summary>The dropped chunks' faces stop hiding (call when no background update is running).</summary>
    public void ClearPending()
    {
        foreach (int c in _pendingOccClear) { Occluders[c].Clear(); Bounds[c] = (new Vector3(float.MaxValue), new Vector3(float.MinValue)); }
        _pendingOccClear.Clear();
    }
}
