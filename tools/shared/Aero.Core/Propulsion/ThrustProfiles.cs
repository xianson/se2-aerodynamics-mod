namespace Aero.Core.Propulsion;

/// <summary>
/// Pre-built thrust profiles for common engine types.
/// Scale values represent T/T_static — thrust relative to static (M=0) rated thrust.
///
/// Sources: NASA SP-36, Mattingly "Elements of Propulsion", Kerrebrock "Aircraft Engines".
/// Values are simplified curve-fits suitable for game simulation.
/// </summary>
public static class ThrustProfiles
{
    // ═══════════════════════════════════════════════════════════════════
    //  PROPELLER
    //  Peak efficiency at low speed, thrust drops with advance ratio.
    //  Completely ineffective above ~M 0.7 (tip shocks, compressibility).
    //  Operating range: M 0 – 0.7
    // ═══════════════════════════════════════════════════════════════════

    public static readonly ThrustProfile Propeller = new("Propeller",
        new (double, double)[]
        {
            (0.00, 1.00),   // static thrust (bollard pull)
            (0.10, 0.95),   // slight loss as prop disc loads up
            (0.30, 0.80),   // blade angle of attack decreasing
            (0.50, 0.55),   // significant advance ratio losses
            (0.60, 0.35),   // approaching compressibility on tips
            (0.70, 0.10),   // tip shocks forming, severe loss
            (0.75, 0.00),   // effectively dead
        },
        minOperatingMach: 0.0,
        maxOperatingMach: 0.75);

    // ═══════════════════════════════════════════════════════════════════
    //  TURBOFAN (high bypass, BPR ~5-8)
    //  Good subsonic cruise efficiency. Fan can't handle supersonic inlet.
    //  Thrust drops through transonic, dies above ~M 1.6.
    //  Operating range: M 0 – 1.6
    // ═══════════════════════════════════════════════════════════════════

    public static readonly ThrustProfile Turbofan = new("Turbofan",
        new (double, double)[]
        {
            (0.00, 1.00),   // static rated thrust
            (0.30, 0.85),   // ram drag building on bypass flow
            (0.60, 0.70),   // cruise sweet spot for efficiency (not thrust)
            (0.80, 0.60),   // approaching transonic
            (0.90, 0.52),   // inlet shock losses beginning
            (1.00, 0.45),   // significant inlet losses, fan surge margin shrinks
            (1.20, 0.30),   // severe — bypass duct choking
            (1.40, 0.15),   // barely operable
            (1.60, 0.05),   // flameout imminent
        },
        minOperatingMach: 0.0,
        maxOperatingMach: 1.6);

    // ═══════════════════════════════════════════════════════════════════
    //  TURBOJET (low/no bypass)
    //  Classic jet engine. Loses thrust subsonically but ram compression
    //  recovers it at supersonic speeds. Operable to ~M 3.
    //  Operating range: M 0 – 3.0
    // ═══════════════════════════════════════════════════════════════════

    public static readonly ThrustProfile Turbojet = new("Turbojet",
        new (double, double)[]
        {
            (0.00, 1.00),   // static rated thrust
            (0.40, 0.85),   // ram drag on intake
            (0.80, 0.75),   // pre-transonic dip
            (0.95, 0.70),   // transonic "thrust pinch"
            (1.00, 0.72),   // through the pinch — ram compression helping
            (1.20, 0.80),   // ram recovery
            (1.50, 0.95),   // approaching parity with static
            (2.00, 1.15),   // ram compression exceeds intake losses
            (2.50, 1.25),   // peak practical thrust (inlet heating limit)
            (3.00, 1.10),   // thermal limits, compressor efficiency drops
        },
        minOperatingMach: 0.0,
        maxOperatingMach: 3.0);

    // ═══════════════════════════════════════════════════════════════════
    //  RAMJET
    //  No moving parts — relies entirely on ram compression.
    //  Zero thrust at zero speed (needs a booster to reach ~M 0.5).
    //  Sweet spot M 2 – 5. Subsonic combustion limits top end.
    //  Operating range: M 0.5 – 6.0
    // ═══════════════════════════════════════════════════════════════════

