#pragma warning disable
namespace AeroMod;

/// <summary>
/// TEARING's game-free part (2026-10-14; the game side: Integration/StructureTear.cs - offline-tested in Tests/TearTests):
/// the hold's ramp over the tear, where a broken block was and how far a split-off piece is from it, and the three anchors
/// a torn piece hangs from (the joint, and two points spread across it - their lever arms hold its turning).
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

    /// <summary>The start of the hold (N, per anchor): ForceFactor x the piece's weight at 1 g, shared by the anchors (the
    /// three together hold the piece at ForceFactor g).</summary>
    public static float StartForce(float mass, float forceFactor, int anchors) => forceFactor * mass * 9.81f / System.Math.Max(1, anchors);
}
