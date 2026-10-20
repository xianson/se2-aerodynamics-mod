using AeroMod;

// THE SHIPS. The helicopters are H25 as dumped in game 2026-10-18 (release-ingame-2: 29.7 t, one 947 kN rotor 7.5 m - 3.0x its
// planet weight at 10.67 m/s2, the anti-torque tail reversible along X 6.7 m aft, a reversible Z tail; positions from its spawn
// file, the centre of mass estimated - Tests/ThrustTests TiltHeliChecks' layout), scaled by mass to 2.5x and 1.5x its weight,
// and with a second tail ahead (a pure yaw couple). The VTOLs: four lift thrusters (one weaker, or the centre of mass off) and
// side thrusters. Planes: the force polars AeroBench exported from the dumped ships (Polar.cs).
static class Ships
{
    public const float H25Mass = 29714f, H25Rotor = 947278f, H25G = 10.67f;

    /// <summary>H25 at thrust / planet weight 'ratio' (the mass scaled; inertia with it), one anti-torque tail.</summary>
    public static SimShip Heli(float ratio = 3f, bool twin = false, float tailCap = 128000f, string name = null)
    {
        float mass = H25Rotor / (ratio * H25G);
        var s = new SimShip { Name = name ?? $"H25 {ratio:0.0}x {(twin ? "two tails" : "one tail")}", Mass = mass, I = new Vector3(3.5e5f, 2.0e5f, 2.2e5f) * (mass / H25Mass), PlanetG = H25G, Gm = 2f };
        s.Rotor = 0; s.RotorR = 7.5f; s.HubH = 3.8f + 0.74f; s.MaxTiltDeg = 10f; s.HubMoment = 0.15f;
        s.Add("rotor", new Vector3(-0.07f, 3.8f, -0.8f), Vector3.UnitY, H25Rotor);
        s.Add("tail X", new Vector3(1.7f, 1.3f, 6.7f), Vector3.UnitX, tailCap, rev: true);
        if (twin) s.Add("nose X", new Vector3(1.7f, 1.3f, -6.7f), Vector3.UnitX, tailCap, rev: true);
        s.Add("tail Z", new Vector3(-0.07f, 1.3f, 8.45f), Vector3.UnitZ, tailCap, rev: true);
        s.Air = new BoxAir { CdA = new Vector3(40f, 90f, 20f), Damping = new Vector3(3e4f, 3e4f, 3e4f) * (mass / H25Mass) };
        s.Bottom = 2.5f;
        return s;
    }

    /// <summary>A dumped plane on its force polar (null: no polar file): one engine along the nose (thrust tw x its real weight),
    /// the gyros' reach (N m per local axis), the control surfaces (N m per Pa per local axis).</summary>
    public static SimShip Plane(string polar, float tw = 0.4f, float gyro = 2e5f, Vector3? surfPerQ = null)
    {
        var air = PolarAir.Load(polar);
        if (air == null) return null;
        if (surfPerQ.HasValue) air.SurfPerQ = surfPerQ.Value;
        var s = new SimShip { Name = $"{polar} (polar)", Mass = air.Mass, I = air.I, PlanetG = 9.81f, Gm = 2f, Air = air, Gyro = new Vector3(gyro), Bottom = 3f };
        s.Add("engine", Vector3.Zero, air.Nose, tw * air.Mass * 9.81f * 2f);
        s.Nose = air.Nose; s.GunAxis = air.Nose; s.Muzzle = 900f; s.GunRange = 1500f;
        return s;
    }

    /// <summary>A quad VTOL (8 t): four lift thrusters at the corners (one at 'weak' of the others), side thrusters both ways on X
    /// and Z (sideShare of the lift's), small gyros; the centre of mass off by comOff (local m).</summary>
    public static SimShip Vtol(float ratio = 2.6f, float weak = 1f, Vector3 comOff = default, float sideShare = 0.25f, float gyro = 2e4f, string name = null)
    {
        var s = new SimShip { Name = name ?? $"VTOL {ratio:0.0}x{(weak < 1f ? $" one lifter at {weak * 100:0}%" : "")}{(comOff != default ? " CoM off" : "")}", Mass = 8000f, I = new Vector3(6e4f, 9e4f, 5e4f), PlanetG = 9.81f, Gm = 2f };
        float lift = ratio * s.Mass * s.PlanetG / (3f + weak);
        Vector3[] corners = { new(3f, 0f, 4f), new(-3f, 0f, 4f), new(3f, 0f, -4f), new(-3f, 0f, -4f) };
        for (int i = 0; i < 4; i++) s.Add($"lift {i}", corners[i] - comOff, Vector3.UnitY, i == 0 ? lift * weak : lift);
        float side = sideShare * ratio * s.Mass * s.PlanetG / 2f;
        s.Add("side +X", new Vector3(-2f, 0f, 0f) - comOff, Vector3.UnitX, side);
        s.Add("side -X", new Vector3(2f, 0f, 0f) - comOff, -Vector3.UnitX, side);
        s.Add("side +Z", new Vector3(0f, 0f, -4f) - comOff, Vector3.UnitZ, side);
        s.Add("side -Z", new Vector3(0f, 0f, 4f) - comOff, -Vector3.UnitZ, side);
        s.Gyro = new Vector3(gyro);
        s.Air = new BoxAir { CdA = new Vector3(20f, 40f, 12f), Damping = new Vector3(5e3f) };
        s.Bottom = 1.5f;
        return s;
    }
}
