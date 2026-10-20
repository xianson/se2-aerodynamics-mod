#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Threading;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Simulation.GameSystems.UserMessages;
using Keen.VRage.Core;
using Keen.VRage.Core.Input;
using Keen.VRage.Core.Platform;
using Keen.VRage.Library.Localization;
namespace AeroMod;

/// <summary>
/// QUICK LAUNCH (creative only; 2026-10-18 - the Orbital Mod's 1.0.1 feature, moved here: the user, "ctrl+T should be in aero
/// creative"). Seated in a cockpit, the key (Ctrl+T by default: QuickLaunch.Key / QuickLaunch.Modifier, client settings)
/// asks the server to lift your ship (everything joined to it with it) to QuickLaunch.AltitudeM over the ground under it,
/// turned level on its heading and flying along the cockpit's nose at QuickLaunch.SpeedMps (0: cruise - aero's top speed at
/// 3/4 throttle; 150 m/s when the ship has none). The server decides (QuickLaunch.Server.cs, QuickLaunchRules): the world
/// allows it (QuickLaunch.Creative), the pilot is in creative, seated - the server's own seat -, the ship dynamic and free of
/// stations, near a planet. The Orbital Mod, when loaded, moves it (OrbitalApi.TryQuickMove: its safe move and space border);
/// else aero does (QuickLaunchMove). The answer comes back as a toast (PendingSay: written by the server, said here).
///
/// Client side, this file: the key - one ask per press (QuickLaunchRules.KeyEdge: up to down, never while held, never a down
/// not seen up first), seated, not while typing (a screen that takes the keyboard), one ask a second, an ask good for 1 s -
/// and the answer. The ask carries only who asks (the local player's identity: the server finds its character, the seat it pilots
/// and that seat's grid itself - no client entity is read on the server, no server entity here). After a move aero made
/// itself, the cockpit's target attitude (TargetControlData, the client grid's) turns with the ship (TurnTarget), or the
/// gyros would swing it back; the Orbital Mod turns its own moves' targets.
/// Harness: AeroApi.RequestQuickLaunch (the Orbital Mod's DevHarness key "quicklaunch") - the player's path, the key's
/// checks skipped. Status / Asks / Launches / Refusals: aeroget.
/// </summary>
public static partial class QuickLaunch
{
    // ── the settings (AeroSettings: QuickLaunch.*) ──
    /// <summary>World: quick launch allowed in creative (the host can turn it off). Survival never has it.</summary>
    public static bool Creative = true;
    /// <summary>World: m over the ground (or, with no ground found, over where the ship is).</summary>
    public static double AltitudeM = 1000;
    /// <summary>World: the launch speed along the nose (m/s); 0: the ship's cruise speed.</summary>
    public static double SpeedMps = 0;
    /// <summary>Client: the key - 0 off, 1..26 the letters A..Z.</summary>
    public static int Key = 20;
    /// <summary>Client: the key's modifier - 0 none, 1 Ctrl, 2 Shift, 3 Alt.</summary>
    public static int Modifier = 1;

    /// <summary>The server's answer, said by the next client tick (null: none).</summary>
    public static volatile string PendingSay;
    /// <summary>Diagnostics (harness aeroget): the last answer or state, and the counts.</summary>
    public static string Status = "-";
    public static int Asks, Launches, Refusals;

    /// <summary>A pending ask: who asks (the client's local player identity; HasWho false - not known: the server takes the
    /// only character if there is one). Swapped whole (Interlocked): the server takes it at most once. At: when it was made
    /// (Stopwatch ticks) - the server drops one older than QuickLaunchRules.AskMaxAgeS.</summary>
    internal sealed class Ask { public Keen.Game2.Simulation.GameSystems.Ownership.IdentityId Who; public bool HasWho; public long At; }
    static Ask _ask;
    static long _devAsk;
    /// <summary>The server's step: something asked (a read, no lock: QuickLaunch.Server.cs takes it).</summary>
    internal static bool Asked => Volatile.Read(ref _ask) != null;
    internal static Ask TakeAsk() => Interlocked.Exchange(ref _ask, null);

    /// <summary>The harness's ask (AeroApi.RequestQuickLaunch): the next client tick asks as the key does, its checks skipped -
    /// once, and only within QuickLaunchRules.AskMaxAgeS (2026-10-18: a flag no client tick took - no client grid's effects
    /// job running, on foot away from ships - would have stayed set and launched the next ship you sat in).</summary>
    internal static void DevAsk() => Interlocked.Exchange(ref _devAsk, Math.Max(1L, System.Diagnostics.Stopwatch.GetTimestamp()));

