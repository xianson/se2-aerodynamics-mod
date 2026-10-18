#pragma warning disable
using System.IO;
using System.Text.Json;
using AeroMod;

/// <summary>SolidRocketTests: the solid rocket motors offline (exit 0 = all pass). The CONVEYOR version (2026-10-17): the propellant is the
/// game's resource in the segments' tanks - the game's draw is modelled here (GameTick: its all-or-nothing sink, its tanks giving in
/// proportion to what each can give this tick), the mod's part is the literal core (SolidRocket.cs, DutyCycle.cs).</summary>
static class Program
{
    static int _checks, _fails;
    static void Check(bool ok, string what) { _checks++; if (!ok) { _fails++; Console.WriteLine("FAIL " + what); } else if (Environment.GetEnvironmentVariable("VERBOSE") != null) Console.WriteLine("ok   " + what); }
    const float Dt = 1f / 60f;

    /// <summary>A segment for the core (the game's AeroSolidSegmentComponent's stand-in): its tank's charge P, a full tank F, working W.</summary>
    sealed class Seg : SolidRocket.IPiece
    {
        public SolidRocket.Geo G; public float P, F = 100f; public bool W = true;
        public SolidRocket.Geo Geo => G;
        public float Propellant => P;
        public float Full => F;
        public bool Feeds => W;
        public override string ToString() => $"seg {G.Min}..{G.Max}";
    }

    // (a 0.5 m motor: a nozzle 2 x 6 x 2 cells, segments 2 x 4 x 2; Up = +Y unless turned)
    static SolidRocket.Geo Box(int x, int y, int z, int sx, int sy, int sz, Vector3 up, string size = "050")
        => SolidRocket.Geo.Of(new Vector3I(x, y, z), new Vector3I(x + sx - 1, y + sy - 1, z + sz - 1), up, size);
    static Seg S(int x, int y, int z, Vector3 up, string size = "050", float p = 100f, int sx = 2, int sy = 4, int sz = 2)
        => new Seg { G = Box(x, y, z, sx, sy, sz, up, size), P = p };
    static Seg Full(float full) => new Seg { P = full, F = full };

    static SolidRocketBlocks.Row RowOf(string name) => SolidRocketBlocks.Rows.First(r => r.Name == name);

    /// <summary>THE GAME's draw on one tick (a model of ResourceDistributorComponent / ResourceFlowDistribution for one conveyor group
    /// and ResourceContainerComponent's limits, decompiled 2.4): each tank can give min(its charge / dt, MaxDischargeRate) this tick; a
    /// nozzle's sink is ALL-OR-NOTHING (UnitTyped.MakeSink): fed only when the tanks can give its whole demand, then every tank gives
    /// in proportion to what it can give (adaptive partial saturation). Returns whether the demand was met (thrust) - and drains.</summary>
    static bool GameTick(List<Seg> tanks, float demand, float discharge)
    {
        if (!(demand > 0f)) return Total(tanks) > 0f;   // (a zero demand: fed while there is any - over-saturated, ReportZeroFlow)
        double caps = 0;
        foreach (var t in tanks) caps += Math.Min(t.P / Dt, discharge);
        if (caps + 1e-6 < demand) return false;
        foreach (var t in tanks) { double give = Math.Min(t.P / Dt, discharge) / caps * demand * Dt; t.P = (float)Math.Max(0.0, t.P - give); }
        return true;
    }
    /// <summary>Two nozzles' sinks on one group in one tick: all-or-nothing each, saturated in order (ResourceFlowDistribution.
    /// TrySaturateAllOrNothing) - returns how many were fed.</summary>
    static int GameTick2(List<Seg> tanks, float demandA, float demandB, float discharge)
    {
        int fed = 0;
        if (demandA + demandB > 0f && GameTick(tanks, demandA + demandB, discharge)) return (demandA > 0f ? 1 : 0) + (demandB > 0f ? 1 : 0);
        if (demandA > 0f && GameTick(tanks, demandA, discharge)) fed++;
        else if (demandB > 0f && GameTick(tanks, demandB, discharge)) fed++;
        return fed;
    }
    static float Total(List<Seg> st) => SolidRocket.Total(st);

    static int Main()
    {
        Stack();
        Ports();
        GameDraw();
        Saved();
        Ignition();
        BurnTime();
        Thrust();
        Allocator();
        PowerLevel();
        LevelBurn();
        LevelSaved();
        Steering();
        AirSource();
        Look();
        Content();
        Console.WriteLine($"SolidRocketTests: {_checks - _fails}/{_checks} passed");
        return _fails == 0 ? 0 : 1;
    }

    // ── 1. the stack: a valid chain; a wrong-size segment breaks it; a side-attached one is inert; removal recomputes ──
    static void Stack()
    {
        var up = Vector3.UnitY;
        var noz = Box(0, 0, 0, 2, 6, 2, up);                    // cells y 0..5: its top face at y 6
        var a = S(0, 6, 0, up); var b = S(0, 10, 0, -up);        // (b upside down: a segment is the same both ways)
        var c = S(0, 14, 0, up);
        var side = S(2, 6, 0, up);                               // beside the first segment, touching its side
        var offset = S(1, 18, 0, up);                            // on top of c, half a footprint across
        var cands = new List<Seg> { c, side, a, offset, b };
        var stack = new List<Seg>();
        Check(SolidRocket.FindStack(in noz, cands, stack) == 3 && stack[0] == a && stack[1] == b && stack[2] == c, $"a straight chain of three on the nozzle's top face, nearest first ({string.Join(", ", stack)})");
        Check(!stack.Contains(side), "a segment attached at the side is not in the motor (inert)");
        Check(!stack.Contains(offset), "a segment offset across the axis is not in the motor");
        // a wrong-size segment in the middle breaks the chain there
        var big = new Seg { G = Box(0, 10, 0, 2, 4, 2, up, "150"), P = 100f };
        var c2 = new List<Seg> { a, big, c };
        Check(SolidRocket.FindStack(in noz, c2, stack) == 1 && stack[0] == a, "a wrong-size segment breaks the chain (only the one below it counts)");
        // removal recomputes: b gone - c is after a gap
        var c3 = new List<Seg> { a, c };
        Check(SolidRocket.FindStack(in noz, c3, stack) == 1 && stack[0] == a, "a segment removed: the chain stops at the gap");
        cands.Remove(a);
        Check(SolidRocket.FindStack(in noz, cands, stack) == 0, "the first segment removed: no motor");
        // a turned segment (its axis across) does not count, even boxed the same way
        var turned = new Seg { G = Box(0, 6, 0, 2, 4, 2, Vector3.UnitX), P = 100f };
        Check(SolidRocket.FindStack(in noz, new List<Seg> { turned }, stack) == 0, "a segment turned on its side does not count");
        // a nozzle pointing down (Up = -Y): its stack goes down
        var down = Box(0, 0, 0, 2, 6, 2, -up);
        var d1 = S(0, -4, 0, up); var d2 = S(0, -8, 0, up); var wrongSide = S(0, 6, 0, up);
        Check(SolidRocket.FindStack(in down, new List<Seg> { wrongSide, d2, d1 }, stack) == 2 && stack[0] == d1 && stack[1] == d2, "a nozzle turned over: its motor is along ITS Up");
        // along X, and a 7.5 m motor (30 x 50 x 30 segments on a 30 x 70 x 30 nozzle)
        var nx = Box(0, 0, 0, 70, 30, 30, Vector3.UnitX, "750");
        var x1 = S(70, 0, 0, -Vector3.UnitX, "750", 1f, 50, 30, 30);
        Check(SolidRocket.FindStack(in nx, new List<Seg> { x1 }, stack) == 1, "a 7.5 m motor along X");
        // a nozzle of another size finds nothing
        var n150 = Box(0, 0, 0, 2, 6, 2, up, "150");
        Check(SolidRocket.FindStack(in n150, new List<Seg> { a, b, c }, stack) == 0, "a nozzle only takes segments of its size");
        // two nozzles facing each other share the chain between them (each sees it all)
        var top = Box(0, 18, 0, 2, 6, 2, -up);
        Check(SolidRocket.FindStack(in top, new List<Seg> { S(0, 6, 0, up), S(0, 10, 0, up), S(0, 14, 0, up) }, stack) == 3 && stack[0].G.Min.Y == 14, "a nozzle at each end: both see the chain (from their own end)");
        // a loop can not hang it (duplicated candidates)
        Check(SolidRocket.FindStack(in noz, new List<Seg> { a, a, a }, stack) == 1, "the same segment twice: counted once");
        // a segment that is not working (unbuilt, damaged) breaks the chain there: the game drops its tank and its conveyor link
        var broken = S(0, 10, 0, up); broken.W = false;
        Check(SolidRocket.FindStack(in noz, new List<Seg> { S(0, 6, 0, up), broken, S(0, 14, 0, up) }, stack) == 1, "a broken segment in the chain: the stack stops below it (as the game's conveyor link)");
        // the nozzle at the far end of the stack (one grain: they share the factor) - only facing back, the same size, on the last segment
        var s1 = S(0, 6, 0, up); var s2 = S(0, 10, 0, up);
        SolidRocket.FindStack(in noz, new List<Seg> { s1, s2 }, stack);
        var lastG = stack[stack.Count - 1].G;
        Check(SolidRocket.OtherEnd(in noz, in lastG, Box(0, 14, 0, 2, 6, 2, -up)), "a nozzle on the far end, facing back: the other end of the grain");
        Check(!SolidRocket.OtherEnd(in noz, in lastG, Box(0, 14, 0, 2, 6, 2, up)) && !SolidRocket.OtherEnd(in noz, in lastG, Box(0, 15, 0, 2, 6, 2, -up)) && !SolidRocket.OtherEnd(in noz, in lastG, Box(0, 14, 0, 2, 6, 2, -up, "150")) && !SolidRocket.OtherEnd(in noz, in lastG, Box(1, 14, 0, 2, 6, 2, -up)),
              "not the other end: facing away, a gap, another size, offset");
        var downN = Box(0, 0, 0, 2, 6, 2, -up); var e1 = S(0, -4, 0, up);
        SolidRocket.FindStack(in downN, new List<Seg> { e1 }, stack);
        var lastD = stack[0].G;
        Check(SolidRocket.OtherEnd(in downN, in lastD, Box(0, -10, 0, 2, 6, 2, up)), "a downward stack: its other end below, facing up");
    }

