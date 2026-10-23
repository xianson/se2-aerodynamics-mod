#pragma warning disable
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AeroMod;

/// <summary>The pad-foot gears (2026-10-21: the vanilla suspension on a HIDDEN wheel, the foot a visual subpart hinged at the ankle and
/// turned to the wheel's ground normal - Gear/FootIk.cs, Gear/PadFoot.cs, tools/meshgen/gen_padgear_defs.py): the ankle's IK, and per
/// variant - the C# variant against the generator's spec, the hidden wheel (the vanilla wheel's model, its pivot at its centre, its
/// bottom on the foot's sole, its corner module with no drive and no steering, its client prefab hidden), the pad block's own model
/// (the wheel rest moved under the sole, the foot's dummy at the ankle pin) and subparts (leg, rod, foot), and the sag at the rated load.</summary>
static class PadFootTests
{
    static int _checks, _fails;
    static void Check(bool ok, string what) { _checks++; if (!ok) { _fails++; if (_fails <= 25) Console.WriteLine("FAIL pad: " + what); } }
    static readonly CultureInfo C = CultureInfo.InvariantCulture;
    const float Deg = MathF.PI / 180f;

    public static (int checks, int fails) Run()
    {
        Ankle();
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

    /// <summary>FootIk: the ankle turns the sole onto the ground across the hinge, clamped; nothing in the air or folding; follows smoothly.</summary>
    static void Ankle()
    {
        Vector3 up = Vector3.UnitY, axis = Vector3.UnitX;
        Check(FootIk.Target(up, up, axis) == 0f, "level ground: no turn");
        Check(FootIk.Target(up, Vector3.Zero, axis) == 0f, "no ground: no turn");
        foreach (float a in new[] { -30f, -10f, 5f, 20f })
        {
            var n = Vector3.Transform(up, Quaternion.CreateFromAxisAngle(axis, a * Deg));
            float t = FootIk.Target(up, n, axis);
            Check(MathF.Abs(t - a * Deg) < 1e-4f, $"a {a} deg slope across the hinge: {t / Deg:F3} deg");
            // the turned sole's normal is the ground's
            var turned = Vector3.Transform(up, FootIk.Turn(axis, t));
            Check(Vector3.Dot(turned, n) > 0.9999f, $"a {a} deg slope: the turned foot lies on it");
        }
        var steep = Vector3.Transform(up, Quaternion.CreateFromAxisAngle(axis, 60f * Deg));
        Check(MathF.Abs(FootIk.Target(up, steep, axis) - FootIk.MaxAngle) < 1e-5f, "a 60 deg slope: clamped to MaxAngle");
        var side = Vector3.Transform(up, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 25f * Deg));
        Check(MathF.Abs(FootIk.Target(up, side, axis)) < 1e-4f, "a slope along the hinge: a hinge cannot follow it");
        Check(FootIk.Target(up, -up, axis) == 0f, "ground beyond a quarter turn: no turn");
        var slope = Vector3.Transform(up, Quaternion.CreateFromAxisAngle(axis, 15f * Deg));
        Check(FootIk.Want(up, slope, axis, 1f) > 0.2f && FootIk.Want(up, slope, axis, 0.98f) == 0f && FootIk.Want(up, slope, axis, 0f) == 0f,
              "only fully down does the foot follow the ground: folding / stowed it sits square on its rod (flat in the well)");
        float now = 0f, want = 20f * Deg;
        for (int i = 0; i < 6; i++) now = FootIk.Follow(now, want, 1f / 60f);
        Check(now > 0f && now < want, "the ankle follows over frames, not in one");
        for (int i = 0; i < 120; i++) now = FootIk.Follow(now, want, 1f / 60f);
        Check(MathF.Abs(now - want) < 1e-3f, "and reaches its target");
        { float f = FootIk.Follow(0.3f, 0f, 5f); Check(f > 0f && f < 0.3f, $"a long frame (clamped to 0.1 s) moves part way, never past ({f:F3})"); }
        Check(FootIk.Turn(Vector3.Zero, 1f) == Quaternion.Identity, "no axis: no turn");
    }

    static float F(JsonElement e) => (float)e.GetDouble();
    static float[] V(JsonElement e) => e.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();