    static long _nextFrame, _lastAsk = long.MinValue / 2, _deviceAt;
    static int _busy;
    static IInputDevice _keyboard;
    static readonly QuickLaunchRules.KeyEdge _edge = new QuickLaunchRules.KeyEdge();
    static DigitalInput[] _keys;
    static System.Reflection.MethodInfo _topInput;
    static Type _sharedType;

    /// <summary>The letter keys, in QuickLaunch.Key's order (1 = A).</summary>
    static DigitalInput[] Keys() => _keys ??= new[]
    {
        KeyboardInputs.A, KeyboardInputs.B, KeyboardInputs.C, KeyboardInputs.D, KeyboardInputs.E, KeyboardInputs.F, KeyboardInputs.G,
        KeyboardInputs.H, KeyboardInputs.I, KeyboardInputs.J, KeyboardInputs.K, KeyboardInputs.L, KeyboardInputs.M, KeyboardInputs.N,
        KeyboardInputs.O, KeyboardInputs.P, KeyboardInputs.Q, KeyboardInputs.R, KeyboardInputs.S, KeyboardInputs.T, KeyboardInputs.U,
        KeyboardInputs.V, KeyboardInputs.W, KeyboardInputs.X, KeyboardInputs.Y, KeyboardInputs.Z,
    };

