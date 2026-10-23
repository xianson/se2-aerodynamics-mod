#pragma warning disable
using System;
using System.Collections.Generic;
using System.Threading;
using Keen.Game2.Client.WorldObjects.CubeBlocks.Render;
using Keen.Game2.Simulation.WorldObjects.FloatingObjects;
using Keen.Game2.Simulation.WorldObjects.Shared;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Core.Model;
using Keen.VRage.DCS.Annotations;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.Library.Utils;
using Keen.VRage.Physics.Components;
using Keen.VRage.Physics.Data;

namespace AeroMod;

/// <summary>
/// FLYING BLADE PIECES (2026-10-23, the user: "break the blade tips / scatter them everywhere"; visual debris, this PC only). When a
/// rotor's published blade state steps down (AeroRotorAnimComponent: a tip lost - chunk B; broken to the stub - chunk A, and B if
/// it was still there) the piece is QUEUED here from the render-time animation job and SPAWNED in a client job before the physics
/// step (OnBladeDebris - the game's own debris spawner runs there too): the game's DAMAGED-BLOCK DEBRIS prefab
/// (FractureDebrisPrefabsConfiguration.DamagedBlockDebrisPrefab - a BlockPreviewComponent model, a SimpleRigidBodyComponent body, a
/// TimeoutComponent) with the chunk's own model (BladeBreakBlocks: a ModelAsset with one convex hull) and its collider from the game's
/// IColliderProvider, as DebrisSpawnerSessionComponent does for a block with no fractures. It leaves at the blade's speed there
/// (the hub's velocity + omega x r) tumbling, at its table mass; it despawns after LifetimeSeconds (its TimeoutComponent), at most
/// MaxActive at once (the oldest removed). Fx.DebrisScale 0: none. Nothing is synced: other players see their own.
/// Health log: once a session each needed part (the configuration, the spawner, the collider provider) is logged found or missing.
/// </summary>
public static class BladeDebris
{
    public static float LifetimeSeconds = 20f;
    public static int MaxActive = 24;
    /// <summary>The share of the blade's spin the piece keeps as its tumble, and a random part (rad/s at most).</summary>
    public static float TumbleShare = 0.3f, TumbleRandom = 6f;

    struct Ask { public Session Session; public Guid Model; public WorldTransform At; public Vector3 Linear, Angular; public float Mass; public string What; }
    static readonly object _lock = new();
    static readonly List<Ask> _queue = new(), _work = new();
    static readonly List<Entity> _alive = new();
    static int _armed;
    static readonly Random _rnd = new();
    static Session _session;

    // diagnostics (harness: aeroget BladeDebris.Status)
    public static int Asked, Spawned, Skipped, Failed, Removed;
    public static string LastFault = "-";
    public static string Status => $"asked {Asked} spawned {Spawned} skipped {Skipped} failed {Failed} removed {Removed} alive {_alive.Count} {LastFault}";

    /// <summary>(the animation job, client) a piece to throw: its model, world transform (origin at its centre of mass), velocities, mass.</summary>
    internal static void Queue(Session session, Guid model, in WorldTransform at, Vector3 linear, Vector3 angular, float mass, string what)
    {
        Asked++;
        if (!ReferenceEquals(session, _session)) { _session = session; NewSession(); }
        if (session == null || model == Guid.Empty || !(TearSmoke.DebrisScale > 0f)) { Skipped++; Health(model == Guid.Empty ? "no chunk model in BladeBreakBlocks (the content not built yet): no flying pieces" : null); return; }
        var spin = new Vector3((float)_rnd.NextDouble() - 0.5f, (float)_rnd.NextDouble() - 0.5f, (float)_rnd.NextDouble() - 0.5f) * (2f * TumbleRandom);
        lock (_lock) { if (_queue.Count < 32) _queue.Add(new Ask { Session = session, Model = model, At = at, Linear = linear, Angular = angular * TumbleShare + spin, Mass = mass, What = what }); }
        Volatile.Write(ref _armed, 1);
    }

