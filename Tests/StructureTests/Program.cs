using AeroMod;

// Structure and hull heat, offline (Scripts/Simulation/Structure - the literal mod sources):
//  1. a cantilever: the root joint's moment and stress match the beam formula;
//  2. balance: random loads on a random ship, with the ship's own accelerations, leave nothing at the root;
//  3. bonded strips bend as one deep section (the cut): two stacked strips well over twice as strong as one;
//  4. whipping: a spinning boom's root carries its blocks' centripetal pull; a flick its tangential inertia;
//  5. heat weakens: a hot root's utilisation goes up by 1 / StrengthAt;
//  6. heat balance: at steady state the heat in equals the heat radiated;
//  7. blunt vs sharp: a blunt face's local radius is large and it runs cooler than a needle's tip;
//  8. a hot hull cools in slow air;
//  9. real shapes: a truss's four one-cell walls far stronger than a thin box of the same mass, a slab buckling over its
//     own width at its own depth, a block's boxes giving the same section however they split it;
// 10. real joints: with mount points, touching faces with no mated mounts carry nothing; a part-mated face carries
//     through its mated cells only.
// 11. permanent bends: a wing lifted past its yield sets about its root - up, the load's way, growing outward, by the
//     overshoot past its last peak; a body-sized far side is not bent.
// 12. fatigue and progressive failure (StructureFatigue, 2026-10-14): nothing under the endurance limit; health only ever
//     falls; 2 Hz and 8 Hz evaluations end within a few percent; a snap only past SnapUtil; the ledger writes the health in
//     steps (5%, at most once a second, settled remainders, damage stages at once, a failure at once) and re-anchors on a
//     fresh reading (a repair, someone else's damage).
// 12. the contact balance under another mod's velocity writes (ContactBalance, 2026-10-14): synthetic ticks in the game's
//     order (thrust, the read, the physics step with its contacts and speed clamp, then the Orbital mod's writes): a frame
//     anchor drained every tick reads no load from its own thrust, a warp switch no impulse, a ship resting on the ground
//     under a spinning planet's fictitious writes its weight, a ship thrusting at the speed cap nothing along its travel;
//     the jump test is not fooled by a written velocity.
// 13. a railed re-entry's braking (StructureEval.EntryLoads): m x the deceleration on the windward faces, no net moment;
//     the free body decelerates at it, the nose carries it.
// 14. the neck fails whole (StructureEval.DamageBySection, 2026-10-15): a wing on a narrow neck under a growing lift - the
//     neck's root cut tires as one, fails in one evaluation with the trim tied across it, and nothing creeps outboard after;
//     by block (A/B) it zipped; a snap with one cut allowed fails one cut whole and leaves the next untouched.
static class Program
{
    static int _fails, _checks;
    static void Check(bool ok, string what) { _checks++; if (!ok) { _fails++; Console.WriteLine("FAIL " + what); } else if (Environment.GetEnvironmentVariable("VERBOSE") != null) Console.WriteLine("ok   " + what); }

    const float SmallMass = 33.1f;   // the game's small light armour cube (kg)

    sealed class TShip
    {
        public readonly List<int> BoxBlock = new(); public readonly List<Vector3I> Lo = new(), Hi = new();
        public readonly List<float> Mass = new(); public readonly List<int> Mat = new();
        public List<(int block, Vector3I dir, Vector3I lo, Vector3I hi)> Mounts;
        /// <summary>A block of several boxes (inclusive cells).</summary>
        public int Cells(float mass, int mat, params (Vector3I lo, Vector3I hi)[] boxes)
        {
            int i = Mass.Count;
            foreach (var (lo, hi) in boxes) { BoxBlock.Add(i); Lo.Add(lo); Hi.Add(hi); }
            Mass.Add(mass); Mat.Add(mat);
            return i;
        }
        public void Mount(int block, Vector3I dir, Vector3I lo, Vector3I hi) => (Mounts ??= new()).Add((block, dir, lo, hi));
        public ShipStructure S; public HullHeat H; public SmoothSurfaceProvider Surf;
        /// <summary>A 0.5 m block at block coordinates b (2 x 2 x 2 cells), or a box of them.</summary>
        public int Block(int x, int y, int z, float mass = SmallMass, int sx = 1, int sy = 1, int sz = 1, int mat = StructMaterial.Armor)
        {
            int i = Mass.Count;
            BoxBlock.Add(i); Lo.Add(new Vector3I(2 * x, 2 * y, 2 * z)); Hi.Add(new Vector3I(2 * (x + sx) - 1, 2 * (y + sy) - 1, 2 * (z + sz) - 1));
            Mass.Add(mass); Mat.Add(mat);
            return i;
        }
        public TShip Build(bool heat = false)
        {
            S = ShipStructure.Build(BoxBlock, Lo, Hi, Mass, Mat, Mounts);
            if (heat)
            {
                var boxes = new List<(Vector3I, Vector3I)>();
                for (int k = 0; k < Lo.Count; k++) boxes.Add((Lo[k], Hi[k]));
                var grid = new SnapshotGridAccessor(boxes);
                Surf = new SmoothSurfaceProvider { CellSize = 0.25f, CellOffset = 0f };
                Surf.BeginBuild(grid, 0.5f);
                while (!Surf.AddCellBatch(int.MaxValue)) { }
                Surf.FinalizeBuild();
                var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var area = new List<float>(); var blk = new List<int>();
                foreach (var f in Surf.Faces)
                {
                    pos.Add(f.Position); nrm.Add(f.Normal); area.Add(f.Area);
                    var raw = f.RawNormal.LengthSquared() > 0.5f ? f.RawNormal : f.Normal;
                    blk.Add(S.BlockAt(f.Position - raw * 0.125f));
                }
                H = HullHeat.Build(S, pos, nrm, area, blk, 0.25f);
            }
            return this;
        }
    }

    static float Tube(float a, float b, float t) => t * b * b * b / 6f + a * t * b * b / 2f;   // I about u (stress across b)

    static void Cantilever()
    {
        var sh = new TShip();
        int root = sh.Block(0, 0, 0, 1e9f);
        int first = -1, tip = -1;
        for (int x = 1; x <= 6; x++) { int b = sh.Block(x, 0, 0); if (x == 1) first = b; tip = b; }
        sh.Build();
        var s = sh.S;
        Check(s.Root == root, $"cantilever: root is the heavy block ({s.Root})");
        s.ExtForce[tip] = new Vector3(0, 1000, 0);
        s.RigidAccel(Vector3.Zero);
        s.Solve(s.Accel, s.AngAccel, Vector3.Zero);
        float arm = s.Center[tip].X - s.JPoint[first].X;   // 2.75 m
        Check(MathF.Abs(arm - 2.75f) < 1e-4f, $"cantilever: arm {arm} m");
        Check(MathF.Abs(MathF.Abs(s.JMoment[first].Z) - 1000f * arm) < 1f, $"cantilever: root moment {s.JMoment[first].Z:F1} vs {1000f * arm:F1}");
        Check(MathF.Abs(MathF.Abs(s.JForce[first].Y) - 1000f) < 0.1f, $"cantilever: root shear {s.JForce[first].Y:F2}");
        float t = SmallMass / (StructMaterial.SteelDensity * 6f * 0.25f);
        float sigma = 1000f * arm * 0.25f / Tube(0.5f, 0.5f, t), tau = 1000f / (0.5f * 2f * (0.5f + 0.5f) * t);
        // (tension at the yield; compression - the governing side - at what the buckled 0.5 m wall panel carries)
        float fy = StructMaterial.Yield[StructMaterial.Armor], fc = ShipStructure.Buckled(fy, t, 0.5f);
        float util = MathF.Sqrt(MathF.Pow(sigma / MathF.Min(fy, fc), 2) + 3f * MathF.Pow(tau / fy, 2));
        Check(fc < fy && MathF.Abs(fc / (MathF.Sqrt(3.615f * 200e9f * (t / 0.5f) * (t / 0.5f) * fy)) - 1f) < 1e-3f, $"buckling: a 0.5 m panel of {t * 1000:F1} mm carries {fc / 1e6:F0} MPa in compression (yield {fy / 1e6:F0})");
        Check(MathF.Abs(s.JUtil[first] / util - 1f) < 0.01f, $"cantilever: root utilisation {s.JUtil[first]:F5} vs beam formula {util:F5}");
        Check(s.MaxJoint == first, $"cantilever: the root is the worst joint ({s.MaxJoint})");
        // the stress falls toward the tip
        Check(s.JUtil[first + 1] < s.JUtil[first] && s.JUtil[tip] < s.JUtil[first + 1], "cantilever: utilisation falls toward the tip");

        // 5. heat weakens: the beam at 950 K
        for (int i = 0; i < s.Count; i++) if (i != root) s.TempK[i] = 950f;
        float cold = s.JUtil[first];
        s.Solve(s.Accel, s.AngAccel, Vector3.Zero);
        float f = StructMaterial.StrengthAt(StructMaterial.Armor, 950f);
        // (compression governs: the buckled panel's ultimate goes as the square root of the yield - so 1/sqrt(f))
        Check(f < 0.9f && MathF.Abs(s.JUtil[first] * MathF.Sqrt(f) / cold - 1f) < 0.02f, $"hot root: utilisation x{s.JUtil[first] / cold:F3} (strength left {f:F3}: x{1f / MathF.Sqrt(f):F3} buckled)");
    }

