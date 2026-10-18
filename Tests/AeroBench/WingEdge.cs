using System.IO;
using AeroMod;

// AeroBench gate: THE LEADING EDGE BY THE AIR (2026-10-18, fix a-wing-edge). A wing's chord axis is a positive grid axis;
// the low end always led - a ship flying toward +chord had its lift at 3/4 chord from its real leading edge, its Mach
// tuck forward and its sweep from the trailing edge. The same swept, tapered wing built mirrored (flying toward -X and
// toward +X; again with the chord along Z) through the real detector and the real wing model: the same lift, the same
// aerodynamic centre a quarter chord behind its own leading edge, the same leading-edge sweep, the Mach tuck downstream
// in both; near-spanwise flow slides the force point through the mid chord (no jump). Then a real ship (the Pelican)
// mirrored along its nose: the same wing lift and wing pitching moment.
//   dotnet run -c Release -- wingedge        (the gate's checks alone)
//   dotnet run -c Release -- wingprobe [ship ...]   (wing CoP and pitching moment at Mach 0.5 / 1.2, for before/after)
static partial class Program
{
    /// <summary>A swept (30 deg leading edge), tapered (chord 24 -> 11 cells) flat plate, 40 cells of span, one cell thick,
    /// normal +Y: its leading edge at the low end of the chord axis (x, or z when swapped); mirrored: at the high end.</summary>
    static List<(Vector3I, Vector3I)> EdgeWing(bool mirror, bool chordAlongZ)
    {
        var boxes = new List<(Vector3I, Vector3I)>();
        for (int s = 0; s < 40; s++)
        {
            int le = (int)MathF.Round(s * MathF.Tan(30f * MathF.PI / 180f)), chord = 24 - s / 3;
            int c0 = le, c1 = le + chord - 1;
            if (mirror) (c0, c1) = (-c1, -c0);
            boxes.Add(chordAlongZ ? (new Vector3I(s, 0, c0), new Vector3I(s, 0, c1)) : (new Vector3I(c0, 0, s), new Vector3I(c1, 0, s)));
        }
        return boxes;
    }

