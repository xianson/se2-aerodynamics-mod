#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Threading;
using Keen.Game2.Simulation.GameSystems.Identities;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Physicss;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.GameSystems.Queries;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.Physics;
using Keen.VRage.Physics.Components;
using Keen.VRage.Physics.Data;
namespace AeroMod;

/// <summary>
/// TEST SPAWNS, the server's side - on the server planets' teleport-phase job (AeroWorldSettingsComponent.QuickLaunchJob, beside
/// quick launch; one planet's job at a time: _serverBusy), only while there is work (an ask, a spawn being set up, a test ship
/// alive - a few reference and count reads otherwise):
///  - the ask: taken once, only while fresh; the asking player's own character here (its identity), creative / admin, the world's
///    rule (Spawn.Allowed), the world's autopilot (Autopilot.Enabled, Autopilot.Unattended - a ship with nobody seated lets go
///    otherwise), the blueprint's Autopilot block, the slot (EnemySpawnRules.Check);
///  - where: the target - the player's seated grid, else its character -, its heading, the direction and distance, at its height
///    over the planet, out of the ground (the planet's own height, GamePlanetHeight) and out of grids (IPhysics.QueryAABB:
///    moved up); a salvo side by side; turned so the Autopilot block's front points at the target (a bogey level);
///  - the spawn: the game's own path for a paste (IGameMode.PrepareForSpawn: the player's ownership, survival's clearing), the
///    bundle moved (EntityBundleFunctions.TransformBundle), its grids dynamic, at the launch velocity (their physics builders'
///    LinearVelocity - what a load restores), tagged by name (EnemySpawnRules.Tag), and spawned STAGED and ASYNC on this server
///    job (IEntitySpawner.SpawnBundleAsync, as the game's encounters and the Orbital Mod's DevTestShip - a synchronous spawn, or one
///    from the client's thread, crashed the game there);
///  - once it is in (a later tick of this job): every Autopilot block on the construct gets its panel (EnemySpawnRules.Configure:
///    Engage, the slot, target source API, kept unattended), the API target (AeroApi.SetAutopilotTarget: the player), and its
///    Enemy switch on (ApEnemySwitch.RequestSpawned - the block's own step turns the whole construct the game's hostile NPC's,
///    every grid, as the panel's switch does); a bogey's speed becomes its cruise once its aero probe knows it;
///  - cleanup: Spawn.DespawnMinutes after it appeared (0: never), and the despawn key (every grid tagged, after a reload too) -
///    IEntitySpawner.DeleteEntity, as the game's encounters remove theirs.
/// </summary>
public static partial class EnemySpawn
{
    sealed class Pending
    {
        public EnemySpawnRules.Kind Kind; public Entity Target; public Vector3 NoseL; public long At;
        public bool WantsCruise, CruiseDone, Configured; public double Setting, PanelSpeed;
        public volatile Entity[] Entities; public volatile string Failed;
        public readonly List<Entity> Grids = new();
    }

    static readonly List<Pending> _pending = new();
    static readonly List<(Entity grid, long at)> _tracked = new();
    static readonly List<Entity> _chars = new(), _scratch = new();
    static readonly List<AeroAutopilotComponent> _blocks = new();
    static volatile int _pendingCount, _trackedCount;
    static int _serverBusy;
    static long _nextSweep;
    static GamePlanetHeight _height;
    static Keen.VRage.Library.Memory.Buffer<Keen.VRage.DCS.Accessors.DEntity> _hits;
    static readonly Random _rnd = new();

