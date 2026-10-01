#pragma warning disable
using System.IO;

namespace AeroMod;

/// <summary>
/// Test aid (harness: aeroexport on|off): while on, every background build writes its grid's shape - one block's
/// occupied-cell box per line, the game's own - to %TEMP%\AeroMod\name.boxes, once per grid. Tests/AeroBench runs the
/// aero code on those offline: real ships, exactly as the game sees them.
/// </summary>
public static class AeroExport
{
    public static bool Enabled = false;
    static readonly HashSet<string> _done = new();

    public static void Write(string name, List<(Vector3I, Vector3I)> boxes, float blockSize, float mass)
    {
        if (!Enabled || boxes.Count == 0) return;
        lock (_done) if (!_done.Add(name)) return;
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "AeroMod");
            Directory.CreateDirectory(dir);
            var sb = new System.Text.StringBuilder();
            sb.Append("# blockSize ").Append(blockSize.ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append(" mass ").Append(mass.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            foreach (var (a, b) in boxes) sb.Append(a.X).Append(' ').Append(a.Y).Append(' ').Append(a.Z).Append(' ').Append(b.X).Append(' ').Append(b.Y).Append(' ').Append(b.Z).Append('\n');
            string safe = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
            File.WriteAllText(Path.Combine(dir, safe + ".boxes"), sb.ToString());
            Log.Default?.Info($"[AERO] exported {boxes.Count} blocks of '{name}' to {dir}");
        }
        catch (System.Exception e) { Log.Default?.Info($"[AERO] export failed: {e.Message}"); }
    }
}
