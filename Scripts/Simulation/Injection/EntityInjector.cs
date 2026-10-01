using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core.Game.Definitions;

#pragma warning disable
namespace AeroMod;

[DefinitionPostProcessor(ForceInstantiation = true)]
public class EntityInjector : SimpleDefinitionPostProcessor<PrefabDefinition>
{
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
        }

        if (hasCubeGrid)
        {
            Add(entity, typeof(AeroGridComponent));

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

        if (hasFlame)
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
