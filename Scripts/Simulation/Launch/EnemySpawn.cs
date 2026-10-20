#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Threading;
using Keen.Game2.Client.GameSystems.Blueprints;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Simulation.GameSystems;
using Keen.Game2.Simulation.GameSystems.Blueprints;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.UserMessages;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Core.Input;
using Keen.VRage.Core.Platform;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.Library.Localization;
namespace AeroMod;

/// <summary>
/// TEST SPAWNS (2026-10-20, a creative / admin test feature approved by the user, like quick launch): Ctrl+Shift+I spawns an enemy
/// "missile" that goes for you, Ctrl+Shift+O an enemy "bogey" fighter, Ctrl+Shift+N removes every test ship (2026-10-20: was Ctrl+M /
/// Ctrl+B / Ctrl+N - the game's M opened the map and Ctrl+B the blueprints: a vanilla binding fires whatever modifiers are held
/// besides its own, so only I, N and O - no vanilla binding at all, ActionControlMapping.def - are clear; Tests/SpawnTests checks it). Each is a BLUEPRINT by name from
/// your Blueprints folder (Spawn.MissileBlueprint "Missile", Spawn.BogeyBlueprint "Bogey") that has an Autopilot block. The server
/// (EnemySpawn.Server.cs) spawns it Spawn.MissileDistance / Spawn.BogeyDistance away (Spawn.Direction: random, behind, ahead, a
/// side - by your heading), at your height, out of the ground and clear of grids; turns the whole construct into the game's hostile
/// NPC's through the block's own Enemy switch; sets its panel to Engage with the author's slot (Rocket Guidance / Dogfight), the
/// target "API" = you (your seated grid, else your character: AeroApi.SetAutopilotTarget); and launches it (Spawn.MissileSpeed /
/// Spawn.BogeySpeed; 0: the blueprint's own speed / the bogey's cruise). Spawn.MissileSalvo missiles per press, side by side. No
/// guidance or dogfight logic here: that is the slots' (ApRocketGuidance.cs / ApDogfight.cs - the author's).
///
/// Client side, this file: the keys (one ask per press - QuickLaunchRules.KeyEdge; not while typing; one a second), the blueprint
/// loaded the way the game's paste tool loads it (IBlueprints.TryLoadAsync, Blueprint.TryLoadGridAsync - the client's own blueprint
/// service: its server cannot see your Blueprints folder), checked (an Autopilot block; its saved panel, its front - the nose -
/// and the construct's size read from the builders), and the ask: who (the local identity), what, the bundle and the choices. The
/// server decides everything else (Spawn.Allowed, creative / admin, the world's autopilot, the slot) and answers with a toast.
/// Multiplayer: as quick launch - SE2 4.0's server shares this process (its networking is a mock); the ask names only the identity
/// and the blueprint's builders (what a paste sends the server), never a client entity.
/// </summary>
public static partial class EnemySpawn
{
    // ── the settings (AeroSettings: Spawn.*) ──
    /// <summary>World: who may spawn (EnemySpawnRules.Rule: 0 off, 1 creative or admin, 2 everyone).</summary>
    public static int Allowed = 1;
    /// <summary>World: spawned test ships removed after this many minutes (0: never).</summary>
    public static double DespawnMinutes = 10;
    /// <summary>Client: the keys (0 off, 1..26 A..Z) and their one modifier (0 none, 1 Ctrl, 2 Shift, 3 Alt, 4 Ctrl+Shift).</summary>
    public static int MissileKey = 9, BogeyKey = 15, DespawnKey = 14, Modifier = 4;
    /// <summary>Client: the blueprints (their folder names under Blueprints).</summary>
    public static string MissileBlueprint = "Missile", BogeyBlueprint = "Bogey";
    /// <summary>Client: how far (m), where (EnemySpawnRules.Dir), how many missiles, how fast (m/s; 0: its own / cruise).</summary>
    public static double MissileDistance = 2000, BogeyDistance = 4000;
    public static int Direction = 0, MissileSalvo = 1;
    public static double MissileSpeed = 0, BogeySpeed = 0;