    static int WingEdgeChecks(AtmosphereState atmo0)
    {
        int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; Console.WriteLine($"   {(ok ? "ok  " : "FAIL")} {what}"); }
        var atmo = new AtmosphereState { Density = 1.225, Pressure = 101325, Temperature = 288, SpeedOfSound = 340 };
        float bs = BlockSize; BlockSize = 0.5f;   // (a small grid: 0.25 m cells, no coarsening)
        try
        {
            foreach (bool alongZ in new[] { false, true })
            {
                var chordGrid = alongZ ? Vector3.UnitZ : Vector3.UnitX;
                string ax = alongZ ? "Z" : "X";
                // per build: the wing, its flight direction (toward its real leading edge), and per Mach the result
                var r = new (LiftingSurface w, Vector3 dir, float le, float te, WingForceResult m05, WingForceResult m12)[2];
                for (int k = 0; k < 2; k++)
                {
                    bool mirror = k == 1;
                    var ship = Build("edgewing", EdgeWing(mirror, alongZ));
                    var wings = ship.Model.Wings;
                    if (wings == null || wings.Count == 0) { Check(false, $"wing edge ({ax}): no wing detected{(mirror ? " (mirrored)" : "")}"); return fails; }
                    var w = wings[0]; foreach (var x in wings) if (x.PlanformArea > w.PlanformArea) w = x;
                    var dir = mirror ? chordGrid : -chordGrid;
                    // the real leading and trailing edges along the flight: per span station the foremost / rearmost cell
                    // face, averaged over the span
                    var fore = new Dictionary<int, float>(); var aft = new Dictionary<int, float>();
                    float cs = ship.Surface.CellSize, co = ship.Surface.CellOffset;
                    foreach (var c in ship.Grid.EnumerateOccupiedCells())
                    {
                        var p = new Vector3((c.X + co) * cs, (c.Y + co) * cs, (c.Z + co) * cs);
                        int s = alongZ ? c.X : c.Z; float f = Vector3.Dot(p, dir);
                        fore[s] = fore.TryGetValue(s, out var e0) ? MathF.Max(e0, f) : f;
                        aft[s] = aft.TryGetValue(s, out var e1) ? MathF.Min(e1, f) : f;
                    }
                    float le = fore.Values.Average() + 0.5f * cs, te = aft.Values.Average() - 0.5f * cs;
                    WingForceResult At(float mach)
                    {
                        float a = 5f * MathF.PI / 180f;
                        var v = (dir * MathF.Cos(a) - w.Normal * MathF.Sin(a)) * (mach * 340f);
                        var ctx = new AeroContext(null, null, v, atmo, ship.Com, BlockSize, Vector3.Zero, -1f, null, Vector3.Zero);
                        return new CompressibleWingModel().ComputeWingForces(new[] { w }, ctx)[0];
                    }
                    r[k] = (w, dir, le, te, At(0.5f), At(1.2f));
                }
                var (wo, dO, leO, teO, o05, o12) = r[0];
                var (wm, dM, leM, teM, m05, m12) = r[1];
                float deg = 180f / MathF.PI;
                // the detector: orientation-free - the mirror swaps the edges' sweeps
                Check(MathF.Abs(wo.SweepLo - wm.SweepHi) < 1e-4f && MathF.Abs(wo.SweepHi - wm.SweepLo) < 1e-4f && MathF.Abs(wo.SweepLo * deg - 30f) < 3f && wo.SweepLo - wo.SweepHi > 0.15f,
                    $"wing edge ({ax}): edges' sweeps {wo.SweepLo * deg:F1}/{wo.SweepHi * deg:F1} deg, mirrored {wm.SweepLo * deg:F1}/{wm.SweepHi * deg:F1} (swapped; the leading edge ~30)");
                Check(o05.Lead == -1f && m05.Lead == 1f, $"wing edge ({ax}): lead {o05.Lead:F2} flying -{ax}, {m05.Lead:F2} flying +{ax} (-1 / +1)");
                float sO = wo.SweepFor(o05.Lead) * deg, sM = wm.SweepFor(m05.Lead) * deg;
                Check(MathF.Abs(sO - sM) < 1e-3f && MathF.Abs(sO - 30f) < 3f, $"wing edge ({ax}): the leading edge's sweep {sO:F2} deg, mirrored {sM:F2} (the same, ~30)");
                foreach (var (mach, ro, rm) in new[] { (0.5f, o05, m05), (1.2f, o12, m12) })
                {
                    float lo = ro.LiftForce.Length(), lm = rm.LiftForce.Length();
                    Check(lo > 0f && MathF.Abs(lo - lm) <= 1e-4f * lo, $"wing edge ({ax}) M{mach}: lift {lo:F0} N, mirrored {lm:F0} N (the same)");
                    // the force point behind the real leading edge, as a share of the real mean chord
                    float fo = (leO - Vector3.Dot(ro.ApplicationPoint, dO)) / (leO - teO), fm = (leM - Vector3.Dot(rm.ApplicationPoint, dM)) / (leM - teM);
                    Check(MathF.Abs(fo - fm) < 1e-4f, $"wing edge ({ax}) M{mach}: force point {fo:F3} chord behind the leading edge, mirrored {fm:F3} (the same)");
                    if (mach == 0.5f)
                    {
                        // (a quarter chord, plus the AoA shift at 5 deg - 0.25 x (5/90)^2 - and the taper's centroid offset)
                        Check(fo > 0.2f && fo < 0.3f, $"wing edge ({ax}) M0.5: force point at {fo:F3} chord (a quarter chord: 0.2 .. 0.3)");
                        float q = Vector3.Dot(ro.ApplicationPoint - wo.Centroid, dO), qe = wo.AcOffset - 0.25f * wo.MeanChord * (5f / 90f) * (5f / 90f);
                        Check(MathF.Abs(q - qe) < 1e-3f * wo.MeanChord, $"wing edge ({ax}) M0.5: force point {q:F3} m ahead of the mid chord ({qe:F3}: the quarter chord, the AoA shift aft)");
                    }
                }
                // the Mach tuck: aft (downstream) in both
                float tO = Vector3.Dot(o12.ApplicationPoint - o05.ApplicationPoint, dO) / wo.MeanChord, tM = Vector3.Dot(m12.ApplicationPoint - m05.ApplicationPoint, dM) / wm.MeanChord;
                Check(tO < -0.15f && MathF.Abs(tO - tM) < 1e-4f, $"wing edge ({ax}): Mach tuck M0.5 -> M1.2 moves the force point {tO:F3} chord along the flight, mirrored {tM:F3} (aft in both)");
                if (!alongZ)
                {
                    // near-spanwise flow (sideslip): the force point slides through the mid chord - no jump as the chordwise
                    // air changes sign, and exactly the mid chord with none
                    Vector3 Cp(float chordShare)
                    {
                        var v = Vector3.Normalize(wo.SpanAxis + chordGrid * chordShare - wo.Normal * 0.09f) * 100f;
                        var ctx = new AeroContext(null, null, v, atmo, Vector3.Zero, BlockSize, Vector3.Zero, -1f, null, Vector3.Zero);
                        return new CompressibleWingModel().ComputeWingForces(new[] { wo }, ctx)[0].ApplicationPoint;
                    }
                    float jump = Vector3.Distance(Cp(0.005f), Cp(-0.005f)) / wo.MeanChord, mid = Vector3.Dot(Cp(0f) - wo.Centroid, chordGrid);
                    float full = Vector3.Distance(Cp(1f), Cp(-1f)) / wo.MeanChord;
                    Check(jump < 0.05f && MathF.Abs(mid) < 1e-4f && full > 0.4f,
                        $"wing edge sideslip: chordwise air +-0.5 % moves the force point {jump:F3} chord (no jump), none: {mid:F4} m off the mid chord, +-45 deg: {full:F2} chord apart");
                }
            }
        }
        finally { BlockSize = bs; }

