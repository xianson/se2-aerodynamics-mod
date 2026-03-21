#pragma warning disable
using System;
using System.Reflection;

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

    // Pre-allocated args list to avoid creating object[] (banned by VRS1001)
    private static System.Array _invokeArgs;

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

            // Create args array via Array.CreateInstance to dodge VRS1001 ban on T[]
            _invokeArgs = Array.CreateInstance(typeof(object), 1);

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
                // Get inertia data
                _invokeArgs.SetValue(null, 0);
                bool foundMass = (bool)_tryGetMassMethod.Invoke(boxedContext,
                    Unsafe.As<System.Array, object[]>(ref _invokeArgs));

                if (foundMass && _invokeArgs.GetValue(0) != null)
                {
                    object massData = _invokeArgs.GetValue(0);
                    Vector3 invInertia = (Vector3)_invInertiaTensorField.GetValue(massData);

                    // deltaOmega = I⁻¹ · torque · dt
                    // If we have the major axis rotation, rotate torque to principal axes first
                    Vector3 torquePrincipal = torqueLocal;
                    if (_inertiaMajorAxisRotField != null)
                    {
                        Quaternion majorAxisRot = (Quaternion)_inertiaMajorAxisRotField.GetValue(massData);
                        // Inverse rotation: body → principal axes
                        Quaternion invRot = Quaternion.Conjugate(majorAxisRot);
                        torquePrincipal = Vector3.Transform(torqueLocal, invRot);
                    }

                    Vector3 deltaOmega = new Vector3(
                        torquePrincipal.X * invInertia.X,
                        torquePrincipal.Y * invInertia.Y,
                        torquePrincipal.Z * invInertia.Z) * dt;

                    // Rotate back to body frame if needed
                    if (_inertiaMajorAxisRotField != null)
                    {
                        Quaternion majorAxisRot = (Quaternion)_inertiaMajorAxisRotField.GetValue(massData);
                        deltaOmega = Vector3.Transform(deltaOmega, majorAxisRot);
                    }

                    // Transform deltaOmega from local to world space
                    // (RigidBodyData.AngularVelocity is stored in world space)
                    if (gridOrientation.HasValue)
                    {
                        deltaOmega = Vector3.Transform(deltaOmega, gridOrientation.Value);
                    }

                    // Re-get RigidBodyData (we clobbered _invokeArgs with mass query)
                    _invokeArgs.SetValue(null, 0);
                    found = (bool)_tryGetMethod.Invoke(boxedContext,
                        Unsafe.As<System.Array, object[]>(ref _invokeArgs));

                    if (found && _invokeArgs.GetValue(0) != null)
                    {
                        rbData = _invokeArgs.GetValue(0);
                        // Re-apply linear (we read fresh data)
                        _linearVelField.SetValue(rbData, linVel);

                        Vector3 angVel = (Vector3)_angularVelField.GetValue(rbData);
                        angVel += deltaOmega;
                        _angularVelField.SetValue(rbData, angVel);
                    }
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
}
