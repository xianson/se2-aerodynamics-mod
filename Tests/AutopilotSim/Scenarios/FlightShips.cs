using AeroMod;

/// <summary>The flight pilot's extra ships and its target source in the sim.</summary>
static class FlightShips
{
    /// <summary>A six-axis thruster gunship (20 t): lift on four corners (2.4x its honest weight), thrust both ways on every axis
    /// through its centre of mass (0.6x its weight a side), gyros, forward guns along -Z (800 m/s, 1.5 km).</summary>
    public static SimShip SixAxis(float side = 0.6f, string name = null)
    {
        var s = new SimShip { Name = name ?? $"six-axis gunship {side:0.0}", Mass = 20000f, I = new Vector3(1.5e5f, 2.0e5f, 1.2e5f), PlanetG = 9.81f, Gm = 2f };
        float w = s.Mass * s.PlanetG * s.Gm, lift = 2.4f * w / 4f, f = side * w / 2f;
        Vector3[] corners = { new(3f, 0f, 4f), new(-3f, 0f, 4f), new(3f, 0f, -4f), new(-3f, 0f, -4f) };
        for (int i = 0; i < 4; i++) s.Add($"lift {i}", corners[i], Vector3.UnitY, lift);
        s.Add("down a", new Vector3(0f, 0f, 3f), -Vector3.UnitY, f); s.Add("down b", new Vector3(0f, 0f, -3f), -Vector3.UnitY, f);
        s.Add("+X a", new Vector3(0f, 0f, 3f), Vector3.UnitX, f); s.Add("+X b", new Vector3(0f, 0f, -3f), Vector3.UnitX, f);
        s.Add("-X a", new Vector3(0f, 0f, 3f), -Vector3.UnitX, f); s.Add("-X b", new Vector3(0f, 0f, -3f), -Vector3.UnitX, f);
        s.Add("+Z a", new Vector3(2f, 0f, 0f), Vector3.UnitZ, f); s.Add("+Z b", new Vector3(-2f, 0f, 0f), Vector3.UnitZ, f);
        s.Add("-Z a", new Vector3(2f, 0f, 0f), -Vector3.UnitZ, f); s.Add("-Z b", new Vector3(-2f, 0f, 0f), -Vector3.UnitZ, f);
        s.Gyro = new Vector3(3e5f);
        s.Air = new BoxAir { CdA = new Vector3(30f, 50f, 20f), Damping = new Vector3(1e4f) };
        s.Bottom = 2f; s.Nose = -Vector3.UnitZ; s.GunAxis = -Vector3.UnitZ; s.Muzzle = 800f; s.GunRange = 1500f;
        return s;
    }

    /// <summary>The H25 helicopter with a nose gun (-Z, 700 m/s, 1.2 km).</summary>
    public static SimShip GunHeli(float ratio = 3f, bool twin = true)
    {
        var s = Ships.Heli(ratio, twin); s.GunAxis = -Vector3.UnitZ; s.Muzzle = 700f; s.GunRange = 1200f; return s;
    }
}

/// <summary>A SimTarget as the pilot's target source (its radius: a ship 10 m across).</summary>
sealed class SimTargetSource : IApTargetSource
{
    public SimTarget T; public float Radius = 6f; public bool On = true;
    public bool Get(in ApSense s, out ApTargetFix f)
    {
        f = default;
        if (!On || T == null) return false;
        f.Valid = true; f.Pos = T.P; f.Vel = T.V; f.Name = T.Name; f.Id = 1; f.Radius = Radius;
        return true;
    }
}