    static void Variants(string root)
    {
        var spec = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tools", "meshgen", "padgear_spec.json"))).RootElement;
        string src = File.ReadAllText(Path.Combine(root, "Scripts", "Simulation", "Blocks", "AeroGearVariants.cs"));
        string guids = File.ReadAllText(Path.Combine(root, "Scripts", "Simulation", "Components", "AeroBlockGuids.cs"));
        int n = 0;
        var all = Regex.Matches(src, @"new\(\) \{ Name = ""([^""]+)"", Block = new\(""([0-9a-f-]+)""\), CornerModule = new\(""([0-9a-f-]+)""\)(.*?)(?=new\(\) \{ Name =|
    \};)", RegexOptions.Singleline);
        Check(all.Count >= 8, $"variants read from AeroGearVariants.cs ({all.Count})");
        foreach (Match m in all)
        {
            bool foot = m.Groups[4].Value.Contains("Foot = new PadFootSpec");
            bool pad = m.Groups[1].Value.StartsWith("Retractable Gear (");
            Check(foot == pad, $"{m.Groups[1].Value}: a Foot {(foot ? "on a wheeled gear" : "missing on a pad gear")}");
            Check(m.Groups[4].Value.Contains("Layout = Pad(Gear(") == pad, $"{m.Groups[1].Value}: the foot's layout (Pad) {(pad ? "missing" : "on a wheeled gear")}");
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
            bool Near(float a, float b, float tol = 1e-3f) => MathF.Abs(a - b) < tol;
            float H = F(L.GetProperty("H")), r = F(L.GetProperty("wheel_r"));
            Check(Near(Num("Height"), H), $"{size}: the foot's height {Num("Height")} vs {H}");
            Check(Near(Num("WheelRadius"), r), $"{size}: WheelRadius {Num("WheelRadius")} is not the hidden wheel's {r}");
            Check(Near(Num("WheelMass"), F(L.GetProperty("mass"))) && Near(Num("Stroke"), F(L.GetProperty("travel"))), $"{size}: mass / stroke differ");

            // the geometry: the foot's ankle on the pad fork's bearings, FootDrop up the leg from the rod's hub (GearLayout.FootPoint), the wheel's bottom on its sole under the pad's centre
            string d = Path.Combine(root, "Assets", "Aero", "PadGear", size);
            var hous = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Assets", "Aero", "RetractableGear", size, $"RetractableGear_{size}_BlockModelDefinition.partialdef"))).RootElement
                .GetProperty("$Value").GetProperty("Manipulator").GetProperty("Dummies").GetProperty("Changed").EnumerateArray().Select(c => c.GetProperty("Value")).ToArray();
            Vector3 Dum(string nm) { var t = hous.First(v => v.GetProperty("Name").GetString() == nm).GetProperty("Transform").GetProperty("Transform").GetProperty("Position"); return new Vector3(F(t.GetProperty("X")), F(t.GetProperty("Y")), F(t.GetProperty("Z"))); }
            Vector3 hub = Dum("GearRodHub"), hinge = Dum("GearLegHinge");
            float drop = F(L.GetProperty("drop"));
            var dm = Regex.Match(cs.Groups[4].Value, @"Layout = Pad\(Gear\([0-9.]+f\), (-?[0-9.]+)f\)");
            Check(dm.Success && Near(float.Parse(dm.Groups[1].Value, C), drop), $"{size}: the variant's foot drop (Pad(Gear(..), drop)) is not the spec's {drop}");
            var lay = new GearLayout { HingePoint = hinge, DeployedHub = hub, HingeAxis = Vector3.UnitX, FootDrop = drop };
            var foot = V(L.GetProperty("foot")); var sole = V(L.GetProperty("sole")); var centre = V(L.GetProperty("wheel_centre"));
            Check(Vector3.Distance(new Vector3(foot[0], foot[1], foot[2]), lay.FootPoint) < 1e-3f, $"{size}: the foot's dummy is not GearLayout.FootPoint (the ankle pin)");
            var ankle = V(L.GetProperty("ankle"));
            Check(Near(sole[1], ankle[1] - H) && Near(centre[1] - r, sole[1]) && Near(centre[0], ankle[0]) && Near(centre[2], ankle[2] + F(L.GetProperty("pad_dz"))),
                  $"{size}: the hidden wheel's bottom is not the foot's sole (under the pad's centre)");
            Vector3 onFork = hub + lay.LegDirection * drop;
            Check(drop < 0f && Vector3.Distance(new Vector3(ankle[0], ankle[1], ankle[2]), onFork) < 1e-3f && Vector3.Distance(lay.AnklePoint, onFork) < 1e-3f, $"{size}: the ankle is not FootDrop up the leg from the rod's hub (on the pad fork's bearings)");
            Check(Vector3.Distance(lay.FootPoint, lay.DeployedHub) > 0.05f && MathF.Abs(Vector3.Dot(lay.FootPoint - lay.AnklePoint, Vector3.Normalize(lay.HingeAxis))) > 0.05f
                  && Vector3.Cross(lay.FootPoint - lay.AnklePoint, lay.HingeAxis).Length() < 1e-4f,
                  $"{size}: the foot's dummy is not on the hinge line, or the client would take it for the rod (FindParts: 5 cm)");

            // the pad block's own model: the wheel rest moved, the foot's dummy at the ankle pin; its subparts: leg, rod, foot
            JsonElement Man(string f) => JsonDocument.Parse(File.ReadAllText(Path.Combine(d, f))).RootElement.GetProperty("$Value").GetProperty("Manipulator");
            var bmd = Man($"PadGear_{size}_BlockModelDefinition.partialdef");
            Check(bmd.GetProperty("Guid").GetString() == L.GetProperty("block_model").GetString(), $"{size}: the block model's GUID is not the spec's");
            var ch = bmd.GetProperty("Dummies").GetProperty("Changed").EnumerateArray().ToArray();
            var restPos = ch.First(c => c.GetProperty("Kind").GetString() == "Update").GetProperty("Value").GetProperty("Transform").GetProperty("Transform").GetProperty("Position");
            var rp = V(L.GetProperty("rest_p"));
            Check(Near(F(restPos.GetProperty("Y")), rp[1]) && Near(F(restPos.GetProperty("Z")), rp[2]), $"{size}: the wheel rest is not moved under the sole");
            var fd = ch.First(c => c.GetProperty("Kind").GetString() == "Insert").GetProperty("Value");
            var fdp = fd.GetProperty("Transform").GetProperty("Transform").GetProperty("Position");
            Check(fd.GetProperty("Name").GetString() == "GearFoot" && Near(F(fdp.GetProperty("Y")), foot[1]), $"{size}: no GearFoot dummy at the ankle pin");
            string csp = File.ReadAllText(Path.Combine(d, $"PadGear_{size}_ConditionalSubPartsDefinition.def"));
            Check(csp.Contains(L.GetProperty("foot_subpart").GetString()), $"{size}: the foot is not one of the block's subparts");
            string rod = L.GetProperty("rod_subpart").GetString();
            Check(csp.Contains(rod) && File.Exists(Path.Combine(d, "Rod", "Models", $"PadGear_{size}_Rod.model")),
                  $"{size}: the pad fork (its own rod: padgear_rod.py) is not the block's rod subpart");
            string scomp = File.ReadAllText(Path.Combine(d, $"PadGear_{size}_ServerComposition.partialdef"));
            Check(scomp.Contains(L.GetProperty("module").GetString()) && scomp.Contains(L.GetProperty("block_model").GetString()), $"{size}: the block is not on its own model and the hidden wheel's corner module");

            // the hidden wheel: its own invisible model (a 1 mm triangle, a collision puck of its radius), its pivot at its centre, its mass,
            // no drive / steering
            string wd = Path.Combine(d, "Foot");
            JsonElement WMan(string f) => JsonDocument.Parse(File.ReadAllText(Path.Combine(wd, f))).RootElement.GetProperty("$Value").GetProperty("Manipulator");
            string W = $"PadGear_{size}_HiddenWheel";
            var mcd = WMan(W + "_ModelComponentDefinition.partialdef");
            Check(mcd.TryGetProperty("Model", out var wm) && wm.GetString() == "{G}" + JsonDocument.Parse(File.ReadAllText(Path.Combine(wd, "Models", W + ".model.meta"))).RootElement.GetProperty("$Value").GetProperty("AssetID").GetString(), $"{size}: the hidden wheel is not on its own invisible model");
            var wmodel = JsonDocument.Parse(File.ReadAllText(Path.Combine(wd, "Models", W + ".model"))).RootElement.GetProperty("$Value");
            Check(wmodel.GetProperty("GeometryAssets").GetArrayLength() == 1 && wmodel.GetProperty("CollisionShapesAsset").GetProperty("AssetReference").ValueKind == JsonValueKind.String,
                  $"{size}: the hidden wheel's model is not one tiny mesh and a collider");
            var puck = JsonDocument.Parse(File.ReadAllText(Path.Combine(wd, "Models", W + "_collision.gltf"))).RootElement.GetProperty("accessors")[0];
            Check(Near(F(puck.GetProperty("max")[0]), r, 0.01f) && Near(F(puck.GetProperty("max")[1]), 0.05f * r, 0.01f), $"{size}: the hidden wheel's collision puck is not its radius");
            var piv = mcd.GetProperty("Dummies").GetProperty("Changed")[0].GetProperty("Value").GetProperty("Transform").GetProperty("Transform").GetProperty("Position");
            Check(F(piv.GetProperty("X")) == 0f && F(piv.GetProperty("Y")) == 0f && F(piv.GetProperty("Z")) == 0f, $"{size}: the hidden wheel's pivot is not its centre");
            Check(F(WMan(W + "_ColliderDefinition.partialdef").GetProperty("WheelMass")) == F(L.GetProperty("mass")), $"{size}: the hidden wheel's mass is not the spec's");
            var mod = WMan($"PadGear_{size}_WheelCornerModuleDefinition.partialdef");
            Check(mod.GetProperty("WheelPrefab").GetString() == L.GetProperty("wheel_server").GetString() && F(mod.GetProperty("MaxTorque")) == 0f && F(mod.GetProperty("MaxSteeringAngle")) == 0f,
                  $"{size}: the corner module is not the hidden wheel's, or drives / steers");
            var fm = JsonDocument.Parse(File.ReadAllText(Path.Combine(wd, "Models", $"PadGear_{size}_Foot.model"))).RootElement.GetProperty("$Value").GetProperty("CollisionShapesAsset");
            Check(fm.GetProperty("AssetReference").ValueKind == JsonValueKind.Null, $"{size}: the foot has a collider (it is visual only)");
            string pdef = File.ReadAllText(Path.Combine(d, $"PadGear_{size}_PowerableBlockDefinition.partialdef"));
            Check(pdef.Contains("\"" + name + "\""), $"{size}: the block's name");

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
