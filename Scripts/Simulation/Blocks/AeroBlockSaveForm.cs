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
/// <item>solid rocket nozzle: "aero-solidnozzle:1 lit level" (1; written only while it burns; level: its power level 0.5..1,
/// latched at ignition - a key without one reads 1)</item>
/// <item>rotor / prop blades (rotor strikes, 2026-10-17): "aero-blades:1 health" (0..1, 0 = broken; written only below 1 - whole
/// blades carry no key)</item>
/// <item>autopilot (2026-10-19): "aero-autopilot:1 mode height ref speed protection g aoa floor lookAhead unattended" + the flight
/// pilot's values appended (style, holds, route, behaviour, target, aim, return point; the target filter, the Enemy toggle and
/// the grid's owner before it; Autopilot)</item>
/// </list>
/// A later format may append values: a reader takes the ones it knows from any format &gt;= 1.
/// Also the reader of the OLD form (<see cref="ParseLegacy"/>): the "surface / gear / airbrake / thrust x y z ..." lines after
/// the world's settings text, matched to blocks by world position (AeroSurfaceSaved). Pure: Tests/BlockSaveTests.
/// </summary>
public static class AeroBlockSaveForm
{
    public const int Format = 1;
    /// <summary>The autopilot key's own format (2026-10-19: 2 - the combat slots' and the formation's values appended, 47..67). Every
    /// reader takes any format >= 1 and the values it knows: a format-1 key reads the new ones as their defaults, an older mod
    /// reads a format-2 key's first 47.</summary>
    public const int AutopilotFormat = 2;
    public const string SurfaceKey = "aero-surface:", GearKey = "aero-gear:", AirbrakeKey = "aero-airbrake:", ThrustKey = "aero-thrust:";
    public const string SolidNozzleKey = "aero-solidnozzle:", BladesKey = "aero-blades:", AutopilotKey = "aero-autopilot:";
    /// <summary>The Enemy switch's owner before, one per grid of the ship (2026-10-19): "aero-enemyprev:1 index owner", its value
    /// THAT grid (an entity reference the save resolves - each grid is a bundle root).</summary>
    public const string EnemyPrevKey = "aero-enemyprev:";
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

    /// <summary>A solid rocket nozzle that is burning, and its power level (null when not burning: the default).</summary>
    public static string SolidNozzle(bool lit, float level = 1f) => lit ? SolidNozzleKey + Format + " 1 " + F(SolidRocket.LevelOr1(level)) : null;

    public static bool TryReadSolidNozzle(string key, out bool lit) => TryReadSolidNozzle(key, out lit, out _);

    /// <summary>... and its power level (0.5..1; 1 when the key has none - saved before levels, 2026-10-17).</summary>
    public static bool TryReadSolidNozzle(string key, out bool lit, out float level)
    {
        lit = false; level = 1f;
        string[] w = Values(key, SolidNozzleKey, 1);
        if (w == null) return false;
        lit = w[1] == "1";
        if (w.Length > 2 && Float(w[2], out float l)) level = SolidRocket.LevelOr1(l);
        return true;
    }

    /// <summary>A rotor's or prop's blades' health after strikes (0..1, 0 broken; RotorStrike). Null when whole (the default).</summary>
    public static string Blades(float health) => float.IsFinite(health) && health < 1f ? BladesKey + Format + " " + F(Math.Clamp(health, 0f, 1f)) : null;

    public static bool TryReadBlades(string key, out float health)
    {
        health = 1f;
        string[] w = Values(key, BladesKey, 1);
        if (w == null || !Float(w[1], out float h)) return false;
        health = Math.Clamp(h, 0f, 1f);
        return true;
    }

