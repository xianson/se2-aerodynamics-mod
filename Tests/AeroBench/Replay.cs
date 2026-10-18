using System.IO;
using System.Globalization;
using AeroMod;

// AeroBench 'replay': an in-game evaluation's loads (StructureLoads.DumpLoadsAbove: %TEMP%/AeroMod/struct_*.loads) on its
// .struct, ramped (x0.25 -> x4 over 30 s, the spin by the square root) with the game's evaluation and damage
// (StructureEval, RunScenario): what wears, breaks and departs first, and at what multiple of the in-game loads.
//   dotnet run -c Release -- replay <loads-struct> <loads> [<target-struct>]
// target-struct: another dump of the same ship (e.g. one with mount points; its block indices differ): the thrusters and
// gyros are found there by their first cell.
static partial class Program
{
    static StructureEval.Loads ParseLoads(string path)
    {
        var L = new StructureEval.Loads(); var ci = CultureInfo.InvariantCulture;
        Vector3 V(string[] p, int i) => new Vector3(float.Parse(p[i], ci), float.Parse(p[i + 1], ci), float.Parse(p[i + 2], ci));
        foreach (var line in File.ReadLines(path))
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries); if (p.Length == 0 || p[0] == "#") continue;
            switch (p[0])
            {
                case "aeroF": L.AeroF = V(p, 1); break;
                case "aeroT": L.AeroT = V(p, 1); break;
                case "aeroCom": L.AeroCom = V(p, 1); break;
                case "airVel": L.AirVel = V(p, 1); break;
                case "gyroT": L.GyroT = V(p, 1); break;
                case "angVel": L.AngVel = V(p, 1); break;
                case "scale": L.LiftScale = float.Parse(p[1], ci); L.CellSize = float.Parse(p[2], ci); L.CellOffset = float.Parse(p[3], ci); break;
                case "wing": { L.WingF.Add(V(p, 1)); var cells = new List<Vector3I>(); for (int i = 4; i + 2 < p.Length; i += 3) cells.Add(new Vector3I(int.Parse(p[i]), int.Parse(p[i + 1]), int.Parse(p[i + 2]))); L.WingCells.Add(cells); break; }
                case "thr": L.ThrBlock.Add(int.Parse(p[1])); L.ThrF.Add(V(p, 2)); break;
                case "gyro": L.GyroBlock.Add(int.Parse(p[1])); break;
                case "comp": L.CompF.Add(V(p, 1)); L.CompP.Add(V(p, 4)); L.CompT.Add(V(p, 7)); L.CompAt.Add(V(p, 10)); break;   // (2026-10-17: a block component's air at its block)
                case "load": L.LoadBlock.Add(int.Parse(p[1])); L.LoadF.Add(V(p, 2)); L.LoadP.Add(V(p, 5)); L.LoadT.Add(V(p, 8)); L.LoadEngine.Add(p[11] == "1"); break;   // (an extra block load)
            }
        }
        return L;
    }

    /// <summary>Each block's first cell (its first box's min) in a .struct.</summary>
    static Dictionary<int, Vector3I> FirstCells(string path)
    {
        var d = new Dictionary<int, Vector3I>();
        foreach (var line in File.ReadLines(path))
        {
            var p = line.Split(' '); if (line.StartsWith("#") || p[0] == "m" || p.Length < 9) continue;
            int b = int.Parse(p[6]); if (!d.ContainsKey(b)) d[b] = new Vector3I(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]));
        }
        return d;
    }

    static int Replay(string loadsStruct, string loadsPath, string target)
    {
        var L0 = ParseLoads(loadsPath);
        string path = target ?? loadsStruct;
        if (target != null)
        {
            // the thrusters and gyros by their first cell in the target's numbering
            var from = FirstCells(loadsStruct); var to = FirstCells(target);
            var byCell = new Dictionary<Vector3I, int>(); foreach (var kv in to) byCell[kv.Value] = kv.Key;
            int Map(int b) => from.TryGetValue(b, out var c) && byCell.TryGetValue(c, out int t) ? t : -1;
            for (int k = 0; k < L0.ThrBlock.Count; k++) L0.ThrBlock[k] = Map(L0.ThrBlock[k]);
            for (int k = 0; k < L0.GyroBlock.Count; k++) L0.GyroBlock[k] = Map(L0.GyroBlock[k]);
        }
        var sh = LoadStruct(path);
        float M = sh.S.TotalMass, W = M * 9.81f;
        Vector3 thr = Vector3.Zero; foreach (var f in L0.ThrF) thr += f;
        Console.WriteLine($"   in-game loads: air {L0.AeroF.Length() / W:F2} g ({L0.AeroF.Length() / 1e6:F2} MN), thrust {thr.Length() / W:F2} g, gyro {L0.GyroT.Length() / 1e6:F2} MN m, spin {L0.AngVel.Length():F3} rad/s");
        // the loads as dumped, once
        {
            var s = sh.S; sh.H.Pass(L0.AirVel);
            StructureEval.Apply(s, sh.H, L0); s.SolveFree(L0.AngVel);
            Console.WriteLine($"   as dumped: worst {s.MaxUtil:F2} at {Where(sh, s.MaxContact)}");
        }
        var sim = MakeSim(sh, path);
        RunScenario(sim, "in-game loads ramped x0.25 -> x4 over 30 s", 30f, (t, sm) =>
        {
            float k = 0.25f + 3.75f * t / 30f;
            // the original blocks' current indices (rebuilds renumber)
            var cur = new Dictionary<int, int>(); for (int b = 0; b < sm.Orig.Length; b++) cur[sm.Orig[b]] = b;
            var L = new StructureEval.Loads { AeroF = L0.AeroF * k, AeroT = L0.AeroT * k, AeroCom = L0.AeroCom, AirVel = L0.AirVel, GyroT = L0.GyroT * k, AngVel = L0.AngVel * MathF.Sqrt(k), LiftScale = L0.LiftScale, CellSize = L0.CellSize, CellOffset = L0.CellOffset };
            for (int w = 0; w < L0.WingCells.Count; w++) { L.WingCells.Add(L0.WingCells[w]); L.WingF.Add(L0.WingF[w] * k); }
            for (int q = 0; q < L0.ThrBlock.Count; q++) if (cur.TryGetValue(L0.ThrBlock[q], out int b)) { L.ThrBlock.Add(b); L.ThrF.Add(L0.ThrF[q] * k); }
            foreach (int g in L0.GyroBlock) if (cur.TryGetValue(g, out int b)) L.GyroBlock.Add(b);
            return (L, 1.0f, 288f, $"x{k:F2} ({L0.AeroF.Length() * k / W:F1} g air)");
        }, false);
        return 0;
    }
}
