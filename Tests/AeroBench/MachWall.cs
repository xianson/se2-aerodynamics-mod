using System.IO;
using AeroMod;

// MACH WALL (2026-10-17, the user's speed roles: "a lil more respeck on mach 1's name"; made reachable the same day, "fix
// this"): the vanilla atmospheric thrusters' Mach curve (ThrustProfiles.Jet - the transonic pinch, then the supersonic ram
// rise; applied in game by JetCurve) against the hull's wave drag (ForceTable: the transonic onset, ForceTable.HullWave /
// BlendHold by streamlining) - as the mod's own top speed takes them (DesignProbe.TopSpeed / ForcesAt: the force table and
// the wings along the travel). Level flight at full forward thrust (0 deg: the lift's induced drag left out, as the top
// speed). The sleek fighter: the F-6 "Diamondback" (bp/3737149863, 43 t, streamlining 1.0 nose-first); the brick: a solid
// 4:1 box of the same frontal area.
// THE REAL CHAIN at altitude: the game's AirData density -> the mod's air (AtmosphereBridge: the ISA temperature of that
// density - the speed of sound the Mach is taken against, AeroGridComponent.SoundSpeedOf) and the thrust = rating x the Jet
// curve x the game's own efficiency of the Atmospheric thrust class in that air (GetThrustEfficiency with
// ThrustClassesConfiguration.def: full from 0.8 kg/m3 down to nothing at 0.2 - a third at 0.4). "T/W" is always the STATIC
// SEA-LEVEL rating over the weight.
// The goals (the lead, 2026-10-17, "fix this"): the F-6 breaks Mach 1 in level flight at T/W <= 1.5 in air of 0.4 kg/m3
// and <= 2.5 at sea level; its bottleneck is the transonic band (the pinch keeps Mach 1 the hardest point); on that thrust
// it goes on to a supersonic cruise (>= Mach 1.4, ~1.6: past the crossing nothing asks more); the brick stalls subsonic
// (<= Mach 0.85) on the thrust that takes the F-6 through at sea level; below Mach 0.75 the drag is the old law's exactly
// (the subsonic calibrations). The before/after table compares today's laws with the ones before the fix.
// THE WINGS' OWN WAVE DRAG (2026-10-18, fix a-wing-wave): those goals are the HULL's wall and stay checked on it (the wing
// terms off: WingTermsOff); the whole model - the F-6's straight t/c 0.12-0.19 wings with their own wave drag - against its
// own goals (T/W <= 3.6 at sea level, <= 2.7 at 0.4 kg/m3; the bottleneck still the transonic band, then on to a
// supersonic cruise). The "below Mach 0.75" check compares the hull laws (the wing terms on in both); the Tu-134's drag
// at Mach 0.79 with and without the wing terms is printed (verbose).
static partial class Program
{
    const float SeaRho = 1.225f, HighRho = 0.4f, FullRho = 0.8f;
    /// <summary>The game's Atmospheric thrust class (ThrustClassesConfiguration.def): full thrust from MaxThrustAirDensity
    /// 0.8, none at MinThrustAirDensity 0.2 - GetThrustEfficiency's linear clamp between them.</summary>
    const float AtmoMaxThrustRho = 0.8f, AtmoMinThrustRho = 0.2f;
    static float AtmoEfficiency(float rho) => Math.Clamp((rho - AtmoMinThrustRho) / (AtmoMaxThrustRho - AtmoMinThrustRho), 0f, 1f);

    static AtmosphereBridge _wallAir;
    /// <summary>The mod's air at a density (AtmosphereBridge, as AeroSimJob builds it in game).</summary>
    static AtmosphereState WallAir(float rho) => (_wallAir ??= new AtmosphereBridge()).GetState(rho);

    /// <summary>A curve's factor (null: flat 1) as JetCurve.Factor takes it: 1 at rest, clamped to 0..JetCurve.MaxFactor.</summary>
    static float Cur(ThrustProfile c, float m)
    {
        if (c == null || !(m > 0f)) return 1f;
        float f = (float)c.Evaluate(m);
        return float.IsFinite(f) ? Math.Clamp(f, 0f, JetCurve.MaxFactor) : 1f;
    }

