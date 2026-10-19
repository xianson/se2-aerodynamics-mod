using System.Numerics;
using System.Runtime.CompilerServices;

namespace Aero.Core.Aero;

/// <summary>
/// Abstract base for inter-grid wake shadow strategies.
///
/// Strategies control both per-pair logic (Apply) and pair iteration (ApplyAll).
/// The default ApplyAll uses N*(N-1)/2 half-triangular iteration.
/// Override ApplyAll for spatial acceleration (e.g. spatial hash).
/// </summary>
public abstract class InterGridShadowStrategy
{
    /// <summary>Human-readable name (for benchmarks/reports).</summary>
    public abstract string Name { get; }

    /// <summary>Short description of the approach.</summary>
    public abstract string Description { get; }

    /// <summary>
    /// Called once per frame before pair iteration starts.
    /// Override to pre-compute per-grid data (depth buffers, bounds, etc.).
    /// </summary>
    public virtual void Prepare(IReadOnlyList<GridAeroInstance> instances,
        Vector3 flowDir, Vector3 axisU, Vector3 axisV) { }

    /// <summary>
    /// Apply wake shadow from a single upstream caster onto a downstream receiver.
    /// </summary>
    public abstract void Apply(GridAeroInstance caster, GridAeroInstance receiver,
        Vector3 flowDir, Vector3 axisU, Vector3 axisV,
        float wakeHalfAngle, float wakeDecayDistance);

    /// <summary>
    /// Apply inter-grid shadow across all grid pairs.
    /// Default: N*(N-1)/2 half-triangular iteration.
    /// Override for spatial acceleration structures.
    /// </summary>
    public virtual void ApplyAll(IReadOnlyList<GridAeroInstance> instances,
        Vector3 flowDir, Vector3 axisU, Vector3 axisV,
        float wakeHalfAngle, float wakeDecayDistance)
    {
        Prepare(instances, flowDir, axisU, axisV);

        for (int a = 0; a < instances.Count; a++)
        {
            for (int b = a + 1; b < instances.Count; b++)
            {
                Vector3 delta = instances[b].WorldPosition - instances[a].WorldPosition;
                float dot = -Vector3.Dot(delta, flowDir);

                if (dot > 0)
                    Apply(instances[a], instances[b], flowDir, axisU, axisV, wakeHalfAngle, wakeDecayDistance);
                else if (dot < 0)
                    Apply(instances[b], instances[a], flowDir, axisU, axisV, wakeHalfAngle, wakeDecayDistance);
            }
        }
    }
}

/// <summary>
/// No inter-grid shadow. Only self-shadow is applied.
/// </summary>
public sealed class NoInterGridShadow : InterGridShadowStrategy
{
    public override string Name => "None";
    public override string Description => "No inter-grid shadow (self-shadow only)";

    public override void Apply(GridAeroInstance caster, GridAeroInstance receiver,
        Vector3 flowDir, Vector3 axisU, Vector3 axisV,
        float wakeHalfAngle, float wakeDecayDistance) { }

    public override void ApplyAll(IReadOnlyList<GridAeroInstance> instances,
        Vector3 flowDir, Vector3 axisU, Vector3 axisV,
        float wakeHalfAngle, float wakeDecayDistance) { }
}

/// <summary>
/// AABB silhouette projection — O(faces) per pair, N²/2 pair iteration.
/// </summary>
public class AabbSilhouetteStrategy : InterGridShadowStrategy
{
    public override string Name => "AABB Silhouette";
    public override string Description => "AABB projection + wake cone, O(faces) per pair";

    public override void Prepare(IReadOnlyList<GridAeroInstance> instances,
        Vector3 flowDir, Vector3 axisU, Vector3 axisV)
    {
        foreach (var inst in instances)
            inst.RefreshWorldBounds();
    }

