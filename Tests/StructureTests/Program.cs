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
// 15. tension vs compression (2026-10-15): a deep beam lifted - its root cut's lower members pulled, upper pushed (sign
//     classification); fatigue by the pull at the full rate, by the push at CompressionFatigueShare (pulled members
//     unchanged, pushed ones slower); worn through, the pulled side tears first and the pushed side - the hinge - lets go
//     last (FractureMath.Plan: monotone, 0 .. CrackSeconds); a snap past CrushUtil crushes the pushed side; a bend folds
//     about the pushed side (the pulled side opens, nothing pushed into the ship).
// 15b. buckling fatigue (2026-10-15): a buckling-limited pushed member tires at the tension rate (BucklingFatigueShare 1),
//     a stocky - yield-limited - one at CompressionFatigueShare.
// 16. the jagged line (FractureMath.JagCut): a plate's cut over 300 seeds - deterministic, each member at the plane or one
//     block either side, steps both ways, and every path across the cut broken (the root never joined to the tip); off -
//     the flat cut's blocks; a failed cut through DamageBySection parts the plate for every seed tried.
// 17. a failed cut parts by itself (CutWatch, 2026-10-15): the crack and its watch played tick by tick on irregular plates -
//     pieces that part mid-crack are credited (no bridge on the jagged rule), the watch never forces a cut, the stubs stay.
// 19. a failed cut always separates (2026-10-17): a stale member's force skipped and a joint off the cut's section - the
//     watch asks the game (seeds both sides still in one grid), forces again and walks the far side for what holds it.
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
        bool was = ShipStructure.MountJoints; float wasShare = ShipStructure.TouchingShare; ShipStructure.MountJoints = true;
        // (the strict mounts' cases: touching-only faces carry nothing)
        ShipStructure.TouchingShare = 0f;
        try { MountsCore(); MountedVsTouching(); TouchingShareJoints(); } finally { ShipStructure.MountJoints = was; ShipStructure.TouchingShare = wasShare; }
    }

    /// <summary>TouchingShare (2026-10-15): with mount data, mated cells carry whole and touching-only cells at the share - a
    /// fully mated face is the touching face at any share; a face with nothing mated joins at the share (none at 0, a
    /// utilisation about 1 / share, the touching face exactly at 1); half mated lies between strict and whole, weaker as the
    /// share falls, its joint area the mated cells plus the share of the rest; MountJoints off ignores the share.</summary>
    static void TouchingShareJoints()
    {
        ShipStructure Pair(int kind, float share)
        {
            ShipStructure.TouchingShare = share;
            var sh = new TShip();
            int a = sh.Cells(1e6f, StructMaterial.Armor, (V(-2, 0, 0), V(-1, 1, 1)));
            int b = sh.Cells(SmallMass, StructMaterial.Armor, (V(0, 0, 0), V(1, 1, 1)));
            if (kind >= 0) sh.Mount(a, V(1, 0, 0), V(-1, 0, 0), V(-1, 1, 1));    // a: its whole +x face
            if (kind == 1) sh.Mount(b, V(-1, 0, 0), V(0, 0, 0), V(0, 1, 1));   // b: its whole -x face (mated whole)
            if (kind == 2) sh.Mount(b, V(-1, 0, 0), V(0, 0, 0), V(0, 0, 1));   // b: half of it
            if (kind == 4) sh.Mount(b, V(1, 0, 0), V(0, 0, 0), V(0, 1, 1));    // b: looking +x - nothing mated
            return sh.Build().S;
        }
        float Util(ShipStructure st) { Array.Clear(st.ExtForce); st.ExtForce[1] = new Vector3(0, 5000f, 0); st.SolveFree(Vector3.Zero); return st.MaxUtil; }
        float A(ShipStructure st) => st.ContactCount > 0 ? st.JArea[st.Root == 0 ? 1 : 0] : 0f;
        float uT = Util(Pair(-1, 0.5f));   // (no mount data: the touching face)
        foreach (float w in new[] { 0f, 0.25f, 0.5f, 1f })
        {
            var f = Pair(1, w); float uF = Util(f);
            Check(f.Components == 1 && MathF.Abs(uF / uT - 1f) < 1e-4f, $"touching share {w}: a fully mated face is the touching face (utilisation {uF:F4} vs {uT:F4})");
        }
        var n0 = Pair(4, 0f); var nH = Pair(4, 0.5f); var n1 = Pair(4, 1f);
        float uN = Util(nH), uN1 = Util(n1);
        Check(n0.ContactCount == 0 && n0.Components == 2, $"touching share 0: nothing mated - no joint ({n0.ContactCount} contacts)");
        Check(nH.Components == 1 && nH.UnmatedContacts == 1 && MathF.Abs(A(nH) - 0.5f * 4 * 0.0625f) < 1e-6f && uN > 1.6f * uT && uN < 2.4f * uT,
            $"touching share 0.5: nothing mated - joined at half (area {A(nH):F4} m2, utilisation {uN:F4} vs {uT:F4} whole)");
        Check(!n1.HasMounts && MathF.Abs(uN1 / uT - 1f) < 1e-6f, $"touching share 1: the touching face exactly, mount data or not ({uN1:F4} vs {uT:F4})");
        float uH0 = Util(Pair(2, 0f)), uH25 = Util(Pair(2, 0.25f)), uH5 = Util(Pair(2, 0.5f)), uH75 = Util(Pair(2, 0.75f));
        var h5 = Pair(2, 0.5f);
        Check(uH0 > uH25 && uH25 > uH5 && uH5 > uH75 && uH75 > uT * 1.0001f, $"touching share: half mated - weaker as the share falls ({uH0:F4} at 0, {uH25:F4}, {uH5:F4}, {uH75:F4} at 0.75; {uT:F4} whole)");
        Check(MathF.Abs(A(h5) - (2 + 0.5f * 2) * 0.0625f) < 1e-6f && h5.MatedContacts == 1, $"touching share 0.5: half mated - the joint area the mated cells and half the rest ({A(h5):F4} m2)");
        ShipStructure.MountJoints = false;
        try
        {
            float uOff = Util(Pair(4, 0.25f));
            Check(MathF.Abs(uOff / uT - 1f) < 1e-6f, $"touching share: MountJoints off ignores it - the touching face ({uOff:F4} vs {uT:F4})");
        }
        finally { ShipStructure.MountJoints = true; ShipStructure.TouchingShare = 0f; }
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

    /// <summary>Mounted vs touching-only joints (2026-10-15, the default's A/B): a fully mated face is the touching face - the
    /// same joint, the same utilisation as without mount data or with MountJoints off; half mated is weaker; mounts that do
    /// not meet (the wrong layer, beside each other, the same way) join nothing; with MountJoints off the mount data is
    /// ignored (every touching face, as before); the SSTO's wing root - armour mated to armour, and against a tank's side
    /// that has no mounts there - holds through the mated pair only.</summary>
    static void MountedVsTouching()
    {
        // a heavy root (x -2..-1) and a small cube (x 0..1) touching across x -1|0; b's -x mount by kind
        ShipStructure Pair(int kind)
        {
            var sh = new TShip();
            int a = sh.Cells(1e6f, StructMaterial.Armor, (V(-2, 0, 0), V(-1, 1, 1)));
            int b = sh.Cells(SmallMass, StructMaterial.Armor, (V(0, 0, 0), V(1, 1, 1)));
            if (kind >= 0) sh.Mount(a, V(1, 0, 0), V(-1, 0, 0), V(-1, 1, 1));    // a: its whole +x face
            if (kind == 1) sh.Mount(b, V(-1, 0, 0), V(0, 0, 0), V(0, 1, 1));   // b: its whole -x face
            if (kind == 2) sh.Mount(b, V(-1, 0, 0), V(0, 0, 0), V(0, 0, 1));   // b: half of it
            if (kind == 3) sh.Mount(b, V(-1, 0, 0), V(1, 0, 0), V(1, 1, 1));   // b: looking -x, but a layer in (not at the face)
            if (kind == 4) sh.Mount(b, V(1, 0, 0), V(0, 0, 0), V(0, 1, 1));    // b: at the face, looking +x (the same way as a's)
            if (kind == 5) { sh.Mount(a, V(1, 0, 0), V(-1, 0, 0), V(-1, 0, 1)); sh.Mounts.RemoveAt(0); sh.Mount(b, V(-1, 0, 0), V(0, 1, 0), V(0, 1, 1)); }   // beside each other
            return sh.Build().S;
        }
        float Util(ShipStructure st) { Array.Clear(st.ExtForce); st.ExtForce[1] = new Vector3(0, 5000f, 0); st.SolveFree(Vector3.Zero); return st.MaxUtil; }
        float A(ShipStructure st) => st.ContactCount > 0 ? st.JArea[st.Root == 0 ? 1 : 0] : 0f;
        var touch = Pair(-1); float uT = Util(touch);   // no mount data at all: every touching face
        var full = Pair(1); float uF = Util(full);
        Check(full.HasMounts && full.MatedContacts == 1 && full.Components == 1 && MathF.Abs(A(full) - 4 * 0.0625f) < 1e-6f && MathF.Abs(uF / uT - 1f) < 1e-4f,
            $"mounted vs touching: a fully mated face is the touching face ({A(full):F4} m2; utilisation {uF:F4} vs {uT:F4})");
        var half = Pair(2); float uH = Util(half);
        Check(half.Components == 1 && MathF.Abs(A(half) - 2 * 0.0625f) < 1e-6f && uH > 1.3f * uF, $"mounted vs touching: half mated is weaker (utilisation {uH:F4} vs {uF:F4} whole)");
        foreach (var (kind, what) in new[] { (3, "a layer in"), (4, "looking the same way"), (5, "beside each other") })
        {
            var st = Pair(kind);
            Check(st.ContactCount == 0 && st.Components == 2 && st.UnmatedContacts == 1, $"mounted vs touching: mounts {what} join nothing ({st.ContactCount} contacts, {st.Components} pieces)");
        }
        // MountJoints off: the mount data ignored - the touching face whole, even where nothing is mated
        ShipStructure.MountJoints = false;
        try
        {
            var off = Pair(4); float uO = Util(off);
            Check(!off.HasMounts && off.ContactCount == 1 && off.Components == 1 && MathF.Abs(A(off) - 4 * 0.0625f) < 1e-6f && MathF.Abs(uO / uT - 1f) < 1e-4f,
                $"mounted vs touching: MountJoints off - every touching face, as without mount data (utilisation {uO:F4} vs {uT:F4})");
        }
        finally { ShipStructure.MountJoints = true; }

        // the SSTO's wing root (ssto_m.struct, x 164|165): a heavy fuselage of an armour block (mounts all round) and a tank
        // (its mounts on its ends only); a 4-block armour wing along +x whose root block touches both - the root's face against
        // the armour mated, against the tank's side not
        ShipStructure Root(bool mounts, out int wingRoot)
        {
            var sh = new TShip();
            int arm = sh.Block(0, 0, 0, 1e6f);
            int tank = sh.Block(0, 1, 0, 1e6f);
            sh.Block(-1, 0, 0, 1e6f, 1, 2, 1);   // (the fuselage behind them: one piece with both)
            wingRoot = sh.Block(1, 0, 0, SmallMass, 1, 2, 1);   // two blocks tall: against the armour (y 0..1) and the tank (y 2..3)
            for (int x = 2; x <= 4; x++) sh.Block(x, 0, 0, SmallMass, 1, 2, 1);
            if (mounts)
            {
                for (int i = 0; i < sh.Mass.Count; i++)
                {
                    var lo = sh.Lo[i]; var hi = sh.Hi[i];
                    foreach (var d in new[] { V(1, 0, 0), V(-1, 0, 0), V(0, 1, 0), V(0, -1, 0), V(0, 0, 1), V(0, 0, -1) })
                    {
                        if (i == tank && d.X != 0) continue;   // (a tank: no mounts on its sides)
                        var fl = lo; var fh = hi;
                        if (d.X > 0) fl.X = hi.X; else if (d.X < 0) fh.X = lo.X; else if (d.Y > 0) fl.Y = hi.Y; else if (d.Y < 0) fh.Y = lo.Y; else if (d.Z > 0) fl.Z = hi.Z; else fh.Z = lo.Z;
                        sh.Mount(i, d, fl, fh);
                    }
                }
            }
            return sh.Build().S;
        }
        float RootUtil(ShipStructure st, int wr) { Array.Clear(st.ExtForce); for (int i = wr; i < st.Count; i++) st.ExtForce[i] = new Vector3(0, 0, 300f); st.SolveFree(Vector3.Zero); return st.JUtil[wr]; }
        var rOff = Root(false, out int w0); var rOn = Root(true, out int w1);
        float uOff = RootUtil(rOff, w0), uOn = RootUtil(rOn, w1);
        Check(rOn.Components == 1 && rOn.UnmatedContacts >= 1 && MathF.Abs(rOn.JArea[w1] - 4 * 0.0625f) < 1e-6f && MathF.Abs(rOff.JArea[w0] - 4 * 0.0625f) < 1e-6f,
            $"mounted vs touching: the wing root holds by the armour alone (joined, {rOn.UnmatedContacts} touching contact(s) dropped)");
        Check(uOn > 1.2f * uOff, $"mounted vs touching: without the tank's side the root is weaker - utilisation {uOn:F3} vs {uOff:F3} touching");
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
            // (2026-10-15: it folds about the root's pushed edge - StructureBend.HingeToCompression - so the tip also swings
            //  out a little, away from the ship: the pulled side opens)
            Check(dTip.Y > 0f && dTip.X >= 0f && dTip.X + MathF.Abs(dTip.Z) < 0.03f * dTip.Y, $"bends: the tip moves up, the load's way ({dTip})");
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
        // (A26, 2026-10-15) windward faces on no block (a coarsened heat grid's) take no share: the whole load still reaches
        // the blocks; none on a block at all - the root carries it, no NaN
        {
            var keep = (int[])h.FaceBlock.Clone();
            int off = 0;
            for (int i = 0; i < h.FaceCount; i++) if (h.FaceWind[i] > 0f && (off++ & 1) == 0) h.FaceBlock[i] = -1;
            h.Wind = null; StructureEval.Apply(s, h, L);
            Vector3 F2 = Vector3.Zero; for (int i = 0; i < s.Count; i++) F2 += s.ExtForce[i];
            Check(off > 1 && (F2 - want).Length() < 1e-3f * want.Length(), $"entry braking: half the windward faces on no block - the whole force still on the blocks ({F2.Length() / 1e3f:F2} kN of {want.Length() / 1e3f:F2})");
            for (int i = 0; i < h.FaceCount; i++) h.FaceBlock[i] = -1;
            h.Wind = null; StructureEval.Apply(s, h, L);
            Vector3 F3 = Vector3.Zero; bool finite = true;
            for (int i = 0; i < s.Count; i++) { F3 += s.ExtForce[i]; finite &= float.IsFinite(s.ExtForce[i].X + s.ExtMoment[i].X + s.ExtForce[i].Y + s.ExtMoment[i].Y + s.ExtForce[i].Z + s.ExtMoment[i].Z); }
            Check(finite && (F3 - want).Length() < 1e-3f * want.Length(), $"entry braking: no face on a block - finite, on the root ({F3.Length() / 1e3f:F2} kN)");
            Array.Copy(keep, h.FaceBlock, keep.Length); h.Wind = null; StructureEval.Apply(s, h, L);
        }
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
        // rate-independent: 2 Hz and 8 Hz evaluations of the same held load end alike (the integrator's checks at the full
        // rate - this boom's root is pushed more than pulled, its buckled wall governing, so tension-driven fatigue
        // (StructureFatigue.CompressionFatigueShare, 2026-10-15) would slow it five-fold were it stocky - buckling, it tires at
        // BucklingFatigueShare, the full rate: tested in TensionCompression, BucklingFatigue)
        var f12 = Loads(1.15f);
        float share0 = StructureFatigue.CompressionFatigueShare; StructureFatigue.CompressionFatigueShare = 1f;
        try {
        var slow = Run(f12, 0.5f, 1f); var fast = Run(f12, 0.125f, 1f);
        Check(slow.failAt < 0f && fast.failAt < 0f && MathF.Abs(slow.worst - fast.worst) < 0.04f, $"rate-independent past the yield: 1 s at 1.15 - health {slow.worst:F3} at 2 Hz, {fast.worst:F3} at 8 Hz");
        var f9 = Loads(0.9f);
        var slowF = Run(f9, 0.5f, 40f); var fastF = Run(f9, 0.125f, 40f);
        Check(slowF.worst < 0.95f && MathF.Abs((1f - slowF.worst) / (1f - fastF.worst) - 1f) < 0.05f, $"rate-independent fatigue: 40 s at 0.9 - health {slowF.worst:F4} at 2 Hz, {fastF.worst:F4} at 8 Hz");
        // (held on, the tired root reaches its yield and runs away: the same time either way)
        var slowE = Run(f9, 0.5f, 120f); var fastE = Run(f9, 0.125f, 120f);
        Check(slowE.failAt > 40f && MathF.Abs(slowE.failAt / fastE.failAt - 1f) < 0.03f, $"rate-independent failure: held at 0.9 the root tires through at {slowE.failAt:F2} s at 2 Hz, {fastE.failAt:F2} s at 8 Hz");
        } finally { StructureFatigue.CompressionFatigueShare = share0; }
        var held = Run(f12, 0.25f, 30f);
        Check(held.failAt > 0.5f, $"progressive: held at 1.15 the root wears through over time - fails at {held.failAt:F2} s, not at once");
        // (A11, 2026-10-15) a block whose definition removes it at 0.5 fails AT 0.5 - worn under it, the game removed it from
        // a health write with no split, smoke or tear
        {
            s.BreakAt = new float[s.Count]; Array.Fill(s.BreakAt, 0.5f);
            var health = new float[s.Count]; Array.Fill(health, 1f);
            float lowest = 1f, failHealth = -1f;
            for (float t = 0f; t < 30f && failHealth < 0f; t += 0.25f)
            {
                for (int i = 0; i < s.Count; i++) { s.Integrity[i] = health[i]; s.ExtForce[i] = f12[i]; }
                s.SolveFree(Vector3.Zero);
                StructureEval.Damage(s, null, 0.25f, true, false, StructureEval.BreakUtil, StructureEval.WearPerSecond, 2, damage);
                foreach (var (b, frac, destroy) in damage) { if (destroy) { failHealth = health[b]; break; } health[b] -= frac; lowest = MathF.Min(lowest, health[b]); }
            }
            s.BreakAt = null;
            Check(failHealth >= 0.5f && lowest > 0.5f - 1e-3f && failHealth < 0.6f, $"own threshold: a block with DestructionThreshold 0.5 wears to {lowest:F3} and fails at {failHealth:F3}, never worn under it");
        }
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
        // the server's readings (A1, 2026-10-15): a fresh reading is a NEW array (Ledger.Sync) - the server refilled one array
        // in place and the ledger never saw a reading again (a weld never taken, its pending written over the repair); it
        // takes two in turn (StructureServer _stInteg / _stIntegOther)
        {
            var led = new StructureFatigue.Ledger(); var integ = new float[1];
            var bufA = new float[] { 0.6f }; var bufB = new float[1];
            led.Sync(bufA, integ, 1, 0.25f);
            led.Accrue(0, 0.03f); led.Sync(bufA, integ, 1, 0.25f);   // (between readings: the last one again)
            int rep0 = StructureFatigue.Repairs;
            bufB[0] = 1f; led.Sync(bufB, integ, 1, 0.25f);           // (welded: the next reading, in the other array)
            Check(integ[0] == 1f && led.Pending[0] == 0f && StructureFatigue.Repairs == rep0 + 1, $"ledger: a weld seen through the server's alternating readings ({integ[0]:F3})");
            led.Accrue(0, 0.02f); led.Sync(bufB, integ, 1, 0.25f);
            bufA[0] = 0.5f; led.Sync(bufA, integ, 1, 0.25f);         // (shot: the reading after, in the first array again)
            Check(MathF.Abs(integ[0] - 0.48f) < 1e-4f, $"ledger: someone else's damage seen through the alternating readings ({integ[0]:F3})");
            // (one array refilled in place: never fresh - why the server alternates)
            var one = new float[] { 0.5f }; var led1 = new StructureFatigue.Ledger(); led1.Sync(one, integ, 1, 0.25f);
            one[0] = 1f; led1.Sync(one, integ, 1, 0.25f);
            Check(integ[0] == 0.5f, "ledger: one array refilled in place is not a fresh reading (the server must not reuse one)");
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


    /// <summary>Joined: from reaches to through contacts, not through gone blocks.</summary>
    static bool Joined(ShipStructure s, HashSet<int> gone, int from, int to)
    {
        StructureBend.Adjacency(s, out var a0, out var a1);
        if (gone.Contains(from) || gone.Contains(to)) return false;
        var seen = new bool[s.Count]; var q = new Queue<int>(); q.Enqueue(from); seen[from] = true;
        while (q.Count > 0)
        {
            int i = q.Dequeue(); if (i == to) return true;
            for (int e = a0[i]; e < a0[i + 1]; e++) { int j = a1[e]; if (seen[j] || gone.Contains(j)) continue; seen[j] = true; q.Enqueue(j); }
        }
        return false;
    }

    /// <summary>15. TENSION VS COMPRESSION (2026-10-15, the user: "add compression vs tension to the break work"): a deep beam -
    /// 12 x 2 x 3 blocks of 0.5 m off a fixed root - lifted: its root cut's lower row pulled, upper row pushed.</summary>
    // 15b. buckling-limited compression fatigues at the full rate (2026-10-15, the user's decision: the sign split made real
    //  ships - thin light-armour walls, buckling-governed - tire ~5x slower; a plate that buckles and un-buckles every pull
    //  cracks at its edges): the light beam's pushed row (its walls buckle) tires as fast as its pulled row; a stocky
    //  beam's (walls thick enough to reach the yield) at CompressionFatigueShare.
    static void BucklingFatigue()
    {
        float r1 = StructureFatigue.HealthRate(StructMaterial.Armor, 0.9f, 1.5f, 0.45f, 1f);
        float rB = StructureFatigue.HealthRate(StructMaterial.Armor, 0.9f, 1.5f, 0.45f, 0f, StructureFatigue.CompressionShare(true));
        float rS = StructureFatigue.HealthRate(StructMaterial.Armor, 0.9f, 1.5f, 0.45f, 0f, StructureFatigue.CompressionShare(false));
        Check(StructureFatigue.BucklingFatigueShare == 1f && rB == r1 && MathF.Abs(rS / r1 - StructureFatigue.CompressionFatigueShare) < 1e-4f,
            $"buckling fatigue: pure compression at 0.9 - buckling-limited at {rB / r1:F2} of tension, stocky at {rS / r1:F2} (CompressionFatigueShare {StructureFatigue.CompressionFatigueShare})");
        // (a 12-block beam two deep, three wide, lifted at a held 0.9 for 40 s; mass: the wall thickness - 33 kg: 2.8 mm, its
        //  0.5 m panels buckle at a third of the yield; 5x: 14 mm, they reach it)
        (float lostLow, float lostUp, bool buckLow, bool buckUp) Beam(float mass)
        {
            var sh = new TShip();
            int root = sh.Block(0, 0, 0, 1e9f, 1, 2, 3);
            var blk = new int[13, 2, 3];
            for (int x = 1; x <= 12; x++) for (int y = 0; y <= 1; y++) for (int z = 0; z <= 2; z++) blk[x, y, z] = sh.Block(x, y, z, mass);
            sh.Build();
            var s = sh.S;
            var health = new float[s.Count]; Array.Fill(health, 1f);
            var dmg = new List<(int block, float fraction, bool destroy)>();
            bool bLow = true, bUp = true;
            for (float t = 0f; t < 40f; t += 0.25f)
            {
                for (int i = 0; i < s.Count; i++) { s.Integrity[i] = 1f; s.ExtForce[i] = i == root ? Vector3.Zero : new Vector3(0, 100f, 0); s.ExtMoment[i] = Vector3.Zero; }
                s.SolveFree(Vector3.Zero);
                float k = 0.9f / s.MaxUtil;
                for (int i = 0; i < s.Count; i++) { s.ExtForce[i] *= k; s.Integrity[i] = health[i]; }
                s.SolveFree(Vector3.Zero);
                if (t == 0f)
                    for (int kc = 0; kc < s.ContactCount; kc++)
                    {
                        s.ContactBlocks(kc, out int a, out int b2);
                        if (!((a == root || b2 == root) && (a == blk[1, 0, 0] || b2 == blk[1, 0, 0]))) continue;
                        int g = s.ContactSection(kc);
                        for (int mi = s.SectionPieceStart(g); mi < s.SectionPieceStart(g) + s.SectionPieceCount(g); mi++)
                            if (s.Center[s.PieceBlock(mi)].Y > 0.375f) bUp &= s.PieceBuckling(mi); else bLow &= s.PieceBuckling(mi);
                    }
                StructureEval.Damage(s, null, 0.25f, true, false, StructureEval.BreakUtil, StructureEval.WearPerSecond, 2, dmg);
                foreach (var (b, frac, destroy) in dmg) if (!destroy) health[b] -= frac;
            }
            return (1f - health[blk[1, 0, 1]], 1f - health[blk[1, 1, 1]], bLow, bUp);
        }
        var thin = Beam(SmallMass); var stocky = Beam(5f * SmallMass);
        Check(thin.buckUp && thin.buckLow && !stocky.buckUp && !stocky.buckLow, $"buckling fatigue: the light beam's root members buckling-limited ({thin.buckUp}/{thin.buckLow}), the stocky beam's not ({stocky.buckUp}/{stocky.buckLow})");
        // (its pulled row is barely loaded - a third of the buckled push - so the tension rate is the fatigue blind to the sign)
        float keepC = StructureFatigue.CompressionFatigueShare; StructureFatigue.CompressionFatigueShare = 1f;
        var thinAll = Beam(SmallMass); StructureFatigue.CompressionFatigueShare = keepC;
        Check(thin.lostUp > 0f && thin.lostUp == thinAll.lostUp,
            $"buckling fatigue: 40 s at 0.9 - the light beam's pushed root block lost {thin.lostUp:F4}, at the tension rate {thinAll.lostUp:F4} (buckling: the full rate)");
        Check(stocky.lostLow > 0f && stocky.lostUp < 0.6f * stocky.lostLow,
            $"buckling fatigue: 40 s at 0.9 - the stocky beam's pushed root block lost {stocky.lostUp:F4}, its pulled one {stocky.lostLow:F4} (yield-limited: CompressionFatigueShare)");
        // (the share is the knob: pinned to the stocky share, the light beam's push slows as the stocky one's)
        float keep = StructureFatigue.BucklingFatigueShare; StructureFatigue.BucklingFatigueShare = StructureFatigue.CompressionFatigueShare;
        try { var thinLow = Beam(SmallMass); Check(thinLow.lostUp < 0.6f * thin.lostUp, $"buckling fatigue: BucklingFatigueShare {StructureFatigue.BucklingFatigueShare} - the light beam's pushed block lost {thinLow.lostUp:F4} (at 1: {thin.lostUp:F4})"); }
        finally { StructureFatigue.BucklingFatigueShare = keep; }
    }

    static void TensionCompression()
    {
        var sh = new TShip();
        int root = sh.Block(0, 0, 0, 1e9f, 1, 2, 3);
        var blk = new int[13, 2, 3];
        for (int x = 1; x <= 12; x++) for (int y = 0; y <= 1; y++) for (int z = 0; z <= 2; z++) blk[x, y, z] = sh.Block(x, y, z);
        sh.Build();
        var s = sh.S;
        // (the lift scaled to a target utilisation of the intact beam, then the health in)
        void Pull(float target, float[] health)
        {
            for (int i = 0; i < s.Count; i++) { s.Integrity[i] = 1f; s.ExtForce[i] = i == root ? Vector3.Zero : new Vector3(0, 100f, 0); s.ExtMoment[i] = Vector3.Zero; }
            s.SolveFree(Vector3.Zero);
            float k = target / s.MaxUtil;
            for (int i = 0; i < s.Count; i++) { s.ExtForce[i] *= k; if (health != null) s.Integrity[i] = health[i]; }
            s.SolveFree(Vector3.Zero);
        }
        Pull(1f, null);
        // the root cut: the section of a contact between the root and the first column
        int g = -1;
        for (int k = 0; k < s.ContactCount && g < 0; k++) { s.ContactBlocks(k, out int a, out int b); if ((a == root || b == root) && (a == blk[1, 0, 0] || b == blk[1, 0, 0])) g = s.ContactSection(k); }
        Check(g >= 0 && s.SectionPieceCount(Math.Max(g, 0)) == 6, $"tension/compression: the root cut and its six members ({(g >= 0 ? s.SectionPieceCount(g) : 0)})");
        if (g < 0) return;
        int m0 = s.SectionPieceStart(g), m1 = m0 + s.SectionPieceCount(g);
        bool signs = true; float utLow = 0f, ucHigh = 0f;
        for (int mi = m0; mi < m1; mi++)
        {
            bool upper = s.Center[s.PieceBlock(mi)].Y > 0.375f;
            if (upper) { signs &= s.PieceCompressive(mi) && s.PieceTension(mi) < 1e-4f; ucHigh = MathF.Max(ucHigh, s.PieceCompression(mi)); }
            else { signs &= !s.PieceCompressive(mi) && s.PieceCompression(mi) < 1e-4f; utLow = MathF.Max(utLow, s.PieceTension(mi)); }
        }
        Check(signs && utLow > 0.1f && ucHigh > 0.1f, $"tension/compression: lifted, the lower row is pulled (tension {utLow:F2}), the upper pushed (compression {ucHigh:F2})");
        Check(FractureMath.ModeOf(1.2f, 0.3f) == FractureMath.Torn && FractureMath.ModeOf(0.3f, 1.2f) == FractureMath.Hinge && FractureMath.ModeOf(0.3f, FractureMath.CrushUtil) == FractureMath.Crushed && FractureMath.ModeOf(0f, 0f) == FractureMath.Torn
            && FractureMath.ModeOf(1, 0.3f, 1.2f) == FractureMath.Torn && FractureMath.ModeOf(-1, 0.3f, 1.2f) == FractureMath.Hinge && FractureMath.ModeOf(-1, 0f, 2.5f) == FractureMath.Crushed && FractureMath.ModeOf(0, 1.2f, 0.3f) == FractureMath.Torn,
            "modes: the pulled side tears (even buckling-governed), the pushed side is the hinge, crushed past CrushUtil; no side - by the governing utilisation");
        {
            bool sides = true;
            for (int mi = m0; mi < m1; mi++) sides &= s.PieceSide(mi) == (s.Center[s.PieceBlock(mi)].Y > 0.375f ? -1 : 1);
            Check(sides, "tension/compression: each member's side of the section - the lower row pulled (+1), the upper pushed (-1)");
        }
        // fatigue's rate by sign
        float share = StructureFatigue.CompressionFatigueShare;
        float rT = StructureFatigue.HealthRate(StructMaterial.Armor, 0.9f, 1.5f, 0.45f, 1f), rC = StructureFatigue.HealthRate(StructMaterial.Armor, 0.9f, 1.5f, 0.45f, 0f);
        Check(rT == StructureFatigue.HealthRate(StructMaterial.Armor, 0.9f, 1.5f, 0.45f) && MathF.Abs(rC / rT - share) < 1e-4f, $"fatigue by sign: pure compression tires at {rC / rT:F2} of tension at 0.9 (CompressionFatigueShare {share})");
        float wT = StructureFatigue.HealthRate(StructMaterial.Armor, 1.3f, 1.5f, 0.45f, 1f), wC = StructureFatigue.HealthRate(StructMaterial.Armor, 1.3f, 1.5f, 0.45f, 0f);
        Check(MathF.Abs((wT - wC) - (1f - share) * StructureFatigue.Rate * StructMaterial.FatigueFactor[StructMaterial.Armor]) < 1e-5f, "fatigue by sign: the yield wear past 1 is the same either way (only the fatigue part differs)");
        Check(StructureFatigue.TensilePart(0.9f, 0.2f, 0.9f) == 1f && StructureFatigue.TensilePart(0f, 0.9f, 0.9f) == 0f && MathF.Abs(StructureFatigue.TensilePart(0.3f, 0.9f, 0.9f) - 1f / 3f) < 1e-5f
            && MathF.Abs(StructureFatigue.TensilePart(0f, 0.8f, 1f) - 0.6f) < 1e-5f && StructureFatigue.TensilePart(float.NaN, 0.5f, 0.6f) == 1f,
            "fatigue by sign: the tensile part - all of a pulled member, none of a pushed one, its shear kept");
        // the beam held at 0.9 for 40 s: the pulled row's fatigue unchanged, the pushed row's slower (the sign split itself:
        // both shares set - this light beam's walls buckle, BucklingFatigueShare; the stocky/buckling split: BucklingFatigue)
        float[] Tire(float sh0)
        {
            float keep = StructureFatigue.CompressionFatigueShare, keepB = StructureFatigue.BucklingFatigueShare;
            StructureFatigue.CompressionFatigueShare = sh0; StructureFatigue.BucklingFatigueShare = sh0;
            try
            {
                var health = new float[s.Count]; Array.Fill(health, 1f);
                var dmg = new List<(int block, float fraction, bool destroy)>();
                for (float t = 0f; t < 40f; t += 0.25f)
                {
                    Pull(0.9f, health);
                    StructureEval.Damage(s, null, 0.25f, true, false, StructureEval.BreakUtil, StructureEval.WearPerSecond, 2, dmg);
                    foreach (var (b, frac, destroy) in dmg) if (!destroy) health[b] -= frac;
                }
                return health;
            }
            finally { StructureFatigue.CompressionFatigueShare = keep; StructureFatigue.BucklingFatigueShare = keepB; }
        }
        var hFull = Tire(1f); var hSign = Tire(share);
        float lowFull = 1f - hFull[blk[1, 0, 1]], lowSign = 1f - hSign[blk[1, 0, 1]], upFull = 1f - hFull[blk[1, 1, 1]], upSign = 1f - hSign[blk[1, 1, 1]];
        Check(lowSign == lowFull && upFull > 0f && upSign < 0.6f * upFull,
            $"fatigue by sign: 40 s at 0.9 - the pulled root block lost {lowSign:F4} (at the full rate {lowFull:F4}), the pushed one {upSign:F4} (at the full rate {upFull:F4}): it lasts {upFull / MathF.Max(upSign, 1e-9f):F1}x longer");

        // worn through at 1.3: the pulled side tears first, the pushed side - the hinge - goes last
        bool jag = FractureMath.Jagged; FractureMath.Jagged = false;
        try
        {
            var health = new float[s.Count]; Array.Fill(health, 1f);
            var dmg = new List<(int block, float fraction, bool destroy)>(); var cuts = new List<StructureEval.CutFail>();
            StructureEval.CutFail cut = null; float at = 0f;
            for (float t = 0f; t < 20f && cut == null; t += 0.25f)
            {
                Pull(1.3f, health);
                StructureEval.Damage(s, null, 0.25f, true, false, StructureEval.BreakUtil, StructureEval.WearPerSecond, 1, dmg, null, cuts);
                if (cuts.Count > 0) { cut = cuts[0]; at = t; break; }
                foreach (var (b, frac, destroy) in dmg) if (!destroy) health[b] -= frac;
            }
            Check(cut != null && cut.Release != null && cut.Mode != null, $"crack: the beam's root wears through at 1.3 ({at:F2} s) and its cut has a crack plan");
            if (cut != null && cut.Release != null)
            {
                float tornLast = 0f, hingeFirst = float.MaxValue, lo = float.MaxValue, hi = 0f; int nT = 0, nH = 0; bool rows = true;
                for (int i = 0; i < cut.Count; i++)
                {
                    int b = dmg[cut.First + i].block; float r = cut.Release[i];
                    lo = MathF.Min(lo, r); hi = MathF.Max(hi, r);
                    if (cut.Mode[i] == FractureMath.Hinge) { nH++; hingeFirst = MathF.Min(hingeFirst, r); rows &= s.Center[b].Y > 0.375f; }
                    else if (cut.Mode[i] == FractureMath.Torn) { nT++; tornLast = MathF.Max(tornLast, r); rows &= s.Center[b].Y < 0.375f; }
                }
                Check(nT == 3 && nH == 3 && rows, $"crack: the pulled row tears, the pushed row is the hinge ({nT} torn, {nH} hinge)");
                Check(lo == 0f && MathF.Abs(hi - FractureMath.CrackSeconds) < 1e-5f && hingeFirst >= tornLast, $"crack: from 0 to CrackSeconds ({lo:F2} .. {hi:F2} s), the hinge after every torn member (torn by {tornLast:F2} s, hinge from {hingeFirst:F2} s)");
            }
            // the plan's order: monotone in the key, the extreme pushed fibre last, ties with their nearest member
            {
                var mode = new byte[] { FractureMath.Torn, FractureMath.Torn, FractureMath.Hinge, FractureMath.Hinge, FractureMath.Torn, FractureMath.Tie };
                var u = new float[] { 1.2f, 1.5f, 1.4f, 1.1f, 1.2f, 0f };
                var uc = new float[] { 0f, 0f, 1.4f, 1.1f, 0f, 0f };
                var pts = new Vector3[] { new(0, 0, 0), new(0, 0, 1), new(0, 1, 0), new(0, 1, 1), new(0, 0, 2), new(0, 1.1f, 0) };
                var rel = new float[6];
                FractureMath.Plan(mode, u, uc, pts, 6, 0.3f, rel);
                Check(rel[1] == 0f && rel[0] == rel[4] && rel[0] > rel[1] && rel[3] > rel[0] && rel[2] > rel[3] && MathF.Abs(rel[2] - 0.3f) < 1e-6f && rel[5] == rel[2],
                    $"plan: most loaded torn first, equal keys together, the less pushed hinge next, the most pushed last at CrackSeconds, a tie with its nearest ({string.Join(" ", rel.Select(r => r.ToString("F3")))})");
                var all = new byte[] { FractureMath.Hinge, FractureMath.Hinge };
                var r2 = new float[2]; FractureMath.Plan(all, new[] { 1.3f, 1.1f }, new[] { 1.3f, 1.1f }, new Vector3[2], 2, 0.3f, r2);
                Check(r2[0] == 0f && r2[1] == 0.3f, "plan: an all-pushed cut goes by its utilisation (the worst first)");
            }
            // a snap past CrushUtil: the pushed side crushed, not the hinge
            {
                Pull(StructureFatigue.SnapUtil * 1.3f, null);
                var cuts2 = new List<StructureEval.CutFail>();
                StructureEval.Damage(s, null, 0.25f, true, false, StructureEval.BreakUtil, StructureEval.WearPerSecond, 1, dmg, null, cuts2);
                int crushed = 0; if (cuts2.Count > 0) foreach (var md in cuts2[0].Mode) if (md == FractureMath.Crushed) crushed++;
                Check(cuts2.Count == 1 && crushed == 3, $"crush: a snap at {StructureFatigue.SnapUtil * 1.3f:F1} crushes the pushed row ({crushed} crushed)");
            }
        }
        finally { FractureMath.Jagged = jag; }

        // the bend folds about the pushed side
        {
            var damage = new List<(int block, float fraction, bool destroy)>(); var where = new List<(Vector3 at, Vector3 dir, float util)>();
            var bends = new List<StructureBend.Bend>(); int mj = StructureBend.MaxJoints; StructureBend.MaxJoints = 1;
            float hc = StructureBend.HingeToCompression;
            try
            {
                Pull(1.3f, null);
                StructureEval.Damage(s, null, 0.25f, true, false, 1.5f, 0.5f, 2, damage, where);
                StructureBend.Plan(s, damage, where, new Dictionary<long, float>(), bends);
                Check(bends.Count == 1, $"fold: one bend ({bends.Count})");
                if (bends.Count == 1)
                {
                    var b = bends[0];
                    var lowP = new Vector3(b.Plane + 0.6f, -0.125f, 0.375f); var upP = new Vector3(b.Plane + 0.6f, 0.875f, 0.375f);
                    var dLow = StructureBend.Displacement(b, lowP); var dUp = StructureBend.Displacement(b, upP);
                    var tipD = StructureBend.Displacement(b, s.Center[blk[12, 0, 1]]);
                    Check(b.Joint.Y > 0.5f && tipD.Y > 0f && dLow.X > 0f && dUp.X > -1e-5f && dLow.X > dUp.X,
                        $"fold: about the pushed side (pivot y {b.Joint.Y:F2}) - the tip up {tipD.Y * 100f:F1} cm, the pulled side opens {dLow.X * 1000f:F1} mm, the pushed side {dUp.X * 1000f:F2} mm (never into the ship)");
                    StructureBend.HingeToCompression = 0f;
                    StructureBend.Plan(s, damage, where, new Dictionary<long, float>(), bends);
                    Check(bends.Count == 1 && MathF.Abs(bends[0].Joint.Y - 0.375f) < 1e-3f && StructureBend.Displacement(bends[0], upP).X < 0f, "fold: HingeToCompression 0 - about the cut's centre, as before (the pushed side moves in)");
                }
            }
            finally { StructureBend.MaxJoints = mj; StructureBend.HingeToCompression = hc; }
        }
    }

    /// <summary>16. THE JAGGED LINE (2026-10-15, the user: "our uniform break is maybe a little too linear?" - FractureMath.JagCut):
    /// a plate 10 x 2 x 6 blocks of 0.5 m off a fixed root, lifted; its cut between columns 4 and 5.</summary>
    static void Jagged()
    {
        var sh = new TShip();
        var blk = new int[10, 2, 6];
        for (int x = 0; x <= 9; x++) for (int y = 0; y <= 1; y++) for (int z = 0; z <= 5; z++) blk[x, y, z] = sh.Block(x, y, z, x == 0 ? 1e9f : SmallMass);
        sh.Build();
        var s = sh.S; int root = blk[0, 0, 0], tip = blk[9, 1, 5];
        void Lift(float target)
        {
            for (int i = 0; i < s.Count; i++) { s.Integrity[i] = 1f; s.ExtForce[i] = s.Center[i].X > 0.3f ? new Vector3(0, 100f, 0) : Vector3.Zero; s.ExtMoment[i] = Vector3.Zero; }
            s.SolveFree(Vector3.Zero);
            if (target > 0f) { float k = target / s.MaxUtil; for (int i = 0; i < s.Count; i++) s.ExtForce[i] *= k; s.SolveFree(Vector3.Zero); }
        }
        Lift(0f);
        int g = -1;
        for (int q = 0; q < s.SectionCount; q++) { s.SectionPlane(q, out int ax, out float pl); if (ax == 0 && MathF.Abs(pl - 2.375f) < 1e-3f) g = q; }
        Check(g >= 0 && s.SectionPieceCount(Math.Max(g, 0)) == 12, $"jagged: the plate's cut between columns 4 and 5 ({(g >= 0 ? s.SectionPieceCount(g) : 0)} members)");
        if (g < 0) return;
        float share = FractureMath.JaggedShare, stress = FractureMath.JaggedStress; bool hs = FractureMath.HingeStays;
        FractureMath.JaggedShare = 1f; FractureMath.JaggedStress = 0f; FractureMath.HingeStays = false;
        try
        {
            int n = s.SectionPieceCount(g);
            var sc = new FractureMath.JagScratch();
            var ob = new int[n]; var at = new Vector3[n]; var step = new int[n]; var stubs = new List<int>();
            int parted = 0, seeds = 300, stepped = 0, inner = 0, outer = 0, withStubs = 0, near = 0; int rep0 = FractureMath.Repairs;
            bool deterministic = true, differ = false; int[] first = null;
            for (int seed = 0; seed < seeds; seed++)
            {
                FractureMath.JagCut(s, g, seed, sc, ob, at, step, stubs);
                var again = new int[n]; var at2 = new Vector3[n]; var st2 = new int[n];
                FractureMath.JagCut(s, g, seed, sc, again, at2, st2, null);
                for (int i = 0; i < n; i++) if (again[i] != ob[i] || st2[i] != step[i]) deterministic = false;
                if (first == null) first = (int[])ob.Clone(); else for (int i = 0; i < n; i++) if (first[i] != ob[i]) differ = true;
                var gone = new HashSet<int>(ob);
                if (!Joined(s, gone, root, tip)) parted++;
                bool ok = true;
                for (int i = 0; i < n; i++)
                {
                    if (step[i] != 0) stepped++;
                    if (step[i] < 0) inner++;
                    if (step[i] > 0) outer++;
                    float dx = s.Center[ob[i]].X - 2.375f;
                    if (dx < -0.26f || dx > 0.76f) ok = false;
                }
                if (ok) near++;
                if (stubs.Count > 0) withStubs++;
            }
            Check(deterministic && differ, "jagged: the same seed the same line, other seeds other lines");
            Check(parted == seeds, $"jagged: every path across the cut broken - the root never joined to the tip ({parted} of {seeds} seeds)");
            Check(near == seeds && inner > 0 && outer > 0 && withStubs > 0, $"jagged: each member at the plane or one block either side ({near} of {seeds}); steps in {inner} / out {outer}, stubs in {withStubs} lines");
            Check(FractureMath.Repairs > rep0 && stepped > 0, $"jagged: steps that would leave a bridge put back ({FractureMath.Repairs - rep0} repairs over {seeds} lines, {stepped} steps kept)");
            // off: the flat cut's blocks
            bool jg = FractureMath.Jagged; FractureMath.Jagged = false;
            var dmg = new List<(int block, float fraction, bool destroy)>(); var cuts = new List<StructureEval.CutFail>();
            try
            {
                Lift(StructureFatigue.SnapUtil * 1.2f);
                StructureEval.Damage(s, null, 0.25f, true, false, StructureEval.BreakUtil, StructureEval.WearPerSecond, 1, dmg, null, cuts);
                bool flat = cuts.Count == 1 && cuts[0].Steps == 0 && cuts[0].Stubs == null;
                if (flat) { var cg = cuts[0]; for (int i = 0; i < cg.Count; i++) flat &= MathF.Abs(s.Center[dmg[cg.First + i].block].X - cg.Plane) < 0.26f; }
                Check(flat, "jagged off: the flat cut's own blocks, on the plane");
            }
            finally { FractureMath.Jagged = jg; }
            // through DamageBySection, jagged: the snap parts the plate for every seed
            int partedEval = 0, steppedEval = 0;
            for (int seed = 1; seed <= 40; seed++)
            {
                s.Seed = seed;
                StructureEval.Damage(s, null, 0.25f, true, false, StructureEval.BreakUtil, StructureEval.WearPerSecond, 1, dmg, null, cuts);
                var gone = new HashSet<int>(); foreach (var (b, _, d) in dmg) if (d) gone.Add(b);
                if (cuts.Count == 1 && !Joined(s, gone, root, tip)) partedEval++;
                if (cuts.Count == 1) steppedEval += cuts[0].Steps;
            }
            s.Seed = 0;
            Check(partedEval == 40 && steppedEval > 0, $"jagged: a failed cut parts the plate for every seed ({partedEval} of 40; {steppedEval} members off the plane in all)");
        }
        finally { FractureMath.JaggedShare = share; FractureMath.JaggedStress = stress; FractureMath.HingeStays = hs; }
        JaggedBound();
    }

    /// <summary>16b. (2026-10-15 review) the jagged line's cost is bounded: on a wide plate (120 members a cut, every member
    /// stepping) the separation check takes a few passes - not one per bad pair (O(n^2) per failed cut) - and still parts
    /// it; and the scratch's adjacency is built once per structure, not per evaluation.</summary>
    static void JaggedBound()
    {
        var sh = new TShip();
        int W = 60;
        var blk = new int[10, 2, W];
        for (int x = 0; x <= 9; x++) for (int y = 0; y <= 1; y++) for (int z = 0; z < W; z++) blk[x, y, z] = sh.Block(x, y, z, x == 0 ? 1e9f : SmallMass);
        sh.Build();
        var s = sh.S; int root = blk[0, 0, 0], tip = blk[9, 1, W - 1];
        for (int i = 0; i < s.Count; i++) { s.Integrity[i] = 1f; s.ExtForce[i] = s.Center[i].X > 0.3f ? new Vector3(0, 100f, 0) : Vector3.Zero; s.ExtMoment[i] = Vector3.Zero; }
        s.SolveFree(Vector3.Zero);
        int g = -1;
        for (int q = 0; q < s.SectionCount; q++) { s.SectionPlane(q, out int ax, out float pl); if (ax == 0 && MathF.Abs(pl - 2.375f) < 1e-3f) g = q; }
        Check(g >= 0, "jagged bound: the wide plate's cut");
        if (g < 0) return;
        float share = FractureMath.JaggedShare, stress = FractureMath.JaggedStress; bool hs = FractureMath.HingeStays;
        FractureMath.JaggedShare = 1f; FractureMath.JaggedStress = 0f; FractureMath.HingeStays = false;
        try
        {
            int n = s.SectionPieceCount(g), worst = 0, parted = 0, seeds = 100, stepped = 0;
            var sc = new FractureMath.JagScratch();
            var ob = new int[n]; var at = new Vector3[n]; var step = new int[n]; var stubs = new List<int>();
            for (int seed = 0; seed < seeds; seed++)
            {
                stepped += FractureMath.JagCut(s, g, seed, sc, ob, at, step, stubs);
                worst = Math.Max(worst, FractureMath.LastPasses);
                if (!Joined(s, new HashSet<int>(ob), root, tip)) parted++;
            }
            Check(worst <= FractureMath.MaxRepairPasses + 1, $"jagged bound: at most {FractureMath.MaxRepairPasses + 1} separation passes a cut of {n} members (worst {worst})");
            Check(parted == seeds && stepped > 0, $"jagged bound: still parted for every seed ({parted} of {seeds}; {stepped} steps kept)");
            // the scratch with the structure: built once, kept over evaluations
            var adj0 = s.Jag?.AdjStart;
            var dmg = new List<(int block, float fraction, bool destroy)>(); var cuts = new List<StructureEval.CutFail>();
            float k = StructureFatigue.SnapUtil * 1.2f / s.MaxUtil;
            for (int i = 0; i < s.Count; i++) s.ExtForce[i] *= k;
            s.SolveFree(Vector3.Zero);
            StructureEval.Damage(s, null, 0.25f, true, false, StructureEval.BreakUtil, StructureEval.WearPerSecond, 1, dmg, null, cuts);
            var adj1 = s.Jag?.AdjStart;
            StructureEval.Damage(s, null, 0.25f, true, false, StructureEval.BreakUtil, StructureEval.WearPerSecond, 1, dmg, null, cuts);
            Check(cuts.Count > 0 && adj1 != null && ReferenceEquals(s.Jag?.AdjStart, adj1), $"jagged bound: the scratch's adjacency kept with the structure between evaluations ({cuts.Count} cuts)");
        }
        finally { FractureMath.JaggedShare = share; FractureMath.JaggedStress = stress; FractureMath.HingeStays = hs; }
    }

    /// <summary>(A31, 2026-10-15) the scorch thresholds follow each class's own heat limits (StructMaterial.ScorchAt):
    /// armour's are the knobs themselves; a heat shield is not scorched below its own softening.</summary>
    static void ScorchThresholds()
    {
        StructMaterial.ScorchAt(StructMaterial.Armor, 700f, 950f, out float la, out float da);
        Check(MathF.Abs(la - 700f) < 1e-3f && MathF.Abs(da - 950f) < 1e-3f, $"scorch: armour at the knobs ({la:F0} / {da:F0} K)");
        StructMaterial.ScorchAt(StructMaterial.HeatShield, 700f, 950f, out float ls, out float ds);
        Check(ls >= StructMaterial.SoftenK[StructMaterial.HeatShield] && ds > ls && ds < StructMaterial.WeakK[StructMaterial.HeatShield], $"scorch: a heat shield from its own softening ({ls:F0} / {ds:F0} K, softens at {StructMaterial.SoftenK[StructMaterial.HeatShield]:F0})");
        bool order = true;
        for (int m = 0; m < StructMaterial.Count; m++) { StructMaterial.ScorchAt(m, 700f, 950f, out float l, out float d); order &= l > 0f && d > l && d < StructMaterial.WeakK[m]; }
        Check(order, "scorch: every class light under dark, dark under its WeakK");
    }


    /// <summary>17. A FAILED CUT PARTS BY ITSELF (2026-10-15, the integration run: the SSTO pulled at Strength 1.5 - "the cut did
    /// not separate - 28 block(s) still bridging it, destroyed", CutsForced +2): the crack and its watch played tick by tick
    /// as the server does (StructureServer.Failure: CheckCutSeparated, then StepCracks; the game's split the tick after the
    /// members let go part the piece) on irregular plates - the organic break's defaults (jagged, the hinge stays). The piece
    /// often parts MID-CRACK, before its last members (a hinge whose path the others already cut); the watch (CutWatch) must
    /// credit that split - no bridge left on the jagged rule, the stubs spared - and never force the cut: before, its
    /// baseline was read at the crack's end, it never saw the split, and its plane's rule destroyed the stubs and their mates
    /// (2 x stubs) on a cut that had parted.</summary>
    static void CrackSeparates()
    {
        int runs = 0, parted = 0, forced = 0, midCrack = 0, stubbed = 0, plainForce = 0, stubKill = 0;
        for (int shape = 0; shape < 24; shape++)
        {
            int H = 2 + shape % 3, W = 4 + (shape / 3) % 4;
            var rng = new Random(shape * 7919 + 1);
            var sh = new TShip();
            var blk = new int[10, H, W];
            for (int x = 0; x <= 9; x++) for (int y = 0; y < H; y++) for (int z = 0; z < W; z++)
            {
                // (holes beyond the root columns - an irregular section and far side, like a real ship's)
                if (x >= 2 && x <= 8 && rng.NextDouble() < 0.12) { blk[x, y, z] = -1; continue; }
                blk[x, y, z] = sh.Block(x, y, z, x == 0 ? 1e9f : SmallMass);
            }
            sh.Build();
            var s = sh.S; int root = blk[0, 0, 0];
            for (int i = 0; i < s.Count; i++) { s.Integrity[i] = 1f; s.ExtForce[i] = s.Center[i].X > 0.3f ? new Vector3(0, 100f, 0) : Vector3.Zero; s.ExtMoment[i] = Vector3.Zero; }
            s.SolveFree(Vector3.Zero);
            float k = StructureFatigue.SnapUtil * 1.2f / s.MaxUtil;
            for (int i = 0; i < s.Count; i++) s.ExtForce[i] *= k;
            s.SolveFree(Vector3.Zero);
            StructureBend.Adjacency(s, out var a0, out var a1);
            var dmg = new List<(int block, float fraction, bool destroy)>(); var cuts = new List<StructureEval.CutFail>();
            for (int seed = 1; seed <= 6; seed++)
            {
                s.Seed = seed;
                StructureEval.Damage(s, null, 0.25f, true, false, StructureEval.BreakUtil, StructureEval.WearPerSecond, 1, dmg, null, cuts);
                if (cuts.Count != 1 || cuts[0].Release == null) continue;
                var cut = cuts[0]; int side = (cut.Axis == 0 ? cut.Dir.X : cut.Axis == 1 ? cut.Dir.Y : cut.Dir.Z) > 0f ? 1 : -1;
                float Ax(Vector3 v) => cut.Axis == 0 ? v.X : cut.Axis == 1 ? v.Y : v.Z;
                // (a far-side block to watch: the farthest out)
                int far = -1; for (int b = 0; b < s.Count; b++) if ((Ax(s.Center[b]) - cut.Plane) * side > 0f && (far < 0 || (Ax(s.Center[b]) - Ax(s.Center[far])) * side > 0f)) far = b;
                if (far < 0) continue;
                var stubs = new HashSet<int>(cut.Stubs ?? Array.Empty<int>());
                var removed = new bool[s.Count]; var grid = new bool[s.Count];
                // (this grid: what still joins the root - the game's split takes the rest away)
                void Grid()
                {
                    Array.Clear(grid); var q = new Queue<int>(); q.Enqueue(root); grid[root] = true;
                    while (q.Count > 0) { int i = q.Dequeue(); for (int e = a0[i]; e < a0[i + 1]; e++) { int j = a1[e]; if (grid[j] || removed[j]) continue; grid[j] = true; q.Enqueue(j); } }
                }
                // (the server's Bridges on the model: pairs across the plane in this grid - stubs spared on the jagged rule -
                //  and, sparing them, what holds a stub from beyond it)
                int Bridges(bool spare)
                {
                    int pairs = 0;
                    for (int c = 0; c < s.ContactCount; c++)
                    {
                        s.ContactPlane(c, out int ax, out float pl); if (ax != cut.Axis || MathF.Abs(pl - cut.Plane) > 1e-3f) continue;
                        s.ContactBlocks(c, out int ca, out int cb);
                        if (!grid[ca] || !grid[cb] || spare && (stubs.Contains(ca) || stubs.Contains(cb))) continue;
                        pairs++;
                    }
                    if (spare) foreach (int sb in stubs)
                        {
                            if (!grid[sb]) continue;
                            for (int e = a0[sb]; e < a0[sb + 1]; e++) { int j = a1[e]; if (grid[j] && !stubs.Contains(j) && (Ax(s.Center[j]) - Ax(s.Center[sb])) * side > 0.1f) pairs++; }
                        }
                    return pairs;
                }
                // the crack, a tick at a time (60 Hz): the watch first, then the releases due; the split the tick after
                var order = new int[cut.Count]; for (int i = 0; i < cut.Count; i++) order[i] = i;
                Array.Sort(order, (p, q) => { int r = cut.Release[p].CompareTo(cut.Release[q]); return r != 0 ? r : p.CompareTo(q); });
                var w = new CutWatch(); w.HasStubs = stubs.Count > 0;
                int stamp = 0, next = 0; bool armed = false, done = false, splitPending = false, split = false, force = false; float clock = 0f;
                w.Begin(stamp);
                for (int tick = 0; tick < 400 && !done; tick++)
                {
                    if (splitPending) { stamp++; splitPending = false; Grid(); }   // (the game's split: the far side gone from this grid)
                    if (armed)
                    {
                        if (w.SplitSince(stamp) && Bridges(w.SpareOnSplit) == 0) { done = true; break; }
                        if (w.Expired())
                        {
                            Grid(); int n = Bridges(w.Spare);
                            if (n > 0) { force = true; if (w.Stage == 1) plainForce++; }
                            if (!w.NextStage(stamp, 30)) done = true;
                        }
                    }
                    if (next < cut.Count)
                    {
                        if (tick > 0) clock += 1f / 60f;
                        while (next < cut.Count && cut.Release[order[next]] <= clock + 1e-4f) removed[dmg[cut.First + order[next++]].block] = true;
                        Grid();
                        if (!split && !grid[far]) { split = true; splitPending = true; if (next < cut.Count) midCrack++; }
                        if (next == cut.Count) { w.Finish(stamp, 30); armed = true; }
                    }
                }
                runs++; if (stubs.Count > 0) stubbed++;
                if (split && !force) parted++;
                if (force) forced++;
                // (what the plane's rule would take once parted: the stubs and their mates across the plane)
                Grid(); if (split) stubKill += Bridges(false);
            }
            s.Seed = 0;
        }
        Check(runs >= 40 && parted == runs && forced == 0, $"crack separates: every failed cut parts by itself and its watch never forces it ({parted} of {runs} parted, {forced} forced - {plainForce} by the plane's rule)");
        Check(midCrack > 0 && stubbed > 0 && stubKill > 0, $"crack separates: the pieces parting mid-crack ({midCrack}) are credited - their stubs ({stubbed} cuts) never taken by the plane's rule ({stubKill} pairs it would have)");
    }
    /// <summary>18. A FAILED CUT'S MEMBERS LET GO, AND A FORCED CUT'S SPLIT IS CREDITED (2026-10-15, the third integration
    /// run: the SSTO pulled at Strength 1.5 - CutsForced +2 on one cut: "5 block(s) still bridging it", then "4"). Two
    /// ControlSurface_Small members split at their failing face kept other mount groups on it (the game disables only a dead
    /// piece's) - the wing hung on until the watch destroyed them and the 3 armour blocks mounted to one's face; the wing
    /// split off 23 ms later, and stage 1 judged that split on the plane's rule: the jagged line's stubs and their mates (on
    /// the ship by design) "still bridging" - destroyed 0.5 s after it had parted. Played tick by tick as CrackSeparates, a
    /// third of the members SPLIT-KIND (split at the face, still mounted across it): CutWatch.Sever on - destroyed whole at
    /// their release (FailBlock: MountedAcross), every cut parts by itself; off - the watch forces them at stage 0 (the
    /// in-game signature) and the split that follows is credited on the jagged surface (CutWatch.SpareOnSplit) - stage 1
    /// never forces a parted cut.</summary>
    static void CutLetsGo()
    {
        bool sev0 = CutWatch.Sever;
        int[] runs = new int[2], parted = new int[2], forced = new int[2], stage1 = new int[2], stubbed = new int[2], held = new int[2];
        try
        {
            for (int mode = 0; mode < 2; mode++)
            {
                CutWatch.Sever = mode == 0;
                for (int shape = 0; shape < 24; shape++)
                {
                    int H = 2 + shape % 3, W = 4 + (shape / 3) % 4;
                    var rng = new Random(shape * 7919 + 1);
                    var sh = new TShip();
                    var blk = new int[10, H, W];
                    for (int x = 0; x <= 9; x++) for (int y = 0; y < H; y++) for (int z = 0; z < W; z++)
                    {
                        if (x >= 2 && x <= 8 && rng.NextDouble() < 0.12) { blk[x, y, z] = -1; continue; }
                        blk[x, y, z] = sh.Block(x, y, z, x == 0 ? 1e9f : SmallMass);
                    }
                    sh.Build();
                    var s = sh.S; int root = blk[0, 0, 0];
                    for (int i = 0; i < s.Count; i++) { s.Integrity[i] = 1f; s.ExtForce[i] = s.Center[i].X > 0.3f ? new Vector3(0, 100f, 0) : Vector3.Zero; s.ExtMoment[i] = Vector3.Zero; }
                    s.SolveFree(Vector3.Zero);
                    float k = StructureFatigue.SnapUtil * 1.2f / s.MaxUtil;
                    for (int i = 0; i < s.Count; i++) s.ExtForce[i] *= k;
                    s.SolveFree(Vector3.Zero);
                    StructureBend.Adjacency(s, out var a0, out var a1);
                    var dmg = new List<(int block, float fraction, bool destroy)>(); var cuts = new List<StructureEval.CutFail>();
                    for (int seed = 1; seed <= 6; seed++)
                    {
                        s.Seed = seed;
                        StructureEval.Damage(s, null, 0.25f, true, false, StructureEval.BreakUtil, StructureEval.WearPerSecond, 1, dmg, null, cuts);
                        if (cuts.Count != 1 || cuts[0].Release == null) continue;
                        var cut = cuts[0]; int side = (cut.Axis == 0 ? cut.Dir.X : cut.Axis == 1 ? cut.Dir.Y : cut.Dir.Z) > 0f ? 1 : -1;
                        float Ax(Vector3 v) => cut.Axis == 0 ? v.X : cut.Axis == 1 ? v.Y : v.Z;
                        int far = -1; for (int b = 0; b < s.Count; b++) if ((Ax(s.Center[b]) - cut.Plane) * side > 0f && (far < 0 || (Ax(s.Center[b]) - Ax(s.Center[far])) * side > 0f)) far = b;
                        if (far < 0) continue;
                        var stubs = new HashSet<int>(cut.Stubs ?? Array.Empty<int>());
                        var removed = new bool[s.Count]; var grid = new bool[s.Count];
                        void Grid()
                        {
                            Array.Clear(grid); var q = new Queue<int>(); q.Enqueue(root); grid[root] = true;
                            while (q.Count > 0) { int i = q.Dequeue(); for (int e = a0[i]; e < a0[i + 1]; e++) { int j = a1[e]; if (grid[j] || removed[j]) continue; grid[j] = true; q.Enqueue(j); } }
                        }
                        // (the server's Bridges on the model - its contacts are the mated ones: pairs across the plane in this
                        //  grid, stubs spared on the jagged rule, and then what holds a stub from beyond the plane, any face)
                        int Bridges(bool spare, HashSet<int> into)
                        {
                            int pairs = 0;
                            for (int c = 0; c < s.ContactCount; c++)
                            {
                                s.ContactPlane(c, out int ax, out float pl); if (ax != cut.Axis || MathF.Abs(pl - cut.Plane) > 1e-3f) continue;
                                s.ContactBlocks(c, out int ca, out int cb);
                                if (!grid[ca] || !grid[cb] || spare && (stubs.Contains(ca) || stubs.Contains(cb))) continue;
                                pairs++; into?.Add(ca); into?.Add(cb);
                            }
                            if (spare) foreach (int sb in stubs)
                                {
                                    if (!grid[sb]) continue;
                                    for (int e = a0[sb]; e < a0[sb + 1]; e++) { int j = a1[e]; if (grid[j] && !stubs.Contains(j) && (Ax(s.Center[j]) - cut.Plane) * side > 0f) { pairs++; into?.Add(j); } }
                                }
                            return pairs;
                        }
                        var order = new int[cut.Count]; for (int i = 0; i < cut.Count; i++) order[i] = i;
                        Array.Sort(order, (p, q) => { int r = cut.Release[p].CompareTo(cut.Release[q]); return r != 0 ? r : p.CompareTo(q); });
                        var survivors = new List<int>();
                        var w = new CutWatch(); w.HasStubs = stubs.Count > 0;
                        int stamp = 0, next = 0; bool armed = false, done = false, splitPending = false, split = false, force = false; float clock = 0f;
                        void Parted() { Grid(); if (!split && !grid[far]) { split = true; splitPending = true; } }
                        w.Begin(stamp);
                        for (int tick = 0; tick < 400 && !done; tick++)
                        {
                            if (splitPending) { stamp++; splitPending = false; Grid(); }   // (the game's split: the far side gone from this grid)
                            if (armed)
                            {
                                if (w.SplitSince(stamp) && Bridges(w.SpareOnSplit, null) == 0) { done = true; break; }
                                if (w.Expired())
                                {
                                    // (forced, as CheckCutSeparated: the members that held on and this stage's bridges destroyed -
                                    //  stage 1's only on a cut not split or still bridged on the jagged surface)
                                    Grid();
                                    if (w.Stage > 0 && !w.Force(Bridges(w.SpareOnSplit, null))) { done = true; break; }
                                    var set = new HashSet<int>(); foreach (int b in survivors) if (grid[b]) set.Add(b);
                                    Bridges(w.Spare, set);
                                    if (set.Count > 0) { force = true; if (w.Stage == 1) stage1[mode]++; foreach (int b in set) removed[b] = true; Parted(); }
                                    if (!w.NextStage(stamp, 30)) done = true;
                                }
                            }
                            if (next < cut.Count)
                            {
                                if (tick > 0) clock += 1f / 60f;
                                while (next < cut.Count && cut.Release[order[next]] <= clock + 1e-4f)
                                {
                                    int e = order[next++], b = dmg[cut.First + e].block;
                                    bool crushed = cut.Mode != null && e < cut.Mode.Length && cut.Mode[e] == FractureMath.Crushed;
                                    // (split-kind: split at its face, other mount groups of that face still on - it stays mounted
                                    //  across it unless destroyed whole for that, CutWatch.Sever)
                                    if (!crushed && b % 3 == 0 && !CutWatch.Sever) { survivors.Add(b); held[mode]++; }
                                    else removed[b] = true;
                                }
                                Parted();
                                if (next == cut.Count) { w.Finish(stamp, 30); armed = true; }
                            }
                        }
                        runs[mode]++; if (stubs.Count > 0) stubbed[mode]++;
                        Grid(); if (!grid[far]) parted[mode]++;
                        if (force) forced[mode]++;
                    }
                    s.Seed = 0;
                }
            }
        }
        finally { CutWatch.Sever = sev0; }
        Check(runs[0] >= 40 && parted[0] == runs[0] && forced[0] == 0, $"cut lets go: split members still mounted destroyed whole at their release - every cut parts by itself ({parted[0]} of {runs[0]} parted, {forced[0]} forced)");
        Check(held[1] > 0 && forced[1] > 0 && parted[1] == runs[1], $"cut lets go: without that they hang on and the watch forces them at stage 0 ({held[1]} held on, {forced[1]} of {runs[1]} cuts forced, {parted[1]} parted)");
        Check(stage1[0] == 0 && stage1[1] == 0 && stubbed[1] > 0, $"cut lets go: a forced cut's split credited on the jagged surface - stage 1 never forces a parted cut ({stage1[1]} stage-1 forcings, {stubbed[1]} cuts with stubs)");
    }
    /// <summary>The model's blocks for CutHolders (a class per block: the server's are CubeBlockComponent references).</summary>
    sealed class HNode { public int I; }
    /// <summary>CutHolders on a test graph: joined - the game's joints (the model's contacts and the hidden ones), live only.</summary>
    sealed class GraphHolders : CutHolders<HNode>
    {
        public HNode[] Nodes; public List<int>[] Adj; public Func<int, bool> Live, LeavesOf;
        /// <summary>A joint the game holds that the mounts' test misses (a with b); found by touch only (the last round).</summary>
        public int TouchA = -1, TouchB = -1; public bool Touch;
        /// <summary>(RIGID THRUSTERS) blocks never broken - an engine: its mounts instead.</summary>
        public Func<int, bool> KeptOf;
        protected override bool Leaves(HNode b) => LeavesOf(b.I);
        protected override bool Kept(HNode b) => KeptOf != null && KeptOf(b.I);
        protected override void Joined(HNode b, List<HNode> into)
        {
            foreach (int j in Adj[b.I])
                if (Live(j) && (Touch || !(b.I == TouchA && j == TouchB || b.I == TouchB && j == TouchA))) into.Add(Nodes[j]);
        }
    }

    /// <summary>19. A FAILED CUT ALWAYS SEPARATES (2026-10-17, the user in play: "completely cut but did not separate"). A
    /// 33-member cut cracked across; its stage-0 force hit only a member that had left with an earlier piece ("bridge skipped:
    /// Data not found" - nothing destroyed, no log) and every rule after it counted bridges in the members' box only: none
    /// there, so the watch let it go while the far side hung on by a joint off the cut's section. Played tick by tick as
    /// CutLetsGo on irregular plates, with that member stale (left with a crumb, its force skipped) and a HIDDEN JOINT - one
    /// far block joined to the ship's side away from the plane, which no box rule sees: the old watch (box bridges only)
    /// lets every such cut hang on; the new one asks the game (seeds both sides still in this grid: CutWatch.Joined), keeps
    /// a cut whose force did nothing watched (CutWatch.Again) and walks the far side for what holds it (CutHolders) - every
    /// cut parts, within CutWatch.MaxRounds, never given up - a hidden joint the mounts' test misses too is found by touch
    /// on the last round, after rounds that broke nothing. Without the hidden joint the new watch forces nothing (the seeds
    /// credit the cut's own split - no stub taken).</summary>
    static void CutAlwaysSeparates()
    {
        bool sev0 = CutWatch.Sever;
        // [hidden: 0 none, 1 a joint off the section, 2 one the mounts' test misses too][old 0 / new 1]
        int[,] runs = new int[3, 2], parted = new int[3, 2], forced = new int[3, 2], gaveUp = new int[3, 2], stubsTaken = new int[3, 2], retried = new int[3, 2], holders = new int[3, 2];
        int maxRounds = 0;
        try
        {
            CutWatch.Sever = true;
            for (int hidden = 0; hidden < 3; hidden++)
            for (int alg = 0; alg < 2; alg++)
            for (int shape = 0; shape < 24; shape++)
            {
                int H = 2 + shape % 3, W = 4 + (shape / 3) % 4;
                var rng = new Random(shape * 7919 + 1);
                var sh = new TShip();
                var blk = new int[10, H, W];
                for (int x = 0; x <= 9; x++) for (int y = 0; y < H; y++) for (int z = 0; z < W; z++)
                {
                    if (x >= 2 && x <= 8 && rng.NextDouble() < 0.12) { blk[x, y, z] = -1; continue; }
                    blk[x, y, z] = sh.Block(x, y, z, x == 0 ? 1e9f : SmallMass);
                }
                sh.Build();
                var s = sh.S;
                for (int i = 0; i < s.Count; i++) { s.Integrity[i] = 1f; s.ExtForce[i] = s.Center[i].X > 0.3f ? new Vector3(0, 100f, 0) : Vector3.Zero; s.ExtMoment[i] = Vector3.Zero; }
                s.SolveFree(Vector3.Zero);
                float k = StructureFatigue.SnapUtil * 1.2f / s.MaxUtil;
                for (int i = 0; i < s.Count; i++) s.ExtForce[i] *= k;
                s.SolveFree(Vector3.Zero);
                StructureBend.Adjacency(s, out var a0, out var a1);
                var dmg = new List<(int block, float fraction, bool destroy)>(); var cuts = new List<StructureEval.CutFail>();
                for (int seed = 1; seed <= 6; seed++)
                {
                    s.Seed = seed;
                    StructureEval.Damage(s, null, 0.25f, true, false, StructureEval.BreakUtil, StructureEval.WearPerSecond, 1, dmg, null, cuts);
                    if (cuts.Count != 1 || cuts[0].Release == null || cuts[0].Count < 3) continue;
                    var cut = cuts[0]; int side = (cut.Axis == 0 ? cut.Dir.X : cut.Axis == 1 ? cut.Dir.Y : cut.Dir.Z) > 0f ? 1 : -1;
                    float Ax(Vector3 v) => cut.Axis == 0 ? v.X : cut.Axis == 1 ? v.Y : v.Z;
                    var stubs = new HashSet<int>(cut.Stubs ?? Array.Empty<int>());
                    var entry = new HashSet<int>(); for (int e = 0; e < cut.Count; e++) entry.Add(dmg[cut.First + e].block);
                    bool Leaves(int b) => (Ax(s.Center[b]) - cut.Plane) * side > 0f && !stubs.Contains(b);
                    int far = -1; for (int b = 0; b < s.Count; b++) if (Leaves(b) && (far < 0 || (Ax(s.Center[b]) - Ax(s.Center[far])) * side > 0f)) far = b;
                    if (far < 0) continue;
                    // (the ship: a block of the heavy root column the cut does not break - a jagged step may break the corner one)
                    int anchor = -1; for (int b = 0; b < s.Count; b++) if (s.Center[b].X < 0.3f && !entry.Contains(b) && !stubs.Contains(b) && !Leaves(b)) { anchor = b; break; }
                    if (anchor < 0) continue;
                    // the game's joints: the model's contacts, and (hidden) one off the cut's section - the farthest-out block
                    // tied straight back to the ship's root, no box rule near the plane sees it
                    var adj = new List<int>[s.Count];
                    for (int i = 0; i < s.Count; i++) { adj[i] = new List<int>(); for (int e = a0[i]; e < a0[i + 1]; e++) adj[i].Add(a1[e]); }
                    if (hidden > 0) { adj[far].Add(anchor); adj[anchor].Add(far); }
                    var removed = new bool[s.Count]; var grid = new bool[s.Count];
                    void Grid()
                    {
                        // (the ship: the heavy root column, what of it is left)
                        Array.Clear(grid); var q = new Queue<int>();
                        for (int i = 0; i < s.Count; i++) if (s.Center[i].X < 0.3f && !removed[i]) { grid[i] = true; q.Enqueue(i); }
                        while (q.Count > 0) { int i = q.Dequeue(); foreach (int j in adj[i]) { if (grid[j] || removed[j]) continue; grid[j] = true; q.Enqueue(j); } }
                    }
                    bool Here(int b) => !removed[b] && grid[b];
                    // (the server's box Bridges on the model: pairs across the plane, stubs spared on the jagged rule, and what
                    //  holds a stub from beyond it)
                    int Bridges(bool spare, HashSet<int> into)
                    {
                        int pairs = 0;
                        for (int c = 0; c < s.ContactCount; c++)
                        {
                            s.ContactPlane(c, out int ax, out float pl); if (ax != cut.Axis || MathF.Abs(pl - cut.Plane) > 1e-3f) continue;
                            s.ContactBlocks(c, out int ca, out int cb);
                            if (!Here(ca) || !Here(cb) || spare && (stubs.Contains(ca) || stubs.Contains(cb))) continue;
                            pairs++; into?.Add(ca); into?.Add(cb);
                        }
                        if (spare) foreach (int sb in stubs)
                            {
                                if (!Here(sb)) continue;
                                for (int e = a0[sb]; e < a0[sb + 1]; e++) { int j = a1[e]; if (Here(j) && !stubs.Contains(j) && (Ax(s.Center[j]) - cut.Plane) * side > 0f) { pairs++; into?.Add(j); } }
                            }
                        return pairs;
                    }
                    // the seeds (the server's Seed): the broken blocks' neighbours on each side, not broken, not stubs
                    var farSeeds = new List<int>(); var nearSeeds = new List<int>();
                    foreach (int e in entry) foreach (int j in adj[e])
                        {
                            if (entry.Contains(j) || stubs.Contains(j)) continue;
                            if (Leaves(j)) { if (!farSeeds.Contains(j)) farSeeds.Add(j); } else if (!nearSeeds.Contains(j)) nearSeeds.Add(j);
                        }
                    int Joined()
                    {
                        if (farSeeds.Count == 0 || nearSeeds.Count == 0) return -1;
                        bool f = false, n = false;
                        foreach (int b in farSeeds) if (Here(b)) { f = true; break; }
                        foreach (int b in nearSeeds) if (Here(b)) { n = true; break; }
                        return CutWatch.Joined(true, f, n);
                    }
                    var nodes = new HNode[s.Count]; for (int i = 0; i < s.Count; i++) nodes[i] = new HNode { I = i };
                    var hw = new GraphHolders { Nodes = nodes, Adj = adj, Live = Here, LeavesOf = Leaves, TouchA = hidden == 2 ? far : -1, TouchB = hidden == 2 ? anchor : -1 };
                    // the crack: one torn member is the in-game Battery - split at its face, it stays and LEAVES WITH A CRUMB
                    // (stale: its force skipped, "Data not found")
                    var order = new int[cut.Count]; for (int i = 0; i < cut.Count; i++) order[i] = i;
                    Array.Sort(order, (p, q) => { int r = cut.Release[p].CompareTo(cut.Release[q]); return r != 0 ? r : p.CompareTo(q); });
                    int staleBlock = dmg[cut.First + order[0]].block;
                    var survivors = new List<int>(); var stale = new HashSet<int>();
                    var w = new CutWatch(); w.HasStubs = stubs.Count > 0;
                    int stamp = 0, next = 0; bool armed = false, done = false, splitPending = false, split = false, force = false; float clock = 0f;
                    void Parted() { Grid(); if (!split && !grid[far]) { split = true; splitPending = true; } }
                    w.Begin(stamp);
                    for (int tick = 0; tick < 1200 && !done; tick++)
                    {
                        if (splitPending) { stamp++; splitPending = false; Grid(); }
                        if (armed)
                        {
                            if (alg == 0)
                            {
                                // the old watch (box bridges only)
                                if (w.SplitSince(stamp) && Bridges(w.SpareOnSplit, null) == 0) { done = true; break; }
                                if (w.Expired())
                                {
                                    Grid();
                                    if (w.Stage > 0 && !w.Force(Bridges(w.SpareOnSplit, null))) { done = true; break; }
                                    var set = new HashSet<int>(); foreach (int b in survivors) set.Add(b);
                                    Bridges(w.Spare, set);
                                    int n = 0; foreach (int b in set) { if (stale.Contains(b) || removed[b]) continue; removed[b] = true; n++; if (stubs.Contains(b)) stubsTaken[hidden, alg]++; }
                                    if (n > 0) { force = true; Parted(); }
                                    if (!w.NextStage(stamp, 30)) done = true;
                                }
                            }
                            else
                            {
                                // the new watch (CheckCutSeparated): the seeds first, forced while joined, the holders, again
                                if (w.SplitSince(stamp)) { int j = Joined(); if (CutWatch.Credit(j < 0 ? Bridges(true, null) : 0, j)) { done = true; break; } }
                                if (w.Expired())
                                {
                                    Grid();
                                    int joined = Joined();
                                    if (w.GiveUp(joined)) { gaveUp[hidden, alg]++; done = true; break; }
                                    if (!w.Force(joined < 0 && w.Stage > 0 ? Bridges(true, null) : 0, joined)) { done = true; break; }
                                    var set = new HashSet<int>(); foreach (int b in survivors) if (Here(b)) set.Add(b);
                                    Bridges(joined >= 0 ? !w.Escalate : w.Spare, set);
                                    int n = 0; foreach (int b in set) { if (removed[b] || !grid[b]) continue; removed[b] = true; n++; if (stubs.Contains(b)) stubsTaken[hidden, alg]++; }
                                    if (joined > 0)
                                    {
                                        var hs = new HashSet<HNode>(ReferenceEqualityComparer.Instance);
                                        var seeds = new List<HNode>(); foreach (int b in farSeeds) seeds.Add(nodes[b]);
                                        hw.Touch = w.Escalate;
                                        int h = hw.Find(seeds, hs, 4096); holders[hidden, alg] += h;
                                        var nf = new List<int>(); foreach (var b in hw.Reached) if (!hs.Contains(b) && Here(b.I)) nf.Add(b.I);
                                        if (nf.Count > 0) farSeeds = nf;
                                        foreach (var b in hs) { if (removed[b.I]) continue; removed[b.I] = true; n++; if (stubs.Contains(b.I)) stubsTaken[hidden, alg]++; }
                                        if (n == 0 || w.Rounds > 0) retried[hidden, alg]++;
                                    }
                                    if (n > 0) { force = true; Parted(); }
                                    if (!w.Again(stamp, 30, joined)) done = true;
                                    maxRounds = Math.Max(maxRounds, w.Rounds);
                                }
                            }
                        }
                        if (next < cut.Count)
                        {
                            if (tick > 0) clock += 1f / 60f;
                            while (next < cut.Count && cut.Release[order[next]] <= clock + 1e-4f)
                            {
                                int b = dmg[cut.First + order[next++]].block;
                                // (the stale member: still in the survivors list, gone from this grid - a crumb took it)
                                if (b == staleBlock) { survivors.Add(b); stale.Add(b); }
                                removed[b] = true;
                            }
                            Parted();
                            if (next == cut.Count) { w.Finish(stamp, 30); armed = true; }
                        }
                    }
                    runs[hidden, alg]++;
                    Grid(); if (!grid[far]) parted[hidden, alg]++;
                    if (force) forced[hidden, alg]++;
                }
                s.Seed = 0;
            }
        }
        finally { CutWatch.Sever = sev0; }
        Check(runs[1, 0] >= 40 && parted[1, 0] < runs[1, 0] / 4, $"cut always separates: the old watch (box bridges only) lets a cut held off its section hang on ({parted[1, 0]} of {runs[1, 0]} parted)");
        Check(runs[1, 1] >= 40 && parted[1, 1] == runs[1, 1] && gaveUp[1, 1] == 0 && holders[1, 1] > 0, $"cut always separates: the new watch parts every such cut - the holders found off the section ({parted[1, 1]} of {runs[1, 1]} parted, {holders[1, 1]} holders, {gaveUp[1, 1]} given up)");
        Check(runs[2, 1] >= 40 && parted[2, 1] == runs[2, 1] && gaveUp[2, 1] == 0 && retried[2, 1] >= runs[2, 1] && maxRounds <= CutWatch.MaxRounds, $"cut always separates: a cut whose force broke nothing stays watched and is forced again until it parts ({parted[2, 1]} of {runs[2, 1]} parted, {retried[2, 1]} rounds after an empty or unparted force, at most {maxRounds} of {CutWatch.MaxRounds} rounds, {gaveUp[2, 1]} given up; old: {parted[2, 0]} parted)");
        Check(runs[0, 1] >= 40 && parted[0, 1] == runs[0, 1] && forced[0, 1] == 0 && stubsTaken[0, 1] == 0, $"cut always separates: with nothing hidden the new watch forces nothing - the seeds credit the cut's own split ({parted[0, 1]} of {runs[0, 1]} parted, {forced[0, 1]} forced, {stubsTaken[0, 1]} stubs taken)");
    }
    /// <summary>20. RIGID THRUSTERS (2026-10-17, the user: "Engines shouldn't get damaged by their own structural thrust force,
    /// they're designed to withstand their own loads. It's just if the pylons can't handle it or the wing that things should
    /// break."). (a) An engine on a pylon under a wing, its thrust ramped past what the engine-pylon joint holds, evaluated at 4
    /// Hz (health, solve, damage, what fails removed): the engine never loses health; the pylon breaks at that joint and the
    /// engine leaves whole - off (the old rule) the engine itself broke there. (b) An engine across a slice of a lifted boom:
    /// no utilisation inside it (rigid), and when that cut fails the engine stays whole - its mount across the plane breaks
    /// and it leaves with the tip. (c) The cut's watch: a far-side engine holding on breaks its mount, not itself.</summary>
    static void RigidThrusters()
    {
        bool rig0 = ShipStructure.RigidThrusters;
        try
        {
            // (a) the wing: a fixed root and 6 blocks along x; the pylon under its 4th block, the engine (a 0.5 x 0.5 x 1 m
            //     thruster, thrust along z) under the pylon. The wing and pylon heavy-walled, the engine's shell the capped
            //     functional wall: its joint to the pylon is the weak one
            var sh = new TShip();
            int root = sh.Block(0, 0, 0, 1e9f);
            for (int x = 1; x <= 6; x++) sh.Block(x, 0, 0, 400f);
            int pylon = sh.Block(3, -1, 0, 400f);
            int engine = sh.Block(3, -2, 0, 300f, 1, 1, 2, StructMaterial.AtmosphericThruster);
            int nb = sh.Mass.Count;
            float k1 = 0f;
            (bool engineHurt, bool engineBroke, bool pylonBroke, bool engineFree, float firstBreak) Ramp(bool rigid)
            {
                ShipStructure.RigidThrusters = rigid;
                var alive = new bool[nb]; Array.Fill(alive, true); var health = new float[nb]; Array.Fill(health, 1f);
                var st = BuildAlive(sh, alive, out var orig);
                ShipStructure Mark(ShipStructure s, int[] o) { s.Thruster = new bool[s.Count]; for (int i = 0; i < s.Count; i++) s.Thruster[i] = o[i] == engine; return s; }
                Mark(st, orig);
                // the thrust at which the intact ship's worst joint reaches 1 by the old rule (the scale, both runs), ramped
                // from 0.6 of it by 0.1 a second
                int ei = Array.IndexOf(orig, engine);
                if (k1 == 0f) { st.ExtForce[ei] = new Vector3(0, 0, 1000f); st.SolveFree(Vector3.Zero); k1 = 1000f / st.MaxUtil; }
                var dmg = new List<(int block, float fraction, bool destroy)>(); var cuts = new List<StructureEval.CutFail>();
                bool hurt = false, eBroke = false, pBroke = false; float first = -1f;
                for (float t = 0f; t < 30f; t += 0.25f)
                {
                    ei = Array.IndexOf(orig, engine);
                    int ri = Array.IndexOf(orig, root);
                    for (int i = 0; i < st.Count; i++) { st.Integrity[i] = health[orig[i]]; st.ExtForce[i] = Vector3.Zero; st.ExtMoment[i] = Vector3.Zero; }
                    if (ei >= 0 && st.Piece[ei] == st.Piece[ri]) st.ExtForce[ei] = new Vector3(0, 0, (0.6f + 0.1f * t) * k1);
                    st.SolveFree(Vector3.Zero);
                    StructureEval.Damage(st, null, 0.25f, true, false, StructureEval.BreakUtil, StructureEval.WearPerSecond, 2, dmg, null, cuts);
                    bool any = false;
                    foreach (var (b, frac, destroy) in dmg)
                    {
                        int o = orig[b];
                        if (o == engine) { hurt = true; if (destroy) eBroke = true; }
                        if (o == pylon && destroy) pBroke = true;
                        if (destroy) { alive[o] = false; any = true; if (first < 0f) first = t; } else health[o] -= frac;
                    }
                    if (any) { st = BuildAlive(sh, alive, out orig); Mark(st, orig); }
                }
                ei = Array.IndexOf(orig, engine);
                bool free = ei >= 0 && st.Piece[ei] != st.Piece[Array.IndexOf(orig, root)];
                return (hurt, eBroke, pBroke, free, first);
            }
            var old = Ramp(false);
            Check(old.engineBroke, $"rigid thrusters (old rule): the engine broke on its own thrust at the engine-pylon joint ({(old.engineBroke ? $"at {old.firstBreak:F2} s" : "it did not")})");
            var neu = Ramp(true);
            Check(!neu.engineHurt, "rigid thrusters: the engine never loses health on its own thrust, ramped to 3.6 x its joint's yield");
            Check(neu.pylonBroke && neu.engineFree && neu.firstBreak >= 0f, $"rigid thrusters: the overloaded joint breaks the pylon and the engine leaves whole (pylon broke {neu.pylonBroke}, engine free {neu.engineFree}, at {(0.6f + 0.1f * neu.firstBreak) * k1 / 1000f:F1} kN of thrust; the old rule broke the engine at {(0.6f + 0.1f * old.firstBreak) * k1 / 1000f:F1} kN)");

            // (b) a boom along x off a fixed root, lifted at its tip; an engine under blocks 3-5 (cells x 6..10), across the
            //     slices 3|4 and 4|5; block 4 light (thin walls) so those slices are the weak ones
            var bm = new TShip();
            int broot = bm.Block(0, 0, 0, 1e9f);
            var boom = new int[8];
            for (int x = 1; x <= 7; x++) boom[x] = bm.Block(x, 0, 0, x == 4 ? SmallMass : 400f);
            int eng = bm.Cells(600f, StructMaterial.IonThruster, (new Vector3I(6, -2, 0), new Vector3I(10, -1, 1)));
            int nb2 = bm.Mass.Count;
            var all = new bool[nb2]; Array.Fill(all, true);
            ShipStructure Boom(bool rigid, out int[] o)
            {
                ShipStructure.RigidThrusters = rigid;
                var s = BuildAlive(bm, all, out o);
                s.Thruster = new bool[s.Count]; for (int i = 0; i < s.Count; i++) s.Thruster[i] = o[i] == eng;
                for (int i = 0; i < s.Count; i++) { s.Integrity[i] = 1f; s.ExtForce[i] = o[i] == boom[7] ? new Vector3(0, 1000f, 0) : Vector3.Zero; }
                s.SolveFree(Vector3.Zero);
                return s;
            }
            var b0 = Boom(false, out var o0);
            int e0 = Array.IndexOf(o0, eng);
            float engOld = b0.BlockUtil[e0], worstOld = b0.MaxUtil;
            var b1 = Boom(true, out var o1);
            int e1 = Array.IndexOf(o1, eng);
            Check(engOld > 0f && b1.BlockUtil[e1] == 0f, $"rigid thrusters: no utilisation inside the engine across the boom's slices (old {engOld / worstOld:F2} of the worst, now {b1.BlockUtil[e1]:F2})");
            // the engine's slice 3|4 (x = 1.875 m) overloaded past the snap: it fails whole - the engine stays whole, its mount
            // across the plane (boom block 3) breaks, and it leaves with the tip
            {
                float k = StructureFatigue.SnapUtil * 1.3f / b1.MaxUtil;
                for (int i = 0; i < b1.Count; i++) b1.ExtForce[i] *= k;
                b1.SolveFree(Vector3.Zero);
                var dmg = new List<(int block, float fraction, bool destroy)>(); var cuts = new List<StructureEval.CutFail>();
                StructureEval.Damage(b1, null, 0.25f, true, false, StructureEval.BreakUtil, StructureEval.WearPerSecond, 1, dmg, null, cuts);
                var gone = new HashSet<int>(); bool engHit = false;
                foreach (var (b, _, destroy) in dmg) { if (destroy) gone.Add(o1[b]); if (o1[b] == eng) engHit = true; }
                bool acrossEngine = cuts.Count == 1 && cuts[0].Axis == 0 && MathF.Abs(cuts[0].Plane - 1.875f) < 1e-3f;
                var left = new bool[nb2]; for (int i = 0; i < nb2; i++) left[i] = !gone.Contains(i);
                var after = BuildAlive(bm, left, out var oa);
                int ea = Array.IndexOf(oa, eng), ra = Array.IndexOf(oa, broot), ta = Array.IndexOf(oa, boom[7]);
                bool parted = ea >= 0 && ta >= 0 && after.Piece[ea] != after.Piece[ra] && after.Piece[ea] == after.Piece[ta];
                Check(acrossEngine && !engHit && gone.Contains(boom[3]) && parted, $"rigid thrusters: the slice across the engine fails whole ({(cuts.Count > 0 ? $"axis {cuts[0].Axis} at {cuts[0].Plane:F3} m" : "none")}) - the engine untouched ({!engHit}), its mount boom[3] broken ({gone.Contains(boom[3])}), the engine gone with the tip ({parted}; {gone.Count} blocks: {string.Join(" ", gone)})");
            }

            // (c) the watch: on the far side T1 - E (an engine, holding on to the ship's M) - and T2 holding on to M2: the
            //     engine's mount M breaks, not E; T2 itself as before
            {
                var adj = new List<int>[5]; for (int i = 0; i < 5; i++) adj[i] = new List<int>();
                void J(int a, int b) { adj[a].Add(b); adj[b].Add(a); }
                const int E = 0, M = 1, T1 = 2, T2 = 3, M2 = 4;
                J(T1, E); J(E, M); J(T1, T2); J(T2, M2);
                var nodes = new HNode[5]; for (int i = 0; i < 5; i++) nodes[i] = new HNode { I = i };
                var hw = new GraphHolders { Nodes = nodes, Adj = adj, Live = _ => true, LeavesOf = i => i == E || i == T1 || i == T2, KeptOf = i => i == E };
                var into = new HashSet<HNode>(ReferenceEqualityComparer.Instance);
                int h = hw.Find(new[] { nodes[T1] }, into, 100);
                Check(h == 2 && into.Contains(nodes[M]) && into.Contains(nodes[T2]) && !into.Contains(nodes[E]), $"rigid thrusters: the watch breaks a far-side engine's mount, never the engine ({h} holders: {string.Join(" ", into.Select(x => x.I))})");
            }
        }
        finally { ShipStructure.RigidThrusters = rig0; }
    }

    /// <summary>COMPONENT LOADS AT THEIR BLOCKS (StructureEval.ComponentLoadsAtBlocks, 2026-10-17, the user: "yes do it" - each
    /// block component's air on its own block, like the wings'). (a) a wing - six 0.5 m blocks off a fuselage - with a
    /// control surface hung at the trailing edge of its 5th block, deflected: 20 kN of lift on the surface, the wing's own
    /// lift over its cells, the hull's drag. (b) an airbrake hung on a pylon under the fuselage, fully open: 30 kN of drag.
    /// Before, the surface's lift and the airbrake's drag were spread over the windward faces; now each loads its own
    /// mount - the hinge joint, the pylon joint. Either way the ship's total force and moment are the same (to float
    /// tolerance); a component whose block is not in the structure falls back to the spread (counted); an extra block load
    /// (PublishBlockLoad) adds to the total at its block.</summary>
    static void ComponentLoads()
    {
        bool on0 = StructureEval.ComponentLoadsAtBlocks;
        try
        {
            var travel = new Vector3(0, 0, -1);   // (the ship flies along -z: the air comes from -z)
            (Vector3 F, Vector3 M) Totals(ShipStructure s)
            {
                Vector3 F = Vector3.Zero, M = Vector3.Zero;
                for (int i = 0; i < s.Count; i++) { F += s.ExtForce[i]; M += Vector3.Cross(s.Center[i] - s.Com, s.ExtForce[i]) + s.ExtMoment[i]; }
                return (F, M);
            }
            float JointUtil(ShipStructure s, int a, int b)
            {
                float u = 0f;
                for (int k = 0; k < s.ContactCount; k++) { s.ContactBlocks(k, out int x, out int y); if ((x == a && y == b) || (x == b && y == a)) u = MathF.Max(u, s.ContactUtil[k]); }
                return u;
            }
            (float ratio, float u0, float u1, Vector3 F, Vector3 M, Vector3 F0, Vector3 M0) Run(TShip sh, StructureEval.Loads L, int a, int b)
            {
                var s = sh.S;
                StructureEval.ComponentLoadsAtBlocks = false;
                StructureEval.Apply(s, sh.H, L); var (f0, m0) = Totals(s);
                s.SolveFree(Vector3.Zero); float u0 = JointUtil(s, a, b);
                StructureEval.ComponentLoadsAtBlocks = true;
                StructureEval.Apply(s, sh.H, L); var (f1, m1) = Totals(s);
                s.SolveFree(Vector3.Zero); float u1 = JointUtil(s, a, b);
                return (u1 / MathF.Max(u0, 1e-12f), u0, u1, f1, m1, f0, m0);
            }
            bool Same(Vector3 x, Vector3 y, float scale) => (x - y).Length() <= 1e-5f * scale + 1e-3f;

            // (a) the fuselage (2 x 2 x 6 blocks, heavy), the wing along +x off its last row (y = 0, z = 5, one block chord);
            //     the control surface at the wing's trailing edge (z = 6, behind its 5th block) - the hinge's plane cuts
            //     nothing else (a plane through the fuselage too gives the surface's side's load to the member nearest each
            //     block on the lighter side - the slices' rule, ShipStructure.Sections - not to the hinge alone)
            {
                var sh = new TShip();
                for (int z = 0; z < 6; z++) for (int x = 0; x < 2; x++) for (int y = 0; y < 2; y++) sh.Block(x, y, z, 400f);
                var wing = new int[7];
                for (int x = 2; x <= 7; x++) wing[x - 1] = sh.Block(x, 0, 5, SmallMass);
                int flap = sh.Block(6, 0, 6, SmallMass);   // (behind wing[5], x = 6)
                sh.Build(heat: true);
                var s = sh.S; sh.H.Pass(travel);
                var L = new StructureEval.Loads { AeroCom = s.Com, AirVel = travel * 100f, CellSize = 0.25f, CellOffset = 0.5f };
                // the wing's lift over its cells (the cells' centres: offset 0.5)
                var cells = new List<Vector3I>(); for (int x = 4; x <= 15; x++) for (int z = 10; z <= 11; z++) cells.Add(new Vector3I(x, 0, z));
                var wingF = new Vector3(0, 8000f, 400f);
                L.WingCells.Add(cells); L.WingF.Add(wingF);
                Vector3 wingM = Vector3.Zero; foreach (var c in cells) wingM += Vector3.Cross(new Vector3((c.X + 0.5f) * 0.25f, (c.Y + 0.5f) * 0.25f, (c.Z + 0.5f) * 0.25f) - s.Com, wingF / cells.Count);
                // the surface's lift and drag at its centre of pressure (a quarter chord into it), a small hinge moment of its own
                var flapF = new Vector3(0, 20000f, 900f); var flapP = s.Center[flap] + new Vector3(0, 0, -0.125f); var flapT = new Vector3(-150f, 0, 0);
                L.CompF.Add(flapF); L.CompP.Add(flapP); L.CompT.Add(flapT); L.CompAt.Add(s.Center[flap]);
                var hullF = new Vector3(0, 0, 1500f);
                L.AeroF = hullF + wingF + flapF;
                L.AeroT = wingM + Vector3.Cross(flapP - s.Com, flapF) + flapT;
                int placed0 = StructureEval.CompPlaced;
                var r = Run(sh, L, flap, wing[5]);
                float scale = L.AeroF.Length() * 3f;
                Console.WriteLine($"   component loads (a): the control surface's hinge joint utilisation {r.u0:F3} spread -> {r.u1:F3} at its block (x{r.ratio:F1}); totals differ {(r.F - r.F0).Length():E2} N, {(r.M - r.M0).Length():E2} N m");
                Check(Same(r.F, r.F0, scale) && Same(r.M, r.M0, scale), $"component loads (a): the total force and moment unchanged (force {r.F.Length():F3} vs {r.F0.Length():F3} N, moment {r.M.Length():F3} vs {r.M0.Length():F3} N m)");
                Check(Same(r.F, L.AeroF, scale), $"component loads (a): the blocks carry the air's whole force ({r.F.Length():F1} of {L.AeroF.Length():F1} N)");
                Check(r.ratio > 3f, $"component loads (a): the control surface's hinge joint carries its lift - utilisation x{r.ratio:F1} over the spread");
                Check(StructureEval.CompPlaced > placed0, "component loads (a): the surface was put on its block");
                // its block is not in the structure (a point in no block): the spread takes it, counted, totals the same
                int missed0 = StructureEval.CompMissed;
                L.CompAt[0] = new Vector3(50f, 50f, 50f);
                var r2 = Run(sh, L, flap, wing[5]);
                Check(StructureEval.CompMissed > missed0 && MathF.Abs(r2.ratio - 1f) < 1e-4f && Same(r2.F, r2.F0, scale) && Same(r2.M, r2.M0, scale), $"component loads (a): a component off the structure falls back to the spread (missed {StructureEval.CompMissed - missed0}, utilisation x{r2.ratio:F3})");
                L.CompAt[0] = s.Center[flap];
                // an extra block load (PublishBlockLoad): on top of the air, at its block
                var extra = new Vector3(0, 0, -5000f);
                L.LoadBlock.Add(flap); L.LoadF.Add(extra); L.LoadP.Add(s.Center[flap]); L.LoadT.Add(Vector3.Zero); L.LoadEngine.Add(false);
                StructureEval.Apply(s, sh.H, L); var (fe, _) = Totals(s);
                Check(Same(fe, L.AeroF + extra, scale), $"component loads (a): an extra block load adds to the total ({fe.Z:F1} N along z, want {(L.AeroF + extra).Z:F1})");
                L.LoadEngine[0] = true; StructureEval.ThrustLoads = false;
                try { StructureEval.Apply(s, sh.H, L); var (fn, _) = Totals(s); Check(Same(fn, L.AeroF, scale), "component loads (a): an engine's extra load is off with Structure.ThrustLoads"); }
                finally { StructureEval.ThrustLoads = true; }
            }

            // (b) the airbrake (one 0.5 m block) on a pylon under a 1.5 x 1.5 m fuselage's 4th row, fully open: 30 kN of drag
            {
                var sh = new TShip();
                for (int z = 0; z < 6; z++) for (int x = 0; x < 3; x++) for (int y = 0; y < 3; y++) sh.Block(x, y, z, 400f);
                int pylon = sh.Block(1, -1, 3, SmallMass);
                int brake = sh.Block(1, -2, 3, SmallMass);
                sh.Build(heat: true);
                var s = sh.S; sh.H.Pass(travel);
                var L = new StructureEval.Loads { AeroCom = s.Com, AirVel = travel * 100f, CellSize = 0.25f, CellOffset = 0.5f };
                var brakeF = new Vector3(0, 0, 30000f); var brakeP = s.Center[brake];
                L.CompF.Add(brakeF); L.CompP.Add(brakeP); L.CompT.Add(Vector3.Zero); L.CompAt.Add(brakeP);
                var hullF = new Vector3(0, 0, 2000f);
                L.AeroF = hullF + brakeF; L.AeroT = Vector3.Cross(brakeP - s.Com, brakeF);
                var r = Run(sh, L, pylon, brake);
                float scale = L.AeroF.Length() * 3f;
                Console.WriteLine($"   component loads (b): the airbrake's pylon joint utilisation {r.u0:F3} spread -> {r.u1:F3} at its block (x{r.ratio:F1}); totals differ {(r.F - r.F0).Length():E2} N, {(r.M - r.M0).Length():E2} N m");
                Check(Same(r.F, r.F0, scale) && Same(r.M, r.M0, scale), $"component loads (b): the total force and moment unchanged (differ {(r.F - r.F0).Length():E2} N, {(r.M - r.M0).Length():E2} N m)");
                Check(r.ratio > 2f, $"component loads (b): the airbrake's mount joint carries its drag - utilisation x{r.ratio:F1} over the spread");
            }
        // (c) the world's drag / lift multipliers (AeroTuning): each component scaled as a part of the total
        //     (AeroTuning.ScalePart) - the scaled parts add up to the scaled total, force and moment
        {
            float dm0 = AeroTuning.DragMultiplier, lm0 = AeroTuning.LiftMultiplier;
            try
            {
                var rnd = new Random(17); float worstF = 0f, worstM = 0f;
                Vector3 R(float k) => new Vector3((float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f) * k;
                for (int trial = 0; trial < 200; trial++)
                {
                    AeroTuning.DragMultiplier = 0.2f + 2f * (float)rnd.NextDouble(); AeroTuning.LiftMultiplier = 0.2f + 2f * (float)rnd.NextDouble();
                    var com = R(4f); var vel = trial % 10 == 0 ? Vector3.Zero : R(400f);
                    int nc = 1 + rnd.Next(6);
                    var f = new Vector3[nc]; var p = new Vector3[nc]; var m = new Vector3[nc];
                    Vector3 F = Vector3.Zero, T = Vector3.Zero;
                    for (int i = 0; i < nc; i++) { f[i] = R(40000f); p[i] = R(20f); m[i] = R(5000f); F += f[i]; T += Vector3.Cross(p[i] - com, f[i]) + m[i]; }
                    float tk = AeroTuning.TorqueScale(F, vel);
                    var Fs = F; var Ts = T; AeroTuning.Scale(ref Fs, ref Ts, vel);
                    Vector3 sf = Vector3.Zero, sm = Vector3.Zero;
                    for (int i = 0; i < nc; i++) { var fi = f[i]; var mi = m[i]; AeroTuning.ScalePart(ref fi, ref mi, p[i], com, tk, vel); sf += fi; sm += Vector3.Cross(p[i] - com, fi) + mi; }
                    worstF = MathF.Max(worstF, (sf - Fs).Length() / MathF.Max(Fs.Length(), 1f));
                    worstM = MathF.Max(worstM, (sm - Ts).Length() / MathF.Max(Ts.Length(), 1f));
                }
                Console.WriteLine($"   component loads (c): the multipliers on the parts vs the total - worst relative difference force {worstF:E1}, moment {worstM:E1} (200 random sets)");
                Check(worstF < 1e-5f && worstM < 1e-5f, $"component loads (c): the scaled components add up to the scaled total (force {worstF:E1}, moment {worstM:E1} relative)");
            }
            finally { AeroTuning.DragMultiplier = dm0; AeroTuning.LiftMultiplier = lm0; }
        }
        }
        finally { StructureEval.ComponentLoadsAtBlocks = on0; }
    }


    static string SimDirS([System.Runtime.CompilerServices.CallerFilePath] string here = "") => System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(here), "..", "..", "Scripts", "Simulation"));

    /// <summary>LOADS THE STRUCTURE COULD NOT SEE (2026-10-17): (a) a ROTOR STRIKE's one-tick impulse, gathered per block and taken
    /// once by the next snapshot (ImpulseWindow, PublishBlockImpulse) - the 4/s sampler sees every strike exactly once at its
    /// honest peak (J over the collisions' crush time), where J / dt published as a latest value (two ticks) would be missed
    /// most of the time; a blade grinding on loads by its mean; moments exact. (b) a hard strike at full rpm can damage the
    /// mast a rotor stands on; a light touch cannot. (c) a SOLID ROCKET's thrust (out of the thruster cache) counted once - the
    /// thrusters' shares of the game's thrust less the rocket's, plus the rocket at its block; off with ThrustLoads. (d) a
    /// ROTOR's cyclic and reaction published at the hub keep the ship's total force and moment about the centre of mass.</summary>
    static void ImpulseLoads()
    {
        const float Dt = 1f / 60f, Crush = 0.05f;   // (the sim step; StructureContactLoads.CrushSeconds - the server passes it, checked below)
        const int Period = 15;                      // (StructureLoads.Rate 4/s: a snapshot every 15 ticks, its window 0.25 s)
        float window = Period * Dt;
        var J = new Vector3(0, 0, -6562f);          // (HelicopterRotor_150, rock, tip, full rpm: docs/PROPULSOR_SPECS.md)
        var P = new Vector3(7.5f, 3f, 0f);
        // (a) one strike at every phase of the sampler: seen by exactly one snapshot, at J / crush
        int wrong = 0, naiveMissed = 0; float worstErr = 0f;
        for (int at = 0; at < 2 * Period; at++)
        {
            var w = new ImpulseWindow(); int seen = 0; Vector3 got = Vector3.Zero; int naiveSeen = 0;
            for (int tick = 0; tick < 6 * Period; tick++)
            {
                if (tick == at) w.Add(J, P, Vector3.Zero, tick);
                if (tick % Period == Period - 1)
                {
                    if (w.Held(window, Crush, out var f, out var p, out var t)) { seen++; got = f; worstErr = MathF.Max(worstErr, (Vector3.Cross(p, f) + t - Vector3.Cross(P, J) / Crush).Length()); }
                    w.Clear();
                    // (the alternative: J / dt published for its tick, latest value - in a snapshot while at most 2 ticks old)
                    if (tick - at >= 0 && tick - at <= 2) naiveSeen++;
                }
            }
            if (seen != 1 || (got - J / Crush).Length() > 1e-2f * (J / Crush).Length()) wrong++;
            if (naiveSeen == 0) naiveMissed++;
        }
        Console.WriteLine($"   impulse loads (a): a strike seen once at J / crush = {(J / Crush).Length() / 1000f:F0} kN at every phase of the 4/s sampler; J / dt for one tick would have been missed at {naiveMissed} of {2 * Period} phases");
        Check(wrong == 0, $"impulse loads (a): every strike taken by exactly one snapshot at J / CrushSeconds ({wrong} of {2 * Period} phases wrong)");
        Check(worstErr < 1f, $"impulse loads (a): the strike's moment kept (worst {worstErr:E2} N m)");
        Check(naiveMissed > Period, $"impulse loads (a): a one-tick J / dt latest value is missed by the sampler at most phases ({naiveMissed} of {2 * Period}) - the reason for the window");
        // two strikes in one tick add; two in different ticks are two hits (the peak, never their sum over the crush)
        {
            var w = new ImpulseWindow(); w.Add(J, P, Vector3.Zero, 3); w.Add(J, P, Vector3.Zero, 3);
            w.Held(window, Crush, out var f2, out _, out _);
            var v = new ImpulseWindow(); v.Add(J, P, Vector3.Zero, 3); v.Add(J, P, Vector3.Zero, 8);
            v.Held(window, Crush, out var f3, out _, out _);
            Check((f2 - 2f * J / Crush).Length() < 1f && (f3 - J / Crush).Length() < 1f, $"impulse loads (a): one tick's strikes add ({f2.Length():F0} N), two ticks' are two hits ({f3.Length():F0} N, not {(2f * J / Crush).Length():F0})");
        }
        // a blade grinding: every 5 ticks (a hit wins: J/crush > 3J/window) and every tick (the push kept up: 15J/window)
        {
            var w5 = new ImpulseWindow(); for (int k = 0; k < Period; k += 5) w5.Add(J, P, Vector3.Zero, k);
            w5.Held(window, Crush, out var g5, out _, out _);
            var w1 = new ImpulseWindow(); for (int k = 0; k < Period; k++) w1.Add(J, P, Vector3.Zero, k);
            w1.Held(window, Crush, out var g1, out _, out _);
            Check((g5 - J / Crush).Length() < 1f && (g1 - J * Period / window).Length() < 1f, $"impulse loads (a): grinding every 5 ticks loads its peak ({g5.Length():F0} N), every tick its mean ({g1.Length():F0} N = sum / window)");
            // strikes all round the disc: the mean's moment about the origin exact
            var wr = new ImpulseWindow(); Vector3 sumM = Vector3.Zero, sumJ = Vector3.Zero;
            for (int k = 0; k < Period; k++)
            {
                float a = k * 0.7f; var pk = new Vector3(7.5f * MathF.Cos(a), 3f, 7.5f * MathF.Sin(a)); var jk = new Vector3(-MathF.Sin(a), 0f, MathF.Cos(a)) * 6562f;
                wr.Add(jk, pk, Vector3.Zero, k); sumM += Vector3.Cross(pk, jk); sumJ += jk;
            }
            wr.Held(window, Crush, out var fr, out var pr, out var tr);
            bool meanBranch = (sumJ / window).LengthSquared() > (wr.PeakJ / Crush).LengthSquared();
            Vector3 wantF = meanBranch ? sumJ / window : wr.PeakJ / Crush, wantM = meanBranch ? sumM / window : wr.PeakM / Crush;
            Check((fr - wantF).Length() < 1f && (Vector3.Cross(pr, fr) + tr - wantM).Length() < 1e-3f * wantM.Length() + 1f,
                  $"impulse loads (a): a disc's strikes all round - the held force and its moment exact ({(meanBranch ? "mean" : "peak")}, {wantM.Length():F0} N m)");
            // a break: the rotor's angular momentum as a torque over the crush
            var wb = new ImpulseWindow(); var Lb = new Vector3(0, 147656f, 0); wb.Add(Vector3.Zero, P, Lb, 4);
            Check(wb.Held(window, Crush, out var fb, out _, out var tb) && fb == Vector3.Zero && (tb - Lb / Crush).Length() < 1f, $"impulse loads (a): a break's angular momentum loads L / crush ({tb.Length() / 1e6f:F2} MN m)");
            var e = new ImpulseWindow(); Check(!e.Held(window, Crush, out _, out _, out _), "impulse loads (a): an empty window loads nothing");
        }

        // (b) a hard strike damages the rotor's mast; a light touch does not. A fuselage (3 x 2 x 6 heavy blocks), a mast of two small
        //     blocks on its top, the rotor (a 1.5 m block, rigid as the game's thruster) on the mast; the strike at the tip, 7.5 m out
        {
            float worstHard = 0f, worstSoft = 0f; bool broke = false, softBroke = false;
            foreach (bool hard in new[] { true, false })
            {
                var sh = new TShip();
                for (int z = 0; z < 6; z++) for (int x = 0; x < 3; x++) for (int y = 0; y < 2; y++) sh.Block(x, y, z, 400f);
                int m1 = sh.Block(1, 2, 3, SmallMass), m2 = sh.Block(1, 3, 3, SmallMass);
                int rotor = sh.Block(0, 4, 2, 600f, 3, 1, 3, StructMaterial.AtmosphericThruster);
                sh.Build(heat: true);
                var s = sh.S; sh.H.Pass(new Vector3(0, 0, -1));
                s.Thruster = new bool[s.Count]; s.Thruster[rotor] = true;
                var L = new StructureEval.Loads { AeroCom = s.Com, CellSize = 0.25f, CellOffset = 0.5f };
                float v = hard ? 200f : 2f;   // (the blade's speed at the hit: J is linear in it - RotorStrike.StrikeImpulse)
                var w = new ImpulseWindow();
                var hub = s.Center[rotor]; var at = hub + new Vector3(7.5f, 0f, 0f);
                w.Add(J * (v / 200f), at, Vector3.Zero, 0);
                w.Held(window, Crush, out var f, out var p, out var t);
                L.LoadBlock.Add(rotor); L.LoadF.Add(f); L.LoadP.Add(p); L.LoadT.Add(t); L.LoadEngine.Add(false);
                for (int i = 0; i < s.Count; i++) s.Integrity[i] = 1f;
                StructureEval.Apply(s, sh.H, L);
                s.SolveFree(Vector3.Zero);
                float u = s.MaxUtil;
                var dmg = new List<(int block, float fraction, bool destroy)>(); var cuts = new List<StructureEval.CutFail>();
                StructureEval.Damage(s, null, window, true, false, StructureEval.BreakUtil, StructureEval.WearPerSecond, 2, dmg, null, cuts);
                bool any = dmg.Any(d => d.destroy || d.fraction > 0f) || cuts.Count > 0;
                if (hard) { worstHard = u; broke = any; } else { worstSoft = u; softBroke = any; }
            }
            Console.WriteLine($"   impulse loads (b): the mast's worst utilisation - a full-rpm rock strike {worstHard:F2} (damage {broke}), a 2 m/s touch {worstSoft:F4} (damage {softBroke})");
            Check(worstHard >= StructureEval.BreakUtil && broke, $"impulse loads (b): a hard strike at the tip damages the structure (utilisation {worstHard:F2})");
            Check(worstSoft < 1f && !softBroke, $"impulse loads (b): a light touch does not (utilisation {worstSoft:F4})");
        }

        // (c) a lit solid rocket beside two thrusters: the thrusters' shares are the game's thrust LESS the rocket's (ThrustTorque:
        //     gameF -= solidF), the rocket's whole thrust (base + steering: ThrustNow) at its own block - counted once
        {
            var sh = new TShip();
            for (int z = 0; z < 4; z++) for (int x = 0; x < 2; x++) sh.Block(x, 0, z, 400f);
            int t1 = sh.Block(0, -1, 0, 300f, 1, 1, 1, StructMaterial.AtmosphericThruster), t2 = sh.Block(1, -1, 3, 300f, 1, 1, 1, StructMaterial.AtmosphericThruster);
            int noz = sh.Block(0, 1, 1, 300f, 1, 1, 1, StructMaterial.AtmosphericThruster);
            sh.Build(heat: true);
            var s = sh.S; sh.H.Pass(new Vector3(0, 0, -1));
            var up = Vector3.UnitY; float rocketBase = 40000f, steer = -6000f, thrusters = 100000f;
            float rocketNow = rocketBase + steer;          // (ThrustNow: its override as steered - the game's thrust on it)
            float gameF = thrusters + rocketNow;           // (the game's thrust holds both)
            float shared = gameF - rocketNow;              // (ThrustTorque: gameF -= solidF - the thrusters' shares)
            var L = new StructureEval.Loads { AeroCom = s.Com, CellSize = 0.25f, CellOffset = 0.5f };
            L.ThrBlock.Add(t1); L.ThrF.Add(up * shared * 0.5f); L.ThrF0.Add(up * shared * 0.5f);
            L.ThrBlock.Add(t2); L.ThrF.Add(up * shared * 0.5f); L.ThrF0.Add(up * shared * 0.5f);
            L.LoadBlock.Add(noz); L.LoadF.Add(up * rocketNow); L.LoadP.Add(s.Center[noz]); L.LoadT.Add(Vector3.Zero); L.LoadEngine.Add(true);
            StructureEval.Apply(s, sh.H, L);
            Vector3 F = Vector3.Zero; for (int i = 0; i < s.Count; i++) F += s.ExtForce[i];
            Check(MathF.Abs(F.Y - gameF) < 1f, $"impulse loads (c): the ship carries the game's whole thrust once - thrusters {thrusters / 1000f:F0} kN + rocket {rocketNow / 1000f:F0} kN (base + steering) = {F.Y / 1000f:F1} kN of {gameF / 1000f:F1}");
            bool tl = StructureEval.ThrustLoads; StructureEval.ThrustLoads = false;
            try { StructureEval.Apply(s, sh.H, L); F = Vector3.Zero; for (int i = 0; i < s.Count; i++) F += s.ExtForce[i]; Check(F.Length() < 1f, $"impulse loads (c): the rocket's thrust is an engine's - off with ThrustLoads ({F.Length():F1} N)"); }
            finally { StructureEval.ThrustLoads = tl; }
        }

        // (d) a rotor's cyclic and reaction: applied to the body at the centre of mass as (force, torque about com); published at
        //     the hub with torque = given - (hub - com) x force + reaction (RotorCyclic.PublishLoads) - the ship's moment the same
        {
            var sh = new TShip();
            for (int z = 0; z < 4; z++) for (int x = 0; x < 2; x++) sh.Block(x, 0, z, 400f);
            int rotor = sh.Block(0, 1, 1, 600f, 2, 1, 2, StructMaterial.AtmosphericThruster);
            sh.Build(heat: true);
            var s = sh.S; sh.H.Pass(new Vector3(0, 0, -1));
            var com = s.Com; var hub = s.Center[rotor] + Vector3.UnitY * 0.4f;
            var force = new Vector3(1200f, 0f, -300f); var given = new Vector3(-2500f, 0f, 9000f); var reaction = new Vector3(0f, 4000f, 0f);
            var L = new StructureEval.Loads { AeroCom = com, CellSize = 0.25f, CellOffset = 0.5f };
            L.LoadBlock.Add(rotor); L.LoadF.Add(force); L.LoadP.Add(hub); L.LoadT.Add(given + reaction - Vector3.Cross(hub - com, force)); L.LoadEngine.Add(true);
            StructureEval.Apply(s, sh.H, L);
            Vector3 F = Vector3.Zero, M = Vector3.Zero;
            for (int i = 0; i < s.Count; i++) { F += s.ExtForce[i]; M += Vector3.Cross(s.Center[i] - com, s.ExtForce[i]) + s.ExtMoment[i]; }
            Check((F - force).Length() < 1e-2f && (M - (given + reaction)).Length() < 1e-1f, $"impulse loads (d): a rotor's cyclic and reaction at its hub - the ship's force and moment as applied (moment off by {(M - (given + reaction)).Length():E2} N m)");
        }

        // the server's wiring (source): the window and the crush passed, impulses taken once, an engine block not rebuilt, the balance
        {
            string sim = SimDirS();
            string bl = System.IO.File.ReadAllText(System.IO.Path.Combine(sim, "Structure", "Server", "StructureServer.BlockLoads.cs"));
            string sv = System.IO.File.ReadAllText(System.IO.Path.Combine(sim, "Structure", "Server", "StructureServer.cs"));
            string ct = System.IO.File.ReadAllText(System.IO.Path.Combine(sim, "Structure", "Server", "StructureContacts.cs"));
            Check(sv.Contains("SnapBlockLoads(L, index, evalDt);") && bl.Contains("float crush = StructureContactLoads.CrushSeconds;") && bl.Contains("e.W.Held(window, crush,") && bl.Contains("_blockImpulses.Clear();"),
                  "impulse loads: the snapshot takes the impulses over its own window and the collisions' crush time, once (cleared)");
            Check(bl.Contains("block.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement.ThrusterComponent>() != null"), "impulse loads: an engine with the game's ThrusterComponent published as one costs no rebuild");
            Check(ct.Contains("Vector3 knownJ = TakeKnownImpulse();") && ct.Contains("x0 += knownJ / dt;"), "impulse loads: the contact balance takes the known impulses (cyclic, strikes) off - not read as the wheels' or plates'");
        }
    }

    static int Main()
    {
        Classes();
        ShipStructure.Strength = 1f;   // (the formulas below at the material's own yield)
        TreeExact();
        Cantilever(); Balance(); Composite(); Whip(); GyroWholeShip();
        RealShapes(); SlabBuckling(); Mounts(); ManyPieces(); Bends(); Fatigue(); NeckFailure();
        TensionCompression(); BucklingFatigue(); Jagged(); CrackSeparates(); CutLetsGo(); CutAlwaysSeparates(); RigidThrusters();
        BalanceUnderEdits(); EntryBraking(); ComponentLoads(); ImpulseLoads();
        HeatBalance(); BluntVsSharp(); Cooling(); ScorchThresholds();
        Console.WriteLine($"StructureTests: {_checks - _fails} passed, {_fails} failed");
        return _fails == 0 ? 0 : 1;
    }
}
