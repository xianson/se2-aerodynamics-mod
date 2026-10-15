#pragma warning disable
using AeroMod;
using System.IO;

/// <summary>
/// The control surface's own section (2026-10-13, independent of wing detection): ControlSurfaceGeometry.Measure on
/// synthetic grids (0.25 m cells, the surfaces' own cells left out as the game's accessor does), then the forces it gives.
/// The large 3x3x1 surface: hinge along Z, chord along +X, thickness along Y; cells x 0..29, y 0..9, z 0..29.
/// </summary>
static partial class Program
{
    static readonly Vector3 H = new(0, 0, 1), C = new(1, 0, 0);

    static ControlSurface Large(float zCells = 0f) => new ControlSurface(new Vector3(3.625f, 1.125f, 3.625f + zCells * 0.25f), Vector3I.Zero, H, C, 56.25f)
        { MaxDeflection = 20f, AspectRatio = 1f, Cd0 = 0.03f, Extents = new Vector3(7.5f, 2.5f, 7.5f) };

    static SnapshotGridAccessor Grid(params (int x0, int y0, int z0, int x1, int y1, int z1)[] boxes)
    {
        var list = new List<(Vector3I, Vector3I)>();
        foreach (var b in boxes) list.Add((new Vector3I(b.x0, b.y0, b.z0), new Vector3I(b.x1, b.y1, b.z1)));
        return new SnapshotGridAccessor(list);
    }

    static ComponentForceResult Result(ControlSurface cs, Vector3 velocity, float input)
    {
        cs.DeflectionInput = input;
        float speed = velocity.Length();
        var cond = new LocalAeroConditions(velocity, speed, velocity / speed, (float)Sea.GetDynamicPressure(speed), (float)Sea.GetMachNumber(speed), Sea, Vector3.Zero);
        return cs.Compute(cond);
    }