    /// <summary>The answer to say (the server's, or the loader's), said by the next client tick (null: none).</summary>
    public static volatile string PendingSay;
    /// <summary>Diagnostics (harness aeroget): the last answer or state, and the counts.</summary>
    public static string Status = "-";
    public static int Asks, Spawned, Refusals, Despawned;

    /// <summary>What the client read from a blueprint's builders (engine-free values): its Autopilot block (the construct's
    /// first), that block's front and top in the main grid's frame (the nose the spawn points), the construct's reach about the
    /// main grid's origin (m), the block's saved panel (HasPanel false: the defaults), the blueprint's title.</summary>
    internal sealed class BundleInfo
    {
        public bool HasAutopilot, HasPanel; public int Autopilots, Grids, Blocks;
        public Vector3 NoseL = Vector3.Forward, TopL = Vector3.Up;
        public double Radius = 5;
        public ApPanel Panel = ApPanel.Defaults;
        public string Title = "";
    }

    /// <summary>A pending ask (swapped whole, taken once by the server): who, what, the loaded blueprint (Despawn: none), and the
    /// player's choices at the press (clamped again by the server). At: made (Stopwatch ticks; the server drops one older than
    /// EnemySpawnRules.AskMaxAgeS).</summary>
    internal sealed class Ask
    {
        public IdentityId Who; public bool HasWho; public long At;
        public EnemySpawnRules.Kind Kind; public string Name;
        public EntityBundle Bundle; public BundleInfo Info;
        public double Distance, Speed; public int Dir, Salvo;
    }
    static Ask _ask;
    internal static bool Asked => Volatile.Read(ref _ask) != null;
    internal static Ask TakeAsk() => Interlocked.Exchange(ref _ask, null);

    static long _nextFrame, _lastAsk = long.MinValue / 2, _deviceAt, _askedAt;
    static int _busy, _loading;
    static IInputDevice _keyboard;
    static readonly QuickLaunchRules.KeyEdge _missileEdge = new(), _bogeyEdge = new(), _despawnEdge = new();
    static DigitalInput[] _keys;

    /// <summary>The letter keys (1 = A).</summary>
    static DigitalInput[] Keys() => _keys ??= new[]
    {
        KeyboardInputs.A, KeyboardInputs.B, KeyboardInputs.C, KeyboardInputs.D, KeyboardInputs.E, KeyboardInputs.F, KeyboardInputs.G,
        KeyboardInputs.H, KeyboardInputs.I, KeyboardInputs.J, KeyboardInputs.K, KeyboardInputs.L, KeyboardInputs.M, KeyboardInputs.N,
        KeyboardInputs.O, KeyboardInputs.P, KeyboardInputs.Q, KeyboardInputs.R, KeyboardInputs.S, KeyboardInputs.T, KeyboardInputs.U,
        KeyboardInputs.V, KeyboardInputs.W, KeyboardInputs.X, KeyboardInputs.Y, KeyboardInputs.Z,
    };

