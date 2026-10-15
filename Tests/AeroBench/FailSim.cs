using System.IO;
using AeroMod;

// AeroBench 'fail': failures simulated offline with the game's own evaluation (Structure/StructureEval - the loads as
// the game spreads them, the damage as the game decides it), every 0.25 s:
//   dotnet run -c Release -- fail [ship ...]
//  1. fidelity: the game's load spreading (wings over their cells, the rest over windward faces) against the exact
//     per-face pressures, on the same pull - the worst joint and the top joints;
//  2. a pull ramped up (250 m/s at sea level, 2 -> 20 deg over 20 s): first wear, first break, what departs and at what g;
//  3. a roll flick ramped up (0 -> 40 rad/s2 over 20 s, the gyros' torque at the root): the same;
//  4. a reentry (2 km/s, rho 0.002, 25 deg nose up, 120 s) with heat damage: what burns, what breaks.
// Blocks broken are removed and the structure rebuilt (its heat carried over); what no longer joins the main piece
// has departed.
static partial class Program
{
    sealed class SimShip
    {
        public StructShip Src;
        public List<int> BoxBlock = new(); public List<Vector3I> Lo = new(), Hi = new();
        public List<float> Mass = new(); public List<int> Mat = new();
        public List<(int block, Vector3I dir, Vector3I lo, Vector3I hi)> Mounts = new();
        public bool[] Alive; public float[] Health;
        public ShipStructure S; public HullHeat H; public int[] Orig;   // current block -> original
        public float Cs, Co;
        /// <summary>Original blocks already in a piece apart from the ship (the blueprint's extra grids at the start).</summary>
        public HashSet<int> Apart = new();
    }

