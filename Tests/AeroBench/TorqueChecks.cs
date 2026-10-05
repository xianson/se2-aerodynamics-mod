using System.IO;
using AeroMod;

// Air torques, offline: the full model (hull faces + wings, as the game computes them exactly) against what physics
// says about torque, on shapes whose answer is known and on real ships.
//  1. symmetry: a brick with its centre of mass at its centre feels no torque in flow along an axis; flow mirrored
//     across a plane of symmetry gives the mirrored torque;
//  2. the reference point: moving the centre of mass by d changes the torque by exactly -d x F;
//  3. the centre of pressure: the force's line of action passes through the hull (a torque too big for its force puts
//     it outside the ship);
//  4. stability: a dart (body, tail fins) flying nose first turns into the wind (weathervanes); tail first it does not;
//  5. damping: a turning dart's air torque opposes the turn.
// dotnet run -c Release -- torque [ship ...] reports the real ships (Red Ship's torque in forward flight).
static partial class Program
{
    static int _tFails;
    static void TCheck(bool ok, string what) { if (!ok) _tFails++; Console.WriteLine($"   {(ok ? "ok  " : "FAIL")} {what}"); }

    static AeroResult Exact(Ship s, Vector3 travel, float speed, AtmosphereState atmo, Vector3 com, Vector3 omega = default)
    {
        AeroResult r = default;
        // (three calls: the model's per-grid smoothing settles, as KnownShapes does)
        for (int i = 0; i < 3; i++) r = s.Model.Compute(new AeroContext(s.Grid, s.Surface, Vector3.Normalize(travel) * speed, atmo, com, BlockSize, omega, -1f, s.Manifold));
        return r;
    }

    /// <summary>The point of the force's line of action nearest the centre of mass, relative to it (T = r x F).</summary>
    static Vector3 PressureArm(in AeroResult r) => r.Force.LengthSquared() > 1e-6f ? Vector3.Cross(r.Force, r.Torque) / r.Force.LengthSquared() : Vector3.Zero;

    static (Vector3 lo, Vector3 hi) Bounds(List<(Vector3I, Vector3I)> boxes)
    {
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (var (a, b) in boxes) { lo = Vector3.Min(lo, new Vector3(a.X, a.Y, a.Z) * 0.25f); hi = Vector3.Max(hi, new Vector3(b.X + 1, b.Y + 1, b.Z + 1) * 0.25f); }
        return (lo, hi);
    }

    /// <summary>A dart: a 2 x 2 x 16 m body along Z (nose at -Z), four tail fins 1 m thick, 3 m long at the +Z end, reaching
    /// 3 m out. (Fins on whole 0.5 m aero cells: a 0.5 m fin straddling two cells was dropped by the coarsening - half of
    /// each cell - and the dart flew finless.)</summary>
    static List<(Vector3I, Vector3I)> Dart() => new()
    {
        (new Vector3I(0, 0, 0), new Vector3I(7, 7, 63)),          // body: x 0..2, y 0..2, z 0..16 m
        (new Vector3I(2, 8, 52), new Vector3I(5, 19, 63)),        // fin up    (1 m thick, 3 m out, 3 m long)
        (new Vector3I(2, -12, 52), new Vector3I(5, -1, 63)),      // fin down
        (new Vector3I(8, 2, 52), new Vector3I(19, 5, 63)),        // fin right
        (new Vector3I(-12, 2, 52), new Vector3I(-1, 5, 63)),      // fin left
    };

    /// <summary>A flat plate 3 m x 3 m x 0.5 m (normal X) at incidence: its normal-force coefficient, for diagnosis.</summary>
    static void PlateDiag(AtmosphereState atmo)
    {
        var s = Build("plate", new List<(Vector3I, Vector3I)> { (Vector3I.Zero, new Vector3I(1, 11, 11)) });
        var inner = (DampedShadowedDragModel)s.Model.InnerModel;
        foreach (float deg in new[] { 2f, 5f, 10f, 20f, 45f, 90f })
        {
            float a = deg * MathF.PI / 180f;
            var travel = new Vector3(MathF.Sin(a), 0f, -MathF.Cos(a));
            var r = Exact(s, travel, 100f, atmo, s.Com);
            float q = 0.5f * (float)atmo.Density * 100f * 100f;
            Console.WriteLine($"      plate 3x3 m at {deg,4} deg: normal force coefficient {-r.Force.X / (q * 9f):F3} (flat-plate thin-wing theory, aspect 1: {1.57f * a:F3}; Newtonian 2 sin^2: {2f * MathF.Sin(a) * MathF.Sin(a):F3}), axial {r.Force.Z / (q * 9f):F3}");
        }
    }

