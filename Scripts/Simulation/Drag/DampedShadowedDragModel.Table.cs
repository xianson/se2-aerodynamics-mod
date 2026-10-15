#pragma warning disable
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace AeroMod;

// The force table: the face pressures for the overlay, the occlusion and visibility bakes, BuildForceTable and SumFaces.
public partial class DampedShadowedDragModel
{
    /// <summary>THE DRAG OVERLAY's pressures (2026-10-11, build hints): each hull face's pressure coefficient for the grid
    /// travelling along v (unit, grid-local) at this Mach, as the force table sees it - shadowed windward faces left out,
    /// the lee side's suction less what pressure recovery gives back. Positive pushes in, negative pulls. At most max faces
    /// (every k-th past that). Reads the per-face arrays only, into work's buffers - the live model is not touched.</summary>
    internal int FacePressures(ISurfaceProvider surface, ManifoldClassifier manifold, Vector3 v, float mach, TableWork work, List<Vector3> pos, List<Vector3> nrm, List<float> area, List<float> cps, int max)
    {
        pos.Clear(); nrm.Clear(); area.Clear(); cps.Clear();
        // (the per-face arrays: a ship whose table came from the disk cache and has not flown has none yet - the
        //  overlay found 0 faces on a parked ship)
        if (surface != null && surface.FaceCount > 0) EnsureSoA(surface.Faces, surface.Version, manifold);
        int total = _px.Count;
        if (total == 0 || _nx.Count < total || _area.Count < total || _origIndex.Count < total) return 0;
        v = Vector3.Normalize(v);
        BakeOcclusion(v, work);
        if (work.Vis.Length < total) return 0;
        var VA = work.Vis.AsSpan(0, total);
        bool recover = PressureRecovery;
        if (recover) BakeRecovery(v, work, VA);
        float st = Streamlining, cdBluff = CdBluff, cpMax = CpMax;
        float baseCp = st > 0f ? CpBase * (1f - 0.7f * st) : CpBase;
        float lo0 = ForceTable.TransonicOnset(SubsonicLimit, st);   // (the table's onset: ForceTable.TransonicOnset)
        float t = SupersonicLimit > lo0 ? Math.Clamp((mach - lo0) / (SupersonicLimit - lo0), 0f, 1f) : (mach >= lo0 ? 1f : 0f);
        t = t * t * (3f - 2f * t);
        var excl = _excluded != null && _excludedVersion == _lastSurfaceVersion ? _excluded : null;
        int exLen = excl?.Count ?? 0, stride = Math.Max(1, total / Math.Max(1, max));
        for (int gi = 0; gi < total; gi += stride)
        {
            int oi = _origIndex[gi];
            if (oi < 0 || (oi < exLen && excl[oi])) continue;
            float a = _area[gi];
            if (a < 1e-6f) continue;
            var n = new Vector3(_nx[gi], _ny[gi], _nz[gi]);
            float cosA = Vector3.Dot(n, v), cp;
            if (cosA > 0f)
            {
                if (VA[gi] <= 0f) continue;   // (shadowed)
                float cpSub = SubsonicSquareLaw ? cdBluff * cosA * cosA : cdBluff * cosA, cpSup = cpMax * cosA * cosA;
                if (st > 0f && !SubsonicSquareLaw) cpSub *= 1f - st * (1f - (0.08f + 0.92f * cosA * cosA));
                cp = cpSub + (cpSup - cpSub) * t;
            }
            else
            {
                cp = LeeCp(cosA, baseCp, cdBluff);
                // (the column's recovered push along the travel, as a pressure on this face: it pushes the face forward)
                if (recover && gi < work.Rec.Length && work.Rec[gi] != 0f) cp += work.Rec[gi] / (a * MathF.Max(0.2f, -cosA)) * (1f - t);
            }
            // (the face's own cell axis out, not the smoothed normal its pressure used: the overlay lays a quad on the block)
            var raw = gi < _raw.Count ? _raw[gi] : Vector3.Zero;
            pos.Add(new Vector3(_px[gi], _py[gi], _pz[gi])); nrm.Add(raw.LengthSquared() > 0.5f ? raw : n); area.Add(a); cps.Add(cp);
        }
        return pos.Count;
    }