    /// <summary>Client, the client's session tick (Runtime/AeroClientHost.cs; a grid's effects job only as its fallback): one
    /// caller a frame claims the tick, and only one runs it at a time (_busy, 2026-10-18: the time claim alone let a second job in while a slow first
    /// one - a toast, the screen lookup - was still at the key: one press read twice). Cheap when nothing is pressed: one key
    /// state read.</summary>
    internal static void ClientTick(Entity anyClient)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp(), f = System.Diagnostics.Stopwatch.Frequency;
        long due = Interlocked.Read(ref _nextFrame);
        if (now < due || Interlocked.CompareExchange(ref _nextFrame, now + f / 250, due) != due) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try { Tick(anyClient, now, f); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    static void Tick(Entity anyClient, long now, long f)
    {
        var session = anyClient?.GetSession();
        var say = PendingSay;
        if (say != null) { PendingSay = null; Status = say; Toast(session, say); }
        long devAt = Interlocked.Exchange(ref _devAsk, 0);
        bool dev = QuickLaunchRules.Fresh(devAt, now, (long)(f * QuickLaunchRules.AskMaxAgeS));
        if (devAt != 0 && !dev) Status = "harness ask dropped: no client tick took it within 1 s";
        // (the key read every tick, a harness ask or not: its press is its own edge, never one left over for later)
        bool pressed = BindingPressed(now, f);
        if (!dev && !pressed) return;
        if (!dev && TopScreenNeedsInput(session)) return;   // (not while typing: a dialog, a terminal, the chat, a menu)
        if (now - _lastAsk < f) return;   // (one ask a second: a double tap moves the ship once)
        if (!LocalPilot(session, out var who, out bool hasWho))
        {
            // (on foot: the key is any other letter - nothing said; the harness hears why)
            if (dev) { Status = QuickLaunchRules.Say(QuickLaunchRules.Verdict.NotSeated); Toast(session, Status); }
            return;
        }
        _lastAsk = now;
        Volatile.Write(ref _ask, new Ask { Who = who, HasWho = hasWho, At = now });
        Interlocked.Increment(ref Asks);
        Status = dev ? "asked (harness)" : "asked";
    }

    /// <summary>The binding went down this read: its key pressed (seen up before - QuickLaunchRules.KeyEdge), its modifier
    /// held. Off, no device, a device just found or lost: no press, and the next one needs the key seen up first.</summary>
    static bool BindingPressed(long now, long f)
    {
        int k = Key;
        var keys = Keys();
        if (k < 1 || k > keys.Length) { _edge.Disarm(); return false; }
        if (_keyboard == null)
        {
            _edge.Disarm();
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
            return false;   // (a device just found: its reads arm the edge from the next tick)
        }
        bool down, held = false;
        try
        {
            down = _keyboard.GetDigitalState(keys[k - 1]);
            if (down)
                held = QuickLaunchRules.ModifierHeld(Modifier, _keyboard.GetDigitalState(KeyboardInputs.Control),
                    _keyboard.GetDigitalState(KeyboardInputs.Shift), _keyboard.GetDigitalState(KeyboardInputs.Alt));
        }
        catch { _keyboard = null; _edge.Disarm(); return false; }   // (the device went: looked up again in 2 s)
        return _edge.Step(down, held);
    }

    /// <summary>A screen takes the keyboard (SharedUIComponent.TryGetTopScreenNeedingInput, public, by reflection - the type
    /// is the client UI's: docs/REFLECTION_AND_WHITELIST.md). Asked only when the binding went down. Unknown: false.</summary>
    internal static bool TopScreenNeedsInput(Session session)
    {
        try
        {
            var shared = AvaloniaReflect.SharedUi(session);
            if (shared == null) return false;
            if (!ReferenceEquals(_sharedType, shared.GetType())) { _sharedType = shared.GetType(); _topInput = _sharedType.GetMethod("TryGetTopScreenNeedingInput", Type.EmptyTypes); }
            return _topInput?.Invoke(shared, null) != null;
        }
        catch { return false; }
    }

    /// <summary>The local player is seated in a grid (its top observable entity is a block on one: MachHud's walk), and its
    /// identity (what the server's character is owned by).</summary>
    static bool LocalPilot(Session session, out Keen.Game2.Simulation.GameSystems.Ownership.IdentityId who, out bool hasWho)
    {
        who = default; hasWho = false;
        try
        {
            var players = session?.SessionComponents.TryGet<ClientPlayersSessionComponent>();
            var seat = players?.LocalPlayerController?.GetTopObservableEntity();
            var grid = SeatGrid(seat);
            if (grid == null) return false;
            who = players.LocalPlayerIdentity;
            hasWho = who.IsValid();
            _seatGrid = grid;
            return true;
        }
        catch { return false; }
    }

    /// <summary>The grid a seat block is on (null: on foot, or a grid's own camera).</summary>
    static Entity SeatGrid(Entity seat)
    {
        var grid = seat;
        for (int up = 0; grid != null && up < 4 && grid.TryGet<AeroGridComponent>() == null; up++)
            grid = grid.TryGet<HierarchyComponent>()?.Parent;
        if (grid == null || grid.TryGet<AeroGridComponent>() == null || ReferenceEquals(grid, seat)) return null;
        return grid;
    }

    static void Toast(Session session, string text)
    {
        try
        {
            session?.TryGet<IUserMessages>()?.DisplayMessage(new UserMessage
            {
                TitleId = LocKey.FromString("Quick launch"), ContentId = LocKey.FromString(text),
                Type = UserMessageType.Game, TextTimeout = TimeSpan.FromSeconds(3),
                MessageKey = StringId.Get("aero.quicklaunch"),   // (keyed: a new answer replaces the last)
            });
        }
        catch { }
    }

    // ── the cockpit's target, turned with a move aero made (the Orbital Mod turns its own: its FrameHost) ──
    sealed class Turn { public Quaternion Rot; public long Until; }
    static Turn _turn;
    static Entity _seatGrid;

    /// <summary>Server, after aero turned the seated ship: the client's cockpit target follows (within 3 s).</summary>
    internal static void NoteTurn(Quaternion rot)
        => Volatile.Write(ref _turn, new Turn { Rot = rot, Until = System.Diagnostics.Stopwatch.GetTimestamp() + 3 * System.Diagnostics.Stopwatch.Frequency });

    /// <summary>Client, each client grid's effects job: the asking pilot's grid turns its own target (TargetControlData,
    /// written by its own job). A reference compare when nothing is pending.</summary>
    internal static void TurnTarget(Entity grid)
    {
        var t = Volatile.Read(ref _turn);
        if (t == null || !ReferenceEquals(grid, _seatGrid)) return;
        if (Interlocked.CompareExchange(ref _turn, null, t) != t) return;
        if (System.Diagnostics.Stopwatch.GetTimestamp() > t.Until) return;
        try
        {
            if (!grid.Data.TryGet<Keen.Game2.Simulation.WorldObjects.Movement.TargetControlData>(out var tcd)) return;
            tcd.TargetOrientation = Quaternion.Normalize(t.Rot * tcd.TargetOrientation);
            grid.Data.Set(tcd);
        }
        catch { }
    }

    /// <summary>A world gone (WorldCarrier.Hooks - Save/WorldCarriers.cs: its last settings carrier left while it unloaded,
    /// planetless worlds too): the last world's ask, answer and turn dropped.</summary>
    internal static void ResetWorld()
    {
        Volatile.Write(ref _ask, null); Volatile.Write(ref _turn, null); Interlocked.Exchange(ref _devAsk, 0);
        PendingSay = null; Status = "-"; _seatGrid = null;
        _edge.Disarm(); _lastAsk = long.MinValue / 2;   // (the next world's first press needs the key seen up there)
    }
}
