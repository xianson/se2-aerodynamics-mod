#pragma warning disable
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace AeroMod;

/// <summary>
/// Shadow + Newtonian drag with per-face rotational velocity (ω×r).
/// Direction-grouped SoA: faces sorted by 6 axis normals, only front-facing
/// groups are iterated. AVX2 SIMD: 8 faces per iteration.
/// </summary>
public class DampedShadowedDragModel : IAeroDragModel
{
    public string Name => "Newtonian + Shadow + Damping (AVX2)";

    private PrecomputedShadowMap _shadowMap = new();

    /// <summary>A shadow map precomputed for the surface about to go live (a background rebuild).</summary>
    /// <summary>Returns the map it replaces (to be reused for the next rebuild).</summary>
    public PrecomputedShadowMap InstallShadowMap(PrecomputedShadowMap map)
    {
        if (map == null) return null;
        var old = _shadowMap;
        _shadowMap = map;
        _lastShadowVersion = -1;   // bake visibility from it on the next compute
        return old;
    }

    // ─── Tuning ─────────────────────────────────────────────────────

    public float CpMax { get; set; } = 2.0f;
    public float CdBluff { get; set; } = 1.1f;
    public float CpBase { get; set; } = -0.15f;
    public float CfSkin { get; set; } = 0.005f;
    public float SubsonicLimit { get; set; } = 0.6f;
    public float SupersonicLimit { get; set; } = 1.2f;
    public float Streamlining { get; set; } = 0.4f;

    public PrecomputedShadowMap ShadowMap => _shadowMap;

    /// <summary>Per-face Cp from the last Compute() call (for debug draw).
    /// Indexed by ORIGINAL face index (not grouped order).</summary>
    public List<float> FaceCp => _cpOut;

    // ─── Direction-grouped SoA ──────────────────────────────────────
    // Faces sorted into 6 groups by dominant normal: +X,-X,+Y,-Y,+Z,-Z
    // Within each group, padded to multiple of 8 for AVX2.

    private List<float> _px = new(), _py = new(), _pz = new();
    private List<float> _nx = new(), _ny = new(), _nz = new();
    private List<float> _area = new(), _visArea = new();
    private List<float> _cpGrouped = new();
    private List<int> _origIndex = new();
    private List<float> _cpOut = new(); // Cp in original face order

    // Per-direction group: start index and count in the SoA arrays
    private readonly List<int> _dirStart = new() { 0, 0, 0, 0, 0, 0 };
    private readonly List<int> _dirCount = new() { 0, 0, 0, 0, 0, 0 };
    private readonly List<int> _dirPadded = new() { 0, 0, 0, 0, 0, 0 };
    private int _totalPadded;

    private int _faceCount;
    private int _lastShadowVersion = -1;
    private int _lastSurfaceVersion = -1;
    private ManifoldClassifier _manifold;

