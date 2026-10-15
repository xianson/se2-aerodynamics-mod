// ApiTests - the public API's offline checks (dotnet run -c Release; exit 0 = all pass; "dump" prints every member's
// signature as the frozen table below wants it).
// 1. THE SHAPE, from source (Roslyn, syntax only - AeroApi itself needs the game): every public member of AeroMod.AeroApi
//    across its partial files (Scripts/Simulation/Api/AeroApi*.cs) - no name declared twice (callers look members up by
//    name: an overload makes a name-only lookup throw); every type in a signature one a caller can name without aero
//    (.NET primitives, string, the engine's Entity, VRage math, Delegate, arrays of these; List<T> only where version <= 6
//    used it); every member of versions 2-6 still declared with exactly its frozen signature (members are only added);
//    every member and the current version in docs/API.md; no try/catch in the version-7+ files (per-tick query paths).
// 2. THE EVENT QUEUE (Api/SeqRing.cs): numbers from 0, only up; the newest Capacity kept; a reader from an old number
//    starts at the oldest kept and sees the gap.
// 4. THE CROSS-MOD LINK (Api/CrossModLink.cs, OrbitalLink's rule): the LAST loaded copy of the other mod's API wins, an
//    assembly change binds anew at once (a hot reload), a member is matched by name and parameter types (a wrong shape is
//    no binding, never a throw), a throwing member waits 10 s doubling to 300 s, and an assembly change ends that wait.
// 3. THE HULL'S FILM (Structure/Model/HullHeat.Api.cs): the published h and T0 give back each face's heat in exactly, as
//    another heat model would take it: FaceHeatW = h x area x (T0 - the skin before the step).
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using AeroMod;

static class Program
{
    static int _fails, _checks;
    static void Check(bool ok, string what) { _checks++; if (!ok) { _fails++; Console.WriteLine("FAIL " + what); } else if (Environment.GetEnvironmentVariable("VERBOSE") != null) Console.WriteLine("ok   " + what); }

