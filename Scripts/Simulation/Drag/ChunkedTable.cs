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
    /// <summary>Per chunk: [slot * 13 + k], slot = a direction of the table.</summary>
    public readonly List<float[]> Entries = new();
    /// <summary>Per chunk: its faces' centres (grid-local).</summary>
    public readonly List<List<Vector3>> Occluders = new();
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
    public int ChunkCount => Entries.Count;
    public double LastUpdateMs;

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
        i = Entries.Count;
        _index[key] = i;
        Entries.Add(new float[Slots * ForceTable.Stride]);
        Occluders.Add(new List<Vector3>());
        Bounds.Add((new Vector3(float.MaxValue), new Vector3(float.MinValue)));
        return i;
    }

    public bool TryIndex(long key, out int i) => _index.TryGetValue(key, out i);

    /// <summary>A face centre among a chunk's occluders.</summary>
    public void AddOccluder(int ci, Vector3 p)
    {
        Occluders[ci].Add(p);
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
    public ForceTable UpdateLocal(ForceTable current, HashSet<long> dirty, List<SurfaceFace> faces, int threads = 3, List<Vector3> occludeOnly = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var dirtyIdx = new List<int>();
        foreach (var k in dirty) dirtyIdx.Add(IndexOf(k));
        var isDirty = new bool[Entries.Count];
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
                // the new faces' footprint across the flow: the depth buffer covers just that
                Proj(newLo, newHi, e1, e2, out float umin, out float umax, out float wmin, out float wmax);
                umin -= Pix; wmin -= Pix; umax += Pix; wmax += Pix;
                umin = MathF.Floor(umin / Pix) * Pix; wmin = MathF.Floor(wmin / Pix) * Pix;   // (the full build's lattice)
                int W = (int)((umax - umin) / Pix) + 1, H = (int)((wmax - wmin) / Pix) + 1;
                if (z.Length < W * H) z = new float[W * H];
                System.Array.Fill(z, float.MinValue, 0, W * H);
                for (int c = 0; c <= Occluders.Count + 1; c++)
                {
                    List<Vector3> pts;
                    if (c == Occluders.Count) pts = fp;
                    else if (c == Occluders.Count + 1) pts = hideOnly;
                    else
                    {
                        if (isDirty[c]) continue;
                        var (blo, bhi) = Bounds[c];
                        if (blo.X > bhi.X) continue;
                        Proj(blo, bhi, e1, e2, out float cu0, out float cu1, out float cw0, out float cw1);
                        if (cu1 < umin || cu0 > umax || cw1 < wmin || cw0 > wmax) continue;   // not in line
                        pts = Occluders[c];
                    }
                    foreach (var p in pts)
                    {
                        float u = Vector3.Dot(p, e1) - umin, w = Vector3.Dot(p, e2) - wmin;
                        if (u < 0 || w < 0) continue;
                        int iu = (int)(u / Pix), iw = (int)(w / Pix);
                        if (iu >= W || iw >= H) continue;
                        float d = Vector3.Dot(p, v);
                        ref float zz = ref z[iw * W + iu];
                        if (d > zz) zz = d;
                    }
                }
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
        var helpers = new List<System.Threading.Tasks.Task>();
        for (int t = 1; t < threads; t++) helpers.Add(System.Threading.Tasks.Task.Factory.StartNew(Worker));
        Worker();
        foreach (var h in helpers) h.Wait();

        // the total: old shares out, new in
        var table = current.Clone();
        for (int d = 0; d < nd; d++)
        {
            var old = Entries[dirtyIdx[d]]; var nw = fresh[d];
            for (int s = 0; s < slots; s++)
            {
                int face = s / ((N + 1) * (N + 1)), r = s % ((N + 1) * (N + 1));
                var e = table.At(face, r / (N + 1), r % (N + 1));
                for (int k = 0; k < ForceTable.Stride; k++) e[k] += nw[s * ForceTable.Stride + k] - old[s * ForceTable.Stride + k];
            }
            Entries[dirtyIdx[d]] = nw;
            Occluders[dirtyIdx[d]].Clear();
            Bounds[dirtyIdx[d]] = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
        }
        for (int i = 0; i < fp.Count; i++) AddOccluder(fc[i], fp[i]);
        for (int i = 0; i < hideOnly.Count; i++) AddOccluder(hideChunk[i], hideOnly[i]);
        LastUpdateMs = sw.Elapsed.TotalMilliseconds;
        return table;
    }
}
