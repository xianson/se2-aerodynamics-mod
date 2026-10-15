#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace AeroMod;

/// <summary>
/// Interface for all aerodynamic drag models.
/// Preferred over abstract class for SE2's DCS (data component system).
/// </summary>
public interface IAeroDragModel
{
    /// <summary>Human-readable model name (for reports).</summary>
    string Name { get; }

    /// <summary>Compute aerodynamic forces and torques for the given context.</summary>
    AeroResult Compute(in AeroContext ctx);
}