    static void Balance()
    {
        var rng = new Random(7);
        var sh = new TShip();
        var used = new HashSet<(int, int, int)> { (0, 0, 0) };
        var list = new List<(int, int, int)> { (0, 0, 0) };
        sh.Block(0, 0, 0);
        while (list.Count < 60)
        {
            var (x, y, z) = list[rng.Next(list.Count)];
            int d = rng.Next(6);
            var c = d switch { 0 => (x + 1, y, z), 1 => (x - 1, y, z), 2 => (x, y + 1, z), 3 => (x, y - 1, z), 4 => (x, y, z + 1), _ => (x, y, z - 1) };
            if (!used.Add(c)) continue;
            list.Add(c); sh.Block(c.Item1, c.Item2, c.Item3, 20f + 100f * (float)rng.NextDouble());
        }
        sh.Build();
        var s = sh.S;
        Check(s.Components == 1 && s.Order.Length == s.Count, $"balance: one piece, every block in the tree ({s.Components}, {s.Order.Length}/{s.Count})");
        float sumF = 0f, sumM = 0f;
        for (int i = 0; i < s.Count; i++)
        {
            s.ExtForce[i] = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * 2000f;
            s.ExtMoment[i] = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * 500f;
            sumF += s.ExtForce[i].Length(); sumM += s.ExtMoment[i].Length() + s.ExtForce[i].Length() * 2f;
        }
        var w = new Vector3(0.7f, -1.3f, 0.4f);
        s.RigidAccel(w);
        s.Solve(s.Accel, s.AngAccel, w);
        Check(s.RelResidual < 1e-4, $"balance: the network converged ({s.Iterations} iterations, {s.RelResidual:E1})");
        Check(s.ResidualForce.Length() < 1e-4f * sumF, $"balance: force left at the root {s.ResidualForce.Length():F3} N of {sumF:F0}");
        Check(s.ResidualMoment.Length() < 1e-3f * sumM, $"balance: moment left at the root {s.ResidualMoment.Length():F3} N m of {sumM:F0}");
    }

    static void Composite()
    {
        float Root(bool two)
        {
            var sh = new TShip();
            sh.Block(0, 0, 0, 1e9f, 1, two ? 2 : 1, 1);
            int a1 = -1, tipA = -1, tipB = -1;
            for (int x = 1; x <= 6; x++) { int b = sh.Block(x, 0, 0); if (x == 1) a1 = b; tipA = b; }
            if (two) for (int x = 1; x <= 6; x++) tipB = sh.Block(x, 1, 0);
            sh.Build();
            var s = sh.S;
            if (two) { s.ExtForce[tipA] = new Vector3(0, 500, 0); s.ExtForce[tipB] = new Vector3(0, 500, 0); }
            else s.ExtForce[tipA] = new Vector3(0, 1000, 0);
            s.RigidAccel(Vector3.Zero); s.Solve(s.Accel, s.AngAccel, Vector3.Zero);
            if (two) Check(s.CutContacts(s.JCut[a1]).Count == 2, $"composite: the root cut holds both strips ({s.CutContacts(s.JCut[a1]).Count})");
            return s.JUtil[a1];
        }
        float one = Root(false), two = Root(true);
        Check(one / two > 2.3f, $"composite: one strip {one:F4}, two bonded {two:F4} - x{one / two:F2} stronger (thin-walled stacked tubes: x2.5)");
    }

    static void Whip()
    {
        var sh = new TShip();
        sh.Block(0, 0, 0, 1e9f);
        int first = -1;
        for (int x = 1; x <= 8; x++) { int b = sh.Block(x, 0, 0); if (x == 1) first = b; }
        sh.Build();
        var s = sh.S;
        var w = new Vector3(0, 0, 2f);
        s.Solve(Vector3.Zero, Vector3.Zero, w);
        double pull = 0, flick = 0;
        float xj = s.JPoint[first].X;
        for (int i = 0; i < s.Count; i++)
        {
            if (i == s.Root) continue;
            float r = s.Center[i].X - s.Com.X;
            pull += s.Mass[i] * 4f * r;
            flick += s.Mass[i] * 5f * (r * (s.Center[i].X - xj) + (0.25f + 0.25f) / 12f);
        }
        Check(MathF.Abs((float)(MathF.Abs(s.JForce[first].X) / pull) - 1f) < 1e-3f, $"whip: spinning boom's root pull {s.JForce[first].X:F1} N vs {pull:F1}");
        // (the flick's torque - the gyros' - at the root: what makes 5 rad/s2)
        s.ExtMoment[s.Root] = Vector3.UnitZ; s.RigidAccel(Vector3.Zero);
        s.ExtMoment[s.Root] = Vector3.UnitZ * (5f / s.AngAccel.Z);
        s.Solve(Vector3.Zero, new Vector3(0, 0, 5f), Vector3.Zero);
        Check(MathF.Abs((float)(MathF.Abs(s.JMoment[first].Z) / flick) - 1f) < 1e-3f, $"whip: flicked boom's root moment {s.JMoment[first].Z:F1} N m vs {flick:F1}");
        Check(s.MaxJoint == first, "whip: the root is the worst joint");
    }

    /// <summary>The gyros' torque over the whole ship (the default): a boom turned by a gyro at one end, not spinning yet,
    /// is unstressed - the torque meets the boom's own inertia block for block; bolted on at the gyro it loads the boom.</summary>
    static void GyroWholeShip()
    {
        var sh = new TShip();
        int gyro = sh.Block(0, 0, 0);
        for (int x = 1; x <= 8; x++) sh.Block(x, 0, 0);
        sh.Build();
        var s = sh.S;
        var T = new Vector3(0, 0, 2e6f);
        for (int i = 0; i < s.Count; i++) { s.ExtForce[i] = Vector3.Zero; s.ExtMoment[i] = Vector3.Zero; }
        StructureEval.SpreadOverShip(s, T);
        Vector3 F = Vector3.Zero, M = Vector3.Zero;
        for (int i = 0; i < s.Count; i++) { F += s.ExtForce[i]; M += Vector3.Cross(s.Center[i] - s.Com, s.ExtForce[i]) + s.ExtMoment[i]; }
        Check(F.Length() < 1f && MathF.Abs(M.Z / T.Z - 1f) < 1e-3f, $"gyros over the ship: no net force ({F.Length():F2} N), the torque exact ({M.Z:F0} of {T.Z:F0} N m)");
        s.SolveFree(Vector3.Zero);
        float spread = s.MaxUtil;
        for (int i = 0; i < s.Count; i++) { s.ExtForce[i] = Vector3.Zero; s.ExtMoment[i] = Vector3.Zero; }
        s.ExtMoment[gyro] = T;
        s.SolveFree(Vector3.Zero);
        float bolted = s.MaxUtil;
        Check(spread < 0.01f * bolted && bolted > 0f, $"gyros over the ship: a gyro-turned boom unstressed ({spread:E2}) where torque bolted on at the gyro loads it ({bolted:F3})");
    }

