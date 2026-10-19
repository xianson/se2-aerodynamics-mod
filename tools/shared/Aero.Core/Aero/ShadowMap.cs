using System.Numerics;
using Aero.Core.Grids;
using Aero.Core.Math;

namespace Aero.Core.Aero;

/// <summary>
/// Aerodynamic shadow map — determines which exposed faces are occluded
/// by upstream blocks along the flow direction.
///
/// Sits between the ISurfaceProvider (geometry) and the drag model (forces).
/// The surface provider says which faces are exposed to empty space;
/// the shadow map says which of those faces are also exposed to the airflow.
///
/// Uses DDA ray marching through the grid's cell map: for each front-facing
/// face, march upstream along -velocity. If the ray hits an occupied cell
/// before exiting the grid, the face is shadowed.
///
/// Caching:
///   - Shadow state is cached per flow direction
///   - Re-evaluated when direction changes by more than DirectionThreshold
///   - Incrementally updated on block add/remove (only affected rays re-marched)
///
/// Usage:
///   shadowMap.Update(grid, surfaceProvider, flowDirection);
///   // then use shadowMap.IsVisible(faceIndex) or shadowMap.VisibleFaces
/// </summary>
public class ShadowMap
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
    /// </summary>
    public float WakeDecayLength { get; set; } = 10f;

    /// <summary>
    /// Set of grid cells that belong to detected wings. When a face is shadowed
    /// by a wing cell, wake decay is applied instead of full occlusion.
    /// When null or empty, all shadow is binary.
    /// </summary>
    public HashSet<Vector3I>? WingCells { get; set; }

    // ─── Output ───────────────────────────────────────────────────

    /// <summary>Per-face visibility: true = exposed to flow, false = shadowed.</summary>
    public bool[] Visibility => _visibility;

    /// <summary>
    /// Per-face visibility factor [0,1]. 0 = fully shadowed, 1 = fully visible.
    /// Intermediate values when WakeDecayLength > 0.
    /// </summary>
    public float[] VisibilityFactor => _visibilityFactor;

    /// <summary>Number of visible (unshadowed) front-facing faces.</summary>
    public int VisibleCount { get; private set; }

    /// <summary>Number of shadowed front-facing faces.</summary>
    public int ShadowedCount { get; private set; }

    /// <summary>Monotonically increasing version counter. Incremented on each shadow re-evaluation.</summary>
    public int Version { get; private set; }

    /// <summary>Total front-facing faces (visible + shadowed).</summary>
    public int FrontFacingCount => VisibleCount + ShadowedCount;

    // ─── State ────────────────────────────────────────────────────

    private bool[] _visibility = Array.Empty<bool>();
    private float[] _visibilityFactor = Array.Empty<float>();
    private Vector3 _cachedDirection;
    private bool _hasCachedDirection;
    private Grid? _cachedGrid;
    private ISurfaceProvider? _cachedProvider;

    // Per-face: which grid cell the shadow ray hit (for incremental updates)
    private Vector3I[] _shadowBlocker = Array.Empty<Vector3I>();
    private static readonly Vector3I NoBlocker = new(int.MinValue, int.MinValue, int.MinValue);

    // ─── Full evaluation ──────────────────────────────────────────

    /// <summary>
    /// Evaluate shadow state for all faces against the given flow direction.
    /// </summary>
    public void Update(Grid grid, ISurfaceProvider provider, Vector3 flowDirection)
    {
        float speed = flowDirection.Length();
        if (speed < 0.001f)
        {
            // No flow — nothing is shadowed, nothing is front-facing
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

        FullEvaluate(grid, provider, flowDir);
        _cachedDirection = flowDir;
        _hasCachedDirection = true;
        _cachedGrid = grid;
        _cachedProvider = provider;
    }

    /// <summary>
    /// Incrementally update after blocks changed. Re-marches only the rays
    /// that could be affected by the changed blocks.
    /// </summary>
    public void OnBlocksChanged(Grid grid, ISurfaceProvider provider,
        Vector3 flowDirection, in BlocksChangedArgs args)
    {
        float speed = flowDirection.Length();
        if (speed < 0.001f || !_hasCachedDirection)
        {
            Update(grid, provider, flowDirection);
            return;
        }

        Vector3 flowDir = flowDirection / speed;

        // The surface provider has already been updated with the new faces.
        // We need to re-evaluate shadow for:
        // 1. Any NEW faces (from added blocks or newly exposed neighbors)
        // 2. Any face whose shadow blocker was one of the removed blocks
        // 3. Any face that might now be shadowed by an added block

        // Simple approach for correctness: if the change is small, re-evaluate
        // only faces near the changed blocks. If large, full re-evaluate.
        int changedCells = 0;
        foreach (var b in args.AddedBlocks)
            changedCells += b.Size.X * b.Size.Y * b.Size.Z;
        foreach (var b in args.RemovedBlocks)
            changedCells += b.Size.X * b.Size.Y * b.Size.Z;

        // If face count changed, arrays need resize → full rebuild
        if (provider.FaceCount != _visibility.Length || changedCells > 50)
        {
            FullEvaluate(grid, provider, flowDir);
            _cachedDirection = flowDir;
            return;
        }

        // Collect affected cell positions
        var changedPositions = new HashSet<Vector3I>();
        foreach (var b in args.AddedBlocks)
            foreach (var cell in b.EnumerateCells())
                changedPositions.Add(cell);
        foreach (var b in args.RemovedBlocks)
            foreach (var cell in b.EnumerateCells())
                changedPositions.Add(cell);

        // Re-evaluate faces whose blocker was in the changed set,
        // or faces near changed blocks
        var faces = provider.Faces;
        int visible = 0, shadowed = 0;

        for (int i = 0; i < faces.Count; i++)
        {
            bool needsReeval = false;

            // If this face's blocker was a changed cell, re-evaluate
            if (i < _shadowBlocker.Length && _shadowBlocker[i] != NoBlocker
                && changedPositions.Contains(_shadowBlocker[i]))
                needsReeval = true;

            // If this face is new (beyond old array), evaluate
            if (i >= _visibility.Length)
                needsReeval = true;

            // If not re-evaluating, check if an added block might now shadow it
            if (!needsReeval)
            {
                // Quick check: is any changed cell on the upstream ray path?
                var faceCell = WorldToCell(faces[i].Position, provider.BlockSize);
                foreach (var changed in changedPositions)
                {
                    if (IsBetweenAlongRay(faceCell, changed, flowDir))
                    {
                        needsReeval = true;
                        break;
                    }
                }
            }

            if (needsReeval)
            {
                EnsureArrays(faces.Count);
                EvaluateFace(grid, faces, i, flowDir, provider.BlockSize);
            }

            if (i < _visibility.Length && _visibility[i])
                visible++;
            else
                shadowed++;
        }

        VisibleCount = visible;
        ShadowedCount = shadowed;
        Version++;
        _cachedDirection = flowDir;
    }

    // ─── Core evaluation ──────────────────────────────────────────

    private void FullEvaluate(Grid grid, ISurfaceProvider provider, Vector3 flowDir)
    {
        var faces = provider.Faces;
        EnsureArrays(faces.Count);

        int visible = 0, shadowed = 0;

        for (int i = 0; i < faces.Count; i++)
        {
            EvaluateFace(grid, faces, i, flowDir, provider.BlockSize);

            if (_visibility[i])
                visible++;
            else
                shadowed++;
        }

        VisibleCount = visible;
        ShadowedCount = shadowed;
        Version++;
    }

    private void EvaluateFace(Grid grid, IReadOnlyList<SurfaceFace> faces,
        int faceIndex, Vector3 flowDir, float blockSize)
    {
        var face = faces[faceIndex];

        // Only front-facing faces can be visible
        // flowDir = velocity; dot(velocity, normal) > 0 means face points
        // in the direction the aircraft is moving = front-facing (into the wind)
        float cosAlpha = Vector3.Dot(flowDir, face.Normal);
        if (cosAlpha <= 0)
        {
            _visibility[faceIndex] = false;
            _visibilityFactor[faceIndex] = 0f;
            _shadowBlocker[faceIndex] = NoBlocker;
            return;
        }

        // March upstream from this face: step along -flowDir through the grid
        // If we hit an occupied cell, this face is shadowed
        var hitCell = MarchUpstream(grid, face.Position, flowDir, blockSize);

        if (hitCell != NoBlocker)
        {
            _shadowBlocker[faceIndex] = hitCell;

            float decayLen = WakeDecayLength;
            var wingCells = WingCells;
            if (decayLen > 0f && wingCells is { Count: > 0 } && wingCells.Contains(hitCell))
            {
                // Wing blocker — apply wake decay
                float dx = face.Position.X / blockSize - (hitCell.X + 0.5f);
                float dy = face.Position.Y / blockSize - (hitCell.Y + 0.5f);
                float dz = face.Position.Z / blockSize - (hitCell.Z + 0.5f);
                float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

                float factor = 1f - MathF.Exp(-dist / decayLen);
                _visibilityFactor[faceIndex] = factor;
                _visibility[faceIndex] = factor >= 0.5f;
            }
            else
            {
                // Non-wing blocker or decay disabled — full shadow
                _visibility[faceIndex] = false;
                _visibilityFactor[faceIndex] = 0f;
            }
        }
        else
        {
            _visibility[faceIndex] = true;
            _visibilityFactor[faceIndex] = 1f;
            _shadowBlocker[faceIndex] = NoBlocker;
        }
    }

    // ─── DDA ray march ────────────────────────────────────────────

    /// <summary>
    /// March upstream from a face position along -flowDir through the grid.
    /// Returns the first occupied cell hit, or NoBlocker if none.
    /// Skips the cell that the face itself belongs to.
    /// </summary>
    private static Vector3I MarchUpstream(Grid grid, Vector3 faceWorldPos,
        Vector3 flowDir, float blockSize)
    {
        // Ray direction: toward wind source = flowDir (velocity direction).
        // flowDir = velocity, which points toward the wind source because the
        // aircraft flies INTO the wind. -flowDir = freestream = downstream.
        Vector3 rayDir = flowDir;

        // Convert face position to grid coordinates (continuous)
        float gx = faceWorldPos.X / blockSize;
        float gy = faceWorldPos.Y / blockSize;
        float gz = faceWorldPos.Z / blockSize;

        // Current cell (the face's own cell — skip it)
        int cx = (int)MathF.Floor(gx);
        int cy = (int)MathF.Floor(gy);
        int cz = (int)MathF.Floor(gz);
        var startCell = new Vector3I(cx, cy, cz);

        // DDA setup
        int stepX = rayDir.X > 0 ? 1 : (rayDir.X < 0 ? -1 : 0);
        int stepY = rayDir.Y > 0 ? 1 : (rayDir.Y < 0 ? -1 : 0);
        int stepZ = rayDir.Z > 0 ? 1 : (rayDir.Z < 0 ? -1 : 0);

        float tDeltaX = stepX != 0 ? MathF.Abs(1.0f / rayDir.X) : float.MaxValue;
        float tDeltaY = stepY != 0 ? MathF.Abs(1.0f / rayDir.Y) : float.MaxValue;
        float tDeltaZ = stepZ != 0 ? MathF.Abs(1.0f / rayDir.Z) : float.MaxValue;

        float tMaxX = stepX > 0 ? (cx + 1 - gx) * tDeltaX : stepX < 0 ? (gx - cx) * tDeltaX : float.MaxValue;
        float tMaxY = stepY > 0 ? (cy + 1 - gy) * tDeltaY : stepY < 0 ? (gy - cy) * tDeltaY : float.MaxValue;
        float tMaxZ = stepZ > 0 ? (cz + 1 - gz) * tDeltaZ : stepZ < 0 ? (gz - cz) * tDeltaZ : float.MaxValue;

        // March up to 200 cells (safety limit)
        for (int i = 0; i < 200; i++)
        {
            // Advance to next cell
            if (tMaxX < tMaxY)
            {
                if (tMaxX < tMaxZ)
                { cx += stepX; tMaxX += tDeltaX; }
                else
                { cz += stepZ; tMaxZ += tDeltaZ; }
            }
            else
            {
                if (tMaxY < tMaxZ)
                { cy += stepY; tMaxY += tDeltaY; }
                else
                { cz += stepZ; tMaxZ += tDeltaZ; }
            }

            var cell = new Vector3I(cx, cy, cz);

            // Skip the starting cell (the face's own block)
            if (cell == startCell)
                continue;

            // Check if this cell is occupied
            if (grid.GetBlockAt(cell) != null)
                return cell; // shadowed by this block
        }

        return NoBlocker; // no obstruction found
    }

    // ─── Helpers ──────────────────────────────────────────────────

    private void EnsureArrays(int count)
    {
        if (_visibility.Length < count)
        {
            _visibility = new bool[count];
            _visibilityFactor = new float[count];
            _shadowBlocker = new Vector3I[count];
        }
        else if (_visibility.Length > count)
        {
            // Shrink not needed — just use first 'count' elements
        }
    }

    private static Vector3I WorldToCell(Vector3 worldPos, float blockSize)
    {
        return new Vector3I(
            (int)MathF.Floor(worldPos.X / blockSize),
            (int)MathF.Floor(worldPos.Y / blockSize),
            (int)MathF.Floor(worldPos.Z / blockSize));
    }

    /// <summary>
    /// Quick check: is 'candidate' roughly on the upstream ray path from 'faceCell'?
    /// Used for incremental updates to decide if a changed cell might affect a face's shadow.
    /// </summary>
    private static bool IsBetweenAlongRay(Vector3I faceCell, Vector3I candidate, Vector3 flowDir)
    {
        // Vector from face to candidate
        float dx = candidate.X - faceCell.X;
        float dy = candidate.Y - faceCell.Y;
        float dz = candidate.Z - faceCell.Z;

        // Dot with -flowDir (upstream direction) — if positive, candidate is upstream
        float dot = -(dx * flowDir.X + dy * flowDir.Y + dz * flowDir.Z);
        if (dot <= 0)
            return false; // candidate is downstream or at same position

        // Check lateral distance — candidate must be roughly on the ray
        // Cross product magnitude gives lateral distance
        float crossX = dy * (-flowDir.Z) - dz * (-flowDir.Y);
        float crossY = dz * (-flowDir.X) - dx * (-flowDir.Z);
        float crossZ = dx * (-flowDir.Y) - dy * (-flowDir.X);
        float lateralDist2 = crossX * crossX + crossY * crossY + crossZ * crossZ;

        // Within 1 cell lateral distance
        return lateralDist2 < 2.0f;
    }
}
