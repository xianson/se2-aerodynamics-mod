using AeroMod;

// Wing structural strain (Lift/WingStrain.cs), offline: the literal mod source on box wings built the way the wing
// detector reports them (both skins' cells, in its cells: 0.5 m on large grids, 0.25 m on small), and their blocks as
// the game gives them (occupied boxes in 0.25 m cells).
//  1. the calibration point: a light-armour wing 4 blocks out, 1 block chord and deep, snaps (strain 1.5) at 40 kPa,
//     on a large grid and a small one alike, from the wing's own cells and from its blocks;
//  2. strain goes with the load, and with the span squared (8 blocks: x4); a shallower root is weaker;
//  3. heavy armour is 4x stronger;
//  4. the root is the end nearest the centre of mass: right wing, left wing, a fin, a wing through the fuselage (two
//     cantilevers; with the fuselage's blocks, it breaks where it leaves the fuselage);
//  5. wear and the break threshold.
static class Program
{
    static int _fails, _checks;
    static void Check(bool ok, string what) { _checks++; if (!ok) { _fails++; Console.WriteLine("FAIL " + what); } }
    static bool Near(float a, float b, float tol = 0.01f) => MathF.Abs(a - b) <= tol * MathF.Max(1e-6f, MathF.Abs(b));

    /// <summary>A box wing as the detector reports it: span along spanAxis (from s0, n cells), chord along +Z (+X for a
    /// span along Z), normal +Y (+X for a span along Y), cells on both skins (normal = 0 and thick - 1). Cell geometry:
    /// size h, offset off.</summary>
    static LiftingSurface BoxWing(Vector3I spanAxis, int s0, int spanCells, int chordCells, int thickCells, float h, float off)
    {
        var chordI = spanAxis.Z != 0 ? new Vector3I(1, 0, 0) : new Vector3I(0, 0, 1);
        var normalI = spanAxis.Y != 0 ? new Vector3I(1, 0, 0) : new Vector3I(0, 1, 0);
        var cells = new List<Vector3I>();
        for (int s = s0; s < s0 + spanCells; s++)
            for (int c = 0; c < chordCells; c++)
                for (int t = 0; t < thickCells; t += Math.Max(1, thickCells - 1))
                    cells.Add(spanAxis * s + chordI * c + normalI * t);
        static Vector3 V(Vector3I v) => new Vector3(v.X, v.Y, v.Z);
        float spanM = spanCells * h, chordM = chordCells * h;
        var centroid = Vector3.Zero;
        foreach (var c in cells) centroid += new Vector3(c.X + off, c.Y + off, c.Z + off) * h;
        centroid /= cells.Count;
        return new LiftingSurface(V(normalI), V(spanAxis), V(chordI), spanM * chordM, spanM, thickCells * h / chordM, 0f, centroid, centroid - V(chordI) * (0.25f * chordM), cells.Count, cells);
    }

    /// <summary>Strain of a wing under a wing loading (Pa): the worse side, on whatever capacity its sides were finished.</summary>
    static float StrainAt(in LiftingSurface w, WingStrain.Root r, float loadingPa, float strength = 1f)
        => MathF.Max(WingStrain.Strain(loadingPa * w.PlanformArea, r.A, strength), WingStrain.Strain(loadingPa * w.PlanformArea, r.B, strength));

    /// <summary>The root finished on blocks' boxes (game cells), as the server does; returns how many blocks are at each
    /// side's worst station.</summary>
    static int[] OnBlocks(WingStrain.Root r, int k, int blockCells, List<(Vector3I min, Vector3I max, bool heavy)> boxes)
    {
        var sec = new WingStrain.Sections(r, k, blockCells);
        foreach (var (mn, mx, hv) in boxes) if (sec.Covers(mn, mx)) sec.Add(mn, mx, hv);
        var at = new int[2];
        for (int s = 0; s < 2; s++)
        {
            var side = r.Get(s);
            if (side == null) continue;
            WingStrain.Finish(side, sec.Capacities(s));
            foreach (var (mn, mx, _) in boxes) if (sec.AtStation(s, side.Critical, mn, mx)) at[s]++;
        }
        return at;
    }

