#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Threading;
using Keen.Game2.Simulation.GameSystems.UserMessages;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.VRage.Core;
using Keen.VRage.Core.Input;
using Keen.VRage.Core.Platform;
using Keen.VRage.DCS.Annotations;
using Keen.VRage.Library.Localization;
namespace AeroMod;

/// <summary>
/// THE AUTOPILOT'S KEYS (client) and their ask (server) - quick launch's pattern (Launch/QuickLaunch*.cs). Seated, the hover hold
/// key (Ctrl+H by default: Autopilot.HoverKey / KeyModifier) toggles hover hold - the place and the height held (over the ground:
/// Autopilot.HoldAboveGround) - and the height hold key (Ctrl+J: Autopilot.HeightKey) toggles the height hold alone. One ask per
/// press (QuickLaunchRules.KeyEdge: up to down, never while held), not while a screen takes the keyboard. The ask carries only who
/// asks (the local player's identity) and which key: the server finds its own character, the seat it pilots and that seat's grid,
/// and hands the grid the ask (AeroGridComponent.ApAsk) - its next attitude step engages or lets go with its flight director. The
/// autopilot's words (ApNotes) come back as a toast here.
/// </summary>
public static class ApKeys
{
    internal sealed class Ask { public Keen.Game2.Simulation.GameSystems.Ownership.IdentityId Who; public bool HasWho; public long At; public int Goal; }
    static Ask _ask;
    internal static bool Asked => Volatile.Read(ref _ask) != null;
    internal static Ask TakeAsk() => Interlocked.Exchange(ref _ask, null);
    /// <summary>Diagnostics (harness aeroget): the last state and the counts.</summary>
    public static string Status = "-";
    public static int Asks, Served;

    static long _nextFrame, _deviceAt;
    static int _busy;
    static IInputDevice _keyboard;
    static readonly QuickLaunchRules.KeyEdge _hover = new QuickLaunchRules.KeyEdge(), _height = new QuickLaunchRules.KeyEdge();
    static DigitalInput[] _letters;
    static DigitalInput[] Letters() => _letters ??= new[]
    {
        KeyboardInputs.A, KeyboardInputs.B, KeyboardInputs.C, KeyboardInputs.D, KeyboardInputs.E, KeyboardInputs.F, KeyboardInputs.G,
        KeyboardInputs.H, KeyboardInputs.I, KeyboardInputs.J, KeyboardInputs.K, KeyboardInputs.L, KeyboardInputs.M, KeyboardInputs.N,
        KeyboardInputs.O, KeyboardInputs.P, KeyboardInputs.Q, KeyboardInputs.R, KeyboardInputs.S, KeyboardInputs.T, KeyboardInputs.U,
        KeyboardInputs.V, KeyboardInputs.W, KeyboardInputs.X, KeyboardInputs.Y, KeyboardInputs.Z,
    };

