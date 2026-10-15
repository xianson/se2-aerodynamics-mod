#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// BLOCK MATERIALS for the structure and hull-heat models - pure data, no game (Tests/StructureTests).
///
/// A block is a thin steel box: its walls carry the load and hold the heat. Their thickness comes from the block's
/// mass over its box's surface (the game's light armour cube: 1049 kg on 37.5 m2 at 2.5 m - 3.6 mm of steel; 33 kg on
/// 1.5 m2 at 0.5 m - 2.8 mm). Functional blocks (thrusters, batteries, cockpits) carry most of their mass inside: their
/// load-bearing shell is capped at FunctionalWall, their whole mass still holds heat and is still swung by the turns.
///  - Strength: yield stress x StrengthAt(T) - full up to SoftenK, falling linearly to MinStrength at WeakK (steel
///    keeps about half its yield at 800 K and a tenth at 1100 K).
///  - Heat: specific heat, emissivity (re-radiation), and BurnK - past it the block takes heat damage (steel melts
///    about 1700 K; a functional block's insides give up long before its shell).
/// Classes from the block's name: Armor (light armour - SE2 2.4 has no heavy), HeatShield (a mod block named so), Trim
/// (detailing: cosmetic - its mass loads the ship and it heats, but it carries no load and the structure never breaks it;
/// a 0.25 m detailing cube's paper walls in a wing root's section failed first at 0.6 g), the thrusters - structurally
/// functional blocks, but built for heat (2026-10-13, the user: "some thruster blocks should be resistant to heat"):
/// hydrogen nozzles softening from 1000 K, burning past 2000; atmospheric (jet) hot sections 800 / 1500; ion 650 / 1300
/// - Mount: the other blocks that move the ship or carry force (gyros, gear, wheels, hinges, rotors, pistons, control
/// surfaces, airbrakes) - and the rest Functional: FITTINGS (batteries, antennas, lights, cockpits, tanks, cargo, tools),
/// bolted on - 120 MPa against the mounts' and armour's 250: they tear off first (2026-10-13, the user: "functional
/// blocks that aren't movement related or forcing should tear off easier"); their insides give up to heat at 600 / 1050.
/// The thrusters' own exhaust heat is not modelled.
/// </summary>
public static class StructMaterial
{
    public const int Armor = 0, Functional = 1, HeatShield = 2, Trim = 3, HydrogenThruster = 4, AtmosphericThruster = 5, IonThruster = 6, Mount = 7, Count = 8;
    public const float SteelDensity = 7850f;
    /// <summary>Most a functional block's shell counts (m): about a light armour block's wall.</summary>
    public static float FunctionalWall = 0.0035f;

    /// <summary>Yield stress at room temperature (Pa).</summary>
    public static readonly float[] Yield = { 250e6f, 120e6f, 200e6f, 200e6f, 250e6f, 250e6f, 250e6f, 250e6f };
    /// <summary>J/(kg K).</summary>
    public static readonly float[] SpecificHeat = { 500f, 600f, 1100f, 500f, 500f, 550f, 600f, 550f };
    /// <summary>Thermal emissivity of the hull (oxidised steel ~0.8; a shield's char ~0.85).</summary>
    public static readonly float[] Emissivity = { 0.8f, 0.8f, 0.85f, 0.8f, 0.85f, 0.8f, 0.8f, 0.8f };
    /// <summary>Strength starts falling (K).</summary>
    public static readonly float[] SoftenK = { 650f, 600f, 1300f, 650f, 1000f, 800f, 650f, 650f };
    /// <summary>Strength down to MinStrength (K).</summary>
    public static readonly float[] WeakK = { 1250f, 1100f, 2300f, 1250f, 1800f, 1400f, 1200f, 1200f };
    /// <summary>Heat damage starts (K).</summary>
    public static readonly float[] BurnK = { 1450f, 1050f, 2500f, 1450f, 2000f, 1500f, 1300f, 1300f };
    public const float MinStrength = 0.05f;
    /// <summary>Fatigue and wear rate, times this (StructureFatigue.HealthRate; 2026-10-14): fittings (bolted on) work loose
    /// half again as fast; trim carries no load and never tires.</summary>
    public static readonly float[] FatigueFactor = { 1f, 1.5f, 1f, 0f, 1f, 1f, 1f, 1f };
    /// <summary>Health lost per second (fraction of full) per 100 K over BurnK.</summary>
    public static float BurnPerSecondPer100K = 0.08f;

