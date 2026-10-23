#pragma warning disable
using System;
using System.Reflection;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.BlockOctrees;
namespace AeroMod;

/// <summary>
/// Creates IAeroBlockComponent instances for blocks based on their definition GUID
/// or the presence of specific SE2 components (e.g. ThrusterComponent).
/// </summary>
public class BlockComponentFactory
{
    private readonly Dictionary<Guid, Func<BlockInfo, IAeroBlockComponent>> _guidFactories = new();
    private readonly List<ComponentRegistration> _componentFactories = new();

    // Cached reflection accessors (CubeBlockDefinition's base is in unreferenced VRage.Game)
    private static PropertyInfo _definitionProperty;
    private static PropertyInfo _guidProperty;

    /// <summary>
    /// The lookup is captured generically at registration time, so finding the sibling
    /// component is a typed Entity.TryGet&lt;T&gt;() rather than a reflective scan over
    /// Entity.Components (which is ImmutableArray and therefore VRS1001-banned).
    /// </summary>
    private readonly record struct ComponentRegistration(
        string ComponentName,
        Func<Entity, Component> Lookup,
        Func<BlockInfo, object, IAeroBlockComponent> Factory);

    /// <summary>
    /// Register a factory that fires when a placed block's Definition.Guid matches.
    /// Used for our custom aero blocks (ControlSurface, Airbrake, Scoop).
    /// </summary>
    public void RegisterByGuid(Guid defId, Func<BlockInfo, IAeroBlockComponent> factory)
    {
        _guidFactories[defId] = factory;
    }

    /// <summary>
    /// Register a factory that fires when a block carries a sibling component of type T.
    /// Used for vanilla blocks (e.g. atmospheric thruster -> AirIntake).
    /// </summary>
    public void RegisterByComponent<T>(Func<BlockInfo, T, IAeroBlockComponent> factory) where T : Component
    {
        _componentFactories.Add(new(
            typeof(T).Name,
            e => e.TryGet<T>(),
            (info, comp) => factory(info, (T)comp)));
    }

