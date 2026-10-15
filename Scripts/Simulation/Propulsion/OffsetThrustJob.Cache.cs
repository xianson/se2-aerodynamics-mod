#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.BlockOctrees;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement;

namespace AeroMod;

/// <summary>The grid's thruster and gyro caches: profiles by block GUID, the block scan, and the one private
/// _definition field each (docs/REFLECTION_AND_WHITELIST.md) for power, direction, class and gyro torque.</summary>
public static partial class OffsetThrustJob
{
    // ═══════════════════════════════════════════════════════════════
    // Thruster cache building
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// GUID → ThrustProfile mapping for custom thruster blocks.
    /// Register entries before the first RebuildThrusterCache call.
    /// Vanilla SE2 thrusters (hydrogen, ion) get null profile (= rocket, flat 1.0).
    /// </summary>
    public static readonly Dictionary<Guid, ThrustProfile> ProfileByGuid = new();

    static OffsetThrustJob()
    {
        // Atmospheric thrusters = Turbofan (air-breathing, thrust drops at transonic)
        var atmo = ThrustProfiles.Turbofan;
        ProfileByGuid[new Guid("8dfddd91-eb3a-4979-8b16-2fdf43061486")] = atmo; // Atmo 100
        ProfileByGuid[new Guid("272859d7-957d-41ba-9f9c-8f84cddb5cb3")] = atmo; // Atmo 250
        ProfileByGuid[new Guid("8a005c16-0a05-444d-ab26-786653199b52")] = atmo; // Atmo 500
        ProfileByGuid[new Guid("bfbbff3e-7396-428a-a12c-3f32ec14c712")] = atmo; // Atmo 1000
        // Hydrogen & Ion = Rocket (no Mach penalty, null profile by default)
    }

    /// <summary>
    /// Scan all blocks on the grid and build a cache of thruster info.
    /// Call on grid init and when blocks change.
    ///
    /// Detection strategy: iterate Entity.Components on each block to find
    /// ThrusterComponent by type. This bypasses the tag-based Entity.TryGet
    /// and DEntityContext.TryGet which fail when invoked via reflection
    /// (DEntityContext is a struct; boxing breaks the archetype-based
    /// ref-returning data lookup).
    /// </summary>
    public static void RebuildThrusterCache(
        BlockOctreeComponent octree,
        float blockSize,
        List<ThrusterInfo> outThrusters,
        Entity gridEntity = null)
    {
        outThrusters.Clear();
        EnsureThrusterReflectionResolved();

        if (_thrusterCompType == null)
        {
            Log.Default?.Info("[AERO] OffsetThrust: ThrusterComponent type not resolved, skipping");
            return;
        }

        // Strategy: iterate grid's HierarchyComponent.Children to find entities
        // with ThrusterComponent. The block octree only stores structural
        // CubeBlockComponents — functional components like ThrusterComponent
        // live on the same entity but aren't findable via the octree's type.
        // HierarchyComponent.Children contains ALL child entities.
        int totalChildren = 0, thrustBlocks = 0;

        if (gridEntity != null)
        {
            var hierarchy = gridEntity.TryGet<HierarchyComponent>();
            if (hierarchy != null && hierarchy.Children != null)
            {
                foreach (var childEntity in hierarchy.Children)
                {
                    if (childEntity == null) continue;
                    totalChildren++;

                    // Typed lookup -- verified in game to agree with both the tag lookup and
                    // the old reflective scan (LOOKUPPROBE tag=True generic=True scan=True).
                    var thrusterComp = childEntity.TryGet<ThrusterComponent>();
                    if (thrusterComp == null) continue;
                    thrustBlocks++;

                    // Get CubeBlockComponent from this entity for orientation/position
                    var block = childEntity.TryGet<CubeBlockComponent>();
                    if (block == null) continue;

                    float maxPower = 0;
                    int directionInt = 0;

                    if (_thrusterDefField != null)
                    {
                        var def = _thrusterDefField.GetValue(thrusterComp);
                        if (def != null)
                        {
                            if (def is ThrusterDefinition td)
                                maxPower = td.ThrustPower;
                            if (def is ThrusterDefinition td2)
                            {
                                var rawDir = (object)td2.ThrustDirection;
                                // Unbox enum to its underlying type first, then convert
                                directionInt = Convert.ToInt32(rawDir);
                                var orientedDir = block.BlockOrientation.TransformDirection(
                                    (Base6Directions.Direction)directionInt);
                                directionInt = (int)orientedDir;
                            }
                        }
                    }

                    if (maxPower <= 0f) continue;

                    var aabb = block.AABB;
                    // (the AABB is in 0.25 m cells, not blocks: x blockSize put every thruster ~10x too far out on a large grid -
                    //  wrong torque arms for ThrustTorque, rotor distances. 2026-10-15: cell i is CENTRED at 0.25 i (the game's
                    //  CubeGridCoords.GridToLocal(AABB).Center); the old (min + max + 1) / 2 put every thruster 0.125 m off on
                    //  each axis - a fake couple of 0.125 m x the total thrust, pitch and yaw, on a symmetric ship)
                    Vector3 localPos = CellCoords.BlockCentre(aabb.Min, aabb.Max);
                    Vector3 drawPos = localPos;

                    Vector3 thrustDir = DirectionToVector(directionInt);

                    // Classify by ThrustClass from ThrusterDefinition
                    ThrustProfile profile = null;
                    string thrustClass = GetThrustClass(thrusterComp);
                    if (thrustClass != null && thrustClass.Contains("Atmospheric"))
                        profile = ThrustProfiles.Turbofan;

                    // Read per-thruster attitude fraction from injected component
                    var aeroSettings = childEntity.TryGet<AeroThrustSettingsComponent>();
                    float attFrac = 0.5f; // hardcoded default — terminal slider can override later

                    outThrusters.Add(new ThrusterInfo
                    {
                        ThrusterEntity = childEntity,
                        ThrusterComponent = thrusterComp,
                        GridLocalPosition = localPos,
                        DrawPosition = drawPos,
                        ThrustDirection = thrustDir,
                        MaxPower = maxPower,
                        Profile = profile,
                        IntakeDirection = thrustDir, // intake faces into airflow = same direction as thrust
                        AttitudeFraction = attFrac,
                        Settings = aeroSettings,
                        Cell = aabb.Min + aabb.Max,
                        CellMin = aabb.Min, CellMax = aabb.Max,
                    });
                }
            }
        }

        int airBreathing = 0;
        for (int i = 0; i < outThrusters.Count; i++)
            if (outThrusters[i].Profile != null) airBreathing++;

        Log.Default?.Info($"[AERO] OffsetThrust: scanned {totalChildren} hierarchy children, {thrustBlocks} with ThrusterComponent, cached {outThrusters.Count} thrusters ({airBreathing} air-breathing)");
    }



