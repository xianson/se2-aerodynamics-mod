#pragma warning disable
namespace AeroMod;

/// <summary>
/// Atmospheric properties at a specific altitude.
/// All SI units: Kelvin, Pascals, kg/m³, m/s, Pa·s.
/// </summary>
public readonly struct AtmosphereState
{
    public double Temperature { get; init; }
    public double Pressure { get; init; }
    public double Density { get; init; }
    public double SpeedOfSound { get; init; }
    public double DynamicViscosity { get; init; }

    /// <summary>Mach number for a given speed (m/s).</summary>
    public double GetMachNumber(double speed) => speed / SpeedOfSound;

    /// <summary>Dynamic pressure q = ½ρv² (Pa).</summary>
    public double GetDynamicPressure(double speed) => 0.5 * Density * speed * speed;

    /// <summary>Reynolds number per unit length: Re/L = ρv/μ (1/m).</summary>
    public double GetReynoldsPerMeter(double speed) => Density * speed / DynamicViscosity;

    public static readonly AtmosphereState Vacuum = new()
    {
        Temperature = 2.7, // CMB
        Pressure = 0,
        Density = 0,
        SpeedOfSound = 0,
        DynamicViscosity = 0,
    };

    public override string ToString() =>
        $"T={Temperature:F1}K, P={Pressure:F0}Pa, ρ={Density:F4}kg/m³, a={SpeedOfSound:F1}m/s";
}