    /// <summary>A server planet's teleport-phase job (QuickLaunchJob): nothing to do - three reads.</summary>
    internal static void ServerTick(AeroWorldSettingsComponent carrier)
    {
        if (!Asked && _pendingCount == 0 && _trackedCount == 0) return;
        if (Interlocked.CompareExchange(ref _serverBusy, 1, 0) != 0) return;
        try
        {
            var session = carrier?.Entity?.GetSession();
            if (session == null) return;
            long now = System.Diagnostics.Stopwatch.GetTimestamp(), f = System.Diagnostics.Stopwatch.Frequency;
            var ask = Asked ? TakeAsk() : null;
            if (ask != null) ServeAsk(session, ask, now, f);
            if (_pendingCount > 0) Configure(now, f);
            if (_trackedCount > 0 && now >= _nextSweep) { _nextSweep = now + f; Sweep(session, now, f); }
        }
        catch (Exception e)   // (an exception escaping into the engine's job is fatal; this path only runs while there is work)
        {
            Log.Default?.Info($"[AERO] test spawn: failed: {e.GetType().Name}: {e.Message}");
            Answer("Test spawn: failed (" + e.GetType().Name + ")", false);
            lock (_pending) { _pending.Clear(); _pendingCount = 0; }
        }
        finally { Volatile.Write(ref _serverBusy, 0); }
    }