    // Direction normals
    private static readonly List<Vector3> DirNormals = new()
    {
        Vector3.UnitX, -Vector3.UnitX,
        Vector3.UnitY, -Vector3.UnitY,
        Vector3.UnitZ, -Vector3.UnitZ,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DominantDir(Vector3 n)
    {
        float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
        if (ax >= ay && ax >= az) return n.X >= 0 ? 0 : 1;
        if (ay >= az) return n.Y >= 0 ? 2 : 3;
        return n.Z >= 0 ? 4 : 5;
    }

    private void EnsureSoA(IReadOnlyList<SurfaceFace> faces, int surfaceVersion, ManifoldClassifier manifold = null)
    {
        _manifold = manifold;
        int n = faces.Count;

        if (_faceCount == n && _lastSurfaceVersion == surfaceVersion)
            return;

        _lastSurfaceVersion = surfaceVersion;
        _faceCount = n;
        bool hasManifold = manifold != null && manifold.IsClassified;

        // Count HULL faces per direction (skip cavity faces)
        for (int d = 0; d < 6; d++) _dirCount[d] = 0;
        for (int i = 0; i < n; i++)
        {
            if (hasManifold && !manifold.IsHull(i)) continue;
            _dirCount[DominantDir(faces[i].Normal)]++;
        }

        // Compute padded starts
        int offset = 0;
        for (int d = 0; d < 6; d++)
        {
            _dirStart[d] = offset;
            _dirPadded[d] = (_dirCount[d] + 7) & ~7;
            offset += _dirPadded[d];
        }
        _totalPadded = offset;

        // Resize lists if needed
        EnsureListSize(_px, _totalPadded); EnsureListSize(_py, _totalPadded); EnsureListSize(_pz, _totalPadded);
        EnsureListSize(_nx, _totalPadded); EnsureListSize(_ny, _totalPadded); EnsureListSize(_nz, _totalPadded);
        EnsureListSize(_area, _totalPadded); EnsureListSize(_visArea, _totalPadded);
        EnsureListSize(_cpGrouped, _totalPadded);
        EnsureListSizeI(_origIndex, _totalPadded);

        // Zero all
        for (int j = 0; j < _totalPadded; j++)
        {
            _px[j] = 0; _py[j] = 0; _pz[j] = 0;
            _nx[j] = 0; _ny[j] = 0; _nz[j] = 0;
            _area[j] = 0; _visArea[j] = 0; _cpGrouped[j] = 0;
            _origIndex[j] = -1;
        }

        // Fill grouped SoA with HULL faces only
        Span<int> cursor = stackalloc int[6];
        for (int d = 0; d < 6; d++) cursor[d] = _dirStart[d];

        for (int i = 0; i < n; i++)
        {
            if (hasManifold && !manifold.IsHull(i)) continue;

            var f = faces[i];
            int d = DominantDir(f.Normal);
            int j = cursor[d]++;
            _px[j] = f.Position.X; _py[j] = f.Position.Y; _pz[j] = f.Position.Z;
            _nx[j] = f.Normal.X;   _ny[j] = f.Normal.Y;   _nz[j] = f.Normal.Z;
            _area[j] = f.Area;
            _visArea[j] = f.Area;
            _origIndex[j] = i;
        }

        // Ensure _cpOut is large enough (indexed by original face index)
        while (_cpOut.Count < n) _cpOut.Add(0f);
        // Zero cavity faces in _cpOut
        if (hasManifold)
            for (int i = 0; i < n; i++)
                if (!manifold.IsHull(i)) _cpOut[i] = 0f;

        _lastShadowVersion = -1;
    }

    private List<bool> _excluded;
    private int _excludedVersion = -1;

    /// <summary>Faces another model owns (a wing's skins): left out of this one. For the surface version given only.</summary>
    public void SetExcludedFaces(List<bool> excluded, int surfaceVersion)
    {
        _excluded = excluded;
        _excludedVersion = surfaceVersion;
        _lastShadowVersion = -1;   // re-bake with it
    }

    /// <summary>One table worker's buffers: its faces' reached areas and its depth buffer.</summary>
    public sealed class TableWork { public float[] Vis = System.Array.Empty<float>(); public float[] Z = System.Array.Empty<float>(); }
    private readonly TableWork _work0 = new();

    /// <summary>
    /// Which hull faces the air reaches with the grid moving along v, exactly at v: a depth buffer across the flow
    /// (0.5 m pixels) keeps the most upstream face of each pixel; a face is reached if it is within 0.4 m of it. One
    /// pass over the faces per direction - the force table bakes every one of its directions this way (blending the
    /// shadow map's 26 directions made the force swing 3x across 10 degrees at the blend's kinks).
    /// </summary>
    private void BakeOcclusion(Vector3 v, TableWork work)
    {
        var excl = _excluded != null && _excludedVersion == _lastSurfaceVersion ? _excluded : null;
        int exLen = excl?.Count ?? 0;
        var seed = MathF.Abs(v.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
        var e1 = Vector3.Normalize(Vector3.Cross(v, seed)); var e2 = Vector3.Cross(v, e1);
        const float Pix = 0.5f, Tol = 0.4f;
        int total = _px.Count;
        var PX = CollectionsMarshal.AsSpan(_px); var PY = CollectionsMarshal.AsSpan(_py); var PZ = CollectionsMarshal.AsSpan(_pz);
        if (work.Vis.Length < total) work.Vis = new float[total];
        var OI = CollectionsMarshal.AsSpan(_origIndex); var VA = work.Vis.AsSpan(0, total); var AR = CollectionsMarshal.AsSpan(_area);
        float umin = float.MaxValue, umax = float.MinValue, wmin = float.MaxValue, wmax = float.MinValue;
        for (int gi = 0; gi < total; gi++)
        {
            if (OI[gi] < 0) continue;
            float px = PX[gi], py = PY[gi], pz = PZ[gi];
            float u = px * e1.X + py * e1.Y + pz * e1.Z, w = px * e2.X + py * e2.Y + pz * e2.Z;
            if (u < umin) umin = u; if (u > umax) umax = u; if (w < wmin) wmin = w; if (w > wmax) wmax = w;
        }
        if (umin > umax) return;
        // (pixels on a fixed 0.5 m lattice across the flow: local updates - ChunkedTable - use the same one)
        umin = MathF.Floor(umin / Pix) * Pix; wmin = MathF.Floor(wmin / Pix) * Pix;
        int W = (int)((umax - umin) / Pix) + 1, H = (int)((wmax - wmin) / Pix) + 1;
        if (work.Z.Length < W * H) work.Z = new float[W * H];
        var zb = work.Z;
        System.Array.Fill(zb, float.MinValue, 0, W * H);
        float inv = 1f / Pix;
        for (int gi = 0; gi < total; gi++)
        {
            if (OI[gi] < 0) continue;
            float px = PX[gi], py = PY[gi], pz = PZ[gi];
            int iu = (int)((px * e1.X + py * e1.Y + pz * e1.Z - umin) * inv), iw = (int)((px * e2.X + py * e2.Y + pz * e2.Z - wmin) * inv);
            float d = px * v.X + py * v.Y + pz * v.Z;
            ref float z = ref zb[iw * W + iu];
            if (d > z) z = d;
        }
        for (int gi = 0; gi < total; gi++)
        {
            int oi = OI[gi];
            if (oi < 0) { VA[gi] = 0f; continue; }
            if (oi < exLen && excl[oi]) { VA[gi] = 0f; continue; }
            float px = PX[gi], py = PY[gi], pz = PZ[gi];
            int iu = (int)((px * e1.X + py * e1.Y + pz * e1.Z - umin) * inv), iw = (int)((px * e2.X + py * e2.Y + pz * e2.Z - wmin) * inv);
            float d = px * v.X + py * v.Y + pz * v.Z;
            VA[gi] = d >= zb[iw * W + iu] - Tol ? AR[gi] : 0f;
        }
    }

    private void BakeVisibility()
    {
        var visFactor = _shadowMap.VisibilityFactor;
        int visLen = visFactor.Count;
        var excl = _excluded != null && _excludedVersion == _lastSurfaceVersion ? _excluded : null;
        int exLen = excl?.Count ?? 0;

        // SoA only contains hull faces — no manifold check needed
        for (int d = 0; d < 6; d++)
        {
            int start = _dirStart[d];
            int count = _dirCount[d];
            for (int j = 0; j < count; j++)
            {
                int gi = start + j;
                int oi = _origIndex[gi];
                float v = oi >= 0 && oi < visLen ? visFactor[oi] : 1f;
                if (oi >= 0 && oi < exLen && excl[oi]) v = 0f;
                _visArea[gi] = _area[gi] * v;
            }
            for (int j = count; j < _dirPadded[d]; j++)
                _visArea[start + j] = 0f;
        }
        _lastShadowVersion = _shadowMap.Version;
    }

    // ─── Force table ────────────────────────────────────────────────

    /// <summary>
    /// This model's face forces for every flow direction (ForceTable), from the surface and shadow map it holds:
    /// for each table direction its shadow visibility is baked, then every windward face's pressure (bluff and
    /// Newtonian - the two ends of the speed-of-sound blend) and skin friction are summed for unit dynamic pressure,
    /// as Compute sums them. The rotation response comes from Compute itself (finite differences), on a coarser map.
    /// Not thread-safe with Compute on the same model: build it on a model of its own (the background build does).
    /// </summary>
    /// <summary>The per-face arrays let go (a big grid flies on its force table; the face loop - only without a
    /// table - rebuilds them if ever needed).</summary>
    public void ReleaseFaces()
    {
        foreach (var l in new List<List<float>> { _px, _py, _pz, _nx, _ny, _nz, _area, _visArea, _cpGrouped, _cpOut }) { l.Clear(); l.TrimExcess(); }
        _origIndex.Clear(); _origIndex.TrimExcess();
        _faceCount = 0; _lastSurfaceVersion = -1; _lastShadowVersion = -1;
        _work0.Vis = System.Array.Empty<float>(); _work0.Z = System.Array.Empty<float>();
    }

    /// <summary>Workers for one force table (the directions are independent).</summary>
    public static int TableThreads = 2;
    /// <summary>Threads a table build may use now (AeroWork sets it: the background budget); null: TableThreads.</summary>
    public static Func<int> ThreadBudget;   // (3 lengthened the game's own hitches while it streamed a ship in; 2 measured within its noise)

    public ForceTable BuildForceTable(IGridAccessor grid, ISurfaceProvider cache, ManifoldClassifier manifold, Vector3 com, int n = 8, int nj = 3, ChunkedTable chunks = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _shadowMap.Manifold = manifold;
        EnsureSoA(cache.Faces, cache.Version, manifold);
        var table = new ForceTable(n, nj) { SurfaceVersion = cache.Version, BuildCom = com };
        int total = _px.Count;
        float cdBluff = CdBluff, cpMax = CpMax, cf = CfSkin, st = Streamlining;

        // the directions, on up to TableThreads workers (each its own buffers; the faces are only read)
        int per = (n + 1) * (n + 1), count = 6 * per;
        int threads = Math.Max(1, ThreadBudget?.Invoke() ?? TableThreads);
        var works = new List<TableWork>(threads);   // (arrays of mod types are banned in scripts: VRS1001)
        int next = -1;
        for (int k = 0; k < threads; k++) works.Add(k == 0 ? _work0 : new TableWork());
        // chunks: each hull face's chunk, and its centre among that chunk's occluders
        int[] faceChunk = null;
        if (chunks != null)
        {
            int tot = _px.Count;
            faceChunk = new int[tot];
            for (int gi = 0; gi < tot; gi++)
            {
                if (_origIndex[gi] < 0) { faceChunk[gi] = -1; continue; }
                var pos = new Vector3(_px[gi], _py[gi], _pz[gi]);
                int ci = chunks.IndexOf(chunks.KeyOf(pos));
                faceChunk[gi] = ci;
                chunks.AddOccluder(ci, pos);
                int oi = _origIndex[gi];
                if (_excluded != null && _excludedVersion == _lastSurfaceVersion && oi < _excluded.Count && _excluded[oi] && cache is SmoothSurfaceProvider ssp)
                {
                    ssp.GetFaceCellDir(oi, out var fcell, out int fdir);
                    chunks.ExcludedFaces.Add(ChunkedTable.FaceKey(fcell, fdir));
                }
            }
        }
        if (chunks != null && manifold != null && manifold.IsClassified && cache is SmoothSurfaceProvider csp)
            for (int fi = 0; fi < csp.FaceCount; fi++)
                if (!manifold.IsHull(fi)) { csp.GetFaceCellDir(fi, out var cc0, out int cd0); chunks.CavityFaces.Add(ChunkedTable.FaceKey(cc0, cd0)); }
        int nChunks = chunks?.ChunkCount ?? 0;
        for (int c = 0; c < nChunks; c++) chunks.RawShare(c);   // (made here: the workers below write them in parallel)
        void Worker(TableWork wk)
        {
            try { System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.BelowNormal; } catch { }
            var acc = nChunks > 0 ? new double[nChunks * ForceTable.Stride] : null;
            int idx;
            while ((idx = System.Threading.Interlocked.Increment(ref next)) < count)
            {
                int face = idx / per, r = idx % per, i = r / (n + 1), j = r % (n + 1);
                if (acc != null) System.Array.Clear(acc, 0, acc.Length);
                SumFaces(grid, cache, ForceTable.Direction(face, i, j, n), table.At(face, i, j), bake: true, work: wk, faceChunk: faceChunk, chunkAcc: acc);   // (v: the grid's motion)
                if (acc != null)
                    for (int c = 0; c < nChunks; c++)
                    {
                        var ent = chunks.RawShare(c);
                        for (int k2 = 0; k2 < ForceTable.Stride; k2++) ent[idx * ForceTable.Stride + k2] = (float)acc[c * ForceTable.Stride + k2];
                    }
            }
        }
        var helpers = new List<System.Threading.Tasks.Task>(works.Count - 1);
        for (int k = 1; k < works.Count; k++) { var wk = works[k]; helpers.Add(System.Threading.Tasks.Task.Factory.StartNew(() => Worker(wk))); }
        Worker(works[0]);   // (this thread is one of them)
        foreach (var h in helpers) h.Wait();

        // rotation: how the torque about com (bluff end) changes with turning, per unit speed - dT/d(w/V), per unit q
        const float dW = 0.002f;   // (rad/s per m/s: 0.2 rad/s at 100 m/s)
        Span<float> e0 = stackalloc float[ForceTable.Stride], e1 = stackalloc float[ForceTable.Stride];
        for (int face = 0; face < 6; face++)
            for (int i = 0; i <= nj; i++)
                for (int j = 0; j <= nj; j++)
                {
                    var v = ForceTable.Direction(face, i, j, nj);
                    SumFaces(grid, cache, v, e0);
                    var f0 = new Vector3(e0[0], e0[1], e0[2]); var tc0 = new Vector3(e0[6], e0[7], e0[8]) - Vector3.Cross(com, f0);
                    var e = table.AtJ(face, i, j);
                    for (int axis = 0; axis < 3; axis++)
                    {
                        var w = axis == 0 ? new Vector3(dW, 0, 0) : axis == 1 ? new Vector3(0, dW, 0) : new Vector3(0, 0, dW);
                        SumFaces(grid, cache, v, e1, w, com, bake: false);   // (turning does not change what the air reaches)
                        var f1 = new Vector3(e1[0], e1[1], e1[2]); var tc1 = new Vector3(e1[6], e1[7], e1[8]) - Vector3.Cross(com, f1);
                        var dt = (tc1 - tc0) / dW;
                        e[0 * 3 + axis] = dt.X; e[1 * 3 + axis] = dt.Y; e[2 * 3 + axis] = dt.Z;
                    }
                }
        _lastShadowVersion = -1;
        chunks?.Seal();
        table.BuildMs = sw.Elapsed.TotalMilliseconds;
        return table;
    }

    /// <summary>
    /// The faces' forces for unit dynamic pressure, the grid moving along v (unit, grid-local), optionally turning
    /// at w (per unit speed, about com): F0 F1 T0 T1 (bluff / Newtonian ends of the speed-of-sound blend; torque about
    /// the grid origin) and frontal area, into 13 floats. One force-table entry. Continuous in v (the table
    /// interpolates it): windward pressure goes to zero as a face turns parallel to the flow, the base suction behind
    /// builds up over the first 30 degrees past parallel, and friction acts on every wetted face, either side. (Base
    /// suction on at full strength and friction off the instant a face turned leeward made the force jump wherever
    /// the many faces of a voxel ship lie parallel to the flow.)
    /// </summary>
    public void SumFaces(IGridAccessor grid, ISurfaceProvider cache, Vector3 v, Span<float> e, Vector3 w = default, Vector3 com = default, bool bake = true, TableWork work = null, int[] faceChunk = null, double[] chunkAcc = null)
    {
        work ??= _work0;
        int total = _px.Count;
        var PX = CollectionsMarshal.AsSpan(_px); var PY = CollectionsMarshal.AsSpan(_py); var PZ = CollectionsMarshal.AsSpan(_pz);
        var NX = CollectionsMarshal.AsSpan(_nx); var NY = CollectionsMarshal.AsSpan(_ny); var NZ = CollectionsMarshal.AsSpan(_nz);
        float cdBluff = CdBluff, cpMax = CpMax, cf = CfSkin, st = Streamlining;
        float baseCp = st > 0f ? CpBase * (1f - 0.7f * st) : CpBase;
        bool turning = w.X != 0 || w.Y != 0 || w.Z != 0;
        if (bake) BakeOcclusion(v, work);
        var VA = work.Vis.AsSpan(0, Math.Min(total, work.Vis.Length));
        if (VA.Length < total) return;
        double f0x = 0, f0y = 0, f0z = 0, f1x = 0, f1y = 0, f1z = 0, t0x = 0, t0y = 0, t0z = 0, t1x = 0, t1y = 0, t1z = 0, frontal = 0;
        for (int gi = 0; gi < total; gi++)
        {
            float vai = VA[gi];
            if (vai < 1e-6f) continue;
            float nx = NX[gi], ny = NY[gi], nz = NZ[gi];
            float px = PX[gi], py = PY[gi], pz = PZ[gi];
            // the air past this face (per unit speed), and its dynamic pressure (per unit q)
            float vx = v.X, vy = v.Y, vz = v.Z, qf = 1f;
            if (turning)
            {
                float rx0 = px - com.X, ry0 = py - com.Y, rz0 = pz - com.Z;
                vx += w.Y * rz0 - w.Z * ry0; vy += w.Z * rx0 - w.X * rz0; vz += w.X * ry0 - w.Y * rx0;
                qf = vx * vx + vy * vy + vz * vz;
                if (qf < 1e-8f) continue;
                float inv = 1f / MathF.Sqrt(qf); vx *= inv; vy *= inv; vz *= inv;
            }
            float cosA = vx * nx + vy * ny + vz * nz;
            float cpSub, cpSup;
            if (cosA > 0)
            {
                cpSub = cdBluff * cosA; cpSup = cpMax * cosA * cosA;
                if (st > 0f) { float factor = 0.08f + 0.92f * cosA * cosA; float m = 1f - st * (1f - factor); cpSub *= m; cpSup *= m; }
                frontal += vai * cosA * qf;
            }
            else
            {
                float ramp = MathF.Min(1f, -cosA * 2f);      // (sin 30 = 0.5: full suction from 30 degrees past parallel)
                ramp = ramp * ramp * (3f - 2f * ramp);
                cpSub = cpSup = baseCp * ramp;
            }
            float sx = 0, sy = 0, sz = 0;
            float sinSq = 1f - cosA * cosA;
            if (cf > 0 && sinSq > 1e-12f)
            {
                float fric = cf * vai * qf / MathF.Sqrt(sinSq);
                sx = (vx - cosA * nx) * fric; sy = (vy - cosA * ny) * fric; sz = (vz - cosA * nz) * fric;
            }
            float ax = -cpSub * vai * qf * nx + sx, ay = -cpSub * vai * qf * ny + sy, az = -cpSub * vai * qf * nz + sz;
            float bx = -cpSup * vai * qf * nx + sx, by = -cpSup * vai * qf * ny + sy, bz = -cpSup * vai * qf * nz + sz;
            f0x += ax; f0y += ay; f0z += az; f1x += bx; f1y += by; f1z += bz;
            t0x += py * az - pz * ay; t0y += pz * ax - px * az; t0z += px * ay - py * ax;
            t1x += py * bz - pz * by; t1y += pz * bx - px * bz; t1z += px * by - py * bx;
            if (chunkAcc != null)
            {
                int c = faceChunk[gi];
                if (c >= 0)
                {
                    int o = c * ForceTable.Stride;
                    chunkAcc[o] += ax; chunkAcc[o + 1] += ay; chunkAcc[o + 2] += az; chunkAcc[o + 3] += bx; chunkAcc[o + 4] += by; chunkAcc[o + 5] += bz;
                    chunkAcc[o + 6] += py * az - pz * ay; chunkAcc[o + 7] += pz * ax - px * az; chunkAcc[o + 8] += px * ay - py * ax;
                    chunkAcc[o + 9] += py * bz - pz * by; chunkAcc[o + 10] += pz * bx - px * bz; chunkAcc[o + 11] += px * by - py * bx;
                    if (cosA > 0) chunkAcc[o + 12] += vai * cosA * qf;
                }
            }
        }
        e[0] = (float)f0x; e[1] = (float)f0y; e[2] = (float)f0z; e[3] = (float)f1x; e[4] = (float)f1y; e[5] = (float)f1z;
        e[6] = (float)t0x; e[7] = (float)t0y; e[8] = (float)t0z; e[9] = (float)t1x; e[10] = (float)t1y; e[11] = (float)t1z;
        e[12] = (float)frontal;
    }

    /// <summary>A flat patch of faces into a force table, with this model's constants (ForceTable.AddPatch).</summary>
    public void AddPatch(ForceTable t, Vector3 n, Vector3 p, float area) => t.AddPatch(n, p, area, CdBluff, CpMax, CfSkin, Streamlining, CpBase);

    /// <summary>The exact face forces at this flight state by SumFaces, rotation included: what the table approximates.</summary>
    public AeroResult ExactTableValue(in AeroContext ctx, ForceTable shape)
    {
        if (ctx.Speed < 0.01f) return default;
        var one = new ForceTable(1, 1);
        var vHat = ctx.Velocity / ctx.Speed;
        Span<float> e = stackalloc float[ForceTable.Stride];
        SumFaces(ctx.GridAccessor, ctx.SurfaceCache, vHat, e, ctx.AngularVelocity / ctx.Speed, ctx.CenterOfMass);
        for (int f = 0; f < 6; f++) for (int i = 0; i <= 1; i++) for (int j = 0; j <= 1; j++) e.CopyTo(one.At(f, i, j));
        _lastShadowVersion = -1;
        var c = new AeroContext(ctx.GridAccessor, ctx.SurfaceCache, ctx.Velocity, ctx.Atmosphere, ctx.CenterOfMass, ctx.BlockSize, Vector3.Zero, ctx.GroundHeight, ctx.Manifold);
        return one.Evaluate(c, SubsonicLimit, SupersonicLimit, Streamlining);
    }

    // ─── Compute ────────────────────────────────────────────────────

    public AeroResult Compute(in AeroContext ctx)
    {
        float speed = ctx.Speed;
        if (speed < 0.01f || ctx.Atmosphere.Density < 1e-8)
            return default;

        var cache = ctx.SurfaceCache;
        Vector3 vHat = ctx.Velocity / speed;

        long t0 = AeroStats.Timestamp();
        _shadowMap.Manifold = ctx.Manifold;
        // The air's direction past the grid: against its motion. (It was given the motion itself: the map then saw
        // the REAR faces as the ones the air reaches and shadowed every front face - the faces' pressure drag, the
        // bulk of a bluff ship's drag, was all but gone: 8 kN on the cruising Jetliner.)
        _shadowMap.Update(ctx.GridAccessor, cache, -ctx.Velocity);

        EnsureSoA(cache.Faces, cache.Version, ctx.Manifold);

        if (_lastShadowVersion != _shadowMap.Version)
            BakeVisibility();
        AeroStats.SetShadow(AeroStats.ElapsedUs(t0));

        float totalFx = 0, totalFy = 0, totalFz = 0;
        float totalTx = 0, totalTy = 0, totalTz = 0;
        float frontalArea = 0;

        long t1 = AeroStats.Timestamp();

        // Pre-compute shared constants
        float halfRho = (float)(0.5 * ctx.Atmosphere.Density);
        float invSoS = ctx.Atmosphere.SpeedOfSound > 0 ? 1f / (float)ctx.Atmosphere.SpeedOfSound : 0;
        float baseCp = Streamlining > 0f ? CpBase * (1f - 0.7f * Streamlining) : CpBase;
        bool hasOmega = ctx.AngularVelocity.X != 0 || ctx.AngularVelocity.Y != 0 || ctx.AngularVelocity.Z != 0;
        bool hasSkin = CfSkin > 0;
        bool hasStreamlining = Streamlining > 0f;

        // Determine which directions are front-facing vs rear-facing
        for (int d = 0; d < 6; d++)
        {
            int count = _dirCount[d];
            if (count == 0) continue;

            float cosDir = Vector3.Dot(vHat, DirNormals[d]);

            if (cosDir > 0.001f)
            {
                // Front-facing group — full force computation
                if (Vector256.IsHardwareAccelerated && _dirPadded[d] >= 8)
                    ComputeGroupAvx2(ctx, d, cosDir, halfRho, invSoS, baseCp,
                        hasOmega, hasSkin, hasStreamlining,
                        ref totalFx, ref totalFy, ref totalFz,
                        ref totalTx, ref totalTy, ref totalTz,
                        ref frontalArea);
                else
                    ComputeGroupScalar(ctx, d, true, baseCp, halfRho, invSoS,
                        hasOmega, hasSkin, hasStreamlining,
                        ref totalFx, ref totalFy, ref totalFz,
                        ref totalTx, ref totalTy, ref totalTz,
                        ref frontalArea);
            }
            else
            {
                // Rear-facing group — just apply base pressure (cheap)
                ApplyBasePressure(ctx, d, baseCp, halfRho, hasOmega,
                    ref totalFx, ref totalFy, ref totalFz,
                    ref totalTx, ref totalTy, ref totalTz);
            }
        }

        // Wave drag: transonic drag rise from shock formation
        if (invSoS > 0)
        {
            float mach0 = speed * invSoS;
            double mcrit = 0.7 + 0.1 * Streamlining;
            double peak = 3.5 - 1.0 * Streamlining;
            float waveMul = (float)CompressibleFlow.WaveDragMultiplier(mach0, mcrit, peak);
            totalFx *= waveMul; totalFy *= waveMul; totalFz *= waveMul;
            totalTx *= waveMul; totalTy *= waveMul; totalTz *= waveMul;
            frontalArea *= waveMul;
        }

        AeroStats.SetForce(AeroStats.ElapsedUs(t1));

        var totalForce = new Vector3(totalFx, totalFy, totalFz);
        var totalTorque = new Vector3(totalTx, totalTy, totalTz);

        // Write _cpOut in original face order
        for (int d = 0; d < 6; d++)
        {
            int start = _dirStart[d];
            int count = _dirCount[d];
            for (int j = 0; j < count; j++)
            {
                int oi = _origIndex[start + j];
                if (oi >= 0) _cpOut[oi] = _cpGrouped[start + j];
            }
        }

        float forceDotV = Vector3.Dot(totalForce, vHat);
        Vector3 liftVec = totalForce - forceDotV * vHat;

        double q = ctx.Atmosphere.GetDynamicPressure(speed);
        double mach = ctx.Atmosphere.SpeedOfSound > 0 ? speed / ctx.Atmosphere.SpeedOfSound : 0;

        return new AeroResult(
            totalForce, totalTorque,
            MathF.Abs(forceDotV),
            liftVec.Length(),
            frontalArea,
            mach, q);
    }

    // ─── Front-facing AVX2 group ─────────────────────────────────────

    private void ComputeGroupAvx2(in AeroContext ctx, int dir, float cosDir,
        float halfRho, float invSoS, float baseCp,
        bool hasOmega, bool hasSkin, bool hasStreamlining,
        ref float totalFx, ref float totalFy, ref float totalFz,
        ref float totalTx, ref float totalTy, ref float totalTz,
        ref float frontalArea)
    {
        int start = _dirStart[dir];
        int padded = _dirPadded[dir];

        var velXV = Vector256.Create(ctx.Velocity.X);
        var velYV = Vector256.Create(ctx.Velocity.Y);
        var velZV = Vector256.Create(ctx.Velocity.Z);
        var wxV = Vector256.Create(ctx.AngularVelocity.X);
        var wyV = Vector256.Create(ctx.AngularVelocity.Y);
        var wzV = Vector256.Create(ctx.AngularVelocity.Z);
        var comXV = Vector256.Create(ctx.CenterOfMass.X);
        var comYV = Vector256.Create(ctx.CenterOfMass.Y);
        var comZV = Vector256.Create(ctx.CenterOfMass.Z);
        var halfRhoV = Vector256.Create(halfRho);
        var baseCpV = Vector256.Create(baseCp);
        var cfSkinV = Vector256.Create(CfSkin);

        // Pre-compute Mach blend factor from CoM speed (constant across group)
        float comSpeed = ctx.Velocity.Length();
        float mach0 = comSpeed * invSoS;
        float t0 = (mach0 - SubsonicLimit) * (SupersonicLimit > SubsonicLimit ? 1f / (SupersonicLimit - SubsonicLimit) : 0f);
        t0 = t0 < 0 ? 0 : (t0 > 1 ? 1 : t0);
        t0 = t0 * t0 * (3f - 2f * t0);
        // Per-face Cp = cdBluff*cosA + (cpMax*cosA² - cdBluff*cosA)*t0
        //             = cosA * (cdBluff + (cpMax*cosA - cdBluff)*t0)
        //             = cosA * (cdBluff*(1-t0) + cpMax*cosA*t0)
        var cdBlend = Vector256.Create(CdBluff * (1f - t0));
        var cpBlend = Vector256.Create(CpMax * t0);
        var streamV = Vector256.Create(Streamlining);

        var zeroV = Vector256<float>.Zero;
        var oneV = Vector256.Create(1f);
        var halfV = Vector256.Create(0.5f);
        var onePointFiveV = Vector256.Create(1.5f);
        var epsV = Vector256.Create(1e-4f);
        var tEpsV = Vector256.Create(1e-12f);
        var minFracV = Vector256.Create(0.08f);
        var frac92V = Vector256.Create(0.92f);
        var negOneV = Vector256.Create(-1f);

        var accFx = zeroV; var accFy = zeroV; var accFz = zeroV;
        var accTx = zeroV; var accTy = zeroV; var accTz = zeroV;
        var accArea = zeroV;

        ref float pxRef = ref MemoryMarshal.GetReference(CollectionsMarshal.AsSpan(_px));
        ref float pyRef = ref MemoryMarshal.GetReference(CollectionsMarshal.AsSpan(_py));
        ref float pzRef = ref MemoryMarshal.GetReference(CollectionsMarshal.AsSpan(_pz));
        ref float nxRef = ref MemoryMarshal.GetReference(CollectionsMarshal.AsSpan(_nx));
        ref float nyRef = ref MemoryMarshal.GetReference(CollectionsMarshal.AsSpan(_ny));
        ref float nzRef = ref MemoryMarshal.GetReference(CollectionsMarshal.AsSpan(_nz));
        ref float vaRef = ref MemoryMarshal.GetReference(CollectionsMarshal.AsSpan(_visArea));
        ref float cpRef = ref MemoryMarshal.GetReference(CollectionsMarshal.AsSpan(_cpGrouped));

        for (int i = 0; i < padded; i += 8)
        {
            int si = start + i;
            var px = Vector256.LoadUnsafe(ref Unsafe.Add(ref pxRef, si));
            var py = Vector256.LoadUnsafe(ref Unsafe.Add(ref pyRef, si));
            var pz = Vector256.LoadUnsafe(ref Unsafe.Add(ref pzRef, si));
            var nx = Vector256.LoadUnsafe(ref Unsafe.Add(ref nxRef, si));
            var ny = Vector256.LoadUnsafe(ref Unsafe.Add(ref nyRef, si));
            var nz = Vector256.LoadUnsafe(ref Unsafe.Add(ref nzRef, si));
            var vai = Vector256.LoadUnsafe(ref Unsafe.Add(ref vaRef, si));

            var rx = px - comXV;
            var ry = py - comYV;
            var rz = pz - comZV;

            // Per-face velocity: v + ω×r (FMA)
            Vector256<float> vx, vy, vz;
            if (Fma.IsSupported)
            {
                vx = Fma.MultiplyAddNegated(wzV, ry, Fma.MultiplyAdd(wyV, rz, velXV));
                vy = Fma.MultiplyAddNegated(wxV, rz, Fma.MultiplyAdd(wzV, rx, velYV));
                vz = Fma.MultiplyAddNegated(wyV, rx, Fma.MultiplyAdd(wxV, ry, velZV));
            }
            else
            {
                vx = velXV + (wyV * rz - wzV * ry);
                vy = velYV + (wzV * rx - wxV * rz);
                vz = velZV + (wxV * ry - wyV * rx);
            }

            // speedSq (FMA chain)
            Vector256<float> speedSq;
            if (Fma.IsSupported)
                speedSq = Fma.MultiplyAdd(vz, vz, Fma.MultiplyAdd(vy, vy, vx * vx));
            else
                speedSq = vx * vx + vy * vy + vz * vz;

            var safeSpeedSq = Vector256.Max(speedSq, epsV);

            // rsqrt + NR (FMA)
            var est = Avx.ReciprocalSqrt(safeSpeedSq);
            Vector256<float> invSpeed;
            if (Fma.IsSupported)
                invSpeed = est * Fma.MultiplyAddNegated(halfV * safeSpeedSq, est * est, onePointFiveV);
            else
                invSpeed = est * (onePointFiveV - halfV * safeSpeedSq * est * est);

            // vhat + cosAlpha (FMA)
            var vhx = vx * invSpeed;
            var vhy = vy * invSpeed;
            var vhz = vz * invSpeed;

            Vector256<float> cosA;
            if (Fma.IsSupported)
                cosA = Fma.MultiplyAdd(vhz, nz, Fma.MultiplyAdd(vhy, ny, vhx * nx));
            else
                cosA = vhx * nx + vhy * ny + vhz * nz;

            // q = ½ρv²
            var q = halfRhoV * speedSq;

            // Cp with pre-computed Mach blend: cosA * (cdBlend + cpBlend * cosA)
            Vector256<float> cpWindward;
            if (Fma.IsSupported)
                cpWindward = cosA * Fma.MultiplyAdd(cpBlend, cosA, cdBlend);
            else
                cpWindward = cosA * (cdBlend + cpBlend * cosA);

            // Streamlining
            if (hasStreamlining)
            {
                var cosA2 = cosA * cosA;
                Vector256<float> factor;
                if (Fma.IsSupported)
                    factor = Fma.MultiplyAdd(frac92V, cosA2, minFracV);
                else
                    factor = minFracV + frac92V * cosA2;
                cpWindward = cpWindward * (oneV - streamV * (oneV - factor));
            }

            // Front/rear select + validity
            var windward = Vector256.GreaterThan(cosA, zeroV);
            var cp = Vector256.ConditionalSelect(windward, cpWindward, baseCpV);
            var valid = Vector256.GreaterThanOrEqual(speedSq, epsV);
            cp = Vector256.ConditionalSelect(valid, cp, zeroV);

            cp.StoreUnsafe(ref Unsafe.Add(ref cpRef, si));

            accArea += Vector256.ConditionalSelect(windward & valid, vai * cosA, zeroV);

            // Force = -Cp * q * visArea * normal
            var pf = negOneV * cp * q * vai;
            var fx = nx * pf;
            var fy = ny * pf;
            var fz = nz * pf;

            // Skin friction (single rsqrt eliminated — use tangent directly)
            if (hasSkin)
            {
                // tangent = vhat - cosA*normal (unnormalized, length = sinA)
                // friction force = cfSkin * q * area * tangent_hat
                //                = cfSkin * q * area * tangent / sinA
                //                = cfSkin * q * area / sinA * (vhat - cosA*n)
                // sinA = sqrt(1 - cosA²), use rsqrt(1 - cosA²) for 1/sinA

                Vector256<float> cosA2;
                if (Fma.IsSupported)
                    cosA2 = Fma.MultiplyAddNegated(cosA, cosA, oneV);
                else
                    cosA2 = oneV - cosA * cosA;
                var sinValid = Vector256.GreaterThan(cosA2, tEpsV);
                var safeSinSq = Vector256.Max(cosA2, epsV);

                var sinEst = Avx.ReciprocalSqrt(safeSinSq);
                Vector256<float> invSinA;
                if (Fma.IsSupported)
                    invSinA = sinEst * Fma.MultiplyAddNegated(halfV * safeSinSq, sinEst * sinEst, onePointFiveV);
                else
                    invSinA = sinEst * (onePointFiveV - halfV * safeSinSq * sinEst * sinEst);

                var fric = cfSkinV * q * vai * invSinA;
                var fricMask = windward & sinValid & valid;

                // tangent = vhat - cosA * normal (FMA)
                Vector256<float> tx, ty, tz;
                if (Fma.IsSupported)
                {
                    tx = Fma.MultiplyAddNegated(cosA, nx, vhx);
                    ty = Fma.MultiplyAddNegated(cosA, ny, vhy);
                    tz = Fma.MultiplyAddNegated(cosA, nz, vhz);
                }
                else
                {
                    tx = vhx - cosA * nx;
                    ty = vhy - cosA * ny;
                    tz = vhz - cosA * nz;
                }

                if (Fma.IsSupported)
                {
                    fx += Vector256.ConditionalSelect(fricMask, Fma.MultiplyAdd(tx, fric, zeroV), zeroV);
                    fy += Vector256.ConditionalSelect(fricMask, Fma.MultiplyAdd(ty, fric, zeroV), zeroV);
                    fz += Vector256.ConditionalSelect(fricMask, Fma.MultiplyAdd(tz, fric, zeroV), zeroV);
                }
                else
                {
                    fx += Vector256.ConditionalSelect(fricMask, tx * fric, zeroV);
                    fy += Vector256.ConditionalSelect(fricMask, ty * fric, zeroV);
                    fz += Vector256.ConditionalSelect(fricMask, tz * fric, zeroV);
                }
            }

            fx = Vector256.ConditionalSelect(valid, fx, zeroV);
            fy = Vector256.ConditionalSelect(valid, fy, zeroV);
            fz = Vector256.ConditionalSelect(valid, fz, zeroV);

            // Accumulate force + torque (FMA for cross product)
            accFx += fx; accFy += fy; accFz += fz;
            if (Fma.IsSupported)
            {
                accTx = Fma.MultiplyAdd(ry, fz, Fma.MultiplyAddNegated(rz, fy, accTx));
                accTy = Fma.MultiplyAdd(rz, fx, Fma.MultiplyAddNegated(rx, fz, accTy));
                accTz = Fma.MultiplyAdd(rx, fy, Fma.MultiplyAddNegated(ry, fx, accTz));
            }
            else
            {
                accTx += ry * fz - rz * fy;
                accTy += rz * fx - rx * fz;
                accTz += rx * fy - ry * fx;
            }
        }

        totalFx += Vector256.Sum(accFx);
        totalFy += Vector256.Sum(accFy);
        totalFz += Vector256.Sum(accFz);
        totalTx += Vector256.Sum(accTx);
        totalTy += Vector256.Sum(accTy);
        totalTz += Vector256.Sum(accTz);
        frontalArea += Vector256.Sum(accArea);
    }

    // ─── Rear-facing base pressure (cheap — no shadow, no Mach blend) ──

    private void ApplyBasePressure(in AeroContext ctx, int dir, float baseCp, float halfRho,
        bool hasOmega,
        ref float totalFx, ref float totalFy, ref float totalFz,
        ref float totalTx, ref float totalTy, ref float totalTz)
    {
        int start = _dirStart[dir];
        int count = _dirCount[dir];
        float comX = ctx.CenterOfMass.X, comY = ctx.CenterOfMass.Y, comZ = ctx.CenterOfMass.Z;
        float velX = ctx.Velocity.X, velY = ctx.Velocity.Y, velZ = ctx.Velocity.Z;
        float wx = ctx.AngularVelocity.X, wy = ctx.AngularVelocity.Y, wz = ctx.AngularVelocity.Z;

        for (int j = 0; j < count; j++)
        {
            int gi = start + j;
            float vai = _visArea[gi];
            if (vai < 1e-6f) { _cpGrouped[gi] = 0; continue; }

            float pxi = _px[gi], pyi = _py[gi], pzi = _pz[gi];
            float nxi = _nx[gi], nyi = _ny[gi], nzi = _nz[gi];

            float rx = pxi - comX, ry = pyi - comY, rz = pzi - comZ;

            float vx, vy, vz;
            if (hasOmega)
            {
                vx = velX + (wy * rz - wz * ry);
                vy = velY + (wz * rx - wx * rz);
                vz = velZ + (wx * ry - wy * rx);
            }
            else { vx = velX; vy = velY; vz = velZ; }

            float speedSq = vx * vx + vy * vy + vz * vz;
            if (speedSq < 1e-4f) { _cpGrouped[gi] = 0; continue; }

            _cpGrouped[gi] = baseCp;

            float q = halfRho * speedSq;
            float pf = -baseCp * q * vai;
            float fx = nxi * pf, fy = nyi * pf, fz = nzi * pf;

            totalFx += fx; totalFy += fy; totalFz += fz;
            totalTx += ry * fz - rz * fy;
            totalTy += rz * fx - rx * fz;
            totalTz += rx * fy - ry * fx;
        }
    }

    // ─── Scalar fallback for front-facing group ──────────────────────

    private void ComputeGroupScalar(in AeroContext ctx, int dir, bool frontFacing,
        float baseCp, float halfRho, float invSoS,
        bool hasOmega, bool hasSkin, bool hasStreamlining,
        ref float totalFx, ref float totalFy, ref float totalFz,
        ref float totalTx, ref float totalTy, ref float totalTz,
        ref float frontalArea)
    {
        int start = _dirStart[dir];
        int count = _dirCount[dir];
        float comX = ctx.CenterOfMass.X, comY = ctx.CenterOfMass.Y, comZ = ctx.CenterOfMass.Z;
        float velX = ctx.Velocity.X, velY = ctx.Velocity.Y, velZ = ctx.Velocity.Z;
        float wx = ctx.AngularVelocity.X, wy = ctx.AngularVelocity.Y, wz = ctx.AngularVelocity.Z;
        float cfSkin = CfSkin, cpMax = CpMax, cdBluff = CdBluff;
        float subLim = SubsonicLimit;
        float invMachRange = SupersonicLimit > SubsonicLimit ? 1f / (SupersonicLimit - SubsonicLimit) : 0;
        float streamlining = Streamlining;

        for (int j = 0; j < count; j++)
        {
            int gi = start + j;
            float pxi = _px[gi], pyi = _py[gi], pzi = _pz[gi];
            float nxi = _nx[gi], nyi = _ny[gi], nzi = _nz[gi];
            float vai = _visArea[gi];

            float rx = pxi - comX, ry = pyi - comY, rz = pzi - comZ;

            float vx, vy, vz;
            if (hasOmega)
            {
                vx = velX + (wy * rz - wz * ry);
                vy = velY + (wz * rx - wx * rz);
                vz = velZ + (wx * ry - wy * rx);
            }
            else { vx = velX; vy = velY; vz = velZ; }

            float speedSq = vx * vx + vy * vy + vz * vz;
            if (speedSq < 0.0001f) { _cpGrouped[gi] = 0; continue; }

            float invSpd = RsqrtNR(speedSq);
            float spd = speedSq * invSpd;
            float vhx = vx * invSpd, vhy = vy * invSpd, vhz = vz * invSpd;

            float cosA = vhx * nxi + vhy * nyi + vhz * nzi;
            float q = halfRho * speedSq;

            float cp;
            if (cosA > 0)
            {
                if (vai < 1e-6f) { _cpGrouped[gi] = 0; continue; }

                float mach = spd * invSoS;
                float cpSub = cdBluff * cosA;
                float cpSup = cpMax * cosA * cosA;
                float t = (mach - subLim) * invMachRange;
                t = t < 0 ? 0 : (t > 1 ? 1 : t);
                t = t * t * (3f - 2f * t);
                cp = cpSub + (cpSup - cpSub) * t;

                if (hasStreamlining)
                {
                    float recovery = cosA * cosA;
                    float factor = 0.08f + 0.92f * recovery;
                    cp *= 1f - streamlining * (1f - factor);
                }

                frontalArea += vai * cosA;
            }
            else
            {
                cp = baseCp;
            }

            _cpGrouped[gi] = cp;

            float pf = -cp * q * vai;
            float fx = nxi * pf, fy = nyi * pf, fz = nzi * pf;

            if (cosA > 0 && hasSkin)
            {
                float sinSq = 1f - cosA * cosA;
                if (sinSq > 1e-12f)
                {
                    float invSinA = RsqrtNR(sinSq);
                    float tx = vhx - cosA * nxi;
                    float ty = vhy - cosA * nyi;
                    float tz = vhz - cosA * nzi;
                    float fric = cfSkin * q * vai * invSinA;
                    fx += tx * fric; fy += ty * fric; fz += tz * fric;
                }
            }

            totalFx += fx; totalFy += fy; totalFz += fz;
            totalTx += ry * fz - rz * fy;
            totalTy += rz * fx - rx * fz;
            totalTz += rx * fy - ry * fx;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float RsqrtNR(float x)
    {
        float est = MathF.ReciprocalSqrtEstimate(x);
        return est * (1.5f - 0.5f * x * est * est);
    }

    private static void EnsureListSize(List<float> list, int size)
    {
        while (list.Count < size) list.Add(0f);
    }

    private static void EnsureListSizeI(List<int> list, int size)
    {
        while (list.Count < size) list.Add(0);
    }
}