    /// <summary>The Jet curve before the fix (2026-10-17 morning: the 0.55 pinch, no ram rise) - the table's "before".</summary>
    static readonly ThrustProfile JetBefore = new("JetBefore", new (double, double)[]
        { (0.00, 1.00), (0.60, 0.95), (0.85, 0.85), (0.95, 0.65), (1.00, 0.55), (1.10, 0.60), (1.30, 0.80), (1.60, 0.90), (2.00, 0.60), (2.40, 0.25), (2.80, 0.00) },
        maxOperatingMach: 2.8);

    /// <summary>The drag along the travel (N) at a Mach in air of this density, as DesignProbe.ForcesAt: the table and the wings.</summary>
    static float WallDrag(Ship ship, ForceTable table, Vector3 travel, float mach, float rho = SeaRho)
    {
        var inner = (DampedShadowedDragModel)ship.Model.InnerModel;
        var atm = WallAir(rho);
        Vector3 v = Vector3.Normalize(travel) * MathF.Max(1f, mach * (float)atm.SpeedOfSound);
        var ctx = new AeroContext(null, null, v, atm, ship.Com);
        var r = table.Evaluate(ctx, inner.SubsonicLimit, inner.SupersonicLimit, inner.Streamlining);
        Vector3 f = r.Force;
        var wings = ship.Model.Wings;
        if (wings != null && wings.Count > 0)
        {
            var wm = new CompressibleWingModel();
            ship.Model.HandHullEdges(wm, ctx, table);   // (the wave-drag guard, as DesignProbe.ForcesAt - 2026-10-18)
            var wf = wm.ComputeWingForces(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(wings), ctx);
            if (wf != null) foreach (var w in wf) f += w.LiftForce + w.InducedDrag;
        }
        return -Vector3.Dot(f, Vector3.Normalize(travel));
    }

    /// <summary>The static sea-level rating (N) the hull needs on a curve (null: flat) in air of this density to get from Mach
    /// 0.5 up to 'to' in level flight: the most of drag / (curve(M) x the class's efficiency) on the way; and where (the
    /// bottleneck).</summary>
    static float NeededThrust(Ship ship, ForceTable table, Vector3 travel, ThrustProfile curve, float to, float rho, out float atMach)
    {
        float need = 0f, eff = AtmoEfficiency(rho); atMach = 0f;
        if (!(eff > 0f)) return float.PositiveInfinity;
        // (every 0.005: the top speed's own step - at 0.01 the true bottleneck could sit between two samples, 0.1 % above
        //  them, and the top speed on the 'needed' thrust then stopped right there; 2026-10-18 fix a-wing-wave, where the
        //  F-6's drag / thrust peaked at Mach 1.015)
        for (float m = 0.5f; m <= to + 1e-4f; m += 0.005f)
        {
            float k = Cur(curve, m) * eff, d = WallDrag(ship, table, travel, m, rho);
            if (k > 0f && d / k > need) { need = d / k; atMach = m; }
        }
        return need;
    }

    /// <summary>The level-flight top speed (Mach) from rest on a static rating t0 (N) on a curve in air of this density.</summary>
    static float WallTop(Ship ship, ForceTable table, Vector3 travel, float t0, ThrustProfile curve, float rho = SeaRho)
    {
        float eff = AtmoEfficiency(rho);
        for (float m = 0.05f; m <= 2.8f; m += 0.005f)
            if (t0 * eff * Cur(curve, m) <= WallDrag(ship, table, travel, m, rho)) return m;
        return 2.8f;
    }