    /// <summary>Versions 2-6, frozen (2026-10-15): name -> "return(parameter types)", namespaces stripped. Never edit a line;
    /// a new member is a new name.</summary>
    static readonly Dictionary<string, string> Frozen = new()
    {
        ["Version"] = "const int",
        ["TryGetFlight"] = "bool(Entity, out Vector3, out float, out float, out float, out float)",
        ["TryGetForces"] = "bool(Entity, out Vector3, out Vector3, out float, out float, out float)",
        ["TryGetLift"] = "bool(Entity, out Vector3, out float)",
        ["TryGetGroundHeight"] = "bool(Entity, out float)",
        ["TryGetEntry"] = "bool(Entity, out float, out bool)",
        ["GetFxConsistency"] = "string(bool)",
        ["EntryStrength"] = "float(float, float)",
        ["GetWingTips"] = "int(Entity, List<Vector3>, List<Vector3>)",
        ["GetSurface"] = "int(Entity, List<Vector3>, List<Vector3>, List<float>, List<float>)",
        ["GetWingCount"] = "int(Entity)",
        ["TryGetWing"] = "bool(Entity, int, out Vector3, out Vector3, out Vector3, out Vector3, out float, out float, out float, out float, out float, out float, out float)",
        ["TryGetWingState"] = "bool(Entity, int, out Vector3, out Vector3, out Vector3, out float, out float, out float)",
        ["TryGetWingStrain"] = "bool(Entity, int, out float, out float, out float)",
        ["TryGetStructure"] = "bool(Entity, out float, out float, out float)",
        ["NoteMoved"] = "void(Entity)",
        ["SetFocusGrid"] = "void(Entity)",
        ["TryGetStall"] = "bool(Entity, out int, out float, out float, out float)",
        ["TryGetLoac"] = "bool(Entity, out int, out float)",
        ["GetMaxWingStrain"] = "float(Entity)",
        ["PredictHullForces"] = "bool(Entity, Vector3, float, float, out Vector3, out Vector3, out float)",
        ["RequestForceTable"] = "bool(Entity)",
        ["SampleForceTable"] = "bool(Entity, Vector3, float[])",
        ["TryDesign"] = "bool(Entity, Vector3, float, out Vector3, out Vector3, out Vector3, out float, out float, out float)",
        ["GetTopSpeed"] = "float(Entity, Vector3)",
        ["GetThrustAlong"] = "float(Entity, Vector3, float, out Vector3)",
        ["TryGetCenterOfMass"] = "bool(Entity, out Vector3)",
        ["GetWavePeak"] = "float(float)",
        ["GetTopSpeedAt"] = "float(Entity, Vector3, float)",
        ["GetControlRatio"] = "float(Entity, Vector3, float)",
        ["GetMass"] = "float(Entity)",
        ["GetDesignPressures"] = "int(Entity, Vector3, float, List<Vector3>, List<Vector3>, List<float>, List<float>, int)",
        ["TryGetAtmosphereAt"] = "bool(Vector3D, out float, out float, out float, out float)",
        ["TryGetAtmosphere"] = "bool(Entity, out float, out float, out float, out float)",
        ["AtmosphereAtDensity"] = "void(float, out float, out float, out float)",
        ["GetAtmosphereStatus"] = "string()",
        ["GetAtmosphereGeneratorCount"] = "int()",
        ["GetThrustProfiles"] = "int(List<string>)",
        ["ThrustEffectiveness"] = "float(string, float)",
        ["TryGetThrustProfile"] = "bool(string, out float, out float)",
        ["GetThrusters"] = "int(Entity, List<Entity>, List<string>, List<float>)",
        ["TryGetChangeCounters"] = "bool(Entity, out int, out int, out int)",
        ["GetGeneration"] = "int()",
        ["GetAeroBlocks"] = "int(Entity, List<string>, List<Vector3I>, List<Vector3>)",
        ["GetAeroBlockState"] = "int(Entity, Vector3I, List<string>, List<float>)",
        ["GetEnabled"] = "bool()",
        ["SetEnabled"] = "bool(bool)",
        ["GetSettings"] = "int(List<string>)",
        ["TryGetSettingInfo"] = "bool(string, out string, out double, out double, out double, out string, out bool, out string)",
        ["TryGetSetting"] = "bool(string, out double)",
        ["SetSetting"] = "bool(string, double, out string)",
        ["GetSettingsVersion"] = "int()",
        ["RegisterEntrySource"] = "int(string, Delegate)",
        ["UnregisterEntrySource"] = "bool(string)",
        ["HasEntrySource"] = "bool(string, Delegate)",
        ["SetDrag"] = "bool(Entity, string, Vector3, float)",
        ["SetPlateDrag"] = "bool(Entity, string, Vector3, float, Vector3)",
        ["ClearDrag"] = "int(Entity, string)",
        ["TryGetDrag"] = "bool(Entity, string, out Vector3, out float, out Vector3)",
        ["SuppressEffects"] = "void(Entity, bool)",
    };

    static readonly HashSet<string> Atoms = new() { "void", "bool", "int", "long", "float", "double", "string", "Entity", "Vector3", "Vector3D", "Vector3I", "Quaternion", "Delegate" };

    static string Strip(string type) => Regex.Replace(Regex.Replace(type, @"\b[A-Za-z_][A-Za-z0-9_]*\.", ""), @"\s+", "");

