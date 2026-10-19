namespace Aero.Core.Atmosphere;

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
    //     Valid for adiabatic, reversible flow (no shocks).
    //     Ratios are stagnation/static (T0/T, P0/P, ρ0/ρ).
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
        return System.Math.Pow(tRatio, gamma / (gamma - 1.0));
    }

    /// <summary>ρ0/ρ = (T0/T)^(1/(γ-1))</summary>
    public static double IsentropicDensityRatio(double mach, double gamma)
    {
        double tRatio = IsentropicTemperatureRatio(mach, gamma);
        return System.Math.Pow(tRatio, 1.0 / (gamma - 1.0));
    }

    /// <summary>
    /// Area ratio A/A* for isentropic flow through a nozzle.
    /// A* is the throat area where M = 1.
    /// </summary>
    public static double IsentropicAreaRatio(double mach, double gamma)
    {
        if (mach < 1e-10) return double.PositiveInfinity;

        double gp1 = gamma + 1.0;
        double gm1 = gamma - 1.0;
        double term = (2.0 / gp1) * (1.0 + 0.5 * gm1 * mach * mach);
        return (1.0 / mach) * System.Math.Pow(term, 0.5 * gp1 / gm1);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  2. NORMAL SHOCK RELATIONS
    //     Jump conditions across a shock perpendicular to the flow.
    //     Input: upstream Mach M1, output: downstream properties.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Downstream Mach number after a normal shock.
    /// M2² = (1 + (γ-1)/2 · M1²) / (γ·M1² - (γ-1)/2)
    /// </summary>
    public static double NormalShockMach(double m1, double gamma)
    {
        if (m1 <= 1.0) return m1; // no shock below Mach 1

        double gm1 = gamma - 1.0;
        double num = 1.0 + 0.5 * gm1 * m1 * m1;
        double den = gamma * m1 * m1 - 0.5 * gm1;
        return System.Math.Sqrt(num / den);
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

    /// <summary>
    /// Stagnation pressure ratio P02/P01 across a normal shock.
    /// Always ≤ 1.0 (entropy increases through shock).
    /// </summary>
    public static double NormalShockStagnationPressureRatio(double m1, double gamma)
    {
        if (m1 <= 1.0) return 1.0;

        double m2 = NormalShockMach(m1, gamma);
        double p2p1 = NormalShockPressureRatio(m1, gamma);
        double p01p1 = IsentropicPressureRatio(m1, gamma);
        double p02p2 = IsentropicPressureRatio(m2, gamma);

        // P02/P01 = (P02/P2) * (P2/P1) * (P1/P01)
        return p02p2 * p2p1 / p01p1;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  3. OBLIQUE SHOCK RELATIONS
    //     Shock at angle β to the flow, caused by a deflection angle θ.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// θ-β-M relation: deflection angle θ from shock angle β and Mach M.
    /// tan(θ) = 2·cot(β) · (M²sin²β - 1) / (M²(γ + cos2β) + 2)
    /// </summary>
    public static double ObliqueShockDeflection(double mach, double betaRad, double gamma)
    {
        double sinB = System.Math.Sin(betaRad);
        double cosB = System.Math.Cos(betaRad);
        double m2sin2 = mach * mach * sinB * sinB;

        if (m2sin2 <= 1.0) return 0; // no shock

        double num = 2.0 * cosB / sinB * (m2sin2 - 1.0);
        double den = mach * mach * (gamma + System.Math.Cos(2.0 * betaRad)) + 2.0;

        return System.Math.Atan(num / den);
    }

    /// <summary>
    /// Find shock angle β for given M and deflection angle θ (radians).
    /// Returns the weak shock solution. Returns NaN if no solution (detached shock).
    /// Uses golden-section search for max deflection + bisection for β.
    /// </summary>
    public static double ObliqueShockBeta(double mach, double thetaRad, double gamma)
    {
        if (mach <= 1.0 || thetaRad <= 0)
            return System.Math.Asin(1.0 / mach); // Mach angle

        double machAngle = System.Math.Asin(1.0 / mach);

        // Golden-section search for β that maximizes deflection θ(β).
        // θ(β) is unimodal between machAngle and π/2.
        const double phi = 0.6180339887498949; // (√5-1)/2
        double a = machAngle + 1e-10;
        double b = System.Math.PI / 2.0 - 1e-10;
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
        double maxTheta = System.Math.Max(f1, f2);

        if (thetaRad > maxTheta)
            return double.NaN; // detached shock

        // Bisection for weak shock: β lies between machAngle and betaMaxDeflection
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

    /// <summary>
    /// Downstream Mach for an oblique shock: use M1n = M1·sin(β),
    /// compute normal shock, then M2 = M2n / sin(β - θ).
    /// </summary>
    public static double ObliqueShockDownstreamMach(double m1, double betaRad, double thetaRad, double gamma)
    {
        double m1n = m1 * System.Math.Sin(betaRad);
        double m2n = NormalShockMach(m1n, gamma);
        return m2n / System.Math.Sin(betaRad - thetaRad);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  4. PRANDTL-MEYER EXPANSION
    //     Isentropic turning of supersonic flow away from itself.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Prandtl-Meyer function ν(M) in radians.
    /// ν = √((γ+1)/(γ-1)) · arctan(√((γ-1)/(γ+1)·(M²-1))) - arctan(√(M²-1))
    /// </summary>
    public static double PrandtlMeyerAngle(double mach, double gamma)
    {
        if (mach <= 1.0) return 0;

        double gp1 = gamma + 1.0;
        double gm1 = gamma - 1.0;
        double m2m1 = mach * mach - 1.0;

        double k = System.Math.Sqrt(gp1 / gm1);
        return k * System.Math.Atan(System.Math.Sqrt(gm1 / gp1 * m2m1))
             - System.Math.Atan(System.Math.Sqrt(m2m1));
    }

    /// <summary>
    /// Maximum turning angle (M → ∞). In radians.
    /// ν_max = (π/2) · (√((γ+1)/(γ-1)) - 1)
    /// </summary>
    public static double PrandtlMeyerMaxAngle(double gamma)
    {
        return (System.Math.PI / 2.0) * (System.Math.Sqrt((gamma + 1.0) / (gamma - 1.0)) - 1.0);
    }

    /// <summary>
    /// Inverse Prandtl-Meyer: given ν (radians), find Mach number.
    /// Uses Newton's method.
    /// </summary>
    public static double PrandtlMeyerInverse(double nuRad, double gamma)
    {
        if (nuRad <= 0) return 1.0;

        // Initial guess from linear approximation
        double mach = 1.0 + nuRad;

        for (int iter = 0; iter < 50; iter++)
        {
            double nu = PrandtlMeyerAngle(mach, gamma);
            double err = nu - nuRad;
            if (System.Math.Abs(err) < 1e-12) break;

            // dν/dM = √(M²-1) / (1 + (γ-1)/2 · M²) / M
            double m2m1 = mach * mach - 1.0;
            if (m2m1 < 0) { mach = 1.001; continue; }
            double dnu = System.Math.Sqrt(m2m1) / (1.0 + 0.5 * (gamma - 1.0) * mach * mach) / mach;

            mach -= err / dnu;
            if (mach < 1.0) mach = 1.001;
        }

        return mach;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  5. COMPRESSIBILITY CORRECTIONS
    //     Correct subsonic Cp for compressibility effects.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Prandtl-Glauert correction: Cp = Cp_incomp / √(1 - M²)
    /// Valid for M &lt; ~0.7. Diverges at M = 1.
    /// </summary>
    public static double PrandtlGlauert(double cpIncomp, double mach)
    {
        if (mach >= 1.0) return double.PositiveInfinity;
        return cpIncomp / System.Math.Sqrt(1.0 - mach * mach);
    }

    /// <summary>
    /// Kármán-Tsien correction: better accuracy near M = 1 than Prandtl-Glauert.
    /// Cp = Cp0 / (√(1-M²) + M²·Cp0 / (2·(1+√(1-M²))))
    /// Returns NaN if the denominator degenerates (large negative Cp near M=1).
    /// </summary>
    public static double KarmanTsien(double cpIncomp, double mach)
    {
        if (mach >= 1.0) return double.NaN;

        double beta = System.Math.Sqrt(1.0 - mach * mach);
        double den = beta + mach * mach * cpIncomp / (2.0 * (1.0 + beta));
        if (System.Math.Abs(den) < 1e-10) return double.NaN;
        return cpIncomp / den;
    }

    /// <summary>
    /// Laitone's correction: further refinement over Kármán-Tsien.
    /// Has a pole where β + correction·Cp0 = 0 (typically around M ≈ 0.8 for Cp0 = -1).
    /// Returns NaN near and past the pole — the correction is unreliable there.
    /// </summary>
    public static double Laitone(double cpIncomp, double mach, double gamma)
    {
        if (mach >= 1.0) return double.NaN;

        double m2 = mach * mach;
        double beta = System.Math.Sqrt(1.0 - m2);
        double correction = m2 * (1.0 + 0.5 * (gamma - 1.0) * m2) / (2.0 * beta);
        double den = beta + correction * cpIncomp;
        if (System.Math.Abs(den) < 1e-10) return double.NaN;

        double result = cpIncomp / den;
        // Past the pole the sign flips — result is physically meaningless
        if (cpIncomp < 0 && result > 0) return double.NaN;
        if (cpIncomp > 0 && result < 0) return double.NaN;
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  6. WAVE DRAG APPROXIMATION
    //     Simplified models for drag rise near and above Mach 1.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Approximate Cd multiplier vs Mach for a blunt body.
    /// Returns a multiplier to apply to the subsonic Cd.
    ///
    /// Uses a piecewise model:
    ///   M &lt; Mcrit:  1.0 (no compressibility drag rise)
    ///   Mcrit..1.2: smooth ramp to peak (transonic drag rise)
    ///   M &gt; 1.2:   1 + K/M² decay (supersonic wave drag)
    ///
    /// This is a curve-fit approximation, not first-principles.
    /// </summary>
    public static double WaveDragMultiplier(double mach, double criticalMach = 0.7, double peakMultiplier = 2.5)
    {
        if (mach <= criticalMach) return 1.0;

        const double peakMach = 1.2;

        if (mach <= peakMach)
        {
            // Smooth cosine ramp from 1.0 at Mcrit to peakMultiplier at peakMach
            double t = (mach - criticalMach) / (peakMach - criticalMach);
            double smooth = 0.5 * (1.0 - System.Math.Cos(System.Math.PI * t));
            return 1.0 + (peakMultiplier - 1.0) * smooth;
        }

        // Supersonic decay: approaches 1.0 as M → ∞
        // Cd_wave ∝ 1/M² (linear theory for slender bodies) but blunt bodies decay slower
        double excess = peakMultiplier - 1.0;
        double ratio = peakMach / mach;
        return 1.0 + excess * ratio * ratio;
    }

    /// <summary>
    /// Supersonic pressure drag coefficient for a flat plate normal to flow.
    /// Newtonian approximation: Cp = 2·sin²(θ) where θ = angle of incidence.
    /// For a flat plate normal to flow, θ = 90° → Cp = 2.0.
    ///
    /// Modified Newtonian: Cp = Cp_max · sin²(θ)
    /// where Cp_max = stagnation pressure coefficient behind normal shock.
    /// </summary>
    public static double NewtonianPressureCoefficient(double angleRad, double mach, double gamma)
    {
        double sin2 = System.Math.Sin(angleRad);
        sin2 *= sin2;

        if (mach <= 1.0)
            return 0; // Newtonian theory only valid for supersonic

        // Modified Newtonian: Cp_max from Rayleigh Pitot formula
        double cpMax = ModifiedNewtonianCpMax(mach, gamma);
        return cpMax * sin2;
    }

    /// <summary>
    /// Maximum pressure coefficient (stagnation point) for supersonic flow.
    /// From Rayleigh Pitot tube formula:
    ///   Cp_max = (2/(γ·M²)) · [(((γ+1)²·M²)/(4γ·M²-2(γ-1)))^(γ/(γ-1)) · ((1-γ+2γ·M²)/(γ+1)) - 1]
    /// </summary>
    public static double ModifiedNewtonianCpMax(double mach, double gamma)
    {
        if (mach <= 1.0) return 1.0; // subsonic stagnation Cp

        double m2 = mach * mach;
        double gm1 = gamma - 1.0;
        double gp1 = gamma + 1.0;

        // Stagnation pressure behind normal shock / freestream dynamic pressure
        // P02/P∞ via Rayleigh Pitot tube
        double term1 = gp1 * gp1 * m2 / (4.0 * gamma * m2 - 2.0 * gm1);
        double term2 = System.Math.Pow(term1, gamma / gm1);
        double term3 = (1.0 - gamma + 2.0 * gamma * m2) / gp1;
        double p02_pinf = term2 * term3;

        // Convert to Cp: Cp = (P02 - P∞) / q∞ = (P02/P∞ - 1) / (γ·M²/2)
        return (p02_pinf - 1.0) / (0.5 * gamma * m2);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  7. SOFT Cd CAP
    //     Smooth saturation to prevent unphysical drag coefficients
    //     while maintaining continuous derivatives (no sim jitter).
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Default maximum drag coefficient (Newtonian limit).</summary>
    public const double DefaultCdMax = 2.0;

    /// <summary>
    /// Smooth drag coefficient cap using tanh saturation.
    ///   Cd_eff = Cd_max · tanh(Cd_raw / Cd_max)
    ///
    /// At low Cd_raw (&lt; ~1.0): nearly linear pass-through.
    /// At high Cd_raw: smoothly rolls off toward Cd_max.
    /// Continuous first derivative everywhere — no jitter in physics sim.
    /// </summary>
    public static double SoftCapCd(double cdRaw, double cdMax = DefaultCdMax)
    {
        if (cdRaw <= 0) return 0;
        return cdMax * System.Math.Tanh(cdRaw / cdMax);
    }

    /// <summary>
    /// Combined: apply wave drag multiplier then soft-cap the result.
    /// Convenience for the common pattern of base Cd → transonic scaling → cap.
    /// </summary>
    public static double EffectiveCd(double baseCd, double mach, double cdMax = DefaultCdMax,
                                      double criticalMach = 0.7, double peakMultiplier = 2.5)
    {
        double raw = baseCd * WaveDragMultiplier(mach, criticalMach, peakMultiplier);
        return SoftCapCd(raw, cdMax);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  8. CONVENIENCE: FULL STATE BEHIND NORMAL SHOCK
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Compute full atmospheric state downstream of a normal shock,
    /// given upstream state and Mach number.
    /// </summary>
    public static AtmosphereState StateAfterNormalShock(AtmosphereState upstream, double mach, double gamma)
    {
        if (mach <= 1.0) return upstream;

        double tRatio = NormalShockTemperatureRatio(mach, gamma);
        double pRatio = NormalShockPressureRatio(mach, gamma);
        double rhoRatio = NormalShockDensityRatio(mach, gamma);
        double m2 = NormalShockMach(mach, gamma);

        double T2 = upstream.Temperature * tRatio;
        double P2 = upstream.Pressure * pRatio;
        double rho2 = upstream.Density * rhoRatio;

        // Speed of sound in downstream gas
        // R_specific = P / (ρ·T) — recover from upstream
        double R = upstream.Pressure / (upstream.Density * upstream.Temperature);
        double a2 = System.Math.Sqrt(gamma * R * T2);

        return new AtmosphereState
        {
            Temperature = T2,
            Pressure = P2,
            Density = rho2,
            SpeedOfSound = a2,
            DynamicViscosity = upstream.DynamicViscosity * System.Math.Pow(T2 / upstream.Temperature, 0.7),
        };
    }
}
