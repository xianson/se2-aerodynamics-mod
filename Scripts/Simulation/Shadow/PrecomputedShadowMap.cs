#pragma warning disable
using System;
using System.Runtime.CompilerServices;

namespace AeroMod;

/// <summary>
/// Pre-computed shadow map for 26 canonical directions.
/// Eliminates per-frame shadow spikes by computing all directions upfront
/// (staggered across frames) and interpolating at runtime.
///
/// 26 directions = 6 face centers + 12 edge midpoints + 8 cube corners.
/// On Update(): nearest precomputed direction is selected (O(26) dot products).
/// On block change: all 26 marked dirty, recomputed 1-2 per frame.
/// </summary>
public class PrecomputedShadowMap : IShadowMap
{
    public const int DirCount = 26;
    public static readonly List<Vector3> Directions = new();

    static PrecomputedShadowMap()
    {
        for (int x = -1; x <= 1; x++)
        for (int y = -1; y <= 1; y++)
        for (int z = -1; z <= 1; z++)
        {
            if (x == 0 && y == 0 && z == 0) continue;
            var v = new Vector3(x, y, z);
            Directions.Add(v / v.Length());
        }
    }

    // ─── Configuration ────────────────────────────────────────────

    public int MaxRayLength { get; set; } = 40;
    public int DirsPerFrame { get; set; } = 2;
    public HashSet<Vector3I> WingCells { get; set; }
    public float WakeDecayLength { get; set; } = 10f;

    /// <summary>Hull classifier — if set, only ray-march hull faces.</summary>
    public ManifoldClassifier Manifold { get; set; }

    // ─── Per-direction precomputed data ──────────────────────────

    private readonly List<List<float>> _cache = new();
    private readonly List<bool> _dirty = new();
    private readonly List<bool> _hasData = new();
    private int _faceCount;
    private int _surfaceVersion = -1;
    private int _rebuildCursor;

    // ─── Output ──────────────────────────────────────────────────

    private List<bool> _visibility = new();
    private List<float> _visibilityFactor = new();
    public List<bool> Visibility => _visibility;
    public List<float> VisibilityFactor => _visibilityFactor;
    public int VisibleCount { get; private set; }
    public int ShadowedCount { get; private set; }
    public int Version { get; private set; }
    private int _lastDirIndex = -1;

    public PrecomputedShadowMap()
    {
        for (int i = 0; i < DirCount; i++)
        {
            _cache.Add(new List<float>());
            _dirty.Add(true);
            _hasData.Add(false);
        }
    }

    // ─── IShadowMap ──────────────────────────────────────────────

    public void Update(IGridAccessor grid, ISurfaceProvider provider, Vector3 flowDirection)
    {
        float speed = flowDirection.Length();
        int n = provider.FaceCount;

        if (speed < 0.001f)
        {
            EnsureLists(n);
            for (int i = 0; i < n; i++)
            {
                _visibility[i] = false;
                _visibilityFactor[i] = 0f;
            }
            VisibleCount = 0;
            ShadowedCount = n;
            Version++;
            return;
        }

        if (provider.Version != _surfaceVersion || _faceCount != n)
        {
            _surfaceVersion = provider.Version;
            _faceCount = n;
            InvalidateAll();
        }

        EnsureLists(n);

        // Staggered rebuild: compute up to DirsPerFrame dirty directions
        int computed = 0;
        for (int i = 0; i < DirCount && computed < DirsPerFrame; i++)
        {
            int d = (_rebuildCursor + i) % DirCount;
            if (_dirty[d])
            {
                ComputeDirection(grid, provider, d);
                _dirty[d] = false;
                _hasData[d] = true;
                computed++;
            }
        }
        _rebuildCursor = (_rebuildCursor + computed) % DirCount;

        // Find nearest precomputed direction
        Vector3 flowDir = flowDirection / speed;
        int best = FindNearest(flowDir);

        if (best != _lastDirIndex || computed > 0)
        {
            if (_hasData[best])
            {
                var src = _cache[best];
                int visible = 0, shadowed = 0;
                for (int i = 0; i < n; i++)
                {
                    float v = src[i];
                    _visibilityFactor[i] = v;
                    bool vis = v >= 0.5f;
                    _visibility[i] = vis;
                    if (vis) visible++; else shadowed++;
                }
                VisibleCount = visible;
                ShadowedCount = shadowed;
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    _visibility[i] = true;
                    _visibilityFactor[i] = 1f;
                }
                VisibleCount = n;
                ShadowedCount = 0;
            }

            _lastDirIndex = best;
            Version++;
        }
    }

    // ─── Compute one direction ───────────────────────────────────

    private void ComputeDirection(IGridAccessor grid, ISurfaceProvider provider, int dirIndex)
    {
        var faces = provider.Faces;
        int n = faces.Count;
        Vector3 flowDir = Directions[dirIndex];

        var result = _cache[dirIndex];
        while (result.Count < n) result.Add(0f);

        float blockSize = provider.BlockSize;
        float invBlock = 1f / blockSize;
        int maxSteps = MaxRayLength;
        var wingCells = WingCells;
        float decayLen = WakeDecayLength;

        float dx = -flowDir.X;
        float dy = -flowDir.Y;
        float dz = -flowDir.Z;

        bool hasManifold = Manifold != null && Manifold.IsClassified;

        for (int i = 0; i < n; i++)
        {
            // Skip cavity faces — they'll never be read by hull-only SoA
            if (hasManifold && !Manifold.IsHull(i))
            {
                result[i] = 0f;
                continue;
            }

            var face = faces[i];
            float cosAlpha = -Vector3.Dot(flowDir, face.Normal);

            if (cosAlpha <= 0)
            {
                result[i] = 0f;
                continue;
            }

            float ox = face.Position.X * invBlock;
            float oy = face.Position.Y * invBlock;
            float oz = face.Position.Z * invBlock;

            float cx = ox + dx * 0.6f;
            float cy = oy + dy * 0.6f;
            float cz = oz + dz * 0.6f;

            bool hit = false;
            float hitDist = 0f;
            bool hitWing = false;

            for (int step = 0; step < maxSteps; step++)
            {
                int ix = (int)MathF.Floor(cx);
                int iy = (int)MathF.Floor(cy);
                int iz = (int)MathF.Floor(cz);

                var cellPos = new Vector3I(ix, iy, iz);
                if (grid.IsCellOccupied(cellPos))
                {
                    hit = true;
                    hitDist = step + 1;
                    if (wingCells != null && wingCells.Contains(cellPos))
                        hitWing = true;
                    break;
                }

                cx += dx; cy += dy; cz += dz;
            }

            if (hit)
            {
                if (hitWing && decayLen > 0f)
                    result[i] = 1f - MathF.Exp(-hitDist / decayLen);
                else
                    result[i] = 0f;
            }
            else
            {
                result[i] = 1f;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindNearest(Vector3 dir)
    {
        int best = 0;
        float bestDot = -2f;
        for (int i = 0; i < DirCount; i++)
        {
            float dot = Vector3.Dot(dir, Directions[i]);
            if (dot > bestDot) { bestDot = dot; best = i; }
        }
        return best;
    }

    private void InvalidateAll()
    {
        for (int i = 0; i < DirCount; i++)
        {
            _dirty[i] = true;
            _hasData[i] = false;
        }
        _rebuildCursor = 0;
    }

    private void EnsureLists(int count)
    {
        while (_visibility.Count < count) _visibility.Add(false);
        while (_visibilityFactor.Count < count) _visibilityFactor.Add(0f);
    }
}