    public override void Apply(GridAeroInstance caster, GridAeroInstance receiver,
        Vector3 flowDir, Vector3 axisU, Vector3 axisV,
        float wakeHalfAngle, float wakeDecayDistance)
    {
        Vector3 delta = receiver.WorldPosition - caster.WorldPosition;
        float wakeDist = -Vector3.Dot(delta, flowDir);
        if (wakeDist <= 0) return;

        float distInBlocks = wakeDist / caster.BlockSize;
        if (distInBlocks > wakeDecayDistance) return;

        // Project caster AABB silhouette
        var (cMin, cMax) = caster.WorldBounds;
        float minU = float.MaxValue, maxU = float.MinValue;
        float minV = float.MaxValue, maxV = float.MinValue;
        ShadowMath.ProjectAABBToPlane(cMin, cMax, axisU, axisV,
            ref minU, ref maxU, ref minV, ref maxV);

        // Expand by wake cone
        float wakeExpansion = wakeDist * MathF.Tan(wakeHalfAngle);
        minU -= wakeExpansion; maxU += wakeExpansion;
        minV -= wakeExpansion; maxV += wakeExpansion;

        // Quick reject: receiver AABB doesn't overlap caster wake silhouette
        var (rMin, rMax) = receiver.WorldBounds;
        float rMinU = float.MaxValue, rMaxU = float.MinValue;
        float rMinV = float.MaxValue, rMaxV = float.MinValue;
        ShadowMath.ProjectAABBToPlane(rMin, rMax, axisU, axisV,
            ref rMinU, ref rMaxU, ref rMinV, ref rMaxV);

        if (rMaxU < minU || rMinU > maxU || rMaxV < minV || rMinV > maxV)
            return;

        // Per-face shadow test
        var faces = receiver.Surface.Faces;
        var selfVis = receiver.ColumnShadowVisibility ?? receiver.SelfShadow.Visibility;
        float wpX = receiver.WorldPosition.X;
        float wpY = receiver.WorldPosition.Y;
        float wpZ = receiver.WorldPosition.Z;

        for (int i = 0; i < faces.Count; i++)
        {
            var face = faces[i];

            float cosAlpha = -(flowDir.X * face.Normal.X + flowDir.Y * face.Normal.Y + flowDir.Z * face.Normal.Z);
            if (cosAlpha <= 0) continue;

            if (i < selfVis.Length && !selfVis[i]) continue;

            float fwX = face.Position.X + wpX;
            float fwY = face.Position.Y + wpY;
            float fwZ = face.Position.Z + wpZ;

            float projU = fwX * axisU.X + fwY * axisU.Y + fwZ * axisU.Z;
            if (projU < minU || projU > maxU) continue;

            float projV = fwX * axisV.X + fwY * axisV.Y + fwZ * axisV.Z;
            if (projV < minV || projV > maxV) continue;

            receiver.ExternalShadow[i] = true;
            receiver.ExternalShadowCount++;
        }
    }
}

/// <summary>
/// Spatial hash + AABB silhouette. Replaces N²/2 pair iteration with
/// a 2D spatial hash on the flow-perpendicular plane. Only checks pairs
/// in the same or adjacent buckets, with flow-depth filtering.
///
/// Pair iteration: O(N × K) where K = average grids per bucket neighborhood.
/// Per-pair: O(faces) via AABB silhouette (same as <see cref="AabbSilhouetteStrategy"/>).
///
/// For 1000 grids spread across a battlefield, K ≈ 1-5.
/// N²/2 = 500K pairs → spatial hash ≈ 5K pairs. ~100x reduction.
/// </summary>
public sealed class SpatialHashAabbStrategy : InterGridShadowStrategy
{
    public override string Name => "Spatial Hash";
    public override string Description => "Spatial hash + AABB silhouette, O(N × K) pairs";

    private readonly AabbSilhouetteStrategy _aabb = new();
    private readonly Dictionary<long, List<int>> _hash = new();
    private readonly List<List<int>> _pool = new();
    private int _poolCursor;

    public override void Apply(GridAeroInstance caster, GridAeroInstance receiver,
        Vector3 flowDir, Vector3 axisU, Vector3 axisV,
        float wakeHalfAngle, float wakeDecayDistance)
    {
        _aabb.Apply(caster, receiver, flowDir, axisU, axisV, wakeHalfAngle, wakeDecayDistance);
    }

