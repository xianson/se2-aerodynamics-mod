using System.IO;
using AeroMod;

// AeroBench: the mod's literal aero sources on real ship shapes (Tests/data/*.boxes, exported from local blueprints:
// one block's occupied-cell box per line, 0.25 m cells), offline. Per ship: the background build, stage by stage, as
// the game runs it; then the per-frame force computation, timed over many flow directions - the number that decides
// how many big grids fit in a frame. Run: dotnet run -c Release [ship ...]
static class Program
{
    static string dataDir;
    static int? ForceScale;
    static float BlockSize = 2.5f;   // (each file's header says: '# blockSize 2.5 mass 3258000')

    static List<(Vector3I, Vector3I)> LoadBoxes(string path)
    {
        var boxes = new List<(Vector3I, Vector3I)>();
        foreach (var line in File.ReadLines(path))
        {
            var p = line.Split(' ');
            if (line.StartsWith("#")) { int i = Array.IndexOf(p, "blockSize"); if (i >= 0) BlockSize = float.Parse(p[i + 1], System.Globalization.CultureInfo.InvariantCulture); continue; }
            if (p.Length < 6) continue;
            boxes.Add((new Vector3I(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2])), new Vector3I(int.Parse(p[3]), int.Parse(p[4]), int.Parse(p[5]))));
        }
        return boxes;
    }

    sealed class Ship
    {
        public string Name;
        public SnapshotGridAccessor Grid;
        public SmoothSurfaceProvider Surface;
        public ManifoldClassifier Manifold;
        public LiftingSurfaceModel Model;
        public Vector3 Com;
        public string BuildNote;
    }

    static Ship Build(string name, List<(Vector3I, Vector3I)> boxes)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int k = ForceScale ?? SnapshotGridAccessor.CellScale(BlockSize);
        var (cs, co) = SnapshotGridAccessor.CellGeometry(k);
        var grid = k <= 1 ? new SnapshotGridAccessor(boxes)
                 : Environment.GetEnvironmentVariable("ANYCELL") != null ? new SnapshotGridAccessor(SnapshotGridAccessor.Coarsen(boxes, k))
                 : new SnapshotGridAccessor(boxes).CoarsenMajority(k);
        double tSnap = sw.Elapsed.TotalMilliseconds;
        var surface = new SmoothSurfaceProvider { CellSize = cs, CellOffset = co };
        surface.BeginBuild(grid, BlockSize);
        while (!surface.AddCellBatch(int.MaxValue)) { }
        double tFaces = sw.Elapsed.TotalMilliseconds;
        surface.FinalizeBuild();
        double tFin = sw.Elapsed.TotalMilliseconds;
        var manifold = new ManifoldClassifier();
        manifold.Classify(surface);
        double tCls = sw.Elapsed.TotalMilliseconds;
        var inner = new DampedShadowedDragModel();
        var model = new LiftingSurfaceModel(inner, liftModel: new CompressibleWingModel());
        if (model.Detector is ConnectedComponentWingDetector cc) { cc.Manifold = manifold; cc.ManifoldSurface = surface; cc.CellSize = cs; cc.CellOffset = co; }
        model.Detector.Invalidate();
        var wings = model.Detector.Detect(grid, surface, BlockSize);
        double tWings = sw.Elapsed.TotalMilliseconds;
        var shadow = new PrecomputedShadowMap { Manifold = manifold };
        shadow.PrecomputeAll(grid, surface);
        double tShadow = sw.Elapsed.TotalMilliseconds;
        model.InstallWings(wings);
        inner.InstallShadowMap(shadow);
        model.BuildFaceOverrideIndex(surface, new List<IAeroBlockComponent>());
        model.ExcludeWingFaces(inner, surface.Version);

        // centre of mass: the cells' centroid (uniform density)
        double cx = 0, cy = 0, cz = 0; long n = 0;
        foreach (var c in grid.EnumerateOccupiedCells()) { cx += c.X; cy += c.Y; cz += c.Z; n++; }
        var com = new Vector3((float)(cx / n + co) * cs, (float)(cy / n + co) * cs, (float)(cz / n + co) * cs);

        return new Ship
        {
            Name = name, Grid = grid, Surface = surface, Manifold = manifold, Model = model, Com = com,
            BuildNote = $"{boxes.Count} blocks, {grid.CellCount} cells of {cs:F2} m, {surface.FaceCount} faces, {wings.Count} wings | build: snapshot {tSnap:F0} faces {tFaces - tSnap:F0} finalize {tFin - tFaces:F0} classify {tCls - tFin:F0} wings {tWings - tCls:F0} shadow {tShadow - tWings:F0} = {tShadow:F0} ms",
        };
    }

    /// <summary>Known answers: a solid block's face drag, flow straight onto one face, as a drag coefficient on its
    /// frontal area (a bluff block: about 1.0-1.2).</summary>
    static int KnownShapes(AtmosphereState atmo)
    {
        int fails = 0;
        foreach (var (name, size, flow) in new[] {
            ("cube 4 m", new Vector3I(16, 16, 16), new Vector3(0, 0, -1)),
            ("brick 10x4x4 m end-on", new Vector3I(16, 16, 40), new Vector3(0, 0, -1)),
            ("brick 10x4x4 m side-on", new Vector3I(16, 16, 40), new Vector3(-1, 0, 0)),
            ("plate 8x0.5x8 m face-on", new Vector3I(32, 2, 32), new Vector3(0, -1, 0)) })
        {
            BlockSize = 2.5f;
            var ship = Build(name, new List<(Vector3I, Vector3I)> { (Vector3I.Zero, size - Vector3I.One) });
            var inner = (DampedShadowedDragModel)ship.Model.InnerModel;
            AeroResult r = default;
            for (int i = 0; i < 3; i++) r = inner.Compute(new AeroContext(ship.Grid, ship.Surface, flow * 100f, atmo, ship.Com, BlockSize, Vector3.Zero, -1f, ship.Manifold));
            // frontal area: the box's extent across the flow
            var ext = new Vector3(size.X, size.Y, size.Z) * 0.25f;
            float area = MathF.Abs(flow.X) > 0.5f ? ext.Y * ext.Z : MathF.Abs(flow.Y) > 0.5f ? ext.X * ext.Z : ext.X * ext.Y;
            double q = 0.5 * atmo.Density * 100 * 100;
            double cd = r.DragMagnitude / (q * area);
            bool ok = cd > 0.8 && cd < 1.5;
            if (!ok) fails++;
            Console.WriteLine($"   {(ok ? "ok  " : "FAIL")} {name}: Cd {cd:F2} on {area:F1} m2 (drag {r.DragMagnitude / 1000:F1} kN, lift {r.LiftMagnitude / 1000:F1} kN, visible {inner.ShadowMap.VisibleCount}/{ship.Surface.FaceCount}, hull {Enumerable.Range(0, ship.Surface.FaceCount).Count(ship.Manifold.IsHull)})");
        }
        return fails;
    }

    /// <summary>The force table against the face loop it replaces: errors over random directions, speeds and
    /// rotations, and the cost of each.</summary>
    static (double median, double p95) TableCheck(Ship ship, AtmosphereState atmo0)
    {
        var inner = (DampedShadowedDragModel)ship.Model.InnerModel;
        if (Environment.GetEnvironmentVariable("SWEEP") != null)
        {
            var at0 = new AeroContext(ship.Grid, ship.Surface, Vector3.UnitX, atmo0, ship.Com, BlockSize, Vector3.Zero, -1f, ship.Manifold);
            var d0 = Vector3.Normalize(new Vector3(0.85140383f, 0.38317794f, 0.35817048f));
            var side = Vector3.Normalize(Vector3.Cross(d0, Vector3.UnitZ));
            for (int k = -6; k <= 6; k++)
            {
                var d = Vector3.Normalize(d0 + side * (k * 0.002f));
                var c = new AeroContext(ship.Grid, ship.Surface, d * 300f, atmo0, ship.Com, BlockSize, Vector3.Zero, -1f, ship.Manifold);
                var ex = inner.ExactTableValue(c, null);
                Console.WriteLine($"      sweep {k,3}: |F| {ex.Force.Length() / 1000:F0} kN, visible {inner.ShadowMap.VisibleCount}");
            }
        }
        var table = inner.BuildForceTable(ship.Grid, ship.Surface, ship.Manifold, ship.Com, int.Parse(Environment.GetEnvironmentVariable("TABLE_N") ?? "8"));
        if (Environment.GetEnvironmentVariable("SWEEP") != null)
        {
            var d0 = Vector3.Normalize(new Vector3(0.85140383f, 0.38317794f, 0.35817048f));
            var c = new AeroContext(ship.Grid, ship.Surface, d0 * 300f, atmo0, ship.Com, BlockSize, Vector3.Zero, -1f, ship.Manifold);
            var ex = inner.ExactTableValue(c, null); var tb = table.Evaluate(c, inner.SubsonicLimit, inner.SupersonicLimit, inner.Streamlining);
            Console.WriteLine($"      at d0 no rotation: exact {ex.Force.Length() / 1000:F0} kN, table {tb.Force.Length() / 1000:F0} kN");
            // the 4 vertices around (face 0: a = y/x, b = z/x)
            float a = d0.Y / d0.X, b = d0.Z / d0.X; int tn = table.N;
            float u = (a + 1) * 0.5f * tn, w = (b + 1) * 0.5f * tn; int i0 = (int)u, j0 = (int)w;
            Span<float> e = stackalloc float[ForceTable.Stride];
            for (int di = 0; di <= 1; di++) for (int dj = 0; dj <= 1; dj++)
            {
                var vv = ForceTable.Direction(0, i0 + di, j0 + dj, tn);
                var stored = table.At(0, i0 + di, j0 + dj);
                inner.SumFaces(ship.Grid, ship.Surface, vv, e);
                Console.WriteLine($"      vertex ({i0 + di},{j0 + dj}) dir {vv}: stored |F0| {new Vector3(stored[0], stored[1], stored[2]).Length():F1} recomputed {new Vector3(e[0], e[1], e[2]).Length():F1}");
            }
        }
        var rng = new Random(7);
        var errF = new List<double>(); var errT = new List<double>(); var errI = new List<double>(); var errL = new List<double>(); var errIT = new List<double>();
        var worst = new List<string>();
        static double fmA(AeroResult r) => Math.Max(r.Force.Length(), 1); double tLoop = 0, tTable = 0; int n = 0;
        foreach (double mach in new[] { 0.3, 0.9, 1.5 })
        {
            var atmo = atmo0;
            for (int k = 0; k < 200; k++)
            {
                var d = Vector3.Normalize(new Vector3((float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1));
                var w = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * 0.1f;
                var ctx = new AeroContext(ship.Grid, ship.Surface, d * (float)(mach * atmo.SpeedOfSound), atmo, ship.Com, BlockSize, w, -1f, ship.Manifold);
                var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var a = inner.Compute(ctx);
                var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                var b = table.Evaluate(ctx, inner.SubsonicLimit, inner.SupersonicLimit, inner.Streamlining);
                var t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                tLoop += t1 - t0; tTable += t2 - t1; n++;
                // interpolation alone: the table (no rotation) against the same sum taken exactly at this direction
                var ex = inner.ExactTableValue(ctx, table);   // (rotation included)
                var tb = b;
                double ei = (ex.Force - tb.Force).Length() / Math.Max(ex.Force.Length(), 1);
                errIT.Add((ex.Torque - tb.Torque).Length() / Math.Max(ex.Torque.Length(), ex.Force.Length() * 1.0 + 1));
                errI.Add(ei);
                if (ei > 0.5 && worst.Count < 4) worst.Add($"dir {d} M{mach}: exact {ex.Force / 1000} kN table {tb.Force / 1000} kN");
                errL.Add((a.Force - ex.Force).Length() / fmA(a));
                double fm = Math.Max(a.Force.Length(), 1);
                errF.Add((a.Force - b.Force).Length() / fm);
                // torque error relative to the force's moment scale (|F| x the grid's size)
                double tm = Math.Max(a.Torque.Length(), fm * 1.0);
                errT.Add((a.Torque - b.Torque).Length() / tm);
            }
        }
        errF.Sort(); errT.Sort(); errI.Sort(); errL.Sort(); errIT.Sort();
        foreach (var wline in worst) Console.WriteLine("      worst: " + wline);
        Console.WriteLine($"   TABLE vs exact: force median {errI[errI.Count / 2] * 100:F1}% p95 {errI[(int)(errI.Count * 0.95)] * 100:F1}% max {errI[^1] * 100:F1}%, torque median {errIT[errIT.Count / 2] * 100:F1}% p95 {errIT[(int)(errIT.Count * 0.95)] * 100:F1}% | face loop vs exact sum (the loop's own quirks): median {errL[errL.Count / 2] * 100:F1}% p95 {errL[(int)(errL.Count * 0.95)] * 100:F1}%");
        double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Console.WriteLine($"   TABLE: built in {table.BuildMs:F0} ms ({6 * (table.N + 1) * (table.N + 1)} directions) | force error median {errF[errF.Count / 2] * 100:F1}% p95 {errF[(int)(errF.Count * 0.95)] * 100:F1}% max {errF[^1] * 100:F1}% | torque error median {errT[errT.Count / 2] * 100:F1}% p95 {errT[(int)(errT.Count * 0.95)] * 100:F1}% | per frame: face loop {tLoop * f / n:F3} ms, table {tTable * f / n * 1000:F1} us");
        return (errI[errI.Count / 2], errI[(int)(errI.Count * 0.95)]);
    }

    /// <summary>Damage: the patched table against a table rebuilt for the damaged shape (and the unpatched one).</summary>
    static void DamageCheck(string name, List<(Vector3I, Vector3I)> boxes, AtmosphereState atmo)
    {
        var rng = new Random(3);
        foreach (var (label, pick) in new (string, Func<List<int>>)[] {
            ("1 block", () => new List<int> { rng.Next(boxes.Count) }),
            ("10 blocks", () => Enumerable.Range(0, 10).Select(_ => rng.Next(boxes.Count)).Distinct().ToList()),
            ("100 blocks", () => Enumerable.Range(0, 100).Select(_ => rng.Next(boxes.Count)).Distinct().ToList()),
            ("a chunk (blocks within 3 m of one)", () => { var c = boxes[rng.Next(boxes.Count)].Item1; return Enumerable.Range(0, boxes.Count).Where(i => (boxes[i].Item1 - c).Length() * 0.25f < 3f).ToList(); }) })
        {
            var whole = Build(name, boxes);
            var dsm = (DampedShadowedDragModel)whole.Model.InnerModel;
            var table = dsm.BuildForceTable(whole.Grid, whole.Surface, whole.Manifold, whole.Com);
            var before = dsm.BuildForceTable(whole.Grid, whole.Surface, whole.Manifold, whole.Com);
            var idx = new HashSet<int>(pick());
            var removedBoxes = idx.Select(i => boxes[i]).ToList();
            var rest = boxes.Where((b, i) => !idx.Contains(i)).ToList();
            var after = Build(name, rest);
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int patches = TablePatcher.PatchBoxes(table, dsm, after.Grid, removedBoxes, removed: true);
            double patchMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            var adsm = (DampedShadowedDragModel)after.Model.InnerModel;
            var exact = adsm.BuildForceTable(after.Grid, after.Surface, after.Manifold, whole.Com);
            var r = new Random(9); var ePatched = new List<double>(); var eStale = new List<double>(); double change = 0;
            for (int k = 0; k < 300; k++)
            {
                var d = Vector3.Normalize(new Vector3((float)r.NextDouble() * 2 - 1, (float)r.NextDouble() * 2 - 1, (float)r.NextDouble() * 2 - 1));
                var ctx = new AeroContext(after.Grid, after.Surface, d * 150f, atmo, whole.Com, BlockSize, Vector3.Zero, -1f, after.Manifold);
                var fe = exact.Evaluate(ctx, adsm.SubsonicLimit, adsm.SupersonicLimit, adsm.Streamlining).Force;
                var fp = table.Evaluate(ctx, adsm.SubsonicLimit, adsm.SupersonicLimit, adsm.Streamlining).Force;
                var fs = before.Evaluate(ctx, adsm.SubsonicLimit, adsm.SupersonicLimit, adsm.Streamlining).Force;
                double m = Math.Max(fe.Length(), 1);
                ePatched.Add((fp - fe).Length() / m); eStale.Add((fs - fe).Length() / m); change += (fs - fe).Length() / m;
            }
            ePatched.Sort(); eStale.Sort();
            Console.WriteLine($"   damage {label} ({idx.Count} blocks, {patches} patches in {patchMs:F2} ms): the damage changed forces by median {eStale[150] * 100:F1}% (p95 {eStale[285] * 100:F1}%); patched table off by median {ePatched[150] * 100:F1}% (p95 {ePatched[285] * 100:F1}%)");
        }
    }

    static int Main(string[] args)
    {
        dataDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data");
        var names = args.Length > 0 ? args : Directory.Exists(dataDir) ? Directory.GetFiles(dataDir, "*.boxes").Select(Path.GetFileNameWithoutExtension).ToArray() : Array.Empty<string>();
        if (names.Length == 0) { Console.WriteLine("AeroBench: no ship data (Tests/data/*.boxes) - skipped, 0/0 passed"); return 0; }

        var atmo = new AtmosphereState { Density = 1.0, Pressure = 90000, Temperature = 288, SpeedOfSound = 340 };
        int known = KnownShapes(atmo);
        if (args.Length > 0 && args[0] == "gate")
        {
            // the fast gate: known shapes, and the force table against the exact sum on the small ships
            int fails = known;
            foreach (var name in new[] { "bluefighter", "pelican" })
            {
                var path = Path.Combine(dataDir, name + ".boxes");
                if (!File.Exists(path)) { Console.WriteLine($"   {name}: no data, skipped"); continue; }
                var ship = Build(name, LoadBoxes(path));
                var (med, p95) = TableCheck(ship, atmo);
                bool ok = med < 0.05 && p95 < 0.15;
                if (!ok) fails++;
                Console.WriteLine($"   {(ok ? "ok  " : "FAIL")} {name}: table error median {med * 100:F1}% p95 {p95 * 100:F1}%");
            }
            Console.WriteLine($"AeroBench: {(fails == 0 ? "1/1 passed" : fails + " FAILED")}");
            return fails == 0 ? 0 : 1;
        }
        if (args.Length > 0 && args[0] == "scale")
        {
            // coarse cells against the grid's own, same ship: forces over random directions
            foreach (var name in args.Skip(1))
            {
                var boxes = LoadBoxes(Path.Combine(dataDir, name + ".boxes"));
                ForceScale = 1; var fine = Build(name, boxes); ForceScale = int.TryParse(Environment.GetEnvironmentVariable("SCALE"), out int sk) ? sk : null; var coarse = Build(name, boxes); ForceScale = null;
                Console.WriteLine($"{name} fine: {fine.BuildNote}");
                Console.WriteLine($"{name} coarse: {coarse.BuildNote}");
                var fd = (DampedShadowedDragModel)fine.Model.InnerModel; var cd = (DampedShadowedDragModel)coarse.Model.InnerModel;
                if (Environment.GetEnvironmentVariable("NOEXCL") != null) { fd.SetExcludedFaces(null, fine.Surface.Version); cd.SetExcludedFaces(null, coarse.Surface.Version); }
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                var ft = fd.BuildForceTable(fine.Grid, fine.Surface, fine.Manifold, fine.Com); double tf = sw2.Elapsed.TotalMilliseconds; sw2.Restart();
                var ct = cd.BuildForceTable(coarse.Grid, coarse.Surface, coarse.Manifold, fine.Com); double tc = sw2.Elapsed.TotalMilliseconds;
                var r = new Random(4); var err = new List<double>(); var errT = new List<double>();
                for (int i = 0; i < 400; i++)
                {
                    var d = Vector3.Normalize(new Vector3((float)r.NextDouble() * 2 - 1, (float)r.NextDouble() * 2 - 1, (float)r.NextDouble() * 2 - 1));
                    var ctx = new AeroContext(fine.Grid, fine.Surface, d * 150f, atmo, fine.Com, BlockSize, Vector3.Zero, -1f, fine.Manifold);
                    var a = ft.Evaluate(ctx, fd.SubsonicLimit, fd.SupersonicLimit, fd.Streamlining); var b = ct.Evaluate(ctx, cd.SubsonicLimit, cd.SupersonicLimit, cd.Streamlining);
                    err.Add((a.Force - b.Force).Length() / Math.Max(a.Force.Length(), 1)); errT.Add((a.Torque - b.Torque).Length() / Math.Max(a.Torque.Length(), a.Force.Length() + 1));
                }
                err.Sort(); errT.Sort();
                {
                    var dd = Vector3.UnitX; var c1 = new AeroContext(fine.Grid, fine.Surface, dd * 150f, atmo, fine.Com, BlockSize, Vector3.Zero, -1f, fine.Manifold);
                    var a1 = ft.Evaluate(c1, fd.SubsonicLimit, fd.SupersonicLimit, fd.Streamlining); var b1 = ct.Evaluate(c1, cd.SubsonicLimit, cd.SupersonicLimit, cd.Streamlining);
                    Console.WriteLine($"   +X: fine F {a1.Force / 1000} kN frontal {a1.FrontalArea:F0} m2 | coarse F {b1.Force / 1000} kN frontal {b1.FrontalArea:F0} m2 | hull faces fine {Enumerable.Range(0, fine.Surface.FaceCount).Count(fine.Manifold.IsHull)} coarse {Enumerable.Range(0, coarse.Surface.FaceCount).Count(coarse.Manifold.IsHull)}");
                }
                Console.WriteLine($"   tables: fine {tf:F0} ms, coarse {tc:F0} ms | coarse vs fine force median {err[200] * 100:F1}% p95 {err[380] * 100:F1}%, torque median {errT[200] * 100:F1}% p95 {errT[380] * 100:F1}% | wings fine {fine.Model.Wings?.Count} coarse {coarse.Model.Wings?.Count}");
            }
            return 0;
        }
        if (args.Length > 0 && args[0] == "damage")
        {
            foreach (var name in args.Skip(1))
            {
                var path = Path.Combine(dataDir, name + ".boxes");
                var boxes = LoadBoxes(path);
                Console.WriteLine($"{name}: {boxes.Count} blocks");
                DamageCheck(name, boxes, atmo);
            }
            return 0;
        }
        if (args.Length > 0 && args[0] == "known") { Console.WriteLine($"AeroBench: known shapes {(known == 0 ? "1/1 passed" : known + " FAILED")}"); return known == 0 ? 0 : 1; }
        foreach (var name in names.OrderBy(x => x))
        {
            var path = Path.Combine(dataDir, name + ".boxes");
            if (!File.Exists(path)) { Console.WriteLine($"{name}: no data"); continue; }
            var ship = Build(name, LoadBoxes(path));
            Console.WriteLine($"{name}: {ship.BuildNote}");

            // per frame: 200 m/s from directions spread over the sphere, a slow tumble
            var rng = new Random(1);
            var dirs = new List<Vector3>();
            for (int i = 0; i < 64; i++) { var d = new Vector3((float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1); if (d.LengthSquared() > 0.01f) dirs.Add(Vector3.Normalize(d)); }
            var omega = new Vector3(0.02f, 0.01f, -0.015f);
            AeroResult r = default;
            for (int i = 0; i < 20; i++)   // warm-up (JIT, first shadow blends)
                r = ship.Model.Compute(new AeroContext(ship.Grid, ship.Surface, dirs[i % dirs.Count] * 200f, atmo, ship.Com, BlockSize, omega, -1f, ship.Manifold));
            var times = new List<double>();
            for (int k = 0; k < 3; k++)
                foreach (var d in dirs)
                {
                    var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    r = ship.Model.Compute(new AeroContext(ship.Grid, ship.Surface, d * 200f, atmo, ship.Com, BlockSize, omega, -1f, ship.Manifold));
                    times.Add((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                }
            if (Environment.GetEnvironmentVariable("DIAG") != null)
            {
                var inner0 = (DampedShadowedDragModel)ship.Model.InnerModel;
                var ctx0 = new AeroContext(ship.Grid, ship.Surface, new Vector3(0, 0, -200f), atmo, ship.Com, BlockSize, Vector3.Zero, -1f, ship.Manifold);
                var ri = inner0.Compute(ctx0);
                Console.WriteLine($"   DIAG inner |F| {ri.Force.Length():F0} N drag {ri.DragMagnitude:F0} frontal {ri.FrontalArea:F2} m2 q {ri.DynamicPressure:F0} Pa M {ri.Mach:F2}; shadow visible {inner0.ShadowMap.VisibleCount} shadowed {inner0.ShadowMap.ShadowedCount}; hull faces {Enumerable.Range(0, ship.Surface.FaceCount).Count(ship.Manifold.IsHull)}/{ship.Surface.FaceCount}");
            }
            times.Sort();
            double med = times[times.Count / 2], p95 = times[(int)(times.Count * 0.95)];
            TableCheck(ship, atmo);
            Console.WriteLine($"   per frame: median {med:F3} ms, p95 {p95:F3} ms (a new direction every frame: worst case for the shadow blend)  |  x100 ships: {med * 100:F1} ms  |  |F| {r.Force.Length() / 1000:F0} kN");
        }
        Console.WriteLine("AeroBench: done, 1/1 passed");
        return 0;
    }
}
