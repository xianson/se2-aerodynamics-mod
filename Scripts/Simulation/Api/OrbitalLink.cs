#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using Keen.VRage.Core;

namespace AeroMod;

/// <summary>
/// THE ORBITAL MOD'S PER-GRID ANSWERS (2026-10-14; mods compile apart: OrbitalMod.OrbitalApi found by type name - the last
/// loaded copy, found again when the loaded assemblies change, LastLoadedType - each member by its name AND parameter types,
/// bound as a typed delegate and bound again after an Orbital reload; looked up again every RetrySeconds while missing; a
/// member that throws is dropped and looked up again after a wait - 10 s, doubling to 300 s a fault: CrossModBinding):
///  - TryGetGridEdits (StructureContacts): what Orbital WROTE into a grid's physics velocity (a running world sum, m/s) and
///    a stamp changed by every teleport or turn it made - its writes land after the physics step, so between two of the
///    contact balance's reads, and are no force on the ship;
///  - TryGetEntryState2 (StructureServer, the railed entry): the travel through the air, density, the band's temperature
///    and the frame's braking acceleration (world). Older Orbital mods: TryGetEntryState (AeroEntryFx.EntryStateAt).
/// Every lookup of Orbital's members is here (one rule; Orbital's tools/check_contract.sh checks them against its source).
/// Never called under a lock of ours (the lock rule, docs/API.md): Orbital's answers take small locks of its own.
/// </summary>
public static class OrbitalLink
{
    /// <summary>The wait (s) between lookups of a member that is missing.</summary>
    public static float RetrySeconds = 5f;
    delegate bool GridEditsFn(Keen.VRage.DCS.Components.Entity grid, out Vector3D velocityWrittenSum, out long moveStamp);
    delegate bool EntryState2Fn(Keen.VRage.DCS.Components.Entity grid, out Vector3 travelWorld, out float density, out float temperature, out Vector3 brakingAccelWorld);
    delegate bool EntryStateFn(Keen.VRage.DCS.Components.Entity grid, out Vector3 trueAirWorld, out float density);
    /// <summary>Diagnostics: why each is (not) bound.</summary>
    public static string EditsStatus = "not looked up", Entry2Status = "not looked up";

    /// <summary>The Orbital Mod's API type: the last loaded copy (2026-10-15: each lookup scanned for its own and kept the
    /// first delegate it bound for good - an Orbital reload left aero on the stale copy).</summary>
    static readonly LastLoadedType _api = new LastLoadedType("OrbitalMod.OrbitalApi");
    static CrossModBinding<T> Bind<T>(string name, Type[] parameterTypes) where T : Delegate
        => new CrossModBinding<T>(_api, "OrbitalApi", name, parameterTypes, static () => RetrySeconds);
    static readonly CrossModBinding<GridEditsFn> _edits =
        Bind<GridEditsFn>("TryGetGridEdits", new[] { typeof(Keen.VRage.DCS.Components.Entity), typeof(Vector3D).MakeByRefType(), typeof(long).MakeByRefType() });
    static readonly CrossModBinding<EntryState2Fn> _entry2 =
        Bind<EntryState2Fn>("TryGetEntryState2", new[] { typeof(Keen.VRage.DCS.Components.Entity), typeof(Vector3).MakeByRefType(), typeof(float).MakeByRefType(), typeof(float).MakeByRefType(), typeof(Vector3).MakeByRefType() });
    static readonly CrossModBinding<EntryStateFn> _entry =
        Bind<EntryStateFn>("TryGetEntryState", new[] { typeof(Keen.VRage.DCS.Components.Entity), typeof(Vector3).MakeByRefType(), typeof(float).MakeByRefType() });

    /// <summary>Diagnostics: the times the Orbital Mod's assembly changed (found, reloaded, gone).</summary>
    public static int Generation => _api.Generation;

    /// <summary>OrbitalApi.TryGetGridEdits: the sum of the velocity changes Orbital wrote into the grid (world, m/s) and its
    /// move stamp. False: no Orbital mod (or older), or it never wrote into this grid.</summary>
    public static bool TryGetGridEdits(Keen.VRage.DCS.Components.Entity grid, out Vector3D velocityWrittenSum, out long moveStamp)
    {
        velocityWrittenSum = default; moveStamp = 0;
        if (grid == null) return false;
        var fn = _edits.Get(ref EditsStatus);
        if (fn == null) return false;
        try { return fn(grid, out velocityWrittenSum, out moveStamp) && double.IsFinite(velocityWrittenSum.X + velocityWrittenSum.Y + velocityWrittenSum.Z); }
        catch (Exception e) { _edits.Fault(e, ref EditsStatus); velocityWrittenSum = default; moveStamp = 0; return false; }
    }

    /// <summary>A grid's railed re-entry, the fuller state (OrbitalApi.TryGetEntryState2; else TryGetEntryState with no
    /// temperature and no braking): the travel into the air (world, m/s), density (kg/m3), the air's temperature (K; 0
    /// unknown) and the frame's braking acceleration (world, m/s2). False: none.</summary>
    public static bool TryGetEntryState2(Keen.VRage.DCS.Components.Entity grid, out Vector3 travelWorld, out float density, out float temperature, out Vector3 brakingWorld)
    {
        travelWorld = brakingWorld = default; density = temperature = 0f;
        if (grid == null) return false;
        var fn = _entry2.Get(ref Entry2Status);
        if (fn == null)
            return TryGetEntryState(grid, out travelWorld, out density);   // (an older Orbital mod: no temperature, no braking)
        try { if (!fn(grid, out travelWorld, out density, out temperature, out brakingWorld)) return false; }
        catch (Exception e) { _entry2.Fault(e, ref Entry2Status); travelWorld = brakingWorld = default; density = temperature = 0f; return false; }
        if (!float.IsFinite(brakingWorld.X + brakingWorld.Y + brakingWorld.Z)) brakingWorld = default;
        if (!(temperature > 0f) || !float.IsFinite(temperature)) temperature = 0f;
        if (!float.IsFinite(density) || density < 0f) density = 0f;
        // (braked with no density - the cap rule alone, Orbital's drag off: still a load on the structure, no heating)
        return (density > 0f || brakingWorld.LengthSquared() > 0f) && float.IsFinite(travelWorld.X + travelWorld.Y + travelWorld.Z) && travelWorld.LengthSquared() > 1f;
    }

    /// <summary>A grid's real re-entry state, the first member (OrbitalApi.TryGetEntryState): the true air velocity (world,
    /// the travel) and density. False: none. AeroEntryFx.EntryStateAt's binding (the scorch's "is re-entering" test) and
    /// TryGetEntryState2's fallback; its status is AeroEntryFx.EntryStateStatus.</summary>
    internal static bool TryGetEntryState(Keen.VRage.DCS.Components.Entity grid, out Vector3 airWorld, out float density)
    {
        airWorld = default; density = 0f;
        if (grid == null) return false;
        var fn = _entry.Get(ref AeroEntryFx.EntryStateStatus);
        if (fn == null) return false;
        try { if (!fn(grid, out airWorld, out density)) return false; }
        catch (Exception e) { _entry.Fault(e, ref AeroEntryFx.EntryStateStatus); airWorld = default; density = 0f; return false; }
        return density > 0f && float.IsFinite(density) && float.IsFinite(airWorld.X + airWorld.Y + airWorld.Z) && airWorld.LengthSquared() > 1f;
    }
}
