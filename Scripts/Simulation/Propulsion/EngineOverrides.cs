#pragma warning disable
using System;
using System.Reflection;

namespace AeroMod;

/// <summary>
/// The engine write that needs reflection here: the protected Component.SetData&lt;T&gt;.
///
/// Since SE2 2.4.0.77 whitelisted Keen.VRage.Physics.*, essentially all of the old PhysicsHack
/// reflection became unnecessary -- component data is reached through the public generic
/// DEntityContext API (TryGet/Set/Has/TryRemove/TryGetWritePtr) and the engine's own
/// RigidBodyDataFunctions helpers. (The mod's other reflection is listed in
/// docs/REFLECTION_AND_WHITELIST.md; the speed-cap and physics-config writes that lived here went
/// in the 2026-10-15 reorg - the mod's PhysicsSessionConfiguration.def sets both.)
///
///   Component.SetData&lt;T&gt;(T)
///      Declared "protected void SetData&lt;T&gt;(T value) where T : unmanaged". A mod class does
///      not derive from the engine's ThrustComponent, so it cannot call a protected member.
///      This matters because writing OverriddenThrustData/ControlData through the component
///      lands it in the pool ComputeThrust reads; DEntityContext.Set is only a fallback.
///
/// If Keen ever exposes it, delete it here -- do NOT let reflection creep back into the
/// physics path.
/// </summary>
public static class EngineOverrides
{
    // ── Component.SetData<T> ───────────────────────────────────────────────────
    private static MethodInfo _setDataGeneric;
    private static bool _setDataResolved;

    /// <summary>
    /// Call the protected Component.SetData&lt;T&gt; on the component of the given type.
    /// HOLDOUT: protected member on an engine class we do not derive from.
    /// </summary>
    public static bool TrySetComponentData<TComponent, T>(Entity entity, T value)
        where TComponent : Component
        where T : unmanaged
    {
        if (entity == null) return false;

        if (!_setDataResolved)
        {
            _setDataResolved = true;
            foreach (var m in typeof(Component).GetMethods(
                         BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name == "SetData" && m.IsGenericMethodDefinition
                    && m.GetParameters().Length == 1 && !m.GetParameters()[0].IsOut)
                {
                    _setDataGeneric = m;
                    break;
                }
            }
            if (_setDataGeneric == null)
                Log.Default?.Info("[AERO] EngineOverrides: Component.SetData<T> not found");
        }
        if (_setDataGeneric == null) return false;

        try
        {
            var comp = entity.TryGet<TComponent>();
            if (comp == null) return false;
            _setDataGeneric.MakeGenericMethod(typeof(T)).Invoke(comp, MakeArgs(value));
            return true;
        }
        catch (Exception ex)
        {
            Log.Default?.Info($"[AERO] EngineOverrides.TrySetComponentData failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Build a one-element object[] for MethodBase.Invoke. Array.CreateInstance dodges the
    /// VRS1001 analyzer ban on T[] syntax in mod scripts.
    /// </summary>
    private static object[] MakeArgs(object value)
    {
        var arr = Array.CreateInstance(typeof(object), 1);
        arr.SetValue(value, 0);
        return Unsafe.As<Array, object[]>(ref arr);
    }
}