    static void SectionTests(Random r)
    {
        Tu134Sections();
        const float tol = 0.3f;
        // 1. a lone surface: nothing ahead, its own span, no plates
        {
            var cs = Large(); ControlSurfaceGeometry.Measure(new IAeroBlockComponent[] { cs }, Grid((200, 200, 200, 200, 200, 200)));
            var g = cs.Surroundings;
            Check(g != null && g.HostChord[0] == 0f && g.HostChord[1] == 0f, $"lone: host {g?.Describe(0)} / {g?.Describe(1)}");
            Check(g != null && MathF.Abs(g.SectionSpan[0] - 7.5f) < tol && g.EndPlates[0] == 0, $"lone: {g?.Describe(0)}");
        }
        // 2. behind a wing: a 10 m chord, 2.5 m thick plate ahead of the -X edge, running 10 m past each end of the surface
        var wing = Grid((-40, 0, -40, -1, 9, 69));
        ControlSurface onWing = Large();
        {
            ControlSurfaceGeometry.Measure(new IAeroBlockComponent[] { onWing }, wing);
            var g = onWing.Surroundings;
            Check(MathF.Abs(g.HostChord[1] - 10f) < tol, $"wing: host ahead of the -X edge {g.HostChord[1]:F2} m, want 10");
            Check(g.HostChord[0] == 0f, $"wing: nothing behind the +X edge, got {g.HostChord[0]:F2} m");
            Check(MathF.Abs(g.SectionSpan[1] - 27.5f) < tol, $"wing: section span {g.SectionSpan[1]:F2} m, want 27.5 (the wing's)");
            Check(g.EndPlates[1] == 0, $"wing: end plates {g.EndPlates[1]}, want 0");
        }
        // 3. a fuselage ahead (22.5 m thick): not a host
        {
            var cs = Large(); ControlSurfaceGeometry.Measure(new IAeroBlockComponent[] { cs }, Grid((-40, -40, -40, -1, 49, 69)));
            Check(cs.Surroundings.HostChord[1] == 0f, $"fuselage ahead counted as a host: {cs.Surroundings.HostChord[1]:F2} m");
        }
        // 4. a row of three side by side on one hinge line: one 22.5 m surface
        {
            var row = new IAeroBlockComponent[] { Large(0), Large(30), Large(60) };
            ControlSurfaceGeometry.Measure(row, Grid((200, 200, 200, 200, 200, 200)));
            foreach (ControlSurface cs in row) Check(MathF.Abs(cs.Surroundings.SectionSpan[0] - 22.5f) < tol, $"row: span {cs.Surroundings.SectionSpan[0]:F2} m, want 22.5");
        }
        // 5. a wall across the span at one end (a fuselage side at z < 0): one end plate
        {
            var cs = Large(); ControlSurfaceGeometry.Measure(new IAeroBlockComponent[] { cs }, Grid((-40, -40, -8, 60, 49, -1)));
            Check(cs.Surroundings.EndPlates[0] == 1 && cs.Surroundings.EndPlates[1] == 1, $"wall: end plates {cs.Surroundings.EndPlates[0]} / {cs.Surroundings.EndPlates[1]}, want 1");
        }

        // physics: air along +X (from the -X edge, where the wing is), 3 deg of attack, 120 m/s
        float a3 = 3f * MathF.PI / 180f;
        var vel = -new Vector3(MathF.Cos(a3), MathF.Sin(a3), 0f) * 120f;
        var lone = Large();
        Vector3 dLone = Result(lone, vel, 0.25f).Force - Result(lone, vel, 0f).Force;
        Vector3 dWing = Result(onWing, vel, 0.25f).Force - Result(onWing, vel, 0f).Force;
        float ratio = dWing.Length() / dLone.Length();
        Console.WriteLine($"section: on a 10 m wing, tau {onWing.LastTau:F2}, AR {onWing.LastAspectRatio:F2}: a deflection gives {ratio:F2} x the lone plate's");
        Check(ratio > 1.5f && ratio < 8f, $"on a wing a deflection gives {ratio:F2} x a lone plate's, want 1.5..8");
        Check(Vector3.Dot(dWing, dLone) > 0f, "on a wing a deflection pushes the other way from a lone plate");
        // its lift acts at the strip's centre: 5 m ahead (half the host chord) - the extra moment is (-X 5 m) x the lift
        var rw = Result(onWing, vel, 0.25f);
        Check(rw.DirectTorque.LengthSquared() > 0f && Vector3.Dot(rw.DirectTorque, Vector3.Cross(-C * 5f, dWing)) > 0f, "on a wing the strip moment is missing or turned");
        // the air the other way (from +X, where nothing is): the lone plate exactly
        var back = -vel;
        Vector3 bLone = Result(lone, back, 0.25f).Force - Result(lone, back, 0f).Force;
        Vector3 bWing = Result(onWing, back, 0.25f).Force - Result(onWing, back, 0f).Force;
        Check((bWing - bLone).Length() <= 0.01f * bLone.Length(), $"air from the free edge: {bWing.Length():F0} N, the lone plate {bLone.Length():F0} N");
        // flap effectiveness (thin airfoil): 1 for an all-moving surface, ~0.82 at half the chord, ~0.66 at 30 %, rising
        Check(ControlSurfaceGeometry.FlapEffectiveness(1f) == 1f, "tau(1) != 1");
        Check(MathF.Abs(ControlSurfaceGeometry.FlapEffectiveness(0.5f) - 0.818f) < 0.01f, $"tau(0.5) {ControlSurfaceGeometry.FlapEffectiveness(0.5f):F3}");
        Check(MathF.Abs(ControlSurfaceGeometry.FlapEffectiveness(0.3f) - 0.661f) < 0.01f, $"tau(0.3) {ControlSurfaceGeometry.FlapEffectiveness(0.3f):F3}");
        for (int k = 1; k < 100; k++) Check(ControlSurfaceGeometry.FlapEffectiveness(k / 100f) > ControlSurfaceGeometry.FlapEffectiveness((k - 1) / 100f), "tau not rising");
        // stall later at low aspect ratio: the lone AR 1 plate still gains from 15 to 25 deg; the AR 7.5 / 2.75 section not
        float Lift(ControlSurface cs, float deg) { float a = deg * MathF.PI / 180f; return Result(cs, -new Vector3(MathF.Cos(a), MathF.Sin(a), 0f) * 60f, 0f).Force.Y; }
        Check(Lift(lone, 25f) > Lift(lone, 15f), $"AR 1 plate stalled before 25 deg ({Lift(lone, 15f):F0} -> {Lift(lone, 25f):F0} N)");
        var hiAr = Large(); hiAr.Surroundings = new SurfaceSurroundings(); hiAr.Surroundings.SectionSpan[0] = hiAr.Surroundings.SectionSpan[1] = 60f;
        Check(Lift(hiAr, 25f) < Lift(hiAr, 15f), $"AR 8 section not stalled by 25 deg ({Lift(hiAr, 15f):F0} -> {Lift(hiAr, 25f):F0} N)");
        // the mixer's sign holds in any surroundings: for random placements, hosts, spans and plates, and the air along the
        // chord either way at a small angle, +input adds force along the mixer's liftDir (hinge x chord, flipped with the air)
        for (int i = 0; i < 4000; i++)
        {
            var h = RandUnit(r); var c = Vector3.Normalize(Vector3.Cross(h, RandUnit(r))); var n = Vector3.Cross(h, c);
            var cs = new ControlSurface(Vector3.Zero, Vector3I.Zero, h, c, 56.25f) { MaxDeflection = 20f, AspectRatio = 1f, Extents = Vector3.Abs(h * 7.5f + c * 7.5f + n * 2.5f) };
            cs.Surroundings = new SurfaceSurroundings();
            for (int e = 0; e < 2; e++) { cs.Surroundings.HostChord[e] = r.Next(2) == 0 ? 0f : (float)r.NextDouble() * 20f; cs.Surroundings.SectionSpan[e] = 7.5f + (float)r.NextDouble() * 40f; cs.Surroundings.EndPlates[e] = r.Next(3); }
            bool backward = r.Next(2) == 0;
            var air = Vector3.Normalize((backward ? -c : c) + n * (float)(r.NextDouble() * 0.2 - 0.1) + h * (float)(r.NextDouble() * 0.3 - 0.15));
            var v = -air * (30f + (float)r.NextDouble() * 150f);
            var liftDir = Vector3.Dot(-v, c) < 0f ? -n : n;
            var f0 = Result(cs, v, 0f).Force;
            Check(Vector3.Dot(Result(cs, v, 0.25f).Force - f0, liftDir) > 0f, $"sign #{i}: +input against the mixer's liftDir with a host of {cs.Surroundings.HostChord[backward ? 0 : 1]:F1} m");
            Check(Vector3.Dot(Result(cs, v, -0.25f).Force - f0, liftDir) < 0f, $"sign #{i}: -input along the mixer's liftDir");
        }
        // any surroundings, any flow, any input: finite
        for (int i = 0; i < 3000; i++)
        {
            var h = RandUnit(r); var c = Vector3.Normalize(Vector3.Cross(h, RandUnit(r)));
            var cs = new ControlSurface(Vector3.Zero, Vector3I.Zero, h, c, 2.25f) { MaxDeflection = 35f, AspectRatio = 1f, Extents = new Vector3(1.5f, 1.5f, 0.5f) };
            cs.Surroundings = new SurfaceSurroundings();
            for (int e = 0; e < 2; e++) { cs.Surroundings.HostChord[e] = (float)r.NextDouble() * 6f; cs.Surroundings.SectionSpan[e] = 1.5f + (float)r.NextDouble() * 30f; cs.Surroundings.EndPlates[e] = r.Next(3); }
            var res = Result(cs, RandUnit(r) * (5f + (float)r.NextDouble() * 400f), (float)(r.NextDouble() * 2 - 1));
            Check(float.IsFinite(res.Force.X + res.Force.Y + res.Force.Z) && float.IsFinite(res.DirectTorque.X + res.DirectTorque.Y + res.DirectTorque.Z), $"surroundings #{i}: not finite");
        }
    }
}

