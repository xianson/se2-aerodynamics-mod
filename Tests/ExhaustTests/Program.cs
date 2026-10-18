#pragma warning disable
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AeroMod;

/// <summary>ExhaustTests: the ramjets' and solid rocket nozzles' exhaust offline (exit 0 = all pass) - the law (Fx/ExhaustLaw.cs, the
/// literal source) and the effects' content gen_propulsor_defs.py (gen_exhaust) wrote into the Blocks project.</summary>
static class Program
{
    static int _checks, _fails;
    static void Check(bool ok, string what) { _checks++; if (!ok) { _fails++; Console.WriteLine("FAIL " + what); } else if (Environment.GetEnvironmentVariable("VERBOSE") != null) Console.WriteLine("ok   " + what); }
    const float Dt = 1f / 60f;

    static int Main()
    {
        RamjetGate();
        Diamonds();
        Unstart();
        SolidPlume();
        Vacuum();
        Burnout();
        SmokeCap();
        Cost();
        Content();
        Console.WriteLine($"ExhaustTests: {_checks - _fails}/{_checks} passed");
        return _fails == 0 ? 0 : 1;
    }

    static ExhaustLaw.RamjetOut RunRamjet(ref ExhaustLaw.RamjetState s, float mach, float share, float factor, bool unstarted, float seconds)
    {
        var i = new ExhaustLaw.RamjetIn { Mach = mach, Share = share, Factor = factor, Unstarted = unstarted, AeroOn = true };
        var o = default(ExhaustLaw.RamjetOut);
        for (float t = 0f; t < seconds; t += Dt) ExhaustLaw.Ramjet(ref s, in i, Dt, ref o);
        return o;
    }

    // ── 1. the ramjet: nothing below Mach 0.5; it lights up as the thrust builds ──
    static void RamjetGate()
    {
        foreach (float m in new[] { 0f, 0.2f, 0.4f, 0.49f, 0.4999f, 0.5f })
        {
            Check(ExhaustLaw.RamjetStrength(m, 1f, 1f, true) == 0f, $"ramjet: no flame at Mach {m} (share 1, factor 1)");
            var s = default(ExhaustLaw.RamjetState);
            var o = RunRamjet(ref s, m, 1f, 1f, false, 2f);
            Check(o.Flame == 0f && o.FlameLength == 0f && o.Diamonds == 0, $"ramjet: nothing drawn at Mach {m} after 2 s at full share");
        }
        Check(ExhaustLaw.RamjetStrength(float.NaN, 1f, 1f, true) == 0f, "ramjet: NaN Mach - no flame");
        Check(ExhaustLaw.RamjetStrength(0.55f, 1f, 1f, true) > 0f && ExhaustLaw.RamjetStrength(0.55f, 1f, 1f, true) < ExhaustLaw.RamjetStrength(0.65f, 1f, 1f, true), "ramjet: gated in between Mach 0.5 and 0.65");
        Check(MathF.Abs(ExhaustLaw.RamjetStrength(2f, 0.7f, 0.8f, true) - 0.56f) < 1e-5f, "ramjet: strength = share x its thrust factor past the gate");
        Check(ExhaustLaw.RamjetStrength(3f, 1f, 1.3f, true) == 1f, "ramjet: at most 1 (dense air at Mach 3)");
        // the thrust builds: monotone in share and in factor, and the length with it
        float last = -1f, lastLen = -1f; bool mono = true;
        for (float sh = 0f; sh <= 1.0001f; sh += 0.05f)
        {
            float st = ExhaustLaw.RamjetStrength(2.5f, sh, 1f, true), len = ExhaustLaw.RamjetLength(st);
            if (st < last - 1e-6f || len < lastLen - 1e-6f) mono = false;
            last = st; lastLen = len;
        }
        Check(mono, "ramjet: strength and flame length never fall as the thrust share rises");
        Check(ExhaustLaw.RamjetLength(0f) == 0f && ExhaustLaw.RamjetLength(1f) == ExhaustLaw.RamjetLenMax, "ramjet: no length at none, RamjetLenMax at full");
        // lights up with the thrust: from cold at Mach 2.5 the drawn flame rises (eased), it does not pop to full
        var s2 = default(ExhaustLaw.RamjetState);
        var o1 = RunRamjet(ref s2, 2.5f, 1f, 1f, false, Dt);
        var o2 = RunRamjet(ref s2, 2.5f, 1f, 1f, false, 0.1f);
        var o3 = RunRamjet(ref s2, 2.5f, 1f, 1f, false, 1f);
        Check(o1.Flame > 0f && o1.Flame < 0.5f && o2.Flame > o1.Flame && o3.Flame > 0.98f, $"ramjet: lights up as the thrust builds ({o1.Flame:F3} -> {o2.Flame:F3} -> {o3.Flame:F3})");
        // aero off: a plain hydrogen thruster - its share, no Mach gate, no diamonds
        Check(ExhaustLaw.RamjetStrength(0f, 0.6f, 0f, false) == 0.6f, "ramjet: aero off - its share, no Mach gate");
        Check(ExhaustLaw.Ease(0f, 1f, 0.05f) > 1f - ExhaustLaw.Ease(1f, 0f, 0.05f), "easing: it lights faster than it dies");
    }

