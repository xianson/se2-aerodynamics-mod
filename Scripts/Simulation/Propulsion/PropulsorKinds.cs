#pragma warning disable
using System;

namespace AeroMod;

/// <summary>
/// The propulsor KINDS and what each one does - game-free (the game part of AeroPropulsorComponent is AeroPropulsorComponent.cs;
/// this part compiles alone into Tests/RotorTests). ONE place for who gets which mechanic:
///   Rotor      a helicopter MAIN rotor: the cyclic and its reaction torque (RotorCyclic), governed rpm (lift x rpm^2),
///              the split head / blades / blur animation.
///   TailRotor  the REVERSIBLE ROTOR block (its kind name kept): governed rpm (lift x rpm^2), the split animation with no
///              collective / cyclic / coning, its blades' pitch by its push's side (TailRotor.cs) - NO cyclic, NO reaction
///              torque. Its thrust either way is its ReversibleThrustComponent's (any block can carry one), not this kind's.
///   Turboprop  a propeller on a turbine: governed rpm that idles at RotorKinematics.TurbopropIdleRpm and rises to 100 % with
///              the thrust asked (2026-10-17, the user: "the same 0.75 blade speed / blur treatment"), the spinner / blades /
///              blur-disc animation when its model is split (AeroRotorAnimComponent); its thrust is not gated by the rpm.
///   Fan        a ducted fan: the vanilla spin.
///   Ramjet     (2026-10-17) a HYDROGEN thruster underneath (thrust class Hydrogen: it draws hydrogen through conveyors) that is an
///              air-breather: ThrustProfiles.Ramjet of the Mach (nothing below Mach 0.5, 100 % at 2.5 .. 3.5, gone past 6) x the
///              air density x its inlet's unstart - RamjetLaw (the inlet law kept out of this file: RotorTests compiles it alone); its hydrogen burn follows that thrust
///              (RamjetLaw.FuelDuty); not governed, no cyclic, no reaction; its inlet spike slides aft with the Mach on the client
///              (AeroRamjetSpikeComponent).
/// </summary>
public partial class AeroPropulsorComponent
{
    public enum Kinds { Turboprop, Fan, Rotor, TailRotor, Ramjet }

    /// <summary>The cyclic (disc tilt as an attitude actuator, RotorCyclic.Give): main rotors only.</summary>
    public static bool HasCyclic(Kinds k) => k == Kinds.Rotor;
    /// <summary>The rotor's reaction torque on the ship (RotorCyclic.ReactionTorque): main rotors only.</summary>
    public static bool HasReaction(Kinds k) => k == Kinds.Rotor;
    /// <summary>The mod's governed rpm (server) and its record for the client animation (RotorCyclic.Publish).</summary>
    public static bool Governed(Kinds k) => k == Kinds.Rotor || k == Kinds.TailRotor || k == Kinds.Turboprop;
    /// <summary>Lift only as the rotor turns: the maximum x RotorKinematics.ThrustFactor(rpm) (none until spun up).</summary>
    public static bool LiftNeedsRpm(Kinds k) => k == Kinds.Rotor || k == Kinds.TailRotor;
    /// <summary>A ramjet: its thrust is RamjetLaw's (the Mach curve, the density, the inlet unstart), its hydrogen burn follows it.</summary>
    public static bool IsRamjet(Kinds k) => k == Kinds.Ramjet;
    /// <summary>The governed rpm it holds while on and asked for nothing (1: always 100 %).</summary>
    public static float IdleRpm(Kinds k) => k == Kinds.Turboprop ? RotorKinematics.TurbopropIdleRpm : 1f;
    /// <summary>Air density giving the rated thrust (the game's sea level is ~1), and the most the density factor gives.</summary>
    public static float RhoRef = 1f, RhoMax = 1.25f;
    /// <summary>The density factor on the rating: rho / RhoRef, at most RhoMax, 0 in vacuum.</summary>
    public static float DensityFactor(float rho) => MathF.Min(MathF.Max(rho, 0f) / RhoRef, RhoMax);

    /// <summary>THE CURVES (the ONE place; 2026-10-17 game-free, so the tests fly the same law): the thrust fraction at a true
    /// airspeed along the thrust (m/s, + forward), the whole true airspeed (m/s) and the flight Mach (that over the local
    /// speed of sound) - in game AeroGridComponent.LastAirVel / LastMach (the wind out; never the ground speed, never a fixed
    /// speed of sound). Turboprop: constant power past ThrustProfiles.TurbopropV0 and the tip-Mach fade; fan: linear to
    /// ThrustProfiles.FanVZero; rotors (main and reversible): ThrustProfiles.HelicopterRotor of the Mach; a ramjet:
    /// ThrustProfiles.Ramjet of the Mach (RamjetFraction - its inlet factor and the density are on top, RamjetLaw).</summary>
    public static float Speed(Kinds kind, float along, float speed, float mach)
    {
        switch (kind)
        {
            case Kinds.Turboprop: return ThrustProfiles.TurbopropFraction(along, mach);
            case Kinds.Fan: return ThrustProfiles.FanFraction(along);
            case Kinds.Ramjet: return RamjetFraction(mach);
            default: return (float)ThrustProfiles.HelicopterRotor.Evaluate(mach);
        }
    }

    /// <summary>The kind's curve BY MACH for axial flight at sea level (ThrustProfiles.Turboprop / DuctedFan / HelicopterRotor:
    /// Speed sampled at ThrustProfiles.SeaLevelSoundSpeed) - what the design probe applies to the rating at the Mach it asks.</summary>
    public static ThrustProfile CurveOf(Kinds kind) => kind switch
    {
        Kinds.Turboprop => ThrustProfiles.Turboprop,
        Kinds.Fan => ThrustProfiles.DuctedFan,
        Kinds.Ramjet => ThrustProfiles.Ramjet,
        _ => ThrustProfiles.HelicopterRotor,
    };

    /// <summary>A ramjet's Mach curve (ThrustProfiles.Ramjet: 0 below Mach 0.5, 1 at 2.5, 1.05 at 3, 1 at 3.5, 0 past 6), never NaN
    /// or negative.</summary>
    public static float RamjetFraction(float mach)
    {
        if (!(mach > 0f)) return 0f;
        float f = (float)ThrustProfiles.Ramjet.Evaluate(mach);
        return float.IsFinite(f) && f > 0f ? f : 0f;
    }

    /// <summary>The governed rpm it holds while on, at this load (thrust / its maximum now, 0..1).</summary>
    public static float TargetRpm(Kinds k, float load)
    {
        float idle = IdleRpm(k);
        return idle + (1f - idle) * Math.Clamp(load, 0f, 1f);
    }
}
