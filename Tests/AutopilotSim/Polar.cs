using System.IO;
using AeroMod;

/// <summary>
/// A dumped ship's air from its force polar (AeroBench "appolar": Tests/data/polars/NAME.polar - the model's exact faces and
/// wings over angle of attack, sideslip and Mach, as force / q and torque / q, plus its rotation damping): bilinear in the flow's
/// angles, linear in Mach between 0.3 and 0.6 (clamped). The control surfaces: SurfPerQ (N m per Pa of dynamic pressure per local
/// axis, either way) - the sim's choice per scenario.
/// </summary>
sealed class PolarAir : IAir
{
    public string Name; public float Mass; public Vector3 I, Nose, Up, Right; public int Wings;
    public Vector3 SurfPerQ = new(400f, 400f, 400f);
    float[] _machs = { 0.3f, 0.6f };
    const int A0 = -30, A1 = 90, DA = 2, B0 = -20, B1 = 20, DB = 10, NA = (A1 - A0) / DA + 1, NB = (B1 - B0) / DB + 1;
    readonly Vector3[,,] _f = new Vector3[2, NB, NA], _t = new Vector3[2, NB, NA];
    readonly Vector3[] _damp = new Vector3[2];
    public bool Lifts => true;
    public Vector3 SurfaceReach(float q) => SurfPerQ * q;

    public static string Dir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "Tests", "data", "polars"))) d = d.Parent;
        return d == null ? null : Path.Combine(d.FullName, "Tests", "data", "polars");
    }

    public static PolarAir Load(string name)
    {
        var dir = Dir(); var path = dir == null ? null : Path.Combine(dir, name + ".polar");
        if (path == null || !File.Exists(path)) return null;
        var p = new PolarAir { Name = name };
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        float F(string s) => float.Parse(s, inv);
        Vector3 V(string[] a, int i) => new(F(a[i]), F(a[i + 1]), F(a[i + 2]));
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("#") || line.Length == 0) continue;
            var a = line.Split(' ');
            switch (a[0])
            {
                case "mass": p.Mass = F(a[1]); break;
                case "inertia": p.I = V(a, 1); break;
                case "nose": p.Nose = Vector3.Normalize(V(a, 1)); break;
                case "up": p.Up = Vector3.Normalize(V(a, 1)); break;
                case "wings": p.Wings = int.Parse(a[1]); break;
                case "damping": p._damp[F(a[1]) < 0.45f ? 0 : 1] = V(a, 2); break;
                case "A":
                {
                    int m = F(a[1]) < 0.45f ? 0 : 1, al = (int.Parse(a[2]) - A0) / DA, be = (int.Parse(a[3]) - B0) / DB;
                    p._f[m, be, al] = V(a, 4); p._t[m, be, al] = V(a, 7);
                    break;
                }
            }
        }
        p.Right = Vector3.Cross(p.Nose, p.Up);
        return p;
    }

    /// <summary>The flow's angle of attack and sideslip (rad) for the ship's motion through the air (local).</summary>
    public void Angles(Vector3 air, out float alpha, out float beta)
    {
        var d = Vector3.Normalize(air);
        beta = MathF.Asin(Math.Clamp(Vector3.Dot(d, Right), -1f, 1f));
        alpha = MathF.Atan2(-Vector3.Dot(d, Up), Vector3.Dot(d, Nose));
    }

    public void Forces(Vector3 a, Vector3 w, float rho, out Vector3 force, out Vector3 torque)
    {
        force = torque = Vector3.Zero;
        float sp = a.Length(); if (sp < 1e-3f) return;
        Angles(a, out float al, out float be);
        float q = 0.5f * rho * sp * sp, mach = sp / 340f;
        float mk = Math.Clamp((mach - 0.3f) / 0.3f, 0f, 1f);
        float ai = Math.Clamp((al * 180f / MathF.PI - A0) / DA, 0f, NA - 1.0001f), bi = Math.Clamp((be * 180f / MathF.PI - B0) / DB, 0f, NB - 1.0001f);
        int ia = (int)ai, ib = (int)bi; float fa = ai - ia, fb = bi - ib;
        Vector3 Lerp2(Vector3[,,] t, int m) =>
            (t[m, ib, ia] * (1f - fa) + t[m, ib, ia + 1] * fa) * (1f - fb) + (t[m, ib + 1, ia] * (1f - fa) + t[m, ib + 1, ia + 1] * fa) * fb;
        Vector3 fc = Lerp2(_f, 0) * (1f - mk) + Lerp2(_f, 1) * mk, tc = Lerp2(_t, 0) * (1f - mk) + Lerp2(_t, 1) * mk;
        Vector3 dc = _damp[0] * (1f - mk) + _damp[1] * mk;
        force = fc * q; torque = (tc + dc * w) * q;
    }
}