    // ── 2. shock diamonds: none subsonic, more / sharper / brighter with the Mach, the spacing growing ──
    static void Diamonds()
    {
        foreach (float m in new[] { 0.6f, 0.9f, 1.0f, ExhaustLaw.DiamondMach0 })
            Check(ExhaustLaw.DiamondCount(m, 1f) == 0, $"diamonds: none at Mach {m}");
        Check(ExhaustLaw.DiamondCount(ExhaustLaw.DiamondMach0 + 0.05f, 1f) >= 1, "diamonds: the first just past DiamondMach0");
        Check(ExhaustLaw.DiamondCount(ExhaustLaw.DiamondMach1, 1f) == ExhaustLaw.DiamondMax && ExhaustLaw.DiamondCount(6f, 1f) == ExhaustLaw.DiamondMax, "diamonds: all DiamondMax from DiamondMach1");
        int lastN = -1; float lastSp = -1f, lastSc = 9f, lastSt = -1f; bool more = true, wider = true, sharper = true, brighter = true;
        var counts = new HashSet<int>();
        for (float m = 1.0f; m <= 6f; m += 0.05f)
        {
            int n = ExhaustLaw.DiamondCount(m, 1f); float sp = ExhaustLaw.DiamondSpacing(m), sc = ExhaustLaw.DiamondScale(m), st = ExhaustLaw.DiamondStrength(m, 1f);
            if (n < lastN) more = false; if (sp < lastSp - 1e-6f) wider = false; if (sc > lastSc + 1e-6f) sharper = false; if (st < lastSt - 1e-6f) brighter = false;
            lastN = n; lastSp = sp; lastSc = sc; lastSt = st; counts.Add(n);
        }
        Check(more && counts.Count == ExhaustLaw.DiamondMax + 1, $"diamonds: they multiply with the Mach (0..{ExhaustLaw.DiamondMax}, each count reached: {string.Join(",", counts.OrderBy(x => x))})");
        Check(wider && ExhaustLaw.DiamondSpacing(3f) > ExhaustLaw.DiamondSpacing(1.5f), "diamonds: the shock cells lengthen with the Mach (sqrt(M^2 - 1))");
        Check(sharper && ExhaustLaw.DiamondScale(4f) < ExhaustLaw.DiamondScale(1.5f), "diamonds: sharper (smaller knots) with the Mach");
        Check(brighter && ExhaustLaw.DiamondStrength(4f, 1f) > ExhaustLaw.DiamondStrength(1.5f, 1f), "diamonds: brighter with the Mach");
        Check(ExhaustLaw.DiamondCount(3f, ExhaustLaw.DiamondMinStrength * 0.9f) == 0, "diamonds: none while the flame is weak");
        var o = new ExhaustLaw.RamjetOut { Diamonds = 3, DiamondStrength = 0.8f, DiamondSpacing = 2f, DiamondFirst = 1f };
        Check(ExhaustLaw.KnotStrength(in o, 0) == 0.8f && ExhaustLaw.KnotStrength(in o, 1) < 0.8f && ExhaustLaw.KnotStrength(in o, 2) < ExhaustLaw.KnotStrength(in o, 1) && ExhaustLaw.KnotStrength(in o, 3) == 0f,
              "diamonds: each fainter down the plume, none past the count");
        Check(ExhaustLaw.KnotDistance(in o, 0) == 1f && ExhaustLaw.KnotDistance(in o, 2) == 5f, "diamonds: placed DiamondFirst + k x spacing aft");
        var s = default(ExhaustLaw.RamjetState);
        var full = RunRamjet(ref s, 3.5f, 1f, 1f, false, 2f);
        Check(full.Diamonds == ExhaustLaw.DiamondCount(3.5f, full.Flame) && full.Diamonds >= 4, $"diamonds: a full ramjet at Mach 3.5 shows {full.Diamonds}");
    }

