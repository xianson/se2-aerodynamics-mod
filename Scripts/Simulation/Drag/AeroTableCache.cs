#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
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
    public const int Version = 22;   // (22: 2026-10-18 fix a-wing-wave - the wing record carries its edge faces' Newtonian areas and its own t/c (LiftingSurface.EdgeLo / EdgeHi / OwnThickness, measured at detection: a cached grid's surface is gone), ForceTable.NewtonBlend factored out of Evaluate - the tables themselves unchanged; 21: 2026-10-18 fix a-wing-edge - a wing is saved orientation-free (both edges' sweeps, the quarter chord's offset from the centroid; the leading edge is the flow's, per tick), the wing record changed; 20: 2026-10-17 the sleek hull's wall lowered to 1.12x (ForceTable.WavePeakNeedle, the Mach wall made reachable), runtime only, tables unchanged; 19: 2026-10-17 the sleek hull's Mach wall - ForceTable.HullWave / BlendHold, runtime only, tables unchanged; 18: 2026-10-15 audit fixes - coplanar wings merged by their own spans (A3); a pooled builder forgets the last grid's flow-through faces (A4); the chunks carry the ship's own skin friction, saved (A5); local updates occlude as the full build and keep its pressure recovery per chunk, saved (A6); table workers restore their thread's priority - tables unchanged (A9); every solid of a grid is hull, not only the largest (A20); 17: an engine box spans its cells centred (lo-0.5 .. hi+0.5) x 0.25 m, not lo .. hi+1 - one cell less on each + side; 16: intakes reach IntakeReach past the engine block - its cowling; transonic onset by streamlining (runtime only); 15: engines flow-through in game - their boxes in the key; 14: 14: the overlay pass returns the cells' own normals - tables unchanged; 13: the overlay builds the face arrays itself - tables unchanged; 12: the drag overlay pass sits in the model's source - tables unchanged; 11: ColumnMode span axis from mirror pairs; 10: low-aspect plates are wings - ConnectedComponentWingDetector.LowAspectRatio; 9: the model switches in the key; 8: the L/D flight model on by default - square law, pressure recovery, Reynolds skin - 2026-10-11; 7: smoothed faces carry their share of the slope - SmoothSurfaceProvider - 2026-10-10)   // (2: skin friction's sign fixed; 3: grazing faces no longer self-shadowed; 4: built about the grid's own centre of mass, not (0,0,0); 5: a pooled builder no longer sums a previous grid's faces in; 6: nor keeps them for a grid with as many faces - tables saved before are rebuilt)
    public static bool Enabled = true;
    /// <summary>The folder's cap: past it, the least recently used files go.</summary>
    public static long MaxBytes = 256L << 20;
    const uint Magic = 0x41455254;   // 'AERT'

    /// <summary>The model's static switches that change a built table, as bits of the key: a table built with one setting
    /// was read back under another (2026-10-11: tables built with ColumnMode on - 9 wings - served after it was turned
    /// off again, under the same Version). Add any new switch that changes how faces, chunks or wings are built.</summary>
    public static int ModelSwitches =>
        (DampedShadowedDragModel.SubsonicSquareLaw ? 1 : 0) | (DampedShadowedDragModel.PressureRecovery ? 2 : 0)
      | (DampedShadowedDragModel.ReynoldsSkin ? 4 : 0) | (DampedShadowedDragModel.FlowThroughEngines ? 8 : 0)
      | (CompressibleWingModel.GeometricCamber ? 16 : 0) | (CompressibleWingModel.WingSystems ? 32 : 0)
      | (ConnectedComponentWingDetector.ColumnMode ? 64 : 0) | (DampedShadowedDragModel.RecoveryPasses & 7) << 7
      | (ConnectedComponentWingDetector.LowAspectRatio > 0f ? 1024 : 0);

    public static int Hits, Misses, Writes;
    public static double LastLoadMs;

    static string Dir => Path.Combine(Path.GetTempPath(), "AeroMod", "tables");

    /// <summary>The shape's key: its block boxes (in any order), the block size and cell scale, the face physics.</summary>
    public static string Key(List<(Vector3I, Vector3I)> boxes, float blockSize, int cellScale, FacePhysics phys, IReadOnlyList<(Vector3I lo, Vector3I hi, Vector3 axis)> engines = null)
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
        ulong p = Mix((ulong)boxes.Count ^ ((ulong)BitConverter.SingleToInt32Bits(blockSize) << 20) ^ ((ulong)cellScale << 52) ^ ((ulong)Version << 56) ^ ((ulong)ModelSwitches << 40));
        foreach (float f in new[] { phys.CdBluff, phys.CpMax, phys.Cf, phys.St, phys.BaseCp }) p = Mix(p ^ (uint)BitConverter.SingleToInt32Bits(f));
        // (the flow-through engines change the table: their boxes and axes, order-free)
        if (engines != null && DampedShadowedDragModel.FlowThroughEngines)
        {
            ulong e = 0;
            foreach (var (lo, hi, ax) in engines)
                e += Mix(Mix(((ulong)(uint)lo.X << 32) ^ (uint)lo.Y ^ ((ulong)(uint)lo.Z << 16)) ^ Mix(((ulong)(uint)hi.X << 32) ^ (uint)hi.Y ^ ((ulong)(uint)hi.Z << 16)) ^ (ulong)(MathF.Abs(ax.X) > 0.5f ? 1 : MathF.Abs(ax.Y) > 0.5f ? 2 : 3));
            p = Mix(p ^ e ^ (ulong)engines.Count);
        }
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
                // (another version's file - every player's, after a Version bump - is a plain miss: an exception costs
                //  ~200 ms in SE2, even caught)
                if (r.BaseStream.Length < 8 || r.ReadUInt32() != Magic || r.ReadInt32() != Version || r.ReadString() != key)
                {
                    System.Threading.Interlocked.Increment(ref Misses);
                    return false;
                }
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
        w.Write(s.PlanformArea); w.Write(s.Span); w.Write(s.ThicknessRatio); w.Write(s.SweepLo); w.Write(s.SweepHi);
        WriteV(w, s.Centroid); w.Write(s.AcOffset); w.Write(s.FaceCount);
        w.Write(s.Cells.Count);
        foreach (var c in s.Cells) { w.Write(c.X); w.Write(c.Y); w.Write(c.Z); }
        w.Write(s.EdgeLo); w.Write(s.EdgeHi); w.Write(s.OwnThickness);   // (the wave drag's edge faces and own t/c, 2026-10-18)
    }

    static LiftingSurface ReadWing(BinaryReader r)
    {
        var n = ReadV(r); var sa = ReadV(r); var ca = ReadV(r);
        float area = r.ReadSingle(), span = r.ReadSingle(), tc = r.ReadSingle(), sweepLo = r.ReadSingle(), sweepHi = r.ReadSingle();
        var cen = ReadV(r); float acOffset = r.ReadSingle(); int fc = r.ReadInt32();
        int nc = r.ReadInt32();
        if (nc < 0 || nc > 1 << 24) throw new InvalidDataException("wing cells");
        var cells = new List<Vector3I>(nc);
        for (int i = 0; i < nc; i++) cells.Add(new Vector3I(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));
        float edgeLo = r.ReadSingle(), edgeHi = r.ReadSingle(), own = r.ReadSingle();
        return new LiftingSurface(n, sa, ca, area, span, tc, sweepLo, sweepHi, cen, acOffset, fc, cells, edgeLo, edgeHi, own);
    }
}
