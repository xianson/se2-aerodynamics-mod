#pragma warning disable
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
