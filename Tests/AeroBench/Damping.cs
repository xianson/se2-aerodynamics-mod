using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AeroMod;

#pragma warning disable
static partial class Program
{
    /// <summary>
    /// DAMPING (2026-10-12, the user: "the interceptor wobbles in pitch at Mach 1.6+ but is recoverable"): the ship's pitch
    /// short-period mode across Mach, from the model itself - the static stability readout says only whether the air turns
    /// the ship back, not whether it settles. At each Mach (sea level, and DAMP_RHO for thinner air), level flight along its
    /// nose:
    ///   M_a  the pitching moment's slope with angle of attack (N m / rad; < 0 restoring) - the ship turned +-1 deg
    ///   M_q  its slope with pitch rate (N m s / rad; < 0 damping) - the ship turning at +-0.05 rad/s
    ///   L_a  the lift's slope with angle of attack (N / rad) - it damps too: a ship that pitches up climbs, and its angle
    ///        of attack falls back
    ///   I    the pitch inertia (uniform block density over its cells, the header's mass)
    /// and from them, as an aircraft's short period: w_n = sqrt(-M_a / I), 2 z w_n = -M_q / I + L_a / (m V) - the period
    /// and the damping ratio. Under ~0.1: a wobble that rings; under 0: one that grows.
    /// </summary>
    static int Damping(string dataDir, AtmosphereState atmo0, string nm)
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
        if (Environment.GetEnvironmentVariable("DAMP_FLIP") == "1") nose = -nose;   // (the nose guess checked the other way)
        // the blueprint's own nose (its cockpit's forward) and centre of mass (its blocks' masses) - tools/gridgen/bpinfo.py
        static Vector3? Env3(string k) { var v = Environment.GetEnvironmentVariable(k)?.Split(','); return v?.Length == 3 ? new Vector3(float.Parse(v[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(v[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(v[2], System.Globalization.CultureInfo.InvariantCulture)) : null; }
        if (Env3("DAMP_NOSE") is Vector3 dn) { nose = Vector3.Normalize(dn); if (MathF.Abs(Vector3.Dot(nose, up)) > 0.5f) up = MathF.Abs(nose.Y) < 0.5f ? Vector3.UnitY : Vector3.UnitZ; }
        if (Env3("DAMP_COM") is Vector3 dc) { Console.WriteLine($"   centre of mass {dc} (cells' centroid {ship.Com}: {Vector3.Dot(dc - ship.Com, nose):+0.00;-0.00} m along the nose)"); ship.Com = dc; }
        var pitch = Vector3.Normalize(Vector3.Cross(nose, up));   // (+: nose up)
        // the pitch inertia: the cells about the centre of mass, uniform density
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
        Console.WriteLine($"   {nm}: mass {mass / 1000f:F1} t, pitch inertia {I:E3} kg m2, nose {nose}, up {up}, wings {ship.Model.Wings?.Count ?? 0}");
        float rhoThin = float.TryParse(Environment.GetEnvironmentVariable("DAMP_RHO"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rr) ? rr : 0.4f;
        foreach (float rho in new[] { 1.225f, rhoThin })
        {
            var atmo = new AtmosphereState { Density = rho, Pressure = 101325 * rho / 1.225, Temperature = 288, SpeedOfSound = 340 };
            Console.WriteLine($"   air {rho:F3} kg/m3:  Mach |   M_a (MN m/rad)   M_q (MN m s/rad)   L_a (MN/rad) |  period s   damping ratio  (aero part / lift part)");
            foreach (float mach in new[] { 0.5f, 0.8f, 0.95f, 1.2f, 1.6f, 2.0f, 2.5f, 3.0f })
            {
                float V = mach * 340f;
                const float da = 1f * MathF.PI / 180f, dq = 0.05f;
                Vector3 Dir(float a) => Vector3.Normalize(nose * MathF.Cos(a) - up * MathF.Sin(a));   // (the ship nose-up by a: the air comes from below)
                var tp = Exact(ship, Dir(+da), V, atmo, ship.Com); var tm = Exact(ship, Dir(-da), V, atmo, ship.Com);
                float Ma = (Vector3.Dot(tp.Torque, pitch) - Vector3.Dot(tm.Torque, pitch)) / (2f * da);
                float La = (Vector3.Dot(tp.Force, up) - Vector3.Dot(tm.Force, up)) / (2f * da);
                var qp = Exact(ship, nose, V, atmo, ship.Com, pitch * dq); var qm = Exact(ship, nose, V, atmo, ship.Com, -pitch * dq);
                float Mq = (Vector3.Dot(qp.Torque, pitch) - Vector3.Dot(qm.Torque, pitch)) / (2f * dq);
                string mode;
                if (Ma >= 0f) mode = "   statically unstable (no oscillation: it diverges)";
                else
                {
                    double wn = Math.Sqrt(-Ma / I);
                    double aero = -Mq / I, lift = La / (mass * V);
                    double z = (aero + lift) / (2 * wn);
                    mode = $"{2 * Math.PI / wn,9:F2}   {z,12:F3}  ({aero / (2 * wn):F3} / {lift / (2 * wn):F3}){(z < 0.1 ? "  <- rings" : "")}";
                }
                Console.WriteLine($"                 {mach,4:F2} | {Ma / 1e6f,12:F3}   {Mq / 1e6f,15:F3}   {La / 1e6f,12:F3} | {mode}");
            }
        }
        return 0;
    }
}
