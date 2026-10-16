#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Globalization;

namespace AeroMod;

/// <summary>
/// DEV (2026-10-15, tools/harness/test_blocksave.sh in the Orbital Mod): the text of the harness's per-block save requests
/// and answers - pure, Tests/BlockSaveTests. The engine side (the server step that applies and dumps) is AeroBlockSaveDev.cs.
/// <list type="bullet">
/// <item>set: "&lt;grid&gt;|&lt;kind&gt;|&lt;which&gt;|&lt;value&gt;" - grid: its name (spaces as '_', as findgrids prints it);
/// kind: surface-trim, surface-max (deg), gear (down|up|1|0), airbrake-max (deg), thrust (attitude fraction 0..1),
/// airshock (on|off|1|0); which: the index among that block kind's blocks on the grid, ordered by cell (x, then y, then z),
/// or the cell itself "x,y,z" (the block's AABB min).</item>
/// <item>dump token, one per block: "&lt;kind&gt;@x,y,z=name:value;name:value..." (kind surface / gear / airbrake / thrust;
/// values invariant culture "R"; rb:1 when the block took its state from its own saved data).</item>
/// </list>
/// (no spaces anywhere: aeroset passes one word)
/// </summary>
public static class AeroBlockSaveDevForm
{
    public enum Kind { SurfaceTrim, SurfaceMax, Gear, AirbrakeMax, Thrust, Airshock }
    static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public struct SetRequest
    {
        public string Grid; public Kind Kind;
        public int Index; public bool ByCell; public int X, Y, Z;
        public float Value; public bool Flag;
    }

    /// <summary>The block kind a request kind acts on (the dump's kind word).</summary>
    public static string BlockKind(Kind k) => k switch
    {
        Kind.SurfaceTrim or Kind.SurfaceMax => "surface",
        Kind.Gear or Kind.Airshock => "gear",
        Kind.AirbrakeMax => "airbrake",
        _ => "thrust",
    };

    /// <summary>A grid name as findgrids prints it (spaces as '_'): true when <paramref name="want"/> names it.</summary>
    public static bool NameMatches(string gridName, string want)
        => gridName != null && want != null && (gridName == want || gridName.Replace(' ', '_') == want);

    static bool Bool(string s, string yes, string no, out bool b)
    {
        b = false;
        if (s == "1" || s.Equals("on", StringComparison.OrdinalIgnoreCase) || s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals(yes, StringComparison.OrdinalIgnoreCase)) { b = true; return true; }
        return s == "0" || s.Equals("off", StringComparison.OrdinalIgnoreCase) || s.Equals("false", StringComparison.OrdinalIgnoreCase) || s.Equals(no, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A set request; false with <paramref name="why"/> when it does not read.</summary>
    public static bool TryParseSet(string text, out SetRequest r, out string why)
    {
        r = default; why = null;
        var w = text?.Split('|');
        if (w == null || w.Length != 4) { why = "want grid|kind|index-or-x,y,z|value"; return false; }
        for (int i = 0; i < 4; i++) w[i] = w[i].Trim();
        if (w[0].Length == 0) { why = "no grid name"; return false; }
        r.Grid = w[0];
        switch (w[1].ToLowerInvariant())
        {
            case "surface-trim": r.Kind = Kind.SurfaceTrim; break;
            case "surface-max": r.Kind = Kind.SurfaceMax; break;
            case "gear": r.Kind = Kind.Gear; break;
            case "airbrake-max": r.Kind = Kind.AirbrakeMax; break;
            case "thrust": r.Kind = Kind.Thrust; break;
            case "airshock": r.Kind = Kind.Airshock; break;
            default: why = $"kind '{w[1]}': surface-trim, surface-max, gear, airbrake-max, thrust, airshock"; return false;
        }
        var cell = w[2].Split(',');
        if (cell.Length == 3)
        {
            if (!int.TryParse(cell[0], NumberStyles.Integer, C, out r.X) || !int.TryParse(cell[1], NumberStyles.Integer, C, out r.Y)
                || !int.TryParse(cell[2], NumberStyles.Integer, C, out r.Z)) { why = $"cell '{w[2]}'"; return false; }
            r.ByCell = true;
        }
        else if (!int.TryParse(w[2], NumberStyles.Integer, C, out r.Index) || r.Index < 0) { why = $"index '{w[2]}'"; return false; }
        switch (r.Kind)
        {
            case Kind.Gear:
                if (!Bool(w[3], "down", "up", out r.Flag)) { why = $"gear '{w[3]}': down|up"; return false; }
                break;
            case Kind.Airshock:
                if (!Bool(w[3], "on", "off", out r.Flag)) { why = $"airshock '{w[3]}': on|off"; return false; }
                break;
            default:
                if (!float.TryParse(w[3], NumberStyles.Float, C, out r.Value) || !float.IsFinite(r.Value)) { why = $"value '{w[3]}'"; return false; }
                if (r.Kind == Kind.Thrust && (r.Value < 0f || r.Value > 1f)) { why = $"thrust fraction {w[3]}: 0..1"; return false; }
                break;
        }
        return true;
    }

    /// <summary>Cell order: x, then y, then z (the index of a set request).</summary>
    public static int CompareCells(int ax, int ay, int az, int bx, int by, int bz)
        => ax != bx ? ax.CompareTo(bx) : ay != by ? ay.CompareTo(by) : az.CompareTo(bz);

    public static string F(float f) => f.ToString("R", C);
    public static string B(bool b) => b ? "1" : "0";

    /// <summary>A dump token's head: "kind@x,y,z=".</summary>
    public static string Head(string kind, int x, int y, int z) => kind + "@" + x.ToString(C) + "," + y.ToString(C) + "," + z.ToString(C) + "=";

    public static string SurfaceToken(int x, int y, int z, float max, float trim, bool p, bool yaw, bool roll, bool rb)
        => Head("surface", x, y, z) + "max:" + F(max) + ";trim:" + F(trim) + ";p:" + B(p) + ";y:" + B(yaw) + ";r:" + B(roll) + ";rb:" + B(rb);
    public static string GearToken(int x, int y, int z, bool down, bool airshock, bool rb)
        => Head("gear", x, y, z) + "down:" + B(down) + ";shock:" + B(airshock) + ";rb:" + B(rb);
    public static string AirbrakeToken(int x, int y, int z, float max, bool rb)
        => Head("airbrake", x, y, z) + "max:" + F(max) + ";rb:" + B(rb);
    public static string ThrustToken(int x, int y, int z, float frac, bool rb)
        => Head("thrust", x, y, z) + "f:" + F(frac) + ";rb:" + B(rb);

    /// <summary>One value of a dump token ("max", "down" ...): null when the token has none.</summary>
    public static string TokenValue(string token, string name)
    {
        int eq = token?.IndexOf('=') ?? -1;
        if (eq < 0) return null;
        foreach (var kv in token.Substring(eq + 1).Split(';'))
        {
            int c = kv.IndexOf(':');
            if (c > 0 && kv.Substring(0, c) == name) return kv.Substring(c + 1);
        }
        return null;
    }
}