    // ── 1b. the ports: the game's link rule (ConveyorElementTranslator.GetConveyorPath) and the solid blocks' ports under it ──
    static void Ports()
    {
        var a = new Vector3(0.125f, 0.87f, 0.125f);
        Check(SolidRocket.PortsLink(a, new Vector3(0.27f, 0.27f, 0.05f), a + new Vector3(0, 0.01f, 0), new Vector3(0.27f, 0.27f, 0.5f)), "the rule: two ports 1 cm apart, the same X / Y scale - linked (the depth Z is ignored)");
        Check(SolidRocket.PortsLink(a, Vector3.One, a + new Vector3(0.06f, 0, 0), Vector3.One) && !SolidRocket.PortsLink(a, Vector3.One, a + new Vector3(0.07f, 0, 0), Vector3.One), "the rule: within 0.0625 m links, 0.07 m does not");
        Check(SolidRocket.PortsLink(a, new Vector3(0.5f, 0.5f, 1), a, new Vector3(0.50009f, 0.5f, 1)) && !SolidRocket.PortsLink(a, new Vector3(0.5f, 0.5f, 1), a, new Vector3(0.5002f, 0.5f, 1)), "the rule: scales within 1e-4 link, 2e-4 apart do not");
        // the table: one port scale per size, none a vanilla port's (so never a vanilla conveyor)
        var scales = SolidRocketBlocks.Rows.GroupBy(r => r.Size).ToDictionary(g => g.Key, g => g.Select(r => r.PortScale).Distinct().ToList());
        Check(scales.All(kv => kv.Value.Count == 1), "the table: a nozzle and a segment of one size share one port scale");
        var per = scales.Values.Select(v => v[0]).ToList();
        Check(per.Distinct().Count() == per.Count, $"the table: every size its own port scale ({string.Join(", ", scales.Select(kv => kv.Key + " " + kv.Value[0]))}) - the 2.5 m and 7.5 m on one grid never link");
        Check(per.All(p => SolidRocketBlocks.VanillaPortScales.All(v => MathF.Abs(p - v) > 10f * SolidRocket.PortScaleTolerance)), $"no solid port scale is a vanilla one ({string.Join(", ", SolidRocketBlocks.VanillaPortScales)})");
        // the generated dummies, placed as the game places stacked blocks (block centres along Y; every model has the same +0.125 shift)
        string root = BlocksRoot();
        if (root == null) { Console.WriteLine("   (no Blocks project: the placed-port checks skipped)"); return; }
        foreach (var size in new[] { "050", "150", "250", "750" })
        {
            var noz = RowOf("SolidNozzle_" + size); var seg = RowOf("SolidSegment_" + size);
            var nTop = Port(root, noz.Name, "SolidPortTop"); var sTop = Port(root, seg.Name, "SolidPortTop"); var sBot = Port(root, seg.Name, "SolidPortBottom");
            float rise = (noz.Box.Y + seg.Box.Y) / 2f, rise2 = seg.Box.Y;   // (the segment's centre over the nozzle's; the next segment's over it)
            Check(SolidRocket.PortsLink(nTop.pos, nTop.scale, sBot.pos + new Vector3(0, rise, 0), sBot.scale), $"{size}: a segment on the nozzle - its bottom port meets the nozzle's top port");
            Check(SolidRocket.PortsLink(sTop.pos + new Vector3(0, rise, 0), sTop.scale, sBot.pos + new Vector3(0, rise + rise2, 0), sBot.scale), $"{size}: segment on segment - linked");
            // upside down (turned 180 deg about Forward: y -> -y about the model centre 0.125): its ports swap and still meet
            Vector3 Flip(Vector3 p) => new Vector3(0.25f - p.X, 0.25f - p.Y, p.Z);
            Check(SolidRocket.PortsLink(nTop.pos, nTop.scale, Flip(sTop.pos) + new Vector3(0, rise, 0), sTop.scale), $"{size}: a segment upside down - linked (a segment is the same both ways)");
            // beside, not on: a segment one footprint across never meets the port
            Check(!SolidRocket.PortsLink(nTop.pos, nTop.scale, sBot.pos + new Vector3(seg.Box.X, rise, 0), sBot.scale), $"{size}: a segment beside the axis - no link");
            // a vanilla conveyor's port of the nearest vanilla scale exactly at the nozzle's port: no link
            foreach (var v in SolidRocketBlocks.VanillaPortScales)
                if (SolidRocket.PortsLink(nTop.pos, nTop.scale, nTop.pos, new Vector3(v, v, 0.05f))) Check(false, $"{size}: links to a vanilla {v} m port");
        }
        // the 2.5 m and the 7.5 m sit on the same 2.5 m grid: a 7.5 m segment centred on a 2.5 m nozzle puts its port on the nozzle's - the scale keeps them apart
        var n25 = Port(root, "SolidNozzle_250", "SolidPortTop"); var s75 = Port(root, "SolidSegment_750", "SolidPortBottom");
        float r = (RowOf("SolidNozzle_250").Box.Y + RowOf("SolidSegment_750").Box.Y) / 2f;
        Check(Vector3.Distance(n25.pos, s75.pos + new Vector3(0, r, 0)) < SolidRocket.PortReach && !SolidRocket.PortsLink(n25.pos, n25.scale, s75.pos + new Vector3(0, r, 0), s75.scale),
              "a 7.5 m segment on a 2.5 m nozzle: the ports touch, the scales differ - no link (the sizes never mix)");
    }

