#pragma warning disable
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AeroMod;

/// <summary>The pad-foot gears (Gear/PadFoot.cs, tools/meshgen/gen_padgear_defs.py): the slider's life, its frames (the 6-DOF rows: the
/// strut axis on row 1 - LinearX -, the point on row 4; the foot's frame and the mount's meet at the vanilla spawn pose, and their
/// difference along the axis is the compression), its axis modes and limits; and per variant - the C# variant against the generator's
/// spec (the pivot the slider uses too), the generated definitions (collider mass, corner module with no drive and no steering, the
/// foot's pivot turned as its housing's wheel rest, the block on the module), the puck a cylinder the corner module can fit
/// (DetermineWheelRadius / WheelColliderComponent: radius above half its length) with the sphere's bottom at the sole, the box, the
/// rating (the housing's wheel's) and the oleo's sag at it.</summary>
static class PadFootTests
{
    static int _checks, _fails;
    static void Check(bool ok, string what) { _checks++; if (!ok) { _fails++; if (_fails <= 25) Console.WriteLine("FAIL pad: " + what); } }
    static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static (int checks, int fails) Run()
    {
        Life();
        Slider();
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
        object w1 = new(), w2 = new();
        var l = new PadSliderLife();
        Check(l.Step(null, false) == PadSliderDo.Nothing && !l.Has, "a slider with no wheel");
        Check(l.Step(w1, false) == PadSliderDo.Create, "no slider on a new wheel");
        l.Made(w1, false);
        Check(!l.Has && l.Step(w1, false) == PadSliderDo.Create, "a refused slider not tried again");
        l.Made(w1, true);
        for (int i = 0; i < 50; i++) Check(l.Step(w1, false) == PadSliderDo.Nothing && l.Has, "a slider made twice on the same wheel");
        Check(l.Step(w2, false) == PadSliderDo.Create && !l.Has && l.Lost == 1, "a new wheel (a load, a new body): the old slider not forgotten / the new one not made");
        l.Made(w2, true);
        Check(l.Step(w2, true) == PadSliderDo.Create && l.Lost == 2, "a slider the engine destroyed not made again");
        l.Made(w2, true);
        Check(l.Step(null, false) == PadSliderDo.Nothing && !l.Has, "the wheel gone: its slider kept");
        l.Step(w1, false); l.Made(w1, true);
        Check(l.Created == 4, "sliders made: " + l.Created);
        var r = new PadSliderLife(); r.Step(w1, false); r.Made(w1, true);
        Check(r.Removing(true, false) == PadSliderDo.Destroy && !r.Has, "removal: a live slider not destroyed");
        r.Step(w1, false); r.Made(w1, true);
        Check(r.Removing(false, false) == PadSliderDo.Nothing, "removal: destroyed with its bodies gone (the engine asserts)");
        r.Step(w1, false); r.Made(w1, true);
        Check(r.Removing(true, true) == PadSliderDo.Nothing, "removal: destroyed twice");
        Check(new PadSliderLife().Removing(true, false) == PadSliderDo.Nothing, "removal: destroyed a slider never made");
    }

