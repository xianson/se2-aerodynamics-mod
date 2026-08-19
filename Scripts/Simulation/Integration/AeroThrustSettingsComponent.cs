#pragma warning disable
using Keen.VRage.Core;

namespace AeroMod;

/// <summary>
/// Per-thruster attitude authority setting. Injected onto thruster entities
/// by EntityInjector. Stores the fraction of thrust capacity reserved for
/// attitude control (0..1, default 0.5 = 50/50 split).
///
/// Value is read by OffsetThrustJob each physics frame.
/// Value is set via the terminal slider (AeroThrustBlockDetailModel).
/// </summary>
[WhenSimulated]
public partial class AeroThrustSettingsComponent : Component, IInSceneListener
{
    /// <summary>
    /// Fraction of this thruster's capacity reserved for attitude control.
    /// 0 = all translation, 1 = all attitude, 0.5 = 50/50 split.
    /// </summary>
    public float AttitudeFraction = 0.5f;

    void IInSceneListener.OnAddedToScene()
    {
        // Nothing to do: BlockDetailProviderIndexer discovers [BlockDetailProvider] types in mod
        // assemblies on its own since 2.4.0.77, so AeroThrustBlockDetailModel self-registers.
    }

    void IInSceneListener.OnBeforeRemovedFromScene() { }
}