    static int TorqueChecks(AtmosphereState atmo, bool realShips)
    {
        if (Environment.GetEnvironmentVariable("PLATE") != null) PlateDiag(atmo);
        _tFails = 0;
        BlockSize = 2.5f;
        float sp = 100f;

        // 1. symmetry: brick 4 x 4 x 10 m, centre of mass at its centre
        {
            var boxes = new List<(Vector3I, Vector3I)> { (Vector3I.Zero, new Vector3I(15, 15, 39)) };
            var s = Build("brick", boxes);
            var c = s.Com;
            Console.WriteLine($"      brick centre of mass (the bench's centroid) {c}");
            foreach (var d in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ })
            {
                var r = Exact(s, d, sp, atmo, c);
                float lim = 0.01f * r.Force.Length() * 5f + 1f;   // 1% of force x half length
                TCheck(r.Torque.Length() < lim, $"brick, flow along {d}: no torque ({r.Torque.Length():F0} N m, force {r.Force.Length():F0} N)");
            }
            var yaw = Vector3.Normalize(new Vector3(0.2f, 0f, -1f)); var yawM = new Vector3(-yaw.X, yaw.Y, yaw.Z);
            var a = Exact(s, yaw, sp, atmo, c); var b = Exact(s, yawM, sp, atmo, c);
            float scale = a.Force.Length() * 5f + 1f;
            TCheck(MathF.Abs(a.Torque.X) < 0.01f * scale && MathF.Abs(a.Torque.Z) < 0.01f * scale, $"brick yawed 11 deg: torque only about yaw ({a.Torque})");
            // mirror across x = 2 (the YZ plane through the centre): a torque (axial vector) maps to (Tx, -Ty, -Tz)
            var mirrored = new Vector3(a.Torque.X, -a.Torque.Y, -a.Torque.Z);
            TCheck((b.Torque - mirrored).Length() < 0.05f * a.Torque.Length() + 1f, $"brick yawed the other way: the mirrored torque ({b.Torque} vs {mirrored})");

            // 2. the reference point
            var shift = new Vector3(0.7f, -1.3f, 2.1f);
            var t1 = Exact(s, yaw, sp, atmo, c); var t2 = Exact(s, yaw, sp, atmo, c + shift);
            var expect = t1.Torque - Vector3.Cross(shift, t1.Force);
            TCheck((t2.Torque - expect).Length() < 0.002f * (t1.Force.Length() * 5f) + 1f && (t2.Force - t1.Force).Length() < 0.001f * t1.Force.Length() + 1f,
                $"centre of mass moved by {shift}: torque changes by -d x F ({t2.Torque} vs {expect})");
        }

        // the model's own small-angle plate slope (a 3 x 3 m plate at 5 deg): the yardstick for the rotation checks
        float slope;
        {
            var pl = Build("plate", new List<(Vector3I, Vector3I)> { (Vector3I.Zero, new Vector3I(1, 11, 11)) });
            float a5 = 5f * MathF.PI / 180f;
            var rp = Exact(pl, new Vector3(MathF.Sin(a5), 0f, -MathF.Cos(a5)), sp, atmo, pl.Com);
            slope = -rp.Force.X / (0.5f * (float)atmo.Density * sp * sp * 9f) / a5;
            TCheck(slope > 0.3f, $"plate at 5 deg: normal force slope {slope:F2} /rad (thin-wing theory for aspect 1: 1.57; a bluff panel model less)");
        }