        // a real ship mirrored along its nose: the same wing lift, the same wing pitching moment (about its own CoM)
        var path = Path.Combine(dataDir, "pelican.boxes");
        if (File.Exists(path))
        {
            var boxes = LoadBoxes(path);
            var ship = Build("pelican", boxes);
            var (nose, up) = FlightAxes(ship, atmo, boxes);
            // mirror: the nose's grid axis flipped (cells c -> -c - 1 along it: the coarse cells mirror onto coarse cells)
            Vector3I M(Vector3I c) => new Vector3I(nose.X != 0 ? -c.X - 1 : c.X, nose.Y != 0 ? -c.Y - 1 : c.Y, nose.Z != 0 ? -c.Z - 1 : c.Z);
            var mb = boxes.Select(b => { var a = M(b.Item1); var z = M(b.Item2); return (Vector3I.Min(a, z), Vector3I.Max(a, z)); }).ToList();
            var mship = Build("pelican-mirrored", mb);
            if (Environment.GetEnvironmentVariable("EDGE_DEBUG") != null)   // (the wings of both, and below each wing's result)
                foreach (var s in new[] { ship, mship })
                {
                    Console.WriteLine($"   {s.Name}: nose {nose} com {s.Com}");
                    foreach (var w in s.Model.Wings) { var cb = CompressibleWingModel.CamberOf(w); Console.WriteLine($"      S {w.PlanformArea:F2} span {w.Span:F2} c {w.MeanChord:F2} n {w.Normal} sp {w.SpanAxis} ch {w.ChordAxis} sw {w.SweepLo * 57.3f:F1}/{w.SweepHi * 57.3f:F1} cen-com {w.Centroid - s.Com} camber {cb.lo:F4}/{cb.hi:F4} tc {w.ThicknessRatio:F3} cells {w.Cells.Count}"); }
                }
            foreach (float mach in new[] { 0.5f, 1.2f })
            {
                (float lift, float moment, float lead) Wings(Ship s, Vector3 n)
                {
                    float a = 4f * MathF.PI / 180f;
                    var v = (n * MathF.Cos(a) - up * MathF.Sin(a)) * (mach * 340f);
                    var ctx = new AeroContext(s.Grid, s.Surface, v, atmo, s.Com, BlockSize, Vector3.Zero, -1f, s.Manifold, Vector3.Zero);
                    var wf = new CompressibleWingModel().ComputeWingForces(CollectionsMarshal.AsSpan(s.Model.Wings), ctx);
                    float L = 0f, Mo = 0f, ld = 0f;
                    // (the moment about the pitch axis; the mirror flips the nose, so pitch = nose x up flips: its moment
                    //  about -pitch is the original's about pitch)
                    var p = Vector3.Normalize(Vector3.Cross(n, up));
                    if (Environment.GetEnvironmentVariable("EDGE_DEBUG") != null) foreach (var f in wf) Console.WriteLine($"      {s.Name} M{mach}: L {f.LiftForce.Length():F0} CL {f.CL:F4} a {f.AlphaEffective * 57.3f:F2} lead {f.Lead:F2} eff {f.Efficiency:F3} at {f.ApplicationPoint - s.Com}");
                    foreach (var f in wf) { var F = f.LiftForce + f.InducedDrag; L += Vector3.Dot(F, up); Mo += Vector3.Dot(Vector3.Cross(f.ApplicationPoint - s.Com, F), p); ld += f.Lead * Vector3.Dot(f.LiftForce, up); }
                    return (L, Mo, ld / MathF.Max(1f, MathF.Abs(L)));
                }
                var o = Wings(ship, nose); var m = Wings(mship, -nose);
                // (exact but for float order: 0.1 %; the old model put the mirrored ship's lift at 3/4 chord - ~25 % off)
                bool ok = MathF.Abs(o.lift - m.lift) <= 1e-3f * MathF.Abs(o.lift) && MathF.Abs(o.moment - m.moment) <= 1e-3f * MathF.Abs(o.moment) + 1f
                    && MathF.Abs(o.lead + m.lead) < 1e-3f && o.lead < -0.9f;
                Check(ok, $"pelican mirrored along its nose, M{mach} 4 deg: wing lift {o.lift / 1000f:F1} / {m.lift / 1000f:F1} kN, wing pitching moment {o.moment / 1000f:F2} / {m.moment / 1000f:F2} kN m, lead {o.lead:F2} / {m.lead:F2} (the same; the lead opposite)");
            }
        }
        return fails;
    }

    /// <summary>The wings' centre of pressure along the nose (m ahead of the CoM, lift-weighted) and pitching moment, at
    /// 4 deg and Mach 0.5 / 1.2 - only APIs the old model had too (the before/after of fix a-wing-edge).</summary>
    static int WingProbe(string dataDir, string[] names)
    {
        var atmo = new AtmosphereState { Density = 1.225, Pressure = 101325, Temperature = 288, SpeedOfSound = 340 };
        foreach (var name in names)
        {
            var path = Directory.GetFiles(dataDir, name + ".boxes", SearchOption.AllDirectories).FirstOrDefault();
            if (path == null) { Console.WriteLine($"   {name}: no data"); continue; }
            var boxes = LoadBoxes(path);
            var ship = Build(name, boxes);
            var (nose, up) = FlightAxes(ship, atmo, boxes);
            var pitch = Vector3.Normalize(Vector3.Cross(nose, up));
            Console.WriteLine($"   {name}: nose {nose} up {up}, {ship.Model.Wings?.Count ?? 0} wings");
            if (ship.Model.Wings != null)
                foreach (var w in ship.Model.Wings)
                    Console.WriteLine($"      wing S {w.PlanformArea,6:F1} chord {w.MeanChord,4:F1} axis {w.ChordAxis} (nose . chord {Vector3.Dot(nose, w.ChordAxis):F0})");
            foreach (float mach in new[] { 0.5f, 1.2f })
            {
                float a = 4f * MathF.PI / 180f;
                var v = (nose * MathF.Cos(a) - up * MathF.Sin(a)) * (mach * 340f);
                var ctx = new AeroContext(ship.Grid, ship.Surface, v, atmo, ship.Com, BlockSize, Vector3.Zero, -1f, ship.Manifold, Vector3.Zero);
                var wf = new CompressibleWingModel().ComputeWingForces(CollectionsMarshal.AsSpan(ship.Model.Wings), ctx);
                float L = 0f, xL = 0f, Mo = 0f;
                for (int i = 0; i < wf.Count; i++)
                {
                    var f = wf[i]; var F = f.LiftForce + f.InducedDrag;
                    float l = Vector3.Dot(f.LiftForce, up);
                    L += l; xL += l * Vector3.Dot(f.ApplicationPoint - ship.Com, nose);
                    Mo += Vector3.Dot(Vector3.Cross(f.ApplicationPoint - ship.Com, F), pitch);
                    Console.WriteLine($"      M{mach} wing {i}: lift {l / 1000f,8:F1} kN at {Vector3.Dot(f.ApplicationPoint - ship.Com, nose),6:F2} m ahead of the CoM");
                }
                Console.WriteLine($"      M{mach}: wing lift {L / 1000f:F1} kN, centre of pressure {(MathF.Abs(L) > 1f ? xL / L : 0f):F2} m ahead of the CoM, pitching moment {Mo / 1000f:F1} kN m (+ nose up)");
            }
        }
        return 0;
    }
}
