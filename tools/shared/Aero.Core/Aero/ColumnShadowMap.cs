using System.Numerics;
using System.Runtime.CompilerServices;
using Aero.Core.Grids;
using Aero.Core.Math;

namespace Aero.Core.Aero;

/// <summary>
/// Shadow map using flow-aligned column depth buffers.
///
/// Instead of per-face DDA ray marching, projects all blocks onto a 2D plane
/// perpendicular to the flow direction and builds a depth buffer. Each "column"
/// (quantized at block resolution on the perpendicular plane) stores the minimum
/// depth (closest to the wind source) of any block in that column.
///
/// Self-shadow query: a front-facing face at depth d is shadowed if the column's
/// min depth is less than d - 0.4 (i.e., there's a block upstream of it, excluding
/// the face's own block which is at depth d + 0.5).
///
/// Build:  O(blocks)  — one pass through all occupied cells
/// Query:  O(1)/face  — project, lookup column, compare depth
/// Total:  O(blocks + faces) vs DDA's O(faces × ray_length)
///
/// Also supports inter-grid shadow via world-space depth buffers.
/// </summary>
public class ColumnShadowMap
{
    // ─── Configuration ────────────────────────────────────────────

    /// <summary>
    /// Angular threshold (radians) below which a direction change
    /// does not trigger re-evaluation. Default ~5 degrees.
    /// </summary>
    public float DirectionThreshold { get; set; } = 0.087f; // ~5°

    /// <summary>
    /// Wake decay length in block units. Shadowed faces behind wing blocks
    /// recover visibility exponentially: factor = 1 - exp(-dist / decayLength).
    /// Set to 0 for binary (legacy) shadow. Default 10.
    /// Only applies when the blocker cell is in <see cref="WingCells"/>.
    /// Non-wing blocks (fuselage, hull) always produce full shadow.
    /// </summary>
    public float WakeDecayLength { get; set; } = 10f;

    /// <summary>
    /// Set of grid cells that belong to detected wings. When a face is shadowed
    /// by a column whose upstream-most block is a wing cell, wake decay is applied.
    /// When null or empty, all shadow is binary (no decay).
    /// Set this from the wing detector results before calling Update().
    /// </summary>
    public HashSet<Vector3I>? WingCells { get; set; }

    // ─── Output ───────────────────────────────────────────────────

    /// <summary>Per-face visibility: true = exposed to flow, false = shadowed.</summary>
    public bool[] Visibility => _visibility;

    /// <summary>
    /// Per-face visibility factor [0,1]. 0 = fully shadowed (blocker immediately
    /// upstream), 1 = fully visible. Intermediate values when WakeDecayLength > 0:
    /// faces far downstream of the blocker recover toward freestream.
    /// When WakeDecayLength == 0, matches Visibility (0 or 1).
    /// </summary>
    public float[] VisibilityFactor => _visibilityFactor;

    /// <summary>Number of visible (unshadowed) front-facing faces.</summary>
    public int VisibleCount { get; private set; }

    /// <summary>Number of shadowed front-facing faces.</summary>
    public int ShadowedCount { get; private set; }

    /// <summary>Number of columns in the depth buffer (for diagnostics).</summary>
    public int ColumnCount => _depthBuffer.Count;

    // ─── State ────────────────────────────────────────────────────

    private readonly Dictionary<long, float> _depthBuffer = new();
    private readonly HashSet<long> _wingBlockerColumns = new();
    private bool[] _visibility = Array.Empty<bool>();
    private float[] _visibilityFactor = Array.Empty<float>();
    private Vector3 _cachedDirection;
    private bool _hasCachedDirection;
    private Grid? _cachedGrid;
    private ISurfaceProvider? _cachedProvider;

    private Vector3 _flowDir, _axisU, _axisV;

    /// <summary>
    /// Self-shadow depth threshold in block units. A face at depth d is
    /// shadowed if min_depth &lt; d - threshold. Must be in (0, 0.5) to
    /// correctly exclude the face's own block (at depth d + 0.5 for
    /// axis-aligned flow) while catching blocks one cell upstream (at d - 0.5).
    /// </summary>
    private const float SelfShadowThreshold = 0.4f;

