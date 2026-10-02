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

    // Each direction's visibility per face, a byte (0..255 for 0..1): 26 directions x every face, twice a grid
    // (double-buffered) - as floats that was 140 MB for Red Ship.
    private readonly List<byte[]> _cache = new();
    private const float Inv255 = 1f / 255f;
    private static byte Q(float v) => (byte)(v <= 0f ? 0 : v >= 1f ? 255 : (int)(v * 255f + 0.5f));
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
            _cache.Add(System.Array.Empty<byte>());
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

        // The 3 nearest precomputed directions, blended: each weighs by how much nearer than the 4th it is, so a
        // direction joins and leaves the blend at zero weight and visibility moves continuously with the flow.
        // (Snapping to the single nearest one made the faces' drag jump at every boundary between directions:
        // the Jetliner's 25 -> 200 kN at 22.5 degrees angle of attack.)
        Vector3 flowDir = flowDirection / speed;
        int i0 = -1, i1 = -1, i2 = -1, i3 = -1; float d0 = -2, d1 = -2, d2 = -2, d3 = -2;
        for (int i = 0; i < DirCount; i++)
        {
            if (!_hasData[i]) continue;
            float d = Vector3.Dot(flowDir, Directions[i]);
            if (d > d0) { i3 = i2; d3 = d2; i2 = i1; d2 = d1; i1 = i0; d1 = d0; i0 = i; d0 = d; }
            else if (d > d1) { i3 = i2; d3 = d2; i2 = i1; d2 = d1; i1 = i; d1 = d; }
            else if (d > d2) { i3 = i2; d3 = d2; i2 = i; d2 = d; }
            else if (d > d3) { i3 = i; d3 = d; }
        }
        if (i0 < 0)
        {
            if (_lastDirIndex != -2)
            {
                for (int i = 0; i < n; i++) { _visibility[i] = true; _visibilityFactor[i] = 1f; }
                VisibleCount = n; ShadowedCount = 0;
                _lastDirIndex = -2; Version++;
            }
            return;
        }
        float floor = i3 >= 0 ? d3 : (i2 >= 0 ? d2 - 1e-3f : (i1 >= 0 ? d1 - 1e-3f : d0 - 1f));
        float w0 = d0 - floor, w1 = i1 >= 0 ? MathF.Max(0f, d1 - floor) : 0f, w2 = i2 >= 0 ? MathF.Max(0f, d2 - floor) : 0f;
        float sum = w0 + w1 + w2;
        if (sum <= 1e-6f) { w0 = 1f; w1 = w2 = 0f; sum = 1f; }
        w0 /= sum; w1 /= sum; w2 /= sum;
        bool changed = computed > 0 || i0 != _b0 || i1 != _b1 || i2 != _b2
            || MathF.Abs(w0 - _w0) > 0.03f || MathF.Abs(w1 - _w1) > 0.03f || MathF.Abs(w2 - _w2) > 0.03f;
        if (!changed) return;
        _b0 = i0; _b1 = i1; _b2 = i2; _w0 = w0; _w1 = w1; _w2 = w2;
        var c0 = _cache[i0];
        var c1 = w1 > 0f ? _cache[i1] : null;
        var c2 = w2 > 0f ? _cache[i2] : null;
        int vis = 0;
        float s0 = w0 * Inv255, s1 = w1 * Inv255, s2 = w2 * Inv255;
        for (int i = 0; i < n; i++)
        {
            float v = s0 * c0[i];
            if (c1 != null) v += s1 * c1[i];
            if (c2 != null) v += s2 * c2[i];
            _visibilityFactor[i] = v;
            bool b = v >= 0.5f;
            _visibility[i] = b;
            if (b) vis++;
        }
        VisibleCount = vis; ShadowedCount = n - vis;
        _lastDirIndex = i0;
        Version++;
    }

    private int _b0 = -1, _b1 = -1, _b2 = -1;

    private float _w0, _w1, _w2;

    /// <summary>
    /// All 26 directions at once, for a surface not in use yet (a background rebuild: the map is swapped in with
    /// its surface, so the first frames after a rebuild need not recompute it, 2 directions a frame, ~15 ms each
    /// on the Jetliner).
    /// </summary>
    public void PrecomputeAll(IGridAccessor grid, ISurfaceProvider provider)
    {
        _surfaceVersion = provider.Version;
        _faceCount = provider.FaceCount;
        for (int d = 0; d < DirCount; d++)
        {
            if (_cache[d].Length < _faceCount) _cache[d] = new byte[_faceCount];   // (sized once: no growth garbage)
            ComputeDirection(grid, provider, d);
            _dirty[d] = false;
            _hasData[d] = true;
        }
        _rebuildCursor = 0;
        _lastDirIndex = -1; _b0 = _b1 = _b2 = -1;
        EnsureLists(_faceCount);
    }

    // ─── Compute one direction ───────────────────────────────────

    private void ComputeDirection(IGridAccessor grid, ISurfaceProvider provider, int dirIndex)
    {
        var faces = provider.Faces;
        int n = faces.Count;
        Vector3 flowDir = Directions[dirIndex];

        var result = _cache[dirIndex];
        if (result.Length < n) { result = new byte[n]; _cache[dirIndex] = result; }

        // Face positions are in metres and the grid's cells are 0.25 m (CubeGridCoords.CELL_SIZE). The march was
        // in BLOCK units (position / block size), so every ray probed cells ten times too near the grid's origin on
        // large grids: shadowing was noise. Now in cells, half a block a step (it cannot skip a whole block), as
        // far as MaxRayLength blocks.
        float CellSize = provider is SmoothSurfaceProvider sp ? sp.CellSize : 0.25f;
        float cellOff = provider is SmoothSurfaceProvider sp2 ? sp2.CellOffset : 0f;
        float blockSize = provider.BlockSize;
        float invCell = 1f / CellSize;
        int cellsPerBlock = Math.Max(1, (int)MathF.Round(blockSize / CellSize));
        int stride = Math.Max(1, cellsPerBlock / 2);
        int maxSteps = MaxRayLength * cellsPerBlock / stride;
        var wingCells = WingCells;
        float decayLen = WakeDecayLength;

        float dx = -flowDir.X * stride;
        float dy = -flowDir.Y * stride;
        float dz = -flowDir.Z * stride;

        bool hasManifold = Manifold != null && Manifold.IsClassified;

        for (int i = 0; i < n; i++)
        {
            // Skip cavity faces — they'll never be read by hull-only SoA
            if (hasManifold && !Manifold.IsHull(i))
            {
                result[i] = 0;
                continue;
            }

            var face = faces[i];
            float cosAlpha = -Vector3.Dot(flowDir, face.Normal);

            // A face turned away from this direction: nothing to say about what blocks it (the force model decides
            // by facing). It read 0 - "hidden" - and blended between directions that dragged down every face that
            // faces the air at the direction in between: ships lost most of their drag at many angles.
            if (cosAlpha <= 0)
            {
                result[i] = 255;
                continue;
            }

            float ox = face.Position.X * invCell - cellOff;
            float oy = face.Position.Y * invCell - cellOff;
            float oz = face.Position.Z * invCell - cellOff;

            // start just outside the face (0.6 cell upstream), into the empty cell it borders
            float cx = ox - flowDir.X * 0.6f;
            float cy = oy - flowDir.Y * 0.6f;
            float cz = oz - flowDir.Z * 0.6f;

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
                    hitDist = (step + 1) * stride / (float)cellsPerBlock;   // (in blocks, as the wake decay is)
                    if (wingCells != null && wingCells.Contains(cellPos))
                        hitWing = true;
                    break;
                }

                cx += dx; cy += dy; cz += dz;
            }

            if (hit)
            {
                if (hitWing && decayLen > 0f)
                    result[i] = Q(1f - MathF.Exp(-hitDist / decayLen));
                else
                    result[i] = 0;
            }
            else
            {
                result[i] = 255;
            }
        }
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
