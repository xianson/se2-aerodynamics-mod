namespace Aero.Core.Atmosphere;

/// <summary>
/// Layered atmosphere model using the barometric formula.
/// Each layer has a linear temperature lapse rate; pressure and density
/// follow from hydrostatic equilibrium and the ideal gas law.
///
/// Supports Earth ISA and configurable alien atmospheres.
/// </summary>
public class AtmosphereModel
{
    /// <summary>Surface gravity in m/s².</summary>
    public double SurfaceGravity { get; }

    /// <summary>Specific gas constant R_specific = R_universal / MolarMass, in J/(kg·K).</summary>
    public double SpecificGasConstant { get; }

    /// <summary>Heat capacity ratio γ = Cp/Cv (1.4 for diatomic, 1.67 for monatomic).</summary>
    public double Gamma { get; }

    /// <summary>Maximum altitude where atmosphere exists (m). Above this → vacuum.</summary>
    public double AtmosphereHeight { get; }

    // Sutherland's law for dynamic viscosity: μ = μ_ref * (T/T_ref)^1.5 * (T_ref + S) / (T + S)
    private readonly double _viscosityRef;     // Pa·s at reference temperature
    private readonly double _viscosityTRef;    // reference temperature (K)
    private readonly double _sutherlandConst;  // Sutherland constant (K)

    private readonly Layer[] _layers;

    private AtmosphereModel(
        double surfaceGravity,
        double specificGasConstant,
        double gamma,
        double atmosphereHeight,
        double viscosityRef,
        double viscosityTRef,
        double sutherlandConst,
        Layer[] layers)
    {
        SurfaceGravity = surfaceGravity;
        SpecificGasConstant = specificGasConstant;
        Gamma = gamma;
        AtmosphereHeight = atmosphereHeight;
        _viscosityRef = viscosityRef;
        _viscosityTRef = viscosityTRef;
        _sutherlandConst = sutherlandConst;
        _layers = layers;
    }

    /// <summary>
    /// Compute atmospheric state at the given altitude (meters above surface).
    /// </summary>
    public AtmosphereState GetState(double altitude)
    {
        if (altitude < 0)
            altitude = 0;

        if (altitude >= AtmosphereHeight)
            return AtmosphereState.Vacuum;

        // Find the layer containing this altitude
        var layer = _layers[0];
        for (int i = _layers.Length - 1; i >= 0; i--)
        {
            if (altitude >= _layers[i].BaseAltitude)
            {
                layer = _layers[i];
                break;
            }
        }

        double dh = altitude - layer.BaseAltitude;
        double T, P, rho;

        if (System.Math.Abs(layer.LapseRate) < 1e-10)
        {
            // Isothermal layer: P = P0 * exp(-g*dh / (R*T))
            T = layer.BaseTemperature;
            double exponent = -SurfaceGravity * dh / (SpecificGasConstant * T);
            P = layer.BasePressure * System.Math.Exp(exponent);
        }
        else
        {
            // Linear lapse: T = T0 + L*dh
            T = layer.BaseTemperature + layer.LapseRate * dh;
            if (T < 1.0) T = 1.0; // clamp to avoid division by zero

            // P = P0 * (T/T0) ^ (-g / (L*R))
            double exp = -SurfaceGravity / (layer.LapseRate * SpecificGasConstant);
            P = layer.BasePressure * System.Math.Pow(T / layer.BaseTemperature, exp);
        }

        // Ideal gas law: ρ = P / (R * T)
        rho = P / (SpecificGasConstant * T);

        // Speed of sound: a = sqrt(γ * R * T)
        double a = System.Math.Sqrt(Gamma * SpecificGasConstant * T);

        // Dynamic viscosity via Sutherland's law
        double mu = ComputeViscosity(T);

        return new AtmosphereState
        {
            Temperature = T,
            Pressure = P,
            Density = rho,
            SpeedOfSound = a,
            DynamicViscosity = mu,
        };
    }

    private double ComputeViscosity(double T)
    {
        if (_viscosityRef <= 0) return 0;

        // Sutherland's law: μ = μ_ref * (T/T_ref)^1.5 * (T_ref + S) / (T + S)
        double ratio = T / _viscosityTRef;
        return _viscosityRef
            * ratio * System.Math.Sqrt(ratio)
            * (_viscosityTRef + _sutherlandConst) / (T + _sutherlandConst);
    }

    // ─── Presets ──────────────────────────────────────────────────────

