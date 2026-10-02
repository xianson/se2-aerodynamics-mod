using AeroMod;

// The smooth surface, offline:
//  1. a surface updated block by block (OnBlocksChanged) equals a fresh build of the same shape, face for face,
//     under every smoothing / crease setting. Damage must not make a ship's aerodynamics drift from what it would be
//     built new; five separate bugs broke this (a diffusion window a hop short, at most 8 faces per vertex, creases
//     decided by insertion order and never updated, weights drifting under +-pi/2).
//  2. the bit-grid snapshot holds exactly the cells it was given, coordinates either side of zero.
//  3. a large plate (a big ship's hull) builds fast: packed-key hashing stays spread (it collapsed to X ^ Z).
static class Program
{
    sealed class SetGrid : IGridAccessor
    {
        public readonly HashSet<Vector3I> Cells = new();
        public bool IsCellOccupied(Vector3I p) => Cells.Contains(p);
        public IEnumerable<Vector3I> EnumerateOccupiedCells() => Cells.OrderBy(c => c.X).ThenBy(c => c.Y).ThenBy(c => c.Z);
        public int CellCount => Cells.Count;
    }

    static int _fails, _checks;
    static void Check(bool ok, string what) { _checks++; if (!ok) { _fails++; if (_fails <= 15) Console.WriteLine("FAIL " + what); } }

    static (int, int, int) Key(Vector3 p) => ((int)MathF.Round(p.X * 8), (int)MathF.Round(p.Y * 8), (int)MathF.Round(p.Z * 8));

    /// <summary>Same faces (by position) with the same normals and areas; face order may differ.</summary>
    static void SameSurface(ISurfaceProvider a, ISurfaceProvider b, string tag)
    {
        if (a.FaceCount != b.FaceCount) { Check(false, $"{tag}: {a.FaceCount} faces vs {b.FaceCount}"); return; }
        var m = new Dictionary<(int, int, int), SurfaceFace>();
        for (int i = 0; i < b.FaceCount; i++) m[Key(b.Faces[i].Position)] = b.Faces[i];
        int bad = 0; string first = null;
        for (int i = 0; i < a.FaceCount; i++)
        {
            var f = a.Faces[i];
            if (!m.TryGetValue(Key(f.Position), out var g) || (g.Normal - f.Normal).Length() > 1e-4f || MathF.Abs(g.Area - f.Area) > 1e-6f)
            { bad++; first ??= $"{f.Position} {f.Normal} vs {(m.ContainsKey(Key(f.Position)) ? g.Normal.ToString() : "missing")}"; }
        }
        Check(bad == 0, $"{tag}: {bad} faces differ, e.g. {first}");
    }

    static SmoothSurfaceProvider Make(int rings, float crease) => new() { SmoothingRings = rings, CreaseAngleDegrees = crease };

    static void IncrementalEqualsFresh(int rings, float crease)
    {
        for (int seed = 1; seed <= 25; seed++)
        {
            var rng = new Random(seed * 7919 + rings * 31 + (int)crease);
            var grid = new SetGrid();
            int boxes = 3 + rng.Next(10);
            for (int b = 0; b < boxes; b++)
            {
                var lo = new Vector3I(rng.Next(-30, 20), rng.Next(-30, 20), rng.Next(-30, 20));
                var sz = rng.Next(3) == 0 ? new Vector3I(1 + rng.Next(30), 1, 1 + rng.Next(12))   // thin plates: wings
                                          : new Vector3I(1 + rng.Next(14), 1 + rng.Next(14), 1 + rng.Next(14));
                for (int x = lo.X; x < lo.X + sz.X; x++) for (int y = lo.Y; y < lo.Y + sz.Y; y++) for (int z = lo.Z; z < lo.Z + sz.Z; z++)
                    grid.Cells.Add(new Vector3I(x, y, z));
            }
            var inc = Make(rings, crease);
            inc.Build(grid, 0.25f);
            for (int st = 0; st < 12; st++)
            {
                var all = grid.Cells.ToList();
                var removed = new List<Vector3I>(); var added = new List<Vector3I>();
                if (rng.Next(2) == 0 && all.Count > 50)
                {
                    var c = all[rng.Next(all.Count)];
                    foreach (var p in all) if (Math.Abs(p.X - c.X) <= 1 && Math.Abs(p.Y - c.Y) <= 2 && Math.Abs(p.Z - c.Z) <= 1) removed.Add(p);
                    foreach (var p in removed) grid.Cells.Remove(p);
                }
                else
                {
                    var c = all.Count > 0 ? all[rng.Next(all.Count)] : Vector3I.Zero;
                    for (int x = 0; x < 2; x++) for (int y = 0; y < 3; y++) for (int z = 0; z < 2; z++)
                    { var p = c + new Vector3I(x + 1, y, z); if (grid.Cells.Add(p)) added.Add(p); }
                }
                inc.OnBlocksChanged(grid, new BlocksChangedArgs(added, removed));
                var fresh = Make(rings, crease); fresh.Build(grid, 0.25f);
                SameSurface(inc, fresh, $"rings {rings} crease {crease} seed {seed} step {st}");
            }
        }
    }

