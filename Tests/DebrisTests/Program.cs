using AeroMod;

// Drag on loose fracture debris (Debris/DebrisDragMath.cs, the literal mod source), offline:
//  1. the projected area (the game's ComputeProjectedAreaLocal);
//  2. one step: small steps match the explicit 0.5 rho Cd A v^2 dt / m; any step - orbital speed in sea-level air, a
//     gram in a gale, dt of a second - takes off less than the speed there is (never reverses, never NaN);
//  3. with the game's own wind push (WindGeneratorComponent.ApplyWindForce, ported below), the two together are one
//     drag against the moving air: a chip at rest in the wind gets the game's push alone; a fast chip in wind gets
//     quadratic drag in all; in still air the game does nothing and this all of it;
//  4. a chip falling in 1 g settles at sqrt(2 m g / (rho Cd A)); a chip released in a wind is carried to the wind's speed;
//  5. spin: decays, keeps its sign, never flips, any dt.
// Run: dotnet run -c Release [-- verbose]. Exit 0 = all pass.
static class Program
{
    static int _fails, _checks;
    static bool _verbose;
    static void Check(bool ok, string what) { _checks++; if (!ok) { _fails++; Console.WriteLine("FAIL " + what); } else if (_verbose) Console.WriteLine("ok   " + what); }
    static bool Near(float a, float b, float rel) => MathF.Abs(a - b) <= rel * MathF.Max(MathF.Abs(a), MathF.Abs(b)) + 1e-6f;

    const float Dt = 1f / 60f, Rho = 1.225f;

    /// <summary>The game's wind push as a velocity change (WindGeneratorComponent.ApplyWindForce: Cd 1, 0.5 rho A Ws
    /// |w - v| along w - v, capped at 5 / InvMass).</summary>
    static Vector3 GameWind(Vector3 v, Vector3 wind, Vector3 size, float rho, float invMass)
    {
        float ws = wind.Length();
        if (ws <= 0f) return Vector3.Zero;
        Vector3 rel = wind - v; float n = rel.Length();
        if (n < 0.001f) return Vector3.Zero;
        Vector3 d = rel / n;
        Vector3 imp = d * (0.5f * rho * DebrisDragMath.ProjectedArea(size, d) * ws * n * Dt);
        float cap = 5f / invMass, l = imp.Length();
        if (l > cap) imp *= cap / l;
        return imp * invMass;
    }

    /// <summary>One step as the job does it (axis-aligned body): the game's wind first, then this mod's drag.</summary>
    static Vector3 Step(Vector3 v, Vector3 wind, Vector3 size, float rho, float cd, float invMass, Vector3 gravity, float dt = Dt)
    {
        v += gravity * dt;
        v += GameWind(v, wind, size, rho, invMass);
        float ws = wind.Length();
        Vector3 rel = v - wind; float s = rel.Length();
        if (s > 0.05f)
        {
            Vector3 d = rel / s;
            v -= d * DebrisDragMath.DeltaV(s, rho, cd, DebrisDragMath.ProjectedArea(size, d), invMass, ws, dt);
        }
        return v;
    }