    static void ServeAsk(Session session, Ask ask, long now, long f)
    {
        if (!QuickLaunchRules.Fresh(ask.At, now, (long)(f * EnemySpawnRules.AskMaxAgeS)))
        {
            Log.Default?.Info($"[AERO] test spawn: an ask {(now - ask.At) / (double)f:F1} s old dropped");
            Answer(EnemySpawnRules.Say(EnemySpawnRules.Verdict.NoServer, ask.Kind, ask.Name), false);
            return;
        }
        // who: the asking player's character on this server (owned by its identity; the only one when that is not known)
        var ch = Character(session, ask);
        var gm = session.SessionComponents.TryGet<GameModeSessionComponent>();
        bool creative = ch != null && gm != null && gm.IsCreative(ch);
        var ids = session.TryGet<IdentitiesSessionComponent>();
        bool admin = ids != null && (!ask.HasWho || ask.Who.Equals(ids.LocalIdentity));   // (the host: 4.0 has no admin list)
        var rule = (EnemySpawnRules.Rule)Math.Clamp(Allowed, 0, 2);
        if (ask.Kind == EnemySpawnRules.Kind.Despawn)
        {
            var dv = EnemySpawnRules.Allowed(rule, creative, admin);
            if (dv != EnemySpawnRules.Verdict.Go) { Refuse(dv, ask); return; }
            int n = DespawnAll(session);
            if (n == 0) { Answer(EnemySpawnRules.Say(EnemySpawnRules.Verdict.NothingToDespawn, ask.Kind, null), false); return; }
            Answer($"{n} test grid(s) removed", true);
            return;
        }
        var info = ask.Info;
        var v = EnemySpawnRules.Check(rule, creative, admin, ch != null, Autopilot.Enabled, Autopilot.Unattended,
                                      info != null && info.HasAutopilot, ApPilot.SlotImplemented(EnemySpawnRules.Slot(ask.Kind)));
        if (v != EnemySpawnRules.Verdict.Go) { Refuse(v, ask); return; }
        if (ask.Bundle == null || ask.Bundle.Roots == 0) { Refuse(EnemySpawnRules.Verdict.LoadFailed, ask); return; }

        // the target: the seat the character pilots (its grid), else the character
        Entity seat = null, grid = null;
        foreach (var se in session.GetEntitiesOfType<SeatComponent>())
            if ((se.TryGet<SeatComponent>() as ISeat)?.Pilot == ch) { seat = se; break; }
        grid = seat?.TryGet<CubeBlockComponent>()?.Grid?.Entity;
        if (grid != null && !GridPhysics.Alive(grid.Data)) grid = null;
        var target = grid ?? ch;
        var twt = target.Data.GetWorldTransform();
        Vector3D tpos = twt.Position;
        var noseFrom = seat != null && grid != null ? seat.Data.GetWorldTransform().Orientation : twt.Orientation;
        Vector3D tnose = Vector3D.Transform(-Vector3D.UnitZ, noseFrom);
        GridPhysics.TryGetVelocity((target.GetTopLevelParent() ?? target).Data, out Vector3 tvel, out _);

        // up: from the nearest server planet's centre (a character's gravity is not read here: on foot counts too; far out in
        // space it is only a frame - the height query answers nothing past the planet's reach), else away from the gravity,
        // else the target's own up
        Vector3D centre = new Vector3D(double.NaN), up;
        Vector3 g = GridPhysics.GetGravityDirection(target.Data);
        bool planet = QuickLaunch.NearestPlanet(target.Data.Scene, tpos, out centre);
        if (planet) up = Vector3D.Normalize(tpos - centre);
        else
        {
            centre = new Vector3D(double.NaN);
            up = g.LengthSquared() > 0.01f ? -Vector3D.Normalize((Vector3D)g) : Vector3D.Transform(Vector3D.UnitY, twt.Orientation);
        }
        var kind = ask.Kind;
        double dist = Math.Clamp(ask.Distance, 100, 30000), radius = Math.Max(2.5, info.Radius);
        var heading = EnemySpawnRules.Heading(up, (Vector3D)tvel, tnose);
        var dirMode = (EnemySpawnRules.Dir)Math.Clamp(ask.Dir, 0, 3);
        var dir = EnemySpawnRules.Direction(dirMode, up, heading, _rnd.NextDouble());
        var basePoint = EnemySpawnRules.Place(tpos, centre, dir, dist);
        Vector3D side = Vector3D.Normalize(Vector3D.Cross(dir, up));
        int count = EnemySpawnRules.SalvoCount(kind, ask.Salvo);
        float cap = StructureContactLoads.PhysicsSpeedCap;
        double speed = EnemySpawnRules.Speed(kind, ask.Speed, double.NaN, info.Panel.SpeedMs, cap > 0f ? cap * QuickLaunchRules.CapShare : 0);
        bool cruise = EnemySpawnRules.WantsCruise(kind, ask.Speed, info.Panel.SpeedMs);
        var spawner = session.Get<IEntitySpawner>();
        var gameMode = session.TryGet<IGameMode>();
        IdentityId owner = ask.HasWho ? ask.Who : ch.TryGet<OwnershipComponent>()?.Owner ?? default;
        _height ??= new GamePlanetHeight();
        int raised = 0;
        for (int i = 0; i < count; i++)
        {
            Vector3D p = basePoint + side * EnemySpawnRules.SalvoOffset(i, count, radius);
            if (planet && _height.Surface(p, 1, out var c2, out double surfaceR)) p = EnemySpawnRules.OverGround(p, c2, surfaceR, radius);
            for (int k = 0; k < EnemySpawnRules.MaxRaises && Occupied(target, p, radius); k++) { p += up * EnemySpawnRules.RaiseStep(radius); raised++; }
            Vector3D noseW = EnemySpawnRules.Nose(kind, p, tpos, up);
            var q = EnemySpawnRules.Aim(info.NoseL, info.TopL, noseW, up);
            Vector3 vel = (Vector3)(noseW * speed);
            // (each member its own copy of the builders: the last one takes the original)
            var bundle = i == count - 1 ? ask.Bundle : ask.Bundle.CloneBundle();
            gameMode?.PrepareForSpawn(owner, ref bundle);   // (the paste's own preparation: the owner, survival's clearing)
            if (bundle == null || bundle.Roots == 0) { Refuse(EnemySpawnRules.Verdict.SpawnFailed, ask); return; }
            var root0 = bundle.Builders[0].TryGetOB<WorldTransformComponent, WorldTransformComponentObjectBuilder>()?.Transform ?? WorldTransform.Identity;
            bundle.TransformBundle(new WorldTransform(p, q) * WorldTransform.Invert(root0));
            for (int r = 0; r < bundle.Roots; r++)
            {
                var rb = bundle.Builders[r];
                foreach (var phys in rb.OBsOfType<RigidBodyComponentObjectBuilder>()) { phys.LinearVelocity = vel; phys.AngularVelocity = Vector3.Zero; }
                foreach (var gp in rb.OBsOfType<GridPhysicsObjectBuilder>()) if (gp.MotionType == BodyArgs.Motion.Static) gp.MotionType = BodyArgs.Motion.Dynamic;
                var cg = rb.TryGetOB<CubeGridComponent, CubeGridObjectBuilder>();
                if (cg != null) cg.DisplayName = EnemySpawnRules.Tagged(r == 0 ? info.Title : NameOr(cg, info.Title));
            }
            var pend = new Pending { Kind = kind, Target = target, NoseL = info.NoseL, At = now, WantsCruise = cruise, Setting = ask.Speed, PanelSpeed = info.Panel.SpeedMs };
            lock (_pending) { _pending.Add(pend); _pendingCount = _pending.Count; }
            Spawn(spawner, bundle, pend);
        }
        string where = dirMode == EnemySpawnRules.Dir.Random ? "around you" : dirMode == EnemySpawnRules.Dir.Behind ? "behind you" : dirMode == EnemySpawnRules.Dir.Ahead ? "ahead of you" : "off your side";
        string what = kind == EnemySpawnRules.Kind.Bogey ? "enemy bogey" : count > 1 ? $"{count} enemy missiles" : "enemy missile";
        string spd = cruise ? "at cruise" : $"{speed:F0} m/s";
        Log.Default?.Info($"[AERO] test spawn: {what} '{ask.Name}' {dist:F0} m {where}, {spd}, on {(grid != null ? "the player's grid" : "the player")}{(raised > 0 ? $" (moved up {raised}x out of grids)" : "")}");
        Answer($"{what} '{ask.Name}' {dist:F0} m {where}, {spd}", true);
    }

    static string NameOr(CubeGridObjectBuilder cg, string fallback)
    {
        try { string n = cg.DisplayName.EvaluateText(); return string.IsNullOrEmpty(n) ? fallback : n; } catch { return fallback; }
    }

    /// <summary>The spawn, staged and async (the entities come in a later tick: Pending.Entities).</summary>
    static async void Spawn(IEntitySpawner spawner, EntityBundle bundle, Pending pend)
    {
        try
        {
            if (spawner == null) { pend.Failed = "no spawner"; return; }
            var ents = await spawner.SpawnBundleAsync(new GameEntitySerializer.BundleInitArgs { Bundle = bundle });
            if (ents == null || ents.Length == 0) pend.Failed = "nothing spawned";
            else pend.Entities = ents;
        }
        catch (Exception e) { pend.Failed = (e.InnerException ?? e).Message; }
    }

    /// <summary>Spawned constructs set up (this job, each tick while one waits; ConfigureTimeoutS at most).</summary>
    static void Configure(long now, long f)
    {
        lock (_pending)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
                if (Step(_pending[i], now, f)) _pending.RemoveAt(i);
            _pendingCount = _pending.Count;
        }
    }

