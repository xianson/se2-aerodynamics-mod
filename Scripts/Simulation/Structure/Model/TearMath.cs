#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace AeroMod;

/// <summary>
/// TEARING's game-free part (2026-10-14; the game side: Structure/Server/StructureTear.cs - offline-tested in Tests/TearTests):
/// the hold's ramp over the tear, where a broken block was and how far a split-off piece is from it, and the anchors a torn
/// piece hangs from (2026-10-15: picked over the real failed section, each with its own spring and its own release in the
/// crack's order - PickAnchors, AnchorEnd; a single place: the joint and two points spread across it - Anchors).
/// </summary>
public static class TearMath
{
    /// <summary>The game's cell (m): block boxes are in cells, a cell's centre at index x 0.25 (CubeGridCoords).</summary>
    public const float Cell = 0.25f;

    /// <summary>The hold over the tear (s: 0 at the break, 1 at TearSeconds): a falling smoothstep - it HOLDS first (flat
    /// at the start: the piece hangs on), lets go through the middle (it sags, flutters), and fades flat to nothing (no
    /// snap at the end: the constraints are destroyed at 0). 1 before the start, 0 after the end; NaN-safe (1).</summary>
    public static float Ramp(float s)
    {
        if (!(s > 0f)) return 1f;
        if (s >= 1f) return 0f;
        return 1f - s * s * (3f - 2f * s);
    }

    /// <summary>A block's box in grid-local metres from its cells' box (inclusive cells: each cell +-half a cell about its centre).</summary>
    public static void CellBox(Vector3I lo, Vector3I hi, out Vector3 min, out Vector3 max)
    {
        min = new Vector3(lo.X, lo.Y, lo.Z) * Cell - new Vector3(Cell * 0.5f);
        max = new Vector3(hi.X, hi.Y, hi.Z) * Cell + new Vector3(Cell * 0.5f);
    }

    /// <summary>A block's centre in grid-local metres from its cells' box.</summary>
    public static Vector3 CellCentre(Vector3I lo, Vector3I hi) => new Vector3(lo.X + hi.X, lo.Y + hi.Y, lo.Z + hi.Z) * (Cell * 0.5f);

    /// <summary>A point's distance (m) from a box (0 inside).</summary>
    public static float BoxDistance(Vector3 min, Vector3 max, Vector3 p) => (p - Vector3.Clamp(p, min, max)).Length();

    /// <summary>
    /// The three anchors a torn piece hangs from (world): the JOINT (the broken blocks' mean), and two points AnchorSpread
    /// (or the joint's own half-extent, if wider) out across it - along the joint's longest spread (a row of broken blocks:
    /// along the row) and square to that, both square to the way out to the piece's centre of mass. Linear axes only (one
    /// shared soft motor), so three non-collinear points hold the piece's turning by their lever arms. Returns how many
    /// were written (3; 0 with no joint points).
    /// </summary>
    public static int Anchors(System.ReadOnlySpan<Vector3D> joint, Vector3D pieceCom, float spread, System.Span<Vector3D> anchors)
    {
        int n = joint.Length;
        if (n == 0 || anchors.Length < 3) return 0;
        Vector3D c = Vector3D.Zero;
        for (int i = 0; i < n; i++) c += joint[i];
        c /= n;
        // (the way out: joint to the piece's centre of mass; none - the joint at its centre - any)
        Vector3 outw = (Vector3)(pieceCom - c);
        outw = outw.LengthSquared() > 1e-6f ? Vector3.Normalize(outw) : Vector3.UnitX;
        // (across: the joint's longest spread square to the way out; a single block - the axis least along the way out)
        Vector3 u = Vector3.Zero; float far = 0f;
        for (int i = 0; i < n; i++)
        {
            Vector3 d = (Vector3)(joint[i] - c);
            d -= outw * Vector3.Dot(d, outw);
            float l = d.Length();
            if (l > far) { far = l; u = d / l; }
        }
        if (far < 0.25f)
        {
            Vector3 ax = System.MathF.Abs(outw.X) < 0.6f ? Vector3.UnitX : System.MathF.Abs(outw.Y) < 0.6f ? Vector3.UnitY : Vector3.UnitZ;
            u = Vector3.Normalize(Vector3.Cross(outw, ax));
        }
        Vector3 v = Vector3.Normalize(Vector3.Cross(outw, u));
        float s = System.MathF.Max(spread, far);
        anchors[0] = c;
        anchors[1] = c + (Vector3D)(u * s);
        anchors[2] = c + (Vector3D)(v * s);
        return 3;
    }

