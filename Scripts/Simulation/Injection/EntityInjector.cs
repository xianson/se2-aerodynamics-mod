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
    private static int _count;

    public static void Please(PrefabDefinition entity)
    {
        var composition = entity.Composition;
        bool hasCubeGrid = false;
        foreach (var type in composition.Types)
        {
            if (type == typeof(CubeGridComponent))
            {
                hasCubeGrid = true;
                break;
            }
        }

        if (hasCubeGrid)
        {
            Add(entity, typeof(AeroGridComponent));

            if (_count < 3)
            {
                Log.Default?.Info($"[AERO] Injected AeroGridComponent into prefab #{_count}");
            }

            _count++;
        }
    }
}
