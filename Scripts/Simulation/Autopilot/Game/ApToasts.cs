#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Threading;
using Keen.Game2.Simulation.GameSystems.UserMessages;
using Keen.VRage.Core;
using Keen.VRage.Library.Localization;
namespace AeroMod;

/// <summary>
/// THE AUTOPILOT'S WORDS to the seated pilot (client): what it engaged, why it let go or refused (ApNotes, said by the server's
/// grid step only while someone is seated) - a toast, keyed so a new line replaces the last. The autopilot itself is controlled
/// from its block's panel (AeroAutopilotBlockDetailModel; the keys it had before were removed on 2026-10-19 - the user: "control it
/// through a SINGLE BLOCK, not in-game keys").
/// </summary>
public static class ApToasts
{
    /// <summary>Diagnostics (harness aeroget): the last line said.</summary>
    public static string Status = "-";
    static long _nextFrame;
    static int _busy;

    /// <summary>Client, the client's session tick (Runtime/AeroClientHost.cs, beside QuickLaunch.ClientTick): one caller a frame claims it.</summary>
    internal static void ClientTick(Entity anyClient)
    {
        if (ApNotes.Pending == null) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp(), f = System.Diagnostics.Stopwatch.Frequency;
        long due = Interlocked.Read(ref _nextFrame);
        if (now < due || Interlocked.CompareExchange(ref _nextFrame, now + f / 30, due) != due) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            var say = ApNotes.Pending;
            if (say == null) return;
            ApNotes.Pending = null; Status = say;
            Toast(anyClient?.GetSession(), say);
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    static void Toast(Session session, string text)
    {
        try
        {
            session?.TryGet<IUserMessages>()?.DisplayMessage(ModToast.Literal("Autopilot", text, 3, "aero.autopilot"));   // (keyed: a new line replaces the last)
        }
        catch { }
    }

    /// <summary>A world gone: the last world's words dropped.</summary>
    internal static void ResetWorld() { ApNotes.Pending = null; Status = "-"; }
}
