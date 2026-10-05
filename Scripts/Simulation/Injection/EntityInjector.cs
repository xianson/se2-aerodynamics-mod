using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core.Game.Definitions;

#pragma warning disable
namespace AeroMod;

[DefinitionPostProcessor(ForceInstantiation = true)]
public class EntityInjector : SimpleDefinitionPostProcessor<PrefabDefinition>
{
    /// <summary>Inject AeroFlameComponent into client thrusters (off: testing whether its presence alone darkens the game's
    /// thruster glow - reported 2026-10-04 with the flame override already off).</summary>
    public static bool InjectFlames = false;

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
            Add(entity, typeof(AeroThrustSettingsComponent));

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

        if (hasTank)
        {
            Add(entity, typeof(AeroFuelMassComponent));

            if (_tankCount < 3)
                Log.Default?.Info($"[AERO] Injected AeroFuelMassComponent into tank prefab #{_tankCount}");
            _tankCount++;
        }
    }
}
