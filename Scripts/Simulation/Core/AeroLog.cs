#pragma warning disable
namespace AeroMod;

/// <summary>Diagnostic log switches. Verbose: the periodic per-grid / per-wing traces (harness: aeroset AeroLog.Verbose 1) -
/// off by default: with many grids they wrote hundreds of formatted lines a second.</summary>
public static class AeroLog
{
    public static bool Verbose;
}
