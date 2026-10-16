using System.IO;
using AeroMod;

// AeroBench 'struct': the structure and hull-heat models (Scripts/Simulation/Structure) on real blueprints
// (Tests/data/struct/*.struct - tools/gridgen/bp2struct.py: every block's cells, mass and name).
//   dotnet run -c Release -- struct [ship ...]
// Per ship: a pull at 250 m/s (the aero model's own loads: wings spread over their cells, the hull's face pressures),
// the joints' utilisation and the load factor it would break at; a roll and a pitch flick (inertia only); a reentry
// soak (2 km/s in thin air, nose first and belly first) - the hottest skins - and the pull again with the hot structure.
static partial class Program
{
    sealed class StructShip
    {
        public string Name; public Ship Aero; public ShipStructure S; public HullHeat H;
        public List<string> BlockName = new();
        /// <summary>The dump's mount points ("m block dx dy dz lo hi" lines; none in older dumps; NOMOUNTS=1 ignores them).</summary>
        public List<(int block, Vector3I dir, Vector3I lo, Vector3I hi)> Mounts = new();
        public Vector3 Nose, Up;
        public float Cs, Co;
    }

    static readonly Dictionary<string, (Vector3 nose, Vector3 up)> StructAxes = new()
    {
        ["tupolev_tu134"] = (Vector3.UnitZ, Vector3.UnitY), ["plane2"] = (-Vector3.UnitX, Vector3.UnitY),
        ["grasshopper"] = (-Vector3.UnitZ, Vector3.UnitY), ["jetliner"] = (Vector3.UnitX, Vector3.UnitY),
    };

