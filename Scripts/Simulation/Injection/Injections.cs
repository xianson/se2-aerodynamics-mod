using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.DCS.Definitions;
using Keen.VRage.Library.Reflection;
using static Keen.VRage.DCS.Builders.EntityBuilder;

#pragma warning disable
namespace AeroMod;

/// <summary>
/// Base class for component injection into SE2 entity prefabs.
/// From the ScriptingTemplate pattern — handles the reflection-based
/// composition patching that SE2 requires.
/// </summary>
public class Injections
{
    protected static void Add(PrefabDefinition prefab, System.Type componentType)
    {
        Add(prefab, new ComponentBuildInfo { Type = componentType });
    }

    protected static void Add(PrefabDefinition prefab, ComponentBuildInfo component)
    {
        var c = prefab.Composition;

        if (component.ComponentId == default)
        {
            component.ComponentId = new($"deadbeee-beef-afaf-beee-{c.ComponentCount:X12}");
        }

        EntityCompositeDefinition.Builder newComposition = new()
        {
            ComponentCount = c.ComponentCount,
            Ids = c.Ids.ToArray(),
            Types = c.Types.ToArray(),
            Definitions = c.Definitions.ToArray(),
            Tags = c.Tags.ToArray(),
        };

        Add(ref newComposition, component);
        c.TryInvokeMethod("Assign", includeNonPublic: true, isStatic: false, [newComposition], out _);

        var eob = prefab.Get();
        eob.ObjectBuilders.Add(component.ComponentId, component.ObjectBuilder);

        prefab.GetFieldInfo("_entity")!.SetValue(prefab, eob);
    }

    protected static void Add(ref EntityCompositeDefinition.Builder entityComposition, ComponentBuildInfo component)
    {
        var type = ResolveComponent(component).ComponentType;

        entityComposition.ComponentCount++;
        entityComposition.InvalidateTagIndex();
        entityComposition.Types = [.. entityComposition.Types, type];
        entityComposition.Ids = [.. entityComposition.Ids, component.ComponentId];
        entityComposition.Definitions = [.. entityComposition.Definitions, (Definition?)component.Definition];
        entityComposition.Tags = [.. entityComposition.Tags, [.. FixTags(type, component.Tags)]];
    }
}