        // 4./5. the dart
        {
            var boxes = Dart();
            var s = Build("dart", boxes);
            var com = new Vector3(1f, 1f, 6f);   // 6 m back of the 16 m body's nose (a dart's weight forward)
            foreach (float deg in new[] { 5f, 10f, 20f })
            {
                float a = deg * MathF.PI / 180f;
                foreach (var (name, side) in new[] { ("yaw", Vector3.UnitX), ("pitch", Vector3.UnitY) })
                {
                    var nose = -Vector3.UnitZ;
                    var travel = Vector3.Normalize(nose * MathF.Cos(a) + side * MathF.Sin(a));
                    var r = Exact(s, travel, sp, atmo, com);
                    var restoring = Vector3.Normalize(Vector3.Cross(nose, travel));   // the turn that brings the nose to the travel
                    float k = Vector3.Dot(r.Torque, restoring);
                    TCheck(k > 0f, $"dart nose first, {name} {deg} deg: weathervanes (restoring torque {k:F0} N m, force {r.Force.Length():F0} N, arm {PressureArm(r)})");
                    // tail first (the same ship flying backwards, travel +Z-ish): the tail swings round - no restoring torque
                    var back = Vector3.Normalize(-nose * MathF.Cos(a) + side * MathF.Sin(a));
                    var rb = Exact(s, back, sp, atmo, com);
                    var restoringBack = Vector3.Normalize(Vector3.Cross(-nose, back));   // would bring the tail to the travel
                    TCheck(Vector3.Dot(rb.Torque, restoringBack) < 0f, $"dart tail first, {name} {deg} deg: unstable (torque {Vector3.Dot(rb.Torque, restoringBack):F0} N m toward the travel)");
                }
            }
            // damping: flying straight, turning at 0.3 rad/s in yaw and in pitch
            foreach (var w in new[] { new Vector3(0f, 0.3f, 0f), new Vector3(0.3f, 0f, 0f) })
            {
                var r0 = Exact(s, -Vector3.UnitZ, sp, atmo, com); var rw = Exact(s, -Vector3.UnitZ, sp, atmo, com, w);
                float d = Vector3.Dot(rw.Torque - r0.Torque, w);
                var inner = (DampedShadowedDragModel)s.Model.InnerModel;
                AeroResult h0 = default, hw = default;
                for (int i = 0; i < 3; i++) h0 = inner.Compute(new AeroContext(s.Grid, s.Surface, -Vector3.UnitZ * sp, atmo, com, BlockSize, Vector3.Zero, -1f, s.Manifold));
                for (int i = 0; i < 3; i++) hw = inner.Compute(new AeroContext(s.Grid, s.Surface, -Vector3.UnitZ * sp, atmo, com, BlockSize, w, -1f, s.Manifold));
                float dh = Vector3.Dot(hw.Torque - h0.Torque, w);
                var table = inner.BuildForceTable(s.Grid, s.Surface, s.Manifold, com);
                var tc0 = new AeroContext(s.Grid, s.Surface, -Vector3.UnitZ * sp, atmo, com, BlockSize, Vector3.Zero, -1f, s.Manifold);
                var tcw = new AeroContext(s.Grid, s.Surface, -Vector3.UnitZ * sp, atmo, com, BlockSize, w, -1f, s.Manifold);
                float dt = Vector3.Dot(table.Evaluate(tcw, inner.SubsonicLimit, inner.SupersonicLimit, inner.Streamlining).Torque - table.Evaluate(tc0, inner.SubsonicLimit, inner.SupersonicLimit, inner.Streamlining).Torque, w);
                float dx = Vector3.Dot(inner.ExactTableValue(tcw, null).Torque - inner.ExactTableValue(tc0, null).Torque, w);
                TCheck(dt < 0f, $"dart turning at {w} rad/s, force table (big ships): the air opposes the turn (torque . w {dt:F0}; its exact sum {dx:F0})");
                // how much: the two fins across the turn (3 x 3 m, 8.5 m behind the centre of mass) each meet the air at w l / V;
                // with the model's own plate slope that is q S slope (w l / V) l - the turn must be damped at least half that
                // (the body adds more)
                float est = 0.5f * (float)atmo.Density * sp * sp * 18f * slope * (0.3f * 8.5f / sp) * 8.5f;
                TCheck(-dt / 0.3f >= 0.5f * est, $"dart turning at {w} rad/s, force table: damping {-dt / 0.3f:F0} N m vs fin estimate {est:F0} (at least half)");
                TCheck(-d / 0.3f >= 0.5f * est, $"dart turning at {w} rad/s, face model: damping {-d / 0.3f:F0} N m vs fin estimate {est:F0} (at least half)");
                TCheck(d < 0f, $"dart turning at {w} rad/s, face model (per frame): the air opposes the turn (torque . w {d:F0}: hull faces {dh:F0}, wings {d - dh:F0}; {s.Model.Wings?.Count ?? 0} wings)");
            }
        }

