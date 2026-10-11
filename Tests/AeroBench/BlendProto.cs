// BLENDED LIFT - a prototype, offline only (the user, 2026-10-10: "I wonder if we should consider blending shapes?",
// "see what you can come up with, eval against all our bps"). Not used by the mod.
//
// Today a part of a ship either IS a wing (a thin plate the detector finds: lift slope, stall, induced drag) or is hull
// (face pressure: draggy, brick-like lift) - a hard line. A blended delta like the Diamondback gets 22 % of its planform
// as wings. The prototype scores every column of the ship seen from above (along its lift axis) by how thin it is for its
// local chord - thickness (top to bottom: a hollow fuselage reads thick, stacked plates read as one thick column) over
// the chord at that span station - 1 thin, 0 thick, and treats the scored planform as ONE equivalent wing: its span,
// its aspect ratio, the low-aspect-ratio lift slope (Helmbold: delta and blended bodies lift by span, not area) and a
// max lift that grows for low aspect ratios (their vortex lift, stalling late). Compared with the CURRENT model's own
// maximum lift (wings and hull, its face model) from an angle-of-attack sweep, as stall speeds at 1 g and 2 g.
using System.Globalization;
using AeroMod;
using System.IO;

static partial class Program
{
    public static double BlendThin = 0.08, BlendThick = 0.22;   // thickness / local chord: score 1 at or under Thin, 0 at or over Thick

    sealed class BlendResult
    {
        public string Name; public float Mass; public int Blocks;
        public Vector3 Nose, Up, Span; public float Length, Width, Height;
        public float Planform, Seff, Beff, AR, CLa, CLmax, AlphaStall;
        public float CurLmax100, CurAlpha, CurWings, CurWingArea, CurL12, CurD12, BestLD, BestLDAlpha, D0;
    }

