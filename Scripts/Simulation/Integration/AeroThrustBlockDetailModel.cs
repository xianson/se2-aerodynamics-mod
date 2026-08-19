#pragma warning disable
using System;
using System.Reflection;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails;
using Keen.VRage.DCS.Annotations;
using Keen.VRage.Library.Reflection;
using Keen.VRage.Library.Serialization.Validation;
using Keen.VRage.Library.UI.PropertyInspector;

namespace AeroMod;

// ════════════════════════════════════════════════════════════════════
// TODO [MULTIPLAYER]
//
// [Replicate] and [SyncFromClients] from Keen.VRage.Multiplayer.Annotations
// are not in the mod script whitelist. Without them:
//   - Slider works for host / single-player only.
//   - Value won't replicate to clients in MP.
// ════════════════════════════════════════════════════════════════════

/// <summary>
/// Terminal slider for per-thruster attitude authority.
/// </summary>
[BlockDetailProvider]
public class AeroThrustBlockDetailModel : BlockDetailModel
{
    [Keen.VRage.DCS.Annotations.Component]
    private AeroThrustSettingsComponent _settings;

    public AeroThrustBlockDetailModel()
    {
        Log.Default?.Info($"[AERO] AeroThrustBlockDetailModel CTOR settings={_settings != null}");
    }

    [Slider(null)]
    [Range<float>(0f, 1f, RangeMode.InIn)]
    [Label("Attitude Authority")]
    [Increment(0.01f, 0.05f, 0.1f)]
    public float AttitudeFraction
    {
        get => _settings?.AttitudeFraction ?? 0.5f;
        set
        {
            if (_settings != null)
                _settings.AttitudeFraction = Math.Clamp(value, 0f, 1f);
        }
    }
}

/// <summary>
/// Manually registers AeroThrustBlockDetailModel into BlockDetailProviderIndexer.
/// The core game indexer runs AfterInit() before mod assemblies are loaded,
/// so our [BlockDetailProvider] is never discovered. We inject via reflection
/// (same pattern as PhysicsHack).
///
/// ImmutableArray is banned from scripts, so we build everything via reflection
/// to avoid referencing banned types at compile time.
/// </summary>
public partial class AeroThrustSettingsComponent
{
    private static bool _registrationDone;