        // 6. a table builder used for one ship, then another (the game pools them across grids): the second ship's table
        //    exactly as a fresh builder makes it (a builder kept the bigger ship's faces and summed them into every later table)
        if (File.Exists(Path.Combine(dataDir, "pelican.boxes")) && File.Exists(Path.Combine(dataDir, "bluefighter.boxes")))
        {
            var big = Build("pelican", LoadBoxes(Path.Combine(dataDir, "pelican.boxes")));
            var small = Build("bluefighter", LoadBoxes(Path.Combine(dataDir, "bluefighter.boxes")));
            var shared = new DampedShadowedDragModel();
            shared.BuildForceTable(big.Grid, big.Surface, big.Manifold, big.Com, n: 4, nj: 2);
            var reused = shared.BuildForceTable(small.Grid, small.Surface, small.Manifold, small.Com, n: 4, nj: 2);
            var fresh = new DampedShadowedDragModel().BuildForceTable(small.Grid, small.Surface, small.Manifold, small.Com, n: 4, nj: 2);
            float worst = 0f;
            foreach (var d in new[] { -Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitY, Vector3.Normalize(new Vector3(0.3f, -0.4f, -1f)) })
            {
                var c = new AeroContext(small.Grid, small.Surface, d * 100f, atmo, small.Com, BlockSize, new Vector3(0.1f, 0.2f, 0f), -1f, small.Manifold);
                var a = reused.Evaluate(c, 0.8f, 1.2f, 0f); var b = fresh.Evaluate(c, 0.8f, 1.2f, 0f);
                worst = MathF.Max(worst, ((a.Force - b.Force).Length() + (a.Torque - b.Torque).Length()) / MathF.Max(1f, b.Force.Length()));
            }
            TCheck(worst < 1e-5f, $"a table builder reused after a bigger ship: the same table as a fresh one (worst difference {worst * 100f:F3}%)");
        }

        // 6b. ... and after a ship with the SAME face count (a brick along Z, then the same brick along X: one face count,
        //     both surfaces at version 1) - the builder took "same count, same version" for "same surface" and kept the
        //     first brick's faces
        {
            var alongZ = Build("brickZ", new List<(Vector3I, Vector3I)> { (Vector3I.Zero, new Vector3I(15, 15, 39)) });
            var alongX = Build("brickX", new List<(Vector3I, Vector3I)> { (Vector3I.Zero, new Vector3I(39, 15, 15)) });
            var shared = new DampedShadowedDragModel();
            shared.BuildForceTable(alongZ.Grid, alongZ.Surface, alongZ.Manifold, alongZ.Com, n: 4, nj: 2);
            var reused = shared.BuildForceTable(alongX.Grid, alongX.Surface, alongX.Manifold, alongX.Com, n: 4, nj: 2);
            var fresh = new DampedShadowedDragModel().BuildForceTable(alongX.Grid, alongX.Surface, alongX.Manifold, alongX.Com, n: 4, nj: 2);
            var c = new AeroContext(alongX.Grid, alongX.Surface, -Vector3.UnitZ * 100f, atmo, alongX.Com, BlockSize, Vector3.Zero, -1f, alongX.Manifold);
            var a = reused.Evaluate(c, 0.8f, 1.2f, 0f); var b = fresh.Evaluate(c, 0.8f, 1.2f, 0f);
            float diff = ((a.Force - b.Force).Length() + (a.Torque - b.Torque).Length()) / MathF.Max(1f, b.Force.Length());
            TCheck(diff < 1e-5f, $"a table builder reused after a ship with the same face count ({alongZ.Surface.FaceCount} / {alongX.Surface.FaceCount}): the same table as a fresh one (difference {diff * 100f:F3}%; head-on force {a.Force.Length():F0} vs {b.Force.Length():F0} N)");
        }

