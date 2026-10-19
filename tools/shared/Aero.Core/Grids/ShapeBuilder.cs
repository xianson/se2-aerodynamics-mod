using Aero.Core.Math;

namespace Aero.Core.Grids;

/// <summary>
/// Builds grid shapes programmatically for testing aerodynamic models.
/// All shapes are built in grid-cell coordinates. Convention:
///   X = forward (nose direction)
///   Y = up
///   Z = right (starboard)
/// </summary>
public static class ShapeBuilder
{
    private static int _nextId;

    private static Block Add(Grid grid, int x, int y, int z)
    {
        var cell = new Vector3I(x, y, z);
        if (grid.GetBlockAt(cell) != null) return null!;
        var block = new Block(Interlocked.Increment(ref _nextId), "armor_cube", cell);
        grid.AddBlock(block);
        return block;
    }

    /// <summary>
    /// Remove all interior blocks (blocks with all 6 face-neighbors occupied).
    /// This leaves only the 1-block-thick shell, dramatically reducing mass
    /// while preserving the aerodynamic surface.
    /// </summary>
    public static void Hollow(Grid grid)
    {
        var toRemove = new List<int>();
        foreach (var block in grid.Blocks.Values)
        {
            var c = block.Min;
            if (grid.GetBlockAt(new Vector3I(c.X + 1, c.Y, c.Z)) != null &&
                grid.GetBlockAt(new Vector3I(c.X - 1, c.Y, c.Z)) != null &&
                grid.GetBlockAt(new Vector3I(c.X, c.Y + 1, c.Z)) != null &&
                grid.GetBlockAt(new Vector3I(c.X, c.Y - 1, c.Z)) != null &&
                grid.GetBlockAt(new Vector3I(c.X, c.Y, c.Z + 1)) != null &&
                grid.GetBlockAt(new Vector3I(c.X, c.Y, c.Z - 1)) != null)
            {
                toRemove.Add(block.Id);
            }
        }
        foreach (var id in toRemove)
            grid.RemoveBlock(id);
    }

    /// <summary>
    /// Fill a rectangular box of blocks.
    /// </summary>
    public static Grid Box(int lenX, int height, int width)
    {
        var grid = new Grid();
        for (int x = 0; x < lenX; x++)
            for (int y = 0; y < height; y++)
                for (int z = -width / 2; z <= width / 2; z++)
                    Add(grid, x, y, z);
        Hollow(grid);
        return grid;
    }

    /// <summary>
    /// Simple airplane: fuselage + rectangular wing + horizontal tail.
    ///
    /// Fuselage: long narrow box along X.
    /// Wing: flat plate (1 block thick) at mid-fuselage, spanning in Z.
    /// Tail: smaller flat plate at the rear.
    /// </summary>
    public static Grid SimpleAirplane(
        int fuselageLength = 20, int fuselageHeight = 2, int fuselageWidth = 2,
        int wingSpan = 20, int wingChord = 4, int wingY = 1,
        int tailSpan = 8, int tailChord = 2)
    {
        var grid = new Grid();

        // Fuselage: centered on Z=0
        int fzHalf = fuselageWidth / 2;
        for (int x = 0; x < fuselageLength; x++)
            for (int y = 0; y < fuselageHeight; y++)
                for (int z = -fzHalf; z <= fzHalf; z++)
                    Add(grid, x, y, z);

        // Wing: flat plate at wingY, starting at ~30% fuselage
        int wingStart = fuselageLength / 3;
        int wzHalf = wingSpan / 2;
        for (int x = wingStart; x < wingStart + wingChord; x++)
            for (int z = -wzHalf; z <= wzHalf; z++)
                Add(grid, x, wingY, z);

        // Horizontal tail: flat plate near the rear
        int tailStart = fuselageLength - tailChord - 1;
        int tzHalf = tailSpan / 2;
        for (int x = tailStart; x < tailStart + tailChord; x++)
            for (int z = -tzHalf; z <= tzHalf; z++)
                Add(grid, x, wingY, z);

        Hollow(grid);
        return grid;
    }