    static StructShip LoadStruct(string path)
    {
        var sh = new StructShip { Name = Path.GetFileNameWithoutExtension(path) };
        var boxes = new List<(Vector3I, Vector3I)>(); var bb = new List<int>(); var lo = new List<Vector3I>(); var hi = new List<Vector3I>();
        var mass = new List<float>(); var mat = new List<int>();
        LastThrusters = new();
        foreach (var line in File.ReadLines(path))
        {
            var p = line.Split(' ');
            if (line.StartsWith("#")) { int i = Array.IndexOf(p, "blockSize"); if (i >= 0) BlockSize = float.Parse(p[i + 1], System.Globalization.CultureInfo.InvariantCulture); continue; }
            if (p[0] == "m") { if (p.Length >= 11 && Environment.GetEnvironmentVariable("NOMOUNTS") == null) sh.Mounts.Add(ParseMount(p)); continue; }
            if (p.Length < 9) continue;
            var a = new Vector3I(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2])); var b = new Vector3I(int.Parse(p[3]), int.Parse(p[4]), int.Parse(p[5]));
            int blk = int.Parse(p[6]);
            boxes.Add((a, b)); bb.Add(blk); lo.Add(a); hi.Add(b);
            while (mass.Count <= blk) { mass.Add(0f); mat.Add(StructMaterial.Functional); sh.BlockName.Add("?"); }
            mass[blk] = float.Parse(p[7], System.Globalization.CultureInfo.InvariantCulture);
            sh.BlockName[blk] = p[8]; mat[blk] = StructMaterial.Classify(p[8]);
            if (p.Length >= 13 && p[9] == "thr") LastThrusters.Add((a, b, new Vector3(int.Parse(p[10]), int.Parse(p[11]), int.Parse(p[12]))));
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        sh.S = ShipStructure.Build(bb, lo, hi, mass, mat, sh.Mounts);
        double tS = sw.Elapsed.TotalMilliseconds;
        if (Environment.GetEnvironmentVariable("PERF") != null) StructPerf(sh.Name, bb, lo, hi, mass, mat, sh.Mounts);
        if (Environment.GetEnvironmentVariable("SHAPES") != null) ShapeStats(sh);
        sh.Aero = Build(sh.Name, boxes);
        sh.Aero.Com = sh.S.Com;   // (the blocks' real masses)
        int k = ForceScale ?? SnapshotGridAccessor.CellScale(BlockSize);
        (sh.Cs, sh.Co) = SnapshotGridAccessor.CellGeometry(k);
        sw.Restart();
        var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var area = new List<float>(); var blkOf = new List<int>();
        int miss = 0;
        foreach (var f in sh.Aero.Surface.Faces)
        {
            var raw = f.RawNormal.LengthSquared() > 0.5f ? f.RawNormal : f.Normal;
            int b = sh.S.BlockAt(f.Position - raw * (sh.Cs * 0.25f));
            if (b < 0) miss++;
            pos.Add(f.Position); nrm.Add(f.Normal); area.Add(f.Area); blkOf.Add(b);
        }
        sh.H = HullHeat.Build(sh.S, pos, nrm, area, blkOf, sh.Cs);
        double tH = sw.Elapsed.TotalMilliseconds;
        (sh.Nose, sh.Up) = StructAxes.TryGetValue(sh.Name, out var ax) ? ax : (Vector3.UnitZ, Vector3.UnitY);
        Console.WriteLine($"{sh.Name}: {sh.S.Count} blocks, {sh.S.TotalMass / 1000f:F1} t, {sh.S.CutCount} cuts, {sh.S.Components} piece(s); {sh.H.FaceCount} faces ({miss} on no block) | structure {tS:F0} ms, heat {tH:F0} ms"
            + (sh.S.HasMounts ? $" | mounts {sh.Mounts.Count}: contacts mated {sh.S.MatedContacts}, touching unmated ({(ShipStructure.TouchingShare > 0f ? $"at {ShipStructure.TouchingShare:F2}" : "dropped")}) {sh.S.UnmatedContacts}, without mount data {sh.S.UnmountedContacts}" : ""));
        return sh;
    }

    static (int, Vector3I, Vector3I, Vector3I) ParseMount(string[] p) =>
        (int.Parse(p[1]), new Vector3I(int.Parse(p[2]), int.Parse(p[3]), int.Parse(p[4])), new Vector3I(int.Parse(p[5]), int.Parse(p[6]), int.Parse(p[7])), new Vector3I(int.Parse(p[8]), int.Parse(p[9]), int.Parse(p[10])));

    /// <summary>(PERF=1) Build, and the build plus its first solve (the sections, the network), and a solve - best of 7.</summary>
    static void StructPerf(string name, List<int> bb, List<Vector3I> lo, List<Vector3I> hi, List<float> mass, List<int> mat, List<(int, Vector3I, Vector3I, Vector3I)> mounts)
    {
        double bMin = double.MaxValue, fMin = double.MaxValue, sMin = double.MaxValue;
        long aBuild = 0, aFirst = 0, aSolve = 0; int pieces = 0;
        var rng = new Random(5);
        for (int rep = 0; rep < 25; rep++)
        {
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var s = ShipStructure.Build(bb, lo, hi, mass, mat, mounts);
            double tb = sw.Elapsed.TotalMilliseconds;
            long a1 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < s.Count; i++) s.ExtForce[i] = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * 1000f;
            s.SolveFree(Vector3.Zero);
            double tf = sw.Elapsed.TotalMilliseconds;
            long a2 = GC.GetAllocatedBytesForCurrentThread();
            sw.Restart(); for (int k = 0; k < 10; k++) s.SolveFree(new Vector3(0.1f, 0.2f, 0.3f)); double ts = sw.Elapsed.TotalMilliseconds / 10;
            long a3 = GC.GetAllocatedBytesForCurrentThread();
            bMin = Math.Min(bMin, tb); fMin = Math.Min(fMin, tf); sMin = Math.Min(sMin, ts);
            aBuild = a1 - a0; aFirst = a2 - a0; aSolve = (a3 - a2) / 10; pieces = s.Components;
        }
        Console.WriteLine($"   PERF {name}: Build {bMin:F1} ms, Build + first solve (sections) {fMin:F1} ms, a solve {sMin:F2} ms (best of 25) | allocated: Build {aBuild / 1048576.0:F1} MB, Build + first solve {aFirst / 1048576.0:F1} MB, a solve {aSolve / 1024.0:F1} KB | {pieces} piece(s){(ShipStructure.MountJoints && mounts.Count > 0 ? ", mount joints" : "")}");
    }

    /// <summary>(SHAPES=1) each block type's shape: thickness (2 x cells / faces), longest side, lattice or shell, wall.</summary>
    static void ShapeStats(StructShip sh)
    {
        var s = sh.S; var seen = new HashSet<string>(); var rows = new List<(float r, string line)>();
        for (int i = 0; i < s.Count; i++)
        {
            string nm = sh.BlockName[i].Split('_')[0]; if (!seen.Add(nm)) continue;
            var d = s.Hi[i] - s.Lo[i]; float L = MathF.Max(d.X, MathF.Max(d.Y, d.Z)) / ShipStructure.Cell;
            rows.Add((s.Thick[i], $"{(s.Thick[i] > 0 ? "LATTICE" : "shell  ")} L {L,4:F0} cells, thick {s.Thick[i] * 1000,5:F0} mm, wall {s.Wall[i] * 1000,5:F2} mm, steel {s.Phi[i] * 100,5:F2}%, {s.Mass[i],7:F0} kg {nm}"));
        }
        foreach (var r in rows.OrderBy(r => r.line)) Console.WriteLine("   " + r.line);
    }

    /// <summary>The aero model's loads on each block, travelling along dir at speed v (N, grid-local; moments about the
    /// blocks' centres). Returns the model's own total force.</summary>
    static Vector3 AeroLoads(StructShip sh, Vector3 dir, float v, AtmosphereState atmo)
    {
        var s = sh.S; var ship = sh.Aero;
        Array.Clear(s.ExtForce); Array.Clear(s.ExtMoment);
        var r = ship.Model.Compute(new AeroContext(ship.Grid, ship.Surface, dir * v, atmo, ship.Com, BlockSize, Vector3.Zero, -1f, ship.Manifold));
        float q = 0.5f * (float)atmo.Density * v * v;
        void Add(Vector3 p, Vector3 f, Vector3 inward)
        {
            int b = s.BlockAt(p + inward);
            if (b < 0) return;
            s.ExtForce[b] += f; s.ExtMoment[b] += Vector3.Cross(p - s.Center[b], f);
        }
        // the wings: their force over their cells
        var wings = ship.Model.Wings; var wf = ship.Model.LastWingForces;
        if (wings != null && wf != null)
            for (int i = 0; i < wings.Count && i < wf.Count; i++)
            {
                var cells = wings[i].Cells;
                if (cells == null || cells.Count == 0) continue;
                var f = (wf[i].LiftForce + wf[i].InducedDrag) / cells.Count;
                foreach (var c in cells) Add(new Vector3((c.X + sh.Co) * sh.Cs, (c.Y + sh.Co) * sh.Cs, (c.Z + sh.Co) * sh.Cs), f, Vector3.Zero);
            }
        // the hull: each face's pressure
        var inner = (DampedShadowedDragModel)ship.Model.InnerModel;
        var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var area = new List<float>(); var cps = new List<float>();
        float mach = v / (float)atmo.SpeedOfSound;
        inner.FacePressures(ship.Surface, ship.Manifold, dir, mach, new DampedShadowedDragModel.TableWork(), pos, nrm, area, cps, int.MaxValue);
        for (int i = 0; i < pos.Count; i++) Add(pos[i], -nrm[i] * (cps[i] * q * area[i]), -nrm[i] * (sh.Cs * 0.25f));
        return r.Force;
    }

    static string Where(StructShip sh, int k)
    {
        if (k < 0 && sh.S.MaxBlock >= 0) { var b0 = sh.S.MaxBlock; var q = sh.S.Center[b0] - sh.S.Com; return $"{sh.BlockName[b0]} (across it) at ({q.X:F1},{q.Y:F1},{q.Z:F1}) m from CoM"; }
        if (k < 0) return "-";
        var s = sh.S; s.ContactBlocks(k, out int a, out int b); var p = s.ContactPoint(k) - s.Com;
        // (how much hangs outboard of it along the tree, and how many contacts share its plane at all)
        int ob = s.Outboard(k); var sub = new bool[s.Count]; float m = 0f;
        foreach (int i in s.Order) { if (i == ob || (s.Parent[i] >= 0 && sub[s.Parent[i]])) { sub[i] = true; m += s.Mass[i]; } }
        int plane = 0; var pk = s.ContactPoint(k);
        for (int c = 0; c < s.ContactCount; c++) { var q = s.ContactPoint(c); if (MathF.Abs(q.X - pk.X) < 1e-3f && MathF.Abs(s.ContactUtil[c]) >= 0 && (MathF.Abs(q.X - pk.X) < 1e-3f)) plane++; }
        return $"{sh.BlockName[a]}|{sh.BlockName[b]} at ({p.X:F1},{p.Y:F1},{p.Z:F1}) m from CoM, {m / 1000f:F1} t outboard";
    }

    static Vector3 Inv3(Vector3 c0, Vector3 c1, Vector3 c2, Vector3 r)
    {
        // columns c0..c2: solve [c0 c1 c2] x = r by Cramer
        float det = Vector3.Dot(c0, Vector3.Cross(c1, c2));
        if (MathF.Abs(det) < 1e-30f) return Vector3.Zero;
        return new Vector3(Vector3.Dot(r, Vector3.Cross(c1, c2)), Vector3.Dot(c0, Vector3.Cross(r, c2)), Vector3.Dot(c0, Vector3.Cross(c1, r))) / det;
    }

    static int StructBench(string dataDir, AtmosphereState atmo, string[] names)
    {
        var dir = Path.Combine(dataDir, "struct");
        var files = names.Length > 0 ? names.Select(n => File.Exists(n) ? n : Path.Combine(dir, n + ".struct")).ToArray() : Directory.GetFiles(dir, "*.struct");
        if (Environment.GetEnvironmentVariable("MOUNTS") is string mj) ShipStructure.MountJoints = mj != "0";   // (MOUNTS=1 / 0: the mount joints on / off, for A/B)
        if (float.TryParse(Environment.GetEnvironmentVariable("TOUCH"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tsv)) ShipStructure.TouchingShare = tsv;   // (TOUCH=0..1: the touching-only faces' share)
        var sea = new AtmosphereState { Density = 1.225, Pressure = 101325, Temperature = 288, SpeedOfSound = 340 };
        foreach (var f in files)
        {
            if (!File.Exists(f)) { Console.WriteLine($"{f}: missing"); continue; }
            var sh = LoadStruct(f);
            var s = sh.S;
            float W = s.TotalMass * 9.81f;
            if (Environment.GetEnvironmentVariable("CGTRACE") != null)
            {
                s.BuildNetwork();
                int m6 = 6 * s.Count; var rng = new Random(1); var rr = new double[m6]; var zz = new double[m6]; var yy = new double[m6];
                for (int i = 0; i < m6; i++) rr[i] = rng.NextDouble() - 0.5;
                for (int i = 0; i < s.Count; i++) if (s.Parent[i] < 0) for (int j = 0; j < 6; j++) rr[6 * i + j] = 0;
                s.TreeSolve(rr, zz); s.MulK(zz, yy, true);
                double e2 = 0, n2 = 0; for (int i = 0; i < m6; i++) { e2 += (yy[i] - rr[i]) * (yy[i] - rr[i]); n2 += rr[i] * rr[i]; }
                Console.WriteLine($"      tree solve exactness on {sh.Name}: {Math.Sqrt(e2 / n2):E2}");
            }
            // ── a pull: 250 m/s at sea level, 6 deg nose up ──
            float a = 6f * MathF.PI / 180f, V = 250f;
            var travel = Vector3.Normalize(sh.Nose * MathF.Cos(a) - sh.Up * MathF.Sin(a));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var total = AeroLoads(sh, travel, V, sea);
            double tLoad = sw.Elapsed.TotalMilliseconds;
            Vector3 sum = Vector3.Zero; for (int i = 0; i < s.Count; i++) sum += s.ExtForce[i];
            sw.Restart();
            s.RigidAccel(Vector3.Zero);
            if (Environment.GetEnvironmentVariable("CGTRACE") != null) ShipStructure.Trace = (it, rr, rz) => { if (it < 20 || it % 50 == 0) Console.WriteLine($"      cg {it}: {rr:E2} rz {rz:E2}"); };
            s.SolveFree(Vector3.Zero);
            ShipStructure.Trace = null;
            double tSolve = sw.Elapsed.TotalMilliseconds;
            // (successive solves, warm-started, as the game would run them: the sharing converging)
            var trail = new System.Text.StringBuilder();
            for (int rep = 0; rep < 3; rep++) { trail.Append($" {s.MaxUtil:F2}/{s.RelResidual:E0}"); s.SolveFree(Vector3.Zero); }
            if (Environment.GetEnvironmentVariable("TOPWORST") != null)
            {
                var worst = Enumerable.Range(0, s.ContactCount).OrderByDescending(k => s.ContactUtil[k]).Take(8);
                foreach (int k in worst) { s.ContactBlocks(k, out int ca, out int cb); Console.WriteLine($"      {s.ContactUtil[k]:F2} {sh.BlockName[ca]}({s.Wall[ca] * 1000:F1} mm, {s.Mass[ca]:F0} kg)|{sh.BlockName[cb]}({s.Wall[cb] * 1000:F1} mm, {s.Mass[cb]:F0} kg) at {s.ContactPoint(k) - s.Com}"); }
                var wb = Enumerable.Range(0, s.Count).OrderByDescending(q => s.BlockUtil[q]).Take(4);
                foreach (int wbk in wb) Console.WriteLine($"      {s.BlockUtil[wbk]:F2} across {sh.BlockName[wbk]} ({s.Wall[wbk] * 1000:F1} mm, {s.Mass[wbk]:F0} kg) at {s.Center[wbk] - s.Com}");
            }
            sw.Restart(); for (int rep = 0; rep < 5; rep++) s.SolveFree(Vector3.Zero);
            Console.WriteLine($"   successive solves (worst/residual):{trail} | a solve, built: {sw.Elapsed.TotalMilliseconds / 5:F1} ms, {s.SliceCount} slices");
            float n = Vector3.Dot(sum, sh.Up) / W;
            Console.WriteLine($"   pull 250 m/s 6 deg: {n:F1} g (blocks' loads {sum.Length() / 1000:F0} kN vs the model's {total.Length() / 1000:F0} kN); worst {s.MaxUtil:F2} at {Where(sh, s.MaxContact)} -> breaks near {n / MathF.Max(s.MaxUtil, 1e-6f):F1} g | residual {s.ResidualForce.Length() / MathF.Max(1f, sum.Length()) * 100:F2}%, {s.Iterations} iterations to {s.RelResidual:E1} | loads {tLoad:F0} ms, solve {tSolve:F1} ms");
            float coldUtil = s.MaxUtil;
            var coldExtF = (Vector3[])s.ExtForce.Clone(); var coldExtM = (Vector3[])s.ExtMoment.Clone();
            // ── flicks: inertia only ──
            Array.Clear(s.ExtForce); Array.Clear(s.ExtMoment);
            var pitchAx = Vector3.Normalize(Vector3.Cross(sh.Nose, sh.Up));
            // (the turn's torque - the gyros' - put in at the root: no torque, no angular acceleration)
            void Flick(Vector3 alpha) { Array.Clear(s.ExtForce); Array.Clear(s.ExtMoment); var m0 = Torque(alpha); Array.Clear(s.ExtMoment); s.ExtMoment[s.Root] = m0; s.SolveFree(Vector3.Zero); }
            Vector3 Torque(Vector3 alpha) { s.ExtMoment[s.Root] = Vector3.UnitX; s.RigidAccel(Vector3.Zero); var ax = s.AngAccel; s.ExtMoment[s.Root] = Vector3.UnitY; s.RigidAccel(Vector3.Zero); var ay = s.AngAccel; s.ExtMoment[s.Root] = Vector3.UnitZ; s.RigidAccel(Vector3.Zero); var az = s.AngAccel;
                // alpha = A m: invert the 3x3 [ax ay az]
                return Inv3(ax, ay, az, alpha); }
            Flick(sh.Nose * 3f);
            Console.WriteLine($"   roll flick 3 rad/s2: worst {s.MaxUtil:F3} at {Where(sh, s.MaxContact)} -> breaks near {3f / MathF.Max(s.MaxUtil, 1e-6f):F0} rad/s2");
            Flick(pitchAx * 3f);
            Console.WriteLine($"   pitch flick 3 rad/s2: worst {s.MaxUtil:F3} at {Where(sh, s.MaxContact)} -> breaks near {3f / MathF.Max(s.MaxUtil, 1e-6f):F0} rad/s2");
            Array.Clear(s.ExtForce); Array.Clear(s.ExtMoment); s.SolveFree(sh.Nose * 3f);
            Console.WriteLine($"   roll spin 3 rad/s: worst {s.MaxUtil:F3} at {Where(sh, s.MaxContact)}");
            // ── reentry soak: 2 km/s, rho 0.002, 90 s; nose first, then belly first ──
            foreach (var (tag, d) in new[] { ("nose first", sh.Nose), ("belly first", Vector3.Normalize(sh.Nose * 0.5f - sh.Up * 0.866f)) })
            {
                var h = sh.H;
                for (int i = 0; i < h.FaceCount; i++) h.FaceT[i] = 250f;
                for (int b = 0; b < h.BlockCount; b++) h.CoreT[b] = 250f;
                sw.Restart(); h.Pass(d); double tPass = sw.Elapsed.TotalMilliseconds;
                sw.Restart(); for (int k = 0; k < 90; k++) h.Step(0.002f, 250f, 2000f, 250f, 1f); double tStep = sw.Elapsed.TotalMilliseconds / 90;
                float hot = h.MaxSkin(out int hf);
                int hb = hf >= 0 ? h.FaceBlock[hf] : -1;
                int over = 0, weak = 0;
                for (int b = 0; b < h.BlockCount; b++) { if (h.BlockSkinMaxK[b] > StructMaterial.BurnK[s.Mat[b]]) over++; if (StructMaterial.StrengthAt(s.Mat[b], h.BlockTempK[b]) < 0.9f) weak++; }
                Console.WriteLine($"   reentry {tag} 90 s: hottest skin {hot:F0} K ({(hb >= 0 ? sh.BlockName[hb] : "-")}, R {(hf >= 0 ? h.FaceRadius[hf] : 0):F2} m), {over} blocks burning, {weak} weakened >10%, body R {h.BodyRadius:F1} m | pass {tPass:F0} ms, step {tStep:F2} ms");
                // the pull again on the hot structure
                Array.Copy(coldExtF, s.ExtForce, s.Count); Array.Copy(coldExtM, s.ExtMoment, s.Count);
                Array.Copy(h.BlockTempK, s.TempK, s.Count);
                s.SolveFree(Vector3.Zero);
                Console.WriteLine($"      the same pull, hot: worst {s.MaxUtil:F2} (cold {coldUtil:F2}) at {Where(sh, s.MaxContact)}");
                for (int b = 0; b < s.Count; b++) s.TempK[b] = 290f;
            }
        }
        return 0;
    }
}