    /// <summary>Blocks n x 1 x 1 along X from game cell x0 (size bc game cells), y 0, z 0.</summary>
    static List<(Vector3I, Vector3I, bool)> BlockRow(int x0, int count, int bc, bool heavy = false)
    {
        var l = new List<(Vector3I, Vector3I, bool)>();
        for (int i = 0; i < count; i++) l.Add((new Vector3I(x0 + bc * i, 0, 0), new Vector3I(x0 + bc * i + bc - 1, bc - 1, bc - 1), heavy));
        return l;
    }

    static void Calibration()
    {
        // large grid: 0.5 m wing cells (k = 2, offset 0.25), blocks of 2.5 m = 5 cells; the wing 4 blocks out at +X
        var w = BoxWing(new Vector3I(1, 0, 0), 10, 20, 5, 5, 0.5f, 0.25f);
        var r = WingStrain.Analyze(w, 0.5f, 0.25f, Vector3.Zero);
        Check(r != null && r.Sides == 1, "large wing: one root");
        Check(r.A.Critical == 0, $"large wing: worst station {r.A.Critical}, expected the root (0)");
        Check(Near(r.A.CriticalArm, 5f), $"large wing: arm {r.A.CriticalArm} m, expected 5 (half its 10 m, from the root face)");
        float s40 = StrainAt(w, r, 40000f);
        Check(Near(s40, 1.5f), $"large wing at 40 kPa: strain {s40}, expected 1.5 (breaks)");
        Check(WingStrain.Breaks(s40 + 1e-4f) && !WingStrain.Breaks(StrainAt(w, r, 39000f)), "large wing: breaks at 40 kPa, not at 39");
        float sLim = StrainAt(w, r, 40000f / 1.5f);
        Check(Near(sLim, 1f), $"large wing at 26.7 kPa: strain {sLim}, expected 1 (the limit)");
        Check(Near(r.A.CriticalCapacity, WingStrain.CellStrength * 2.5f * 2.5f * 2.5f), "large wing: root capacity = CellStrength * b^3 (one block-sized cell)");

        // from its blocks: four 2.5 m blocks (10 game cells each), x from 20 (wing cell 10 = game cells 20..21)
        var at = OnBlocks(r, 2, 10, BlockRow(20, 4, 10));
        float sb = StrainAt(w, r, 40000f);
        Check(Near(sb, 1.5f), $"large wing from blocks at 40 kPa: strain {sb}, expected 1.5");
        Check(r.A.Critical == 0 && at[0] == 1, $"large wing from blocks: worst station {r.A.Critical}, {at[0]} block(s) there (expected 0, 1)");

        // small grid: 0.25 m cells (k = 1), blocks of 0.5 m = 2 cells; the same wing in blocks breaks at the same loading
        var ws = BoxWing(new Vector3I(1, 0, 0), 4, 8, 2, 2, 0.25f, 0f);
        var rs = WingStrain.Analyze(ws, 0.25f, 0f, Vector3.Zero);
        Check(Near(StrainAt(ws, rs, 40000f), 1.5f), $"small wing at 40 kPa: strain {StrainAt(ws, rs, 40000f)}, expected 1.5 (block-size invariant)");
        OnBlocks(rs, 1, 2, BlockRow(4, 4, 2));
        Check(Near(StrainAt(ws, rs, 40000f), 1.5f), $"small wing from blocks at 40 kPa: strain {StrainAt(ws, rs, 40000f)}, expected 1.5");

        // the chord cancels: a 3-block chord, same loading, same strain
        var wc = BoxWing(new Vector3I(1, 0, 0), 10, 20, 15, 5, 0.5f, 0.25f);
        float sc = StrainAt(wc, WingStrain.Analyze(wc, 0.5f, 0.25f, Vector3.Zero), 40000f);
        Check(Near(sc, 1.5f), $"3-block chord at 40 kPa: strain {sc}, expected 1.5");
    }

