#pragma warning disable
using System;
using System.Reflection;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Physicss;
using Keen.VRage.Core;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.ResourceDistribution.Resources;

namespace AeroMod;

/// <summary>
/// Adds dynamic mass to hydrogen tanks based on stored fuel level.
/// Implements IDynamicMassProvider so GridMassComputerComponent includes
/// fuel mass in physics calculations. Makes delta-v and staging meaningful.
///
/// 1 game-liter of hydrogen = 1 kg of combined propellant (H2+O2).
/// </summary>
[WhenSimulated]
public partial class AeroFuelMassComponent : Component, IDynamicMassProvider, IInSceneListener
{
    private const float KgPerLiter = 1.0f;

    private ResourceContainerComponent _tankComponent;
    private static Type _resourceContainerType;
    private static bool _reflectionResolved;

    public float DynamicMass
    {
        get
        {
            if (_tankComponent == null) return 0f;
            try { return (float)(double)_tankComponent.CurrentChargeValue * KgPerLiter; }
            catch { return 0f; }
        }
    }

    void IInSceneListener.OnAddedToScene()
    {
        EnsureReflection();
        if (_resourceContainerType == null) return;

        // Find ResourceContainerComponent on this entity
        _tankComponent = Entity.TryGet<ResourceContainerComponent>();

        if (_tankComponent != null)
        {
            float initial = DynamicMass;
            Log.Default?.Info($"[AERO] FuelMass: tank found, initial mass={initial:F0} kg");
        }
    }

    void IInSceneListener.OnBeforeRemovedFromScene() { }

    private static void EnsureReflection()
    {
        if (_reflectionResolved) return;
        _reflectionResolved = true;

        // ResourceContainerComponent and its CurrentChargeValue are both public.
        _resourceContainerType = typeof(ResourceContainerComponent);
    }
}
