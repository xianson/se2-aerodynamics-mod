using Aero.Core.Math;

namespace Aero.Core.Components;

/// <summary>
/// Components that replace (not just add to) the normal drag/lift on their block faces.
/// The registry subtracts estimated Newtonian force on owned faces before adding the
/// component's own force. Same pattern as LiftingSurfaceModel subtracting Newtonian
/// pressure on wing faces.
/// </summary>
public interface IFaceOverride
{
    /// <summary>
    /// Cells whose surface faces this component handles.
    /// The registry subtracts estimated Newtonian force on these faces
    /// before adding the component's own force.
    /// </summary>
    IReadOnlyList<Vector3I> OwnedCells { get; }

    /// <summary>
    /// Effective pressure coefficient for heatmap visualization.
    /// Set during Compute() to reflect the replacement force model.
    /// </summary>
    float EffectiveCp { get; }
}
