#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// The shock grid: the hull's shadow along the flow on a (Res+1)^2 grid across it - the windward surface, the chord, the
/// shock standing off it - and the skirt's rim. Pure arithmetic (no game types: Tests/ShockGridTests compiles this file
/// alone and checks it bit for bit against the original code). The knobs (Res, Blur, Standoff, ...) are FlightField.cs's.
/// </summary>
internal sealed partial class FlightField
{
    /// <summary>The windward ("wetted") hull surface on the shock grid: which cells have hull, and its front-most point.</summary>
    private Vector3 _ga, _gb;
    private float _gx0, _gy0, _gdx, _gdy;
    private bool[,] _hullHas;
    private float[,] _hullBack;
    private Vector3[,] _hullSurf;
    /// <summary>The shock surface: positions on a (Res+1)^2 grid, and how far past the hull each is (0 on it, 1 at the margin).</summary>
    private Vector3[,] _P3;
    private float[,] _past;
    private Vector3[] _ring;
    private Vector3 _ringC;

    // (the KEPT outputs - the hull surface, the chord's back, P3, past, the rim - in two sets, used in turn: a re-layout
    //  fills the set not live and only then points the fields at it. Every reader goes through the fields, on this field's
    //  own thread - the placement, the spawn queue (its closures hold positions, never the grids), the spot, the weights -
    //  and none keeps a grid across a re-layout, so the set two layouts old is free. They were fresh each time: five grids,
    //  two of them past the large-object limit, ~310 KB a re-layout - 1.7 MB/s of the game's garbage, 2026-10-14)
    private sealed class ShockSet { public float[,] Back, Past; public bool[,] Has; public Vector3[,] Surf, P3; public Vector3[] Ring; }
    private readonly ShockSet _shockA = new ShockSet(), _shockB = new ShockSet();   // (two fields, not an array of a mod type: the game's whitelist)
    private int _shockLive;   // (0: A live, 1: B)
    // the working grids (flat, row-major: cell (i, j) at i * (N + 1) + j), kept between rebuilds
    float[] _sDepth, _sDist, _sHullZ, _sHullZ2, _sZ, _sT; bool[] _sHas; byte[] _sDirty;
    readonly Dictionary<int, List<float>> _sCols = new Dictionary<int, List<float>>();
    float[] _rimBest; bool[] _rimGot;
    static T[] Scr<T>(ref T[] slot, int n) => slot != null && slot.Length == n ? slot : (slot = new T[n]);
    static T[,] Scr<T>(ref T[,] slot, int N) => slot != null && slot.GetLength(0) == N + 1 ? slot : (slot = new T[N + 1, N + 1]);
    // the eight neighbours in the relaxation's order (di outer, dj inner)
    static readonly int[] RelaxDi = { -1, -1, -1, 0, 0, 1, 1, 1 }, RelaxDj = { -1, 0, 1, -1, 1, -1, 0, 1 };
    readonly float[] _relaxStep = new float[8]; readonly int[] _relaxOff = new int[8];

