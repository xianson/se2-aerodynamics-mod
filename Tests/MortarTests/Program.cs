#pragma warning disable
using System.IO;
using System.Text.Json;
using AeroMod;

/// <summary>MortarTests: the mortar offline (exit 0 = all pass) - the law (Mortar.cs), the stack (SolidRocket.FindStack with charges),
/// the generated table and content, and the firing component's rules in its source.</summary>
static class Program
{
    static int _checks, _fails;
    static void Check(bool ok, string what) { _checks++; if (!ok) { _fails++; Console.WriteLine("FAIL " + what); } else if (Environment.GetEnvironmentVariable("VERBOSE") != null) Console.WriteLine("ok   " + what); }
    static bool Near(float a, float b, float tol = 1e-4f) => MathF.Abs(a - b) <= tol * MathF.Max(1f, MathF.Abs(b));

    sealed class Charge : SolidRocket.IPiece
    {
        public SolidRocket.Geo G; public float P = 10f; public bool W = true;
        public SolidRocket.Geo Geo => G; public float Propellant => P; public float Full => 10f; public bool Feeds => W;
    }
    static SolidRocket.Geo Box(int x, int y, int z, int sx, int sy, int sz, Vector3 up, string size = "250")
        => SolidRocket.Geo.Of(new Vector3I(x, y, z), new Vector3I(x + sx - 1, y + sy - 1, z + sz - 1), up, size);