    static bool AllowedType(string t, bool legacy)
    {
        t = Strip(t);
        if (t.EndsWith("[]")) t = t.Substring(0, t.Length - 2);
        if (legacy && t.StartsWith("List<") && t.EndsWith(">")) t = t.Substring(5, t.Length - 6);
        return Atoms.Contains(t) && (t != "void" || true);
    }

    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Scripts", "Simulation", "Api", "AeroApi.cs"))) d = d.Parent;
        return d?.FullName ?? throw new Exception("no Scripts/Simulation/Api/AeroApi.cs above " + AppContext.BaseDirectory);
    }

    static void Shape(bool dump)
    {
        string root = Root(), api = Path.Combine(root, "Scripts", "Simulation", "Api");
        var files = Directory.GetFiles(api, "AeroApi*.cs").OrderBy(f => f).ToList();
        var members = new List<(string name, string sig, string file, MemberDeclarationSyntax node)>();
        int version = -1;
        foreach (var f in files)
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(f));
            var cls = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c => c.Identifier.Text == "AeroApi").ToList();
            Check(cls.Count == 1, $"{Path.GetFileName(f)}: one AeroApi class ({cls.Count})");
            foreach (var c in cls)
            {
                Check(c.Modifiers.Any(SyntaxKind.PartialKeyword) || files.Count == 1, $"{Path.GetFileName(f)}: AeroApi is partial");
                if (Path.GetFileName(f) != "AeroApi.cs")
                    Check(!c.DescendantNodes().OfType<TryStatementSyntax>().Any(), $"{Path.GetFileName(f)}: no try/catch (the version-7+ query paths)");
                foreach (var m in c.Members)
                {
                    if (!m.Modifiers.Any(SyntaxKind.PublicKeyword)) continue;
                    switch (m)
                    {
                        case MethodDeclarationSyntax md:
                            string ps = string.Join(", ", md.ParameterList.Parameters.Select(p => (p.Modifiers.Count > 0 ? p.Modifiers.ToString() + " " : "") + Strip(p.Type.ToString()).Replace(",", ", ")));
                            members.Add((md.Identifier.Text, $"{Strip(md.ReturnType.ToString())}({ps})", f, md));
                            Check(md.TypeParameterList == null, $"{md.Identifier.Text}: not generic");
                            Check(md.Modifiers.Any(SyntaxKind.StaticKeyword), $"{md.Identifier.Text}: static");
                            break;
                        case FieldDeclarationSyntax fd:
                            foreach (var v in fd.Declaration.Variables)
                            {
                                members.Add((v.Identifier.Text, (fd.Modifiers.Any(SyntaxKind.ConstKeyword) ? "const " : "") + Strip(fd.Declaration.Type.ToString()), f, fd));
                                if (v.Identifier.Text == "Version" && v.Initializer != null) version = int.Parse(v.Initializer.Value.ToString());
                            }
                            break;
                        default:
                            Check(false, $"{Path.GetFileName(f)}: a public member that is not a method or a field: {m.ToString().Split('\n')[0]}");
                            break;
                    }
                }
            }
        }
        if (dump) { foreach (var m in members) Console.WriteLine($"        [\"{m.name}\"] = \"{m.sig}\","); return; }

        Check(members.Count > Frozen.Count, $"shape: {members.Count} public members found");
        // no name twice (an overload)
        foreach (var g in members.GroupBy(m => m.name).Where(g => g.Count() > 1))
            Check(false, $"overloaded: {g.Key} declared {g.Count()} times ({string.Join(" | ", g.Select(x => x.sig))})");
        // the frozen members, unchanged
        foreach (var (name, sig) in Frozen)
        {
            var m = members.FirstOrDefault(x => x.name == name);
            Check(m.name != null, $"frozen: {name} still declared");
            if (m.name != null) Check(m.sig == sig, $"frozen: {name} is {sig} (now {m.sig})");
        }
        // types a caller can name
        foreach (var m in members)
        {
            bool legacy = Frozen.ContainsKey(m.name);
            if (m.node is MethodDeclarationSyntax md)
            {
                Check(AllowedType(md.ReturnType.ToString(), legacy), $"{m.name}: return type {md.ReturnType} is one a caller can name");
                foreach (var p in md.ParameterList.Parameters)
                {
                    Check(AllowedType(p.Type.ToString(), legacy), $"{m.name}: parameter {p.Identifier.Text} of type {Strip(p.Type.ToString())} is one a caller can name{(legacy ? "" : " (List<T> only up to version 6: arrays)")}");
                    Check(!p.Modifiers.Any(SyntaxKind.RefKeyword) && !p.Modifiers.Any(SyntaxKind.InKeyword) && !p.Modifiers.Any(SyntaxKind.ParamsKeyword), $"{m.name}: parameter {p.Identifier.Text} is plain or out");
                    Check(p.Default == null, $"{m.name}: parameter {p.Identifier.Text} has no default (a reflection caller passes every argument)");
                }
            }
            else Check(m.name == "Version", $"{m.name}: the only public field is Version");
        }
        // documented
        Check(version >= 7, $"version: {version}");
        string doc = File.ReadAllText(Path.Combine(root, "docs", "API.md"));
        Check(doc.Contains($"## Version {version}"), $"docs/API.md has a 'Version {version}' section");
        foreach (var m in members)
            Check(m.name == "Version" || doc.Contains(m.name + "("), $"docs/API.md documents {m.name}");
        // (A8, 2026-10-15) the readers of lists the simulation thread clears and refills read published copies or take the
        // list's lock - iterated bare they tore or threw (~200 ms an exception), caught by a try
        foreach (var (name, live) in new[] { ("GetSurface", "FxSurface"), ("GetThrusters", "_thrusterCache"), ("GetAeroBlocks", "catch"), ("GetAeroBlockState", "catch") })
        {
            var m = members.FirstOrDefault(x => x.name == name);
            if (m.node is not MethodDeclarationSyntax md) { Check(false, $"{name}: declared"); continue; }
            string body = md.Body?.ToString() ?? "";
            Check(!md.DescendantNodes().OfType<TryStatementSyntax>().Any() && !body.Contains(live) && (name.StartsWith("GetAero") ? body.Contains("lock (reg.SyncRoot)") : true),
                $"{name}: reads a published copy or under the list's lock, never the live list in a try");
        }
        // (2026-10-15 review) GetSurface fills the caller's lists after the grid's API lock, never inside it: 4 x 10^5 List.Adds
        // with LOH growth held the lock the grid's simulation tick waits on
        {
            var m = members.FirstOrDefault(x => x.name == "GetSurface");
            var locks = m.node?.DescendantNodes().OfType<LockStatementSyntax>().ToList() ?? new();
            Check(locks.Count > 0 && locks.All(l => !l.Statement.ToString().Contains(".Add(")), "GetSurface: no List.Add under the grid's API lock (copied out, added after)");
        }
        // (2026-10-15 review) each reader pushes on only the record it reads (ApiGridState.Watch.cs): one window for all kept a
        // flight-state reader's grid publishing its whole structure every evaluation, and a structure reader the tick record
        foreach (var (name, rec) in new[] { ("GetFlightState", "RecFlight"), ("GetControlSurfaces", "RecFlight"), ("GetThrusterForces", "RecFlight"),
            ("GetSurface", "RecHull"), ("TryGetStructureCounters", "RecStruct"), ("GetStructureBlocks", "RecStruct"), ("GetStructureBlockState", "RecStruct"),
            ("GetStructureBlockLoads", "RecStruct"), ("SetBlockTemperatures", "RecStruct"), ("GetHeatFaces", "RecFaces"), ("GetHeatFaceState", "RecFaces") })
        {
            var m = members.FirstOrDefault(x => x.name == name);
            var watches = m.node?.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => i.Expression.ToString() == "Watch").Select(i => i.ArgumentList.Arguments.Last().ToString()).ToList() ?? new();
            Check(watches.Count == 1 && watches[0] == "ApiGridState." + rec, $"{name}: watches {rec} only ({string.Join(", ", watches)})");
        }
        // a new member says its version in its summary
        foreach (var m in members.Where(x => !Frozen.ContainsKey(x.name)))
        {
            string trivia = m.node.GetLeadingTrivia().ToFullString();
            Check(Regex.IsMatch(trivia, @"\(version \d+\)"), $"{m.name}: its summary says '(version N)'");
        }
    }

    static void Ring()
    {
        var r = new SeqRing<int>(4);
        Check(r.Next == 0 && r.First == 0, "ring: empty");
        for (int i = 0; i < 3; i++) Check(r.Add(100 + i) == i, $"ring: item {i} numbered {i}");
        lock (r.Lock) Check(r.First == 0 && r.NextUnlocked == 3 && r.At(1) == 101, "ring: three kept, read by number");
        for (int i = 3; i < 10; i++) r.Add(100 + i);
        lock (r.Lock)
        {
            Check(r.NextUnlocked == 10 && r.First == 6, $"ring: ten added, the newest four kept (first {r.First})");
            bool ok = true; for (long s = r.First; s < r.NextUnlocked; s++) ok &= r.At(s) == 100 + s;
            Check(ok, "ring: each kept item at its number");
        }
    }

    static void Film()
    {
        // a 4 x 4 x 2 block slab of 0.5 m blocks (2 x 2 x 2 cells each), travelling edge-first along -Z at 2 km/s, thin air
        var boxBlock = new List<int>(); var lo = new List<Vector3I>(); var hi = new List<Vector3I>(); var mass = new List<float>(); var mat = new List<int>();
        for (int x = 0; x < 4; x++) for (int y = 0; y < 4; y++) for (int z = 0; z < 2; z++)
        {
            boxBlock.Add(mass.Count); lo.Add(new Vector3I(2 * x, 2 * y, 2 * z)); hi.Add(new Vector3I(2 * x + 1, 2 * y + 1, 2 * z + 1)); mass.Add(33.1f); mat.Add(StructMaterial.Armor);
        }
        var st = ShipStructure.Build(boxBlock, lo, hi, mass, mat);
        var boxes = new List<(Vector3I, Vector3I)>(); for (int k = 0; k < lo.Count; k++) boxes.Add((lo[k], hi[k]));
        var surf = new SmoothSurfaceProvider { CellSize = 0.25f, CellOffset = 0f };
        surf.BeginBuild(new SnapshotGridAccessor(boxes), 0.5f);
        while (!surf.AddCellBatch(int.MaxValue)) { }
        surf.FinalizeBuild();
        var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var area = new List<float>(); var blk = new List<int>();
        foreach (var f in surf.Faces)
        {
            pos.Add(f.Position); nrm.Add(f.Normal); area.Add(f.Area);
            var raw = f.RawNormal.LengthSquared() > 0.5f ? f.RawNormal : f.Normal;
            blk.Add(st.BlockAt(f.Position - raw * 0.125f));
        }
        var h = HullHeat.Build(st, pos, nrm, area, blk, 0.25f);
        Check(h.FaceCount > 0, $"film: {h.FaceCount} faces");
        h.Pass(new Vector3(0.3f, 0.2f, -1f));
        float worst = 0f, peak = 0f;
        for (int step = 0; step < 50; step++)
        {
            var before = (float[])h.FaceT.Clone();
            h.Step(0.002f, 250f, 2000f, 250f, 0.5f);
            for (int i = 0; i < h.FaceCount; i++)
            {
                if (h.FaceBlock[i] < 0) continue;
                float want = h.FaceFilm(i) * h.FaceArea(i) * (h.LastRecoveryK - before[i]);
                worst = MathF.Max(worst, MathF.Abs(want - h.FaceHeatW[i]) / MathF.Max(1f, MathF.Abs(h.FaceHeatW[i])));
                peak = MathF.Max(peak, h.FaceHeatW[i]);
            }
        }
        Check(peak > 0f, $"film: the faces heat ({peak:F0} W at the hottest)");
        Check(worst < 1e-4f, $"film: h x area x (T0 - skin) is each face's heat in (worst {worst:E1} relative)");
        Check(MathF.Abs(h.LastRecoveryK - (250f + 2000f * 2000f / (2f * HullHeat.CpAir))) < 0.5f, $"film: T0 is the recovery temperature ({h.LastRecoveryK:F0} K)");
        h.Step(0f, 250f, 0f, 250f, 0.5f);
        bool still = true; for (int i = 0; i < h.FaceCount; i++) still &= h.FaceFilm(i) == 0f;
        Check(still, "film: no air, no film");
    }

    /// <summary>A stand-in for another mod's build: an assembly with OrbitalMod.OrbitalApi whose Which() returns
    /// <paramref name="which"/> and whose Boom() throws.</summary>
    static System.Reflection.Assembly FakeApi(int which)
    {
        var ab = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new System.Reflection.AssemblyName("FakeOrbital" + which), System.Reflection.Emit.AssemblyBuilderAccess.Run);
        var tb = ab.DefineDynamicModule("m").DefineType("OrbitalMod.OrbitalApi", System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.Abstract | System.Reflection.TypeAttributes.Sealed);
        var sa = System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static;
        var il = tb.DefineMethod("Which", sa, typeof(int), Type.EmptyTypes).GetILGenerator();
        il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4, which); il.Emit(System.Reflection.Emit.OpCodes.Ret);
        il = tb.DefineMethod("Boom", sa, typeof(int), Type.EmptyTypes).GetILGenerator();
        il.Emit(System.Reflection.Emit.OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor(Type.EmptyTypes)); il.Emit(System.Reflection.Emit.OpCodes.Throw);
        tb.CreateType();
        return ab;
    }

    static void Link()
    {
        var loaded = new List<System.Reflection.Assembly> { typeof(object).Assembly };
        var api = new LastLoadedType("OrbitalMod.OrbitalApi", () => loaded.ToArray()) { CheckSeconds = 0 };
        var which = new CrossModBinding<Func<int>>(api, "OrbitalApi", "Which", Type.EmptyTypes);
        long now = System.Diagnostics.Stopwatch.GetTimestamp(), f = System.Diagnostics.Stopwatch.Frequency;
        string st = "-";
        Check(which.GetAt(now, ref st) == null && api.Type == null && st.Contains("not found"), $"link: no other mod - no binding ({st})");
        loaded.Add(FakeApi(1));
        var fn = which.GetAt(now, ref st);   // (the type appeared: a new generation, bound at once - no retry wait)
        Check(fn != null && fn() == 1 && api.GenerationAt(now) == 1 && st == "bound to OrbitalApi.Which", $"link: bound to the first copy ({st})");
        loaded.Add(FakeApi(2));
        fn = which.GetAt(now, ref st);
        Check(fn != null && fn() == 2 && api.GenerationAt(now) == 2, "link: a reload (a second copy) - bound again to the LAST copy");
        loaded.Add(typeof(Program).Assembly);
        var again = which.GetAt(now, ref st);
        Check(ReferenceEquals(again, fn) && api.GenerationAt(now) == 2, "link: an unrelated assembly loading - same copy, same generation, same delegate");
        string s2 = "-";
        Check(new CrossModBinding<Func<int>>(api, "OrbitalApi", "Which", new[] { typeof(int) }).GetAt(now, ref s2) == null && s2.Contains("not found"), $"link: matched by parameter types ({s2})");
        s2 = "-";
        Check(new CrossModBinding<Func<string>>(api, "OrbitalApi", "Which", Type.EmptyTypes).GetAt(now, ref s2) == null && s2.Contains("unexpected signature"), $"link: a wrong return type - no binding, no throw ({s2})");
        // a member that throws: dropped, then 10 s, 20 s ... 300 s before the next lookup
        var boom = new CrossModBinding<Func<int>>(api, "OrbitalApi", "Boom", Type.EmptyTypes);
        var b = boom.GetAt(now, ref st);
        bool threw = false;
        try { b(); } catch (InvalidOperationException e) { threw = true; boom.Fault(e, ref st); }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Check(threw && st.Contains("threw"), $"link: the throw reported ({st})");
        Check(boom.GetAt(t0 + 9 * f, ref st) == null, "link: after a throw, no lookup for 10 s");
        Check(boom.GetAt(t0 + 11 * f, ref st) != null, "link: looked up again after 10 s");
        boom.Fault(new InvalidOperationException(), ref st); t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Check(boom.GetAt(t0 + 19 * f, ref st) == null && boom.GetAt(t0 + 21 * f, ref st) != null, "link: a second fault in a row waits 20 s");
        boom.Fault(new InvalidOperationException(), ref st); t0 = System.Diagnostics.Stopwatch.GetTimestamp();   // (the third: 40 s)
        loaded.Add(FakeApi(3));
        // (the clock only forward: the reads above were at times ahead of now)
        Check(boom.GetAt(t0 + 30 * f, ref st) != null, "link: an assembly change ends the fault wait (a fixed mod binds at once)");
        double[] want = { 10, 20, 40, 80, 160, 300, 300 };
        bool ok = true; for (int n = 1; n <= want.Length; n++) ok &= CrossModBinding<Func<int>>.FaultSeconds(n) == want[n - 1];
        Check(ok, "link: fault waits 10, 20, 40, 80, 160, 300, 300 s");
        loaded.RemoveAll(a => a.GetName().Name.StartsWith("FakeOrbital"));
        Check(which.GetAt(t0 + 31 * f, ref st) == null && api.Type == null, $"link: the mod gone - unbound ({st})");
    }

    // 5. THE WATCH WINDOWS (Api/ApiGridState.Watch.cs, 2026-10-15 review): one per record - a read keeps its own record on,
    //    never the others; the simulation tick runs for the tick record or the hull's faces only.
    static void Watches()
    {
        var s = new ApiGridState();
        long t = 1_000_000, w = 5 * System.Diagnostics.Stopwatch.Frequency;
        bool none = true; for (int r = 0; r < ApiGridState.RecCount; r++) none &= !s.On(r, t);
        Check(none && !s.TickWatched(t), "watch: nothing on before a read");
        s.Push(ApiGridState.RecFlight, t + w);
        Check(s.On(ApiGridState.RecFlight, t) && !s.On(ApiGridState.RecStruct, t) && !s.On(ApiGridState.RecFaces, t) && !s.On(ApiGridState.RecHull, t) && s.TickWatched(t),
            "watch: a flight read keeps the flight record on, not the structure's, the faces' or the hull's");
        Check(!s.On(ApiGridState.RecFlight, t + w) && !s.TickWatched(t + w), "watch: the flight record off 5 s after its last read");
        s.Push(ApiGridState.RecFlight, t + w / 2);
        Check(s.Until[ApiGridState.RecFlight] == t + w, "watch: an earlier push never shortens a window");
        var s2 = new ApiGridState();
        s2.Push(ApiGridState.RecStruct, t + w);
        Check(s2.On(ApiGridState.RecStruct, t) && !s2.On(ApiGridState.RecFlight, t) && !s2.TickWatched(t), "watch: a structure read keeps the structure on, never the tick record");
        s2.Push(ApiGridState.RecFaces, t + w);
        Check(s2.On(ApiGridState.RecFaces, t) && !s2.TickWatched(t), "watch: a heat-face read is the evaluation's, not the tick's");
        var s3 = new ApiGridState();
        s3.Push(ApiGridState.RecHull, t + w);
        Check(s3.TickWatched(t) && !s3.On(ApiGridState.RecFlight, t), "watch: a GetSurface read runs the tick for the hull's faces only");

        // releasing (2026-10-15 review: the arrays were kept for the life of the grid, ~12-15 MB on a big ship)
        long f = System.Diagnostics.Stopwatch.Frequency, rel = (long)(ApiGridState.ReleaseSeconds * f);
        var a = new ApiGridState();
        Check(ApiGridState.Fit(new float[10], 1000).Length == 1000 && ApiGridState.Fit(new float[10], 4).Length == 10, "release: Fit grows to exactly the count, never doubles");
        a.Push(ApiGridState.RecStruct, t + w);
        int n = 50_000;
        lock (a.Lock)
        {
            a.Layout = 3; a.BlockCount = n; a.StateLayout = 3;
            a.Util = new float[n]; a.UtilB = new float[n]; a.Health = new float[n]; a.LoadF = new Vector3[n]; a.LoadMB = new Vector3[n];
            a.Held[ApiGridState.RecStruct] = true;
        }
        Check(!a.Due(ApiGridState.RecStruct, t) && !a.Due(ApiGridState.RecStruct, t + w) && !a.Due(ApiGridState.RecStruct, t + w + rel - 1),
            "release: kept while watched and for ReleaseSeconds after the window closed");
        Check(a.Due(ApiGridState.RecStruct, t + w + rel) && !a.Due(ApiGridState.RecFlight, t + w + rel), "release: due after that - only a record that holds arrays");
        Check(a.TickWatched(t + w + rel), "release: the tick runs while a record holds arrays (it drops them)");
        lock (a.Lock) a.Release(ApiGridState.RecStruct);
        Check(a.Util.Length == 0 && a.UtilB.Length == 0 && a.Health.Length == 0 && a.LoadF.Length == 0 && a.LoadMB.Length == 0, "release: the block state's arrays dropped, front and back");
        Check(a.StateLayout == -1 && a.Layout == 3 && a.BlockCount == n, "release: the state reads 0 until published again (StateLayout); the layout kept");
        Check(!a.AnyHeld && !a.Due(ApiGridState.RecStruct, t + 10 * w) && !a.TickWatched(t + 10 * w), "release: nothing held, the tick idle again");
        var h = new ApiGridState();
        lock (h.Lock)
        {
            h.HullCount = n; h.HullPos = new Vector3[n]; h.HullCpB = new float[n]; h.HullOf = new object(); h.HullVersion = 2; h.Held[ApiGridState.RecHull] = true;
            h.FlightStamp = 123; h.SurfCount = 2; h.SurfCmd = new float[2]; h.ThrCount = 3; h.ThrForce = new float[3]; h.Held[ApiGridState.RecFlight] = true;
            h.FaceLayout = 1; h.FaceCount = n; h.FacePos = new Vector3[n]; h.FaceFilmB = new float[n]; h.Held[ApiGridState.RecFaces] = true;
            h.ExtLayout = 1; h.ExtTempK = new float[n]; h.Held[ApiGridState.RecExt] = true;
            for (int r = 0; r < ApiGridState.RecCount; r++) h.Release(r);
        }
        Check(h.HullCount == -1 && h.HullOf == null && h.HullVersion == -1 && h.HullPos.Length == 0 && h.HullCpB.Length == 0, "release: the hull's faces dropped (GetSurface answers 0 until the next publish)");
        Check(h.FlightStamp == 0 && h.SurfCount == 0 && h.ThrCount == 0 && h.SurfCmd.Length == 0 && h.ThrForce.Length == 0 && h.Flight.Length == AeroApi.FlightStateLength,
            "release: the tick record unpublished (GetFlightState answers 0 as on a first read)");
        Check(h.FaceLayout == -1 && h.FaceCount == 0 && h.FacePos.Length == 0 && h.FaceFilmB.Length == 0, "release: the heat faces dropped");
        Check(h.ExtLayout == -1 && h.ExtTempK.Length == 0 && !h.AnyHeld, "release: another model's temperatures dropped");
    }

    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "dump") { Shape(true); return 0; }
        Shape(false); Ring(); Film(); Link(); Watches();
        Console.WriteLine($"ApiTests: {_checks - _fails}/{_checks} passed");
        return _fails == 0 ? 0 : 1;
    }
}