    public static void RegisterDetailModel()
    {
        if (_registrationDone) return;
        _registrationDone = true;

        try
        {
            // 1. Get BlockDetailProviderIndexer.Instance
            var indexerType = Type.GetType(
                "Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails.BlockDetailProviderIndexer, Game2.Simulation",
                throwOnError: false);
            if (indexerType == null) { Log.Default?.Info("[AERO] DetailReg: indexer type not found"); return; }

            var instanceProp = indexerType.BaseType?.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            var instance = instanceProp?.GetValue(null);
            if (instance == null) { Log.Default?.Info("[AERO] DetailReg: indexer instance null"); return; }

            // 2. Get or create BlockDetails backing dictionary
            var detailsProp = indexerType.GetProperty("BlockDetails",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            object details = detailsProp?.GetValue(instance);

            var viewInfoType = Type.GetType(
                "Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails.BlockDetailViewModelInfo, Game2.Simulation",
                throwOnError: false);
            if (viewInfoType == null) { Log.Default?.Info("[AERO] DetailReg: ViewModelInfo type not found"); return; }

            // Get the backing dictionary from DictionaryReader via reflection
            object backingDict = null;
            if (details != null)
            {
                foreach (var f in details.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    var val = f.GetValue(details);
                    if (val != null && val.GetType().IsGenericType &&
                        val.GetType().GetGenericTypeDefinition().FullName.Contains("Dictionary"))
                    {
                        backingDict = val;
                        break;
                    }
                }
            }

            if (backingDict == null)
            {
                // Create new dictionary and set it on the indexer
                var dictType = typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(typeof(Type), viewInfoType);
                backingDict = Activator.CreateInstance(dictType);

                var readerType = typeof(DictionaryReader<,>).MakeGenericType(typeof(Type), viewInfoType);
                var reader = Activator.CreateInstance(readerType, backingDict);
                detailsProp?.SetValue(instance, reader);
                Log.Default?.Info("[AERO] DetailReg: created new BlockDetails dictionary");
            }

            // 3. Build BlockDetailViewModelInfo entirely via reflection
            //    (ImmutableArray is banned from scripts, must use reflection)
            var viewInfo = Activator.CreateInstance(viewInfoType);
            var ourType = typeof(AeroThrustBlockDetailModel);

            // Type property — expects SubclassOf<BlockDetailModel>, not raw Type
            var subclassOfType = typeof(SubclassOf<>).MakeGenericType(typeof(BlockDetailModel));
            var subclassValue = MakeSubclassOf(subclassOfType, ourType);
            if (subclassValue == null)
            {
                Log.Default?.Info("[AERO] DetailReg: could not construct SubclassOf<BlockDetailModel>");
                return;
            }
            viewInfoType.GetProperty("Type")?.SetValue(viewInfo, subclassValue);

            // Dependencies: ImmutableArray<(FieldInfo, DependencyKind)>
            // Build via reflection: ImmutableArray.Create(item)
            var settingsField = ourType.GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance);
            var depKindType = viewInfoType.GetNestedType("DependencyKind");
            var componentKind = Enum.Parse(depKindType, "Component");

            // Create the tuple (FieldInfo, DependencyKind)
            var tupleType = typeof(ValueTuple<,>).MakeGenericType(typeof(FieldInfo), depKindType);
            var tuple = Activator.CreateInstance(tupleType, settingsField, componentKind);

            // Use ImmutableArray.Create<T>(T item) via reflection to avoid banned symbol
            var immArrayType = Type.GetType("System.Collections.Immutable.ImmutableArray, System.Collections.Immutable");
            if (immArrayType == null)
            {
                // Try the runtime assembly
                var sampleField = viewInfoType.GetProperty("Dependencies");
                if (sampleField != null)
                    immArrayType = sampleField.PropertyType.Assembly.GetType("System.Collections.Immutable.ImmutableArray");
            }
            if (immArrayType != null)
            {
                // ImmutableArray.Create<T>(T item) — single-element overload
                MethodInfo createMethod = null;
                foreach (var m in immArrayType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name == "Create" && m.IsGenericMethod && m.GetParameters().Length == 1)
                    {
                        var p = m.GetParameters()[0];
                        if (!p.ParameterType.IsArray)
                        {
                            createMethod = m.MakeGenericMethod(tupleType);
                            break;
                        }
                    }
                }
                if (createMethod != null)
                {
                    var deps = createMethod.Invoke(null, new[] { tuple });
                    viewInfoType.GetProperty("Dependencies")?.SetValue(viewInfo, deps);
                }
                else
                {
                    Log.Default?.Info("[AERO] DetailReg: ImmutableArray.Create method not found");
                }
            }
            else
            {
                Log.Default?.Info("[AERO] DetailReg: ImmutableArray type not found");
            }

            // Ctor delegate: Action<BlockDetailModel, Entity>
            var ctorDelegateType = typeof(Action<,>).MakeGenericType(typeof(BlockDetailModel), typeof(Entity));
            var ctorDelegate = Delegate.CreateDelegate(ctorDelegateType,
                typeof(AeroThrustSettingsComponent).GetMethod(nameof(InvokeDetailCtor),
                    BindingFlags.Public | BindingFlags.Static));
            viewInfoType.GetProperty("Ctor")?.SetValue(viewInfo, ctorDelegate);

            // 4. Add to the dictionary
            // SE2 2.4.0.77 auto-registers BlockDetailModel subclasses found in mod assemblies,
            // so our key is often already present. Add() would throw "An item with the same key
            // has already been added"; use the indexer so registration is idempotent either way.
            var containsKey = backingDict.GetType().GetMethod("ContainsKey");
            if (containsKey != null && containsKey.Invoke(backingDict, new object[] { ourType }) is bool present && present)
            {
                Log.Default?.Info("[AERO] DetailReg: already registered by the game, leaving it alone");
                _registrationDone = true;
                return;
            }
            var setItem = backingDict.GetType().GetMethod("set_Item");
            if (setItem != null) setItem.Invoke(backingDict, new object[] { ourType, viewInfo });
            else backingDict.GetType().GetMethod("Add")?.Invoke(backingDict, new object[] { ourType, viewInfo });

            var countProp = backingDict.GetType().GetProperty("Count");
            int count = countProp != null ? (int)countProp.GetValue(backingDict) : -1;
            Log.Default?.Info($"[AERO] DetailReg: SUCCESS (dict count={count})");
        }
        catch (Exception ex)
        {
            Log.Default?.Info($"[AERO] DetailReg FAILED: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
                Log.Default?.Info($"[AERO] DetailReg inner: {ex.InnerException.Message}");
        }
    }

    public static void InvokeDetailCtor(BlockDetailModel vm, Entity entity)
    {
        vm.GetType().GetConstructor(Type.EmptyTypes)?.Invoke(vm, null);
    }

    /// <summary>
    /// Build a SubclassOf&lt;T&gt; by reflection.
    ///
    /// SE2 2.4.0.77 made SubclassOf&lt;T&gt;'s (Type, bool) constructor PRIVATE, so the old
    /// Activator.CreateInstance(subclassOfType, ourType) throws MissingMethodException and
    /// registration dies with "[AERO] DetailReg FAILED". The supported entry points are now
    /// the static TryCreate(Type, out SubclassOf&lt;T&gt;) and the implicit Type conversion.
    /// Try those first, keeping the old constructor as a fallback for pre-2.4.0 builds.
    /// </summary>
    private static object MakeSubclassOf(Type subclassOfType, Type value)
    {
        // 2.4.0+: public static bool TryCreate(Type, out SubclassOf<T>)
        var tryCreate = subclassOfType.GetMethod("TryCreate", BindingFlags.Public | BindingFlags.Static);
        if (tryCreate != null)
        {
            var args = new object[] { value, null };
            try
            {
                if (tryCreate.Invoke(null, args) is bool ok && ok && args[1] != null)
                    return args[1];
            }
            catch { }
        }

        // public static implicit operator SubclassOf<T>(Type) -- non-nullable overload
        foreach (var m in subclassOfType.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "op_Implicit" || m.ReturnType != subclassOfType) continue;
            var ps = m.GetParameters();
            if (ps.Length != 1 || ps[0].ParameterType != typeof(Type)) continue;
            try { return m.Invoke(null, new object[] { value }); } catch { }
        }

        // Pre-2.4.0: the (Type, bool) / (Type) constructor, public or not.
        foreach (var ctorArgs in new[] { new object[] { value, false }, new object[] { value } })
        {
            try
            {
                return Activator.CreateInstance(subclassOfType,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, ctorArgs, null);
            }
            catch { }
        }
        return null;
    }

}