    /// <summary>Client, the effects jobs (AeroEntryFxComponent.EntryJob, beside QuickLaunch.ClientTick): one job a frame claims it.</summary>
    internal static void ClientTick(Entity anyGrid)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp(), f = System.Diagnostics.Stopwatch.Frequency;
        long due = Interlocked.Read(ref _nextFrame);
        if (now < due || Interlocked.CompareExchange(ref _nextFrame, now + f / 250, due) != due) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try { Tick(anyGrid, now, f); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    static void Tick(Entity anyGrid, long now, long f)
    {
        var session = anyGrid?.GetSession();
        var say = ApNotes.Pending;
        if (say != null) { ApNotes.Pending = null; Status = say; Toast(session, say); }
        bool hover = Pressed(Autopilot.HoverKey, _hover, now, f), height = !hover && Pressed(Autopilot.HeightKey, _height, now, f);
        if (!hover && !height) return;
        if (QuickLaunch.TopScreenNeedsInput(session)) return;   // (not while typing)
        if (!QuickLaunch.LocalPilot(session, out var who, out bool hasWho)) return;   // (on foot: the letter is someone else's)
        Volatile.Write(ref _ask, new Ask { Who = who, HasWho = hasWho, At = now, Goal = AeroGridComponent.ApAskKey + (int)(hover ? ApGoal.Hover : ApGoal.Height) });
        Interlocked.Increment(ref Asks);
        Status = hover ? "asked: hover hold" : "asked: height hold";
    }

    /// <summary>The key went down this read (seen up before), the modifier held. Off / no device: no press.</summary>
    static bool Pressed(int key, QuickLaunchRules.KeyEdge edge, long now, long f)
    {
        var keys = Letters();
        if (key < 1 || key > keys.Length) { edge.Disarm(); return false; }
        if (_keyboard == null)
        {
            edge.Disarm();
            if (now < _deviceAt) return false;
            _deviceAt = now + 2 * f;
            try
            {
                var input = VRageCore.Instance?.Engine?.Single<IPlatformInput>();
                if (input != null)
                    foreach (var dev in input.ConnectedDevices)
                        if (dev.Class.GenericClasses.Contains(GenericDeviceClass.Keyboard)) { _keyboard = dev; break; }
            }
            catch { _keyboard = null; }
            return false;
        }
        bool down, held = false;
        try
        {
            down = _keyboard.GetDigitalState(keys[key - 1]);
            if (down) held = QuickLaunchRules.ModifierHeld(Autopilot.KeyModifier, _keyboard.GetDigitalState(KeyboardInputs.Control),
                _keyboard.GetDigitalState(KeyboardInputs.Shift), _keyboard.GetDigitalState(KeyboardInputs.Alt));
        }
        catch { _keyboard = null; edge.Disarm(); return false; }
        return edge.Step(down, held);
    }

    static void Toast(Session session, string text)
    {
        try
        {
            session?.TryGet<IUserMessages>()?.DisplayMessage(new UserMessage
            {
                TitleId = LocKey.FromString("Autopilot"), ContentId = LocKey.FromString(text),
                Type = UserMessageType.Game, TextTimeout = TimeSpan.FromSeconds(3),
                MessageKey = StringId.Get("aero.autopilot"),   // (keyed: a new line replaces the last)
            });
        }
        catch { }
    }

    /// <summary>A world gone: the last world's ask and words dropped, the keys need seeing up again.</summary>
    internal static void ResetWorld() { Volatile.Write(ref _ask, null); ApNotes.Pending = null; Status = "-"; _hover.Disarm(); _height.Disarm(); }

    // ── the server's side: the ask to its own grid ──
    static readonly System.Collections.Generic.List<Entity> _chars = new();

    /// <summary>Server (a planet's settings carrier, before the simulation - AeroWorldSettingsComponent.OnAutopilotAsk): the ask
    /// taken once and only while fresh; the asking player's character here, the seat it pilots, that seat's grid's autopilot.</summary>
    internal static void ServerStep(Session session)
    {
        var ask = TakeAsk();
        if (ask == null || session == null) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!QuickLaunchRules.Fresh(ask.At, now, (long)(System.Diagnostics.Stopwatch.Frequency * QuickLaunchRules.AskMaxAgeS))) { Status = "dropped: the ask waited too long"; return; }
        if (!Autopilot.Enabled) { ApNotes.Say(ApText.Refused(ApReason.NotAllowed)); return; }
        Entity ch = null;
        lock (_chars)
        {
            _chars.Clear();
            if (session.TryFillAliveCharacters(_chars))
            {
                if (ask.HasWho)
                    foreach (var c in _chars)
                    {
                        var own = c.TryGet<Keen.Game2.Simulation.GameSystems.Ownership.OwnershipComponent>();
                        if (own != null && own.Owner.Equals(ask.Who)) { ch = c; break; }
                    }
                if (ch == null && _chars.Count == 1) ch = _chars[0];
            }
            _chars.Clear();
        }
        Entity seat = null;
        if (ch != null)
            foreach (var se in session.GetEntitiesOfType<SeatComponent>())
                if ((se.TryGet<SeatComponent>() as ISeat)?.Pilot == ch) { seat = se; break; }
        var grid = seat?.TryGet<CubeBlockComponent>()?.Grid?.Entity;
        var aero = grid != null && GridPhysics.Alive(grid.Data) ? grid.TryGet<AeroGridComponent>() : null;
        if (aero == null || !aero.IsServerScene) { ApNotes.Say("Autopilot: sit in a cockpit first"); Status = "refused: not seated"; return; }
        Volatile.Write(ref aero.ApAsk, ask.Goal);
        Interlocked.Increment(ref Served);
        Status = $"served: '{grid.DebugName}'";
    }
}

public partial class AeroWorldSettingsComponent
{
    [Before(typeof(Keen.VRage.Core.Systems.Simulation))]
    private class OnAutopilotAsk : JobGroup;

    [OnAutopilotAsk]
    [MustHave(typeof(AeroWorldSettingsComponent))]
    private static void AutopilotAskJob(AeroWorldSettingsComponent c)
    {
        if (!ApKeys.Asked) return;
        if (c.Data.Scene?.DebugName == "Client") return;   // (the carrier is the server planets'; never answer from a client copy)
        ApKeys.ServerStep(c.Entity?.GetSession());
    }
}
