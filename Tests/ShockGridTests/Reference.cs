#pragma warning disable
namespace AeroMod;

/// <summary>The shock grid as it was before the buffers were kept and the relaxation looked only at changed cells
/// (Scripts/Simulation/Fx/FlightField.cs, 2026-10-14, verbatim): every output fresh, every pass over every cell. The
/// reference the kept code must match bit for bit.</summary>
internal sealed class ReferenceShock
{
    static int Res => FlightField.Res; static int Blur => FlightField.Blur; static int Dilate => FlightField.Dilate;
    static float Standoff => FlightField.Standoff; static float Sweep => FlightField.Sweep; static float Margin => FlightField.Margin;
    static float MinStandoff => FlightField.MinStandoff; static float ChordGap => FlightField.ChordGap;
    public Vector3 _ga, _gb;
    public float _gx0, _gy0, _gdx, _gdy;
    public bool[,] _hullHas;
    public float[,] _hullBack;
    public Vector3[,] _hullSurf;

    public void ShockGrid(Vector3 f, Vector3 a, Vector3 b, float R, List<Vector3> pts, out Vector3[,] P3, out float[,] past)
    {
        // the extent across the flow: the hull's projection, plus the margin the shock sweeps back over
        float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
        foreach (var p in pts) { float x = Vector3.Dot(p, a), y = Vector3.Dot(p, b); x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
        float m = Margin * R; x0 -= m; x1 += m; y0 -= m; y1 += m;
        int N = Res; float dx = (x1 - x0) / N, dy = (y1 - y0) / N;
        _ga = a; _gb = b; _gx0 = x0; _gy0 = y0; _gdx = dx; _gdy = dy;
        // the front-most hull point per cell (the surface the air reaches first)
        var depth = new float[N + 1, N + 1]; var has = new bool[N + 1, N + 1];
        foreach (var p in pts)
        {
            int i = (int)MathF.Round((Vector3.Dot(p, a) - x0) / dx), j = (int)MathF.Round((Vector3.Dot(p, b) - y0) / dy);
            if (i < 0 || j < 0 || i > N || j > N) continue;
            float d = Vector3.Dot(p, f);
            if (!has[i, j] || d > depth[i, j]) { depth[i, j] = d; has[i, j] = true; }
        }
        // ...and the back of the FIRST CONTIGUOUS piece behind it (the cell's chord: front - back). The whole column's
        // rear-most point took the tailplane behind a wing as part of the wing's chord - half of that sat in the air
        // between them (a detached line of vapour); now the chord stops at the first gap of ChordGap metres
        _hullBack = new float[N + 1, N + 1];
        var colDepths = new Dictionary<int, List<float>>();
        foreach (var p in pts)
        {
            int i = (int)MathF.Round((Vector3.Dot(p, a) - x0) / dx), j = (int)MathF.Round((Vector3.Dot(p, b) - y0) / dy);
            if (i < 0 || j < 0 || i > N || j > N) continue;
            int key = i * (N + 1) + j;
            if (!colDepths.TryGetValue(key, out var l)) colDepths[key] = l = new List<float>();
            l.Add(Vector3.Dot(p, f));
        }
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++) _hullBack[i, j] = float.MaxValue;
        foreach (var kv in colDepths)
        {
            var l = kv.Value; l.Sort(); l.Reverse();   // front (largest) first
            float back = l[0];
            for (int k = 1; k < l.Count; k++) { if (back - l[k] > ChordGap) break; back = l[k]; }
            _hullBack[kv.Key / (N + 1), kv.Key % (N + 1)] = back;
        }
        _hullHas = (bool[,])has.Clone(); _hullSurf = new Vector3[N + 1, N + 1];
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++)
            if (has[i, j]) _hullSurf[i, j] = a * (x0 + i * dx) + b * (y0 + j * dy) + f * depth[i, j];
        // every other cell takes its nearest hull cell's depth, and its distance from it
        var dist = new float[N + 1, N + 1];
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++) dist[i, j] = has[i, j] ? 0f : float.MaxValue;
        for (int pass = 0; pass < 2 * N; pass++)
        {
            bool changed = false;
            for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++)
                for (int di = -1; di <= 1; di++) for (int dj = -1; dj <= 1; dj++)
                {
                    int ii = i + di, jj = j + dj;
                    if ((di == 0 && dj == 0) || ii < 0 || jj < 0 || ii > N || jj > N || dist[ii, jj] == float.MaxValue) continue;
                    float step = MathF.Sqrt(di * di * dx * dx + dj * dj * dy * dy), nd = dist[ii, jj] + step;
                    if (nd < dist[i, j] - 1e-4f) { dist[i, j] = nd; depth[i, j] = depth[ii, jj]; changed = true; }
                }
            if (!changed) break;
        }
        // the hull's peaks grown (a max over neighbours, hull cells only): a lone nose cell must not be averaged away
        var hullZ = new float[N + 1, N + 1];
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++) hullZ[i, j] = has[i, j] ? depth[i, j] : float.MinValue;
        for (int k = 0; k < Dilate; k++)
        {
            var nz = (float[,])hullZ.Clone();
            for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++)
                for (int di = -1; di <= 1; di++) for (int dj = -1; dj <= 1; dj++)
                {
                    int ii = i + di, jj = j + dj;
                    if (ii < 0 || jj < 0 || ii > N || jj > N) continue;
                    if (hullZ[ii, jj] > nz[i, j]) nz[i, j] = hullZ[ii, jj];
                }
            hullZ = nz;
        }
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++) if (hullZ[i, j] > float.MinValue && hullZ[i, j] > depth[i, j]) depth[i, j] = hullZ[i, j];
        // the shock: standing off the hull, swept back past the silhouette, then blurred (loosely over the hull)
        var z = new float[N + 1, N + 1];
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++)
        {
            float d = dist[i, j] == float.MaxValue ? m : dist[i, j];
            z[i, j] = depth[i, j] + Standoff * R - Sweep * d * d / Math.Max(R, 0.1f);
        }
        var t = new float[N + 1, N + 1];
        for (int k = 0; k < Blur; k++)
        {
            for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++)
            {
                float sum = 0; int cnt = 0;
                for (int di = -1; di <= 1; di++) for (int dj = -1; dj <= 1; dj++)
                { int ii = i + di, jj = j + dj; if (ii < 0 || jj < 0 || ii > N || jj > N) continue; sum += z[ii, jj]; cnt++; }
                t[i, j] = sum / cnt;
            }
            var sw = z; z = t; t = sw;
        }
        // never closer than MinStandoff to the hull (its grown surface), wherever there is hull
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++)
            if (hullZ[i, j] > float.MinValue) z[i, j] = Math.Max(z[i, j], hullZ[i, j] + MinStandoff * R);
        P3 = new Vector3[N + 1, N + 1]; past = new float[N + 1, N + 1];
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++)
        {
            float u = i / (float)N, v = j / (float)N;
            P3[i, j] = a * (x0 + u * (x1 - x0)) + b * (y0 + v * (y1 - y0)) + f * z[i, j];
            past[i, j] = (dist[i, j] == float.MaxValue ? m : dist[i, j]) / Math.Max(m, 1e-3f);
        }
    }

    /// <summary>A closed ring (last = first) averaged with its neighbours (+-2), passes times.</summary>
    public Vector3[] SmoothRing(Vector3[] ring, int passes)
    {
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

    /// <summary>The skirt's rim: round the silhouette (Around angles), the outermost shock point just past the hull.</summary>
    public Vector3[] Rim(Vector3[,] P3, float[,] past, Vector3 a, Vector3 b, out Vector3 c, int around = 99)
    {
        int N = P3.GetLength(0) - 1; c = Vector3.Zero; int cnt = 0;
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++) { c += P3[i, j]; cnt++; }
        c /= cnt;
        var ring = new Vector3[around + 1]; var best = new float[around]; var got = new bool[around];
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