    static SimShip MakeSim(StructShip sh, string path)
    {
        var sim = new SimShip { Src = sh, Cs = sh.Cs, Co = sh.Co };
        foreach (var line in File.ReadLines(path))
        {
            var p = line.Split(' ');
            if (p[0] == "m") { if (p.Length >= 11 && Environment.GetEnvironmentVariable("NOMOUNTS") == null) sim.Mounts.Add(ParseMount(p)); continue; }
            if (line.StartsWith("#") || p.Length < 9) continue;
            int blk = int.Parse(p[6]);
            sim.BoxBlock.Add(blk); sim.Lo.Add(new Vector3I(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]))); sim.Hi.Add(new Vector3I(int.Parse(p[3]), int.Parse(p[4]), int.Parse(p[5])));
            while (sim.Mass.Count <= blk) { sim.Mass.Add(0f); sim.Mat.Add(StructMaterial.Functional); }
            sim.Mass[blk] = float.Parse(p[7], System.Globalization.CultureInfo.InvariantCulture); sim.Mat[blk] = StructMaterial.Classify(p[8]);
        }
        sim.Alive = Enumerable.Repeat(true, sim.Mass.Count).ToArray();
        sim.Health = Enumerable.Repeat(1f, sim.Mass.Count).ToArray();
        Rebuild(sim);
        var masses = new double[sim.S.Components]; for (int b = 0; b < sim.S.Count; b++) masses[sim.S.Piece[b]] += sim.S.Mass[b];
        int main = Array.IndexOf(masses, masses.Max());
        for (int b = 0; b < sim.S.Count; b++) if (sim.S.Piece[b] != main) sim.Apart.Add(sim.Orig[b]);
        return sim;
    }

    /// <summary>The structure and heat model of the blocks still alive (the old heat carried over by face and block).</summary>
    static void Rebuild(SimShip sim)
    {
        var map = new Dictionary<int, int>(); var orig = new List<int>();
        var bb = new List<int>(); var lo = new List<Vector3I>(); var hi = new List<Vector3I>(); var m = new List<float>(); var mat = new List<int>();
        for (int k = 0; k < sim.BoxBlock.Count; k++)
        {
            int o = sim.BoxBlock[k];
            if (!sim.Alive[o]) continue;
            if (!map.TryGetValue(o, out int c)) { map[o] = c = orig.Count; orig.Add(o); m.Add(sim.Mass[o]); mat.Add(sim.Mat[o]); }
            bb.Add(c); lo.Add(sim.Lo[k]); hi.Add(sim.Hi[k]);
        }
        var mounts = new List<(int, Vector3I, Vector3I, Vector3I)>();
        foreach (var (b, d, ml, mh) in sim.Mounts) if (map.TryGetValue(b, out int c)) mounts.Add((c, d, ml, mh));
        var st = ShipStructure.Build(bb, lo, hi, m, mat, mounts);
        var boxes = new List<(Vector3I, Vector3I)>(); for (int k = 0; k < lo.Count; k++) boxes.Add((lo[k], hi[k]));
        int kk = SnapshotGridAccessor.CellScale(BlockSize);
        var grid = kk <= 1 ? new SnapshotGridAccessor(boxes) : new SnapshotGridAccessor(boxes).CoarsenMajority(kk);
        var surf = new SmoothSurfaceProvider { CellSize = sim.Cs, CellOffset = sim.Co };
        surf.BeginBuild(grid, BlockSize); while (!surf.AddCellBatch(int.MaxValue)) { } surf.FinalizeBuild();
        var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var area = new List<float>(); var blk = new List<int>();
        foreach (var f in surf.Faces) { var raw = f.RawNormal.LengthSquared() > 0.5f ? f.RawNormal : f.Normal; pos.Add(f.Position); nrm.Add(f.Normal); area.Add(f.Area); blk.Add(st.BlockAt(f.Position - raw * (sim.Cs * 0.25f))); }
        var hh = HullHeat.Build(st, pos, nrm, area, blk, sim.Cs);
        if (sim.H != null)
        {
            // carry the heat: faces by place, cores by original block
            var faces = new Dictionary<(int, int, int, int), float>();
            for (int i = 0; i < sim.H.FaceCount; i++) { var p = sim.H.FacePosition(i); faces[((int)MathF.Round(p.X * 8), (int)MathF.Round(p.Y * 8), (int)MathF.Round(p.Z * 8), (int)MathF.Round(sim.H.FaceNormal(i).X * 3 + sim.H.FaceNormal(i).Y * 9 + sim.H.FaceNormal(i).Z * 27))] = sim.H.FaceT[i]; }
            for (int i = 0; i < hh.FaceCount; i++) { var p = hh.FacePosition(i); if (faces.TryGetValue(((int)MathF.Round(p.X * 8), (int)MathF.Round(p.Y * 8), (int)MathF.Round(p.Z * 8), (int)MathF.Round(hh.FaceNormal(i).X * 3 + hh.FaceNormal(i).Y * 9 + hh.FaceNormal(i).Z * 27)), out float t)) hh.FaceT[i] = t; }
            var cores = new Dictionary<int, float>(); for (int b = 0; b < sim.S.Count; b++) cores[sim.Orig[b]] = sim.H.CoreT[b];
            for (int b = 0; b < st.Count; b++) if (cores.TryGetValue(orig[b], out float t)) hh.CoreT[b] = t;
        }
        for (int b = 0; b < st.Count; b++) st.Integrity[b] = sim.Health[orig[b]];
        sim.S = st; sim.H = hh; sim.Orig = orig.ToArray();
    }

    /// <summary>The game's loads for the intact aero model's forces along travel at speed v (the wings' cells that are gone
    /// carry nothing).</summary>
    static StructureEval.Loads GameLoads(SimShip sim, Vector3 travel, float v, AtmosphereState atmo)
    {
        var ship = sim.Src.Aero;
        var r = ship.Model.Compute(new AeroContext(ship.Grid, ship.Surface, travel * v, atmo, ship.Com, BlockSize, Vector3.Zero, -1f, ship.Manifold));
        var L = new StructureEval.Loads { AeroF = r.Force, AeroT = r.Torque, AeroCom = ship.Com, AirVel = travel * v, CellSize = sim.Cs, CellOffset = sim.Co };
        var wings = ship.Model.Wings; var wf = ship.Model.LastWingForces;
        if (wings != null && wf != null) for (int i = 0; i < wings.Count && i < wf.Count; i++) { L.WingCells.Add(wings[i].Cells); L.WingF.Add(wf[i].LiftForce + wf[i].InducedDrag); }
        return L;
    }

    static string Piece(SimShip sim, int piece, out float massT)
    {
        var s = sim.S; double m = 0; var names = new Dictionary<string, int>();
        for (int b = 0; b < s.Count; b++) if (s.Piece[b] == piece) { m += s.Mass[b]; var nm = sim.Src.BlockName[sim.Orig[b]]; names[nm] = names.GetValueOrDefault(nm) + 1; }
        massT = (float)(m / 1000);
        return string.Join(", ", names.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Value}x {kv.Key}"));
    }

    /// <summary>One scenario: each 0.25 s step sets the loads (step(t, sim) -> loads, density, air speed, air temperature),
    /// solves, damages, removes what broke. Reports the first wear, each break and each departure.</summary>
    static void RunScenario(SimShip sim, string name, float duration, Func<float, SimShip, (StructureEval.Loads L, float rho, float tAir, string cond)> step, bool heatDamage)
    {
        Console.WriteLine($"   == {name}");
        // (the evaluation's step: EVAL_DT - the game evaluates at StructureLoads.Rate, 4 Hz; the damage is dt-scaled, so 2 or
        //  8 Hz should end the same - 2026-10-14)
        float dt = float.TryParse(Environment.GetEnvironmentVariable("EVAL_DT"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var edt) && edt > 0f ? edt : 0.25f;
        var dmg = new List<(int, float, bool)>(); var where = new List<(Vector3 at, Vector3 dir, float util)>(); var cuts = new List<StructureEval.CutFail>();
        bool worn = false, tired = false; int breaks = 0, departures = 0, cutsFailed = 0, evalsBreaking = 0; float wornAt = -1f;
        // the main piece's count at the start (a blueprint of several grids starts with several)
        int pieces0 = sim.S.Components;
        float worstSeen = 0f; string worstCond = "";
        for (float t = 0; t < duration; t += dt)
        {
            var (L, rho, tAir, cond) = step(t, sim);
            var s = sim.S; var h = sim.H;
            float speed = L.AirVel.Length();
            if (speed > 1f && h.NeedsPass(L.AirVel)) h.Pass(L.AirVel);
            h.Step(rho, tAir, speed, rho > 1e-6f ? tAir : 3f, dt);
            Array.Copy(h.BlockTempK, s.TempK, s.Count);
            StructureEval.Apply(s, h, L);
            s.SolveFree(L.AngVel);
            if (s.MaxUtil > worstSeen) { worstSeen = s.MaxUtil; worstCond = cond; }
            StructureEval.Damage(s, h, dt, true, heatDamage, StructureEval.BreakUtil, StructureEval.WearPerSecond, StructureEval.MaxBreaks, dmg, where, cuts);
            bool changed = false;
            // (BY SECTION, 2026-10-15: each failed cut whole - its members together, one line)
            foreach (var c in cuts)
            {
                cutsFailed++;
                var names = new Dictionary<string, int>(); float uMax = 0f;
                for (int di = c.First; di < c.First + c.Count; di++) { var nm = sim.Src.BlockName[sim.Orig[dmg[di].Item1]]; names[nm] = names.GetValueOrDefault(nm) + 1; uMax = MathF.Max(uMax, where[di].util); }
                var p = c.At - s.Com;
                string how = uMax >= StructureFatigue.SnapUtil ? "SNAP" : "worn out";
                if (cutsFailed <= 8) Console.WriteLine($"      t={t,5:F2} {cond}: CUT FAILED ({how}) - {c.Count} blocks together at ({p.X:F1},{p.Y:F1},{p.Z:F1}) m across {"xyz"[c.Axis]} ({string.Join(", ", names.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Value}x {kv.Key}"))}; utilisation {c.Util:F2}){(cutsFailed == 1 && wornAt >= 0f ? $" - {t - wornAt:F2} s after the first wear" : "")}");
            }
            if (Environment.GetEnvironmentVariable("CUTDEBUG") != null)
                foreach (var c in cuts)
                {
                    // (debug) what still crosses the cut's plane after its members go: contacts, straddlers, by section
                    var gone = new HashSet<int>(); for (int di = c.First; di < c.First + c.Count; di++) gone.Add(dmg[di].Item1);
                    float Cx(Vector3 v) => c.Axis == 0 ? v.X : c.Axis == 1 ? v.Y : v.Z;
                    for (int k = 0; k < s.ContactCount; k++)
                    {
                        s.ContactPlane(k, out int ax, out float pl); if (ax != c.Axis || MathF.Abs(pl - c.Plane) > 1e-3f) continue;
                        s.ContactBlocks(k, out int ka, out int kb); if (gone.Contains(ka) || gone.Contains(kb)) continue;
                        Console.WriteLine($"         left across: contact {k} {sim.Src.BlockName[sim.Orig[ka]]}-{sim.Src.BlockName[sim.Orig[kb]]} at {s.ContactPoint(k) - s.Com} section {s.ContactSection(k)} util {s.ContactUtil[k]:F2}");
                    }
                    for (int b = 0; b < s.Count; b++) if (!gone.Contains(b) && Cx(s.Lo[b]) < c.Plane - 1e-3f && Cx(s.Hi[b]) > c.Plane + 1e-3f) Console.WriteLine($"         left across: straddler {sim.Src.BlockName[sim.Orig[b]]} at {s.Center[b] - s.Com} util {s.BlockUtil[b]:F2}");
                }
            bool brokeNow = false;
            for (int di = 0; di < dmg.Count; di++)
            {
                var (b, frac, destroy) = dmg[di]; float u = di < where.Count ? where[di].util : 0f;
                int o = sim.Orig[b];
                if (!sim.Alive[o]) continue;
                // (PROGRESSIVE, 2026-10-14: fatigue from the endurance limit, wear past the yield, a break when the health is out)
                if (!tired && !destroy && u > 0f && u <= 1f) { tired = true; Console.WriteLine($"      t={t,5:F2} {cond}: first fatigue - {sim.Src.BlockName[o]} (utilisation {u:F2})"); }
                if (!worn && !destroy && u > 1f) { worn = true; wornAt = t; Console.WriteLine($"      t={t,5:F2} {cond}: first wear - {sim.Src.BlockName[o]} (utilisation {s.MaxUtil:F2}, skin {h.BlockSkinMaxK[b]:F0} K)"); }
                sim.Health[o] -= destroy ? 1f : frac;
                if (destroy || sim.Health[o] <= 0.02f)
                {
                    sim.Alive[o] = false; changed = true; breaks++; brokeNow = true;
                    var p = s.Center[b] - s.Com;
                    string how = u * MathF.Max(MathF.Min(s.Integrity[b], 1f), ShipStructure.MinIntegrity) >= StructureFatigue.SnapUtil ? "SNAP" : u > 0f ? "BREAK (worn out)" : "burnt through";
                    bool inCut = false; foreach (var c in cuts) if (di >= c.First && di < c.First + c.Count) inCut = true;
                    if (!inCut && breaks <= 12) Console.WriteLine($"      t={t,5:F2} {cond}: {how} {sim.Src.BlockName[o]} at ({p.X:F1},{p.Y:F1},{p.Z:F1}) m{(u > 0f ? $" (utilisation {u:F2})" : $" ({h.BlockSkinMaxK[b]:F0} K)")}{(breaks == 1 && wornAt >= 0f ? $" - {t - wornAt:F2} s after the first wear" : "")}");
                }
            }
            if (brokeNow) evalsBreaking++;
            if (changed)
            {
                int before = sim.S.Components;
                Rebuild(sim);
                if (sim.S.Components > before)
                {
                    // the new pieces: all but the heaviest (the ship)
                    var masses = new double[sim.S.Components]; for (int b = 0; b < sim.S.Count; b++) masses[sim.S.Piece[b]] += sim.S.Mass[b];
                    int main = Array.IndexOf(masses, masses.Max());
                    for (int q = 0; q < sim.S.Components; q++)
                    {
                        if (q == main) continue;
                        // (only pieces not seen apart before: the blueprint's own extra grids, and what already left, are not news)
                        bool seen = false;
                        for (int b = 0; b < sim.S.Count && !seen; b++) if (sim.S.Piece[b] == q && sim.Apart.Contains(sim.Orig[b])) seen = true;
                        for (int b = 0; b < sim.S.Count; b++) if (sim.S.Piece[b] == q) sim.Apart.Add(sim.Orig[b]);
                        if (seen) continue;
                        string what = Piece(sim, q, out float mt);
                        if (mt < 0.05f) continue;   // (crumbs)
                        departures++;
                        if (departures <= 8) Console.WriteLine($"      t={t,5:F2} {cond}: DEPARTED {mt:F2} t ({what})");
                    }
                }
            }
            for (int b = 0; b < sim.S.Count; b++) sim.S.Integrity[b] = sim.Health[sim.Orig[b]];
        }
        Console.WriteLine($"      end: {breaks} blocks lost in {cutsFailed} cuts over {evalsBreaking} evaluations, worst utilisation {worstSeen:F2} at {worstCond}");
    }

    static int FailSim(string dataDir, string[] names)
    {
        // (calibration knobs, 2026-10-14: the progressive damage's constants from the environment - WEAR_RATE, WEAR_EXP,
        //  FEEDBACK_EXP, FATIGUE_RATE, SNAP_UTIL)
        static void Knob(string env, Action<float> set) { if (float.TryParse(Environment.GetEnvironmentVariable(env), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) set(v); }
        Knob("WEAR_RATE", v => StructureEval.WearPerSecond = v); Knob("WEAR_EXP", v => StructureFatigue.WearExponent = v);
        Knob("FEEDBACK_EXP", v => StructureFatigue.FeedbackExponent = v); Knob("FATIGUE_RATE", v => StructureFatigue.Rate = v);
        Knob("SNAP_UTIL", v => StructureFatigue.SnapUtil = v);
        var dir = Path.Combine(dataDir, "struct");
        var files = names.Length > 0 ? names.Select(n => Path.Combine(dir, n + ".struct")).ToArray() : new[] { "plane2", "tupolev_tu134", "grasshopper" }.Select(n => Path.Combine(dir, n + ".struct")).ToArray();
        var sea = new AtmosphereState { Density = 1.225, Pressure = 101325, Temperature = 288, SpeedOfSound = 340 };
        var thin = new AtmosphereState { Density = 0.002, Pressure = 200, Temperature = 250, SpeedOfSound = 317 };
        foreach (var f in files)
        {
            if (!File.Exists(f)) { Console.WriteLine($"{f}: missing"); continue; }
            var sh = LoadStruct(f);
            float W = sh.S.TotalMass * 9.81f;
            Vector3 Travel(float deg) { float a = deg * MathF.PI / 180f; return Vector3.Normalize(sh.Nose * MathF.Cos(a) - sh.Up * MathF.Sin(a)); }

            // ── 1. fidelity: exact face pressures vs the game's spreading, same pull ──
            {
                var s = sh.S; var tr = Travel(6f);
                AeroLoads(sh, tr, 250f, sea);
                s.SolveFree(Vector3.Zero);
                var exact = (float[])s.ContactUtil.Clone(); float exactMax = s.MaxUtil;
                var exactF = (Vector3[])s.ExtForce.Clone();
                sh.H.Pass(tr);
                var sim0 = new SimShip { Src = sh, Cs = sh.Cs, Co = sh.Co, S = s, H = sh.H };
                var L = GameLoads(sim0, tr, 250f, sea);
                StructureEval.Apply(s, sh.H, L);
                s.SolveFree(Vector3.Zero);
                var game = s.ContactUtil;
                var topE = Enumerable.Range(0, s.ContactCount).OrderByDescending(k => exact[k]).Take(20).ToHashSet();
                var topG = Enumerable.Range(0, s.ContactCount).OrderByDescending(k => game[k]).Take(20).ToHashSet();
                double num = 0, den = 0; for (int b = 0; b < s.Count; b++) { num += (s.ExtForce[b] - exactF[b]).LengthSquared(); den += exactF[b].LengthSquared(); }
                double se = 0, sg = 0, seg = 0, see = 0, sgg = 0; int nn = s.ContactCount;
                for (int k = 0; k < nn; k++) { se += exact[k]; sg += game[k]; } se /= nn; sg /= nn;
                for (int k = 0; k < nn; k++) { seg += (exact[k] - se) * (game[k] - sg); see += (exact[k] - se) * (exact[k] - se); sgg += (game[k] - sg) * (game[k] - sg); }
                Console.WriteLine($"   fidelity (pull 250 m/s 6 deg): worst {exactMax:F2} exact vs {s.MaxUtil:F2} game; top-20 joints shared {topE.Intersect(topG).Count()}/20; joint utilisation correlation {seg / Math.Sqrt(see * sgg):F3}; block loads differ {Math.Sqrt(num / den) * 100:F0}% (RMS)");
            }

            if (Environment.GetEnvironmentVariable("WINGINFO") != null)
            {
                var ws = sh.Aero.Model.Wings; float S = 0f; if (ws != null) foreach (var w in ws) S += w.PlanformArea;
                Console.WriteLine($"   wings: {ws?.Count ?? 0}, planform {S:F0} m2, mass {sh.S.TotalMass / 1000f:F1} t -> wing loading {sh.S.TotalMass / Math.Max(1f, S):F0} kg/m2");
                foreach (float deg in new[] { 4f, 8f, 12f, 16f, 20f })
                {
                    var tr = Travel(deg); var ship = sh.Aero;
                    var r = ship.Model.Compute(new AeroContext(ship.Grid, ship.Surface, tr * 250f, sea, ship.Com, BlockSize, Vector3.Zero, -1f, ship.Manifold));
                    float lift = Vector3.Dot(r.Force, sh.Up), wl = 0f; var wf = ship.Model.LastWingForces; if (wf != null) foreach (var x in wf) wl += Vector3.Dot(x.LiftForce, sh.Up);
                    float q = 0.5f * 1.225f * 250f * 250f;
                    Console.WriteLine($"      {deg,4:F0} deg: {lift / W:F1} g, CL(wings) {wl / (q * Math.Max(1f, S)):F2}, wings {wl / 1000:F0} kN of {lift / 1000:F0} kN");
                }
                continue;
            }
            // ── 2. pull ramp ──
            if (float.TryParse(Environment.GetEnvironmentVariable("STRENGTH"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var stv)) ShipStructure.Strength = stv;
            var sim = MakeSim(sh, f);
            RunScenario(sim, "pull ramp: 250 m/s at sea level, 2 -> 20 deg over 20 s", 20f, (t, sm) =>
            {
                float deg = 2f + 18f * t / 20f; var tr = Travel(deg);
                var L = GameLoads(sm, tr, 250f, sea);
                float n = Vector3.Dot(L.AeroF, sh.Up) / W;
                return (L, 1.225f, 288f, $"{deg:F1} deg {n:F1} g");
            }, false);
            if (Environment.GetEnvironmentVariable("ONLY") == "pull") continue;
            // ── 2b. FATIGUE (2026-10-14): repeated pulls at 80% of the yield, a brief spike past it, a sustained overload ──
            {
                // the angle at which the intact ship's worst joint reaches a utilisation (bisection on a fresh ship)
                var probe = MakeSim(sh, f);
                float UtilAt(float deg) { var L = GameLoads(probe, Travel(deg), 250f, sea); StructureEval.Apply(probe.S, probe.H, L); probe.S.SolveFree(Vector3.Zero); return probe.S.MaxUtil; }
                float DegFor(float u) { float lo = 0f, hi = 20f; for (int i = 0; i < 30; i++) { float m = 0.5f * (lo + hi); if (UtilAt(m) < u) lo = m; else hi = m; } return 0.5f * (lo + hi); }
                probe.H.Pass(Travel(6f));
                float cruise = 2f, d80 = DegFor(0.8f), d150 = DegFor(1.5f), d120 = DegFor(1.2f);
                float MinHealth(SimShip sm) { float m = 1f; for (int o = 0; o < sm.Health.Length; o++) if (sm.Alive[o] && !sm.Apart.Contains(o)) m = MathF.Min(m, sm.Health[o]); return m; }
                // N pulls: 1 s up, 3 s held at 80% of the yield, 1 s down, 5 s cruising - the worst block's health after each
                const float cycle = 10f; int lastPull = -1; float peak = 0f;
                int pulls = int.TryParse(Environment.GetEnvironmentVariable("PULLS"), out var pv) ? pv : 120;
                sim = MakeSim(sh, f);
                if (pulls > 0)
                RunScenario(sim, $"repeated pulls: 1 s up, 3 s at 80% of the yield ({d80:F1} deg), 1 s down, 5 s at {cruise} deg - {pulls} pulls", pulls * cycle, (t, sm) =>
                {
                    int k = (int)(t / cycle); float c = t - k * cycle;
                    if (k != lastPull && k > 0 && (k % 10 == 0 || k < 3)) Console.WriteLine($"      after {k,3} pulls: worst block health {MinHealth(sm):F3}, the last pull's worst utilisation {peak:F2}");
                    if (k != lastPull) peak = 0f;
                    peak = MathF.Max(peak, sm.S.MaxUtil);
                    lastPull = k;
                    float a = c < 1f ? c : c < 4f ? 1f : c < 5f ? 5f - c : 0f;
                    float deg = cruise + (d80 - cruise) * a;
                    var L = GameLoads(sm, Travel(deg), 250f, sea);
                    return (L, 1.225f, 288f, $"pull {k + 1} {deg:F1} deg {Vector3.Dot(L.AeroF, sh.Up) / W:F1} g");
                }, false);
                if (pulls > 0) Console.WriteLine($"      worst block health at the end {MinHealth(sim):F3}");
                // a brief spike: 0.5 s at 1.5 x the yield (the old instant break) from cruise, then cruise
                sim = MakeSim(sh, f);
                RunScenario(sim, $"brief spike: 0.5 s at 1.5 x the yield ({d150:F1} deg), then {cruise} deg", 5f, (t, sm) =>
                {
                    float deg = t >= 1f && t < 1.5f ? d150 : cruise;
                    var L = GameLoads(sm, Travel(deg), 250f, sea);
                    return (L, 1.225f, 288f, $"{deg:F1} deg {Vector3.Dot(L.AeroF, sh.Up) / W:F1} g");
                }, false);
                Console.WriteLine($"      worst block health after the spike {MinHealth(sim):F3}");
                // a sustained overload: held at 1.2 x the yield
                sim = MakeSim(sh, f);
                RunScenario(sim, $"sustained: held at 1.2 x the yield ({d120:F1} deg)", 30f, (t, sm) =>
                {
                    var L = GameLoads(sm, Travel(d120), 250f, sea);
                    return (L, 1.225f, 288f, $"{t:F1} s {Vector3.Dot(L.AeroF, sh.Up) / W:F1} g, worst health {MinHealth(sm):F2}");
                }, false);
            }
            if (Environment.GetEnvironmentVariable("ONLY") == "fatigue") continue;
            // ── 3. roll flick ramp (no air) ──
            sim = MakeSim(sh, f);
            RunScenario(sim, "roll flick ramp: 0 -> 40 rad/s2 over 20 s (gyro torque at the root)", 20f, (t, sm) =>
            {
                float alpha = 40f * t / 20f;
                var s = sm.S;
                // the torque that makes alpha about the nose (the main piece's inertia)
                Array.Clear(s.ExtForce); Array.Clear(s.ExtMoment);
                var dirs = new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ }; var cols = new Vector3[3];
                for (int i = 0; i < 3; i++) { s.ExtMoment[s.Root] = dirs[i]; s.RigidAccel(Vector3.Zero); cols[i] = s.AngAccel; }
                var torque = Inv3(cols[0], cols[1], cols[2], sh.Nose * alpha);
                var L = new StructureEval.Loads { GyroT = torque };
                L.GyroBlock.Add(s.Root);
                return (L, 0f, 3f, $"{alpha:F1} rad/s2");
            }, false);
            // ── 4. reentry with heat damage ──
            sim = MakeSim(sh, f);
            RunScenario(sim, "reentry: 2 km/s, rho 0.002, 25 deg nose up, 120 s, heat damage on", 120f, (t, sm) =>
            {
                var tr = Travel(25f);
                var L = GameLoads(sm, tr, 2000f, thin);
                float g = L.AeroF.Length() / W;
                return (L, 0.002f, 250f, $"{t:F0} s ({g:F1} g decel, skin {sm.H.MaxSkin(out _):F0} K)");
            }, true);
        }
        return 0;
    }
}
