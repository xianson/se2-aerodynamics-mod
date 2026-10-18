#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.BlockOctrees;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement;

namespace AeroMod;

/// <summary>The grid's thruster and gyro caches: the block scan, and the one private
/// _definition field each (docs/REFLECTION_AND_WHITELIST.md) for power, direction, class and gyro torque.</summary>
public static partial class OffsetThrustJob
{
    // ═══════════════════════════════════════════════════════════════
    // Thruster cache building
    // ═══════════════════════════════════════════════════════════════

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
                    // a SOLID ROCKET nozzle (2026-10-17): never in the thruster set - no allocation, steering or offset share ever
                    // uses it (ThrustTorque takes its forced thrust out of the game's and adds its own torque: AeroSolidNozzleComponent)
                    if (SolidRocketBlocks.IsNozzle(block)) continue;

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

                    Vector3 thrustDir = Base6Directions.GetVector(directionInt);

                    // Classify by ThrustClass from ThrusterDefinition: a vanilla atmospheric thruster flies the Jet curve
                    // (JetCurve); our propulsor blocks (atmospheric underneath) their own - a fan none by Mach (by airspeed)
                    ThrustProfile profile = null;
                    string thrustClass = GetThrustClass(thrusterComp);
                    bool isAtmo = thrustClass != null && thrustClass.Contains("Atmospheric");
                    var prow = PropulsorBlocks.RowOf(childEntity);
                    bool propulsor = prow != null || childEntity.TryGet<AeroPropulsorComponent>() != null;
                    if (isAtmo)
                        profile = !propulsor ? ThrustProfiles.Jet
                            : prow == null ? null
                            : prow.Kind == AeroPropulsorComponent.Kinds.Turboprop ? ThrustProfiles.Turboprop
                            : prow.Kind == AeroPropulsorComponent.Kinds.Fan ? null
                            : ThrustProfiles.HelicopterRotor;
                    // a RAMJET (2026-10-17): a hydrogen thruster underneath, an air-breather all the same - its curve for the reports
                    // (its own component shapes its maximum: AppliedProfile gives a propulsor none), a flow-through engine box
                    bool ramjet = prow != null && AeroPropulsorComponent.IsRamjet(prow.Kind);
                    if (ramjet) profile = ThrustProfiles.Ramjet;

                    var aeroSettings = childEntity.TryGet<AeroThrustSettingsComponent>();

                    outThrusters.Add(new ThrusterInfo
                    {
                        ThrusterEntity = childEntity,
                        GridLocalPosition = localPos,
                        ThrustDirection = thrustDir,
                        MaxPower = maxPower,
                        Profile = profile,
                        AirBreathing = isAtmo || ramjet,
                        Propulsor = propulsor,
                        Settings = aeroSettings,
                        Cell = aabb.Min + aabb.Max,
                        CellMin = aabb.Min, CellMax = aabb.Max,
                        Rev = childEntity.TryGet<ReversibleThrustComponent>(),
                    });
                }
            }
        }

        int airBreathing = 0;
        for (int i = 0; i < outThrusters.Count; i++)
            if (outThrusters[i].AirBreathing) airBreathing++;

        Log.Default?.Info($"[AERO] OffsetThrust: scanned {totalChildren} hierarchy children, {thrustBlocks} with ThrusterComponent, cached {outThrusters.Count} thrusters ({airBreathing} air-breathing)");
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
    // Gyroscope detection
    // ═══════════════════════════════════════════════════════════════

    private static Type _gyroCompType;
    private static System.Reflection.FieldInfo _gyroDefField;
    private static bool _gyroReflectionResolved;

    private static void EnsureGyroReflectionResolved()
    {
        if (_gyroReflectionResolved) return;
        _gyroReflectionResolved = true;

        try
        {
            // Same shape as the thruster: public type, private _definition field, public
            // GyroscopeDefinition.MaxTorque.
            _gyroCompType = typeof(GyroscopeComponent);
            _gyroDefField = _gyroCompType.GetField("_definition",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

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

            outGyros.Add(new GyroInfo
            {
                GyroEntity = child,
                MaxTorque = maxTorque,
            });
        }

        if (outGyros.Count > 0)
            Log.Default?.Info($"[AERO] GyroCache: found {outGyros.Count} gyros, totalTorque={outGyros.Sum(g => g.MaxTorque):F0} N·m");
    }
}
