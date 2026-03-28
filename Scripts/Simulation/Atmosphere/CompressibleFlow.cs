#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// Compressible flow relations: isentropic, normal shocks, oblique shocks,
/// Prandtl-Meyer expansion, and subsonic compressibility corrections.
///
/// All methods take γ (heat capacity ratio) as a parameter so they work
/// with any atmosphere (Earth γ=1.4, CO₂ γ=1.3, etc.).
/// </summary>
public static class CompressibleFlow
{
    // ═══════════════════════════════════════════════════════════════════
    //  1. ISENTROPIC FLOW RELATIONS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>T0/T = 1 + (γ-1)/2 · M²</summary>
    public static double IsentropicTemperatureRatio(double mach, double gamma)
    {
        return 1.0 + 0.5 * (gamma - 1.0) * mach * mach;
    }

    /// <summary>P0/P = (T0/T)^(γ/(γ-1))</summary>
    public static double IsentropicPressureRatio(double mach, double gamma)
    {
        double tRatio = IsentropicTemperatureRatio(mach, gamma);
        return Math.Pow(tRatio, gamma / (gamma - 1.0));
    }

    /// <summary>ρ0/ρ = (T0/T)^(1/(γ-1))</summary>
    public static double IsentropicDensityRatio(double mach, double gamma)
    {
        double tRatio = IsentropicTemperatureRatio(mach, gamma);
        return Math.Pow(tRatio, 1.0 / (gamma - 1.0));
    }