    // ─── Self-shadow (same interface as ShadowMap) ──────────────

    /// <summary>
    /// Evaluate shadow state for all faces against the given flow direction.
    /// </summary>
    public void Update(Grid grid, ISurfaceProvider provider, Vector3 flowDirection)
    {
        float speed = flowDirection.Length();
        if (speed < 0.001f)
        {
            EnsureArrays(provider.FaceCount);
            Array.Fill(_visibility, false);
            VisibleCount = 0;
            ShadowedCount = 0;
            _hasCachedDirection = false;
            return;
        }

        Vector3 flowDir = flowDirection / speed;

        // Check if we can skip re-evaluation
        if (_hasCachedDirection && _cachedGrid == grid && _cachedProvider == provider)
        {
            float dot = Vector3.Dot(flowDir, _cachedDirection);
            if (dot > MathF.Cos(DirectionThreshold))
                return; // direction hasn't changed enough
        }

        _flowDir = flowDir;
        GetPerpendicularAxes(flowDir, out _axisU, out _axisV);

        BuildDepthBuffer(grid, provider.BlockSize, Vector3.Zero);
        EvaluateFaces(provider);

        _cachedDirection = flowDir;
        _hasCachedDirection = true;
        _cachedGrid = grid;
        _cachedProvider = provider;
    }

    // ─── Inter-grid depth buffer ────────────────────────────────

    /// <summary>
    /// Build a world-space depth buffer from a grid instance.
    /// Call once per caster grid per frame, then query receiver faces.
    /// Axes must match across all grids in the same frame.
    /// </summary>
    public void BuildWorldBuffer(Grid grid, float blockSize, Vector3 worldPosition,
        Vector3 flowDir, Vector3 axisU, Vector3 axisV)
    {
        _flowDir = flowDir;
        _axisU = axisU;
        _axisV = axisV;
        BuildDepthBuffer(grid, blockSize, worldPosition);
    }

    /// <summary>
    /// Check if a world-space face position is shadowed by this depth buffer.
    /// Returns true if there's a caster block upstream in the same column.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool QueryShadow(Vector3 faceWorldPos, float blockSize)
    {
        float invBlock = 1f / blockSize;
        float bx = faceWorldPos.X * invBlock;
        float by = faceWorldPos.Y * invBlock;
        float bz = faceWorldPos.Z * invBlock;

        int u = (int)MathF.Floor(bx * _axisU.X + by * _axisU.Y + bz * _axisU.Z);
        int v = (int)MathF.Floor(bx * _axisV.X + by * _axisV.Y + bz * _axisV.Z);
        float depth = bx * _flowDir.X + by * _flowDir.Y + bz * _flowDir.Z;

        return _depthBuffer.TryGetValue(Pack(u, v), out float minDepth) && minDepth < depth;
    }

    /// <summary>
    /// Check if a caster block exists in the same column as this face (inter-grid shadow).
    /// No depth comparison — the wake distance check handles upstream/downstream.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool QueryColumnExists(Vector3 faceWorldPos, float blockSize)
    {
        float invBlock = 1f / blockSize;
        float bx = faceWorldPos.X * invBlock;
        float by = faceWorldPos.Y * invBlock;
        float bz = faceWorldPos.Z * invBlock;

        int u = (int)MathF.Floor(bx * _axisU.X + by * _axisU.Y + bz * _axisU.Z);
        int v = (int)MathF.Floor(bx * _axisV.X + by * _axisV.Y + bz * _axisV.Z);

        return _depthBuffer.ContainsKey(Pack(u, v));
    }

    /// <summary>
    /// Check if a caster block exists in neighboring columns (inter-grid with wake expansion).
    /// </summary>
    public bool QueryColumnExistsExpanded(Vector3 faceWorldPos, float blockSize, int expansionRadius)
    {
        float invBlock = 1f / blockSize;
        float bx = faceWorldPos.X * invBlock;
        float by = faceWorldPos.Y * invBlock;
        float bz = faceWorldPos.Z * invBlock;

        int cu = (int)MathF.Floor(bx * _axisU.X + by * _axisU.Y + bz * _axisU.Z);
        int cv = (int)MathF.Floor(bx * _axisV.X + by * _axisV.Y + bz * _axisV.Z);

        for (int du = -expansionRadius; du <= expansionRadius; du++)
            for (int dv = -expansionRadius; dv <= expansionRadius; dv++)
                if (_depthBuffer.ContainsKey(Pack(cu + du, cv + dv)))
                    return true;
        return false;
    }

