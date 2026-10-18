#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;

namespace AeroMod;

/// <summary>
/// The game burns a thruster's whole ResourcesRequiredToThrust on every tick it fires (ThrusterComponent: a SingleFrameBinaryResourceConsumptionBuffer,
/// on while the block fires - all or nothing; RamjetFuelBridge reaches it). To make the resource follow a SHARE of that rate (the ramjet's thrust
/// factor, a solid rocket's stack factor x power level) the mod lets the game burn on only some ticks of each window: round(duty x window) of
/// them, the rounding carried on, the burning ticks first (one switch on, one off per window). Game-free (Tests/ThrustTests RamjetChecks,
/// Tests/SolidRocketTests). One law for both users (RamjetLaw.Burn, AeroSolidNozzleComponent.FuelTick) - 2026-10-17, pulled out of RamjetLaw
/// when the solid rocket motors began to burn a conveyor-carried propellant the same way.
/// </summary>
public static class DutyCycle
{
    /// <summary>The window (ticks) both users run (RamjetLaw.FuelWindow, SolidRocket.FuelWindow): at most two switches of the game's burn
    /// per window per block.</summary>
    public const int Window = 12;

    /// <summary>One block's schedule: the tick in the window, the burning ticks of this window, the rounding carried.</summary>
    public struct State { public int Tick, On; public float Carry; }

    /// <summary>One tick: true - let the game burn (if it fires) this tick; false - its burn held off. At each window's start the window's
    /// burning ticks are round(duty x window) with the remainder carried, so the average burn is the duty exactly. Duty 1 (or more):
    /// always; 0 (or less, NaN): never.</summary>
    public static bool Step(ref State s, float duty, int window)
    {
        if (window < 1) window = 1;
        if (!(duty > 0f)) { s.Tick = 0; s.On = 0; s.Carry = 0f; return false; }
        if (duty >= 1f) { s.Tick = 0; s.On = 0; s.Carry = 0f; return true; }
        if (s.Tick == 0)
        {
            float want = duty * window + s.Carry;
            s.On = Math.Clamp((int)MathF.Floor(want + 0.5f), 0, window);
            s.Carry = Math.Clamp(want - s.On, -1f, 1f);
        }
        bool on = s.Tick < s.On;
        if (++s.Tick >= window) s.Tick = 0;
        return on;
    }
}
