#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;

namespace AeroMod;

/// <summary>
/// THE RAMJET'S LAW (2026-10-17, the user: "wire the new RAMJET blocks"; docs/PROPULSOR_SPECS.md "Ramjets") - game-free (Tests/ThrustTests
/// RamjetChecks). The game part is AeroPropulsorComponent (the thrust, the hydrogen burn - server) and AeroRamjetSpikeComponent (the
/// spike - client). A ramjet block is a vanilla HYDROGEN thruster underneath (gen_propulsor_defs.py gen_ramjet: thrust class Hydrogen,
/// it draws hydrogen through conveyors); everything here goes through the game's own thrust (ThrustData.MaxThrustPower) and its own
/// hydrogen consumption - no extra force, no fuel of our own.
///   THRUST   max = rating x ThrustProfiles.Ramjet(Mach) x DensityFactor(rho) x InletAngle.Step(InletAngle.Ramjet, ...):
///            the rating (the definition's ThrustPower) is its thrust at the curve's 100 % (Mach 2.5 .. 3.5; 1.05 at Mach 3);
///            nothing below Mach 0.5 (no static thrust: it needs a boost), nothing past Mach 6, nothing in vacuum (rho 0), and
///            the inlet unstarts past 10 deg off the airflow (2 % until it relights). The Mach is the true airspeed over the
///            local speed of sound (AeroGridComponent.LastMach), the density the mod's (AeroGridComponent.LastDensity).
///   FUEL     the game charges a hydrogen thruster's whole ResourcesRequiredToThrust for every tick it fires (ThrusterComponent:
///            a SingleFrameBinaryResourceConsumptionBuffer, on while the grid asks thrust its way - IsThrusting - whatever its
///            maximum), so a ramjet below Mach 0.5 would burn its full rate for nothing. The ramjet's rate (the definition) is its
///            rating's: 1/3 of its paired vanilla hydrogen thruster's hydrogen per newton (it carries no oxidiser), and the mod
///            duty-cycles the game's burn to the share of its rating it can give now (FuelDuty - the thrust factor, 0..1): over
///            each FuelWindow ticks it burns on round(duty x window) of them (the rounding carried on), so the hydrogen used
///            follows the thrust - none when it gives none. Knob: FuelFollowsThrust (off: the game's flat burn).
///   SPIKE    the inlet spike (SR-71 style) is held fully forward up to SpikeMach0 (1.6), retracts linearly to its full free aft
///            travel at SpikeMach1 (3.2) and holds there - never beyond its free travel (per size: gen_propulsor_defs.py ->
///            PropulsorBlocks.Row.SpikeTravel). It moves at most a full travel in SpikeTravelSeconds.
/// </summary>
public static class RamjetLaw
{
    // ── thrust ──
    /// <summary>The ramjet's thrust factor (the fraction of its rating): its Mach curve x the density factor x its inlet factor.
    /// Never NaN or negative.</summary>
    public static float Factor(float mach, float density, float inlet)
    {
        float f = AeroPropulsorComponent.RamjetFraction(mach) * AeroPropulsorComponent.DensityFactor(density) * (float.IsFinite(inlet) ? Math.Clamp(inlet, 0f, 1f) : 1f);
        return float.IsFinite(f) && f > 0f ? f : 0f;
    }

    // ── fuel ──
    /// <summary>Always on in play: the hydrogen burn follows the thrust (off, A/B: the game's flat burn while it fires; harness:
    /// aeroset RamjetLaw.FuelFollowsThrust false).</summary>
    public static bool FuelFollowsThrust = true;
    /// <summary>The duty-cycle window (ticks): at most two switches of the game's burn per window per ramjet.</summary>
    public const int FuelWindow = DutyCycle.Window;
    /// <summary>The share of its full hydrogen rate a ramjet should burn now: its thrust factor (its maximum now over its
    /// rating), 0..1 (above 1 - Mach 3 at 1.05, dense air - the game's full rate, no more).</summary>
    public static float FuelDuty(float factor) => float.IsFinite(factor) ? Math.Clamp(factor, 0f, 1f) : 1f;

    /// <summary>One ramjet's burn schedule (DutyCycle's - the law shared with the solid rocket nozzles).</summary>
    public struct FuelState { public DutyCycle.State Duty; }

    /// <summary>One tick: true - let the game burn (if it fires) this tick; false - its burn held off (DutyCycle.Step over FuelWindow
    /// ticks: the average burn is the duty exactly; the burning ticks first). Duty 1: always; duty 0: never; NaN: the game's full burn.</summary>
    public static bool Burn(ref FuelState s, float duty) => DutyCycle.Step(ref s.Duty, FuelDuty(duty), FuelWindow);

    // ── spike ──
    /// <summary>The spike's schedule (Mach): fully forward up to SpikeMach0, fully retracted from SpikeMach1, linear between.</summary>
    public const float SpikeMach0 = 1.6f, SpikeMach1 = 3.2f;
    /// <summary>A full travel takes this long at the most (s): the spike follows the Mach smoothly, not a step per update.</summary>
    public const float SpikeTravelSeconds = 1.5f;

    /// <summary>The share of its free travel the spike sits aft at this Mach: 0 up to SpikeMach0, 1 from SpikeMach1 (NaN: 0).</summary>
    public static float SpikeFraction(float mach)
    {
        if (!(mach > SpikeMach0)) return 0f;
        return MathF.Min(1f, (mach - SpikeMach0) / (SpikeMach1 - SpikeMach0));
    }

    /// <summary>The spike's aft travel (m, along the block's +Z: aft) at this Mach, for this free travel (m): 0 .. free.</summary>
    public static float SpikeTravel(float mach, float freeTravel) => SpikeFraction(mach) * MathF.Max(0f, freeTravel);

    /// <summary>One step of the drawn spike (m): toward the target at most freeTravel / SpikeTravelSeconds per second, and
    /// always within 0 .. freeTravel.</summary>
    public static float StepSpike(float current, float target, float freeTravel, float dt)
    {
        float free = MathF.Max(0f, freeTravel);
        if (!float.IsFinite(current)) current = 0f;
        if (!float.IsFinite(target)) target = 0f;
        target = Math.Clamp(target, 0f, free);
        float step = (float.IsFinite(dt) && dt > 0f ? dt : 0f) * free / SpikeTravelSeconds;
        float next = current < target ? MathF.Min(target, current + step) : MathF.Max(target, current - step);
        return Math.Clamp(next, 0f, free);
    }
}