    static (Vector3 pos, Vector3 scale, float rotX, string type) Port(string root, string block, string name)
    {
        var bmd = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, block, $"{block}_BlockModelDefinition.partialdef"))).RootElement
            .GetProperty("$Value").GetProperty("Manipulator").GetProperty("Dummies").GetProperty("Changed");
        foreach (var c in bmd.EnumerateArray())
        {
            var v = c.GetProperty("Value");
            if (v.GetProperty("Name").GetString() != name) continue;
            var t = v.GetProperty("Transform").GetProperty("Transform");
            Vector3 V3(JsonElement e) => new Vector3(e.GetProperty("X").GetSingle(), e.GetProperty("Y").GetSingle(), e.GetProperty("Z").GetSingle());
            var o = t.GetProperty("Orientation");
            return (V3(t.GetProperty("Position")), V3(v.GetProperty("Scale")), o.GetProperty("X").GetSingle(), v.GetProperty("Type").GetString());
        }
        return (new Vector3(float.NaN), Vector3.Zero, 0f, null);
    }

    // ── 2. the game's draw and the core's numbers for it: the rate, the tanks' limit, sqrt(n) from the live tanks, burnout ──
    static void GameDraw()
    {
        var row = RowOf("SolidNozzle_050");
        float R = SolidRocket.GameRate(row.Thrust), full = SolidRocket.FullSegment(row.Thrust);
        Check(MathF.Abs(R - SolidRocket.MassFlow(row.Thrust) * 3f) < 1e-3f, $"the nozzle's ResourcesRequiredToThrust: one segment's mass flow x 3 ({R:F2} L/s)");
        Check(SolidRocket.TankDischargeRate(row.Thrust) == 2f * R, "a tank gives up to two nozzles' rate (a lone tank between two nozzles feeds both in one tick)");
        var one = new List<Seg> { Full(full) };
        Check(GameTick(one, 2f * R, SolidRocket.TankDischargeRate(row.Thrust)), "(model) one full tank under two firing nozzles: fed");
        // sqrt(n) from the tanks still holding propellant; an empty one in the chain does not count
        var st = new List<Seg> { Full(full), Full(full), Full(full), Full(full) };
        Check(SolidRocket.Live(st) == 4 && SolidRocket.ShareFactor(SolidRocket.Live(st), 1) == 2f, "four full tanks: factor 2 (sqrt 4)");
        st[1].P = 0f; st[3].P = 0f;
        Check(SolidRocket.Live(st) == 2 && MathF.Abs(SolidRocket.ShareFactor(2, 1) - MathF.Sqrt(2f)) < 1e-6f, "two empty: two live tanks, factor sqrt 2");
        st[3].P = 1e-4f * full;
        Check(SolidRocket.Live(st) == 3, "a crumb counts (the game draws from any tank with charge - a stack can never be usable with no live tank)");
        Check(SolidRocket.ShareFactor(9, 1) == 3f && SolidRocket.ShareFactor(16, 1) == 3f && SolidRocket.ShareFactor(4, 2) == 1f && SolidRocket.ShareFactor(4, 0) == 2f && SolidRocket.ShareFactor(0, 1) == 0f,
              "the factor: capped at 3; two lit nozzles on one stack share it (4 tanks: 1 each); none live: 0");
        Check(MathF.Abs(SolidRocket.BurnDuty(3f, 1f) - 1f) < 1e-6f && MathF.Abs(SolidRocket.BurnDuty(1f, 1f) - 1f / 3f) < 1e-6f && MathF.Abs(SolidRocket.BurnDuty(2f, 0.5f) - 1f / 3f) < 1e-6f
              && SolidRocket.BurnDuty(2f, 0.1f) == SolidRocket.BurnDuty(2f, 0.5f) && SolidRocket.BurnDuty(float.NaN, 1f) == 0f && SolidRocket.BurnDuty(5f, 1f) == 1f,
              "the duty: factor x share / 3 (a full-throat stack at full power: every tick), the share floored at 0.5, clamped 0..1");
        // the duty cycle's average is the duty, at most two switches a window
        foreach (float d in new[] { 0.05f, 1f / 3f, 0.5f, 0.71f, 0.97f })
        {
            var s = new DutyCycle.State(); int on = 0, sw = 0; bool last = false; const int N = 1200;
            for (int i = 0; i < N; i++) { bool b = DutyCycle.Step(ref s, d, SolidRocket.FuelWindow); if (b) on++; if (i > 0 && b != last) sw++; last = b; }
            Check(MathF.Abs(on / (float)N - d) < 1.5f / N * SolidRocket.FuelWindow && sw <= 2 * N / SolidRocket.FuelWindow, $"the duty cycle at {d:F2}: {on / (float)N:F4} of the ticks, {sw} switches");
        }
        // usable: below one tick of the game's rate the sink can not be fed - spent there
        Check(SolidRocket.Usable(R * Dt * 0.99f, row.Thrust, Dt) == 0f && SolidRocket.Usable(R * Dt * 1.01f, row.Thrust, Dt) > 0f && SolidRocket.Usable(-1f, row.Thrust, Dt) == 0f && SolidRocket.Usable(float.NaN, row.Thrust, Dt) == 0f,
              "usable: less than one tick of the game's rate is spent (the game's all-or-nothing sink is not fed from it)");
        var crumbs = new List<Seg> { new Seg { P = 0.4f * R * Dt, F = full }, new Seg { P = 0.4f * R * Dt, F = full } };
        Check(!GameTick(crumbs, R, SolidRocket.TankDischargeRate(row.Thrust)), "(model) crumbs under one tick's demand: the game does not feed the nozzle");
        // never refilled: the core has no way to add propellant (IPiece.Propellant is read only) and the game's tank charges at 0 L/s
        Check(typeof(SolidRocket.IPiece).GetProperty("Propellant").CanWrite == false, "no refill path in the core: a piece's propellant is read only (the game drains the tank; nothing writes it)");
    }

    // ── 3. saved and loaded: the game saves the tanks; the nozzle's burning ──
    static void Saved()
    {
        Check(AeroBlockSaveForm.SolidNozzle(false) == null && AeroBlockSaveForm.TryReadSolidNozzle(AeroBlockSaveForm.SolidNozzle(true), out bool lit) && lit, "nozzle: a key only while it burns, read back");
        // (the retired segment key "aero-solid:1 f" - a few hours of 2026-10-17 builds - is no longer read: ignored then too)
        var map = new Dictionary<string, int> { [AeroBlockSaveForm.SolidNozzle(true)] = 1, ["aero-solid:1 0.5"] = 2 };
        Check(AeroBlockSaveForm.Find(map, AeroBlockSaveForm.SolidNozzleKey) == AeroBlockSaveForm.SolidNozzle(true), "the nozzle's tag never matches the retired segment key");
        // the segment component: nothing read, nothing written (the game saves the tank)
        string save = File.ReadAllText(Path.Combine(SimDir(), "Blocks", "AeroBlockSave.cs"));
        int a = save.IndexOf("public partial class AeroSolidSegmentComponent", StringComparison.Ordinal), b = save.IndexOf("public partial class AeroSolidNozzleComponent", StringComparison.Ordinal);
        string segSave = a > 0 && b > a ? save.Substring(a, b - a) : "";
        Check(!segSave.Contains("TryReadSolid") && !segSave.Contains("[Keen.VRage.DCS.Annotations.Init]") && segSave.Contains("ob.NamedEntities = AeroBlockSave.One(null, Entity);"),
              "the segment: no key read or written - the game saves its tank (the builder still complete: an empty map)");
    }

    // ── 4. ignition: off until lit; once lit it burns to the end - nothing stops it; the end in ONE tick (no override-1 spike) ──
    static void Ignition()
    {
        var m = new SolidRocket.Motor();
        var w = SolidRocket.Step(ref m, 100f, true, 0.9f);
        Check(!w.Enabled && w.Override == 0f && m.Phase == SolidRocket.Phase.Idle, "idle: switched off, no override (the game's movement can not fire it)");
        m.Request = true; w = SolidRocket.Step(ref m, 0f, true, 0.9f);
        Check(m.Phase == SolidRocket.Phase.Idle && !w.Enabled, "no propellant: Ignite does nothing");
        m.Request = true; w = SolidRocket.Step(ref m, 100f, false, 0.9f);
        Check(m.Phase == SolidRocket.Phase.Idle && !w.Enabled && !m.Request, "a broken nozzle does not light (the ask is not kept)");
        m.Request = true; w = SolidRocket.Step(ref m, 100f, true, 0.9f);
        Check(m.Phase == SolidRocket.Phase.Enabling && w.Enabled && w.Override == 0f, "ignite: switched on first (the game books its rating)");
        w = SolidRocket.Step(ref m, 100f, true, 0.9f);
        Check(m.Phase == SolidRocket.Phase.Lit && w.Enabled && w.Override == 0.9f, "the next tick: the override (the game takes its rating out of the controllable maximum)");
        bool held = true;
        for (int i = 0; i < 600; i++)
        {
            m.Request = (i % 7) == 0;                  // (asked again and again: nothing changes)
            bool broken = i > 300 && i < 320;          // (the nozzle damaged a while: it stays lit)
            w = SolidRocket.Step(ref m, 50f, !broken, 0.95f);
            held &= m.Phase == SolidRocket.Phase.Lit && w.Enabled && w.Override == 0.95f;
        }
        Check(held, "lit: whatever happens - the switch, the override, asks, damage - it stays on at its thrust while it has propellant (no throttle, no shutdown)");
        float peak = 0f;
        w = SolidRocket.Step(ref m, 0f, true, 0.95f); peak = MathF.Max(peak, w.Override);
        Check(m.Phase == SolidRocket.Phase.Idle && !w.Enabled && w.Override == 0f && !w.Keep, "spent: the override removed AND switched off in one tick (the game's books stay right - SolidRocket's class text)");
        for (int i = 0; i < 5; i++) { w = SolidRocket.Step(ref m, 0f, true, 0.95f); peak = MathF.Max(peak, w.Override); }
        Check(peak == 0f, "no tick at override 1 at the end (it was up to 3x the vacuum thrust for 1/60 s)");
        m.Request = true; w = SolidRocket.Step(ref m, 0f, true, 0.95f);
        Check(m.Phase == SolidRocket.Phase.Idle && !w.Enabled, "spent: Ignite does nothing (no refill)");
        var t = new SolidRocket.Motor { Request = true };
        SolidRocket.Step(ref t, 1f, true, 0f); w = SolidRocket.Step(ref t, 1f, true, 0f);
        Check(w.Override > 0f, "a zero target never reads as 'no override' (which would hand it back mid-burn)");
        // the source: the nozzle writes the switch before the override (a hand-back finds the block disabled) and no override-1 tolerance is left
        string noz = File.ReadAllText(Path.Combine(SimDir(), "Propulsion", "AeroSolidNozzleComponent.cs"));
        int sw = noz.IndexOf("_pb.Enabled = w.Enabled", StringComparison.Ordinal), ov = noz.IndexOf("_tc.ThrustOverride = 0f", StringComparison.Ordinal);
        Check(sw > 0 && ov > sw && !noz.Contains("w.Override >= 1f"), "Apply: the switch written before the override; no override-1 special case");
    }

    /// <summary>One motor flown to burnout through the game's draw (GameTick) and the mod's core: lit ticks the game fed (thrust), the
    /// propellant the tanks lost, the propellant the thrust asked for (sum of BurnRate x dt), its impulse, the first factor, tanks even.</summary>
    static (int fed, double burned, double wanted, double impulse, float firstFactor, bool even, float peakOverride) Fly(SolidRocketBlocks.Row row, List<Seg> st, float playerOverride, Func<int, float> steer = null)
    {
        var m = new SolidRocket.Motor { Request = true }; var duty = new DutyCycle.State();
        float R = SolidRocket.GameRate(row.Thrust), dis = SolidRocket.TankDischargeRate(row.Thrust), rating = row.Thrust * 1.1f * 3f;
        double start = Total(st), wanted = 0, impulse = 0; int fed = 0; float first = -1f, peak = 0f; bool even = true;
        for (int t = 0; t < 400000; t++)
        {
            float sf = SolidRocket.ShareFactor(SolidRocket.Live(st), 1);
            float fullT = SolidRocket.FullThrust(row.Thrust, sf, 1f, rating, 1f);
            float lv = SolidRocket.LevelOr1(m.Level);
            float frac = steer != null && m.Phase == SolidRocket.Phase.Lit ? Math.Clamp(steer(t), SolidRocket.MinLevel, 1f) : lv;
            var w = SolidRocket.Step(ref m, SolidRocket.Usable(Total(st), row.Thrust, Dt), true, SolidRocket.OverrideFor(frac * fullT, rating, 1f), playerOverride);
            peak = MathF.Max(peak, w.Override);
            if (SolidRocket.Burning(m.Phase))
            {
                if (first < 0f) first = sf;
                bool on = DutyCycle.Step(ref duty, SolidRocket.BurnDuty(sf, frac), SolidRocket.FuelWindow);
                if (GameTick(st, on ? R : 0f, dis)) { fed++; impulse += w.Override * rating * Dt; }
                wanted += SolidRocket.BurnRate(row.Thrust, sf, frac) * Dt;
                for (int i = 1; i < st.Count; i++) if (MathF.Abs(st[i].P - st[0].P) > 1e-2f) even = false;
            }
            if (t > 3 && m.Phase == SolidRocket.Phase.Idle) break;
        }
        return (fed, start - Total(st), wanted, impulse, first, even, peak);
    }

    // ── 5. the burn (the user, 2026-10-17: "3"): n segments burn together - sqrt(n) x the thrust for 8 s x sqrt(n), the factor capped
    //    at 3 (9 segments; a taller stack burns longer at 3x) - every size, through the game's draw ──
    static void BurnTime()
    {
        foreach (var size in new[] { "050", "150", "250", "750" })
        {
            var row = RowOf("SolidNozzle_" + size);
            float full = SolidRocket.FullSegment(row.Thrust), R = SolidRocket.GameRate(row.Thrust);
            Check(MathF.Abs(full - RowOf("SolidSegment_" + size).Propellant) < 0.1f, $"{size}: the table's propellant is the law's ({full:F1} kg)");
            Check(MathF.Abs(full - row.Thrust * 8f / (250f * 9.80665f)) < 0.1f, $"{size}: a full segment = thrust x 8 s / (Isp g0)");
            foreach (int n in new[] { 1, 2, 4, 9, 12 })
            {
                var st = new List<Seg>(); for (int i = 0; i < n; i++) st.Add(Full(full));
                var r = Fly(row, st, 0f);
                float burn = r.fed * Dt, k = MathF.Min(MathF.Sqrt(n), 3f), expect = n * 8f / k;
                Check(MathF.Abs(burn - expect) <= 0.01f * expect + 0.25f && MathF.Abs(r.firstFactor - k) < 1e-5f && r.even && Total(st) <= R * Dt * 1.01f,
                      $"{size}, {n} segment(s): {r.firstFactor:F2}x the thrust for {burn:F3} s (= {n} x 8 s / {k:F2}); every tank drawn together; spent to under one tick's rate ({Total(st):F2} kg left)");
                Check(Math.Abs(r.burned - r.wanted) <= SolidRocket.FuelWindow * R * Dt + 1e-4 * r.wanted, $"{size}, {n}: the game drew what the thrust asked, within one duty window ({r.burned:F1} of {r.wanted:F1} kg)");
            }
        }
        // two nozzles at the ends of one stack, both lit: the same gas through two throats - each half the factor, the stack burns as long as under one
        {
            var row = RowOf("SolidNozzle_150"); float full = SolidRocket.FullSegment(row.Thrust), R = SolidRocket.GameRate(row.Thrust), dis = SolidRocket.TankDischargeRate(row.Thrust);
            var st = new List<Seg>(); for (int i = 0; i < 4; i++) st.Add(Full(full));
            var d1 = new DutyCycle.State(); var d2 = new DutyCycle.State(); int ticks = 0, unfed = 0;
            while (SolidRocket.Usable(Total(st), row.Thrust, Dt) > 0f && ticks < 100000)
            {
                float sf = SolidRocket.ShareFactor(SolidRocket.Live(st), 2);
                bool a = DutyCycle.Step(ref d1, SolidRocket.BurnDuty(sf, 1f), SolidRocket.FuelWindow), b = DutyCycle.Step(ref d2, SolidRocket.BurnDuty(sf, 1f), SolidRocket.FuelWindow);
                unfed += (a ? 1 : 0) + (b ? 1 : 0) - GameTick2(st, a ? R : 0f, b ? R : 0f, dis);
                ticks++;
            }
            Check(MathF.Abs(ticks * Dt - 4f * 8f / 2f) < 0.3f && unfed <= 2, $"two lit nozzles on four segments: each factor 1 (2 / 2), the stack burns {ticks * Dt:F2} s (= 16 s, as under one nozzle at 2x); both fed but {unfed} tick(s) at the very end (the last crumbs: one sink at a time)");
        }
    }

    // ── 10. the burn follows the thrust - level AND steering: the same impulse at any level, half power burns twice as long ──
    static void LevelBurn()
    {
        var row = RowOf("SolidNozzle_150");
        float full1 = SolidRocket.FullSegment(row.Thrust), R = SolidRocket.GameRate(row.Thrust);
        foreach (int n in new[] { 1, 4 })
        {
            double impulse1 = -1;
            foreach (float level in new[] { 1f, 0.7f, 0.5f })
            {
                var st = new List<Seg>(); for (int i = 0; i < n; i++) st.Add(Full(full1));
                var r = Fly(row, st, level == 1f ? 0f : level);
                if (level == 1f) impulse1 = r.impulse;
                float burn = r.fed * Dt, k = MathF.Sqrt(n), expect = n * 8f / k / level;
                Check(MathF.Abs(burn - expect) <= 0.01f * expect + 0.25f && Math.Abs(r.impulse - impulse1) <= 0.01 * impulse1 && Math.Abs(r.burned - r.wanted) <= SolidRocket.FuelWindow * R * Dt + 1e-4 * r.wanted,
                      $"{n} segment(s) at {level * 100f:F0} %: burns {burn:F3} s (= {expect:F3} s, 1/level), impulse {r.impulse / 1e6:F3} MN s (at full: {impulse1 / 1e6:F3}); drew {r.burned:F0} of {r.wanted:F0} kg asked");
            }
            // steered: the allocator moves the share every few ticks (0.5..1) - the game's draw still follows it
            var rnd = new Random(7 + n); float cur = 0.8f;
            var st2 = new List<Seg>(); for (int i = 0; i < n; i++) st2.Add(Full(full1));
            var s2 = Fly(row, st2, 0f, t => { if (t % 5 == 0) cur = 0.5f + 0.5f * (float)rnd.NextDouble(); return cur; });
            Check(Math.Abs(s2.burned - s2.wanted) <= SolidRocket.FuelWindow * R * Dt + 1e-3 * s2.wanted && Math.Abs(s2.impulse - impulse1) <= 0.015 * impulse1,
                  $"{n} segment(s) steered (the share moving 0.5..1): drew {s2.burned:F0} of the {s2.wanted:F0} kg its thrust asked; impulse {s2.impulse / 1e6:F3} MN s (the stack's total)");
        }
        Check(SolidRocket.BurnRate(1000f, 2f, 0.2f) == SolidRocket.BurnRate(1000f, 2f, 0.5f) && SolidRocket.BurnRate(1000f, 2f, float.NaN) == SolidRocket.BurnRate(1000f, 2f, 1f), "the burn rate's share: clamped to 0.5..1 (NaN: full)");
    }

    // ── 6. the thrust: flat with speed (no speed in the law), works in vacuum, +10 % there ──
    static void Thrust()
    {
        var row = RowOf("SolidNozzle_250");
        float vac = row.Thrust * (1f + SolidRocketBlocks.VacuumBoost);
        Check(SolidRocket.ThrustAt(row.Thrust, 1f) == row.Thrust && SolidRocket.ThrustAt(row.Thrust, 2f) == row.Thrust, "sea level (and denser): the rated thrust");
        Check(MathF.Abs(SolidRocket.ThrustAt(row.Thrust, 0f) - vac) < 1f && MathF.Abs(SolidRocketBlocks.VacuumBoost - 0.10f) < 1e-6f, "vacuum: +10 % (it works in space)");
        Check(MathF.Abs(SolidRocket.ThrustAt(row.Thrust, 0.5f) - row.Thrust * 1.05f) < 1f && SolidRocket.ThrustAt(row.Thrust, float.NaN) == row.Thrust, "half pressure: +5 % (linear); NaN air reads as sea level");
        Check(SolidRocket.PressureRatio(1f) == 1f && SolidRocket.PressureRatio(1.3f) == 1f && SolidRocket.PressureRatio(0f) == 0f && MathF.Abs(SolidRocket.PressureRatio(0.5f) - 0.5f) < 1e-6f,
            "the pressure ratio on the game's scale (a vanilla planet's generator Density 1 = sea level)");
        // the override that the game turns into that thrust (its ThrustPower is the vacuum thrust; hydrogen class efficiency 1)
        float oSea = SolidRocket.OverrideFor(SolidRocket.ThrustAt(row.Thrust, 1f), vac, 1f), oVac = SolidRocket.OverrideFor(SolidRocket.ThrustAt(row.Thrust, 0f), vac, 1f);
        Check(MathF.Abs(oSea - 1f / 1.1f) < 1e-4f && MathF.Abs(oVac - 1f) < 1e-5f, $"the override: {oSea:F4} at sea level, {oVac:F4} in vacuum (never above 1)");
        Check(MathF.Abs(oSea * vac - row.Thrust) < 2f, "override x ThrustPower x efficiency = the thrust wanted (the game's overridden thrust)");
        Check(SolidRocket.OverrideFor(1f, 0f, 1f) == 0f && SolidRocket.OverrideFor(row.Thrust, vac, 0f) == 1f && SolidRocket.OverrideFor(row.Thrust, vac, float.NaN) <= 1f, "no rating / no efficiency: clamped, never NaN");
        // flat with speed: the mass flow and the thrust never see a speed - one burn at 0 and at Mach 5 is the same
        Check(SolidRocket.MassFlow(row.Thrust) == row.Thrust / (250f * 9.80665f), "the mass flow is flat (thrust / Isp g0): the air changes the thrust, never the burn time");
    }

    // ── 13. the air a nozzle reads (2026-10-17, in game: p 0.85 idle, 0.69 lit, 0.84 after on a grid held at 300 m - the source
    //        was picked on whether the force model ran that step (HasResult: only above its minimum speed) over two scales) ──
    static void AirSource()
    {
        // the replay: game density 0.845 at 300 m, aero's the same (no upper tail there); the grid still, then moving (lit), then still
        float game = 0.845f, aeroRho = 0.845f;
        float idle = SolidRocket.PressureRatioOf(true, aeroRho, game), lit = SolidRocket.PressureRatioOf(true, aeroRho, game), after = SolidRocket.PressureRatioOf(true, aeroRho, game);
        Check(idle == lit && lit == after && MathF.Abs(lit - 0.845f) < 1e-6f, $"one source: p {idle:F3} idle, {lit:F3} lit, {after:F3} after (no speed in it)");
        // before aero has read the grid's air (AirKnown false): the game's - on the SAME scale, so the hand-over is seamless
        float before = SolidRocket.PressureRatioOf(false, 0f, game);
        Check(before == lit, $"before aero reads the air: the game's density on the same scale ({before:F3} = {lit:F3}) - no step at the hand-over");
        Check(SolidRocket.PressureRatioOf(false, 0f, 1f) == 1f && SolidRocket.PressureRatioOf(true, 1f, 1f) == 1f, "a vanilla planet's sea level (Density 1): p = 1 on both paths - the rated sea-level thrust");
        // the old law's dip, for the record: 0.845 / 1.225 = 0.69 (the 1.5 % extra thrust seen while lit)
        float oldLit = 0.845f / 1.225f;
        var row = RowOf("SolidNozzle_150");
        float extra = SolidRocket.ThrustAt(row.Thrust, oldLit) / SolidRocket.ThrustAt(row.Thrust, lit) - 1f;
        Check(MathF.Abs(oldLit - 0.69f) < 0.005f && extra > 0.01f && extra < 0.02f, $"(the old aero path's p {oldLit:F2}: +{extra * 100f:F1} % thrust - gone)");
        // aero's thin upper tail above the game's air edge is aero's own air: used when aero has read it, never less than the game's
        Check(SolidRocket.PressureRatioOf(true, 0.002f, 0f) == 0.002f && SolidRocket.PressureRatioOf(false, 0.002f, 0f) == 0f, "above the game's air edge: aero's tail density when aero reads it, the game's 0 otherwise");
        Check(SolidRocket.PressureRatioOf(false, 0f, float.NaN) == 0f && SolidRocket.PressureRatioOf(true, float.NaN, 0.5f) == 1f, "a NaN game density is vacuum; a NaN aero density reads as sea level (PressureRatio's rule)");
    }

    // ── 14. a component's first look, retried (LookRetry: the propulsor / nozzle / segment / exhaust components) ──
    static void Look()
    {
        var l = new LookRetry();
        Check(l.Due(), "the first look: at once");
        int tries = 1, steps = 0;
        // fails for 3 tries, then the block's row is readable
        for (; steps < 10 * LookRetry.RetryTicks && !l.Done; steps++)
            if (l.Due()) { tries++; if (tries >= 4) l.Done = true; }
        Check(l.Done && tries == 4, $"a failed look is tried again (4 tries) - not given up after the first ({tries})");
        Check(steps >= 3 * LookRetry.RetryTicks && steps <= 3 * LookRetry.RetryTicks + 3, $"one try every {LookRetry.RetryTicks} steps (3 retries in {steps})");
        bool any = false; for (int i = 0; i < 5 * LookRetry.RetryTicks; i++) any |= l.Due();
        Check(!any, "done: never due again (no lookups once found)");
        l.Reset();
        Check(l.Due(), "back in the scene (Reset): looks again at once");
        // never gives up: still trying after a long time
        var m = new LookRetry(); int n = 0;
        for (int i = 0; i < 100 * LookRetry.RetryTicks; i++) if (m.Due()) n++;
        Check(n >= 99, $"a look that keeps failing keeps being tried ({n} tries in {100 * LookRetry.RetryTicks} steps)");
    }

    // ── 7. the allocator never uses it: out of the thruster set, its thrust out of the shares ──
    static void Allocator()
    {
        string sim = SimDir();
        string cache = File.ReadAllText(Path.Combine(sim, "Propulsion", "OffsetThrustJob.Cache.cs"));
        int skip = cache.IndexOf("if (SolidRocketBlocks.IsNozzle(block)) continue;", StringComparison.Ordinal), add = cache.IndexOf("outThrusters.Add(", StringComparison.Ordinal);
        Check(skip > 0 && add > skip, "the thruster cache skips a solid rocket nozzle before it is added (no allocation, steering or share)");
        string tt = File.ReadAllText(Path.Combine(sim, "Propulsion", "ThrustTorque.cs"));
        Check(tt.Contains("if (solid && aero.IsServerScene) gameF -= solidF;") && tt.Contains("if (solid) rotorReaction += solidTau - steerTau;") && tt.Contains("offsetTorque += rotorReaction + steerTau;"),
              "ThrustTorque: the rockets' thrust out of the shared thrust (server); an unlit-for-steering one's torque an outside torque, a lit one's in the solve's slots - all applied");
        string noz = File.ReadAllText(Path.Combine(sim, "Propulsion", "AeroSolidNozzleComponent.cs"));
        Check(noz.Contains("n.Motor.Phase == SolidRocket.Phase.Lit && n.ThrustNow > 0f"), "the attitude solve takes a nozzle only when it is LIT and thrusting (never before ignition)");
        // THE STRUCTURE (2026-10-17): the nozzle's whole thrust (the game's override on it now: base + steering) at its block, every
        // server tick it thrusts, as an engine; the thruster loads hold the cache's thrusters only (never a nozzle) - once
        Check(noz.IndexOf("PublishLoad();", StringComparison.Ordinal) > noz.IndexOf("if (!_server) return;", StringComparison.Ordinal) && noz.IndexOf("if (!_server) return;", StringComparison.Ordinal) > 0 && noz.Contains("aero.PublishBlockLoad(Entity, PushLocal * t, PosLocal, Vector3.Zero, true);") && noz.Contains("float t = ThrustNow;"),
              "the nozzle publishes its thrust now (ThrustNow: base + steering) along its push at its block, every server tick, isEngine");
        string ss = File.ReadAllText(Path.Combine(sim, "Structure", "Server", "StructureServer.cs"));
        int thr = ss.IndexOf("for (int i = 0; i < _thrusterCache.Count && i < ns", StringComparison.Ordinal);
        Check(thr > 0 && ss.IndexOf("L.ThrF0.Add(", StringComparison.Ordinal) > thr && !ss.Contains("AeroSolidNozzleComponent"),
              "the structure's thruster loads (ThrF / ThrF0) are the thruster cache's shares only - a nozzle's thrust is not in them (no double count)");
        // the published thrust is the commanded one - base, or the allocator's steer - through the override: ThrustNow = override x ThrustPower x eff
        {
            var row = RowOf("SolidNozzle_050");
            float tp = row.Thrust * (1f + SolidRocketBlocks.VacuumBoost) * SolidRocket.MaxStackFactor, eff = 1f;
            float full = SolidRocket.FullThrust(row.Thrust, 1f, 1f, tp, eff);
            float worst = 0f;
            foreach (float frac in new[] { 1f, 0.8f, 0.5f, 0.73f })
            {
                float cmd = frac * full;   // (base: level x full; steered: the allocator's share of full)
                float now = SolidRocket.OverrideFor(cmd, tp, eff) * tp * eff;
                worst = MathF.Max(worst, MathF.Abs(now - cmd) / cmd);
            }
            Check(worst < 1e-5f, $"the nozzle's thrust now is exactly what it commands - base or steered (worst {worst:E1} relative): the load published is base + steering");
        }
        Check(tt.Contains("if (anyRev || RotorCyclic.Has(aero, n) || steerN > 0)") && tt.Contains("if (steerN > 0) SolveWithSolids("), "both torque branches (steered and rate) solve with the lit rockets");
        Check(noz.Contains("FuelDutyNow = SolidRocket.Burning(Motor.Phase) ? SolidRocket.BurnDuty(StackFactor, frac) : 0f;") && noz.Contains("DutyCycle.Step(ref _duty, FuelDutyNow, SolidRocket.FuelWindow)")
              && noz.Contains("SolidRocket.OverrideFor(CommandThrust, ThrustPower, Efficiency)") && noz.Contains("[Before(typeof(Keen.Game2.Simulation.GridUpdateOrder.ResourceDistributorUpdate))]"),
              "the nozzle writes the commanded thrust through its override and lets the game burn that share (its duty, before the distributor charges the tick)");
        Check(noz.Contains("StackFactor = SolidRocket.ShareFactor(live, sharers);") && noz.Contains("SolidRocket.Usable(total, Row.Thrust, AeroPhysics.Dt)") && !noz.Contains("DrainEven"),
              "the nozzle: sqrt(n) of its stack's live tanks shared with a lit partner; spent below one tick of the game's rate; it never drains anything itself");
        string inj = File.ReadAllText(Path.Combine(sim, "Injection", "EntityInjector.cs"));
        Check(inj.Contains("SolidRocketBlocks.SegmentServerPrefabs") && inj.Contains("SolidRocketBlocks.NozzleClientPrefabs"), "the injector gives the segments and nozzles their components");
        // the numbers: two Up thrusters (100 kN each) at half throttle and a lit 0.5 m rocket (its overridden thrust in the game's):
        // with the rocket's thrust taken out, the thrusters get exactly their own share - none of the rocket's
        var arms = new[] { new Vector3(0, 0, 5), new Vector3(0, 0, -5) }; var dirs = new[] { Vector3.UnitY, Vector3.UnitY };
        var caps = new[] { 100e3f, 100e3f }; var capDir = new float[6]; capDir[ThrustAllocator.DirIndex(Vector3.UnitY)] = 200e3f;
        float rocket = RowOf("SolidNozzle_050").Thrust;
        var gameF = new Vector3(0, 100e3f + rocket, 0); var solidF = new Vector3(0, rocket, 0);
        var f0 = new float[2];
        ThrustAllocator.GameShare(gameF - solidF, arms, dirs, caps, capDir, f0, 2);
        Check(MathF.Abs(f0[0] - 50e3f) < 1f && MathF.Abs(f0[1] - 50e3f) < 1f, $"the thrusters' shares without the rocket: 50 kN each ({f0[0]:F0}, {f0[1]:F0})");
        ThrustAllocator.GameShare(gameF, arms, dirs, caps, capDir, f0, 2);
        Check(f0[0] > 50e3f + 1f, "(left in, the rocket's thrust would have been booked on the thrusters - the mistake this avoids)");
    }

    // ── 9. the power level (the user, 2026-10-17: "It must be at least 50% power, which you can set in thrust override prior") ──
    static void PowerLevel()
    {
        Check(SolidRocket.LevelFrom(0f) == 1f && SolidRocket.LevelFrom(float.NaN) == 1f && SolidRocket.LevelFrom(-0.2f) == 1f, "the level: an unset override (0) is full power");
        Check(SolidRocket.LevelFrom(0.3f) == 0.5f && SolidRocket.LevelFrom(0.01f) == 0.5f, "the level: under 50 % is 50 % (the floor)");
        Check(SolidRocket.LevelFrom(0.7f) == 0.7f && SolidRocket.LevelFrom(1f) == 1f && SolidRocket.LevelFrom(1.5f) == 1f, "the level: 70 % is 70 %, 100 % is 100 %");
        // idle: the player's override left alone (it is the level); ignition latches it and takes it off BEFORE switching on
        var m = new SolidRocket.Motor();
        var w = SolidRocket.Step(ref m, 100f, true, 0.4f, 0.7f);
        Check(w.Keep && !w.Enabled && m.Phase == SolidRocket.Phase.Idle, "idle: switched off, the player's override kept (the slider is the level)");
        m.Request = true; w = SolidRocket.Step(ref m, 100f, true, 0.4f, 0.7f);
        Check(m.Phase == SolidRocket.Phase.Clearing && !w.Enabled && !w.Keep && w.Override == 0f && m.Level == 0.7f, "ignite: the level latched (0.7), the player's override removed first, still switched off");
        w = SolidRocket.Step(ref m, 100f, true, 0.4f, 0f);
        Check(m.Phase == SolidRocket.Phase.Enabling && w.Enabled && w.Override == 0f, "then switched on (no override on it: the game books its rating)");
        w = SolidRocket.Step(ref m, 100f, true, 0.4f, 0f);
        Check(m.Phase == SolidRocket.Phase.Lit && w.Enabled && w.Override == 0.4f, "then the motor's own override (the commanded thrust)");
        bool locked = true;
        for (int i = 0; i < 200; i++) { m.Request = i % 5 == 0; w = SolidRocket.Step(ref m, 50f, true, 0.4f, (i % 10) / 10f); locked &= m.Phase == SolidRocket.Phase.Lit && m.Level == 0.7f && w.Enabled && w.Override == 0.4f; }
        Check(locked, "lit: the player's slider changes nothing (the level is locked, the override put back) and nothing shuts it down");
        var b = new SolidRocket.Motor { Request = true };
        w = SolidRocket.Step(ref b, 100f, false, 0.4f, 0.6f);
        Check(b.Phase == SolidRocket.Phase.Idle && w.Keep, "a broken nozzle does not light: the player's override stays");
        var c = new SolidRocket.Motor { Request = true };
        SolidRocket.Step(ref c, 100f, true, 0.4f, 0.6f); w = SolidRocket.Step(ref c, 100f, false, 0.4f, 0f);
        Check(c.Phase == SolidRocket.Phase.Idle && !w.Enabled, "broken while clearing: back to idle, never switched on");
        var d = new SolidRocket.Motor { Request = true };
        w = SolidRocket.Step(ref d, 100f, true, 0.4f, 0f);
        Check(d.Phase == SolidRocket.Phase.Enabling && d.Level == 1f, "no override set: straight to switching on, at full power");
        // the bounds the allocator gets
        SolidRocket.SteerBounds(1000f, 0.7f, out float lo, out float hi, out float bs);
        Check(lo == 500f && hi == 1000f && MathF.Abs(bs - 700f) < 1e-3f, "the allocator's bounds: 0.5..1 x full, its base level x full");
        SolidRocket.SteerBounds(1000f, 0f, out _, out _, out bs);
        Check(bs == 1000f, "a level never latched (a burn saved before levels): full power");
        Check(SolidRocket.FractionOf(300f, 1000f, 0.7f) == 0.5f && SolidRocket.FractionOf(2000f, 1000f, 0.7f) == 1f && SolidRocket.FractionOf(float.NaN, 1000f, 0.7f) == 0.7f && SolidRocket.FractionOf(800f, 0f, 0.7f) == 0.7f,
              "a solved force as the share of full: clamped to 0.5..1; NaN or no full: its level");
        float full = SolidRocket.FullThrust(1000f, 2f, 1f, 1e9f, 1f);
        Check(full == 2000f && SolidRocket.FullThrust(1000f, 3f, 0f, 3000f, 1f) == 3000f && SolidRocket.FullThrust(1000f, 2f, 1f, 1e9f, float.NaN) == 2000f, "full thrust: the stack's (x StackFactor, the air's boost), never above what the override can give");
    }

    // ── 11. the level saved with the burn and restored ──
    static void LevelSaved()
    {
        string k = AeroBlockSaveForm.SolidNozzle(true, 0.7f);
        Check(k == AeroBlockSaveForm.SolidNozzleKey + "1 1 0.7" && AeroBlockSaveForm.TryReadSolidNozzle(k, out bool lit, out float lv) && lit && lv == 0.7f, $"a burn at 70 % saved and read back ('{k}')");
        Check(AeroBlockSaveForm.TryReadSolidNozzle("aero-solidnozzle:1 1", out lit, out lv) && lit && lv == 1f, "a burn saved before levels (no level): full power");
        Check(AeroBlockSaveForm.TryReadSolidNozzle("aero-solidnozzle:1 1 0.2", out _, out lv) && lv == 0.5f && AeroBlockSaveForm.TryReadSolidNozzle("aero-solidnozzle:1 1 junk", out _, out lv) && lv == 1f, "a level under the floor reads 0.5, an unreadable one 1");
        Check(AeroBlockSaveForm.SolidNozzle(false, 0.7f) == null && AeroBlockSaveForm.SolidNozzle(true, 0f) == AeroBlockSaveForm.SolidNozzleKey + "1 1 1", "idle: no key; a level never latched: saved as 1");
        Check(AeroBlockSaveForm.TryReadSolidNozzle(k, out bool lit2) && lit2, "the old reader still reads the new key");
        Check(!SolidRocket.SavedLit(SolidRocket.Phase.Idle) && !SolidRocket.SavedLit(SolidRocket.Phase.Clearing) && SolidRocket.SavedLit(SolidRocket.Phase.Enabling) && SolidRocket.SavedLit(SolidRocket.Phase.Lit),
              "saved as burning: lit or switched on to light - not idle, not while the player's override is being cleared");
        // a burn continuing after load keeps its level: the motor restored Lit with the level read back holds it
        var m = new SolidRocket.Motor { Phase = SolidRocket.Phase.Lit, Level = SolidRocket.LevelOr1(lv = 0.7f) };
        SolidRocket.Step(ref m, 10f, true, 0.3f, 0.95f);
        Check(m.Phase == SolidRocket.Phase.Lit && m.Level == 0.7f, "restored mid-burn: still lit at its saved level (the slider changes nothing)");
    }

    // ── 12. steering: lit nozzles are bounded slots of the attitude solve (ThrustTorque.SolveWithSolids' assembly, the real solver) ──
    /// <summary>The solve with thrusters (pos, dir, cap, game share) and lit nozzles (pos, push, full, level) appended as
    /// SolveWithSolids does; returns each slot's force (thrusters then nozzles).</summary>
    static float[] SolveSteer(Vector3 tau, Vector3 gameF, (Vector3 pos, Vector3 dir, float cap, float share)[] thr, (Vector3 pos, Vector3 push, float full, float level)[] noz,
        out Vector3 netKept, out Vector3 torque, out float[] lo, out float[] hi, out float[] bs)
    {
        int n = thr.Length, m = noz.Length, N = n + m;
        var arms = new Vector3[N]; var dirs = new Vector3[N]; var caps = new float[N]; var los = new float[N]; var f0 = new float[N]; var f = new float[N]; var rev = new bool[N];
        lo = new float[m]; hi = new float[m]; bs = new float[m];
        Vector3 net = gameF; float sq = 0f;
        for (int i = 0; i < n; i++) { arms[i] = Vector3.Cross(thr[i].pos, thr[i].dir); dirs[i] = thr[i].dir; caps[i] = thr[i].cap; f0[i] = f[i] = thr[i].share; los[i] = -caps[i]; sq += thr[i].pos.LengthSquared(); }
        for (int j = 0; j < m; j++)
        {
            int k = n + j;
            SolidRocket.SteerBounds(noz[j].full, noz[j].level, out lo[j], out hi[j], out bs[j]);
            arms[k] = Vector3.Cross(noz[j].pos, noz[j].push); dirs[k] = noz[j].push; caps[k] = hi[j]; los[k] = lo[j]; f0[k] = f[k] = bs[j]; rev[k] = true;
            net += noz[j].push * bs[j]; sq += noz[j].pos.LengthSquared();
        }
        // (warm: the solve runs every frame, warm-started from the last - a few frames here)
        for (int it = 0; it < 6; it++) ThrustAllocator.Solve(tau, net, arms, dirs, caps, f0, f, new float[N], new float[N], sq, N, 60, out _, rev, los);
        torque = Vector3.Zero; netKept = Vector3.Zero;
        for (int i = 0; i < N; i++) { torque += arms[i] * f[i]; netKept += dirs[i] * f[i]; }
        netKept -= net;   // (the net the solve kept, less what it was asked to keep: 0 = no translation from the steering)
        return f;
    }

    static void Steering()
    {
        const float Full = 1e6f;
        // two strap-on boosters pushing +Y at x = +-3 m (torque per newton about Z: +3 / -3), 80 %
        var two = new[] { (new Vector3(3, 0, 0), Vector3.UnitY, Full, 0.8f), (new Vector3(-3, 0, 0), Vector3.UnitY, Full, 0.8f) };
        var none = Array.Empty<(Vector3, Vector3, float, float)>();
        var f = SolveSteer(Vector3.Zero, Vector3.Zero, none, two, out var dn, out var tq, out var lo, out var hi, out var bs);
        Check(MathF.Abs(f[0] - bs[0]) < 1e-3f * Full && MathF.Abs(f[1] - bs[1]) < 1e-3f * Full, $"lit, no torque asked: both hold their base ({f[0] / 1e3f:F1}, {f[1] / 1e3f:F1} kN of {bs[0] / 1e3f:F0})");
        var ask = new Vector3(0, 0, 0.3e6f);
        f = SolveSteer(ask, Vector3.Zero, none, two, out dn, out tq, out lo, out hi, out bs);
        Check((tq - ask).Length() < 0.02f * ask.Length() && f[0] > bs[0] && f[1] < bs[1], $"lit, a yaw asked: the boosters throttle differentially ({f[0] / 1e3f:F0} / {f[1] / 1e3f:F0} kN, torque {tq.Z / 1e3f:F0} of {ask.Z / 1e3f:F0} kN m)");
        Check(f.All(x => x >= lo[0] - 1f && x <= hi[0] + 1f), "within 0.5..1 x full");
        Check(MathF.Abs(dn.Y) < 0.01f * Full, $"no net push from the steering: the deviations cancel ({dn.Y / 1e3f:F2} kN)");
        f = SolveSteer(new Vector3(0, 0, 10e6f), Vector3.Zero, none, two, out dn, out tq, out lo, out hi, out bs);
        Check(MathF.Abs(f[0] - hi[0]) < 0.01f * Full && MathF.Abs(f[1] - lo[1]) < 0.01f * Full, $"a torque past their reach: one at full, the other at 50 % - never outside ({f[0] / 1e3f:F0} / {f[1] / 1e3f:F0} kN)");
        // at 100 %: they can only throttle down - each way by the other one
        var full2 = new[] { (new Vector3(3, 0, 0), Vector3.UnitY, Full, 1f), (new Vector3(-3, 0, 0), Vector3.UnitY, Full, 1f) };
        f = SolveSteer(ask, Vector3.Zero, none, full2, out dn, out tq, out lo, out hi, out bs);
        Check(f[1] < hi[1] - 0.05f * Full && f[0] > hi[0] - 0.01f * Full && (tq - ask).Length() < 0.02f * ask.Length(), $"at 100 %: the one on the side to slow throttles down, the other stays at full ({f[0] / 1e3f:F0} / {f[1] / 1e3f:F0} kN)");
        // a single nozzle on the axis (no arm): it can not turn the ship and holds its base whatever is asked
        var axial = new[] { (Vector3.Zero, Vector3.UnitY, Full, 0.6f) };
        f = SolveSteer(new Vector3(1e6f, 0, 1e6f), Vector3.Zero, none, axial, out dn, out tq, out lo, out hi, out bs);
        Check(MathF.Abs(f[0] - bs[0]) < 1e-3f * Full, $"a nozzle on the centre line (no arm, no gimbal): stays at its base ({f[0] / 1e3f:F1} of {bs[0] / 1e3f:F0} kN)");
        // the net-force bookkeeping with thrusters: an off-centre booster (x = +2 m, base 300 kN) trimmed by a thruster at x = -2 m (its
        // game share 300 kN) and a main thruster on the centre line (100 kN); a yaw asked: whatever moves, the net push stays the game's
        // thrust + the booster's base - its departure is the allocator's, taken back by the thrusters
        var thr = new[] { (new Vector3(-2, 0, 0), Vector3.UnitY, 600e3f, 300e3f), (Vector3.Zero, Vector3.UnitY, 400e3f, 100e3f) };
        var one = new[] { (new Vector3(2, 0, 0), Vector3.UnitY, 0.4e6f, 0.75f) };
        var gameF = new Vector3(0, 400e3f, 0);
        var ask2 = new Vector3(0, 0, -0.3e6f);
        f = SolveSteer(ask2, gameF, thr, one, out dn, out tq, out lo, out hi, out bs);
        Check(f[2] >= lo[0] - 1f && f[2] <= hi[0] + 1f && MathF.Abs(f[2] - bs[0]) > 1e3f, $"thrusters + a booster, a yaw asked: the booster moves within its bounds ({f[2] / 1e3f:F0} kN, base {bs[0] / 1e3f:F0})");
        Check(MathF.Abs(dn.Y) < 0.01f * (gameF.Y + bs[0]), $"the net push stays the game's + the booster's base: its departure taken back by the thrusters ({dn.Y / 1e3f:F2} kN off; thrusters {f[0] / 1e3f:F0} + {f[1] / 1e3f:F0} kN)");
        Check(Vector3.Dot(tq, ask2) > 0f && (tq - ask2).Length() < 0.15f * ask2.Length(), $"and the torque asked is given, within the take-back's spread (KeepNetThrust adds the departure onto the thrusters by headroom, as for a reversible thruster; the rate loop closes the rest): {tq.Z / 1e3f:F1} of {ask2.Z / 1e3f:F1} kN m");
        // no torque asked: everything at the game's share and the base
        f = SolveSteer(Vector3.Zero, gameF, thr, one, out dn, out tq, out lo, out hi, out bs);
        Check(MathF.Abs(f[2] - bs[0]) < 0.01f * bs[0] && MathF.Abs(dn.Y) < 0.01f * gameF.Y, $"no torque asked: the booster at its base, the net unchanged ({f[2] / 1e3f:F1} of {bs[0] / 1e3f:F0} kN)");
        // the reach the aware law plans with: two opposite boosters at 100 % turn both ways by half of full x arm; one alone, not at all
        Vector3 plus = Vector3.Zero, minus = Vector3.Zero;
        SolidRocket.AddSteerReach(new Vector3(0, 0, -3), 0.5f * Full, Full, Full, ref plus, ref minus);
        Check(Vector3.Min(plus, minus).Z == 0f, "one booster at 100 %: no reach both ways (it can only throttle down)");
        SolidRocket.AddSteerReach(new Vector3(0, 0, 3), 0.5f * Full, Full, Full, ref plus, ref minus);
        Check(MathF.Abs(Vector3.Min(plus, minus).Z - 1.5f * Full) < 1f, $"two opposite boosters at 100 %: {Vector3.Min(plus, minus).Z / 1e6f:F2} MN m each way (3 m x 50 % of full)");
        // the old maths unchanged: lo null is -cap for a reversible slot (ThrustTests covers the reversible thrusters)
        var a1 = new[] { new Vector3(0, 0, 2) }; var d1 = new[] { Vector3.UnitY }; var c1 = new[] { 1000f }; var z1 = new[] { 0f }; var g1 = new[] { 0f };
        ThrustAllocator.Solve(new Vector3(0, 0, -1000f), Vector3.Zero, a1, d1, c1, z1, g1, new float[1], new float[1], 4f, 1, 200, out _, new[] { true }, null);
        Check(g1[0] < -400f, $"lo null: a reversible slot still goes negative ({g1[0]:F0} N)");
    }

    // ── 8. the content gen_propulsor_defs.py wrote (the conveyor version) ──
    static string BlocksRoot()
    {
        string root = Path.GetFullPath(Path.Combine(SimDir(), "..", "..", "Assets", "Aero", "Propulsors"));
        return Directory.Exists(root) ? root : null;
    }

    static void Content()
    {
        string root = BlocksRoot();
        if (root == null) { Console.WriteLine("   (no Blocks project: content checks skipped)"); return; }
        const string Hole = "e4203734-4c68-4490-a77e-7e299ad6839d", Use = "29d45e5b-2ddf-4ba5-9031-885260550947", Breakable = "bc5652a0-1296-4040-adc3-a2b9c35d5afc";
        const string TConveyor = "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.ResourceDistribution.Conveyors.ConveyorComponent";
        const string TTank = "Game2:Keen.Game2.Simulation.WorldObjects.CubeBlocks.ResourceDistribution.Resources.ResourceContainerComponent";
        JsonElement V(string rel) => JsonDocument.Parse(File.ReadAllText(Path.Combine(root, rel))).RootElement.GetProperty("$Value");
        Check(SolidRocketBlocks.Rows.Count == 8 && SolidRocketBlocks.Rows.Count(r => r.Nozzle) == 4, "the table: 4 segments and 4 nozzles");
        string groupGuid = V("Propulsor_SolidRockets_BlockGroup.def").GetProperty("Guid").GetString();
        Check(V("Propulsor_SolidRockets_BlockGroup.def").GetProperty("Name").GetString() == "Solid Rockets", "the G-menu group 'Solid Rockets'");
        foreach (var kind in new[] { "SolidSegment", "SolidNozzle" })
        {
            var k = V($"{kind}_BlockKind.def");
            Check(k.GetProperty("BlockGroups").GetArrayLength() == 1 && k.GetProperty("BlockGroups")[0].GetProperty("Value").GetString() == groupGuid, $"{kind}: in the Solid Rockets group");
        }
        // the resource: its own type, carried by conveyors, litres
        var res = V("SolidPropellant_ResourceTypeDefinition.def");
        string resGuid = SolidRocketBlocks.Resource.ToString();
        Check(res.GetProperty("Guid").GetString() == resGuid && res.GetProperty("Name").GetString() == SolidRocketBlocks.ResourceName && res.GetProperty("RequiresConveyors").GetBoolean()
              && res.GetProperty("StorageUnits").GetString() == "Liters" && res.GetProperty("FlowRateUnits").GetString() == "LitersPerSecond",
              $"the resource '{SolidRocketBlocks.ResourceName}': its own ResourceTypeDefinition, RequiresConveyors, litres (= kg)");
        var conv = V("SolidRocket_ConveyorComponentDefinition.def");
        string convGuid = conv.GetProperty("Guid").GetString();
        Check(conv.GetProperty("ConveyorSubgraph").ValueKind == JsonValueKind.Null, "the solid blocks' conveyor definition: no subgraph (the game builds it from the ports)");
        foreach (var r in SolidRocketBlocks.Rows)
        {
            string n = r.Name;
            // every conveyor dummy of a solid block: a ConveyorHole at the size's own scale (never a vanilla one, never a UseConveyor)
            var dummies = V($"{n}/{n}_BlockModelDefinition.partialdef").GetProperty("Manipulator").GetProperty("Dummies").GetProperty("Changed").EnumerateArray().Select(c => c.GetProperty("Value")).ToList();
            var ports = dummies.Where(dd => dd.GetProperty("Type").GetString() == Hole || dd.GetProperty("Type").GetString() == Use).ToList();
            Check(ports.Count == (r.Nozzle ? 1 : 2) && ports.All(dd => dd.GetProperty("Type").GetString() == Hole), $"{n}: {(r.Nozzle ? "one port" : "two ports")}, ConveyorHole (pipeable, no hand access)");
            Check(ports.All(dd => MathF.Abs(dd.GetProperty("Scale").GetProperty("X").GetSingle() - r.PortScale) < 1e-6f && MathF.Abs(dd.GetProperty("Scale").GetProperty("Y").GetSingle() - r.PortScale) < 1e-6f)
                  && ports.All(dd => SolidRocketBlocks.VanillaPortScales.All(v => MathF.Abs(dd.GetProperty("Scale").GetProperty("X").GetSingle() - v) > 1e-3f)),
                  $"{n}: its ports at {r.PortScale} m square - the size's own scale, no vanilla port's");
            var top = Port(root, n, "SolidPortTop"); var bot = Port(root, n, "SolidPortBottom");
            float half = r.Box.Y / 2f;
            Check(MathF.Abs(top.pos.X - 0.125f) < 1e-4f && MathF.Abs(top.pos.Z - 0.125f) < 1e-4f && MathF.Abs(top.pos.Y - (half + 0.125f)) < 0.01f && top.rotX > 0.7f,
                  $"{n}: the top port on the top face centre (the axis, at the face), facing up");
            if (!r.Nozzle)
                Check(MathF.Abs(bot.pos.X - 0.125f) < 1e-4f && MathF.Abs(bot.pos.Y - (-half + 0.125f)) < 0.01f && bot.rotX < -0.7f, $"{n}: the bottom port on the bottom face centre, facing down");
            else
                Check(bot.type == null, $"{n}: no bottom port (the bell)");
            var sc = V($"{n}/{n}_ServerComposition.partialdef").GetProperty("Manipulator").GetProperty("Components");
            var keys = sc.GetProperty("Keys").EnumerateArray().Select(x => x.GetString()).ToList();
            var removed = sc.GetProperty("Removed").EnumerateArray().Select(x => x.GetString()).ToList();
            var changed = sc.GetProperty("Changed").EnumerateArray().ToList();
            bool HasType(string t) => changed.Any(c => c.GetProperty("Value").TryGetProperty("Type", out var ty) && ty.GetString() == t);
            bool Uses(string def) => changed.Any(c => c.GetProperty("Value").TryGetProperty("Definition", out var df) && df.GetString() == def);
            Check(keys.Contains(Breakable) && !removed.Contains(Breakable), $"{n}: keeps its BreakableBlockComponent (a two-port block without one becomes a conveyor LINE and its tank leaves the groups)");
            Check(Uses(convGuid) && removed.Count == 0, $"{n}: a ConveyorComponent with the solid conveyor definition, nothing removed");
            var fbd = V($"{n}/{n}_PowerableBlockDefinition.partialdef").GetProperty("Manipulator");
            Check(fbd.GetProperty("UIData").GetProperty("Name").GetString().StartsWith("Solid Rocket "), $"{n}: its name");
            var col = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, n, "Models", "NonFractured", $"{n}_collision.gltf"))).RootElement.GetProperty("nodes").EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToList();
            var mounts = col.Where(x => x.StartsWith("mountpoints_")).Select(x => x.Substring(x.LastIndexOf('_') + 1)).OrderBy(x => x).ToList();
            Check(string.Join(",", mounts) == (r.Nozzle ? "PZ" : "MZ,PZ"), $"{n}: mount faces {(r.Nozzle ? "Up only" : "Up and Down")} ({string.Join(",", mounts)}; never the sides)");
            if (!r.Nozzle)
            {
                Check(HasType(TConveyor) && HasType(TTank), $"{n}: a ConveyorComponent and a ResourceContainerComponent (its tank) on the server");
                var tank = V($"{n}/{n}_ResourceContainerDefinition.def");
                float cap = tank.GetProperty("MaxCapacity").GetSingle(), dis = tank.GetProperty("MaxDischargeRate").GetSingle();
                Check(tank.GetProperty("ResourceType").GetString() == resGuid && MathF.Abs(cap - SolidRocket.FullSegment(r.Thrust)) < 0.1f && MathF.Abs(cap - r.Propellant) < 1e-3f,
                      $"{n}: a Solid Propellant tank of one full segment ({cap:F1} L = kg)");
                Check(tank.GetProperty("MaxChargingRate").GetSingle() == 0f, $"{n}: charging rate 0 - never refilled (no conveyor, no player can fill it)");
                Check(MathF.Abs(dis - SolidRocket.TankDischargeRate(r.Thrust)) < 0.01f && dis >= SolidRocket.GameRate(r.Thrust) * SolidRocket.MaxSharers - 0.01f,
                      $"{n}: discharge {dis:F1} L/s - two nozzles' full rate (the peak burn at 3x, from both ends)");
                Check(!tank.GetProperty("InfiniteSourceInCreative").GetBoolean() && tank.GetProperty("ChargeLevelInCreative").ValueKind == JsonValueKind.Null, $"{n}: creative and NPC grids drain it too (InfiniteSourceInCreative false)");
                var sp = V($"{n}/{n}_Server.partialdef").GetProperty("Manipulator").GetProperty("_entity").GetProperty("ObjectBuilders").GetProperty("Changed").EnumerateArray()
                    .Select(c => c.GetProperty("Value")).FirstOrDefault(v => v.ValueKind == JsonValueKind.Object && v.GetProperty("$Type").GetString().EndsWith("ResourceContainerObjectBuilder"));
                Check(sp.ValueKind == JsonValueKind.Object && sp.GetProperty("CurrentChargeLevel").GetSingle() == 1f && sp.GetProperty("Mode").GetString() == "Auto", $"{n}: built FULL (its server prefab's charge level 1, Auto)");
                var cc = V($"{n}/{n}_ClientComposition.partialdef").GetProperty("Manipulator").GetProperty("Components").GetProperty("Changed").EnumerateArray().ToList();
                Check(cc.Any(c => c.GetProperty("Value").TryGetProperty("Type", out var ty) && ty.GetString() == TConveyor) && !cc.Any(c => c.GetProperty("Value").TryGetProperty("Type", out var ty) && ty.GetString() == TTank),
                      $"{n}: the client copy has the conveyor, not the tank (the game's tanks are server-only)");
                Check(fbd.GetProperty("ConsumedResource").ValueKind == JsonValueKind.Null, $"{n}: no power (always supplied)");
            }
            else
            {
                var thr = V($"{n}/{n}_ThrusterDefinition.partialdef");
                var man = thr.GetProperty("Manipulator");
                Check(thr.GetProperty("BaseDefinition").GetString() == "2fa7936c-0ecb-4294-b709-c772eb3d5c09" && MathF.Abs(man.GetProperty("ResourcesRequiredToThrust").GetSingle() - SolidRocket.GameRate(r.Thrust)) < 0.01f
                      && MathF.Abs(man.GetProperty("ThrustPower").GetSingle() - r.Thrust * 1.1f * SolidRocket.MaxStackFactor) < 2f && man.GetProperty("ThrustDirection").GetString() == "Up",
                      $"{n}: the vanilla hydrogen thruster's chain, burning {SolidRocket.GameRate(r.Thrust):F1} L/s while it fires (SolidRocket.GameRate), ThrustPower = its vacuum thrust x {SolidRocket.MaxStackFactor}, pushing Up");
                Check(fbd.GetProperty("ConsumedResource").GetProperty("Type").GetString() == resGuid, $"{n}: it consumes Solid Propellant (needs it present, as a hydrogen thruster hydrogen)");
                var fl = dummies.FirstOrDefault(dd => dd.GetProperty("Name").GetString() == "thruster");
                Check(fl.ValueKind == JsonValueKind.Object && fl.GetProperty("Transform").GetProperty("Transform").GetProperty("Position").GetProperty("Y").GetSingle() < 0f, $"{n}: the flame dummy below the centre (the bell's exit)");
            }
        }
        // the mod's side: the tank read (never written), the mass from it, one mass provider, the put-backs
        string sim = SimDir();
        string seg = File.ReadAllText(Path.Combine(sim, "Propulsion", "AeroSolidSegmentComponent.cs"));
        Check(seg.Contains("_charge = MathF.Max(0f, (float)(double)_rc.CurrentChargeValue);") && seg.Contains("public float DynamicMass => Propellant;") && seg.Contains("massComputer.OnBlockMassChanged(_cb)"),
              "the segment's mass: the tank's charge (1 L = 1 kg) as its dynamic mass, the grid told when it moves");
        Check(seg.Contains("if (_rc.Mode != ContainerMode.Auto) { _rc.Mode = ContainerMode.Auto;") && seg.Contains("if (_pb != null && !_pb.Enabled) { _pb.Enabled = true;"),
              "no backdoor shutdown: a tank set to Charge goes back to Auto, a segment switched off back on");
        bool writes = Directory.GetFiles(sim, "*.cs", SearchOption.AllDirectories).Any(f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f), @"CurrentChargeValue\s*[+\-]?=[^=]"));
        Check(!writes, "no refill path: no mod code writes a tank's charge");
        string inj = File.ReadAllText(Path.Combine(sim, "Injection", "EntityInjector.cs"));
        Check(inj.Contains("if (hasTank && !SolidRocketBlocks.SegmentServerPrefabs.Contains(entity.Guid))"), "the segments' tanks are left out of AeroFuelMassComponent (one mass provider)");
    }

    static string SimDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "Scripts", "Simulation"))) d = d.Parent;
        return d == null ? "" : Path.Combine(d.FullName, "Scripts", "Simulation");
    }
}