    static string Root()
    {
        var d = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && d != null; i++, d = Path.GetDirectoryName(d))
            if (File.Exists(Path.Combine(d, "tools", "meshgen", "gen_propulsor_defs.py"))) return d;
        return null;
    }

    static int Main()
    {
        Law();
        Stack();
        string root = Root();
        Check(root != null, "the mod folder found");
        if (root != null) { Table(root); Content(root); Source(root); }
        Console.WriteLine(_fails == 0 ? $"MortarTests: {_checks}/{_checks} passed" : $"MortarTests: {_fails} of {_checks} FAILED");
        return _fails == 0 ? 0 : 1;
    }

    static void Law()
    {
        Check(Mortar.Impulse(0f) == 0f && Mortar.Impulse(-1f) == 0f && Mortar.Impulse(float.NaN) == 0f, "no charge (or nonsense): no impulse");
        Check(Near(Mortar.Impulse(10f), 10f * MortarBlocks.ImpulsePerKg), "impulse = propellant x the impulse per kg");
        // a 2.5 m breech: 10 x 5 x 10 cells, Up +Y, cells y 0..4: its front (Down) face at y = -0.125 m; turned over: at the top
        float cell = 0.25f;
        float f = Mortar.FrontFace(0, 4, 1, cell), fd = Mortar.FrontFace(0, 4, -1, cell);
        Check(Near(f, -0.125f) && Near(fd, 1.125f), $"the front face: {f} / {fd} m");
        Check(Near(Mortar.SealMiddle(f, 1, 0.2f), -0.025f) && Near(Mortar.SealMiddle(fd, -1, 0.2f), 1.025f), "the seal's middle: half its thickness in from the front face");
        Check(Near(Mortar.RoundMiddle(f, 1, 1.25f), -0.75f) && Near(Mortar.RoundMiddle(fd, -1, 1.25f), 1.75f), "the round's middle: half its height out from the front face");
        Mortar.Kick(1000f, new Vector3(0, -2, 0), out var r, out var s);
        Check(Near(r.Y, -1000f) && Near((r + s).Length(), 0f) && Near(r.Length(), 1000f), "the kick: the round forward, the ship back, one momentum");
        Mortar.Kick(1000f, Vector3.Zero, out r, out s);
        Check(r.Length() == 0f && s.Length() == 0f, "no direction: no kick");
        Check(Mortar.SplitWaitTicks >= 30 && Mortar.KickWaitTicks >= 10, "the waits: the split within a second, the bodies a moment");
    }

    static void Stack()
    {
        // the breech (2.5 m: 10 x 5 x 10 cells at y 0..4, Up +Y) and charges (10 x 10 x 10) stacked on its back (Up) face
        var me = Box(0, 0, 0, 10, 5, 10, Vector3.UnitY);
        var a = new Charge { G = Box(0, 5, 0, 10, 10, 10, Vector3.UnitY) };
        var b = new Charge { G = Box(0, 15, 0, 10, 10, 10, -Vector3.UnitY) };   // (either way up)
        var c = new Charge { G = Box(0, 26, 0, 10, 10, 10, Vector3.UnitY) };    // (a gap: not in the stack)
        var under = new Charge { G = Box(0, -10, 0, 10, 10, 10, Vector3.UnitY) }; // (on the front: the round's side - never a charge of it)
        var cands = new List<Charge> { c, under, b, a }; var stack = new List<Charge>();
        int n = SolidRocket.FindStack(in me, cands, stack);
        Check(n == 2 && stack[0] == a && stack[1] == b, $"the stack: the touching charges on the back face, nearest first ({n})");
        Check(Near(SolidRocket.Total(stack), 20f), "the stack's propellant: the charges' sum");
        a.P = 0f;
        n = SolidRocket.FindStack(in me, cands, stack);
        Check(n == 2 && Near(SolidRocket.Total(stack), 10f), "a spent charge in the stack: it holds the place, adds nothing");
        a.W = false;
        n = SolidRocket.FindStack(in me, cands, stack);
        Check(n == 0, "a damaged charge breaks the stack there");
        a.W = true;
        var small = new Charge { G = Box(0, 5, 0, 10, 10, 10, Vector3.UnitY, "150") };
        n = SolidRocket.FindStack(in me, new List<Charge> { small }, stack);
        Check(n == 0, "a charge of another size is not in the stack");
        var off = new Charge { G = Box(1, 5, 0, 10, 10, 10, Vector3.UnitY) };
        n = SolidRocket.FindStack(in me, new List<Charge> { off }, stack);
        Check(n == 0, "an offset charge is not in the stack");
    }

    static void Table(string root)
    {
        var rows = MortarBlocks.Rows;
        Check(rows.Count == 9, $"9 mortar blocks ({rows.Count})");
        foreach (var size in new[] { "050", "150", "250" })
            foreach (var k in new[] { MortarBlocks.Kind.Charge, MortarBlocks.Kind.Breech, MortarBlocks.Kind.Round })
                Check(rows.Count(r => r.Size == size && r.Kind == k) == 1, $"{size}: one {k}");
        Check(rows.Select(r => r.Block).Distinct().Count() == 9 && rows.Select(r => r.Server).Distinct().Count() == 9 && rows.Select(r => r.Client).Distinct().Count() == 9, "distinct GUIDs");
        foreach (var r in rows)
        {
            float cellM = r.Box.X;
            Check(Near(r.Box.X, r.Box.Z) && (r.Kind == MortarBlocks.Kind.Charge ? Near(r.Box.Y, cellM) : Near(r.Box.Y, cellM / 2f)), $"{r.Name}: a cube / a half cube ({r.Box})");
            var seg = SolidRocketBlocks.Rows.FirstOrDefault(s => !s.Nozzle && s.Size == r.Size);
            if (r.Kind == MortarBlocks.Kind.Charge)
                Check(seg != null && Near(r.Propellant, seg.Propellant * 0.1f / 2f, 1e-2f), $"{r.Name}: a tenth of a segment's propellant per volume ({r.Propellant} kg)");
            else Check(r.Propellant == 0f, $"{r.Name}: no charge of its own");
            Check(r.Seal >= 0.1f - 1e-4f && r.Seal < r.Box.Y / 2f, $"{r.Name}: the seal {r.Seal} m - at least 10 cm, under half the breech");
        }
    }

    static (float[] min, float[] max) Acc(JsonElement g, JsonElement node)
    {
        int mesh = node.GetProperty("mesh").GetInt32();
        int acc = g.GetProperty("meshes")[mesh].GetProperty("primitives")[0].GetProperty("attributes").GetProperty("POSITION").GetInt32();
        var a = g.GetProperty("accessors")[acc];
        return (a.GetProperty("min").EnumerateArray().Select(x => x.GetSingle()).ToArray(), a.GetProperty("max").EnumerateArray().Select(x => x.GetSingle()).ToArray());
    }
    static bool Overlap((float[] min, float[] max) p, (float[] min, float[] max) q)
    { for (int i = 0; i < 3; i++) if (p.max[i] <= q.min[i] || q.max[i] <= p.min[i]) return false; return true; }

    static void Content(string root)
    {
        string dir = Path.Combine(root, "Assets", "Aero", "Propulsors");
        foreach (var r in MortarBlocks.Rows)
        {
            string d = Path.Combine(dir, r.Name);
            Check(Directory.Exists(d), $"{r.Name}: its content");
            if (!Directory.Exists(d)) continue;
            string fbd = File.ReadAllText(Path.Combine(d, $"{r.Name}_PowerableBlockDefinition.partialdef"));
            Check(fbd.Contains(r.Block.ToString()) && fbd.Contains("\"ConsumedResource\": null"), $"{r.Name}: its definition (GUID, no power)");
            Check(File.ReadAllText(Path.Combine(d, $"{r.Name}_Server.partialdef")).Contains(r.Server.ToString()), $"{r.Name}: its server prefab");
            string scomp = File.ReadAllText(Path.Combine(d, $"{r.Name}_ServerComposition.partialdef"));
            Check(!scomp.Contains("ConveyorComponent") && !scomp.Contains("ResourceContainer"), $"{r.Name}: no conveyor, no tank (the charge is the mod's)");
            string brk = File.ReadAllText(Path.Combine(d, $"{r.Name}_BreakableBlockDefinition.partialdef"));
            bool breech = r.Kind == MortarBlocks.Kind.Breech;
            Check(brk.Contains("Fracture_01") && brk.Contains("Fracture_02") != breech, $"{r.Name}: critical pieces {(breech ? "the casing only - the seal breaks off, the breech stays" : "both")}");
            // the colliders: the mount faces Up and Down; the breech's seal under its front (Down) mount slab, the casing under its back one
            var nf = JsonDocument.Parse(File.ReadAllText(Path.Combine(d, "Models", "NonFractured", $"{r.Name}_collision.gltf"))).RootElement;
            JsonElement? pz = null, mz = null;
            foreach (var n in nf.GetProperty("nodes").EnumerateArray())
            {
                string nm = n.GetProperty("name").GetString();
                if (nm.EndsWith("_PZ")) pz = n; else if (nm.EndsWith("_MZ")) mz = n;
            }
            Check(pz.HasValue && mz.HasValue, $"{r.Name}: mount points on both ends");
            if (!breech || !pz.HasValue || !mz.HasValue) continue;
            var fr = JsonDocument.Parse(File.ReadAllText(Path.Combine(d, "Models", "Fractured", $"{r.Name}_Fractured_collision.gltf"))).RootElement;
            JsonElement? f1 = null, f2 = null;
            foreach (var n in fr.GetProperty("nodes").EnumerateArray())
            {
                string nm = n.GetProperty("name").GetString();
                if (nm == "Fracture_01") f1 = n; else if (nm == "Fracture_02") f2 = n;
            }
            Check(f1.HasValue && f2.HasValue, $"{r.Name}: two fracture pieces");
            if (!f1.HasValue || !f2.HasValue) continue;
            var m = Acc(nf, mz.Value); var p = Acc(nf, pz.Value); var a1 = Acc(fr, f1.Value); var a2 = Acc(fr, f2.Value);
            // (the model's Y is the block's Up: the round's side is -Y)
            Check(Overlap(m, a2) && !Overlap(m, a1), $"{r.Name}: the front mount slab is the SEAL's only (killing it frees that face)");
            Check(Overlap(p, a1) && !Overlap(p, a2), $"{r.Name}: the back mount slab is the casing's (the charges stay on)");
            Check(a2.max[1] < 0f && a2.max[1] - a2.min[1] <= r.Seal + 1e-3f, $"{r.Name}: the seal is a thin piece at the front ({a2.min[1]:F3} .. {a2.max[1]:F3})");
        }
        string grp = Path.Combine(dir, "Propulsor_Mortars_BlockGroup.def");
        Check(File.Exists(grp) && File.ReadAllText(grp).Contains("\"Mortars\""), "the G-menu group Mortars");
    }

    static void Source(string root)
    {
        string sim = Path.Combine(root, "Scripts", "Simulation");
        string m = File.ReadAllText(Path.Combine(sim, "Propulsion", "AeroMortarComponent.cs"));
        Check(m.Contains("if (on && _wasOff && !Fired) Fire();") && m.Contains("_wasOff = !_pb.Enabled;"), "a flip off -> on fires (one placed or loaded switched on does not)");
        int iCharges = m.IndexOf("no charges on its back face"), iRound = m.IndexOf("no mortar round of its size"), iSeal = m.IndexOf("KillPieceAt(");
        Check(iCharges > 0 && iRound > 0 && iSeal > iCharges && iSeal > iRound, "no charges or no round: refused before anything breaks");
        int iTake = m.IndexOf("_piece = TakeSplit("), iImp = m.IndexOf("TryApplyImpulse(onRound");
        Check(iTake > 0 && iImp > iTake && m.IndexOf("TryApplyImpulse(") == iImp, "the impulse only after the split (no split: no impulse)");
        Check(m.Contains("foreach (var c in _stack) c.Spend();"), "the charges spent when it fires");
        string inj = File.ReadAllText(Path.Combine(sim, "Injection", "EntityInjector.cs"));
        Check(inj.Contains("MortarBlocks.ChargeServerPrefabs") && inj.Contains("MortarBlocks.BreechServerPrefabs"), "injected into the charges' and breeches' server prefabs");
        string save = File.ReadAllText(Path.Combine(sim, "Blocks", "AeroBlockSave.cs"));
        Check(save.Contains("MortarChargeId") && save.Contains("MortarId"), "their saving ids");
    }
}
