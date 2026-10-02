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
    /// <summary>The faces of the damaged shape inside the dirty chunks (a surface built over them plus a margin).</summary>
    static readonly List<Vector3> _hideOnly = new();
    static List<SurfaceFace> LocalFaces(SnapshotGridAccessor gridAfter, HashSet<long> dirty, ChunkedTable ct, SmoothSurfaceProvider like)
    {
        float cs = like.CellSize, co = like.CellOffset, margin = 2f;
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (var k in dirty) { var (a, b) = ct.BoxOf(k); lo = Vector3.Min(lo, a); hi = Vector3.Max(hi, b); }
        lo -= new Vector3(margin); hi += new Vector3(margin);
        var cells = new List<(Vector3I, Vector3I)>();
        foreach (var c in gridAfter.EnumerateOccupiedCells())
        {
            var p = (new Vector3(c.X, c.Y, c.Z) + new Vector3(co)) * cs;
            if (p.X >= lo.X && p.X <= hi.X && p.Y >= lo.Y && p.Y <= hi.Y && p.Z >= lo.Z && p.Z <= hi.Z) cells.Add((c, c));
        }
        _hideOnly.Clear();
        long m0 = GC.GetAllocatedBytesForCurrentThread();
        if (Environment.GetEnvironmentVariable("ALLOC") != null) AllocSampler.Instance ??= new AllocSampler(); var aFrom = DateTime.UtcNow;
        var acc = new SnapshotGridAccessor(cells);
        long m1 = GC.GetAllocatedBytesForCurrentThread();
        var res = ct.FacesFor(acc, dirty, cs, co, BlockSize, _hideOnly);
        AllocSampler.Instance?.Dump("region surface", aFrom, DateTime.UtcNow);
        Console.WriteLine($"        region {cells.Count} cells: accessor {(m1 - m0) / 1048576.0:F1} MB, surface {(GC.GetAllocatedBytesForCurrentThread() - m1) / 1048576.0:F1} MB");
        return res;
    }

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
            var chunks = new ChunkedTable(8, float.Parse(Environment.GetEnvironmentVariable("CHUNK") ?? "16", System.Globalization.CultureInfo.InvariantCulture), new FacePhysics(dsm));
            var table = dsm.BuildForceTable(whole.Grid, whole.Surface, whole.Manifold, whole.Com, chunks: chunks);
            var before = table.Clone();
            var idx = new HashSet<int>(pick());
            var removedBoxes = idx.Select(i => boxes[i]).ToList();
            var rest = boxes.Where((b, i) => !idx.Contains(i)).ToList();
            var after = Build(name, rest);
            // the chunks the removed blocks reach (metres, 2 m margin), rebuilt locally
            var dirty = new HashSet<long>();
            float cs = whole.Surface.CellSize, co = whole.Surface.CellOffset; int ks = (int)MathF.Round(cs / 0.25f);
            foreach (var (a, b) in removedBoxes)
            {
                var lo = new Vector3(a.X, a.Y, a.Z) * 0.25f - new Vector3(2f); var hi = new Vector3(b.X + 1, b.Y + 1, b.Z + 1) * 0.25f + new Vector3(2f);
                chunks.KeysIn(lo, hi, dirty);
            }
            long mem0 = GC.GetAllocatedBytesForCurrentThread(); var wFrom = DateTime.UtcNow;
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var faces = LocalFaces(after.Grid, dirty, chunks, after.Surface);
            long memSurf = GC.GetAllocatedBytesForCurrentThread() - mem0;
            double surfMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            chunks.Prepare(dirty);
            table = chunks.Apply(chunks.ComputeLocal(dirty, faces, 3, _hideOnly), table);
            ChunkedTable.FacesDone(faces);
            double patchMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            int patches = dirty.Count;
            AllocSampler.Instance?.Dump("whole local update", wFrom, DateTime.UtcNow);
            Console.WriteLine($"      local: {chunks.LastProfile}; garbage: surface {memSurf / 1048576.0:F1} MB, all {(GC.GetAllocatedBytesForCurrentThread() - mem0) / 1048576.0:F1} MB (this thread)");
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
            Console.WriteLine($"   damage {label} ({idx.Count} blocks; {patches} of {chunks.ChunkCount} chunks, {faces.Count} faces: local update {patchMs:F0} ms of which surface {surfMs:F0} ms; full table {table.BuildMs:F0} ms): the damage changed forces by median {eStale[150] * 100:F1}% (p95 {eStale[285] * 100:F1}%); local update off by median {ePatched[150] * 100:F1}% (p95 {ePatched[285] * 100:F1}%)");
        }
    }

    /// <summary>The table cache: a ship's table, chunks and wings saved and read back give the same forces exactly.</summary>
    static bool CacheRoundTrip(Ship ship, List<(Vector3I, Vector3I)> boxes, AtmosphereState atmo, out string note)
    {
        var dsm = (DampedShadowedDragModel)ship.Model.InnerModel;
        var phys = new FacePhysics(dsm);
        var chunks = new ChunkedTable(8, 8f, phys);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var table = dsm.BuildForceTable(ship.Grid, ship.Surface, ship.Manifold, ship.Com, chunks: chunks);
        double tBuild = sw.Elapsed.TotalMilliseconds;
        var wings = ship.Model.Wings?.ToList() ?? new List<LiftingSurface>();
        string key = AeroTableCache.Key(boxes, BlockSize, SnapshotGridAccessor.CellScale(BlockSize), phys) + "test";
        sw.Restart();
        AeroTableCache.Save(key, table, chunks, wings);
        double tSave = sw.Elapsed.TotalMilliseconds; sw.Restart();
        bool ok = AeroTableCache.TryLoad(key, phys, out var t2, out var c2, out var w2);
        double tLoad = sw.Elapsed.TotalMilliseconds;
        note = $"build {tBuild:F0} ms, save {tSave:F0} ms, load {tLoad:F0} ms";
        if (!ok) { note += ", NOT READ BACK"; return false; }
        var r = new Random(9); double worst = 0;
        for (int i = 0; i < 300; i++)
        {
            var d = Vector3.Normalize(new Vector3((float)r.NextDouble() * 2 - 1, (float)r.NextDouble() * 2 - 1, (float)r.NextDouble() * 2 - 1));
            var ctx = new AeroContext(ship.Grid, ship.Surface, d * (50f + 400f * (float)r.NextDouble()), atmo, ship.Com, BlockSize, d * 0.3f, -1f, ship.Manifold);
            var a = table.Evaluate(ctx, dsm.SubsonicLimit, dsm.SupersonicLimit, dsm.Streamlining); var b = t2.Evaluate(ctx, dsm.SubsonicLimit, dsm.SupersonicLimit, dsm.Streamlining);
            worst = Math.Max(worst, (a.Force - b.Force).Length() + (a.Torque - b.Torque).Length());
        }
        bool same = worst == 0 && c2.ChunkCount == chunks.ChunkCount && c2.Bytes() == chunks.Bytes()
            && c2.ExcludedFaces.SetEquals(chunks.ExcludedFaces) && c2.CavityFaces.SetEquals(chunks.CavityFaces) && w2.Count == wings.Count;
        for (int c = 0; same && c < chunks.ChunkCount; c++)
        {
            var x = chunks.GetShare(c); var y = c2.GetShare(c);
            for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) { same = false; break; }
            if (chunks.Bounds[c] != c2.Bounds[c]) same = false;
        }
        for (int i = 0; same && i < wings.Count; i++)
            same = wings[i].Cells.SequenceEqual(w2[i].Cells) && wings[i].Normal == w2[i].Normal && wings[i].PlanformArea == w2[i].PlanformArea && wings[i].CLAlpha == w2[i].CLAlpha;
        note += same ? $", identical ({chunks.ChunkCount} chunks, {wings.Count} wings)" : $", DIFFERENT (force/torque diff {worst})";
        try { System.IO.File.Delete(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AeroMod", "tables", key + ".bin")); } catch { }
        return same;
    }

    /// <summary>A grid's full rebuild as the game runs it (its surfaces, classifier, detector and table builder
    /// reused build after build): what each stage allocates (by type too, with dump), per build. Returns the last
    /// build's total, MB (this thread).</summary>
    static double BuildGarbage(string name, int runs, bool dump)
    {
        if (dump) AllocSampler.Instance ??= new AllocSampler();
        var boxes = LoadBoxes(Path.Combine(dataDir, name + ".boxes"));
        int k = SnapshotGridAccessor.CellScale(BlockSize); var (cs, co) = SnapshotGridAccessor.CellGeometry(k);
        var surfaces = new[] { new SmoothSurfaceProvider(), new SmoothSurfaceProvider() };
        var manifolds = new[] { new ManifoldClassifier(), new ManifoldClassifier() };
        var builder = new DampedShadowedDragModel();
        var model = new LiftingSurfaceModel(new DampedShadowedDragModel(), liftModel: new CompressibleWingModel());
        var detector = model.Detector;
        double last = 0;
        for (int run = 0; run < runs; run++)
        {
            var surface = surfaces[run & 1]; var manifold = manifolds[run & 1];
            var stages = new List<(string, DateTime, DateTime, long)>();
            DateTime t0 = DateTime.UtcNow; long m0 = GC.GetAllocatedBytesForCurrentThread();
            void Stage(string label) { var t1 = DateTime.UtcNow; long m1 = GC.GetAllocatedBytesForCurrentThread(); stages.Add((label, t0, t1, m1 - m0)); t0 = t1; m0 = m1; }
            var snapshot = k > 1 ? new SnapshotGridAccessor(boxes).CoarsenMajority(k) : new SnapshotGridAccessor(boxes);
            surface.CellSize = cs; surface.CellOffset = co;
            Stage("snapshot");
            surface.BeginBuild(snapshot, BlockSize);
            while (!surface.AddCellBatch(int.MaxValue)) { }
            Stage("faces");
            surface.FinalizeBuild();
            Stage("finalize");
            manifold.Classify(surface);
            Stage("classify");
            if (detector is ConnectedComponentWingDetector cc) { cc.Manifold = manifold; cc.ManifoldSurface = surface; cc.CellSize = cs; cc.CellOffset = co; }
            detector.Invalidate();
            var wings = detector.Detect(snapshot, surface, BlockSize);
            Stage("wings");
            var tmp = new LiftingSurfaceModel(builder, liftModel: new CompressibleWingModel());
            tmp.InstallWings(wings);
            tmp.BuildFaceOverrideIndex(surface, new List<IAeroBlockComponent>());
            tmp.ExcludeWingFaces(builder, surface.Version);
            var chunks = new ChunkedTable(8, 8f, new FacePhysics(builder));
            builder.BuildForceTable(snapshot, surface, manifold, Vector3.Zero, chunks: chunks);
            Stage("table");
            last = stages.Sum(x => x.Item4) / 1048576.0;
            Console.WriteLine($"   {name} build {run + 1}: {surface.FaceCount} faces, {string.Join(", ", stages.Select(x => $"{x.Item1} {x.Item4 / 1048576.0:F1} MB"))}, total {last:F1} MB (this thread)");
            if (dump) foreach (var (label, a, b, _) in stages) AllocSampler.Instance.Dump(label, a, b);
        }
        return last;
    }

    /// <summary>The table cache's edges: its key ignores the blocks' order and sees the block size; a truncated or
    /// garbage file is a miss, never an exception.</summary>
    static bool CacheEdges(Ship ship, List<(Vector3I, Vector3I)> boxes, out string note)
    {
        var dsm = (DampedShadowedDragModel)ship.Model.InnerModel;
        var phys = new FacePhysics(dsm);
        var shuffled = new List<(Vector3I, Vector3I)>(boxes);
        var rng = new Random(5);
        for (int i = shuffled.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]); }
        int k = SnapshotGridAccessor.CellScale(BlockSize);
        string k1 = AeroTableCache.Key(boxes, BlockSize, k, phys), k2 = AeroTableCache.Key(shuffled, BlockSize, k, phys);
        string k3 = AeroTableCache.Key(boxes, BlockSize * 0.2f, k, phys);
        bool keys = k1 == k2 && k1 != k3;
        // a real file, cut short / garbage
        var chunks = new ChunkedTable(8, 8f, phys);
        var table = dsm.BuildForceTable(ship.Grid, ship.Surface, ship.Manifold, ship.Com, n: 4, nj: 2, chunks: chunks);
        string key = k1 + "edge";
        AeroTableCache.Save(key, table, chunks, new List<LiftingSurface>());
        string path = Path.Combine(Path.GetTempPath(), "AeroMod", "tables", key + ".bin");
        bool miss1, miss2;
        try
        {
            var bytes = File.ReadAllBytes(path);
            File.WriteAllBytes(path, bytes.Take(bytes.Length / 2).ToArray());
            miss1 = !AeroTableCache.TryLoad(key, phys, out _, out _, out _);
            var junk = new byte[4096]; new Random(7).NextBytes(junk);
            File.WriteAllBytes(path, junk);
            miss2 = !AeroTableCache.TryLoad(key, phys, out _, out _, out _);
        }
        catch (Exception e) { note = "threw: " + e.Message; return false; }
        finally { try { File.Delete(path); } catch { } }
        note = $"key order-free {k1 == k2}, sees block size {k1 != k3}; truncated file a miss {miss1}, garbage a miss {miss2}";
        return keys && miss1 && miss2;
    }

    /// <summary>Wing detection with its pooled working sets: the same detector run again, and a fresh one, find the
    /// same wings (count, cells, area, sweep).</summary>
    static bool WingsRepeat(Ship ship, out string note)
    {
        int k = SnapshotGridAccessor.CellScale(BlockSize); var (cs, co) = SnapshotGridAccessor.CellGeometry(k);
        List<LiftingSurface> Run(IWingDetector d)
        {
            if (d is ConnectedComponentWingDetector cc) { cc.Manifold = ship.Manifold; cc.ManifoldSurface = ship.Surface; cc.CellSize = cs; cc.CellOffset = co; }
            d.Invalidate();
            return d.Detect(ship.Grid, ship.Surface, BlockSize);
        }
        var det = new LiftingSurfaceModel(new DampedShadowedDragModel(), liftModel: new CompressibleWingModel()).Detector;
        var a = Run(det); var b = Run(det);
        var c = Run(new LiftingSurfaceModel(new DampedShadowedDragModel(), liftModel: new CompressibleWingModel()).Detector);
        bool Same(List<LiftingSurface> x, List<LiftingSurface> y)
        {
            if (x.Count != y.Count) return false;
            for (int i = 0; i < x.Count; i++)
                if (x[i].PlanformArea != y[i].PlanformArea || x[i].SweepAngle != y[i].SweepAngle || !x[i].Cells.SequenceEqual(y[i].Cells)) return false;
            return true;
        }
        bool ok = a.Count > 0 && Same(a, b) && Same(a, c);
        note = $"{a.Count} wing(s); again {Same(a, b)}, fresh detector {Same(a, c)}";
        return ok;
    }

    // The table cache keys on shape and AeroTableCache.Version: a change to how tables are built must bump Version, or
    // players fly on stale tables from disk. The gate keeps a hash of the sources that build them (Tests/table_sources.txt:
    // "version hash"); changed sources at the same version fail it. After bumping: dotnet run -- tablehash
    static readonly string[] TableSources = {
        @"Drag\DampedShadowedDragModel.cs", @"Drag\ChunkedTable.cs", @"Drag\ForceTable.cs", @"Drag\AeroTableCache.cs",
        @"Surface\SmoothSurfaceProvider.cs", @"Surface\ManifoldClassifier.cs", @"Lift\ConnectedComponentWingDetector.cs",
        @"Lift\LiftingSurface.cs", @"Core\SnapshotGridAccessor.cs" };
    static string TableSourceHash()
    {
        string sim = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Scripts", "Simulation");
        using var sha = System.Security.Cryptography.SHA256.Create();
        var all = new System.Text.StringBuilder();
        foreach (var f in TableSources) all.Append(File.ReadAllText(Path.Combine(sim, f)).Replace("\r\n", "\n")).Append('|');
        return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(all.ToString())))[..16];
    }
    static string TableHashFile => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "table_sources.txt");

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
                var boxes = LoadBoxes(path);
                var ship = Build(name, boxes);
                var (med, p95) = TableCheck(ship, atmo);
                bool ok = med < 0.05 && p95 < 0.15;
                if (!ok) fails++;
                Console.WriteLine($"   {(ok ? "ok  " : "FAIL")} {name}: table error median {med * 100:F1}% p95 {p95 * 100:F1}%");
                bool cok = CacheRoundTrip(ship, boxes, atmo, out string cnote);
                if (!cok) fails++;
                Console.WriteLine($"   {(cok ? "ok  " : "FAIL")} {name}: table cache {cnote}");
                if (name == "pelican")
                {
                    bool wok = WingsRepeat(ship, out string wnote);
                    if (!wok) fails++;
                    Console.WriteLine($"   {(wok ? "ok  " : "FAIL")} {name}: wings with pooled working sets: {wnote}");
                }
                bool eok = CacheEdges(ship, boxes, out string enote);
                if (!eok) fails++;
                Console.WriteLine($"   {(eok ? "ok  " : "FAIL")} {name}: table cache edges: {enote}");
            }
            // the table cache's version follows its sources
            {
                string h = TableSourceHash();
                var rec = File.Exists(TableHashFile) ? File.ReadAllText(TableHashFile).Trim().Split(' ') : new[] { "", "" };
                bool same = rec.Length == 2 && rec[1] == h;
                bool bumped = rec.Length == 2 && rec[0] != AeroTableCache.Version.ToString();
                bool ok = same || false;
                if (!ok) fails++;
                Console.WriteLine(ok ? $"   ok   table cache version {AeroTableCache.Version} matches its sources"
                    : bumped ? $"   FAIL table sources recorded for another version: run 'dotnet run -- tablehash' in Tests/AeroBench"
                    : $"   FAIL table-building sources changed at the same AeroTableCache.Version ({AeroTableCache.Version}): bump it (stale tables on disk otherwise), then 'dotnet run -- tablehash'");
            }
            // a rebuild's garbage stays down (the reused tables and pools: 72 -> 20 MB on Red Ship, 2026-10-02)
            {
                var path = Path.Combine(dataDir, "pelican.boxes");
                if (File.Exists(path))
                {
                    double mb = BuildGarbage("pelican", 3, false);
                    bool ok = mb < 4.0;
                    if (!ok) fails++;
                    Console.WriteLine($"   {(ok ? "ok  " : "FAIL")} pelican: steady-state rebuild garbage {mb:F1} MB (limit 4)");
                }
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
        if (args.Length > 0 && args[0] == "cache")
        {
            foreach (var name in args.Skip(1))
            {
                var boxes = LoadBoxes(Path.Combine(dataDir, name + ".boxes"));
                var ship = Build(name, boxes);
                bool ok = CacheRoundTrip(ship, boxes, atmo, out string note);
                Console.WriteLine($"   {(ok ? "ok  " : "FAIL")} {name}: table cache {note}");
            }
            return 0;
        }
        if (args.Length > 0 && args[0] == "tablehash")
        {
            File.WriteAllText(TableHashFile, $"{AeroTableCache.Version} {TableSourceHash()}");
            Console.WriteLine($"recorded: version {AeroTableCache.Version}, sources {TableSourceHash()}");
            return 0;
        }
        if (args.Length > 0 && args[0] == "gcbuild")
        {
            foreach (var name in args.Skip(1)) BuildGarbage(name, 3, Environment.GetEnvironmentVariable("ALLOC") != null);
            return 0;
        }
        if (args.Length > 0 && args[0] == "impact")
        {
            // half the ship gone (everything past its middle along its longest axis): drop, then local, vs exact
            foreach (var name in args.Skip(1))
            {
                var boxes = LoadBoxes(Path.Combine(dataDir, name + ".boxes"));
                var whole = Build(name, boxes);
                var dsm = (DampedShadowedDragModel)whole.Model.InnerModel;
                var chunks = new ChunkedTable(8, 8f, new FacePhysics(dsm));
                var table = dsm.BuildForceTable(whole.Grid, whole.Surface, whole.Manifold, whole.Com, chunks: chunks);
                foreach (var (a, b) in boxes) chunks.CountBlock(chunks.KeyOf(new Vector3(a.X + b.X + 1, a.Y + b.Y + 1, a.Z + b.Z + 1) * 0.125f), 1); chunks.SealCounts();
                var lo = boxes.Aggregate(new Vector3I(int.MaxValue), (m, b) => Vector3I.Min(m, b.Item1)); var hi = boxes.Aggregate(new Vector3I(int.MinValue), (m, b) => Vector3I.Max(m, b.Item2));
                var ext = hi - lo; int axis = ext.X >= ext.Y && ext.X >= ext.Z ? 0 : ext.Y >= ext.Z ? 1 : 2;
                int mid = axis == 0 ? (lo.X + hi.X) / 2 : axis == 1 ? (lo.Y + hi.Y) / 2 : (lo.Z + hi.Z) / 2;
                int Ax(Vector3I v) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
                var removed = boxes.Where(b => Ax(b.Item1) + Ax(b.Item2) > 2 * mid).ToList();
                var rest = boxes.Where(b => Ax(b.Item1) + Ax(b.Item2) <= 2 * mid).ToList();
                var after = Build(name, rest);
                var adsm = (DampedShadowedDragModel)after.Model.InnerModel;
                var exact = adsm.BuildForceTable(after.Grid, after.Surface, after.Manifold, whole.Com);
                // the frame of the impact: emptied chunks out
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var keys = new HashSet<long>();
                foreach (var (a, b) in removed) { long key = chunks.KeyOf(new Vector3(a.X + b.X + 1, a.Y + b.Y + 1, a.Z + b.Z + 1) * 0.125f); chunks.CountBlock(key, -1); keys.Add(key); }
                var gone = new List<(long, float[])>();
                var dropped = chunks.DropEmptied(keys, table, false, out int nDropped, gone);
                double dropMs = sw.Elapsed.TotalMilliseconds;
                // the piece that broke off: seeded from what the parent dropped, against its own exact table
                {
                    OrphanChunks.Add(new Vector3D(0, 0, 0), Quaternion.Identity, chunks.N, chunks.ChunkSize, gone);
                    var childKeys = new HashSet<long>();
                    foreach (var (a, b) in removed) childKeys.Add(chunks.KeyOf(new Vector3(a.X + b.X + 1, a.Y + b.Y + 1, a.Z + b.Z + 1) * 0.125f));
                    var seed = OrphanChunks.Take(new Vector3D(0, 0, 0), Quaternion.Identity, childKeys, chunks.N, 3);
                    var child = Build(name + " piece", removed);
                    var cd = (DampedShadowedDragModel)child.Model.InnerModel;
                    var cexact = cd.BuildForceTable(child.Grid, child.Surface, child.Manifold, child.Com);
                    var rr = new Random(6); var eF = new List<double>(); var eT = new List<double>();
                    for (int i = 0; i < 300; i++)
                    {
                        var d = Vector3.Normalize(new Vector3((float)rr.NextDouble() * 2 - 1, (float)rr.NextDouble() * 2 - 1, (float)rr.NextDouble() * 2 - 1));
                        var ctx = new AeroContext(child.Grid, child.Surface, d * 150f, atmo, child.Com, BlockSize, Vector3.Zero, -1f, child.Manifold);
                        var a = cexact.Evaluate(ctx, cd.SubsonicLimit, cd.SupersonicLimit, cd.Streamlining); var b = seed.Evaluate(ctx, cd.SubsonicLimit, cd.SupersonicLimit, cd.Streamlining);
                        eF.Add((a.Force - b.Force).Length() / Math.Max(a.Force.Length(), 1)); eT.Add((a.Torque - b.Torque).Length() / Math.Max(a.Torque.Length(), a.Force.Length() + 1));
                    }
                    eF.Sort(); eT.Sort();
                    Console.WriteLine($"   the broken-off piece ({removed.Count} blocks), seeded from its parent: force off by median {eF[150] * 100:F0}% (p95 {eF[285] * 100:F0}%), torque median {eT[150] * 100:F0}% | its own build would take {cexact.BuildMs:F0} ms of table alone");
                }
                // then the local update of what is left of the touched chunks
                var dirty = new HashSet<long>();
                foreach (var (a, b) in removed) chunks.KeysIn(new Vector3(a.X, a.Y, a.Z) * 0.25f - new Vector3(2f), new Vector3(b.X + 1, b.Y + 1, b.Z + 1) * 0.25f + new Vector3(2f), dirty);
                dirty.RemoveWhere(k => !chunks.BlockCount.TryGetValue(k, out int c) || c == 0);   // (emptied ones are done)
                sw.Restart();
                var faces = LocalFaces(after.Grid, dirty, chunks, after.Surface);
                chunks.Prepare(dirty);
                var local = chunks.Apply(chunks.ComputeLocal(dirty, faces, 3, _hideOnly), dropped);
                ChunkedTable.FacesDone(faces);
                double localMs = sw.Elapsed.TotalMilliseconds;
                var r = new Random(5); var eS = new List<double>(); var eD = new List<double>(); var eL = new List<double>();
                for (int i = 0; i < 300; i++)
                {
                    var d = Vector3.Normalize(new Vector3((float)r.NextDouble() * 2 - 1, (float)r.NextDouble() * 2 - 1, (float)r.NextDouble() * 2 - 1));
                    var ctx = new AeroContext(after.Grid, after.Surface, d * 150f, atmo, whole.Com, BlockSize, Vector3.Zero, -1f, after.Manifold);
                    Vector3 F(ForceTable t) => t.Evaluate(ctx, adsm.SubsonicLimit, adsm.SupersonicLimit, adsm.Streamlining).Force;
                    var fe = F(exact); double m = Math.Max(fe.Length(), 1);
                    eS.Add((F(table) - fe).Length() / m); eD.Add((F(dropped) - fe).Length() / m); eL.Add((F(local) - fe).Length() / m);
                }
                eS.Sort(); eD.Sort(); eL.Sort();
                Console.WriteLine($"   chunk memory: {chunks.Bytes() / 1048576.0:F1} MB for {chunks.ChunkCount} chunks");
                Console.WriteLine($"{name}: half gone ({removed.Count} of {boxes.Count} blocks): stale off by median {eS[150] * 100:F0}% | the same frame, {nDropped} emptied chunks dropped in {dropMs:F1} ms: off by median {eD[150] * 100:F0}% (p95 {eD[285] * 100:F0}%) | + local update of {dirty.Count} chunks in {localMs:F0} ms: median {eL[150] * 100:F1}% (p95 {eL[285] * 100:F1}%) | full table {table.BuildMs:F0} ms");
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
