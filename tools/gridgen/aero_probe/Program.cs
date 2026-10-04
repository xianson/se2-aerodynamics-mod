using System.IO;
using AeroMod;

// gridgen aero probe: a generated plane (.boxes: one block's occupied-cell box per line, inclusive, 0.25 m cells;
// header '# blockSize 0.5') through the aero mod's own build -> wing detection -> force model, offline.
// Prints the wings found, then lift / drag / pitch torque over an AoA sweep with the nose at grid -Z
// (travel a = (0, -sin a, -cos a), as flighttest.sh flies it).
static class Program
{
    static float BlockSize = 0.5f;

    static List<(Vector3I, Vector3I)> LoadBoxes(string path)
    {
        var boxes = new List<(Vector3I, Vector3I)>();
        foreach (var line in File.ReadLines(path))
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (line.StartsWith("#")) { int i = Array.IndexOf(p, "blockSize"); if (i >= 0) BlockSize = float.Parse(p[i + 1], System.Globalization.CultureInfo.InvariantCulture); continue; }
            if (p.Length < 6) continue;
            boxes.Add((new Vector3I(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2])), new Vector3I(int.Parse(p[3]), int.Parse(p[4]), int.Parse(p[5]))));
        }
        return boxes;
    }

    static int Main(string[] args)
    {
        if (args.Length < 1) { Console.WriteLine("usage: AeroProbe PLANE.boxes [speed m/s]"); return 2; }
        var boxes = LoadBoxes(args[0]);
        float speed = args.Length > 1 ? float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 220f;
        int k = SnapshotGridAccessor.CellScale(BlockSize);
        var (cs, co) = SnapshotGridAccessor.CellGeometry(k);
        var grid = k <= 1 ? new SnapshotGridAccessor(boxes) : new SnapshotGridAccessor(boxes).CoarsenMajority(k);
        var surface = new SmoothSurfaceProvider { CellSize = cs, CellOffset = co };
        surface.BeginBuild(grid, BlockSize);
        while (!surface.AddCellBatch(int.MaxValue)) { }
        surface.FinalizeBuild();
        var manifold = new ManifoldClassifier();
        manifold.Classify(surface);
        var inner = new DampedShadowedDragModel();
        var model = new LiftingSurfaceModel(inner, liftModel: new CompressibleWingModel());
        if (model.Detector is ConnectedComponentWingDetector cc) { cc.Manifold = manifold; cc.ManifoldSurface = surface; cc.CellSize = cs; cc.CellOffset = co; }
        model.Detector.Invalidate();
        var wings = model.Detector.Detect(grid, surface, BlockSize);
        var shadow = new PrecomputedShadowMap { Manifold = manifold };
        shadow.PrecomputeAll(grid, surface);
        model.InstallWings(wings);
        inner.InstallShadowMap(shadow);
        model.BuildFaceOverrideIndex(surface, new List<IAeroBlockComponent>());
        model.ExcludeWingFaces(inner, surface.Version);
        double cx = 0, cy = 0, cz = 0; long n = 0;
        foreach (var c in grid.EnumerateOccupiedCells()) { cx += c.X; cy += c.Y; cz += c.Z; n++; }
        var com = new Vector3((float)(cx / n + co) * cs, (float)(cy / n + co) * cs, (float)(cz / n + co) * cs);
        Console.WriteLine($"{Path.GetFileName(args[0])}: {boxes.Count} blocks, {grid.CellCount} cells of {cs:F2} m, {wings.Count} wings");
        float area = 0;
        foreach (var w in wings)
        {
            Console.WriteLine($"  wing n=({w.Normal.X:F0},{w.Normal.Y:F0},{w.Normal.Z:F0}) area {w.PlanformArea:F1} m2 span {w.Span:F1} chord {w.MeanChord:F2} AR {w.AspectRatio:F2} t/c {w.ThicknessRatio:F2} sweep {w.SweepAngle * 57.3f:F0} centroid ({w.Centroid.X:F1},{w.Centroid.Y:F1},{w.Centroid.Z:F1}) CLa {w.CLAlpha:F2}");
            if (Math.Abs(w.Normal.Y) > 0.5f) area += w.PlanformArea;
            if (w.Cells != null && w.Cells.Count > 0)
                Console.WriteLine($"     cells {w.Cells.Count}: X {w.Cells.Min(c => c.X)}..{w.Cells.Max(c => c.X)} Y {w.Cells.Min(c => c.Y)}..{w.Cells.Max(c => c.Y)} Z {w.Cells.Min(c => c.Z)}..{w.Cells.Max(c => c.Z)}");
        }
        var atmo = new AtmosphereState { Density = 1.225, Pressure = 101325, Temperature = 288, SpeedOfSound = 340 };
        Console.WriteLine($"  horizontal wing area {area:F1} m2; sweep at {speed} m/s, nose -Z:");
        foreach (float aoa in new[] { -4f, 0f, 4f, 8f, 12f, 16f })
        {
            float a = aoa * MathF.PI / 180f;
            var v = new Vector3(0, -MathF.Sin(a), -MathF.Cos(a)) * speed;
            AeroResult r = default;
            for (int i = 0; i < 4; i++)
                r = model.Compute(new AeroContext(grid, surface, v, atmo, com, BlockSize, Vector3.Zero, -1f, manifold));
            // lift: perpendicular to travel in the Y-Z plane, positive up
            var vd = Vector3.Normalize(v);
            var liftDir = Vector3.Normalize(new Vector3(0, MathF.Cos(a), -MathF.Sin(a)));
            float lift = Vector3.Dot(r.Force, liftDir), drag = -Vector3.Dot(r.Force, vd);
            float q = 0.5f * 1.225f * speed * speed;
            Console.WriteLine($"    AoA {aoa,5:F0}  lift {lift / 1000,9:F1} kN  drag {drag / 1000,9:F1} kN  CL(wing) {(area > 0 ? lift / (q * area) : 0),6:F3}  pitch {r.Torque.X / 1000,9:F1} kN·m");
        }
        return 0;
    }
}
