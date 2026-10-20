#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Collections.Immutable;
using System.Reflection;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails;
using Keen.VRage.Library.Serialization;

namespace AeroMod;

/// <summary>
/// The member list a mod's control-panel model needs to show its controls. The terminal's inspector reads a model's
/// members from its IAccessible.GetTypeInfo() - for the game's own models the source generators write that (a
/// TypeInfoHolder per model), and they do not run on mod scripts: a mod model inherited BlockDetailModel's list and its
/// toggles and sliders never showed. Built here by hand the way the generated code builds it: BlockDetailModel's members,
/// then the model's own, in the order given, each with its attributes ([Label], [Slider], [Range], [Increment],
/// [ReadOnlyInInspector], [ShowInInspectorWhen] - read off the property). bool, float, string and enum properties; a parameterless
/// [Button] method's name places that button (order only).
/// THE ORDER SHOWN (decompiled 4.0 TerminalInspectorViewModel.InjectInspectable): each property's place is
/// MemberReference.DeclarationOrder, which asks AccessibleTypeIndexer for the DECLARING type's info - found only through a public
/// nested class TypeInfoHolder with a static Value field (the generated code's). Without one every mod property sorts as -1, and
/// List.Sort is unstable past 16 entries: a big panel came out shuffled (the autopilot's, 2026-10-19). So a model built here
/// declares <c>public static class TypeInfoHolder { public static readonly AccessibleTypeInfo Value = _typeInfo; }</c>. Buttons
/// take their place from this info's MembersDeclarationOrder (a method not named: last).
/// </summary>
public static class ModDetailTypeInfo
{
    public static AccessibleTypeInfo Build<TModel>(string documentation, params string[] properties) where TModel : BlockDetailModel
        => Build<TModel>(documentation, null, properties);

    /// <summary>The same, with tooltips (2026-10-20): a member's documentation is what the inspector shows on hover (ValuePropertyElement /
    /// MethodPropertyElement read MembersDocumentation; plain text - the game's XmlToRichTextConverter) - by member name.</summary>
    public static AccessibleTypeInfo Build<TModel>(string documentation, IReadOnlyDictionary<string, string> tips, params string[] properties) where TModel : BlockDetailModel
    {
        var baseInfo = BlockDetailModel.TypeInfoHolder.Value;
        var accessors = new List<(string Name, IMemberAccessor Accessor)>(BlockDetailModel.TypeInfoHolder.RawAccessors);
        var order = ImmutableDictionary.CreateBuilder<MemberInfo, int>();
        var docs = ImmutableDictionary.CreateBuilder<MemberInfo, string>();
        var attrs = ImmutableDictionary.CreateBuilder<MemberInfo, ListReader<Attribute>>();
        order.AddRange(baseInfo.MembersDeclarationOrder);
        docs.AddRange(baseInfo.MembersDocumentation);
        attrs.AddRange(baseInfo.MembersAttributes);
        int next = 0;
        foreach (var kv in baseInfo.MembersDeclarationOrder) next = Math.Max(next, kv.Value + 1);
        foreach (var name in properties)
        {
            var p = typeof(TModel).GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (p == null)
            {
                // a [Button] method: placed by its name too (the inspector reads a button's place from MembersDeclarationOrder by
                // its MethodInfo - TerminalInspectorViewModel.InjectInspectable; one not listed goes to the bottom, int.MaxValue)
                var m = typeof(TModel).GetMethod(name, BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                if (m == null) throw new ArgumentException($"{typeof(TModel).Name} has no public property or parameterless method {name}");
                order[m] = next++;
                if (tips != null && tips.TryGetValue(name, out var mt)) docs[m] = mt;
                continue;
            }
            IMemberAccessor a;
            if (p.PropertyType == typeof(bool)) a = Accessor<TModel, bool>(p);
            else if (p.PropertyType == typeof(float)) a = Accessor<TModel, float>(p);
            else if (p.PropertyType == typeof(string)) a = Accessor<TModel, string>(p);   // (2026-10-19: the autopilot's status line, its route and target texts)
            else if (p.PropertyType.IsEnum)   // (2026-10-19: the autopilot's style / behaviour / target source - the inspector's enum selector)
                a = (IMemberAccessor)typeof(ModDetailTypeInfo).GetMethod(nameof(Accessor), BindingFlags.NonPublic | BindingFlags.Static)
                    .MakeGenericMethod(typeof(TModel), p.PropertyType).Invoke(null, new object[] { p });
            else throw new ArgumentException($"{typeof(TModel).Name}.{name}: only bool, float, string and enum are supported");

            accessors.Add((name, a));
            order[p] = next++;
            docs[p] = tips != null && tips.TryGetValue(name, out var tip) ? tip : null;
            var list = new List<Attribute>();
            foreach (var at in p.GetCustomAttributes(true)) if (at is Attribute att) list.Add(att);
            attrs[p] = list;
        }
        return new AccessibleTypeInfo(typeof(TModel), documentation, accessors, order.ToImmutable(), docs.ToImmutable(), attrs.ToImmutable());
    }

    static IMemberAccessor Accessor<TModel, T>(PropertyInfo p) where TModel : class
    {
        var get = (Func<TModel, T>)p.GetMethod.CreateDelegate(typeof(Func<TModel, T>));
        var set = (Action<TModel, T>)p.SetMethod?.CreateDelegate(typeof(Action<TModel, T>));
        return new ClassMemberAccessor<TModel, T>(p.Name, p.Name, SerializerFormatSet.None, p, typeof(T), null,
            instance => get(instance),
            set == null ? null : (TModel instance, in T value) => set(instance, value),
            null);
    }
}
