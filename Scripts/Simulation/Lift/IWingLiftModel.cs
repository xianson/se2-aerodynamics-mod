#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// Pluggable wing aero model — computes lift + induced drag + interference.
/// Called every frame with the detected wings and current flight conditions.
/// </summary>
public interface IWingLiftModel
{
    List<WingForceResult> ComputeWingForces(ReadOnlySpan<LiftingSurface> wings, in AeroContext ctx);
}