    // ── 3. the inlet unstart: a pop, a sputter, out until it relights; back with the thrust ──
    static void Unstart()
    {
        var s = default(ExhaustLaw.RamjetState);
        var lit = RunRamjet(ref s, 3f, 1f, 1f, false, 2f);
        var i = new ExhaustLaw.RamjetIn { Mach = 3f, Share = 1f, Factor = 0.02f, Unstarted = true, AeroOn = true };   // (the inlet's residual: 2 %)
        var o = default(ExhaustLaw.RamjetOut);
        ExhaustLaw.Ramjet(ref s, in i, Dt, ref o);
        Check(o.Glow == ExhaustLaw.PopGain && o.Flame >= lit.Flame * 0.99f && o.Diamonds == 0, $"unstart: a POP - the flame it had ({o.Flame:F2}), x{o.Glow} bright, no diamonds");
        Check(s.Unstarts == 1, "unstart: counted once");
        float t = Dt; int flips = 0; bool wasOn = true, anyOn = false; float maxAfter = 0f;
        while (t < ExhaustLaw.PopSeconds + ExhaustLaw.UnstartSputterSeconds)
        {
            ExhaustLaw.Ramjet(ref s, in i, Dt, ref o); t += Dt;
            if (t < ExhaustLaw.PopSeconds) continue;
            bool on = o.Flame > 0.1f;
            if (on != wasOn) flips++;
            wasOn = on; anyOn |= on;
            Check(o.Glow == 1f, "unstart: the glow only through the pop");
        }
        Check(flips >= 2 && anyOn, $"unstart: it SPUTTERS after the pop ({flips} on / off changes)");
        for (float u = 0f; u < 1.5f; u += Dt) { ExhaustLaw.Ramjet(ref s, in i, Dt, ref o); maxAfter = MathF.Max(maxAfter, o.Flame); }
        Check(maxAfter == 0f && o.FlameLength == 0f && o.Diamonds == 0, "unstart: then the flame is OUT while the inlet stays unstarted");
        // relit: the inlet spools back (its factor rises) - the flame comes back from nothing, with the thrust
        var j = new ExhaustLaw.RamjetIn { Mach = 3f, Share = 1f, Factor = 0.1f, Unstarted = false, AeroOn = true };
        ExhaustLaw.Ramjet(ref s, in j, Dt, ref o);
        Check(o.Flame < 0.1f && o.Glow == 1f, "relight: no jump - it comes back from nothing");
        float prev = o.Flame; bool rising = true;
        for (float f = 0.1f; f <= 1f; f += 0.03f) { j.Factor = f; ExhaustLaw.Ramjet(ref s, in j, Dt, ref o); if (o.Flame < prev - 1e-6f) rising = false; prev = o.Flame; }
        Check(rising && o.Flame > 0.5f, $"relight: the flame rises with the spooling thrust (to {o.Flame:F2})");
        // a second unstart: counted again
        j.Unstarted = true; ExhaustLaw.Ramjet(ref s, in j, Dt, ref o);
        Check(s.Unstarts == 2 && o.Glow == ExhaustLaw.PopGain, "unstart: every unstart pops");
    }

    static ExhaustLaw.SolidOut RunSolid(ref ExhaustLaw.SolidState s, float ratio, float p, float seconds, float speed = 0f)
    {
        var i = new ExhaustLaw.SolidIn { Ratio = ratio, Pressure = p, Speed = speed, Scale = 1f };
        var o = default(ExhaustLaw.SolidOut);
        for (float t = 0f; t < seconds; t += Dt) ExhaustLaw.Solid(ref s, in i, Dt, ref o);
        return o;
    }

    // ── 4. the solid plume: its length follows the thrust (stack factor x level x steering) ──
    static void SolidPlume()
    {
        Check(ExhaustLaw.PlumeLength(0f) == 0f && ExhaustLaw.PlumeStrength(0f) == 0f, "solid: no plume unlit");
        float last = 0f; bool mono = true;
        for (float r = 0.05f; r <= 3.3f; r += 0.05f) { float l = ExhaustLaw.PlumeLength(r); if (l < last - 1e-6f) mono = false; last = l; }
        Check(mono, "solid: plume length never falls as the thrust rises");
        float one = ExhaustLaw.PlumeLength(1f), two = ExhaustLaw.PlumeLength(MathF.Sqrt(2f)), nine = ExhaustLaw.PlumeLength(3f), half = ExhaustLaw.PlumeLength(0.5f), steer = ExhaustLaw.PlumeLength(0.75f);
        Check(half < steer && steer < one && one < two && two < nine, $"solid: level 50 % {half:F1} < steered 75 % {steer:F1} < 1 segment {one:F1} < 2 segments {two:F1} < 9 segments {nine:F1} radii");
        Check(MathF.Abs(one - ExhaustLaw.PlumeLenPerRadius) < 1e-4f, "solid: one segment at full = PlumeLenPerRadius radii");
        Check(ExhaustLaw.PlumeLength(1e6f) == ExhaustLaw.PlumeLenMaxRadii && ExhaustLaw.PlumeLength(float.NaN) == 0f, "solid: capped; NaN none");
        Check(ExhaustLaw.PlumeStrength(3.3f) == 1f && ExhaustLaw.PlumeStrength(1f) > 0.6f && ExhaustLaw.PlumeStrength(0.5f) < ExhaustLaw.PlumeStrength(1f), "solid: strength grows with the thrust, 1 at a full-throat stack");
        var s = default(ExhaustLaw.SolidState);
        var o = RunSolid(ref s, 1f, 1f, 1f);
        Check(MathF.Abs(o.PlumeLength - one) < 0.05f && o.Plume > 0.6f, $"solid: lit 1 s at one segment: the plume {o.PlumeLength:F2} radii");
        var o2 = RunSolid(ref s, 0.6f, 1f, 1f);
        Check(o2.PlumeLength < o.PlumeLength, "solid: the steering down shortens it");
    }

