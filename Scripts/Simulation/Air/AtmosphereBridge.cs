#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// Converts SE2's AirData.Density (the only value SE2 provides) into a full
/// AtmosphereState with speed of sound, viscosity, etc.
///
/// Purely density-driven — no altitude dependency. Uses Earth ISA as a reference
/// to build a density→temperature lookup table, then derives all other properties
/// from density + temperature via ideal gas law and Sutherland's law.
///
/// Singleton — one instance shared across all grids.
/// </summary>
public class AtmosphereBridge
{
    private readonly AtmosphereModel _model;

    // LUT sorted by descending density (sea level first, high altitude last)
    private readonly List<(double density, double temperature)> _lut;

    // Pre-computed log(density) for each LUT entry (avoids per-frame Math.Log calls)
    private readonly double[] _logDensities;

    // Gas constants from the reference model
    private readonly double _R;     // specific gas constant J/(kg·K)
    private readonly double _gamma; // heat capacity ratio

    // Sutherland's law constants (cached from model via Earth reference)
    private readonly double _viscRef;
    private readonly double _viscTRef;
    private readonly double _suthC;

    /// <summary>
    /// Build a density→temperature LUT from the given atmosphere model.
    /// Samples every 100m from 0 to atmosphere height.
    /// </summary>
    public AtmosphereBridge(AtmosphereModel? model = null)
    {
        _model = model ?? AtmosphereModel.Earth();
        _R = _model.SpecificGasConstant;
        _gamma = _model.Gamma;

        // Earth reference values for Sutherland's law
        _viscRef = 1.716e-5;
        _viscTRef = 273.15;
        _suthC = 110.4;

        // Sample the atmosphere at 100m intervals
        int steps = (int)(_model.AtmosphereHeight / 100.0) + 1;
        var samples = new List<(double density, double temperature)>(steps);

        double prevDensity = -1;
        for (int i = 0; i < steps; i++)
        {
            double altitude = i * 100.0;
            var state = _model.GetState(altitude);
            if (state.Density <= 0) break;

            // Skip duplicate densities (shouldn't happen, but be safe)
            if (Math.Abs(state.Density - prevDensity) < 1e-12) continue;
            prevDensity = state.Density;

            samples.Add((state.Density, state.Temperature));
        }

        // Sort by descending density (sea level = highest density first)
        samples.Sort((a, b) => b.density.CompareTo(a.density));
        _lut = samples;

        // Pre-compute log(density) for each LUT entry
        _logDensities = new double[samples.Count];
        for (int i = 0; i < samples.Count; i++)
            _logDensities[i] = Math.Log(samples[i].density);
    }

    /// <summary>
    /// Convert a density value to a full AtmosphereState.
    /// O(log N) binary search on the density LUT.
    /// </summary>
    public AtmosphereState GetState(float density)
    {
        if (density < AeroConfig.MinDensity || _lut.Count == 0)
            return AtmosphereState.Vacuum;

        // Binary search for the two LUT entries bracketing this density.
        // LUT is sorted descending by density.
        double T = InterpolateTemperature(density);

        // Derive everything from density + temperature
        double P = density * _R * T;                        // ideal gas law: P = ρRT
        double a = Math.Sqrt(_gamma * _R * T);              // speed of sound
        double mu = ComputeViscosity(T);                     // Sutherland's law

        return new AtmosphereState
        {
            Temperature = T,
            Pressure = P,
            Density = density,
            SpeedOfSound = a,
            DynamicViscosity = mu,
        };
    }

    private double InterpolateTemperature(double density)
    {
        // LUT is sorted descending by density: lut[0] has highest density (sea level)
        // If density is above sea level, clamp to sea level temperature
        if (density >= _lut[0].density)
            return _lut[0].temperature;

        // If density is below the lowest entry, clamp
        if (density <= _lut[_lut.Count - 1].density)
            return _lut[_lut.Count - 1].temperature;

        // Binary search: find the first entry where density < lut[i].density
        // Since sorted descending, we want the interval [i, i+1] where
        // lut[i].density >= density >= lut[i+1].density
        int lo = 0, hi = _lut.Count - 2;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_lut[mid + 1].density >= density)
                lo = mid + 1;
            else
                hi = mid;
        }

        // Interpolate linearly in log-density space (density varies exponentially)
        double logD = Math.Log(density);
        double t = (logD - _logDensities[lo]) / (_logDensities[lo + 1] - _logDensities[lo]);

        return _lut[lo].temperature + t * (_lut[lo + 1].temperature - _lut[lo].temperature);
    }

    private double ComputeViscosity(double T)
    {
        if (T <= 0) return 0;
        double ratio = T / _viscTRef;
        return _viscRef * ratio * Math.Sqrt(ratio)
            * (_viscTRef + _suthC) / (T + _suthC);
    }
}