    static void HeatBalance()
    {
        var sh = new TShip();
        for (int x = 0; x < 8; x++) for (int y = 0; y < 8; y++) sh.Block(x, y, 0);
        sh.Build(heat: true);
        var h = sh.H;
        h.Pass(new Vector3(0, 0, -1));
        for (int k = 0; k < 4000; k++) h.Step(0.002f, 250f, 2000f, 250f, 1f);
        double qin = 0, qout = 0;
        for (int i = 0; i < h.FaceCount; i++)
        {
            int b = h.FaceBlock[i]; if (b < 0) continue;
            qin += h.FaceHeatW[i];
            float T = h.FaceT[i];
            qout += StructMaterial.Emissivity[StructMaterial.Armor] * HullHeat.Sigma * (Math.Pow(T, 4) - Math.Pow(250, 4)) * h.FaceArea(i);
        }
        Check(Math.Abs(qin / qout - 1) < 0.02, $"heat balance: in {qin / 1e3:F1} kW, radiated {qout / 1e3:F1} kW at steady state");
        Check(h.FaceT.Count(f => f > 3f) == h.FaceCount && h.FaceBlock.All(b => b >= 0), "heat balance: every face on a block, temperatures sane");
    }

    static void BluntVsSharp()
    {
        // capsule: a 3 m (6 x 6 block) face, two blocks deep; needle: one block across, 12 long. Both travel along -Z.
        var cap = new TShip();
        for (int x = 0; x < 6; x++) for (int y = 0; y < 6; y++) for (int z = 0; z < 2; z++) cap.Block(x, y, z);
        cap.Build(heat: true);
        var ndl = new TShip();
        for (int z = 0; z < 12; z++) ndl.Block(0, 0, z);
        ndl.Build(heat: true);
        var dir = new Vector3(0, 0, -1);
        cap.H.Pass(dir); ndl.H.Pass(dir);
        for (int k = 0; k < 120; k++) { cap.H.Step(0.002f, 250f, 2000f, 250f, 0.5f); ndl.H.Step(0.002f, 250f, 2000f, 250f, 0.5f); }
        // the capsule's face: its centre's radius, its mean front temperature; the needle's tip
        float rCentre = 0f, tFront = 0f; int nFront = 0;
        for (int i = 0; i < cap.H.FaceCount; i++)
        {
            var n = cap.H.FaceNormal(i); var p = cap.H.FacePosition(i);
            if (n.Z > -0.9f) continue;
            tFront += cap.H.FaceT[i]; nFront++;
            if (MathF.Abs(p.X - 1.375f) < 0.2f && MathF.Abs(p.Y - 1.375f) < 0.2f) rCentre = MathF.Max(rCentre, cap.H.FaceRadius[i]);
        }
        tFront /= Math.Max(1, nFront);
        float rTip = float.MaxValue, tTip = 0f;
        for (int i = 0; i < ndl.H.FaceCount; i++)
            if (ndl.H.FaceNormal(i).Z < -0.5f) { rTip = MathF.Min(rTip, ndl.H.FaceRadius[i]); tTip = MathF.Max(tTip, ndl.H.FaceT[i]); }
        Check(rCentre > 1f, $"blunt: the capsule face's centre radius {rCentre:F2} m");
        Check(rTip <= 0.5f, $"sharp: the needle tip's radius {rTip:F2} m");
        Check(tTip > tFront + 100f, $"blunt vs sharp after 60 s at 2 km/s: capsule front mean {tFront:F0} K, needle tip {tTip:F0} K");
        Console.WriteLine($"   blunt vs sharp: capsule face R {rCentre:F2} m, front mean {tFront:F0} K (max {cap.H.MaxSkin(out _):F0}); needle tip R {rTip:F2} m, {tTip:F0} K");
    }

    static void Cooling()
    {
        var sh = new TShip();
        for (int x = 0; x < 4; x++) for (int y = 0; y < 4; y++) sh.Block(x, y, 0);
        sh.Build(heat: true);
        var h = sh.H;
        for (int i = 0; i < h.FaceCount; i++) h.FaceT[i] = 1000f;
        for (int b = 0; b < h.BlockCount; b++) h.CoreT[b] = 1000f;
        h.Pass(new Vector3(0, 0, -1));
        for (int k = 0; k < 300; k++) h.Step(1.2f, 290f, 100f, 290f, 1f);
        float skin = h.FaceT.Average(), mean = h.BlockTempK.Average();
        // (the skins cool fast in the wind; a hollow block's inner walls only through radiation across it: slower)
        Check(skin < 450f && mean < 650f, $"cooling: a 1000 K hull after 300 s at 100 m/s at sea level: skins {skin:F0} K, blocks {mean:F0} K");
    }

    /// <summary>The tree preconditioner is the tree's exact inverse: on a ship with no loops (a branching snake), K (tree
    /// solve of r) = r.</summary>
    static void TreeExact()
    {
        var rng = new Random(3);
        var sh = new TShip();
        var used = new HashSet<(int, int, int)> { (0, 0, 0) };
        var list = new List<(int, int, int)> { (0, 0, 0) };
        sh.Block(0, 0, 0);
        int guard = 0;
        while (list.Count < 30 && guard++ < 10000)
        {
            var (x, y, z) = list[rng.Next(list.Count)];
            int d = rng.Next(6);
            var c = d switch { 0 => (x + 1, y, z), 1 => (x - 1, y, z), 2 => (x, y + 1, z), 3 => (x, y - 1, z), 4 => (x, y, z + 1), _ => (x, y, z - 1) };
            if (used.Contains(c)) continue;
            // no loops: the new block may touch only the one it grows from
            int touching = 0;
            foreach (var o in used) if (Math.Abs(o.Item1 - c.Item1) + Math.Abs(o.Item2 - c.Item2) + Math.Abs(o.Item3 - c.Item3) == 1) touching++;
            if (touching != 1) continue;
            used.Add(c); list.Add(c); sh.Block(c.Item1, c.Item2, c.Item3);
        }
        sh.Build();
        var s = sh.S;
        s.BuildNetwork();
        int m = 6 * s.Count;
        var r = new double[m]; var zz = new double[m]; var yy = new double[m];
        for (int i = 0; i < m; i++) r[i] = rng.NextDouble() - 0.5;
        for (int j = 0; j < 6; j++) r[6 * s.Root + j] = 0;
        s.TreeSolve(r, zz);
        s.MulK(zz, yy);
        double err = 0, nr = 0;
        for (int i = 0; i < m; i++) { err += (yy[i] - r[i]) * (yy[i] - r[i]); nr += r[i] * r[i]; }
        Check(Math.Sqrt(err / nr) < 1e-6, $"tree solve is K_tree's inverse: |K z - r| / |r| = {Math.Sqrt(err / nr):E2} ({s.Count} blocks)");
    }

    /// <summary>The classes from the game's block names; thrusters built for heat, in the right order.</summary>
    static void Classes()
    {
        Check(StructMaterial.Classify("ArmorCubeLight50") == StructMaterial.Armor, "classes: armour");
        Check(StructMaterial.Classify("DetailingCube25") == StructMaterial.Trim, "classes: detailing is trim");
        Check(StructMaterial.Classify("HydrogenThruster250") == StructMaterial.HydrogenThruster && StructMaterial.Classify("AtmosphericThruster500") == StructMaterial.AtmosphericThruster
            && StructMaterial.Classify("IonThruster750") == StructMaterial.IonThruster, "classes: thrusters by kind");
        Check(StructMaterial.Classify("Battery250") == StructMaterial.Functional && StructMaterial.Classify("Cockpit150") == StructMaterial.Functional, "classes: fittings");
        Check(StructMaterial.Classify("Gyroscope50") == StructMaterial.Mount && StructMaterial.Classify("Hinge250") == StructMaterial.Mount && StructMaterial.Classify("Retractable Gear 1 m (test)") == StructMaterial.Mount
            && StructMaterial.Classify("Control Surface 3x3x1") == StructMaterial.Mount && StructMaterial.Classify("Airbrake") == StructMaterial.Mount, "classes: mounts (move the ship or carry force)");
        var y = StructMaterial.Yield;
        Check(y[StructMaterial.Functional] < y[StructMaterial.Mount] && y[StructMaterial.Mount] == y[StructMaterial.Armor] && y[StructMaterial.AtmosphericThruster] == y[StructMaterial.Armor], "strength: fittings tear off first; mounts and thrusters as armour");
        var b = StructMaterial.BurnK;
        Check(b[StructMaterial.HydrogenThruster] > b[StructMaterial.AtmosphericThruster] && b[StructMaterial.AtmosphericThruster] > b[StructMaterial.IonThruster]
            && b[StructMaterial.IonThruster] > b[StructMaterial.Functional] && b[StructMaterial.AtmosphericThruster] >= b[StructMaterial.Armor], "heat: hydrogen nozzles > jets > ion > electronics; a jet's hot section takes armour's");
        Check(StructMaterial.Wall(StructMaterial.HydrogenThruster, 1005f, new Vector3(2.5f)) <= StructMaterial.FunctionalWall + 1e-6f, "thrusters: structurally functional (capped wall)");
    }

