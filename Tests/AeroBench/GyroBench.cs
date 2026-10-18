using System.IO;
using AeroMod;

// AeroBench 'gyros' (2026-10-18, the gyro rebalance - the user: "just do 1, maybe a lil 2"): on the real ships, the vanilla
// gyros' torque against what else turns the ship, to choose Gyros.TorqueScale by measurement.
//   dotnet run -c Release -- gyros [ship ...]
// Per ship (Tests/data/struct/*.struct: every block's cells, mass and name; Tests/data/*.flight + *_game.boxes: dumped from
// the game): the gyros' budget as the game's target law spends it (GridGyroscopesComponent.ComputeTorqueTarget:
// min(0.01 m^2, MaxTorque x 0.25) - the vanilla TargetGyroRotation numbers, FlightFixture's "gyro" line), against
//  (a) the air's pitching moment at 150 m/s sea level: at 0 deg (the trim it needs), the change over a 5 deg upset (the size
//      of torque control surfaces work in - none of these vanilla blueprints has the mod's surfaces), the largest up to the
//      lift peak (stall), and the yaw moment at 5 deg sideslip;
//  (c) one main engine out: the main thrust group's torque with its worst single thruster lost;
//  (d) a turn: pitch inertia x 15 deg/s reached in 1 s.
// Then the same at each candidate scale: the share of each the gyros could hold.
static partial class Program
{
    // the vanilla definitions (GameData/Vanilla/Content/Blocks: *_GyroscopeDefinition.def MaxTorque, *_ThrusterDefinition.def ThrustPower)
    static readonly (string key, float torque)[] GyroDefs = { ("Gyroscope750", 798784900f), ("Gyroscope250", 44376940f), ("Gyroscope50", 217980f) };
    static readonly (string key, float thrust)[] ThrustDefs = {
        ("AtmosphericThruster1000", 15465370f), ("AtmosphericThruster500", 1516383f), ("AtmosphericThruster250", 287136.3f), ("AtmosphericThruster100", 40000f),
        ("IonThruster750", 5636987f), ("IonThruster500", 856368.56f), ("IonThruster150", 82492.88f), ("IonThruster100", 8950.306f) };
    const float GyroTargetDamp = 0.25f, GyroMassCoef = 0.01f;   // (MovementDefinition.TargetGyroRotation, as dumped: FlightFixture)

    sealed class GyroRow
    {
        public string Name; public float Mass, Ip, Iy, GyroSum, Budget, M0, M5, MStall, StallDeg, N5, OneOut, Turn; public int Gyros, Engines;
    }

