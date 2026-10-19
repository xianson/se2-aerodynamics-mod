using System.Numerics;
using System.Runtime.CompilerServices;
using Aero.Core.Atmosphere;
using Aero.Core.Grids;
using Aero.Core.Math;

namespace Aero.Core.Aero;

/// <summary>
/// Per-grid aerodynamic state: surface provider, shadow map, world transform.
/// Registered with <see cref="MultiGridAeroSystem"/> for multi-grid shadowing.
/// </summary>
public class GridAeroInstance
{
    /// <summary>The grid.</summary>
    public Grid Grid { get; }

    /// <summary>Surface provider for exposed face detection.</summary>
    public ISurfaceProvider Surface { get; }

    /// <summary>Self-shadow map (single-grid occlusion).</summary>
    public ShadowMap SelfShadow { get; } = new();

    /// <summary>Block size in meters.</summary>
    public float BlockSize { get; }

    /// <summary>World-space position of the grid origin.</summary>
    public Vector3 WorldPosition { get; set; }

    /// <summary>
    /// Per-face external shadow flag. True = face is additionally shadowed
    /// by another grid's wake. Indexed by face index in Surface.Faces.
    /// </summary>
    public bool[] ExternalShadow { get; private set; } = Array.Empty<bool>();

    /// <summary>Number of faces externally shadowed by other grids.</summary>
    public int ExternalShadowCount { get; internal set; }

    /// <summary>
    /// Group-level external shadow fraction [0,1]. Set by group-level inter-grid
    /// strategies that skip per-face iteration. 0 = fully exposed, 1 = fully shadowed.
    /// Used by <see cref="MultiGridAeroSystem.ComputeGroupLevel"/> when ExternalShadowCount is 0.
    /// </summary>
    public float ExternalShadowFraction { get; internal set; }

    // ─── Cached world bounds ──────────────────────────────────────

    private Vector3 _localBoundsMin, _localBoundsMax;
    private int _cachedBlockCount = -1;

    /// <summary>
    /// Cached world-space AABB. Call <see cref="RefreshWorldBounds"/> before first use.
    /// </summary>
    public (Vector3 min, Vector3 max) WorldBounds
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_localBoundsMin + WorldPosition, _localBoundsMax + WorldPosition);
    }

    /// <summary>
    /// Recompute local bounds from blocks. O(blocks), skips if block count unchanged.
    /// </summary>
    public void RefreshWorldBounds()
    {
        int count = Grid.Blocks.Count;
        if (count == _cachedBlockCount) return;
        _cachedBlockCount = count;

        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        foreach (var block in Grid.Blocks.Values)
        {
            foreach (var cell in block.EnumerateCells())
            {
                float x0 = cell.X * BlockSize;
                float y0 = cell.Y * BlockSize;
                float z0 = cell.Z * BlockSize;
                min = Vector3.Min(min, new Vector3(x0, y0, z0));
                max = Vector3.Max(max, new Vector3(x0 + BlockSize, y0 + BlockSize, z0 + BlockSize));
            }
        }
        _localBoundsMin = min;
        _localBoundsMax = max;
    }

    /// <summary>Force bounds recalculation on next <see cref="RefreshWorldBounds"/> call.</summary>
    public void InvalidateBounds() => _cachedBlockCount = -1;

    public GridAeroInstance(Grid grid, ISurfaceProvider surface, float blockSize, Vector3 worldPosition = default)
    {
        Grid = grid;
        Surface = surface;
        BlockSize = blockSize;
        WorldPosition = worldPosition;
    }

    /// <summary>
    /// Optional override for self-shadow visibility. When set (by MultiGridAeroSystem
    /// in column shadow mode), IsFaceVisible uses this instead of SelfShadow.Visibility.
    /// </summary>
    internal bool[]? ColumnShadowVisibility { get; set; }

    /// <summary>
    /// Optional float visibility factor from column shadow (with wake decay).
    /// </summary>
    internal float[]? ColumnShadowVisibilityFactor { get; set; }

    /// <summary>
    /// Is face at index visible (not self-shadowed AND not externally shadowed)?
    /// </summary>
    public bool IsFaceVisible(int faceIndex)
    {
        var selfVis = ColumnShadowVisibility ?? SelfShadow.Visibility;
        if (faceIndex < selfVis.Length && !selfVis[faceIndex])
            return false;
        if (faceIndex < ExternalShadow.Length && ExternalShadow[faceIndex])
            return false;
        return true;
    }

    /// <summary>
    /// Get the self-shadow visibility factor [0,1] for a face. Uses wake-decay
    /// attenuation when available, otherwise returns 0 or 1 from bool visibility.
    /// Does NOT include external shadow — caller should check ExternalShadow separately.
    /// </summary>
    public float GetSelfShadowFactor(int faceIndex)
    {
        var factor = ColumnShadowVisibilityFactor ?? SelfShadow.VisibilityFactor;
        if (faceIndex < factor.Length)
            return factor[faceIndex];
        return 1f;
    }

    internal void EnsureExternalShadowArray()
    {
        if (ExternalShadow.Length < Surface.FaceCount)
            ExternalShadow = new bool[Surface.FaceCount];
    }

    internal void ClearExternalShadow()
    {
        Array.Clear(ExternalShadow);
        ExternalShadowCount = 0;
        ExternalShadowFraction = 0;
    }

    /// <summary>Convert a face's grid-local position to world space.</summary>
    public Vector3 FaceToWorld(Vector3 localPos) => localPos + WorldPosition;

    /// <summary>Convert a world position to this grid's cell coordinates.</summary>
    public Vector3I WorldToCell(Vector3 worldPos)
    {
        var local = worldPos - WorldPosition;
        return new Vector3I(
            (int)MathF.Floor(local.X / BlockSize),
            (int)MathF.Floor(local.Y / BlockSize),
            (int)MathF.Floor(local.Z / BlockSize));
    }
}

