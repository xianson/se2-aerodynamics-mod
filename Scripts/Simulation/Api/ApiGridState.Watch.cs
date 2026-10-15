#pragma warning disable
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
    internal const int RecFlight = 0, RecHull = 1, RecStruct = 2, RecFaces = 3, RecCount = 4;

    /// <summary>Each record published while Stopwatch.GetTimestamp() is under its entry (0: never asked for).</summary>
    public readonly long[] Until = new long[RecCount];

    /// <summary>Push a record's window on to <paramref name="until"/> (only later). Any thread: a racing push may lose to an
    /// earlier one by a read's worth - the next read pushes again.</summary>
    public void Push(int rec, long until) { if (until > Until[rec]) Until[rec] = until; }

    /// <summary>The record is watched at <paramref name="now"/> (Stopwatch ticks).</summary>
    public bool On(int rec, long now) => now < Until[rec];

    /// <summary>The simulation tick has API work: its tick record or the hull's faces watched.</summary>
    public bool TickWatched(long now) => now < Until[RecFlight] || now < Until[RecHull];
}
