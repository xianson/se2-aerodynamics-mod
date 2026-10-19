using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AeroMod;

#pragma warning disable
static partial class Program
{
    /// <summary>
    /// THE AUTOPILOT SIM'S FORCE POLARS (dotnet run -- appolar NAME... ; written to Tests/data/polars/NAME.polar): a dumped
    /// ship's whole air force - the model's exact face sum and its wings (Exact) - over the flow's angle of attack (-30..90 deg,
    /// 2 apart) and sideslip (-20..20, 10 apart) at Mach 0.3 and 0.6, as coefficients (force / q in m2, torque / q in m3, grid
    /// axes, about its centre of mass), its rotation damping per grid axis (torque / (q x rate)), its mass and the inertia of
    /// its cells. Tests/AutopilotSim flies the planes on these, the stall from the mod's own LiftPeak over them.
    /// The flow for (alpha, beta): the ship moving along nose cos a cos b - up sin a cos b + right sin b (right = nose x up).
    /// </summary>
    static int ApPolarExport(string dataDir, string[] names)
    {
        var outDir = Path.Combine(dataDir, "polars");
        Directory.CreateDirectory(outDir);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var nm in names)
        {
            var path = Directory.GetFiles(dataDir, nm + ".boxes", SearchOption.AllDirectories).FirstOrDefault();
            if (path == null) { Console.WriteLine($"   {nm}: no .boxes"); continue; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var boxes = LoadBoxes(path);
            float mass = 0f;
            foreach (var line in File.ReadLines(path).Take(3))
            {
                int i = line.IndexOf(" mass ");
                if (line.StartsWith("#") && i >= 0 && float.TryParse(line.Substring(i + 6).Split(' ')[0], System.Globalization.NumberStyles.Float, inv, out var m)) mass = m;
            }
            var atmo = new AtmosphereState { Density = 1.225, Pressure = 101325, Temperature = 288, SpeedOfSound = 340, DynamicViscosity = 1.8e-5 };
            var ship = Build(nm, boxes);
            var (nose, up) = FlightAxes(ship, atmo, boxes);
            var right = Vector3.Cross(nose, up);
            // the cells' inertia about the centre of mass, per grid axis (the products left out)
            float cs = ship.Surface.CellSize, co = ship.Surface.CellOffset; long n = 0;
            foreach (var c in ship.Grid.EnumerateOccupiedCells()) n++;
            double cm = mass / Math.Max(1, n), ix = 0, iy = 0, iz = 0;
            foreach (var c in ship.Grid.EnumerateOccupiedCells())
            {
                var r = new Vector3((c.X + co) * cs, (c.Y + co) * cs, (c.Z + co) * cs) - ship.Com;
                ix += cm * (r.Y * r.Y + r.Z * r.Z + cs * cs / 6.0); iy += cm * (r.X * r.X + r.Z * r.Z + cs * cs / 6.0); iz += cm * (r.X * r.X + r.Y * r.Y + cs * cs / 6.0);
            }
            var file = Path.Combine(outDir, nm.Replace('/', '_') + ".polar");
            using var w = new StreamWriter(file);
            string V(Vector3 v) => $"{v.X.ToString("R", inv)} {v.Y.ToString("R", inv)} {v.Z.ToString("R", inv)}";
            w.WriteLine($"# appolar {nm}: AeroBench Exact (faces + wings) at rho 1.225, sea level; coefficients = force / q (m2), torque / q (m3), grid axes, about the centre of mass");
            w.WriteLine($"mass {mass.ToString("R", inv)}");
            w.WriteLine($"inertia {ix.ToString("R", inv)} {iy.ToString("R", inv)} {iz.ToString("R", inv)}");
            w.WriteLine($"nose {V(nose)}");
            w.WriteLine($"up {V(up)}");
            w.WriteLine($"wings {ship.Model.Wings?.Count ?? 0}");
            float[] machs = { 0.3f, 0.6f };
            w.WriteLine("machs 0.3 0.6");
            w.WriteLine("alphas -30 90 2");
            w.WriteLine("betas -20 20 10");
            foreach (float mach in machs)
            {
                float V0 = mach * 340f, q = 0.5f * 1.225f * V0 * V0;
                // the rotation damping: the torque change per rad/s about each grid axis, flying straight
                const float dw = 0.05f;
                Vector3 damp = Vector3.Zero;
                for (int ax = 0; ax < 3; ax++)
                {
                    var e = ax == 0 ? Vector3.UnitX : ax == 1 ? Vector3.UnitY : Vector3.UnitZ;
                    var tp = Exact(ship, nose, V0, atmo, ship.Com, e * dw).Torque; var tm = Exact(ship, nose, V0, atmo, ship.Com, -e * dw).Torque;
                    float d = Vector3.Dot(tp - tm, e) / (2f * dw) / q;
                    if (ax == 0) damp.X = d; else if (ax == 1) damp.Y = d; else damp.Z = d;
                }
                w.WriteLine($"damping {mach.ToString("R", inv)} {V(damp)}");
                for (int b = -20; b <= 20; b += 10)
                    for (int a = -30; a <= 90; a += 2)
                    {
                        float ar = a * MathF.PI / 180f, br = b * MathF.PI / 180f;
                        var d = Vector3.Normalize(nose * MathF.Cos(ar) * MathF.Cos(br) - up * MathF.Sin(ar) * MathF.Cos(br) + right * MathF.Sin(br));
                        var res = Exact(ship, d, V0, atmo, ship.Com);
                        w.WriteLine($"A {mach.ToString("R", inv)} {a} {b} {V(res.Force / q)} {V(res.Torque / q)}");
                    }
            }
            Console.WriteLine($"   {nm}: {mass / 1000f:F1} t, nose {nose}, up {up}, {ship.Model.Wings?.Count ?? 0} wings, inertia ({ix:E2}, {iy:E2}, {iz:E2}) -> {file} ({sw.Elapsed.TotalSeconds:F0} s)");
        }
        return 0;
    }
}