    /// <summary>
    /// SPRINGS ACROSS THE REAL SECTION (2026-10-15, the user: "also maybe two or more spring constraints, instead of just
    /// one?"): the anchors a torn piece hangs from, picked from the failed members' own points (world) and their places in
    /// the crack's order (0 the first to let go .. 1 the hinge): the HINGE first (the latest order), then the crack's
    /// ORIGIN (the earliest), then each the point farthest from those picked (spread over the section), skipping any within
    /// MinGap of one, up to max (at least 3). Points in a row (or one) cannot hold the piece's turning about the row by
    /// linear springs alone: one anchor is added AnchorSpread (or the row's half-length, if more) out square to the row and
    /// to the way to the piece's centre of mass, at the hinge's order - a single point gets the old three (Anchors). Writes
    /// anchors and their orders (0..1 over the picked: all equal - all 1); returns how many.
    /// </summary>
    public static int PickAnchors(System.ReadOnlySpan<Vector3D> pts, System.ReadOnlySpan<float> order, Vector3D pieceCom, float spread, int max,
        System.Span<Vector3D> anchors, System.Span<float> outOrder)
    {
        int n = pts.Length;
        max = System.Math.Min(System.Math.Max(max, 3), System.Math.Min(anchors.Length, outOrder.Length));
        if (n == 0 || max < 3) return 0;
        int hinge = 0, origin = 0;
        for (int i = 1; i < n; i++) { if (Ord(order, i) > Ord(order, hinge)) hinge = i; if (Ord(order, i) < Ord(order, origin)) origin = i; }
        int k = 0;
        anchors[k] = pts[hinge]; outOrder[k++] = Ord(order, hinge);
        if ((pts[origin] - pts[hinge]).Length() >= MinGap) { anchors[k] = pts[origin]; outOrder[k++] = Ord(order, origin); }
        // (the rest: farthest from those picked)
        while (k < max)
        {
            int far = -1; double fd = MinGap;
            for (int i = 0; i < n; i++)
            {
                double d = double.MaxValue;
                for (int j = 0; j < k; j++) d = System.Math.Min(d, (pts[i] - anchors[j]).Length());
                if (d > fd) { fd = d; far = i; }
            }
            if (far < 0) break;
            anchors[k] = pts[far]; outOrder[k++] = Ord(order, far);
        }
        if (k == 1)
        {
            // (one place: the old three, all at its order)
            float o = outOrder[0];
            int na = Anchors(pts.Slice(hinge, 1), pieceCom, spread, anchors);
            for (int i = 0; i < na; i++) outOrder[i] = o;
            k = na;
        }
        else
        {
            // (in a row: one more, out square to it - the row's half-length or AnchorSpread out, at the hinge's order)
            Vector3 u = Vector3.Zero; double far = 0;
            for (int i = 0; i < k; i++) for (int j = i + 1; j < k; j++) { double d = (anchors[i] - anchors[j]).Length(); if (d > far) { far = d; u = (Vector3)(anchors[j] - anchors[i]); } }
            u = Vector3.Normalize(u);
            float off = 0f;
            for (int i = 1; i < k; i++) { Vector3 d = (Vector3)(anchors[i] - anchors[0]); d -= u * Vector3.Dot(d, u); off = System.MathF.Max(off, d.Length()); }
            if (off < MinGap)
            {
                if (k >= max) k = max - 1;   // (room for it: the last picked gives way)
                Vector3D c = Vector3D.Zero; for (int i = 0; i < k; i++) c += anchors[i]; c /= k;
                Vector3 outw = (Vector3)(pieceCom - c);
                outw -= u * Vector3.Dot(outw, u);
                outw = outw.LengthSquared() > 1e-6f ? Vector3.Normalize(outw) : (System.MathF.Abs(u.X) < 0.6f ? Vector3.UnitX : Vector3.UnitY);
                Vector3 v = Vector3.Cross(u, outw);
                if (v.LengthSquared() < 1e-6f) v = Vector3.Cross(u, System.MathF.Abs(u.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
                v = Vector3.Normalize(v);
                anchors[k] = anchors[0] + (Vector3D)(v * System.MathF.Max(spread, (float)far * 0.5f)); outOrder[k] = outOrder[0]; k++;
            }
        }
        // (orders over the picked: 0 the first to let go, 1 the hinge; all equal - all 1)
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0; i < k; i++) { lo = System.MathF.Min(lo, outOrder[i]); hi = System.MathF.Max(hi, outOrder[i]); }
        for (int i = 0; i < k; i++) outOrder[i] = hi > lo ? (outOrder[i] - lo) / (hi - lo) : 1f;
        return k;
    }

    static float Ord(System.ReadOnlySpan<float> order, int i) => i < order.Length && !float.IsNaN(order[i]) ? order[i] : 1f;

    /// <summary>Anchors closer than this (m) are one place.</summary>
    public const float MinGap = 0.4f;

    /// <summary>When an anchor lets go (its share of the tear's time): the crack's origin (order 0) at peelFirst, the hinge
    /// (order 1) at the end - so the piece peels from the torn side and swings on the hinge last.</summary>
    public static float AnchorEnd(float order, float peelFirst)
    {
        float p = System.Math.Clamp(float.IsNaN(peelFirst) ? 1f : peelFirst, 0.05f, 1f);
        float o = float.IsNaN(order) ? 1f : System.Math.Clamp(order, 0f, 1f);
        return p + (1f - p) * o;
    }

    /// <summary>An anchor's hold (0..1 of its start) at s (0 the split, 1 TearSeconds) for its end (AnchorEnd): the ramp
    /// over its own time - flat at the start, nothing at its end.</summary>
    public static float AnchorHold(float s, float end) => end > 0f ? Ramp(s / end) : 0f;

    /// <summary>The start of the hold (N, per anchor): ForceFactor x the piece's weight at 1 g, shared by the anchors (the
    /// three together hold the piece at ForceFactor g).</summary>
    public static float StartForce(float mass, float forceFactor, int anchors) => forceFactor * mass * 9.81f / System.Math.Max(1, anchors);
}