    /// <summary>
    /// Check with wake expansion: look in a neighborhood of columns.
    /// expansionRadius is in column units (≈ block units).
    /// </summary>
    public bool QueryShadowExpanded(Vector3 faceWorldPos, float blockSize, int expansionRadius)
    {
        float invBlock = 1f / blockSize;
        float bx = faceWorldPos.X * invBlock;
        float by = faceWorldPos.Y * invBlock;
        float bz = faceWorldPos.Z * invBlock;

        int cu = (int)MathF.Floor(bx * _axisU.X + by * _axisU.Y + bz * _axisU.Z);
        int cv = (int)MathF.Floor(bx * _axisV.X + by * _axisV.Y + bz * _axisV.Z);
        float depth = bx * _flowDir.X + by * _flowDir.Y + bz * _flowDir.Z;

        for (int du = -expansionRadius; du <= expansionRadius; du++)
        {
            for (int dv = -expansionRadius; dv <= expansionRadius; dv++)
            {
                if (_depthBuffer.TryGetValue(Pack(cu + du, cv + dv), out float minDepth)
                    && minDepth < depth)
                    return true;
            }
        }
        return false;
    }

    // ─── Core ───────────────────────────────────────────────────

    private void BuildDepthBuffer(Grid grid, float blockSize, Vector3 worldOffset)
    {
        _depthBuffer.Clear();
        _wingBlockerColumns.Clear();
        float invBlock = 1f / blockSize;
        var wingCells = WingCells;

        // Track which cell is the upstream-most (min depth) per column
        // so we can check if it's a wing cell.
        Dictionary<long, Vector3I>? blockerCells = wingCells is { Count: > 0 }
            ? new Dictionary<long, Vector3I>() : null;

        foreach (var block in grid.Blocks.Values)
        {
            foreach (var cell in block.EnumerateCells())
            {
                // Cell center in block units, translated by world offset
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
        }

        // Mark columns whose upstream-most block is a wing cell
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
        EnsureArrays(faces.Count);

        int visible = 0, shadowed = 0;
        float invBlock = 1f / provider.BlockSize;
        float decayLen = WakeDecayLength;

        for (int i = 0; i < faces.Count; i++)
        {
            var face = faces[i];
            float cosAlpha = -Vector3.Dot(_flowDir, face.Normal);

            if (cosAlpha <= 0)
            {
                // Rear-facing — not visible to flow
                _visibility[i] = false;
                _visibilityFactor[i] = 0f;
                shadowed++;
                continue;
            }

            // Project face position to (u, v, depth) in block units
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
                // Block upstream in this column — face is shadowed.
                // Wake decay only when the blocker is a wing cell.
                // Non-wing blockers (fuselage, hull) stay fully occluding.
                if (decayLen > 0f && _wingBlockerColumns.Contains(key))
                {
                    float dist = depth - minDepth;
                    float factor = 1f - MathF.Exp(-dist / decayLen);
                    _visibilityFactor[i] = factor;
                    _visibility[i] = factor >= 0.5f;
                    if (factor < 0.5f)
                        shadowed++;
                    else
                        visible++;
                }
                else
                {
                    // Non-wing blocker or decay disabled — full shadow
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

    private void EnsureArrays(int count)
    {
        if (_visibility.Length < count)
        {
            _visibility = new bool[count];
            _visibilityFactor = new float[count];
        }
    }

    /// <summary>Pack two ints into a single long for dictionary key.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Pack(int u, int v) => ((long)u << 32) | (uint)v;

    /// <summary>Get two orthonormal axes perpendicular to a direction vector.</summary>
    private static void GetPerpendicularAxes(Vector3 dir, out Vector3 axisU, out Vector3 axisV)
    {
        Vector3 up = MathF.Abs(dir.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        axisU = Vector3.Normalize(Vector3.Cross(dir, up));
        axisV = Vector3.Cross(dir, axisU); // already normalized
    }
}
