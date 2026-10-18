#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// Compressible flow: the wave-drag multiplier the force table's transonic / supersonic drag uses (ForceTable). (The
/// isentropic, shock, Prandtl-Meyer and Newtonian relations once here were used by nothing and went 2026-10-17.)
/// </summary>
public static class CompressibleFlow
{
    /// <summary>Approximate Cd multiplier vs Mach: 1 up to the critical Mach, a cosine rise to the peak at peakMach, then
    /// falling as (peakMach / M)^fallExponent (1.3: a blunt body's slow fall; a slender hull's is much faster - the hull's
    /// law, ForceTable.WaveFall).</summary>
    public static double WaveDragMultiplier(double mach, double criticalMach = 0.7, double peakMultiplier = 2.5, double peakMach = 1.05, double fallExponent = 1.3)
    {
        if (mach <= criticalMach) return 1.0;
        if (peakMach <= criticalMach) peakMach = criticalMach + 1e-3;

        if (mach <= peakMach)
        {
            double t = (mach - criticalMach) / (peakMach - criticalMach);
            double smooth = 0.5 * (1.0 - Math.Cos(Math.PI * t));
            return 1.0 + (peakMultiplier - 1.0) * smooth;
        }

        double excess = peakMultiplier - 1.0;
        double ratio = peakMach / mach;
        return 1.0 + excess * Math.Pow(ratio, fallExponent);
    }
}