    /// <summary>Client, the client's session tick (Runtime/AeroClientHost.cs, beside QuickLaunch.ClientTick): one caller a frame
    /// claims it, one runs it at a time. Cheap when nothing is pressed: three key state reads.</summary>
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
        // (an ask no server step took: said once - a world with no server planet, a paused server)
        var waiting = Volatile.Read(ref _ask);
        if (waiting != null && now - waiting.At > (long)(f * (EnemySpawnRules.AskMaxAgeS + 0.5)) && Interlocked.CompareExchange(ref _ask, null, waiting) == waiting)
        {
            Status = EnemySpawnRules.Say(EnemySpawnRules.Verdict.NoServer, waiting.Kind, waiting.Name); Toast(session, Status);
        }
        // (every binding read every tick: each press is its own edge, never one left over for later)
        var keys = Keys();
        bool missile = Pressed(MissileKey, _missileEdge, keys, now, f), bogey = Pressed(BogeyKey, _bogeyEdge, keys, now, f),
             despawn = Pressed(DespawnKey, _despawnEdge, keys, now, f);
        int dev = Interlocked.Exchange(ref _devKind, -1);
        if (dev >= 0) { _lastAsk = now; RequestKind(session, (EnemySpawnRules.Kind)dev, now); return; }   // (the API's / harness's ask)
        if (!missile && !bogey && !despawn) return;
        if (QuickLaunch.TopScreenNeedsInput(session)) return;   // (not while typing: a dialog, a terminal, the chat, a menu)
        if (now - _lastAsk < f) return;   // (one ask a second)
        _lastAsk = now;
        var kind = despawn ? EnemySpawnRules.Kind.Despawn : bogey ? EnemySpawnRules.Kind.Bogey : EnemySpawnRules.Kind.Missile;
        RequestKind(session, kind, now);
    }

    /// <summary>The ask for a kind (a key's press; the harness / API's RequestTestSpawn): who asks, then the blueprint loaded
    /// (Despawn: asked at once).</summary>
    static void RequestKind(Session session, EnemySpawnRules.Kind kind, long now)
    {
        var players = session?.SessionComponents.TryGet<ClientPlayersSessionComponent>();
        IdentityId who = default; bool hasWho = false;
        try { if (players != null) { who = players.LocalPlayerIdentity; hasWho = who.IsValid(); } } catch { }
        var ask = new Ask { Who = who, HasWho = hasWho, Kind = kind, At = now };
        Interlocked.Increment(ref Asks);
        if (kind == EnemySpawnRules.Kind.Despawn) { Post(ask); Status = "despawn asked"; return; }
        // (the world's rule and the slot first: no blueprint loaded for a spawn that cannot be - the server judges again)
        var pre = EnemySpawnRules.Allowed((EnemySpawnRules.Rule)Math.Clamp(Allowed, 0, 2), true, true);
        if (pre == EnemySpawnRules.Verdict.Off) { Say(EnemySpawnRules.Say(pre, kind, null)); return; }
        if (!ApPilot.SlotImplemented(EnemySpawnRules.Slot(kind))) { Say(EnemySpawnRules.Say(EnemySpawnRules.Verdict.SlotMissing, kind, null)); return; }
        string name = EnemySpawnRules.CleanName(kind == EnemySpawnRules.Kind.Bogey ? BogeyBlueprint : MissileBlueprint);
        if (name == null) { Say(EnemySpawnRules.Say(EnemySpawnRules.Verdict.NoBlueprintName, kind, null)); return; }
        if (Interlocked.CompareExchange(ref _loading, 1, 0) != 0) { Say(EnemySpawnRules.Say(EnemySpawnRules.Verdict.Busy, kind, name)); return; }
        ask.Name = name;
        ask.Distance = kind == EnemySpawnRules.Kind.Bogey ? BogeyDistance : MissileDistance;
        ask.Speed = kind == EnemySpawnRules.Kind.Bogey ? BogeySpeed : MissileSpeed;
        ask.Dir = Direction; ask.Salvo = MissileSalvo;
        Status = $"loading blueprint '{name}'";
        Load(session, ask);
    }

    static void Post(Ask ask) { ask.At = System.Diagnostics.Stopwatch.GetTimestamp(); _askedAt = ask.At; Volatile.Write(ref _ask, ask); }
    static void Say(string s) { PendingSay = s; Interlocked.Increment(ref Refusals); }

    /// <summary>The blueprint, as the game's paste tool loads it (GridPastingToolComponent.HandleBlueprintToolbarTile: the client's
    /// IBlueprints by id - its folder name -, then its grid bundle), scanned, and asked. Once at a time (_loading).</summary>
    static async void Load(Session session, Ask ask)
    {
        string say = null;
        try
        {
            var service = session?.TryGet<IBlueprints>();
            if (service == null) { say = EnemySpawnRules.Say(EnemySpawnRules.Verdict.LoadFailed, ask.Kind, ask.Name) + " (no blueprint service)"; return; }
            object life = session.SessionComponents;   // (the client session's lifetime, as the game's screenshot writer uses)
            var bp = await service.TryLoadAsync(new BlueprintId(ask.Name), life);
            if (bp == null) { say = EnemySpawnRules.Say(EnemySpawnRules.Verdict.NoBlueprint, ask.Kind, ask.Name); return; }
            var grid = await bp.TryLoadGridAsync(life);
            if (grid?.Bundle == null || grid.Bundle.Roots == 0) { say = EnemySpawnRules.Say(EnemySpawnRules.Verdict.LoadFailed, ask.Kind, ask.Name); return; }
            var info = Scan(grid.Bundle);
            try { info.Title = bp.Meta.BaseMetadata.Value.Title ?? ask.Name; } catch { info.Title = ask.Name; }
            if (!info.HasAutopilot) { say = EnemySpawnRules.Say(EnemySpawnRules.Verdict.NoAutopilot, ask.Kind, ask.Name); return; }
            ask.Bundle = grid.Bundle; ask.Info = info;
            Post(ask);
            Status = $"asked: {EnemySpawnRules.KindName(ask.Kind)} '{ask.Name}' ({info.Grids} grid(s), {info.Blocks} blocks, {info.Radius:F0} m)";
        }
        catch (Exception e)   // (one press, never per tick)
        {
            Log.Default?.Info($"[AERO] test spawn: loading '{ask.Name}' failed: {e.GetType().Name}: {e.Message}");
            say = EnemySpawnRules.Say(EnemySpawnRules.Verdict.LoadFailed, ask.Kind, ask.Name) + $" ({e.GetType().Name})";
        }
        finally
        {
            Volatile.Write(ref _loading, 0);
            if (say != null) Say(say);
        }
    }

    // ── the blueprint's builders, read (no entity: the bundle is data) ──

    static Guid[] _apCompositions;

    /// <summary>The Autopilot block's server compositions (its prefabs': AeroBlockGuids.AutopilotServerPrefabs).</summary>
    static Guid[] ApCompositions()
    {
        if (_apCompositions != null) return _apCompositions;
        var list = new List<Guid>();
        foreach (var g in AeroBlockGuids.AutopilotServerPrefabs)
            if (DefinitionManager.Instance.TryGetDefinition(g, out PrefabDefinition p) && p?.Composition != null) list.Add(p.Composition.Guid);
        return _apCompositions = list.ToArray();
    }

    /// <summary>A block builder is an Autopilot block: it carries the autopilot's saved slot (AeroBlockSave.AutopilotId - the mod's
    /// injected component), or its composition is the block's.</summary>
    static bool IsAutopilot(EntityObjectBuilder b, Guid[] comps)
    {
        var obs = b.ObjectBuilders;
        if (obs != null) foreach (var k in obs.Keys) if (k == AeroBlockSave.AutopilotId) return true;
        var d = b.Definition;
        if (d != null) foreach (var c in comps) if (d.Guid == c) return true;
        return false;
    }

    /// <summary>The construct in the bundle: its grids (the roots; the first is the main one), their blocks (each root's children),
    /// the first Autopilot block's front / top in the main grid's frame and its saved panel, the reach about the main grid.</summary>
    internal static BundleInfo Scan(EntityBundle bundle)
    {
        var info = new BundleInfo();
        var comps = ApCompositions();
        int roots = bundle.Roots;
        var root0 = bundle.Builders[0].TryGetOB<WorldTransformComponent, WorldTransformComponentObjectBuilder>()?.Transform ?? WorldTransform.Identity;
        double reach = 0;
        for (int i = 0; i < roots; i++)
        {
            var rb = bundle.Builders[i];
            var wt = rb.TryGetOB<WorldTransformComponent, WorldTransformComponentObjectBuilder>()?.Transform ?? root0;
            var rel = WorldTransform.GetRelativeTransform(wt, root0);   // (this grid in the main grid's frame)
            info.Grids++;
            var kids = rb.TryGetOB<HierarchyComponent, HierarchyComponentObjectBuilder>()?.Children;
            if (kids == null) continue;
            foreach (var b in kids)
            {
                if (b == null) continue;
                info.Blocks++;
                var ct = b.TryGetOB<ChildTransformComponent, ChildTransformComponentObjectBuilder>();
                Vector3 at = ct != null ? ct.Transform.Position : Vector3.Zero;
                Quaternion q = ct != null ? ct.Transform.Orientation : Quaternion.Identity;
                Vector3D p = rel.Position + Vector3D.Transform((Vector3D)at, rel.Orientation);
                reach = Math.Max(reach, p.Length());
                if (!IsAutopilot(b, comps)) continue;
                info.Autopilots++;
                if (info.HasAutopilot) continue;
                info.HasAutopilot = true;
                var bq = Quaternion.Normalize(rel.Orientation * q);
                info.NoseL = Vector3.Normalize(Vector3.Transform(Vector3.Forward, bq));
                info.TopL = Vector3.Normalize(Vector3.Transform(Vector3.Up, bq));
                info.HasPanel = SavedPanel(b, out info.Panel);
            }
        }
        info.Radius = reach + 2.5;   // (a block's own half size past its centre)
        return info;
    }

    /// <summary>The Autopilot block's panel as the blueprint saved it (AeroBlockSaveForm's key in its builder - the panel the block
    /// itself restores at spawn); false: none (the defaults).</summary>
    static bool SavedPanel(EntityObjectBuilder b, out ApPanel p)
    {
        p = ApPanel.Defaults;
        var obs = b.ObjectBuilders;
        if (obs == null) return false;
        var keys = obs.Keys; var vals = obs.Values;
        for (int i = 0; i < keys.Length && i < vals.Length; i++)
        {
            if (keys[i] != AeroBlockSave.AutopilotId || vals[i] is not EntityNameSessionComponentObjectBuilder ob) continue;
            string key = AeroBlockSaveForm.Find(ob.NamedEntities, AeroBlockSaveForm.AutopilotKey);
            return key != null && AeroBlockSaveForm.TryReadAutopilot(key, out p);
        }
        return false;
    }

    // ── the keys ──

    /// <summary>The binding went down this read (QuickLaunchRules.KeyEdge: up to down, its modifier held; never while held, never a
    /// down not seen up first). Off, no device, a device just found or lost: no press.</summary>
    static bool Pressed(int k, QuickLaunchRules.KeyEdge edge, DigitalInput[] keys, long now, long f)
    {
        if (k < 1 || k > keys.Length) { edge.Disarm(); return false; }
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
            down = _keyboard.GetDigitalState(keys[k - 1]);
            if (down)
                held = QuickLaunchRules.ModifierHeld(Modifier, _keyboard.GetDigitalState(KeyboardInputs.Control),
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
                TitleId = LocKey.FromString("Test spawn"), ContentId = LocKey.FromString(text),
                Type = UserMessageType.Game, TextTimeout = TimeSpan.FromSeconds(4),
                MessageKey = StringId.Get("aero.testspawn"),   // (keyed: a new answer replaces the last)
            });
        }
        catch { }
    }

    // ── the harness / API ──
    static int _devKind = -1;

    /// <summary>The API's / harness's ask (AeroApi.RequestTestSpawn): the next client tick asks as the key does (0 missile, 1 bogey,
    /// 2 despawn), the key's own checks (the binding, not while typing) skipped; the server's all kept.</summary>
    internal static void DevAsk(int kind) => Interlocked.Exchange(ref _devKind, Math.Clamp(kind, 0, 2));

    /// <summary>A world gone (WorldCarrier.Hooks): the last world's ask, answer, pending spawns and tracked ships dropped.</summary>
    internal static void ResetWorld()
    {
        Volatile.Write(ref _ask, null); PendingSay = null; Status = "-";
        _missileEdge.Disarm(); _bogeyEdge.Disarm(); _despawnEdge.Disarm(); _lastAsk = long.MinValue / 2;
        Interlocked.Exchange(ref _devKind, -1);
        ResetServer();
    }
}