    // ── 5. thin air / vacuum: the plume balloons, the smoke thins away ──
    static void Vacuum()
    {
        var s = default(ExhaustLaw.SolidState);
        float lastBal = -1f, lastScale = -1f, lastSmoke = 9f, lastAlpha = 9f, lastCore = 9f; bool ok = true;
        for (float p = 1f; p >= -0.0001f; p -= 0.05f)
        {
            var o = RunSolid(ref s, 1f, MathF.Max(0f, p), 0.5f);
            if (o.Balloon < lastBal - 1e-6f || o.BalloonScale < lastScale - 1e-6f || o.Smoke > lastSmoke + 1e-6f || o.SmokeAlpha > lastAlpha + 1e-6f || o.CoreAlpha > lastCore + 1e-6f) ok = false;
            lastBal = o.Balloon; lastScale = o.BalloonScale; lastSmoke = o.Smoke; lastAlpha = o.SmokeAlpha; lastCore = o.CoreAlpha;
        }
        Check(ok, "vacuum: as the air thins the balloon grows and widens, the smoke and the core thin - never the other way");
        var sea = RunSolid(ref s, 1f, 1f, 0.5f);
        var vac = RunSolid(ref s, 1f, 0f, 0.5f);
        Check(sea.Balloon == 0f && sea.BalloonScale == 1f && sea.Smoke > 0.5f && sea.SmokeAlpha == 1f && sea.CoreAlpha == 1f, "sea level: no balloon, thick smoke, the full core");
        Check(vac.Balloon > 0.5f && vac.BalloonScale == 1f + ExhaustLaw.BalloonWiden && vac.Smoke == 0f && vac.CoreAlpha == ExhaustLaw.VacuumCoreAlpha, $"vacuum: the plume balloons (x{vac.BalloonScale}), no smoke, the core dimmed");
        Check(ExhaustLaw.SmokeStrength(1f, ExhaustLaw.SmokeMinPressure * 0.9f) == 0f && ExhaustLaw.SmokeStrength(1f, ExhaustLaw.SmokeMinPressure * 1.5f) > 0f, "thin air: no smoke below SmokeMinPressure");
        Check(vac.PlumeLength == sea.PlumeLength, "vacuum: the plume's length is the thrust's (the balloon is the widening)");
        var fast = RunSolid(ref s, 1f, 1f, 0.2f, 600f);
        Check(fast.SmokeStretch > sea.SmokeStretch && fast.SmokeStretch <= ExhaustLaw.SmokeStretchMax && sea.SmokeStretch == 1f, $"smoke: stretched along the flight at speed ({fast.SmokeStretch:F1}), 1 at rest");
    }

    // ── 6. burnout: a quick sputter, then nothing ──
    static void Burnout()
    {
        var s = default(ExhaustLaw.SolidState);
        var lit = RunSolid(ref s, 1.4f, 1f, 2f);
        var i = new ExhaustLaw.SolidIn { Ratio = 0f, Pressure = 1f, Scale = 1f };
        var o = default(ExhaustLaw.SolidOut);
        int flips = 0; bool was = true; float t = 0f, maxAfter = 0f;
        while (t < ExhaustLaw.BurnoutSputterSeconds) { ExhaustLaw.Solid(ref s, in i, Dt, ref o); t += Dt; bool on = o.Plume > 0.1f; if (on != was) flips++; was = on; }
        Check(s.Burnouts == 1, "burnout: seen once");
        Check(flips >= 2, $"burnout: a quick SPUTTER ({flips} on / off changes in {ExhaustLaw.BurnoutSputterSeconds} s)");
        for (float u = 0f; u < 1f; u += Dt) { ExhaustLaw.Solid(ref s, in i, Dt, ref o); maxAfter = MathF.Max(maxAfter, o.Plume + o.Balloon + o.Smoke); }
        Check(maxAfter == 0f && !s.Sputtering, "burnout: then nothing (no plume, balloon or smoke)");
        float peak = 0f;
        for (float u = 0f; u < ExhaustLaw.BurnoutSputterSeconds; u += 0.001f) peak = MathF.Max(peak, ExhaustLaw.Sputter(u, ExhaustLaw.BurnoutSputterSeconds, ExhaustLaw.SputterHz));
        Check(peak <= 1f && ExhaustLaw.Sputter(ExhaustLaw.BurnoutSputterSeconds, ExhaustLaw.BurnoutSputterSeconds, 22f) == 0f && ExhaustLaw.Sputter(-1f, 1f, 22f) == 0f, "sputter: never above the thrust it had, 0 outside its time");
    }