    /// <summary>
    /// Swept wing airplane: fuselage + swept wings + tail.
    /// Wing leading edge sweeps back as Z increases.
    /// </summary>
    public static Grid SweptWingAirplane(
        int fuselageLength = 24, int fuselageHeight = 2, int fuselageWidth = 2,
        int wingSpan = 24, int wingChord = 5, int wingY = 1,
        float sweepAngleDeg = 30f,
        int tailSpan = 8, int tailChord = 2)
    {
        var grid = new Grid();

        // Fuselage
        int fzHalf = fuselageWidth / 2;
        for (int x = 0; x < fuselageLength; x++)
            for (int y = 0; y < fuselageHeight; y++)
                for (int z = -fzHalf; z <= fzHalf; z++)
                    Add(grid, x, y, z);

        // Swept wings: leading edge x = wingStart + |z| * tan(sweep)
        int wingStart = fuselageLength / 3;
        float tanSweep = MathF.Tan(sweepAngleDeg * MathF.PI / 180f);
        int wzHalf = wingSpan / 2;

        for (int z = -wzHalf; z <= wzHalf; z++)
        {
            int sweepOffset = (int)(MathF.Abs(z) * tanSweep);
            int leX = wingStart + sweepOffset;

            // Taper: reduce chord toward tip
            float spanFraction = MathF.Abs(z) / (float)wzHalf;
            int localChord = System.Math.Max(1, (int)(wingChord * (1f - 0.5f * spanFraction)));

            for (int x = leX; x < leX + localChord; x++)
                Add(grid, x, wingY, z);
        }

        // Tail
        int tailStart = fuselageLength - tailChord - 1;
        int tzHalf = tailSpan / 2;
        for (int x = tailStart; x < tailStart + tailChord; x++)
            for (int z = -tzHalf; z <= tzHalf; z++)
                Add(grid, x, wingY, z);

        Hollow(grid);
        return grid;
    }

    /// <summary>
    /// Delta wing: triangular planform, no separate tail.
    /// Leading edge fans out from the nose.
    /// </summary>
    public static Grid DeltaWing(
        int length = 20, int maxSpan = 16, int fuselageHeight = 2, int fuselageWidth = 2)
    {
        var grid = new Grid();

        // Fuselage spine
        int fzHalf = fuselageWidth / 2;
        for (int x = 0; x < length; x++)
            for (int y = 0; y < fuselageHeight; y++)
                for (int z = -fzHalf; z <= fzHalf; z++)
                    Add(grid, x, y, z);

        // Delta wing: span grows linearly from nose to tail
        for (int x = 0; x < length; x++)
        {
            float fraction = (float)x / (length - 1);
            int halfSpan = (int)(maxSpan * 0.5f * fraction);

            for (int z = -halfSpan; z <= halfSpan; z++)
                Add(grid, x, 0, z);
        }

        Hollow(grid);
        return grid;
    }

    /// <summary>
    /// Flying wing / blended wing body: thick center, thin tips, no fuselage.
    /// </summary>
    public static Grid FlyingWing(int span = 30, int rootChord = 10, int tipChord = 3, int rootHeight = 3)
    {
        var grid = new Grid();
        int halfSpan = span / 2;

        for (int z = -halfSpan; z <= halfSpan; z++)
        {
            float spanFrac = MathF.Abs(z) / (float)halfSpan;
            int chord = (int)(rootChord + (tipChord - rootChord) * spanFrac);
            int height = System.Math.Max(1, (int)(rootHeight * (1f - 0.7f * spanFrac)));

            // Sweep: leading edge moves back toward tip
            int leX = (int)(spanFrac * (rootChord - tipChord) * 0.5f);

            for (int x = leX; x < leX + chord; x++)
                for (int y = 0; y < height; y++)
                    Add(grid, x, y, z);
        }

        Hollow(grid);
        return grid;
    }

    /// <summary>
    /// Rocket: tall narrow cylinder-ish shape (octagonal cross-section from blocks).
    /// </summary>
    public static Grid Rocket(int height = 30, int radius = 3)
    {
        var grid = new Grid();
        float r2 = (radius + 0.5f) * (radius + 0.5f);

        for (int y = 0; y < height; y++)
        {
            // Taper nose: top 20% narrows
            float localR2 = r2;
            float fraction = (float)y / height;
            if (fraction > 0.8f)
            {
                float taper = 1f - (fraction - 0.8f) / 0.2f;
                localR2 = r2 * taper * taper;
            }

            for (int x = -radius; x <= radius; x++)
                for (int z = -radius; z <= radius; z++)
                    if (x * x + z * z <= localR2)
                        Add(grid, x, y, z);
        }

        Hollow(grid);
        return grid;
    }

    /// <summary>
    /// Flat plate: single-block-thick rectangle. Pure lifting surface for testing.
    /// </summary>
    public static Grid FlatPlate(int chord, int span)
    {
        var grid = new Grid();
        int halfSpan = span / 2;
        for (int x = 0; x < chord; x++)
            for (int z = -halfSpan; z <= halfSpan; z++)
                Add(grid, x, 0, z);
        return grid;
    }

