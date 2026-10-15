using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core.Game.Definitions;

#pragma warning disable
namespace AeroMod;

[DefinitionPostProcessor(ForceInstantiation = true)]
public class EntityInjector : SimpleDefinitionPostProcessor<PrefabDefinition>
{
    /// <summary>Inject AeroFlameComponent into client thrusters: each thruster's flame shows its share of the thrust,
    /// attitude included (ThrustTorque.FlamesEnabled). (Off 2026-10-04 while the shares darkened the game's flames: those
    /// were x efficiency twice - capacities are now x efficiency - and dropped whenever the controller stopped steering.)</summary>
    public static bool InjectFlames = true;

    public override void PostProcess(PrefabDefinition definition)
    {
        InjectAeroComponents.Please(definition);
    }
}

public class InjectAeroComponents : Injections
{
    private static int _gridCount;
    private static int _thrusterCount;
    private static int _tankCount;
    private static int _flameCount;

    public static void Please(PrefabDefinition entity)
    {
        var composition = entity.Composition;
        bool hasCubeGrid = false;
        bool hasThruster = false;
        bool hasTank = false;
        bool hasFlame = false;   // a client thruster (it draws flames)
        bool isServerPlanet = false;   // (carries the world's aero settings in the save)

        foreach (var type in composition.Types)
        {
            if (type == typeof(CubeGridComponent))
                hasCubeGrid = true;
            if (type.Name == "ThrusterComponent")
                hasThruster = true;
            if (type.Name == "ResourceContainerComponent")
                hasTank = true;
            if (type.Name == "ThrusterEffectsComponent")
                hasFlame = true;
            if (type == typeof(Keen.Game2.Simulation.GameSystems.Discoveries.Discoverables.DiscoverablePlanetComponent))
                isServerPlanet = true;
        }

        if (isServerPlanet)
        {
            // (a fixed component id and an object builder: the settings are saved with it, so the id must be the same
            //  every load - not derived from the composition size, which depends on what other mods inject)
            Add(entity, new Keen.VRage.DCS.Builders.EntityBuilder.ComponentBuildInfo
            {
                Type = typeof(AeroWorldSettingsComponent),
                ComponentId = new System.Guid("ae205e77-1a65-4c0d-9b1e-0000000000a5"),
                ObjectBuilder = new Keen.Game2.Simulation.GameSystems.EntityNameSessionComponentObjectBuilder(),
            });
            Log.Default?.Info($"[AERO] Injected AeroWorldSettingsComponent into server planet prefab '{entity.DebugName}'");
        }

        if (hasCubeGrid)
        {
            Add(entity, typeof(AeroGridComponent));
            Add(entity, typeof(AeroEntryFxComponent));

            if (_gridCount < 3)
                Log.Default?.Info($"[AERO] Injected AeroGridComponent into prefab #{_gridCount}");
            _gridCount++;
        }

        if (hasThruster)
        {
            // (2026-10-15: a fixed id and the engine's builder - its attitude fraction is saved with the block, AeroBlockSave)
            Add(entity, AeroBlockSave.Info(typeof(AeroThrustSettingsComponent), AeroBlockSave.ThrustId));

            if (_thrusterCount < 3)
                Log.Default?.Info($"[AERO] Injected AeroThrustSettingsComponent into thruster prefab #{_thrusterCount}");
            _thrusterCount++;
        }

        if (hasFlame && EntityInjector.InjectFlames)
        {
            Add(entity, typeof(AeroFlameComponent));
            if (_flameCount < 3)
                Log.Default?.Info($"[AERO] Injected AeroFlameComponent into client thruster prefab #{_flameCount}");
            _flameCount++;
        }

        // the 3x3x1 control surfaces' client prefabs: their flap subpart turned by the server's deflection
        if (AeroBlockGuids.FlapClientPrefabs.Contains(entity.Guid))
        {
            Add(entity, typeof(AeroFlapComponent));
            Log.Default?.Info($"[AERO] Injected AeroFlapComponent into control surface client prefab '{entity.DebugName}'");
        }

        // their server prefabs: the terminal's settings (axes, max deflection, trim), read by the grid's control
        if (AeroBlockGuids.SurfaceServerPrefabs.Contains(entity.Guid))
        {
            Add(entity, AeroBlockSave.Info(typeof(AeroSurfaceSettingsComponent), AeroBlockSave.SurfaceId));   // (saved with the block)
            Log.Default?.Info($"[AERO] Injected AeroSurfaceSettingsComponent into control surface server prefab '{entity.DebugName}'");
        }

        // the retractable gears: the server drives the gear (its vanilla wheel made and unmade), the client animates it
        if (AeroBlockGuids.GearServerPrefabs.Contains(entity.Guid))
        {
            Add(entity, AeroBlockSave.Info(typeof(AeroGearComponent), AeroBlockSave.GearId));   // (saved with the block)
            Log.Default?.Info($"[AERO] Injected AeroGearComponent into gear server prefab '{entity.DebugName}'");
        }
        // the 3x3x3 airbrakes: the server opens them on the parking signal, the client turns their leaves
        if (AeroBlockGuids.AirbrakeServerPrefabs.Contains(entity.Guid))
        {
            Add(entity, AeroBlockSave.Info(typeof(AeroAirbrakeComponent), AeroBlockSave.AirbrakeId));   // (saved with the block)
            Log.Default?.Info($"[AERO] Injected AeroAirbrakeComponent into airbrake server prefab '{entity.DebugName}'");
        }
        if (AeroBlockGuids.AirbrakeClientPrefabs.Contains(entity.Guid))
        {
            Add(entity, typeof(AeroAirbrakeAnimComponent));
            Log.Default?.Info($"[AERO] Injected AeroAirbrakeAnimComponent into airbrake client prefab '{entity.DebugName}'");
        }
        if (AeroBlockGuids.GearClientPrefabs.Contains(entity.Guid))
        {
            Add(entity, typeof(AeroGearAnimComponent));
            Log.Default?.Info($"[AERO] Injected AeroGearAnimComponent into gear client prefab '{entity.DebugName}'");
        }

        if (hasTank)
        {
            Add(entity, typeof(AeroFuelMassComponent));

            if (_tankCount < 3)
                Log.Default?.Info($"[AERO] Injected AeroFuelMassComponent into tank prefab #{_tankCount}");
            _tankCount++;
        }
    }
}