    /// <summary>One spawned construct: true when it is done with (set up, failed or given up on).</summary>
    static bool Step(Pending p, long now, long f)
    {
        bool late = now - p.At > (long)(f * EnemySpawnRules.ConfigureTimeoutS);
        if (p.Failed != null) { Refuse(EnemySpawnRules.Verdict.SpawnFailed, null, p.Failed); return true; }
        var ents = p.Entities;
        if (ents == null) { if (late) { Answer("Test spawn: the spawned ship never came in", false); return true; } return false; }
        if (p.Grids.Count == 0)
        {
            foreach (var e in ents) if (e != null && e.TryGet<CubeGridComponent>() != null) p.Grids.Add(e);
            foreach (var g in p.Grids) _tracked.Add((g, now));
            _trackedCount = _tracked.Count;
            Interlocked.Increment(ref Spawned);
        }
        if (!p.Configured)
        {
            _blocks.Clear();
            lock (AeroAutopilotComponent.All)
                foreach (var a in AeroAutopilotComponent.All)
                {
                    var g = ApEnemyGame.GridOfBlock(a.Entity);
                    if (g != null && p.Grids.Contains(g)) _blocks.Add(a);
                }
            bool all = _blocks.Count > 0;
            foreach (var a in _blocks)
            {
                var g = ApEnemyGame.GridOfBlock(a.Entity);
                // (the API target needs the grid's server aero component: not there yet - the next tick)
                if (!AeroApi.SetAutopilotTarget(g, p.Target)) { all = false; continue; }
                a.SetPanel(EnemySpawnRules.Configure(a.Panel, p.Kind));
                a.AskEnemySpawned();
            }
            if (!all)
            {
                if (!late) return false;
                Answer(_blocks.Count == 0 ? "Test spawn: spawned, but its Autopilot block never came up - it will not fly" : "Test spawn: spawned, but its autopilot could not be given the target", false);
                return true;
            }
            p.Configured = true;
            bool working = false;
            foreach (var a in _blocks) if (a.Working) working = true;
            if (!working) Answer("Test spawn: its Autopilot block is off or has no power - it will not fly", false);
            Log.Default?.Info($"[AERO] test spawn: {EnemySpawnRules.KindName(p.Kind)} set up - {_blocks.Count} autopilot block(s), {p.Grids.Count} grid(s), Engage {EnemySpawnRules.Slot(p.Kind)}, enemy switch asked");
        }
        if (p.WantsCruise && !p.CruiseDone)
        {
            // (a bogey with no speed of its own: its cruise once its aero probe knows it - the top speed along its nose at 3/4 throttle)
            var main = p.Grids.Count > 0 ? p.Grids[0] : null;
            float top = main != null ? AeroApi.GetTopSpeedAt(main, p.NoseL, EnemySpawnRules.CruiseThrottle) : float.NaN;
            float cap = StructureContactLoads.PhysicsSpeedCap;
            if (float.IsFinite(top) && top > 1f && main != null && GridPhysics.Alive(main.Data))
            {
                double speed = EnemySpawnRules.Speed(p.Kind, p.Setting, top, p.PanelSpeed, cap > 0f ? cap * QuickLaunchRules.CapShare : 0);
                var vel = (Vector3)(Vector3D.Transform((Vector3D)p.NoseL, main.Data.GetWorldTransform().Orientation) * speed);
                foreach (var g in p.Grids) if (GridPhysics.Alive(g.Data)) GridPhysics.TrySetLinearVelocity(g.Data, vel);
                Log.Default?.Info($"[AERO] test spawn: bogey at its cruise, {speed:F0} m/s");
                p.CruiseDone = true;
            }
            else if (now - p.At > 3 * f) p.CruiseDone = true;   // (not known in 3 s: the fallback it was launched at stands)
            if (!p.CruiseDone) return false;
        }
        return true;
    }

