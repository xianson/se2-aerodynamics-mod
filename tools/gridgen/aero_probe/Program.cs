using System.IO;
using AeroMod;

// gridgen aero probe: a generated plane through the aero mod's own build -> wing detection -> force model, offline.
// Input: one .boxes file per GRID (one block's occupied-cell box per line, inclusive, 0.25 m cells, header
// '# blockSize 0.5'), all in the main grid's cell frame.  The first is the main grid; the others (flap head grids)
// are computed as separate aero grids, as in game: their own surface, wings and shadowing, no shadow from the main
// grid.  All grids fly the same true airflow (nose at grid -Z, travel a = (0, -sin a, -cos a)); torques about one
// CoM.  Prints the wings found per grid, then the totals over an AoA sweep, CL/CD on the frontal area (the plane's
// projection on the X-Y plane) and the neutral point vs the CoM.
// Thrusters (--thruster px,py,pz,bx,by,bz,fx,fy,fz,area: block centre m, block min cell, Forward, capture m2) get
// the AirIntake the mod's DefaultMappings gives them (atmospheric thrusters only, 0.25 x width^2), evaluated as
// AeroGridComponent does (AeroComponentRegistry.EvaluateAll on the main grid); --no-intake leaves them out.
//   AeroProbe MAIN.boxes [GRID.boxes ...] [--speed 220] [--com cx,cy,cz (cells)] [--thruster ...]* [--no-intake] [--quiet]
static class Program
{
    sealed class Grid
    {
        public string Name;
        public float BlockSize = 0.5f;
        public List<(Vector3I, Vector3I)> Boxes = new();
        public SnapshotGridAccessor Acc;
        public SmoothSurfaceProvider Surface;
        public ManifoldClassifier Manifold;
        public LiftingSurfaceModel Model;
        public List<LiftingSurface> Wings;
        public float Cs, Co;
        public Vector3? Ax, Ay, Az;     // '# frame': the grid's local axes in the main frame (head grids)
        public Vector3 Flow(Vector3 vMainDir, Vector3 vLocalDir, bool testForces)
            => testForces && Ax.HasValue ? Ax.Value * vLocalDir.X + Ay.Value * vLocalDir.Y + Az.Value * vLocalDir.Z : vMainDir;
    }