    // ── 7. the smoke's lifetime and particle cap ──
    static void SmokeCap()
    {
        bool lifeOk = true, capOk = true, mono = true; float last = -1f;
        for (float s = 0f; s <= 1.0001f; s += 0.01f)
        {
            float life = ExhaustLaw.SmokeLife(s), n = ExhaustLaw.SmokeParticles(s);
            if (life > ExhaustBlocks.SmokeLifeMax + 1e-5f || life < ExhaustBlocks.SmokeLifeMin - 1e-5f) lifeOk = false;
            if (n > ExhaustBlocks.SmokeCap) capOk = false;
            if (n < last - 1e-4f) mono = false; last = n;
        }
        Check(lifeOk, $"smoke: a particle lives {ExhaustBlocks.SmokeLifeMin}..{ExhaustBlocks.SmokeLifeMax} s, never longer");
        Check(capOk && mono, $"smoke: at most {ExhaustBlocks.SmokeCap} particles per nozzle at any strength (full: {ExhaustLaw.SmokeParticles(1f):F0})");
        float was = ExhaustLaw.SmokeMax;
        ExhaustLaw.SmokeMax = 0.5f;
        var st = default(ExhaustLaw.SolidState);
        var o = RunSolid(ref st, 3.3f, 1f, 1f);
        Check(o.Smoke <= 0.5f && ExhaustLaw.SmokeLife(o.Smoke) < ExhaustBlocks.SmokeLifeMax, "smoke: the SmokeMax knob caps its strength - and so its lifetime");
        ExhaustLaw.SmokeMax = was;
    }

    // ── 8. the cost per engine: no garbage per step, a few hundred ns ──
    static void Cost()
    {
        var rs = default(ExhaustLaw.RamjetState); var ro = default(ExhaustLaw.RamjetOut);
        var ss = default(ExhaustLaw.SolidState); var so = default(ExhaustLaw.SolidOut);
        var ri = new ExhaustLaw.RamjetIn { Mach = 3f, Share = 0.8f, Factor = 1f, AeroOn = true };
        var si = new ExhaustLaw.SolidIn { Ratio = 1.4f, Pressure = 0.6f, Speed = 300f, Scale = 1f };
        for (int k = 0; k < 2000; k++) { ExhaustLaw.Ramjet(ref rs, in ri, Dt, ref ro); ExhaustLaw.Solid(ref ss, in si, Dt, ref so); }
        const int N = 200_000;
        var sw = System.Diagnostics.Stopwatch.StartNew();   // (before the count: the Stopwatch itself is an allocation)
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        float sink = 0f;
        for (int k = 0; k < N; k++)
        {
            ri.Mach = 2f + (k & 63) * 0.03f; ri.Unstarted = (k & 1023) < 40;
            ExhaustLaw.Ramjet(ref rs, in ri, Dt, ref ro);
            for (int d = 0; d < ExhaustLaw.DiamondMax; d++) sink += ExhaustLaw.KnotStrength(in ro, d) + ExhaustLaw.KnotDistance(in ro, d);
            sink += ExhaustLaw.Flicker(k * Dt, 7);
            si.Ratio = (k & 2047) < 1900 ? 1.4f : 0f;
            ExhaustLaw.Solid(ref ss, in si, Dt, ref so);
            sink += so.Plume + ro.Flame;
        }
        sw.Stop();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - a0;
        double ns = sw.Elapsed.TotalMilliseconds * 1e6 / N;
        Console.WriteLine($"  cost: one ramjet (+ {ExhaustLaw.DiamondMax} knots) and one solid nozzle's law per frame: {ns:F0} ns together, {bytes} bytes allocated over {N} frames (sink {sink:F0})");
        Check(bytes == 0, $"cost: no allocation in the law's per-frame step ({bytes} bytes over {N} frames)");
        Check(ns < 5000, $"cost: the per-frame law is cheap ({ns:F0} ns for two engines)");
    }

