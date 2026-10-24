#pragma warning disable
using System;
using System.Collections.Generic;
using Keen.Game2.Simulation.GameSystems;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.BlockOctrees;
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
///   2. the SEAL: the ship's split signal watched, the breech marked FIRED and the charges SPENT (before the break: a split made inside
///      the fracture call rebuilds a side from the blocks' saved state), its front fracture piece killed (AeroGridComponent.KillPieceAt,
///      the structure's split path: the game cuts that face's mount points and splits the grid there if nothing else joins the round's
///      side; a seal that would not break puts both back), torn-edge smoke out of the face (TearSmoke);
///   3. the KICK, only if the game split the grid: a SHOT (static - the game may rebuild either side as the new grid, this breech with
///      it, so no block's component holds it; any mortar component's step moves the shots on) takes the ship grid's next split whose
///      one side holds a mortar round where the round was - that side forward, the other back, by the stack's impulse, at the breech's
///      front face (RigidBodyDataFunctions.TryApplyImpulse, the engine's own - both the same step, once both bodies take it). No
///      split within Mortar.SplitWaitTicks: no impulse.
/// </summary>
public partial class AeroMortarComponent : Component, IInSceneListener
{
    public MortarBlocks.Row Row;
    /// <summary>Fired (its seal broken): spent for good.</summary>
    public bool Fired { get; private set; }
    bool _wasOff; LookRetry _look;
    CubeBlockComponent _cb; PowerableBlockComponent _pb;
    readonly List<AeroMortarChargeComponent> _cands = new(), _stack = new();

    /// <summary>Diagnostics (aeroget AeroMortarComponent.Summary): shots, splits and kicks, fizzles (no split), refusals; the last.</summary>
    public static int Shots, Kicks, Fizzles, Refused; public static string Last = "-";
    /// <summary>The last kick as measured (aeroget AeroMortarComponent.LastCheck): each grid's speed change along the shot and its mass,
    /// a few steps after the impulse - the momenta should match the impulse, opposite.</summary>
    public static string LastCheck = "-";
    public static string Summary => $"shots {Shots} kicks {Kicks} fizzles {Fizzles} refused {Refused}; in flight {_shots.Count}; last: {Last}";
    /// <summary>DEV (harness: aeroset AeroMortarComponent.DevFlip N): a new value flips every breech's switch off, then on the next
    /// tick (the player's off -> on, on the server thread).</summary>
    public static int DevFlip;
    int _devSeen; bool _devOn;
    /// <summary>This breech's state for the harness.</summary>
    public string Status { get; private set; } = "ready";

    void IInSceneListener.OnAddedToScene() { _look.Reset(); _wasOff = false; }
    void IInSceneListener.OnBeforeRemovedFromScene() { }

    [Before(typeof(Keen.VRage.Core.Systems.Simulation))]
    internal class OnMortar : JobGroup;

    [OnMortar]
    [MustHave(typeof(AeroMortarComponent))]
    private static void MortarJob(AeroMortarComponent m) => m.Tick();

