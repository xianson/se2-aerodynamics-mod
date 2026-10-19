using System.Numerics;
using Aero.Core.Grids;
using Aero.Core.Math;

namespace Aero.Core.Aero;

/// <summary>
/// Surface provider that replicates SE2's water-drag corner-based approach.
///
/// This exists for benchmarking SE2's method through the same pipeline as our
/// real providers (CubeSurfaceProvider, etc.), so we can measure exactly how
/// much accuracy and performance we gain from proper exposure detection.
///
/// SE2's approach (from decompiled water drag):
///   - Iterates ALL blocks with no exposure/neighbor check
///   - For each cell, emits 8 "faces" — one per AABB corner
///   - The "normal" for each corner is normalize(corner - blockCenter),
///     always a diagonal: approximately (±0.577, ±0.577, ±0.577)
///   - Face area = (total block surface area) / 8 = (6 * blockSize²) / 8
///   - Position = the corner's world-space position
///
/// This means interior blocks contribute drag identically to exterior blocks,
/// and all normals are diagonal (never axis-aligned). The result is a crude
/// omnidirectional drag that scales linearly with block count.
/// </summary>
public class Se2CornerSurfaceProvider : ISurfaceProvider
{
    /// <summary>
    /// The 8 corner offsets from block center, in units of ±0.5.
    /// Each one, when normalized, gives one of the 8 diagonal normals.
    /// </summary>
    private static readonly Vector3[] CornerOffsets =
    {
        new(-0.5f, -0.5f, -0.5f),
        new(-0.5f, -0.5f,  0.5f),
        new(-0.5f,  0.5f, -0.5f),
        new(-0.5f,  0.5f,  0.5f),
        new( 0.5f, -0.5f, -0.5f),
        new( 0.5f, -0.5f,  0.5f),
        new( 0.5f,  0.5f, -0.5f),
        new( 0.5f,  0.5f,  0.5f),
    };

    /// <summary>
    /// The 8 unique diagonal normals (unit vectors toward each corner of a cube).
    /// 1/sqrt(3) ≈ 0.57735.
    /// </summary>
    private static readonly Vector3[] DiagonalNormals;

    static Se2CornerSurfaceProvider()
    {
        DiagonalNormals = new Vector3[8];
        for (int i = 0; i < 8; i++)
            DiagonalNormals[i] = Vector3.Normalize(CornerOffsets[i]);
    }

    private readonly List<SurfaceFace> _faces = new();
    private readonly List<NormalGroup> _groups = new();
    private float _blockSize;

    public string Name => "SE2 Corner (water-drag replica)";
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

        // SE2: face area = total cube surface area / 8 = 6 * blockSize² / 8
        float cornerArea = 6.0f * blockSize * blockSize / 8.0f;

        // Accumulators for the 8 normal groups
        var totalAreas = new float[8];
        var weightedPositions = new Vector3[8];
        var counts = new int[8];

        foreach (var block in grid.Blocks.Values)
        {
            foreach (var cell in block.EnumerateCells())
            {
                // Cell center in world space
                float cx = (cell.X + 0.5f) * blockSize;
                float cy = (cell.Y + 0.5f) * blockSize;
                float cz = (cell.Z + 0.5f) * blockSize;
                var center = new Vector3(cx, cy, cz);

                for (int c = 0; c < 8; c++)
                {
                    // Corner position = center + offset * blockSize
                    var pos = center + CornerOffsets[c] * blockSize;
                    var normal = DiagonalNormals[c];

                    _faces.Add(new SurfaceFace(pos, normal, cornerArea));

                    totalAreas[c] += cornerArea;
                    weightedPositions[c] += pos * cornerArea;
                    counts[c]++;
                }
            }
        }

        RebuildGroups(totalAreas, weightedPositions, counts);
    }

    // ─── Event handlers (no incremental optimization) ─────────────

    public void OnBlocksChanged(Grid grid, in BlocksChangedArgs args)
        => Build(grid, _blockSize);

    public void OnGridSplit(Grid grid, in GridSplitArgs args)
        => Build(grid, _blockSize);

    // ─── Internals ────────────────────────────────────────────────

    private void RebuildGroups(float[] totalAreas, Vector3[] weightedPositions, int[] counts)
    {
        _groups.Clear();
        for (int i = 0; i < 8; i++)
        {
            if (counts[i] == 0)
                continue;

            _groups.Add(new NormalGroup(
                DiagonalNormals[i],
                totalAreas[i],
                weightedPositions[i] / totalAreas[i],
                counts[i]));
        }
    }
}
