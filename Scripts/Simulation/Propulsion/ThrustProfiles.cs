#pragma warning disable
using System;
namespace AeroMod;

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
    //  ROCKET — no air dependency, thrust constant with airspeed.
    //  Only varies with backpressure (altitude), not Mach.
    // ═══════════════════════════════════════════════════════════════════

    public static readonly ThrustProfile Rocket = new("Rocket",
        new (double, double)[]
        {
            (0.0,  1.00),
            (25.0, 1.00),
        });

    // ═══════════════════════════════════════════════════════════════════
    //  PROPELLER — peak at low speed, drops to zero by M ~0.7.
    //  Tip compressibility kills efficiency.
    //  Operating range: M 0 – 0.75
    // ═══════════════════════════════════════════════════════════════════

    public static readonly ThrustProfile Propeller = new("Propeller",
        new (double, double)[]
        {
            (0.00, 1.00),
            (0.10, 0.95),
            (0.30, 0.80),
            (0.50, 0.55),
            (0.60, 0.35),
            (0.70, 0.10),
            (0.75, 0.00),
        },
        maxOperatingMach: 0.75);

    // ═══════════════════════════════════════════════════════════════════
    //  TURBOFAN (high bypass, BPR ~5-8)
    //  Good subsonic cruise. Fan can't handle supersonic inlet.
    //  Operating range: M 0 – 1.6
    // ═══════════════════════════════════════════════════════════════════

    public static readonly ThrustProfile Turbofan = new("Turbofan",
        new (double, double)[]
        {
            (0.00, 1.00),
            (0.30, 0.85),
            (0.60, 0.70),
            (0.80, 0.60),
            (0.90, 0.52),
            (1.00, 0.45),
            (1.20, 0.30),
            (1.40, 0.15),
            (1.60, 0.05),
        },
        maxOperatingMach: 1.6);

    // ═══════════════════════════════════════════════════════════════════
    //  TURBOJET (low/no bypass)
    //  Transonic thrust pinch, then ram recovery supersonic.
    //  Operating range: M 0 – 3.0
    // ═══════════════════════════════════════════════════════════════════

    public static readonly ThrustProfile Turbojet = new("Turbojet",
        new (double, double)[]
        {
            (0.00, 1.00),
            (0.40, 0.85),
            (0.80, 0.75),
            (0.95, 0.70),
            (1.00, 0.72),
            (1.20, 0.80),
            (1.50, 0.95),
            (2.00, 1.15),
            (2.50, 1.25),
            (3.00, 1.10),
        },
        maxOperatingMach: 3.0);

    // ═══════════════════════════════════════════════════════════════════
    //  RAMJET — relies on ram compression, zero static thrust.
    //  Needs booster to ~M 0.5.
    //  Operating range: M 0.5 – 6.0
    // ═══════════════════════════════════════════════════════════════════

    public static readonly ThrustProfile Ramjet = new("Ramjet",
        new (double, double)[]
        {
            (0.50, 0.00),
            (0.80, 0.15),
            (1.00, 0.35),
            (1.50, 0.65),
            (2.00, 0.85),
            (2.50, 1.00),
            (3.00, 1.05),
            (3.50, 1.00),
            (4.00, 0.85),
            (5.00, 0.55),
            (6.00, 0.20),
        },
        minOperatingMach: 0.5,
        maxOperatingMach: 6.0);

    // ═══════════════════════════════════════════════════════════════════
    //  SCRAMJET — supersonic combustion, M 4+.
    //  Operating range: M 4.0 – 15.0
    // ═══════════════════════════════════════════════════════════════════

    public static readonly ThrustProfile Scramjet = new("Scramjet",
        new (double, double)[]
        {
            (4.00,  0.00),
            (4.50,  0.20),
            (5.00,  0.50),
            (6.00,  0.80),
            (7.00,  1.00),
            (8.00,  0.95),
            (10.0,  0.80),
            (12.0,  0.60),
            (15.0,  0.35),
        },
        minOperatingMach: 4.0,
        maxOperatingMach: 15.0);

    // ═══════════════════════════════════════════════════════════════════
    //  HELICOPTER ROTOR — thrust depends on density, not forward speed.
    //  However, retreating blade compressibility limits forward speed.
    //  At high forward speed, the advancing blade tips go supersonic
    //  and the retreating blade stalls — rotor effectiveness drops.
    //  VNE is typically M 0.3–0.45 for conventional rotors.
    //  Operating range: M 0 – 0.55
    // ═══════════════════════════════════════════════════════════════════

    public static readonly ThrustProfile HelicopterRotor = new("HelicopterRotor",
        new (double, double)[]
        {
            (0.00, 1.00),   // hover — full authority
            (0.10, 1.05),   // translational lift bonus (ETL)
            (0.20, 1.10),   // peak ETL
            (0.30, 1.05),   // ETL fading, approaching VNE
            (0.40, 0.85),   // retreating blade stall beginning
            (0.50, 0.50),   // severe retreating blade stall
            (0.55, 0.20),   // structural/aero limit
            (0.60, 0.00),   // rotor disintegration speed
        },
        maxOperatingMach: 0.60);

    /// <summary>All built-in profiles by name.</summary>
    public static readonly Dictionary<string, ThrustProfile> All = new()
    {
        [nameof(Rocket)]          = Rocket,
        [nameof(Propeller)]       = Propeller,
        [nameof(Turbofan)]        = Turbofan,
        [nameof(Turbojet)]        = Turbojet,
        [nameof(Ramjet)]          = Ramjet,
        [nameof(Scramjet)]        = Scramjet,
        [nameof(HelicopterRotor)] = HelicopterRotor,
    };
}