    /// <summary>An Autopilot block's panel (2026-10-19): "aero-autopilot:1 mode height ref speed protection g aoa floor lookAhead
    /// unattended" (mode 0 off / 1 hover hold / 2 height hold; ref 0 over the ground / 1 altitude; m, m/s, g, deg, m; switches
    /// 1/0). Always written (the block's mode survives a save: an unattended hover holds on after a load).</summary>
    public static string Autopilot(in ApPanel p)
        => AutopilotKey + AutopilotFormat + " " + ((int)p.Mode).ToString(C) + " " + F(p.HeightM) + " " + ((int)p.HeightRef).ToString(C) + " " + F(p.HoverSpeed)
           + " " + B(p.Protection) + " " + F(p.GLimit) + " " + F(p.AoaMarginDeg) + " " + F(p.FloorM) + " " + B(p.LookAhead) + " " + B(p.Unattended)
           // (2026-10-19, appended: the flight pilot - 11 style, 12..19 holds, 20..22 route, 23..30 behaviour, 31 target source, 32..34
           //  aim, 35..39 the return point, 40 route text, 41 target text - texts escaped: no spaces)
           + " " + ((int)p.Style).ToString(C) + " " + F(p.HeadingDeg) + " " + B(p.HeadingOn) + " " + F(p.AltM) + " " + B(p.AltOn) + " " + F(p.VsMs)
           + " " + F(p.SpeedMs) + " " + B(p.SpeedOn) + " " + F(p.BankDeg) + " " + B(p.RouteLoop) + " " + F(p.LoiterM) + " " + F(p.MinAglM)
           + " " + ((int)p.Behaviour).ToString(C) + " " + F(p.RangeM) + " " + F(p.AbortM) + " " + F(p.RerunS) + " " + F(p.AngleDeg) + " " + F(p.AltOffsetM)
           + " " + F(p.Aggression) + " " + B(p.Evasive) + " " + ((int)p.TargetSrc).ToString(C) + " " + B(p.AimOn) + " " + F(p.MuzzleSpeed) + " " + F(p.ConeDeg)
           + " " + B(p.HasHome) + " " + p.HomeX.ToString("R", C) + " " + p.HomeY.ToString("R", C) + " " + p.HomeZ.ToString("R", C) + " " + B(p.ReturnHome)
           + " " + Esc(p.Route) + " " + Esc(p.TargetText)
           // (2026-10-19, appended: 42 the target filter, 43 the enemy toggle, 44 the grid's owner before it - an identity's number)
           + " " + ((int)p.TargetFilter).ToString(C) + " " + B(p.Enemy) + " " + p.EnemyPrevOwner.ToString(C)
           // (45 the collision avoidance on, 46 its look-ahead s)
           + " " + B(p.AvoidOn) + " " + F(p.AvoidS)
           // (format 2, 2026-10-19: 47..54 the dogfight slot - fight, head-on, hard deck, ceiling, bingo fuel / damage, guns, skill; 55..61
           //  the rocket slot - law, nav constant, accel gain, lateral g, gravity, arm, timeout; 62..65 the formation; 66, 67 the slots' state)
           + " " + ((int)p.DfFight).ToString(C) + " " + B(p.DfHeadOn) + " " + F(p.DeckM) + " " + F(p.CeilingM) + " " + F(p.DfBingoFuelPct) + " " + F(p.DfBingoDamagePct)
           + " " + F(p.DfRespectGuns) + " " + F(p.DfSkill)
           + " " + ((int)p.RgLaw).ToString(C) + " " + F(p.RgNavConst) + " " + F(p.RgTargetAccelGain) + " " + F(p.RgMaxLatG) + " " + B(p.RgGravityComp)
           + " " + F(p.RgArmM) + " " + F(p.RgTimeoutS)
           + " " + F(p.FmRightM) + " " + F(p.FmUpM) + " " + F(p.FmBackM) + " " + F(p.FmCatchUpMs)
           + " " + Esc(p.DfSaved) + " " + Esc(p.RgSaved);

    /// <summary>One grid's owner before the Enemy switch (index: the entry's number - keys of one map must differ).</summary>
    public static string EnemyPrev(int index, long owner) => EnemyPrevKey + Format + " " + index.ToString(C) + " " + owner.ToString(C);

    public static bool TryReadEnemyPrev(string key, out long owner)
    {
        owner = 0;
        string[] w = Values(key, EnemyPrevKey, 2);
        return w != null && long.TryParse(w[2], NumberStyles.Integer, C, out owner);
    }

    /// <summary>A text as one space-free value: "s" then the text with '%', spaces, tabs and line breaks as %25 %20 %09 %0A %0D.</summary>
    public static string Esc(string t)
    {
        if (string.IsNullOrEmpty(t)) return "s";
        var sb = new System.Text.StringBuilder("s", t.Length + 8);
        foreach (char ch in t)
            sb.Append(ch switch { '%' => "%25", ' ' => "%20", '\t' => "%09", '\n' => "%0A", '\r' => "%0D", _ => ch.ToString() });
        return sb.ToString();
    }

    /// <summary>Esc's text back (null: not an escaped value).</summary>
    public static string Unesc(string v)
    {
        if (v == null || v.Length == 0 || v[0] != 's') return null;
        var sb = new System.Text.StringBuilder(v.Length);
        for (int i = 1; i < v.Length; i++)
        {
            if (v[i] == '%' && i + 2 < v.Length && int.TryParse(v.Substring(i + 1, 2), NumberStyles.HexNumber, C, out int code)) { sb.Append((char)code); i += 2; }
            else sb.Append(v[i]);
        }
        return sb.ToString();
    }

