#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Globalization;

namespace AeroMod;

/// <summary>
/// The text each aero block keeps in its OWN saved data (2026-10-15; AeroBlockSave.cs has the components' side): one key
/// of the engine's EntityNameSessionComponentObjectBuilder (a string -&gt; Entity map, the value the block itself), so a
/// save names only engine types and a world still loads with the mod removed (a mod-defined builder type would not:
/// the binary reader throws on the missing type). The key is "&lt;tag&gt;&lt;format&gt; &lt;values&gt;", invariant culture:
/// <list type="bullet">
/// <item>control surface: "aero-surface:1 pitch yaw roll max trim" (1/0 1/0 1/0 deg deg)</item>
/// <item>gear: "aero-gear:1 down airshock" (1/0 1/0)</item>
/// <item>airbrake: "aero-airbrake:1 maxOpening" (deg)</item>
/// <item>thruster: "aero-thrust:1 attitudeFraction" (0..1; written only off the default 0.5)</item>
/// </list>
/// A later format may append values: a reader takes the ones it knows from any format &gt;= 1.
/// Also the reader of the OLD form (<see cref="ParseLegacy"/>): the "surface / gear / airbrake / thrust x y z ..." lines after
/// the world's settings text, matched to blocks by world position (AeroSurfaceSaved). Pure: Tests/BlockSaveTests.
/// </summary>
public static class AeroBlockSaveForm
{
    public const int Format = 1;
    public const string SurfaceKey = "aero-surface:", GearKey = "aero-gear:", AirbrakeKey = "aero-airbrake:", ThrustKey = "aero-thrust:";
    static readonly CultureInfo C = CultureInfo.InvariantCulture;

    static string B(bool b) => b ? "1" : "0";
    static string F(float f) => f.ToString("R", C);
    static bool Float(string s, out float f) => float.TryParse(s, NumberStyles.Float, C, out f) && float.IsFinite(f);

    /// <summary>The key's values after its tag and format (null: not this tag, an unreadable format, or fewer than
    /// <paramref name="need"/> values).</summary>
    static string[] Values(string key, string tag, int need)
    {
        if (key == null || !key.StartsWith(tag, StringComparison.Ordinal)) return null;
        string[] w = key.Substring(tag.Length).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (w.Length < need + 1 || !int.TryParse(w[0], NumberStyles.Integer, C, out int format) || format < 1) return null;
        return w;
    }

    /// <summary>The first key with this tag in a saved map (null: none - a block saved before 2026-10-15, or a new one).</summary>
    public static string Find<T>(Dictionary<string, T> map, string tag)
    {
        if (map == null) return null;
        foreach (var kv in map) if (kv.Key != null && kv.Key.StartsWith(tag, StringComparison.Ordinal)) return kv.Key;
        return null;
    }

    public static string Surface(bool pitch, bool yaw, bool roll, float max, float trim)
        => SurfaceKey + Format + " " + B(pitch) + " " + B(yaw) + " " + B(roll) + " " + F(max) + " " + F(trim);

    public static bool TryReadSurface(string key, out bool pitch, out bool yaw, out bool roll, out float max, out float trim)
    {
        pitch = yaw = roll = true; max = trim = 0f;
        string[] w = Values(key, SurfaceKey, 5);
        if (w == null || !Float(w[4], out max) || !Float(w[5], out trim)) return false;
        pitch = w[1] == "1"; yaw = w[2] == "1"; roll = w[3] == "1";
        return true;
    }

    public static string Gear(bool down, bool airshock) => GearKey + Format + " " + B(down) + " " + B(airshock);

    public static bool TryReadGear(string key, out bool down, out bool airshock)
    {
        down = airshock = true;
        string[] w = Values(key, GearKey, 2);
        if (w == null) return false;
        down = w[1] == "1"; airshock = w[2] == "1";
        return true;
    }

    public static string Airbrake(float maxOpening) => AirbrakeKey + Format + " " + F(maxOpening);

