#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// Shadow map using flow-aligned column depth buffers.
///
/// Projects all blocks onto a 2D plane perpendicular to the flow direction
/// and builds a depth buffer. Each "column" stores the minimum depth of any
/// block in that column.
///
/// Build:  O(blocks)  — one pass through all occupied cells
/// Query:  O(1)/face  — project, lookup column, compare depth
/// Total:  O(blocks + faces) vs DDA's O(faces × ray_length)
/// </summary>
public class ColumnShadowMap : IShadowMap
{
    // ─── Configuration ────────────────────────────────────────────

    /// <summary>
    /// Angular threshold (radians) below which a direction change
    /// does not trigger re-evaluation. Default ~5 degrees.
    /// </summary>
    public float DirectionThreshold { get; set; } = 0.087f; // ~5°

    /// <summary>
    /// Wake decay length in block units. Shadowed faces behind wing blocks
    /// recover visibility exponentially. Default 10.
    /// Only applies when the blocker cell is in WingCells.
    /// </summary>
    public float WakeDecayLength { get; set; } = 10f;

    /// <summary>
    /// Set of grid cells that belong to detected wings.
    /// When null or empty, all shadow is binary (no decay).
    /// </summary>
    public HashSet<Vector3I>? WingCells { get; set; }

    // ─── Output ───────────────────────────────────────────────────

    public List<bool> Visibility => _visibility;
    public List<float> VisibilityFactor => _visibilityFactor;
    public int VisibleCount { get; private set; }
    public int ShadowedCount { get; private set; }

    /// <summary>Incremented each time visibility is recomputed. Used by drag model to avoid rebaking visArea.</summary>
    public int Version { get; private set; }

    /// <summary>Number of columns in the depth buffer (for diagnostics).</summary>
    public int ColumnCount => _depthBuffer.Count;

    // ─── State ────────────────────────────────────────────────────

    private readonly Dictionary<long, float> _depthBuffer = new(LongKey.Comparer);
    private readonly HashSet<long> _wingBlockerColumns = new(LongKey.Comparer);
    private Dictionary<long, Vector3I>? _blockerCells;
    private List<bool> _visibility = new();
    private List<float> _visibilityFactor = new();
    private Vector3 _cachedDirection;
    private bool _hasCachedDirection;
    private IGridAccessor? _cachedGrid;
    private ISurfaceProvider? _cachedProvider;
    private int _cachedSurfaceVersion = -1;
    private int _cachedGridVersion = -1;

    private Vector3 _flowDir, _axisU, _axisV;

    private const float SelfShadowThreshold = 0.4f;

    // ─── Self-shadow ──────────────────────────────────────────────

    public void Update(IGridAccessor grid, ISurfaceProvider provider, Vector3 flowDirection)
    {
        float speed = flowDirection.Length();
        if (speed < 0.001f)
        {
            EnsureLists(provider.FaceCount);
            for (int ci = 0; ci < _visibility.Count; ci++) _visibility[ci] = false;
            VisibleCount = 0;
            ShadowedCount = 0;
            Version++;
            _hasCachedDirection = false;
            return;
        }

        Vector3 flowDir = flowDirection / speed;

        bool surfaceChanged = _cachedSurfaceVersion != provider.Version;
        if (_hasCachedDirection && _cachedGrid == grid && _cachedProvider == provider
            && !surfaceChanged)
        {
            float dot = Vector3.Dot(flowDir, _cachedDirection);
            if (dot > MathF.Cos(DirectionThreshold))
                return;
        }

        _flowDir = flowDir;
        GetPerpendicularAxes(flowDir, out _axisU, out _axisV);

        BuildDepthBuffer(grid, provider.BlockSize, Vector3.Zero);
        EvaluateFaces(provider);
        Version++;

        _cachedDirection = flowDir;
        _hasCachedDirection = true;
        _cachedGrid = grid;
        _cachedProvider = provider;
        _cachedSurfaceVersion = provider.Version;
    }

    // ─── Core ───────────────────────────────────────────────────