    /// <summary>Read it (false: not this key - the defaults out). Values missing at the end keep the defaults (a later format
    /// appends), each one clamped to its range, an unknown mode or reference read as off / over the ground.</summary>
    public static bool TryReadAutopilot(string key, out ApPanel p)
    {
        p = ApPanel.Defaults;
        string[] w = Values(key, AutopilotKey, 1);
        if (w == null) return false;
        int I(int i, int def) => i < w.Length && int.TryParse(w[i], NumberStyles.Integer, C, out int v) ? v : def;
        float Fl(int i, float def) => i < w.Length && Float(w[i], out float v) ? v : def;
        bool Bo(int i, bool def) => i < w.Length ? w[i] == "1" || (w[i] != "0" && def) : def;
        int mode = I(1, 0), href = I(3, (int)p.HeightRef);
        p.Mode = mode >= 0 && mode <= (int)ApMode.Engage ? (ApMode)mode : ApMode.Off;
        p.HeightM = Fl(2, p.HeightM);
        p.HeightRef = href == 1 ? ApHeightRef.Inertial : ApHeightRef.Ground;
        p.HoverSpeed = Fl(4, p.HoverSpeed); p.Protection = Bo(5, p.Protection); p.GLimit = Fl(6, p.GLimit);
        p.AoaMarginDeg = Fl(7, p.AoaMarginDeg); p.FloorM = Fl(8, p.FloorM); p.LookAhead = Bo(9, p.LookAhead); p.Unattended = Bo(10, p.Unattended);
        // (the flight pilot's, appended 2026-10-19: absent in an older key - the defaults kept)
        p.Style = (ApStyle)I(11, (int)p.Style); p.HeadingDeg = Fl(12, p.HeadingDeg); p.HeadingOn = Bo(13, p.HeadingOn); p.AltM = Fl(14, p.AltM);
        p.AltOn = Bo(15, p.AltOn); p.VsMs = Fl(16, p.VsMs); p.SpeedMs = Fl(17, p.SpeedMs); p.SpeedOn = Bo(18, p.SpeedOn); p.BankDeg = Fl(19, p.BankDeg);
        p.RouteLoop = Bo(20, p.RouteLoop); p.LoiterM = Fl(21, p.LoiterM); p.MinAglM = Fl(22, p.MinAglM);
        p.Behaviour = (ApBehaviour)I(23, (int)p.Behaviour); p.RangeM = Fl(24, p.RangeM); p.AbortM = Fl(25, p.AbortM); p.RerunS = Fl(26, p.RerunS);
        p.AngleDeg = Fl(27, p.AngleDeg); p.AltOffsetM = Fl(28, p.AltOffsetM); p.Aggression = Fl(29, p.Aggression); p.Evasive = Bo(30, p.Evasive);
        p.TargetSrc = (ApTargetSrc)I(31, (int)p.TargetSrc); p.AimOn = Bo(32, p.AimOn); p.MuzzleSpeed = Fl(33, p.MuzzleSpeed); p.ConeDeg = Fl(34, p.ConeDeg);
        p.HasHome = Bo(35, p.HasHome);
        double Db(int i, double def) => i < w.Length && double.TryParse(w[i], NumberStyles.Float, C, out double v) && double.IsFinite(v) ? v : def;
        p.HomeX = Db(36, p.HomeX); p.HomeY = Db(37, p.HomeY); p.HomeZ = Db(38, p.HomeZ); p.ReturnHome = Bo(39, p.ReturnHome);
        p.Route = (40 < w.Length ? Unesc(w[40]) : null) ?? p.Route; p.TargetText = (41 < w.Length ? Unesc(w[41]) : null) ?? p.TargetText;
        p.TargetFilter = (ApTargetFilter)I(42, (int)p.TargetFilter); p.Enemy = Bo(43, p.Enemy);
        p.EnemyPrevOwner = 44 < w.Length && long.TryParse(w[44], NumberStyles.Integer, C, out long prev) ? prev : p.EnemyPrevOwner;
        p.AvoidOn = Bo(45, p.AvoidOn); p.AvoidS = Fl(46, p.AvoidS);
        // (format 2: the combat slots and the formation - absent in a format-1 key: the defaults kept)
        p.DfFight = (ApFightPref)I(47, (int)p.DfFight); p.DfHeadOn = Bo(48, p.DfHeadOn); p.DeckM = Fl(49, p.DeckM); p.CeilingM = Fl(50, p.CeilingM);
        p.DfBingoFuelPct = Fl(51, p.DfBingoFuelPct); p.DfBingoDamagePct = Fl(52, p.DfBingoDamagePct); p.DfRespectGuns = Fl(53, p.DfRespectGuns); p.DfSkill = Fl(54, p.DfSkill);
        p.RgLaw = (ApGuidanceLaw)I(55, (int)p.RgLaw); p.RgNavConst = Fl(56, p.RgNavConst); p.RgTargetAccelGain = Fl(57, p.RgTargetAccelGain); p.RgMaxLatG = Fl(58, p.RgMaxLatG);
        p.RgGravityComp = Bo(59, p.RgGravityComp); p.RgArmM = Fl(60, p.RgArmM); p.RgTimeoutS = Fl(61, p.RgTimeoutS);
        p.FmRightM = Fl(62, p.FmRightM); p.FmUpM = Fl(63, p.FmUpM); p.FmBackM = Fl(64, p.FmBackM); p.FmCatchUpMs = Fl(65, p.FmCatchUpMs);
        p.DfSaved = (66 < w.Length ? Unesc(w[66]) : null) ?? p.DfSaved; p.RgSaved = (67 < w.Length ? Unesc(w[67]) : null) ?? p.RgSaved;

        p = p.Clamped();
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