    static void Scaling()
    {
        var w4 = BoxWing(new Vector3I(1, 0, 0), 10, 20, 5, 5, 0.5f, 0.25f);
        var r4 = WingStrain.Analyze(w4, 0.5f, 0.25f, Vector3.Zero);
        float a = StrainAt(w4, r4, 10000f), b = StrainAt(w4, r4, 20000f);
        Check(Near(b, 2f * a), $"strain with load: {a} at 10 kPa, {b} at 20 kPa (expected x2)");
        var w8 = BoxWing(new Vector3I(1, 0, 0), 10, 40, 5, 5, 0.5f, 0.25f);
        var r8 = WingStrain.Analyze(w8, 0.5f, 0.25f, Vector3.Zero);
        float c = StrainAt(w8, r8, 10000f);
        Check(Near(c, 4f * a), $"strain with span^2: 4 blocks {a}, 8 blocks {c} (expected x4)");
        Check(Near(StrainAt(w8, r8, 10000f), 1.5f), "8-block wing snaps at 10 kPa");
        // a shallower root: 1 m (two 0.5 m cells) instead of the block's 2.5 m: (2.5 / 1)^2 = 6.25x the strain
        var thin = BoxWing(new Vector3I(1, 0, 0), 10, 20, 5, 2, 0.5f, 0.25f);
        float t = StrainAt(thin, WingStrain.Analyze(thin, 0.5f, 0.25f, Vector3.Zero), 10000f);
        Check(Near(t, 6.25f * a), $"a 1 m deep wing: strain {t} vs {a} for 2.5 m (expected x6.25: depth squared)");
        // the strength multiplier
        Check(Near(StrainAt(w4, r4, 10000f, strength: 2f), a / 2f), "Structure.WingStrength 2 halves the strain");
        // the normal force: lift along the normal counts, induced drag across it does not; either sign bends alike
        float fn = WingStrain.NormalForce(w4, new Vector3(0, 1000, 0), new Vector3(0, 0, -300));
        Check(Near(fn, 1000f), $"normal force {fn}, expected 1000");
        Check(WingStrain.Strain(-1000f, r4.A) == WingStrain.Strain(1000f, r4.A), "down-load bends as much as up-load");
    }

    static void Heavy()
    {
        var w = BoxWing(new Vector3I(1, 0, 0), 10, 20, 5, 5, 0.5f, 0.25f);
        var r = WingStrain.Analyze(w, 0.5f, 0.25f, Vector3.Zero);
        OnBlocks(r, 2, 10, BlockRow(20, 4, 10));
        float l = StrainAt(w, r, 40000f);
        OnBlocks(r, 2, 10, BlockRow(20, 4, 10, heavy: true));
        float h = StrainAt(w, r, 40000f);
        Check(Near(h, l / 4f), $"heavy armour: strain {h} vs light {l} (expected / 4)");
        Check(!WingStrain.Breaks(h) && h < WingStrain.LimitStrain, "a heavy 4-block wing at 40 kPa holds (no wear)");
        // a heavy root block only: the root is the worst station still, and 4x stronger there
        var mixed = BoxWing(new Vector3I(1, 0, 0), 10, 20, 5, 5, 0.5f, 0.25f);
        var rm = WingStrain.Analyze(mixed, 0.5f, 0.25f, Vector3.Zero);
        var boxes = BlockRow(20, 4, 10); boxes[0] = (boxes[0].Item1, boxes[0].Item2, true);
        OnBlocks(rm, 2, 10, boxes);
        Check(rm.A.Critical > 0 && StrainAt(mixed, rm, 40000f) < l, $"heavy root block: worst station moves out ({rm.A.Critical}), strain {StrainAt(mixed, rm, 40000f)} < {l}");
    }

