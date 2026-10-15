#pragma warning disable
using AeroMod;

namespace AeroMod
{
    /// <summary>The knobs the shock grid reads (FlightField.cs's, at their defaults: that file needs the game) and a way in.</summary>
    internal sealed partial class FlightField
    {
        public static float Standoff = 0.4f, Sweep = 1.0f, Margin = 0.8f, MinStandoff = 0.12f, ChordGap = 1.5f;
        public static int Dilate = 2, Res = 96, Blur = 12, RimSmooth = 0;

        /// <summary>One layout, as Shape lays it out: the grid, then the rim.</summary>
        public void Layout(Vector3 f, Vector3 a, Vector3 b, float R, List<Vector3> pts)
        {
            ShockGrid(f, a, b, R, pts, out _P3, out _past);
            _ring = SmoothRing(Rim(_P3, _past, a, b, out _ringC), RimSmooth);
        }
        public Vector3[,] P3 => _P3; public float[,] Past => _past; public bool[,] Has => _hullHas; public float[,] Back => _hullBack;
        public Vector3[,] Surf => _hullSurf; public Vector3[] Ring => _ring; public Vector3 RingC => _ringC;
        public float[] Frame => new[] { _ga.X, _ga.Y, _ga.Z, _gb.X, _gb.Y, _gb.Z, _gx0, _gy0, _gdx, _gdy };
    }
}

/// <summary>ShockGridTests: the kept-buffer, changed-cells-only shock grid against the original, bit for bit, on synthetic
/// hulls, flows and knobs - one field laid out again and again (its buffers reused across sizes); then the timings.</summary>
static class Program
{
    static int _checks, _fails;
    static void Check(bool ok, string what) { _checks++; if (!ok) { _fails++; if (_fails <= 20) Console.WriteLine("FAIL " + what); } }
    static bool Same(float x, float y) => BitConverter.SingleToInt32Bits(x) == BitConverter.SingleToInt32Bits(y);
    static bool Same(Vector3 x, Vector3 y) => Same(x.X, y.X) && Same(x.Y, y.Y) && Same(x.Z, y.Z);
    static string Diff<T>(T[,] x, T[,] y, Func<T, T, bool> eq)
    {
        if (x.GetLength(0) != y.GetLength(0) || x.GetLength(1) != y.GetLength(1)) return "size";
        for (int i = 0; i < x.GetLength(0); i++) for (int j = 0; j < x.GetLength(1); j++) if (!eq(x[i, j], y[i, j])) return $"[{i},{j}] {x[i, j]} vs {y[i, j]}";
        return null;
    }
    static string Diff<T>(T[] x, T[] y, Func<T, T, bool> eq)
    {
        if (x.Length != y.Length) return "size";
        for (int i = 0; i < x.Length; i++) if (!eq(x[i], y[i])) return $"[{i}] {x[i]} vs {y[i]}";
        return null;
    }

