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

    private readonly record struct ComponentRegistration(
        Type ComponentType,
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
    public void RegisterByComponent<T>(Func<BlockInfo, T, IAeroBlockComponent> factory) where T : class
    {
        _componentFactories.Add(new(typeof(T),
            (info, comp) => factory(info, (T)comp)));
    }

    /// <summary>
    /// Non-generic overload for runtime-resolved component types.
    /// The component is passed as object to the factory.
    /// </summary>
    public void RegisterByComponent(Type componentType, Func<BlockInfo, object, IAeroBlockComponent> factory)
    {
        _componentFactories.Add(new(componentType, factory));
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
            Log.Default?.Info($"[AERO] Factory created {comp.GetType().Name} at {info.BlockPosition} " +
                $"fwd={orient.Forward} up={orient.Up} " +
                $"fwdVec=({info.Forward.X:F2},{info.Forward.Y:F2},{info.Forward.Z:F2}) " +
                $"upVec=({info.Up.X:F2},{info.Up.Y:F2},{info.Up.Z:F2}) " +
                $"pos=({info.Position.X:F2},{info.Position.Y:F2},{info.Position.Z:F2}) " +
                $"area={info.FaceArea:F3}");
            return comp;
        }

        // 2. Component match (vanilla blocks with specific SE2 components)
        // Iterate Entity.Components to find matching types. This bypasses
        // the tag-based Entity.TryGet which fails due to DEntityContext boxing
        // when invoked via reflection.
        for (int i = 0; i < _componentFactories.Count; i++)
        {
            var reg = _componentFactories[i];
            var siblingComp = PhysicsHack.FindComponentByType(block.Entity, reg.ComponentType);
            if (siblingComp != null)
            {
                var info = BuildBlockInfo(block, blockSize);
                var comp = reg.Factory(info, siblingComp);
                if (comp != null)
                {
                    Log.Default?.Info($"[AERO] Factory created {comp.GetType().Name} (by component {reg.ComponentType.Name}) at {info.BlockPosition}");
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
            Log.Default?.Info($"[AERO] Factory.CreateAll: {created} aero components from {blocks.Length} blocks");
        }
    }

    /// <summary>
    /// Get the definition GUID via reflection. CubeBlockDefinition inherits from
    /// MaxHealthComponentDefinition (VRage.Game) which isn't referenced by mod scripts,
    /// so we can't access block.Definition or its .Guid property directly at compile time.
    /// </summary>
    private static Guid? GetDefinitionGuid(CubeBlockComponent block)
    {
        _definitionProperty ??= typeof(CubeBlockComponent).GetProperty("Definition",
            BindingFlags.Public | BindingFlags.Instance);

        var def = _definitionProperty?.GetValue(block);
        if (def == null)
        {
            return null;
        }

        _guidProperty ??= def.GetType().GetProperty("Guid",
            BindingFlags.Public | BindingFlags.Instance);

        if (_guidProperty != null)
        {
            return (Guid)_guidProperty.GetValue(def);
        }

        return null;
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
        Vector3 forward = DirectionToVector(orientation.Forward);
        Vector3 up = DirectionToVector(orientation.Up);

        // Grid-local position in meters (block center)
        // SE2 cells are 0.25m each; use center of min..max range
        const float CellSize = 0.25f;
        Vector3 position = new Vector3(
            (blockMin.X + blockMax.X) * 0.5f * CellSize,
            (blockMin.Y + blockMax.Y) * 0.5f * CellSize,
            (blockMin.Z + blockMax.Z) * 0.5f * CellSize);

        return new BlockInfo(position, blockPos, forward, up, blockSize);
    }

    /// <summary>
    /// Convert a Base6Directions.Direction to a float unit vector.
    /// </summary>
    private static Vector3 DirectionToVector(Base6Directions.Direction dir)
    {
        var iv = Base6Directions.GetIntVector(dir);
        return new Vector3(iv.X, iv.Y, iv.Z);
    }
}
