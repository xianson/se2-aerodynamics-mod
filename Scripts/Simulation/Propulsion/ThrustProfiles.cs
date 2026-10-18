#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Collections.Generic;
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
    //  PROPELLER — a piston engine on a fixed-pitch prop (2026-10-17: kept
    //  apart from the turboprop, the user: "is there a difference between a
    //  propeller and a turboprop?"). The fixed pitch is efficient near one
    //  speed only, so thrust falls steadily as airspeed rises; practical top
    //  speed ~300-500 km/h. Not used by a block yet (a future piston prop).
    //  Operating range: M 0 – 0.45
    // ═══════════════════════════════════════════════════════════════════

    public static readonly ThrustProfile Propeller = new("Propeller",
        new (double, double)[]
        {
            (0.00, 1.00),
            (0.10, 0.92),
            (0.20, 0.78),
            (0.30, 0.55),
            (0.38, 0.30),
            (0.45, 0.00),
        },
        maxOperatingMach: 0.45);

    // ═══════════════════════════════════════════════════════════════════
    //  TURBOPROP — a gas turbine driving a constant-speed prop: roughly
    //  constant POWER, so full static thrust up to TurbopropV0, then thrust
    //  ∝ 1/airspeed; the prop tips' compressibility fades it out from M 0.6
    //  to nothing at M 0.75. The Turboprop blocks use TurbopropFraction
    //  (AeroPropulsorComponent.Speed, at their true airspeed and local Mach);
    //  this profile samples the same curve for axial flight at sea level, so
    //  the reports and the design probe show what the blocks do.
    //  Operating range: M 0 – 0.75
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>The SEA-LEVEL speed of sound (m/s) - only where a curve indexed by Mach needs an airspeed to sample a curve that
    /// is really by airspeed (the Turboprop and DuctedFan profiles below: the reports' and the design probe's sea-level
    /// figures; DesignProbe.SeaSos is this). In flight the blocks never use it: they take the grid's true airspeed and its
    /// local speed of sound (AeroGridComponent.LastAirVel / LastMach - 2026-10-17: it was a fixed 330 m/s over the GROUND speed).</summary>
    public const float SeaLevelSoundSpeed = 340.3f;
    /// <summary>Turboprop: the airspeed (m/s along the thrust) where constant power takes over from static thrust.</summary>
    public static float TurbopropV0 = 40f;
    /// <summary>Turboprop: tip compressibility - full up to this Mach, nothing at MachZero.</summary>
    public static float TurbopropMachFade = 0.6f, TurbopropMachZero = 0.75f;

    /// <summary>A turboprop's thrust fraction at its true airspeed along its thrust (m/s, + forward) and its flight Mach (the
    /// whole airspeed over the local speed of sound): the constant-power fall past TurbopropV0 and the tip-Mach fade (the
    /// constants above: game-free, so the tests use the same curve).</summary>
    public static float TurbopropFraction(float along, float mach)
    {
        float v0 = TurbopropV0;
        float f = along > v0 ? v0 / along : 1f;
        float fade = TurbopropMachFade, zero = TurbopropMachZero;
        if (mach > fade) f *= MathF.Max(0f, 1f - (mach - fade) / (zero - fade));
        return f;
    }

    /// <summary>Ducted fan: the true airspeed (m/s along the thrust) where its thrust is gone - a fan's static thrust falls
    /// off faster than a prop's.</summary>
    public static float FanVZero = 130f;
    /// <summary>A ducted fan's thrust fraction at its true airspeed along its thrust (m/s, + forward): 1 - v / FanVZero, never below 0.</summary>
    public static float FanFraction(float along) => Math.Clamp(1f - MathF.Max(along, 0f) / FanVZero, 0f, 1f);

    static (double, double)[] TurbopropKnots()
    {
        // (sampled every M 0.0125 for axial flight at sea level: dense enough that the profile's smoothing between knots stays within ~1 %)
        int n = (int)MathF.Ceiling(TurbopropMachZero / 0.0125f);
        var k = new (double, double)[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float m = MathF.Min(i * 0.0125f, TurbopropMachZero), v = m * SeaLevelSoundSpeed;
            k[i] = (m, TurbopropFraction(v, m));
        }
        return k;
    }

    static (double, double)[] FanKnots()
    {
        float mz = FanVZero / SeaLevelSoundSpeed;
        int n = (int)MathF.Ceiling(mz / 0.0125f);
        var k = new (double, double)[n + 1];
        for (int i = 0; i <= n; i++) { float m = MathF.Min(i * 0.0125f, mz); k[i] = (m, FanFraction(m * SeaLevelSoundSpeed)); }
        return k;
    }

    public static readonly ThrustProfile Turboprop = new("Turboprop", TurbopropKnots(),
        maxOperatingMach: TurbopropMachZero);

    /// <summary>DUCTED FAN - FanFraction sampled for axial flight at sea level (as Turboprop: the reports' and the design
    /// probe's curve by Mach; the blocks themselves use FanFraction at their true airspeed). Gone at FanVZero (~M 0.38).</summary>
    public static readonly ThrustProfile DuctedFan = new("DuctedFan", FanKnots(),
        maxOperatingMach: FanVZero / SeaLevelSoundSpeed);

    // ═══════════════════════════════════════════════════════════════════
    //  JET — the VANILLA ATMOSPHERIC THRUSTERS (2026-10-17, the user's speed
    //  roles: "turboprops kings of subsonic, atmos king of transonic-ish,
    //  ramjets for supersonic", "a lil more respeck on mach 1's name").
    //  The subsonic lapse to 0.85 at Mach 0.85, then the RAM RISE: the
    //  inlet's ram pressure takes the thrust above its rating from ~Mach
    //  0.97 (JetMach1 at Mach 1), to JetRamPeak at Mach 1.6, then the
    //  compressor's limit cuts it fast: below the ramjet's curve from Mach
    //  2.0 (the ramjet owns Mach 2-6), gone at 2.8.
    //  NO TRANSONIC PINCH (2026-10-18, the user: '1 and 2' - soften the
    //  pinch, scale the block wings' wave drag): the curve dipped to 0.76 at
    //  Mach 1 (0.80 at 0.95, 0.95 at 1.1) - an intake choking through Mach 1;
    //  a real afterburning engine does not pinch there, its sea-level thrust
    //  rises with the ram from the high subsonic on. With the wings' own wave
    //  drag (CompressibleWingModel.WingWaveDrag) the pinch made the crossing
    //  an endgame wall (the F-6 T/W 3.4 at sea level). The rise is steeper
    //  and higher than the pinch's ram rise (1.48 -> 1.94 at Mach 1.45, the
    //  top 1.80 at 1.7 -> 1.98 at 1.6): with no pinch to make Mach 1 the
    //  costly point, the supersonic cruise past it must still be easier than
    //  the crossing - the hull's drag past Mach 1 grows as M^2 - so the curve
    //  rises about as the F-6's drag does from Mach 1 to 1.45. The fall from
    //  Mach 1.8 on (the ramjet's handover ~Mach 2.0) is unchanged.
    //  Applied in normal play to the game's own maximum (JetCurve,
    //  AeroThrustSettingsComponent.Jet; above the rating too: JetCurve.MaxFactor);
    //  the game's air-density efficiency stacks on it (this curve has no
    //  density in it). JetMach1, the Mach 1 knot, is the tuning knob
    //  (Tests/AeroBench machwall, the whole model: the sleek F-6 breaks Mach 1
    //  in level flight at static T/W ~1.55 at sea level and ~1.17 in air of
    //  0.4 kg/m3, then cruises on to ~Mach 1.45; a brick stalls subsonic).
    //  Operating range: M 0 – 2.8
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>The Jet curve's value at Mach 1 (x the rating): the ram rise's transonic point (2026-10-18, the user: '1 and 2'
    /// - soften the pinch, scale the block wings' wave drag: it was the pinch's depth, 0.76, the curve's lowest point in the
    /// transonic band).</summary>
    public const double JetMach1 = 1.08;
    /// <summary>The ram rise's top (x the rating, at JetRamPeakMach; 2026-10-18: 1.80 at Mach 1.7 before - see JetMach1).</summary>
    public const double JetRamPeak = 1.98;
    /// <summary>Where the ram rise tops out (Mach).</summary>
    public const double JetRamPeakMach = 1.60;

    public static readonly ThrustProfile Jet = JetWithMach1(JetMach1);

    /// <summary>The Jet curve with another value at Mach 1 (the tuning tests' sweep: Tests/AeroBench machwall).</summary>
    public static ThrustProfile JetWithMach1(double atMach1) => new("Jet",
        new (double, double)[]
        {
            (0.00, 1.00),
            (0.60, 0.95),
            (0.85, 0.85),
            (1.00, atMach1),
            (1.10, 1.28),
            (1.20, 1.48),
            (1.30, 1.68),
            (1.45, 1.94),
            (JetRamPeakMach, JetRamPeak),
            (1.70, 1.85),
            (1.80, 1.55),
            (1.90, 1.20),
            (2.00, 0.82),
            (2.20, 0.45),
            (2.40, 0.25),
            (2.80, 0.00),
        },
        maxOperatingMach: 2.8);

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
        [nameof(Turboprop)]       = Turboprop,
        [nameof(DuctedFan)]       = DuctedFan,
        [nameof(Jet)]             = Jet,
        [nameof(Turbofan)]        = Turbofan,
        [nameof(Turbojet)]        = Turbojet,
        [nameof(Ramjet)]          = Ramjet,
        [nameof(Scramjet)]        = Scramjet,
        [nameof(HelicopterRotor)] = HelicopterRotor,
    };
}