    static (Vector3 nose, Vector3 up) FlightAxes(Ship ship, AtmosphereState atmo, List<(Vector3I, Vector3I)> boxes)
    {
        // up: the axis the ship is thinnest along (a plane is flat); nose: of the other two, the one with the least drag per
        // frontal area going that way (the current model's own) - both signs tried
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (var (a, b) in boxes) { lo = Vector3.Min(lo, new Vector3(a.X, a.Y, a.Z)); hi = Vector3.Max(hi, new Vector3(b.X, b.Y, b.Z)); }
        var ext = hi - lo;
        var axes = new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        int upI = ext.Y <= ext.X && ext.Y <= ext.Z ? 1 : ext.X <= ext.Z ? 0 : 2;
        Vector3 bestNose = -Vector3.UnitZ; double best = double.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            if (i == upI) continue;
            foreach (int sg in new[] { -1, 1 })
            {
                var d = axes[i] * sg;
                AeroResult r = default;
                for (int k = 0; k < 2; k++) r = ship.Model.Compute(new AeroContext(ship.Grid, ship.Surface, d * 100f, atmo, ship.Com, BlockSize, Vector3.Zero, -1f, ship.Manifold));
                double cd = r.FrontalArea > 0.01f ? r.DragMagnitude / r.FrontalArea : double.MaxValue;
                if (cd < best) { best = cd; bestNose = d; }
            }
        }
        return (bestNose, axes[upI]);
    }

    static BlendResult BlendOne(string name, List<(Vector3I, Vector3I)> boxes, float mass, AtmosphereState atmo, bool sweepCurrent)
    {
        var res = new BlendResult { Name = name, Mass = mass, Blocks = boxes.Count };
        var ship = Build(name, boxes);
        var (nose, up) = FlightAxes(ship, atmo, boxes);
        var span = Vector3.Cross(up, nose);
        res.Nose = nose; res.Up = up; res.Span = span;
        res.CurWings = ship.Model.Wings?.Count ?? 0;
        if (ship.Model.Wings != null) foreach (var w in ship.Model.Wings) res.CurWingArea += w.PlanformArea;
        float cell = 0.25f;
        // columns: (span index, chord index) -> the column's top and bottom along up (cells)
        int Comp(Vector3I v, Vector3 axis) => (int)MathF.Round(v.X * axis.X + v.Y * axis.Y + v.Z * axis.Z);
        var cols = new Dictionary<(int s, int c), (int lo, int hi)>();
        foreach (var (a, b) in boxes)
        {
            // the box's ranges along span, chord (nose) and up
            int s0 = Comp(a, span), s1 = Comp(b, span); if (s0 > s1) (s0, s1) = (s1, s0);
            int c0 = Comp(a, nose), c1 = Comp(b, nose); if (c0 > c1) (c0, c1) = (c1, c0);
            int u0 = Comp(a, up), u1 = Comp(b, up); if (u0 > u1) (u0, u1) = (u1, u0);
            for (int s = s0; s <= s1; s++)
                for (int c = c0; c <= c1; c++)
                    cols[(s, c)] = cols.TryGetValue((s, c), out var e) ? (Math.Min(e.lo, u0), Math.Max(e.hi, u1)) : (u0, u1);
        }
        // the chord at each span station: leading to trailing edge
        var chord = new Dictionary<int, (int lo, int hi)>();
        foreach (var k in cols.Keys) chord[k.s] = chord.TryGetValue(k.s, out var e) ? (Math.Min(e.lo, k.c), Math.Max(e.hi, k.c)) : (k.c, k.c);
        double seff = 0; int sMin = int.MaxValue, sMax = int.MinValue;
        foreach (var (k, uv) in cols)
        {
            float t = (uv.hi - uv.lo + 1) * cell;
            var ch = chord[k.s]; float c = (ch.hi - ch.lo + 1) * cell;
            double tau = t / Math.Max(cell, c);
            double sc = Math.Clamp((BlendThick - tau) / (BlendThick - BlendThin), 0, 1);
            seff += sc * cell * cell;
            if (sc > 0.3) { sMin = Math.Min(sMin, k.s); sMax = Math.Max(sMax, k.s); }
        }
        res.Planform = cols.Count * cell * cell;
        res.Seff = (float)seff;
        res.Beff = sMax >= sMin ? (sMax - sMin + 1) * cell : 0f;
        res.AR = res.Seff > 0.01f ? res.Beff * res.Beff / res.Seff : 0f;
        res.CLa = (float)(2 * Math.PI * res.AR / (2 + Math.Sqrt(res.AR * res.AR + 4)));
        res.CLmax = 1.0f + 0.25f * Math.Clamp((2.5f - res.AR) / 2f, 0f, 1f);
        res.AlphaStall = res.CLa > 0.01f ? res.CLmax / res.CLa * 57.3f : 0f;
        // the CURRENT model's maximum lift along up (wings and hull), nose up 0..40 deg at 100 m/s, sea level
        if (sweepCurrent)
        {
            var sea = new AtmosphereState { Density = 1.225, Pressure = 101325, Temperature = 288, SpeedOfSound = 340 };
            for (int ai = 0; ai <= 16; ai++)
            {
                float al = ai * 2.5f / 57.3f;
                var v = (nose * MathF.Cos(al) - up * MathF.Sin(al)) * 100f;   // (nose up: the air from below)
                AeroResult r = default;
                for (int k = 0; k < 2; k++) r = ship.Model.Compute(new AeroContext(ship.Grid, ship.Surface, v, sea, ship.Com, BlockSize, Vector3.Zero, -1f, ship.Manifold));
                var vh = Vector3.Normalize(v);
                var liftV = r.Force - Vector3.Dot(r.Force, vh) * vh;
                float lift = Vector3.Dot(liftV, Vector3.Normalize(up - Vector3.Dot(up, vh) * vh));
                if (lift > res.CurLmax100) { res.CurLmax100 = lift; res.CurAlpha = ai * 2.5f; }
                float drag = -Vector3.Dot(r.Force, vh);
                if (ai == 5) { res.CurL12 = lift; res.CurD12 = drag; }   // (12.5 deg)
                if (ai == 0) res.D0 = drag;
                if (ai <= 8 && drag > 1f && lift / drag > res.BestLD) { res.BestLD = lift / drag; res.BestLDAlpha = ai * 2.5f; }
            }
        }
        var lo3 = new Vector3(float.MaxValue); var hi3 = new Vector3(float.MinValue);
        foreach (var (a, b) in boxes) { lo3 = Vector3.Min(lo3, new Vector3(a.X, a.Y, a.Z)); hi3 = Vector3.Max(hi3, new Vector3(b.X, b.Y, b.Z) + Vector3.One); }
        var e3 = (hi3 - lo3) * cell;
        res.Length = MathF.Abs(Vector3.Dot(e3, nose)); res.Width = MathF.Abs(Vector3.Dot(e3, span)); res.Height = MathF.Abs(Vector3.Dot(e3, up));
        return res;
    }

    static string Stall(float mass, float g, float liftPerQ)
    {
        if (!(mass > 0) || !(liftPerQ > 0)) return "-";
        float v = MathF.Sqrt(2f * mass * g / (1.225f * liftPerQ));
        return v > 9999 ? ">9999" : $"{v:F0}";
    }

    static int Blend(string dataDir, AtmosphereState atmo)
    {
        Console.WriteLine($"   BLENDED LIFT prototype (thin {BlendThin} .. thick {BlendThick} thickness/chord) vs the current model's max lift (wings + hull, 0-40 deg)");
        Console.WriteLine("   ship                                  mass t  wings wing m2 | plan m2 S_eff AR  | stall 2g now  blended | lift @12.5deg/weight@2g now (L/D)  blended");
        var files = Directory.GetFiles(Path.Combine(dataDir, "bp"), "*.boxes").Concat(Directory.GetFiles(dataDir, "*.boxes")).OrderBy(x => x).ToList();
        foreach (var path in files)
        {
            string id = Path.GetFileNameWithoutExtension(path);
            float mass = 0f; string title = id;
            foreach (var l in File.ReadLines(path).Take(3))
            {
                var m = System.Text.RegularExpressions.Regex.Match(l, @"mass\s+([0-9.]+)"); if (m.Success) mass = float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var t = System.Text.RegularExpressions.Regex.Match(l, @"from blueprint '([^']*)'"); if (t.Success) title = t.Groups[1].Value;
            }
            var boxes = LoadBoxes(path);
            if (boxes.Count > 40000) { Console.WriteLine($"   {Trunc(title, 36),-36}  skipped ({boxes.Count} boxes: a capital ship)"); continue; }
            BlendResult r;
            try { r = BlendOne(id, boxes, mass, atmo, true); }
            catch (Exception e) { Console.WriteLine($"   {Trunc(title, 36),-36}  failed: {e.GetType().Name} {e.Message}"); continue; }
            float curLq = r.CurLmax100 / (0.5f * 1.225f * 100f * 100f);   // (the current max lift over q: the S x CLmax it has)
            float blLq = r.Seff * r.CLmax;
            // lift at 12.5 deg and 200 m/s against the weight at 2 g: now (wings + hull, its L/D) and blended (CLa x alpha, under CLmax)
            float q200 = 0.5f * 1.225f * 200f * 200f, w2 = mass * 19.62f;
            float now12 = r.CurL12 * 4f, bl12 = q200 * r.Seff * MathF.Min(r.CLmax, r.CLa * 12.5f / 57.3f);
            string Ratio(float l) => w2 > 0 ? $"{l / w2:0.00}" : "-";
            // the drag at zero lift, as a coefficient on the frontal area along the nose (Cd0), and the best L/D 0-20 deg
            Console.WriteLine($"   {Trunc(title, 36),-36}  {mass / 1000f,6:F0}  {r.CurWings,5} {r.CurWingArea,7:F0} | {r.Planform,7:F0} {r.Seff,5:F0} {r.AR,3:F1} | {Stall(mass, 19.62f, curLq),5} {Stall(mass, 19.62f, blLq),7}   | {Ratio(now12),9} ({(r.CurD12 > 0 ? r.CurL12 / r.CurD12 : 0),4:F1})      {Ratio(bl12),6}   | best L/D {r.BestLD,4:F1} at {r.BestLDAlpha,4:F1} deg  D0 {r.D0 / 1000f / 4f * 4f,7:F0} kN @100m/s  frontal Cd {r.D0 / (0.5f * 1.225f * 100f * 100f) / Math.Max(1f, r.Width * r.Height),4:F2}");
        }
        // the shapes that must NOT gain: a cube, a brick, stacked plates; and the plate that must equal a wing
        Console.WriteLine("   gaming check (scored planform, S_eff; a stack should not exceed one plate):");
        BlockSize = 2.5f;
        (Vector3I, Vector3I) Plate(int y) => (new Vector3I(0, y, 0), new Vector3I(63, y + 1, 31));
        var shapes = new List<(string, List<(Vector3I, Vector3I)>)>
        {
            ("cube 4 m", new() { (Vector3I.Zero, new Vector3I(15, 15, 15)) }),
            ("brick 10x4x4 m", new() { (Vector3I.Zero, new Vector3I(15, 15, 39)) }),
            ("plate 16x0.5x8 m", new() { Plate(0) }),
        };
        foreach (int n in new[] { 3, 6 })
        {
            var l = new List<(Vector3I, Vector3I)>();
            for (int k = 0; k < n; k++) l.Add(Plate(k * 4));
            l.Add((new Vector3I(0, 0, 28), new Vector3I(3, (n - 1) * 4 + 1, 31)));
            shapes.Add(($"{n} plates stacked 1 m apart", l));
        }
        foreach (var (nm, bx) in shapes)
        {
            var r = BlendOne(nm, bx, 0, atmo, false);
            Console.WriteLine($"     {nm,-30} planform {r.Planform,6:F0} m2  S_eff {r.Seff,6:F1} m2  ({(r.Planform > 0 ? r.Seff / r.Planform * 100 : 0),3:F0} %)  AR {r.AR:F1}");
        }
        return 0;
    }

    // zero-lift drag split: base suction (CpBase off), skin friction (CfSkin off), the rest windward pressure; along the
    // nose at 100 m/s, sea level - and the windward pressure's frontal area at full Cd (how much of the front is "wall")
    static int DragSplit(string dataDir, AtmosphereState atmo, string[] names)
    {
        var sea = new AtmosphereState { Density = 1.225, Pressure = 101325, Temperature = 288, SpeedOfSound = 340 };
        Console.WriteLine("   ship                       D0 kN   base kN  skin kN  pressure kN  -> pressure CdA m2  (frontal m2)");
        foreach (var nm in names)
        {
            var path = Directory.GetFiles(dataDir, nm + ".boxes", SearchOption.AllDirectories).FirstOrDefault();
            if (path == null) { Console.WriteLine($"   {nm}: no file"); continue; }
            var boxes = LoadBoxes(path);
            var ship = Build(nm, boxes);
            var (nose, up) = FlightAxes(ship, atmo, boxes);
            var inner = (AeroMod.DampedShadowedDragModel)ship.Model.InnerModel;
            float D(float cpBase, float cf)
            {
                float b0 = inner.CpBase, c0 = inner.CfSkin; inner.CpBase = cpBase; inner.CfSkin = cf;
                AeroResult r = default;
                for (int k = 0; k < 2; k++) r = ship.Model.Compute(new AeroContext(ship.Grid, ship.Surface, nose * 100f, sea, ship.Com, BlockSize, Vector3.Zero, -1f, ship.Manifold));
                inner.CpBase = b0; inner.CfSkin = c0;
                return r.DragMagnitude;
            }
            {
                float dsA = float.Parse(Environment.GetEnvironmentVariable("AERO_DSALPHA") ?? "0", CultureInfo.InvariantCulture) / 57.3f;
                var cx = new AeroContext(ship.Grid, ship.Surface, (nose * MathF.Cos(dsA) - up * MathF.Sin(dsA)) * 100f, sea, ship.Com, BlockSize, Vector3.Zero, -1f, ship.Manifold);
                AeroResult tb = default; for (int k = 0; k < 2; k++) tb = ship.Model.Compute(cx);
                var exr = inner.ExactTableValue(cx, null);
                Console.WriteLine($"   {nm}: table {tb.DragMagnitude / 1000:F1} kN (used table {ship.Model.UsedTable}), exact {exr.DragMagnitude / 1000:F1} kN");
                if (ship.Model.Wings != null)
                {
                    var wm = new AeroMod.CompressibleWingModel();
                    var wf = wm.ComputeWingForces(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(ship.Model.Wings), cx);
                    for (int i = 0; i < wf.Count; i++)
                    {
                        var w = ship.Model.Wings[i];
                        Console.WriteLine($"      wing {i}: S {w.PlanformArea,5:F1} chord {w.MeanChord,4:F1}  AR own {w.AspectRatio:F1} system {wm.SystemAR(i):F1}  alpha {wf[i].AlphaEffective * 57.3f,5:F2} deg  CL {wf[i].CL:F3}  lift {wf[i].LiftForce.Length() / 1000,5:F1} kN  drag {wf[i].InducedDrag.Length() / 1000,5:F1} kN  e {wf[i].Efficiency:F2}");
                    }
                }
            }
            float all = D(inner.CpBase, inner.CfSkin), noBase = D(0f, inner.CfSkin), noBoth = D(0f, 0f);
            AeroResult rr = ship.Model.Compute(new AeroContext(ship.Grid, ship.Surface, nose * 100f, sea, ship.Com, BlockSize, Vector3.Zero, -1f, ship.Manifold));
            float q = 0.5f * 1.225f * 100f * 100f;
            Console.WriteLine($"   {nm,-24} {all / 1000,7:F0}  {(all - noBase) / 1000,7:F0}  {(noBase - noBoth) / 1000,7:F0}  {noBoth / 1000,10:F0}   -> {noBoth / q,6:F1}            ({rr.FrontalArea,5:F0})");
        }
        return 0;
    }

    // reference bodies as 0.25 m voxels, flying -Z at 100 m/s, sea level: drag coefficient on the frontal (cross-section)
    // area, against the textbook (subcritical, attached where it can be)
    static int Bodies(AtmosphereState atmo)
    {
        var sea = new AtmosphereState { Density = 1.225, Pressure = 101325, Temperature = 288, SpeedOfSound = 340 };
        BlockSize = float.Parse(Environment.GetEnvironmentVariable("AERO_BS") ?? "0.5", System.Globalization.CultureInfo.InvariantCulture);
        List<(Vector3I, Vector3I)> Revolve(Func<double, double> radius, double len, double maxR)
        {
            // a body of revolution along z (nose at -z): radius(x in 0..1 from nose) x maxR, in 0.25 m cells
            var l = new List<(Vector3I, Vector3I)>(); int nz = (int)Math.Round(len / 0.25), nr = (int)Math.Ceiling(maxR / 0.25);
            for (int k = 0; k < nz; k++)
            {
                double r = radius((k + 0.5) / nz) * maxR / 0.25;
                for (int i = -nr; i < nr; i++)
                    for (int j = -nr; j < nr; j++)
                        if ((i + 0.5) * (i + 0.5) + (j + 0.5) * (j + 0.5) <= r * r) l.Add((new Vector3I(i, j, k - nz), new Vector3I(i, j, k - nz)));
            }
            return l;
        }
        var cases = new List<(string, List<(Vector3I, Vector3I)>, double, string)>
        {
            ("sphere 4 m", Revolve(x => Math.Sqrt(Math.Max(0, 1 - (2 * x - 1) * (2 * x - 1))), 4, 2), Math.PI * 4, "0.47"),
            ("cylinder 4 m dia x 8 m, flat ends", Revolve(x => 1, 8, 2), Math.PI * 4, "0.82"),
            ("box 4x4x8 m", new() { (new Vector3I(-8, -8, -32), new Vector3I(7, 7, -1)) }, 16, "1.05"),
            ("teardrop 4:1 (16 m x 4 m)", Revolve(x => x < 0.3 ? Math.Sqrt(Math.Max(0, 1 - Math.Pow((0.3 - x) / 0.3, 2))) : Math.Cos((x - 0.3) / 0.7 * Math.PI / 2) * 0.98 + 0.02, 16, 2), Math.PI * 4, "0.05-0.1"),
            ("cylinder 8:1 with 2:1 nose and tail cones", Revolve(x => x < 0.2 ? x / 0.2 : x > 0.8 ? (1 - x) / 0.2 : 1, 32, 2), Math.PI * 4, "~0.15"),
        };
        Console.WriteLine($"   body                                       Cd model   Cd real   (square {AeroMod.DampedShadowedDragModel.SubsonicSquareLaw}, recovery {AeroMod.DampedShadowedDragModel.PressureRecovery})");
        foreach (var (nm, bx, area, real) in cases)
        {
            var ship = Build(nm, bx);
            AeroResult r = default;
            for (int k = 0; k < 3; k++) r = ship.Model.Compute(new AeroContext(ship.Grid, ship.Surface, new Vector3(0, 0, -100f), sea, ship.Com, BlockSize, Vector3.Zero, -1f, ship.Manifold));
            double q = 0.5 * 1.225 * 100 * 100;
            Console.WriteLine($"   {nm,-42} {r.DragMagnitude / (q * area),6:F2}     {real}");
            if (Environment.GetEnvironmentVariable("AERO_RECDBG") == "1" && nm.StartsWith(Environment.GetEnvironmentVariable("AERO_RECDBG_NAME") ?? "teardrop"))
            {
                AeroMod.DampedShadowedDragModel.RecDebug = new();
                var ex = ((AeroMod.DampedShadowedDragModel)ship.Model.InnerModel).ExactTableValue(new AeroContext(ship.Grid, ship.Surface, new Vector3(0, 0, -100f), sea, ship.Com, BlockSize, Vector3.Zero, -1f, ship.Manifold), null);
                Console.WriteLine($"      exact face sum: Cd {ex.DragMagnitude / (q * area):F2}");
                var dbg = AeroMod.DampedShadowedDragModel.RecDebug; AeroMod.DampedShadowedDragModel.RecDebug = null;
                int wMid = dbg.Count > 0 ? dbg.Max(x => x.w) / 2 : 0;
                foreach (var x in dbg.Where(x => x.w == wMid).OrderBy(x => x.u)) Console.WriteLine($"      u {x.u,2} exit depth {x.d,7:F2}  face s {x.sFace:F2}  slope s {x.s:F2}  front {x.front:F3}  run {x.run:F2} m");
            }
        }
        return 0;
    }

    // where the 737's (or any ship's) unrecovered pressure drag sits: each column's front pressure by its exit's fate,
    // and a map of the columns across the flow (the travel along the nose)
    static int Columns(string dataDir, AtmosphereState atmo, string nm)
    {
        var path = Directory.GetFiles(dataDir, nm + ".boxes", SearchOption.AllDirectories).First();
        var boxes = LoadBoxes(path); var ship = Build(nm, boxes);
        var (nose, up) = FlightAxes(ship, atmo, boxes);
        return ColumnsOf(ship, nm, nose, up);
    }

    static int ColumnsOf(Ship ship, string nm, Vector3 nose, Vector3 up)
    {
        var sea = new AtmosphereState { Density = 1.225, Pressure = 101325, Temperature = 288, SpeedOfSound = 340 };
        var inner = (AeroMod.DampedShadowedDragModel)ship.Model.InnerModel;
        foreach (var aDeg in (Environment.GetEnvironmentVariable("AERO_ALPHAS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => float.Parse(x, CultureInfo.InvariantCulture)))
        {
            float al = aDeg / 57.3f; var va = (nose * MathF.Cos(al) - up * MathF.Sin(al)) * 100f;
            AeroMod.DampedShadowedDragModel.RecDebug = new();
            var ea = inner.ExactTableValue(new AeroContext(ship.Grid, ship.Surface, va, sea, ship.Com, BlockSize, Vector3.Zero, -1f, ship.Manifold), null);
            var dd = AeroMod.DampedShadowedDragModel.RecDebug; AeroMod.DampedShadowedDragModel.RecDebug = null;
            double tt = dd.Sum(x => x.front), fl = dd.Where(x => x.s < 0).Sum(x => x.front);
            double rc = dd.Where(x => x.s >= 0).Sum(x => x.front * AeroMod.DampedShadowedDragModel.Attached(x.s));
            Console.WriteLine($"   alpha {aDeg,4:F1}: drag {-Vector3.Dot(ea.Force, Vector3.Normalize(va)) / 1000,5:F1} kN  columns {dd.Count}  front CdA {tt:F2}  attached {rc:F2}  flat {fl:F2}  steep {tt - rc - fl:F2}");
        }
        AeroMod.DampedShadowedDragModel.RecDebug = new();
        var ex = inner.ExactTableValue(new AeroContext(ship.Grid, ship.Surface, nose * 100f, sea, ship.Com, BlockSize, Vector3.Zero, -1f, ship.Manifold), null);
        var dbg = AeroMod.DampedShadowedDragModel.RecDebug; AeroMod.DampedShadowedDragModel.RecDebug = null;
        double q = 0.5 * 1.225 * 100 * 100;
        double noExit = dbg.Where(x => x.s < -1.5f).Sum(x => x.front);
        Console.WriteLine($"   columns with front pressure and no exit: {dbg.Count(x => x.s < -1.5f)}, CdA {noExit:F2} m2");
        dbg = dbg.Where(x => x.s > -1.5f).ToList();
        double tot = dbg.Sum(x => x.front), flat = dbg.Where(x => x.s < 0).Sum(x => x.front);
        double rec = dbg.Where(x => x.s >= 0).Sum(x => x.front * AeroMod.DampedShadowedDragModel.Attached(x.s) * AeroMod.DampedShadowedDragModel.RecoveryFrac);
        double sep = dbg.Where(x => x.s >= 0).Sum(x => x.front * (1 - AeroMod.DampedShadowedDragModel.Attached(x.s)));
        Console.WriteLine($"   {nm}: total drag {ex.DragMagnitude / 1000:F0} kN; columns' front pressure CdA {tot:F1} m2: recovered {rec:F1}, flat exit {flat:F1}, steep exit {sep:F1} (columns with no exit not listed)");
        // the map: one char per column, by front pressure (CdA per 0.25 m2 pixel) and fate
        int W = dbg.Max(x => x.u) + 1, H = dbg.Max(x => x.w) + 1;
        var grid = new char[H, W]; for (int i = 0; i < H; i++) for (int j = 0; j < W; j++) grid[i, j] = ' ';
        foreach (var x in dbg)
        {
            char c = x.front < 0.01 ? '.' : x.s < 0 ? (x.front > 0.12 ? '#' : '=') : AeroMod.DampedShadowedDragModel.Attached(x.s) > 0.5 ? 'o' : (x.front > 0.12 ? 'X' : 'x');
            grid[x.w, x.u] = c;
        }
        foreach (var x in dbg.Where(x => x.s >= 0 && AeroMod.DampedShadowedDragModel.Attached(x.s) < 0.5).OrderByDescending(x => x.front).Take(int.Parse(Environment.GetEnvironmentVariable("AERO_TOPN") ?? "0")))
            Console.WriteLine($"      steep: u {x.u,3} w {x.w,3} exit depth {x.d,7:F2} face s {x.sFace:F2} slope s {x.s:F2} front CdA {x.front:F3} run {x.run:F2} m");
        Console.WriteLine("   (#/= flat exit heavy/light, X/x steep exit, o recovered, . little pressure)");
        for (int i = H - 1; i >= 0; i--) { var sb = new System.Text.StringBuilder("   "); for (int j = 0; j < W; j++) sb.Append(grid[i, j]); Console.WriteLine(sb.ToString().TrimEnd()); }
        return 0;
    }

    // the drag polar of one ship, split hull / wings: nose up 0..16 deg at 100 m/s, sea level
    static int Polar(string dataDir, AtmosphereState atmo, string nm)
    {
        var path = Directory.GetFiles(dataDir, nm + ".boxes", SearchOption.AllDirectories).First();
        var boxes = LoadBoxes(path);
        if (Environment.GetEnvironmentVariable("AERO_REJECTS") == "1") AeroMod.ConnectedComponentWingDetector.Rejected = new();
        if (Environment.GetEnvironmentVariable("AERO_DBGBOX") is string dbx)
        {
            var q = dbx.Split(',').Select(int.Parse).ToArray();
            AeroMod.ConnectedComponentWingDetector.DebugRegion = c => c.X >= q[0] && c.X <= q[3] && c.Y >= q[1] && c.Y <= q[4] && c.Z >= q[2] && c.Z <= q[5];
            AeroMod.ConnectedComponentWingDetector.Rejected ??= new();
        }
        var ship = Build(nm, boxes);
        if (AeroMod.ConnectedComponentWingDetector.Rejected != null)
        {
            foreach (var r in AeroMod.ConnectedComponentWingDetector.Rejected.Where(r => !r.Contains(" S 0.") && !r.Contains(" S 1.") ).Take(60)) Console.WriteLine("     rejected: " + r);
            AeroMod.ConnectedComponentWingDetector.Rejected = null;
        }
        Console.WriteLine("   detector: " + AeroMod.ConnectedComponentWingDetector.LastProfile);
        var (nose, up) = FlightAxes(ship, atmo, boxes);
        return PolarOf(ship, boxes, nm, nose, up, atmo);
    }

    // a CLEAN voxel airliner (0.25 m cells, nose -Z, up +Y): a round fuselage 32 m x 3.5 m with a 2:1 nose and a 3:1
    // tail cone; a tapered wing (span 28 m, chord 5 -> 1.75 m, AR ~8.5, 0.5 m thick, its leading and trailing 0.5 m
    // stepped down to 0.25 m); a tailplane and a fin alike - what the model gives a plane built as well as cells allow
    static int CleanPlane(AtmosphereState atmo)
    {
        BlockSize = 0.5f;
        var cells = new HashSet<(int, int, int)>();
        double L = 32, R = 1.75; int nz = (int)(L / 0.25);
        for (int k = 0; k < nz; k++)
        {
            double x = (k + 0.5) / nz;   // 0 nose .. 1 tail
            double r = x < 0.11 ? R * Math.Sqrt(1 - Math.Pow((0.11 - x) / 0.11, 2)) : x > 0.67 ? R * Math.Max(0.12, 1 - (x - 0.67) / 0.33) : R;
            double rc = r / 0.25;
            for (int i = -8; i < 8; i++) for (int j = -8; j < 8; j++)
                if ((i + 0.5) * (i + 0.5) + (j + 0.5) * (j + 0.5) <= rc * rc) cells.Add((i, j, k - nz));
        }
        void Wing(double zLe0, double span, double cRoot, double cTip, double sweep, int y0, bool vertical)
        {
            int ns = (int)(span / 2 / 0.25);
            for (int si = 0; si < ns; si++)
            {
                double f = (si + 0.5) / ns, c = cRoot + (cTip - cRoot) * f, zLe = zLe0 + sweep * f * span / 2;
                int c0 = (int)Math.Round(zLe / 0.25), cn = Math.Max(2, (int)Math.Round(c / 0.25));
                for (int ci = 0; ci < cn; ci++)
                {
                    bool edge = ci < 2 || ci >= cn - 2;   // (the leading and trailing 0.5 m one cell thick)
                    for (int t = 0; t < (edge ? 1 : 2); t++)
                    {
                        if (vertical) cells.Add((0, y0 + si, c0 + ci));
                        else { cells.Add((si, y0 + t, c0 + ci)); cells.Add((-1 - si, y0 + t, c0 + ci)); }
                        if (vertical && !edge) cells.Add((-1, y0 + si, c0 + ci));
                    }
                }
            }
        }
        Wing(-19 / 0.25 * 0.25, 28, 5, 1.75, 0.35, -3, false);       // main wing, low on the fuselage
        Wing(-4.5, 10, 2.5, 1.25, 0.3, 0, false);                   // tailplane
        Wing(-4.5, 10, 3, 1.5, 0.4, 7, true);                       // fin (half of its 'span' = 5 m tall)
        var boxes = cells.Select(c => (new Vector3I(c.Item1, c.Item2, c.Item3), new Vector3I(c.Item1, c.Item2, c.Item3))).ToList();
        LastThrusters = new();
        var ship = Build("clean airliner", boxes);
        ship.Model.Compute(new AeroContext(ship.Grid, ship.Surface, -Vector3.UnitZ * 100f, atmo, ship.Com, BlockSize, Vector3.Zero, -1f, ship.Manifold));   // (sets the face model up)
        if (Environment.GetEnvironmentVariable("AERO_CLEANCOLS") == "1") ColumnsOf(ship, "clean airliner", -Vector3.UnitZ, Vector3.UnitY);
        return PolarOf(ship, boxes, "clean airliner", -Vector3.UnitZ, Vector3.UnitY, atmo);
    }

    static int PolarOf(Ship ship, List<(Vector3I, Vector3I)> boxes, string nm, Vector3 nose, Vector3 up, AtmosphereState atmo)
    {
        var sea = new AtmosphereState { Density = 1.225, Pressure = 101325, Temperature = 288, SpeedOfSound = 340 };
        var inner = (AeroMod.DampedShadowedDragModel)ship.Model.InnerModel;
        float wingArea = 0; if (ship.Model.Wings != null) foreach (var w in ship.Model.Wings) wingArea += w.PlanformArea;
        var bl = BlendOne(nm, boxes, 0, atmo, false);
        Console.WriteLine($"   {nm}: wings {ship.Model.Wings?.Count ?? 0} ({wingArea:F0} m2), blended S_eff {bl.Seff:F0} m2 AR {bl.AR:F1}; q = 6125 Pa");
        if (ship.Model.Wings != null) foreach (var w in ship.Model.Wings)
        {
            if (Environment.GetEnvironmentVariable("AERO_WINGBOX") == "1" && w.Cells.Count > 0) Console.WriteLine($"       cells {w.Cells.Count}: x {w.Cells.Min(c => c.X)}..{w.Cells.Max(c => c.X)}  y {w.Cells.Min(c => c.Y)}..{w.Cells.Max(c => c.Y)}  z {w.Cells.Min(c => c.Z)}..{w.Cells.Max(c => c.Z)}  distinct y {w.Cells.Select(c => c.Y).Distinct().Count()}");
            Console.WriteLine($"     wing: S {w.PlanformArea,6:F1} m2  span {w.Span,5:F1}  AR {w.AspectRatio,4:F1}  sweep {w.SweepAngle * 57.3f,4:F0}  CLmax {w.CLMax:F2} stall {w.AlphaStall * 57.3f:F0}  zero-lift {AeroMod.CompressibleWingModel.CamberOf(w).lo * 57.3f:F1}/{AeroMod.CompressibleWingModel.CamberOf(w).hi * 57.3f:F1} deg  t/c {w.ThicknessRatio:F2}  normal ({w.Normal.X:F2},{w.Normal.Y:F2},{w.Normal.Z:F2})  span axis ({w.SpanAxis.X:F2},{w.SpanAxis.Y:F2},{w.SpanAxis.Z:F2})  at ({w.Centroid.X:F1},{w.Centroid.Y:F1},{w.Centroid.Z:F1})");
        }
        Console.WriteLine("   alpha |  total L kN  D kN  L/D |  hull L   hull D |  wings L  wings D");
        for (int ai = 0; ai <= 8; ai++)
        {
            float al = ai * 2f / 57.3f;
            var v = (nose * MathF.Cos(al) - up * MathF.Sin(al)) * 100f;
            var vh = Vector3.Normalize(v); var lu = Vector3.Normalize(up - Vector3.Dot(up, vh) * vh);
            var ctx = new AeroContext(ship.Grid, ship.Surface, v, sea, ship.Com, BlockSize, Vector3.Zero, -1f, ship.Manifold);
            AeroResult t = default; for (int k = 0; k < 2; k++) t = ship.Model.Compute(ctx);
            var h = inner.ExactTableValue(ctx, null);
            float L(Vector3 f) => Vector3.Dot(f, lu) / 1000f; float D(Vector3 f) => -Vector3.Dot(f, vh) / 1000f;
            Console.WriteLine($"   {ai * 2,4}  | {L(t.Force),9:F0} {D(t.Force),6:F0} {L(t.Force) / Math.Max(0.01f, D(t.Force)),5:F1} | {L(h.Force),7:F0} {D(h.Force),7:F0}  | {L(t.Force) - L(h.Force),7:F0} {D(t.Force) - D(h.Force),7:F0}");
        }
        return 0;
    }

    // wing detection alone, old (layer flood) vs columns, best of 3, on each ship named
    static int DetectTime(string dataDir, string[] names)
    {
        foreach (var nm in names)
        {
            var path = Directory.GetFiles(dataDir, nm + ".boxes", SearchOption.AllDirectories).First();
            var boxes = LoadBoxes(path);
            int k = ForceScale ?? SnapshotGridAccessor.CellScale(BlockSize);
            var (cs, co) = SnapshotGridAccessor.CellGeometry(k);
            var grid = k <= 1 ? new SnapshotGridAccessor(boxes) : new SnapshotGridAccessor(boxes).CoarsenMajority(k);
            var surface = new SmoothSurfaceProvider { CellSize = cs, CellOffset = co };
            surface.BeginBuild(grid, BlockSize); while (!surface.AddCellBatch(int.MaxValue)) { } surface.FinalizeBuild();
            var manifold = new ManifoldClassifier(); manifold.Classify(surface);
            foreach (bool col in new[] { false, true })
            {
                AeroMod.ConnectedComponentWingDetector.ColumnMode = col;
                double best = double.MaxValue; int nw = 0; string prof = "";
                for (int r = 0; r < 3; r++)
                {
                    var det = new AeroMod.ConnectedComponentWingDetector { Manifold = manifold, ManifoldSurface = surface, CellSize = cs, CellOffset = co };
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var w = det.Detect(grid, surface, BlockSize);
                    best = Math.Min(best, sw.Elapsed.TotalMilliseconds); nw = w.Count; prof = AeroMod.ConnectedComponentWingDetector.LastProfile;
                }
                Console.WriteLine($"   {nm,-12} {grid.CellCount,8} cells  {(col ? "columns" : "layers "),-8} {best,8:F1} ms  {nw,3} wings   | {prof}");
            }
        }
        return 0;
    }

    static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";
}
