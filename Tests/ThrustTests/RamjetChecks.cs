using System.IO;
using System.Text.Json;
using AeroMod;

// THE RAMJETS (2026-10-17, the user: "wire the new RAMJET blocks"; docs/PROPULSOR_SPECS.md "Ramjets"): RamjetLaw (thrust x Mach /
// density / inlet, the hydrogen burn's duty, the spike schedule), the generated table (thrust 1.5 x the vanilla atmospheric of its
// kit, hydrogen 1/3 of its paired hydrogen thruster's per kN), the speed ladder (the ramjet leads above ~Mach 2), the game code that
// carries it, and the content gen_propulsor_defs.py writes for it.
static partial class Program
{
    static void RamjetChecks()
    {
        var rows = PropulsorBlocks.Rows.Where(r => r.Kind == AeroPropulsorComponent.Kinds.Ramjet).OrderBy(r => r.StaticThrust).ToList();
        Check(rows.Count == 4 && string.Join(",", rows.Select(r => r.Size)) == "050,150,250,750", $"ramjet: four sizes in the generated table ({string.Join(", ", rows.Select(r => r.Name))})");

        // 1. thrust vs Mach (sea level, straight in): none below Mach 0.5, the peak at 2.5 .. 3.5, gone past 6
        float F(float m) => RamjetLaw.Factor(m, 1f, 1f);
        bool none = true; for (float m = 0f; m <= 0.5f; m += 0.01f) if (F(m) != 0f) none = false;
        float peak = 0f, peakAt = 0f; for (float m = 0f; m <= 8f; m += 0.01f) if (F(m) > peak) { peak = F(m); peakAt = m; }
        bool gone = new[] { 6.01f, 6.5f, 8f, 15f, float.PositiveInfinity }.All(m => F(m) == 0f);
        Check(none && F(0.6f) > 0f && float.IsFinite(F(float.NaN)) && F(float.NaN) == 0f && F(-1f) == 0f,
            $"ramjet thrust: none from rest to Mach 0.5 (no static thrust: it needs a boost), {F(0.8f):F2} at 0.8; NaN / negative Mach 0");
        Check(MathF.Abs(F(2.5f) - 1f) < 1e-4f && MathF.Abs(F(3f) - 1.05f) < 1e-4f && MathF.Abs(F(3.5f) - 1f) < 1e-4f && peakAt >= 2.5f && peakAt <= 3.5f && F(2f) < 1f && F(4f) < 1f,
            $"ramjet thrust: 100 % of its rating at Mach 2.5 and 3.5, {F(3f):F2} at 3 - the peak ({peak:F3} at Mach {peakAt:F2}) in 2.5 .. 3.5 ({F(2f):F2} at 2, {F(4f):F2} at 4)");
        Check(gone && F(5.5f) > 0f && F(5.5f) < 0.5f, $"ramjet thrust: fading by Mach 6 ({F(5.5f):F2} at 5.5, {F(6f):F2} at 6), gone past it");
        var vanillaAtmo = new (string kit, float thrust)[] { ("Atmo100", 40000f), ("Atmo250", 287136.3f), ("Atmo500", 1516383f), ("Atmo1000", 15465370f) };
        bool rated = rows.Count == 4 && rows.Select((r, i) => r.StaticThrust / vanillaAtmo[i].thrust).All(k => k > 1.49f && k < 1.51f);
        Check(rated, $"ramjet ratings: 1.5 x the vanilla atmospheric of its kit at the curve's 100 % ({string.Join(", ", rows.Select((r, i) => $"{r.Name} {r.StaticThrust / 1e3f:F0} kN = x{r.StaticThrust / vanillaAtmo[Math.Min(i, 3)].thrust:F3} {vanillaAtmo[Math.Min(i, 3)].kit}"))})");

        // 2. zero in vacuum; the density scales it like the other propulsors (capped at RhoMax)
        Check(RamjetLaw.Factor(3f, 0f, 1f) == 0f && RamjetLaw.Factor(3f, -1f, 1f) == 0f && MathF.Abs(RamjetLaw.Factor(3f, 0.5f, 1f) - 0.525f) < 1e-4f
              && MathF.Abs(RamjetLaw.Factor(3f, 5f, 1f) - 1.05f * AeroPropulsorComponent.RhoMax) < 1e-4f,
            $"ramjet: zero in vacuum (rho 0: {RamjetLaw.Factor(3f, 0f, 1f)}), half the thrust in half the air ({RamjetLaw.Factor(3f, 0.5f, 1f):F3} at Mach 3), no more than x{AeroPropulsorComponent.RhoMax} in thick air");

        // 3. the inlet unstart applied (the game's write pass: every 3 ticks, InletAngle.Step with one state per engine)
        {
            const float dt = 3f / 60f, v = 1000f, m = 3f;
            var s = InletAngle.NewState();
            float At(float th) => RamjetLaw.Factor(m, 1f, InletAngle.Step(InletAngle.Ramjet, ref s, th, v, dt));
            float straight = At(0f); float off = 0f;
            for (int i = 0; i < 6; i++) off = At(12f);                      // 0.3 s 12 deg off: unstarted
            bool unstarted = s.Unstarted && s.Unstarts == 1;
            float back = 0f, t = 0f, relit = -1f;
            for (int i = 0; i < 60; i++) { back = At(3f); t += dt; if (relit < 0f && back > 0.99f * straight) relit = t; }   // straight again: relights after the delay and spool
            Check(MathF.Abs(straight - 1.05f) < 1e-4f && unstarted && off <= InletAngle.Ramjet.Residual * 1.05f + 1e-4f,
                $"ramjet inlet: full straight in ({straight:F3} at Mach 3); 12 deg off the airflow - it unstarts, the thrust to {off:F4} of the rating ({s.Unstarts} unstart)");
            Check(relit >= InletAngle.Ramjet.RelightDelay && relit <= InletAngle.Ramjet.RelightDelay + InletAngle.Ramjet.SpoolTime + 2 * dt && !s.Unstarted,
                $"ramjet inlet: back inside 6 deg it relights after the {InletAngle.Ramjet.RelightDelay:F1} s delay and spools up: full thrust again after {relit:F2} s");
            var s2 = InletAngle.NewState(); float slow = 0f;
            for (int i = 0; i < 10; i++) slow = RamjetLaw.Factor(0.04f, 1f, InletAngle.Step(InletAngle.Ramjet, ref s2, 45f, 15f, dt));
            Check(!s2.Unstarted && ReferenceEquals(InletAngle.Of(ThrustProfiles.Ramjet), InletAngle.Ramjet), "ramjet inlet: below 20 m/s the flow counts as straight (no unstart); its curve maps to the ramjet inlet");
        }

        // 4. fuel: the rate at full 1/3 of its paired vanilla hydrogen thruster's per kN; the burn follows the thrust, none at none
        var pair = new (string name, float thrust, float rate)[] { ("H50", 60000f, 0.75f), ("H200", 359468.8f, 4f), ("H250", 1895631f, 12f), ("H750", 19395660f, 120f) };
        bool third = rows.Count == 4 && rows.Select((r, i) => (r.PowerKw / r.StaticThrust) / (pair[i].rate / pair[i].thrust)).All(k => MathF.Abs(k - 1f / 3f) < 0.002f);
        Check(third, $"ramjet fuel: 1/3 of its paired hydrogen thruster's hydrogen per kN ({string.Join(", ", rows.Select((r, i) => $"{r.Name} {r.PowerKw}/s = {r.PowerKw / r.StaticThrust * 1e3f:F5} per kN vs {pair[Math.Min(i, 3)].name} {pair[Math.Min(i, 3)].rate / pair[Math.Min(i, 3)].thrust * 1e3f:F5}"))})");
        bool dutyOk = true; var dutyNotes = new List<string>();
        foreach (float d in new[] { 0f, 0.02f, 0.25f, 0.5f, 0.7333f, 0.97f, 1f, 1.05f })
        {
            var fs = new RamjetLaw.FuelState(); int on = 0, switches = 0; bool last = false; const int N = 1200;
            for (int i = 0; i < N; i++) { bool b = RamjetLaw.Burn(ref fs, d); if (b) on++; if (i > 0 && b != last) switches++; last = b; }
            float want = Math.Clamp(d, 0f, 1f), got = on / (float)N;
            if (MathF.Abs(got - want) > 1.5f / RamjetLaw.FuelWindow / (N / RamjetLaw.FuelWindow) + 0.002f || switches > 2 * N / RamjetLaw.FuelWindow) dutyOk = false;
            dutyNotes.Add($"{d:F2}->{got:F3} ({switches} switches)");
        }
        Check(dutyOk, $"ramjet fuel: the game's burn duty-cycled to the thrust share, at most two switches per {RamjetLaw.FuelWindow}-tick window ({string.Join(", ", dutyNotes)})");
        {
            var fs = new RamjetLaw.FuelState(); int burned = 0;
            foreach (float m in new[] { 0f, 0.2f, 0.45f, 6.5f })   // (no thrust: below Mach 0.5, past 6)
                for (int i = 0; i < 120; i++) if (RamjetLaw.Burn(ref fs, RamjetLaw.FuelDuty(RamjetLaw.Factor(m, 1f, 1f)))) burned++;
            for (int i = 0; i < 120; i++) if (RamjetLaw.Burn(ref fs, RamjetLaw.FuelDuty(RamjetLaw.Factor(3f, 0f, 1f)))) burned++;   // (vacuum)
            // per kN of the thrust it has: the burn x its rate over the thrust x its rating - 1/3 of the paired thruster's at any factor <= 1
            var r = rows[2]; float perKnFull = r.PowerKw / r.StaticThrust, worst = 0f;
            foreach (float m in new[] { 0.8f, 1.2f, 2f, 2.5f, 4f, 5.5f })
            {
                float f = RamjetLaw.Factor(m, 1f, 1f), duty = RamjetLaw.FuelDuty(f);
                worst = MathF.Max(worst, MathF.Abs(r.PowerKw * duty / (r.StaticThrust * f) / perKnFull - 1f));
            }
            Check(burned == 0 && worst < 1e-4f, $"ramjet fuel: no hydrogen when it gives no thrust (below Mach 0.5, past 6, vacuum: {burned} burning ticks); per kN of the thrust it has, its rate (worst {worst:E1} off)");
        }

        // 5. the spike: forward below Mach 1.6, full travel from 3.2, linear between, never beyond its free travel (each size)
        {
            bool sched = true, within = true; var notes = new List<string>();
            foreach (var r in rows)
            {
                float free = r.SpikeTravel;
                if (!(free > 0f)) sched = false;
                if (RamjetLaw.SpikeTravel(0f, free) != 0f || RamjetLaw.SpikeTravel(1.6f, free) != 0f || MathF.Abs(RamjetLaw.SpikeTravel(2.4f, free) - free / 2f) > 1e-6f
                    || MathF.Abs(RamjetLaw.SpikeTravel(3.2f, free) - free) > 1e-6f || MathF.Abs(RamjetLaw.SpikeTravel(9f, free) - free) > 1e-6f || RamjetLaw.SpikeTravel(float.NaN, free) != 0f) sched = false;
                float x = 0f;
                for (float m = 0f; m <= 10f; m += 0.01f) { float tr = RamjetLaw.SpikeTravel(m, free); if (tr < 0f || tr > free + 1e-6f) within = false; }
                for (int i = 0; i < 600; i++) { x = RamjetLaw.StepSpike(x, i < 300 ? free * 5f : -1f, free, 1f / 60f); if (x < 0f || x > free + 1e-6f) within = false; }
                notes.Add($"{r.Name} {free * 1000f:F1} mm");
            }
            float y = RamjetLaw.StepSpike(0f, 1f, 1f, 0.5f);
            Check(sched, $"ramjet spike: forward to Mach 1.6, half its travel at 2.4, full from 3.2 and held ({string.Join(", ", notes)})");
            Check(within && MathF.Abs(y - 0.5f / RamjetLaw.SpikeTravelSeconds) < 1e-5f, $"ramjet spike: never beyond its free travel, never forward of rest (driven past both ends); a full travel takes {RamjetLaw.SpikeTravelSeconds} s");
            var expected = new Dictionary<string, float> { ["Ramjet_050"] = 0.0375f, ["Ramjet_150"] = 0.1125f, ["Ramjet_250"] = 0.5375f, ["Ramjet_750"] = 0.7875f };
            Check(rows.All(r => expected.TryGetValue(r.Name, out float e) && MathF.Abs(r.SpikeTravel - e) < 1e-6f), "ramjet spike: the table's free travels are the modeller's (37.5 / 112.5 / 537.5 / 787.5 mm)");
        }

        // 6. the speed ladder (sea level, axial, absolute thrust per block of a kit): the turboprop's band, then the jet's, then the ramjet's
        {
            float Jet(float m) => JetCurve.Factor(m);
            float crossRating = -1f; for (float m = 0.5f; m <= 3f; m += 0.005f) if (F(m) > Jet(m)) { crossRating = m; break; }   // per rating
            float crossKit = -1f; for (float m = 0.5f; m <= 3f; m += 0.005f) if (1.5f * F(m) > Jet(m)) { crossKit = m; break; }  // per block of a kit (1.5 x)
            bool leads = true; for (float m = 2f; m <= 5.9f; m += 0.01f) if (!(F(m) > Jet(m)) || !(1.5f * F(m) > Jet(m))) leads = false;
            var tp = PropulsorBlocks.Rows.First(r => r.Kind == AeroPropulsorComponent.Kinds.Turboprop && r.Size == "250");
            float Tp(float m) { float v = m * ThrustProfiles.SeaLevelSoundSpeed; return AeroPropulsorComponent.Speed(AeroPropulsorComponent.Kinds.Turboprop, v, v, m) * tp.StaticThrust; }
            var r5 = rows[2];
            bool beatsTp = true; for (float m = 2f; m <= 5.9f; m += 0.01f) if (!(F(m) * r5.StaticThrust > Tp(m))) beatsTp = false;
            bool jetBelow = true; for (float m = 0f; m <= 1.2f; m += 0.01f) if (1.5f * F(m) > Jet(m)) jetBelow = false;
            Console.WriteLine("   speed ladder (sea level, axial; thrust per block of the Atmo500 kit, kN: the 2.5 m turboprop, the vanilla Atmo500, the Ramjet_250):");
            foreach (float m in new[] { 0f, 0.3f, 0.6f, 0.9f, 1.2f, 1.5f, 2f, 2.5f, 3f, 4f, 5f, 6f })
                Console.WriteLine($"   M {m,3:F1}  turboprop {Tp(m) / 1e3f,6:F0}  Atmo500 {Jet(m) * vanillaAtmo[2].thrust / 1e3f,6:F0}  ramjet {F(m) * r5.StaticThrust / 1e3f,6:F0}");
            Console.WriteLine($"   the ramjet passes the jet from Mach {crossKit:F2} per block of a kit (its 1.5 x rating), from {crossRating:F2} per rated newton");
            Check(leads && beatsTp && crossRating > 1.6f && crossRating < 2.1f && crossKit > 1f && crossKit < 2f && jetBelow,
                $"speed ladder: the ramjet leads from ~Mach 2 to 6 (per rated newton from Mach {crossRating:F2}, per block of its kit from {crossKit:F2}); the jet leads it through Mach 1.2; the turboprop is nowhere above Mach 2");
        }

        // 7. the game code: the ramjet's write carries the inlet and the density; the Jet curve skips it; its burn follows its thrust
        {
            string sim = SimDir();
            string pc = File.ReadAllText(Path.Combine(sim, "Propulsion", "AeroPropulsorComponent.cs"));
            int at = pc.IndexOf("void Tick()", StringComparison.Ordinal), end = pc.IndexOf("void StepRotor()", StringComparison.Ordinal);
            string tick = at > 0 && end > at ? pc.Substring(at, end - at) : "";
            Check(tick.Contains("InletAngle.Step(InletAngle.Ramjet, ref _inlet") && tick.Contains("RamjetLaw.Factor(Mach, Density, Inlet)") && tick.Contains("IsRamjet(Kind)"),
                "AeroPropulsorComponent: a ramjet's maximum = rating x RamjetLaw.Factor (Mach curve x density x its own inlet state, InletAngle.Ramjet)");
            int fa = pc.IndexOf("void FuelTick()", StringComparison.Ordinal), fe = pc.IndexOf("/// <summary>The world transform of the thruster's OWN grid", StringComparison.Ordinal);
            string fuel = fa > 0 && fe > fa ? pc.Substring(fa, fe - fa) : "";
            Check(pc.Contains("[Before(typeof(Keen.Game2.Simulation.GridUpdateOrder.ResourceDistributorUpdate))]") && fuel.Contains("RamjetLaw.Burn(ref _fuel") && fuel.Contains("OnStopConsuming()")
                  && fuel.Contains("firing && b.CurrentState") && !fuel.Contains("new ") && !System.Text.RegularExpressions.Regex.IsMatch(fuel, @"\btry\b") && !fuel.Contains("$\""),
                "AeroPropulsorComponent: the ramjet's burn held off before the game's resource distributor charges the tick, switched on only while the game fires it; no allocation, try or formatting per tick");
            Check(!JetCurve.Applies("Hydrogen", true) && !JetCurve.Applies("Hydrogen", false), "Jet curve: never on a ramjet (hydrogen class, and a propulsor block)");
            string jet = File.ReadAllText(Path.Combine(sim, "Propulsion", "AeroThrustSettingsComponent.Jet.cs"));
            Check(jet.Contains("PropulsorBlocks.RowOf(Entity) != null") && jet.Contains("JetCurve.Applies("), "Jet curve component: a propulsor row (a ramjet too) is left alone - no second Mach curve or inlet factor");
            string cache = File.ReadAllText(Path.Combine(sim, "Propulsion", "OffsetThrustJob.Cache.cs"));
            Check(cache.Contains("AirBreathing = isAtmo || ramjet") && cache.Contains("if (ramjet) profile = ThrustProfiles.Ramjet;"), "thruster cache: a ramjet is air-breathing (a flow-through engine box) and reports its Ramjet curve");
            string inj = File.ReadAllText(Path.Combine(sim, "Injection", "EntityInjector.cs"));
            Check(inj.Contains("PropulsorBlocks.RamjetClientPrefabs.Contains(entity.Guid)") && inj.Contains("typeof(AeroRamjetSpikeComponent)"), "injection: the ramjets' client prefabs get the spike drive");
            string spike = File.ReadAllText(Path.Combine(sim, "Propulsion", "AeroRamjetSpikeComponent.cs"));
            int sa = spike.IndexOf("void Step()", StringComparison.Ordinal), se = spike.IndexOf("void FindSpike()", StringComparison.Ordinal);
            string step = sa > 0 && se > sa ? spike.Substring(sa, se - sa) : "";
            Check(step.Contains("RamjetLaw.SpikeTravel(") && step.Contains("RamjetLaw.StepSpike(") && step.Contains("AeroEntryFx.TryGetFlow(") && !step.Contains("$\"") && !System.Text.RegularExpressions.Regex.IsMatch(step, @"\btry\b")
                  && !System.Text.RegularExpressions.Regex.IsMatch(step, @"new (?!Vector3|RelativeTransform)"),
                "spike drive: RamjetLaw's schedule at the grid's published Mach, rate-limited; no allocation, try or formatting per frame");
        }

        // 8. the content gen_propulsor_defs.py wrote (the Blocks project): the hydrogen chain, our numbers, the spike, the ports, the menu
        {
            string root = Path.GetFullPath(Path.Combine(SimDir(), "..", "..", "..", "Aerodynamics Mod Blocks", "Assets", "Aero", "Propulsors"));
            if (!Directory.Exists(root)) { Console.WriteLine($"   (no Blocks project at {root}: content checks skipped)"); return; }
            JsonElement V(string rel) => JsonDocument.Parse(File.ReadAllText(Path.Combine(root, rel))).RootElement.GetProperty("$Value");
            bool chain = true, numbers = true, spikeOk = true, ports = true; var why = new List<string>();
            foreach (var r in rows)
            {
                string n = r.Name;
                var thr = V($"{n}/{n}_ThrusterDefinition.partialdef");
                var man = thr.GetProperty("Manipulator");
                if (thr.GetProperty("BaseDefinition").GetString() != "2fa7936c-0ecb-4294-b709-c772eb3d5c09") { chain = false; why.Add(n + " thruster base"); }
                if (MathF.Abs(man.GetProperty("ThrustPower").GetSingle() - r.StaticThrust) > 0.5f || MathF.Abs(man.GetProperty("ResourcesRequiredToThrust").GetSingle() - r.PowerKw) > 1e-4f) { numbers = false; why.Add(n + " numbers"); }
                if (V($"{n}/{n}_PowerableBlockDefinition.partialdef").GetProperty("BaseDefinition").GetString() != "5b8af618-cb27-4501-9ee1-d52912f8dc79") { chain = false; why.Add(n + " block base (hydrogen)"); }
                if (V($"{n}/{n}_ServerComposition.partialdef").GetProperty("BaseDefinition").GetString() != "88aa4df4-20f4-479c-84db-7095f4cc7852") { chain = false; why.Add(n + " server composition base"); }
                var cc = V($"{n}/{n}_ClientComposition.partialdef").GetProperty("Manipulator").GetProperty("Components");
                bool csp = cc.GetProperty("Changed").EnumerateArray().Any(c => c.GetProperty("Kind").GetString() == "Insert" && c.GetProperty("Value").GetProperty("Type").GetString().EndsWith("ConditionalSubPartsComponent"));
                if (!csp) { spikeOk = false; why.Add(n + " no ConditionalSubPartsComponent"); }
                var subs = V($"{n}/{n}_ConditionalSubPartsDefinition.def").GetProperty("SubParts");
                var spikePrefab = V($"{n}/Subparts/{n}_Spike_Client.def").GetProperty("_entity");
                if (subs.EnumerateObject().Count() != 1 || spikePrefab.GetProperty("DebugName").GetString() != "RamjetSpike") { spikeOk = false; why.Add(n + " spike subpart"); }
                if (File.Exists(Path.Combine(root, n, "Subparts", $"{n}_Rotor_InfiniteRotationAnimationDefinition.def"))) { spikeOk = false; why.Add(n + " a spin definition"); }
                var dummies = V($"{n}/{n}_BlockModelDefinition.partialdef").GetProperty("Manipulator").GetProperty("Dummies").GetProperty("Changed").EnumerateArray().Select(c => c.GetProperty("Value")).ToList();
                int conv = dummies.Count(d => d.GetProperty("Type").GetString() == "29d45e5b-2ddf-4ba5-9031-885260550947");
                var sp = dummies.FirstOrDefault(d => d.GetProperty("Name").GetString() == "RamjetSpike");
                if (conv != 2) { ports = false; why.Add($"{n} {conv} conveyor ports"); }
                // (the user's port places, 2026-10-17: centre z per block - gen_propulsor_defs.py RAMJET_PORT_Z - + the 0.125 model offset)
                float wantZ = n switch { "Ramjet_050" => 0.25f, "Ramjet_150" => 1.00f, "Ramjet_250" => 1.25f, "Ramjet_750" => 3.75f, _ => float.NaN } + 0.125f;
                foreach (var d in dummies.Where(d => d.GetProperty("Type").GetString() == "29d45e5b-2ddf-4ba5-9031-885260550947"))
                {
                    var pos = d.GetProperty("Transform").GetProperty("Transform").GetProperty("Position");
                    if (!(MathF.Abs(pos.GetProperty("Z").GetSingle() - wantZ) < 1e-4f) || MathF.Abs(pos.GetProperty("Y").GetSingle() - 0.125f) > 1e-4f) { ports = false; why.Add($"{n} port {d.GetProperty("Name").GetString()} at z {pos.GetProperty("Z").GetSingle()} (want {wantZ})"); }
                }
                if (sp.ValueKind != JsonValueKind.Object || MathF.Abs(sp.GetProperty("Transform").GetProperty("Transform").GetProperty("Position").GetProperty("Z").GetSingle() - (0.125f - r.Hub)) > 1e-4f) { spikeOk = false; why.Add(n + " spike dummy"); }
            }
            var kind = V("Ramjet_BlockKind.def"); var group = V("Propulsor_Ramjets_BlockGroup.def");
            bool menu = kind.GetProperty("BlockGroups")[0].GetProperty("Value").GetString() == group.GetProperty("Guid").GetString() && group.GetProperty("Name").GetString() == "Ramjets"
                        && kind.GetProperty("Description").GetString().Contains("hydrogen");
            Check(chain, $"ramjet content: the vanilla HydrogenThruster250 chain (thruster, block, compositions){(why.Count > 0 ? ": " + string.Join(", ", why) : "")}");
            Check(numbers, "ramjet content: ThrustPower and ResourcesRequiredToThrust are the table's");
            Check(spikeOk, "ramjet content: one plain translating spike subpart (no spin), its dummy at the spike's pivot, a ConditionalSubPartsComponent added to the client");
            Check(ports, "ramjet content: two conveyor ports, on the +X / -X side faces at the user's places (hydrogen in; the user, 2026-10-17)");
            Check(menu, "ramjet content: its own G-menu group (Ramjets); the description says it burns hydrogen");
        }
    }
}