    /// <summary>
    /// Convert Base6Directions.Direction (int) to a unit vector.
    /// Forward=0, Backward=1, Left=2, Right=3, Up=4, Down=5
    /// </summary>
    private static Vector3 DirectionToVector(int dir)
    {
        switch (dir)
        {
            case 0: return -Vector3.UnitZ; // Forward
            case 1: return Vector3.UnitZ;  // Backward
            case 2: return -Vector3.UnitX; // Left
            case 3: return Vector3.UnitX;  // Right
            case 4: return Vector3.UnitY;  // Up
            case 5: return -Vector3.UnitY; // Down
            default: return Vector3.Zero;
        }
    }

    // ─── ThrusterComponent reflection resolution ─────────

    private static Type _thrusterCompType;
    private static System.Reflection.FieldInfo _thrusterDefField;
    private static bool _thrusterReflectionResolved;

    /// <summary>
    /// HOLDOUT: ThrusterComponent._definition is a private field with no public accessor.
    /// The type itself is public and every value we want off ThrusterDefinition
    /// (ThrustPower / ThrustDirection / ThrustClass) is a public property, so this resolves the
    /// one field and everything after it is typed.
    /// </summary>
    private static void EnsureThrusterReflectionResolved()
    {
        if (_thrusterReflectionResolved) return;
        _thrusterReflectionResolved = true;
        try
        {
            _thrusterCompType = typeof(ThrusterComponent);
            _thrusterDefField = _thrusterCompType.GetField("_definition",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Log.Default?.Info($"[AERO] ThrusterComponent def field={_thrusterDefField != null}");
        }
        catch (Exception ex)
        {
            Log.Default?.Info($"[AERO] ThrusterComponent resolution failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Whether the thruster is firing. Component.HasData is protected, but Component.Data is a
    /// public DEntityContext, so the public Has&lt;T&gt; gets there without reflection.
    /// </summary>
    private static bool IsComponentThrusting(Component thrusterComp)
    {
        if (thrusterComp == null || !PhysicsHack.Alive(thrusterComp.Data)) return false;
        try { return thrusterComp.Data.Has<IsThrusting>(); }
        catch { System.Threading.Interlocked.Increment(ref PhysicsHack.Caught); return false; }
    }

    /// <summary>
    /// Game-computed thrust for this thruster this frame, in newtons; 0 when the game produced
    /// none. SE2's equivalent of SE1 Thrust.CurrentStrength * ForceMagnitude.
    /// Component.TryGetData is protected, but Component.Data is public, so TryGet works.
    /// </summary>
    private static float GetActualThrust(Component thrusterComp)
    {
        if (thrusterComp == null || !PhysicsHack.Alive(thrusterComp.Data)) return -1f;
        try
        {
            return thrusterComp.Data.TryGet<ActiveThrustData>(out var atd)
                ? atd.ComputedThrustPerFrame.Length()
                : 0f;
        }
        catch { return -1f; }
    }

    /// <summary>Public wrapper to force a thrust override from outside.</summary>
    public static void ForceOverride(ThrusterInfo thruster, float value)
    {
        if (value > 0f)
            PhysicsHack.TrySetThrustOverride(thruster.ThrusterEntity.Data, value);
        else
            PhysicsHack.TryRemoveThrustOverride(thruster.ThrusterEntity.Data);
    }

    /// <summary>Read ThrustClass (StringId) from ThrusterDefinition on a ThrusterComponent.</summary>
    private static string GetThrustClass(Component thrusterComp)
    {
        if (_thrusterDefField == null) return null;
        try
        {
            var def = _thrusterDefField.GetValue(thrusterComp);
            if (def == null) return null;
            var tc = (object)((ThrusterDefinition)def).ThrustClass;
            return tc?.ToString();
        }
        catch { return null; }
    }

    // ═══════════════════════════════════════════════════════════════
    // Gyroscope detection and control
    // ═══════════════════════════════════════════════════════════════

    private static Type _gyroCompType;
    private static System.Reflection.FieldInfo _gyroDefField;
    private static Type _powerableBlockType;
    private static bool _gyroReflectionResolved;

    private static void EnsureGyroReflectionResolved()
    {
        if (_gyroReflectionResolved) return;
        _gyroReflectionResolved = true;

        try
        {
            // Same shape as the thruster: public type, private _definition field, public
            // GyroscopeDefinition.MaxTorque. PowerableBlockComponent.Enabled is public outright.
            _gyroCompType = typeof(GyroscopeComponent);
            _gyroDefField = _gyroCompType.GetField("_definition",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            _powerableBlockType = typeof(PowerableBlockComponent);

            Log.Default?.Info($"[AERO] GyroscopeComponent def field={_gyroDefField != null}");
        }
        catch (Exception ex)
        {
            Log.Default?.Info($"[AERO] GyroscopeComponent resolution failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Scan grid hierarchy for gyroscope blocks. Same pattern as thruster detection.
    /// </summary>
    public static void RebuildGyroCache(Entity gridEntity, List<GyroInfo> outGyros)
    {
        outGyros.Clear();
        EnsureGyroReflectionResolved();
        if (_gyroCompType == null || gridEntity == null) return;

        var hierarchy = gridEntity.TryGet<HierarchyComponent>();
        if (hierarchy == null || hierarchy.Children == null) return;

        var tag = DefaultTag.Get(_gyroCompType);

        foreach (var child in hierarchy.Children)
        {
            if (child == null) continue;

            var gyroComp = child.TryGet(tag);
            gyroComp ??= child.TryGet<GyroscopeComponent>();
            if (gyroComp == null) continue;

            // Read MaxTorque from definition
            float maxTorque = 0f;
            if (_gyroDefField != null)
            {
                try
                {
                    var def = _gyroDefField.GetValue(gyroComp);
                    if (def != null)
                        maxTorque = ((GyroscopeDefinition)def).MaxTorque;
                }
                catch { }
            }

            // Find the PowerableBlockComponent on the same entity for Enabled toggle
            Component blockComp = null;
            if (_powerableBlockType != null)
            {
                var blockTag = DefaultTag.Get(_powerableBlockType);
                blockComp = child.TryGet(blockTag);
                blockComp ??= child.TryGet<PowerableBlockComponent>();
            }

            outGyros.Add(new GyroInfo
            {
                GyroEntity = child,
                BlockComponent = blockComp,
                MaxTorque = maxTorque,
            });
        }

        if (outGyros.Count > 0)
            Log.Default?.Info($"[AERO] GyroCache: found {outGyros.Count} gyros, totalTorque={outGyros.Sum(g => g.MaxTorque):F0} N·m");
    }

    /// <summary>
    /// Enable or disable all gyros on a grid via PowerableBlockComponent.Enabled.
    /// </summary>
    public static void SetGyrosEnabled(List<GyroInfo> gyros, bool enabled)
    {
        int toggled = 0;
        for (int i = 0; i < gyros.Count; i++)
        {
            if (gyros[i].BlockComponent == null) continue;
            try
            {
                bool current = ((PowerableBlockComponent)gyros[i].BlockComponent).Enabled;
                if (current != enabled)
                {
                    ((PowerableBlockComponent)gyros[i].BlockComponent).Enabled = enabled;
                    toggled++;
                }
            }
            catch { }
        }
        if (toggled > 0)
            Log.Default?.Info($"[AERO] Gyros: {(enabled ? "enabled" : "disabled")} {toggled}/{gyros.Count}");
    }
}
