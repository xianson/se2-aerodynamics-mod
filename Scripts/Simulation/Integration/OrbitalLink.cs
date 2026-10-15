#pragma warning disable
using System;
using Keen.VRage.Core;

namespace AeroMod;

/// <summary>
/// THE ORBITAL MOD'S PER-GRID ANSWERS, bound once (2026-10-14; mods compile apart: OrbitalMod.OrbitalApi found by type name
/// across the loaded assemblies - the last copy wins - each member by its name AND parameter types, bound as a typed
/// delegate; looked up again every RetrySeconds while missing; a member that throws is dropped and looked up again):
///  - TryGetGridEdits (StructureContacts): what Orbital WROTE into a grid's physics velocity (a running world sum, m/s) and
///    a stamp changed by every teleport or turn it made - its writes land after the physics step, so between two of the
///    contact balance's reads, and are no force on the ship;
///  - TryGetEntryState2 (StructureServer, the railed entry): the travel through the air, density, the band's temperature
///    and the frame's braking acceleration (world). Older Orbital mods: TryGetEntryState (AeroEntryFx.EntryStateAt).
/// Never called under a lock of ours (the lock rule, docs/API.md): Orbital's answers take small locks of its own.
/// </summary>
public static class OrbitalLink
{
    public static float RetrySeconds = 5f;
    delegate bool GridEditsFn(Keen.VRage.DCS.Components.Entity grid, out Vector3D velocityWrittenSum, out long moveStamp);
    delegate bool EntryState2Fn(Keen.VRage.DCS.Components.Entity grid, out Vector3 travelWorld, out float density, out float temperature, out Vector3 brakingAccelWorld);
    static GridEditsFn _edits; static long _editsRetryAt;
    static EntryState2Fn _entry2; static long _entry2RetryAt;
    /// <summary>Diagnostics: why each is (not) bound.</summary>
    public static string EditsStatus = "not looked up", Entry2Status = "not looked up";

    /// <summary>OrbitalApi's method of this name and parameter types (the last loaded copy), or null.</summary>
    static System.Reflection.MethodInfo Find(string name, Type[] types)
    {
        System.Reflection.MethodInfo mi = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t; try { t = asm.GetType("OrbitalMod.OrbitalApi"); } catch { continue; }
            var m = t?.GetMethod(name, types);
            if (m != null) mi = m;
        }
        return mi;
    }

    static bool Due(ref long retryAt)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now < System.Threading.Volatile.Read(ref retryAt)) return false;
        System.Threading.Volatile.Write(ref retryAt, now + (long)(RetrySeconds * System.Diagnostics.Stopwatch.Frequency));
        return true;
    }

    /// <summary>OrbitalApi.TryGetGridEdits: the sum of the velocity changes Orbital wrote into the grid (world, m/s) and its
    /// move stamp. False: no Orbital mod (or older), or it never wrote into this grid.</summary>
    public static bool TryGetGridEdits(Keen.VRage.DCS.Components.Entity grid, out Vector3D velocityWrittenSum, out long moveStamp)
    {
        velocityWrittenSum = default; moveStamp = 0;
        if (grid == null) return false;
        var fn = System.Threading.Volatile.Read(ref _edits);
        if (fn == null)
        {
            if (!Due(ref _editsRetryAt)) return false;
            var mi = Find("TryGetGridEdits", new[] { typeof(Keen.VRage.DCS.Components.Entity), typeof(Vector3D).MakeByRefType(), typeof(long).MakeByRefType() });
            if (mi == null) { EditsStatus = "OrbitalApi.TryGetGridEdits not found (no Orbital mod, or an older one)"; return false; }
            fn = (GridEditsFn)Delegate.CreateDelegate(typeof(GridEditsFn), mi, false);
            if (fn == null) { EditsStatus = "OrbitalApi.TryGetGridEdits: unexpected signature"; return false; }
            System.Threading.Volatile.Write(ref _edits, fn);
            EditsStatus = "bound to OrbitalApi.TryGetGridEdits";
        }
        try { return fn(grid, out velocityWrittenSum, out moveStamp) && double.IsFinite(velocityWrittenSum.X + velocityWrittenSum.Y + velocityWrittenSum.Z); }
        catch (Exception e) { EditsStatus = "OrbitalApi.TryGetGridEdits threw: " + e.GetType().Name; System.Threading.Volatile.Write(ref _edits, null); velocityWrittenSum = default; moveStamp = 0; return false; }
    }

    /// <summary>A grid's railed re-entry, the fuller state (OrbitalApi.TryGetEntryState2; else TryGetEntryState with no
    /// temperature and no braking): the travel into the air (world, m/s), density (kg/m3), the air's temperature (K; 0
    /// unknown) and the frame's braking acceleration (world, m/s2). False: none.</summary>
    public static bool TryGetEntryState2(Keen.VRage.DCS.Components.Entity grid, out Vector3 travelWorld, out float density, out float temperature, out Vector3 brakingWorld)
    {
        travelWorld = brakingWorld = default; density = temperature = 0f;
        if (grid == null) return false;
        var fn = System.Threading.Volatile.Read(ref _entry2);
        if (fn == null && Due(ref _entry2RetryAt))
        {
            var fr = typeof(float).MakeByRefType(); var vr = typeof(Vector3).MakeByRefType();
            var mi = Find("TryGetEntryState2", new[] { typeof(Keen.VRage.DCS.Components.Entity), vr, fr, fr, vr });
            if (mi == null) Entry2Status = "OrbitalApi.TryGetEntryState2 not found (an older Orbital mod: TryGetEntryState)";
            else
            {
                fn = (EntryState2Fn)Delegate.CreateDelegate(typeof(EntryState2Fn), mi, false);
                if (fn == null) Entry2Status = "OrbitalApi.TryGetEntryState2: unexpected signature";
                else { System.Threading.Volatile.Write(ref _entry2, fn); Entry2Status = "bound to OrbitalApi.TryGetEntryState2"; }
            }
        }
        if (fn == null)
            return AeroEntryFx.EntryStateAt(grid, out travelWorld, out density);   // (the older member: no temperature, no braking)
        try { if (!fn(grid, out travelWorld, out density, out temperature, out brakingWorld)) return false; }
        catch (Exception e) { Entry2Status = "OrbitalApi.TryGetEntryState2 threw: " + e.GetType().Name; System.Threading.Volatile.Write(ref _entry2, null); travelWorld = brakingWorld = default; density = temperature = 0f; return false; }
        if (!float.IsFinite(brakingWorld.X + brakingWorld.Y + brakingWorld.Z)) brakingWorld = default;
        if (!(temperature > 0f) || !float.IsFinite(temperature)) temperature = 0f;
        if (!float.IsFinite(density) || density < 0f) density = 0f;
        // (braked with no density - the cap rule alone, Orbital's drag off: still a load on the structure, no heating)
        return (density > 0f || brakingWorld.LengthSquared() > 0f) && float.IsFinite(travelWorld.X + travelWorld.Y + travelWorld.Z) && travelWorld.LengthSquared() > 1f;
    }
}
