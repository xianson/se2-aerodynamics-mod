using AeroMod;

// Tearing's game-free part (Structure/Model/TearMath.cs; the game side Structure/Server/StructureTear.cs), offline:
//  1. the hold's ramp: full at the break, nothing at TearSeconds, flat at both ends (holds first, no snap at the end),
//     falling all the way, NaN-safe; the start force (ForceFactor g shared by the anchors);
//  2. block boxes and centres from the game's cells (0.25 m, inclusive), a point's distance from a box;
//  3. pairing: a broken block next to the piece is within PairRange of it, one across the ship is not;
//  4. the anchors: three, non-collinear, the joint first, the other two AnchorSpread out square to the way to the
//     piece's centre of mass; a row of broken blocks spreads them along the row (at least its half-length).
static class Program
{
    static int _fails, _checks;
    static void Check(bool ok, string what) { _checks++; if (!ok) { _fails++; Console.WriteLine("FAIL " + what); } }
    static bool Near(double a, double b, double tol = 1e-4) => Math.Abs(a - b) <= tol;

    static int Main()
    {
        // 1. the ramp
        Check(TearMath.Ramp(0f) == 1f && TearMath.Ramp(-1f) == 1f, "ramp: full at and before the break");
        Check(TearMath.Ramp(1f) == 0f && TearMath.Ramp(2f) == 0f, "ramp: nothing at and after the end");
        Check(TearMath.Ramp(float.NaN) == 1f, "ramp: NaN holds (never a NaN force)");
        Check(Near(TearMath.Ramp(0.5f), 0.5f), "ramp: half at half time");
        Check(TearMath.Ramp(0.05f) > 0.99f, "ramp: holds first (flat start)");
        Check(TearMath.Ramp(0.95f) < 0.01f, "ramp: fades out (flat end, no snap)");
        float prev = 1f; bool falling = true;
        for (int i = 1; i <= 100; i++) { float r = TearMath.Ramp(i / 100f); if (r > prev) falling = false; prev = r; }
        Check(falling, "ramp: falls all the way");
        // (over 1.2 s at 60 Hz: the force each tick - the game side's steps)
        int steps = (int)(1.2f * 60f + 0.5f);
        Check(steps == 72 && TearMath.Ramp(steps / (float)steps) == 0f, "ramp: 72 steps over 1.2 s, nothing at the last");
        Check(Near(TearMath.StartForce(10000f, 6f, 3), 6f * 10000f * 9.81f / 3f, 1e-2), "start force: 6 g of a 10 t piece over 3 anchors");
        Check(TearMath.StartForce(1000f, 6f, 0) == 6f * 1000f * 9.81f, "start force: no anchors is one");

        // 2. cells
        TearMath.CellBox(new Vector3I(0, 0, 0), new Vector3I(9, 9, 9), out var mn, out var mx);   // (a large block: 10 cells)
        Check(Near(mn.X, -0.125) && Near(mx.X, 2.375) && Near(mx.X - mn.X, 2.5), "cell box: a large block is 2.5 m");
        var c = TearMath.CellCentre(new Vector3I(0, 0, 0), new Vector3I(9, 9, 9));
        Check(Near(c.X, 1.125) && Near(c.Y, 1.125) && Near(c.Z, 1.125), "cell centre: the box's middle");
        Check(TearMath.BoxDistance(mn, mx, c) == 0f, "box distance: 0 inside");
        Check(Near(TearMath.BoxDistance(mn, mx, new Vector3(5.375f, 1f, 1f)), 3.0), "box distance: along an axis");
        Check(Near(TearMath.BoxDistance(mn, mx, new Vector3(5.375f, 6.375f, 1f)), 5.0), "box distance: to the corner edge (3-4-5)");

        // 3. pairing: a row of large blocks along X (cells 0..49: blocks 0..4); the piece is blocks 3..4 after block 2 broke
        float pairRange = 3f;
        var pieceMin = new List<Vector3>(); var pieceMax = new List<Vector3>();
        for (int b = 3; b <= 4; b++) { TearMath.CellBox(new Vector3I(b * 10, 0, 0), new Vector3I(b * 10 + 9, 9, 9), out var a0, out var a1); pieceMin.Add(a0); pieceMax.Add(a1); }
        float Nearest(Vector3 p) { float best = float.MaxValue; for (int i = 0; i < pieceMin.Count; i++) best = MathF.Min(best, TearMath.BoxDistance(pieceMin[i], pieceMax[i], p)); return best; }
        var broken = TearMath.CellCentre(new Vector3I(20, 0, 0), new Vector3I(29, 9, 9));
        var farAway = TearMath.CellCentre(new Vector3I(-50, 0, 0), new Vector3I(-41, 9, 9));
        Check(Nearest(broken) <= pairRange && Near(Nearest(broken), 1.25), "pairing: the broken neighbour is half a block from the piece");
        Check(Nearest(farAway) > pairRange, "pairing: a break across the ship is not this piece's joint");

        // 4. anchors
        var anchors = new Vector3D[3];
        var joint = new[] { new Vector3D(10, 0, 0) };
        int n = TearMath.Anchors(joint, new Vector3D(20, 0, 0), 1.5f, anchors);
        Check(n == 3, "anchors: three");
        Check(anchors[0] == joint[0], "anchors: the joint first");
        for (int k = 1; k < 3; k++)
        {
            var d = anchors[k] - anchors[0];
            Check(Near(d.Length(), 1.5, 1e-3), $"anchors: #{k} AnchorSpread out");
            Check(Math.Abs(d.X) < 1e-4, $"anchors: #{k} square to the way to the piece");
        }
        var e1 = anchors[1] - anchors[0]; var e2 = anchors[2] - anchors[0];
        Check(Vector3D.Cross(e1, e2).Length() > 1.0, "anchors: not collinear (they hold the turning)");
        foreach (var a in anchors) Check(!double.IsNaN(a.X + a.Y + a.Z), "anchors: finite");
        // a row of three broken blocks along Z (5 m long), the piece out along +X: spread along the row, half its length
        var row = new[] { new Vector3D(0, 0, -2.5), new Vector3D(0, 0, 0), new Vector3D(0, 0, 2.5) };
        TearMath.Anchors(row, new Vector3D(8, 0, 0), 1.5f, anchors);
        var r1 = anchors[1] - anchors[0];
        Check(Near(Math.Abs(r1.Z), 2.5, 1e-3) && Math.Abs(r1.X) < 1e-4 && Math.Abs(r1.Y) < 1e-4, "anchors: along the row, its half-length");
        Check(Near((anchors[2] - anchors[0]).Length(), 2.5, 1e-3), "anchors: the other as far, across");
        // the piece's centre at the joint (degenerate way out): still three finite, non-collinear anchors
        TearMath.Anchors(joint, joint[0], 1.5f, anchors);
        Check(Vector3D.Cross(anchors[1] - anchors[0], anchors[2] - anchors[0]).Length() > 1.0 && !double.IsNaN(anchors[2].X), "anchors: degenerate way out still sound");
        Check(TearMath.Anchors(ReadOnlySpan<Vector3D>.Empty, Vector3D.Zero, 1.5f, anchors) == 0, "anchors: none without a joint");

        Console.WriteLine($"TearTests: {_checks - _fails}/{_checks} passed");
        return _fails == 0 ? 0 : 1;
    }
}
