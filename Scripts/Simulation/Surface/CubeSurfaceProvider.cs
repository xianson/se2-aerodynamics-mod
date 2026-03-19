#pragma warning disable
namespace AeroMod;

/// <summary>
/// Surface provider for pure cube blocks.
///
/// Finds exposed faces by checking 6 neighbors per cell via IGridAccessor.
/// All normals are axis-aligned, all face areas are blockSize².
/// Fast: O(N × 6) with O(1) neighbor lookups.
/// </summary>
public class CubeSurfaceProvider : ISurfaceProvider
{
    private static readonly List<Vector3I> DirOffsets = new List<Vector3I>
    {
        new( 1, 0, 0), new(-1, 0, 0),
        new( 0, 1, 0), new( 0,-1, 0),
        new( 0, 0, 1), new( 0, 0,-1),
    };

    private static readonly List<Vector3> DirNormals = new List<Vector3>
    {
        Vector3.UnitX, -Vector3.UnitX,
        Vector3.UnitY, -Vector3.UnitY,
        Vector3.UnitZ, -Vector3.UnitZ,
    };

    private readonly List<SurfaceFace> _faces = new();
    private readonly List<NormalGroup> _groups = new();
    private float _blockSize;

    public string Name => "Cube (6-neighbor)";
    public IReadOnlyList<SurfaceFace> Faces => _faces;
    public IReadOnlyList<NormalGroup> NormalGroups => _groups;
    public int FaceCount => _faces.Count;
    public int GroupCount => _groups.Count;
    public float BlockSize => _blockSize;
    public int Version { get; private set; }

    // ─── Full rebuild ─────────────────────────────────────────────

    public void Build(IGridAccessor grid, float blockSize)
    {
        _blockSize = blockSize;
        _faces.Clear();
        _groups.Clear();

        // Each cell face is 0.25m × 0.25m = 0.0625 m²
        const float CellSize = 0.25f;
        float faceArea = CellSize * CellSize;

        var accum = new List<float> { 0f, 0f, 0f, 0f, 0f, 0f };
        var weighted = new List<Vector3> { Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero };
        var counts = new List<int> { 0, 0, 0, 0, 0, 0 };
        var rawSxx = new List<float> { 0f, 0f, 0f, 0f, 0f, 0f }; var rawSyy = new List<float> { 0f, 0f, 0f, 0f, 0f, 0f }; var rawSzz = new List<float> { 0f, 0f, 0f, 0f, 0f, 0f };
        var rawSxy = new List<float> { 0f, 0f, 0f, 0f, 0f, 0f }; var rawSxz = new List<float> { 0f, 0f, 0f, 0f, 0f, 0f }; var rawSyz = new List<float> { 0f, 0f, 0f, 0f, 0f, 0f };

        foreach (var cell in grid.EnumerateOccupiedCells())
        {
            for (int d = 0; d < 6; d++)
            {
                if (grid.IsCellOccupied(cell + DirOffsets[d]))
                    continue;

                var pos = CellFaceCenter(cell, DirOffsets[d], blockSize);
                _faces.Add(new SurfaceFace(pos, DirNormals[d], faceArea));

                accum[d] += faceArea;
                weighted[d] += pos * faceArea;
                counts[d]++;

                rawSxx[d] += faceArea * pos.X * pos.X;
                rawSyy[d] += faceArea * pos.Y * pos.Y;
                rawSzz[d] += faceArea * pos.Z * pos.Z;
                rawSxy[d] += faceArea * pos.X * pos.Y;
                rawSxz[d] += faceArea * pos.X * pos.Z;
                rawSyz[d] += faceArea * pos.Y * pos.Z;
            }
        }

        RebuildGroups(accum, weighted, counts, rawSxx, rawSyy, rawSzz, rawSxy, rawSxz, rawSyz);
        Version++;
    }

    // ─── Events (delegate to full rebuild) ────────────────────

    public void OnBlocksChanged(IGridAccessor grid, in BlocksChangedArgs args)
        => Build(grid, _blockSize);

    // ─── Internals ────────────────────────────────────────────────

    private void RebuildGroups(List<float> accum, List<Vector3> weighted, List<int> counts,
        List<float> rSxx, List<float> rSyy, List<float> rSzz,
        List<float> rSxy, List<float> rSxz, List<float> rSyz)
    {
        _groups.Clear();
        for (int d = 0; d < 6; d++)
        {
            if (counts[d] == 0) continue;

            float a = accum[d];
            var centroid = weighted[d] / a;

            // Parallel axis theorem: S_centroid = S_origin - A * centroid ⊗ centroid
            float sxx = rSxx[d] - a * centroid.X * centroid.X;
            float syy = rSyy[d] - a * centroid.Y * centroid.Y;
            float szz = rSzz[d] - a * centroid.Z * centroid.Z;
            float sxy = rSxy[d] - a * centroid.X * centroid.Y;
            float sxz = rSxz[d] - a * centroid.X * centroid.Z;
            float syz = rSyz[d] - a * centroid.Y * centroid.Z;

            _groups.Add(new NormalGroup(
                DirNormals[d], a, centroid, counts[d],
                sxx, syy, szz, sxy, sxz, syz));
        }
    }

    private static Vector3 CellFaceCenter(Vector3I cell, Vector3I faceDir, float blockSize)
    {
        // SE2 cells are 0.25m each. GridToLocal = cell * 0.25.
        // Cell center = cell * 0.25 (grid-local meters).
        // Face center = cell center + faceDir * half cell.
        const float CellSize = 0.25f;
        const float HalfCell = 0.125f;

        float cx = cell.X * CellSize;
        float cy = cell.Y * CellSize;
        float cz = cell.Z * CellSize;

        return new Vector3(
            cx + faceDir.X * HalfCell,
            cy + faceDir.Y * HalfCell,
            cz + faceDir.Z * HalfCell);
    }
}