    static Vector3I V(int x, int y, int z) => new Vector3I(x, y, z);

    /// <summary>A TRUSS (a 2.5 m square tube of four one-cell walls, 344.5 kg: the game's Truss250) between a heavy root and a
    /// heavy tip, pushed end-on, against a 2.5 m box of the same mass (its six faces 1.2 mm thick - what the bounding box made
    /// of the truss): the truss's lattice walls do not buckle, the thin box's do. And the box split into two boxes (as the
    /// game's cell groups split shapes) is the same section as one.</summary>
    static void RealShapes()
    {
        float Push(int kind, out ShipStructure st)
        {
            var sh = new TShip();
            sh.Cells(1e9f, StructMaterial.Armor, (V(-10, 0, 0), V(-1, 9, 9)));
            int mid = kind switch
            {
                0 => sh.Cells(344.5f, StructMaterial.Armor, (V(0, 0, 0), V(9, 0, 9)), (V(0, 9, 0), V(9, 9, 9)), (V(0, 1, 0), V(9, 8, 0)), (V(0, 1, 9), V(9, 8, 9))),
                1 => sh.Cells(344.5f, StructMaterial.Armor, (V(0, 0, 0), V(9, 9, 9))),
                _ => sh.Cells(344.5f, StructMaterial.Armor, (V(0, 0, 0), V(9, 4, 9)), (V(0, 5, 0), V(9, 9, 9))),
            };
            int tip = sh.Cells(5e4f, StructMaterial.Armor, (V(10, 0, 0), V(19, 9, 9)));
            sh.Build();
            var s = st = sh.S;
            s.ExtForce[tip] = new Vector3(-50e3f, 0, 0);
            s.RigidAccel(Vector3.Zero); s.Solve(s.Accel, s.AngAccel, Vector3.Zero);
            return s.MaxUtil;
        }
        float truss = Push(0, out var ts), box = Push(1, out var bs), split = Push(2, out _);
        Check(ts.Thick[1] > 0.2f && bs.Thick[1] == 0f, $"real shapes: the truss is a lattice ({ts.Thick[1] * 1000:F0} mm deep walls), the box a shell ({bs.Wall[1] * 1000:F2} mm)");
        Check(box > 10f * truss, $"real shapes: a truss pushed end-on {truss:F4} vs a thin box of its mass {box:F4} - x{box / truss:F0} stronger");
        Check(MathF.Abs(split / box - 1f) < 1e-3f, $"real shapes: one box or two, the same section ({box:F5} vs {split:F5})");
    }

    /// <summary>A lattice SLAB (one cell deep, 2 m long) pushed along its length between a heavy root and tip: its plate
    /// buckles at its own depth over its own width - 16 m wide it buckles (k pi^2 E (t/b)^2 under the yield), 8 m wide it
    /// does not (no frames: FramePitch 0).</summary>
    static void SlabBuckling()
    {
        float frames = ShipStructure.FramePitch; ShipStructure.FramePitch = 0f;
        foreach (int w in new[] { 64, 32 })
        {
            var sh = new TShip();
            sh.Cells(1e9f, StructMaterial.Armor, (V(-8, 0, 0), V(-1, w - 1, 0)));
            int slab = sh.Cells(200f * w / 64f, StructMaterial.Armor, (V(0, 0, 0), V(7, w - 1, 0)));
            int tip = sh.Cells(1e6f, StructMaterial.Armor, (V(8, 0, 0), V(15, w - 1, 0)));
            sh.Build();
            var s = sh.S;
            float F = 20e3f * w / 64f;
            s.ExtForce[tip] = new Vector3(-F, 0, 0);
            s.RigidAccel(Vector3.Zero); s.Solve(s.Accel, s.AngAccel, Vector3.Zero);
            float fy = StructMaterial.Yield[StructMaterial.Armor], T = s.Thick[slab], b = w * ShipStructure.Cell;
            float sigma = F / (s.Phi[slab] * b * ShipStructure.Cell), comp = ShipStructure.Buckled(fy, T, b);
            if (w == 64) Check(comp < 0.9f * fy && MathF.Abs(s.MaxUtil / (sigma / comp) - 1f) < 0.02f, $"slab {b:F0} m wide, {T * 1000:F0} mm deep: buckles at {comp / 1e6:F0} MPa (yield {fy / 1e6:F0}) - utilisation {s.MaxUtil:F4} vs {sigma / comp:F4}");
            else Check(comp == fy && MathF.Abs(s.MaxUtil / (sigma / fy) - 1f) < 0.02f, $"slab {b:F0} m wide: holds to the yield - utilisation {s.MaxUtil:F4} vs {sigma / fy:F4}");
        }
        ShipStructure.FramePitch = frames;
    }

    /// <summary>With mount points: a block touching the root on a face whose mounts do not face each other is not joined
    /// (two pieces, no contact, a load on it stresses nothing); mounts mated over half the face join through that half; a
    /// block without mount data keeps its touching faces.</summary>
    /// <summary>Many pieces free at once (SolveFree's one pass for all pieces): 12 separate random booms under random loads,
    /// each balanced on its own - nothing left at any piece's root.</summary>
    static void ManyPieces()
    {
        var rng = new Random(11);
        var sh = new TShip();
        for (int q = 0; q < 12; q++) for (int x = 0; x < 3 + q % 4; x++) sh.Block(x, 0, 3 * q, 20f + 80f * (float)rng.NextDouble());
        sh.Build();
        var s = sh.S;
        float sum = 0f;
        for (int i = 0; i < s.Count; i++) { s.ExtForce[i] = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * 2000f; sum += s.ExtForce[i].Length(); }
        s.SolveFree(new Vector3(0.3f, -0.2f, 0.5f));
        float worst = 0f; for (int i = 0; i < s.Count; i++) if (s.Parent[i] < 0) worst = MathF.Max(worst, s.JForce[i].Length());
        Check(s.Components == 12 && worst < 1e-4f * sum, $"many pieces: {s.Components} pieces each free on its own loads, worst force left at a root {worst:F4} N of {sum:F0}");
    }

    static void Mounts()
    {
        bool was = ShipStructure.MountJoints; ShipStructure.MountJoints = true;
        try { MountsCore(); } finally { ShipStructure.MountJoints = was; }
    }

    static void MountsCore()
    {
        TShip Pair(int mode)
        {
            var sh = new TShip();
            int a = sh.Cells(1e6f, StructMaterial.Armor, (V(-2, 0, 0), V(-1, 1, 1)));
            int b = sh.Cells(SmallMass, StructMaterial.Armor, (V(0, 0, 0), V(1, 1, 1)));
            sh.Mount(a, V(1, 0, 0), V(-1, 0, 0), V(-1, 1, 1));               // a: its whole +x face
            if (mode == 0) sh.Mount(b, V(0, 1, 0), V(0, 1, 0), V(1, 1, 1));  // b: only its +y face - nothing faces a
            if (mode == 1) sh.Mount(b, V(-1, 0, 0), V(0, 0, 0), V(0, 0, 1)); // b: half its -x face
            return sh.Build();
        }
        var s = Pair(0).S;
        Check(s.HasMounts && s.ContactCount == 0 && s.Components == 2 && s.UnmatedContacts == 1, $"mounts: touching, nothing mated - no joint ({s.ContactCount} contacts, {s.Components} pieces)");
        s.ExtForce[1] = new Vector3(0, 5000f, 0); s.SolveFree(Vector3.Zero);
        Check(s.MaxUtil == 0f, $"mounts: a load on the unmated block stresses nothing ({s.MaxUtil})");
        s = Pair(1).S;
        Check(s.ContactCount == 1 && s.Components == 1 && MathF.Abs(s.JArea[s.Root == 0 ? 1 : 0] - 2 * 0.0625f) < 1e-6f, $"mounts: half the face mated - the joint is those 2 cells ({(s.ContactCount > 0 ? s.JArea[s.Root == 0 ? 1 : 0] : 0f):F4} m2)");
        s = Pair(2).S;
        Check(s.ContactCount == 1 && s.UnmountedContacts == 1, "mounts: a block with no mount data keeps its touching faces");
    }

