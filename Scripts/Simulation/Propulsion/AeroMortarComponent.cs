#pragma warning disable
using System;
using System.Collections.Generic;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.BlockOctrees;
using Keen.Game2.Simulation.GameSystems;
using Keen.VRage.Core;
using Keen.VRage.DCS.Annotations;
using Keen.VRage.Physics.Data;

namespace AeroMod;

/// <summary>
/// A MORTAR BREECH (2026-10-23; the law and the user's words: Mortar.cs). Injected into the breeches' SERVER prefabs only
/// (MortarBlocks.BreechServerPrefabs). Its switch flipped off -> on FIRES it, once:
///   1. the STACK: the charges of its size on its back (Up) face, touching, co-axial, working (SolidRocket.FindStack - the solid
///      rockets' search; AeroMortarChargeComponent is a stack piece) - none, or no MORTAR ROUND of its size on its front (Down) face:
///      nothing happens (Status says why), it may be fired again;
///   2. the SEAL: its front fracture piece killed (AeroGridComponent.KillPieceAt, the structure's split path: the game cuts that face's
///      mount points and splits the grid there if nothing else joins the round's side), torn-edge smoke out of the face (TearSmoke),
///      the charges spent (empty blocks), the breech FIRED for good (saved: "aero-mortar:1 1");
///   3. the KICK, only if the game split the grid (its split signal, within Mortar.SplitWaitTicks, a new grid with a mortar round where
///      the round was): the round's grid pushed forward and the ship back by the stack's impulse, at the breech's front face
///      (RigidBodyDataFunctions.TryApplyImpulse, the engine's own - both, the same step, once both bodies take it). No split: no impulse.
/// </summary>
public partial class AeroMortarComponent : Component, IInSceneListener
{
    public MortarBlocks.Row Row;
    /// <summary>Fired (its seal broken): spent for good.</summary>
    public bool Fired { get; private set; }
    bool _wasOff; LookRetry _look;
    CubeBlockComponent _cb; PowerableBlockComponent _pb;
    readonly List<AeroMortarChargeComponent> _cands = new(), _stack = new();
    // the shot in flight: the ship grid, the kick (N s), where the round was (world), the fire direction (world), steps waited
    Entity _ship; float _impulse; Vector3D _roundAt, _faceAt; Vector3 _dir; int _wait, _kickWait; Entity _piece;

    /// <summary>Diagnostics (aeroget AeroMortarComponent.Summary): shots, splits and kicks, fizzles (no split), refusals; the last.</summary>
    public static int Shots, Kicks, Fizzles, Refused; public static string Last = "-";
    public static string Summary => $"shots {Shots} kicks {Kicks} fizzles {Fizzles} refused {Refused}; last: {Last}";
    /// <summary>This breech's state for the harness.</summary>
    public string Status { get; private set; } = "ready";

    void IInSceneListener.OnAddedToScene() { _look.Reset(); _wasOff = false; }
    void IInSceneListener.OnBeforeRemovedFromScene() { _ship = null; _piece = null; }

    [Before(typeof(Keen.VRage.Core.Systems.Simulation))]
    internal class OnMortar : JobGroup;

    [OnMortar]
    [MustHave(typeof(AeroMortarComponent))]
    private static void MortarJob(AeroMortarComponent m) => m.Tick();

    void Tick()
    {
        if (Entity == null || !GridPhysics.Alive(Data)) return;
        if (!_look.Done)
        {
            if (!_look.Due()) return;
            _cb = Entity.TryGet<CubeBlockComponent>(); _pb = Entity.TryGet<PowerableBlockComponent>();
            Row = MortarBlocks.RowOf(_cb);
            _look.Done = Row != null && _cb != null && _pb != null;
            if (!_look.Done) return;
            _wasOff = !_pb.Enabled;
        }
        if (_ship != null) { Watch(); return; }
        bool on = _pb.Enabled;
        if (on && _wasOff && !Fired) Fire();
        _wasOff = !on;
    }

    // ── firing ──