        // 3. the centre of pressure inside the hull, real ships, random directions
        foreach (var name in realShips ? Directory.GetFiles(dataDir, "*.boxes").Select(Path.GetFileNameWithoutExtension).ToArray() : new[] { "bluefighter", "pelican" })
        {
            var path = Path.Combine(dataDir, name + ".boxes");
            if (!File.Exists(path)) continue;
            var boxes = LoadBoxes(path);
            var s = Build(name, boxes);
            var (lo, hi) = Bounds(boxes);
            var rnd = new Random(7);
            float worst = 0f; Vector3 worstDir = default; int outside = 0, n = 60;
            for (int i = 0; i < n; i++)
            {
                var d = Vector3.Normalize(new Vector3((float)rnd.NextDouble() * 2 - 1, (float)rnd.NextDouble() * 2 - 1, (float)rnd.NextDouble() * 2 - 1));
                var r = Exact(s, d, sp, atmo, s.Com);
                var p = s.Com + PressureArm(r);
                // distance outside the hull's box (0 inside)
                var o = Vector3.Max(Vector3.Max(lo - p, p - hi), Vector3.Zero);
                float out_ = o.Length();
                if (out_ > 0.5f) outside++;
                if (out_ > worst) { worst = out_; worstDir = d; }
            }
            var size = hi - lo;
            TCheck(outside == 0, $"{name}: the centre of pressure inside the hull ({outside}/{n} directions outside; worst {worst:F1} m out, travel {worstDir}; hull {size.X:F0} x {size.Y:F0} x {size.Z:F0} m)");

            if (realShips)
            {
                // the face loop's cost in steady flight (the travel turning 0.1 deg a frame: its occlusion bake reused)
                {
                    var inner = (DampedShadowedDragModel)s.Model.InnerModel;
                    var sw = System.Diagnostics.Stopwatch.StartNew(); int frames = 120;
                    for (int i = 0; i < frames; i++)
                    {
                        float a = i * 0.1f * MathF.PI / 180f;
                        inner.Compute(new AeroContext(s.Grid, s.Surface, new Vector3(MathF.Sin(a), 0f, -MathF.Cos(a)) * 150f, atmo, s.Com, BlockSize, new Vector3(0f, 0.02f, 0f), -1f, s.Manifold));
                    }
                    Console.WriteLine($"      {name}: face loop in steady flight {sw.Elapsed.TotalMilliseconds / frames:F2} ms a frame");
                }
                // forward flight report: q 20 kPa (the in-game Red Ship numbers: 250-300 MN m at q 14-20 kPa)
                var atm = new AtmosphereState { Density = 1.0, Pressure = 90000, Temperature = 288, SpeedOfSound = 340 };
                float v = MathF.Sqrt(2f * 20000f / 1.0f);
                foreach (var (label, d) in new[] { ("forward", -Vector3.UnitZ), ("forward, 5 deg yaw", Vector3.Normalize(new Vector3(0.087f, 0f, -1f))), ("forward, 5 deg pitch", Vector3.Normalize(new Vector3(0f, 0.087f, -1f))) })
                {
                    var r = Exact(s, d, v, atm, s.Com);
                    float L = size.Length();
                    Console.WriteLine($"      {name} {label} at q 20 kPa: force {r.Force.Length() / 1e6f:F1} MN, torque {r.Torque / 1e6f} MN m (|T| {r.Torque.Length() / 1e6f:F1}), frontal {r.FrontalArea:F0} m2, Cm {r.Torque.Length() / (20000f * MathF.Max(r.FrontalArea, 1f) * L):F3}, pressure arm {PressureArm(r)} m");
                }
            }
        }
        return _tFails;
    }
}