    /// <summary>
    /// Staircase slope: steps going up in +Y as X increases.
    /// Each step is stepWidth blocks in X, 1 block rise in Y, span blocks in Z.
    /// Tests whether smoothing correctly recovers diagonal slope normals.
    /// </summary>
    /// <summary>
    /// Dart: aerodynamically stable shape. Heavy/wide nose tapering to thin tail with fins.
    /// CoM is well forward of CoP, ensuring restoring torque at positive AoA.
    ///
    /// Nose:  wide blunt section (lots of mass forward)
    /// Body:  linearly tapers from nose to thin tail
    /// Fins:  4 flat stabilizer fins at the very rear (cross pattern)
    /// </summary>
    public static Grid Dart(
        int length = 30, int noseRadius = 4, int tailRadius = 1,
        int finSpan = 6, int finChord = 4, int finThickness = 1)
    {
        var grid = new Grid();

        // Main body: circular cross-section tapering from noseRadius to tailRadius
        for (int x = 0; x < length; x++)
        {
            float frac = (float)x / (length - 1);
            // Radius tapers linearly
            float r = noseRadius + (tailRadius - noseRadius) * frac;
            float r2 = (r + 0.5f) * (r + 0.5f);

            for (int y = -(int)MathF.Ceiling(r); y <= (int)MathF.Ceiling(r); y++)
                for (int z = -(int)MathF.Ceiling(r); z <= (int)MathF.Ceiling(r); z++)
                    if (y * y + z * z <= r2)
                        Add(grid, x, y, z);
        }

        // 4 stabilizer fins at the rear (last finChord blocks)
        int finStart = length - finChord;

        // Vertical fins (top + bottom): extend in Y
        for (int x = finStart; x < length; x++)
            for (int dy = 1; dy <= finSpan; dy++)
            {
                for (int dz = 0; dz < finThickness; dz++)
                {
                    Add(grid, x, dy, dz);   // top fin
                    Add(grid, x, -dy, dz);  // bottom fin
                }
            }

        // Horizontal fins (left + right): extend in Z
        for (int x = finStart; x < length; x++)
            for (int dz = 1; dz <= finSpan; dz++)
            {
                for (int dy = 0; dy < finThickness; dy++)
                {
                    Add(grid, x, dy, dz);    // starboard fin
                    Add(grid, x, dy, -dz);   // port fin
                }
            }

        Hollow(grid);
        return grid;
    }

    /// <summary>
    /// Reentry capsule: blunt heat-shield base at X=0, conical/tapered body narrowing toward X=length.
    /// Like Apollo/Orion — wide flat bottom, tapering sidewalls, narrow top.
    /// Convention: X=forward means the heat shield faces the airflow during reentry.
    /// </summary>
    public static Grid Capsule(int length = 12, int baseRadius = 6, int topRadius = 2)
    {
        var grid = new Grid();

        for (int x = 0; x < length; x++)
        {
            float frac = (float)x / (length - 1);
            // Linear taper from baseRadius to topRadius
            float r = baseRadius + (topRadius - baseRadius) * frac;
            float r2 = (r + 0.5f) * (r + 0.5f);

            for (int y = -(int)MathF.Ceiling(r); y <= (int)MathF.Ceiling(r); y++)
                for (int z = -(int)MathF.Ceiling(r); z <= (int)MathF.Ceiling(r); z++)
                    if (y * y + z * z <= r2)
                        Add(grid, x, y, z);
        }

        Hollow(grid);
        return grid;
    }