    /// <summary>One table worker's buffers: its faces' reached areas and its depth buffer.</summary>
    public sealed class TableWork
    {
        public float[] Vis = System.Array.Empty<float>(); public float[] Z = System.Array.Empty<float>();
        public float[] Rec = System.Array.Empty<float>(), Front = System.Array.Empty<float>(), RearD = System.Array.Empty<float>(); public int[] RearI = System.Array.Empty<int>(); public ulong[] Run = System.Array.Empty<ulong>(), Step = System.Array.Empty<ulong>(); public float[] FrontA = System.Array.Empty<float>(), ExitA = System.Array.Empty<float>(), FrontD = System.Array.Empty<float>(); public ulong[] FStep = System.Array.Empty<ulong>();   // (PressureRecovery's)
        // (the recovery passes' faces, packed in list order - one array per field: arrays of mod types are banned, VRS1001)
        public float[] WU = System.Array.Empty<float>(), WWc = System.Array.Empty<float>(), WD = System.Array.Empty<float>(), WV = System.Array.Empty<float>(), WA = System.Array.Empty<float>();
        public float[] LU = System.Array.Empty<float>(), LW = System.Array.Empty<float>(), LD = System.Array.Empty<float>(), LV = System.Array.Empty<float>();
        public int[] LG = System.Array.Empty<int>(); public bool[] WS = System.Array.Empty<bool>(), LS = System.Array.Empty<bool>();
    }
    private readonly TableWork _work0 = new();

