#pragma warning disable
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using AeroMod;
using Keen.Game2.Simulation.GameSystems;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Reflection;
using Keen.VRage.Library.Serialization;
using Keen.VRage.Library.Serialization.Binary;
using Keen.VRage.Library.Serialization.Validation.ErrorRecovery;
using Keen.VRage.Library.Utils;

/// <summary>BlockSaveTests: the aero blocks' own saved data, offline (exit 0 = all pass).</summary>
static class Program
{
    const string Game2 = @"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers2\Game2";
    static int _checks, _fails;
    static void Check(bool ok, string what) { _checks++; if (!ok) { _fails++; if (_fails <= 30) Console.WriteLine("FAIL " + what); } }
    static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    static readonly CultureInfo C = CultureInfo.InvariantCulture;

    static int Main()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            var p = Path.Combine(Game2, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };
        Keys();
        Legacy();
        Engine();
        DevForm();
        Console.WriteLine($"BlockSaveTests: {_checks - _fails}/{_checks} passed");
        return _fails == 0 ? 0 : 1;
    }

    static readonly float[] Floats = { 0f, -0f, 20f, 35f, -35f, 0.5f, 1f, 1f / 3f, 12.345678f, float.Epsilon, -3.5f, 1e-7f };

    // ── 1. each block kind's key: written, read back bit-exact; what a reader refuses ──
    static void Keys()
    {
        foreach (float max in Floats)
            foreach (float trim in Floats)
                for (int axes = 0; axes < 8; axes++)
                {
                    bool p = (axes & 1) != 0, y = (axes & 2) != 0, r = (axes & 4) != 0;
                    string k = AeroBlockSaveForm.Surface(p, y, r, max, trim);
                    Check(k.StartsWith(AeroBlockSaveForm.SurfaceKey + "1 ") && !k.Contains('\n'), $"surface key shape '{k}'");
                    Check(AeroBlockSaveForm.TryReadSurface(k, out bool p2, out bool y2, out bool r2, out float m2, out float t2)
                          && p2 == p && y2 == y && r2 == r && Same(m2, max) && Same(t2, trim), $"surface round trip '{k}'");
                }
        foreach (bool down in new[] { true, false })
            foreach (bool shock in new[] { true, false })
            {
                string k = AeroBlockSaveForm.Gear(down, shock);
                Check(AeroBlockSaveForm.TryReadGear(k, out bool d2, out bool s2) && d2 == down && s2 == shock, $"gear round trip '{k}'");
            }
        foreach (float f in Floats)
        {
            Check(AeroBlockSaveForm.TryReadAirbrake(AeroBlockSaveForm.Airbrake(f), out float b) && Same(b, f), $"airbrake round trip {f}");
            float want = Math.Clamp(f, 0f, 1f);
            Check(AeroBlockSaveForm.TryReadThrust(AeroBlockSaveForm.Thrust(f), out float t) && Same(t, want), $"thrust round trip {f} -> {t} (want {want})");
        }
        // the culture never leaks in (a German PC writes "0,5" with ToString())
        var was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            string k = AeroBlockSaveForm.Thrust(0.25f);
            Check(k == "aero-thrust:1 0.25" && AeroBlockSaveForm.TryReadThrust(k, out float t) && t == 0.25f, $"invariant culture: '{k}'");
        }
        finally { CultureInfo.CurrentCulture = was; }
        Check(AeroBlockSaveForm.TryReadThrust("aero-thrust:1 7", out float tc) && tc == 1f, "thrust clamped to 1");

        // refusals: another tag, too few values, format 0 / not a number, NaN, null
        bool b1, b2, b3; float f1, f2;
        Check(!AeroBlockSaveForm.TryReadSurface(AeroBlockSaveForm.Gear(true, true), out b1, out b2, out b3, out f1, out f2), "a gear key read as a surface");
        Check(!AeroBlockSaveForm.TryReadSurface("aero-surface:1 1 1 1 20", out b1, out b2, out b3, out f1, out f2), "a surface key short of trim read");
        Check(!AeroBlockSaveForm.TryReadSurface("aero-surface:0 1 1 1 20 0", out b1, out b2, out b3, out f1, out f2), "format 0 read");
        Check(!AeroBlockSaveForm.TryReadSurface("aero-surface:x 1 1 1 20 0", out b1, out b2, out b3, out f1, out f2), "format x read");
        Check(!AeroBlockSaveForm.TryReadSurface("aero-surface:1 1 1 1 NaN 0", out b1, out b2, out b3, out f1, out f2), "NaN max read");
        Check(!AeroBlockSaveForm.TryReadGear("aero-gear:1 1", out b1, out b2), "a gear key without airshock read");
        Check(!AeroBlockSaveForm.TryReadAirbrake(null, out f1) && !AeroBlockSaveForm.TryReadThrust("", out f1) && !AeroBlockSaveForm.TryReadThrust("aero-thrust:1 Infinity", out f1), "null / empty / infinite read");
        // a later format that appends values still reads (an older mod on a newer save keeps what it knows)
        Check(AeroBlockSaveForm.TryReadGear("aero-gear:2 0 1 0.75 extra", out b1, out b2) && !b1 && b2, "format 2 with more values");
        Check(AeroBlockSaveForm.TryReadSurface("aero-surface:3 0 1 0 12.5 -4 9 9", out b1, out b2, out b3, out f1, out f2) && !b1 && b2 && !b3 && f1 == 12.5f && f2 == -4f, "surface format 3");

        // the key among others in a block's map; none -> null
        var map = new Dictionary<string, object> { ["something-else:1"] = null, [AeroBlockSaveForm.Airbrake(12f)] = null };
        Check(AeroBlockSaveForm.Find(map, AeroBlockSaveForm.AirbrakeKey) == "aero-airbrake:1 12", "Find the airbrake key");
        Check(AeroBlockSaveForm.Find(map, AeroBlockSaveForm.GearKey) == null && AeroBlockSaveForm.Find<object>(null, AeroBlockSaveForm.GearKey) == null, "Find: none / null map");
        Check(AeroBlockSaveForm.Find(new Dictionary<string, object>(), AeroBlockSaveForm.ThrustKey) == null, "Find: an empty map (a new block, or one from an old save after the migration's insert)");
        // ROTOR STRIKES (2026-10-17): the blades' health - whole blades carry no key; worn and broken round-trip bit-exact; clamped 0..1
        Check(AeroBlockSaveForm.Blades(1f) == null && AeroBlockSaveForm.Blades(float.NaN) == null, "whole blades: no key");
        foreach (float h in new[] { 0f, 1e-7f, 0.25f, 1f / 3f, 0.66f, 0.999f })
            Check(AeroBlockSaveForm.TryReadBlades(AeroBlockSaveForm.Blades(h), out float h2) && Same(h2, h), $"blades round trip {h} -> {h2}");
        Check(AeroBlockSaveForm.Blades(0f) == "aero-blades:1 0" && AeroBlockSaveForm.TryReadBlades("aero-blades:1 0", out float hb) && hb == 0f, "broken blades saved and loaded broken");
        Check(AeroBlockSaveForm.TryReadBlades("aero-blades:1 -3", out float hn) && hn == 0f && AeroBlockSaveForm.TryReadBlades("aero-blades:1 7", out float hx) && hx == 1f, "blades clamped 0..1");
        Check(!AeroBlockSaveForm.TryReadBlades(null, out float hz) && hz == 1f && !AeroBlockSaveForm.TryReadBlades("aero-blades:1 NaN", out hz) && !AeroBlockSaveForm.TryReadBlades(AeroBlockSaveForm.Thrust(0.25f), out hz), "blades: none / NaN / another tag refused (whole)");
        Check(AeroBlockSaveForm.Find(new Dictionary<string, object> { [AeroBlockSaveForm.Blades(0.5f)] = null }, AeroBlockSaveForm.BladesKey) == "aero-blades:1 0.5", "Find the blades key");
        // tags do not prefix each other, nor the old lines' tags
        string[] tags = { AeroBlockSaveForm.SurfaceKey, AeroBlockSaveForm.GearKey, AeroBlockSaveForm.AirbrakeKey, AeroBlockSaveForm.ThrustKey, AeroBlockSaveForm.BladesKey,
                          AeroBlockSaveForm.SolidNozzleKey,
                          AeroBlockSaveForm.LegacySurfaceTag, AeroBlockSaveForm.LegacyGearTag, AeroBlockSaveForm.LegacyBrakeTag, AeroBlockSaveForm.LegacyThrustTag };
        for (int i = 0; i < tags.Length; i++)
            for (int j = 0; j < tags.Length; j++)
                if (i != j) Check(!tags[i].StartsWith(tags[j]), $"tag '{tags[i]}' starts with '{tags[j]}'");
    }

    // ── 2. the old lines (what saves before 2026-10-15 hold): read, then migrated into the new keys ──

    // (the removed writers, word for word: AeroSurfaceSaved.Text and the components' SaveLine)
    static string P(Vector3D p) => string.Join(" ", p.X.ToString("R", C), p.Y.ToString("R", C), p.Z.ToString("R", C));
    static string OldSurface(Vector3D p, bool pi, bool y, bool r, float max, float trim) => "surface " + P(p) + " " + (pi ? "1" : "0") + " " + (y ? "1" : "0") + " " + (r ? "1" : "0") + " " + max.ToString("R", C) + " " + trim.ToString("R", C);
    static string OldGear(Vector3D p, bool down) => "gear " + P(p) + " " + (down ? "1" : "0");
    static string OldBrake(Vector3D p, float max) => "airbrake " + P(p) + " " + max.ToString("R", C);
    static string OldThrust(Vector3D p, float f) => "thrust " + P(p) + " " + f.ToString("R", C);

    static void Legacy()
    {
        var a = new Vector3D(1.5, -2.25, 3e7); var b = new Vector3D(-1234567.875, 0.125, 6.02e6); var c = new Vector3D(0, 0, 0);
        string text = "Drag.Scale=0.8\nStructure.Rate = 2"
            + "\n" + OldSurface(a, true, false, true, 20f, -3.5f)
            + "\n" + OldSurface(b, false, true, false, 1f / 3f, 35f)
            + "\n" + OldGear(a, false) + "\n" + OldGear(c, true)
            + "\n" + OldBrake(b, 12.5f)
            + "\n" + OldThrust(c, 0.25f) + "\n" + OldThrust(a, 3f)
            + "\nsurface 1 2 3 1 1"            // (short: skipped)
            + "\nthrust 1 2 3 NaN"             // (not finite: skipped)
            + "\ngear x 2 3 1"                 // (bad position: skipped)
            + "\n  airbrake 4 5 6 7  \r";      // (trimmed: read)
        var s = new List<(Vector3D at, bool p, bool y, bool r, float max, float trim, double since)>();
        var g = new List<(Vector3D at, bool down, double since)>();
        var br = new List<(Vector3D at, float max, double since)>();
        var th = new List<(Vector3D at, float frac, double since)>();
        int n = AeroBlockSaveForm.ParseLegacy(text, s, g, br, th);
        Check(n == 8 && s.Count == 2 && g.Count == 2 && br.Count == 2 && th.Count == 2, $"legacy: read {n} lines (s {s.Count} g {g.Count} b {br.Count} t {th.Count}), want 8 (2 each)");
        Check(s.Count == 2 && s[0].at == a && s[0].p && !s[0].y && s[0].r && s[0].max == 20f && s[0].trim == -3.5f && s[0].since < 0, "legacy surface 1");
        Check(s.Count == 2 && s[1].at == b && !s[1].p && s[1].y && !s[1].r && Same(s[1].max, 1f / 3f) && s[1].trim == 35f, "legacy surface 2 (positions and floats bit-exact)");
        Check(g.Count == 2 && g[0].at == a && !g[0].down && g[1].at == c && g[1].down, "legacy gears");
        Check(br.Count == 2 && br[0].at == b && br[0].max == 12.5f && br[1].at == new Vector3D(4, 5, 6) && br[1].max == 7f, "legacy airbrakes (the padded line too)");
        Check(th.Count == 2 && th[0].frac == 0.25f && th[1].frac == 1f, "legacy thrusts (3 clamped to 1)");
        Check(AeroBlockSaveForm.ParseLegacy(null, s, g, br, th) == 0 && AeroBlockSaveForm.ParseLegacy("Drag.Scale=1", s, g, br, th) == 0, "legacy: no lines");
        // a new save's world text (no block lines) reads none - nothing to claim
        Check(AeroBlockSaveForm.ParseLegacy("Drag.Scale=0.8\nAero.GroundEffect=0", s, g, br, th) == 0, "legacy: the settings text alone");

        // migration: a block with no data of its own takes the old line (AeroSurfaceSaved.ApplyPending), and its first save
        // writes the new key, which reads back to the same state - the old line is then gone for good
        var s0 = s[0];
        string sk = AeroBlockSaveForm.Surface(s0.p, s0.y, s0.r, s0.max, s0.trim);
        Check(AeroBlockSaveForm.TryReadSurface(sk, out bool p2, out bool y2, out bool r2, out float m2, out float t2) && p2 == s0.p && y2 == s0.y && r2 == s0.r && m2 == s0.max && t2 == s0.trim, "migrate surface: old line -> block -> key");
        string gk = AeroBlockSaveForm.Gear(g[0].down, true);   // (airshock was never in the old lines: the block's default, on)
        Check(AeroBlockSaveForm.TryReadGear(gk, out bool d2, out bool sh2) && d2 == g[0].down && sh2, "migrate gear: old line -> block -> key (airshock default on)");
        Check(AeroBlockSaveForm.TryReadAirbrake(AeroBlockSaveForm.Airbrake(br[0].max), out float b2) && b2 == br[0].max, "migrate airbrake");
        Check(AeroBlockSaveForm.TryReadThrust(AeroBlockSaveForm.Thrust(th[0].frac), out float f2) && f2 == th[0].frac, "migrate thrust");
    }

    // ── 3. through the ENGINE's serializer, as a world save writes a block's slot: binary + Brotli (EntityContainerSerializer),
    //       polymorphic (EntityObjectBuilder.ObjectBuilders is object-typed), with NO mod assembly in the type context ──
    static void Engine()
    {
        if (!File.Exists(Path.Combine(Game2, "Game2.Simulation.dll"))) { Console.WriteLine("SKIP  engine round trip: no SE2 install at " + Game2); return; }
        Keen.VRage.Library.Memory.BumpAllocator.Instance.Initialize();
        // (only the game: the type table can name nothing of ours - what a world sees with the mod removed)
        Singleton<MetadataManager>.Instance.PushContext(new[] { Assembly.Load("SpaceEngineers2") });

        string[] keys = { AeroBlockSaveForm.Surface(true, false, true, 17.5f, -4.25f), AeroBlockSaveForm.Gear(false, false),
                          AeroBlockSaveForm.Airbrake(22f), AeroBlockSaveForm.Thrust(0.8f) };
        foreach (string key in keys)
        {
            // (the value is the block itself in game; offline there is no Entity, so null - the read is what is checked)
            var ob = new EntityNameSessionComponentObjectBuilder { NamedEntities = new Dictionary<string, Entity> { [key] = null } };
            foreach (var fmt in new[] { SerializerFormat.Binary, SerializerFormat.Json })
            {
                var ms = new MemoryStream();
                var services = ServiceLocatorContext.Builder.Create(Array.Empty<object>()).Add(new BinarySerializationParameters { MainChunkCompression = Keen.VRage.Library.Serialization.Binary.Archive.CompressionType.Brotli });
                using (var ctx = new SerializationContext(ms, "save", services)) SerializationHelper.Serialize<object>(ctx, ob, fmt);
                byte[] bytes = ms.ToArray();
                if (fmt == SerializerFormat.Json)
                {
                    string json = Encoding.UTF8.GetString(bytes);
                    if (Environment.GetEnvironmentVariable("BLOCKSAVE_SHOW") == "1") Console.WriteLine(json);
                    Check(json.Contains("\"Game2:Keen.Game2.Simulation.GameSystems.EntityNameSessionComponentObjectBuilder\""), $"json names the engine builder ({json.Length} chars)");
                    Check(!json.Contains("AeroMod") && !json.Contains("BlockSaveTests") && !json.Contains("Aerodynamics"), "json names nothing of the mod");
                    int bs = json.IndexOf("\"$Bundles\""), be = bs < 0 ? -1 : json.IndexOf('}', bs);
                    string bundles = bs < 0 || be < 0 ? "" : json.Substring(bs, be - bs);
                    foreach (var line in bundles.Split('\n'))
                    {
                        var t = line.Trim();
                        if (t.StartsWith("\"") && !t.StartsWith("\"$Bundles"))
                            Check(t.StartsWith("\"Game2\"") || t.StartsWith("\"VRage\"") || t.StartsWith("\"System."), $"bundle {t}: only the game's and the runtime's");
                    }
                }
                // read back as a world load does: the deferred-error contexts (EntityContainerDeserializer.TryReadFile)
                object back;
                var read = DeferredErrorHandlingSerializationContext.AddContexts(ServiceLocatorContext.Builder.Create(Array.Empty<object>()));
                try
                {
                    using var rctx = new SerializationContext(new MemoryStream(bytes), "load", read);
                    back = SerializationHelper.Deserialize<object>(rctx, fmt);
                }
                catch (Exception e) { back = null; Console.WriteLine($"      {fmt} read threw {e.GetBaseException().Message}"); }
                var eob = back as EntityNameSessionComponentObjectBuilder;
                Check(eob != null && eob.NamedEntities != null && eob.NamedEntities.Count == 1 && eob.NamedEntities.ContainsKey(key), $"{fmt} ({bytes.Length} bytes): the engine builder reads back whole, mod absent: '{key}'");
                Check(eob != null && AeroBlockSaveForm.Find(eob.NamedEntities, key.Substring(0, key.IndexOf(':') + 1)) == key, $"{fmt}: the mod finds its key again");
            }
        }
    }

    // ── 4. the harness's set requests and dump tokens (Dev/AeroBlockSaveDevForm.cs: test_blocksave.sh) ──
    static void DevForm()
    {
        bool P(string t, out AeroBlockSaveDevForm.SetRequest r) => AeroBlockSaveDevForm.TryParseSet(t, out r, out _);
        Check(P("SSTO|thrust|3|0.73", out var r) && r.Grid == "SSTO" && r.Kind == AeroBlockSaveDevForm.Kind.Thrust && !r.ByCell && r.Index == 3 && Same(r.Value, 0.73f), "set: thrust by index");
        Check(P("My_Ship|surface-trim|-4,2,10|-7.5", out r) && r.Grid == "My_Ship" && r.Kind == AeroBlockSaveDevForm.Kind.SurfaceTrim && r.ByCell && r.X == -4 && r.Y == 2 && r.Z == 10 && Same(r.Value, -7.5f), "set: surface trim by cell");
        Check(P("SSTO|surface-max|0|27", out r) && r.Kind == AeroBlockSaveDevForm.Kind.SurfaceMax && Same(r.Value, 27f), "set: surface max");
        Check(P("SSTO|airbrake-max|1|12.5", out r) && r.Kind == AeroBlockSaveDevForm.Kind.AirbrakeMax && r.Index == 1, "set: airbrake max");
        foreach (var (v, down) in new[] { ("up", false), ("down", true), ("0", false), ("1", true), ("UP", false) })
            Check(P("SSTO|gear|0|" + v, out r) && r.Kind == AeroBlockSaveDevForm.Kind.Gear && r.Flag == down, $"set: gear {v}");
        foreach (var (v, on) in new[] { ("off", false), ("on", true), ("0", false), ("true", true) })
            Check(P("SSTO|airshock|0|" + v, out r) && r.Kind == AeroBlockSaveDevForm.Kind.Airshock && r.Flag == on, $"set: airshock {v}");
        Check(P(" SSTO | THRUST | 0 | 1 ", out r) && r.Grid == "SSTO" && r.Kind == AeroBlockSaveDevForm.Kind.Thrust, "set: trims and case");
        // (refused, with a reason)
        foreach (var bad in new[] { null, "", "SSTO", "SSTO|thrust|0", "SSTO|thrust|0|0.5|x", "|thrust|0|0.5", "SSTO|wing|0|1", "SSTO|thrust|-1|0.5",
                                    "SSTO|thrust|a|0.5", "SSTO|thrust|1,2|0.5", "SSTO|thrust|1,2,x|0.5", "SSTO|thrust|0|1.5", "SSTO|thrust|0|-0.1",
                                    "SSTO|thrust|0|NaN", "SSTO|surface-trim|0|Infinity", "SSTO|gear|0|sideways", "SSTO|airshock|0|maybe", "SSTO|surface-max|0|1,5" })
            Check(!AeroBlockSaveDevForm.TryParseSet(bad, out _, out string why) && !string.IsNullOrEmpty(why), $"set refused: '{bad}'");
        Check(AeroBlockSaveDevForm.BlockKind(AeroBlockSaveDevForm.Kind.Airshock) == "gear" && AeroBlockSaveDevForm.BlockKind(AeroBlockSaveDevForm.Kind.SurfaceMax) == "surface"
              && AeroBlockSaveDevForm.BlockKind(AeroBlockSaveDevForm.Kind.AirbrakeMax) == "airbrake" && AeroBlockSaveDevForm.BlockKind(AeroBlockSaveDevForm.Kind.Thrust) == "thrust", "set: block kinds");
        Check(AeroBlockSaveDevForm.NameMatches("My Ship", "My_Ship") && AeroBlockSaveDevForm.NameMatches("SSTO", "SSTO") && !AeroBlockSaveDevForm.NameMatches("SSTO 2", "SSTO")
              && !AeroBlockSaveDevForm.NameMatches(null, "SSTO"), "grid names as findgrids prints them");
        Check(AeroBlockSaveDevForm.CompareCells(0, 5, 5, 1, 0, 0) < 0 && AeroBlockSaveDevForm.CompareCells(1, 0, 9, 1, 1, 0) < 0 && AeroBlockSaveDevForm.CompareCells(1, 1, 2, 1, 1, 1) > 0
              && AeroBlockSaveDevForm.CompareCells(-3, 0, 0, -3, 0, 0) == 0, "cell order x, y, z");
        // (dump tokens: no spaces - the script splits the answer on them - and every value read back)
        string s = AeroBlockSaveDevForm.SurfaceToken(-4, 2, 10, 27f, -7.5f, true, false, true, true);
        Check(s == "surface@-4,2,10=max:27;trim:-7.5;p:1;y:0;r:1;rb:1", $"surface token '{s}'");
        string g = AeroBlockSaveDevForm.GearToken(1, -2, 3, false, false, true);
        Check(g == "gear@1,-2,3=down:0;shock:0;rb:1", $"gear token '{g}'");
        string a = AeroBlockSaveDevForm.AirbrakeToken(0, 0, 0, 12.5f, false), t = AeroBlockSaveDevForm.ThrustToken(5, 6, 7, 0.73f, true);
        Check(a == "airbrake@0,0,0=max:12.5;rb:0" && t == "thrust@5,6,7=f:0.73;rb:1", $"airbrake / thrust tokens '{a}' '{t}'");
        Check(AeroBlockSaveDevForm.TokenValue(s, "trim") == "-7.5" && AeroBlockSaveDevForm.TokenValue(g, "down") == "0" && AeroBlockSaveDevForm.TokenValue(t, "f") == "0.73"
              && AeroBlockSaveDevForm.TokenValue(t, "max") == null && AeroBlockSaveDevForm.TokenValue("nonsense", "f") == null, "token values");
        foreach (float f in Floats)
        {
            string k = AeroBlockSaveDevForm.ThrustToken(0, 0, 0, f, false);
            Check(!k.Contains(' ') && float.TryParse(AeroBlockSaveDevForm.TokenValue(k, "f"), NumberStyles.Float, C, out float back) && Same(back, f), $"thrust token round trip '{k}'");
        }
        var old = System.Threading.Thread.CurrentThread.CurrentCulture;
        System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
        Check(AeroBlockSaveDevForm.TryParseSet("SSTO|thrust|0|0.73", out r, out _) && Same(r.Value, 0.73f) && AeroBlockSaveDevForm.ThrustToken(0, 0, 0, 0.73f, false).EndsWith("f:0.73;rb:0"),
              "invariant culture under a comma locale");
        System.Threading.Thread.CurrentThread.CurrentCulture = old;
    }
}
