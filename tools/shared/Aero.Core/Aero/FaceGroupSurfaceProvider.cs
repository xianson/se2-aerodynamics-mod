using System.Numerics;
using Aero.Core.Grids;
using Aero.Core.Math;

namespace Aero.Core.Aero;

/// <summary>
/// Surface provider that merges coplanar adjacent faces into larger rectangular patches.
///
/// <para>
/// The algorithm works in two phases:
/// <list type="number">
///   <item>
///     <description>
///       <b>Exposure detection</b> — identical to <see cref="CubeSurfaceProvider"/>:
///       for every occupied cell, check the 6 axis-aligned neighbors. A face is exposed
///       when the neighbor cell is empty.
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>Greedy rectangle merging</b> — for each of the 6 normal directions, exposed
///       faces are grouped by the plane they lie on (e.g., all +X faces at x=5 lie in
///       the same YZ plane). Within each plane, a greedy row-then-column merge finds
///       maximal rectangular patches:
///       <list type="bullet">
///         <item>Sort faces by their 2D position within the plane.</item>
///         <item>Sweep rows to form horizontal strips of consecutive faces.</item>
///         <item>Merge vertically adjacent strips that share the same column span.</item>
///       </list>
///       Each merged rectangle becomes a single <see cref="SurfaceFace"/> whose area
///       equals the sum of constituent face areas and whose position is the rectangle's
///       centroid.
///     </description>
///   </item>
/// </list>
/// </para>
///
/// <para>
/// <b>Benefits:</b> Dramatically reduces face count on large flat surfaces, which
/// directly improves per-face torque computation performance. A 10x10 flat plate that
/// would produce 100 individual faces with <see cref="CubeSurfaceProvider"/> becomes
/// a single merged face here.
/// </para>
///
/// <para>
/// <b>Limitations:</b>
/// <list type="bullet">
///   <item>Treats all blocks as full cubes (no slope/corner geometry).</item>
///   <item>Greedy merging does not guarantee minimum rectangle count (NP-hard),
///         but captures the vast majority of merges in practice.</item>
/// </list>
/// </para>
/// </summary>
public class FaceGroupSurfaceProvider : ISurfaceProvider
{
    private static readonly Vector3I[] DirOffsets =
    {
        new( 1, 0, 0), new(-1, 0, 0),
        new( 0, 1, 0), new( 0,-1, 0),
        new( 0, 0, 1), new( 0, 0,-1),
    };

    private static readonly Vector3[] DirNormals =
    {
        Vector3.UnitX, -Vector3.UnitX,
        Vector3.UnitY, -Vector3.UnitY,
        Vector3.UnitZ, -Vector3.UnitZ,
    };

    /// <summary>
    /// For each normal direction index (0..5), defines which two axes form the 2D
    /// plane that coplanar faces are projected onto, and which axis is the "depth"
    /// (plane-grouping) axis.
    ///
    /// PlaneAxis: the axis perpendicular to the face (used to group faces into planes).
    /// RowAxis / ColAxis: the two axes that span the plane (used for 2D merging).
    /// </summary>
    private static readonly (int PlaneAxis, int RowAxis, int ColAxis)[] AxisMapping =
    {
        // +X / -X faces → plane grouped by X, merge in YZ
        (0, 1, 2), (0, 1, 2),
        // +Y / -Y faces → plane grouped by Y, merge in XZ
        (1, 0, 2), (1, 0, 2),
        // +Z / -Z faces → plane grouped by Z, merge in XY
        (2, 0, 1), (2, 0, 1),
    };

    private readonly List<SurfaceFace> _faces = new();
    private readonly List<NormalGroup> _groups = new();
    private float _blockSize;

    public string Name => "FaceGroup (merged)";
    public IReadOnlyList<SurfaceFace> Faces => _faces;
    public IReadOnlyList<NormalGroup> NormalGroups => _groups;
    public int FaceCount => _faces.Count;
    public int GroupCount => _groups.Count;
    public float BlockSize => _blockSize;