    /// <summary>
    /// Which hull faces the air reaches with the grid moving along v, exactly at v: a depth buffer across the flow
    /// (0.5 m pixels) keeps the most upstream face of each pixel; a face is reached if it is within 0.4 m of it. One
    /// pass over the faces per direction - the force table bakes every one of its directions this way (blending the
    /// shadow map's 26 directions made the force swing 3x across 10 degrees at the blend's kinks).
    /// </summary>
    /// <summary>Faces more nearly parallel to the flow than this (|cos|) are never shadowed (BakeOcclusion).</summary>
    public const float GrazingCos = 0.05f;

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
        var NX = CollectionsMarshal.AsSpan(_nx); var NY = CollectionsMarshal.AsSpan(_ny); var NZ = CollectionsMarshal.AsSpan(_nz);
        for (int gi = 0; gi < total; gi++)
        {
            int oi = OI[gi];
            if (oi < 0) { VA[gi] = 0f; continue; }
            if (oi < exLen && excl[oi]) { VA[gi] = 0f; continue; }
            float px = PX[gi], py = PY[gi], pz = PZ[gi];
            // A face's own surface cannot shadow it: across one pixel its plane's depth changes by Pix tan(theta) (theta: from
            // facing the flow), so that much more tolerance. A grazing face (nearly parallel to the flow) is never shadowed.
            // (With the flat 0.4 m only the first 0.4 m of a side or a fin along the flow was visible - the rest hidden by
            //  its own faces: almost no side force at small angles, no rotation damping. Tests/AeroBench: torque)
            float c = MathF.Abs(NX[gi] * v.X + NY[gi] * v.Y + NZ[gi] * v.Z);
            if (c < GrazingCos) { VA[gi] = AR[gi]; continue; }
            int iu = (int)((px * e1.X + py * e1.Y + pz * e1.Z - umin) * inv), iw = (int)((px * e2.X + py * e2.Y + pz * e2.Z - wmin) * inv);
            float d = px * v.X + py * v.Y + pz * v.Z;
            float tol = Tol + Pix * MathF.Sqrt(MathF.Max(0f, 1f - c * c)) / c;
            VA[gi] = d >= zb[iw * W + iu] - tol ? AR[gi] : 0f;
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
        _faceCount = 0; _lastSurfaceVersion = -1; _lastShadowVersion = -1; _loopBakeVersion = int.MinValue;
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
        // (the chunks' local updates take this surface's physics - 2026-10-15: they kept what the chunks were made with,
        //  a pooled builder's previous ship's length, or 1 m before any build: the wrong skin friction after damage)
        if (chunks != null) chunks.Phys = new FacePhysics(this);
        var table = new ForceTable(n, nj) { SurfaceVersion = cache.Version, BuildCom = com };
        int total = _px.Count;
        float cdBluff = CdBluff, cpMax = CpMax, cf = SkinCf, st = Streamlining;

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
        for (int c = 0; c < nChunks; c++) { chunks.RawShare(c); if (PressureRecovery) chunks.RawRecShare(c); }   // (made here: the workers below write them in parallel)
        void Worker(TableWork wk)
        {
            // (below the game's threads while it runs, and back after: the helpers are thread-pool threads - 2026-10-15:
            //  they stayed BelowNormal for the session, starving the game's own pool work under load)
            var th = System.Threading.Thread.CurrentThread; var was = System.Threading.ThreadPriority.Normal;
            try { was = th.Priority; th.Priority = System.Threading.ThreadPriority.BelowNormal; } catch { }
            try { WorkerCore(wk); } finally { try { th.Priority = was; } catch { } }
        }
        void WorkerCore(TableWork wk)
        {
            var acc = nChunks > 0 ? new double[nChunks * ForceTable.Stride] : null;
            var rec = nChunks > 0 && PressureRecovery ? new double[nChunks * ForceTable.Stride] : null;   // (each chunk's recovered push alone: ChunkedTable.RawRecShare)
            int idx;
            while ((idx = System.Threading.Interlocked.Increment(ref next)) < count)
            {
                int face = idx / per, r = idx % per, i = r / (n + 1), j = r % (n + 1);
                if (acc != null) System.Array.Clear(acc, 0, acc.Length);
                if (rec != null) System.Array.Clear(rec, 0, rec.Length);
                SumFaces(grid, cache, ForceTable.Direction(face, i, j, n), table.At(face, i, j), bake: true, work: wk, faceChunk: faceChunk, chunkAcc: acc, chunkRec: rec);   // (v: the grid's motion)
                if (acc != null)
                    for (int c = 0; c < nChunks; c++)
                    {
                        var ent = chunks.RawShare(c);
                        for (int k2 = 0; k2 < ForceTable.Stride; k2++) ent[idx * ForceTable.Stride + k2] = (float)acc[c * ForceTable.Stride + k2];
                        if (rec != null) { var re = chunks.RawRecShare(c); for (int k2 = 0; k2 < ForceTable.Stride; k2++) re[idx * ForceTable.Stride + k2] = (float)rec[c * ForceTable.Stride + k2]; }
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
        _lastShadowVersion = -1; _loopBakeVersion = int.MinValue;
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
    public void SumFaces(IGridAccessor grid, ISurfaceProvider cache, Vector3 v, Span<float> e, Vector3 w = default, Vector3 com = default, bool bake = true, TableWork work = null, int[] faceChunk = null, double[] chunkAcc = null, double[] chunkRec = null)
    {
        work ??= _work0;
        int total = _px.Count;
        var PX = CollectionsMarshal.AsSpan(_px); var PY = CollectionsMarshal.AsSpan(_py); var PZ = CollectionsMarshal.AsSpan(_pz);
        var NX = CollectionsMarshal.AsSpan(_nx); var NY = CollectionsMarshal.AsSpan(_ny); var NZ = CollectionsMarshal.AsSpan(_nz);
        float cdBluff = CdBluff, cpMax = CpMax, cf = SkinCf, st = Streamlining;
        float baseCp = st > 0f ? CpBase * (1f - 0.7f * st) : CpBase;
        bool turning = w.X != 0 || w.Y != 0 || w.Z != 0;
        if (bake) BakeOcclusion(v, work);
        var VA = work.Vis.AsSpan(0, Math.Min(total, work.Vis.Length));
        if (VA.Length < total) return;
        bool recover = PressureRecovery;
        if (recover && (bake || work.Rec.Length < total)) BakeRecovery(v, work, VA);
        var REC = recover ? work.Rec.AsSpan(0, total) : default;
        double f0x = 0, f0y = 0, f0z = 0, f1x = 0, f1y = 0, f1z = 0, t0x = 0, t0y = 0, t0z = 0, t1x = 0, t1y = 0, t1z = 0, frontal = 0;
        for (int gi = 0; gi < total; gi++)
        {
            float vai = VA[gi];
            if (vai < 1e-6f)
            {
                // (an unreached face behind: only a column's recovered push, if it is that column's exit)
                float r0 = recover ? REC[gi] : 0f;
                if (r0 == 0f) continue;
                float rax = r0 * v.X, ray = r0 * v.Y, raz = r0 * v.Z, qx = PX[gi], qy = PY[gi], qz = PZ[gi];
                f0x += rax; f0y += ray; f0z += raz;
                t0x += qy * raz - qz * ray; t0y += qz * rax - qx * raz; t0z += qx * ray - qy * rax;
                if (chunkAcc != null && faceChunk[gi] >= 0)
                {
                    int o = faceChunk[gi] * ForceTable.Stride;
                    chunkAcc[o] += rax; chunkAcc[o + 1] += ray; chunkAcc[o + 2] += raz;
                    chunkAcc[o + 6] += qy * raz - qz * ray; chunkAcc[o + 7] += qz * rax - qx * raz; chunkAcc[o + 8] += qx * ray - qy * rax;
                    if (chunkRec != null)
                    {
                        chunkRec[o] += rax; chunkRec[o + 1] += ray; chunkRec[o + 2] += raz;
                        chunkRec[o + 6] += qy * raz - qz * ray; chunkRec[o + 7] += qz * rax - qx * raz; chunkRec[o + 8] += qx * ray - qy * rax;
                    }
                }
                continue;
            }
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
                cpSub = SubsonicSquareLaw ? cdBluff * cosA * cosA : cdBluff * cosA; cpSup = cpMax * cosA * cosA;
                if (st > 0f && !SubsonicSquareLaw) { float factor = 0.08f + 0.92f * cosA * cosA; float m = 1f - st * (1f - factor); cpSub *= m; cpSup *= m; }
                frontal += vai * cosA * qf;
            }
            else
            {
                cpSub = cpSup = LeeCp(cosA, baseCp, cdBluff);   // (full suction from 30 degrees past parallel; recovery while attached)
            }
            float sx = 0, sy = 0, sz = 0;
            float sinSq = 1f - cosA * cosA;
            if (cf > 0 && sinSq > 1e-12f)
            {
                float fric = cf * vai * qf / MathF.Sqrt(sinSq);
                sx = -(vx - cosA * nx) * fric; sy = -(vy - cosA * ny) * fric; sz = -(vz - cosA * nz) * fric;   // (friction opposes the motion: -tangent - it was +, pushing every hull along its travel)
            }
            float ax = -cpSub * vai * qf * nx + sx, ay = -cpSub * vai * qf * ny + sy, az = -cpSub * vai * qf * nz + sz;
            if (recover)
            {
                float r = REC[gi];
                if (r != 0f)
                {
                    float rx = r * v.X, ry = r * v.Y, rz = r * v.Z;
                    ax += rx; ay += ry; az += rz;   // (the column's recovered push, along the travel: subsonic end)
                    if (chunkRec != null && faceChunk[gi] >= 0)
                    {
                        int o = faceChunk[gi] * ForceTable.Stride;
                        chunkRec[o] += rx; chunkRec[o + 1] += ry; chunkRec[o + 2] += rz;
                        chunkRec[o + 6] += py * rz - pz * ry; chunkRec[o + 7] += pz * rx - px * rz; chunkRec[o + 8] += px * ry - py * rx;
                    }
                }
            }
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


    /// <summary>The exact face forces at this flight state by SumFaces, rotation included: what the table approximates.</summary>
    public AeroResult ExactTableValue(in AeroContext ctx, ForceTable shape, bool bake = true, TableWork work = null)
    {
        if (ctx.Speed < 0.01f) return default;
        var one = _oneEntry ??= new ForceTable(1, 1);
        var vHat = ctx.Velocity / ctx.Speed;
        Span<float> e = stackalloc float[ForceTable.Stride];
        SumFaces(ctx.GridAccessor, ctx.SurfaceCache, vHat, e, ctx.AngularVelocity / ctx.Speed, ctx.CenterOfMass, bake, work);
        for (int f = 0; f < 6; f++) for (int i = 0; i <= 1; i++) for (int j = 0; j <= 1; j++) e.CopyTo(one.At(f, i, j));
        _lastShadowVersion = -1;
        var c = new AeroContext(ctx.GridAccessor, ctx.SurfaceCache, ctx.Velocity, ctx.Atmosphere, ctx.CenterOfMass, ctx.BlockSize, Vector3.Zero, ctx.GroundHeight, ctx.Manifold);
        return one.Evaluate(c, SubsonicLimit, SupersonicLimit, Streamlining);
    }
}