    public override void ApplyAll(IReadOnlyList<GridAeroInstance> instances,
        Vector3 flowDir, Vector3 axisU, Vector3 axisV,
        float wakeHalfAngle, float wakeDecayDistance)
    {
        int count = instances.Count;
        if (count < 2) return;

        float blockSize = instances[0].BlockSize;
        float maxWakeDist = wakeDecayDistance * blockSize;
        float maxWakeWidth = maxWakeDist * MathF.Tan(wakeHalfAngle);
        float bucketSize = MathF.Max(maxWakeWidth * 2, blockSize * 10);
        float invBucket = 1f / bucketSize;

        // Refresh bounds for AABB silhouette
        for (int i = 0; i < count; i++)
            instances[i].RefreshWorldBounds();

        // Build spatial hash on flow-perpendicular plane
        _hash.Clear();
        _poolCursor = 0;

        for (int i = 0; i < count; i++)
        {
            var pos = instances[i].WorldPosition;
            int bu = (int)MathF.Floor(Vector3.Dot(pos, axisU) * invBucket);
            int bv = (int)MathF.Floor(Vector3.Dot(pos, axisV) * invBucket);
            long key = Pack(bu, bv);

            if (!_hash.TryGetValue(key, out var bucket))
            {
                bucket = RentList();
                _hash[key] = bucket;
            }
            bucket.Add(i);
        }

        // For each grid, check grids in same + adjacent buckets
        for (int i = 0; i < count; i++)
        {
            var pos = instances[i].WorldPosition;
            float iDepth = Vector3.Dot(pos, flowDir);
            int bu = (int)MathF.Floor(Vector3.Dot(pos, axisU) * invBucket);
            int bv = (int)MathF.Floor(Vector3.Dot(pos, axisV) * invBucket);

            for (int du = -1; du <= 1; du++)
            {
                for (int dv = -1; dv <= 1; dv++)
                {
                    long key = Pack(bu + du, bv + dv);
                    if (!_hash.TryGetValue(key, out var bucket)) continue;

                    for (int bi = 0; bi < bucket.Count; bi++)
                    {
                        int j = bucket[bi];
                        if (j <= i) continue; // half-triangular: avoid duplicate pairs

                        float jDepth = Vector3.Dot(instances[j].WorldPosition, flowDir);
                        float depthDiff = jDepth - iDepth;

                        if (depthDiff > 0 && depthDiff < maxWakeDist)
                        {
                            // j is upstream (further along flow), i receives wake
                            _aabb.Apply(instances[j], instances[i],
                                flowDir, axisU, axisV, wakeHalfAngle, wakeDecayDistance);
                        }
                        else if (depthDiff < 0 && -depthDiff < maxWakeDist)
                        {
                            // i is upstream, j receives wake
                            _aabb.Apply(instances[i], instances[j],
                                flowDir, axisU, axisV, wakeHalfAngle, wakeDecayDistance);
                        }
                    }
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Pack(int u, int v) => ((long)u << 32) | (uint)v;

    private List<int> RentList()
    {
        if (_poolCursor < _pool.Count)
        {
            var list = _pool[_poolCursor++];
            list.Clear();
            return list;
        }
        var newList = new List<int>(4);
        _pool.Add(newList);
        _poolCursor++;
        return newList;
    }
}

/// <summary>
/// AABB overlap → group-level shadow fraction. O(1) per pair, no face iteration.
/// Combines with <see cref="SpatialHashAabbStrategy"/>-style spatial hash for
/// O(N × K) total with K ≈ avg neighbors.
///
/// Instead of iterating faces, computes what fraction of the receiver's
/// projected AABB is covered by the caster's wake silhouette, and stores
/// this as ExternalShadowFraction on the receiver. Used with
/// <see cref="MultiGridAeroSystem.ComputeGroupLevel"/> for O(6) force computation.
///
/// Fastest possible inter-grid shadow. No per-face arrays touched.
/// </summary>
public sealed class SpatialHashGroupStrategy : InterGridShadowStrategy
{
    public override string Name => "Spatial Group";
    public override string Description => "Spatial hash + AABB overlap fraction, O(1) per pair";

    private readonly Dictionary<long, List<int>> _hash = new();
    private readonly List<List<int>> _pool = new();
    private int _poolCursor;

    public override void Apply(GridAeroInstance caster, GridAeroInstance receiver,
        Vector3 flowDir, Vector3 axisU, Vector3 axisV,
        float wakeHalfAngle, float wakeDecayDistance)
    {
        Vector3 delta = receiver.WorldPosition - caster.WorldPosition;
        float wakeDist = -Vector3.Dot(delta, flowDir);
        if (wakeDist <= 0) return;

        float distInBlocks = wakeDist / caster.BlockSize;
        if (distInBlocks > wakeDecayDistance) return;

        // Project caster AABB + wake expansion
        var (cMin, cMax) = caster.WorldBounds;
        float cMinU = float.MaxValue, cMaxU = float.MinValue;
        float cMinV = float.MaxValue, cMaxV = float.MinValue;
        ShadowMath.ProjectAABBToPlane(cMin, cMax, axisU, axisV,
            ref cMinU, ref cMaxU, ref cMinV, ref cMaxV);

        float wakeExp = wakeDist * MathF.Tan(wakeHalfAngle);
        cMinU -= wakeExp; cMaxU += wakeExp;
        cMinV -= wakeExp; cMaxV += wakeExp;

        // Project receiver AABB
        var (rMin, rMax) = receiver.WorldBounds;
        float rMinU = float.MaxValue, rMaxU = float.MinValue;
        float rMinV = float.MaxValue, rMaxV = float.MinValue;
        ShadowMath.ProjectAABBToPlane(rMin, rMax, axisU, axisV,
            ref rMinU, ref rMaxU, ref rMinV, ref rMaxV);

        if (rMaxU < cMinU || rMinU > cMaxU || rMaxV < cMinV || rMinV > cMaxV)
            return;

        // Overlap fraction
        float overlapU = MathF.Min(cMaxU, rMaxU) - MathF.Max(cMinU, rMinU);
        float overlapV = MathF.Min(cMaxV, rMaxV) - MathF.Max(cMinV, rMinV);
        float receiverU = rMaxU - rMinU;
        float receiverV = rMaxV - rMinV;

        float frac = (overlapU / MathF.Max(receiverU, 0.001f))
                   * (overlapV / MathF.Max(receiverV, 0.001f));
        frac = MathF.Min(frac, 1f);

        // Accumulate — multiple casters can shadow the same receiver
        receiver.ExternalShadowFraction = MathF.Min(receiver.ExternalShadowFraction + frac, 1f);
    }

    public override void ApplyAll(IReadOnlyList<GridAeroInstance> instances,
        Vector3 flowDir, Vector3 axisU, Vector3 axisV,
        float wakeHalfAngle, float wakeDecayDistance)
    {
        int count = instances.Count;
        if (count < 2) return;

        float blockSize = instances[0].BlockSize;
        float maxWakeDist = wakeDecayDistance * blockSize;
        float maxWakeWidth = maxWakeDist * MathF.Tan(wakeHalfAngle);
        float bucketSize = MathF.Max(maxWakeWidth * 2, blockSize * 10);
        float invBucket = 1f / bucketSize;

        for (int i = 0; i < count; i++)
            instances[i].RefreshWorldBounds();

        _hash.Clear();
        _poolCursor = 0;

        for (int i = 0; i < count; i++)
        {
            var pos = instances[i].WorldPosition;
            int bu = (int)MathF.Floor(Vector3.Dot(pos, axisU) * invBucket);
            int bv = (int)MathF.Floor(Vector3.Dot(pos, axisV) * invBucket);
            long key = Pack(bu, bv);

            if (!_hash.TryGetValue(key, out var bucket))
            {
                bucket = RentList();
                _hash[key] = bucket;
            }
            bucket.Add(i);
        }

        for (int i = 0; i < count; i++)
        {
            var pos = instances[i].WorldPosition;
            int bu = (int)MathF.Floor(Vector3.Dot(pos, axisU) * invBucket);
            int bv = (int)MathF.Floor(Vector3.Dot(pos, axisV) * invBucket);

            for (int du = -1; du <= 1; du++)
            {
                for (int dv = -1; dv <= 1; dv++)
                {
                    long key = Pack(bu + du, bv + dv);
                    if (!_hash.TryGetValue(key, out var bucket)) continue;

                    for (int bi = 0; bi < bucket.Count; bi++)
                    {
                        int j = bucket[bi];
                        if (j <= i) continue;

                        float jDepth = Vector3.Dot(instances[j].WorldPosition, flowDir);
                        float iDepth = Vector3.Dot(pos, flowDir);
                        float depthDiff = jDepth - iDepth;

                        if (depthDiff > 0 && depthDiff < maxWakeDist)
                            Apply(instances[j], instances[i], flowDir, axisU, axisV, wakeHalfAngle, wakeDecayDistance);
                        else if (depthDiff < 0 && -depthDiff < maxWakeDist)
                            Apply(instances[i], instances[j], flowDir, axisU, axisV, wakeHalfAngle, wakeDecayDistance);
                    }
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Pack(int u, int v) => ((long)u << 32) | (uint)v;

    private List<int> RentList()
    {
        if (_poolCursor < _pool.Count) { var l = _pool[_poolCursor++]; l.Clear(); return l; }
        var n = new List<int>(4); _pool.Add(n); _poolCursor++; return n;
    }
}

/// <summary>
/// Column depth buffer approach — O(faces × (2r+1)²) per pair, N²/2 iteration.
/// </summary>
public sealed class ColumnDepthStrategy : InterGridShadowStrategy
{
    public override string Name => "Column Depth";
    public override string Description => "Per-column depth buffer, O(faces × (2r+1)²) per pair";

    private readonly Dictionary<int, ColumnShadowMap> _buffers = new();

    public override void Prepare(IReadOnlyList<GridAeroInstance> instances,
        Vector3 flowDir, Vector3 axisU, Vector3 axisV)
    {
        foreach (var inst in instances)
        {
            var buf = GetOrCreate(inst.Grid.Id);
            buf.BuildWorldBuffer(inst.Grid, inst.BlockSize, inst.WorldPosition,
                flowDir, axisU, axisV);
        }
    }

    public override void Apply(GridAeroInstance caster, GridAeroInstance receiver,
        Vector3 flowDir, Vector3 axisU, Vector3 axisV,
        float wakeHalfAngle, float wakeDecayDistance)
    {
        Vector3 delta = receiver.WorldPosition - caster.WorldPosition;
        float wakeDist = -Vector3.Dot(delta, flowDir);
        if (wakeDist <= 0) return;

        float distInBlocks = wakeDist / caster.BlockSize;
        if (distInBlocks > wakeDecayDistance) return;

        int expansion = (int)MathF.Ceiling(distInBlocks * MathF.Tan(wakeHalfAngle));

        var casterBuf = _buffers[caster.Grid.Id];
        var faces = receiver.Surface.Faces;
        var selfVis = receiver.ColumnShadowVisibility ?? receiver.SelfShadow.Visibility;

        for (int i = 0; i < faces.Count; i++)
        {
            float cosAlpha = -Vector3.Dot(flowDir, faces[i].Normal);
            if (cosAlpha <= 0) continue;

            if (i < selfVis.Length && !selfVis[i]) continue;

            Vector3 faceWorld = receiver.FaceToWorld(faces[i].Position);

            bool shadowed = expansion <= 0
                ? casterBuf.QueryColumnExists(faceWorld, caster.BlockSize)
                : casterBuf.QueryColumnExistsExpanded(faceWorld, caster.BlockSize, expansion);

            if (shadowed)
            {
                receiver.ExternalShadow[i] = true;
                receiver.ExternalShadowCount++;
            }
        }
    }

    private ColumnShadowMap GetOrCreate(int gridId)
    {
        if (!_buffers.TryGetValue(gridId, out var shadow))
        {
            shadow = new ColumnShadowMap();
            _buffers[gridId] = shadow;
        }
        return shadow;
    }
}

/// <summary>
/// Shared math utilities for shadow strategies.
/// </summary>
internal static class ShadowMath
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ProjectAABBToPlane(Vector3 bmin, Vector3 bmax,
        Vector3 axisU, Vector3 axisV,
        ref float minU, ref float maxU, ref float minV, ref float maxV)
    {
        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = new(
                (i & 1) == 0 ? bmin.X : bmax.X,
                (i & 2) == 0 ? bmin.Y : bmax.Y,
                (i & 4) == 0 ? bmin.Z : bmax.Z);

            float u = Vector3.Dot(corner, axisU);
            float v = Vector3.Dot(corner, axisV);

            minU = MathF.Min(minU, u);
            maxU = MathF.Max(maxU, u);
            minV = MathF.Min(minV, v);
            maxV = MathF.Max(maxV, v);
        }
    }

    public static void GetPerpendicularAxes(Vector3 dir, out Vector3 axisU, out Vector3 axisV)
    {
        Vector3 up = MathF.Abs(dir.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        axisU = Vector3.Normalize(Vector3.Cross(dir, up));
        axisV = Vector3.Cross(dir, axisU);
    }
}