    static int Main(string[] args)
    {
        _verbose = args.Contains("verbose");
        Vector3 cube = new Vector3(1f, 1f, 1f), plate = new Vector3(2f, 0.5f, 1f);

        // ── 1. projected area ──
        Check(Near(DebrisDragMath.ProjectedArea(plate, Vector3.UnitX), 0.5f, 1e-6f), "area along X = Y*Z");
        Check(Near(DebrisDragMath.ProjectedArea(plate, -Vector3.UnitY), 2f, 1e-6f), "area along -Y = X*Z");
        Check(Near(DebrisDragMath.ProjectedArea(plate, Vector3.UnitZ), 1f, 1e-6f), "area along Z = X*Y");
        Vector3 diag = Vector3.Normalize(new Vector3(1, 1, 1));
        Check(Near(DebrisDragMath.ProjectedArea(cube, diag), MathF.Sqrt(3f), 1e-5f), "unit cube along its diagonal: sqrt 3 (the true silhouette)");

        // ── 2. one step ──
        {
            float m = 50f, A = 1f, v = 30f;
            float dv = DebrisDragMath.DeltaV(v, Rho, 1f, A, 1f / m, 0f, Dt);
            float expl = 0.5f * Rho * A * v * v * Dt / m;
            Check(Near(dv, expl, 0.01f), $"small step = explicit drag ({dv:F5} vs {expl:F5})");
            Check(Near(DebrisDragMath.DeltaV(v, Rho, 2f, A, 1f / m, 0f, Dt), 2f * expl, 0.02f), "Cd scales it");
            Check(DebrisDragMath.DeltaV(v, Rho, 0f, A, 1f / m, 0f, Dt) == 0f, "Cd 0: none");
            Check(DebrisDragMath.DeltaV(0f, Rho, 1f, A, 1f / m, 0f, Dt) == 0f, "at rest: none");
            Check(DebrisDragMath.DeltaV(v, 0f, 1f, A, 1f / m, 0f, Dt) == 0f, "vacuum: none");
            Check(DebrisDragMath.DeltaV(v, Rho, 1f, A, 0f, 0f, Dt) == 0f, "static body (InvMass 0): none");
            Check(DebrisDragMath.DeltaV(float.NaN, Rho, 1f, A, 1f / m, 0f, Dt) == 0f, "NaN speed: none");
            Check(DebrisDragMath.DeltaV(v, float.NaN, 1f, A, 1f / m, 0f, Dt) == 0f, "NaN density: none");
            // stiff cases: never past zero
            foreach (var (s, rho, a, mass, dt, what) in new[] {
                (7800f, Rho, 4f, 1f, Dt, "orbital speed, sea-level air, 1 kg plate"),
                (7800f, 1e-5f, 6f, 500f, Dt, "orbital speed, 80 km air (re-entry)"),
                (300f, Rho, 1f, 0.001f, Dt, "a gram at 300 m/s"),
                (100f, Rho, 1f, 1f, 1f, "dt of a whole second"),
                (1e6f, 1000f, 100f, 1e-4f, 10f, "absurd: everything at once") })
            {
                float d = DebrisDragMath.DeltaV(s, rho, 1f, a, 1f / mass, 0f, dt);
                Check(d >= 0f && d < s && float.IsFinite(d), $"{what}: 0 <= dv ({d:G4}) < v ({s})");
            }
            // the exact step: two half steps land where one full step does, for the drag alone (v / (1 + k v dt) composes)
            float k = 0.5f * Rho * 1f / 1f;   // m 1, A 1
            float v1 = 200f - DebrisDragMath.DeltaV(200f, Rho, 1f, 1f, 1f, 0f, 0.5f);
            float vh = 200f - DebrisDragMath.DeltaV(200f, Rho, 1f, 1f, 1f, 0f, 0.25f);
            vh -= DebrisDragMath.DeltaV(vh, Rho, 1f, 1f, 1f, 0f, 0.25f);
            Check(Near(v1, vh, 1e-4f) && Near(v1, 200f / (1f + k * 200f * 0.5f), 1e-4f), $"exact over the step: 1 x 0.5 s = 2 x 0.25 s = v/(1+kv dt) ({v1:F3} / {vh:F3})");
        }

        // ── 3. with the game's wind ──
        {
            float m = 20f, inv = 1f / m; Vector3 wind = new Vector3(15f, 0, 0);
            // at rest in the wind: rel = 15 m/s, the game pushes 0.5 rho A 15 15 = quadratic already: this adds nothing
            float dvRest = DebrisDragMath.DeltaV(15f, Rho, 1f, 1f, inv, 15f, Dt);
            Check(dvRest < 0.02f * (0.5f * Rho * 225f * Dt * inv), $"at rest in a 15 m/s wind: ~nothing added ({dvRest:G3}), the game's push is the drag");
            // slower than the wind relative to it (Cd |v_rel| < Ws): the game's term is the larger - nothing added
            Check(DebrisDragMath.DeltaV(5f, Rho, 1f, 1f, inv, 15f, Dt) == 0f, "moving with a 15 m/s wind at 5 m/s relative: nothing added");
            // fast through the wind: game + this = the quadratic drag's exact step in the relative speed, s x / (1 + x)
            float s = 120f, A = 1f;
            float game = 0.5f * Rho * A * 15f * s * Dt * inv;
            float ours = DebrisDragMath.DeltaV(s, Rho, 1f, A, inv, 15f, Dt);
            float x = 0.5f * Rho * A * s * Dt * inv, quad = s * x / (1f + x);
            Check(Near(game + ours, quad, 1e-4f), $"120 m/s through a 15 m/s wind: game {game:F3} + this {ours:F3} = quadratic {quad:F3}");
            // still air: the game does nothing, this is all of it
            Check(Near(DebrisDragMath.DeltaV(s, Rho, 1f, A, inv, 0f, Dt), quad, 1e-4f), "still air: this alone = quadratic");

            // a chip released at rest in a 15 m/s wind (no gravity) is carried up to the wind, never past it
            Vector3 v = Vector3.Zero; float maxVx = 0f;
            for (int i = 0; i < 60 * 60; i++) { v = Step(v, wind, cube, Rho, 1f, inv, Vector3.Zero); maxVx = MathF.Max(maxVx, v.X); }
            Check(v.X > 14f && maxVx <= 15.001f, $"a chip in a 15 m/s wind is carried to it (v {v.X:F2}, max {maxVx:F3}), never past");
            // a chip thrown at 300 m/s into a 15 m/s headwind: slows, comes round, ends with the wind, never overshoots it
            v = new Vector3(-300f, 0, 0); maxVx = -1e9f;
            for (int i = 0; i < 60 * 120; i++) { v = Step(v, wind, plate, Rho, 1f, inv, Vector3.Zero); maxVx = MathF.Max(maxVx, v.X); }
            Check(v.X > 14f && maxVx <= 15.001f && v.IsValid(), $"thrown into a headwind: ends carried by it (v {v.X:F2}, max {maxVx:F3})");
        }

        // ── 4. terminal velocity (to within one step's gravity: the loop measures after the drag, the game after gravity) ──
        foreach (var (size, mass, cd) in new[] { (cube, 50f, 1f), (plate, 120f, 1f), (new Vector3(0.25f, 0.25f, 0.5f), 4f, 1f), (cube, 200f, 0.5f) })
        {
            Vector3 g = new Vector3(0, -9.81f, 0);
            float A = DebrisDragMath.ProjectedArea(size, Vector3.UnitY);
            float vt = MathF.Sqrt(2f * mass * 9.81f / (Rho * cd * A));
            Vector3 v = Vector3.Zero;
            for (int i = 0; i < 60 * 120; i++) v = Step(v, Vector3.Zero, size, Rho, cd, 1f / mass, g);
            Check(MathF.Abs(-v.Y - vt) <= 1.1f * 9.81f * Dt, $"terminal velocity {size} {mass} kg Cd {cd}: {-v.Y:F2} m/s vs sqrt(2mg/(rho Cd A)) {vt:F2}");
        }
        // falling from orbit speed straight down into thick air: slows to terminal, never reverses (stable at any speed)
        {
            Vector3 v = new Vector3(0, -7800f, 0); bool upward = false;
            for (int i = 0; i < 60 * 60; i++) { v = Step(v, Vector3.Zero, cube, Rho, 1f, 1f / 10f, new Vector3(0, -9.81f, 0)); upward |= v.Y > 0f || !v.IsValid(); }
            float vt = MathF.Sqrt(2f * 10f * 9.81f / Rho);
            Check(!upward && MathF.Abs(-v.Y - vt) <= 1.1f * 9.81f * Dt, $"7800 m/s down into sea-level air: never reverses, settles at {-v.Y:F2} (terminal {vt:F2})");
        }

        // ── 5. spin ──
        {
            Vector3 w = new Vector3(20f, -5f, 0.5f);
            Vector3 w1 = DebrisDragMath.SpinDamped(w, plate, 30f, Rho, 1f, Dt);
            Check(MathF.Abs(w1.X) < MathF.Abs(w.X) && MathF.Abs(w1.Y) < MathF.Abs(w.Y) && MathF.Abs(w1.Z) <= MathF.Abs(w.Z), "spin decays on every axis");
            Check(MathF.Sign(w1.X) == MathF.Sign(w.X) && MathF.Sign(w1.Y) == MathF.Sign(w.Y) && MathF.Sign(w1.Z) == MathF.Sign(w.Z), "spin keeps its sign");
            Check(1f - w1.X / w.X < 0.05f, $"slight: a 30 kg plate at 20 rad/s loses {100f * (1f - w1.X / w.X):F2}% a step");
            Vector3 wStiff = DebrisDragMath.SpinDamped(new Vector3(1000f, -1000f, 1000f), new Vector3(10f, 10f, 10f), 0.01f, 1000f, 10f, 10f);
            Check(wStiff.X > 0f && wStiff.Y < 0f && wStiff.Z > 0f && wStiff.IsValid(), $"stiff spin: never flips ({wStiff})");
            Check(DebrisDragMath.SpinDamped(w, plate, 30f, 0f, 1f, Dt) == w, "vacuum: spin untouched");
            Check(DebrisDragMath.SpinDamped(w, Vector3.Zero, 30f, Rho, 1f, Dt) == w, "zero box: untouched, no NaN");
            Check(DebrisDragMath.SpinDamped(w, plate, 0f, Rho, 1f, Dt) == w, "massless: untouched");
            // a rod spinning about its own axis barely meets the air; end over end its 2 m span sweeps it
            Vector3 rod = new Vector3(2f, 0.2f, 0.2f);
            Vector3 about = DebrisDragMath.SpinDamped(new Vector3(10f, 0, 0), rod, 30f, Rho, 1f, Dt);
            Vector3 across = DebrisDragMath.SpinDamped(new Vector3(0, 10f, 0), rod, 30f, Rho, 1f, Dt);
            Check(10f - about.X < 0.2f * (10f - across.Y), $"a rod's spin about its axis is damped far less than end over end ({10f - about.X:G3} vs {10f - across.Y:G3})");
        }

        Console.WriteLine(_fails == 0 ? $"DebrisTests: all {_checks} checks passed" : $"DebrisTests: {_fails} of {_checks} checks FAILED");
        return _fails == 0 ? 0 : 1;
    }
}