/// <summary>
/// Multi-grid aerodynamic system. Manages self-shadow and inter-grid wake shadow.
///
/// Self-shadow: per-grid DDA ray march or column depth buffer (UseColumnShadow).
/// Inter-grid: swappable via <see cref="InterGridStrategy"/> property.
///   Default: <see cref="AabbSilhouetteStrategy"/> — O(faces) per pair.
///   Alternative: <see cref="ColumnDepthStrategy"/> — O(faces × (2r+1)²) per pair.
///
/// Pair iteration uses half-triangular loop (N*(N-1)/2) with upstream/downstream
/// determination per pair.
///
/// Usage:
///   var system = new MultiGridAeroSystem();
///   system.InterGridStrategy = new AabbSilhouetteStrategy(); // or ColumnDepthStrategy
///   system.Register(gridInstance1);
///   system.Register(gridInstance2);
///   system.Update(flowDirection);
/// </summary>
public class MultiGridAeroSystem
{
    private readonly List<GridAeroInstance> _instances = new();

    /// <summary>Wake cone half-angle in radians. Default ~10° (turbulent wake).</summary>
    public float WakeHalfAngle { get; set; } = 0.175f; // ~10°

    /// <summary>
    /// Wake decay distance in block sizes. Shadow strength drops to zero
    /// at this many block-widths downstream. Default 50.
    /// </summary>
    public float WakeDecayDistance { get; set; } = 50f;

    /// <summary>
    /// Minimum overlap fraction [0,1] between a face's projected position
    /// and the upstream grid's silhouette to count as shadowed. Default 0.3.
    /// </summary>
    public float ShadowThreshold { get; set; } = 0.3f;

    /// <summary>
    /// When true, uses column depth buffers instead of DDA ray march for self-shadow.
    /// Faster: O(blocks + faces) vs O(faces × ray_length). Default true.
    /// </summary>
    public bool UseColumnShadow { get; set; } = true;

    /// <summary>
    /// Inter-grid shadow strategy. Controls how wake shadow from upstream grids
    /// is projected onto downstream faces. Default: <see cref="AabbSilhouetteStrategy"/>.
    /// Swap to <see cref="ColumnDepthStrategy"/> for per-block accuracy, or
    /// <see cref="NoInterGridShadow"/> to disable inter-grid shadow entirely.
    /// </summary>
    public InterGridShadowStrategy InterGridStrategy { get; set; } = new AabbSilhouetteStrategy();

    /// <summary>
    /// Maximum grids to recompute self-shadow for per tick. 0 = unlimited.
    /// When limited, round-robins through grids so all are eventually updated.
    /// Stale grids keep their last-computed shadow until their turn.
    /// At 1000 grids with budget of ~600μs for self-shadow (~12μs/grid),
    /// set to 50 for ~50 grids/tick = full cycle in 20 ticks (0.33s at 60Hz).
    /// </summary>
    public int MaxSelfShadowUpdatesPerTick { get; set; }

