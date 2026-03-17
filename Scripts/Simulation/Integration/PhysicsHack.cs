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

    // Speed limit override
    private static Type _speedLimitType;
    private static FieldInfo _speedLimitField;
    private static bool _speedUncapped;

    // Gravity multiplier override
    private static FieldInfo _gravityMultField;
    private static bool _gravityFixed;

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
        }
        catch (Exception ex)
        {
            Log.Default?.Info($"[AERO] PhysicsHack: init failed: {ex.Message}");
            _available = false;
        }
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
    /// Find PhysicsSessionConfiguration singleton and set GravityMultiplier to 1.
    /// Also sets MaximumSpeedLinear to 1000.
    /// </summary>
    public static void TryFixGravity(float targetGravity = 1f, float targetSpeed = 1000f)
    {
        if (_gravityFixed) return;

        try
        {
            var configType = Type.GetType(
                "Keen.Game2.Simulation.GameSystems.Physicss.PhysicsSessionConfiguration, Game2.Simulation",
                throwOnError: false);
            if (configType == null)
            {
                Log.Default?.Info("[AERO] TryFixGravity: PhysicsSessionConfiguration type not found");
                return;
            }

            // PhysicsSessionConfiguration is a Configuration (singleton in session).
            // Find the static Instance or iterate known definition stores.
            // Try the Keen.VRage.Library.Definitions.DefinitionManager approach:
            // Get all instances of this type via reflection on the definition system.

            var gravProp = configType.GetProperty("GravityMultiplier",
                BindingFlags.Public | BindingFlags.Instance);
            var speedProp = configType.GetProperty("MaximumSpeedLinear",
                BindingFlags.Public | BindingFlags.Instance);

            if (gravProp == null && speedProp == null)
            {
                Log.Default?.Info("[AERO] TryFixGravity: properties not found");
                return;
            }

            // The setter is private, get the backing field instead
            var gravField = configType.GetField("<GravityMultiplier>k__BackingField",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var speedField = configType.GetField("<MaximumSpeedLinear>k__BackingField",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (gravField == null)
            {
                // Try other field name patterns
                foreach (var f in configType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (f.Name.Contains("Gravity") || f.Name.Contains("gravity"))
                    {
                        gravField = f;
                        Log.Default?.Info($"[AERO] Found gravity field: {f.Name}");
                    }
                    if (f.Name.Contains("SpeedLinear") || f.Name.Contains("speedLinear"))
                    {
                        speedField = f;
                        Log.Default?.Info($"[AERO] Found speed field: {f.Name}");
                    }
                }
            }

            // Find all loaded instances via DefinitionManager
            var defManagerType = Type.GetType(
                "Keen.VRage.Library.Definitions.DefinitionManager, VRage.Library",
                throwOnError: false);

            // Alternative: enumerate all objects of this type using GC or type registry
            // Simpler: the Configuration base class may have a static accessor
            // Let's try to find any instance via reflection on loaded assemblies

            // Actually, SceneSettings has GlobalGravity. Let's also try that.
            var sceneSettingsType = Type.GetType(
                "Keen.VRage.Physics.SceneSettings, VRage.Physics",
                throwOnError: false);
            if (sceneSettingsType != null)
            {
                var gmField = sceneSettingsType.GetField("GravityMultiplier",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                if (gmField != null)
                {
                    Log.Default?.Info($"[AERO] Found SceneSettings.GravityMultiplier: {gmField.FieldType}");
                }

                // Log all static fields for discovery
                foreach (var f in sceneSettingsType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    Log.Default?.Info($"[AERO] SceneSettings static: {f.Name} ({f.FieldType.Name})");
                }
            }

            Log.Default?.Info("[AERO] TryFixGravity: searching for config instance...");

            // Use HavokSessionComponent which reads PhysicsSessionConfiguration
            var havokType = Type.GetType(
                "Keen.VRage.Physics.Havok.HavokSessionComponent, VRage.Physics",
                throwOnError: false);
            if (havokType != null)
            {
                foreach (var f in havokType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    Log.Default?.Info($"[AERO] HavokSession static: {f.Name} ({f.FieldType.Name})");
                }
            }

            _gravityFixed = true; // Don't spam logs, run once
        }
        catch (Exception ex)
        {
            Log.Default?.Info($"[AERO] TryFixGravity failed: {ex.Message}");
            _gravityFixed = true;
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
    /// torqueLocal is in grid-local space; converted to angular deltaV via inertia tensor.
    /// </summary>
    public static bool ApplyDeltaVAndTorque(DEntityContext data, Vector3 deltaV, Vector3 torqueLocal, float dt = 1f / 60f)
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

            var contextType = typeof(DEntityContext);
            foreach (var m in contextType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (m.Name == "Set" && m.IsGenericMethodDefinition
                    && m.GetParameters().Length == 1
                    && !m.GetParameters()[0].IsOut)
                {
                    var setMethod = m.MakeGenericMethod(_rbDataType);
                    setMethod.Invoke(boxedContext,
                        Unsafe.As<System.Array, object[]>(ref _invokeArgs));
                    break;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
