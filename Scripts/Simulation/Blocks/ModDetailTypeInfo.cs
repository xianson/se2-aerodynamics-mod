#pragma warning disable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails;
using Keen.VRage.Library.Collections.Readers;
using Keen.VRage.Library.Serialization;

namespace AeroMod;

/// <summary>
/// The member list a mod's control-panel model needs to show its controls. The terminal's inspector reads a model's
/// members from its IAccessible.GetTypeInfo() - for the game's own models the source generators write that (a
/// TypeInfoHolder per model), and they do not run on mod scripts: a mod model inherited BlockDetailModel's list and its
/// toggles and sliders never showed. Built here by hand the way the generated code builds it: BlockDetailModel's members,
/// then the model's own, in the order given (the inspector sorts by it), each with its attributes ([Label], [Slider],
/// [Range], [Increment] - read off the property). Only bool and float properties (what the panels use).
/// </summary>
public static class ModDetailTypeInfo
{
    public static AccessibleTypeInfo Build<TModel>(string documentation, params string[] properties) where TModel : BlockDetailModel
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
            if (p == null) throw new ArgumentException($"{typeof(TModel).Name} has no public property {name}");
            IMemberAccessor a;
            if (p.PropertyType == typeof(bool)) a = Accessor<TModel, bool>(p);
            else if (p.PropertyType == typeof(float)) a = Accessor<TModel, float>(p);
            else throw new ArgumentException($"{typeof(TModel).Name}.{name}: only bool and float are supported");
            accessors.Add((name, a));
            order[p] = next++;
            docs[p] = null;
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
