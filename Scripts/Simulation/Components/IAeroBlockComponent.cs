#pragma warning disable
namespace AeroMod;

/// <summary>
/// A block-level aerodynamic component that contributes forces.
/// No SE2 type references — just math types. Keeps components testable.
/// </summary>
public interface IAeroBlockComponent
{
    /// <summary>Action point in grid-local meters.</summary>
    Vector3 Position { get; }

    /// <summary>Grid cell position (for block-keyed removal).</summary>
    Vector3I BlockPosition { get; }

    /// <summary>Compute the aerodynamic force contribution.</summary>
    ComponentForceResult Compute(in LocalAeroConditions conditions);
}
