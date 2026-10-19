using System.IO;
using AeroMod;

/// <summary>
/// THE SIM'S TERRAIN, behind the autopilot's own ITerrainQuery (world: Y up, the ground's height H(x, z)): flat ground, a slope, a
/// field of ridges and valleys, a cliff, a canyon, or a heightfield dumped from a real planet (Tests/data/terrain/*.hf: the
/// game's TerrainDump harness verb - Dev/TerrainDump.cs; none dumped yet). Below: the height over H along the gravity (the
/// normal from H's slope); Cast: marched along the ray at a fifth of its length's resolution, then bisected. The plant stands
/// on the same H (a ship below it has hit the ground).
/// </summary>
sealed class SimTerrain : ITerrainQuery
{
    public string Name = "flat";
    public Func<double, double, double> H = (x, z) => 0.0;
    public int Casts;

    public static SimTerrain Flat(double h = 0) => new() { Name = "flat", H = (x, z) => h };
    /// <summary>A plane rising 'grade' (rise / run) toward +X.</summary>
    public static SimTerrain Slope(double grade) => new() { Name = $"slope {grade:0.00}", H = (x, z) => Math.Max(0, x) * grade };
    /// <summary>Ridges across X (crests 'height' high, 'wave' m apart) with a second set across Z: hills and valleys.</summary>
    public static SimTerrain Hills(double height = 60, double wave = 400) => new()
    {
        Name = $"hills {height:0} m / {wave:0} m",
        H = (x, z) => height * 0.5 * (1 - Math.Cos(2 * Math.PI * x / wave)) * (0.75 + 0.25 * Math.Cos(2 * Math.PI * z / (1.7 * wave))),
    };
    /// <summary>A cliff face 'height' high at X = at (rising over 'width' m toward 'toward' (+1: +X, -1: -X)), flat before and after.</summary>
    public static SimTerrain Cliff(double at, double height, double width = 5, int toward = 1) => new()
    {
        Name = $"cliff {height:0} m at {at:0} m",
        H = (x, z) => height * Math.Clamp(toward * (x - at) / width, 0, 1),
    };
    /// <summary>A canyon along X, 'depth' deep and 'width' wide, in a plateau at height 'top'.</summary>
    public static SimTerrain Canyon(double top, double depth, double width) => new()
    {
        Name = $"canyon {depth:0} x {width:0} m",
        H = (x, z) => top - depth * Math.Clamp(1.5 - Math.Abs(z) / (0.5 * width), 0, 1),
    };

    /// <summary>A dumped heightfield (header "hf nx nz step originX originZ", then nz lines of nx heights, m): bilinear.</summary>
    public static SimTerrain Load(string path)
    {
        var lines = File.ReadAllLines(path); var inv = System.Globalization.CultureInfo.InvariantCulture;
        var head = lines[0].Split(' ');
        int nx = int.Parse(head[1]), nz = int.Parse(head[2]); double step = double.Parse(head[3], inv), ox = double.Parse(head[4], inv), oz = double.Parse(head[5], inv);
        var h = new double[nz, nx];
        for (int j = 0; j < nz; j++) { var row = lines[1 + j].Split(' '); for (int i = 0; i < nx; i++) h[j, i] = double.Parse(row[i], inv); }
        return new SimTerrain
        {
            Name = Path.GetFileNameWithoutExtension(path),
            H = (x, z) =>
            {
                double fi = Math.Clamp((x - ox) / step, 0, nx - 1.0001), fj = Math.Clamp((z - oz) / step, 0, nz - 1.0001);
                int i = (int)fi, j = (int)fj; double a = fi - i, b = fj - j;
                return (h[j, i] * (1 - a) + h[j, i + 1] * a) * (1 - b) + (h[j + 1, i] * (1 - a) + h[j + 1, i + 1] * a) * b;
            },
        };
    }

    public Vector3 NormalAt(double x, double z)
    {
        const double e = 0.5;
        double dx = (H(x + e, z) - H(x - e, z)) / (2 * e), dz = (H(x, z + e) - H(x, z - e)) / (2 * e);
        return Vector3.Normalize(new Vector3((float)-dx, 1f, (float)-dz));
    }

    public bool Below(Vector3D p, Vector3 down, float reach, out float height, out Vector3 normal)
    {
        Casts++;
        height = (float)(p.Y - H(p.X, p.Z)); normal = NormalAt(p.X, p.Z);
        return height >= 0f && height <= reach;
    }

    public bool Cast(Vector3D p, Vector3 dir, float length, out float distance, out Vector3 normal)
    {
        Casts++;
        distance = 0f; normal = Vector3.UnitY;
        double Above(double t) { var q = p + (Vector3D)(dir * (float)t); return q.Y - H(q.X, q.Z); }
        if (Above(0) <= 0) return true;
        int steps = 200; double dtc = length / steps, prev = 0;
        for (int k = 1; k <= steps; k++)
        {
            double t = k * dtc;
            if (Above(t) <= 0)
            {
                double lo = prev, hi = t;
                for (int b = 0; b < 20; b++) { double m = 0.5 * (lo + hi); if (Above(m) <= 0) hi = m; else lo = m; }
                distance = (float)hi; var q = p + (Vector3D)(dir * distance); normal = NormalAt(q.X, q.Z);
                return true;
            }
            prev = t;
        }
        return false;
    }
}
