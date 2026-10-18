#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace AeroMod;

/// <summary>Diagnostic log switches. A player's log carries the startup summary, settings, warnings, failures and structure
/// breaks only; the rest is behind these (2026-10-17 release prep: one 50-minute session wrote ~2000 aero lines, 750 of them
/// a grid entering the scene).</summary>
public static class AeroLog
{
    /// <summary>The periodic per-grid / per-wing traces (harness: aeroset AeroLog.Verbose 1) - off by default: with many
    /// grids they wrote hundreds of formatted lines a second.</summary>
    public static bool Verbose;

    /// <summary>The per-grid / per-build diagnostic lines that used to be on for everyone: a grid entering the scene, each
    /// table build or cache hit, wing merges, control surface geometry, the first sim frames ([AERO-SIM]), the 10 s profile
    /// ([AERO-PROF]). On by default only where the developer's harness has put %TEMP%\OrbitalMod\dev.flag (the same flag as
    /// Orbital's OrbitalConfig.DevHarness); aeroset AeroLog.Detail 1 / 0 any time.</summary>
    public static bool Detail = DevFlagPresent();

    private static bool DevFlagPresent()
    {
        try { return System.IO.File.Exists(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OrbitalMod", "dev.flag")); }
        catch { return false; }
    }
}