    // ─── Full rebuild ─────────────────────────────────────────────

    public void Build(Grid grid, float blockSize)
    {
        _blockSize = blockSize;
        _faces.Clear();
        _groups.Clear();

        // Phase 1: find all exposed faces, grouped by normal direction.
        // Each entry is the cell coordinate of the exposed face.
        var exposedByDir = new List<Vector3I>[6];
        for (int d = 0; d < 6; d++)
            exposedByDir[d] = new List<Vector3I>();

        foreach (var block in grid.Blocks.Values)
        {
            foreach (var cell in block.EnumerateCells())
            {
                for (int d = 0; d < 6; d++)
                {
                    if (grid.GetBlockAt(cell + DirOffsets[d]) == null)
                        exposedByDir[d].Add(cell);
                }
            }
        }

        // Phase 2: merge coplanar faces and emit SurfaceFaces + NormalGroups.
        float faceArea = blockSize * blockSize;

        for (int d = 0; d < 6; d++)
        {
            var exposed = exposedByDir[d];
            if (exposed.Count == 0)
                continue;

            var merged = MergeCoplanarFaces(exposed, d, blockSize, faceArea);

            float totalArea = 0f;
            Vector3 weightedPos = Vector3.Zero;

            foreach (var face in merged)
            {
                _faces.Add(face);
                totalArea += face.Area;
                weightedPos += face.Position * face.Area;
            }

            _groups.Add(new NormalGroup(
                DirNormals[d],
                totalArea,
                weightedPos / totalArea,
                merged.Count));
        }
    }

    // ─── Incremental updates (fall back to full rebuild) ──────────

    public void OnBlocksChanged(Grid grid, in BlocksChangedArgs args)
        => Build(grid, _blockSize);

    public void OnGridSplit(Grid grid, in GridSplitArgs args)
        => Build(grid, _blockSize);

    // ─── Merging ──────────────────────────────────────────────────

    /// <summary>
    /// Merges a list of exposed face cells (all sharing the same normal direction)
    /// into larger rectangular patches via greedy row-then-column merging.
    /// </summary>
    private List<SurfaceFace> MergeCoplanarFaces(
        List<Vector3I> cells, int dirIndex, float blockSize, float faceArea)
    {
        var (planeAxis, rowAxis, colAxis) = AxisMapping[dirIndex];
        var faceDir = DirOffsets[dirIndex];
        var normal = DirNormals[dirIndex];

        // Group cells by their plane coordinate (the axis perpendicular to the face).
        // All faces in the same plane can potentially merge.
        var planes = new Dictionary<int, List<(int row, int col)>>();

        foreach (var cell in cells)
        {
            int planeCoord = GetAxis(cell, planeAxis);
            int row = GetAxis(cell, rowAxis);
            int col = GetAxis(cell, colAxis);

            if (!planes.TryGetValue(planeCoord, out var list))
            {
                list = new List<(int row, int col)>();
                planes[planeCoord] = list;
            }
            list.Add((row, col));
        }

        var result = new List<SurfaceFace>();

        foreach (var (planeCoord, facesInPlane) in planes)
        {
            var rectangles = GreedyMerge2D(facesInPlane);

            foreach (var rect in rectangles)
            {
                // rect: (minRow, minCol, width, height)
                // width is along rowAxis, height is along colAxis
                int cellCount = rect.Width * rect.Height;
                float area = cellCount * faceArea;

                // Compute centroid of the merged rectangle in world space.
                // The centroid is at the center of the rectangular region.
                float centerRow = rect.MinRow + (rect.Width - 1) * 0.5f;
                float centerCol = rect.MinCol + (rect.Height - 1) * 0.5f;

                // Build the cell-space centroid, then convert to world position.
                // The face center is offset from the cell center by half a block
                // in the normal direction.
                float cellPlane = planeCoord + 0.5f;
                float cellRow = centerRow + 0.5f;
                float cellCol = centerCol + 0.5f;

                // Map back to XYZ using axis indices.
                var pos = new float[3];
                pos[planeAxis] = cellPlane * blockSize + GetAxis(faceDir, planeAxis) * 0.5f * blockSize;
                pos[rowAxis] = cellRow * blockSize;
                pos[colAxis] = cellCol * blockSize;

                result.Add(new SurfaceFace(
                    new Vector3(pos[0], pos[1], pos[2]),
                    normal,
                    area));
            }
        }

        return result;
    }