    static int GyroBench(string dataDir, string[] names)
    {
        var sea = new AtmosphereState { Density = 1.225, Pressure = 101325, Temperature = 288, SpeedOfSound = 340 };
        const float V = 150f, TurnAccel = 15f * MathF.PI / 180f;   // (15 deg/s in 1 s)
        var rows = new List<GyroRow>();
        var dir = Path.Combine(dataDir, "struct");
        var files = names.Length > 0 ? names.Select(n => Path.Combine(dir, n + ".struct")).Where(File.Exists).ToList() : Directory.GetFiles(dir, "*.struct").OrderBy(x => x).ToList();
        foreach (var path in files)
        {
            var sh = LoadStruct(path);
            var row = new GyroRow { Name = sh.Name, Mass = sh.S.TotalMass };
            // the blocks: mass spread over each block's boxes by volume; gyros and thrusters by name
            var lines = File.ReadLines(path).Where(l => !l.StartsWith("#") && !l.StartsWith("m ")).Select(l => l.Split(' ')).Where(p => p.Length >= 9).ToList();
            var vol = new Dictionary<int, float>();
            foreach (var p in lines) { int b = int.Parse(p[6]); vol[b] = vol.GetValueOrDefault(b) + BoxVol(p); }
            var (nose, up) = StructAxes.TryGetValue(sh.Name, out var ax) ? ax : (Vector3.UnitZ, Vector3.UnitY);
            var pitch = Vector3.Normalize(Vector3.Cross(nose, up));
            var com = sh.S.Com;
            double ip = 0, iy = 0;
            var seenGyro = new HashSet<int>(); var thr = new List<(Vector3 pos, Vector3 dir, float f)>(); var seenThr = new HashSet<int>();
            foreach (var p in lines)
            {
                int b = int.Parse(p[6]); float bm = float.Parse(p[7], System.Globalization.CultureInfo.InvariantCulture);
                var lo = new Vector3(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2])) * 0.25f; var hi = (new Vector3(int.Parse(p[3]), int.Parse(p[4]), int.Parse(p[5])) + Vector3.One) * 0.25f;
                float m = bm * BoxVol(p) / MathF.Max(vol[b], 1e-6f);
                var c = (lo + hi) * 0.5f - com; var s = hi - lo;
                ip += m * (AxisR2(c, pitch) + AxisBox(s, pitch)); iy += m * (AxisR2(c, up) + AxisBox(s, up));
                string nm = p[8];
                foreach (var (key, t) in GyroDefs) if (nm.StartsWith(key, StringComparison.Ordinal) && seenGyro.Add(b)) { row.GyroSum += t; row.Gyros++; break; }
                if (p.Length >= 13 && p[9] == "thr" && seenThr.Add(b))
                    foreach (var (key, f) in ThrustDefs) if (nm.StartsWith(key, StringComparison.Ordinal)) { thr.Add(((lo + hi) * 0.5f, new Vector3(int.Parse(p[10]), int.Parse(p[11]), int.Parse(p[12])), f)); break; }
            }
            row.Ip = (float)ip; row.Iy = (float)iy;
            row.Budget = MathF.Min(GyroMassCoef * row.Mass * row.Mass, row.GyroSum * GyroTargetDamp);
            row.Turn = row.Ip * TurnAccel;
            Air(sh.Aero, sea, V, nose, up, pitch, com, row);
            OneOut(thr, com, row);
            rows.Add(row);
        }
        // the game's own dumps (Tests/data/*.flight: mass, principal inertia, the gyros' MaxTorque, thrusters)
        foreach (var fl in Directory.GetFiles(dataDir, "*.flight").OrderBy(x => x))
        {
            string nm = Path.GetFileNameWithoutExtension(fl);
            if (names.Length > 0 && !names.Contains(nm)) continue;
            var row = new GyroRow { Name = nm + " (dump)" };
            var CI = System.Globalization.CultureInfo.InvariantCulture; Vector3 com = Vector3.Zero, invI = Vector3.One; var thr = new List<(Vector3 pos, Vector3 dir, float f)>();
            foreach (var raw in File.ReadAllLines(fl))
            {
                var p = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 0) continue;
                Vector3 R(int i) => new Vector3(float.Parse(p[i], CI), float.Parse(p[i + 1], CI), float.Parse(p[i + 2], CI));
                if (p[0] == "mass") row.Mass = float.Parse(p[1], CI);
                else if (p[0] == "com") com = R(1);
                else if (p[0] == "invInertia") invI = R(1);
                else if (p[0] == "gyro") row.GyroSum = float.Parse(p[1], CI);
                else if (p[0] == "thruster") thr.Add((R(1), R(4), float.Parse(p[7], CI)));
            }
            row.Budget = MathF.Min(GyroMassCoef * row.Mass * row.Mass, row.GyroSum * GyroTargetDamp);
            float iMax = MathF.Max(1f / invI.X, MathF.Max(1f / invI.Y, 1f / invI.Z)), iMin = MathF.Min(1f / invI.X, MathF.Min(1f / invI.Y, 1f / invI.Z));
            row.Ip = iMax; row.Iy = iMin; row.Turn = row.Ip * TurnAccel;   // (principal axes: the largest taken as pitch/yaw, the conservative)
            var bx = Path.Combine(dataDir, nm + "_game.boxes");
            if (File.Exists(bx))
            {
                var boxes = LoadBoxes(bx); var ship = Build(nm, boxes); ship.Com = com;
                var (nose, up) = FlightAxes(ship, sea, boxes);
                Air(ship, sea, V, nose, up, Vector3.Normalize(Vector3.Cross(nose, up)), com, row);
            }
            OneOut(thr, com, row);
            rows.Add(row);
        }

        Console.WriteLine($"\n   GYROS vs THE SHIP (vanilla; 150 m/s sea level; kN m unless noted). budget = min(0.01 m^2, 0.25 x sum MaxTorque): the target law's");
        Console.WriteLine($"   {"ship",-20} {"mass t",7} {"gyros",5} {"budget",9} | {"M0 trim",8} {"dM 5deg",8} {"M stall",8} {"(deg)",5} {"N 5deg",8} | {"1 eng out",9} {"turn 15/s",9} | budget over: {"dM5",6} {"stall",6} {"1out",6} {"turn",6}");
        foreach (var r in rows)
            Console.WriteLine($"   {r.Name,-20} {r.Mass / 1000f,7:F1} {r.Gyros,5} {r.Budget / 1e3f,9:F0} | {r.M0 / 1e3f,8:F0} {r.M5 / 1e3f,8:F0} {r.MStall / 1e3f,8:F0} {r.StallDeg,5:F0} {r.N5 / 1e3f,8:F0} | {r.OneOut / 1e3f,9:F0} {r.Turn / 1e3f,9:F0} | {"",12} {Ratio(r.Budget, r.M5),6} {Ratio(r.Budget, r.MStall),6} {Ratio(r.Budget, r.OneOut),6} {Ratio(r.Budget, r.Turn),6}");
        foreach (float sc in new[] { 0.5f, 0.25f, 0.15f, 0.1f, 0.05f })
        {
            Console.WriteLine($"\n   at scale {sc:F2}: the gyros' budget over (>1: the gyros alone hold it)");
            foreach (var r in rows)
            {
                float b = MathF.Min(GyroMassCoef * r.Mass * r.Mass, r.GyroSum * sc * GyroTargetDamp);
                Console.WriteLine($"   {r.Name,-20} budget {b / 1e3f,8:F0} kN m | dM5 {Ratio(b, r.M5),6} stall {Ratio(b, r.MStall),6} 1out {Ratio(b, r.OneOut),6} turn {Ratio(b, r.Turn),6} | {(r.Ip > 0f ? b / r.Ip * 180f / MathF.PI : 0f),6:F1} deg/s2 pitch accel");
            }
        }
        Console.WriteLine("AeroBench: gyros report (no checks) 1/1 passed");
        return 0;

        static float BoxVol(string[] p) => (int.Parse(p[3]) - int.Parse(p[0]) + 1) * (int.Parse(p[4]) - int.Parse(p[1]) + 1) * (float)(int.Parse(p[5]) - int.Parse(p[2]) + 1);
        static double AxisR2(Vector3 r, Vector3 a) => (r - a * Vector3.Dot(r, a)).LengthSquared();
        static double AxisBox(Vector3 s, Vector3 a) { var q = s * s / 12f; return q.X + q.Y + q.Z - Vector3.Dot(q, a * a); }
        static string Ratio(float a, float b) => b > 1f ? (a / b).ToString("F2") : "-";
    }

    /// <summary>The air's moments (row.M0, M5, MStall at the lift peak 0..30 deg, N5): pitch about 'pitch', yaw about 'up'.</summary>
    static void Air(Ship ship, AtmosphereState atmo, float V, Vector3 nose, Vector3 up, Vector3 pitch, Vector3 com, GyroRow row)
    {
        Vector3 Dir(float deg) { float a = deg * MathF.PI / 180f; return Vector3.Normalize(nose * MathF.Cos(a) - up * MathF.Sin(a)); }
        float M(float deg) => Vector3.Dot(Exact(ship, Dir(deg), V, atmo, com).Torque, pitch);
        float m0 = M(0f);
        row.M0 = MathF.Abs(m0); row.M5 = MathF.Abs(M(5f) - m0);
        float best = float.MinValue, bestDeg = 0f, mMax = 0f;
        for (float d = 0f; d <= 30f; d += 1f)
        {
            var v = Dir(d); var r = Exact(ship, v, V, atmo, com);
            float lift = Vector3.Dot(r.Force, Vector3.Normalize(up - v * Vector3.Dot(up, v)));
            if (lift > best) { best = lift; bestDeg = d; }
            mMax = MathF.Max(mMax, MathF.Abs(Vector3.Dot(r.Torque, pitch)));
            if (d > bestDeg + 4f) break;   // (past the peak)
        }
        row.MStall = mMax; row.StallDeg = bestDeg;
        float b = 5f * MathF.PI / 180f;
        var side = Vector3.Normalize(nose * MathF.Cos(b) + pitch * MathF.Sin(b));
        row.N5 = MathF.Abs(Vector3.Dot(Exact(ship, side, V, atmo, com).Torque - Exact(ship, nose, V, atmo, com).Torque, up));
    }

    /// <summary>The main thrust group (the direction with the most thrust): its torque with its worst single thruster out.</summary>
    static void OneOut(List<(Vector3 pos, Vector3 dir, float f)> thr, Vector3 com, GyroRow row)
    {
        if (thr.Count == 0) return;
        var main = thr.GroupBy(t => (MathF.Round(t.dir.X), MathF.Round(t.dir.Y), MathF.Round(t.dir.Z))).OrderByDescending(g => g.Sum(t => t.f)).First().ToList();
        row.Engines = main.Count;
        Vector3 all = Vector3.Zero; foreach (var t in main) all += Vector3.Cross(t.pos - com, t.dir * t.f);
        float worst = 0f; foreach (var t in main) worst = MathF.Max(worst, (all - Vector3.Cross(t.pos - com, t.dir * t.f)).Length());
        row.OneOut = main.Count > 1 ? worst : 0f;
    }
}
