#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>The gimbals' cones as the panels, the saves and the API take them (engine-free; ThrustVectorGame flies them): a solid
/// rocket nozzle's thrust vectoring cone (0.5..15 deg, a new nozzle's 5 - off until switched on) and a thruster's gimbal cone
/// (0 none - a fixed thruster - .. 15 deg).</summary>
public static class GimbalLimits
{
    public const float DefaultDeg = 5f, MinDeg = 0.5f, MaxDeg = 15f;
    /// <summary>A nozzle's cone in its range (not finite: the default).</summary>
    public static float Cone(float deg) => float.IsFinite(deg) ? Math.Clamp(deg, MinDeg, MaxDeg) : DefaultDeg;
    /// <summary>A thruster's cone in its range (0 none; not finite: none).</summary>
    public static float Thruster(float deg) => float.IsFinite(deg) ? Math.Clamp(deg, 0f, MaxDeg) : 0f;
}
