#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Reflection;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement;

namespace AeroMod;

/// <summary>
/// The game's hydrogen burn of a thruster block (2026-10-17, the ramjets: RamjetLaw FUEL). A hydrogen thruster's ThrusterComponent keeps
/// a private SingleFrameBinaryResourceConsumptionBuffer (_buffer): on while the block fires (IsThrusting), it adds the definition's
/// whole ResourcesRequiredToThrust to the block's hydrogen consumption (IConsumedResourceModifier), whatever the thrust. The buffer
/// type and its members (CurrentState, IsActive, OnConsuming, OnStopConsuming) are public; only the field is private - read ONCE per
/// block (FieldInfo, looked up once per session), never per tick. AeroPropulsorComponent.FuelTick then switches it with the public
/// members, as the game's own OnStartFiring / OnStopFiring do. Not found (a game update renamed it): a warning once and null - the
/// ramjet then burns as the game has it (flat while it fires). docs/REFLECTION_AND_WHITELIST.md.
/// </summary>
internal static class RamjetFuelBridge
{
    static FieldInfo _field;
    static bool _looked;
    /// <summary>Why the buffer is not reached ("" while it is).</summary>
    internal static string Problem = "";

    /// <summary>The block's thruster's consumption buffer (null: no ThrusterComponent, no buffer - not a hydrogen-burning
    /// definition - or the field not found). Once per block: not a per-tick call.</summary>
    internal static SingleFrameBinaryResourceConsumptionBuffer BufferOf(Entity block)
    {
        var tc = block?.TryGet<ThrusterComponent>();
        if (tc == null) return null;
        if (!_looked)
        {
            _looked = true;
            try { _field = typeof(ThrusterComponent).GetField("_buffer", BindingFlags.Instance | BindingFlags.NonPublic); }
            catch { _field = null; }
            if (_field == null || _field.FieldType != typeof(SingleFrameBinaryResourceConsumptionBuffer))
            {
                _field = null;
                Problem = "ThrusterComponent._buffer (SingleFrameBinaryResourceConsumptionBuffer) not found";
                Log.Default?.Warning($"[AERO] Ramjet fuel: {Problem} - the ramjets burn hydrogen as the game has it (flat while they fire)");
            }
        }
        if (_field == null) return null;
        try { return _field.GetValue(tc) as SingleFrameBinaryResourceConsumptionBuffer; }
        catch { return null; }
    }
}
