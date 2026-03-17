#pragma warning disable
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
