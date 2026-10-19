#pragma warning disable
using System.IO;
using AeroMod;
using K = AeroMod.RotorKinematics;

/// <summary>RotorTests: the helicopter rotor's governed rpm and part poses, offline (exit 0 = all pass).</summary>
static class Program
{
    static int _checks, _fails;
    static void Check(bool ok, string what) { _checks++; if (!ok) { _fails++; if (_fails <= 30) Console.WriteLine("FAIL " + what); } }
    const float Deg = MathF.PI / 180f;

    /// <summary>The mod's Scripts/Simulation folder (from this file's own path at compile time).</summary>
    static string SimDir([System.Runtime.CompilerServices.CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here), "..", "..", "Scripts", "Simulation"));

    /// <summary>THE ROTOR BALANCE (2026-10-17): thrust by disc area, rotors at half the vanilla power per newton, the reaction
    /// torque calibrated to a real helicopter's tail share, the Reversible Rotor sized to hold a 1.5 m rotor; and the
    /// controllability limiter's law (RotorLimiter: the authority, the inverse of the reaction, the smoothing).</summary>
    static void BalanceChecks()
    {
        var rows = PropulsorBlocks.Rows;
        PropulsorBlocks.Row Row(string n) => rows.First(r => r.Name == n);
        var r05 = Row("HelicopterRotor_050"); var r15 = Row("HelicopterRotor_150"); var r25 = Row("HelicopterRotor_250"); var tail = Row("HelicopterTailRotor_050");
        // 1. thrust ~ disc area (one disc loading), anchored on the 1.5 m = 900 kN (2026-10-18: 1.5x for honest dampeners, from 600); the tip radii are the spec's 2.5 / 7.5 / 12.5 m
        Check(r05.Radius == 2.5f && r15.Radius == 7.5f && r25.Radius == 12.5f, $"rotor tip radii {r05.Radius} / {r15.Radius} / {r25.Radius} m");
        Check(r15.StaticThrust == 900000f, $"HelicopterRotor_150: {r15.StaticThrust / 1e3f:F0} kN");
        float dl = r15.StaticThrust / (MathF.PI * r15.Radius * r15.Radius);
        foreach (var r in new[] { r05, r25 })
        {
            float byArea = r15.StaticThrust * (r.Radius / r15.Radius) * (r.Radius / r15.Radius);
            Check(MathF.Abs(r.StaticThrust - byArea) <= 500f, $"{r.Name}: {r.StaticThrust / 1e3f:F1} kN, by disc area {byArea / 1e3f:F1} kN");
            float rdl = r.StaticThrust / (MathF.PI * r.Radius * r.Radius);
            Check(MathF.Abs(rdl - dl) < 0.01f * dl, $"{r.Name}: disc loading {rdl:F0} N/m^2 (the 1.5 m's {dl:F0})");
        }
        Check(tail.StaticThrust == 38000f, $"Reversible Rotor: {tail.StaticThrust / 1e3f:F0} kN");
        // 2. power per kN: every helicopter rotor half the vanilla 1.58 (the most efficient lifters); the turboprops 0.32 (2026-10-17,
        //    the kings of subsonic thrust per kW to ~Mach 0.6 - Tests/ThrustTests power ladder); fans and the Reversible Rotor 1.58
        float Ratio(PropulsorBlocks.Row r) => r.PowerKw / (r.StaticThrust / 1000f);
        // (a RAMJET's PowerKw is its HYDROGEN per second, not kW - gen_propulsor_defs.py; Tests/ThrustTests RamjetChecks hold it)
        foreach (var r in rows.Where(r => r.Kind != AeroPropulsorComponent.Kinds.Ramjet))
        {
            float want = r.Kind == AeroPropulsorComponent.Kinds.Rotor ? 0.79f : r.Kind == AeroPropulsorComponent.Kinds.Turboprop ? 0.32f : 1.58f;
            Check(MathF.Abs(Ratio(r) - want) < 0.005f, $"{r.Name}: {Ratio(r):F3} kW per kN (want {want})");
        }
        // 3. the reaction torque at RATED thrust: a tail at the rotor's tip radius needs 10 % of its thrust - every size, any air
        //    (thrust ~ rho: the same induced velocity) - and it still grows faster than the thrust beyond it
        foreach (var r in new[] { r05, r15, r25 })
            foreach (float rho in new[] { 1f, 0.89f, 0.5f })
            {
                float T = r.StaticThrust * rho, share = K.ReactionTorque(T, r.Radius, 1f, rho) / r.Radius / T;
                Check(share > 0.0285f && share < 0.0315f, $"{r.Name} at rho {rho}: a tail at {r.Radius} m needs {share * 100f:F1} % of its thrust");
            }
        Check(K.ReactionTorque(1.25f * r15.StaticThrust, 7.5f, 1f, 1f) > 1.25f * 1.1f * K.ReactionTorque(r15.StaticThrust, 7.5f, 1f, 1f), "reaction: still grows faster than thrust (1.25 x thrust > 1.37 x torque)");
        // 4. the Reversible Rotor (128 kN, 2026-10-18; 85 kN until the rotors went 1.5x): one at a 1.5 m rotor's tip radius holds it at its rating WITH the limiter's
        //    margin (no cap below 100 %); two at the 2.5 m rotor's tip radius hold ~87 % of its rating (the limiter's own targets)
        {
            float q15 = K.ReactionTorque(r15.StaticThrust, 7.5f, 1f, 1f), q25 = K.ReactionTorque(r25.StaticThrust, 12.5f, 1f, 1f);
            float held15 = RotorLimiter.Margin * tail.StaticThrust * 7.5f;
            Check(held15 >= q15 && held15 < 1.25f * q15, $"one Reversible Rotor at 7.5 m: {held15 / 1e3f:F0} kN m (x {RotorLimiter.Margin}) against the 1.5 m rotor's {q15 / 1e3f:F0} kN m at rated");
            float need = q25 / 12.5f / tail.StaticThrust;
            Check(need > 1.9f && need < 2.1f, $"the 2.5 m rotor at rated needs {need:F2} Reversible Rotors at 12.5 m ({q25 / 1e3f:F0} kN m) - x {RotorLimiter.Margin}: {need / RotorLimiter.Margin:F2}");
            // the caps themselves (RotorLimiter.Targets, rpm 1, rho 1): the tails pushing +X at (0, 0, z): arm (0, -z, 0) against +Y
            float Cap(PropulsorBlocks.Row r, int tails, float z)
            {
                int n = 1 + tails;
                var dirs = new Vector3[n]; var arms = new Vector3[n]; var caps = new float[n]; var rev = new bool[n];
                dirs[0] = Vector3.UnitY; caps[0] = r.StaticThrust;
                for (int i = 1; i < n; i++) { dirs[i] = Vector3.UnitX; arms[i] = new Vector3(0f, -z, 0f); caps[i] = tail.StaticThrust; rev[i] = true; }
                float[] au = new float[1], bu = new float[1], tg = new float[1];
                RotorLimiter.Targets(1, new[] { 0 }, new[] { r.Radius }, new[] { 1f }, new[] { r.StaticThrust }, arms, dirs, caps, rev, n, Vector3.Zero, 1f, au, bu, tg);
                return tg[0] / r.StaticThrust;
            }
            float c15 = Cap(r15, 1, 7.5f), c25 = Cap(r25, 2, 12.5f);
            Check(c15 >= 1f, $"1.5 m rotor + one Reversible Rotor at its tip radius: cap {c15 * 100f:F0} % of rated - not capped below 100 %");
            Check(c25 > 0.85f && c25 < 0.89f, $"2.5 m rotor + two Reversible Rotors at its tip radius: cap {c25 * 100f:F1} % of rated (~87 %)");
        }
        // 5. ThrustForTorque is ReactionTorque's inverse; 0 below the profile torque; none when stopped; never NaN
        {
            bool inv = true;
            foreach (float R in new[] { 2.5f, 7.5f, 12.5f })
                foreach (float rpm in new[] { 0.7f, 1f })
                    foreach (float T in new[] { 1e3f, 5e4f, 6e5f, 3e6f })
                    {
                        float q = K.ReactionTorque(T, R, rpm, 0.89f), back = K.ThrustForTorque(q, R, rpm, 0.89f);
                        if (MathF.Abs(back - T) > 2e-3f * T + 1f) { inv = false; Console.WriteLine($"  inverse R {R} rpm {rpm} T {T}: {back}"); }
                    }
            Check(inv, "ThrustForTorque inverts ReactionTorque (every size, rpm, thrust)");
            Check(K.ThrustForTorque(0.5f * K.ReactionTorque(0f, 7.5f, 1f, 1f), 7.5f, 1f, 1f) == 0f && K.ThrustForTorque(0f, 7.5f, 1f, 1f) == 0f, "ThrustForTorque: below the profile torque - none");
            Check(float.IsPositiveInfinity(K.ThrustForTorque(1e5f, 7.5f, 0f, 1f)) && float.IsPositiveInfinity(K.ThrustForTorque(1e5f, 0f, 1f, 1f)), "ThrustForTorque: stopped / no radius - no cap");
            Check(!float.IsNaN(K.ThrustForTorque(float.NaN, 7.5f, 1f, 1f)) && !float.IsNaN(K.ThrustForTorque(-5f, 7.5f, 1f, 1f)) && !float.IsNaN(K.ThrustForTorque(1e5f, 7.5f, 1f, 0f)), "ThrustForTorque: never NaN");
        }
        // 6. the authority: a reversible thruster both ways, a one-way one only when it answers the reaction (along -axis), the
        //    gyros about the axis; arms about other axes count nothing
        {
            var arms = new[] { new Vector3(0f, 6f, 0f), new Vector3(0f, -4f, 0f), new Vector3(0f, 3f, 0f), new Vector3(5f, 0f, 0f), Vector3.Zero };
            var caps = new[] { 1000f, 1000f, 1000f, 1000f, 1e6f };
            var rev = new[] { true, false, false, true, false };
            float a = RotorLimiter.Authority(Vector3.UnitY, arms, caps, rev, 5, new Vector3(7f, 900f, 11f), out float th, out float gy);
            Check(th == 10000f && gy == 900f && a == 10900f, $"authority: reversible both ways 6000 + one-way answering 4000 (wrong way 0, other axis 0) = {th}, gyros {gy}");
            float a2 = RotorLimiter.Authority(Vector3.UnitY, arms, caps, null, 5, Vector3.Zero, out _, out _);
            Check(a2 == 4000f, $"authority: nothing reversible - only the answering one-way thruster ({a2})");
            Check(RotorLimiter.Authority(Vector3.UnitY, arms, new float[5], rev, 5, Vector3.Zero, out _, out _) == 0f, "authority: all at zero maximum - none");
            // the control surfaces (2026-10-17, "any source of torque counts"): the reach left toward -axis - Pull on an axis the
            // rotor's axis is + along, Push where it is -; added to the others (separate actuators); none at hover; never NaN
            var push = new Vector3(100f, 2000f, 300f); var pull = new Vector3(50f, 500f, 70f);
            float a3 = RotorLimiter.Authority(Vector3.UnitY, arms, caps, rev, 5, new Vector3(7f, 900f, 11f), push, pull, out float th3, out float gy3, out float su3);
            Check(su3 == 500f && th3 == th && gy3 == gy && a3 == a + 500f, $"authority: surfaces answer +Y with their pull ({su3}), added once to thrusters {th3} + gyros {gy3} = {a3}");
            Check(RotorLimiter.SurfaceAuthority(-Vector3.UnitY, push, pull) == 2000f, "authority: surfaces answer -Y with their push");
            Check(MathF.Abs(RotorLimiter.SurfaceAuthority(Vector3.Normalize(new Vector3(1f, 1f, 0f)), push, pull) - (50f + 500f) * 0.70710677f) < 1e-2f, "authority: surfaces about a slanted axis - each axis by its share");
            Check(RotorLimiter.Authority(Vector3.UnitY, arms, caps, rev, 5, Vector3.Zero, Vector3.Zero, Vector3.Zero, out _, out _, out float su0) == RotorLimiter.Authority(Vector3.UnitY, arms, caps, rev, 5, Vector3.Zero, out _, out _) && su0 == 0f, "authority: no surface reach (hover) - as without");
            Check(RotorLimiter.SurfaceAuthority(Vector3.UnitY, new Vector3(float.NaN), new Vector3(float.NaN)) == 0f && RotorLimiter.SurfaceAuthority(Vector3.UnitY, -push, -pull) == 0f, "authority: surfaces never NaN, never negative");
            float[] au = new float[1], bu = new float[1], tg = new float[1], tg0 = new float[1];
            var d1 = new[] { Vector3.UnitY }; var arm1 = new[] { Vector3.Zero }; var cap1 = new[] { 6e5f };
            RotorLimiter.Targets(1, new[] { 0 }, new[] { 7.5f }, new[] { 1f }, new[] { 6e5f }, arm1, d1, cap1, null, 1, new Vector3(1e5f), 1f, au, bu, tg0);
            RotorLimiter.Targets(1, new[] { 0 }, new[] { 7.5f }, new[] { 1f }, new[] { 6e5f }, arm1, d1, cap1, null, 1, new Vector3(1e5f), Vector3.Zero, new Vector3(0f, 1.5e5f, 0f), 1f, au, bu, tg);
            Check(au[0] == 2.5e5f && tg[0] > tg0[0], $"targets: a rudder's reach raises the cap ({tg0[0] / 1e3f:F0} -> {tg[0] / 1e3f:F0} kN)");
        }
        // 7. the targets: one rotor - the thrust whose reaction is Margin of the authority; two alike on one axis - half each;
        //    no authority - a cap of 0 (no lift); stopped - none
        {
            var dirs = new[] { Vector3.UnitY, Vector3.UnitX, Vector3.UnitY };
            var arms = new[] { Vector3.Zero, new Vector3(0f, -7.5f, 0f), Vector3.Zero };   // (the tail 7.5 m aft pushing +X: (0,0,7.5) x X, against +Y)
            var caps = new[] { 6e5f, 6e4f, 6e5f };
            float[] auth = new float[2], bud = new float[2], tgt = new float[2];
            RotorLimiter.Targets(1, new[] { 0 }, new[] { 7.5f }, new[] { 1f }, new[] { 6e5f }, arms, dirs, caps, null, 2, Vector3.Zero, 1f, auth, bud, tgt);
            Check(auth[0] == 4.5e5f && MathF.Abs(bud[0] - RotorLimiter.Margin * 4.5e5f) < 1f && MathF.Abs(K.ReactionTorque(tgt[0], 7.5f, 1f, 1f) - bud[0]) < 1e-3f * bud[0],
                $"targets: one rotor - authority {auth[0]:F0}, reaction allowed {bud[0]:F0} N m, cap {tgt[0]:F0} N ({tgt[0] / 6e5f * 100f:F0} % of rated)");
            RotorLimiter.Targets(2, new[] { 0, 2 }, new[] { 7.5f, 7.5f }, new[] { 1f, 1f }, new[] { 6e5f, 6e5f }, arms, dirs, caps, null, 3, Vector3.Zero, 1f, auth, bud, tgt);
            Check(MathF.Abs(bud[0] - 0.5f * RotorLimiter.Margin * 4.5e5f) < 1f && bud[0] == bud[1] && tgt[0] == tgt[1], $"targets: two rotors alike share it ({bud[0]:F0} + {bud[1]:F0} N m)");
            RotorLimiter.Targets(1, new[] { 0 }, new[] { 7.5f }, new[] { 1f }, new[] { 6e5f }, arms, dirs, new[] { 6e5f, 0f, 6e5f }, null, 2, Vector3.Zero, 1f, auth, bud, tgt);
            Check(tgt[0] == 0f, $"targets: no anti-torque at all - no lift ({tgt[0]})");
            RotorLimiter.Targets(1, new[] { 0 }, new[] { 7.5f }, new[] { 0f }, new[] { 6e5f }, arms, dirs, caps, null, 2, Vector3.Zero, 1f, auth, bud, tgt);
            Check(float.IsPositiveInfinity(tgt[0]), "targets: a stopped rotor - no cap");
        }
        // 8. the smoothing: at once from none; first order down (FallSeconds) and up (RiseSeconds, slower); monotonic, no overshoot
        {
            Check(RotorLimiter.Step(float.PositiveInfinity, 1000f, 1f / 60f) == 1000f && float.IsPositiveInfinity(RotorLimiter.Step(1000f, float.PositiveInfinity, 1f / 60f)), "smoothing: from / to none at once");
            float c = 1000f; bool mono = true; int k = 0;
            for (; k < 600 && c > 500.5f; k++) { float nx = RotorLimiter.Step(c, 500f, 1f / 60f); if (nx > c || nx < 500f) mono = false; c = nx; }
            Check(mono && k / 60f < 8f * RotorLimiter.FallSeconds, $"smoothing: down to 500 in {k / 60f:F2} s, monotonic");
            int j = 0;
            for (; j < 600 && c < 999.5f; j++) { float nx = RotorLimiter.Step(c, 1000f, 1f / 60f); if (nx < c || nx > 1000f) mono = false; c = nx; }
            Check(mono && j > k && j / 60f < 8f * RotorLimiter.RiseSeconds, $"smoothing: back up in {j / 60f:F2} s (slower than down), monotonic");
            Check(RotorLimiter.Step(1000f, -5f, 1f / 60f) >= 0f && !float.IsNaN(RotorLimiter.Step(float.NaN, 100f, 1f / 60f)), "smoothing: never below 0, never NaN");
        }
        // 9. the game code: the cap goes through the propulsor's own maximum write; the limiter runs before the reaction, cheaply
        {
            string sim = SimDir();
            string prop = File.ReadAllText(Path.Combine(sim, "Propulsion", "AeroPropulsorComponent.cs"));
            Check(prop.Contains("want = MathF.Min(want, TorqueCap / eff)") && prop.Contains("CapAge <= CapHoldTicks"), "AeroPropulsorComponent: the cap is written as the rotor's maximum, let go when stale");
            string tt = File.ReadAllText(Path.Combine(sim, "Propulsion", "ThrustTorque.cs"));
            int lim = tt.IndexOf("RotorCyclic.Limit(", StringComparison.Ordinal), react = tt.IndexOf("RotorCyclic.ReactionTorque(aero", StringComparison.Ordinal);
            Check(lim > 0 && react > lim && tt.Contains("RotorCyclic.KeepCaps(aero, n)"), "ThrustTorque: the limiter every step before the reaction; kept at rest");
            string cyc = File.ReadAllText(Path.Combine(sim, "Propulsion", "RotorCyclic.cs"));
            int at = cyc.IndexOf("internal static void Limit(", StringComparison.Ordinal), end = cyc.IndexOf("internal static void KeepCaps(", StringComparison.Ordinal);
            string body = at > 0 && end > at ? cyc.Substring(at, end - at) : "";
            Check(body.Length > 0 && !body.Contains("new ") && !System.Text.RegularExpressions.Regex.IsMatch(body, @"\btry\b") && !body.Contains("$\""), "RotorCyclic.Limit: no allocation, no try, no formatting per step");
            Check(body.Contains("aero.SurfaceReachFresh") && body.Contains("aero.SurfacePull * ThrustTorque.SurfaceReachShare") && body.Contains("gyro, sPush, sPull, rho"),
                "RotorCyclic.Limit: the control surfaces' fresh reach (x the planning share) goes into the authority");
            string loac = File.ReadAllText(Path.Combine(sim, "Hud", "Loac.cs")), surf = File.ReadAllText(Path.Combine(sim, "Grid", "AeroGridComponent.Surfaces.cs"));
            Check(loac.Contains("_surfReachFrame = _simFrameCount") && surf.Contains("_surfReachFrame = _simFrameCount") && tt.Contains("AllocEnabled && aero.SurfaceReachFresh"),
                "the surfaces' reach is stamped where it is measured, and read fresh only (the limiter, the attitude law)");
        }
    }

    static int Main()
    {
        // 0. the quaternion convention the poses rely on: (a * b) turns by b first, then a
        {
            var a = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f); var b = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.4f);
            var v = new Vector3(0.3f, -0.2f, 1f);
            Check((Vector3.Transform(v, a * b) - Vector3.Transform(Vector3.Transform(v, b), a)).Length() < 1e-5f, "quaternion product order");
        }
        // 1. governed rpm: spin-up from rest reaches 95 % by SpinUpSeconds, never overshoots, holds exactly 100 %
        {
            float w = 0f, t95 = -1f; bool mono = true;
            for (int i = 0; i < 60 * 30; i++)
            {
                float n = K.StepRpm(w, true, 1f / 60f);
                if (n < w) mono = false;
                w = n; if (t95 < 0f && w >= 0.95f) t95 = i / 60f;
            }
            Check(mono && w == 1f, $"spin-up: ended at {w}, monotonic {mono}");
            Check(t95 > 0.5f * K.SpinUpSeconds && t95 <= K.SpinUpSeconds * 1.05f, $"spin-up: 95 % at {t95:F2} s");
            // spin-down: monotonic, stops in finite time, slower than the spin-up
            float t0 = -1f; mono = true;
            for (int i = 0; i < 60 * 120; i++) { float n = K.StepRpm(w, false, 1f / 60f); if (n > w) mono = false; w = n; if (t0 < 0f && w <= 0f) t0 = i / 60f; }
            Check(mono && t0 > K.SpinUpSeconds && t0 < 90f, $"spin-down: stopped at {t0:F1} s, monotonic {mono}");
            Check(K.ThrustFactor(1f) == 1f && K.ThrustFactor(0f) == 0f && MathF.Abs(K.ThrustFactor(0.9f) - 0.81f) < 1e-6f, "thrust ~ rpm^2 near the governed rpm");
            // no thrust while visibly slow (the user: a minimum spin speed while thrusting); monotonic
            bool tm = true; float pt = -1f;
            for (int i = 0; i <= 100; i++) { float t = K.ThrustFactor(i / 100f); if (t < pt) tm = false; pt = t; if (i / 100f < K.MinThrustRpm && t != 0f) tm = false; }
            Check(tm, "thrust: none below MinThrustRpm, monotonic in rpm");
        }
        // 2. collective: flat pitch at no thrust, rising with thrust, MaxPitch at the whole maximum
        {
            Check(MathF.Abs(K.Collective(0f, 1e6f) - K.FlatPitchDeg * Deg) < 1e-6f, "collective: flat at no thrust");
            Check(MathF.Abs(K.Collective(1e6f, 1e6f) - K.MaxPitchDeg * Deg) < 1e-6f, "collective: max at the maximum");
            float prev = -1f; bool mono = true;
            for (int i = 0; i <= 20; i++) { float c = K.Collective(i * 5e4f, 1e6f); if (c <= prev) mono = false; prev = c; }
            Check(mono, "collective: rises with thrust");
            Check(K.Collective(5e5f, 0f) == K.FlatPitchDeg * Deg, "collective: no maximum - flat");
        }
        // 3. coning: rises with thrust, larger at lower rpm for the same thrust, droop at rest, capped
        {
            Check(K.Coning(0f, 1e6f, 0f) < 0f && MathF.Abs(K.Coning(0f, 1e6f, 0f) + K.DroopDeg * Deg) < 1e-6f, "coning: droop at rest");
            Check(MathF.Abs(K.Coning(1e6f, 1e6f, 1f) - K.ConeAtRatedDeg * Deg) < 1e-6f, "coning: ConeAtRated at the rated thrust");
            Check(K.Coning(5e5f, 1e6f, 1f) > K.Coning(2e5f, 1e6f, 1f), "coning: rises with thrust");
            Check(K.Coning(3e5f, 1e6f, 0.7f) > K.Coning(3e5f, 1e6f, 1f), "coning: a slower rotor cones more for the same lift");
            Check(K.Coning(1e8f, 1e6f, 1f) <= K.MaxConeDeg * Deg + 1e-6f, "coning: capped");
        }
        // 4. blur: blades shown slow and gone fast, the disc the reverse; monotonic
        {
            Check(K.BladeOpacity(0f) == 1f && K.BladeOpacity(0.3f) == 1f && K.BladeOpacity(1f) == K.BladeMinOpacity, "blades: shown slow, gone at speed");
            Check(K.BlurOpacity(0f) == 0f && MathF.Abs(K.BlurOpacity(1f) - K.BlurMaxOpacity) < 1e-6f, "blur: none slow, full at speed");
            bool mono = true; float pb = 2f, pu = -1f;
            for (int i = 0; i <= 100; i++) { float w = i / 100f, b = K.BladeOpacity(w), u = K.BlurOpacity(w); if (b > pb || u < pu) mono = false; pb = b; pu = u; }
            Check(mono, "fades monotonic in rpm");
        }
        // 5. poses: blade 0 (span +Z, leading edge -X) - the spin moves its hinge leading edge first; positive pitch raises the
        //    leading edge; positive flap raises the tip; the disc tilt puts the tips' plane normal on the lean
        {
            var hub = new Vector3(0f, 2f, 0f); var hinge = new Vector3(0f, 1.8f, 0.7f);
            K.BladePose(hub, Quaternion.Identity, 0.1f, hinge, Quaternion.Identity, 0f, 0f, out var p, out _);
            Check(p.X < -0.05f && p.Z > 0f, $"spin: blade 0 moved toward -X (leading edge first): {p}");
            K.BladePose(hub, Quaternion.Identity, 0f, hinge, Quaternion.Identity, 0f, 10f * Deg, out _, out var r);
            Check(Vector3.Transform(-Vector3.UnitX, r).Y > 0.1f, "pitch: leading edge up");
            K.BladePose(hub, Quaternion.Identity, 0f, hinge, Quaternion.Identity, 5f * Deg, 0f, out _, out r);
            Check(Vector3.Transform(Vector3.UnitZ, r).Y > 0.05f, "flap: tip up");
            // a blade at 120 deg: its span turned with its rest
            var r120 = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 2f * MathF.PI / 3f);
            var s = K.SpanDir(r120, 0f);
            Check(MathF.Abs(s.X - MathF.Sin(2f * MathF.PI / 3f)) < 1e-4f && MathF.Abs(s.Z - MathF.Cos(2f * MathF.PI / 3f)) < 1e-4f, $"span of blade 2: {s}");
            // the disc's tilt: three tips, a lean toward +X by 8 deg: their plane's normal leans to +X by 8 deg
            var lean = new Vector3(8f * Deg, 0f, 0f);
            var disc = K.Tilt(lean);
            var tips = new Vector3[3];
            for (int k = 0; k < 3; k++)
            {
                var rk = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 2f * MathF.PI * k / 3f);
                var hk = hub + Vector3.Transform(new Vector3(0f, -0.2f, 0.7f), rk);
                K.BladePose(hub, disc, 0.37f, hk, rk, 0f, 0f, out var pk, out var qk);
                tips[k] = pk + Vector3.Transform(new Vector3(0f, 0f, 6f), qk);
            }
            var nrm = Vector3.Normalize(Vector3.Cross(tips[1] - tips[0], tips[2] - tips[0])); if (nrm.Y < 0f) nrm = -nrm;
            Check(MathF.Abs(MathF.Atan2(nrm.X, nrm.Y) - 8f * Deg) < 0.2f * Deg && MathF.Abs(nrm.Z) < 1e-3f, $"tilt: tip plane normal {nrm}");
            // cyclic pitch: with the lean to +X (high side -X), the most pitch 90 deg before -X in the clockwise turn: at +Z
            // (clockwise from above the span goes +Z -> -X -> -Z -> +X)
            float atMinusZ = K.BladePitch(0f, lean, -Vector3.UnitZ), atPlusZ = K.BladePitch(0f, lean, Vector3.UnitZ), atX = K.BladePitch(0f, lean, Vector3.UnitX);
            Check(MathF.Abs(atPlusZ - 8f * Deg) < 1e-4f && MathF.Abs(atMinusZ + 8f * Deg) < 1e-4f && MathF.Abs(atX) < 1e-4f, $"cyclic pitch: -Z {atMinusZ / Deg:F2} +Z {atPlusZ / Deg:F2} +X {atX / Deg:F2} deg");
            var turned = Vector3.Transform(Vector3.UnitZ, K.Spin(MathF.PI / 2f));
            Check(Vector3.Dot(turned, -Vector3.UnitX) > 0.999f, $"a quarter turn takes +Z to -X (clockwise): {turned}");
            Check(K.BladePitch(0.1f, Vector3.Zero, Vector3.UnitZ) == 0.1f, "no tilt: collective only");
            // the blur disc rises with coning above its model's, tilts with the disc
            K.BlurPose(hub, Quaternion.Identity, new Vector3(0f, 1.8f, 0f), Quaternion.Identity, 5f * Deg, 2.5f * Deg, 6f, out var bp, out _);
            Check(bp.Y > 1.8f + 0.1f, $"blur: raised with coning ({bp.Y:F3})");
        }
        {
            // the reaction torque: Q = P / omega, momentum theory - the 1.5 m rotor (tip 6.675 m) holding a 17.2 t ship at
            // 0.84 kg/m^3: induced velocity sqrt(T / 2 rho A), P = 1.15 T v + profile; Q in the hundreds of kN m (a real
            // helicopter of that weight needs ~4-5 MW)
            float T = 17161f * 9.81f, R = 6.675f, rho = 0.84f, A = MathF.PI * R * R;
            float vi = MathF.Sqrt(T / (2f * rho * A)), omega = K.TipSpeed / R;
            float P = K.Power(T, R, 1f, rho), Q = K.ReactionTorque(T, R, 1f, rho);
            Check(MathF.Abs(P - (K.InducedFactor * T * vi + K.ProfileCoef * rho * A * MathF.Pow(K.TipSpeed, 3))) < 1f, $"power: momentum theory ({P:F0} W)");
            Check(MathF.Abs(Q - K.ReactionScale * P / omega) < 1e-2f * Q, $"reaction: Q = ReactionScale x P / omega ({Q:F0} N m)");
            Check(P > 2e6f && P < 8e6f && Q > 1e4f && Q < 6e4f, $"reaction: a 17 t helicopter's hover power {P / 1e6f:F2} MW, torque {Q / 1e3f:F0} kN m (x ReactionScale {K.ReactionScale})");
            Check(K.ReactionTorque(2f * T, R, 1f, rho) > 2f * Q, "reaction: grows faster than thrust (T^1.5 induced)");
            Check(K.ReactionTorque(0f, R, 1f, rho) > 0f && K.ReactionTorque(0f, R, 1f, rho) < 0.2f * Q, "reaction: flat pitch still drags (profile power), little");
            Check(K.ReactionTorque(T, R, 0f, rho) == 0f && K.ReactionTorque(T, R, 0.01f, rho) == 0f, "reaction: none when stopped");
            Check(K.ReactionTorque(T, R, 1f, 0.5f * rho) > Q, "reaction: thinner air - more induced power for the same thrust");
            Check(K.BlurOpacity(1f) == 1f, "blur: fully shown at speed (the material's alpha is the translucency - no dithering)");
        }
        // 7. the kinds: the REVERSIBLE ROTOR (kind TailRotor) is governed (rpm, animation), lifts with rpm - and has NO cyclic and
        //    NO reaction torque (RotorCyclic's list is HasCyclic's); its thrust either way is NOT a kind's: ReversibleThrustComponent
        {
            var T = AeroPropulsorComponent.Kinds.TailRotor; var R = AeroPropulsorComponent.Kinds.Rotor;
            var P = AeroPropulsorComponent.Kinds.Turboprop; var F = AeroPropulsorComponent.Kinds.Fan;
            Check(!AeroPropulsorComponent.HasCyclic(T) && !AeroPropulsorComponent.HasReaction(T), "reversible rotor: no cyclic, no reaction torque");
            Check(AeroPropulsorComponent.Governed(T) && AeroPropulsorComponent.LiftNeedsRpm(T), "reversible rotor: governed, lift with rpm");
            Check(AeroPropulsorComponent.HasCyclic(R) && AeroPropulsorComponent.HasReaction(R), "main rotor: cyclic and reaction");
            Check(!AeroPropulsorComponent.HasCyclic(P) && !AeroPropulsorComponent.HasReaction(P) && AeroPropulsorComponent.Governed(P) && !AeroPropulsorComponent.LiftNeedsRpm(P),
                "turboprop: governed (rpm, blur), thrust not gated by rpm, no rotor mechanics");
            Check(!AeroPropulsorComponent.Governed(F) && !AeroPropulsorComponent.HasCyclic(F), "fan: none of it");
            Check(AeroPropulsorComponent.TargetRpm(R, 0f) == 1f && AeroPropulsorComponent.TargetRpm(T, 0f) == 1f, "rotors hold 100 % whatever the load");
            // the game code routes by these - the source says so
            string sim = SimDir();
            string cyc = File.ReadAllText(Path.Combine(sim, "Propulsion", "RotorCyclic.cs"));
            Check(cyc.Contains("AeroPropulsorComponent.HasCyclic(kind)") && !cyc.Contains("Kinds.Rotor") && !cyc.Contains("GiveTail"), "RotorCyclic: its rotors (cyclic + reaction) are HasCyclic's; no special yaw actuator");
            string tt = File.ReadAllText(Path.Combine(sim, "Propulsion", "ThrustTorque.cs"));
            Check(tt.Contains("RotorCyclic.Has(aero, n) ? RotorCyclic.ReactionTorque"), "ThrustTorque: the reaction only for RotorCyclic's rotors");
            // THE STRUCTURE (2026-10-17): the cyclic's force and torque and the reaction - applied to the body at the centre of mass -
            // published at each rotor's block, every step that applied them, as an engine's (off with ThrustLoads); not a contact
            Check(cyc.Contains("if (applied && k < loadF.Length) { loadF[k] = forceK; loadT[k] = givenK; }") && cyc.Contains("each[k] = dirs[i] * Q;")
                  && cyc.Contains("aero.PublishBlockLoad(e, f, hub, t - Vector3.Cross(hub - com, f), true);"),
                  "RotorCyclic: each rotor's cyclic force, the torque it gave and its reaction published at its hub (PublishBlockLoad, isEngine), the ship's moment kept");
            Check(tt.Contains("if (RotorCyclic.Has(aero, n)) RotorCyclic.PublishLoads(aero, com, dirs, n, ApplyForces);"), "ThrustTorque: the rotors' loads published every step, after they were applied");
            Check(cyc.Contains("if (aero.IsServerScene) aero.NoteKnownImpulse(forceW * dt);"), "RotorCyclic: the tilt's force noted for the contact balance (not the wheels' or plates')");
            Check(tt.Contains("aero.LastExtraForceWorld = Vector3.Zero;   // (every step"), "ThrustTorque: the extra force for the contact balance reset every step (no stale one after an early return)");
            // reversible = the component on the thruster entity, cached per thruster list; the solve and the share take it
            string cache = File.ReadAllText(Path.Combine(sim, "Propulsion", "OffsetThrustJob.Cache.cs"));
            Check(cache.Contains("Rev = childEntity.TryGet<ReversibleThrustComponent>()"), "thruster list: reversible = ReversibleThrustComponent on the block");
            Check(tt.Contains("rev[i] = thrusters[i].Rev != null") && tt.Contains("anyRev ? rev : null") && tt.Contains("anyRev ? side : null"), "ThrustTorque: the solve and the game share take the reversible thrusters");
            Check(tt.Contains("if (anyRev || RotorCyclic.Has(aero, n) || steerN > 0)"), "ThrustTorque: a rate pilot's grid with reversible thrusters, main rotors or a lit solid rocket is solved too");
            string all = string.Concat(Directory.GetFiles(sim, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
            Check(!all.Contains("Reversible(kind") && !all.Contains("Reversible(p.Kind") && !all.Contains("Reversible(Kind"), "nobody decides reversible by a propulsor kind");
            string comp = File.ReadAllText(Path.Combine(sim, "Propulsion", "ReversibleThrustComponent.cs"));
            Check(comp.Contains("td.Direction = want; Data.Set(td);"), "ReversibleThrustComponent: it turns the block's ThrustData.Direction (the game's own booking)");
            string inj = File.ReadAllText(Path.Combine(sim, "Injection", "EntityInjector.cs"));
            Check(inj.Contains("ReversibleThrustComponent.Prefabs.Contains(entity.Guid)"), "EntityInjector: the component by prefab GUID");
            string table = File.ReadAllText(Path.Combine(sim, "Blocks", "PropulsorBlocks.g.cs"));
            Check(table.Contains("\"HelicopterTailRotor_050\", AeroPropulsorComponent.Kinds.TailRotor") && PropulsorBlocks.Rows.Any(r => r.Name == "HelicopterTailRotor_050" && r.Reversible), "PropulsorBlocks: HelicopterTailRotor_050 - kind TailRotor, Reversible");
            Check(System.Text.RegularExpressions.Regex.Matches(table, ", true\\),").Count == 1, "PropulsorBlocks: only it is reversible");
        }
        // 8. the reversible rotor's blades: the drawn pitch is +/- PitchDeg by its push's side, the same size both ways (no collective)
        {
            Check(TailRotor.Pitch(1) > 0f && TailRotor.Pitch(-1) < 0f && TailRotor.Pitch(1) == -TailRotor.Pitch(-1)
                  && MathF.Abs(TailRotor.Pitch(1) - TailRotor.PitchDeg * Deg) < 1e-6f, $"reversible rotor pitch: +{TailRotor.Pitch(1) / Deg:F1} / {TailRotor.Pitch(-1) / Deg:F1} deg");
            // a + pitch (leading edge up) is thrust along the block's Up: its definition's way (blade 0 pitched: leading edge -X goes up)
            K.BladePose(Vector3.Zero, Quaternion.Identity, 0f, new Vector3(0f, 0f, 0.5f), Quaternion.Identity, 0f, TailRotor.Pitch(-1), out _, out var rq);
            Check(Vector3.Transform(-Vector3.UnitX, rq).Y < -0.05f, "reversible rotor pitch: reversed - the leading edge down");
        }
        // 9. the turboprop's governor: on from rest it spins up to the idle (75 %) and holds it; full load takes it to 100 %; load
        //    off back to idle; off it coasts down - never above 100 %, never below 0
        {
            var P = AeroPropulsorComponent.Kinds.Turboprop; float w = 0f; bool ok = true;
            for (int i = 0; i < 60 * 20; i++) { w = K.StepRpm(w, true, 1f / 60f, AeroPropulsorComponent.TargetRpm(P, 0f)); if (w > K.TurbopropIdleRpm + 1e-6f) ok = false; }
            Check(ok && MathF.Abs(w - K.TurbopropIdleRpm) < 1e-6f, $"turboprop: on, no load - idles at {w * 100f:F1} %");
            float t95 = -1f;
            for (int i = 0; i < 60 * 20; i++) { w = K.StepRpm(w, true, 1f / 60f, AeroPropulsorComponent.TargetRpm(P, 1f)); if (t95 < 0f && w >= 0.99f) t95 = i / 60f; }
            Check(w == 1f && t95 > 0f && t95 < K.SpinUpSeconds, $"turboprop: full load - 100 % ({t95:F1} s from idle)");
            for (int i = 0; i < 60 * 20; i++) w = K.StepRpm(w, true, 1f / 60f, AeroPropulsorComponent.TargetRpm(P, 0.5f));
            Check(MathF.Abs(w - (K.TurbopropIdleRpm + 0.5f * (1f - K.TurbopropIdleRpm))) < 1e-5f, $"turboprop: half load - {w * 100f:F1} %");
            for (int i = 0; i < 60 * 20; i++) w = K.StepRpm(w, true, 1f / 60f, AeroPropulsorComponent.TargetRpm(P, 0f));
            Check(MathF.Abs(w - K.TurbopropIdleRpm) < 1e-5f, "turboprop: load off - back to idle");
            for (int i = 0; i < 60 * 120; i++) w = K.StepRpm(w, false, 1f / 60f, AeroPropulsorComponent.TargetRpm(P, 1f));
            Check(w == 0f, "turboprop: off - coasts to a stop");
            // the blur treatment at idle: the blades mostly gone, the disc mostly in
            Check(K.BladeOpacity(K.TurbopropIdleRpm) < 0.2f && K.BlurOpacity(K.TurbopropIdleRpm) > 0.6f, $"turboprop at idle: blades {K.BladeOpacity(K.TurbopropIdleRpm):F2}, blur {K.BlurOpacity(K.TurbopropIdleRpm):F2}");
            // the rotors' spin-up law unchanged by the target (default 1)
            float a = 0.3f, b = 0.3f; for (int i = 0; i < 100; i++) { a = K.StepRpm(a, true, 1f / 60f); b = K.StepRpm(b, true, 1f / 60f, 1f); }
            Check(a == b, "rotor: the governor's default target is 100 %");
            // the split turboprop's parts by name: the generator names them as the client component looks for them
            string gen = File.ReadAllText(Path.Combine(SimDir(), "..", "..", "tools", "meshgen", "gen_propulsor_defs.py"));
            string anim = File.ReadAllText(Path.Combine(SimDir(), "Propulsion", "AeroRotorAnimComponent.cs"));
            foreach (var nm in new[] { "TurbopropSpinner", "TurbopropBlades", "TurbopropBlur" })
                Check(gen.Contains($"name=\"{nm}\"") && anim.Contains($"\"{nm}\""), $"turboprop part name {nm}: generator and AeroRotorAnimComponent agree");
        }
        BalanceChecks();
        Console.WriteLine(_fails == 0 ? $"RotorTests: all {_checks} checks passed" : $"RotorTests: {_fails} of {_checks} checks FAILED");
        return _fails == 0 ? 0 : 1;
    }
}
