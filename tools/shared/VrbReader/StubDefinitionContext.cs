using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Keen.VRage.Library.Definitions;

namespace VrbReader;

/// <summary>
/// Stub DefinitionSerializationContext that creates placeholder Definition objects
/// for any GUID. Allows deserializing blueprints without the full game definition database.
/// </summary>
public class StubDefinitionContext : DefinitionSerializationContext
{
    private readonly Dictionary<Guid, Definition> _cache = new();
    private static readonly PropertyInfo GuidProp =
        typeof(Definition).GetProperty("Guid", BindingFlags.Public | BindingFlags.Instance)!;

    static readonly Dictionary<Type, Type?> _concrete = new();
    static Type? Concrete(Type abs)
    {
        if (_concrete.TryGetValue(abs, out var c)) return c;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] ts; try { ts = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { ts = e.Types.Where(x => x != null).ToArray()!; }
            foreach (var x in ts) if (x != null && !x.IsAbstract && abs.IsAssignableFrom(x) && x.GetConstructor(Type.EmptyTypes) != null) { _concrete[abs] = x; return x; }
        }
        _concrete[abs] = null; return null;
    }

    public override bool TryLocateDefinition(Guid id, Type hintType,
        [MaybeNullWhen(false)] out Definition definition)
    {
        if (_cache.TryGetValue(id, out definition))
            return true;

        // Create a stub definition with the right GUID (an abstract hint - a weapon's ProjectileDefinition - stands in as any
        // concrete subtype of it: only the GUID is read)
        var t = hintType ?? typeof(Definition);
        if (t.IsAbstract) t = Concrete(t) ?? typeof(Definition);
        definition = (Definition)Activator.CreateInstance(t)!;
        GuidProp.SetValue(definition, id);
        _cache[id] = definition;
        return true;
    }
}
