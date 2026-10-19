using System.Numerics;
using Aero.Core.Grids;
using Aero.Core.Math;

namespace Aero.Core.Aero;

/// <summary>
/// An exposed face on the grid surface.
/// </summary>
public readonly struct SurfaceFace
{
    /// <summary>World-space center of the face (meters).</summary>
    public readonly Vector3 Position;

    /// <summary>Outward unit normal.</summary>
    public readonly Vector3 Normal;

    /// <summary>Face area in m².</summary>
    public readonly float Area;

    public SurfaceFace(Vector3 position, Vector3 normal, float area)
    {
        Position = position;
        Normal = normal;
        Area = area;
    }
}

/// <summary>
/// Faces grouped by normal direction for O(K) per-frame force computation.
/// </summary>
public readonly struct NormalGroup
{
    public readonly Vector3 Normal;
    public readonly float TotalArea;

    /// <summary>Area-weighted centroid of all faces in this group (meters from grid origin).</summary>
    public readonly Vector3 Centroid;

    public readonly int FaceCount;

    /// <summary>
    /// Area-weighted second moment of face positions about the centroid (m⁴).
    /// Symmetric 3×3 tensor: S = Σ A_i · (r_i - centroid) ⊗ (r_i - centroid).
    /// Used for O(K) pressure gradient torque correction.
    /// Zero if the surface provider doesn't compute it.
    /// </summary>
    public readonly float Sxx, Syy, Szz, Sxy, Sxz, Syz;

    public NormalGroup(Vector3 normal, float totalArea, Vector3 centroid, int faceCount)
    {
        Normal = normal;
        TotalArea = totalArea;
        Centroid = centroid;
        FaceCount = faceCount;
    }

    public NormalGroup(Vector3 normal, float totalArea, Vector3 centroid, int faceCount,
        float sxx, float syy, float szz, float sxy, float sxz, float syz)
    {
        Normal = normal;
        TotalArea = totalArea;
        Centroid = centroid;
        FaceCount = faceCount;
        Sxx = sxx; Syy = syy; Szz = szz;
        Sxy = sxy; Sxz = sxz; Syz = syz;
    }

    /// <summary>S · v (matrix-vector multiply with the second moment tensor).</summary>
    public Vector3 SecondMomentMul(Vector3 v) => new(
        Sxx * v.X + Sxy * v.Y + Sxz * v.Z,
        Sxy * v.X + Syy * v.Y + Syz * v.Z,
        Sxz * v.X + Syz * v.Y + Szz * v.Z);

    /// <summary>Whether this group has precomputed second moments.</summary>
    public bool HasSecondMoment => Sxx != 0 || Syy != 0 || Szz != 0;
}

/// <summary>
/// Surface provider for pure cube blocks.
///
/// Finds exposed faces by checking 6 neighbors per cell in Grid's cell map.
/// All normals are axis-aligned, all face areas are blockSize².
/// Fast: O(N × 6) with O(1) neighbor lookups.
///
/// Supports incremental updates for block add/remove — only re-examines
/// the affected cells and their immediate neighbors.
///
/// Limitations:
///   - Treats all blocks as full cubes (no slope/corner geometry)
///   - No shadowing or occlusion
/// </summary>
public class CubeSurfaceProvider : ISurfaceProvider
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

    private readonly List<SurfaceFace> _faces = new();
    private readonly List<NormalGroup> _groups = new();
    private float _blockSize;

    public string Name => "Cube (6-neighbor)";
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

        float faceArea = blockSize * blockSize;

        var accum = new float[6];
        var weighted = new Vector3[6];
        var counts = new int[6];

        // Raw second moments about origin: Σ A_i * r_i ⊗ r_i
        // Converted to about-centroid via parallel axis theorem after the loop.
        var rawSxx = new float[6]; var rawSyy = new float[6]; var rawSzz = new float[6];
        var rawSxy = new float[6]; var rawSxz = new float[6]; var rawSyz = new float[6];

        foreach (var block in grid.Blocks.Values)
        {
            foreach (var cell in block.EnumerateCells())
            {
                for (int d = 0; d < 6; d++)
                {
                    if (grid.GetBlockAt(cell + DirOffsets[d]) != null)
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
        }

        RebuildGroups(accum, weighted, counts, rawSxx, rawSyy, rawSzz, rawSxy, rawSxz, rawSyz);
    }

    // ─── Events (all delegate to full rebuild) ─────────────────

    public void OnBlocksChanged(Grid grid, in BlocksChangedArgs args)
        => Build(grid, _blockSize);

    public void OnGridSplit(Grid grid, in GridSplitArgs args)
        => Build(grid, _blockSize);

    // ─── Internals ────────────────────────────────────────────────

    private void RebuildGroups(float[] accum, Vector3[] weighted, int[] counts,
        float[] rSxx, float[] rSyy, float[] rSzz,
        float[] rSxy, float[] rSxz, float[] rSyz)
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
        float cx = (cell.X + 0.5f) * blockSize;
        float cy = (cell.Y + 0.5f) * blockSize;
        float cz = (cell.Z + 0.5f) * blockSize;

        return new Vector3(
            cx + faceDir.X * 0.5f * blockSize,
            cy + faceDir.Y * 0.5f * blockSize,
            cz + faceDir.Z * 0.5f * blockSize);
    }
}

/// <summary>
/// Backwards-compatible alias. Use CubeSurfaceProvider directly in new code.
/// </summary>
public class SurfaceCache : CubeSurfaceProvider { }