    /// <summary>Area ratio A/A* for isentropic flow through a nozzle.</summary>
    public static double IsentropicAreaRatio(double mach, double gamma)
    {
        if (mach < 1e-10) return double.PositiveInfinity;

        double gp1 = gamma + 1.0;
        double gm1 = gamma - 1.0;
        double term = (2.0 / gp1) * (1.0 + 0.5 * gm1 * mach * mach);
        return (1.0 / mach) * Math.Pow(term, 0.5 * gp1 / gm1);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  2. NORMAL SHOCK RELATIONS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Downstream Mach number after a normal shock.</summary>
    public static double NormalShockMach(double m1, double gamma)
    {
        if (m1 <= 1.0) return m1;

        double gm1 = gamma - 1.0;
        double num = 1.0 + 0.5 * gm1 * m1 * m1;
        double den = gamma * m1 * m1 - 0.5 * gm1;
        return Math.Sqrt(num / den);
    }

    /// <summary>P2/P1 across a normal shock.</summary>
    public static double NormalShockPressureRatio(double m1, double gamma)
    {
        if (m1 <= 1.0) return 1.0;
        return 1.0 + 2.0 * gamma / (gamma + 1.0) * (m1 * m1 - 1.0);
    }

    /// <summary>T2/T1 across a normal shock.</summary>
    public static double NormalShockTemperatureRatio(double m1, double gamma)
    {
        if (m1 <= 1.0) return 1.0;

        double gm1 = gamma - 1.0;
        double gp1 = gamma + 1.0;
        double m1sq = m1 * m1;

        double num = (1.0 + 2.0 * gamma / gp1 * (m1sq - 1.0)) * (2.0 + gm1 * m1sq);
        double den = gp1 * m1sq;
        return num / den;
    }

    /// <summary>ρ2/ρ1 across a normal shock.</summary>
    public static double NormalShockDensityRatio(double m1, double gamma)
    {
        if (m1 <= 1.0) return 1.0;

        double gp1 = gamma + 1.0;
        double gm1 = gamma - 1.0;
        double m1sq = m1 * m1;
        return gp1 * m1sq / (2.0 + gm1 * m1sq);
    }

    /// <summary>Stagnation pressure ratio P02/P01 across a normal shock.</summary>
    public static double NormalShockStagnationPressureRatio(double m1, double gamma)
    {
        if (m1 <= 1.0) return 1.0;

        double m2 = NormalShockMach(m1, gamma);
        double p2p1 = NormalShockPressureRatio(m1, gamma);
        double p01p1 = IsentropicPressureRatio(m1, gamma);
        double p02p2 = IsentropicPressureRatio(m2, gamma);

        return p02p2 * p2p1 / p01p1;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  3. OBLIQUE SHOCK RELATIONS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>θ-β-M relation: deflection angle θ from shock angle β and Mach M.</summary>
    public static double ObliqueShockDeflection(double mach, double betaRad, double gamma)
    {
        double sinB = Math.Sin(betaRad);
        double cosB = Math.Cos(betaRad);
        double m2sin2 = mach * mach * sinB * sinB;

        if (m2sin2 <= 1.0) return 0;

        double num = 2.0 * cosB / sinB * (m2sin2 - 1.0);
        double den = mach * mach * (gamma + Math.Cos(2.0 * betaRad)) + 2.0;

        return Math.Atan(num / den);
    }

    /// <summary>Find shock angle β for given M and deflection angle θ (radians). Weak shock solution.</summary>
    public static double ObliqueShockBeta(double mach, double thetaRad, double gamma)
    {
        if (mach <= 1.0 || thetaRad <= 0)
            return Math.Asin(1.0 / mach);

        double machAngle = Math.Asin(1.0 / mach);

        const double phi = 0.6180339887498949;
        double a = machAngle + 1e-10;
        double b = Math.PI / 2.0 - 1e-10;
        double x1 = b - phi * (b - a);
        double x2 = a + phi * (b - a);
        double f1 = ObliqueShockDeflection(mach, x1, gamma);
        double f2 = ObliqueShockDeflection(mach, x2, gamma);

        for (int i = 0; i < 25; i++)
        {
            if (f1 < f2)
            {
                a = x1; x1 = x2; f1 = f2;
                x2 = a + phi * (b - a);
                f2 = ObliqueShockDeflection(mach, x2, gamma);
            }
            else
            {
                b = x2; x2 = x1; f2 = f1;
                x1 = b - phi * (b - a);
                f1 = ObliqueShockDeflection(mach, x1, gamma);
            }
        }

        double betaMaxDeflection = 0.5 * (x1 + x2);
        double maxTheta = Math.Max(f1, f2);

        if (thetaRad > maxTheta)
            return double.NaN; // detached shock

        double lo = machAngle;
        double hi = betaMaxDeflection;

        for (int iter = 0; iter < 35; iter++)
        {
            double mid = 0.5 * (lo + hi);
            double theta = ObliqueShockDeflection(mach, mid, gamma);
            if (theta < thetaRad)
                lo = mid;
            else
                hi = mid;
        }

        return 0.5 * (lo + hi);
    }

    /// <summary>Downstream Mach for an oblique shock.</summary>
    public static double ObliqueShockDownstreamMach(double m1, double betaRad, double thetaRad, double gamma)
    {
        double m1n = m1 * Math.Sin(betaRad);
        double m2n = NormalShockMach(m1n, gamma);
        return m2n / Math.Sin(betaRad - thetaRad);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  4. PRANDTL-MEYER EXPANSION
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Prandtl-Meyer function ν(M) in radians.</summary>
    public static double PrandtlMeyerAngle(double mach, double gamma)
    {
        if (mach <= 1.0) return 0;

        double gp1 = gamma + 1.0;
        double gm1 = gamma - 1.0;
        double m2m1 = mach * mach - 1.0;

        double k = Math.Sqrt(gp1 / gm1);
        return k * Math.Atan(Math.Sqrt(gm1 / gp1 * m2m1))
             - Math.Atan(Math.Sqrt(m2m1));
    }

    /// <summary>Maximum turning angle (M → ∞). In radians.</summary>
    public static double PrandtlMeyerMaxAngle(double gamma)
    {
        return (Math.PI / 2.0) * (Math.Sqrt((gamma + 1.0) / (gamma - 1.0)) - 1.0);
    }

    /// <summary>Inverse Prandtl-Meyer: given ν (radians), find Mach number.</summary>
    public static double PrandtlMeyerInverse(double nuRad, double gamma)
    {
        if (nuRad <= 0) return 1.0;

        double mach = 1.0 + nuRad;

        for (int iter = 0; iter < 50; iter++)
        {
            double nu = PrandtlMeyerAngle(mach, gamma);
            double err = nu - nuRad;
            if (Math.Abs(err) < 1e-12) break;

            double m2m1 = mach * mach - 1.0;
            if (m2m1 < 0) { mach = 1.001; continue; }
            double dnu = Math.Sqrt(m2m1) / (1.0 + 0.5 * (gamma - 1.0) * mach * mach) / mach;

            mach -= err / dnu;
            if (mach < 1.0) mach = 1.001;
        }

        return mach;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  5. COMPRESSIBILITY CORRECTIONS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Prandtl-Glauert correction: Cp = Cp_incomp / √(1 - M²)</summary>
    public static double PrandtlGlauert(double cpIncomp, double mach)
    {
        if (mach >= 1.0) return double.PositiveInfinity;
        return cpIncomp / Math.Sqrt(1.0 - mach * mach);
    }

    /// <summary>Kármán-Tsien correction: better accuracy near M = 1.</summary>
    public static double KarmanTsien(double cpIncomp, double mach)
    {
        if (mach >= 1.0) return double.NaN;

        double beta = Math.Sqrt(1.0 - mach * mach);
        double den = beta + mach * mach * cpIncomp / (2.0 * (1.0 + beta));
        if (Math.Abs(den) < 1e-10) return double.NaN;
        return cpIncomp / den;
    }

    /// <summary>Laitone's correction: further refinement over Kármán-Tsien.</summary>
    public static double Laitone(double cpIncomp, double mach, double gamma)
    {
        if (mach >= 1.0) return double.NaN;

        double m2 = mach * mach;
        double beta = Math.Sqrt(1.0 - m2);
        double correction = m2 * (1.0 + 0.5 * (gamma - 1.0) * m2) / (2.0 * beta);
        double den = beta + correction * cpIncomp;
        if (Math.Abs(den) < 1e-10) return double.NaN;

        double result = cpIncomp / den;
        if (cpIncomp < 0 && result > 0) return double.NaN;
        if (cpIncomp > 0 && result < 0) return double.NaN;
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  6. WAVE DRAG APPROXIMATION
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Approximate Cd multiplier vs Mach for a blunt body.</summary>
    public static double WaveDragMultiplier(double mach, double criticalMach = 0.7, double peakMultiplier = 2.5)
    {
        if (mach <= criticalMach) return 1.0;

        const double peakMach = 1.05;

        if (mach <= peakMach)
        {
            double t = (mach - criticalMach) / (peakMach - criticalMach);
            double smooth = 0.5 * (1.0 - Math.Cos(Math.PI * t));
            return 1.0 + (peakMultiplier - 1.0) * smooth;
        }

        double excess = peakMultiplier - 1.0;
        double ratio = peakMach / mach;
        return 1.0 + excess * Math.Pow(ratio, 1.3);
    }

    /// <summary>Supersonic pressure drag coefficient for a flat plate normal to flow.</summary>
    public static double NewtonianPressureCoefficient(double angleRad, double mach, double gamma)
    {
        double sin2 = Math.Sin(angleRad);
        sin2 *= sin2;

        if (mach <= 1.0) return 0;

        double cpMax = ModifiedNewtonianCpMax(mach, gamma);
        return cpMax * sin2;
    }

    /// <summary>Maximum pressure coefficient (stagnation point) for supersonic flow.</summary>
    public static double ModifiedNewtonianCpMax(double mach, double gamma)
    {
        if (mach <= 1.0) return 1.0;

        double m2 = mach * mach;
        double gm1 = gamma - 1.0;
        double gp1 = gamma + 1.0;

        double term1 = gp1 * gp1 * m2 / (4.0 * gamma * m2 - 2.0 * gm1);
        double term2 = Math.Pow(term1, gamma / gm1);
        double term3 = (1.0 - gamma + 2.0 * gamma * m2) / gp1;
        double p02_pinf = term2 * term3;

        return (p02_pinf - 1.0) / (0.5 * gamma * m2);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  7. SOFT Cd CAP
    // ═══════════════════════════════════════════════════════════════════

    public const double DefaultCdMax = 2.0;

    /// <summary>Smooth drag coefficient cap using tanh saturation.</summary>
    public static double SoftCapCd(double cdRaw, double cdMax = DefaultCdMax)
    {
        if (cdRaw <= 0) return 0;
        return cdMax * Math.Tanh(cdRaw / cdMax);
    }

    /// <summary>Combined: apply wave drag multiplier then soft-cap the result.</summary>
    public static double EffectiveCd(double baseCd, double mach, double cdMax = DefaultCdMax,
                                      double criticalMach = 0.7, double peakMultiplier = 2.5)
    {
        double raw = baseCd * WaveDragMultiplier(mach, criticalMach, peakMultiplier);
        return SoftCapCd(raw, cdMax);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  8. FULL STATE BEHIND NORMAL SHOCK
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Compute full atmospheric state downstream of a normal shock.</summary>
    public static AtmosphereState StateAfterNormalShock(AtmosphereState upstream, double mach, double gamma)
    {
        if (mach <= 1.0) return upstream;

        double tRatio = NormalShockTemperatureRatio(mach, gamma);
        double pRatio = NormalShockPressureRatio(mach, gamma);
        double rhoRatio = NormalShockDensityRatio(mach, gamma);

        double T2 = upstream.Temperature * tRatio;
        double P2 = upstream.Pressure * pRatio;
        double rho2 = upstream.Density * rhoRatio;

        double R = upstream.Pressure / (upstream.Density * upstream.Temperature);
        double a2 = Math.Sqrt(gamma * R * T2);

        return new AtmosphereState
        {
            Temperature = T2,
            Pressure = P2,
            Density = rho2,
            SpeedOfSound = a2,
            DynamicViscosity = upstream.DynamicViscosity * Math.Pow(T2 / upstream.Temperature, 0.7),
        };
    }
}
