#pragma warning disable
namespace AeroMod;

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
    /// Wing faces show |CL|, airbrakes show Cd×effectiveness, etc.
    /// </summary>
    float EffectiveCp { get; }
}
