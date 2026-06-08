#pragma warning disable
using System;
using System.Reflection;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Physicss;
using Keen.VRage.Core;

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

    private Component _tankComponent;
    private static PropertyInfo _currentChargeProp;
    private static Type _resourceContainerType;
    private static bool _reflectionResolved;

    public float DynamicMass
    {
        get
        {
            if (_tankComponent == null || _currentChargeProp == null) return 0f;
            try
            {
                object val = _currentChargeProp.GetValue(_tankComponent);
                return (float)System.Convert.ToDouble(val) * KgPerLiter;
            }
            catch { return 0f; }
        }
    }

    void IInSceneListener.OnAddedToScene()
    {
        EnsureReflection();
        if (_resourceContainerType == null) return;

        // Find ResourceContainerComponent on this entity
        var tag = DefaultTag.Get(_resourceContainerType);
        _tankComponent = Entity.TryGet(tag);
        _tankComponent ??= PhysicsHack.FindComponentByType(Entity, _resourceContainerType);

        if (_tankComponent != null && _currentChargeProp != null)
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

        _resourceContainerType = Type.GetType(
            "Keen.Game2.Simulation.WorldObjects.CubeBlocks.ResourceDistribution.Resources.ResourceContainerComponent, Game2.Simulation",
            throwOnError: false);

        if (_resourceContainerType != null)
        {
            _currentChargeProp = _resourceContainerType.GetProperty("CurrentChargeValue",
                BindingFlags.Public | BindingFlags.Instance);
        }

        Log.Default?.Info($"[AERO] FuelMass reflection: container={_resourceContainerType != null} charge={_currentChargeProp != null}");
    }
}
