#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace AeroMod;

/// <summary>
/// Interface for shadow/visibility computation.
/// Determines which exposed faces are occluded by upstream blocks.
/// </summary>
public interface IShadowMap
{
    /// <summary>Per-face visibility: true = exposed to flow, false = shadowed.</summary>
    List<bool> Visibility { get; }

    /// <summary>Per-face visibility factor [0,1]. 0 = fully shadowed, 1 = fully visible.</summary>
    List<float> VisibilityFactor { get; }

    /// <summary>Number of visible (unshadowed) front-facing faces.</summary>
    int VisibleCount { get; }

    /// <summary>Number of shadowed front-facing faces.</summary>
    int ShadowedCount { get; }

    /// <summary>Evaluate shadow state for all faces against the given flow direction.</summary>
    void Update(IGridAccessor grid, ISurfaceProvider provider, Vector3 flowDirection);
}
