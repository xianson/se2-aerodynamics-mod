#pragma warning disable
using System;
namespace AeroMod;

// (harness) The structure's input and loads dumped for the offline bench (Tests/data/struct, AeroBench 'replay').
public partial class AeroGridComponent
{
    /// <summary>%TEMP%/AeroMod/struct_{grid name}{ext} (the folder made).</summary>
    string DumpPath(string ext)
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AeroMod");
        System.IO.Directory.CreateDirectory(dir);
        string safe = string.Concat((Entity?.DebugName ?? "grid").Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_'));
        return System.IO.Path.Combine(dir, "struct_" + safe + ext);
    }

    /// <summary>(harness: aeroset StructureLoads.DumpLoadsAbove U) the first evaluation over utilisation U: its loads, for the
    /// offline bench to replay on the .struct dumped with DumpNext (same block indices while the grid is not rebuilt).</summary>
    void DumpLoads(StructureEval.Loads L, float util)
    {
        try
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            string V(Vector3 v) => string.Format(ci, "{0:R} {1:R} {2:R}", v.X, v.Y, v.Z);
            var sb = new System.Text.StringBuilder();
            sb.Append("# loads of '").Append(Entity?.DebugName).Append("' at utilisation ").Append(util.ToString(ci)).Append('\n');
            sb.Append("aeroF ").Append(V(L.AeroF)).Append('\n').Append("aeroT ").Append(V(L.AeroT)).Append('\n').Append("aeroCom ").Append(V(L.AeroCom)).Append('\n');
            sb.Append("airVel ").Append(V(L.AirVel)).Append('\n').Append("gyroT ").Append(V(L.GyroT)).Append('\n').Append("angVel ").Append(V(L.AngVel)).Append('\n');
            sb.Append("scale ").Append(L.LiftScale.ToString(ci)).Append(' ').Append(L.CellSize.ToString(ci)).Append(' ').Append(L.CellOffset.ToString(ci)).Append('\n');
            for (int w = 0; w < L.WingCells.Count; w++)
            {
                sb.Append("wing ").Append(V(L.WingF[w]));
                foreach (var c in L.WingCells[w]) sb.Append(' ').Append(c.X).Append(' ').Append(c.Y).Append(' ').Append(c.Z);
                sb.Append('\n');
            }
            for (int k = 0; k < L.ThrBlock.Count; k++) sb.Append("thr ").Append(L.ThrBlock[k]).Append(' ').Append(V(L.ThrF[k])).Append('\n');
            foreach (int b in L.GyroBlock) sb.Append("gyro ").Append(b).Append('\n');
            string path = DumpPath(".loads");
            System.IO.File.WriteAllText(path, sb.ToString());
            StructureLoads.LastDump = path;
            Log.Default?.Info($"[AERO-STRUCT] dumped the loads at utilisation {util:F2} to {path}");
        }
        catch (Exception e) { StructureLoads.LastDump = "loads failed: " + e.Message; }
    }

    /// <summary>(harness: aeroset StructureLoads.DumpNext true) the next rebuilt player grid's blocks, as the offline bench's
    /// .struct file (Tests/data/struct: boxes in cells, block, mass, kind) in %TEMP%/AeroMod.</summary>
    void DumpStructure(StructBuildInput b)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            float total = 0f; foreach (var m in b.Mass) total += m;
            sb.Append("# blockSize ").Append(b.BlockSize.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" mass ").Append(total.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)).Append(" blocks ").Append(b.Blocks.Count).Append('\n');
            sb.Append("# from game grid '").Append(Entity?.DebugName).Append("'\n");
            for (int i = 0; i < b.BoxBlock.Count; i++)
            {
                int k = b.BoxBlock[i];
                var lo = b.Lo[i]; var hi = b.Hi[i];
                sb.Append(lo.X).Append(' ').Append(lo.Y).Append(' ').Append(lo.Z).Append(' ').Append(hi.X).Append(' ').Append(hi.Y).Append(' ').Append(hi.Z).Append(' ')
                  .Append(k).Append(' ').Append(b.Mass[k].ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(' ')
                  .Append((b.Names[k] ?? "?").Replace(' ', '_')).Append('\n');
            }
            // mount points: "m block dx dy dz loX loY loZ hiX hiY hiZ" (grid cells, the outward direction)
            foreach (var (k, mdir, lo, hi) in b.Mounts)
                sb.Append("m ").Append(k).Append(' ').Append(mdir.X).Append(' ').Append(mdir.Y).Append(' ').Append(mdir.Z).Append(' ')
                  .Append(lo.X).Append(' ').Append(lo.Y).Append(' ').Append(lo.Z).Append(' ').Append(hi.X).Append(' ').Append(hi.Y).Append(' ').Append(hi.Z).Append('\n');
            string path = DumpPath(".struct");
            System.IO.File.WriteAllText(path, sb.ToString());
            StructureLoads.LastDump = path;
            Log.Default?.Info($"[AERO-STRUCT] dumped {b.Blocks.Count} blocks to {path}");
        }
        catch (Exception e) { StructureLoads.LastDump = "failed: " + e.Message; }
    }
}
