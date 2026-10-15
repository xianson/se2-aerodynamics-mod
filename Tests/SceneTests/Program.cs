// SceneTests - ONE SCENE PER THREAD, checked from source (dotnet run -c Release; exit 0 = all pass).
// SE2 runs a CLIENT scene (the main thread) and a SERVER scene (the in-process server's thread) in one process. A component's
// IInSceneListener.OnAddedToScene fires for BOTH copies of an entity, so a static list filled there holds both scenes'
// entities; a loop over it on one thread that reads the other scene's entities (alive, transform, data, components) is a
// cross-thread read - the game's DataGuard asserts ("Concurrent access (read) ... Reason: IsEntityAlive, Last Writer:
// SyncEntities on thread 'Main Thread'", found in game 2026-10-15 in the Orbital Mod's StationKeepCharge) and it is a data race.
// 1. THE REGISTRIES: every static collection a component adds itself to in OnAddedToScene is classed below - BOTH scenes'
//    copies, or one scene's only (and why). A new one fails here until it is classed.
// 2. THE LOOPS: every foreach over a both-scenes collection that reads an element's engine data (its Data other than .Scene,
//    an alive check, a component lookup) first keeps to its own scene: ReferenceEquals(x.Entity.Data.Scene, <scene>) - the
//    test is in the loop before the first read. (A loop that reads only mod-owned fields needs none.)
// 3. THE SITES (each fixed 2026-10-15): the client never reads the server copy's entity where the server publishes what it
//    needs - TearSmoke.Pairs (the event's pose), StructureFeel (Live, not alive), the flight job's pair line (the flow record),
//    AeroApi's suppressed list (the flow record's grid; pruned by Live).
// 4. THE DESIGN PROBE (2026-10-15): DesignProbe reads a server grid's engine data (transform, mass, gyros, thrusters, block
//    boxes, joint search) only in the server grid's own step (PublishDesign / PublishPose, called from AeroSimJobCore on the
//    server scene); every probe works on the snapshots (DesignSnapshot.cs: engine-free - compiled and run here,
//    DesignSnapshotTests.cs). The dev reads of the server blocks (the surface / gear / airbrake summaries, the panel probe's
//    nearest settings) go through ServerDevReads, answered on the server step.
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static class Program
{
    static int _fails, _checks;
    static void Check(bool ok, string what) { _checks++; if (!ok) { _fails++; Console.WriteLine("FAIL " + what); } else if (Environment.GetEnvironmentVariable("VERBOSE") != null) Console.WriteLine("ok   " + what); }

    /// <summary>Static collections components add themselves to in OnAddedToScene: true = both scenes' copies land there.</summary>
    static readonly Dictionary<string, (bool both, string why)> Registries = new()
    {
        ["AeroGridComponent.Live"] = (true, "every grid composition, client and server (EntityInjector: CubeGridComponent)"),
        ["AeroThrustSettingsComponent.All"] = (true, "every thruster composition, client and server (EntityInjector: ThrusterComponent; its Server flag)"),
        ["AeroSurfaceSettingsComponent.All"] = (false, "the control surfaces' SERVER prefabs only (AeroBlockGuids.SurfaceServerPrefabs)"),
        ["AeroGearComponent.All"] = (false, "the gears' SERVER prefabs only (AeroBlockGuids.GearServerPrefabs)"),
        ["AeroAirbrakeComponent.All"] = (false, "the airbrakes' SERVER prefabs only (AeroBlockGuids.AirbrakeServerPrefabs)"),
    };

    static string Root()
    {
        // (SCENE_ROOT: another checkout's tree - how the checks were seen failing on the code before the fixes)
        var env = Environment.GetEnvironmentVariable("SCENE_ROOT");
        if (!string.IsNullOrEmpty(env)) return env;
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Scripts", "Simulation", "Api", "AeroApi.cs"))) d = d.Parent;
        return d?.FullName ?? throw new Exception("no Scripts/Simulation/Api/AeroApi.cs above " + AppContext.BaseDirectory);
    }

    static string ClassOf(SyntaxNode n) => n.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "";

    /// <summary>A collection expression as Type.Member (a bare name inside its own class gets the class's name).</summary>
    static string Qualify(ExpressionSyntax e, SyntaxNode at)
    {
        string s = Regex.Replace(e.ToString(), @"\s+", "");
        if (!s.Contains('.')) s = ClassOf(at) + "." + s;
        return Regex.Replace(s, @"^.*\.(\w+\.\w+)$", "$1");
    }

    static int Main()
    {
        string sim = Path.Combine(Root(), "Scripts", "Simulation");
        // (the dev harness copies are local and never committed: left out)
        var files = Directory.GetFiles(sim, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("AeroTestHarness.cs") && !f.EndsWith("AeroSpeedSpike.cs")).OrderBy(f => f).ToList();
        var trees = files.ToDictionary(f => f, f => CSharpSyntaxTree.ParseText(File.ReadAllText(f)).GetRoot());
        string Rel(string f) => Path.GetRelativePath(sim, f).Replace('\\', '/');

        // ── 1. the registries ──
        var seen = new HashSet<string>();
        foreach (var (f, root) in trees)
            foreach (var m in root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.Text == "OnAddedToScene"))
                foreach (var inv in m.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (inv.Expression is not MemberAccessExpressionSyntax ma || ma.Name.Identifier.Text != "Add") continue;
                    if (inv.ArgumentList.Arguments.Count != 1 || inv.ArgumentList.Arguments[0].Expression is not ThisExpressionSyntax) continue;
                    string reg = Qualify(ma.Expression, m);
                    seen.Add(reg);
                    Check(Registries.ContainsKey(reg), $"{Rel(f)}: {reg} (filled in OnAddedToScene) is classed as both scenes' or one scene's");
                }
        foreach (var r in Registries.Keys) Check(seen.Contains(r), $"registry {r} still filled in an OnAddedToScene");

        // ── 2. the loops over both scenes' registries ──
        var both = Registries.Where(kv => kv.Value.both).Select(kv => kv.Key).ToHashSet();
        both.Add("AeroApi._suppressed");   // (AeroApi.SuppressEffects: "the grid you have (server or client copy)")
        int loops = 0;
        foreach (var (f, root) in trees)
            foreach (var fe in root.DescendantNodes().OfType<ForEachStatementSyntax>())
            {
                string coll = Qualify(fe.Expression, fe);
                if (!both.Contains(coll)) continue;
                loops++;
                string v = Regex.Escape(fe.Identifier.Text), body = fe.Statement.ToString();
                var read = Regex.Match(body, $@"\b{v}\.(Entity\??\.)?Data\.(?!Scene\b)\w+|Alive\(\s*{v}\.(Entity\??\.)?Data\b|\b{v}\.Entity\??\.(TryGet|Get|GetTopLevelParent|GetSession|AsInterface)\b|\b{v}\.Data\)");
                if (!read.Success) continue;
                var scene = Regex.Match(body, $@"ReferenceEquals\(\s*{v}\.(Entity\??\.)?Data\.Scene\s*,");
                int line = fe.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                Check(scene.Success && scene.Index < read.Index, $"{Rel(f)}:{line}: foreach over {coll} reads '{read.Value}' - keeps to its own scene first");
            }
        Check(loops >= 6, $"loops over both scenes' registries found ({loops})");

        // ── 3. the sites ──
        string Text(string rel) => File.ReadAllText(Path.Combine(sim, rel));
        MethodDeclarationSyntax Method(string rel, string name) => trees[Path.Combine(sim, rel.Replace('/', Path.DirectorySeparatorChar))]
            .DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == name);

        var pairs = Method("Structure/Fx/TearSmoke.cs", "Pairs");
        Check(pairs != null && !Regex.IsMatch(pairs.ToString(), @"\b(server|\w+\.Grid)\.Data\b"), "TearSmoke.Pairs: the server grid's pose from its event, never its entity");
        var pose = Method("Structure/Fx/TearSmoke.cs", "ServerPose");
        Check(pose != null && Text("Grid/AeroSimJob.cs").Contains("if (aero.IsServerScene && TearSmoke.Pending != 0) TearSmoke.ServerPose("), "TearSmoke.ServerPose: called from the server grid's own step");
        Check(!Regex.IsMatch(Text("Structure/Fx/StructureFeel.cs"), @"_server\??\.Entity\??\.Data"), "StructureFeel: the server copy's entity never read (Live)");
        Check(!Regex.IsMatch(Text("Fx/AeroEntryFx.cs"), @"\.Aero\??\.Entity\b"), "AeroEntryFx: the flow record's server grid entity never read on the client");
        var sup = Method("Api/AeroApi.cs", "SuppressEffects");
        Check(sup != null && !Regex.IsMatch(sup.ToString(), @"_suppressed\[\w+\]\.Data"), "AeroApi.SuppressEffects: the list pruned without reading its grids (Live)");
        var serverAero = Method("Design/DesignProbe.cs", "ServerAero");
        Check(serverAero != null && serverAero.ToString().Contains("a.DesignPose") && Text("Grid/AeroSimJob.cs").Contains("DesignProbe.PublishPose(aero, wt)"),
            "DesignProbe.ServerAero: pairs on the poses the server grids publish (PublishPose, their own step)");
        var ccc = Method("Blocks/AeroBlockSave.cs", "ChangedClientCopy");
        Check(ccc != null && ccc.ToString().Contains("th.SeenAt") && Text("Grid/AeroGridComponent.Compute.cs").Contains("AeroSurfaceSaved.ClientStep(Data.Scene)"),
            "ChangedClientCopy: the client copies' places from their own step (SeenAt, ClientStep)");

        // ── 4. the design probe: the server grid's engine data on its own step only ──
        var dp = trees[Path.Combine(sim, "Design", "DesignProbe.cs")];
        // (the server step's own: PublishDesign, PublishPose, and SeaLevelMax - a thruster's entity, PublishDesign's)
        var serverStep = new HashSet<string> { "PublishDesign", "PublishPose", "SeaLevelMax" };
        const string engine = @"\.Data\b|PhysicsHack\.|GetWorldTransform|IsEntityAlive|\.TryGet<|\.Components\b(?!\.)|CaptureBoxes\(|RefreshFxGroup\(|_thrusterCache|ThrusterSnap|LiveMax\(|SeaLevelEfficiency\(|GetTopLevelParent|GetSession";
        // (code only: the comments name what they no longer do)
        static string Code(SyntaxNode n) => string.Concat(n.DescendantTokens().Select(t => t.Text));
        int probes = 0;
        foreach (var m in dp.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            string name = m.Identifier.Text, body = Code(m);
            if (serverStep.Contains(name)) continue;
            probes++;
            // (ServerAero reads the caller's own grid - its own scene; the server grids only by their published poses)
            if (name == "ServerAero") body = Regex.Replace(body, @"PhysicsHack\.Alive\(grid\.Data\)|grid\.Data\.(GetWorldTransform\(\)|TryGet<\w+>)|grid\.Components\b", "");
            var hit = Regex.Match(body, engine);
            Check(!hit.Success, $"DesignProbe.{name}: no engine read of a server grid off its step ('{hit.Value}')");
        }
        Check(probes >= 15, $"DesignProbe: its probes found ({probes})");
        Check(Regex.Matches(Code(dp), @"SeaLevelMax\(").Count == 2 && Method("Design/DesignProbe.cs", "PublishDesign").ToString().Contains("SeaLevelMax("),
            "DesignProbe.SeaLevelMax: called from PublishDesign only");
        Check(Text("Grid/AeroSimJob.cs").Contains("if (aero.IsServerScene) DesignProbe.PublishDesign(aero, wt);"), "DesignProbe.PublishDesign: called from the server grid's own step");
        var snapTree = Code(trees[Path.Combine(sim, "Design", "DesignSnapshot.cs")]);
        Check(!Regex.IsMatch(snapTree, @"Keen\.|Entity|AeroGridComponent"), "DesignSnapshot.cs: engine-free (its engine side lives in DesignProbe.cs)");
        // (the dev reads of the server blocks)
        Check(!Regex.IsMatch(Text("Blocks/AeroPanelProbe.cs"), @"AeroSurfaceSettingsComponent\.All\b"), "AeroPanelProbe: never walks the server settings itself (ServerDevReads.AskNear)");
        foreach (var (rel, which) in new[] { ("Blocks/AeroSurfaceSettingsComponent.cs", "Surfaces"), ("Integration/AeroGearComponent.cs", "Gears"), ("Integration/AeroAirbrakeComponent.cs", "Airbrakes") })
            Check(Text(rel).Contains($"public static string Summary => ServerDevReads.Summary(ServerDevReads.{which});"), $"{rel}: Summary is the server step's (ServerDevReads)");
        Check(Text("Grid/AeroSimJob.cs").Contains("if (aero.IsServerScene && ServerDevReads.Pending != 0) ServerDevReads.Step();"), "ServerDevReads.Step: called from the server grid's own step");
        DesignSnapshotTests.Run(Check);

        Console.WriteLine(_fails == 0 ? $"SceneTests: {_checks}/{_checks} passed" : $"SceneTests: {_fails} of {_checks} FAILED");
        return _fails == 0 ? 0 : 1;
    }
}