    // ── synthetic hulls: exposed face centres of voxel shapes (as the force table's occluders), and odd ones ──
    static List<Vector3> Voxels(float s, Func<int, int, int, bool> solid, int r)
    {
        var pts = new List<Vector3>();
        var n = new[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) };
        for (int x = -r; x <= r; x++) for (int y = -r; y <= r; y++) for (int z = -r; z <= r; z++)
        {
            if (!solid(x, y, z)) continue;
            foreach (var (nx, ny, nz) in n)
                if (!solid(x + nx, y + ny, z + nz)) pts.Add(new Vector3((x + 0.5f * nx) * s, (y + 0.5f * ny) * s, (z + 0.5f * nz) * s));
        }
        return pts;
    }
    static List<Vector3> Plane() => Voxels(0.5f, (x, y, z) =>
        (x * x + y * y <= 4 && z >= -30 && z <= 30)                                  // fuselage
        || (y == 0 && z >= -4 && z <= 3 && Math.Abs(x) <= 24 - Math.Abs(z))          // wing
        || (y == 0 && z >= 24 && z <= 28 && Math.Abs(x) <= 10)                       // tailplane
        || (x == 0 && y >= 0 && y <= 10 && z >= 22 && z <= 28), 32);                  // fin
    static List<Vector3> Sphere(float rad, Vector3 c) { var p = Voxels(0.5f, (x, y, z) => x * x + y * y + z * z <= rad * rad * 4, (int)(rad * 2) + 2); for (int i = 0; i < p.Count; i++) p[i] += c; return p; }
    static List<Vector3> Brick() => Voxels(2.5f, (x, y, z) => Math.Abs(x) <= 2 && Math.Abs(y) <= 1 && Math.Abs(z) <= 5, 6);
    static List<Vector3> Cloud(int n, int seed) { var r = new Random(seed); var p = new List<Vector3>(); for (int i = 0; i < n; i++) p.Add(new Vector3((float)r.NextDouble() * 20 - 10, (float)r.NextDouble() * 8 - 4, (float)r.NextDouble() * 30 - 15)); return p; }
    static List<Vector3> Two() { var p = Sphere(3, new Vector3(-8, 0, 0)); p.AddRange(Sphere(2, new Vector3(9, 3, 4))); return p; }
    static List<Vector3> Line() { var p = new List<Vector3>(); for (int i = 0; i < 40; i++) p.Add(new Vector3(0, 0, i * 0.5f)); return p; }

    /// <summary>The frame Shape lays the grid out in, and R.</summary>
    static (Vector3 a, Vector3 b, float R) Frame(Vector3 f, List<Vector3> pts)
    {
        Vector3 a = Vector3.Normalize(Vector3.Cross(f, Math.Abs(f.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY)), b = Vector3.Cross(f, a);
        float xa = float.MaxValue, xb = float.MinValue, ya = float.MaxValue, yb = float.MinValue;
        foreach (var q in pts) { float x = Vector3.Dot(q, a), y = Vector3.Dot(q, b); xa = Math.Min(xa, x); xb = Math.Max(xb, x); ya = Math.Min(ya, y); yb = Math.Max(yb, y); }
        return (a, b, 0.5f * Math.Max(xb - xa, yb - ya));
    }

    static void Compare(FlightField ff, string name, Vector3 f, List<Vector3> pts)
    {
        var (a, b, R) = Frame(f, pts);
        var rf = new ReferenceShock();
        rf.ShockGrid(f, a, b, R, pts, out var P3, out var past);
        var ring = rf.SmoothRing(rf.Rim(P3, past, a, b, out var ringC), FlightField.RimSmooth);
        ff.Layout(f, a, b, R, pts);
        string d;
        Check((d = Diff(ff.P3, P3, Same)) == null, $"{name}: P3 {d}");
        Check((d = Diff(ff.Past, past, Same)) == null, $"{name}: past {d}");
        Check((d = Diff(ff.Has, rf._hullHas, (x, y) => x == y)) == null, $"{name}: hull cells {d}");
        Check((d = Diff(ff.Back, rf._hullBack, Same)) == null, $"{name}: chord back {d}");
        Check((d = Diff(ff.Surf, rf._hullSurf, Same)) == null, $"{name}: hull surface {d}");
        Check((d = Diff(ff.Ring, ring, Same)) == null, $"{name}: rim {d}");
        Check(Same(ff.RingC, ringC), $"{name}: rim centre");
        Check((d = Diff(ff.Frame, new[] { rf._ga.X, rf._ga.Y, rf._ga.Z, rf._gb.X, rf._gb.Y, rf._gb.Z, rf._gx0, rf._gy0, rf._gdx, rf._gdy }, Same)) == null, $"{name}: grid frame {d}");
    }

    static int Main()
    {
        var hulls = new (string, List<Vector3>)[] { ("plane", Plane()), ("sphere", Sphere(5, Vector3.Zero)), ("brick", Brick()), ("cloud", Cloud(3000, 1)), ("two", Two()), ("line", Line()), ("point", new List<Vector3> { new Vector3(1, 2, 3) }) };
        var rng = new Random(5);
        var flows = new List<Vector3> { -Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, Vector3.Normalize(new Vector3(0.3f, -0.2f, -1f)), Vector3.Normalize(new Vector3(-0.7f, 0.4f, 0.6f)) };
        for (int k = 0; k < 3; k++) flows.Add(Vector3.Normalize(new Vector3((float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1)));
        // one field throughout: its two sets and working grids reused, across sizes and knobs
        var ff = new FlightField();
        var knobs = new (int res, int blur, int dilate, int smooth)[] { (96, 12, 2, 0), (17, 12, 2, 0), (40, 0, 0, 0), (96, 5, 3, 2), (96, 12, 2, 0) };
        foreach (var (res, blur, dilate, smooth) in knobs)
        {
            FlightField.Res = res; FlightField.Blur = blur; FlightField.Dilate = dilate; FlightField.RimSmooth = smooth;
            foreach (var (hn, pts) in hulls)
                foreach (var f in flows)
                    Compare(ff, $"{hn} f({f.X:F2},{f.Y:F2},{f.Z:F2}) res {res} blur {blur} dilate {dilate} smooth {smooth}", f, pts);
        }
        // the same layout twice in a row and back again (a set two layouts old refilled over stale values)
        FlightField.Res = 96; FlightField.Blur = 12; FlightField.Dilate = 2; FlightField.RimSmooth = 0;
        var plane = hulls[0].Item2;
        for (int k = 0; k < 4; k++) Compare(ff, $"plane again {k}", k % 2 == 0 ? flows[4] : flows[5], plane);
        Compare(ff, "sphere after plane", flows[4], hulls[1].Item2);

        // ── the timings: the default grid (96), a re-layout of each hull along an oblique flow ──
        foreach (var (hn, pts) in new[] { hulls[0], hulls[1], hulls[3] })
        {
            var f = flows[4]; var (a, b, R) = Frame(f, pts);
            var rf = new ReferenceShock();
            for (int w = 0; w < 2; w++) { rf.ShockGrid(f, a, b, R, pts, out _, out _); ff.Layout(f, a, b, R, pts); }
            const int reps = 8;
            long gc0 = GC.GetAllocatedBytesForCurrentThread(); var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int r = 0; r < reps; r++) { rf.ShockGrid(f, a, b, R, pts, out var P3, out var past); rf.SmoothRing(rf.Rim(P3, past, a, b, out _), 0); }
            double tOld = sw.Elapsed.TotalMilliseconds / reps; long aOld = (GC.GetAllocatedBytesForCurrentThread() - gc0) / reps;
            gc0 = GC.GetAllocatedBytesForCurrentThread(); sw.Restart();
            for (int r = 0; r < reps; r++) ff.Layout(f, a, b, R, pts);
            double tNew = sw.Elapsed.TotalMilliseconds / reps; long aNew = (GC.GetAllocatedBytesForCurrentThread() - gc0) / reps;
            Console.WriteLine($"  {hn,-6} {pts.Count,5} points, res 96: original {tOld,6:F2} ms {aOld / 1024,5} KB  now {tNew,6:F2} ms {aNew / 1024,5} KB a re-layout");
        }
        Console.WriteLine(_fails == 0 ? $"ShockGridTests: all {_checks} checks passed" : $"ShockGridTests: {_fails} of {_checks} checks FAILED");
        return _fails == 0 ? 0 : 1;
    }
}
