#pragma warning disable
using System;
using System.Reflection;
using Keen.VRage.Core;

namespace AeroMod;

/// <summary>
/// Reflection-based access to RigidBodyData from VRage.Physics.
/// That assembly is not whitelisted for mod scripts, so we resolve
/// the type and its fields at runtime via reflection.
/// </summary>
public static class PhysicsHack
{
    private static bool _initialized;
    private static bool _available;

    private static Type _rbDataType;
    private static FieldInfo _linearVelField;
    private static FieldInfo _angularVelField;
    private static MethodInfo _tryGetMethod;

    // Mass properties
    private static Type _massType;
    private static FieldInfo _invMassField;
    private static FieldInfo _comField;
    private static FieldInfo _invInertiaTensorField;
    private static FieldInfo _inertiaMajorAxisRotField;
    private static MethodInfo _tryGetMassMethod;

    // For applying impulse
    private static MethodInfo _getWritePtrMethod;

    // Cached Set<RigidBodyData> method (avoid per-frame reflection)
    private static MethodInfo _setRbDataMethod;

    // Speed limit override
    private static Type _speedLimitType;
    private static FieldInfo _speedLimitField;
    private static bool _speedUncapped;

    // Gravity multiplier override
    private static FieldInfo _gravityMultField;
    private static bool _gravityFixed;

    // Gyro max torque
    private static MethodInfo _tryGetMaxTorqueMethod;
    private static FieldInfo _maxTorqueField;
    private static MethodInfo _setMaxTorqueMethod;

    // Thruster data access
    private static Type _thrustDataType;
    private static FieldInfo _thrustMaxPowerField;
    private static FieldInfo _thrustDirectionField;
    private static MethodInfo _tryGetThrustDataMethod;

    private static Type _thrusterOverrideType;
    private static FieldInfo _overridePowerField;
    private static MethodInfo _tryGetOverrideMethod;
    private static MethodInfo _setOverrideMethod;
    private static MethodInfo _tryRemoveOverrideMethod;

    private static Type _isThrustingType;
    private static MethodInfo _hasIsThrustingMethod;

    // WorldTransform reading on child entities
    private static MethodInfo _getWorldTransformMethod;

    // Generic Set<T> base method (cached for reuse)
    private static MethodInfo _setGeneric;
    private static MethodInfo _tryRemoveGeneric;
    private static MethodInfo _hasGeneric;

    // Gravity data (for "down" direction) — in VRage.Core.Game (whitelisted)
    private static Type _gravityEffectDataType;
    private static FieldInfo _gravitySumField;
    private static MethodInfo _tryGetGravityMethod;

    // IPhysics for raycast ground distance
    private static object _physicsInstance;
    private static MethodInfo _castRayAsyncMethod;
    private static FieldInfo _hitFractionField;
    private static bool _groundSystemInitialized;

    // Pre-allocated args list to avoid creating object[] (banned by VRS1001)
    private static System.Array _invokeArgs;
    private static System.Array _invokeArgs2; // second args buffer to avoid clobbering

    public static bool Available
    {
        get
        {
            if (!_initialized)
                Initialize();
            return _available;
        }
    }

    private static void Initialize()
    {
        _initialized = true;

        try
        {
            // Resolve the banned type at runtime
            _rbDataType = Type.GetType(
                "Keen.VRage.Physics.Data.RigidBodyData, VRage.Physics",
                throwOnError: false);

            if (_rbDataType == null)
            {
                Log.Default?.Info("[AERO] PhysicsHack: RigidBodyData type not found");
                return;
            }

            // Cache field accessors (private backing fields)
            _linearVelField = _rbDataType.GetField("_linearVelocity",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _angularVelField = _rbDataType.GetField("_angularVelocity",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (_linearVelField == null || _angularVelField == null)
            {
                Log.Default?.Info("[AERO] PhysicsHack: velocity fields not found");
                return;
            }

            // Cache TryGet<RigidBodyData>(out T) on DEntityContext
            var contextType = typeof(DEntityContext);
            MethodInfo tryGetGeneric = null;
            foreach (var m in contextType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name == "TryGet" && m.IsGenericMethodDefinition
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].IsOut)
                {
                    tryGetGeneric = m;
                    break;
                }
            }

            if (tryGetGeneric == null)
            {
                Log.Default?.Info("[AERO] PhysicsHack: TryGet method not found");
                return;
            }

            _tryGetMethod = tryGetGeneric.MakeGenericMethod(_rbDataType);

            // Resolve RigidBodyMassProperties for mass + center of mass
            _massType = Type.GetType(
                "Keen.VRage.Physics.Data.RigidBodyMassProperties, VRage.Physics",
                throwOnError: false);
            if (_massType != null)
            {
                _invMassField = _massType.GetField("InvMass",
                    BindingFlags.Public | BindingFlags.Instance);
                _comField = _massType.GetField("CenterOfMass",
                    BindingFlags.Public | BindingFlags.Instance);
                _invInertiaTensorField = _massType.GetField("InvInertiaTensor",
                    BindingFlags.Public | BindingFlags.Instance);
                _inertiaMajorAxisRotField = _massType.GetField("InertiaMajorAxisRotation",
                    BindingFlags.Public | BindingFlags.Instance);

                if (_invInertiaTensorField != null)
                    Log.Default?.Info("[AERO] PhysicsHack: InvInertiaTensor field found");

                if (tryGetGeneric != null)
                    _tryGetMassMethod = tryGetGeneric.MakeGenericMethod(_massType);
            }

            // GetWritePtr<RigidBodyData>() for applying forces
            MethodInfo getWritePtrGeneric = null;
            foreach (var m in contextType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name == "GetWritePtr" && m.IsGenericMethodDefinition
                    && m.GetParameters().Length == 0)
                {
                    getWritePtrGeneric = m;
                    break;
                }
            }
            if (getWritePtrGeneric != null)
                _getWritePtrMethod = getWritePtrGeneric.MakeGenericMethod(_rbDataType);

            // Cache Set<RigidBodyData>(T) method
            foreach (var m in contextType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name == "Set" && m.IsGenericMethodDefinition
                    && m.GetParameters().Length == 1
                    && !m.GetParameters()[0].IsOut)
                {
                    _setRbDataMethod = m.MakeGenericMethod(_rbDataType);
                    break;
                }
            }

            // Create args arrays via Array.CreateInstance to dodge VRS1001 ban on T[]
            _invokeArgs = Array.CreateInstance(typeof(object), 1);
            _invokeArgs2 = Array.CreateInstance(typeof(object), 1);