    void Fire()
    {
        var grid = _cb.Grid?.Entity;
        if (grid == null) return;
        var bb = _cb.AABB;
        var up = Base6Directions.GetVector(_cb.BlockOrientation.TransformDirection(Base6Directions.Direction.Up));
        var me = SolidRocket.Geo.Of(bb.Min, bb.Max, up, Row.Size);
        if (!me.Valid) return;
        // 1. the stack (this grid's charges of its size) and the round
        _cands.Clear();
        lock (AeroMortarChargeComponent.All)
            foreach (var c in AeroMortarChargeComponent.All)   // (their cached places - at most GeoTicks old: never written from here,
                if (ReferenceEquals(c.GridEntity, grid) && !c.Spent) _cands.Add(c);   //  their own job may be running)
        SolidRocket.FindStack(in me, _cands, _stack);
        float kg = SolidRocket.Total(_stack);
        int a = me.Axis, s = me.Sign;
        float cell = TearMath.Cell;
        float front = Mortar.FrontFace(C(bb.Min, a), C(bb.Max, a), s, cell);
        Vector3 across = (new Vector3(bb.Min.X, bb.Min.Y, bb.Min.Z) + new Vector3(bb.Max.X, bb.Max.Y, bb.Max.Z)) * 0.5f * cell;
        var roundRow = FindRoundRow();
        float roundH = roundRow?.Box.Y ?? Row.Box.Y;
        Vector3 sealAt = With(across, a, Mortar.SealMiddle(front, s, Row.Seal));
        Vector3 faceAt = With(across, a, front), roundAt = With(across, a, Mortar.RoundMiddle(front, s, roundH));
        if (kg <= 0f) { Refuse("no charges on its back face (stack charges of its size straight on it)"); return; }
        if (!HasRound(grid, roundAt)) { Refuse("no mortar round of its size on its front face"); return; }
        // 2. the seal
        bool killed = false;
        try { killed = AeroGridComponent.KillPieceAt(_cb, grid, sealAt, Row.Seal * 0.8f); }
        catch (Exception e) { Refuse("the seal would not break: " + (e.InnerException ?? e).Message); return; }
        if (!killed) { Refuse("the seal would not break (no fracture piece there: content older than the scripts?)"); return; }
        Fired = true; Shots++;
        foreach (var c in _stack) c.Spend();
        var gwt = grid.Data.GetWorldTransform();
        Vector3 dirLocal = Base6Directions.GetVector(_cb.BlockOrientation.TransformDirection(Base6Directions.Direction.Down));
        TearSmoke.Publish(grid, faceAt, dirLocal);
        _ship = grid; _impulse = Mortar.Impulse(kg); _wait = 0; _kickWait = 0; _piece = null;
        _roundAt = WorldTransform.Transform((Vector3D)roundAt, in gwt);
        _faceAt = WorldTransform.Transform((Vector3D)faceAt, in gwt);
        _dir = (Vector3)(WorldTransform.Transform((Vector3D)(faceAt + dirLocal), in gwt) - _faceAt);
        Watching(grid);
        Status = $"fired: {_stack.Count} charges, {kg:F0} kg, {_impulse / 1e3:F0} kN s - waiting for the split";
        Last = $"{Row.Name}: {Status}";
        Log.Default?.Info($"[AERO-MORTAR] {Last}");
    }

    void Refuse(string why)
    {
        Refused++; Status = "not fired: " + why; Last = $"{Row?.Name}: {Status}";
        Log.Default?.Info($"[AERO-MORTAR] {Last}");
    }

    /// <summary>The round on the front face (the row of the block there - read after the fire, for its height: same size only).</summary>
    MortarBlocks.Row FindRoundRow()
    {
        foreach (var r in MortarBlocks.Rows) if (r.Kind == MortarBlocks.Kind.Round && r.Size == Row.Size) return r;
        return null;
    }

    /// <summary>A mortar round of this breech's size in this grid with its middle at roundAt (grid-local m, within a quarter cell).</summary>
    bool HasRound(Entity grid, Vector3 roundAt) => RoundIn(grid?.TryGet<BlockOctreeComponent>(), roundAt, TearMath.Cell) != null;

    CubeBlockComponent RoundIn(BlockOctreeComponent octree, Vector3 at, float cell)
    {
        if (octree == null) return null;
        foreach (var b in octree.GetAllCubeBlocks())
        {
            var r = MortarBlocks.RowOf(b);
            if (r == null || r.Kind != MortarBlocks.Kind.Round || r.Size != Row.Size) continue;
            var bb = b.AABB;
            var mid = (new Vector3(bb.Min.X, bb.Min.Y, bb.Min.Z) + new Vector3(bb.Max.X, bb.Max.Y, bb.Max.Z)) * 0.5f * cell;
            if ((mid - at).LengthSquared() < cell * cell * 0.0625f) return b;
        }
        return null;
    }

    // ── the split, then the kick ──