    /// <summary>
    /// Try to create an aero component for a single block.
    /// Returns null if no factory matches this block.
    /// </summary>
    public IAeroBlockComponent TryCreate(CubeBlockComponent block, float blockSize)
    {
        if (blockSize <= 0)
        {
            return null;
        }

        // 1. GUID match (custom aero blocks)
        var guid = GetDefinitionGuid(block);
        if (guid.HasValue && _guidFactories.TryGetValue(guid.Value, out var guidFactory))
        {
            var info = BuildBlockInfo(block, blockSize);
            var comp = guidFactory(info);
            var orient = block.BlockOrientation;
            if (AeroLog.Detail) Log.Default?.Info($"[AERO] Factory created {comp.GetType().Name} at {info.BlockPosition} " +
                $"fwd={orient.Forward} up={orient.Up} " +
                $"fwdVec=({info.Forward.X:F2},{info.Forward.Y:F2},{info.Forward.Z:F2}) " +
                $"upVec=({info.Up.X:F2},{info.Up.Y:F2},{info.Up.Z:F2}) " +
                $"pos=({info.Position.X:F2},{info.Position.Y:F2},{info.Position.Z:F2}) " +
                $"extents=({info.Extents.X:F2},{info.Extents.Y:F2},{info.Extents.Z:F2})");
            return comp;
        }

        // 2. Component match (vanilla blocks with specific SE2 components).
        for (int i = 0; i < _componentFactories.Count; i++)
        {
            var reg = _componentFactories[i];
            var siblingComp = reg.Lookup(block.Entity);
            if (siblingComp != null)
            {
                var info = BuildBlockInfo(block, blockSize);
                var comp = reg.Factory(info, siblingComp);
                if (comp != null)
                {
                    if (AeroLog.Detail) Log.Default?.Info($"[AERO] Factory created {comp.GetType().Name} (by component {reg.ComponentName}) at {info.BlockPosition}");
                    return comp;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Scan every block in the grid and create aero components for all matches.
    /// Called during full grid rebuild.
    /// </summary>
    public void CreateAll(BlockOctreeComponent octree, float blockSize, AeroComponentRegistry target)
    {
        int created = 0;
        var blocks = octree.GetAllCubeBlocks();
        foreach (var block in blocks)
        {
            if (block == null)
            {
                continue;
            }

            var comp = TryCreate(block, blockSize);
            if (comp != null)
            {
                target.Add(comp);
                created++;
            }
        }

        if (created > 0)
        {
            if (AeroLog.Detail) Log.Default?.Info($"[AERO] Factory.CreateAll: {created} aero components from {blocks.Length} blocks");
        }
    }

    /// <summary>
    /// Definition GUID. HOLDOUT -- CubeBlockComponent.Definition is public, but
    /// CubeBlockDefinition derives from MaxHealthComponentDefinition in VRage.Game, which mod
    /// scripts do not reference (CS0012), so the compiler cannot reach Definition.Guid.
    /// </summary>
    /// <summary>Blocks whose component models ALL their air (lift, drag): their cells stay out of the voxel surface -
    /// else the 3x3x1 control surface, a flat plate, was also a wing (lift twice) and a bluff body.</summary>
    static readonly HashSet<Guid> _selfModelled = SelfModelled();

    static HashSet<Guid> SelfModelled()
    {
        var s = new HashSet<Guid>(AeroBlockGuids.ControlSurfaces3x3x1);
        foreach (var (guid, _) in AeroBlockGuids.Airbrakes3x3x3) s.Add(guid);   // (the airbrake: its box is reserved room, not a body)
        return s;
    }

    /// <summary>True for a block the surface must leave out (its component models it whole).</summary>
    public static bool IsSelfModelled(CubeBlockComponent block)
    {
        var g = GetDefinitionGuid(block);
        return g.HasValue && (_selfModelled.Contains(g.Value) || PropulsorBlocks.Blocks.Contains(g.Value));   // (a propulsor's disc is reserved room, not a body)
    }

    internal static Guid? GetDefinitionGuid(CubeBlockComponent block)
    {
        if (block == null) return null;
        _definitionProperty ??= typeof(CubeBlockComponent).GetProperty("Definition",
            BindingFlags.Public | BindingFlags.Instance);
        var def = _definitionProperty?.GetValue(block);
        if (def == null) return null;

        _guidProperty ??= def.GetType().GetProperty("Guid",
            BindingFlags.Public | BindingFlags.Instance);
        return _guidProperty != null ? (Guid)_guidProperty.GetValue(def) : (Guid?)null;
    }


    /// <summary>
    /// Extract placement info from an SE2 CubeBlockComponent.
    /// </summary>
    private static BlockInfo BuildBlockInfo(CubeBlockComponent block, float blockSize)
    {
        // Get block center from occupied cell groups
        Vector3I blockMin = default;
        Vector3I blockMax = default;
        bool first = true;
        foreach (var cellGroup in block.GetTransformedOccupiedCellGroups())
        {
            if (first)
            {
                blockMin = cellGroup.Min;
                blockMax = cellGroup.Max;
                first = false;
            }
            else
            {
                blockMin = Vector3I.Min(blockMin, cellGroup.Min);
                blockMax = Vector3I.Max(blockMax, cellGroup.Max);
            }
        }

        // Use center of occupied region, not min corner
        Vector3I blockPos = blockMin;

        var orientation = block.BlockOrientation;
        Vector3 forward = Base6Directions.GetVector(orientation.Forward);
        Vector3 up = Base6Directions.GetVector(orientation.Up);

        // Grid-local position in meters (block center)
        // SE2 cells are 0.25m each and cell i is centred at i x 0.25 (CubeGridCoords.GridToLocal), so the centre of the
        // min..max range is (min + max) / 2 x 0.25 - no half-cell term
        const float CellSize = 0.25f;
        Vector3 position = new Vector3(
            (blockMin.X + blockMax.X) * 0.5f * CellSize,
            (blockMin.Y + blockMax.Y) * 0.5f * CellSize,
            (blockMin.Z + blockMax.Z) * 0.5f * CellSize);

        // (cell i is CENTRED at i x 0.25 m - CubeGridCoords.GridToLocal: the block spans (max - min + 1) cells per axis)
        Vector3 extents = new Vector3(blockMax.X - blockMin.X + 1, blockMax.Y - blockMin.Y + 1, blockMax.Z - blockMin.Z + 1) * CellSize;

        var aabb = block.AABB;
        return new BlockInfo(position, blockPos, forward, up, blockSize, extents) { CellKey = aabb.Min + aabb.Max, Settings = block.Entity?.TryGet<AeroSurfaceSettingsComponent>(), Gear = block.Entity?.TryGet<AeroGearComponent>(), Airbrake = block.Entity?.TryGet<AeroAirbrakeComponent>() };
    }
}
