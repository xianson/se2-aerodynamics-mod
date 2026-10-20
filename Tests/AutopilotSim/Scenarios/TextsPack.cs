using AeroMod;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// THE MOD'S TEXTS (2026-10-20): every text key the game looks up for this mod is in its en-US texts file
/// (Assets/Texts/en-US/AeroMod.loc-texts) - else the game shows the raw key ("ApBehaviour_Auto") and logs "Could not find key".
/// The keys: every [Label] / [Group] / [Button("...")] of every mod panel (*BlockDetailModel.cs, read as source - the models need
/// the game), every choice of an enum a panel shows ("&lt;Type&gt;_&lt;Value&gt;": the game's localized enum names), the blocks' names and
/// descriptions in their definitions (UIData, BlockKind, BlockGroup), and the probe ModTexts reads them in by (Ui/ModTexts.cs).
/// The Content copy is the Assets file as built (the P_DEFAULT processor copies it).
/// </summary>
sealed class TextsPack : IScenarioPack
{
    public void Register(ScenarioRegistry r)
    {
        r.Add("texts: every panel label, enum choice and block name is in the en-US texts file", true, Keys);
    }

    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Assets", "Texts", "en-US", "AeroMod.loc-texts"))) d = d.Parent;
        return d?.FullName;
    }

    static void Keys()
    {
        string root = Root();
        Sim.Check(root != null, "texts: the mod's texts file found from the test's folder");
        if (root == null) return;
        string file = Path.Combine(root, "Assets", "Texts", "en-US", "AeroMod.loc-texts");
        Dictionary<string, string> texts;
        try { texts = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)); }
        catch (Exception e) { Sim.Check(false, "texts: the texts file is a JSON object of strings - " + e.Message); return; }
        string built = Path.Combine(root, "Content", "Texts", "en-US", "AeroMod.loc-texts");
        Sim.Check(File.Exists(built) && File.ReadAllText(built).Replace("\r", "") == File.ReadAllText(file).Replace("\r", ""),
            "texts: Content/Texts/en-US/AeroMod.loc-texts is the Assets file (rebuild the content, or copy it, after an edit)");
        Sim.Check(texts.ContainsKey("AeroModTexts_Probe"), "texts: the probe key ModTexts reads the texts in by (AeroClientHost.ModTextsProbe)");

        var need = new SortedDictionary<string, string>(StringComparer.Ordinal);   // key -> where
        need["AeroModTexts_Probe"] = "AeroClientHost.ModTextsProbe";
        var asm = typeof(ApPanel).Assembly;
        int models = 0, enums = 0;
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "Scripts"), "*BlockDetailModel.cs", SearchOption.AllDirectories))
        {
            string src = File.ReadAllText(f), name = Path.GetFileNameWithoutExtension(f);
            models++;
            foreach (Match m in Regex.Matches(src, @"\[(?:Label|Group|Button)\(""((?:[^""\\]|\\.)*)""\)\]"))
                need.TryAdd(Regex.Unescape(m.Groups[1].Value), name);
            // (an enum property shown on the panel: each of its values' "<Type>_<Value>" - the game's localized enum names)
            foreach (Match m in Regex.Matches(src, @"public (\w+) \w+\s*\{\s*get"))
            {
                var t = asm.GetType("AeroMod." + m.Groups[1].Value);
                if (t == null || !t.IsEnum) continue;
                enums++;
                foreach (var v in Enum.GetNames(t)) need.TryAdd(t.Name + "_" + v, name + " (" + t.Name + ")");
            }
        }
        Sim.Check(models >= 6 && enums >= 6, $"texts: the mod's panels read ({models} models, {enums} enum choices)");
        // (the blocks' names and descriptions: UIData, BlockKind, BlockGroup - text keys too)
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "Assets"), "*def", SearchOption.AllDirectories))
        {
            if (!f.EndsWith(".def") && !f.EndsWith(".partialdef")) continue;
            string s = File.ReadAllText(f);
            foreach (Match u in Regex.Matches(s, @"""UIData""\s*:\s*\{(.*?)\}", RegexOptions.Singleline))
                foreach (Match m in Regex.Matches(u.Groups[1].Value, @"""(?:Name|Description)""\s*:\s*""((?:[^""\\]|\\.)*)"""))
                    need.TryAdd(Regex.Unescape(m.Groups[1].Value), Path.GetFileName(f));
            if (s.Contains("BlockKindDefinition") || s.Contains("BlockGroupDefinition"))
                foreach (Match m in Regex.Matches(s, @"""(?:Name|Description)""\s*:\s*""((?:[^""\\]|\\.)*)"""))
                    need.TryAdd(Regex.Unescape(m.Groups[1].Value), Path.GetFileName(f));
        }
        var missing = need.Where(kv => !string.IsNullOrEmpty(kv.Key) && !texts.ContainsKey(kv.Key)).ToList();
        foreach (var kv in missing) Sim.Note($"texts: missing '{kv.Key}' ({kv.Value})");
        Sim.Check(missing.Count == 0, $"texts: all {need.Count} keys in the texts file ({missing.Count} missing: {string.Join(" | ", missing.Take(8).Select(kv => kv.Key))})");
        // (no key left behind by a rename: each one is looked up by something above)
        var unused = texts.Keys.Where(k => !need.ContainsKey(k)).ToList();
        foreach (var k in unused) Sim.Note($"texts: unused '{k}'");
        Sim.Check(unused.Count == 0, $"texts: every key in the file is used ({unused.Count} unused: {string.Join(" | ", unused.Take(8))})");
        // (the autopilot's choice names in code are the texts file's: ApPanelLayout.Names)
        var differ = ApPanelLayout.Names.Where(kv => !texts.TryGetValue(kv.Key, out var t) || t != kv.Value).Select(kv => kv.Key).ToList();
        Sim.Check(differ.Count == 0, $"texts: the panel's choice names (ApPanelLayout.Names) are the texts file's ({string.Join(", ", differ)})");
        // (a text never empty: the game's label lookup falls back to the key on an empty text)
        var empty = texts.Where(kv => string.IsNullOrEmpty(kv.Value)).Select(kv => kv.Key).ToList();
        Sim.Check(empty.Count == 0, $"texts: no empty text ({string.Join(", ", empty)})");
    }
}