    /// <summary>11. PERMANENT BENDS (StructureBend, 2026-10-14): a 10 m wing of 0.5 m blocks off a fixed root, lifted past its
    /// yield at the root: one bend, the whole wing its far side (not the root); the tip moves up (the load's way) by PerUtil x the
    /// overshoot x its arm, nothing at the joint's plane, growing outward; the same pull again bends nothing, a harder one by
    /// the new overshoot only; a far side over MaxBlocks is not bent; a point's total is capped at Max.</summary>
    static void Bends()
    {
        var sh = new TShip();
        int root = sh.Block(0, 0, 0, 1e9f);
        int tip = -1;
        for (int x = 1; x <= 20; x++) tip = sh.Block(x, 0, 0);
        sh.Build();
        var s = sh.S;
        // lift on every wing block, scaled to the target utilisation at the root (load is linear in it)
        void Pull(float target)
        {
            for (int i = 0; i < s.Count; i++) s.ExtForce[i] = i == root ? Vector3.Zero : new Vector3(0, 100f, 0);
            s.SolveFree(Vector3.Zero);
            float k = target / s.MaxUtil;
            for (int i = 0; i < s.Count; i++) s.ExtForce[i] *= k;
            s.SolveFree(Vector3.Zero);
        }
        var damage = new List<(int block, float fraction, bool destroy)>(); var where = new List<(Vector3 at, Vector3 dir, float util)>();
        var peaks = new Dictionary<long, float>(); var bends = new List<StructureBend.Bend>();
        int mj = StructureBend.MaxJoints; StructureBend.MaxJoints = 1;
        try
        {
            Pull(1.3f);
            StructureEval.Damage(s, null, 0.25f, true, false, 1.5f, 0.5f, 2, damage, where);
            int nb = StructureBend.Plan(s, damage, where, peaks, bends);
            Check(nb == 1 && bends[0].Blocks.Count == 20 && !bends[0].Blocks.Contains(root), $"bends: one bend, the whole wing its far side ({nb}, {(nb > 0 ? bends[0].Blocks.Count : 0)} blocks)");
            if (nb == 0) return;
            var b = bends[0];
            Vector3 tipP = s.Center[tip], midP = s.Center[10];
            var dTip = StructureBend.Displacement(b, tipP); var dMid = StructureBend.Displacement(b, midP);
            float arm = tipP.X - b.Joint.X, want = StructureBend.PerUtil * (b.Util - 1f) * arm;
            Check(dTip.Y > 0f && MathF.Abs(dTip.X) + MathF.Abs(dTip.Z) < 0.01f * dTip.Y, $"bends: the tip moves up, the load's way ({dTip})");
            Check(MathF.Abs(dTip.Length() / want - 1f) < 0.05f, $"bends: tip set {dTip.Length() * 100f:F1} cm vs PerUtil x {b.Util - 1f:F2} x {arm:F2} m = {want * 100f:F1} cm");
            var onPlane = new Vector3(b.Plane, 0.3f, 0.2f);
            Check(StructureBend.Displacement(b, onPlane) == Vector3.Zero && StructureBend.Displacement(b, new Vector3(b.Plane - 0.5f, 0, 0)) == Vector3.Zero, "bends: nothing on the joint's plane or behind it");
            Check(dMid.Y > 0f && dMid.Y < dTip.Y, $"bends: growing outward (mid {dMid.Y * 100f:F1} cm, tip {dTip.Y * 100f:F1} cm)");
            // the peak: the same pull again bends nothing; a harder one by the new overshoot
            StructureBend.Plan(s, damage, where, peaks, bends);
            Check(bends.Count == 0, "bends: the same pull again - no further set");
            float u0 = b.Util;
            Pull(1.4f);
            StructureEval.Damage(s, null, 0.25f, true, false, 1.5f, 0.5f, 2, damage, where);
            StructureBend.Plan(s, damage, where, peaks, bends);
            Check(bends.Count == 1 && MathF.Abs(bends[0].Rot.Length() / (StructureBend.PerUtil * (bends[0].Util - u0)) - 1f) < 1e-3f, $"bends: a harder pull sets the new overshoot only ({(bends.Count > 0 ? bends[0].Util - u0 : 0f):F3})");
            // too big a far side: not bent
            int mb = StructureBend.MaxBlocks; StructureBend.MaxBlocks = 5;
            peaks.Clear(); Pull(1.3f);
            StructureEval.Damage(s, null, 0.25f, true, false, 1.5f, 0.5f, 2, damage, where);
            StructureBend.Plan(s, damage, where, peaks, bends);
            StructureBend.MaxBlocks = mb;
            Check(bends.Count == 0, "bends: a far side over MaxBlocks is not bent");
            var capped = StructureBend.Capped(new Vector3(0, StructureBend.Max * 0.9f, 0), new Vector3(0, StructureBend.Max * 0.5f, 0));
            Check(MathF.Abs(capped.Length() - StructureBend.Max) < 1e-5f, $"bends: a point's total capped at {StructureBend.Max} m ({capped.Length():F3})");
        }
        finally { StructureBend.MaxJoints = mj; }
    }

    /// <summary>One synthetic run of the contact balance (the game's tick order): per tick the thrust into the velocity, the
    /// READ (the balance against the last read), the physics step (gravity, the contact model's impulse, the speed clamp; the
    /// body moves), then the other mod's write. Returns the worst |support - the step's real contact impulse| (N s), the
    /// mean support (N s per tick), and the worst jump-test miss (m) with and without the writes known.</summary>
    static (float err, Vector3 meanSup, float oldErr, double moveMiss, double oldMoveMiss) RunBalance(int ticks, float m, Vector3 v0, Vector3 thrustN, Vector3 g, float cap,
        Func<Vector3, Vector3> contact, Func<int, Vector3, Vector3> write)
    {
        const float dt = 1f / 60f;
        Vector3 v = v0; Vector3D x = Vector3D.Zero; double wSum = 0, wSumY = 0, wSumZ = 0;
        bool have = false; Vector3 pv = default; Vector3D pSum = default, px = default; Vector3 lastC = Vector3.Zero;
        float err = 0f, oldErr = 0f; double miss = 0, oldMiss = 0; Vector3 sum = Vector3.Zero; int n = 0;
        for (int k = 0; k < ticks; k++)
        {
            Vector3 thrJ = thrustN * dt;
            v += thrJ / m;
            // the read
            var sumNow = new Vector3D(wSum, wSumY, wSumZ);
            if (have)
            {
                var written = (Vector3)(sumNow - pSum);
                var sup = ContactBalance.Support(m, v, pv, written, thrJ, Vector3.Zero, g, Vector3.Zero, dt, cap);
                var old = m * (v - pv) - thrJ - g * m * dt;   // (before 2026-10-14: no writes known, no cap)
                err = MathF.Max(err, (sup - lastC).Length()); oldErr = MathF.Max(oldErr, (old - lastC).Length());
                sum += sup; n++;
                var moved = x - px;
                miss = Math.Max(miss, (moved - ContactBalance.ExpectedMove(v, written, thrJ, Vector3.Zero, m, dt)).Length());
                oldMiss = Math.Max(oldMiss, (moved - (Vector3D)(v * dt)).Length());
            }
            have = true; pv = v; pSum = sumNow; px = x;
            // the physics step: gravity, the contacts, the clamp, the move
            v += g * dt;
            lastC = contact(v);
            v += lastC / m;
            if (cap > 0f && v.Length() > cap) v *= cap / v.Length();   // (the engine's clamp: no contact)
            x += (Vector3D)(v * dt);
            // the other mod's write, after the step
            var dv = write(k, v);
            v += dv; wSum += dv.X; wSumY += dv.Y; wSumZ += dv.Z;
        }
        return (err, n > 0 ? sum / n : Vector3.Zero, oldErr, miss, oldMiss);
    }

