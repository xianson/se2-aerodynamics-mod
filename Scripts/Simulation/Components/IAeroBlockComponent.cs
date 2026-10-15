#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
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
