#pragma warning disable
using System;
using System.IO;

namespace AeroMod;

/// <summary>
/// Built force tables on disk (%TEMP%\AeroMod\tables), by the grid's shape: a grid seen before - a reloaded world,
/// a blueprint pasted again, the second of a hundred copies - gets its forces from the file instead of a build
/// (Red Ship: ~2.5 s of a worker -> tens of ms). A table is a function of the blocks' cells, the block size and the
/// face physics alone (the centre of mass only tilts its small rotation map), so those are the key; anything that
/// changes how tables are built must bump Version. A file that does not read back exactly is a miss: the build runs.
/// </summary>
public static class AeroTableCache
{
    /// <summary>Bump on any change to how a table, its chunks or its wings are built.</summary>
    public const int Version = 1;
    public static bool Enabled = true;
    /// <summary>The folder's cap: past it, the least recently used files go.</summary>
    public static long MaxBytes = 256L << 20;
    const uint Magic = 0x41455254;   // 'AERT'

    public static int Hits, Misses, Writes;
    public static double LastLoadMs;

    static string Dir => Path.Combine(Path.GetTempPath(), "AeroMod", "tables");

    /// <summary>The shape's key: its block boxes (in any order), the block size and cell scale, the face physics.</summary>
    public static string Key(List<(Vector3I, Vector3I)> boxes, float blockSize, int cellScale, FacePhysics phys)
    {
        // order-free: two sums of a strong per-box hash (the octree's order is not the same from load to load)
        ulong a = 0, b = 0;
        foreach (var (lo, hi) in boxes)
        {
            ulong h = Mix(((ulong)(uint)lo.X << 32) ^ (uint)lo.Y);
            h = Mix(h ^ ((ulong)(uint)lo.Z << 32 ^ (uint)hi.X));
            h = Mix(h ^ ((ulong)(uint)hi.Y << 32 ^ (uint)hi.Z));
            a += h; b += Mix(h ^ 0x9E3779B97F4A7C15UL);
        }
        ulong p = Mix((ulong)boxes.Count ^ ((ulong)BitConverter.SingleToInt32Bits(blockSize) << 20) ^ ((ulong)cellScale << 52) ^ ((ulong)Version << 56));
        foreach (float f in new[] { phys.CdBluff, phys.CpMax, phys.Cf, phys.St, phys.BaseCp }) p = Mix(p ^ (uint)BitConverter.SingleToInt32Bits(f));
        return $"{a ^ p:x16}{b:x16}";
    }

    static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9UL;
        z = (z ^ (z >> 27)) * 0x94d049bb133111ebUL;
        return z ^ (z >> 31);
    }

    /// <summary>Background: a saved table for this shape, if any (a damaged or unreadable file: a miss).</summary>
    public static bool TryLoad(string key, FacePhysics phys, out ForceTable table, out ChunkedTable chunks, out List<LiftingSurface> wings)
    {
        table = null; chunks = null; wings = null;
        if (!Enabled) return false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string path = Path.Combine(Dir, key + ".bin");
        if (!File.Exists(path)) { System.Threading.Interlocked.Increment(ref Misses); return false; }
        try
        {
            using (var r = new BinaryReader(new BufferedStream(File.OpenRead(path), 1 << 16)))
            {
                if (r.ReadUInt32() != Magic || r.ReadInt32() != Version || r.ReadString() != key) throw new InvalidDataException("header");
                table = ForceTable.Read(r);
                chunks = ChunkedTable.Read(r, phys);
                int nw = r.ReadInt32();
                if (nw < 0 || nw > 100000) throw new InvalidDataException("wings");
                wings = new List<LiftingSurface>(nw);
                for (int i = 0; i < nw; i++) wings.Add(ReadWing(r));
                if (r.ReadUInt32() != Magic) throw new InvalidDataException("end");
            }
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }   // (recently used: kept)
            LastLoadMs = sw.Elapsed.TotalMilliseconds;
            System.Threading.Interlocked.Increment(ref Hits);
            return true;
        }
        catch (Exception e)
        {
            Log.Default?.Info($"[AERO] table cache: {key} unreadable ({e.Message}): rebuilding");
            table = null; chunks = null; wings = null;
            System.Threading.Interlocked.Increment(ref Misses);
            return false;
        }
    }

    /// <summary>Background, after a build (before the table is shared): written to a temporary file, then moved in.</summary>
    public static void Save(string key, ForceTable table, ChunkedTable chunks, List<LiftingSurface> wings)
    {
        if (!Enabled || table == null || chunks == null) return;
        try
        {
            Directory.CreateDirectory(Dir);
            string path = Path.Combine(Dir, key + ".bin"), tmp = path + "." + System.Threading.Thread.CurrentThread.ManagedThreadId + ".tmp";
            using (var w = new BinaryWriter(new BufferedStream(File.Create(tmp), 1 << 16)))
            {
                w.Write(Magic); w.Write(Version); w.Write(key);
                table.Write(w);
                chunks.Write(w);
                w.Write(wings?.Count ?? 0);
                if (wings != null) foreach (var wing in wings) WriteWing(w, wing);
                w.Write(Magic);
            }
            File.Move(tmp, path, true);
            System.Threading.Interlocked.Increment(ref Writes);
            Trim();
        }
        catch (Exception e) { Log.Default?.Info($"[AERO] table cache: write failed ({e.Message})"); }
    }

    static void Trim()
    {
        var files = new DirectoryInfo(Dir).GetFiles("*.bin");
        long total = 0; foreach (var f in files) total += f.Length;
        if (total <= MaxBytes) return;
        System.Array.Sort(files, (x, y) => x.LastWriteTimeUtc.CompareTo(y.LastWriteTimeUtc));
        foreach (var f in files)
        {
            if (total <= MaxBytes * 3 / 4) break;
            try { long len = f.Length; f.Delete(); total -= len; } catch { }
        }
    }

    static void WriteV(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
    static Vector3 ReadV(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    static void WriteWing(BinaryWriter w, LiftingSurface s)
    {
        WriteV(w, s.Normal); WriteV(w, s.SpanAxis); WriteV(w, s.ChordAxis);
        w.Write(s.PlanformArea); w.Write(s.Span); w.Write(s.ThicknessRatio); w.Write(s.SweepAngle);
        WriteV(w, s.Centroid); WriteV(w, s.AeroCenter); w.Write(s.FaceCount);
        w.Write(s.Cells.Count);
        foreach (var c in s.Cells) { w.Write(c.X); w.Write(c.Y); w.Write(c.Z); }
    }

    static LiftingSurface ReadWing(BinaryReader r)
    {
        var n = ReadV(r); var sa = ReadV(r); var ca = ReadV(r);
        float area = r.ReadSingle(), span = r.ReadSingle(), tc = r.ReadSingle(), sweep = r.ReadSingle();
        var cen = ReadV(r); var ac = ReadV(r); int fc = r.ReadInt32();
        int nc = r.ReadInt32();
        if (nc < 0 || nc > 1 << 24) throw new InvalidDataException("wing cells");
        var cells = new List<Vector3I>(nc);
        for (int i = 0; i < nc; i++) cells.Add(new Vector3I(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));
        return new LiftingSurface(n, sa, ca, area, span, tc, sweep, cen, ac, fc, cells);
    }
}