    static void BalanceUnderEdits()
    {
        const float m = 2e5f, dt = 1f / 60f;
        var none = new Func<Vector3, Vector3>(_ => Vector3.Zero);
        // a FRAME ANCHOR on rails thrusting: its motion drained (zeroed) after every step - no contact, so no support
        var thr = new Vector3(3e5f, 0, 0);
        var a = RunBalance(120, m, Vector3.Zero, thr, Vector3.Zero, 0f, none, (k, v) => -v);
        Check(a.err < 1e-3f * thr.Length() * dt, $"balance: a drained anchor's own thrust reads no load ({a.err:E2} N s a step; before: {a.oldErr:F0} N s = its thrust {thr.Length() * dt:F0})");
        Check(a.oldErr > 0.5f * thr.Length() * dt, "balance: (the drain did read as a load before the writes were known)");
        // a WARP SWITCH: the velocity rescaled x10 after one step - no impulse; the jump test not fooled
        var w = RunBalance(30, m, new Vector3(50f, 0, 0), Vector3.Zero, Vector3.Zero, 0f, none, (k, v) => k == 10 ? v * 9f : Vector3.Zero);
        Check(w.err < 1f, $"balance: a warp switch (x10) reads no impulse ({w.err:F3} N s; before: {w.oldErr / 1e3f:F0} kN s)");
        Check(w.moveMiss < 1e-3 && w.oldMoveMiss > 0.5, $"balance: the jump test sees the written velocity (miss {w.moveMiss:E1} m; before {w.oldMoveMiss:F1} m - a false teleport)");
        // RESTING ON THE GROUND under a spinning planet's fictitious writes: the support is the weight
        var g = new Vector3(0, -9.81f, 0);
        var rnd = new Random(7);
        var ground = new Func<Vector3, Vector3>(v => new Vector3(0, -v.Y * m, 0));   // (the ground stops the fall: v.y to 0)
        var c = RunBalance(600, m, Vector3.Zero, Vector3.Zero, g, 0f, ground,
            (k, v) => new Vector3((float)(rnd.NextDouble() - 0.5) * 0.02f, (float)(rnd.NextDouble() - 0.5) * 0.02f, (float)(rnd.NextDouble() - 0.5) * 0.02f));
        float weight = m * 9.81f;
        Check(c.err < 1e-3f * weight * dt, $"balance: resting under fictitious writes, each step's support exact ({c.err:E2} N s)");
        Check(MathF.Abs(c.meanSup.Y / dt / weight - 1f) < 0.01f, $"balance: support = weight ({c.meanSup.Y / dt / 1e3f:F1} kN vs {weight / 1e3f:F1}; before: off by up to {c.oldErr / dt / 1e3f:F0} kN a step)");
        // AT THE SPEED CAP, thrusting along the travel: the clamp is no contact (and a side contact still reads)
        float cap = 1000f;
        var side = new Func<Vector3, Vector3>(_ => new Vector3(0, 0, 50f));
        var t = RunBalance(60, m, new Vector3(cap, 0, 0), new Vector3(1e6f, 0, 0), Vector3.Zero, cap, side, (k, v) => Vector3.Zero);
        Check(MathF.Abs(t.meanSup.X) < 1e-3f * 1e6f * dt && MathF.Abs(t.meanSup.Z / 50f - 1f) < 0.02f, $"balance: at the speed cap the clamp reads no load ({t.meanSup.X:F2} N s along; the side contact {t.meanSup.Z:F2} of 50 N s; before: {t.oldErr:F0} N s)");
    }

    static void EntryBraking()
    {
        // a 1 x 1 m (2 x 2 block) column, 3 m long, travelling along -Z (its nose the z = 0 layer), braked at 5 g
        var sh = new TShip();
        for (int z = 0; z < 6; z++) for (int x = 0; x < 2; x++) for (int y = 0; y < 2; y++) sh.Block(x, y, z);
        sh.Build(heat: true);
        var s = sh.S; var h = sh.H;
        var travel = new Vector3(0, 0, -1);
        h.Pass(travel);
        var accel = -travel * (5f * 9.81f);
        var L = new StructureEval.Loads { AeroCom = s.Com, EntryAccel = accel };
        StructureEval.Apply(s, h, L);
        Vector3 F = Vector3.Zero, M = Vector3.Zero, front = Vector3.Zero;
        for (int i = 0; i < s.Count; i++)
        {
            F += s.ExtForce[i]; M += Vector3.Cross(s.Center[i] - s.Com, s.ExtForce[i]) + s.ExtMoment[i];
            if (s.Center[i].Z < 0.5f) front += s.ExtForce[i];
        }
        var want = accel * s.TotalMass;
        Check((F - want).Length() < 1e-3f * want.Length(), $"entry braking: the force is the mass x 5 g ({F.Length() / 1e3f:F2} kN of {want.Length() / 1e3f:F2})");
        Check(M.Length() < 1e-3f * want.Length() * 3f, $"entry braking: no net moment ({M.Length():F2} N m)");
        Check(Vector3.Dot(front, want) > 0.99f * want.LengthSquared(), $"entry braking: the nose's faces carry it ({Vector3.Dot(front, want) / want.Length() / 1e3f:F2} kN at the nose)");
        s.RigidAccel(Vector3.Zero);
        Check((s.Accel - accel).Length() < 1e-3f * accel.Length() && s.AngAccel.Length() < 1e-3f, $"entry braking: the free body decelerates at it, unturned ({s.Accel.Length() / 9.81f:F3} g, {s.AngAccel.Length():E1} rad/s2)");
        s.SolveFree(Vector3.Zero);
        Check(s.ResidualForce.Length() < 1e-3f * want.Length(), $"entry braking: the solve balances it through the ship (left {s.ResidualForce.Length():F2} N)");
        float util = s.MaxUtil;
        Check(util > 0f && s.Center[s.MaxJoint].Z < s.Com.Z, $"entry braking: the structure is loaded, worst toward the nose (util {util:E2} at z {s.Center[s.MaxJoint].Z:F2}, centre {s.Com.Z:F2})");
        // off: nothing
        StructureEval.EntryLoads = false;
        try
        {
            StructureEval.Apply(s, h, L);
            float any = 0f; for (int i = 0; i < s.Count; i++) any += s.ExtForce[i].Length();
            Check(any == 0f, "entry braking: Structure.EntryLoads off - no load");
        }
        finally { StructureEval.EntryLoads = true; }
    }