    private void ShockGrid(Vector3 f, Vector3 a, Vector3 b, float R, List<Vector3> pts, out Vector3[,] P3, out float[,] past)
    {
        // the extent across the flow: the hull's projection, plus the margin the shock sweeps back over
        float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
        foreach (var p in pts) { float x = Vector3.Dot(p, a), y = Vector3.Dot(p, b); x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
        float m = Margin * R; x0 -= m; x1 += m; y0 -= m; y1 += m;
        int N = Res, W = N + 1, C = W * W; float dx = (x1 - x0) / N, dy = (y1 - y0) / N;
        _ga = a; _gb = b; _gx0 = x0; _gy0 = y0; _gdx = dx; _gdy = dy;
        var set = _shockLive == 0 ? _shockB : _shockA;
        // the front-most hull point per cell (the surface the air reaches first)
        // (the working grids are this field's own, kept between rebuilds - a turning ship rebuilt its shock every frame,
        //  eight fresh grids and a list per column each time: 1.8 MB/s in a profile, 2026-10-14)
        var depth = Scr(ref _sDepth, C); var has = Scr(ref _sHas, C);
        Array.Clear(depth); Array.Clear(has);
        foreach (var p in pts)
        {
            int i = (int)MathF.Round((Vector3.Dot(p, a) - x0) / dx), j = (int)MathF.Round((Vector3.Dot(p, b) - y0) / dy);
            if (i < 0 || j < 0 || i > N || j > N) continue;
            float d = Vector3.Dot(p, f); int k = i * W + j;
            if (!has[k] || d > depth[k]) { depth[k] = d; has[k] = true; }
        }
        // ...and the back of the FIRST CONTIGUOUS piece behind it (the cell's chord: front - back). The whole column's
        // rear-most point took the tailplane behind a wing as part of the wing's chord - half of that sat in the air
        // between them (a detached line of vapour); now the chord stops at the first gap of ChordGap metres
        var hullBack = Scr(ref set.Back, N);
        var colDepths = _sCols;
        foreach (var kv in colDepths) kv.Value.Clear();
        foreach (var p in pts)
        {
            int i = (int)MathF.Round((Vector3.Dot(p, a) - x0) / dx), j = (int)MathF.Round((Vector3.Dot(p, b) - y0) / dy);
            if (i < 0 || j < 0 || i > N || j > N) continue;
            int key = i * (N + 1) + j;
            if (!colDepths.TryGetValue(key, out var l)) colDepths[key] = l = new List<float>();
            l.Add(Vector3.Dot(p, f));
        }
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++) hullBack[i, j] = float.MaxValue;
        foreach (var kv in colDepths)
        {
            var l = kv.Value; if (l.Count == 0) continue;
            l.Sort(); l.Reverse();   // front (largest) first
            float back = l[0];
            for (int k = 1; k < l.Count; k++) { if (back - l[k] > ChordGap) break; back = l[k]; }
            hullBack[kv.Key / (N + 1), kv.Key % (N + 1)] = back;
        }
        var hullHas = Scr(ref set.Has, N); var hullSurf = Scr(ref set.Surf, N);
        Array.Clear(hullSurf);   // (only the hull cells are written: the rest zero, as a fresh grid)
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++)
        {
            bool h = has[i * W + j]; hullHas[i, j] = h;
            if (h) hullSurf[i, j] = a * (x0 + i * dx) + b * (y0 + j * dy) + f * depth[i * W + j];
        }
        // every other cell takes its nearest hull cell's depth, and its distance from it
        // (in place, row by row, as it always was - the order decides ties, so it is kept exactly. Only a cell with a
        //  neighbour changed since its last look is looked at again: any other would find nothing - its neighbours and its
        //  own distance are what they were when it last settled. The step lengths once per layout, not per neighbour. The
        //  same passes, the same changes, bit for bit (Tests/ShockGridTests); ~52 ms a second of FlightJob's 60 in a CPU
        //  profile, 2026-10-14)
        var dist = Scr(ref _sDist, C); var dirty = Scr(ref _sDirty, C);
        for (int k = 0; k < C; k++) { dist[k] = has[k] ? 0f : float.MaxValue; dirty[k] = 1; }
        var stepN = _relaxStep; var offN = _relaxOff;
        for (int n = 0; n < 8; n++) { int di = RelaxDi[n], dj = RelaxDj[n]; stepN[n] = MathF.Sqrt(di * di * dx * dx + dj * dj * dy * dy); offN[n] = di * W + dj; }
        float s0 = stepN[0], s1 = stepN[1], s2 = stepN[2], s3 = stepN[3], s4 = stepN[4], s5 = stepN[5], s6 = stepN[6], s7 = stepN[7];
        int lo = 0, hi = C - 1;
        for (int pass = 0; pass < 2 * N; pass++)
        {
            bool changed = false; int nlo = int.MaxValue, nhi = -1;
            for (int k = lo; k <= hi; k++)
            {
                if (dirty[k] == 0) continue;
                dirty[k] = 0;
                int i = k / W, j = k - i * W; bool mine = false;
                if (i > 0 && i < N && j > 0 && j < N)
                {
                    // (inside: the eight in order, no edge tests; its own distance and depth held in locals - only this
                    //  look writes them, and no neighbour is itself)
                    float dk = dist[k], zk = depth[k], dn, nd; int kk;
                    kk = k - W - 1; dn = dist[kk]; if (dn != float.MaxValue) { nd = dn + s0; if (nd < dk - 1e-4f) { dk = nd; zk = depth[kk]; mine = true; } }
                    kk = k - W;     dn = dist[kk]; if (dn != float.MaxValue) { nd = dn + s1; if (nd < dk - 1e-4f) { dk = nd; zk = depth[kk]; mine = true; } }
                    kk = k - W + 1; dn = dist[kk]; if (dn != float.MaxValue) { nd = dn + s2; if (nd < dk - 1e-4f) { dk = nd; zk = depth[kk]; mine = true; } }
                    kk = k - 1;     dn = dist[kk]; if (dn != float.MaxValue) { nd = dn + s3; if (nd < dk - 1e-4f) { dk = nd; zk = depth[kk]; mine = true; } }
                    kk = k + 1;     dn = dist[kk]; if (dn != float.MaxValue) { nd = dn + s4; if (nd < dk - 1e-4f) { dk = nd; zk = depth[kk]; mine = true; } }
                    kk = k + W - 1; dn = dist[kk]; if (dn != float.MaxValue) { nd = dn + s5; if (nd < dk - 1e-4f) { dk = nd; zk = depth[kk]; mine = true; } }
                    kk = k + W;     dn = dist[kk]; if (dn != float.MaxValue) { nd = dn + s6; if (nd < dk - 1e-4f) { dk = nd; zk = depth[kk]; mine = true; } }
                    kk = k + W + 1; dn = dist[kk]; if (dn != float.MaxValue) { nd = dn + s7; if (nd < dk - 1e-4f) { dk = nd; zk = depth[kk]; mine = true; } }
                    if (!mine) continue;
                    dist[k] = dk; depth[k] = zk; changed = true;
                    // its neighbours look again: those ahead later in this pass, those behind in the next
                    dirty[k - W - 1] = dirty[k - W] = dirty[k - W + 1] = dirty[k - 1] = dirty[k + 1] = dirty[k + W - 1] = dirty[k + W] = dirty[k + W + 1] = 1;
                    if (k + W + 1 > hi) hi = k + W + 1;
                    if (k - W - 1 < nlo) nlo = k - W - 1;
                    if (k - 1 > nhi) nhi = k - 1;
                    continue;
                }
                // (at the edge: each neighbour tested, the same order)
                for (int n = 0; n < 8; n++)
                {
                    int di = RelaxDi[n], dj = RelaxDj[n];
                    if ((di < 0 && i == 0) || (di > 0 && i == N) || (dj < 0 && j == 0) || (dj > 0 && j == N)) continue;
                    int kk = k + offN[n];
                    if (dist[kk] == float.MaxValue) continue;
                    float nd = dist[kk] + stepN[n];
                    if (nd < dist[k] - 1e-4f) { dist[k] = nd; depth[k] = depth[kk]; mine = true; }
                }
                if (!mine) continue;
                changed = true;
                for (int n = 0; n < 8; n++)
                {
                    int di = RelaxDi[n], dj = RelaxDj[n];
                    if ((di < 0 && i == 0) || (di > 0 && i == N) || (dj < 0 && j == 0) || (dj > 0 && j == N)) continue;
                    int kk = k + offN[n];
                    dirty[kk] = 1;
                    if (kk > k) { if (kk > hi) hi = kk; }
                    else { if (kk < nlo) nlo = kk; if (kk > nhi) nhi = kk; }
                }
            }
            if (!changed) break;
            lo = nlo; hi = nhi;
            if (nhi < 0) { lo = 0; hi = -1; }   // (nothing behind: the next pass finds no change and ends it, as before)
        }
        // the hull's peaks grown (a max over neighbours, hull cells only): a lone nose cell must not be averaged away
        var hullZ = Scr(ref _sHullZ, C); var hullZ2 = Scr(ref _sHullZ2, C);
        for (int k = 0; k < C; k++) hullZ[k] = has[k] ? depth[k] : float.MinValue;
        for (int it = 0; it < Dilate; it++)
        {
            var nz = hullZ2;   // (every cell written: a max is the same in any order)
            for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++)
            {
                int k = i * W + j; float v = hullZ[k];
                for (int di = -1; di <= 1; di++) for (int dj = -1; dj <= 1; dj++)
                {
                    int ii = i + di, jj = j + dj;
                    if (ii < 0 || jj < 0 || ii > N || jj > N) continue;
                    float h = hullZ[ii * W + jj]; if (h > v) v = h;
                }
                nz[k] = v;
            }
            hullZ2 = hullZ; hullZ = nz;
        }
        for (int k = 0; k < C; k++) if (hullZ[k] > float.MinValue && hullZ[k] > depth[k]) depth[k] = hullZ[k];
        // the shock: standing off the hull, swept back past the silhouette, then blurred (loosely over the hull)
        var z = Scr(ref _sZ, C);
        for (int k = 0; k < C; k++)
        {
            float d = dist[k] == float.MaxValue ? m : dist[k];
            z[k] = depth[k] + Standoff * R - Sweep * d * d / Math.Max(R, 0.1f);
        }
        var t = Scr(ref _sT, C);
        for (int it = 0; it < Blur; it++)
        {
            for (int i = 0; i <= N; i++)
            {
                bool inI = i > 0 && i < N;
                for (int j = 0; j <= N; j++)
                {
                    int k = i * W + j;
                    if (inI && j > 0 && j < N)
                    {
                        // (inside: all nine, in the same order - the float sum is order-sensitive)
                        float s = 0;
                        s += z[k - W - 1]; s += z[k - W]; s += z[k - W + 1];
                        s += z[k - 1]; s += z[k]; s += z[k + 1];
                        s += z[k + W - 1]; s += z[k + W]; s += z[k + W + 1];
                        t[k] = s / 9;
                        continue;
                    }
                    float sum = 0; int cnt = 0;
                    for (int di = -1; di <= 1; di++) for (int dj = -1; dj <= 1; dj++)
                    { int ii = i + di, jj = j + dj; if (ii < 0 || jj < 0 || ii > N || jj > N) continue; sum += z[ii * W + jj]; cnt++; }
                    t[k] = sum / cnt;
                }
            }
            var sw = z; z = t; t = sw;
        }
        // never closer than MinStandoff to the hull (its grown surface), wherever there is hull
        for (int k = 0; k < C; k++)
            if (hullZ[k] > float.MinValue) z[k] = Math.Max(z[k], hullZ[k] + MinStandoff * R);
        P3 = Scr(ref set.P3, N); past = Scr(ref set.Past, N);
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++)
        {
            float u = i / (float)N, v = j / (float)N; int k = i * W + j;
            P3[i, j] = a * (x0 + u * (x1 - x0)) + b * (y0 + v * (y1 - y0)) + f * z[k];
            past[i, j] = (dist[k] == float.MaxValue ? m : dist[k]) / Math.Max(m, 1e-3f);
        }
        // the new set live (only now: the old one stood whole until here)
        _shockLive ^= 1;
        _hullBack = hullBack; _hullHas = hullHas; _hullSurf = hullSurf;
    }

    /// <summary>A closed ring (last = first) averaged with its neighbours (+-2), passes times. (0 passes: the ring itself -
    /// it was a copy, of a ring nothing else holds.)</summary>
    private Vector3[] SmoothRing(Vector3[] ring, int passes)
    {
        if (passes <= 0) return ring;
        int n = ring.Length - 1;
        var r = (Vector3[])ring.Clone();
        for (int k = 0; k < passes; k++)
        {
            var t = new Vector3[n + 1];
            for (int i = 0; i < n; i++)
            {
                Vector3 sum = Vector3.Zero;
                for (int d = -2; d <= 2; d++) sum += r[((i + d) % n + n) % n];
                t[i] = sum / 5f;
            }
            t[n] = t[0];
            r = t;
        }
        return r;
    }

    /// <summary>The skirt's rim: round the silhouette (Around angles), the outermost shock point just past the hull. (Into
    /// the live shock set's ring: call it after ShockGrid.)</summary>
    private Vector3[] Rim(Vector3[,] P3, float[,] past, Vector3 a, Vector3 b, out Vector3 c, int around = 99)
    {
        int N = P3.GetLength(0) - 1; c = Vector3.Zero; int cnt = 0;
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++) { c += P3[i, j]; cnt++; }
        c /= cnt;
        var ring = Scr(ref (_shockLive == 0 ? _shockA : _shockB).Ring, around + 1); var best = Scr(ref _rimBest, around); var got = Scr(ref _rimGot, around);
        Array.Clear(ring); Array.Clear(best); Array.Clear(got);
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++)
        {
            if (!(past[i, j] > 0.02f && past[i, j] < 0.2f)) continue;
            Vector3 rel = P3[i, j] - c;
            float x = Vector3.Dot(rel, a), y = Vector3.Dot(rel, b), ang = MathF.Atan2(y, x);
            int k = (int)MathF.Floor((ang + MathF.PI) / (2 * MathF.PI) * around) % around;
            float rr = MathF.Sqrt(x * x + y * y);
            if (!got[k] || rr > best[k]) { best[k] = rr; ring[k] = P3[i, j]; got[k] = true; }
        }
        for (int k = 0; k < around; k++)
            if (!got[k])
                for (int d = 1; d < around; d++) { int kk = (k + d) % around; if (got[kk]) { ring[k] = ring[kk]; break; } }
        ring[around] = ring[0];
        return ring;
    }
}
