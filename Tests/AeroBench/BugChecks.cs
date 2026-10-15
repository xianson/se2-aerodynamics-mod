using System.IO;
using System.Reflection;
using AeroMod;

// AeroBench gate: regressions for bugs fixed 2026-10-15 (the reorg audit, section 5 - ids A3...), each failing before its fix.
static partial class Program
{
    static int BugChecks(AtmosphereState atmo)
    {
        int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; Console.WriteLine($"   {(ok ? "ok  " : "FAIL")} {what}"); }

        // A3: MergeCoplanarWings kept its span lists in step with the wings (it copied the last wing's span into the
        // removed slot while RemoveAt shifted the wings): four coplanar panels, the first two close, the last two 15 m apart.
        {
            static LiftingSurface Panel(int x0)
            {
                var cells = new List<Vector3I>();
                for (int x = x0; x < x0 + 10; x++) for (int z = 0; z < 4; z++) cells.Add(new Vector3I(x, 0, z));
                var c = new Vector3((x0 + 4.5f) * 0.25f, 0f, 0.375f);
                return new LiftingSurface(Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ, 2.5f, 2.5f, 0.1f, 0f, c, c, 40, cells);
            }
            var det = new ConnectedComponentWingDetector();
            var merge = typeof(ConnectedComponentWingDetector).GetMethod("MergeCoplanarWings", BindingFlags.NonPublic | BindingFlags.Instance);
            var wings = (List<LiftingSurface>)merge.Invoke(det, new object[] { new List<LiftingSurface> { Panel(0), Panel(12), Panel(40), Panel(100) }, 0.5f });
            Check(wings.Count == 3 && MathF.Abs(wings[0].Span - 5.5f) < 1e-3f,
                $"A3 coplanar merge: 4 panels -> {wings.Count} wings (3: the close pair merged, 5.5 m span: {(wings.Count > 0 ? wings[0].Span : 0f):F2})");
        }
        // A4: a pooled table builder forgot the last grid's flow-through faces - an engine-less grid's table built on
        // the builder after a grid with intakes is the one a fresh builder makes.
        {
            var pa = Path.Combine(dataDir, "pelican.boxes"); var pb = Path.Combine(dataDir, "bluefighter.boxes");
            if (File.Exists(pa) && File.Exists(pb))
            {
                ForceTable TableOn(DampedShadowedDragModel builder, Ship s, List<bool> through)
                {
                    var tmp = new LiftingSurfaceModel(builder, liftModel: new CompressibleWingModel());
                    tmp.InstallWings(s.Model.Wings?.ToList() ?? new List<LiftingSurface>());
                    tmp.BuildFaceOverrideIndex(s.Surface, new List<IAeroBlockComponent>());
                    tmp.ExcludeWingFaces(builder, s.Surface.Version);
                    if (through != null) builder.SetThroughFaces(through, s.Surface.Version);
                    return builder.BuildForceTable(s.Grid, s.Surface, s.Manifold, s.Com, n: 4, nj: 1);
                }
                var x = Build("pelican", LoadBoxes(pa)); var y = Build("bluefighter", LoadBoxes(pb));
                var pooled = new DampedShadowedDragModel();
                var all = Enumerable.Repeat(true, Math.Max(x.Surface.FaceCount, y.Surface.FaceCount)).ToList();   // (every face an intake)
                TableOn(pooled, x, all);
                var a = TableOn(pooled, y, null); var b = TableOn(new DampedShadowedDragModel(), y, null);
                double worst = 0;
                for (int f = 0; f < 6; f++) for (int i = 0; i <= 4; i++) for (int j = 0; j <= 4; j++)
                {
                    var ea = a.At(f, i, j); var eb = b.At(f, i, j);
                    worst = Math.Max(worst, Math.Abs(ea[0] - eb[0]) + Math.Abs(ea[1] - eb[1]) + Math.Abs(ea[2] - eb[2]));
                }
                Check(worst < 1e-3, $"A4 pooled builder: an engine-less grid after one with intakes, table off a fresh builder's by {worst:F3} (unit q)");
            }
        }
        // A5: the chunks' face physics (their local updates after damage) are the ship's own - skin friction from its
        // length - made before the build on a pooled builder, and read back from the table cache.
        {
            var pa = Path.Combine(dataDir, "jetliner.boxes"); var pb = Path.Combine(dataDir, "bluefighter.boxes");
            if (File.Exists(pa) && File.Exists(pb))
            {
                var x = Build("jetliner", LoadBoxes(pa)); var yBoxes = LoadBoxes(pb); var y = Build("bluefighter", yBoxes);
                var pooled = new DampedShadowedDragModel();
                pooled.BuildForceTable(x.Grid, x.Surface, x.Manifold, x.Com, n: 2, nj: 1);
                var ch = new ChunkedTable(4, 8f, new FacePhysics(pooled));   // (as the game makes them: before the build)
                var t = pooled.BuildForceTable(y.Grid, y.Surface, y.Manifold, y.Com, n: 4, nj: 1, chunks: ch);
                float own = pooled.SkinCf, lengthOne = new FacePhysics(new DampedShadowedDragModel()).Cf;
                Check(ch.Phys.Cf == own, $"A5 chunk physics after a pooled build: Cf {ch.Phys.Cf:F5}, the ship's {own:F5} (the jetliner's {DampedShadowedDragModel.TurbulentCf(60f):F5}, 1 m {lengthOne:F5})");
                string key = AeroTableCache.Key(yBoxes, BlockSize, SnapshotGridAccessor.CellScale(BlockSize), new FacePhysics(new DampedShadowedDragModel())) + "a5test";
                AeroTableCache.Save(key, t, ch, new List<LiftingSurface>());
                bool ok = AeroTableCache.TryLoad(key, new FacePhysics(new DampedShadowedDragModel()), out _, out var c2, out _);
                try { File.Delete(Path.Combine(Path.GetTempPath(), "AeroMod", "tables", key + ".bin")); } catch { }
                Check(ok && c2.Phys.Cf == own, $"A5 chunk physics from the table cache: Cf {(ok ? c2.Phys.Cf : -1f):F5}, the ship's {own:F5}");
            }
        }
        // A6: a local update after damage (ChunkedTable.ComputeLocal) as the full build sees the damaged ship: the same
        // occlusion rule (grazing faces, a face's own plane) and the full build's pressure recovery kept per chunk.
        {
            var pb = Path.Combine(dataDir, "bluefighter.boxes");
            if (File.Exists(pb))
            {
                var boxes = LoadBoxes(pb);
                var whole = Build("bluefighter", boxes);
                var dsm = (DampedShadowedDragModel)whole.Model.InnerModel;
                var chunks = new ChunkedTable(8, 16f, new FacePhysics(dsm));
                var table = dsm.BuildForceTable(whole.Grid, whole.Surface, whole.Manifold, whole.Com, chunks: chunks);
                var rng = new Random(3);
                var gone = new HashSet<int>(Enumerable.Range(0, 10).Select(_ => rng.Next(boxes.Count)));
                var after = Build("bluefighter", boxes.Where((bx, i) => !gone.Contains(i)).ToList());
                var dirty = new HashSet<long>();
                foreach (int i in gone) { var (lo, hi) = boxes[i]; chunks.KeysIn(new Vector3(lo.X, lo.Y, lo.Z) * 0.25f - new Vector3(2f), new Vector3(hi.X + 1, hi.Y + 1, hi.Z + 1) * 0.25f + new Vector3(2f), dirty); }
                var faces = LocalFaces(after.Grid, dirty, chunks, after.Surface);
                chunks.Prepare(dirty);
                table = chunks.Apply(chunks.ComputeLocal(dirty, faces, 2, _hideOnly), table);
                ChunkedTable.FacesDone(faces);
                var adsm = (DampedShadowedDragModel)after.Model.InnerModel;
                var exact = adsm.BuildForceTable(after.Grid, after.Surface, after.Manifold, whole.Com);
                var r = new Random(9); var err = new List<double>();
                for (int k = 0; k < 200; k++)
                {
                    var d = Vector3.Normalize(new Vector3((float)r.NextDouble() * 2 - 1, (float)r.NextDouble() * 2 - 1, (float)r.NextDouble() * 2 - 1));
                    var ctx = new AeroContext(after.Grid, after.Surface, d * 150f, atmo, whole.Com, BlockSize, Vector3.Zero, -1f, after.Manifold);
                    var fe = exact.Evaluate(ctx, adsm.SubsonicLimit, adsm.SupersonicLimit, adsm.Streamlining).Force;
                    var fp = table.Evaluate(ctx, adsm.SubsonicLimit, adsm.SupersonicLimit, adsm.Streamlining).Force;
                    err.Add((fp - fe).Length() / Math.Max(fe.Length(), 1));
                }
                err.Sort();
                Check(err[100] < 0.05, $"A6 local update after 10 blocks lost (Bluefighter): off the full build by median {err[100] * 100:F1}% (p95 {err[190] * 100:F1}%)");
            }
        }
        // A9: a table build leaves the priority of the threads it runs on as it found it (they are thread-pool threads:
        // the helpers, and here the caller - a pool thread too - runs the same worker).
        {
            var pb = Path.Combine(dataDir, "bluefighter.boxes");
            if (File.Exists(pb))
            {
                var y = Build("bluefighter", LoadBoxes(pb));
                var after = System.Threading.Tasks.Task.Run(() =>
                {
                    System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Normal;
                    ((DampedShadowedDragModel)y.Model.InnerModel).BuildForceTable(y.Grid, y.Surface, y.Manifold, y.Com, n: 2, nj: 1);
                    var p = System.Threading.Thread.CurrentThread.Priority;
                    System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Normal;
                    return p;
                }).Result;
                Check(after == System.Threading.ThreadPriority.Normal, $"A9 table build leaves its thread's priority alone: {after} after (Normal before)");
            }
        }
        // A30 (audited, not a bug on the game's .NET 9): the force table asked along no direction (or NaN) - (int)NaN
        // saturates to 0 since .NET 9 (no negative index), and the NaN sample's area fails the area test: the fallback.
        {
            var t = new ForceTable(4, 1);
            for (int f = 0; f < 6; f++) for (int i = 0; i <= 4; i++) for (int j = 0; j <= 4; j++) { var e0 = t.At(f, i, j); e0[0] = e0[1] = e0[2] = -0.5f; e0[12] = 1f; }   // (a hull: frontal 1 m2)
            float sZero = float.NaN; bool threw = false;
            try { sZero = t.StreamliningAlong(Vector3.Zero, 0.4f); Span<float> e = stackalloc float[ForceTable.Stride]; t.Sample(new Vector3(float.NaN), e); }
            catch (Exception ex) { threw = true; Console.WriteLine("      " + ex.GetType().Name + ": " + ex.Message); }
            Check(!threw && sZero == 0.4f, $"A30 force table along a zero / NaN direction: {(threw ? "threw" : "streamlining " + sZero + " (the fallback 0.4)")}");
        }
        // A20: two solids touching only at a corner are both hull (only the larger was; the other culled as a cavity), and
        // a sealed room inside a box is still a cavity.
        {
            int Classified(List<(Vector3I, Vector3I)> bx, out int cavity)
            {
                var g = new SnapshotGridAccessor(bx);
                var surf = new SmoothSurfaceProvider();
                surf.BeginBuild(g, 0.5f);
                while (!surf.AddCellBatch(int.MaxValue)) { }
                surf.FinalizeBuild();
                var mc = new ManifoldClassifier(); mc.Classify(surf);
                cavity = mc.CavityFaceCount; return mc.HullFaceCount;
            }
            var edge = new List<(Vector3I, Vector3I)> { (new Vector3I(0, 0, 0), new Vector3I(3, 3, 3)), (new Vector3I(4, 4, 4), new Vector3I(9, 9, 9)) };
            int hull = Classified(edge, out int cav);
            var room = new List<(Vector3I, Vector3I)>();
            for (int x = 0; x < 6; x++) for (int y = 0; y < 6; y++) for (int z = 0; z < 6; z++)
                if (x == 0 || x == 5 || y == 0 || y == 5 || z == 0 || z == 5) room.Add((new Vector3I(x, y, z), new Vector3I(x, y, z)));
            int roomHull = Classified(room, out int roomCav);
            Check(cav == 0 && hull == 6 * 16 + 6 * 36 && roomCav == 6 * 16 && roomHull == 6 * 36,
                $"A20 hull: two solids touching at a corner {hull} hull / {cav} cavity faces (all {6 * 16 + 6 * 36} hull); a sealed room {roomHull} / {roomCav} (its {6 * 16} inner faces cavity)");
        }
        return fails;
    }
}