    /// <summary>Test ships due (Spawn.DespawnMinutes) removed; gone ones forgotten (once a second).</summary>
    static void Sweep(Session session, long now, long f)
    {
        lock (_pending) SweepLocked(session, now, f);
    }

    static void SweepLocked(Session session, long now, long f)
    {
        var spawner = session.Get<IEntitySpawner>();
        int removed = 0;
        for (int i = _tracked.Count - 1; i >= 0; i--)
        {
            var (g, at) = _tracked[i];
            if (g == null || !GridPhysics.Alive(g.Data)) { _tracked.RemoveAt(i); continue; }
            if (!EnemySpawnRules.Expired(at, now, DespawnMinutes, f)) continue;
            spawner?.DeleteEntity(g); removed++;
            _tracked.RemoveAt(i);
        }
        _trackedCount = _tracked.Count;
        if (removed > 0) { Interlocked.Add(ref Despawned, removed); Log.Default?.Info($"[AERO] test spawn: {removed} test grid(s) removed after {DespawnMinutes:F0} min"); }
    }

    /// <summary>Every test grid: the ones spawned this session and any grid whose name carries the tag (a reloaded world's).</summary>
    static int DespawnAll(Session session)
    {
        lock (_pending) return DespawnLocked(session);
    }

    static int DespawnLocked(Session session)
    {
        var spawner = session.Get<IEntitySpawner>();
        var list = _scratch; list.Clear();
        foreach (var (g, _) in _tracked) if (g != null && GridPhysics.Alive(g.Data)) list.Add(g);
        foreach (var ge in session.GetEntitiesOfType<CubeGridComponent>())
        {
            if (list.Contains(ge) || !GridPhysics.Alive(ge.Data)) continue;
            string name;
            try { name = ge.TryGet<CubeGridComponent>()?.DisplayName.EvaluateText(); } catch { name = null; }
            if (EnemySpawnRules.IsTagged(name)) list.Add(ge);
        }
        int n = 0;
        foreach (var g in list) if (GridPhysics.Alive(g.Data)) { spawner?.DeleteEntity(g); n++; }
        list.Clear();
        _tracked.Clear(); _trackedCount = 0;
        Interlocked.Add(ref Despawned, n);
        Log.Default?.Info($"[AERO] test spawn: {n} test grid(s) removed (despawn)");
        return n;
    }

