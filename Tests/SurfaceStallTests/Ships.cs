#pragma warning disable
using System.IO;
using AeroMod;

// 5. dumped ships: the airframe (LiftingSurfaceModel over the hull, as AeroBench builds it) and the surfaces (the mod's own
// ControlSurface / ControlSurfaceGeometry, and Legacy for the before), measured over the angle of attack.
static partial class Program
{
    const float BlockSize = 0.5f;
    static readonly System.Globalization.CultureInfo CI = System.Globalization.CultureInfo.InvariantCulture;

    sealed class Ship
    {
        public string Name; public SnapshotGridAccessor Grid; public SmoothSurfaceProvider Surface; public ManifoldClassifier Manifold;
        public LiftingSurfaceModel Model; public Vector3 Com; public float Mass; public double[] I = new double[9];
        public Vector3 Nose = Vector3.UnitZ, Up = Vector3.UnitY; public List<ControlSurface> Surf = new(); public float GyroSum;
    }

    static string DataDir() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "surfaces"));

    /// <summary>A ship's airframe from its blocks (min, max per box), as AeroBench builds it: the surface, the wings, the shadow map.</summary>
    static Ship Build(string name, List<(Vector3I, Vector3I)> boxes)
    {
        int k = SnapshotGridAccessor.CellScale(BlockSize);
        var (cs, co) = SnapshotGridAccessor.CellGeometry(k);
        var grid = new SnapshotGridAccessor(boxes);
        var surface = new SmoothSurfaceProvider { CellSize = cs, CellOffset = co };
        surface.BeginBuild(grid, BlockSize); while (!surface.AddCellBatch(int.MaxValue)) { } surface.FinalizeBuild();
        var manifold = new ManifoldClassifier(); manifold.Classify(surface);
        var inner = new DampedShadowedDragModel();
        var model = new LiftingSurfaceModel(inner, liftModel: new CompressibleWingModel());
        if (model.Detector is ConnectedComponentWingDetector cc) { cc.Manifold = manifold; cc.ManifoldSurface = surface; cc.CellSize = cs; cc.CellOffset = co; }
        model.Detector.Invalidate();
        var wings = model.Detector.Detect(grid, surface, BlockSize);
        var shadow = new PrecomputedShadowMap { Manifold = manifold }; shadow.PrecomputeAll(grid, surface);
        model.InstallWings(wings); inner.InstallShadowMap(shadow);
        model.BuildFaceOverrideIndex(surface, new List<IAeroBlockComponent>());
        model.ExcludeWingFaces(inner, surface.Version);
        return new Ship { Name = name, Grid = grid, Surface = surface, Manifold = manifold, Model = model };
    }

    static Ship Load(string dir, string stem)
    {
        var boxes = new List<(Vector3I, Vector3I)>();
        foreach (var line in File.ReadLines(Path.Combine(dir, stem + ".boxes")))
        {
            if (line.StartsWith("#")) continue;
            var p = line.Split(' '); if (p.Length < 6) continue;
            boxes.Add((new Vector3I(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2])), new Vector3I(int.Parse(p[3]), int.Parse(p[4]), int.Parse(p[5]))));
        }
        var ship = Build(stem, boxes);
        var grid = ship.Grid;
        static Vector3 V3(string[] p, int i) => new Vector3(float.Parse(p[i], CI), float.Parse(p[i + 1], CI), float.Parse(p[i + 2], CI));
        foreach (var raw in File.ReadAllLines(Path.Combine(dir, stem + ".ship")))
        {
            var p = raw.Split(' ');
            switch (p[0])
            {
                case "mass": ship.Mass = float.Parse(p[1], CI); break;
                case "com": ship.Com = V3(p, 1); break;
                case "inertia": for (int i = 0; i < 9; i++) ship.I[i] = double.Parse(p[1 + i], CI); break;
                case "nose": ship.Nose = V3(p, 1); break;
                case "up": ship.Up = V3(p, 1); break;
                case "gyro": ship.GyroSum += float.Parse(p[1], CI); break;
                case "surf":
                {
                    var pos = V3(p, 2); var F = V3(p, 5); var U = V3(p, 8); var ext = V3(p, 11);
                    static float AreaFacing(Vector3 e, Vector3 n) { float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z); return ax >= ay && ax >= az ? e.Y * e.Z : ay >= az ? e.X * e.Z : e.X * e.Y; }
                    if (p[1] == "cs1")
                    {
                        // (DefaultMappings ControlSurface_1x1: hinge = F x U canonicalised, chord = F, planform facing Up; no settings)
                        var h = Vector3.Cross(F, U); float ax = MathF.Abs(h.X), ay = MathF.Abs(h.Y), az = MathF.Abs(h.Z);
                        float dom = ay >= ax && ay >= az ? h.Y : ax >= az ? h.X : h.Z; if (dom < 0) h = -h;
                        ship.Surf.Add(new ControlSurface(pos, Vector3I.Zero, h, F, AreaFacing(ext, U)));
                    }
                    else ship.Surf.Add(new ControlSurface(pos, Vector3I.Zero, F, Vector3.Cross(F, U), AreaFacing(ext, U)) { MaxDeflection = 20f, AspectRatio = 1f, Cd0 = 0.03f, Extents = ext });
                    break;
                }
            }
        }
        ControlSurfaceGeometry.Measure(ship.Surf.Cast<IAeroBlockComponent>().ToList(), ship.Grid);
        return ship;
    }

    static double Inertia(Ship s, Vector3 e) { double r = 0; float[] v = { e.X, e.Y, e.Z }; for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) r += v[i] * s.I[i * 3 + j] * v[j]; return r; }

    static AeroContext Ctx(Ship s, Vector3 vel, Vector3 omega = default) => new AeroContext(s.Grid, s.Surface, vel, Sea, s.Com, BlockSize, omega, -1f, s.Manifold);
    static Vector3 Airframe(Ship s, Vector3 vel, Vector3 omega = default)
    {
        AeroResult r = default;
        for (int i = 0; i < 3; i++) r = s.Model.Compute(Ctx(s, vel, omega));
        return r.Torque;
    }

    /// <summary>A surface's torque about the centre of mass at input u.</summary>
    static Vector3 T(ControlSurface cs, in AeroContext ctx, Vector3 com, float u, bool legacy) => Eval(cs, LocalAeroConditions.FromContext(ctx, cs.Position), u, legacy).TorqueAbout(com);

    static readonly float[] Scan = { -1f, -0.875f, -0.75f, -0.625f, -0.5f, -0.375f, -0.25f, -0.125f, 0.125f, 0.25f, 0.375f, 0.5f, 0.625f, 0.75f, 0.875f, 1f };

    // scratch (the allocator's lists, as AeroGridComponent keeps them)
    static readonly List<Vector3> _a = new(); static float[] _u = new float[64];
    static readonly Vector3[] _kT = new Vector3[64 * ControlSurface.Knots]; static readonly float[] _kU = new float[64 * ControlSurface.Knots];

    /// <summary>The allocator's measure of surface i (its knots into slot i; the base's torque back) - after: Measure, as
    /// AeroGridComponent; and its full-throw slope added to _a.</summary>
    static Vector3 Knots(ControlSurface cs, in AeroContext ctx, Vector3 com, int i)
    {
        int o = i * ControlSurface.Knots;
        var tb = ControlSurface.Measure(cs, LocalAeroConditions.FromContext(ctx, cs.Position), com, 0f, _kT.AsSpan(o, ControlSurface.Knots), _kU.AsSpan(o, ControlSurface.Knots));
        _a.Add(0.5f * (_kT[o + ControlSurface.Knots - 1] - _kT[o]));
        return tb;
    }

    /// <summary>The surfaces' torque change along s e (N m) three ways: best (each one's inputs scanned), alloc (the allocator
    /// asked for far more than they have along s e: legacy - the +-1/2 secant, Allocate, clamped; after - Measure, Allocate,
    /// Refine), mixer (the keyboard mixer at a full command along s e: legacy - geometric sign; after - MixerInput); and the
    /// allocator's worst single surface along s e (N m, < 0: against).</summary>
    static (float best, float alloc, float mixer, float worstOne) Auth(Ship s, in AeroContext ctx, Vector3 vel, Vector3 e, float sgn, bool legacy)
    {
        var com = s.Com; int n = s.Surf.Count;
        Vector3 dir = e * sgn;
        float best = 0f, mixer = 0f;
        _a.Clear();
        var t0 = new Vector3[n]; var tp = new Vector3[n]; var tm = new Vector3[n];
        float reach = 0f;
        for (int i = 0; i < n; i++)
        {
            var cs = s.Surf[i];
            t0[i] = T(cs, ctx, com, 0f, legacy);
            float b = 0f;
            foreach (var u in Scan) b = MathF.Max(b, Vector3.Dot(T(cs, ctx, com, u, legacy) - t0[i], dir));
            best += b;
            tp[i] = T(cs, ctx, com, 1f, legacy); tm[i] = T(cs, ctx, com, -1f, legacy);
            if (legacy) _a.Add(T(cs, ctx, com, 0.5f, true) - T(cs, ctx, com, -0.5f, true));
            else Knots(cs, ctx, com, i);
            reach += _a[i].Length();
            // the mixer: a full command along dir through the rate law (saturating) - legacy: its geometric effectiveness;
            // after: ControlSurface.MixerInput on the measured sides, as AeroGridComponent
            float um;
            if (legacy)
            {
                Vector3 eff = Legacy.MixerEffect(cs, vel, com); float el = eff.Length();
                um = el > 1e-6f ? Math.Clamp(ControlSurface.RateCommand(eff / el, dir * 20f, Vector3.Zero, 1f), -1f, 1f) : 0f;
            }
            else um = Math.Clamp(ControlSurface.MixerInput(tp[i] - t0[i], tm[i] - t0[i], dir * 20f, Vector3.Zero, 1f), -1f, 1f);
            mixer += Vector3.Dot(T(cs, ctx, com, um, legacy) - t0[i], dir);
        }
        var want = dir * (1000f * MathF.Max(reach, 1f));
        float alloc = 0f, worst = 0f;
        if (ControlSurface.Allocate(_a, want, _u))
        {
            if (!legacy) ControlSurface.Refine(_kT, _kU, want, _u, n);
            for (int i = 0; i < n; i++)
            {
                float u = Math.Clamp(_u[i], -1f, 1f);
                float c = Vector3.Dot(T(s.Surf[i], ctx, com, u, legacy) - t0[i], dir);
                alloc += c; worst = MathF.Min(worst, c);
            }
        }
        return (best, alloc, mixer, worst);
    }

    static Vector3 Neutral(Ship s, in AeroContext ctx, bool legacy) { Vector3 t = Vector3.Zero; foreach (var cs in s.Surf) t += T(cs, ctx, s.Com, 0f, legacy); return t; }

    static void Ships(bool quick)
    {
        string dir = DataDir();
        string[] stems = { "ssto", "ssto_full", "TupolevTu134Big", "Plane2" };
        if (!Directory.Exists(dir) || !stems.Any(st => File.Exists(Path.Combine(dir, st + ".boxes"))))
        { Console.WriteLine($"\n== 5. dumped ships: none in {dir} (local only: Tests/data is not committed) - skipped"); return; }
        const float V = 100f;
        var gyroOf = new Dictionary<string, float>();
        foreach (var stem in stems.Append("ssto_gyro"))
            if (File.Exists(Path.Combine(dir, stem + ".ship")))
                foreach (var l in File.ReadLines(Path.Combine(dir, stem + ".ship"))) if (l.StartsWith("gyro ")) gyroOf[stem] = gyroOf.GetValueOrDefault(stem) + float.Parse(l.Split(' ')[1], CI);
        foreach (var stem in stems)
        {
            if (!File.Exists(Path.Combine(dir, stem + ".boxes")) || !File.Exists(Path.Combine(dir, stem + ".ship"))) { Console.WriteLine($"\n== 5. {stem}: no dump - skipped"); continue; }
            var s = Load(dir, stem);
            var nose = Vector3.Normalize(s.Nose); var up = Vector3.Normalize(s.Up); var pitch = Vector3.Normalize(Vector3.Cross(nose, up));
            Console.WriteLine($"\n== 5. {stem}: {s.Mass / 1000:F1} t, {s.Surf.Count} surfaces, 100 m/s sea level (kN m; pitch + nose up). best: each surface's inputs scanned; alloc: the allocator; mixer: the keyboard mixer at full command. before | after");
            Console.WriteLine($"   {"AoA",3} | {"air bef",8} {"aft",6} | {"DOWN best",9} {"alloc",6} {"mixer",6} | {"best",6} {"alloc",6} {"mixer",6} | {"UP best",7} {"alloc",6} {"mixer",6} | {"best",6} {"alloc",6} {"mixer",6} | {"ROLL best",9} {"alloc",6} | {"best",6} {"alloc",6} {"mixer",6} | {"YAW best",8} {"alloc",6} | {"best",6} {"alloc",6} {"mixer",6}");
            float[] degs = quick ? new[] { 0f, 20f, 45f, 90f } : new[] { 0f, 5f, 10f, 15f, 20f, 25f, 30f, 35f, 40f, 45f, 50f, 60f, 70f, 80f, 90f };
            foreach (float deg in degs)
            {
                float a = deg * D2R;
                var tv = Vector3.Normalize(nose * MathF.Cos(a) - up * MathF.Sin(a)); var vel = tv * V;
                var ctx = Ctx(s, vel);
                var air = Airframe(s, vel);
                float mB = Vector3.Dot(air + Neutral(s, ctx, true), pitch), mA = Vector3.Dot(air + Neutral(s, ctx, false), pitch);
                var dnB = Auth(s, ctx, vel, pitch, -1f, true); var dnA = Auth(s, ctx, vel, pitch, -1f, false);
                var upB = Auth(s, ctx, vel, pitch, +1f, true); var upA = Auth(s, ctx, vel, pitch, +1f, false);
                (float best, float alloc, float mixer, float worstOne) Min2(Ship sh, in AeroContext c, Vector3 e, bool lg)
                {
                    var p = Auth(sh, c, vel, e, 1f, lg); var m = Auth(sh, c, vel, e, -1f, lg);
                    return (MathF.Min(p.best, m.best), MathF.Min(p.alloc, m.alloc), MathF.Min(p.mixer, m.mixer), MathF.Min(p.worstOne, m.worstOne));
                }
                var rB = Min2(s, ctx, nose, true); var rA = Min2(s, ctx, nose, false);
                var yB = Min2(s, ctx, up, true); var yA = Min2(s, ctx, up, false);
                static string K(float v) => (v / 1000f).ToString("F0", CI);
                Console.WriteLine($"   {deg,3:F0} | {K(mB),8} {K(mA),6} | {K(dnB.best),9} {K(dnB.alloc),6} {K(dnB.mixer),6} | {K(dnA.best),6} {K(dnA.alloc),6} {K(dnA.mixer),6} | {K(upB.best),7} {K(upB.alloc),6} {K(upB.mixer),6} | {K(upA.best),6} {K(upA.alloc),6} {K(upA.mixer),6} | {K(rB.best),9} {K(rB.alloc),6} | {K(rA.best),6} {K(rA.alloc),6} {K(rA.mixer),6} | {K(yB.best),8} {K(yB.alloc),6} | {K(yA.best),6} {K(yA.alloc),6} {K(yA.mixer),6}");
                // no reversal (after): the allocator and the mixer never give torque against what was asked, on any axis, either
                // way; no surface of the allocator's against it (tolerance: 1 % of the best, 50 N m)
                foreach (var (what, x) in new[] { ("down", dnA), ("up", upA), ("roll", rA), ("yaw", yA) })
                {
                    float tol = 0.01f * MathF.Abs(x.best) + 50f;
                    Check(x.alloc >= -tol, $"{stem} {deg} deg {what}: the allocator gives {x.alloc / 1000:F1} kN m against the demand");
                    Check(x.mixer >= -tol, $"{stem} {deg} deg {what}: the mixer gives {x.mixer / 1000:F1} kN m against the pilot");
                    Check(x.worstOne >= -tol, $"{stem} {deg} deg {what}: an allocated surface gives {x.worstOne / 1000:F1} kN m against the demand");
                    // (the allocator gets most of what the surfaces have, measured at the inputs it may give)
                    Check(x.alloc >= 0.6f * x.best - tol, $"{stem} {deg} deg {what}: the allocator gives {x.alloc / 1000:F1} of a best {x.best / 1000:F1} kN m");
                }
                if (deg <= 5f)
                {
                    // attached flow: the surfaces' torque at small inputs as before (both models; the airframe is the same)
                    foreach (var cs in s.Surf)
                        foreach (float u in new[] { -0.25f, 0.25f })
                        {
                            var tn = T(cs, ctx, s.Com, u, false); var to = T(cs, ctx, s.Com, u, true);
                            Check((tn - to).Length() <= 1e-4f * MathF.Max(to.Length(), 100f), $"{stem} {deg} deg: a surface's torque at input {u} changed in attached flow ({(tn - to).Length():F0} of {to.Length():F0} N m)");
                        }
                }
            }
            LockIn(s, stem, pitch, nose, up, gyroOf);
            if (!quick) Recovery(s, stem, pitch, nose, up, gyroOf);
        }
        if (!quick) AllocCost(dir);
    }

    /// <summary>The law's gyro reach (N m): min(0.01 m^2, the summed rating x Gyros.TorqueScale 0.25 x the target law's 0.25) -
    /// as the measurement harness and Tests/AeroBench gyros take it.</summary>
    static float GyroReach(float mass, float sum) => MathF.Min(0.01f * mass * mass, sum * 0.25f * 0.25f);

    /// <summary>The deep-stall lock-in: the lowest angle of attack (1 deg steps, from 10) where the air's nose-up moment beats
    /// the most nose-down the allocator gets from the surfaces (and the gyros' reach) - pitched up there it cannot come down.</summary>
    static void LockIn(Ship s, string stem, Vector3 pitch, Vector3 nose, Vector3 up, Dictionary<string, float> gyroOf)
    {
        float gyroOwn = GyroReach(s.Mass, s.GyroSum), gyroBorrowed = s.GyroSum > 0f ? gyroOwn : GyroReach(s.Mass, gyroOf.GetValueOrDefault("ssto_gyro"));
        float[] lockB = { float.NaN, float.NaN }, lockA = { float.NaN, float.NaN };
        for (int deg = 10; deg <= 90; deg++)
        {
            float a = deg * D2R;
            var tv = Vector3.Normalize(nose * MathF.Cos(a) - up * MathF.Sin(a)); var vel = tv * 100f;
            var ctx = Ctx(s, vel); var air = Airframe(s, vel);
            for (int lg = 0; lg < 2; lg++)
            {
                bool legacy = lg == 0;
                float m = Vector3.Dot(air + Neutral(s, ctx, legacy), pitch);
                var dn = Auth(s, ctx, vel, pitch, -1f, legacy);
                var arr = legacy ? lockB : lockA;
                if (float.IsNaN(arr[0]) && m > dn.alloc) arr[0] = deg;
                if (float.IsNaN(arr[1]) && m > dn.alloc + gyroBorrowed) arr[1] = deg;
            }
        }
        static string D(float v) => float.IsNaN(v) ? "none" : $"{v:F0} deg";
        string gyroNote = s.GyroSum > 0f ? $"its own gyros {gyroOwn / 1e6:F2} MN m" : $"with the SSTO's gyro fitted {gyroBorrowed / 1e6:F2} MN m";
        Console.WriteLine($"   LOCK-IN (air nose-up past the allocator's nose-down): surfaces only - before {D(lockB[0])}, after {D(lockA[0])}; {gyroNote} - before {D(lockB[1])}, after {D(lockA[1])}");
        // (more authority never locks it in sooner)
        Check(float.IsNaN(lockA[0]) || (!float.IsNaN(lockB[0]) && lockA[0] >= lockB[0] - 1f), $"{stem}: locks in sooner after ({D(lockA[0])}) than before ({D(lockB[0])})");
    }

    /// <summary>A time-stepped pitch recovery: the ship at 35 deg angle of attack, 100 m/s, its path held (the speed and the
    /// path's direction fixed - only its pitch turns: the attitude law's view of a deep stall), asked to come back to 5 deg by
    /// a pitch law (rate toward the angle wanted, x the inertia, less the air's moment - the aware law's shape) flown by the
    /// surfaces through the allocator - before (Legacy, +-1/2 secant) and after - with and without the gyros' reach. 10 s at 60
    /// Hz; the airframe's moment from a 1 deg table (rate damping from +-0.1 rad/s at each), the surfaces computed each step.</summary>
    static void Recovery(Ship s, string stem, Vector3 pitch, Vector3 nose, Vector3 up, Dictionary<string, float> gyroOf)
    {
        double Ip = Inertia(s, pitch);
        float gyro = s.GyroSum > 0f ? GyroReach(s.Mass, s.GyroSum) : GyroReach(s.Mass, gyroOf.GetValueOrDefault("ssto_gyro"));
        // airframe table: moment about pitch and its rate damping, every degree 0..90
        var mT = new float[91]; var dT = new float[91];
        for (int deg = 0; deg <= 90; deg++)
        {
            float a = deg * D2R; var tv = Vector3.Normalize(nose * MathF.Cos(a) - up * MathF.Sin(a)); var vel = tv * 100f;
            mT[deg] = Vector3.Dot(Airframe(s, vel), pitch);
            float mp = Vector3.Dot(Airframe(s, vel, pitch * 0.1f), pitch), mm = Vector3.Dot(Airframe(s, vel, -pitch * 0.1f), pitch);
            dT[deg] = (mp - mm) / 0.2f;
        }
        float Lerp(float[] t, float deg) { float x = Math.Clamp(deg, 0f, 90f); int i = Math.Min(89, (int)x); float f = x - i; return t[i] * (1 - f) + t[i + 1] * f; }
        string line = $"   RECOVERY from 35 deg (want 5 deg, 10 s):";
        foreach (bool withGyro in new[] { false, true })
        {
            if (withGyro && !(gyro > 0f)) continue;
            float endB = 0f, endA = 0f;
            for (int lg = 0; lg < 2; lg++)
            {
                bool legacy = lg == 0;
                float alpha = 35f * D2R, q = 0f;   // (q: the pitch rate, + nose up = + alpha with the path held)
                const float dt = 1f / 60f;
                for (int step = 0; step < 600; step++)
                {
                    var tv = Vector3.Normalize(nose * MathF.Cos(alpha) - up * MathF.Sin(alpha)); var vel = tv * 100f;
                    var ctx = Ctx(s, vel, pitch * q);
                    float deg = alpha / D2R;
                    float air = Lerp(mT, deg) + Lerp(dT, deg) * q;
                    // the law: the rate toward 5 deg (braking curve-free: 0.5 rad/s per rad, at most 0.3 rad/s), closed in 0.3 s
                    float wWant = Math.Clamp(0.5f * (5f * D2R - alpha), -0.3f, 0.3f);
                    float demand = (float)(Ip * (wWant - q) / 0.3) - air;
                    // the surfaces through the allocator (after: Measure / Allocate / Refine; before: the +-1/2 secant)
                    int n = s.Surf.Count; _a.Clear();
                    Vector3 t0 = Vector3.Zero; var tb = new Vector3[n];
                    for (int i = 0; i < n; i++)
                    {
                        var cs = s.Surf[i];
                        if (legacy) { tb[i] = T(cs, ctx, s.Com, 0f, true); _a.Add(T(cs, ctx, s.Com, 0.5f, true) - T(cs, ctx, s.Com, -0.5f, true)); }
                        else tb[i] = Knots(cs, ctx, s.Com, i);
                        t0 += tb[i];
                    }
                    Vector3 want = pitch * (demand - Vector3.Dot(t0, pitch));   // (pitch only: the other axes are not flown here)
                    float surf = Vector3.Dot(t0, pitch);
                    if (ControlSurface.Allocate(_a, want, _u))
                    {
                        if (!legacy) ControlSurface.Refine(_kT, _kU, want, _u, n);
                        for (int i = 0; i < n; i++) surf += Vector3.Dot(T(s.Surf[i], ctx, s.Com, Math.Clamp(_u[i], -1f, 1f), legacy) - tb[i], pitch);
                    }
                    float g = withGyro ? Math.Clamp(demand - surf, -gyro, gyro) : 0f;
                    float total = air + surf + g;
                    q += (float)(total / Ip) * dt;
                    alpha += q * dt;
                    alpha = Math.Clamp(alpha, -10f * D2R, 90f * D2R);
                }
                if (legacy) endB = alpha / D2R; else endA = alpha / D2R;
            }
            line += $" {(withGyro ? $"with gyros ({gyro / 1e6:F2} MN m)" : "surfaces only")}: before ends at {endB:F1} deg, after {endA:F1} deg{(endA < 15f ? " (recovered)" : " (held in the stall)")};";
            Check(endA <= endB + 1f, $"{stem} recovery {(withGyro ? "with gyros" : "surfaces only")}: after ends at {endA:F1} deg, worse than before {endB:F1}");
        }
        Console.WriteLine(line);
    }

    /// <summary>The allocation's cost per grid per tick as AeroGridComponent runs it (the SSTO's 10 surfaces): before - 3 Computes
    /// per surface (+-1/2, base) and Allocate; after - Measure (5 Computes) + Allocate + Refine. Reported; after: no allocation.</summary>
    static void AllocCost(string dir)
    {
        if (!File.Exists(Path.Combine(dir, "ssto.boxes"))) return;
        var s = Load(dir, "ssto");
        var nose = Vector3.Normalize(s.Nose); var up = Vector3.Normalize(s.Up); var pitch = Vector3.Normalize(Vector3.Cross(nose, up));
        int n = s.Surf.Count; var r = new Random(3);
        foreach (float maxDeg in new[] { 8f, 60f })
        foreach (bool legacyPath in new[] { true, false })
        {
            int reps = 4000; var times = new double[reps]; long bytes = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int rep = 0; rep < reps; rep++)
            {
                float a = (float)(r.NextDouble() * maxDeg) * D2R;
                var ctx = Ctx(s, Vector3.Normalize(nose * MathF.Cos(a) - up * MathF.Sin(a)) * 100f);
                var want = RandUnit(r) * (float)(r.NextDouble() * 3e5);
                long g0 = GC.GetAllocatedBytesForCurrentThread(); long t0 = sw.ElapsedTicks;
                _a.Clear();
                Vector3 sum = Vector3.Zero;
                for (int i = 0; i < n; i++)
                {
                    var cs = s.Surf[i]; var c = LocalAeroConditions.FromContext(ctx, cs.Position);
                    if (legacyPath)
                    {
                        cs.DeflectionInput = 0.5f; var tp = cs.Compute(c).TorqueAbout(s.Com);
                        cs.DeflectionInput = -0.5f; var tm = cs.Compute(c).TorqueAbout(s.Com);
                        cs.DeflectionInput = 0f; sum += cs.Compute(c).TorqueAbout(s.Com); _a.Add(tp - tm);
                    }
                    else
                    {
                        int o = i * ControlSurface.Knots;
                        sum += ControlSurface.Measure(cs, c, s.Com, 0f, _kT.AsSpan(o, ControlSurface.Knots), _kU.AsSpan(o, ControlSurface.Knots));
                        _a.Add(0.5f * (_kT[o + ControlSurface.Knots - 1] - _kT[o]));
                    }
                }
                ControlSurface.Allocate(_a, want - sum, _u);
                if (!legacyPath) ControlSurface.Refine(_kT, _kU, want - sum, _u, n);
                times[rep] = (sw.ElapsedTicks - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
                if (rep > 10) bytes += GC.GetAllocatedBytesForCurrentThread() - g0;
            }
            Array.Sort(times);
            Console.WriteLine($"   ALLOCATION COST (ssto, {n} surfaces, 0-{maxDeg:F0} deg) {(legacyPath ? "before (+-1/2 secant)" : "after (Measure + Allocate + Refine)")}: median {times[reps / 2]:F1} us, p99 {times[(int)(reps * 0.99)]:F1} us per grid per tick, {bytes / (double)(reps - 11):F0} B allocated per tick");
            if (!legacyPath) Check(bytes == 0, $"allocation after: {bytes} B allocated over {reps} ticks");
        }
    }
}