    static Grid Load(string path)
    {
        var g = new Grid { Name = Path.GetFileName(path) };
        foreach (var line in File.ReadLines(path))
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (line.StartsWith("#"))
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                int i = Array.IndexOf(p, "blockSize"); if (i >= 0) g.BlockSize = float.Parse(p[i + 1], ci);
                int f = Array.IndexOf(p, "frame");
                if (f >= 0)
                {
                    var r = p.Skip(f + 1).Take(9).Select(x => float.Parse(x, ci)).ToArray();   // row-major, local -> main
                    g.Ax = new Vector3(r[0], r[3], r[6]); g.Ay = new Vector3(r[1], r[4], r[7]); g.Az = new Vector3(r[2], r[5], r[8]);
                }
                continue;
            }
            if (p.Length < 6) continue;
            g.Boxes.Add((new Vector3I(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2])), new Vector3I(int.Parse(p[3]), int.Parse(p[4]), int.Parse(p[5]))));
        }
        return g;
    }

    static void Build(Grid g)
    {
        int k = SnapshotGridAccessor.CellScale(g.BlockSize);
        (g.Cs, g.Co) = SnapshotGridAccessor.CellGeometry(k);
        g.Acc = k <= 1 ? new SnapshotGridAccessor(g.Boxes) : new SnapshotGridAccessor(g.Boxes).CoarsenMajority(k);
        g.Surface = new SmoothSurfaceProvider { CellSize = g.Cs, CellOffset = g.Co };
        g.Surface.BeginBuild(g.Acc, g.BlockSize);
        while (!g.Surface.AddCellBatch(int.MaxValue)) { }
        g.Surface.FinalizeBuild();
        g.Manifold = new ManifoldClassifier();
        g.Manifold.Classify(g.Surface);
        var inner = new DampedShadowedDragModel();
        g.Model = new LiftingSurfaceModel(inner, liftModel: new CompressibleWingModel());
        if (g.Model.Detector is ConnectedComponentWingDetector cc) { cc.Manifold = g.Manifold; cc.ManifoldSurface = g.Surface; cc.CellSize = g.Cs; cc.CellOffset = g.Co; }
        g.Model.Detector.Invalidate();
        g.Wings = g.Model.Detector.Detect(g.Acc, g.Surface, g.BlockSize);
        var shadow = new PrecomputedShadowMap { Manifold = g.Manifold };
        shadow.PrecomputeAll(g.Acc, g.Surface);
        g.Model.InstallWings(g.Wings);
        inner.InstallShadowMap(shadow);
        g.Model.BuildFaceOverrideIndex(g.Surface, new List<IAeroBlockComponent>());
        g.Model.ExcludeWingFaces(inner, g.Surface.Version);
    }

    static int Main(string[] args)
    {
        var files = new List<string>();
        float speed = 220f;
        Vector3? comCells = null;
        bool quiet = false, intake = true, testForces = false;
        float arefScale = 2.31f;
        var thrusters = new List<(Vector3 pos, Vector3I blk, Vector3 fwd, float area)>();
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--speed") speed = float.Parse(args[++i], ci);
            else if (args[i] == "--com") { var p = args[++i].Split(','); comCells = new Vector3(float.Parse(p[0], ci), float.Parse(p[1], ci), float.Parse(p[2], ci)); }
            else if (args[i] == "--quiet") quiet = true;
            else if (args[i] == "--no-intake") intake = false;
            else if (args[i] == "--testforces") testForces = true;   // as AeroEntryFx.TestForces: each grid along its own axes
            else if (args[i] == "--aref-scale") arefScale = float.Parse(args[++i], ci);
            else if (args[i] == "--thruster")
            {
                var t = args[++i].Split(',').Select(x => float.Parse(x, ci)).ToArray();
                thrusters.Add((new Vector3(t[0], t[1], t[2]), new Vector3I((int)t[3], (int)t[4], (int)t[5]), new Vector3(t[6], t[7], t[8]), t.Length > 9 ? t[9] : 30f));
            }
            else if (float.TryParse(args[i], System.Globalization.NumberStyles.Float, ci, out float s) && files.Count > 0 && !File.Exists(args[i])) speed = s;
            else files.Add(args[i]);
        }
        if (files.Count < 1) { Console.WriteLine("usage: AeroProbe MAIN.boxes [GRID.boxes ...] [--speed 220] [--com cx,cy,cz] [--quiet]"); return 2; }
        var grids = files.Select(Load).ToList();
        foreach (var g in grids) Build(g);
        var main = grids[0];
        var registry = new AeroComponentRegistry();
        if (intake)
            foreach (var t in thrusters)   // DefaultMappings: new AirIntake(pos, blockPos, -Forward, capture area)
                registry.Add(new AirIntake(t.pos, t.blk, facingDirection: -t.fwd, captureArea: t.area));
        if (registry.Count > 0)
        {
            main.Model.BuildFaceOverrideIndex(main.Surface, registry.Components);
            if (main.Model.InnerModel is DampedShadowedDragModel dsmEx) main.Model.ExcludeWingFaces(dsmEx, main.Surface.Version);
        }
        Vector3 com;
        if (comCells.HasValue) com = (comCells.Value + new Vector3(main.Co)) * main.Cs;
        else
        {
            double cx = 0, cy = 0, cz = 0; long n = 0;
            foreach (var c in main.Acc.EnumerateOccupiedCells()) { cx += c.X; cy += c.Y; cz += c.Z; n++; }
            com = new Vector3((float)(cx / n + main.Co) * main.Cs, (float)(cy / n + main.Co) * main.Cs, (float)(cz / n + main.Co) * main.Cs);
        }
        // frontal area: every grid's cells projected on X-Y
        var proj = new HashSet<(int, int)>();
        foreach (var c in main.Acc.EnumerateOccupiedCells()) proj.Add((c.X, c.Y));
        float projected = proj.Count * main.Cs * main.Cs;
        // reference area for CL / CD: the projected frontal area times --aref-scale (default 2.31: the in-game TestForces
        // readout's area, back-computed from its CD and drag - 41 m2 on straight_wing, 40 m2 on swept_jet - is 2.2-2.3x
        // the projected area of the main grid's cells; offline, the drag model's own FrontalArea comes out lower)
        float frontal = projected * arefScale;
        float wingArea = 0;
        int nWingGrids = 0;
        foreach (var g in grids)
        {
            float a = 0;
            foreach (var w in g.Wings) if (MathF.Abs(w.Normal.Y) > 0.5f) a += w.PlanformArea;
            wingArea += a;
            if (g.Wings.Count > 0) nWingGrids++;
            if (quiet && g != main) continue;
            Console.WriteLine($"{g.Name}: {g.Boxes.Count} blocks, {g.Acc.CellCount} cells of {g.Cs:F2} m, {g.Wings.Count} wings");
            foreach (var w in g.Wings)
            {
                Console.WriteLine($"  wing n=({w.Normal.X:F0},{w.Normal.Y:F0},{w.Normal.Z:F0}) area {w.PlanformArea:F1} m2 span {w.Span:F1} chord {w.MeanChord:F2} AR {w.AspectRatio:F2} t/c {w.ThicknessRatio:F2} sweep {w.SweepAngle * 57.3f:F0} centroid ({w.Centroid.X:F1},{w.Centroid.Y:F1},{w.Centroid.Z:F1}) CLa {w.CLAlpha:F2}");
                if (w.Cells != null && w.Cells.Count > 0)
                    Console.WriteLine($"     cells {w.Cells.Count}: X {w.Cells.Min(c => c.X)}..{w.Cells.Max(c => c.X)} Y {w.Cells.Min(c => c.Y)}..{w.Cells.Max(c => c.Y)} Z {w.Cells.Min(c => c.Z)}..{w.Cells.Max(c => c.Z)}");
            }
        }
        var atmo = new AtmosphereState { Density = 1.225, Pressure = 101325, Temperature = 288, SpeedOfSound = 340 };
        float q = 0.5f * 1.225f * speed * speed;
        Console.WriteLine($"  {grids.Count} grids ({nWingGrids} with wings), horizontal wing area {wingArea:F1} m2, reference {frontal:F1} m2 (projected {projected:F1} x {arefScale}); at {speed} m/s, nose -Z:");
        var aoas = new[] { -4f, 0f, 4f, 8f, 12f, 16f };
        var L = new float[aoas.Length]; var M = new float[aoas.Length]; var D = new float[aoas.Length]; var DI = new float[aoas.Length];
        for (int ai = 0; ai < aoas.Length; ai++)
        {
            float aoa = aoas[ai];
            float a = aoa * MathF.PI / 180f;
            var v = new Vector3(0, -MathF.Sin(a), -MathF.Cos(a)) * speed;
            var vd = Vector3.Normalize(v);
            var liftDir = Vector3.Normalize(new Vector3(0, MathF.Cos(a), -MathF.Sin(a)));
            float lift = 0, drag = 0, pitch = 0, dragMain = 0, compDrag = 0;
            foreach (var g in grids)
            {
                AeroResult r = default;
                var vg = g.Flow(v, v, testForces);
                for (int i = 0; i < 4; i++)
                    r = g.Model.Compute(new AeroContext(g.Acc, g.Surface, vg, atmo, com, g.BlockSize, Vector3.Zero, -1f, g.Manifold));
                var F = r.Force; var T = r.Torque;
                if (g == main && registry.Count > 0)
                {
                    var ctx = new AeroContext(g.Acc, g.Surface, vg, atmo, com, g.BlockSize, Vector3.Zero, -1f, g.Manifold);
                    var (cf, ct) = registry.EvaluateAll(ctx);
                    F += cf; T += ct; compDrag = -Vector3.Dot(cf, vd);
                }
                lift += Vector3.Dot(F, liftDir);
                float d = -Vector3.Dot(F, vd);
                drag += d;
                if (g == main) dragMain = d;
                pitch += T.X;
            }
            L[ai] = lift; M[ai] = pitch; D[ai] = drag; DI[ai] = compDrag;
            Console.WriteLine($"    AoA {aoa,5:F0}  lift {lift / 1000,8:F1} kN  drag {drag / 1000,8:F1} kN (main {dragMain / 1000,7:F1}, intakes {compDrag / 1000,7:F1})  CL {lift / (q * frontal),6:F3}  CD {drag / (q * frontal),6:F3}  CL(wing) {(wingArea > 0 ? lift / (q * wingArea) : 0),6:F3}  pitch {pitch / 1000,8:F1} kN·m");
        }
        // static stability over the flight range, as the in-game TestForces readout judges it: least-squares slopes
        // of the pitch torque (+X = nose up) against AoA and against lift, over 0..16 deg in 2 deg steps (the moment
        // is not linear: one interval - the old 4..8 deg - can read stable on a plane that is unstable overall).
        // NP aft of the CoM = -dM/dL (+ = stable), dL and dM both per degree.
        var fa = new List<double>(); var fl = new List<double>(); var fm = new List<double>();
        for (float aoa = 0f; aoa <= 16.01f; aoa += 2f)
        {
            float a = aoa * MathF.PI / 180f;
            var v = new Vector3(0, -MathF.Sin(a), -MathF.Cos(a)) * speed;
            var liftDir = Vector3.Normalize(new Vector3(0, MathF.Cos(a), -MathF.Sin(a)));
            float lift = 0, pitch = 0;
            foreach (var g in grids)
            {
                AeroResult r = default;
                var vg = g.Flow(v, v, testForces);
                for (int i = 0; i < 4; i++)
                    r = g.Model.Compute(new AeroContext(g.Acc, g.Surface, vg, atmo, com, g.BlockSize, Vector3.Zero, -1f, g.Manifold));
                var F = r.Force; var T = r.Torque;
                if (g == main && registry.Count > 0)
                {
                    var (cf, ct) = registry.EvaluateAll(new AeroContext(g.Acc, g.Surface, vg, atmo, com, g.BlockSize, Vector3.Zero, -1f, g.Manifold));
                    F += cf; T += ct;
                }
                lift += Vector3.Dot(F, liftDir); pitch += T.X;
            }
            fa.Add(aoa); fl.Add(lift); fm.Add(pitch);
        }
        static double Slope(List<double> x, List<double> y)
        {
            double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0;
            for (int i = 0; i < x.Count; i++) { sxy += (x[i] - mx) * (y[i] - my); sxx += (x[i] - mx) * (x[i] - mx); }
            return sxx > 0 ? sxy / sxx : double.NaN;
        }
        // NP = -(dM/dAoA) / (dL/dAoA): both against AoA, so its sign always agrees with dM/dAoA (the readout's)
        double dMdA = Slope(fa, fm), dLdA = Slope(fa, fl);
        double dMdL = Math.Abs(dLdA) > 1e-3 ? dMdA / dLdA : double.NaN;
        Console.WriteLine($"  pitch 0..16 deg: " + string.Join(" ", fm.Select(x => $"{x / 1000:F0}")) + $" kN·m; dM/dAoA {dMdA / 1000:F1} kN·m/deg");
        string np = !double.IsNaN(dMdL) ? $"{-dMdL:F2}" : "nan";
        Console.WriteLine($"  SUMMARY CL4 {L[2] / (q * frontal):F3} CL8 {L[3] / (q * frontal):F3} CD0 {D[1] / (q * frontal):F3} NP_aft_of_CoM {np} dMdAoA_kNm {dMdA / 1000:F1} frontal {frontal:F1} drag0_kN {D[1] / 1000:F0} intake0_kN {DI[1] / 1000:F0} CD0_no_intake {(D[1] - DI[1]) / (q * frontal):F3}");
        return 0;
    }

}
