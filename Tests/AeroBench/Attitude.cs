using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AeroMod;

#pragma warning disable
static partial class Program
{
    /// <summary>
    /// ATTITUDE (2026-10-12, the user: "fix the controllers so they are aware of exact aero torque but they don't cheat"):
    /// the pitch of a ship flown by the mod's attitude law (ThrustAllocator.AttitudeAccel - the game's code, compiled in),
    /// at 60 Hz in the model's own air. Each frame, in the game's order: the air's torque (the model's pitching moment at
    /// this angle of attack, tabled from it, plus its rate damping) turns the ship; then the controller asks its actuators
    /// for I x the law's acceleration, with the air's torque answered (FF) or not (no FF: the law alone, as a controller that
    /// does not know the air), clamped to the actuators' real torque (gyros: cap); then the ship turns and climbs (its
    /// lift bends the flight path, so the angle of attack is the attitude less the path).
    /// Cases: a gust (pitch rate 0.24 rad/s, as measured at Mach 1.65), a 5 deg step of the target, at Mach 0.8, 1.6, 3
    /// at sea level, gyros at their full torque (ATT_CAP, N m; the F-7's 6975 kN m) and at a fifth of it. Reported: the
    /// largest error, the overshoot past the target, the time to settle (under 0.1 deg and 0.005 rad/s), frames at the
    /// torque limit, and departure (over 30 deg off).
    /// </summary>
    static int Attitude(string dataDir, AtmosphereState atmo0, string nm)
    {
        var path = Directory.GetFiles(dataDir, nm + ".boxes", SearchOption.AllDirectories).First();
        var boxes = LoadBoxes(path);
        float mass = 0f;
        foreach (var line in File.ReadLines(path).Take(3))
        {
            int i = line.IndexOf(" mass ");
            if (line.StartsWith("#") && i >= 0 && float.TryParse(line.Substring(i + 6).Split(' ')[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m)) mass = m;
        }
        var ship = Build(nm, boxes);
        var (nose, up) = FlightAxes(ship, atmo0, boxes);
        var pitch = Vector3.Normalize(Vector3.Cross(nose, up));
        float cs = ship.Surface.CellSize, co = ship.Surface.CellOffset;
        double I = 0; long n = 0;
        foreach (var c in ship.Grid.EnumerateOccupiedCells()) n++;
        double cellMass = mass / Math.Max(1, n);
        foreach (var c in ship.Grid.EnumerateOccupiedCells())
        {
            var r = new Vector3((c.X + co) * cs, (c.Y + co) * cs, (c.Z + co) * cs) - ship.Com;
            var rp = r - pitch * Vector3.Dot(r, pitch);
            I += cellMass * (rp.LengthSquared() + cs * cs / 6.0);
        }
        float capFull = float.TryParse(Environment.GetEnvironmentVariable("ATT_CAP"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cc) ? cc : 6.975e6f;
        Console.WriteLine($"   {nm}: mass {mass / 1000f:F1} t, pitch inertia {I:E3} kg m2, gyros {capFull / 1e6f:F2} MN m; law: gain {ThrustAllocator.LinearGain}/s, rate time {ThrustAllocator.RateTime} s, margin {ThrustAllocator.Margin}");
        var atmo = new AtmosphereState { Density = 1.225f, Pressure = 101325, Temperature = 288, SpeedOfSound = 340 };
        int fails = 0;
        foreach (float mach in new[] { 0.8f, 1.6f, 3.0f })
        {
            float V = mach * 340f;
            // the pitching moment over angle of attack (-20..20 deg, 0.5 deg) and its rate damping, from the model
            const float step = 0.5f * MathF.PI / 180f; const int half = 40;
            var M = new float[2 * half + 1];
            Vector3 Dir(float a) => Vector3.Normalize(nose * MathF.Cos(a) - up * MathF.Sin(a));
            for (int k = -half; k <= half; k++) M[k + half] = Vector3.Dot(Exact(ship, Dir(k * step), V, atmo, ship.Com).Torque, pitch);
            const float dq = 0.05f;
            float Mq = (Vector3.Dot(Exact(ship, nose, V, atmo, ship.Com, pitch * dq).Torque, pitch) - Vector3.Dot(Exact(ship, nose, V, atmo, ship.Com, -pitch * dq).Torque, pitch)) / (2f * dq);
            float La = (Vector3.Dot(Exact(ship, Dir(step), V, atmo, ship.Com).Force, up) - Vector3.Dot(Exact(ship, Dir(-step), V, atmo, ship.Com).Force, up)) / (2f * step);
            float Mof(float a) { float x = Math.Clamp(a / step + half, 0f, 2 * half - 1e-3f); int k = (int)x; return M[k] + (M[k + 1] - M[k]) * (x - k); }
            Console.WriteLine($"   Mach {mach:F1}: moment at 0 deg {M[half] / 1e3f:F0} kN m, slope {(M[half + 2] - M[half - 2]) / (4f * step) / 1e6f:F1} MN m/rad, rate damping {Mq / 1e6f:F2} MN m s/rad");
            Console.WriteLine("        case            cap   |  FF  |  peak err  overshoot   settle s   at limit   result");
            foreach (var (label, q0, tgt) in new[] { ("gust 0.24 rad/s", 0.24f, 0f), ("step 5 deg", 0f, 5f * MathF.PI / 180f) })
            foreach (float capShare in new[] { 1f, 0.2f })
            foreach (bool ff in new[] { true, false })
            {
                float cap = capFull * capShare, dt = 1f / 60f;
                float th = 0f, gam = 0f, q = q0;   // (attitude and path from level; the trim moment at 0 deg answered from the start by FF only)
                float peak = 0f, over = 0f, settle = float.NaN; int limit = 0; bool departed = false;
                for (int f = 0; f < 60 * 6; f++)
                {
                    float a = th - gam;
                    float Maero = Mof(a) + Mq * q;
                    q += (float)(Maero / I) * dt;                                     // the air's torque (AeroSimJob)
                    var reach = new Vector3((float)(cap / I));
                    var acc = ThrustAllocator.AttitudeAccel(new Vector3(tgt - th, 0, 0), new Vector3(ff ? q - (float)(Maero / I) * dt : q, 0, 0), reach, new Vector3((float)(Maero / I), 0, 0), 0f, 0f);
                    float tau = (float)(acc.X * I) - (ff ? Maero : 0f);
                    if (MathF.Abs(tau) >= cap) { tau = MathF.Sign(tau) * cap; limit++; }
                    q += (float)(tau / I) * dt;                                       // the actuators (gyros)
                    th += q * dt;
                    gam += La * a / (mass * V) * dt;                                  // the path bends with the lift
                    float e = tgt - th;
                    if (f > 6) peak = MathF.Max(peak, MathF.Abs(e));
                    // overshoot: past the target on the far side from where it started
                    float side = tgt != 0f ? MathF.Sign(tgt) : -MathF.Sign(q0);
                    over = MathF.Max(over, -side * e);
                    if (MathF.Abs(e) > 30f * MathF.PI / 180f) { departed = true; break; }
                    bool calm = MathF.Abs(e) < 0.1f * MathF.PI / 180f && MathF.Abs(q) < 0.005f;
                    if (calm && float.IsNaN(settle)) settle = f * dt;
                    if (!calm) settle = float.NaN;
                    if (Environment.GetEnvironmentVariable("ATT_TRACE") == $"{mach:F1}{label[0]}{capShare:F1}{(ff ? 1 : 0)}" && f % 6 == 0)
                        Console.WriteLine($"          t {f * dt:F2} th {th * 57.3f:F3} gam {gam * 57.3f:F3} a {a * 57.3f:F3} q {q:F4} Maero {Maero / 1e3f:F0} tau {tau / 1e3f:F0} kN m");
                }
                // beyond the gyros: the air's torque at the target angle, or the gust's stop (its rate at the braking the gyros
                // leave against the air's torque on the way), more than they give - a departure there is the ship's, not the law's
                float stopA = (float)((cap - MathF.Abs(Mof(MathF.Abs(q0) * MathF.Abs(q0) / (2f * (float)(cap / I))))) / I);
                bool beyond = MathF.Abs(Mof(tgt)) > cap || (q0 != 0f && (stopA <= 0f || MathF.Abs(Mof(q0 * q0 / (2f * stopA))) > cap));
                string res = departed ? (beyond ? "departed (beyond the gyros)" : "DEPARTED") : float.IsNaN(settle) ? "not settled" : "ok";
                if (ff && !beyond && res != "ok") fails++;
                Console.WriteLine($"        {label,-15} {cap / 1e6f,5:F2}  | {(ff ? "yes" : "no "),-4} | {peak * 57.2958f,8:F2}d  {over * 57.2958f,8:F2}d  {(float.IsNaN(settle) ? "-" : settle.ToString("F2")),8}   {limit,8}   {res}");
            }
        }
        Console.WriteLine(fails == 0 ? "   attitude: every case within the gyros, the air fed forward, settles (ok)" : $"   attitude: {fails} case(s) within the gyros, with the air fed forward did not settle (FAIL)");
        return fails == 0 ? 0 : 1;
    }
}
