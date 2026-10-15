#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// The API's watch windows, one per record (engine-free: Tests/ApiTests). A read pushes on only the record it reads
/// (AeroApi.Watch); its writer publishes it only while its window is open. 2026-10-15 review: one window served the flight
/// record and the structure record - a HUD reading only GetFlightState kept the O(blocks) structure publish running every
/// evaluation, and a structure reader turned on the per-tick flight, surface and thruster copy.
/// </summary>
internal sealed partial class ApiGridState
{
    /// <summary>The records: the tick record (flight state, control surfaces, thruster forces - the simulation tick), the
    /// hull's faces (GetSurface - the simulation tick), the structure's block state (the background evaluation), the hull's
    /// heat faces (the same evaluation).</summary>
    internal const int RecFlight = 0, RecHull = 1, RecStruct = 2, RecFaces = 3, RecExt = 4, RecCount = 5;
    // (RecExt: another heat model's block temperatures, AeroApi.SetBlockTemperatures - not a published record, but held
    //  ExternalHeatHold s after the last set, then released like one)

    /// <summary>Each record published while Stopwatch.GetTimestamp() is under its entry (0: never asked for).</summary>
    public readonly long[] Until = new long[RecCount];

    /// <summary>Push a record's window on to <paramref name="until"/> (only later). Any thread: a racing push may lose to an
    /// earlier one by a read's worth - the next read pushes again.</summary>
    public void Push(int rec, long until) { if (until > Until[rec]) Until[rec] = until; }

    /// <summary>The record is watched at <paramref name="now"/> (Stopwatch ticks).</summary>
    public bool On(int rec, long now) => now < Until[rec];

    /// <summary>The simulation tick has API work: its tick record or the hull's faces watched, or a record's arrays to drop.</summary>
    public bool TickWatched(long now) => now < Until[RecFlight] || now < Until[RecHull] || AnyHeld;

    // ── releasing (2026-10-15 review: the arrays only grew, doubling, double-buffered, and were never released - one
    //    GetSurface or GetHeatFaces on a 10^5-face ship left ~12-15 MB of large-object arrays for the life of the grid) ──

    /// <summary>How long (s) after a record's window closed (WatchSeconds after its last read) its arrays are dropped.</summary>
    internal static float ReleaseSeconds = 5f;

    /// <summary>The record has arrays published (its writer sets it under Lock; Release clears it).</summary>
    public readonly bool[] Held = new bool[RecCount];

    public bool AnyHeld { get { for (int r = 0; r < RecCount; r++) if (Held[r]) return true; return false; } }

    /// <summary>The record's arrays are to be dropped at <paramref name="now"/>: published, and its window closed
    /// ReleaseSeconds ago. Test it again under Lock before Release (a read may have pushed the window since).</summary>
    public bool Due(int rec, long now) => Held[rec] && now - Until[rec] >= (long)(ReleaseSeconds * System.Diagnostics.Stopwatch.Frequency);
}