    /// <summary>
    /// Greedy 2D rectangle merge on a set of integer (row, col) positions.
    ///
    /// Algorithm:
    /// 1. Build a hash set of all occupied positions.
    /// 2. Sort positions by (row, col).
    /// 3. For each unvisited position, extend right as far as possible to form a strip.
    /// 4. Then extend that strip downward (increasing col) as long as the entire row
    ///    span is occupied.
    /// 5. Mark all consumed positions as visited.
    /// </summary>
    private static List<MergedRect> GreedyMerge2D(List<(int row, int col)> positions)
    {
        var occupied = new HashSet<long>(positions.Count);
        foreach (var (r, c) in positions)
            occupied.Add(PackKey(r, c));

        // Sort by row, then col for deterministic sweep order.
        positions.Sort((a, b) =>
        {
            int cmp = a.row.CompareTo(b.row);
            return cmp != 0 ? cmp : a.col.CompareTo(b.col);
        });

        var visited = new HashSet<long>(positions.Count);
        var result = new List<MergedRect>();

        foreach (var (startRow, startCol) in positions)
        {
            long startKey = PackKey(startRow, startCol);
            if (visited.Contains(startKey))
                continue;

            // Extend along the row axis (increasing row) as far as possible.
            int width = 1;
            while (occupied.Contains(PackKey(startRow + width, startCol)) &&
                   !visited.Contains(PackKey(startRow + width, startCol)))
            {
                width++;
            }

            // Extend along the col axis (increasing col) as far as possible,
            // requiring the full row span to be present.
            int height = 1;
            bool canExtend = true;
            while (canExtend)
            {
                int nextCol = startCol + height;
                for (int r = 0; r < width; r++)
                {
                    long key = PackKey(startRow + r, nextCol);
                    if (!occupied.Contains(key) || visited.Contains(key))
                    {
                        canExtend = false;
                        break;
                    }
                }
                if (canExtend)
                    height++;
            }

            // Mark all cells in this rectangle as visited.
            for (int r = 0; r < width; r++)
                for (int c = 0; c < height; c++)
                    visited.Add(PackKey(startRow + r, startCol + c));

            result.Add(new MergedRect(startRow, startCol, width, height));
        }

        return result;
    }

    /// <summary>
    /// Packs two ints into a single long for use as a hash key.
    /// </summary>
    private static long PackKey(int a, int b) =>
        ((long)a << 32) | (uint)b;

    /// <summary>
    /// Extracts an axis component from a Vector3I by index (0=X, 1=Y, 2=Z).
    /// </summary>
    private static int GetAxis(Vector3I v, int axis) => axis switch
    {
        0 => v.X,
        1 => v.Y,
        2 => v.Z,
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };

    /// <summary>
    /// Represents a merged rectangle in 2D grid space.
    /// </summary>
    private readonly struct MergedRect
    {
        /// <summary>Starting position along the row axis.</summary>
        public readonly int MinRow;

        /// <summary>Starting position along the col axis.</summary>
        public readonly int MinCol;

        /// <summary>Extent along the row axis (number of cells).</summary>
        public readonly int Width;

        /// <summary>Extent along the col axis (number of cells).</summary>
        public readonly int Height;

        public MergedRect(int minRow, int minCol, int width, int height)
        {
            MinRow = minRow;
            MinCol = minCol;
            Width = width;
            Height = height;
        }
    }
}
