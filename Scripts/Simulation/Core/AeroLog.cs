#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace AeroMod;

/// <summary>Diagnostic log switches. Verbose: the periodic per-grid / per-wing traces (harness: aeroset AeroLog.Verbose 1) -
/// off by default: with many grids they wrote hundreds of formatted lines a second.</summary>
public static class AeroLog
{
    public static bool Verbose;
}