    /// <summary>(OnBladeDebris, the first client rotor's job a tick) every queued piece spawned; the oldest beyond MaxActive removed.</summary>
    internal static void Step()
    {
        if (Interlocked.Exchange(ref _armed, 0) == 0) return;
        lock (_lock) { _work.AddRange(_queue); _queue.Clear(); }
        for (int i = 0; i < _work.Count; i++)
        {
            var a = _work[i];
            try { Spawn(in a); }
            catch (Exception e) { Failed++; LastFault = a.What + ": " + (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message; Health(LastFault); }
        }
        _work.Clear();
    }

    static void Spawn(in Ask a)
    {
        var spawner = a.Session.TryGet<IEntitySpawner>();
        var colliders = a.Session.TryGet<IColliderProvider>();
        FractureDebrisPrefabsConfiguration cfg = null;
        try { cfg = DefinitionManager.Instance?.GetConfiguration<FractureDebrisPrefabsConfiguration>(); } catch { }
        if (!_healthLogged)
        {
            _healthLogged = true;
            Log.Default?.Info($"[AERO-DEBRIS] blade debris health: spawner {(spawner != null ? "found" : "MISSING")}, collider provider {(colliders != null ? "found" : "MISSING")}, debris configuration {(cfg?.DamagedBlockDebrisPrefab != null ? "found" : "MISSING")}");
        }
        if (spawner == null || colliders == null || cfg?.DamagedBlockDebrisPrefab == null) { Failed++; return; }
        var prefab = cfg.DamagedBlockDebrisPrefab.Get();   // (a deep copy each time)
        var model = new ResourceHandle<ModelAsset>(a.Model);
        // (by object builder type: BlockPreviewComponent's base is in an assembly mods do not reference - its builder is reachable)
        var view = First<BlockPreviewObjectBuilder>(prefab);
        var body = First<SimpleRigidBodyComponentObjectBuilder>(prefab);
        var place = First<WorldTransformComponentObjectBuilder>(prefab);
        if (view == null || body == null || place == null) { Failed++; Health("the debris prefab has no BlockPreview / SimpleRigidBody / WorldTransform part"); return; }
        var collider = colliders.TryGetCollider(model);
        if (collider == null) { Failed++; Health($"no collider for chunk model {a.Model}"); return; }
        view.Model = new ResourceHandle(a.Model);
        body.Collider = collider;
        body.LinearVelocity = a.Linear; body.AngularVelocity = a.Angular;
        place.Transform = a.At;
        var timeout = First<TimeoutComponentObjectBuilder>(prefab);
        if (timeout != null) timeout.TimeToClose = TimeSpan.FromSeconds(LifetimeSeconds * (0.85 + 0.3 * _rnd.NextDouble()));
        var e = spawner.SpawnEntity(prefab);
        if (e == null) { Failed++; return; }
        Spawned++;
        // (its mass: the table's - the collider's own density would make a hollow blade shell weigh nothing or a tonne)
        var data = e.Data;
        if (a.Mass > 0f && data.TryGet<RigidBodyMassProperties>(out var mp) && mp.InvMass > 0f)
        {
            float k = mp.Mass / a.Mass;   // (scale the inverse mass and inverse inertia alike: the same shape, the table's mass)
            mp.InvMass *= k; mp.InvInertiaTensor *= k;
            data.Set(mp);
        }
        _alive.Add(e);
        for (int i = _alive.Count - 1; i >= 0; i--) if (_alive[i] == null || !GridPhysics.Alive(_alive[i].Data)) _alive.RemoveAt(i);
        while (_alive.Count > MaxActive) { var old = _alive[0]; _alive.RemoveAt(0); try { spawner.DeleteEntity(old); Removed++; } catch { } }
    }

    static T First<T>(EntityObjectBuilder prefab) where T : class { foreach (var ob in prefab.OBsOfType<T>()) return ob; return null; }

    static bool _healthLogged;
    static readonly HashSet<string> _warned = new();
    /// <summary>A problem, logged once per reason (the health log: never a silent no-show).</summary>
    static void Health(string why)
    {
        if (why == null) return;
        lock (_warned) if (!_warned.Add(why)) return;
        Log.Default?.Warning("[AERO-DEBRIS] blade debris: " + why);
    }
    /// <summary>A new session: the health lines again.</summary>
    internal static void NewSession() { _healthLogged = false; lock (_warned) _warned.Clear(); _alive.Clear(); }
}