    /// <summary>12. FATIGUE AND PROGRESSIVE FAILURE (StructureFatigue, 2026-10-14): a 10 m boom of 0.5 m blocks off a fixed root,
    /// loaded to a set utilisation at the root and held; the evaluation run as the game runs it (health into the strength, the
    /// solve, the damage).</summary>
    static void Fatigue()
    {
        var sh = new TShip();
        int root = sh.Block(0, 0, 0, 1e9f);
        for (int x = 1; x <= 20; x++) sh.Block(x, 0, 0);
        sh.Build();
        var s = sh.S;
        // the loads for a target utilisation of the intact boom (load is linear in it), kept fixed after
        Vector3[] Loads(float target)
        {
            for (int i = 0; i < s.Count; i++) { s.Integrity[i] = 1f; s.ExtForce[i] = i == root ? Vector3.Zero : new Vector3(0, 100f, 0); }
            s.SolveFree(Vector3.Zero);
            float k = target / s.MaxUtil;
            var f = new Vector3[s.Count]; for (int i = 0; i < s.Count; i++) f[i] = s.ExtForce[i] * k;
            return f;
        }
        var damage = new List<(int block, float fraction, bool destroy)>();
        // run: dt steps for a time; the worst health and when a block failed (-1: none)
        (float worst, float failAt) Run(Vector3[] f, float dt, float time)
        {
            var health = new float[s.Count]; Array.Fill(health, 1f);
            for (float t = 0f; t < time - 1e-4f; t += dt)
            {
                for (int i = 0; i < s.Count; i++) { s.Integrity[i] = health[i]; s.ExtForce[i] = f[i]; }
                s.SolveFree(Vector3.Zero);
                StructureEval.Damage(s, null, dt, true, false, StructureEval.BreakUtil, StructureEval.WearPerSecond, 2, damage);
                foreach (var (b, frac, destroy) in damage) { if (destroy) return (0f, t + dt); health[b] -= frac; }
            }
            float w = 1f; for (int i = 0; i < s.Count; i++) if (i != root) w = MathF.Min(w, health[i]);
            return (w, -1f);
        }
        // no fatigue under the endurance limit, a steep power law over it, monotonic in the load
        Check(StructureFatigue.HealthRate(StructMaterial.Armor, StructureFatigue.EnduranceUtil - 0.01f, 1.5f, 0.6f) == 0f, "fatigue: none under the endurance limit");
        float r6 = StructureFatigue.HealthRate(StructMaterial.Armor, 0.6f, 1.5f, 0.6f), r8 = StructureFatigue.HealthRate(StructMaterial.Armor, 0.8f, 1.5f, 0.6f);
        float r10 = StructureFatigue.HealthRate(StructMaterial.Armor, 1f, 1.5f, 0.6f), r12 = StructureFatigue.HealthRate(StructMaterial.Armor, 1.2f, 1.5f, 0.6f);
        Check(r6 > 0f && r8 > 8f * r6 && r10 > r8 && r12 > 10f * r10, $"fatigue: steep in the load (0.6 {r6:E1}/s, 0.8 {r8:E1}, 1.0 {r10:E1}, 1.2 {r12:E1})");
        Check(StructureFatigue.HealthRate(StructMaterial.Trim, 0.9f, 1.5f, 0.6f) == 0f, "fatigue: trim never tires");
        var cruise = Run(Loads(0.45f), 0.25f, 120f);
        Check(cruise.worst == 1f && cruise.failAt < 0f, $"fatigue: two minutes at 0.45 of the yield cost nothing ({cruise.worst})");
        // health only ever falls (the ledger's integrity under a load going up and down)
        {
            var led = new StructureFatigue.Ledger(); var game = new float[3] { 1f, 1f, 1f }; var integ = new float[3];
            led.Sync(game, integ, 3, 0.25f);
            bool mono = true; float last = 1f;
            for (int k = 0; k < 200; k++)
            {
                float u = 0.6f + 0.5f * MathF.Abs(MathF.Sin(k * 0.1f));
                led.Accrue(1, StructureFatigue.HealthRate(StructMaterial.Armor, u, 1.5f, 0.6f) * 0.25f);
                led.Sync(game, integ, 3, 0.25f);
                if (integ[1] > last + 1e-6f) mono = false;
                last = integ[1];
            }
            Check(mono && last < 1f, $"fatigue: health only falls ({last:F3})");
        }
        // rate-independent: 2 Hz and 8 Hz evaluations of the same held load end alike
        var f12 = Loads(1.15f);
        var slow = Run(f12, 0.5f, 1f); var fast = Run(f12, 0.125f, 1f);
        Check(slow.failAt < 0f && fast.failAt < 0f && MathF.Abs(slow.worst - fast.worst) < 0.04f, $"rate-independent past the yield: 1 s at 1.15 - health {slow.worst:F3} at 2 Hz, {fast.worst:F3} at 8 Hz");
        var f9 = Loads(0.9f);
        var slowF = Run(f9, 0.5f, 40f); var fastF = Run(f9, 0.125f, 40f);
        Check(slowF.worst < 0.95f && MathF.Abs((1f - slowF.worst) / (1f - fastF.worst) - 1f) < 0.05f, $"rate-independent fatigue: 40 s at 0.9 - health {slowF.worst:F4} at 2 Hz, {fastF.worst:F4} at 8 Hz");
        // (held on, the tired root reaches its yield and runs away: the same time either way)
        var slowE = Run(f9, 0.5f, 120f); var fastE = Run(f9, 0.125f, 120f);
        Check(slowE.failAt > 40f && MathF.Abs(slowE.failAt / fastE.failAt - 1f) < 0.03f, $"rate-independent failure: held at 0.9 the root tires through at {slowE.failAt:F2} s at 2 Hz, {fastE.failAt:F2} s at 8 Hz");
        var held = Run(f12, 0.25f, 30f);
        Check(held.failAt > 0.5f, $"progressive: held at 1.15 the root wears through over time - fails at {held.failAt:F2} s, not at once");
        // the snap: past SnapUtil of the intact strength at once; just under it, not in one step
        var over = Run(Loads(StructureFatigue.SnapUtil * 1.05f), 0.05f, 0.05f);
        Check(over.failAt > 0f && over.failAt <= 0.05f + 1e-4f, $"snap: past SnapUtil the joint goes at once ({over.failAt:F2} s)");
        var under = Run(Loads(StructureFatigue.SnapUtil * 0.9f), 0.05f, 0.05f);
        Check(under.failAt < 0f && under.worst < 1f, $"snap: just under SnapUtil it wears, not snaps, in a step (health {under.worst:F3})");
        // the batched writes
        {
            var led = new StructureFatigue.Ledger(); var game = new float[2] { 1f, 1f }; var integ = new float[2];
            var due = new List<(int block, float loss)>();
            led.Sync(game, integ, 2, 0f);
            int writes = 0; float written = 0f; bool steps = true;
            for (int k = 0; k < 40; k++)   // 10 s of 0.004 per 0.25 s: 0.16 in all
            {
                led.Accrue(0, 0.004f); led.Sync(game, integ, 2, 0.25f);
                led.TakeDue(due);
                foreach (var (b, l) in due) { writes++; written += l; if (l < StructureFatigue.WriteStep - 1e-5f) steps = false; }
            }
            Check(steps && writes >= 2 && writes <= 4, $"batched: 40 evaluations of wear, {writes} health writes, each a step");
            for (int k = 0; k < 16; k++) { led.Sync(game, integ, 2, 0.25f); led.TakeDue(due); foreach (var (b, l) in due) { writes++; written += l; } }
            Check(MathF.Abs(written + led.Pending[0] - 0.16f) < 1e-4f && led.Pending[0] < StructureFatigue.SettleMin, $"batched: settled - {written:F3} of 0.160 written, under SettleMin left ({led.Pending[0]:F3})");
            // a damage stage crossed: written at once
            led.Accrue(1, 0.26f); led.TakeDue(due);
            Check(due.Count == 1 && due[0].block == 1, "batched: a damage stage crossed (0.75) writes at once");
            // a failing block: all of it, now
            float before = led.Pending[0];
            led.Accrue(0, 0.01f); float all = led.TakeAll(0);
            Check(MathF.Abs(all - before - 0.01f) < 1e-6f && led.Pending[0] == 0f, "batched: a failure takes the pending at once");
            // a fresh reading: repaired - taken; damaged by something else - taken less the pending
            led.Accrue(0, 0.02f);
            led.Sync(new float[] { 1f, 0.74f }, integ, 2, 0.25f);
            Check(integ[0] == 1f && led.Pending[0] == 0f, $"ledger: a repair (welded) re-anchors on the game ({integ[0]:F3})");
            led.Accrue(0, 0.02f);
            led.Sync(new float[] { 0.5f, 0.74f }, integ, 2, 0.25f);
            Check(MathF.Abs(integ[0] - 0.48f) < 1e-4f, $"ledger: damage by something else taken, less the pending ({integ[0]:F3})");
            // a rebuild: the pending carried to the block's new index
            led.Resize(3, new[] { 2, 0 });
            Check(MathF.Abs(led.Pending[2] - 0.02f) < 1e-6f && led.Dirty.Contains(2), "ledger: a rebuild carries the pending by block");
        }
    }

    /// <summary>The blocks of sh still alive, as a structure (orig: its block -> sh's).</summary>
    static ShipStructure BuildAlive(TShip sh, bool[] alive, out int[] orig)
    {
        var map = new Dictionary<int, int>(); var o = new List<int>();
        var bb = new List<int>(); var lo = new List<Vector3I>(); var hi = new List<Vector3I>(); var m = new List<float>(); var mat = new List<int>();
        for (int k = 0; k < sh.BoxBlock.Count; k++)
        {
            int b = sh.BoxBlock[k]; if (!alive[b]) continue;
            if (!map.TryGetValue(b, out int c)) { map[b] = c = o.Count; o.Add(b); m.Add(sh.Mass[b]); mat.Add(sh.Mat[b]); }
            bb.Add(c); lo.Add(sh.Lo[k]); hi.Add(sh.Hi[k]);
        }
        orig = o.ToArray();
        return ShipStructure.Build(bb, lo, hi, m, mat);
    }