    void Tick()
    {
        StepShots();
        if (Entity == null || !GridPhysics.Alive(Data)) return;
        if (!_look.Done)
        {
            if (!_look.Due()) return;
            _cb = Entity.TryGet<CubeBlockComponent>(); _pb = Entity.TryGet<PowerableBlockComponent>();
            Row = MortarBlocks.RowOf(_cb);
            _look.Done = Row != null && _cb != null && _pb != null;
            if (!_look.Done) return;
            _wasOff = !_pb.Enabled; _devSeen = DevFlip;
        }
        if (_devOn) { _devOn = false; _pb.Enabled = true; }
        else if (_devSeen != DevFlip) { _devSeen = DevFlip; _pb.Enabled = false; _devOn = true; }
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
        float roundH = RoundRow(Row.Size)?.Box.Y ?? Row.Box.Y;
        Vector3 sealAt = With(across, a, Mortar.SealMiddle(front, s, Row.Seal));
        Vector3 faceAt = With(across, a, front), roundAt = With(across, a, Mortar.RoundMiddle(front, s, roundH));
        if (kg <= 0f) { Refuse("no charges on its back face (stack charges of its size straight on it)"); return; }
        if (RoundIn(grid.TryGet<BlockOctreeComponent>(), roundAt, Row.Size) == null) { Refuse("no mortar round of its size on its front face"); return; }
        // 2. the seal - the split signal watched and the blocks marked FIRST: the game may split the grid inside the fracture call
        Watching(grid);
        Fired = true;
        foreach (var c in _stack) c.Spend();
        bool killed = false; string why = null;
        try { killed = AeroGridComponent.KillPieceAt(_cb, grid, sealAt, Row.Seal * 0.8f); }
        catch (Exception e) { why = (e.InnerException ?? e).Message; }
        if (!killed)
        {
            Fired = false;
            foreach (var c in _stack) c.Unspend();
            Refuse("the seal would not break" + (why != null ? ": " + why : " (no fracture piece there: content older than the scripts?)"));
            return;
        }
        Shots++;
        var gwt = grid.Data.GetWorldTransform();
        Vector3 dirLocal = Base6Directions.GetVector(_cb.BlockOrientation.TransformDirection(Base6Directions.Direction.Down));
        TearSmoke.Publish(grid, faceAt, dirLocal);
        var shot = new Shot
        {
            Name = Row.Name, Size = Row.Size, Ship = grid, Impulse = Mortar.Impulse(kg), Started = Environment.TickCount64,
            RoundAt = WorldTransform.Transform((Vector3D)roundAt, in gwt), FaceAt = WorldTransform.Transform((Vector3D)faceAt, in gwt),
        };
        shot.Dir = (Vector3)(WorldTransform.Transform((Vector3D)(faceAt + dirLocal), in gwt) - shot.FaceAt);
        lock (_lock) _shots.Add(shot);
        Status = $"fired: {_stack.Count} charges, {kg:F0} kg, {shot.Impulse / 1e3:F0} kN s - waiting for the split";
        Last = $"{Row.Name}: {Status}";
        Log.Default?.Info($"[AERO-MORTAR] {Last}");
    }

    void Refuse(string why)
    {
        Refused++; Status = "not fired: " + why; Last = $"{Row?.Name}: {Status}";
        Log.Default?.Info($"[AERO-MORTAR] {Last}");
    }

    /// <summary>The mortar round of a size (its row: its height).</summary>
    static MortarBlocks.Row RoundRow(string size)
    {
        foreach (var r in MortarBlocks.Rows) if (r.Kind == MortarBlocks.Kind.Round && r.Size == size) return r;
        return null;
    }

    /// <summary>A mortar round of this size in a grid with its middle at 'at' (grid-local m, within a quarter cell); null: none.</summary>
    static CubeBlockComponent RoundIn(BlockOctreeComponent octree, Vector3 at, string size)
    {
        if (octree == null) return null;
        float cell = TearMath.Cell;
        foreach (var b in octree.GetAllCubeBlocks())
        {
            var r = MortarBlocks.RowOf(b);
            if (r == null || r.Kind != MortarBlocks.Kind.Round || r.Size != size) continue;
            var bb = b.AABB;
            var mid = (new Vector3(bb.Min.X, bb.Min.Y, bb.Min.Z) + new Vector3(bb.Max.X, bb.Max.Y, bb.Max.Z)) * 0.5f * cell;
            if ((mid - at).LengthSquared() < cell * cell * 0.0625f) return b;
        }
        return null;
    }

    /// <summary>A grid holds the round (a mortar round of this size with its middle within a quarter cell of a world point).</summary>
    static bool Holds(Entity grid, Vector3D roundWorld, string size)
    {
        if (grid == null || !GridPhysics.Alive(grid.Data)) return false;
        var wt = grid.Data.GetWorldTransform();
        return RoundIn(grid.TryGet<BlockOctreeComponent>(), (Vector3)WorldTransform.TransformInv(roundWorld, wt), size) != null;
    }

    // ── the shots: the split, then the kick ──

    sealed class Shot
    {
        public string Name, Size; public Entity Ship, RoundGrid, ShipGrid; public float Impulse; public long Started, Split;
        public Vector3D RoundAt, FaceAt; public Vector3 Dir;
        public long Kicked; public Vector3 V0Round, V0Ship;
    }
    static readonly List<Shot> _shots = new();
    static long _stepped;

    /// <summary>The shots in flight moved on, once a step (any mortar component's job calls it; the first each step does the work).</summary>
    internal static void StepShots()
    {
        long now = Environment.TickCount64;
        if (_shots.Count == 0) return;
        lock (_lock)
        {
            if (now - _stepped < 8) return;   // (once a step: the jobs run at 60 Hz)
            _stepped = now;
            Prune(now);
            for (int i = _shots.Count - 1; i >= 0; i--)
                if (StepShot(_shots[i], now)) _shots.RemoveAt(i);
        }
    }