    public static bool TryReadAirbrake(string key, out float maxOpening)
    {
        maxOpening = 0f;
        string[] w = Values(key, AirbrakeKey, 1);
        return w != null && Float(w[1], out maxOpening);
    }

    public static string Thrust(float attitudeFraction) => ThrustKey + Format + " " + F(attitudeFraction);

    public static bool TryReadThrust(string key, out float attitudeFraction)
    {
        attitudeFraction = 0f;
        string[] w = Values(key, ThrustKey, 1);
        if (w == null || !Float(w[1], out attitudeFraction)) return false;
        attitudeFraction = Math.Clamp(attitudeFraction, 0f, 1f);
        return true;
    }

    // ── the OLD form (read for one release: see AeroSurfaceSaved) ──

    public const string LegacySurfaceTag = "surface ", LegacyGearTag = "gear ", LegacyBrakeTag = "airbrake ", LegacyThrustTag = "thrust ";

    /// <summary>The old position-matched lines of a world's settings text into the lists (appended; since = -1: not yet
    /// waited for). Returns how many were read. (Moved here unchanged from AeroSurfaceSaved.Load, 2026-10-15.)</summary>
    public static int ParseLegacy(string text,
        List<(Vector3D at, bool p, bool y, bool r, float max, float trim, double since)> surfaces,
        List<(Vector3D at, bool down, double since)> gears,
        List<(Vector3D at, float max, double since)> brakes,
        List<(Vector3D at, float frac, double since)> thrusts)
    {
        int n = 0;
        if (string.IsNullOrEmpty(text)) return 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(LegacyThrustTag))
            {
                var tw = line.Substring(LegacyThrustTag.Length).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tw.Length >= 4 && double.TryParse(tw[0], NumberStyles.Float, C, out double tx)
                    && double.TryParse(tw[1], NumberStyles.Float, C, out double ty)
                    && double.TryParse(tw[2], NumberStyles.Float, C, out double tz)
                    && float.TryParse(tw[3], NumberStyles.Float, C, out float tf) && float.IsFinite(tf))
                { thrusts.Add((new Vector3D(tx, ty, tz), Math.Clamp(tf, 0f, 1f), -1)); n++; }
                continue;
            }
            if (line.StartsWith(LegacyBrakeTag))
            {
                var bw = line.Substring(LegacyBrakeTag.Length).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (bw.Length >= 4 && double.TryParse(bw[0], NumberStyles.Float, C, out double bx)
                    && double.TryParse(bw[1], NumberStyles.Float, C, out double by)
                    && double.TryParse(bw[2], NumberStyles.Float, C, out double bz)
                    && float.TryParse(bw[3], NumberStyles.Float, C, out float bm))
                { brakes.Add((new Vector3D(bx, by, bz), bm, -1)); n++; }
                continue;
            }
            if (line.StartsWith(LegacyGearTag))
            {
                var gw = line.Substring(LegacyGearTag.Length).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (gw.Length >= 4 && double.TryParse(gw[0], NumberStyles.Float, C, out double gx)
                    && double.TryParse(gw[1], NumberStyles.Float, C, out double gy)
                    && double.TryParse(gw[2], NumberStyles.Float, C, out double gz))
                { gears.Add((new Vector3D(gx, gy, gz), gw[3] == "1", -1)); n++; }
                continue;
            }
            if (!line.StartsWith(LegacySurfaceTag)) continue;
            var w = line.Substring(LegacySurfaceTag.Length).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (w.Length < 8 || !double.TryParse(w[0], NumberStyles.Float, C, out double x)
                || !double.TryParse(w[1], NumberStyles.Float, C, out double y)
                || !double.TryParse(w[2], NumberStyles.Float, C, out double z)
                || !float.TryParse(w[6], NumberStyles.Float, C, out float max)
                || !float.TryParse(w[7], NumberStyles.Float, C, out float trim)) continue;
            surfaces.Add((new Vector3D(x, y, z), w[3] == "1", w[4] == "1", w[5] == "1", max, trim, -1));
            n++;
        }
        return n;
    }
}