    /// <summary>Blocks that move the ship or carry force - mounted hard, armour's strength: any other thruster, gyros,
    /// gear, wheels, hinges, rotors, pistons, the aero mod's control surfaces and airbrakes.</summary>
    public static readonly string[] MountNames = { "Thruster", "Gyroscope", "Gear", "Suspension", "Wheel", "Hinge", "Rotor", "Piston",
        "ControlSurface", "Control Surface", "Airbrake", "Flap", "Aileron", "Elevator", "Rudder" };

    public static int Classify(string name)
    {
        if (string.IsNullOrEmpty(name)) return Functional;
        if (name.IndexOf("HeatShield", StringComparison.OrdinalIgnoreCase) >= 0) return HeatShield;
        if (name.IndexOf("Detailing", StringComparison.OrdinalIgnoreCase) >= 0) return Trim;
        // thrusters: built for heat, each by its kind (structurally functional blocks: the capped wall, 200 MPa)
        if (name.IndexOf("HydrogenThruster", StringComparison.OrdinalIgnoreCase) >= 0) return HydrogenThruster;
        if (name.IndexOf("AtmosphericThruster", StringComparison.OrdinalIgnoreCase) >= 0) return AtmosphericThruster;
        if (name.IndexOf("IonThruster", StringComparison.OrdinalIgnoreCase) >= 0) return IonThruster;
        foreach (var k in MountNames) if (name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return Mount;
        if (name.IndexOf("Armor", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Armour", StringComparison.OrdinalIgnoreCase) >= 0) return Armor;
        return Functional;
    }

    /// <summary>The load-bearing wall (m) of a block of this mass and box (m).</summary>
    public static float Wall(int mat, float mass, Vector3 size) => WallOver(mat, mass, 2f * (size.X * size.Y + size.Y * size.Z + size.Z * size.X));

    /// <summary>The load-bearing wall (m) of a block of this mass over this surface (m2: its cells' real exposed surface).</summary>
    public static float WallOver(int mat, float mass, float area)
    {
        float t = area > 1e-6f ? mass / (SteelDensity * area) : 0f;
        if (CappedWall(mat)) t = MathF.Min(t, FunctionalWall);
        return MathF.Max(t, 1e-4f);
    }

    /// <summary>The classes whose load-bearing shell is capped at FunctionalWall (fittings, thrusters, mounts).</summary>
    public static bool CappedWall(int mat) => mat == Functional || mat >= HydrogenThruster;

    /// <summary>Strength left at temperature tK (1 cold, MinStrength past WeakK).</summary>
    public static float StrengthAt(int mat, float tK)
    {
        float a = SoftenK[mat], b = WeakK[mat];
        if (tK <= a) return 1f;
        if (tK >= b) return MinStrength;
        return 1f - (1f - MinStrength) * (tK - a) / (b - a);
    }

    /// <summary>The skin temperatures (K) a block of this class scorches light / dark at (HeatScorch, 2026-10-15): armour's
    /// (lightK, darkK) moved with the class's own limits - light in proportion to its SoftenK, dark at the same place between
    /// its SoftenK and WeakK. Armour's own: unchanged; a heat shield (softening at 1300 K) no longer scorches dark at 950.</summary>
    public static void ScorchAt(int mat, float lightK, float darkK, out float light, out float dark)
    {
        if (mat < 0 || mat >= Count) mat = Armor;
        float a0 = SoftenK[Armor], b0 = WeakK[Armor];
        light = lightK * SoftenK[mat] / a0;
        dark = SoftenK[mat] + (darkK - a0) * (WeakK[mat] - SoftenK[mat]) / (b0 - a0);
    }

    /// <summary>Heat damage rate (fraction of full health per second) at tK.</summary>
    public static float BurnRate(int mat, float tK) => tK > BurnK[mat] ? BurnPerSecondPer100K * (tK - BurnK[mat]) / 100f : 0f;
}