    /// <summary>
    /// When true, per-grid self-shadow updates run in parallel via Parallel.For.
    /// Safe because each grid's shadow is independent. Inter-grid shadow remains
    /// sequential (shared writes to receiver shadow arrays).
    /// Default false — enable for multi-grid scenarios with many grids.
    /// </summary>
    public bool UseParallel { get; set; }

    /// <summary>Registered grid instances.</summary>
    public IReadOnlyList<GridAeroInstance> Instances => _instances;

    // Column shadow state for self-shadow (lazy-allocated)
    private readonly Dictionary<int, ColumnShadowMap> _columnSelfShadows = new();
    private int _selfShadowCursor;

    // ─── Registration ─────────────────────────────────────────────

    public void Register(GridAeroInstance instance)
    {
        if (!_instances.Contains(instance))
            _instances.Add(instance);
    }

    public void Unregister(GridAeroInstance instance)
    {
        _instances.Remove(instance);
    }

    public void Clear()
    {
        _instances.Clear();
    }

    /// <summary>
    /// Force column shadow maps to re-evaluate on next Update (for benchmarking).
    /// </summary>
    public void ResetColumnShadowCaches()
    {
        foreach (var s in _columnSelfShadows.Values)
            s.DirectionThreshold = 0;
    }

    // ─── Update ───────────────────────────────────────────────────

    /// <summary>
    /// Update all shadow maps (self + inter-grid).
    /// Call once per physics tick with the current flow conditions.
    /// </summary>
    public void Update(Vector3 flowDirection)
    {
        float speed = flowDirection.Length();
        if (speed < 0.001f || _instances.Count == 0)
            return;

        Vector3 flowDir = flowDirection / speed;

        if (UseColumnShadow)
            UpdateColumn(flowDir, flowDirection);
        else
            UpdateDDA(flowDir, flowDirection);
    }

    private void UpdateDDA(Vector3 flowDir, Vector3 flowDirection)
    {
        int count = _instances.Count;

        if (UseParallel && count > 1)
        {
            // Parallel self-shadow — each grid is independent
            Parallel.For(0, count, i =>
            {
                var inst = _instances[i];
                inst.ColumnShadowVisibility = null;
                inst.ColumnShadowVisibilityFactor = null;
                inst.SelfShadow.Update(inst.Grid, inst.Surface, flowDirection);
                inst.EnsureExternalShadowArray();
                inst.ClearExternalShadow();
            });
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                var inst = _instances[i];
                inst.ColumnShadowVisibility = null;
                inst.ColumnShadowVisibilityFactor = null;
                inst.SelfShadow.Update(inst.Grid, inst.Surface, flowDirection);
                inst.EnsureExternalShadowArray();
                inst.ClearExternalShadow();
            }
        }

