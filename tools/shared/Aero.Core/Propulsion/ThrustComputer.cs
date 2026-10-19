using Aero.Core.Atmosphere;

namespace Aero.Core.Propulsion;

/// <summary>
/// Computes effective thrust for an engine given flight conditions.
/// Combines the Mach-dependent thrust profile with altitude-dependent
/// density scaling (for air-breathers) and backpressure effects (for rockets).
/// </summary>
public static class ThrustComputer
{
    /// <summary>
    /// Compute thrust scale factor for air-breathing engines.
    /// Combines Mach profile with density ratio scaling.
    ///
    /// T_eff = T_rated × MachScale(M) × (ρ/ρ_ref)^densityExponent
    ///
    /// densityExponent is typically ~1.0 for jets (mass flow proportional to density)
    /// and ~0.7 for propellers (partial density dependence via advance ratio).
    /// </summary>
    public static double AirBreathingScale(ThrustProfile profile, double mach,
                                            double density, double seaLevelDensity,
                                            double densityExponent = 1.0)
    {
        double machScale = profile.Evaluate(mach);

        if (seaLevelDensity <= 0 || density <= 0)
            return 0.0;

        double densityRatio = density / seaLevelDensity;
        double densityScale = System.Math.Pow(densityRatio, densityExponent);

        return machScale * densityScale;
    }

    /// <summary>
    /// Compute thrust scale factor for rocket engines.
    /// Only backpressure matters — no Mach dependence.
    ///
    /// T_eff = T_vac - A_e × P_ambient
    /// Scale = T_eff / T_vac = 1 - (A_e × P_ambient) / T_vac
    ///
    /// At sea level this typically reduces thrust by 10-15%.
    /// In vacuum it's exactly 1.0.
    /// </summary>
    public static double RocketScale(double ambientPressure, double exitArea, double vacuumThrust)
    {
        if (vacuumThrust <= 0) return 0.0;

        double pressureLoss = exitArea * ambientPressure;
        double scale = 1.0 - pressureLoss / vacuumThrust;

        return System.Math.Max(scale, 0.0);
    }

    /// <summary>
    /// Full effective thrust computation for an air-breathing engine.
    /// Returns the actual thrust force in Newtons.
    /// </summary>
    public static double ComputeThrust(ThrustProfile profile, double ratedThrust,
                                        double mach, double density, double seaLevelDensity,
                                        double densityExponent = 1.0)
    {
        return ratedThrust * AirBreathingScale(profile, mach, density, seaLevelDensity, densityExponent);
    }

    /// <summary>
    /// Full effective thrust for a rocket engine.
    /// </summary>
    public static double ComputeRocketThrust(double vacuumThrust, double ambientPressure,
                                              double exitArea)
    {
        return vacuumThrust * RocketScale(ambientPressure, exitArea, vacuumThrust);
    }
}