    static void SnapshotHoldsItsCells()
    {
        var rng = new Random(5);
        for (int t = 0; t < 20; t++)
        {
            var boxes = new List<(Vector3I, Vector3I)>(); var expect = new HashSet<Vector3I>();
            int nb = 1 + rng.Next(6);
            for (int b = 0; b < nb; b++)
            {
                var lo = new Vector3I(rng.Next(-40, 30), rng.Next(-40, 30), rng.Next(-40, 30));
                var hi = lo + new Vector3I(rng.Next(6), rng.Next(6), rng.Next(6));
                boxes.Add((lo, hi));
                for (int x = lo.X; x <= hi.X; x++) for (int y = lo.Y; y <= hi.Y; y++) for (int z = lo.Z; z <= hi.Z; z++) expect.Add(new Vector3I(x, y, z));
            }
            var snap = new SnapshotGridAccessor(boxes);
            var got = snap.EnumerateOccupiedCells().ToList();
            Check(got.Count == expect.Count && snap.CellCount == expect.Count && got.All(expect.Contains), $"snapshot {t}: {got.Count}/{snap.CellCount} cells vs {expect.Count}");
            Check(expect.All(snap.IsCellOccupied) && !snap.IsCellOccupied(new Vector3I(999, 0, 0)), $"snapshot {t}: occupancy");
        }
    }

    static void BigPlateIsFast()
    {
        // a 120 x 2 x 120 cell plate (30 m square)
        var boxes = new List<(Vector3I, Vector3I)> { (new Vector3I(-60, -1, -60), new Vector3I(59, 0, 59)) };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var s = new SmoothSurfaceProvider();
        s.Build(new SnapshotGridAccessor(boxes), 0.25f);
        long ms = sw.ElapsedMilliseconds;
        Check(s.FaceCount == 2 * 120 * 120 + 4 * 120 * 2, $"big plate: {s.FaceCount} faces");
        Check(ms < 3000, $"big plate built in {ms} ms (packed-key hashing collapsed?)");
        Console.WriteLine($"   big plate: {s.FaceCount} faces in {ms} ms");
    }

    /// <summary>A big surface hands its build tables back after building; damage then rebuilds it whole - and it
    /// still equals a fresh build. Its tables are reused by the next big build (the shared pool).</summary>
    static void BigSurfaceReleasesAndRebuilds()
    {
        var grid = new SetGrid();
        for (int x = -60; x < 60; x++) for (int y = -1; y <= 0; y++) for (int z = -60; z < 60; z++) grid.Cells.Add(new Vector3I(x, y, z));
        var s = new SmoothSurfaceProvider();
        s.Build(grid, 0.25f);
        Check(s.Released, $"big surface ({s.FaceCount} faces) released its build tables");
        var removed = new List<Vector3I>();
        foreach (var c in grid.Cells.ToList()) if (Math.Abs(c.X - 5) <= 2 && Math.Abs(c.Z + 7) <= 3) removed.Add(c);
        foreach (var c in removed) grid.Cells.Remove(c);
        s.OnBlocksChanged(grid, new BlocksChangedArgs(removed: removed));
        var fresh = new SmoothSurfaceProvider(); fresh.Build(grid, 0.25f);
        SameSurface(s, fresh, "big surface after damage");
        // the staged build (what the game's background rebuild uses) on the same big shape
        var staged = new SmoothSurfaceProvider();
        staged.BeginBuild(grid, 0.25f); while (!staged.AddCellBatch(int.MaxValue)) { } staged.FinalizeBuild();
        SameSurface(staged, fresh, "big surface, staged build");
        Check(staged.Released, "staged big surface released its build tables");
        // and a small one keeps them (block-by-block updates)
        var small = new SetGrid(); for (int x = 0; x < 6; x++) for (int y = 0; y < 4; y++) small.Cells.Add(new Vector3I(x, y, 0));
        var ss = new SmoothSurfaceProvider(); ss.Build(small, 0.25f);
        Check(!ss.Released, "small surface keeps its build tables");
    }

    /// <summary>A pooled surface (the damage updates': BorrowScratch, reused build after build, its tables borrowed
    /// from the shared pool) builds exactly what a fresh surface builds, shape after shape.</summary>
    static void BorrowedTablesBuildTheSame()
    {
        var pooled = new SmoothSurfaceProvider { BorrowScratch = true };
        var big = new SetGrid();
        for (int x = -40; x < 40; x++) for (int y = -1; y <= 1; y++) for (int z = -40; z < 40; z++) big.Cells.Add(new Vector3I(x, y, z));
        var small = new SetGrid();
        for (int x = 0; x < 7; x++) for (int y = 0; y < 3; y++) for (int z = 0; z < 2; z++) small.Cells.Add(new Vector3I(x, y, z));
        var damaged = new SetGrid();
        foreach (var c in big.Cells) if (!(Math.Abs(c.X + 3) <= 4 && Math.Abs(c.Z - 9) <= 5)) damaged.Cells.Add(c);
        foreach (var (g, what) in new[] { (big, "big"), (small, "small"), (damaged, "big, damaged"), (small, "small again") })
        {
            pooled.Build(g, 0.25f);
            var fresh = new SmoothSurfaceProvider(); fresh.Build(g, 0.25f);
            SameSurface(pooled, fresh, "pooled surface (BorrowScratch), " + what);
            Check(pooled.Released, "pooled surface gave its build tables back (" + what + ")");
        }
    }

    static int Main()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        SnapshotHoldsItsCells();
        BigPlateIsFast();
        BigSurfaceReleasesAndRebuilds();
        BorrowedTablesBuildTheSame();
        foreach (var (rings, crease) in new[] { (2, 90f), (1, 90f), (3, 90f), (2, 45f), (2, 180f) })
            IncrementalEqualsFresh(rings, crease);
        Console.WriteLine($"SurfaceTests: {_checks - _fails}/{_checks} passed ({sw.ElapsedMilliseconds} ms)");
        return _fails == 0 ? 0 : 1;
    }
}