    static float FixtureMass(string path)
    {
        foreach (var l in File.ReadLines(path).Take(3))
        {
            var mm = System.Text.RegularExpressions.Regex.Match(l, @"mass\s+([0-9.]+)");
            if (mm.Success) return float.Parse(mm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        return 0f;
    }

    /// <summary>Scan (by hand: dotnet run -- machscan [ship ...]): each fixture's streamlining, frontal area and the static
    /// thrust-to-weight it needs to break Mach 1 on the Jet curve at sea level and at 0.4 kg/m3, along +-Z and +-X.</summary>
    static int MachScan(string dataDir, string[] names)
    {
        foreach (var nm in names)
        {
            var path = Directory.GetFiles(dataDir, nm + ".boxes", SearchOption.AllDirectories).FirstOrDefault();
            if (path == null) { Console.WriteLine($"   {nm}: no fixture"); continue; }
            float mass = FixtureMass(path);
            var ship = Build(nm, LoadBoxes(path));
            var table = ((DampedShadowedDragModel)ship.Model.InnerModel).BuildForceTable(ship.Grid, ship.Surface, ship.Manifold, ship.Com, 8);
            foreach (var d in new[] { -Vector3.UnitZ, Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitX })
            {
                Span<float> e = stackalloc float[ForceTable.Stride]; table.Sample(d, e);
                float need = NeededThrust(ship, table, d, ThrustProfiles.Jet, 1.05f, SeaRho, out float at), high = NeededThrust(ship, table, d, ThrustProfiles.Jet, 1.05f, HighRho, out _);
                Console.WriteLine($"   {nm,-12} {mass / 1000f,6:F1} t  travel {d}: streamlining {table.StreamliningAlong(d, -1f):F2} frontal {e[12],6:F1} m2  D(M0.6) {WallDrag(ship, table, d, 0.6f) / 1e3f,7:F0} kN  D(M1) {WallDrag(ship, table, d, 1f) / 1e3f,7:F0} kN  -> static T/W {need / (mass * 9.81f),6:F2} to break Mach 1 at sea level (bottleneck M{at:F2}), {high / (mass * 9.81f),5:F2} at 0.4 kg/m3");
            }
        }
        return 0;
    }

    /// <summary>The old hull law (before 2026-10-17: peak 1.2x at Mach 1.05 for a needle, fall ^1.3, the full Newtonian blend) -
    /// on until the returned action runs (the comparisons: below Mach 0.75 nothing may move).</summary>
    static Action OldHullLaw()
    {
        float a = ForceTable.WavePeakNeedle, b = ForceTable.WavePeakMachNeedle, c = ForceTable.WaveFallNeedle, d = ForceTable.SleekHold;
        ForceTable.WavePeakNeedle = 1.2f; ForceTable.WavePeakMachNeedle = 1.05f; ForceTable.WaveFallNeedle = 1.3f; ForceTable.SleekHold = 0f;
        return () => { ForceTable.WavePeakNeedle = a; ForceTable.WavePeakMachNeedle = b; ForceTable.WaveFallNeedle = c; ForceTable.SleekHold = d; };
    }

    /// <summary>The sleek hull's wall before the fix (2026-10-17 morning: its peak 1.45x at Mach 1) - on until the returned
    /// action runs (the table's "before", with JetBefore).</summary>
    static Action WallBeforeFix()
    {
        float a = ForceTable.WavePeakNeedle;
        ForceTable.WavePeakNeedle = 1.45f;
        return () => ForceTable.WavePeakNeedle = a;
    }

    /// <summary>The wings' own wave drag and supersonic drag due to lift off (fda38c9's wing model) until the returned action
    /// runs (2026-10-18, fix a-wing-wave: the hull wall's goals are checked on it).</summary>
    static Action WingTermsOff()
    {
        bool a = CompressibleWingModel.WingWaveDrag, b = CompressibleWingModel.SupersonicLiftDrag;
        CompressibleWingModel.WingWaveDrag = false; CompressibleWingModel.SupersonicLiftDrag = false;
        return () => { CompressibleWingModel.WingWaveDrag = a; CompressibleWingModel.SupersonicLiftDrag = b; };
    }

    /// <summary>One law's figures for the F-6 and the brick (the table's columns).</summary>
    struct WallFigures
    {
        public float SeaTw, SeaAt, HighTw, HighAt, FullTw, SeaTop, HighTop, Top15High, Top25Sea, BrickStall, BrickStallHigh, Post, PostAt;
    }

    static WallFigures WallMeasure(Ship f, ForceTable ft, Vector3 travel, float w, Ship b, ForceTable bt, ThrustProfile curve)
    {
        var r = new WallFigures();
        float sea = NeededThrust(f, ft, travel, curve, 1.05f, SeaRho, out r.SeaAt), high = NeededThrust(f, ft, travel, curve, 1.05f, HighRho, out r.HighAt);
        r.SeaTw = sea / w; r.HighTw = high / w; r.FullTw = NeededThrust(f, ft, travel, curve, 1.05f, FullRho, out _) / w;
        r.SeaTop = WallTop(f, ft, travel, sea * 1.001f, curve, SeaRho);
        r.HighTop = WallTop(f, ft, travel, high * 1.001f, curve, HighRho);
        r.Top15High = WallTop(f, ft, travel, 1.5f * w, curve, HighRho);
        r.Top25Sea = WallTop(f, ft, travel, 2.5f * w, curve, SeaRho);
        r.BrickStall = WallTop(b, bt, -Vector3.UnitZ, sea, curve, SeaRho);
        r.BrickStallHigh = WallTop(b, bt, -Vector3.UnitZ, high, curve, HighRho);
        // the most drag / curve past the crossing, up to Mach 1.4 (sea level): the cruise's ask against the crossing's
        for (float m = 1.055f; m <= 1.4001f; m += 0.005f) { float k = Cur(curve, m), d = WallDrag(f, ft, travel, m); if (k > 0f && d / k > r.Post) { r.Post = d / k; r.PostAt = m; } }
        r.Post /= w;
        return r;
    }

    /// <summary>The gate's check (and the tuning report with verbose): PASS/FAIL on the goals above, and the before/after
    /// table (today's laws against the ones before the fix: the 0.55 pinch with no ram rise, the 1.45x wall).</summary>
    static int MachWall(string dataDir, bool verbose)
    {
        int fails = 0;
        var path = Directory.GetFiles(dataDir, "3737149863.boxes", SearchOption.AllDirectories).FirstOrDefault();
        if (path == null) { Console.WriteLine("   machwall: no F-6 fixture - skipped"); return 0; }
        float mass = FixtureMass(path), w = mass * 9.81f;
        BlockSize = 0.5f;
        var fighter = Build("F-6 Diamondback", LoadBoxes(path));
        var ftable = ((DampedShadowedDragModel)fighter.Model.InnerModel).BuildForceTable(fighter.Grid, fighter.Surface, fighter.Manifold, fighter.Com, 8);
        Vector3 travel = -Vector3.UnitZ;   // (nose first: the lower drag of the two ends along its length - machscan)
        float st = ftable.StreamliningAlong(travel, -1f);
        Span<float> e = stackalloc float[ForceTable.Stride]; ftable.Sample(travel, e); float fa = e[12];
        int side = Math.Max(4, (int)MathF.Round(MathF.Sqrt(fa) / 0.25f));
        var keepThr = LastThrusters; LastThrusters = new();
        var brick = Build("brick", new List<(Vector3I, Vector3I)> { (Vector3I.Zero, new Vector3I(side - 1, side - 1, 4 * side - 1)) });
        LastThrusters = keepThr;
        var btable = ((DampedShadowedDragModel)brick.Model.InnerModel).BuildForceTable(brick.Grid, brick.Surface, brick.Manifold, brick.Com, 8);
        float bst = btable.StreamliningAlong(-Vector3.UnitZ, -1f);

        var now = WallMeasure(fighter, ftable, travel, w, brick, btable, ThrustProfiles.Jet);
        WallFigures before, hull;
        { var restore = WallBeforeFix(); before = WallMeasure(fighter, ftable, travel, w, brick, btable, JetBefore); restore(); }
        // the hull's wall alone (the wings' own wave drag and supersonic drag due to lift off - fda38c9's wing model): the
        // 2026-10-17 calibration's goals are the hull law's, and stay checked on it
        { var restore = WingTermsOff(); hull = WallMeasure(fighter, ftable, travel, w, brick, btable, ThrustProfiles.Jet); restore(); }

        // the Tu-134 (its L/D calibration at Mach 0.79): nose = the lower-drag end along the axes
        var tuPath = Directory.GetFiles(dataDir, "tu134_game.boxes", SearchOption.AllDirectories).FirstOrDefault();
        Ship tu = null; ForceTable tutable = null; Vector3 tuNose = Vector3.UnitZ;
        if (tuPath != null)
        {
            tu = Build("Tu-134", LoadBoxes(tuPath));
            tutable = ((DampedShadowedDragModel)tu.Model.InnerModel).BuildForceTable(tu.Grid, tu.Surface, tu.Manifold, tu.Com, 8);
            float best = float.MaxValue;
            foreach (var d in new[] { Vector3.UnitZ, -Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitX }) { float dd = WallDrag(tu, tutable, d, 0.5f); if (dd < best) { best = dd; tuNose = d; } }
        }
        var machs = new[] { 0.3f, 0.5f, 0.6f, 0.7f, 0.75f };
        void Sweep(List<float> into)
        {
            foreach (float rho in new[] { SeaRho, HighRho })
                foreach (float m in machs) { into.Add(WallDrag(fighter, ftable, travel, m, rho)); into.Add(WallDrag(brick, btable, -Vector3.UnitZ, m, rho)); if (tu != null) into.Add(WallDrag(tu, tutable, tuNose, m, rho)); }
            if (tu != null) into.Add(WallDrag(tu, tutable, tuNose, 0.79f));
            for (int i = 0; i <= 10; i++) foreach (float m in new[] { 0.5f, 0.7f, 0.75f }) into.Add(ForceTable.HullWave(m, i / 10f) * (1f - ForceTable.BlendHold(m, i / 10f)));
        }
        var old = new List<float>();
        { var restore = OldHullLaw(); Sweep(old); restore(); }
        var after = new List<float>(); Sweep(after);
        float worstSub = 0f; for (int i = 0; i < old.Count; i++) worstSub = MathF.Max(worstSub, MathF.Abs(after[i] - old[i]) / MathF.Max(1e-3f, MathF.Abs(old[i])));

        bool InBand(float m) => m >= 0.9f && m <= 1.06f;
        // the hull's wall (the wings' own terms off): the 2026-10-17 goals, unchanged
        bool altOk = hull.HighTw <= 1.5f && InBand(hull.HighAt) && hull.HighTop >= 1.4f && hull.Top15High >= 1.4f;
        bool seaOk = hull.SeaTw <= 2.5f && InBand(hull.SeaAt) && hull.SeaTop >= 1.4f && hull.Post <= hull.SeaTw && st > 0.8f;
        bool bOk = now.BrickStall <= 0.85f && now.BrickStallHigh <= 0.85f && bst < 0.2f && hull.BrickStall <= 0.85f;
        bool sOk = worstSub < 1e-4f;
        // THE WHOLE MODEL (2026-10-18, fix a-wing-wave): the F-6's wings are straight and, by their own columns, t/c
        // 0.12-0.19 - with their own wave drag (CompressibleWingModel.WingWaveDrag) Mach 1 costs more: its goals move from
        // T/W 2.5 / 1.5 to 3.6 / 2.7 (measured 3.36 / 2.53); the rest kept - the bottleneck in the transonic band, on the
        // crossing thrust on to a supersonic cruise (>= 1.4), past the crossing nothing asks more
        bool wingOk = now.SeaTw <= 3.6f && now.HighTw <= 2.7f && InBand(now.SeaAt) && InBand(now.HighAt) && now.SeaTop >= 1.4f && now.HighTop >= 1.4f
            && now.Post <= now.SeaTw && now.SeaTw > hull.SeaTw;
        if (!altOk) fails++; if (!seaOk) fails++; if (!bOk) fails++; if (!sOk) fails++; if (!wingOk) fails++;
        Console.WriteLine($"   {(altOk ? "ok  " : "FAIL")} machwall (the hull's wall - wing wave drag off): F-6 ({mass / 1000f:F1} t, streamlining {st:F2}, frontal {fa:F1} m2) at 0.4 kg/m3 (a = {WallAir(HighRho).SpeedOfSound:F0} m/s, atmospheric thrust x{AtmoEfficiency(HighRho):F2}) breaks Mach 1 in level flight at static T/W {hull.HighTw:F2} (<= 1.5; bottleneck M{hull.HighAt:F2}), then on to Mach {hull.HighTop:F2}; on T/W 1.5 there: Mach {hull.Top15High:F2}");
        Console.WriteLine($"   {(seaOk ? "ok  " : "FAIL")} machwall (the hull's wall - wing wave drag off): at sea level it breaks Mach 1 at T/W {hull.SeaTw:F2} (<= 2.5; bottleneck M{hull.SeaAt:F2}; pinch {ThrustProfiles.JetPinch:F2}, ram peak {ThrustProfiles.JetRamPeak:F2}), then on to Mach {hull.SeaTop:F2} (past the crossing up to 1.4 it asks T/W {hull.Post:F2} at most, at M{hull.PostAt:F2})");
        Console.WriteLine($"   {(wingOk ? "ok  " : "FAIL")} machwall (the whole model - the wings' own wave drag on): the F-6 breaks Mach 1 at T/W {now.SeaTw:F2} at sea level (<= 3.6; bottleneck M{now.SeaAt:F2}, then on to Mach {now.SeaTop:F2}; past the crossing it asks T/W {now.Post:F2} at most, at M{now.PostAt:F2}), {now.HighTw:F2} at 0.4 kg/m3 (<= 2.7; bottleneck M{now.HighAt:F2}, then on to Mach {now.HighTop:F2})");
        Console.WriteLine($"   {(bOk ? "ok  " : "FAIL")} machwall: a brick of the same frontal area ({side * 0.25f:F2} m square, 4:1, streamlining {bst:F2}) on the F-6's crossing thrust stalls at Mach {now.BrickStall:F2} at sea level, {now.BrickStallHigh:F2} at 0.4 kg/m3 ({hull.BrickStall:F2} on the hull wall's)");
        Console.WriteLine($"   {(sOk ? "ok  " : "FAIL")} machwall: below Mach 0.75 the drag is the old law's (F-6, brick{(tu != null ? ", Tu-134 to Mach 0.79" : "")}, at 1.225 and 0.4 kg/m3, the law at every streamlining: worst {worstSub * 100f:F4}%)");
        if (verbose || !(altOk && seaOk && bOk && wingOk))
        {
            Console.WriteLine("      the F-6 (static sea-level T/W)              10-17  hull wall  now (+ wing wave drag)");
            void Row(string what, float a, float h, float b, string fmt = "F2") => Console.WriteLine($"      {what,-44}{a.ToString(fmt),7}  {h.ToString(fmt),8}  {b.ToString(fmt),8}");
            Row("T/W to break Mach 1, sea level", before.SeaTw, hull.SeaTw, now.SeaTw);
            Row("  its bottleneck Mach", before.SeaAt, hull.SeaAt, now.SeaAt);
            Row("  top level-flight Mach on it", before.SeaTop, hull.SeaTop, now.SeaTop);
            Row("T/W to break Mach 1, 0.8 kg/m3", before.FullTw, hull.FullTw, now.FullTw);
            Row("T/W to break Mach 1, 0.4 kg/m3", before.HighTw, hull.HighTw, now.HighTw);
            Row("  its bottleneck Mach", before.HighAt, hull.HighAt, now.HighAt);
            Row("  top level-flight Mach on it", before.HighTop, hull.HighTop, now.HighTop);
            Row("top Mach on T/W 1.5 at 0.4 kg/m3", before.Top15High, hull.Top15High, now.Top15High);
            Row("top Mach on T/W 2.5 at sea level", before.Top25Sea, hull.Top25Sea, now.Top25Sea);
            Row("brick stall Mach (sea-level crossing thrust)", before.BrickStall, hull.BrickStall, now.BrickStall);
            if (tu != null)
            {
                // (the subsonic calibration the wing terms DO move: the Tu-134's straight-ish t/c 0.16 wing at Mach 0.79)
                float tuNow = WallDrag(tu, tutable, tuNose, 0.79f); var rw = WingTermsOff(); float tuHull = WallDrag(tu, tutable, tuNose, 0.79f); rw();
                Console.WriteLine($"      Tu-134 drag at Mach 0.79 (sea level, 0 deg): {tuHull / 1e3f:F0} kN on the hull wall's wings, {tuNow / 1e3f:F0} kN with their own wave drag ({tuNow / tuHull:F2}x)");
            }
        }
        if (verbose)
        {
            Console.WriteLine("      the pinch depth (the Jet curve at Mach 1) against the F-6's static T/W to break Mach 1 (to 1.05):");
            Console.WriteLine("      pinch   sea level 1.225   0.4 kg/m3");
            foreach (double p in new[] { 0.55, 0.65, ThrustProfiles.JetPinch, 0.85, 1.00 })
            {
                var c = ThrustProfiles.JetWithPinch(p);
                float a = NeededThrust(fighter, ftable, travel, c, 1.05f, SeaRho, out float am), b = NeededThrust(fighter, ftable, travel, c, 1.05f, HighRho, out float bm);
                Console.WriteLine($"      {p,5:F2}   {a / w,10:F2} (M{am:F2})   {b / w,6:F2} (M{bm:F2})");
            }
            float flat = NeededThrust(fighter, ftable, travel, null, 1.05f, SeaRho, out float fm);
            Console.WriteLine($"      no curve at all (flat 1): T/W {flat / w:F2} (M{fm:F2}) at sea level");
            float q06 = 0.5f * SeaRho * MathF.Pow(0.6f * (float)WallAir(SeaRho).SpeedOfSound, 2f), cda = WallDrag(fighter, ftable, travel, 0.6f) / q06;
            float t0 = now.SeaTw * w;
            float qb06 = 0.5f * SeaRho * MathF.Pow(0.6f * (float)WallAir(SeaRho).SpeedOfSound, 2f), bcda = WallDrag(brick, btable, -Vector3.UnitZ, 0.6f) / qb06;
            var mlist = new[] { 0.6f, 0.8f, 0.85f, 0.9f, 0.95f, 1.0f, 1.05f, 1.1f, 1.2f, 1.3f, 1.4f, 1.5f, 1.6f, 1.7f, 1.8f, 1.9f, 2.0f, 2.2f, 2.4f, 3.0f };
            var hullD = new Dictionary<float, float>();
            { var rw = WingTermsOff(); foreach (float m in mlist) hullD[m] = WallDrag(fighter, ftable, travel, m); rw(); }
            Console.WriteLine($"      Mach   Jet   before  F-6 drag kN (x subsonic area {cda:F1} m2)  wing terms off   thrust kN (sea crossing)   brick drag kN (x {bcda:F1} m2)");
            foreach (float m in mlist)
            {
                float d = WallDrag(fighter, ftable, travel, m), q = 0.5f * SeaRho * MathF.Pow(m * (float)WallAir(SeaRho).SpeedOfSound, 2f), bd = WallDrag(brick, btable, -Vector3.UnitZ, m);
                Console.WriteLine($"      {m,4:F2}  {JetCurve.Factor(m),5:F2}  {Cur(JetBefore, m),5:F2}  {d / 1e3f,11:F0} ({d / q / cda,4:F2}x)  {hullD[m] / 1e3f,9:F0} ({hullD[m] / q / cda,4:F2}x)  {t0 * JetCurve.Factor(m) / 1e3f,18:F0}  {bd / 1e3f,13:F0} ({bd / q / bcda,4:F2}x)");
            }
        }
        return fails;
    }
}