    // ── 9. the content gen_propulsor_defs.py wrote (gen_exhaust) ──
    static string SimDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "Scripts", "Simulation"))) d = d.Parent;
        return d == null ? "" : Path.Combine(d.FullName, "Scripts", "Simulation");
    }
    const string VanillaParticles = @"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers2\GameData\Vanilla\Content\System\Particles";
    static readonly Regex Guid36 = new(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$");

    static void Strings(JsonElement e, string name, List<(string key, string value)> into)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) Strings(p.Value, p.Name, into); break;
            case JsonValueKind.Array: foreach (var x in e.EnumerateArray()) Strings(x, name, into); break;
            case JsonValueKind.String: into.Add((name, e.GetString())); break;
        }
    }
    static List<(float key, JsonElement value)> Line(JsonElement timeline) =>
        timeline.GetProperty("KeyFrames").GetProperty("_data").EnumerateArray().Select(k => (k.GetProperty("Value").GetProperty("Key").GetSingle(), k.GetProperty("Value").GetProperty("Value"))).ToList();

    static void Content()
    {
        string root = Path.GetFullPath(Path.Combine(SimDir(), "..", "..", "Assets", "Aero", "Propulsors"));
        string ex = Path.Combine(root, "Exhaust");
        if (!Directory.Exists(ex)) { Console.WriteLine("   (no Blocks project exhaust: content checks skipped)"); return; }
        var defs = new Dictionary<string, (string file, JsonElement doc)>();
        foreach (var f in Directory.GetFiles(ex, "*.def"))
        {
            var doc = JsonDocument.Parse(File.ReadAllText(f)).RootElement;
            string g = doc.GetProperty("$Value").GetProperty("Guid").GetString();
            Check(!defs.ContainsKey(g), $"content: {Path.GetFileName(f)} - a GUID of its own");
            defs[g] = (f, doc);
            // its meta: the same asset id, compiled as a whole definition
            var meta = JsonDocument.Parse(File.ReadAllText(f + ".meta")).RootElement.GetProperty("$Value");
            Check(meta.GetProperty("AssetID").GetString() == g && meta.GetProperty("InternalItems")[1].GetProperty("ProcessorInfo").GetProperty("Identifier").GetString() == "P_DEF",
                  $"content: {Path.GetFileName(f)}.meta - its asset id, P_DEF");
        }
        Check(defs.Count == 17, $"content: 17 definitions in Exhaust ({defs.Count}: 5 effects, 11 emitters, 1 light)");
        // the vanilla stamps per type (each copy keeps its vanilla template's: one a vanilla file of that type has)
        var stamps = new Dictionary<string, HashSet<string>>();
        var vanillaTex = new HashSet<string>();
        if (Directory.Exists(VanillaParticles))
            foreach (var f in Directory.GetFiles(VanillaParticles, "*.def", SearchOption.AllDirectories))
            {
                JsonElement d;
                try { d = JsonDocument.Parse(File.ReadAllText(f)).RootElement; } catch { continue; }
                if (!d.TryGetProperty("$Type", out var ty) || !d.TryGetProperty("$Bundles", out var b) || !b.TryGetProperty("VRage", out var vr)) continue;
                (stamps.TryGetValue(ty.GetString(), out var set) ? set : stamps[ty.GetString()] = new HashSet<string>()).Add(vr.GetString());
                var ss = new List<(string, string)>(); Strings(d, "", ss);
                foreach (var (_, v) in ss) if (v.StartsWith("{G}")) vanillaTex.Add(v);
            }
        else Console.WriteLine("   (no game install: the vanilla stamp / texture checks skipped)");
        foreach (var (g, (file, doc)) in defs)
        {
            string n = Path.GetFileName(file), type = doc.GetProperty("$Type").GetString();
            if (stamps.Count > 0) Check(stamps.TryGetValue(type, out var st) && st.Contains(doc.GetProperty("$Bundles").GetProperty("VRage").GetString()), $"content: {n} - a vanilla {type.Split('.').Last()} stamp ({doc.GetProperty("$Bundles").GetProperty("VRage").GetString()})");
            // RESOLVABLE: every definition it names is one of ours in this folder; everything else a texture file a vanilla effect uses
            var strs = new List<(string key, string value)>(); Strings(doc.GetProperty("$Value"), "", strs);
            foreach (var (key, v) in strs)
            {
                if (key == "Key" || key == "Guid") continue;
                if (Guid36.IsMatch(v)) Check(defs.ContainsKey(v), $"content: {n} - '{key}' {v} is one of ours (no vanilla definition reference: the build cannot resolve those)");
                else if (v.StartsWith("{G}") && vanillaTex.Count > 0) Check(vanillaTex.Contains(v), $"content: {n} - texture {v} is a vanilla particle texture");
            }
        }
        JsonElement V(Guid g) => defs[g.ToString()].doc.GetProperty("$Value");
        // the effects: present, looping, our duration, their emitters / lights ours
        foreach (var (name, g, emitters, lights) in new[] { ("RamjetFlame", ExhaustBlocks.RamjetFlame, 4, 0), ("RamjetDiamond", ExhaustBlocks.RamjetDiamond, 1, 0),
                                                         ("SolidPlume", ExhaustBlocks.SolidPlume, 3, 1), ("SolidBalloon", ExhaustBlocks.SolidBalloon, 1, 0), ("SolidSmoke", ExhaustBlocks.SolidSmoke, 2, 0) })
        {
            bool has = defs.ContainsKey(g.ToString());
            Check(has, $"content: the effect {name} ({g}) the code spawns is there");
            if (!has) continue;
            var v = V(g);
            Check(defs[g.ToString()].doc.GetProperty("$Type").GetString().EndsWith("ParticleEffectDefinitionObjectBuilder") && v.GetProperty("IsLooping").GetBoolean()
                  && v.GetProperty("EffectDuration").GetSingle() == ExhaustBlocks.Duration && v.GetProperty("ParticleAudio").ValueKind == JsonValueKind.Null,
                  $"content: {name} - an effect, looping, {ExhaustBlocks.Duration} s (the code's time base), no audio");
            var em = v.GetProperty("ParticleEmitters").EnumerateArray().Select(e => e.GetProperty("Value").GetProperty("Definition").GetString()).ToList();
            var li = v.GetProperty("ParticleLight").EnumerateArray().Select(e => e.GetProperty("Value").GetString()).ToList();
            Check(em.Count == emitters && em.All(e => defs.ContainsKey(e) && defs[e].doc.GetProperty("$Type").GetString().EndsWith("ParticleEmitterDefinitionObjectBuilder")), $"content: {name} - its {emitters} emitters, ours");
            Check(li.Count == lights && li.All(l => defs.ContainsKey(l) && defs[l].doc.GetProperty("$Type").GetString().EndsWith("ParticleLightDefinitionObjectBuilder")), $"content: {name} - its {lights} light(s), ours");
        }
        // every emitter: nothing at effect time 0 (dark = time 0), the plumes' speed constant (the code's length), the smoke as authored
        float vRj = ExhaustBlocks.RamjetLenAuthored * ExhaustBlocks.RefRadius / ExhaustBlocks.PlumeLife, vSp = ExhaustBlocks.SolidLenAuthored * ExhaustBlocks.RefRadius / ExhaustBlocks.PlumeLife;
        foreach (var (g, (file, doc)) in defs)
        {
            if (!doc.GetProperty("$Type").GetString().EndsWith("ParticleEmitterDefinitionObjectBuilder")) continue;
            string n = Path.GetFileName(file);
            var t = doc.GetProperty("$Value").GetProperty("TimeLines");
            var pps = Line(t.GetProperty("ParticlesPerSecond"));
            Check(pps[0].key == 0f && pps[0].value.GetSingle() == 0f, $"content: {n} - no emission at effect time 0 (dark)");
            var vel = Line(t.GetProperty("LinearVelocity"));
            Check(vel.Count == 1, $"content: {n} - its speed constant over the effect time (the plume's length is the code's VelocityMultiplier)");
            foreach (var tl in new[] { "Color", "ParticleSize", "Emissivity" })
                foreach (var (_, inner) in Line(t.GetProperty(tl)))
                    Check(inner.GetProperty("KeyFrames").GetProperty("_data").GetArrayLength() is >= 1 and <= 4, $"content: {n} - {tl}: 1-4 over-life keys (the game's limit)");
        }
        var trail = V(new Guid("ae200001-ae00-7f00-0071-000000000001"));
        var tt = trail.GetProperty("TimeLines");
        float ppsMax = Line(tt.GetProperty("ParticlesPerSecond")).Max(k => k.value.GetSingle()), lifeMax = Line(tt.GetProperty("ParticleLifeSpan")).Max(k => k.value.GetSingle()), lifeMin = Line(tt.GetProperty("ParticleLifeSpan")).Min(k => k.value.GetSingle());
        Check(ppsMax == ExhaustBlocks.SmokePpsMax && lifeMax == ExhaustBlocks.SmokeLifeMax && lifeMin == ExhaustBlocks.SmokeLifeMin, $"content: the smoke trail as the table says (rate {ppsMax}/s, life {lifeMin}..{lifeMax} s)");
        Check(!trail.GetProperty("EmitterFlags").GetProperty("EnableLocalSpaceSimulation").GetBoolean() && trail.GetProperty("SimulationParameters").GetProperty("EmitterMotionInheritance").GetSingle() == 0f,
              "content: the smoke trail in world space, no motion inherited - it stays where it was laid and lingers");
        var billow = V(new Guid("ae200001-ae00-7f00-0072-000000000001")).GetProperty("TimeLines");
        float bpps = Line(billow.GetProperty("ParticlesPerSecond")).Max(k => k.value.GetSingle()), blife = Line(billow.GetProperty("ParticleLifeSpan")).Max(k => k.value.GetSingle());
        Check(ppsMax * lifeMax + bpps * blife <= ExhaustBlocks.SmokeCap && bpps == ExhaustBlocks.BillowPpsMax && blife == ExhaustBlocks.BillowLife, $"content: one nozzle's smoke at most {ppsMax * lifeMax + bpps * blife:F0} <= {ExhaustBlocks.SmokeCap} particles");
        var knot = V(new Guid("ae200001-ae00-7f00-0066-000000000001"));
        Check(knot.GetProperty("EmitterFlags").GetProperty("EnableLocalSpaceSimulation").GetBoolean() && Line(knot.GetProperty("TimeLines").GetProperty("LinearVelocity"))[0].value.GetSingle() == 0f,
              "content: a shock-diamond knot held still in the engine's frame (local space, no speed)");
        Check(MathF.Abs(Line(V(new Guid("ae200001-ae00-7f00-0061-000000000001")).GetProperty("TimeLines").GetProperty("LinearVelocity"))[0].value.GetSingle() - vRj) < 1e-3f
              && MathF.Abs(Line(V(new Guid("ae200001-ae00-7f00-0069-000000000001")).GetProperty("TimeLines").GetProperty("LinearVelocity"))[0].value.GetSingle() - vSp) < 1e-3f,
              "content: the main flames' speed = the authored length x RefRadius / PlumeLife (the code's length law)");
        var light = V(new Guid("ae200001-ae00-7f00-006c-000000000001"));
        Check(light.GetProperty("FlareDefinition").ValueKind == JsonValueKind.Null && Line(light.GetProperty("TimeLines").GetProperty("Intensity")).All(k => k.value.GetSingle() <= 1000f),
              "content: the plume's light - no flare definition (a vanilla reference), intensity at most 1000 (the game's validation)");
        // the table: four ramjets and four solid nozzles, each a block of ours, its exhaust where its flame dummy / spec puts it
        Check(ExhaustBlocks.Rows.Count == 8 && ExhaustBlocks.Rows.Count(r => r.Kind == ExhaustBlocks.Kinds.Ramjet) == 4, "table: 4 ramjets and 4 solid nozzles");
        foreach (var r in ExhaustBlocks.Rows)
        {
            Check(ExhaustBlocks.RowOf(r.Block) == r, $"table: {r.Name} found by its block GUID");
            string fbd = Path.Combine(root, r.Name, $"{r.Name}_PowerableBlockDefinition.partialdef");
            if (!File.Exists(fbd)) { Check(false, $"table: {r.Name} - its block definition is there"); continue; }
            var man = JsonDocument.Parse(File.ReadAllText(fbd)).RootElement.GetProperty("$Value").GetProperty("Manipulator");
            Check(man.GetProperty("Guid").GetString() == r.Block.ToString(), $"table: {r.Name} - its block GUID is the block definition's");
            Check(MathF.Abs(r.Axis.Length() - 1f) < 1e-4f && r.ExitRadius > 0.05f && r.ExitRadius < 3.5f, $"table: {r.Name} - a unit axis, an exit radius ({r.ExitRadius} m)");
            // the turn sends the effect's +Z down the axis
            var z = Vector3.Transform(Vector3.UnitZ, r.Turn);
            Check((z - r.Axis).Length() < 1e-3f, $"table: {r.Name} - its turn points the effect's +Z down the plume axis");
            var flame = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, r.Name, $"{r.Name}_ThrusterEffectsComponentDefinition.def"))).RootElement.GetProperty("$Value");
            Check(!flame.TryGetProperty("OnThrustPoweredParticleEffect", out var fx) || fx.ValueKind == JsonValueKind.Null, $"table: {r.Name} - the game's flame carries no particle effect (ours draws)");
            var dummies = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, r.Name, $"{r.Name}_BlockModelDefinition.partialdef"))).RootElement.GetProperty("$Value").GetProperty("Manipulator")
                .GetProperty("Dummies").GetProperty("Changed").EnumerateArray().Select(c => c.GetProperty("Value")).ToList();
            var th = dummies.FirstOrDefault(d => d.TryGetProperty("Name", out var nm) && nm.GetString() == "thruster");
            if (th.ValueKind != JsonValueKind.Object) { Check(false, $"table: {r.Name} - its flame dummy"); continue; }
            var tp = th.GetProperty("Transform").GetProperty("Transform").GetProperty("Position");
            var dp = new Vector3(tp.GetProperty("X").GetSingle(), tp.GetProperty("Y").GetSingle(), tp.GetProperty("Z").GetSingle());
            // (a nozzle's flame dummy IS its exhaust; a ramjet's sits 0.1 cell inside its can)
            Check((dp - r.Exhaust).Length() < (r.Kind == ExhaustBlocks.Kinds.Solid ? 1e-3f : 0.3f), $"table: {r.Name} - the exhaust {r.Exhaust} at its flame dummy {dp}");
        }
    }
}