    static void Roots()
    {
        // right wing (+X of the CoM): root at its inboard end (lowest X), extends +X
        var right = BoxWing(new Vector3I(1, 0, 0), 4, 20, 5, 5, 0.5f, 0.25f);
        var rr = WingStrain.Analyze(right, 0.5f, 0.25f, Vector3.Zero);
        Check(rr.Sides == 1 && rr.A.Dir == 1 && rr.A.RootIndex == 4, $"right wing: root at x = {rr.A.RootIndex}, dir {rr.A.Dir} (expected 4, +1)");
        // left wing, the mirror: root at its highest X, extends -X; same arm and strain
        var left = BoxWing(new Vector3I(1, 0, 0), -24, 20, 5, 5, 0.5f, 0.25f);
        var rl = WingStrain.Analyze(left, 0.5f, 0.25f, Vector3.Zero);
        Check(rl.Sides == 1 && rl.A.Dir == -1 && rl.A.RootIndex == -5, $"left wing: root at x = {rl.A.RootIndex}, dir {rl.A.Dir} (expected -5, -1)");
        Check(Near(rl.A.CriticalArm, rr.A.CriticalArm) && Near(StrainAt(left, rl, 20000f), StrainAt(right, rr, 20000f)), $"left and right alike (arms {rl.A.CriticalArm}, {rr.A.CriticalArm})");
        // left wing on its blocks (negative coordinates): the root block is the inboard one
        var atL = OnBlocks(rl, 2, 10, BlockRow(-48, 4, 10));
        Check(rl.A.Critical == 0 && atL[0] == 1 && Near(StrainAt(left, rl, 40000f), 1.5f), $"left wing from blocks: worst station {rl.A.Critical}, {atL[0]} block(s), strain {StrainAt(left, rl, 40000f)} (expected 0, 1, 1.5)");
        // the centre of mass moved past the left wing's root: still the end nearest it
        var rl2 = WingStrain.Analyze(left, 0.5f, 0.25f, new Vector3(20f, 0, 0));
        Check(rl2.Sides == 1 && rl2.A.RootIndex == -5, "left wing, centre of mass far to the right: root still its inboard end");
        // a fin: span up (+Y) above the centre of mass: root at the bottom
        var fin = BoxWing(new Vector3I(0, 1, 0), 6, 12, 6, 1, 0.5f, 0.25f);
        var rf = WingStrain.Analyze(fin, 0.5f, 0.25f, Vector3.Zero);
        Check(rf.Sides == 1 && rf.A.Dir == 1 && rf.A.RootIndex == 6, $"fin: root at y = {rf.A?.RootIndex} (expected 6, its bottom)");
        // one wing through the fuselage (8 blocks tip to tip, CoM in the middle): two 4-block cantilevers, each with half
        // the load - the same strain as a separate 4-block wing at the same loading
        var through = BoxWing(new Vector3I(1, 0, 0), -20, 40, 5, 5, 0.5f, 0.25f);
        var rt = WingStrain.Analyze(through, 0.5f, 0.25f, Vector3.Zero);
        Check(rt.Sides == 2 && rt.A.RootIndex == 0 && rt.B.RootIndex == -1, $"through wing: {rt.Sides} roots at {rt.A?.RootIndex}, {rt.B?.RootIndex} (expected 2: 0, -1)");
        float st = StrainAt(through, rt, 40000f);
        Check(Near(st, 1.5f), $"through wing at 40 kPa: strain {st}, expected 1.5 (as two 4-block wings)");
        // ... and with a fuselage 4 blocks wide and 3 deep around its middle (the wing along its bottom): it breaks where
        // it leaves the fuselage, less loaded there than at the middle
        var fus = BlockRow(-40, 8, 10);   // (the wing's blocks, x -40..39 game cells)
        for (int bx = -2; bx < 2; bx++) for (int by = 1; by < 3; by++) fus.Add((new Vector3I(bx * 10, by * 10, 0), new Vector3I(bx * 10 + 9, by * 10 + 9, 9), false));
        // (the fuselage: game x -20..19 - wing cells -10..9 - two blocks above the wing's middle four)
        var at = OnBlocks(rt, 2, 10, fus);
        Check(rt.A.Critical == 10 && rt.B.Critical == 10, $"through wing with fuselage: worst stations {rt.A.Critical}, {rt.B.Critical} (expected 10: where it leaves, 5 m out)");
        float sf = StrainAt(through, rt, 40000f);
        Check(sf < st && sf > 0.1f && at[0] == 1 && at[1] == 1, $"through wing with fuselage: strain {sf} < {st} (the fuselage carries the middle), {at[0]} + {at[1]} root blocks");
    }

    static void Wear()
    {
        Check(Near(WingStrain.Wear(0.2f), 0.1f), "wear: 0.2 strain-seconds over the limit take 10% health");
        Check(WingStrain.Wear(0f) == 0f && WingStrain.Wear(-1f) == 0f, "wear: none at or under the limit");
        Check(WingStrain.Breaks(1.5f) && !WingStrain.Breaks(1.49f), "break threshold 1.5");
        var empty = new LiftingSurface(Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ, 1, 1, 0.1f, 0, Vector3.Zero, Vector3.Zero, 0, new List<Vector3I>());
        Check(WingStrain.Analyze(empty, 0.5f, 0.25f, Vector3.Zero) == null, "a wing without cells: no root");
        Check(WingStrain.Strain(1000f, null) == 0f, "no side: no strain");
    }

    static int Main()
    {
        Calibration(); Scaling(); Heavy(); Roots(); Wear();
        Console.WriteLine($"WingStrainTests: {_checks - _fails}/{_checks} passed");
        return _fails == 0 ? 0 : 1;
    }
}
