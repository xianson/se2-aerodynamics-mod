using System.IO;
using AeroMod;

// THE VANILLA ATMOSPHERIC THRUSTERS' MACH CURVE (JetCurve, ThrustProfiles.Jet) and THE POWER LADDER (2026-10-17, the user's
// speed roles: "turboprops kings of subsonic, atmos king of transonic-ish, ramjets for supersonic").
static partial class Program
{
    static string SimDir([System.Runtime.CompilerServices.CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here), "..", "..", "Scripts", "Simulation"));

    static void JetChecks()
    {
        // 1. the curve: the spec's table exactly at its knots (rating fractions), 1 at rest, 0 past Mach 2.8, never NaN
        // (2026-10-17, the Mach wall made reachable - "fix this": the supersonic RAM RISE above the rating, below the ramjet's
        //  curve from Mach 2.0 - Tests/AeroBench machwall tunes it. 2026-10-18, the user: '1 and 2' - soften the pinch, scale
        //  the block wings' wave drag: NO PINCH - the 0.76 at Mach 1 (0.80 at 0.95, 0.95 at 1.1) gone, the ram rise from
        //  ~Mach 0.97, JetMach1 1.08 at Mach 1, to JetRamPeak 1.98 at Mach 1.6 (was 1.80 at 1.7); from Mach 1.8 on unchanged)
        var spec = new (float m, float f)[] { (0f, 1f), (0.6f, 0.95f), (0.85f, 0.85f), (1.0f, (float)ThrustProfiles.JetMach1), (1.1f, 1.28f), (1.2f, 1.48f), (1.3f, 1.68f), (1.45f, 1.94f),
            ((float)ThrustProfiles.JetRamPeakMach, (float)ThrustProfiles.JetRamPeak), (1.7f, 1.85f), (1.8f, 1.55f), (1.9f, 1.20f), (2.0f, 0.82f), (2.2f, 0.45f), (2.4f, 0.25f), (2.8f, 0f) };
        bool knots = spec.All(k => MathF.Abs(JetCurve.Factor(k.m) - k.f) < 1e-4f);
        Check(knots && JetCurve.Factor(3f) == 0f && JetCurve.Factor(float.NaN) == 1f && JetCurve.Factor(-1f) == 1f,
            $"Jet curve: the spec's knots ({string.Join(", ", spec.Select(k => $"M{k.m:F2} {JetCurve.Factor(k.m):F2}"))}), 0 past 2.8, 1 at rest / NaN");
        // (no pinch, 2026-10-18: the subsonic lapse bottoms out at Mach 0.85 - nothing deeper in the transonic band - and the
        //  curve is past its rating by Mach 1)
        float lo = 1f, loAt = 0f; for (float m = 0.6f; m <= 1.3f; m += 0.005f) if (JetCurve.Factor(m) < lo) { lo = JetCurve.Factor(m); loAt = m; }
        Check(MathF.Abs(loAt - 0.85f) < 0.011f && JetCurve.Factor(1f) > 1f, $"Jet curve: no transonic pinch - its lowest point from Mach 0.6 to 1.3 is the subsonic lapse's end ({lo:F2} at M{loAt:F2}); {JetCurve.Factor(1f):F2} at Mach 1");
        float hi = 0f, hiAt = 0f, over = -1f; bool rising = true;
        for (float m = 0f; m <= 2.8f; m += 0.005f) { float f = JetCurve.Factor(m); if (f > hi) { hi = f; hiAt = m; } if (over < 0f && m > 0.5f && f > 1f) over = m; if (m >= 0.85f && m < (float)ThrustProfiles.JetRamPeakMach - 0.01f && JetCurve.Factor(m + 0.005f) < f - 1e-6f) rising = false; }
        Check(rising && hi <= JetCurve.MaxFactor && MathF.Abs(hi - (float)ThrustProfiles.JetRamPeak) < 1e-4f && MathF.Abs(hiAt - (float)ThrustProfiles.JetRamPeakMach) < 0.006f && over > 0.9f && over < 1f,
            $"Jet curve: the ram rise - above the rating from Mach {over:F3}, rising all the way from Mach 0.85 to its top {hi:F2} at M{hiAt:F2} (<= MaxFactor {JetCurve.MaxFactor})");
        Check(ThrustProfiles.All.TryGetValue("Jet", out var jp) && ReferenceEquals(jp, ThrustProfiles.Jet) && ThrustProfiles.All.ContainsKey("Turbofan") && ThrustProfiles.All.ContainsKey("Turbojet"),
            "profiles: Jet listed by name; the old names still answer");
        // 2. who: vanilla atmospherics only - not our propulsor blocks (their own curves), not hydrogen or ion (rockets)
        Check(JetCurve.Applies("Atmospheric", false) && !JetCurve.Applies("Atmospheric", true) && !JetCurve.Applies("Hydrogen", false) && !JetCurve.Applies("Ion", false) && !JetCurve.Applies(null, false),
            "Jet curve applies: vanilla atmospheric yes; a propulsor block, hydrogen, ion no");
        // 3. the write rule (as the game part runs it every JetCurve.Period ticks): from the rating, never compounding; a game
        //    zero respected; the rating written back exactly when lifted
        {
            const float R = 287136.3f;
            var s = JetCurve.NewState(); float cur = R;
            bool w0 = JetCurve.Step(ref s, cur, true, 1f, out _);
            bool w1 = JetCurve.Step(ref s, cur, true, 0.55f, out float x1); if (w1) cur = x1;
            bool w2 = JetCurve.Step(ref s, cur, true, 0.55f, out _);
            bool w3 = JetCurve.Step(ref s, cur, true, 0.553f, out _);   // (within the write tolerance: no write)
            bool w4 = JetCurve.Step(ref s, cur, true, 0.60f, out float x4); if (w4) cur = x4;
            Check(!w0 && w1 && MathF.Abs(x1 - 0.55f * R) < 1f && !w2 && !w3 && w4 && MathF.Abs(x4 - 0.60f * R) < 1f && s.Rated == R,
                $"Jet write: the rating x the factor ({x1:F0}, then {x4:F0} N - of the rating, not compounded), none within tolerance");
            Check(MathF.Abs(JetCurve.Unshaped(in s, cur) - R) < 1f && JetCurve.Unshaped(in s, 0f) == 0f, $"Jet write: a report takes the live maximum back to the rating ({JetCurve.Unshaped(in s, cur):F0} N)");
            // the ram rise: a factor above 1 is written above the rating (clamped at MaxFactor), and a report takes it back out
            bool r1 = JetCurve.Step(ref s, cur, true, 1.8f, out float xr); if (r1) cur = xr;
            bool r2 = JetCurve.Step(ref s, cur, true, 1.8f, out _);
            Check(r1 && MathF.Abs(xr - 1.8f * R) < 1f && !r2 && s.Rated == R && MathF.Abs(JetCurve.Unshaped(in s, cur) - R) < 1f,
                $"Jet write: the ram rise above the rating ({xr:F0} N = 1.8 x the rating, read back as the rating {JetCurve.Unshaped(in s, cur):F0} N, not as the game's new rating)");
            var sc = JetCurve.NewState(); JetCurve.Step(ref sc, R, true, 5f, out float xc);
            Check(MathF.Abs(xc - JetCurve.MaxFactor * R) < 1f, $"Jet write: a factor past MaxFactor is clamped ({xc / R:F2} x the rating)");
            bool r3 = JetCurve.Step(ref s, cur, true, 0.60f, out float xr3); if (r3) cur = xr3;
            bool z1 = JetCurve.Step(ref s, 0f, true, 0.6f, out _); cur = 0f;   // the game: unpowered
            bool z2 = JetCurve.Step(ref s, cur, true, 0.9f, out _);
            Check(!z1 && !z2 && s.GameZero && !s.Applied, "Jet write: the game's zero (unpowered) is left alone");
            cur = R;   // the game: power back, its rating written
            bool b1 = JetCurve.Step(ref s, cur, true, 0.9f, out float xb); if (b1) cur = xb;
            Check(b1 && MathF.Abs(xb - 0.9f * R) < 1f && !s.GameZero, $"Jet write: the game's rating back - shaped again ({xb:F0} N)");
            bool l1 = JetCurve.Step(ref s, cur, false, 0.5f, out float xl); if (l1) cur = xl;
            bool l2 = JetCurve.Step(ref s, cur, false, 0.5f, out _);
            Check(l1 && xl == R && !l2 && !s.Applied && s.Factor == 1f, "Jet write: the curve lifted (off / aero off / no aero) - the rating written back exactly, once");
            var t = JetCurve.NewState(); float c = 1000f;
            JetCurve.Step(ref t, c, true, 0.7f, out float y1); c = y1;
            bool past = JetCurve.Step(ref t, c, true, 0f, out float y2); c = y2;   // (past Mach 2.8: nothing)
            bool back = JetCurve.Step(ref t, c, true, 0.9f, out float y3);
            Check(past && y2 == 0f && back && MathF.Abs(y3 - 900f) < 0.01f && t.Rated == 1000f && !t.GameZero, "Jet write: our own zero (past Mach 2.8) is not taken for the game's - thrust returns as it slows");
        }
        // 4. the game code: the curve on the game's own maximum, cheaply; reports truthful; propulsors not shaped twice
        {
            string sim = SimDir();
            string jet = File.ReadAllText(Path.Combine(sim, "Propulsion", "AeroThrustSettingsComponent.Jet.cs"));
            int at = jet.IndexOf("void JetTick()", StringComparison.Ordinal), end = jet.IndexOf("void RestoreJet()", StringComparison.Ordinal);
            string body = at > 0 && end > at ? jet.Substring(at, end - at) : "";
            Check(body.Length > 0 && !body.Replace("new DEntityContext(", "").Contains("new ") && !System.Text.RegularExpressions.Regex.IsMatch(body, @"\btry\b") && !body.Contains("$\"")
                  && body.Contains("aero.LastMach") && body.Contains("td.MaxThrustPower = write") && body.Contains("ThrustTorque.Enabled") && body.Contains("AeroSwitch.Enabled"),
                "AeroThrustSettingsComponent.Jet: the grid's Mach into the thruster's own MaxThrustPower; no allocation (a DEntityContext is a struct), try or formatting per tick; off with aero / thrust torque");
            string comp = File.ReadAllText(Path.Combine(sim, "Propulsion", "AeroThrustSettingsComponent.cs"));
            Check(comp.Contains("RestoreJet()"), "AeroThrustSettingsComponent: the rating written back when it leaves the scene");
            string otj = File.ReadAllText(Path.Combine(sim, "Propulsion", "OffsetThrustJob.cs")), cache = File.ReadAllText(Path.Combine(sim, "Propulsion", "OffsetThrustJob.Cache.cs"));
            Check(otj.Contains("=> t.Settings != null && t.Settings.JetApplied ? ThrustProfiles.Jet : null;") && !otj.Contains("Execute(")
                  && cache.Contains("profile = !propulsor ? ThrustProfiles.Jet"),
                "OffsetThrustJob: the applied profile is Jet exactly while the curve shapes a vanilla atmospheric's maximum, else none (a propulsor block's own curve is in its maximum; no legacy path)");
            string probe = File.ReadAllText(Path.Combine(sim, "Design", "DesignProbe.cs")), api = File.ReadAllText(Path.Combine(sim, "Api", "AeroApi.cs"));
            Check(probe.Contains("t.Settings.JetUnshaped(max)") && api.Contains("t.Settings.JetFactor"), "DesignProbe takes the curve out of the live maximum (it applies it at the Mach it asks); GetThrusters reports the factor applied");
            // (2026-10-17) a propulsor block: its RATING and its kind's curve at the probed Mach - not its live maximum (which
            // carries its airspeed factor, rpm and torque cap now)
            Check(probe.Contains("AeroPropulsorComponent.CurveOf(p.Kind)") && probe.Contains("p.Rated > 0f ? p.Rated") && probe.Contains("s.ThrProfile[i] = curve"),
                "DesignProbe: a propulsor block at its rating x its density factor, its kind's curve applied at the Mach the probe asks");
            string grid = File.ReadAllText(Path.Combine(sim, "Grid", "AeroSimJob.cs")), comp2 = File.ReadAllText(Path.Combine(sim, "Grid", "AeroGridComponent.cs"));
            int setAir = System.Text.RegularExpressions.Regex.Matches(grid, @"aero\.SetAir\(airVel, ").Count;
            Check(setAir == 2 && comp2.Contains("LastMach = float.IsFinite(m) ? m : 0f") && comp2.Contains("LastAirVel = airVel") && comp2.Contains("LastSoundSpeed = SoundSpeedOf(density)"),
                $"AeroSimJob: the grid's air every step (the quiet path too - {setAir} SetAir calls): its airspeed (the wind out), the model's local speed of sound and their Mach together");
        }
    }

    /// <summary>THE PROPULSORS' MACH (2026-10-17): the turboprop / fan / rotor curves take the TRUE airspeed (the wind out) and
    /// the flight Mach over the LOCAL speed of sound - the grid's LastAirVel / LastMach, the source the Jet curve reads - never
    /// the ground speed or a fixed 330 m/s; aero off, the rating written back (as the Jet curve); and no other place derives a
    /// Mach from the ground speed or a hard-coded speed of sound.</summary>
    static void PropulsorMachChecks()
    {
        // 1. the law, game-free: the Mach it is given, not the speed over a constant
        float f1 = AeroPropulsorComponent.Speed(AeroPropulsorComponent.Kinds.Turboprop, 100f, 100f, 0.7f);
        float want1 = 0.4f * (1f - (0.7f - ThrustProfiles.TurbopropMachFade) / (ThrustProfiles.TurbopropMachZero - ThrustProfiles.TurbopropMachFade));
        float f2 = AeroPropulsorComponent.Speed(AeroPropulsorComponent.Kinds.Turboprop, 100f, 100f, 0.3f);   // (100 m/s in thin, cold air: Mach 0.3 - no fade)
        Check(MathF.Abs(f1 - want1) < 1e-4f && MathF.Abs(f2 - 0.4f) < 1e-4f, $"turboprop: constant power by true airspeed ({f2:F3} at 100 m/s), the tip fade by the Mach it is given ({f1:F3} at Mach 0.7)");
        float r1 = AeroPropulsorComponent.Speed(AeroPropulsorComponent.Kinds.Rotor, 0f, 170f, 0.5f), r2 = (float)ThrustProfiles.HelicopterRotor.Evaluate(0.5);
        float fa = AeroPropulsorComponent.Speed(AeroPropulsorComponent.Kinds.Fan, 65f, 200f, 0.9f);
        Check(MathF.Abs(r1 - r2) < 1e-5f && MathF.Abs(fa - 0.5f) < 1e-4f, $"rotor: its profile at the given Mach ({r1:F3}); fan: by its true airspeed along the thrust ({fa:F3} at 65 m/s)");
        // the reports' curves by Mach: the same law sampled at sea level
        float m = 0.4f, v = m * ThrustProfiles.SeaLevelSoundSpeed;
        Check(MathF.Abs((float)ThrustProfiles.Turboprop.Evaluate(m) - ThrustProfiles.TurbopropFraction(v, m)) < 0.01f
              && MathF.Abs((float)ThrustProfiles.DuctedFan.Evaluate(0.2) - ThrustProfiles.FanFraction(0.2f * ThrustProfiles.SeaLevelSoundSpeed)) < 0.01f
              && ReferenceEquals(AeroPropulsorComponent.CurveOf(AeroPropulsorComponent.Kinds.Fan), ThrustProfiles.DuctedFan)
              && ReferenceEquals(AeroPropulsorComponent.CurveOf(AeroPropulsorComponent.Kinds.TailRotor), ThrustProfiles.HelicopterRotor),
            "the kinds' curves by Mach (the reports, the design probe): the blocks' own law sampled at sea level");
        // 2. the game code
        string sim = SimDir();
        string pc = File.ReadAllText(Path.Combine(sim, "Propulsion", "AeroPropulsorComponent.cs"));
        int at = pc.IndexOf("void Tick()", StringComparison.Ordinal), end = pc.IndexOf("void StepRotor()", StringComparison.Ordinal);
        string tick = at > 0 && end > at ? pc.Substring(at, end - at) : "";
        Check(tick.Contains("aero.LastAirVel") && tick.Contains("aero.LastMach") && !tick.Contains("LastLinVel") && !pc.Contains("SoundSpeed")
              && !tick.Contains("new ") && !System.Text.RegularExpressions.Regex.IsMatch(tick, @"\btry\b") && !tick.Contains("$\""),
            "AeroPropulsorComponent: the true airspeed (LastAirVel) and the local Mach (LastMach), no ground speed, no fixed speed of sound; no allocation, try or formatting per tick");
        Check(tick.Contains("Enabled && AeroSwitch.Enabled") && tick.Contains("aero == null ? cur == want") && pc.Contains("void Restore()") && pc.Contains("All.Remove(this); Restore();"),
            "AeroPropulsorComponent: aero off (or no aero grid) - the rating written back exactly, as the Jet curve; and when it leaves the scene");
        // 3. the sweep: no Mach from the ground speed (LastSpeed over a Mach / a constant), no hard-coded speed of sound in the mod
        var bad = new List<string>();
        foreach (var f in Directory.GetFiles(sim, "*.cs", SearchOption.AllDirectories))
        {
            int ln = 0;
            foreach (var line in File.ReadLines(f))
            {
                ln++;
                string code = line; int c = code.IndexOf("//", StringComparison.Ordinal); if (c >= 0) code = code.Substring(0, c);
                if (System.Text.RegularExpressions.Regex.IsMatch(code, @"LastSpeed\s*/\s*(m0|LastResult\.Mach|\d)") || System.Text.RegularExpressions.Regex.IsMatch(code, @"(?<![\w.])3[34]\d(\.\d+)?f?(?![\w.])")
                    && !code.Contains("SeaLevelSoundSpeed = 340.3f"))
                    bad.Add($"{Path.GetFileName(f)}:{ln}: {code.Trim()}");
            }
        }
        Check(bad.Count == 0, $"no Mach from the ground speed or a hard-coded speed of sound in the mod (one source: AeroGridComponent.SetAir; ThrustProfiles.SeaLevelSoundSpeed for sea-level figures){(bad.Count > 0 ? ": " + string.Join(" | ", bad.Take(6)) : "")}");
    }

    /// <summary>THE POWER LADDER: thrust per kW (N/kW) across Mach at sea level, axial - the turboprop (the generated table's
    /// power, its own curve AeroPropulsorComponent.Speed at the true airspeed and Mach), each vanilla atmospheric thruster (the
    /// game's definitions' power, the Jet curve), the ramjet's profile (no block yet: at the vanilla 500's power per newton).
    /// PASS/FAIL (2026-10-17, the turboprop route: 0.32 kW/kN, gen_propulsor_defs.py): the turboprop leads every common vanilla
    /// size (100, 250, 500) per kW up to Mach 0.6 and each of them leads it from its crossover (<= 0.66) to Mach 2; the 1000 -
    /// the vanilla's most efficient - is the special case (it takes over from ~Mach 0.4: printed with what a Mach 0.6
    /// crossover there would take); the ramjet would take over from the jet between Mach 1.6 and 2.1.</summary>
    static void PowerLadderChecks()
    {
        var vanilla = new (string name, float thrust, float kw)[]
        {
            ("Atmo100", 40000f, 75f), ("Atmo250", 287136.3f, 650f), ("Atmo500", 1516383f, 2400f), ("Atmo1000", 15465370f, 16000f),
        };
        var tps = PropulsorBlocks.Rows.Where(r => r.Kind == AeroPropulsorComponent.Kinds.Turboprop).ToList();
        float tpKwPerN = tps.Average(r => r.PowerKw / r.StaticThrust);
        Check(tps.All(r => MathF.Abs(r.PowerKw / r.StaticThrust - tpKwPerN) < 2e-6f), $"ladder: one power per kN for every turboprop size ({string.Join(", ", tps.Select(r => $"{r.Name} {r.PowerKw / r.StaticThrust * 1e3f:F3}"))} kW/kN)");
        float Frac(float m) { float v = m * ThrustProfiles.SeaLevelSoundSpeed; return AeroPropulsorComponent.Speed(AeroPropulsorComponent.Kinds.Turboprop, v, v, m); }
        float Tp(float m) => Frac(m) / tpKwPerN;   // N per kW, axial
        float Jet(float m, float kwPerN) => JetCurve.Factor(m) / kwPerN;
        float Ram(float m, float kwPerN) => (float)ThrustProfiles.Ramjet.Evaluate(m) / kwPerN;
        Console.WriteLine($"   power ladder (N per kW, sea level, axial; turboprop {tpKwPerN * 1000f:F2} kW/kN):");
        Console.WriteLine("   Mach   turboprop " + string.Join(" ", vanilla.Select(v => $"{v.name,9}")) + "  ramjet(@500)");
        foreach (float m in new[] { 0f, 0.1f, 0.2f, 0.3f, 0.45f, 0.6f, 0.8f, 1.0f, 1.3f, 1.6f, 2.0f, 2.4f, 3.0f })
            Console.WriteLine($"   {m,4:F2}  {Tp(m),9:F0} " + string.Join(" ", vanilla.Select(v => $"{Jet(m, v.kw / v.thrust),9:F0}")) + $"  {Ram(m, vanilla[2].kw / vanilla[2].thrust),11:F0}");
        foreach (var v in vanilla)
        {
            float p = v.kw / v.thrust, cross = -1f;
            for (float m = 0f; m <= 0.8f; m += 0.0025f) if (Tp(m) < Jet(m, p)) { cross = m; break; }
            // the turboprop power per newton that puts this crossover at Mach 0.6
            float fix = Frac(0.6f) / JetCurve.Factor(0.6f) * p;
            Console.WriteLine($"   turboprop vs {v.name} ({p * 1000f:F2} kW/kN): the jet leads per kW from Mach {(cross < 0f ? "never below 0.8" : cross.ToString("F3"))}; a Mach 0.6 crossover would take {fix * 1000f:F2} kW/kN ({fix / tpKwPerN:F2} x now)");
            if (v.name == "Atmo1000")
            {
                Check(cross > 0.3f && cross < 0.6f, $"  ladder: the Atmo1000 (the vanilla's most efficient) is the special case - it takes over from Mach {cross:F3}");
                continue;
            }
            Check(cross >= 0.6f, $"  ladder: the turboprop leads {v.name} per kW up to Mach 0.6 (the jet from Mach {cross:F3})");
            bool jetLeads = cross > 0f && cross <= 0.66f; for (float m = MathF.Max(cross, 0.6f) + 0.005f; m <= 2.0f; m += 0.01f) if (Jet(m, p) <= Tp(m)) jetLeads = false;
            Check(jetLeads, $"  ladder: {v.name} leads the turboprop per kW from its crossover (Mach {cross:F3}) to 2");
        }
        float rc = -1f; float p5 = vanilla[2].kw / vanilla[2].thrust;
        for (float m = 1f; m <= 3f; m += 0.005f) if (Ram(m, p5) > Jet(m, p5)) { rc = m; break; }
        Console.WriteLine($"   the ramjet profile (per rated newton; the block burns hydrogen, not power - RamjetChecks) takes over from Mach {rc:F2}");
        Check(rc > 1.6f && rc < 2.1f, $"  ladder: the ramjet profile would take over from the jet between Mach 1.6 and 2.1 ({rc:F2})");
    }
}
