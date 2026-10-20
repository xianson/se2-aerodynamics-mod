#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using Keen.VRage.Core.Systems;
using Keen.VRage.DCS.Annotations;

namespace AeroMod;

/// <summary>
/// THE CLIENT'S SESSION TICK (2026-10-19): injected into the client's render camera prefab (the one entity with the game's
/// CameraComponent - ClientPlayersSessionConfiguration.RenderCamera; the client scene always has it: on foot, seated, in
/// spectator, with no grid anywhere). Its job runs the client's session-level work every frame (AeroClientHost.Frame).
/// Before, that work ran from each client grid's effects job (AeroEntryFxComponent.EntryJob): with no client grid - a fresh
/// world, on foot, nothing nearby - the settings menu never appeared (unless the Orbital Mod ticked its shared tab), the
/// settings files were not polled, a creak or a Mach readout of a grid gone was left as it was.
/// </summary>
public partial class AeroClientHostComponent : Component, IInSceneListener
{
    void IInSceneListener.OnAddedToScene() { }
    void IInSceneListener.OnBeforeRemovedFromScene() { }

    [After(typeof(RenderSubmissionBegin))]
    private class OnAeroClientFrame : JobGroup;

    [OnAeroClientFrame]
    [MustHave(typeof(AeroClientHostComponent))]
    private static void ClientFrameJob(AeroClientHostComponent c) => AeroClientHost.Frame(c.Entity, fromHost: true);
}

/// <summary>
/// The client's session-level work, once a frame, from the render camera (AeroClientHostComponent) - not from a grid:
///  - the settings: SawClient, the files' poll (~2 s), the Options screen's Mods tab (Ui/SettingsTab.cs: one caller per 250 ms);
///  - the HUD's Mach / autopilot readout (MachHud), creative quick launch's key and answer (QuickLaunch.ClientTick), the
///    autopilot's toasts (ApToasts), the airframe's creaks and cockpit shake (StructureFeel) - each the LOCAL player's, found
///    through the session (each has its own claim: one caller per its period).
/// Per-grid work stays in the grid's effects job (the flight field, scorch, torn-edge smoke, quick launch's cockpit turn).
/// Fallback: a client grid's effects job calls Frame too (fromHost: false); it does the work only while the host has not
/// ticked for a second (the camera prefab not injected - a game update renamed something): the old behaviour, never twice.
/// </summary>
public static class AeroClientHost
{
    /// <summary>Diagnostics (aeroget): frames run by the host / by a grid's fallback; the in-process server check.</summary>
    public static long HostFrames, FallbackFrames;
    public static string Status = "not ticked";
    static long _hostAt;
    static object _session;

    /// <summary>A client frame: `any` is a client-scene entity (only its session is used).</summary>
    internal static void Frame(Entity any, bool fromHost)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (fromHost) System.Threading.Volatile.Write(ref _hostAt, now);
        else if (now - System.Threading.Volatile.Read(ref _hostAt) < System.Diagnostics.Stopwatch.Frequency) return;   // (the host runs it)
        if (any == null) return;
        try
        {
            var session = any.GetSession();
            if (session == null) return;
            if (!ReferenceEquals(session, _session)) { _session = session; NewSession(session, fromHost); }
            if (fromHost) HostFrames++; else FallbackFrames++;
            if (!AeroSettings.SawClient) AeroSettings.SawClient = true;
            AeroSettings.Poll();   // (the settings files, every ~2 s)
            if (SettingsTab.Claim()) SettingsTab.Ticked(session);   // (the Options screen's Mods tab, Ui/SettingsTab.cs: one per 250 ms)
            MachHud.Tick(any);   // (the speed readout's Mach: not an effect - runs with the effects off too)
            QuickLaunch.ClientTick(any);   // (creative quick launch's key and answer - Launch/QuickLaunch.cs)
            EnemySpawn.ClientTick(any);   // (the test spawn keys, the blueprint's load and the answer - Launch/EnemySpawn.cs)
            ApToasts.ClientTick(any);   // (the autopilot's words to the pilot - Autopilot/Game/ApToasts.cs)
            StructureFeel.Tick(any);   // (the airframe's creaks and cockpit shake - Structure/Fx/StructureFeel.cs; its own switches)
        }
        catch (Exception e)
        {
            // (an exception escaping into the engine's job is fatal; this is not a path that throws by design)
            string why = "client frame failed: " + e.GetType().Name + ": " + e.Message;
            if (why != Status) { Status = why; Log.Default?.Warning("[AERO] " + why); }
        }
    }

    /// <summary>A new client session (once each): whether the server runs in this process (single player / the host) - the
    /// client's session holds the in-process transport then (VRage.Multiplayer InProcessClientPeerTransportSessionComponent;
    /// a remote client holds the out-of-process one). Seen: the world's settings may be edited here (AeroSettings.SawServer)
    /// with no server grid or planet carrier yet - a planetless world with no grid had them read-only.</summary>
    static void NewSession(Session session, bool fromHost)
    {
        bool inProcess = false;
        foreach (var c in session.SessionComponents.Components)
            if (c?.GetType().FullName == "Keen.VRage.Multiplayer.Transport.InProcess.InProcessClientPeerTransportSessionComponent") { inProcess = true; break; }
        if (inProcess && !AeroSettings.SawServer) AeroSettings.SawServer = true;
        Status = $"session: {(fromHost ? "camera host" : "grid fallback")}, server {(inProcess ? "in this process" : "remote (or not seen)")}";
        Log.Default?.Info("[AERO] client " + Status);
    }
}