    private void BuildDepthBuffer(IGridAccessor grid, float blockSize, Vector3 worldOffset)
    {
        _depthBuffer.Clear();
        _wingBlockerColumns.Clear();
        float invBlock = 1f / blockSize;
        var wingCells = WingCells;

        if (wingCells is { Count: > 0 })
        {
            _blockerCells ??= new Dictionary<long, Vector3I>(LongKey.Comparer);
            _blockerCells.Clear();
        }
        Dictionary<long, Vector3I>? blockerCells = wingCells is { Count: > 0 }
            ? _blockerCells : null;

        foreach (var cell in grid.EnumerateOccupiedCells())
        {
            float bx = (cell.X + 0.5f) + worldOffset.X * invBlock;
            float by = (cell.Y + 0.5f) + worldOffset.Y * invBlock;
            float bz = (cell.Z + 0.5f) + worldOffset.Z * invBlock;

            int u = (int)MathF.Floor(bx * _axisU.X + by * _axisU.Y + bz * _axisU.Z);
            int v = (int)MathF.Floor(bx * _axisV.X + by * _axisV.Y + bz * _axisV.Z);
            float depth = bx * _flowDir.X + by * _flowDir.Y + bz * _flowDir.Z;

            long key = Pack(u, v);
            if (!_depthBuffer.TryGetValue(key, out float existing) || depth < existing)
            {
                _depthBuffer[key] = depth;
                if (blockerCells != null)
                    blockerCells[key] = cell;
            }
        }

        if (blockerCells != null)
        {
            foreach (var (key, cell) in blockerCells)
            {
                if (wingCells!.Contains(cell))
                    _wingBlockerColumns.Add(key);
            }
        }
    }

    private void EvaluateFaces(ISurfaceProvider provider)
    {
        var faces = provider.Faces;
        EnsureLists(faces.Count);

        int visible = 0, shadowed = 0;
        float invBlock = 1f / provider.BlockSize;
        float decayLen = WakeDecayLength;

        for (int i = 0; i < faces.Count; i++)
        {
            var face = faces[i];
            float cosAlpha = -Vector3.Dot(_flowDir, face.Normal);

            if (cosAlpha <= 0)
            {
                _visibility[i] = false;
                _visibilityFactor[i] = 0f;
                shadowed++;
                continue;
            }

            float bx = face.Position.X * invBlock;
            float by = face.Position.Y * invBlock;
            float bz = face.Position.Z * invBlock;

            int u = (int)MathF.Floor(bx * _axisU.X + by * _axisU.Y + bz * _axisU.Z);
            int v = (int)MathF.Floor(bx * _axisV.X + by * _axisV.Y + bz * _axisV.Z);
            float depth = bx * _flowDir.X + by * _flowDir.Y + bz * _flowDir.Z;

            long key = Pack(u, v);
            if (_depthBuffer.TryGetValue(key, out float minDepth)
                && minDepth < depth - SelfShadowThreshold)
            {
                if (decayLen > 0f && _wingBlockerColumns.Contains(key))
                {
                    float dist = depth - minDepth;
                    float factor = 1f - MathF.Exp(-dist / decayLen);
                    _visibilityFactor[i] = factor;
                    _visibility[i] = factor >= 0.5f;
                    if (factor < 0.5f) shadowed++; else visible++;
                }
                else
                {
                    _visibility[i] = false;
                    _visibilityFactor[i] = 0f;
                    shadowed++;
                }
            }
            else
            {
                _visibility[i] = true;
                _visibilityFactor[i] = 1f;
                visible++;
            }
        }

        VisibleCount = visible;
        ShadowedCount = shadowed;
    }

    // ─── Helpers ────────────────────────────────────────────────

    private void EnsureLists(int count)
    {
        if (_visibility.Count >= count) return;
        _visibility.Clear();
        _visibilityFactor.Clear();
        for (int i = 0; i < count; i++)
        {
            _visibility.Add(false);
            _visibilityFactor.Add(0f);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Pack(int u, int v) => ((long)u << 32) | (uint)v;

    private static void GetPerpendicularAxes(Vector3 dir, out Vector3 axisU, out Vector3 axisV)
    {
        Vector3 up = MathF.Abs(dir.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        axisU = Vector3.Normalize(Vector3.Cross(dir, up));
        axisV = Vector3.Cross(dir, axisU);
    }
}