    /// <summary>14. THE NECK FAILS WHOLE (StructureEval.DamageBySection, 2026-10-15, the user: "one pattern im not sure i like is
    /// that a fracture creeps along the span instead of uniformly crumpling the weak neck"): a cantilever wing - 10 x 9 blocks
    /// of 0.5 m, lifted - on a narrow neck (3 blocks wide, 3 long) off a fixed root, its lift growing from 0.6 of the neck's
    /// yield by 0.08 a second, evaluated at 4 Hz as the game does (health into strength, the solve, the damage; what fails
    /// removed and the structure rebuilt, as AeroBench 'fail'). The neck's root cut: every member tires together (one
    /// health), then fails in ONE evaluation, all of it, with the trim tied across it - and nothing after it, the load still
    /// growing: no failure creeps outboard. By block (A/B, BySection off) the same neck zipped over evaluations. A snap with
    /// one cut allowed: one cut, whole; the next cut out - also over SnapUtil, but leaving with it - neither failed nor worn.</summary>
    static void NeckFailure()
    {
        var sh = new TShip();
        int root = sh.Block(0, 0, -3, 1e9f, 1, 1, 9);
        var neck = new int[3, 3];   // [x - 1, z]
        for (int x = 1; x <= 3; x++) for (int z = 0; z <= 2; z++) neck[x - 1, z] = sh.Block(x, 0, z);
        var wing = new List<int>();
        for (int x = 4; x <= 13; x++) for (int z = -3; z <= 5; z++) wing.Add(sh.Block(x, 0, z));
        // (detailing on the neck's root: a trim cube on its top, across the root's plane - a tie, no member)
        int trim = sh.Cells(5f, StructMaterial.Trim, (new Vector3I(1, 2, 2), new Vector3I(2, 2, 3)));
        int nb = sh.Mass.Count;
        var isWing = new bool[nb]; foreach (int b in wing) isWing[b] = true;
        var lift = new Vector3(0, 100f, 0);
        // the scale: the intact neck's worst at 1 for this lift x k1
        var alive0 = new bool[nb]; Array.Fill(alive0, true);
        var s0 = BuildAlive(sh, alive0, out var o0);
        for (int i = 0; i < s0.Count; i++) s0.ExtForce[i] = isWing[o0[i]] ? lift : Vector3.Zero;
        s0.SolveFree(Vector3.Zero);
        float k1 = 1f / s0.MaxUtil;
        Check(s0.SectionCount > 0 && s0.MaxContact >= 0, $"neck: the slices' sections ({s0.SectionCount})");
        {
            s0.ContactBlocks(s0.MaxContact, out int a, out int b);
            int ob = o0[s0.Outboard(s0.MaxContact)];
            Check((o0[a] == root || o0[b] == root) && (ob == neck[0, 0] || ob == neck[0, 1] || ob == neck[0, 2]), "neck: the worst cut is the neck's root");
        }
        const float dt = 0.25f;
        // one run: the evaluations that broke something (time, blocks failed, cuts), the root column's widest health spread
        // while it wore, and any damage after the first break
        (List<(float t, List<int> blocks, int cuts)> breaks, float spread, float spread2, float wornRoot, int after, float[] health) Run(bool bySection, float time, float u0, float rate, int maxBreaks)
        {
            bool prev = StructureEval.BySection; StructureEval.BySection = bySection;
            try
            {
                var alive = new bool[nb]; Array.Fill(alive, true); var health = new float[nb]; Array.Fill(health, 1f);
                var st = BuildAlive(sh, alive, out var orig);
                var dmg = new List<(int block, float fraction, bool destroy)>(); var cuts = new List<StructureEval.CutFail>();
                var breaks = new List<(float, List<int>, int)>();
                float spread = 0f, spread2 = 0f, wornRoot = 0f; int after = 0;
                for (float t = 0f; t < time - 1e-4f; t += dt)
                {
                    float k = (u0 + rate * t) * k1;
                    for (int i = 0; i < st.Count; i++) { st.Integrity[i] = health[orig[i]]; st.ExtForce[i] = isWing[orig[i]] ? lift * k : Vector3.Zero; st.ExtMoment[i] = Vector3.Zero; }
                    st.SolveFree(Vector3.Zero);
                    StructureEval.Damage(st, null, dt, true, false, StructureEval.BreakUtil, StructureEval.WearPerSecond, maxBreaks, dmg, null, cuts);
                    var failed = new List<int>();
                    foreach (var (b, frac, destroy) in dmg)
                    {
                        int o = orig[b];
                        if (breaks.Count > 0) after++;
                        if (destroy) { alive[o] = false; failed.Add(o); } else health[o] -= frac;
                    }
                    if (failed.Count > 0) breaks.Add((t, failed, cuts.Count));
                    if (breaks.Count == 0)
                    {
                        // (the root column's and the next column's members: one health each)
                        float lo = 1f, hi = 0f, lo2 = 1f, hi2 = 0f;
                        for (int z = 0; z < 3; z++) { lo = MathF.Min(lo, health[neck[0, z]]); hi = MathF.Max(hi, health[neck[0, z]]); lo2 = MathF.Min(lo2, health[neck[1, z]]); hi2 = MathF.Max(hi2, health[neck[1, z]]); }
                        spread = MathF.Max(spread, hi - lo); spread2 = MathF.Max(spread2, hi2 - lo2); wornRoot = 1f - lo;
                    }
                    if (failed.Count > 0) st = BuildAlive(sh, alive, out orig);
                }
                return (breaks, spread, spread2, wornRoot, after, health);
            }
            finally { StructureEval.BySection = prev; }
        }
        // the ramp: 0.6 -> 2.2 of the yield over 20 s
        var r = Run(true, 20f, 0.6f, 0.08f, 2);
        Check(r.breaks.Count >= 1, $"neck: it fails under the growing lift ({r.breaks.Count} breaking evaluations)");
        if (r.breaks.Count == 0) return;
        var (t1, blocks1, cuts1) = r.breaks[0];
        Check(r.wornRoot > 0.5f && r.spread < 1e-3f, $"neck: the root cut's three members tired together - {r.wornRoot:F2} of their health lost, spread {r.spread:E1}");
        Check(r.spread2 < 1e-3f && r.health[neck[1, 0]] < 1f, $"neck: the next cut out tired as one too (health {r.health[neck[1, 0]]:F3}, spread {r.spread2:E1})");
        bool whole = blocks1.Count == 4 && cuts1 == 1 && blocks1.Contains(trim);
        for (int z = 0; z < 3; z++) whole &= blocks1.Contains(neck[0, z]);
        Check(whole, $"neck: at {t1:F2} s ({0.6f + 0.08f * t1:F2} of the yield) the root cut failed whole in one evaluation - {blocks1.Count} blocks in {cuts1} cut ({string.Join(" ", blocks1)}; the 3 neck roots and the trim tie)");
        Check(r.breaks.Count == 1 && r.after == 0, $"neck: nothing more failed or wore after it, the lift still growing to {0.6f + 0.08f * 20f:F1} - no creep outboard ({r.breaks.Count} breaking evaluations, {r.after} entries after)");
        // (A/B) by block the same neck went over evaluations, a block or two at a time
        var legacy = Run(false, 20f, 0.6f, 0.08f, 2);
        int legacyBlocks = 0; foreach (var b in legacy.breaks) legacyBlocks += b.blocks.Count;
        Check(legacy.breaks.Count >= 2, $"neck (A/B, by block): {legacyBlocks} blocks over {legacy.breaks.Count} evaluations - the zipper this replaces");
        // the budget: a snap past SnapUtil at two cuts, one cut allowed - the root whole; the next, leaving with it, untouched
        var snap = Run(true, dt, StructureFatigue.SnapUtil * 1.25f, 0f, 1);
        bool rootWhole = snap.breaks.Count == 1 && snap.breaks[0].cuts == 1;
        if (rootWhole) for (int z = 0; z < 3; z++) rootWhole &= snap.breaks[0].blocks.Contains(neck[0, z]) && !snap.breaks[0].blocks.Contains(neck[1, z]) && snap.health[neck[1, z]] == 1f;
        Check(rootWhole, $"neck: a snap at {StructureFatigue.SnapUtil * 1.25f:F2} with one cut allowed - the root cut whole ({(snap.breaks.Count > 0 ? snap.breaks[0].blocks.Count : 0)} blocks), the next cut out neither failed nor worn");
        // two cuts allowed: still one - the second is inside what the first takes off
        var snap2 = Run(true, dt, StructureFatigue.SnapUtil * 1.25f, 0f, 2);
        Check(snap2.breaks.Count == 1 && snap2.breaks[0].cuts == 1, $"neck: two cuts allowed, still one cut - the next is inside what leaves ({(snap2.breaks.Count > 0 ? snap2.breaks[0].cuts : 0)} cuts)");
    }

    static int Main()
    {
        Classes();
        ShipStructure.Strength = 1f;   // (the formulas below at the material's own yield)
        TreeExact();
        Cantilever(); Balance(); Composite(); Whip(); GyroWholeShip();
        RealShapes(); SlabBuckling(); Mounts(); ManyPieces(); Bends(); Fatigue(); NeckFailure();
        BalanceUnderEdits(); EntryBraking();
        HeatBalance(); BluntVsSharp(); Cooling();
        Console.WriteLine($"StructureTests: {_checks - _fails} passed, {_fails} failed");
        return _fails == 0 ? 0 : 1;
    }
}