    /// <summary>
    /// International Standard Atmosphere (ISA) — Earth sea level to 84.8 km.
    /// </summary>
    public static AtmosphereModel Earth()
    {
        const double g = 9.80665;
        const double R = 287.0528;  // J/(kg·K) for dry air
        const double gamma = 1.4;

        // ISA layers (altitude in m, lapse rate in K/m)
        //   Troposphere:    0 - 11,000 m    L = -0.0065 K/m
        //   Tropopause: 11,000 - 20,000 m   L =  0       (isothermal)
        //   Stratosphere1: 20,000 - 32,000 m L = +0.001  K/m
        //   Stratosphere2: 32,000 - 47,000 m L = +0.0028 K/m
        //   Stratopause: 47,000 - 51,000 m  L =  0       (isothermal)
        //   Mesosphere1: 51,000 - 71,000 m  L = -0.0028 K/m
        //   Mesosphere2: 71,000 - 84,852 m  L = -0.002  K/m

        var layers = new Layer[7];
        layers[0] = new Layer(0,     288.15, 101325.0, -0.0065);
        layers[1] = ComputeNextLayer(layers[0], 11000, 0,       g, R);
        layers[2] = ComputeNextLayer(layers[1], 20000, 0.001,   g, R);
        layers[3] = ComputeNextLayer(layers[2], 32000, 0.0028,  g, R);
        layers[4] = ComputeNextLayer(layers[3], 47000, 0,       g, R);
        layers[5] = ComputeNextLayer(layers[4], 51000, -0.0028, g, R);
        layers[6] = ComputeNextLayer(layers[5], 71000, -0.002,  g, R);

        return new AtmosphereModel(
            surfaceGravity: g,
            specificGasConstant: R,
            gamma: gamma,
            atmosphereHeight: 84852,
            viscosityRef: 1.716e-5,     // Pa·s at 273.15 K
            viscosityTRef: 273.15,
            sutherlandConst: 110.4,     // K
            layers: layers);
    }

    /// <summary>
    /// Simple single-layer atmosphere — constant lapse rate from surface to vacuum.
    /// Good for alien planets or quick prototyping.
    /// </summary>
    public static AtmosphereModel CreateSimple(
        double surfaceTemperature,
        double surfacePressure,
        double lapseRate,
        double surfaceGravity,
        double gamma = 1.4,
        double molarMass = 0.02896)  // kg/mol, air = 28.96 g/mol
    {
        const double R_universal = 8.31446;
        double R = R_universal / molarMass;

        double atmosphereHeight;
        if (System.Math.Abs(lapseRate) > 1e-10)
        {
            // Height where T would reach ~0 K (clamp to where density is negligible)
            atmosphereHeight = -surfaceTemperature / lapseRate;
            if (atmosphereHeight < 0)
                atmosphereHeight = 200000; // lapse rate heats with altitude — use large ceiling
        }
        else
        {
            // Isothermal — atmosphere extends ~7 scale heights (density < 0.1%)
            double scaleHeight = R * surfaceTemperature / surfaceGravity;
            atmosphereHeight = scaleHeight * 7;
        }

        var layers = new[] { new Layer(0, surfaceTemperature, surfacePressure, lapseRate) };

        // Scale viscosity reference from Earth proportionally
        double viscRef = 1.716e-5 * System.Math.Sqrt(molarMass / 0.02896);

        return new AtmosphereModel(
            surfaceGravity: surfaceGravity,
            specificGasConstant: R,
            gamma: gamma,
            atmosphereHeight: atmosphereHeight,
            viscosityRef: viscRef,
            viscosityTRef: 273.15,
            sutherlandConst: 110.4 * (molarMass / 0.02896), // rough scaling
            layers: layers);
    }

    /// <summary>
    /// Mars-like: thin CO₂ atmosphere.
    /// </summary>
    public static AtmosphereModel Mars()
    {
        return CreateSimple(
            surfaceTemperature: 210,
            surfacePressure: 636,         // ~0.6% of Earth
            lapseRate: -0.0045,
            surfaceGravity: 3.72,
            gamma: 1.3,                   // CO₂
            molarMass: 0.04401);          // CO₂ = 44.01 g/mol
    }

    /// <summary>
    /// Titan-like: thick cold N₂ atmosphere.
    /// </summary>
    public static AtmosphereModel Titan()
    {
        return CreateSimple(
            surfaceTemperature: 94,
            surfacePressure: 146700,      // 1.45 atm
            lapseRate: -0.0012,
            surfaceGravity: 1.352,
            gamma: 1.4,                   // N₂
            molarMass: 0.02802);          // N₂ = 28.02 g/mol
    }

    // ─── Layer computation ────────────────────────────────────────────

    private readonly struct Layer
    {
        public readonly double BaseAltitude;
        public readonly double BaseTemperature;
        public readonly double BasePressure;
        public readonly double LapseRate;

        public Layer(double baseAltitude, double baseTemperature, double basePressure, double lapseRate)
        {
            BaseAltitude = baseAltitude;
            BaseTemperature = baseTemperature;
            BasePressure = basePressure;
            LapseRate = lapseRate;
        }
    }

    /// <summary>
    /// Compute the base conditions for the next layer given the previous layer's
    /// properties and the altitude where the new layer begins.
    /// </summary>
    private static Layer ComputeNextLayer(in Layer prev, double nextAltitude, double nextLapseRate, double g, double R)
    {
        double dh = nextAltitude - prev.BaseAltitude;
        double T, P;

        if (System.Math.Abs(prev.LapseRate) < 1e-10)
        {
            T = prev.BaseTemperature;
            P = prev.BasePressure * System.Math.Exp(-g * dh / (R * T));
        }
        else
        {
            T = prev.BaseTemperature + prev.LapseRate * dh;
            double exp = -g / (prev.LapseRate * R);
            P = prev.BasePressure * System.Math.Pow(T / prev.BaseTemperature, exp);
        }

        return new Layer(nextAltitude, T, P, nextLapseRate);
    }
}