static partial class Program
{
    static Vector3 Dir(string d) => d switch
    {
        "Forward" => new Vector3(0, 0, -1), "Backward" => new Vector3(0, 0, 1), "Up" => new Vector3(0, 1, 0),
        "Down" => new Vector3(0, -1, 0), "Right" => new Vector3(1, 0, 0), "Left" => new Vector3(-1, 0, 0), _ => Vector3.Zero,
    };

    /// <summary>The Tu-134 Big as flown (Tests/data/tu134/tu134_big.blocks - a harness "gen dump", local only): each of its
    /// five 3x3x1 surfaces' measured section. Skipped when the dump is not there.</summary>
    static void Tu134Sections()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "tu134", "tu134_big.blocks");
        if (!File.Exists(path)) { Console.WriteLine("tu134: no dump, skipped"); return; }
        var boxes = new List<(Vector3I, Vector3I)>(); var surfaces = new List<IAeroBlockComponent>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("#")) continue;
            var w = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (w.Length < 8) continue;
            var lo = new Vector3I(int.Parse(w[0]), int.Parse(w[1]), int.Parse(w[2])); var hi = new Vector3I(int.Parse(w[3]), int.Parse(w[4]), int.Parse(w[5]));
            var dims = hi - lo + new Vector3I(1, 1, 1);
            int six = (dims.X == 6 ? 1 : 0) + (dims.Y == 6 ? 1 : 0) + (dims.Z == 6 ? 1 : 0), two = (dims.X == 2 ? 1 : 0) + (dims.Y == 2 ? 1 : 0) + (dims.Z == 2 ? 1 : 0);
            if (six == 2 && two == 1)
            {
                Vector3 f = Dir(w[6]), u = Dir(w[7]);
                surfaces.Add(new ControlSurface(new Vector3(lo.X + hi.X, lo.Y + hi.Y, lo.Z + hi.Z) * 0.125f, lo, f, Vector3.Cross(f, u), 2.25f)
                    { MaxDeflection = 20f, AspectRatio = 1f, Extents = new Vector3(dims.X, dims.Y, dims.Z) * 0.25f });
            }
            else boxes.Add((lo, hi));   // (the surfaces are not structure - the game's accessor leaves them out)
        }
        ControlSurfaceGeometry.Measure(surfaces, new SnapshotGridAccessor(boxes));
        foreach (ControlSurface cs in surfaces)
            Console.WriteLine($"tu134 surface at {cs.BlockPosition} hinge {cs.HingeAxis} chord {cs.ChordDirection}: +C {cs.Surroundings.Describe(0)}; -C {cs.Surroundings.Describe(1)}");
        Check(surfaces.Count == 5, $"tu134: {surfaces.Count} surfaces in the dump, want 5");
        // as measured 2026-10-13 (checked against a slice of the dump: the swept wing tapers to its tip, the ailerons sit
        // beside it - little wing directly ahead): the leading edge (-C, toward the nose) only; nothing behind (+C)
        foreach (ControlSurface cs in surfaces)
        {
            var g = cs.Surroundings; bool fin = MathF.Abs(cs.HingeAxis.Y) > 0.5f, tail = cs.BlockPosition.Y > 40;
            float want = fin ? 2.17f : tail ? 0.75f : 0.5f;
            Check(MathF.Abs(g.HostChord[1] - want) < 0.13f && g.HostChord[0] == 0f, $"tu134 {cs.BlockPosition}: host {g.HostChord[1]:F2} m ahead, want {want:F2}");
        }
    }
}