    /// <summary>
    /// Detailed airliner-style airplane with:
    ///   - Circular-cross-section fuselage that tapers at nose and tail
    ///   - Swept, tapered wings with 2-3 block thickness (airfoil-like)
    ///   - Horizontal stabilizer (swept, tapered)
    ///   - Vertical tail fin
    ///
    /// Much higher fidelity than SimpleAirplane for pressure visualization.
    /// ~4000-6000 blocks depending on parameters.
    /// </summary>
    public static Grid Airliner(
        int fuselageLength = 48, int fuselageRadius = 3,
        int wingSpan = 40, int wingRootChord = 8, int wingTipChord = 3,
        float wingSweepDeg = 28f, int wingRootThickness = 3, int wingY = 0,
        int hStabSpan = 14, int hStabRootChord = 5, int hStabTipChord = 2, float hStabSweepDeg = 35f,
        int vTailHeight = 8, int vTailRootChord = 7, int vTailTipChord = 3, float vTailSweepDeg = 40f)
    {
        var grid = new Grid();

        // ── Fuselage: circular cross-section with nose/tail tapering ──
        int noseLen = fuselageLength / 5;     // front 20% tapers
        int tailLen = fuselageLength / 4;     // rear 25% tapers
        int tailStart = fuselageLength - tailLen;

        for (int x = 0; x < fuselageLength; x++)
        {
            float r = fuselageRadius;

            // Nose taper: ogive-like curve
            if (x < noseLen)
            {
                float t = (float)x / noseLen;
                r = fuselageRadius * MathF.Sqrt(t); // sqrt gives a blunt-ish nose
            }
            // Tail taper: linear to ~40% radius
            else if (x >= tailStart)
            {
                float t = (float)(x - tailStart) / tailLen;
                r = fuselageRadius * (1f - 0.6f * t);
            }

            float r2 = (r + 0.4f) * (r + 0.4f);
            int ir = (int)MathF.Ceiling(r);
            for (int y = -ir; y <= ir; y++)
                for (int z = -ir; z <= ir; z++)
                    if (y * y + z * z <= r2)
                        Add(grid, x, y, z);
        }

        // ── Main wings: swept, tapered, with thickness ──
        float tanSweep = MathF.Tan(wingSweepDeg * MathF.PI / 180f);
        int wingAttach = fuselageLength * 35 / 100; // 35% along fuselage
        int halfWing = wingSpan / 2;

        for (int z = -halfWing; z <= halfWing; z++)
        {
            int absZ = System.Math.Abs(z);
            if (absZ <= fuselageRadius) continue; // skip fuselage zone

            float spanFrac = (float)(absZ - fuselageRadius) / (halfWing - fuselageRadius);
            int chord = System.Math.Max(2, (int)(wingRootChord + (wingTipChord - wingRootChord) * spanFrac));
            int thickness = System.Math.Max(1, (int)(wingRootThickness * (1f - 0.7f * spanFrac)));
            int leX = wingAttach + (int)(absZ * tanSweep);

            // Wing cross-section: thickest at ~30% chord, tapers to trailing edge
            for (int dx = 0; dx < chord; dx++)
            {
                float chordFrac = (float)dx / chord;
                // NACA-like thickness distribution (simplified)
                float thickFrac = chordFrac < 0.3f
                    ? chordFrac / 0.3f
                    : 1f - (chordFrac - 0.3f) / 0.7f;
                int localThick = System.Math.Max(1, (int)(thickness * thickFrac + 0.5f));
                int halfT = localThick / 2;

                for (int dy = -halfT; dy <= halfT; dy++)
                    Add(grid, leX + dx, wingY + dy, z);
            }
        }

        // ── Horizontal stabilizer: swept, tapered, 1 block thick ──
        float tanHStab = MathF.Tan(hStabSweepDeg * MathF.PI / 180f);
        int hStabAttach = fuselageLength - hStabRootChord - 2;
        int halfHStab = hStabSpan / 2;

        for (int z = -halfHStab; z <= halfHStab; z++)
        {
            int absZ = System.Math.Abs(z);
            if (absZ <= 1) continue; // skip fuselage spine

            float spanFrac = (float)absZ / halfHStab;
            int chord = System.Math.Max(1, (int)(hStabRootChord + (hStabTipChord - hStabRootChord) * spanFrac));
            int leX = hStabAttach + (int)(absZ * tanHStab);

            for (int dx = 0; dx < chord; dx++)
                Add(grid, leX + dx, wingY, z);
        }

        // ── Vertical tail fin: swept, tapered ──
        float tanVTail = MathF.Tan(vTailSweepDeg * MathF.PI / 180f);
        int vTailAttach = fuselageLength - vTailRootChord - 2;

        for (int dy = 1; dy <= vTailHeight; dy++)
        {
            float heightFrac = (float)dy / vTailHeight;
            int chord = System.Math.Max(1, (int)(vTailRootChord + (vTailTipChord - vTailRootChord) * heightFrac));
            int leX = vTailAttach + (int)(dy * tanVTail);

            for (int dx = 0; dx < chord; dx++)
                Add(grid, leX + dx, dy + fuselageRadius, 0);
        }

        Hollow(grid);
        return grid;
    }

    public static Grid StaircaseSlope(int steps = 10, int stepWidth = 2, int span = 20)
    {
        var grid = new Grid();
        int halfSpan = span / 2;

        for (int s = 0; s < steps; s++)
        {
            int xStart = s * stepWidth;
            int y = s;

            // Fill solid below this step (so it's a solid ramp, not floating steps)
            for (int yy = 0; yy <= y; yy++)
                for (int x = xStart; x < xStart + stepWidth; x++)
                    for (int z = -halfSpan; z <= halfSpan; z++)
                        Add(grid, x, yy, z);
        }

        return grid;
    }
}
