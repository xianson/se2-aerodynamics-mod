#pragma warning disable
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AeroMod;

/// <summary>The pad-foot gears (Gear/PadFoot.cs, tools/meshgen/gen_padgear_defs.py): the orientation lock's life and axes, and per
/// variant - the C# variant against the generator's spec, the generated definitions (collider mass, corner module with no drive and no
/// steering, the foot's pivot turned as its housing's wheel rest, the block on the module), the puck a cylinder the corner module can
/// fit (DetermineWheelRadius / WheelColliderComponent: radius above half its length) with the sphere's bottom at the sole, the box,
/// and the suspension's sag at the rated load.</summary>
static class PadFootTests
{
    static int _checks, _fails;
    static void Check(bool ok, string what) { _checks++; if (!ok) { _fails++; if (_fails <= 25) Console.WriteLine("FAIL pad: " + what); } }
    static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static (int checks, int fails) Run()
    {
        Life();
        string root = Root();
        Check(root != null, "the mod's tree found above the test");
        if (root != null) Variants(root);
        return (_checks, _fails);
    }

    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "tools", "meshgen", "padgear_spec.json"))) d = d.Parent;
        return d?.FullName;
    }

    static void Life()
    {
        var a = PadLockLife.FixedAxes();
        Check(a.Length == 6 && a[3] && !a[0] && !a[1] && !a[2] && !a[4] && !a[5], "the lock fixes only the twist about the axle (AngularX), nothing linear");
        Check(!PadLockLife.FixedAxes(1)[1], "a linear axis can be fixed");
        object w1 = new(), w2 = new();
        var l = new PadLockLife();
        Check(l.Step(null, false) == PadLockDo.Nothing && !l.Has, "a lock with no wheel");
        Check(l.Step(w1, false) == PadLockDo.Create, "no lock on a new wheel");
        l.Made(w1, false);
        Check(!l.Has && l.Step(w1, false) == PadLockDo.Create, "a refused lock not tried again");
        l.Made(w1, true);
        for (int i = 0; i < 50; i++) Check(l.Step(w1, false) == PadLockDo.Nothing && l.Has, "a lock made twice on the same wheel");
        Check(l.Step(w2, false) == PadLockDo.Create && !l.Has && l.Lost == 1, "a new wheel (a load, a new body): the old lock not forgotten / the new one not made");
        l.Made(w2, true);
        Check(l.Step(w2, true) == PadLockDo.Create && l.Lost == 2, "a lock the engine destroyed not made again");
        l.Made(w2, true);
        Check(l.Step(null, false) == PadLockDo.Nothing && !l.Has, "the wheel gone: its lock kept");
        l.Step(w1, false); l.Made(w1, true);
        Check(l.Created == 4, "locks made: " + l.Created);
        var r = new PadLockLife(); r.Step(w1, false); r.Made(w1, true);
        Check(r.Removing(true, false) == PadLockDo.Destroy && !r.Has, "removal: a live lock not destroyed");
        r.Step(w1, false); r.Made(w1, true);
        Check(r.Removing(false, false) == PadLockDo.Nothing, "removal: destroyed with its bodies gone (the engine asserts)");
        r.Step(w1, false); r.Made(w1, true);
        Check(r.Removing(true, true) == PadLockDo.Nothing, "removal: destroyed twice");
        Check(new PadLockLife().Removing(true, false) == PadLockDo.Nothing, "removal: destroyed a lock never made");
    }

    static float F(JsonElement e) => (float)e.GetDouble();
    static float[] V(JsonElement e) => e.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();

    static void Variants(string root)
    {
        var spec = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tools", "meshgen", "padgear_spec.json"))).RootElement;
        string src = File.ReadAllText(Path.Combine(root, "Scripts", "Simulation", "Blocks", "AeroGearVariants.cs"));
        int n = 0;
        // (every variant in the C#: a Foot only on the pad gears - their lock and box are for pads only)
        // (each entry: from its "new() { Name =" to the next one or the array's end)
        var all = Regex.Matches(src, @"new\(\) \{ Name = ""([^""]+)"", Block = new\(""([0-9a-f-]+)""\), CornerModule = new\(""([0-9a-f-]+)""\)(.*?)(?=new\(\) \{ Name =|
    \};)", RegexOptions.Singleline);
        Check(all.Count >= 8, $"variants read from AeroGearVariants.cs ({all.Count})");
        foreach (Match m in all)
        {
            bool foot = m.Groups[4].Value.Contains("Foot = new PadFootSpec");
            Check(foot == m.Groups[1].Value.StartsWith("Retractable Gear ("), $"{m.Groups[1].Value}: a Foot {(foot ? "on a wheeled gear" : "missing on a pad gear")}");
        }
        foreach (var p in spec.EnumerateObject())
        {
            n++;
            string size = p.Name; var L = p.Value;
            string name = L.GetProperty("name").GetString();
            var cs = all.Cast<Match>().FirstOrDefault(m => m.Groups[2].Value == L.GetProperty("block").GetString());
            Check(cs != null, $"{size}: its block {L.GetProperty("block")} has a variant in AeroGearVariants");
            if (cs == null) continue;
            string body = cs.Groups[4].Value;
            Check(cs.Groups[1].Value.StartsWith(name + " ") && cs.Groups[3].Value == L.GetProperty("module").GetString(), $"{size}: name / corner module differ from the spec");
            float Num(string key) { var mm = Regex.Match(body, key + @" = (-?[0-9.]+)f"); return mm.Success ? float.Parse(mm.Groups[1].Value, C) : float.NaN; }
            float[] Vec(string key) { var mm = Regex.Match(body, key + @" = new Vector3\((-?[0-9.]+)f?, (-?[0-9.]+)f?, (-?[0-9.]+)f?\)"); return mm.Success ? new[] { 1, 2, 3 }.Select(i => float.Parse(mm.Groups[i].Value, C)).ToArray() : null; }
            bool Near(float a, float b) => MathF.Abs(a - b) < 1e-3f;
            float H = F(L.GetProperty("H"));
            Check(Near(Num("WheelRadius"), H) && Near(Num("Height"), H), $"{size}: height / wheel radius {Num("Height")} vs {H}");
            Check(Near(Num("WheelMass"), F(L.GetProperty("mass"))) && Near(Num("RatedLoad"), F(L.GetProperty("rated"))) && Near(Num("Stroke"), F(L.GetProperty("travel"))), $"{size}: mass / rated / stroke differ");
            var bc = Vec("BoxCenter"); var bh = Vec("BoxHalf"); var sbc = V(L.GetProperty("box_center")); var sbh = V(L.GetProperty("box_half"));
            Check(bc != null && bh != null && Enumerable.Range(0, 3).All(i => Near(bc[i], sbc[i]) && Near(bh[i], sbh[i]) && bh[i] > 0.05f), $"{size}: the box differs or is flat");
            Check(Near(sbc[1] - sbh[1], -H) && Near(sbc[1] + sbh[1], 0f), $"{size}: the box is not the foot (top at the hub, sole at -H)");

            // the definitions
            string d = Path.Combine(root, "Assets", "Aero", "PadGear", size);
            string fd = Path.Combine(d, "Foot");
            JsonElement Man(string f) => JsonDocument.Parse(File.ReadAllText(Path.Combine(fd, f))).RootElement.GetProperty("$Value").GetProperty("Manipulator");
            string N = $"PadGear_{size}_Foot";
            Check(F(Man(N + "_ColliderDefinition.partialdef").GetProperty("WheelMass")) == F(L.GetProperty("mass")), $"{size}: the collider's WheelMass is not the spec's");
            var mod = Man($"PadGear_{size}_WheelCornerModuleDefinition.partialdef");
            Check(mod.GetProperty("Guid").GetString() == L.GetProperty("module").GetString() && mod.GetProperty("WheelPrefab").GetString() == L.GetProperty("wheel_server").GetString(), $"{size}: the corner module is not the foot's");
            Check(F(mod.GetProperty("MaxTorque")) == 0f && F(mod.GetProperty("MaxSteeringAngle")) == 0f, $"{size}: the foot's corner module drives or steers");
            var mcd = Man(N + "_ModelComponentDefinition.partialdef");
            var piv = mcd.GetProperty("Dummies").GetProperty("Changed")[0].GetProperty("Value").GetProperty("Transform").GetProperty("Transform");
            var q = piv.GetProperty("Orientation"); var sq = V(L.GetProperty("pivot_q"));
            Check(Near(F(q.GetProperty("X")), sq[0]) && Near(F(q.GetProperty("W")), sq[3]), $"{size}: the pivot is not turned as the wheel rest (the foot would spawn tilted)");
            var hous = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Assets", "Aero", "RetractableGear", size, $"RetractableGear_{size}_BlockModelDefinition.partialdef"))).RootElement;
            var rest = hous.GetProperty("$Value").GetProperty("Manipulator").GetProperty("Dummies").GetProperty("Changed").EnumerateArray()
                .First(c => c.GetProperty("Value").GetProperty("Name").GetString() == "Wheel Rest Position").GetProperty("Value").GetProperty("Transform").GetProperty("Transform").GetProperty("Orientation");
            Check(Near(F(rest.GetProperty("X")), sq[0]) && Near(F(rest.GetProperty("W")), sq[3]), $"{size}: the spec's pivot turn is not the housing's wheel rest");
            string scomp = File.ReadAllText(Path.Combine(d, $"PadGear_{size}_ServerComposition.partialdef"));
            Check(scomp.Contains(L.GetProperty("module").GetString()) && scomp.Contains("WheelCornerModuleComponent"), $"{size}: the block is not on the foot's corner module");
            string pdef = File.ReadAllText(Path.Combine(d, $"PadGear_{size}_PowerableBlockDefinition.partialdef"));
            string iconMeta = Path.Combine(d, "Icon", $"PadGear_{size}_Icon.png.meta");
            string iconId = File.Exists(iconMeta) ? JsonDocument.Parse(File.ReadAllText(iconMeta)).RootElement.GetProperty("$Value").GetProperty("AssetID").GetString() : "?";
            Check(pdef.Contains("\"" + name + "\"") && pdef.Contains("{G}" + iconId) && File.Exists(Path.Combine(d, "Icon", $"PadGear_{size}_Icon.png")), $"{size}: the block's name or its rendered icon");

            // the puck: a cylinder about Y the corner module fits (radius above half its length), the sphere's bottom at the sole
            var gl = JsonDocument.Parse(File.ReadAllText(Path.Combine(fd, "Models", N + "_collision.gltf"))).RootElement;
            int count = gl.GetProperty("accessors")[0].GetProperty("count").GetInt32();
            var bin = File.ReadAllBytes(Path.Combine(fd, "Models", N + "_collision.bin"));
            var pts = Enumerable.Range(0, count).Select(i => new Vector3(BitConverter.ToSingle(bin, i * 12), BitConverter.ToSingle(bin, i * 12 + 4), BitConverter.ToSingle(bin, i * 12 + 8))).ToArray();
            float r = pts.Max(v => MathF.Sqrt(v.X * v.X + v.Z * v.Z)), rmin = pts.Min(v => MathF.Sqrt(v.X * v.X + v.Z * v.Z));
            float y0 = pts.Min(v => v.Y), y1 = pts.Max(v => v.Y), cy = (y0 + y1) / 2;
            Check(count >= 24 && r - rmin < 1e-4f && r > 0.05f, $"{size}: the puck is not round ({rmin:F3}..{r:F3})");
            Check(r > (y1 - y0) * 0.5f, $"{size}: the puck fails the cylinder fit (radius {r:F3} vs half length {(y1 - y0) / 2:F3})");
            Check(Near(cy - r, -H), $"{size}: the corner module's sphere bottom {cy - r:F3} is not the sole {-H:F3}");

            // the sag at the rated load: the default stiffness within half the stroke, the slider's top a few cm
            float mass = F(L.GetProperty("mass")), rated = F(L.GetProperty("rated")), stroke = F(L.GetProperty("travel")), dt = 1f / 60f;
            float sl = F(L.GetProperty("default_stiffness"));
            float sagDef = GearStiffness.Sag(rated, GearStiffness.SpringRate(GearStiffness.Strength(sl, 3f), mass, dt));
            float sagTop = GearStiffness.Sag(rated, GearStiffness.SpringRate(GearStiffness.MaxStrength, mass, dt));
            Check(sagDef <= 0.5f * stroke, $"{size}: default stiffness {sl}: sag {sagDef:F3} m of a {stroke} m stroke");
            Check(sagTop < 0.1f, $"{size}: the slider's top: sag {sagTop:F3} m");
        }
        Check(n == 4, $"four pad gears in the spec ({n})");
    }
}