    /// <summary>The spawn point is inside something (a grid, an asteroid - not the planet: the height took care of that): the
    /// physics' broadphase over the ship's box there (IPhysics.QueryAABB, CollisionPreset.Bodies - as ApGameObstacles asks).</summary>
    static bool Occupied(Entity near, Vector3D p, double radius)
    {
        var physics = GridPhysics.Physics;
        if (physics == null || near == null) return false;
        double r = radius + 10;
        var args = new AABBQueryArgs(new BoundingBoxD(p - new Vector3D(r), p + new Vector3D(r)));
        _hits.Clear();
        physics.QueryAABB(_hits, in args, CollisionPreset.Bodies);   // (args own no buffer: never args.Dispose)
        var scene = near.Scene;
        for (int i = 0; i < _hits.Count; i++)
        {
            var e = Entity.TryGetFromDataEntity((Scene: scene, Entity: _hits[i]));
            if (e == null || !GridPhysics.Alive(e.Data)) continue;
            var bb = BoundingBoxData.TryGetWorldAABB(e.Data);
            if (!bb.HasValue) continue;
            double size = (bb.Value.Max - bb.Value.Min).Length() * 0.5;
            if (size > 0.1 && size <= ApGameObstacles.MaxRadius) return true;   // (a planet's body is bigger: its height is the ground's)
        }
        return false;
    }

    /// <summary>The asking player's character here: the alive one its identity owns; not known, the only one (single player).</summary>
    static Entity Character(Session session, Ask ask)
    {
        var list = _chars; list.Clear();
        Entity found = null;
        if (session.TryFillAliveCharacters(list))
        {
            if (ask.HasWho)
                foreach (var c in list)
                {
                    var own = c.TryGet<OwnershipComponent>();
                    if (own != null && own.Owner.Equals(ask.Who)) { found = c; break; }
                }
            if (found == null && list.Count == 1) found = list[0];
        }
        list.Clear();
        return found;
    }

    static void Refuse(EnemySpawnRules.Verdict v, Ask ask, string detail = null)
    {
        string say = EnemySpawnRules.Say(v, ask?.Kind ?? EnemySpawnRules.Kind.Missile, ask?.Name) + (detail != null ? " (" + detail + ")" : "");
        Log.Default?.Info($"[AERO] test spawn: refused: {v}{(detail != null ? " (" + detail + ")" : "")}");
        Interlocked.Increment(ref Refusals);
        Answer(say, false);
    }

    static void Answer(string say, bool ok) { PendingSay = say; Status = (ok ? "done: " : "refused: ") + say; }

    /// <summary>The server's state dropped (a world gone).</summary>
    static void ResetServer()
    {
        lock (_pending) { _pending.Clear(); _pendingCount = 0; _tracked.Clear(); _trackedCount = 0; }
        _nextSweep = 0;
        Volatile.Write(ref _loading, 0);
    }
}