        // 2. Inter-grid (sequential — shared writes to receiver shadow arrays)
        if (count >= 2)
            ApplyInterGrid(flowDir);
    }

    private void UpdateColumn(Vector3 flowDir, Vector3 flowDirection)
    {
        int count = _instances.Count;

        // 1. Prepare external shadow arrays for all grids
        if (UseParallel && count > 1)
        {
            Parallel.For(0, count, i =>
            {
                _instances[i].EnsureExternalShadowArray();
                _instances[i].ClearExternalShadow();
            });
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                _instances[i].EnsureExternalShadowArray();
                _instances[i].ClearExternalShadow();
            }
        }

        // 2. Self-shadow via column depth buffers (possibly amortized)
        bool amortized = MaxSelfShadowUpdatesPerTick > 0 && count > MaxSelfShadowUpdatesPerTick;
        int updateCount = amortized ? MaxSelfShadowUpdatesPerTick : count;

        // Pre-allocate column shadow maps (must be sequential — dict access)
        var updateList = new (GridAeroInstance inst, ColumnShadowMap shadow)[updateCount];
        for (int j = 0; j < updateCount; j++)
        {
            int idx = amortized ? (_selfShadowCursor + j) % count : j;
            var inst = _instances[idx];
            updateList[j] = (inst, GetOrCreateColumnShadow(_columnSelfShadows, inst.Grid.Id));
        }

        if (UseParallel && updateCount > 1)
        {
            // Parallel self-shadow update — each (grid, shadow) pair is independent
            Parallel.For(0, updateCount, j =>
            {
                var (inst, shadow) = updateList[j];
                shadow.Update(inst.Grid, inst.Surface, flowDirection);
                inst.ColumnShadowVisibility = shadow.Visibility;
                inst.ColumnShadowVisibilityFactor = shadow.VisibilityFactor;
            });
        }
        else
        {
            for (int j = 0; j < updateCount; j++)
            {
                var (inst, shadow) = updateList[j];
                shadow.Update(inst.Grid, inst.Surface, flowDirection);
                inst.ColumnShadowVisibility = shadow.Visibility;
                inst.ColumnShadowVisibilityFactor = shadow.VisibilityFactor;
            }
        }

        if (amortized)
            _selfShadowCursor = (_selfShadowCursor + updateCount) % count;

        // Grids not updated this tick keep their previous ColumnShadowVisibility.
        // Newly registered grids with null visibility get an initial update.
        for (int i = 0; i < count; i++)
        {
            var inst = _instances[i];
            if (inst.ColumnShadowVisibility == null)
            {
                var shadow = GetOrCreateColumnShadow(_columnSelfShadows, inst.Grid.Id);
                shadow.Update(inst.Grid, inst.Surface, flowDirection);
                inst.ColumnShadowVisibility = shadow.Visibility;
                inst.ColumnShadowVisibilityFactor = shadow.VisibilityFactor;
            }
        }

        // 3. Inter-grid (sequential — shared writes to receiver shadow arrays)
        if (count >= 2)
            ApplyInterGrid(flowDir);
    }

    private ColumnShadowMap GetOrCreateColumnShadow(Dictionary<int, ColumnShadowMap> dict, int gridId)
    {
        if (!dict.TryGetValue(gridId, out var shadow))
        {
            shadow = new ColumnShadowMap();
            dict[gridId] = shadow;
        }
        return shadow;
    }

    // ─── Inter-grid shadow dispatch ───────────────────────────────

    private void ApplyInterGrid(Vector3 flowDir)
    {
        ShadowMath.GetPerpendicularAxes(flowDir, out var axisU, out var axisV);
        InterGridStrategy.ApplyAll(_instances, flowDir, axisU, axisV, WakeHalfAngle, WakeDecayDistance);
    }

    // ─── Force computation helpers ────────────────────────────────

    /// <summary>
    /// O(K) group-level force computation with approximate shadow.
    /// Uses NormalGroups (typically 6) instead of per-face iteration.
    /// Shadow is applied as a uniform reduction based on ExternalShadowCount.
    /// ~100x faster than ComputeWithShadow for large grids.
    /// </summary>
    public static AeroResult ComputeGroupLevel(
        GridAeroInstance instance,
        Vector3 velocity,
        AtmosphereState atmo,
        float cpMax = 2.0f,
        float cdBluff = 1.1f,
        float cpBase = -0.15f,
        float cfSkin = 0.005f,
        float subsonicLimit = 0.6f,
        float supersonicLimit = 1.2f)
    {
        float speed = velocity.Length();
        if (speed < 0.01f || atmo.Density < 1e-8)
            return default;

        Vector3 vHat = velocity / speed;
        double q = atmo.GetDynamicPressure(speed);
        float qf = (float)q;
        double mach = atmo.SpeedOfSound > 0 ? speed / atmo.SpeedOfSound : 0;

        // Shadow fraction: from per-face count or group-level fraction
        int faceCount = instance.Surface.FaceCount;
        float extShadowFrac = instance.ExternalShadowFraction;
        if (extShadowFrac == 0 && instance.ExternalShadowCount > 0 && faceCount > 0)
            extShadowFrac = MathF.Min((float)instance.ExternalShadowCount / (faceCount * 0.5f), 1f);

        Vector3 totalForce = Vector3.Zero;
        Vector3 totalTorque = Vector3.Zero;
        float frontalArea = 0;

        var groups = instance.Surface.NormalGroups;
        for (int g = 0; g < groups.Count; g++)
        {
            var grp = groups[g];
            float cosAlpha = -Vector3.Dot(vHat, grp.Normal);

            if (cosAlpha > 0)
            {
                float visibleArea = grp.TotalArea * (1f - extShadowFrac);

                float cp;
                if (mach >= supersonicLimit)
                    cp = cpMax * cosAlpha * cosAlpha;
                else if (mach <= subsonicLimit)
                    cp = cdBluff * cosAlpha;
                else
                {
                    float t = ((float)mach - subsonicLimit) / (supersonicLimit - subsonicLimit);
                    t = t * t * (3f - 2f * t);
                    cp = cdBluff * cosAlpha + (cpMax * cosAlpha * cosAlpha - cdBluff * cosAlpha) * t;
                }

                frontalArea += visibleArea * cosAlpha;
                Vector3 F = grp.Normal * (-cp * qf * visibleArea);

                if (cfSkin > 0)
                {
                    Vector3 tangent = vHat - Vector3.Dot(vHat, grp.Normal) * grp.Normal;
                    float tLen = tangent.Length();
                    if (tLen > 1e-6f)
                        F += (tangent / tLen) * (cfSkin * qf * visibleArea);
                }

                totalForce += F;
                totalTorque += Vector3.Cross(grp.Centroid, F);
            }
            else
            {
                Vector3 F = grp.Normal * (-cpBase * qf * grp.TotalArea);
                totalForce += F;
                totalTorque += Vector3.Cross(grp.Centroid, F);
            }
        }

        float forceDotV = Vector3.Dot(totalForce, vHat);
        Vector3 liftVec = totalForce - forceDotV * vHat;

        return new AeroResult(
            totalForce, totalTorque,
            MathF.Abs(forceDotV),
            liftVec.Length(),
            frontalArea,
            mach, q);
    }

    /// <summary>
    /// Compute aero forces for a grid instance, respecting both self-shadow
    /// and inter-grid shadow. Uses per-face iteration with shadow filtering.
    /// </summary>
    public static AeroResult ComputeWithShadow(
        GridAeroInstance instance,
        Vector3 velocity,
        AtmosphereState atmo,
        float cpMax = 2.0f,
        float cdBluff = 1.1f,
        float cpBase = -0.15f,
        float cfSkin = 0.005f,
        float subsonicLimit = 0.6f,
        float supersonicLimit = 1.2f)
    {
        float speed = velocity.Length();
        if (speed < 0.01f || atmo.Density < 1e-8)
            return default;

        Vector3 vHat = velocity / speed;
        double q = atmo.GetDynamicPressure(speed);
        float qf = (float)q;
        double mach = atmo.SpeedOfSound > 0 ? speed / atmo.SpeedOfSound : 0;

        Vector3 totalForce = Vector3.Zero;
        Vector3 totalTorque = Vector3.Zero;
        float frontalArea = 0;

        var faces = instance.Surface.Faces;
        // Estimate CoM from face centroid (rough — caller should provide real CoM)
        Vector3 com = Vector3.Zero;
        if (instance.Surface.NormalGroups.Count > 0)
        {
            float totalA = 0;
            foreach (var g in instance.Surface.NormalGroups)
            {
                com += g.Centroid * g.TotalArea;
                totalA += g.TotalArea;
            }
            if (totalA > 0) com /= totalA;
        }

        for (int i = 0; i < faces.Count; i++)
        {
            var face = faces[i];
            float cosAlpha = -Vector3.Dot(vHat, face.Normal);

            if (cosAlpha > 0)
            {
                // Get self-shadow attenuation (continuous [0,1] with wake decay)
                float shadowFactor = instance.GetSelfShadowFactor(i);
                // External shadow is still binary
                if (i < instance.ExternalShadow.Length && instance.ExternalShadow[i])
                    shadowFactor = 0f;
                if (shadowFactor < 0.01f)
                    continue;

                float cp;
                if (mach >= supersonicLimit)
                    cp = cpMax * cosAlpha * cosAlpha;
                else if (mach <= subsonicLimit)
                    cp = cdBluff * cosAlpha;
                else
                {
                    float t = ((float)mach - subsonicLimit) / (supersonicLimit - subsonicLimit);
                    t = t * t * (3f - 2f * t);
                    cp = cdBluff * cosAlpha + (cpMax * cosAlpha * cosAlpha - cdBluff * cosAlpha) * t;
                }

                cp *= shadowFactor;
                frontalArea += face.Area * cosAlpha * shadowFactor;
                Vector3 F = face.Normal * (-cp * qf * face.Area);

                if (cfSkin > 0)
                {
                    Vector3 tangent = vHat - Vector3.Dot(vHat, face.Normal) * face.Normal;
                    float tLen = tangent.Length();
                    if (tLen > 1e-6f)
                        F += (tangent / tLen) * (cfSkin * qf * face.Area);
                }

                totalForce += F;
                totalTorque += Vector3.Cross(face.Position - com, F);
            }
            else
            {
                // Rear-facing: base pressure
                Vector3 F = face.Normal * (-cpBase * qf * face.Area);
                totalForce += F;
                totalTorque += Vector3.Cross(face.Position - com, F);
            }
        }

        float forceDotV = Vector3.Dot(totalForce, vHat);
        Vector3 liftVec = totalForce - forceDotV * vHat;

        return new AeroResult(
            totalForce, totalTorque,
            MathF.Abs(forceDotV),
            liftVec.Length(),
            frontalArea,
            mach, q);
    }
}