    void Watch()
    {
        if (_piece == null)
        {
            _piece = TakeSplit(_ship, _roundAt);
            if (_piece == null)
            {
                if (++_wait < Mortar.SplitWaitTicks) return;
                Fizzles++; Status = "fired - no split (the round's side is still joined to the ship): no kick"; Last = $"{Row.Name}: {Status}";
                Log.Default?.Info($"[AERO-MORTAR] {Last}");
                _ship = null; return;
            }
        }
        // (both bodies must take it the same step: one momentum - a new grid's body comes a step or two after it)
        Mortar.Kick(_impulse, _dir, out var onRound, out var onShip);
        bool ok = GridPhysics.Alive(_piece.Data) && GridPhysics.Alive(_ship.Data);
        if (ok && RigidBodyDataFunctions.TryApplyImpulse(onRound, _faceAt, _piece))
        {
            RigidBodyDataFunctions.TryApplyImpulse(onShip, _faceAt, _ship);
            Kicks++; Status = $"fired: the round away, {_impulse / 1e3:F0} kN s"; Last = $"{Row.Name}: {Status}";
            Log.Default?.Info($"[AERO-MORTAR] {Last}");
            _ship = null; _piece = null; return;
        }
        if (++_kickWait < Mortar.KickWaitTicks) return;
        Fizzles++; Status = "fired - split, but the round's grid took no impulse: no kick"; Last = $"{Row.Name}: {Status}";
        Log.Default?.Info($"[AERO-MORTAR] {Last}");
        _ship = null; _piece = null;
    }

    // the ship grids' split signal (subscribed once per grid that fires; the splits kept a short while for the breeches to claim)
    static readonly object _lock = new();
    static readonly List<(Entity original, Entity piece, long at)> _splits = new();
    /// <summary>How long a split waits to be claimed (ms): a watched grid keeps reporting every split of its own (structural breaks too).</summary>
    const long SplitKeepMs = 5000;
    static readonly HashSet<Entity> _watched = new();
    static CubeGridComponent.SplitGridSpawnedSignal.ExternalApi _onSplit;

    static void Watching(Entity grid)
    {
        lock (_lock)
        {
            if (!_watched.Add(grid)) return;
            if (_watched.Count > 256) { _watched.Clear(); _watched.Add(grid); }   // (an occasional rebuild: a grid that left is a dead key)
        }
        try { _onSplit ??= OnSplit; CubeGridComponent.SplitGridSpawnedSignal.Subscribe(grid, _onSplit); }
        catch (Exception e) { Last = "split watch failed: " + e.Message; }
    }

    static void OnSplit(Entity original, Entity piece)
    {
        if (original == null || piece == null) return;
        long now = Environment.TickCount64;
        lock (_lock)
        {
            Prune(now);
            if (_splits.Count < 64) _splits.Add((original, piece, now));
        }
    }

    static void Prune(long now)
    {
        for (int i = _splits.Count - 1; i >= 0; i--) if (now - _splits[i].at > SplitKeepMs) _splits.RemoveAt(i);
    }

    /// <summary>A split of this ship grid since it fired whose new grid holds the round (a mortar round of this size with its middle
    /// within a quarter cell of where the round was); claimed (taken off the list). Splits older than SplitKeepMs dropped.</summary>
    Entity TakeSplit(Entity ship, Vector3D roundWorld)
    {
        lock (_lock)
        {
            Prune(Environment.TickCount64);
            for (int i = _splits.Count - 1; i >= 0; i--)
            {
                var (o, p, _) = _splits[i];
                if (!ReferenceEquals(o, ship) || !GridPhysics.Alive(p.Data)) continue;
                var pwt = p.Data.GetWorldTransform();
                var local = (Vector3)WorldTransform.TransformInv(roundWorld, pwt);
                if (RoundIn(p.TryGet<BlockOctreeComponent>(), local, TearMath.Cell) == null) continue;
                _splits.RemoveAt(i);
                return p;
            }
        }
        return null;
    }

    static int C(Vector3I v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;
    static Vector3 With(Vector3 v, int a, float x) => a == 0 ? new Vector3(x, v.Y, v.Z) : a == 1 ? new Vector3(v.X, x, v.Z) : new Vector3(v.X, v.Y, x);

    // ── saved ──

    public const string SaveKey = "aero-mortar:";

    [Init]
    private void InitBlockSave(EntityNameSessionComponentObjectBuilder ob)
    {
        string key = AeroBlockSaveForm.Find(ob?.NamedEntities, SaveKey);
        if (key == null) return;
        var w = key.Substring(SaveKey.Length).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (w.Length >= 2 && w[1] == "1") { Fired = true; Status = "fired (saved)"; }
    }

    [Serializer]
    private void SaveBlock(EntityNameSessionComponentObjectBuilder ob)
        => ob.NamedEntities = AeroBlockSave.One(Fired ? SaveKey + "1 1" : null, Entity);
}
