using System.IO;
using AeroMod;

// THE INLET-ANGLE THRUST FACTOR (InletAngle, 2026-10-17, the user: "ramjet needs to be very intolerant, jet tolerant,
// turboprop dgaf"; docs/PROPULSOR_SPECS.md "Inlet angle"): the jet's curve vs the angle and the airspeed, the ramjet's
// unstart / hysteresis / re-light, the turboprop and the rest untouched, no chatter at the cone edge, and the game code and
// reports carrying it.
static partial class Program
{
    static void InletChecks()
    {
        var J = InletAngle.Jet; var R = InletAngle.Ramjet;
        const float Fast = 200f;   // (m/s: past the fade - the full effect)

        // 1. the angle: intake = the push direction; theta 0 flying straight along it, 90 sideways, 180 the air from behind
        Vector3 push = new Vector3(0f, 0f, -1f);
        Check(MathF.Abs(InletAngle.AngleDeg(push, push * 150f)) < 0.01f && MathF.Abs(InletAngle.AngleDeg(push, new Vector3(150f, 0f, 0f)) - 90f) < 0.01f
              && MathF.Abs(InletAngle.AngleDeg(push, -push * 150f) - 180f) < 0.01f && InletAngle.AngleDeg(push, Vector3.Zero) == 0f && InletAngle.AngleDeg(Vector3.Zero, push) == 0f
              && MathF.Abs(InletAngle.AngleDeg(push * 3f, new Vector3(0f, 1f, -1f) * 80f) - 45f) < 0.01f,
            "inlet angle: 0 straight in, 90 sideways, 180 from behind, 45 at 45; 0 for a zero vector; any lengths");

        // 2. the jet: tolerant - full within the cone, a compressor-stall fall-off to a residual by ~65 deg, flameout from behind
        float F(float th) => InletAngle.Factor(J, th, Fast);
        Check(F(0f) == 1f && F(20f) == 1f && F(J.FullDeg) == 1f && J.FullDeg >= 25f && J.FullDeg <= 30f,
            $"jet: full thrust within its cone ({J.FullDeg:F0} deg: {F(0f):F2} / {F(20f):F2} / {F(J.FullDeg):F2})");
        Check(F(40f) < 0.97f && F(40f) > 0.6f && F(50f) < F(40f) && MathF.Abs(F(J.StallDeg) - J.Residual) < 1e-5f && J.StallDeg >= 60f && J.StallDeg <= 70f && MathF.Abs(J.Residual - 0.6f) < 1e-6f,
            $"jet: compressor stall - {F(40f):F2} at 40 deg, {F(50f):F2} at 50, the residual {J.Residual:F2} by {J.StallDeg:F0} deg");
        Check(MathF.Abs(F(90f) - 0.6f) < 1e-5f && F(120f) < 0.6f && F(120f) > 0.3f && MathF.Abs(F(125f) - 0.3f) < 1e-5f && MathF.Abs(F(180f) - 0.3f) < 1e-5f,
            $"jet (softened, the user 2026-10-17): sideways {F(90f):F2} at 90 deg; air from behind {F(120f):F2} at 120, {F(180f):F2} from 125 (no hard flameout)");
        // monotone and smooth: never rises with the angle, its slope bounded (no step anywhere), continuous at every knot
        float worstSlope = 0f; bool mono = true; float prev = F(0f);
        for (float th = 0.05f; th <= 180f; th += 0.05f) { float f = F(th); if (f > prev + 1e-6f) mono = false; worstSlope = MathF.Max(worstSlope, (prev - f) / 0.05f); prev = f; }
        bool cont = true; foreach (float k in new[] { J.FullDeg, J.StallDeg, J.FlameoutDeg0, J.FlameoutDeg1 }) cont &= MathF.Abs(F(k - 0.01f) - F(k + 0.01f)) < 2e-3f;
        Check(mono && cont && worstSlope < 0.04f, $"jet: smooth - monotone, continuous at its knots, slope at most {worstSlope:F3} per deg (no step)");

        // 3. the low-speed fade: the jet's bites only at real speed - none up to 100 m/s, the full effect from 200 (~Mach 0.6);
        //    the ramjet keeps the default 20 / 60 m/s
        float S(float v) => InletAngle.Factor(J, 90f, v);
        Check(J.FadeNone == 100f && J.FadeFull == 200f && InletAngle.Ramjet.FadeNone == 20f && InletAngle.Ramjet.FadeFull == 60f
              && S(0f) == 1f && S(60f) == 1f && S(100f) == 1f && S(150f) < 1f && S(150f) > J.Residual
              && MathF.Abs(S(200f) - J.Residual) < 1e-5f && MathF.Abs(S(300f) - J.Residual) < 1e-5f && S(float.NaN) == 1f && InletAngle.Factor(J, 180f, 90f) == 1f,
            $"fade: jet sideways at 0 / 60 / 100 m/s {S(0f):F2} / {S(60f):F2} / {S(100f):F2}, 150 m/s {S(150f):F2}, 200 m/s {S(200f):F2}; backwards at 90 m/s untouched");
        bool vmono = true; float vp = S(0f); for (float v = 0.5f; v <= 250f; v += 0.5f) { float s = S(v); if (s > vp + 1e-6f) vmono = false; vp = s; }
        Check(vmono, "fade: the factor only falls as the airspeed rises (no bump through the fade)");

        // 4. no chatter at the jet's cone edge: the angle jittering +-2 deg about the edge, through the real write rule
        //    (JetCurve.Step every 3 ticks, its 0.5 % tolerance) - the thrust never moves more than 1 % of the rating; and a
        //    steady sweep through the whole fall-off moves it in small steps only (no jump anywhere)
        {
            const float Rt = 287136.3f;
            var st = JetCurve.NewState(); float cur = Rt; float lo = 1f, worst = 0f; var rng = new Random(7);
            for (int k = 0; k < 2000; k++)
            {
                float th = J.FullDeg + ((float)rng.NextDouble() * 4f - 2f);
                float f = F(th); lo = MathF.Min(lo, f);
                if (JetCurve.Step(ref st, cur, true, f, out float w)) { worst = MathF.Max(worst, MathF.Abs(w - Rt)); cur = w; }
            }
            Check(lo > 0.99f && worst <= 0.01f * Rt, $"jet: the cone edge with +-2 deg of jitter - the factor never below {lo:F3}, the thrust within {worst / Rt * 100f:F2} % of the rating");
            var sw = JetCurve.NewState(); float c2 = Rt, step = 0f; int n = 0;
            for (int k = 0; k <= 200; k++)   // (0 to 140 deg in 10 s: 14 deg/s, a hard turn)
            {
                float th = 0.7f * k;
                if (JetCurve.Step(ref sw, c2, true, F(th), out float w)) { step = MathF.Max(step, MathF.Abs(w - c2)); c2 = w; n++; }
            }
            Check(step <= 0.03f * Rt && MathF.Abs(c2 - J.Behind * Rt) <= 0.005f * Rt, $"jet: a steady sweep 0 -> 140 deg at 14 deg/s - {n} writes, none more than {step / Rt * 100f:F1} % of the rating, {c2 / Rt * 100f:F0} % (the from-behind floor) at the end");
        }

        // 5. the ramjet: very intolerant - full within ~9 deg; past 10 the inlet unstarts (near nothing, fast); hysteresis (it
        //    stays unstarted until back within 6 deg) and a re-light delay (1.5 s) then a spool-up
        {
            Check(R.FullDeg >= 8f && R.FullDeg <= 10f && R.Unstarts && R.UnstartDeg <= 10.5f && R.RestartDeg < R.UnstartDeg && R.RelightDelay >= 1f && R.RelightDelay <= 2f && R.Residual <= 0.05f,
                $"ramjet profile: full within {R.FullDeg:F0} deg, unstart past {R.UnstartDeg:F0}, restart within {R.RestartDeg:F0}, re-light {R.RelightDelay:F1} s, residual {R.Residual:F2}");
            const float dt = 0.05f; const float V = 700f;   // (3 ticks; ~Mach 2)
            var s = InletAngle.NewState();
            float Run(float th, float secs) { float f = 1f; for (float t = 0f; t < secs - 1e-4f; t += dt) f = InletAngle.Step(R, ref s, th, V, dt); return f; }
            float a0 = Run(0f, 1f), a8 = Run(8f, 1f), a9 = Run(9f, 1f), a10 = Run(10f, 1f);
            Check(a0 == 1f && a8 == 1f && a9 == 1f && a10 > 0.9f && !s.Unstarted, $"ramjet: started inside its cone ({a0:F2} at 0, {a8:F2} at 8, {a9:F2} at 9, {a10:F2} at 10 deg)");
            float u1 = InletAngle.Step(R, ref s, 12f, V, dt);
            float u4 = Run(12f, 0.15f);
            Check(s.Unstarted && s.Unstarts == 1 && u1 < 0.9f && u4 <= R.Residual + 1e-4f, $"ramjet: 12 deg unstarts it - {u1:F2} after one update, {u4:F3} within 0.2 s (near nothing)");
            float h = Run(8f, 5f);
            Check(s.Unstarted && h <= R.Residual + 1e-4f, $"ramjet: hysteresis - back at 8 deg (inside the cone, outside the restart) for 5 s it stays unstarted ({h:F3})");
            float b1 = Run(3f, R.RelightDelay - 0.1f);
            Check(s.Unstarted && b1 <= R.Residual + 1e-4f, $"ramjet: the re-light delay - straight air for {R.RelightDelay - 0.1f:F1} s, still out ({b1:F3})");
            float b2 = Run(7f, 0.1f);   // (a flick outside the restart cone during the countdown: it starts again)
            float b3 = Run(3f, R.RelightDelay - 0.1f);
            Check(s.Unstarted && b3 <= R.Residual + 1e-4f, $"ramjet: a flick to 7 deg during the countdown restarts the delay ({b3:F3})");
            float b4 = Run(3f, 0.25f);
            float b5 = Run(3f, R.SpoolTime + 0.05f);
            Check(!s.Unstarted && b4 > R.Residual && b4 < 1f && b5 == 1f, $"ramjet: relit after the delay, spooling up ({b4:F2} soon after, {b5:F2} after {R.SpoolTime:F1} s)");
            // chatter at its cone edge: the angle jittering 9..11 deg - one unstart, then it stays out (no flip-flop)
            var s2 = InletAngle.NewState(); var rng = new Random(3); int flips = 0; bool high = true;
            for (int k = 0; k < 400; k++)
            {
                float f = InletAngle.Step(R, ref s2, 9f + 2f * (float)rng.NextDouble(), V, dt);
                bool hi = f > 0.5f; if (hi != high) { flips++; high = hi; }
            }
            Check(s2.Unstarts == 1 && flips == 1, $"ramjet: the cone edge with jitter (9-11 deg, 20 s) - {s2.Unstarts} unstart, {flips} change of state (no chatter)");
            // slow: no unstart under the fade (a ramjet makes no thrust there anyway) - and it relights there
            var s3 = InletAngle.NewState();
            float slow = 1f; for (int k = 0; k < 40; k++) slow = InletAngle.Step(R, ref s3, 90f, 15f, dt);
            Check(!s3.Unstarted && slow == 1f, "ramjet: under 20 m/s no unstart, the factor 1");
            var s4 = InletAngle.NewState(); InletAngle.Step(R, ref s4, 30f, V, dt);
            for (int k = 0; k < 60; k++) InletAngle.Step(R, ref s4, 90f, 10f, dt);
            Check(!s4.Unstarted, "ramjet: unstarted, then slowed under 20 m/s - it relights after the delay");
            // the stateless Factor for the ramjet: its shape alone
            Check(InletAngle.Factor(R, 5f, V) == 1f && InletAngle.Factor(R, 20f, V) < 0.05f, "ramjet: the stateless factor - full at 5 deg, near nothing at 20");
        }

        // 6. who: the jet family flies Jet, ramjets / scramjets Ramjet; the turboprop and every other kind no factor at all
        {
            Check(ReferenceEquals(InletAngle.Of(ThrustProfiles.Jet), J) && ReferenceEquals(InletAngle.Of(ThrustProfiles.Turbojet), J) && ReferenceEquals(InletAngle.Of(ThrustProfiles.Turbofan), J)
                  && ReferenceEquals(InletAngle.Of(ThrustProfiles.Ramjet), R) && ReferenceEquals(InletAngle.Of(ThrustProfiles.Scramjet), R),
                "inlet profiles: Jet / Turbojet / Turbofan - the jet's; Ramjet / Scramjet - the ramjet's");
            Check(InletAngle.Of(ThrustProfiles.Turboprop) == null && InletAngle.Of(ThrustProfiles.Propeller) == null && InletAngle.Of(ThrustProfiles.DuctedFan) == null
                  && InletAngle.Of(ThrustProfiles.HelicopterRotor) == null && InletAngle.Of(ThrustProfiles.Rocket) == null && InletAngle.Of(null) == null
                  && InletAngle.Factor(null, 180f, Fast) == 1f,
                "inlet profiles: turboprop, propeller, fan, rotors, rockets, none - no factor (1 at any angle)");
            bool was = InletAngle.Enabled; InletAngle.Enabled = false;
            float off = InletAngle.Factor(J, 90f, Fast); var so = InletAngle.NewState(); so.Unstarted = true; so.Level = 0.02f; float offR = InletAngle.Step(R, ref so, 90f, Fast, 0.05f);
            InletAngle.Enabled = was;
            Check(off == 1f && offR == 1f && !so.Unstarted, "InletAngle.Enabled off: the factor 1 (jet and ramjet), the ramjet's state reset");
        }

        // 7. the write: one maximum, rating x Mach curve x inlet factor; the reports' rating comes back whole (the design probe:
        //    straight-in air); lifted - the rating back exactly
        {
            const float Rt = 287136.3f;
            var st = JetCurve.NewState(); float cur = Rt;
            float both = JetCurve.Factor(0.6f) * InletAngle.Factor(J, 90f, Fast);
            bool w = JetCurve.Step(ref st, cur, true, both, out float x); if (w) cur = x;
            Check(w && MathF.Abs(cur - Rt * 0.95f * J.Residual) < 1f && MathF.Abs(st.Factor - both) < 1e-6f, $"write: sideways at Mach 0.6 - {cur:F0} N (rating x 0.95 x {J.Residual:F2}); JetFactor is both ({st.Factor:F4})");
            Check(MathF.Abs(JetCurve.Unshaped(in st, cur) - Rt) < 1f, $"report: the live maximum taken back to the rating ({JetCurve.Unshaped(in st, cur):F0} N) - the design probe then applies the Mach curve with straight-in air");
            // (a zero factor - e.g. the ramjet's unstart floor, or a Mach curve's end - written as exactly 0, and not then taken for the game's own 0)
            bool f0 = JetCurve.Step(ref st, cur, true, 0f, out float x0); if (f0) cur = x0;
            bool back = JetCurve.Step(ref st, cur, true, JetCurve.Factor(0.6f) * InletAngle.Factor(J, 0f, Fast), out float x1); if (back) cur = x1;
            Check(f0 && x0 == 0f && back && MathF.Abs(cur - Rt * 0.95f) < 1f && !st.GameZero, $"write: a zero factor (0 N), straight again - {cur:F0} N (our zero not taken for the game's)");
            bool l = JetCurve.Step(ref st, cur, false, 0.1f, out float xl);
            Check(l && xl == Rt, "write: lifted (both knobs off, aero off, thrust torque off) - the rating written back exactly");
        }

        // 8. the game code and the reports
        {
            string sim = SimDir();
            string jet = File.ReadAllText(Path.Combine(sim, "Propulsion", "AeroThrustSettingsComponent.Jet.cs"));
            int at = jet.IndexOf("void JetTick()", StringComparison.Ordinal), end = jet.IndexOf("void RestoreJet()", StringComparison.Ordinal);
            string body = at > 0 && end > at ? jet.Substring(at, end - at) : "";
            Check(body.Contains("aero.LastAirVel") && body.Contains("InletAngle.Factor(InletAngle.Jet, JetInletDeg, vLocal.Length())") && body.Contains("factor = JetMachFactor * JetInletFactor")
                  && body.Contains("InletAngle.Enabled") && body.Contains("td.ParentGrid") && body.Contains("td.MaxThrustPower = write"),
                "AeroThrustSettingsComponent.Jet: the inlet factor from the true airspeed in the thruster's own grid frame, x the Mach curve, into the one MaxThrustPower write; its own knob");
            Check(jet.Contains("internal float JetFactor => _jetKind == 1 && Jet.Applied ? Jet.Factor : 1f") && jet.Contains("JetApplied => _jetKind == 1 && Jet.Applied && _jetMachOn"),
                "reports: JetFactor is the factor applied (Mach x inlet); JetApplied (the Mach curve the probe re-applies) only while the Mach curve is on");
            string otj = File.ReadAllText(Path.Combine(sim, "Propulsion", "OffsetThrustJob.cs")), cache = File.ReadAllText(Path.Combine(sim, "Propulsion", "OffsetThrustJob.Cache.cs"));
            string prof = File.ReadAllText(Path.Combine(sim, "Propulsion", "ThrustProfile.cs")), api = File.ReadAllText(Path.Combine(sim, "Api", "AeroApi.cs"));
            string simjob = File.ReadAllText(Path.Combine(sim, "Grid", "AeroSimJob.cs"));
            // (2026-10-17: the legacy per-thruster path - OffsetThrustJob.Execute, LegacyScale, the phantom torque - removed: one
            //  thrust path, the game's own maximum shaped here; the old sideways-Mach Evaluate stays retired)
            Check(!System.Text.RegularExpressions.Regex.IsMatch(prof, @"Evaluate\(double mach,\s*Vector3") && !otj.Contains("LegacyScale") && !otj.Contains("Execute(")
                  && !cache.Contains("ForceOverride") && !simjob.Contains("OffsetThrustJob.Execute") && !simjob.Contains("SasTorque"),
                "legacy path gone: no Execute / LegacyScale / thrust overrides / phantom torque; the old sideways-Mach Evaluate retired");
            Check(api.Contains("effectiveness?.Add(t.Settings != null ? t.Settings.JetFactor : 1f)") && !api.Contains("LegacyScale"),
                "AeroApi.GetThrusters: effectiveness is the factor applied (Mach x inlet; 1 with the curve lifted)");
            string pc = File.ReadAllText(Path.Combine(sim, "Propulsion", "AeroPropulsorComponent.cs")), pk = File.ReadAllText(Path.Combine(sim, "Propulsion", "PropulsorKinds.cs"));
            // (2026-10-17: the RAMJET is a propulsor block with the ramjet inlet - its one Step, inside its own branch; the others none)
            int ri = pc.IndexOf("if (IsRamjet(Kind))", StringComparison.Ordinal), rs = pc.IndexOf("InletAngle.Step(", StringComparison.Ordinal);
            Check(!pk.Contains("InletAngle") && !System.Text.RegularExpressions.Regex.IsMatch(pc, @"InletAngle\.(Jet|Of|ByName|Factor)\b")
                  && System.Text.RegularExpressions.Regex.Matches(pc, @"InletAngle\.Step\(").Count == 1 && pc.Contains("InletAngle.Step(InletAngle.Ramjet,") && ri > 0 && rs > ri && rs - ri < 600,
                "propulsors (turboprops, fans, rotors): no inlet factor anywhere in their path - only the ramjet's own (InletAngle.Ramjet, in its branch)");
            string ia = File.ReadAllText(Path.Combine(sim, "Propulsion", "InletAngle.cs"));
            int sa = ia.IndexOf("public static float AngleDeg", StringComparison.Ordinal), se = ia.Length;
            string hot = sa > 0 ? ia.Substring(sa, se - sa) : "";
            Check(hot.Length > 0 && !hot.Replace("new State", "").Contains("new ") && !System.Text.RegularExpressions.Regex.IsMatch(hot, @"\btry\b") && !hot.Contains("$\""),
                "InletAngle: the per-update functions allocate nothing, no try, no formatting");
        }
    }
}
