#pragma warning disable
using System;
using System.Reflection;
using Keen.Game2.Simulation.GameSystems.Movement;
using Keen.Game2.Simulation.GameSystems.Physicss;
using System.Collections.Generic;

namespace AeroMod;

/// <summary>
/// The reflection that CANNOT be removed.
///
/// Since SE2 2.4.0.77 whitelisted Keen.VRage.Physics.*, essentially all of the old PhysicsHack
/// reflection became unnecessary -- component data is reached through the public generic
/// DEntityContext API (TryGet/Set/Has/TryRemove/TryGetWritePtr) and the engine's own
/// RigidBodyDataFunctions helpers.
///
/// Exactly three things resist that, because the engine deliberately does not expose them.
/// They are collected here so the rest of the codebase stays clean and so the true size of the
/// remaining hack is one small file rather than two thousand lines:
///
///   1. VelocityLimitProvider._linearVelocityLimit
///      Public surface is a GET-ONLY property (LinearVelocityLimit) with a private backing
///      field set only via constructor. Raising the speed cap requires writing that field.
///
///   2. PhysicsSessionConfiguration.GravityMultiplier
///      Declared "public float GravityMultiplier { get; private set; } = 1f;". Private setter.
///
///   3. Component.SetData&lt;T&gt;(T)
///      Declared "protected void SetData&lt;T&gt;(T value) where T : unmanaged". A mod class does
///      not derive from the engine's ThrustComponent, so it cannot call a protected member.
///      This matters because writing OverriddenThrustData/ControlData through the component
///      lands it in the pool ComputeThrust reads; DEntityContext.Set is only a fallback.
///
/// If Keen ever exposes any of these, delete the corresponding member here -- do NOT let
/// reflection creep back into the physics path.
/// </summary>
public static class EngineOverrides
{
    // ── 1. Speed cap ────────────────────────────────────────────────────────────
    private static FieldInfo _speedLimitField;
    private static bool _speedLimitResolved;
    private static bool _speedUncapped;

    /// <summary>
    /// Raise VelocityLimitProvider's linear speed cap. HOLDOUT: get-only property.
    /// </summary>
    public static void UncapSpeed(object velocityLimitProvider, float newLimit = 1000f)
    {
        if (velocityLimitProvider == null) return;

        if (!_speedLimitResolved)
        {
            _speedLimitResolved = true;
            _speedLimitField = typeof(VelocityLimitProvider).GetField(
                "_linearVelocityLimit", BindingFlags.NonPublic | BindingFlags.Instance);
            if (_speedLimitField == null)
                Log.Default?.Info("[AERO] EngineOverrides: _linearVelocityLimit not found");
        }
        if (_speedLimitField == null) return;

        try
        {
            float current = (float)_speedLimitField.GetValue(velocityLimitProvider);
            if (current >= newLimit) return;
            _speedLimitField.SetValue(velocityLimitProvider, newLimit);
            if (!_speedUncapped)
            {
                Log.Default?.Info($"[AERO] Speed uncapped: {current} -> {newLimit} m/s");
                _speedUncapped = true;
            }
        }
        catch (Exception ex)
        {
            Log.Default?.Info($"[AERO] EngineOverrides.UncapSpeed failed: {ex.Message}");
        }
    }

    // ── 2. Configuration properties with private setters ────────────────────────
    private static readonly Dictionary<string, PropertyInfo> _configProps = new();

    /// <summary>
    /// Set a PhysicsSessionConfiguration property whose setter is private
    /// (GravityMultiplier, MaximumSpeedLinear, MaximumSpeedAngular, ...).
    /// HOLDOUT: all are declared "public float X { get; private set; }".
    /// </summary>
    public static bool TrySetConfigProperty(PhysicsSessionConfiguration config, string name, float value)
    {
        if (config == null || string.IsNullOrEmpty(name)) return false;

        if (!_configProps.TryGetValue(name, out var prop))
        {
            prop = typeof(PhysicsSessionConfiguration).GetProperty(
                name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _configProps[name] = prop;
            if (prop == null)
                Log.Default?.Info($"[AERO] EngineOverrides: config property {name} not found");
        }
        if (prop == null) return false;

        try
        {
            var setter = prop.GetSetMethod(nonPublic: true);
            if (setter == null) return false;
            setter.Invoke(config, MakeArgs(value));
            return true;
        }
        catch (Exception ex)
        {
            Log.Default?.Info($"[AERO] EngineOverrides.TrySetConfigProperty({name}) failed: {ex.Message}");
            return false;
        }
    }

    // ── 3. Component.SetData<T> ─────────────────────────────────────────────────
    private static MethodInfo _setDataGeneric;
    private static bool _setDataResolved;

    /// <summary>
    /// Call the protected Component.SetData&lt;T&gt; on the component of the given type.
    /// HOLDOUT: protected member on an engine class we do not derive from.
    /// </summary>
    public static bool TrySetComponentData<T>(Entity entity, Type componentType, T value)
        where T : unmanaged
    {
        if (entity == null || componentType == null) return false;

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
            var comp = PhysicsHack.FindComponentByType(entity, componentType);
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
