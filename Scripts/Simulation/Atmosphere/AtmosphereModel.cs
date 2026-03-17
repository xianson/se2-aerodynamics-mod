#pragma warning disable
using System;
namespace AeroMod;

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

    // Sutherland's law for dynamic viscosity
    private readonly double _viscosityRef;
    private readonly double _viscosityTRef;
    private readonly double _sutherlandConst;

    private readonly List<Layer> _layers;

    private AtmosphereModel(
        double surfaceGravity,
        double specificGasConstant,
        double gamma,
        double atmosphereHeight,
        double viscosityRef,
        double viscosityTRef,
        double sutherlandConst,
        List<Layer> layers)
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

        var layer = _layers[0];
        for (int i = _layers.Count - 1; i >= 0; i--)
        {
            if (altitude >= _layers[i].BaseAltitude)
            {
                layer = _layers[i];
                break;
            }
        }

        double dh = altitude - layer.BaseAltitude;
        double T, P, rho;

        if (Math.Abs(layer.LapseRate) < 1e-10)
        {
            T = layer.BaseTemperature;
            double exponent = -SurfaceGravity * dh / (SpecificGasConstant * T);
            P = layer.BasePressure * Math.Exp(exponent);
        }
        else
        {
            T = layer.BaseTemperature + layer.LapseRate * dh;
            if (T < 1.0) T = 1.0;

            double exp = -SurfaceGravity / (layer.LapseRate * SpecificGasConstant);
            P = layer.BasePressure * Math.Pow(T / layer.BaseTemperature, exp);
        }

        rho = P / (SpecificGasConstant * T);
        double a = Math.Sqrt(Gamma * SpecificGasConstant * T);
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

        double ratio = T / _viscosityTRef;
        return _viscosityRef
            * ratio * Math.Sqrt(ratio)
            * (_viscosityTRef + _sutherlandConst) / (T + _sutherlandConst);
    }

    // ─── Presets ──────────────────────────────────────────────────────

    /// <summary>International Standard Atmosphere (ISA) — Earth sea level to 84.8 km.</summary>
    public static AtmosphereModel Earth()
    {
        const double g = 9.80665;
        const double R = 287.0528;
        const double gamma = 1.4;

        var layers = new List<Layer>(7);
        layers.Add(new Layer(0,     288.15, 101325.0, -0.0065));
        layers.Add(ComputeNextLayer(layers[0], 11000, 0,       g, R));
        layers.Add(ComputeNextLayer(layers[1], 20000, 0.001,   g, R));
        layers.Add(ComputeNextLayer(layers[2], 32000, 0.0028,  g, R));
        layers.Add(ComputeNextLayer(layers[3], 47000, 0,       g, R));
        layers.Add(ComputeNextLayer(layers[4], 51000, -0.0028, g, R));
        layers.Add(ComputeNextLayer(layers[5], 71000, -0.002,  g, R));

        return new AtmosphereModel(
            surfaceGravity: g,
            specificGasConstant: R,
            gamma: gamma,
            atmosphereHeight: 84852,
            viscosityRef: 1.716e-5,
            viscosityTRef: 273.15,
            sutherlandConst: 110.4,
            layers: layers);
    }

    /// <summary>Simple single-layer atmosphere — constant lapse rate from surface to vacuum.</summary>
    public static AtmosphereModel CreateSimple(
        double surfaceTemperature,
        double surfacePressure,
        double lapseRate,
        double surfaceGravity,
        double gamma = 1.4,
        double molarMass = 0.02896)
    {
        const double R_universal = 8.31446;
        double R = R_universal / molarMass;

        double atmosphereHeight;
        if (Math.Abs(lapseRate) > 1e-10)
        {
            atmosphereHeight = -surfaceTemperature / lapseRate;
            if (atmosphereHeight < 0)
                atmosphereHeight = 200000;
        }
        else
        {
            double scaleHeight = R * surfaceTemperature / surfaceGravity;
            atmosphereHeight = scaleHeight * 7;
        }

        var layers = new List<Layer> { new Layer(0, surfaceTemperature, surfacePressure, lapseRate) };
        double viscRef = 1.716e-5 * Math.Sqrt(molarMass / 0.02896);

        return new AtmosphereModel(
            surfaceGravity: surfaceGravity,
            specificGasConstant: R,
            gamma: gamma,
            atmosphereHeight: atmosphereHeight,
            viscosityRef: viscRef,
            viscosityTRef: 273.15,
            sutherlandConst: 110.4 * (molarMass / 0.02896),
            layers: layers);
    }

    /// <summary>Mars-like: thin CO₂ atmosphere.</summary>
    public static AtmosphereModel Mars()
    {
        return CreateSimple(
            surfaceTemperature: 210,
            surfacePressure: 636,
            lapseRate: -0.0045,
            surfaceGravity: 3.72,
            gamma: 1.3,
            molarMass: 0.04401);
    }

    /// <summary>Titan-like: thick cold N₂ atmosphere.</summary>
    public static AtmosphereModel Titan()
    {
        return CreateSimple(
            surfaceTemperature: 94,
            surfacePressure: 146700,
            lapseRate: -0.0012,
            surfaceGravity: 1.352,
            gamma: 1.4,
            molarMass: 0.02802);
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

    private static Layer ComputeNextLayer(in Layer prev, double nextAltitude, double nextLapseRate, double g, double R)
    {
        double dh = nextAltitude - prev.BaseAltitude;
        double T, P;

        if (Math.Abs(prev.LapseRate) < 1e-10)
        {
            T = prev.BaseTemperature;
            P = prev.BasePressure * Math.Exp(-g * dh / (R * T));
        }
        else
        {
            T = prev.BaseTemperature + prev.LapseRate * dh;
            double exp = -g / (prev.LapseRate * R);
            P = prev.BasePressure * Math.Pow(T / prev.BaseTemperature, exp);
        }

        return new Layer(nextAltitude, T, P, nextLapseRate);
    }
}
