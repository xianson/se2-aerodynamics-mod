using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AeroMod;

#pragma warning disable
static partial class Program
{
    /// <summary>
    /// DRAGMAP (2026-10-12, the user: the Tu-134 flies at L/D 3.4 where the real one makes ~7 - where does its drag come
    /// from?): the hull's zero-lift pressure drag face by face (DampedShadowedDragModel.FacePressures - the force table's
    /// pressures, the drag overlay's), along the nose (DAMP_NOSE, else the bench's guess) at sea level, 100 m/s and Mach
    /// 0.5 / 0.8 / 0.95 / 1.2: by station (tenths of the length, nose first), front-facing (windward pressure) against
    /// rear-facing (base suction), and the fuselage (within FUSE_R m of the centreline, default 1.6) against the parts off
    /// it (nacelles, wing and tail roots, pylons). Faces the wing model owns are left out (their drag is the wings').
    /// Drag area CdA = drag / q (m2).
    /// </summary>
    static int DragMap(string dataDir, AtmosphereState atmo0, string nm)
    {
        var path = Directory.GetFiles(dataDir, nm + ".boxes", SearchOption.AllDirectories).First();
        var boxes = LoadBoxes(path);
        var ship = Build(nm, boxes);
        var (nose, up) = FlightAxes(ship, atmo0, boxes);
        static Vector3? Env3(string k) { var v = Environment.GetEnvironmentVariable(k)?.Split(','); return v?.Length == 3 ? new Vector3(float.Parse(v[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(v[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(v[2], System.Globalization.CultureInfo.InvariantCulture)) : null; }
        if (Env3("DAMP_NOSE") is Vector3 dn) { nose = Vector3.Normalize(dn); if (MathF.Abs(Vector3.Dot(nose, up)) > 0.5f) up = MathF.Abs(nose.Y) < 0.5f ? Vector3.UnitY : Vector3.UnitZ; }
        float fuseR = float.TryParse(Environment.GetEnvironmentVariable("FUSE_R"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fr) ? fr : 1.6f;
        var span = Vector3.Normalize(Vector3.Cross(up, nose));
        var inner = (DampedShadowedDragModel)ship.Model.InnerModel;
        var work = new DampedShadowedDragModel.TableWork();
        var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var area = new List<float>(); var cps = new List<float>();
        // the hull's extent along the nose, and its centreline (the cells' median across)
        float lo = float.MaxValue, hi = float.MinValue; var ss = new List<float>(); var us = new List<float>();
        float cs = ship.Surface.CellSize, co = ship.Surface.CellOffset;
        foreach (var c in ship.Grid.EnumerateOccupiedCells())
        {
            var p = new Vector3((c.X + co) * cs, (c.Y + co) * cs, (c.Z + co) * cs);
            float x = Vector3.Dot(p, nose); lo = MathF.Min(lo, x); hi = MathF.Max(hi, x);
            ss.Add(Vector3.Dot(p, span)); us.Add(Vector3.Dot(p, up));
        }
        ss.Sort(); us.Sort(); float s0 = ss[ss.Count / 2], u0 = us[us.Count / 2];
        float len = hi - lo;
        Console.WriteLine($"   {nm}: length {len:F1} m along the nose {nose}; fuselage = within {fuseR} m of the centreline");
        foreach (float mach in new[] { 0.29f, 0.5f, 0.8f, 0.95f, 1.2f })
        {
            float V = mach * 340f, q = 0.5f * 1.225f * V * V;
            // the model's own whole drag at zero lift, for scale
            AeroResult r = default;
            for (int k = 0; k < 2; k++) r = ship.Model.Compute(new AeroContext(ship.Grid, ship.Surface, nose * V, new AtmosphereState { Density = 1.225, Pressure = 101325, Temperature = 288, SpeedOfSound = 340 }, ship.Com, BlockSize, Vector3.Zero, -1f, ship.Manifold));
            int nf = inner.FacePressures(ship.Surface, ship.Manifold, nose, mach, work, pos, nrm, area, cps, int.MaxValue);
            var stF = new double[10]; var stR = new double[10];
            double fuseF = 0, fuseR2 = 0, offF = 0, offR = 0;
            for (int i = 0; i < nf; i++)
            {
                double cosA = Vector3.Dot(nrm[i], nose);
                double d = cps[i] * area[i] * cosA;   // (drag area: Cp A (n . travel) - the push back on the ship)
                int b = Math.Clamp((int)((hi - Vector3.Dot(pos[i], nose)) / len * 10f), 0, 9);
                bool front = cosA > 0;
                if (front) stF[b] += d; else stR[b] += d;
                float ds = Vector3.Dot(pos[i], span) - s0, du = Vector3.Dot(pos[i], up) - u0;
                bool fuse = ds * ds + du * du <= fuseR * fuseR;
                if (fuse) { if (front) fuseF += d; else fuseR2 += d; } else { if (front) offF += d; else offR += d; }
            }
            if (Environment.GetEnvironmentVariable("DRAGMAP_SPOTS") == "1" && mach < 0.3f)
            {
                // the biggest front-facing spots: 1 m squares across the flow (span, up), with where they are along it
                var spots = new Dictionary<(int, int), (double cda, double x, double a)>();
                for (int i = 0; i < nf; i++)
                {
                    double cosA = Vector3.Dot(nrm[i], nose); if (cosA <= 0) continue;
                    double d = cps[i] * area[i] * cosA;
                    var key = ((int)MathF.Floor(Vector3.Dot(pos[i], span) - s0), (int)MathF.Floor(Vector3.Dot(pos[i], up) - u0));
                    spots.TryGetValue(key, out var v); spots[key] = (v.cda + d, v.x + d * Vector3.Dot(pos[i], nose), v.a + area[i] * cosA);
                }
                foreach (var kv in spots.OrderByDescending(k => k.Value.cda).Take(14))
                    Console.WriteLine($"      spot span {kv.Key.Item1,3} m up {kv.Key.Item2,3} m: CdA {kv.Value.cda:F2} m2 on {kv.Value.a:F2} m2 facing the flow, {(hi - kv.Value.x / kv.Value.cda) / len * 100:F0}% back");
            }
            double tot = stF.Sum() + stR.Sum();
            Console.WriteLine($"   Mach {mach:F2} ({V:F0} m/s): model drag {r.DragMagnitude / q:F1} m2 (whole: hull, skin, wings); hull pressure {tot:F1} m2 = front-facing {stF.Sum():F1} + rear-facing {stR.Sum():F1}");
            Console.WriteLine($"      fuselage {fuseF + fuseR2:F1} m2 (front {fuseF:F1}, rear {fuseR2:F1})   off the fuselage {offF + offR:F1} m2 (front {offF:F1}, rear {offR:F1})");
            Console.WriteLine("      station (nose->tail):  " + string.Join(" ", Enumerable.Range(0, 10).Select(k => $"{stF[k] + stR[k],5:F1}")) + "   (front-facing: " + string.Join(" ", Enumerable.Range(0, 10).Select(k => $"{stF[k]:F1}")) + ")");
        }
        return 0;
    }
}