    /// <summary>One shot: its split claimed (either side may be the new grid), then the kick; true: done.</summary>
    static bool StepShot(Shot s, long now)
    {
        if (s.Kicked != 0) return now - s.Kicked >= CheckMs && Check(s);
        if (s.RoundGrid == null)
        {
            for (int i = _splits.Count - 1; i >= 0; i--)
            {
                var (o, p, at) = _splits[i];
                if (!ReferenceEquals(o, s.Ship) || at < s.Started - 50) continue;
                // (the game keeps one side as the old grid and spawns the other: the round's side is whichever holds the round)
                if (Holds(p, s.RoundAt, s.Size)) { s.RoundGrid = p; s.ShipGrid = o; }
                else if (Holds(o, s.RoundAt, s.Size) && !Holds(p, s.RoundAt, s.Size)) { s.RoundGrid = o; s.ShipGrid = p; }
                else continue;
                _splits.RemoveAt(i); s.Split = now; break;
            }
            if (s.RoundGrid == null)
            {
                if (now - s.Started < Mortar.SplitWaitTicks * 1000L / 60) return false;
                Fizzles++; Last = $"{s.Name}: fired - no split (the round's side is still joined to the ship): no kick";
                Log.Default?.Info($"[AERO-MORTAR] {Last}");
                return true;
            }
        }
        // (both bodies take it the same step: one momentum - a new grid's body comes a step or two after it)
        Mortar.Kick(s.Impulse, s.Dir, out var onRound, out var onShip);
        if (GridPhysics.Alive(s.RoundGrid.Data) && GridPhysics.Alive(s.ShipGrid.Data))
        {
            AeroPhysics.TryGetVelocity(s.RoundGrid, out s.V0Round, out _); AeroPhysics.TryGetVelocity(s.ShipGrid, out s.V0Ship, out _);
            if (RigidBodyDataFunctions.TryApplyImpulse(onRound, s.FaceAt, s.RoundGrid))
            {
                RigidBodyDataFunctions.TryApplyImpulse(onShip, s.FaceAt, s.ShipGrid);
                Kicks++; Last = $"{s.Name}: fired - the round away, {s.Impulse / 1e3:F0} kN s";
                Log.Default?.Info($"[AERO-MORTAR] {Last}");
                s.Kicked = now;
                return false;
            }
        }
        if (now - s.Split < Mortar.KickWaitTicks * 1000L / 60) return false;
        Fizzles++; Last = $"{s.Name}: fired - split, but the round's grid took no impulse: no kick";
        Log.Default?.Info($"[AERO-MORTAR] {Last}");
        return true;
    }

    /// <summary>How long after the kick its check reads the grids (ms: a few steps - the engine applies an impulse in its next step).</summary>
    const long CheckMs = 100;

    /// <summary>The kick measured: each grid's speed change along the shot (world m/s), its mass, its momentum; true (done).</summary>
    static bool Check(Shot s)
    {
        var d = s.Dir.LengthSquared() > 1e-6f ? Vector3.Normalize(s.Dir) : Vector3.Zero;
        bool okR = AeroPhysics.TryGetVelocity(s.RoundGrid, out var vr, out _), okS = AeroPhysics.TryGetVelocity(s.ShipGrid, out var vs, out _);
        bool mR = GridPhysics.TryGetMassProperties(s.RoundGrid.Data, out float kgR, out _), mS = GridPhysics.TryGetMassProperties(s.ShipGrid.Data, out float kgS, out _);
        float dvR = Vector3.Dot(vr - s.V0Round, d), dvS = Vector3.Dot(vs - s.V0Ship, d);
        LastCheck = $"{s.Name}: round {dvR:F2} m/s x {kgR:F0} kg = {dvR * kgR / 1e3:F2} kN s, ship {dvS:F3} m/s x {kgS:F0} kg = {dvS * kgS / 1e3:F2} kN s, "
            + $"impulse {s.Impulse / 1e3:F2} kN s{(okR && okS && mR && mS ? "" : " (a read failed)")}";
        Log.Default?.Info($"[AERO-MORTAR] check: {LastCheck}");
        return true;
    }

    // the ship grids' split signal (subscribed once per grid that fires; the splits kept a short while for the shots to claim)
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