    public static readonly ThrustProfile Ramjet = new("Ramjet",
        new (double, double)[]
        {
            (0.50, 0.00),   // minimum start — barely enough ram pressure
            (0.80, 0.15),   // marginal combustion
            (1.00, 0.35),   // getting usable
            (1.50, 0.65),   // inlet efficiency improving
            (2.00, 0.85),   // near design point
            (2.50, 1.00),   // peak — optimal inlet recovery + combustion
            (3.00, 1.05),   // still strong
            (3.50, 1.00),   // inlet heating, diminishing returns
            (4.00, 0.85),   // thermal limits on combustor walls
            (5.00, 0.55),   // excessive inlet temperature, poor combustion
            (6.00, 0.20),   // subsonic combustion untenable at this enthalpy
        },
        minOperatingMach: 0.5,
        maxOperatingMach: 6.0);

    // ═══════════════════════════════════════════════════════════════════
    //  SCRAMJET (supersonic combustion ramjet)
    //  Combustion stays supersonic — no normal shock in inlet.
    //  Only works at very high Mach. Needs booster to M ~4.
    //  Operating range: M 4.0 – 15+
    // ═══════════════════════════════════════════════════════════════════

    public static readonly ThrustProfile Scramjet = new("Scramjet",
        new (double, double)[]
        {
            (4.00,  0.00),   // minimum start — barely igniting
            (4.50,  0.20),   // unstable combustion
            (5.00,  0.50),   // design point lower bound
            (6.00,  0.80),   // approaching optimal
            (7.00,  1.00),   // peak — best fuel-air mixing efficiency
            (8.00,  0.95),   // slight loss from dissociation
            (10.0,  0.80),   // significant dissociation of combustion products
            (12.0,  0.60),   // thermal management critical
            (15.0,  0.35),   // extreme — wall cooling limits thrust
        },
        minOperatingMach: 4.0,
        maxOperatingMach: 15.0);

    // ═══════════════════════════════════════════════════════════════════
    //  ROCKET (included for completeness — no air dependency)
    //  Thrust is constant with airspeed. Only varies with backpressure
    //  (altitude), not Mach. This profile is flat.
    // ═══════════════════════════════════════════════════════════════════

    public static readonly ThrustProfile Rocket = new("Rocket",
        new (double, double)[]
        {
            (0.0,  1.00),
            (25.0, 1.00),   // orbital velocity — still 1.0
        },
        minOperatingMach: 0.0,
        maxOperatingMach: double.PositiveInfinity);

    // ═══════════════════════════════════════════════════════════════════
    //  HELICOPTER ROTOR — thrust depends on density, not forward speed.
    //  Retreating blade compressibility limits forward speed.
    //  VNE typically M 0.3–0.45 for conventional rotors.
    //  Operating range: M 0 – 0.6
    // ═══════════════════════════════════════════════════════════════════

    public static readonly ThrustProfile HelicopterRotor = new("HelicopterRotor",
        new (double, double)[]
        {
            (0.00, 1.00),
            (0.10, 1.05),
            (0.20, 1.10),   // peak translational lift
            (0.30, 1.05),
            (0.40, 0.85),   // retreating blade stall
            (0.50, 0.50),
            (0.55, 0.20),
            (0.60, 0.00),
        },
        maxOperatingMach: 0.60);

    /// <summary>All built-in profiles, indexed by name.</summary>
    public static readonly System.Collections.Generic.Dictionary<string, ThrustProfile> All = new()
    {
        [nameof(Propeller)]       = Propeller,
        [nameof(Turbofan)]        = Turbofan,
        [nameof(Turbojet)]        = Turbojet,
        [nameof(Ramjet)]          = Ramjet,
        [nameof(Scramjet)]        = Scramjet,
        [nameof(Rocket)]          = Rocket,
        [nameof(HelicopterRotor)] = HelicopterRotor,
    };
}