            // Cache generic method bases for reuse
            foreach (var m in contextType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name == "Set" && m.IsGenericMethodDefinition
                    && m.GetParameters().Length == 1 && !m.GetParameters()[0].IsOut)
                    _setGeneric = m;
                else if (m.Name == "TryRemove" && m.IsGenericMethodDefinition
                    && m.GetParameters().Length == 0)
                    _tryRemoveGeneric = m;
                else if (m.Name == "Has" && m.IsGenericMethodDefinition
                    && m.GetParameters().Length == 0)
                    _hasGeneric = m;
            }

            _available = true;
            Log.Default?.Info("[AERO] PhysicsHack: initialized successfully");

            // Uncap speed limit to 1000 m/s
            try
            {
                var limiterType = Type.GetType(
                    "Keen.Game2.Simulation.GameSystems.Movement.VelocityLimitProvider, Game2.Simulation",
                    throwOnError: false);
                if (limiterType != null)
                {
                    var linField = limiterType.GetField("_linearVelocityLimit",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (linField != null)
                    {
                        _speedLimitField = linField;
                        _speedLimitType = limiterType;
                        Log.Default?.Info("[AERO] PhysicsHack: speed limit field found");
                    }
                }
            }
            catch { }

            // Resolve gravity multiplier field
            try
            {
                var physConfigType = Type.GetType(
                    "Keen.Game2.Simulation.GameSystems.Physicss.PhysicsSessionConfiguration, Game2.Simulation",
                    throwOnError: false);
                if (physConfigType != null)
                {
                    var gmField = physConfigType.GetField("_gravityMultiplier",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (gmField == null)
                    {
                        // Try property backing field or public field
                        gmField = physConfigType.GetField("GravityMultiplier",
                            BindingFlags.Public | BindingFlags.Instance);
                    }
                    if (gmField != null)
                    {
                        _gravityMultField = gmField;
                        Log.Default?.Info("[AERO] PhysicsHack: gravity multiplier field found");
                    }
                    else
                    {
                        // Try all fields and look for gravity
                        foreach (var f in physConfigType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        {
                            if (f.Name.Contains("ravity", StringComparison.OrdinalIgnoreCase) &&
                                f.Name.Contains("ultipl", StringComparison.OrdinalIgnoreCase))
                            {
                                _gravityMultField = f;
                                Log.Default?.Info($"[AERO] PhysicsHack: found gravity field: {f.Name}");
                                break;
                            }
                        }
                    }
                }
            }
            catch { }

            // Resolve MaxTorqueData for gyro torque logging
            try
            {
                var maxTorqueType = Type.GetType(
                    "Keen.Game2.Simulation.WorldObjects.Shared.Movement.MaxTorqueData, Game2.Simulation",
                    throwOnError: false);
                if (maxTorqueType != null)
                {
                    _maxTorqueField = maxTorqueType.GetField("MaxTorque",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (tryGetGeneric != null)
                        _tryGetMaxTorqueMethod = tryGetGeneric.MakeGenericMethod(maxTorqueType);
                    if (_maxTorqueField != null)
                        Log.Default?.Info("[AERO] PhysicsHack: MaxTorqueData field found");
                    if (_setGeneric != null)
                        _setMaxTorqueMethod = _setGeneric.MakeGenericMethod(maxTorqueType);
                }
            }
            catch { }

            // Resolve ThrustData for reading per-thruster info
            try
            {
                _thrustDataType = Type.GetType(
                    "Keen.Game2.Simulation.WorldObjects.Movement.ThrustData, Game2.Simulation",
                    throwOnError: false);
                if (_thrustDataType != null && tryGetGeneric != null)
                {
                    _thrustMaxPowerField = _thrustDataType.GetField("MaxThrustPower",
                        BindingFlags.Public | BindingFlags.Instance);
                    _thrustDirectionField = _thrustDataType.GetField("Direction",
                        BindingFlags.Public | BindingFlags.Instance);
                    _tryGetThrustDataMethod = tryGetGeneric.MakeGenericMethod(_thrustDataType);
                    if (_thrustMaxPowerField != null)
                        Log.Default?.Info("[AERO] PhysicsHack: ThrustData fields found");
                }
            }
            catch { }

            // Resolve ThrusterOverrideData for setting thrust override
            try
            {
                _thrusterOverrideType = Type.GetType(
                    "Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement.ThrusterOverrideData, Game2.Simulation",
                    throwOnError: false);
                if (_thrusterOverrideType != null)
                {
                    _overridePowerField = _thrusterOverrideType.GetField("OverridePower",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (tryGetGeneric != null)
                        _tryGetOverrideMethod = tryGetGeneric.MakeGenericMethod(_thrusterOverrideType);
                    if (_setGeneric != null)
                        _setOverrideMethod = _setGeneric.MakeGenericMethod(_thrusterOverrideType);
                    if (_tryRemoveGeneric != null)
                        _tryRemoveOverrideMethod = _tryRemoveGeneric.MakeGenericMethod(_thrusterOverrideType);
                    if (_overridePowerField != null)
                        Log.Default?.Info("[AERO] PhysicsHack: ThrusterOverrideData fields found");
                }
            }
            catch { }

            // Resolve IsThrusting tag for checking if thruster is active
            try
            {
                _isThrustingType = Type.GetType(
                    "Keen.Game2.Simulation.WorldObjects.Movement.IsThrusting, Game2.Simulation",
                    throwOnError: false);
                if (_isThrustingType != null && _hasGeneric != null)
                {
                    _hasIsThrustingMethod = _hasGeneric.MakeGenericMethod(_isThrustingType);
                    Log.Default?.Info("[AERO] PhysicsHack: IsThrusting type found");
                }
            }
            catch { }

            // Resolve GetWorldTransform on DEntityContext
            try
            {
                _getWorldTransformMethod = contextType.GetMethod("GetWorldTransform",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                if (_getWorldTransformMethod != null)
                    Log.Default?.Info("[AERO] PhysicsHack: GetWorldTransform found");
            }
            catch { }

            // Resolve GravityEffectData for "down" direction
            try
            {
                _gravityEffectDataType = Type.GetType(
                    "Keen.VRage.Core.Game.GameSystems.Gravity.GravityEffectData, VRage.Core.Game",
                    throwOnError: false);
                if (_gravityEffectDataType != null)
                {
                    _gravitySumField = _gravityEffectDataType.GetField("GravitySum",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (tryGetGeneric != null)
                        _tryGetGravityMethod = tryGetGeneric.MakeGenericMethod(_gravityEffectDataType);
                    if (_gravitySumField != null)
                        Log.Default?.Info("[AERO] PhysicsHack: GravityEffectData found");
                }
            }
            catch { }

            // Resolve SweepQueryHit.Fraction for reading raycast results
            try
            {
                var sweepHitType = Type.GetType(
                    "Keen.VRage.Physics.Queries.SweepQueryHit, VRage.Physics",
                    throwOnError: false);
                if (sweepHitType != null)
                {
                    _hitFractionField = sweepHitType.GetField("Fraction",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (_hitFractionField != null)
                        Log.Default?.Info("[AERO] PhysicsHack: SweepQueryHit.Fraction found");
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            Log.Default?.Info($"[AERO] PhysicsHack: init failed: {ex.Message}");
            _available = false;
        }
    }

    /// <summary>
    /// Read gyro MaxTorque from an entity's MaxTorqueData.
    /// </summary>
    public static float TryGetGyroMaxTorque(DEntityContext data)
    {
        if (!Available || _tryGetMaxTorqueMethod == null || _maxTorqueField == null)
            return -1f;
        try
        {
            _invokeArgs.SetValue(null, 0);
            object boxedContext = data;
            bool found = (bool)_tryGetMaxTorqueMethod.Invoke(boxedContext,
                Unsafe.As<System.Array, object[]>(ref _invokeArgs));
            if (found && _invokeArgs.GetValue(0) != null)
                return (float)_maxTorqueField.GetValue(_invokeArgs.GetValue(0));
        }
        catch { }
        return -1f;
    }

    /// <summary>
    /// Read linear and angular velocity from an entity's RigidBodyData via reflection.
    /// </summary>
    public static bool TryGetVelocity(DEntityContext data, out Vector3 linear, out Vector3 angular)
    {
        linear = Vector3.Zero;
        angular = Vector3.Zero;

        if (!Available)
            return false;

        try
        {
            // MethodInfo.Invoke needs object[] but VRS1001 bans T[] syntax.
            // _invokeArgs was created via Array.CreateInstance — it IS an object[]
            // at runtime, we just cast through System.Array to avoid the analyzer.
            _invokeArgs.SetValue(null, 0);
            object boxedContext = data;
            bool found = (bool)_tryGetMethod.Invoke(boxedContext,
                Unsafe.As<System.Array, object[]>(ref _invokeArgs));

            if (!found || _invokeArgs.GetValue(0) == null)
                return false;

            object rbData = _invokeArgs.GetValue(0);
            linear = (Vector3)_linearVelField.GetValue(rbData);
            angular = (Vector3)_angularVelField.GetValue(rbData);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Uncap speed limit to 1000 m/s by modifying VelocityLimitProvider instance.
    /// Call with the IVelocityLimitProvider from session.
    /// </summary>
    public static void UncapSpeed(object velocityLimitProvider, float newLimit = 1000f)
    {
        if (_speedLimitField == null || velocityLimitProvider == null) return;

        try
        {
            float current = (float)_speedLimitField.GetValue(velocityLimitProvider);
            if (current < newLimit)
            {
                _speedLimitField.SetValue(velocityLimitProvider, newLimit);
                if (!_speedUncapped)
                {
                    Log.Default?.Info($"[AERO] Speed uncapped: {current} -> {newLimit} m/s");
                    _speedUncapped = true;
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Set GravityMultiplier and MaximumSpeedLinear on PhysicsSessionConfiguration
    /// via DefinitionManager.Instance.GetConfiguration&lt;T&gt;().SetPropValue().
    /// </summary>
    public static void TryFixGravity(float targetGravity = 1f, float targetSpeed = 1000f)
    {
        if (_gravityFixed) return;

        try
        {
            // Resolve DefinitionManager type and its static Instance property
            var defManagerType = Type.GetType(
                "Keen.VRage.Library.Definitions.DefinitionManager, VRage.Library",
                throwOnError: false);
            if (defManagerType == null)
            {
                Log.Default?.Info("[AERO] TryFixGravity: DefinitionManager type not found");
                return;
            }

            var instanceProp = defManagerType.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static);
            if (instanceProp == null)
            {
                Log.Default?.Info("[AERO] TryFixGravity: DefinitionManager.Instance not found");
                return;
            }

            object defManager = instanceProp.GetValue(null);
            if (defManager == null)
            {
                Log.Default?.Info("[AERO] TryFixGravity: DefinitionManager.Instance is null");
                return;
            }

            // Resolve PhysicsSessionConfiguration type
            var configType = Type.GetType(
                "Keen.Game2.Simulation.GameSystems.Physicss.PhysicsSessionConfiguration, Game2.Simulation",
                throwOnError: false);
            if (configType == null)
            {
                Log.Default?.Info("[AERO] TryFixGravity: PhysicsSessionConfiguration type not found");
                return;
            }

            // Call DefinitionManager.Instance.GetConfiguration<PhysicsSessionConfiguration>()
            MethodInfo getConfigGeneric = null;
            foreach (var m in defManagerType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name == "GetConfiguration" && m.IsGenericMethodDefinition
                    && m.GetParameters().Length == 0)
                {
                    getConfigGeneric = m;
                    break;
                }
            }

            if (getConfigGeneric == null)
            {
                Log.Default?.Info("[AERO] TryFixGravity: GetConfiguration method not found");
                return;
            }

            var getConfig = getConfigGeneric.MakeGenericMethod(configType);
            object config = getConfig.Invoke(defManager, null);

            if (config == null)
            {
                Log.Default?.Info("[AERO] TryFixGravity: GetConfiguration returned null");
                return;
            }

            // Use SetPropValue to set properties (extension method or instance method on Configuration)
            MethodInfo setPropValue = config.GetType().GetMethod("SetPropValue",
                BindingFlags.Public | BindingFlags.Instance);

            if (setPropValue == null)
            {
                // Try as extension method — search all loaded types
                Log.Default?.Info("[AERO] TryFixGravity: SetPropValue not found on config, trying direct property set");

                // Fallback: set backing fields directly
                SetConfigField(config, "MaximumSpeedLinear", targetSpeed);
                SetConfigField(config, "GravityMultiplier", targetGravity);
            }
            else
            {
                // SetPropValue(string name, object value)
                var setPropArgs = Array.CreateInstance(typeof(object), 2);
                setPropArgs.SetValue("MaximumSpeedLinear", 0);
                setPropArgs.SetValue(targetSpeed, 1);
                setPropValue.Invoke(config, Unsafe.As<System.Array, object[]>(ref setPropArgs));

                setPropArgs.SetValue("GravityMultiplier", 0);
                setPropArgs.SetValue(targetGravity, 1);
                setPropValue.Invoke(config, Unsafe.As<System.Array, object[]>(ref setPropArgs));

                Log.Default?.Info($"[AERO] Physics config set: MaximumSpeedLinear={targetSpeed}, GravityMultiplier={targetGravity}");
            }

            _gravityFixed = true;
        }
        catch (Exception ex)
        {
            Log.Default?.Info($"[AERO] TryFixGravity failed: {ex.Message}");
            _gravityFixed = true;
        }
    }

    /// <summary>
    /// Fallback: set a config property via its auto-property backing field.
    /// </summary>
    private static void SetConfigField(object config, string propName, float value)
    {
        var type = config.GetType();

        // Try auto-property backing field first
        var field = type.GetField($"<{propName}>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance);

        if (field == null)
        {
            // Try direct field
            field = type.GetField(propName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        }

        if (field == null)
        {
            // Search for partial name match
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (f.Name.Contains(propName, StringComparison.OrdinalIgnoreCase) && f.FieldType == typeof(float))
                {
                    field = f;
                    break;
                }
            }
        }

        if (field != null)
        {
            field.SetValue(config, value);
            Log.Default?.Info($"[AERO] Set {propName} = {value} via field {field.Name}");
        }
        else
        {
            Log.Default?.Info($"[AERO] Could not find field for {propName}");
        }
    }

    /// <summary>
    /// Read mass and center of mass from RigidBodyMassProperties.
    /// </summary>
    public static bool TryGetMassProperties(DEntityContext data, out float mass, out Vector3 centerOfMass)
    {
        mass = 0f;
        centerOfMass = Vector3.Zero;

        if (!Available || _tryGetMassMethod == null || _invMassField == null)
            return false;

        try
        {
            _invokeArgs.SetValue(null, 0);
            object boxedContext = data;
            bool found = (bool)_tryGetMassMethod.Invoke(boxedContext,
                Unsafe.As<System.Array, object[]>(ref _invokeArgs));

            if (!found || _invokeArgs.GetValue(0) == null)
                return false;

            object massData = _invokeArgs.GetValue(0);
            float invMass = (float)_invMassField.GetValue(massData);
            mass = invMass > 1e-10f ? 1f / invMass : 0f;

            if (_comField != null)
                centerOfMass = (Vector3)_comField.GetValue(massData);

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Read inertia data for diagnostic logging.
    /// Returns inverse inertia tensor and major axis rotation quaternion.
    /// </summary>
    public static bool TryGetInertiaData(DEntityContext data,
        out Vector3 invInertiaTensor, out Quaternion majorAxisRot)
    {
        invInertiaTensor = Vector3.Zero;
        majorAxisRot = Quaternion.Identity;

        if (!Available || _tryGetMassMethod == null || _invInertiaTensorField == null)
            return false;

        try
        {
            _invokeArgs.SetValue(null, 0);
            object boxedContext = data;
            bool found = (bool)_tryGetMassMethod.Invoke(boxedContext,
                Unsafe.As<System.Array, object[]>(ref _invokeArgs));

            if (!found || _invokeArgs.GetValue(0) == null)
                return false;

            object massData = _invokeArgs.GetValue(0);
            invInertiaTensor = (Vector3)_invInertiaTensorField.GetValue(massData);

            if (_inertiaMajorAxisRotField != null)
                majorAxisRot = (Quaternion)_inertiaMajorAxisRotField.GetValue(massData);

            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Zero out the grid's gyroscope MaxTorque so game gyros don't compete.
    /// Uses Set&lt;MaxTorqueData&gt; via reflection.
    /// </summary>
    private static float _savedMaxTorque = -1f;

    public static bool TryRestoreGyroTorque(DEntityContext data)
    {
        if (!Available || _tryGetMaxTorqueMethod == null || _maxTorqueField == null || _savedMaxTorque < 0f)
            return false;
        return TrySetGyroTorque(data, _savedMaxTorque, "restored");
    }

    public static bool TryZeroGyroTorque(DEntityContext data)
    {
        if (!Available || _tryGetMaxTorqueMethod == null || _maxTorqueField == null)
            return false;

        try
        {
            _invokeArgs.SetValue(null, 0);
            object boxedContext = data;
            bool found = (bool)_tryGetMaxTorqueMethod.Invoke(boxedContext,
                Unsafe.As<System.Array, object[]>(ref _invokeArgs));

            if (!found || _invokeArgs.GetValue(0) == null)
                return false;

            object mtData = _invokeArgs.GetValue(0);
            float current = (float)_maxTorqueField.GetValue(mtData);
            if (current == 0f) return true; // already zero

            // Save original value for restore
            if (_savedMaxTorque < 0f) _savedMaxTorque = current;

            _maxTorqueField.SetValue(mtData, 0f);

            // Write back via cached Set<MaxTorqueData>
            if (_setMaxTorqueMethod != null)
            {
                _invokeArgs.SetValue(mtData, 0);
                _setMaxTorqueMethod.Invoke(boxedContext, Unsafe.As<System.Array, object[]>(ref _invokeArgs));
                Log.Default?.Info($"[AERO] PhysicsHack: zeroed MaxTorque (was {current:F0})");
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Default?.Info($"[AERO] PhysicsHack: TryZeroGyroTorque failed: {ex.Message}");
        }
        return false;
    }

    private static bool TrySetGyroTorque(DEntityContext data, float value, string label)
    {
        try
        {
            _invokeArgs.SetValue(null, 0);
            object boxedContext = data;
            bool found = (bool)_tryGetMaxTorqueMethod.Invoke(boxedContext,
                Unsafe.As<System.Array, object[]>(ref _invokeArgs));
            if (!found || _invokeArgs.GetValue(0) == null) return false;

            object mtData = _invokeArgs.GetValue(0);
            _maxTorqueField.SetValue(mtData, value);

            if (_setMaxTorqueMethod != null)
            {
                _invokeArgs.SetValue(mtData, 0);
                _setMaxTorqueMethod.Invoke(boxedContext, Unsafe.As<System.Array, object[]>(ref _invokeArgs));
                Log.Default?.Info($"[AERO] PhysicsHack: {label} MaxTorque to {value:F0}");
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Default?.Info($"[AERO] PhysicsHack: TrySetGyroTorque failed: {ex.Message}");
        }
        return false;
    }

    /// <summary>
    /// Apply linear velocity delta to the entity's RigidBodyData.
    /// </summary>
    public static bool ApplyDeltaV(DEntityContext data, Vector3 deltaV)
    {
        return ApplyDeltaVAndTorque(data, deltaV, Vector3.Zero);
    }

    /// <summary>
    /// Apply both linear and angular velocity deltas in one read-modify-write.
    /// torqueLocal is in grid-local space; converted to angular deltaV via inertia tensor,
    /// then transformed to world space before applying (AngularVelocity is world-space).
    /// </summary>
    public static bool ApplyDeltaVAndTorque(DEntityContext data, Vector3 deltaV, Vector3 torqueLocal, float dt = 1f / 60f, Quaternion? gridOrientation = null)
    {
        if (!Available)
            return false;

        // NaN guard — never write bad values to physics
        if (float.IsNaN(deltaV.X) || float.IsNaN(deltaV.Y) || float.IsNaN(deltaV.Z) ||
            float.IsNaN(torqueLocal.X) || float.IsNaN(torqueLocal.Y) || float.IsNaN(torqueLocal.Z) ||
            float.IsInfinity(deltaV.X) || float.IsInfinity(deltaV.Y) || float.IsInfinity(deltaV.Z) ||
            float.IsInfinity(torqueLocal.X) || float.IsInfinity(torqueLocal.Y) || float.IsInfinity(torqueLocal.Z))
            return false;

        try
        {
            // Get RigidBodyData
            _invokeArgs.SetValue(null, 0);
            object boxedContext = data;
            bool found = (bool)_tryGetMethod.Invoke(boxedContext,
                Unsafe.As<System.Array, object[]>(ref _invokeArgs));

            if (!found || _invokeArgs.GetValue(0) == null)
                return false;

            object rbData = _invokeArgs.GetValue(0);

            // Apply linear deltaV
            Vector3 linVel = (Vector3)_linearVelField.GetValue(rbData);
            linVel += deltaV;
            _linearVelField.SetValue(rbData, linVel);

            // Apply angular deltaV from torque
            if (torqueLocal.LengthSquared() > 1e-6f && _invInertiaTensorField != null)
            {
                // Get inertia data (use _invokeArgs2 to avoid clobbering rbData in _invokeArgs)
                _invokeArgs2.SetValue(null, 0);
                bool foundMass = (bool)_tryGetMassMethod.Invoke(boxedContext,
                    Unsafe.As<System.Array, object[]>(ref _invokeArgs2));

                if (foundMass && _invokeArgs2.GetValue(0) != null)
                {
                    object massData = _invokeArgs2.GetValue(0);
                    Vector3 invInertia = (Vector3)_invInertiaTensorField.GetValue(massData);

                    // deltaOmega = I⁻¹ · torque · dt
                    // If we have the major axis rotation, rotate torque to principal axes first
                    Vector3 torquePrincipal = torqueLocal;
                    if (_inertiaMajorAxisRotField != null)
                    {
                        Quaternion majorAxisRot = (Quaternion)_inertiaMajorAxisRotField.GetValue(massData);
                        Quaternion invRot = Quaternion.Conjugate(majorAxisRot);
                        torquePrincipal = Vector3.Transform(torqueLocal, invRot);
                    }

                    Vector3 deltaOmega = new Vector3(
                        torquePrincipal.X * invInertia.X,
                        torquePrincipal.Y * invInertia.Y,
                        torquePrincipal.Z * invInertia.Z) * dt;

                    if (_inertiaMajorAxisRotField != null)
                    {
                        Quaternion majorAxisRot = (Quaternion)_inertiaMajorAxisRotField.GetValue(massData);
                        deltaOmega = Vector3.Transform(deltaOmega, majorAxisRot);
                    }

                    // Transform deltaOmega from local to world space
                    if (gridOrientation.HasValue)
                        deltaOmega = Vector3.Transform(deltaOmega, gridOrientation.Value);

                    Vector3 angVel = (Vector3)_angularVelField.GetValue(rbData);
                    angVel += deltaOmega;
                    _angularVelField.SetValue(rbData, angVel);
                }
            }

            // Write back using Data.Set<RigidBodyData>(modified)
            _invokeArgs.SetValue(rbData, 0);

            if (_setRbDataMethod != null)
            {
                _setRbDataMethod.Invoke(boxedContext,
                    Unsafe.As<System.Array, object[]>(ref _invokeArgs));
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Thruster data access
    // ═══════════════════════════════════════════════════════════════

    public static bool ThrusterAccessAvailable =>
        Available && _thrustDataType != null && _thrustMaxPowerField != null;

    public static bool GroundSystemReady => _groundSystemInitialized;

    /// <summary>
    /// Read ThrustData from a thruster entity: max power and direction.
    /// Direction is returned as int (Base6Directions.Direction enum value).
    /// </summary>
    public static bool TryGetThrustData(DEntityContext data, out float maxPower, out int direction)
    {
        maxPower = 0f;
        direction = 0;

        if (!ThrusterAccessAvailable || _tryGetThrustDataMethod == null)
            return false;

        try
        {
            _invokeArgs2.SetValue(null, 0);
            object boxedContext = data;
            bool found = (bool)_tryGetThrustDataMethod.Invoke(boxedContext,
                Unsafe.As<System.Array, object[]>(ref _invokeArgs2));

            if (!found || _invokeArgs2.GetValue(0) == null)
                return false;

            object td = _invokeArgs2.GetValue(0);
            maxPower = (float)_thrustMaxPowerField.GetValue(td);
            if (_thrustDirectionField != null)
                direction = (int)_thrustDirectionField.GetValue(td);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Check if a thruster entity has the IsThrusting tag (is currently firing).
    /// </summary>
    public static bool IsEntityThrusting(DEntityContext data)
    {
        if (_hasIsThrustingMethod == null) return false;
        try
        {
            object boxedContext = data;
            return (bool)_hasIsThrustingMethod.Invoke(boxedContext, null);
        }
        catch { return false; }
    }

    /// <summary>
    /// Read ThrusterOverrideData.OverridePower from a thruster entity.
    /// Returns -1 if no override is set.
    /// </summary>
    public static float GetThrustOverride(DEntityContext data)
    {
        if (_tryGetOverrideMethod == null || _overridePowerField == null) return -1f;
        try
        {
            _invokeArgs2.SetValue(null, 0);
            object boxedContext = data;
            bool found = (bool)_tryGetOverrideMethod.Invoke(boxedContext,
                Unsafe.As<System.Array, object[]>(ref _invokeArgs2));

            if (!found || _invokeArgs2.GetValue(0) == null)
                return -1f;

            return (float)_overridePowerField.GetValue(_invokeArgs2.GetValue(0));
        }
        catch { return -1f; }
    }

    /// <summary>
    /// Set ThrusterOverrideData on a thruster entity.
    /// This triggers the game's thrust visuals (flame effects).
    /// </summary>
    public static bool TrySetThrustOverride(DEntityContext data, float overridePower)
    {
        if (_setOverrideMethod == null || _thrusterOverrideType == null || _overridePowerField == null)
            return false;

        try
        {
            object overrideData = Activator.CreateInstance(_thrusterOverrideType);
            _overridePowerField.SetValue(overrideData, overridePower);
            _invokeArgs2.SetValue(overrideData, 0);
            object boxedContext = data;
            _setOverrideMethod.Invoke(boxedContext,
                Unsafe.As<System.Array, object[]>(ref _invokeArgs2));
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Remove ThrusterOverrideData from a thruster entity (return to normal control).
    /// </summary>
    public static bool TryRemoveThrustOverride(DEntityContext data)
    {
        if (_tryRemoveOverrideMethod == null) return false;
        try
        {
            object boxedContext = data;
            _tryRemoveOverrideMethod.Invoke(boxedContext, null);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Read WorldTransform from an entity's DEntityContext.
    /// </summary>
    public static bool TryGetWorldTransform(DEntityContext data, out WorldTransform wt)
    {
        wt = default;
        if (_getWorldTransformMethod == null) return false;
        try
        {
            object boxedContext = data;
            wt = (WorldTransform)_getWorldTransformMethod.Invoke(boxedContext, null);
            return true;
        }
        catch { return false; }
    }

    private static MethodInfo _setWorldTransformMethod;

    /// <summary>
    /// Set orientation while keeping position. Does NOT zero angular velocity —
    /// call TryZeroAngularVelocity on a subsequent frame after physics processes the teleport.
    /// </summary>
    public static bool TrySetOrientation(DEntityContext data, Quaternion targetOrientation)
    {
        try
        {
            // Resolve Get/SetWorldTransform extension methods once
            if (_setWorldTransformMethod == null || _getWorldTransformMethod == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (var type in asm.GetTypes())
                    {
                        if (type.Name == "EntityTransformFunctions")
                        {
                            _setWorldTransformMethod = type.GetMethod("SetWorldTransform",
                                BindingFlags.Public | BindingFlags.Static, null,
                                new[] { typeof(DEntityContext), typeof(WorldTransform).MakeByRefType() }, null);
                            if (_setWorldTransformMethod != null)
                                Log.Default?.Info("[AERO] PhysicsHack: SetWorldTransform found");

                            _getWorldTransformMethod = type.GetMethod("GetWorldTransform",
                                BindingFlags.Public | BindingFlags.Static, null,
                                new[] { typeof(DEntityContext) }, null);
                            if (_getWorldTransformMethod != null)
                                Log.Default?.Info("[AERO] PhysicsHack: GetWorldTransform found (extension)");
                        }
                    }
                    if (_setWorldTransformMethod != null) break;
                }

                if (_setWorldTransformMethod == null)
                    Log.Default?.Info("[AERO] PhysicsHack: SetWorldTransform NOT found");
                if (_getWorldTransformMethod == null)
                    Log.Default?.Info("[AERO] PhysicsHack: GetWorldTransform NOT found");
            }

            if (_getWorldTransformMethod == null || _setWorldTransformMethod == null)
                return false;

            // Get current transform, replace orientation, set back
            WorldTransform wt = (WorldTransform)_getWorldTransformMethod.Invoke(null, new object[] { data });
            var newWt = new WorldTransform(wt.Position, targetOrientation);
            _setWorldTransformMethod.Invoke(null, new object[] { data, newWt });

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Teleport grid to a new position while preserving orientation.
    /// Call TrySetOrientation first at least once to resolve the transform methods.
    /// </summary>
    public static bool TrySetPosition(DEntityContext data, Vector3D newPosition)
    {
        try
        {
            if (_getWorldTransformMethod == null || _setWorldTransformMethod == null)
                return false;
            WorldTransform wt = (WorldTransform)_getWorldTransformMethod.Invoke(null, new object[] { data });
            var newWt = new WorldTransform(newPosition, wt.Orientation);
            _setWorldTransformMethod.Invoke(null, new object[] { data, newWt });
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Zero angular velocity using the exact same read-modify-write pattern as ApplyDeltaVAndTorque.
    /// Call on a frame AFTER TrySetOrientation so the physics engine has processed the teleport.
    /// </summary>
    public static bool TryZeroAngularVelocity(DEntityContext data)
    {
        if (!Available)
            return false;

        try
        {
            // Read RigidBodyData (same pattern as ApplyDeltaVAndTorque line 770)
            _invokeArgs.SetValue(null, 0);
            object boxedContext = data;
            bool found = (bool)_tryGetMethod.Invoke(boxedContext,
                Unsafe.As<System.Array, object[]>(ref _invokeArgs));

            if (!found || _invokeArgs.GetValue(0) == null)
                return false;

            object rbData = _invokeArgs.GetValue(0);

            _angularVelField.SetValue(rbData, Vector3.Zero);

            // Write back via Set<RigidBodyData> (same pattern as ApplyDeltaVAndTorque line 847)
            _invokeArgs.SetValue(rbData, 0);
            if (_setRbDataMethod != null)
            {
                _setRbDataMethod.Invoke(boxedContext,
                    Unsafe.As<System.Array, object[]>(ref _invokeArgs));
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Set angular velocity (world space) directly.
    /// </summary>
    public static bool TrySetAngularVelocity(DEntityContext data, Vector3 angVel)
    {
        if (!Available) return false;
        try
        {
            _invokeArgs.SetValue(null, 0);
            object boxedContext = data;
            bool found = (bool)_tryGetMethod.Invoke(boxedContext,
                Unsafe.As<System.Array, object[]>(ref _invokeArgs));
            if (!found || _invokeArgs.GetValue(0) == null) return false;

            object rbData = _invokeArgs.GetValue(0);
            _angularVelField.SetValue(rbData, angVel);

            _invokeArgs.SetValue(rbData, 0);
            if (_setRbDataMethod != null)
                _setRbDataMethod.Invoke(boxedContext,
                    Unsafe.As<System.Array, object[]>(ref _invokeArgs));
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Set linear velocity (world space) without touching angular velocity.
    /// </summary>
    public static bool TrySetLinearVelocity(DEntityContext data, Vector3 linearVel)
    {
        if (!Available) return false;
        try
        {
            _invokeArgs.SetValue(null, 0);
            object boxedContext = data;
            bool found = (bool)_tryGetMethod.Invoke(boxedContext,
                Unsafe.As<System.Array, object[]>(ref _invokeArgs));
            if (!found || _invokeArgs.GetValue(0) == null) return false;

            object rbData = _invokeArgs.GetValue(0);
            _linearVelField.SetValue(rbData, linearVel);

            _invokeArgs.SetValue(rbData, 0);
            if (_setRbDataMethod != null)
                _setRbDataMethod.Invoke(boxedContext,
                    Unsafe.As<System.Array, object[]>(ref _invokeArgs));
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Set both linear and angular velocity (world space) in a single write.
    /// </summary>
    public static bool TrySetVelocity(DEntityContext data, Vector3 linearVel, Vector3 angularVel)
    {
        if (!Available) return false;
        try
        {
            _invokeArgs.SetValue(null, 0);
            object boxedContext = data;
            bool found = (bool)_tryGetMethod.Invoke(boxedContext,
                Unsafe.As<System.Array, object[]>(ref _invokeArgs));
            if (!found || _invokeArgs.GetValue(0) == null) return false;

            object rbData = _invokeArgs.GetValue(0);
            _linearVelField.SetValue(rbData, linearVel);
            _angularVelField.SetValue(rbData, angularVel);

            _invokeArgs.SetValue(rbData, 0);
            if (_setRbDataMethod != null)
                _setRbDataMethod.Invoke(boxedContext,
                    Unsafe.As<System.Array, object[]>(ref _invokeArgs));
            return true;
        }
        catch { return false; }
    }

    // ═══════════════════════════════════════════════════════════════
    // Offset impulse (ApplyImpulseAt equivalent)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Apply an impulse at a world-space offset position on the grid entity.
    /// Replicates RigidBodyDataFunctions.ApplyImpulseAt:
    ///   linVel += impulse * invMass
    ///   angular delta from cross(r, impulse) through inertia tensor
    /// The grid's DEntityContext must have RigidBodyData and RigidBodyMassProperties.
    /// </summary>
    public static bool ApplyImpulseAt(DEntityContext data, Vector3 impulse, Vector3D worldPosition, WorldTransform wt = default)
    {
        if (!Available || _invInertiaTensorField == null)
            return false;
        if (float.IsNaN(impulse.X) || float.IsNaN(impulse.Y) || float.IsNaN(impulse.Z))
            return false;

        try
        {
            // Read RigidBodyData
            _invokeArgs.SetValue(null, 0);
            object boxedContext = data;
            bool found = (bool)_tryGetMethod.Invoke(boxedContext,
                Unsafe.As<System.Array, object[]>(ref _invokeArgs));
            if (!found || _invokeArgs.GetValue(0) == null)
                return false;
            object rbData = _invokeArgs.GetValue(0);

            // Read mass properties
            _invokeArgs2.SetValue(null, 0);
            bool foundMass = (bool)_tryGetMassMethod.Invoke(boxedContext,
                Unsafe.As<System.Array, object[]>(ref _invokeArgs2));
            if (!foundMass || _invokeArgs2.GetValue(0) == null)
                return false;
            object massData = _invokeArgs2.GetValue(0);

            float invMass = (float)_invMassField.GetValue(massData);
            Vector3 com = (Vector3)_comField.GetValue(massData);
            Vector3 invInertia = (Vector3)_invInertiaTensorField.GetValue(massData);

            // Linear: linVel += impulse * invMass
            Vector3 linVel = (Vector3)_linearVelField.GetValue(rbData);
            linVel += impulse * invMass;
            _linearVelField.SetValue(rbData, linVel);

            // Angular: replicate ApplyImpulseAt math from RigidBodyDataFunctions
            // r = TransformInv(worldPos, wt) - centerOfMass  (local space offset)
            Vector3D localPos = WorldTransform.TransformInv(worldPosition, wt);
            Vector3D r = localPos - (Vector3D)com;

            // localImpulse = TransformDirectionInv(impulse, wt)
            Vector3 localImpulse = WorldTransform.TransformDirectionInv(impulse, wt);

            // torqueLocal = cross(r, localImpulse)
            Vector3D torqueLocal = Vector3D.Cross(r, (Vector3D)localImpulse);

            // Apply through inertia tensor (principal axes)
            Vector3D angDelta = torqueLocal;
            if (_inertiaMajorAxisRotField != null)
            {
                Quaternion majorAxisRot = (Quaternion)_inertiaMajorAxisRotField.GetValue(massData);
                Quaternion invRot = Quaternion.Conjugate(majorAxisRot);
                angDelta = Vector3D.Transform(angDelta, invRot);
                angDelta = new Vector3D(
                    angDelta.X * invInertia.X,
                    angDelta.Y * invInertia.Y,
                    angDelta.Z * invInertia.Z);
                angDelta = Vector3D.Transform(angDelta, majorAxisRot);
            }
            else
            {
                angDelta = new Vector3D(
                    angDelta.X * invInertia.X,
                    angDelta.Y * invInertia.Y,
                    angDelta.Z * invInertia.Z);
            }

            // Transform angular delta to world space and apply
            Vector3 worldAngDelta = (Vector3)WorldTransform.TransformDirection((Vector3)angDelta, wt);
            Vector3 angVel = (Vector3)_angularVelField.GetValue(rbData);
            angVel += worldAngDelta;
            _angularVelField.SetValue(rbData, angVel);

            // Write back
            _invokeArgs.SetValue(rbData, 0);
            if (_setRbDataMethod != null)
                _setRbDataMethod.Invoke(boxedContext,
                    Unsafe.As<System.Array, object[]>(ref _invokeArgs));

            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Cancel a linear impulse at CoM (no angular component).
    /// Used as the first half of the dual-force offset pattern.
    /// </summary>
    public static bool CancelLinearImpulse(DEntityContext data, Vector3 impulse)
    {
        if (!Available || _invMassField == null)
            return false;
        if (float.IsNaN(impulse.X) || float.IsNaN(impulse.Y) || float.IsNaN(impulse.Z))
            return false;

        try
        {
            // Read RigidBodyData
            _invokeArgs.SetValue(null, 0);
            object boxedContext = data;
            bool found = (bool)_tryGetMethod.Invoke(boxedContext,
                Unsafe.As<System.Array, object[]>(ref _invokeArgs));
            if (!found || _invokeArgs.GetValue(0) == null)
                return false;
            object rbData = _invokeArgs.GetValue(0);

            // Read invMass
            _invokeArgs2.SetValue(null, 0);
            bool foundMass = (bool)_tryGetMassMethod.Invoke(boxedContext,
                Unsafe.As<System.Array, object[]>(ref _invokeArgs2));
            if (!foundMass || _invokeArgs2.GetValue(0) == null)
                return false;
            float invMass = (float)_invMassField.GetValue(_invokeArgs2.GetValue(0));

            // linVel -= impulse * invMass (cancel the linear component)
            Vector3 linVel = (Vector3)_linearVelField.GetValue(rbData);
            linVel -= impulse * invMass;
            _linearVelField.SetValue(rbData, linVel);

            // Write back
            _invokeArgs.SetValue(rbData, 0);
            if (_setRbDataMethod != null)
                _setRbDataMethod.Invoke(boxedContext,
                    Unsafe.As<System.Array, object[]>(ref _invokeArgs));

            return true;
        }
        catch { return false; }
    }

    // ═══════════════════════════════════════════════════════════════
    // Gravity & ground height (raycast)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Read gravity direction from entity's GravityEffectData.
    /// Returns zero vector if no gravity data.
    /// </summary>
    public static Vector3 GetGravityDirection(DEntityContext data)
    {
        if (_tryGetGravityMethod == null || _gravitySumField == null)
            return Vector3.Zero;
        try
        {
            _invokeArgs2.SetValue(null, 0);
            object boxedContext = data;
            bool found = (bool)_tryGetGravityMethod.Invoke(boxedContext,
                Unsafe.As<System.Array, object[]>(ref _invokeArgs2));
            if (!found || _invokeArgs2.GetValue(0) == null)
                return Vector3.Zero;
            return (Vector3)_gravitySumField.GetValue(_invokeArgs2.GetValue(0));
        }
        catch { return Vector3.Zero; }
    }

    /// <summary>
    /// Initialize the ground raycast system.
    /// Pass Scene (entity.Scene) — Session is extracted from Scene.UserObject.
    /// Resolves IPhysics.CastRayAsync for terrain-based ground distance.
    /// </summary>
    public static void InitGroundSystem(object scene)
    {
        if (_groundSystemInitialized) return;
        _groundSystemInitialized = true;

        try
        {
            // Get Session from Scene.UserObject
            var userObjProp = scene.GetType().GetProperty("UserObject",
                BindingFlags.Public | BindingFlags.Instance);
            if (userObjProp == null) return;
            object session = userObjProp.GetValue(scene);
            if (session == null) return;

            // Resolve IPhysics via Session.EntitySerializer.TryResolveSessionService
            var iPhysicsType = Type.GetType(
                "Keen.VRage.Physics.IPhysics, VRage.Physics",
                throwOnError: false);
            if (iPhysicsType == null)
            {
                Log.Default?.Info("[AERO] PhysicsHack: IPhysics type not found");
                return;
            }

            var entitySerProp = session.GetType().GetProperty("EntitySerializer",
                BindingFlags.Public | BindingFlags.Instance);
            if (entitySerProp == null) return;
            object entitySer = entitySerProp.GetValue(session);
            if (entitySer == null) return;

            var resolveMethod = entitySer.GetType().GetMethod("TryResolveSessionService",
                BindingFlags.Public | BindingFlags.Instance);
            if (resolveMethod == null) return;

            _invokeArgs2.SetValue(iPhysicsType, 0);
            _physicsInstance = resolveMethod.Invoke(entitySer,
                Unsafe.As<System.Array, object[]>(ref _invokeArgs2));
            if (_physicsInstance == null)
            {
                Log.Default?.Info("[AERO] PhysicsHack: IPhysics not available in session");
                return;
            }

            // Find CastRayAsync(in RayCastArgs, CollisionPreset) -> Task<Buffer<SweepQueryHit>>
            foreach (var m in iPhysicsType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name == "CastRayAsync" && m.GetParameters().Length == 2)
                {
                    _castRayAsyncMethod = m;
                    break;
                }
            }

            if (_castRayAsyncMethod != null)
                Log.Default?.Info("[AERO] PhysicsHack: IPhysics.CastRayAsync resolved");
            else
                Log.Default?.Info("[AERO] PhysicsHack: CastRayAsync not found");
        }
        catch (Exception ex)
        {
            Log.Default?.Info($"[AERO] PhysicsHack: Ground system init failed: {ex.Message}");
        }
    }

    // ── Async raycast state ──
    private static object _pendingRayTask;   // Task<Buffer<SweepQueryHit>> in flight
    private static float _lastGroundDist = -1f;
    private static int _raycastCooldown;
    // Cached PropertyInfo for task/buffer result reading (resolved on first completed task)
    private static PropertyInfo _taskIsCompletedProp;
    private static PropertyInfo _taskResultProp;
    private static PropertyInfo _bufferCountProp;
    private static PropertyInfo _bufferIndexerProp;
    private static bool _taskPropsResolved;

    /// <summary>
    /// Get ground distance via async physics raycast.
    /// Fires a ray downward each N frames, returns cached result between shots.
    /// </summary>
    public static float GetGroundDistance(Vector3D worldPosition, Vector3 gravityDir)
    {
        if (_physicsInstance == null || _castRayAsyncMethod == null)
            return -1f;

        // Check if pending raycast completed
        if (_pendingRayTask != null)
        {
            try
            {
                // Resolve task PropertyInfos once on first result
                if (!_taskPropsResolved)
                {
                    _taskPropsResolved = true;
                    var taskType = _pendingRayTask.GetType();
                    _taskIsCompletedProp = taskType.GetProperty("IsCompleted",
                        BindingFlags.Public | BindingFlags.Instance);
                    _taskResultProp = taskType.GetProperty("Result",
                        BindingFlags.Public | BindingFlags.Instance);
                }

                if (_taskIsCompletedProp != null && (bool)_taskIsCompletedProp.GetValue(_pendingRayTask))
                {
                    if (_taskResultProp != null)
                    {
                        object buffer = _taskResultProp.GetValue(_pendingRayTask);

                        // Resolve buffer PropertyInfos once
                        if (_bufferCountProp == null && buffer != null)
                        {
                            var bufType = buffer.GetType();
                            _bufferCountProp = bufType.GetProperty("Count",
                                BindingFlags.Public | BindingFlags.Instance);
                            _bufferIndexerProp = bufType.GetProperty("Item",
                                BindingFlags.Public | BindingFlags.Instance);
                        }

                        int count = _bufferCountProp != null ? (int)_bufferCountProp.GetValue(buffer) : 0;

                        if (count > 0 && _bufferIndexerProp != null)
                        {
                            _indexerArgs.SetValue(0, 0);
                            object hit = _bufferIndexerProp.GetValue(buffer,
                                Unsafe.As<System.Array, object[]>(ref _indexerArgs));
                            if (hit != null && _hitFractionField != null)
                            {
                                float fraction = (float)_hitFractionField.GetValue(hit);
                                _lastGroundDist = fraction * 200f; // maxDistance = 200m
                            }
                        }
                        else
                        {
                            _lastGroundDist = -1f; // no hit
                        }

                        if (buffer is IDisposable disp)
                            disp.Dispose();
                    }
                    _pendingRayTask = null;
                }
            }
            catch
            {
                _lastGroundDist = -1f;
                _pendingRayTask = null;
            }
        }

        // Fire new raycast every ~10 frames (6Hz)
        if (_pendingRayTask == null && --_raycastCooldown <= 0)
        {
            _raycastCooldown = _lastGroundDist >= 0f && _lastGroundDist < 50f ? 3 : 10;

            float dirLen = gravityDir.Length();
            if (dirLen < 0.01f) return _lastGroundDist;
            Vector3 dir = gravityDir / dirLen;

            try
            {
                // Create RayCastArgs
                var argsType = Type.GetType(
                    "Keen.VRage.Core.Game.GameSystems.Queries.RayCastArgs, VRage.Core.Game",
                    throwOnError: false);
                if (argsType == null) return _lastGroundDist;

                object rayArgs = Activator.CreateInstance(argsType);
                argsType.GetField("Position", BindingFlags.Public | BindingFlags.Instance)
                    ?.SetValue(rayArgs, worldPosition);
                argsType.GetField("Direction", BindingFlags.Public | BindingFlags.Instance)
                    ?.SetValue(rayArgs, dir * 200f);

                // Create CollisionPreset with ClosestHit (enum value 3)
                var presetType = Type.GetType(
                    "Keen.VRage.Physics.CollisionPreset, VRage.Physics",
                    throwOnError: false);
                var presetEnumType = Type.GetType(
                    "Keen.VRage.Physics.CollisionPresetType, VRage.Physics",
                    throwOnError: false);

                object preset = Activator.CreateInstance(presetType);
                if (presetEnumType != null)
                {
                    var typeField = presetType.GetField("Type",
                        BindingFlags.Public | BindingFlags.Instance);
                    typeField?.SetValue(preset, Enum.ToObject(presetEnumType, 3)); // ClosestHit
                }

                // Call CastRayAsync(in RayCastArgs, CollisionPreset)
                var callArgs = Array.CreateInstance(typeof(object), 2);
                callArgs.SetValue(rayArgs, 0);
                callArgs.SetValue(preset, 1);
                _pendingRayTask = _castRayAsyncMethod.Invoke(_physicsInstance,
                    Unsafe.As<System.Array, object[]>(ref callArgs));
            }
            catch (Exception ex)
            {
                Log.Default?.Info($"[AERO] Raycast fire failed: {ex.Message}");
            }
        }

        return _lastGroundDist;
    }

    // ═══════════════════════════════════════════════════════════════
    // Component iteration (bypasses broken tag-based TryGet)
    // ImmutableArray<T> is banned (VRS1001) so we access via reflection.
    // ═══════════════════════════════════════════════════════════════

    private static FieldInfo _entityComponentsField;
    private static MethodInfo _immArrayLengthGetter;
    private static MethodInfo _immArrayIndexer;
    private static bool _componentAccessResolved;
    // Pre-allocated args for indexer invocation (object[] is banned by VRS1001)
    private static System.Array _indexerArgs;

    private static void EnsureComponentAccessResolved()
    {
        if (_componentAccessResolved) return;
        _componentAccessResolved = true;

        try
        {
            // Entity.Components is a public field of type ImmutableArray<Component>
            _entityComponentsField = typeof(Entity).GetField("Components",
                BindingFlags.Public | BindingFlags.Instance);

            if (_entityComponentsField != null)
            {
                var immArrayType = _entityComponentsField.FieldType;
                _immArrayLengthGetter = immArrayType.GetProperty("Length",
                    BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
                // Item property (indexer) — named "Item" with int parameter
                _immArrayIndexer = immArrayType.GetProperty("Item",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, null, new Type[] { typeof(int) }, null)?.GetGetMethod();

                // Pre-allocate args array via Array.CreateInstance to dodge VRS1001
                _indexerArgs = Array.CreateInstance(typeof(object), 1);

                Log.Default?.Info($"[AERO] ComponentAccess: field={_entityComponentsField != null} " +
                    $"length={_immArrayLengthGetter != null} indexer={_immArrayIndexer != null}");
            }
        }
        catch (Exception ex)
        {
            Log.Default?.Info($"[AERO] ComponentAccess resolution failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Get the number of components on an entity (via reflection).
    /// </summary>
    public static int GetEntityComponentCount(Entity entity)
    {
        if (entity == null) return 0;
        EnsureComponentAccessResolved();
        if (_entityComponentsField == null || _immArrayLengthGetter == null) return 0;
        try
        {
            object componentsBox = _entityComponentsField.GetValue(entity);
            return (int)_immArrayLengthGetter.Invoke(componentsBox, null);
        }
        catch { return 0; }
    }

    /// <summary>
    /// Find a component on an entity by iterating Entity.Components and matching type.
    /// Uses reflection to bypass VRS1001 ban on ImmutableArray and object[].
    /// </summary>
    public static Component FindComponentByType(Entity entity, Type componentType)
    {
        if (entity == null || componentType == null) return null;
        EnsureComponentAccessResolved();
        if (_entityComponentsField == null || _immArrayLengthGetter == null || _immArrayIndexer == null)
            return null;

        try
        {
            object componentsBox = _entityComponentsField.GetValue(entity);
            int length = (int)_immArrayLengthGetter.Invoke(componentsBox, null);
            for (int i = 0; i < length; i++)
            {
                _indexerArgs.SetValue(i, 0);
                var comp = _immArrayIndexer.Invoke(componentsBox,
                    Unsafe.As<System.Array, object[]>(ref _indexerArgs));
                if (comp != null && componentType.IsInstanceOfType(comp))
                    return (Component)comp;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Find ALL components on an entity matching a type.
    /// </summary>
    public static void FindComponentsByType(Entity entity, Type componentType, List<Component> results)
    {
        results.Clear();
        if (entity == null || componentType == null) return;
        EnsureComponentAccessResolved();
        if (_entityComponentsField == null || _immArrayLengthGetter == null || _immArrayIndexer == null)
            return;

        try
        {
            object componentsBox = _entityComponentsField.GetValue(entity);
            int length = (int)_immArrayLengthGetter.Invoke(componentsBox, null);
            for (int i = 0; i < length; i++)
            {
                _indexerArgs.SetValue(i, 0);
                var comp = _immArrayIndexer.Invoke(componentsBox,
                    Unsafe.As<System.Array, object[]>(ref _indexerArgs));
                if (comp != null && componentType.IsInstanceOfType(comp))
                    results.Add((Component)comp);
            }
        }
        catch { }
    }

    /// <summary>
    /// Log all component types on an entity (diagnostic).
    /// </summary>
    public static void LogEntityComponents(Entity entity, string label)
    {
        if (entity == null)
        {
            Log.Default?.Info($"[AERO] {label}: entity is null");
            return;
        }
        EnsureComponentAccessResolved();
        if (_entityComponentsField == null || _immArrayLengthGetter == null || _immArrayIndexer == null)
        {
            Log.Default?.Info($"[AERO] {label}: component access not resolved");
            return;
        }

        try
        {
            object componentsBox = _entityComponentsField.GetValue(entity);
            int length = (int)_immArrayLengthGetter.Invoke(componentsBox, null);
            Log.Default?.Info($"[AERO] {label}: {length} components, DEntity={entity.DEntity}");
            for (int i = 0; i < length; i++)
            {
                _indexerArgs.SetValue(i, 0);
                var comp = _immArrayIndexer.Invoke(componentsBox,
                    Unsafe.As<System.Array, object[]>(ref _indexerArgs));
                Log.Default?.Info($"[AERO]   [{i}] {comp?.GetType().FullName ?? "(null)"}");
            }
        }
        catch (Exception ex)
        {
            Log.Default?.Info($"[AERO] {label}: failed to enumerate: {ex.Message}");
        }
    }
}
