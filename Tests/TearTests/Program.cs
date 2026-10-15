using AeroMod;

// Tearing's game-free part (Structure/Model/TearMath.cs; the game side Structure/Server/StructureTear.cs), offline:
//  1. the hold's ramp: full at the break, nothing at TearSeconds, flat at both ends (holds first, no snap at the end),
//     falling all the way, NaN-safe; the start force (ForceFactor g shared by the anchors);
//  2. block boxes and centres from the game's cells (0.25 m, inclusive), a point's distance from a box;
//  3. pairing: a broken block next to the piece is within PairRange of it, one across the ship is not;
//  4. the anchors: three, non-collinear, the joint first, the other two AnchorSpread out square to the way to the
//     piece's centre of mass; a row of broken blocks spreads them along the row (at least its half-length).
//  5. the springs over the real section (2026-10-15, PickAnchors / AnchorEnd / AnchorHold): 3..MaxAnchors on the failed
//     members' own places, the hinge and the crack's origin among them, spanning the section; a row gets one off it; one
//     place the old three; each anchor's release monotone in the crack's order, the hinge last; ForceFactor g in all.
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

        // 5. SPRINGS ACROSS THE REAL SECTION (2026-10-15, the user: "also maybe two or more spring constraints, instead of just
        //    one?"): a failed section of 4 x 2 places 0.5 m apart on the plane x = 10, the piece out along +x; the crack runs
        //    from the pulled row (y 0: order 0 .. 0.3) to the pushed one (y 0.5: 0.7 .. 1, the hinge at z 1.5)
        {
            var pts = new List<Vector3D>(); var ord = new List<float>();
            for (int y = 0; y < 2; y++) for (int z = 0; z < 4; z++) { pts.Add(new Vector3D(10, y * 0.5, z * 0.5)); ord.Add(y * 0.7f + z * 0.1f); }
            var an = new Vector3D[8]; var ao = new float[8];
            int na = TearMath.PickAnchors(pts.ToArray(), ord.ToArray(), new Vector3D(20, 0.25, 0.75), 1.5f, 6, an, ao);
            Check(na >= 3 && na <= 6, $"springs: 3..6 anchors over the section ({na})");
            Check(an[0] == pts[7] && ao[0] == 1f, "springs: the hinge first, order 1");
            bool origin = false; for (int k = 0; k < na; k++) if (an[k] == pts[0] && ao[k] == 0f) origin = true;
            Check(origin, "springs: the crack's origin among them, order 0");
            double z0 = double.MaxValue, z1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue; bool onSection = true, apart = true;
            for (int k = 0; k < na; k++)
            {
                z0 = Math.Min(z0, an[k].Z); z1 = Math.Max(z1, an[k].Z); y0 = Math.Min(y0, an[k].Y); y1 = Math.Max(y1, an[k].Y);
                bool found = false; foreach (var p in pts) if (p == an[k]) found = true;
                onSection &= found;
                for (int j = 0; j < k; j++) if ((an[k] - an[j]).Length() < TearMath.MinGap) apart = false;
            }
            Check(onSection && apart && z1 - z0 >= 1.5 - 1e-6 && y1 - y0 >= 0.5 - 1e-6, $"springs: on the failed members' own places, spanning the whole section (z {z1 - z0:F2} of 1.5 m, y {y1 - y0:F2} of 0.5 m), none within MinGap");
            float lo = 1f, hi = 0f; for (int k = 0; k < na; k++) { lo = MathF.Min(lo, ao[k]); hi = MathF.Max(hi, ao[k]); }
            Check(lo == 0f && hi == 1f, "springs: their orders over 0 (the origin) .. 1 (the hinge)");
            Check(TearMath.PickAnchors(pts.ToArray(), ord.ToArray(), new Vector3D(20, 0, 0), 1.5f, 3, an, ao) == 3, "springs: MaxAnchors 3 respected");
            Check(TearMath.PickAnchors(pts.ToArray(), ord.ToArray(), new Vector3D(20, 0, 0), 1.5f, 1, an, ao) == 3, "springs: never under 3 (they hold the turning)");
            // a row (a thin skin's one line of members): one more, out square to it, at the hinge's order
            var line = new Vector3D[] { new(10, 0, 0), new(10, 0, 1), new(10, 0, 2), new(10, 0, 3) };
            na = TearMath.PickAnchors(line, new float[] { 0f, 0.3f, 0.6f, 1f }, new Vector3D(20, 0, 1.5), 1.5f, 6, an, ao);
            double area = 0; for (int i = 0; i < na; i++) for (int j = 0; j < na; j++) for (int k = 0; k < na; k++) area = Math.Max(area, Vector3D.Cross(an[j] - an[i], an[k] - an[i]).Length());
            int off = -1; for (int k = 0; k < na; k++) if (Math.Abs(an[k].Y) > 1.0) off = k;
            Check(na >= 3 && area > 1.0 && off >= 0 && ao[off] == 1f && Math.Abs(an[off].X - 10) < 1e-6, $"springs: a row gets an anchor out square to it and to the way out, at the hinge's order ({na}, area {area:F2})");
            // one place: the old three, all at its order
            na = TearMath.PickAnchors(new[] { new Vector3D(10, 0, 0) }, new[] { 0.4f }, new Vector3D(20, 0, 0), 1.5f, 6, an, ao);
            var old = new Vector3D[3]; TearMath.Anchors(new[] { new Vector3D(10, 0, 0) }, new Vector3D(20, 0, 0), 1.5f, old);
            Check(na == 3 && an[0] == old[0] && an[1] == old[1] && an[2] == old[2] && ao[0] == 1f && ao[2] == 1f, "springs: one place - the old three, together");
            Check(TearMath.PickAnchors(ReadOnlySpan<Vector3D>.Empty, ReadOnlySpan<float>.Empty, Vector3D.Zero, 1.5f, 6, an, ao) == 0, "springs: none without a place");
            // the release: each its own end in the crack's order - the origin's at PeelFirst, the hinge's at the end
            float peel = 0.35f;
            Check(TearMath.AnchorEnd(0f, peel) == peel && TearMath.AnchorEnd(1f, peel) == 1f && TearMath.AnchorEnd(float.NaN, peel) == 1f && TearMath.AnchorEnd(0f, 1f) == 1f, "release: the origin at PeelFirst, the hinge at the end (PeelFirst 1: all together)");
            bool mono = true, hingeLast = true, holdingFalls = true; int lastHolding = int.MaxValue;
            float[] orders = { 0f, 0.2f, 0.5f, 0.8f, 1f };
            for (int i = 1; i < orders.Length; i++) if (TearMath.AnchorEnd(orders[i], peel) < TearMath.AnchorEnd(orders[i - 1], peel)) mono = false;
            for (int k = 0; k <= 100; k++)
            {
                float sx = k / 100f; int holding = 0;
                foreach (float o in orders) if (TearMath.AnchorHold(sx, TearMath.AnchorEnd(o, peel)) > 0f) holding++;
                if (holding > lastHolding) holdingFalls = false; lastHolding = holding;
                if (TearMath.AnchorHold(sx, TearMath.AnchorEnd(1f, peel)) < TearMath.AnchorHold(sx, TearMath.AnchorEnd(0f, peel))) hingeLast = false;
            }
            Check(mono && hingeLast && holdingFalls, "release: monotone in the crack's order - anchors let go one by one, the hinge holds longest");
            Check(TearMath.AnchorHold(0f, 0.35f) == 1f && TearMath.AnchorHold(0.35f, 0.35f) == 0f && TearMath.AnchorHold(0.5f, 1f) == TearMath.Ramp(0.5f), "release: each anchor's own ramp - full at the split, nothing at its end (the hinge's: the old ramp)");
            Check(Near(TearMath.StartForce(10000f, 6f, 5) * 5, 6f * 10000f * 9.81f, 1e-1), "release: the anchors together hold ForceFactor g at the start");
        }

        Console.WriteLine($"TearTests: {_checks - _fails}/{_checks} passed");
        return _fails == 0 ? 0 : 1;
    }
}