    static bool NearV(Vector3 a, Vector3 b, float t = 1e-4f) => (a - b).Length() < t;
    static Vector3 R1(Matrix m) => new(m.M11, m.M12, m.M13);
    static Vector3 R2(Matrix m) => new(m.M21, m.M22, m.M23);
    static Vector3 R3(Matrix m) => new(m.M31, m.M32, m.M33);
    static Vector3 R4(Matrix m) => new(m.M41, m.M42, m.M43);
    static Matrix Pose(Vector3 pos, Quaternion rot) { var mm = Matrix.CreateFromQuaternion(rot); mm.Translation = pos; return mm; }
    static Quaternion RandQ(Random rnd) => Quaternion.Normalize(new Quaternion((float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f));

    /// <summary>The slider's frames and limits (PadSlider): its rows, the poses meeting, the compression's sign, the modes, the limits.</summary>
    static void Slider()
    {
        // the rows: row 1 the frame's Up (the strut - the engine's twist axis, LinearX), row 2 its Right, a proper rotation, row 4 the point
        var q = Quaternion.Normalize(new Quaternion(-0.096531f, 0f, 0f, 0.99533f));
        var p = new Vector3(0.36025f, -0.12267f, 0.02402f);
        var m = PadSlider.Frame(p, q);
        Check(NearV(R1(m), Vector3.Transform(Vector3.Up, q)) && NearV(R2(m), Vector3.Transform(Vector3.Right, q)) && NearV(R4(m), p), "the frame's rows: 1 the strut (Up), 2 Right, 4 the point");
        Check(MathF.Abs(R1(m).Length() - 1f) < 1e-5f && MathF.Abs(Vector3.Dot(R1(m), R2(m))) < 1e-5f && NearV(Vector3.Cross(R1(m), R2(m)), R3(m)) && MathF.Abs(m.M44 - 1f) < 1e-6f, "the frame is orthonormal, row 3 = row 1 x row 2");
        // the poses meet: a block turned and placed anywhere, the vanilla spawn (wheel = inverse(pivot) * attach(x) * block, row vectors) -
        // the foot's frame A carried by the wheel's pose and the mount's frame B carried by the block's are the same frame shifted x along
        // the axis (the fixed angular rows hold the foot as it spawned; LinearX measures the compression)
        var rnd = new Random(7);
        var qi = Quaternion.Normalize(new Quaternion(-0.096531f, 0f, 0f, 0.99533f)); var pi = new Vector3(0.125f, -1.4f, 0.31f);   // (the idle frame, block space)
        for (int k = 0; k < 40; k++)
        {
            var qb = RandQ(rnd); var qp = RandQ(rnd);
            var pb = new Vector3((float)rnd.NextDouble() * 20f - 10f, (float)rnd.NextDouble() * 20f - 10f, (float)rnd.NextDouble() * 20f - 10f);
            var pp = new Vector3((float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f);   // (the foot's pivot, foot space)
            float x = (float)rnd.NextDouble() * 0.8f - 0.2f;
            Matrix block = Pose(pb, qb), attach = Pose(Vector3.Transform(Vector3.Up, qi) * x + pi, qi), pivot = Pose(pp, qp);
            Matrix wheel = Matrix.Invert(pivot) * attach * block;
            Matrix a = PadSlider.Frame(pp, qp) * wheel, b = PadSlider.Frame(pi, qi) * block;
            Vector3 axis = R1(b);
            Check(NearV(R1(a), R1(b), 1e-4f) && NearV(R2(a), R2(b), 1e-4f), $"spawn pose {k}: the foot's frame is not turned as the mount's (the fixed angular rows would twist it)");
            Vector3 d = R4(a) - R4(b);
            Check(MathF.Abs(Vector3.Dot(d, axis) - x) < 1e-3f && (d - axis * Vector3.Dot(d, axis)).Length() < 1e-3f, $"spawn pose {k}: A - B is not the compression {x:F3} along the axis ({Vector3.Dot(d, axis):F3}, off it {(d - axis * Vector3.Dot(d, axis)).Length():F4})");
            Check(MathF.Abs(PadSlider.Compression((Vector3D)R4(a), (Vector3D)R4(b), axis) - x) < 1e-3f, $"spawn pose {k}: Compression()");
        }
        // the modes: LinearX limited, the other five fixed (ConstraintAxisMode: 1 Limited, 2 Fixed)
        Check(PadSlider.Modes.Length == 6 && PadSlider.Modes[0] == 1 && PadSlider.Modes.Skip(1).All(v => v == 2), "the modes: the strut limited, every other axis fixed");
        // the limits: down [-extra, stroke]; up the well held at the travel; the sense
        PadSlider.Limits(0f, 0.4f, 0.16f, out float mn, out float mx);
        Check(mn == -0.16f && mx == 0.4f, $"down: {mn}..{mx}");
        PadSlider.Limits(1.2f, 0.4f, 0.16f, out mn, out mx);
        Check(mn == 1.2f && mx == 1.2f, $"retracting: held at the travel ({mn}..{mx})");
        PadSlider.Limits(0.0005f, 0.4f, 0f, out mn, out mx);
        Check(mn == 0f && mx == 0.4f, "a travel under a millimetre is down");
        PadSlider.ToAxis(-0.16f, 0.4f, 1, out float lo, out float hi);
        Check(lo == -0.16f && hi == 0.4f, "sense +1: as is");
        PadSlider.ToAxis(-0.16f, 0.4f, -1, out lo, out hi);
        Check(lo == -0.4f && hi == 0.16f, "sense -1: swapped and negated");
        // the rating: the housing's wheel's load, the stroke, the foot's mass, the extra stroke
        var f = new PadFootSpec { RatedLoad = WheelRating.RatedFor(1.5f), Stroke = 0.4f, WheelMass = 600f };
        var r = f.Rating(1f, 0.4f);
        Check(r.Valid && MathF.Abs(r.RatedMass - 33750f) < 1f && MathF.Abs(r.Extra - 0.16f) < 1e-5f && r.Diameter == 1.5f && r.WheelMass == 600f, $"the pad's rating: {r}");
        Check(MathF.Abs(f.Rating(2f, 0f).RatedMass - 67500f) < 1f && f.Rating(2f, 0f).Extra == 0f && MathF.Abs(f.Rating(9f, 0.4f).RatedMass - 101250f) < 1f, "the rating scale (clamped 0.5..3) and no extension");
    }

    static float F(JsonElement e) => (float)e.GetDouble();
    static float[] V(JsonElement e) => e.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();

    static void Variants(string root)
    {
        var spec = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tools", "meshgen", "padgear_spec.json"))).RootElement;
        string src = File.ReadAllText(Path.Combine(root, "Scripts", "Simulation", "Blocks", "AeroGearVariants.cs"));
        int n = 0;
        // (every variant in the C#: a Foot only on the pad gears - their slider and box are for pads only)
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
            float[] Quat(string key) { var mm = Regex.Match(body, key + @" = new Quaternion\((-?[0-9.]+)f?, (-?[0-9.]+)f?, (-?[0-9.]+)f?, (-?[0-9.]+)f?\)"); return mm.Success ? new[] { 1, 2, 3, 4 }.Select(i => float.Parse(mm.Groups[i].Value, C)).ToArray() : null; }
            // the slider's frame A: the foot's pivot dummy as the generator writes it
            var pp = Vec("PivotPosition"); var pq = Quat("PivotOrientation"); var sp = V(L.GetProperty("pivot")); var spq = V(L.GetProperty("pivot_q"));
            Check(pp != null && pq != null && Enumerable.Range(0, 3).All(i => MathF.Abs(pp[i] - sp[i]) < 2e-5f) && Enumerable.Range(0, 4).All(i => MathF.Abs(pq[i] - spq[i]) < 1e-5f), $"{size}: the variant's pivot (the slider's frame A) is not the spec's");
            // the rating: the housing's wheel's (PadFootSpec.HousingWheel), its sag on the oleo within the stroke
            float hw = PadFootSpec.HousingWheel[n - 1];
            Check(Near(F(L.GetProperty("rated")), WheelRating.RatedFor(hw)), $"{size}: rated {F(L.GetProperty("rated"))} kg, want the {hw} m wheel's {WheelRating.RatedFor(hw)}");
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

            // the sag at the rated load on the oleo (WheelOleo, the default extension): inside the stroke, above its middle - the foot rides
            var fr = new PadFootSpec { RatedLoad = F(L.GetProperty("rated")), Stroke = F(L.GetProperty("travel")), WheelMass = F(L.GetProperty("mass")) }.Rating(1f, WheelShockSettings.Extension);
            float sag = WheelOleo.SagFor(fr, fr.RatedMass, WheelRating.DesignG);
            Check(sag > 0.3f * fr.Stroke && sag < 0.8f * fr.Stroke, $"{size}: the rated sag on the oleo {sag:F3} m of a {fr.Stroke} m stroke");
        }
        Check(n == 4, $"four pad gears in the spec ({n})");
    }
}
