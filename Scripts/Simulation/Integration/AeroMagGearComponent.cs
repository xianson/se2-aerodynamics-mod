#pragma warning disable
using System;
using System.Threading;
using Keen.Game2.Simulation.GameSystems.Identities;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks.Wheels;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Core.Game.GameSystems.Queries;
using Keen.VRage.DCS.Annotations;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.Physics;
using Keen.VRage.Physics.Constraints;
using Keen.VRage.Physics.Constraints.Arguments;
using Keen.VRage.Physics.Data;

namespace AeroMod;

/// <summary>
/// THE MAGNETIC LANDING GEAR (server; 2026-10-20, the user: "I'd prefer the vanilla mag behavior and wheel"). On the magnetic gear
/// block, beside its AeroGearComponent (the vanilla suspension, retraction, airshock - unchanged): the block's wheel is a HIDDEN wheel
/// (its own prefab, heavy, never drawn: AeroHiddenWheelComponent), and its foot is a one-block PAD GRID this component spawns - a
/// real vanilla magnetic landing gear block (LandingGear100) - joined rigidly to the hidden wheel (a shared-motion constraint, as the
/// game's connectors and magnets join). The wheel's suspension carries the pad: spring, damper, stroke, retraction and airshock are
/// the gear's; the pad is a real grid, its magnet the vanilla magnet. Pure parts: Gear/MagPad.cs.
///  - LIFE (MagPadLife): built - a new id, the pad spawned at the bottom of the stroke (the gear down, room under it), joined; loaded
///    - the pad found again by its name ("Gear Pad [id]", the id saved with the block: AeroBlockSave), joined again (the game saves
///    no constraint a mod made); lost (destroyed, ground down) - saved so; the gear block repaired rebuilds it. Without a pad the
///    hidden wheel takes the ground, so the gear still carries the ship.
///  - THE MAGNET (MagGates): its own auto-lock is kept off - its detector ignores only the pad's grid, so it would lock onto the
///    parent ship; this component locks it: the parking brake (the ship's P, through the gear block's corner module), the terminal's
///    Lock / Unlock, and its own auto-lock - only onto something that is NOT its own ship (the ship's grids, its wheels and pads); a
///    lock onto its own ship (the vanilla magnet re-locks to whatever its detector sees first) is undone at once.
///  - RETRACTION: refused while locked (AeroGearComponent.RetractVeto); stowed, the pad's collision goes with the wheel's
///    (MagPadCollision); the pad never collides with its own ship (its collision group is the ship's, as the wheels').
///  - THE PAD GRID: its owner follows the ship's (the autopilot's Enemy switch too); no aero of its own (AeroGridComponent.GearPad);
///    a pad no gear claims for OrphanSeconds (its gear ground down, its ship removed) is removed.
/// Every spawn, join, lock and removal runs here, on the server, one gear at a time (the Host lock): the game spawns staged and
/// async (IEntitySpawner.SpawnBundleAsync - as EnemySpawn and the Orbital harness).
/// </summary>
public partial class AeroMagGearComponent : Component, IInSceneListener
{
    public readonly MagPadLife Life = new();
    public readonly MagGates Gates = new();
    /// <summary>The player's auto-lock (saved with the block; the terminal's).</summary>
    public bool AutoLock = true;

    public static bool Enabled = true;
    /// <summary>The pad to its wheel: 0 a shared-motion join (one rigid motion, the game's connector / magnet way), 1 a fixed
    /// constraint (the solver holds them). Harness: aeroset AeroMagGearComponent.JoinMode (new joins only).</summary>
    public static int JoinMode;
    /// <summary>A spawned pad joins only within this of where it should be (m; a pad found after a load: LoadJoinError).</summary>
    public static float MaxJoinError = 0.5f, LoadJoinError = 2f;
    /// <summary>A loaded gear looks this far for its pad (m).</summary>
    public static float SearchRadius = 15f;
    /// <summary>A pad no gear claims this long (s) is removed.</summary>
    public static float OrphanSeconds = 20f;
    /// <summary>Harness: 1 lock, 2 unlock, 3 auto-lock on, 4 auto-lock off, 5 forget the pad (lost: as if destroyed), 6 repair
    /// (as the gear block welded back) - every magnetic gear (aeroset AeroMagGearComponent.DevCommand n, then 0).</summary>
    public static int DevCommand;
    public static int Spawns, Joins, Losses, OrphansRemoved, SelfLockVetoes, Locks, Unlocks;
    internal static readonly List<AeroMagGearComponent> All = new();
    static readonly object Host = new();
    static readonly Random _rnd = new();

    bool _server, _looked, _faulted;
    int _devSeen;
    string _step = "";
    public string Last = "";
    AeroGearComponent _gear; AeroGearVariant _variant; FunctionalBlockComponent _fb; CubeBlockComponent _cb;
    // the pad: its grid, its block's magnet and functional state, the join and the wheel it was made with
    Entity _pad; MagneticBlockComponent _magnet; FunctionalBlockComponent _padFb;
    IPhysicsConstraint _join; Entity _joinWheel; bool _fresh;
    sealed class SpawnJob { public volatile Entity[] Got; public volatile string Failed; }
    SpawnJob _spawn;
    // the ship (its grids, their collision group), re-read once a second
    readonly HashSet<Entity> _ship = new(); int _shipGroup;
    double _shipAt, _ownAt, _searchAt, _roomAt, _lockedAt; bool _room, _lockedForeign;
    bool _padLayerOff;
    /// <summary>The terminal's asks (any thread): 1 lock, 2 unlock.</summary>
    internal volatile int Ask;
    public string MagnetState => Gates.Status;
    public bool Locked => _magnet != null && _magnet.IsLocked;
    public bool PadJoined => Life.Phase == PadPhase.Joined;

    /// <summary>The pad's magnetic face in its grid's frame (m) as measured on the first pad in (NaN: not yet - the variant's guess).</summary>
    static float _learnedFaceY = float.NaN;

    void IInSceneListener.OnAddedToScene()
    {
        try { _server = !string.Equals(Data.Scene?.DebugName, "Client", StringComparison.Ordinal); } catch { _server = true; }
        if (_server) lock (All) All.Add(this);
    }

    void IInSceneListener.OnBeforeRemovedFromScene()
    {
        lock (All) All.Remove(this);
        // (the block going - ground down, its grid removed, the world unloading: the join goes with the wheel's body, which the
        //  vanilla corner module deletes; a pad left unclaimed is removed by the orphan sweep - never here: an unloading world
        //  must keep it, and its save already has it)
        if (_gear != null) { _gear.PadJoined = false; _gear.RetractVeto = null; _gear.PadContact = false; }
        // (its pad off its ship's ray list - under the lock, but never waiting long for it on the scene's thread: a stale entry only
        //  makes the ship's rays skip a body that is gone)
        if (Monitor.TryEnter(Host, 50)) { try { PadBody(false); } finally { Monitor.Exit(Host); } }
        _join = null; _joinWheel = null; _pad = null; _magnet = null; _padFb = null; _spawn = null;
        Life.Reset();
    }

    [Before(typeof(Keen.VRage.Core.Systems.Simulation))]
    private class OnMagGear : JobGroup;

    [OnMagGear]
    [MustHave(typeof(AeroMagGearComponent))]
    private static void MagGearJob(AeroMagGearComponent g)
    {
        if (!g._server || !Enabled || g._faulted) return;
        lock (Host)   // (one gear at a time: spawns, joins, locks and removals are never made from two jobs at once)
        {
            try { g.Tick(); Orphans(g.Entity?.GetSession()); }
            catch (Exception e)
            {
                // (once: a gear that throws stops - an exception every tick costs the frame dearly)
                g._faulted = true; g.Last = "fault in " + g._step + ": " + e.Message;
                if (g._gear != null) { g._gear.RetractVeto = null; g._gear.PadContact = false; }
                Log.Default?.Info($"[AERO-MAGGEAR] fault in {g._step} (gear stopped): {e.GetType().Name}: {e.Message} @ {string.Join(" < ", (e.StackTrace ?? "").Split((char)10).Take(6).Select(l => l.Trim()))}");
            }
        }
    }

    static double Now() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    void Tick()
    {
        double now = Now();
        _step = "setup";
        if (!_looked)
        {
            _looked = true;
            _gear = Entity?.TryGet<AeroGearComponent>(); _variant = AeroGearVariants.Of(Entity);
            _fb = Entity?.TryGet<FunctionalBlockComponent>(); _cb = Entity?.TryGet<CubeBlockComponent>();
        }
        var spec = _variant?.MagPad;
        if (_gear == null || spec == null) { Last = "not a magnetic gear block"; return; }
        var grid = _cb?.Grid?.Entity;
        if (grid == null || !GridPhysics.Alive(grid.Data)) return;
        var cm = _gear.CornerModule;
        var wheel = cm?.WheelEntity;
        bool wheelReady = wheel != null && _gear.WheelConstraint != null && GridPhysics.Alive(wheel.Data);
        if (now - _shipAt > 1.0) { _shipAt = now; RefreshShip(grid); }

        // a joined pad whose wheel went (the vanilla block made a new one): the join went with it - join again
        if (_join != null && !ReferenceEquals(wheel, _joinWheel)) { PadBody(false); _join = null; _joinWheel = null; _gear.PadJoined = false; if (Life.Phase == PadPhase.Joined) Life.Reset(); }

        if (DevCommand != _devSeen) { _devSeen = DevCommand; Dev(DevCommand, cm); }

        // ── the pad's life ──
        _step = "life";
        bool down = _gear.Seq.Down && _gear.Seq.Progress >= 1f && _gear.Travel <= 0f;
        var w = new PadWorld { WheelReady = wheelReady, Functional = _fb == null || _fb.Functional, Down = down };
        var ph = Life.Phase;
        if (ph == PadPhase.Start || ph == PadPhase.Searching || ph == PadPhase.NeedSpawn)
        {
            if (ph == PadPhase.Searching && now >= _searchAt) { _searchAt = now + 0.5; w.Found = Search(grid, wheel, Life.Uid); }
            if (wheelReady && down && now >= _roomAt) { _roomAt = now + 0.25; _room = Room(grid, wheel, spec); }
            w.Room = _room;
        }
        else if (ph == PadPhase.Spawning) { w.Arrived = TakeSpawned(); w.SpawnFailed = _spawn?.Failed != null; }
        else if (ph == PadPhase.Joining) w.Join = wheelReady ? TryJoin(wheel, spec) : 0;
        else if (ph == PadPhase.Joined) w.PadAlive = PadAlive();
        ulong fresh = ph == PadPhase.Start || ph == PadPhase.Searching || ph == PadPhase.NeedSpawn ? MagPadNames.NewUid(_rnd) : 0;
        var did = Life.Step(now, w, fresh);
        switch (did)
        {
            case PadDo.Spawn: SpawnPad(grid, wheel, spec); break;
            case PadDo.Respawn: Log.Default?.Info($"[AERO-MAGGEAR] {Life.Why}"); RemovePad(); break;
            case PadDo.Release: Interlocked.Increment(ref Losses); Log.Default?.Info($"[AERO-MAGGEAR] pad {MagPadNames.Name(Life.Uid)} lost: the gear carries on its hidden wheel"); DropJoin(); _pad = null; _magnet = null; _padFb = null; Say("Magnetic gear pad lost: repair the gear block to rebuild it"); break;
            case PadDo.GiveUp: Log.Default?.Info($"[AERO-MAGGEAR] gave up: {Life.Why}"); RemovePad(); break;
        }
        _gear.PadJoined = Life.Phase == PadPhase.Joined && _join != null;

        // ── the magnet ──
        _step = "magnet";
        if (_gear.PadJoined && _magnet != null) Magnet(now, cm, down, spec);
        else
        {
            _gear.RetractVeto = null; _gear.PadContact = false;
            if (Ask != 0) { Ask = 0; Last = "no pad to lock"; }
        }
        _step = "dev";
        DevStep(now, grid, cm);   // (the harness's hop and recorder - Dev/AeroMagGearDev.cs; two reads while off)
    }

    // ── the magnet ──

    void Magnet(double now, WheelCornerModuleComponent cm, bool down, MagPadSpec spec)
    {
        var m = _magnet;
        if (m.AutoLock) m.AutoLock = false;   // (ours, never the vanilla's: its detector would take the parent ship)
        bool locked = m.IsLocked;
        if (!locked) _lockedAt = 0;
        else if (now >= _lockedAt) { _lockedAt = now + 0.25; _lockedForeign = Foreign(m.LockedEntity); }   // (LockedEntity enumerates: 4 Hz)
        var ready = m.ReadyToLockEntity;
        // the terminal's Lock / Unlock: as the parking brake (P) - its brake on locks when ready, off unlocks
        int ask = Ask;
        if (ask != 0)
        {
            Ask = 0;
            if (ask == 1 && cm != null && !cm.IsParked) ((IParkingBrake)cm).RequestToggle();
            else if (ask == 2) { if (cm != null && cm.IsParked) ((IParkingBrake)cm).RequestToggle(); else if (locked) { m.RequestUnlock(); Interlocked.Increment(ref Unlocks); } }
        }
        var seen = new MagSeen
        {
            Joined = true, Down = down, Locked = locked, LockedForeign = locked && _lockedForeign,
            Ready = ready != null, ReadyForeign = ready != null && Foreign(ready),
            CooldownDone = m.AutoLockCooldown == null || m.AutoLockCooldown.IsCooldownFinished,
            Working = _padFb == null || (_padFb.Functional && _padFb.Working), AutoLock = AutoLock, Parked = cm != null && cm.IsParked,
        };
        int vetoes = Gates.SelfLockVetoes;
        var act = Gates.Decide(seen, out bool flip);
        if (Gates.SelfLockVetoes != vetoes)
        {
            Interlocked.Increment(ref SelfLockVetoes);
            Log.Default?.Info($"[AERO-MAGGEAR] pad {MagPadNames.Name(Life.Uid)} had locked onto its own ship: unlocked");
        }
        if (act == MagAct.Lock) { m.RequestLock(); Interlocked.Increment(ref Locks); _lockedAt = 0; }
        else if (act == MagAct.Unlock) { m.RequestUnlock(); Interlocked.Increment(ref Unlocks); }
        if (flip && cm != null) ((IParkingBrake)cm).RequestToggle();
        _gear.RetractVeto = MagGates.RetractRefusal(locked);
        _gear.PadContact = ready != null || locked;

        // stowed: the pad's collision off with the wheel's (MagPadCollision; never its own ship anyway - its collision group)
        float offAt = _variant?.CollisionOffAt ?? 1f;
        SetPadCollision(MagPadCollision.Pad(true, _gear.Travel, offAt));

        // its owner: the ship's (once a second - the autopilot's Enemy switch makes the ship the hostile NPC's)
        if (now - _ownAt > 1.0) { _ownAt = now; FollowOwner(); }
    }

    /// <summary>Not its own ship: not one of the ship's grids, not this pad, not a body in the ship's collision group (its wheels,
    /// its pads).</summary>
    bool Foreign(Entity e)
    {
        if (e == null) return false;
        var top = e.GetTopLevelParent() ?? e;
        if (ReferenceEquals(top, _pad) || _ship.Contains(top) || _ship.Contains(e)) return false;
        if (_shipGroup != 0 && GridPhysics.Alive(top.Data) && top.Data.TryGet<GroupCollisionFilterData>(out var gf) && gf.SystemGroupId == _shipGroup) return false;
        return true;
    }

    void RefreshShip(Entity grid)
    {
        _ship.Clear(); _ship.Add(grid);
        var cg = grid.TryGet<CubeGridComponent>();
        if (cg != null) foreach (var g in cg.GetAllGridsInShip()) if (g != null) _ship.Add(g);   // (an enumeration: once a second)
        _shipGroup = grid.Data.TryGet<GroupCollisionFilterData>(out var gf) ? gf.SystemGroupId : 0;
    }

    void FollowOwner()
    {
        if (_pad == null || !GridPhysics.Alive(_pad.Data)) return;
        var grid = _cb?.Grid?.Entity;
        if (grid == null || !ApEnemyGame.TryOwner(grid, out var ship) || !ApEnemyGame.TryOwner(_pad, out var pad) || ship.Equals(pad)) return;
        bool ok = ApEnemyGame.IsEnemy(grid) ? ApEnemyGame.IsEnemy(_pad) || ApEnemyGame.MakeEnemy(_pad, out _) : ApEnemyGame.GiveTo(_pad, ship);
        Log.Default?.Info($"[AERO-MAGGEAR] pad {MagPadNames.Name(Life.Uid)}: owner to the ship's ({(ok ? "done" : "refused")})");
    }

    void SetPadCollision(bool on)
    {
        if (on != _padLayerOff) return;   // (already so)
        var off = AeroGearComponent.NoCollisionLayer();
        if (off == null || _pad == null || !GridPhysics.Alive(_pad.Data)) return;
        var grid = _cb?.Grid?.Entity;
        if (!on) { _pad.Data.Set(off.Value); _padLayerOff = true; }
        else if (grid != null && grid.Data.TryGet<EntityLayerData>(out var shipLayer)) { _pad.Data.Set(shipLayer); _padLayerOff = false; }   // (a grid's layer: the ship's)
    }

    // ── the pad's life, the game's side ──

    /// <summary>Where the pad grid goes (world): its magnetic face Clearance above the hidden wheel's lowest point, turned as the
    /// gear block (the pad's down is the block's down).</summary>
    WorldTransform PadPose(Entity wheel, MagPadSpec spec)
    {
        var bwt = Entity.Data.GetWorldTransform();
        var wwt = wheel.Data.GetWorldTransform();
        Vector3D hub = wwt.Position;
        if (wheel.Data.TryGet<BoundingBoxData>(out var bb)) hub = WorldTransform.Transform((Vector3D)bb.BoundingBox.Center, in wwt);   // (the wheel's collider centre)
        var up = Vector3D.Transform(Vector3D.Up, bwt.Orientation);
        float r = _variant?.WheelRadius ?? 0.75f;
        float faceY = float.IsNaN(_learnedFaceY) ? spec.FaceY : _learnedFaceY;
        Vector3D face = hub - up * (r - spec.Clearance);
        return new WorldTransform(face - up * faceY, bwt.Orientation);
    }

    /// <summary>Nothing but its own ship in the pad's box (other grids, characters, floating objects - not a planet: the hidden wheel
    /// stands on the ground, the face spawns above its lowest point).</summary>
    static Keen.VRage.Library.Memory.Buffer<Keen.VRage.DCS.Accessors.DEntity> _hits;
    bool Room(Entity grid, Entity wheel, MagPadSpec spec)
    {
        var physics = GridPhysics.Physics;
        if (physics == null) return false;
        var at = PadPose(wheel, spec).Position;
        var args = new AABBQueryArgs(new BoundingBoxD(at - new Vector3D(0.6), at + new Vector3D(0.6)));
        _hits.Clear();
        physics.QueryAABB(_hits, in args, CollisionPreset.Bodies);   // (args own no buffer)
        var scene = grid.Scene;
        for (int i = 0; i < _hits.Count; i++)
        {
            var e = Entity.TryGetFromDataEntity((Scene: scene, Entity: _hits[i]));
            if (e == null || !GridPhysics.Alive(e.Data) || !Foreign(e)) continue;
            if (ReferenceEquals(e, wheel)) continue;
            var box = BoundingBoxData.TryGetWorldAABB(e.Data);
            if (!box.HasValue) continue;
            double size = (box.Value.Max - box.Value.Min).Length() * 0.5;
            if (size > 0.05 && size <= ApGameObstacles.MaxRadius) { Last = "something under the gear: no pad yet"; _hits.Clear(); return false; }
        }
        _hits.Clear();
        return true;
    }

    /// <summary>The pad grid with this id near the wheel, not another gear's.</summary>
    bool Search(Entity grid, Entity wheel, ulong uid)
    {
        if (uid == 0 || wheel == null) return false;
        var session = grid.GetSession();
        if (session == null) return false;
        string want = MagPadNames.Name(uid);
        var at = wheel.Data.GetWorldTransform().Position;
        foreach (var e in session.GetEntitiesOfType<CubeGridComponent>())
        {
            if (!GridPhysics.Alive(e.Data) || (e.Data.GetWorldTransform().Position - at).LengthSquared() > SearchRadius * SearchRadius) continue;
            string n;
            try { n = e.TryGet<CubeGridComponent>()?.DisplayName.EvaluateText(); } catch { continue; }   // (a search: a few times after a load)
            if (n != want || Claimed(e, this)) continue;
            _pad = e; _magnet = null; _padFb = null; _fresh = false;
            Log.Default?.Info($"[AERO-MAGGEAR] found its pad {want}");
            return true;
        }
        return false;
    }

    static bool Claimed(Entity pad, AeroMagGearComponent except)
    {
        lock (All) foreach (var g in All) if (!ReferenceEquals(g, except) && ReferenceEquals(g._pad, pad)) return true;
        return false;
    }

    void SpawnPad(Entity grid, Entity wheel, MagPadSpec spec)
    {
        var job = new SpawnJob(); _spawn = job; _pad = null; _magnet = null; _padFb = null; _fresh = true;
        var session = grid.GetSession();
        var spawner = session?.Get<IEntitySpawner>();
        if (spawner == null) { job.Failed = "no spawner"; return; }
        if (!DefinitionManager.Instance.TryGetDefinition(spec.PadPrefab, out PrefabDefinition pd) || pd == null) { job.Failed = "no pad block " + spec.PadPrefab; return; }
        var at = PadPose(wheel, spec);
        string name = MagPadNames.Name(Life.Uid);
        var gb = new GridBuilder(session);
        gb.SetName(name); gb.SetDebugName(name);
        gb.SetMotionType(BodyArgs.Motion.Dynamic);
        gb.SetTransform(at);
        if (GridPhysics.TryGetVelocity(wheel.Data, out Vector3 lin, out Vector3 ang)) { gb.SetLinearVelocity(lin); gb.SetAngularVelocity(ang); }   // (moving with it)
        ApEnemyGame.TryOwner(grid, out IdentityId owner);
        gb.SetOwner(owner);
        var b = pd.Get();
        var bo = b.TryGetOB<OwnershipComponent, OwnershipObjectBuilder>();
        if (bo != null) bo.Owner = owner;
        var mo = b.TryGetOB<MagneticBlockComponent, MagneticBlockObjectBuilder>();
        if (mo != null) mo.AutoLock = false;   // (ours: MagGates)
        gb.AddBlock(Vector3I.Zero, new IntegerOrientation(Base6Directions.Direction.Forward, Base6Directions.Direction.Up), b);
        var bundle = new EntityBundle();
        bundle.Add(gb.CompileObjectBuilder(), null, true);
        Interlocked.Increment(ref Spawns);
        Log.Default?.Info($"[AERO-MAGGEAR] spawning its pad {name} at ({at.Position.X:F1}, {at.Position.Y:F1}, {at.Position.Z:F1})");
        Run(spawner, bundle, job);
    }

    static async void Run(IEntitySpawner spawner, EntityBundle bundle, SpawnJob job)
    {
        try
        {
            var ents = await spawner.SpawnBundleAsync(new GameEntitySerializer.BundleInitArgs { Bundle = bundle });
            if (ents == null || ents.Length == 0) job.Failed = "nothing spawned"; else job.Got = ents;
        }
        catch (Exception e) { job.Failed = (e.InnerException ?? e).Message; }   // (one spawn, never per tick)
    }

    bool TakeSpawned()
    {
        var ents = _spawn?.Got;
        if (ents == null) return false;
        foreach (var e in ents) if (e != null && e.TryGet<CubeGridComponent>() != null) { _pad = e; break; }
        _spawn = null;
        return _pad != null;
    }

    /// <summary>The pad's block (its magnet), found once it is in.</summary>
    bool FindPadBlock()
    {
        var kids = _pad?.TryGet<HierarchyComponent>()?.Children;
        if (kids == null) return false;
        foreach (var k in kids)
        {
            var mg = k?.TryGet<MagneticBlockComponent>();
            if (mg == null) continue;
            _magnet = mg; _padFb = k.TryGet<FunctionalBlockComponent>();
            return true;
        }
        return false;
    }

    /// <summary>The join: 1 made, 2 the pad is too far from where it should be (or gone), 0 not yet (its body, its block not in).</summary>
    int TryJoin(Entity wheel, MagPadSpec spec)
    {
        if (_pad == null) return 2;
        if (!GridPhysics.Alive(_pad.Data)) return 2;
        if (_magnet == null && !FindPadBlock()) return 0;
        var physics = GridPhysics.Physics;
        if (physics == null || !_pad.Data.Has<RigidBodyData>()) return 0;
        // (the first pad in: where its face really is in its grid - the variant's FaceY was a guess; later pads spawn by this)
        if (float.IsNaN(_learnedFaceY) && _pad.Data.TryGet<BoundingBoxData>(out var pb))
        {
            _learnedFaceY = pb.BoundingBox.Min.Y;
            Log.Default?.Info($"[AERO-MAGGEAR] the pad's face is {_learnedFaceY:F3} m down its grid (the guess: {spec.FaceY:F3})");
        }
        var want = PadPose(wheel, spec);
        double err = (_pad.Data.GetWorldTransform().Position - want.Position).Length();
        if (err > (_fresh ? MaxJoinError : LoadJoinError)) { Last = $"pad {err:F2} m off where it should be"; return 2; }
        // (its collision group: the ship's, as the wheels' - it never meets its own ship, its wheels or its other pads; none yet - the
        //  ship's wheels give the ship one when they are made: wait for it, a pad without it would meet its own wheel)
        if (_shipGroup == 0) { _shipAt = 0; Last = "waiting for the ship's collision group"; return 0; }
        _pad.Data.Set(new GroupCollisionFilterData { SystemGroupId = _shipGroup, SubsystemId = 1, SubsystemNotToCollideWith = 1 });
        var p = _pad.Data.GetWorldTransform().Position;
        IConstraintArgs args = JoinMode == 1 ? ConstraintHelper.CreateFixed(_pad.Data, wheel.Data, p) : ConstraintHelper.CreateSharedFixed(_pad.Data, wheel.Data, p);
        _join = physics.AllocateConstraint(_pad.DEntity, wheel.DEntity, args);   // (A the pad, constrained to B the wheel)
        if (_join == null) { Last = "the join could not be made"; return 0; }
        _joinWheel = wheel;
        PadBody(true);
        var aero = _pad.TryGet<AeroGridComponent>();
        if (aero != null) aero.GearPad = true;   // (no aero of its own: AeroGridComponent.PadStep)
        if (_magnet.AutoLock) _magnet.AutoLock = false;
        _padLayerOff = true;   // (it came in with no collision - AeroGridComponent.PadOnAdded: the magnet step gives the ground back when down)
        Interlocked.Increment(ref Joins);
        Log.Default?.Info($"[AERO-MAGGEAR] pad {MagPadNames.Name(Life.Uid)} joined to its wheel ({(JoinMode == 1 ? "fixed" : "shared motion")}, {err:F3} m off)");
        return 1;
    }

    bool PadAlive() => _pad != null && GridPhysics.Alive(_pad.Data) && _magnet != null && _magnet.Entity != null && GridPhysics.Alive(_magnet.Entity.Data);

    /// <summary>The join undone - only while both bodies live (a body's removal takes its constraints with it: destroying one twice
    /// asserts in the engine).</summary>
    void DropJoin()
    {
        PadBody(false);
        var j = _join; _join = null;
        var wheel = _joinWheel; _joinWheel = null;
        if (_gear != null) _gear.PadJoined = false;
        if (j == null || _pad == null || wheel == null || !GridPhysics.Alive(_pad.Data) || !GridPhysics.Alive(wheel.Data)) return;
        GridPhysics.Physics?.DestroyConstraint(j);
    }

    /// <summary>The pad's body on its ship's list (its rays and obstacle scans skip it: AeroGridComponent.PadBodies) - or off.</summary>
    Entity _padListedOn; DEntity _padListed;
    void PadBody(bool on)
    {
        if (_padListedOn != null) { if (GridPhysics.Alive(_padListedOn.Data)) _padListedOn.TryGet<AeroGridComponent>()?.SetPadBody(_padListed, false); _padListedOn = null; }
        if (!on || _pad == null) return;
        var grid = _cb?.Grid?.Entity;
        var aero = grid?.TryGet<AeroGridComponent>();
        if (aero == null) return;
        _padListed = _pad.DEntity; _padListedOn = grid;
        aero.SetPadBody(_padListed, true);
    }

    /// <summary>The pad removed (a respawn, given up), the join first.</summary>
    void RemovePad()
    {
        DropJoin();
        var pad = _pad; _pad = null; _magnet = null; _padFb = null; _spawn = null;
        if (pad != null && GridPhysics.Alive(pad.Data)) pad.GetSession()?.Get<IEntitySpawner>()?.DeleteEntity(pad);
    }

    void Dev(int c, WheelCornerModuleComponent cm)
    {
        switch (c)
        {
            case 1: Ask = 1; break;
            case 2: Ask = 2; break;
            case 3: AutoLock = true; break;
            case 4: AutoLock = false; break;
            case 5: if (Life.Phase == PadPhase.Joined) RemovePad(); break;   // (its PadAlive goes false: lost, as if destroyed)
            case 6: Life.Repair(); break;
        }
    }

    // ── the player's toast (host / single player, as every mod panel): a refused retraction, a lost pad ──

    public static volatile string PendingSay;
    internal static void Say(string text) => PendingSay = text;

    /// <summary>The client host's tick (Runtime/AeroClientHost.cs): a pending line shown once.</summary>
    internal static void ClientTick(Entity anyClient)
    {
        var say = PendingSay;
        if (say == null) return;
        PendingSay = null;
        try { anyClient?.GetSession()?.TryGet<Keen.Game2.Simulation.GameSystems.UserMessages.IUserMessages>()?.DisplayMessage(ModToast.Literal("Magnetic gear", say, 4, "aero.maggear")); }
        catch { }   // (a line now and then, never per frame)
    }

    // ── orphans: pads no gear claims ──

    static readonly Dictionary<Entity, double> _padsSeen = new();
    static readonly List<Entity> _drop = new(), _orphans = new(), _claimed = new();
    static volatile int _padsSeenCount;
    static double _orphanAt;

    /// <summary>A pad grid's own step (AeroGridComponent.PadStep, server, once a second): it exists.</summary>
    internal static void PadSeen(Entity pad)
    {
        if (pad == null) return;
        lock (_padsSeen) if (!_padsSeen.ContainsKey(pad)) { _padsSeen[pad] = Now(); _padsSeenCount = _padsSeen.Count; }
    }

    /// <summary>The server planets' job (no magnetic gear left to run it): the orphan sweep.</summary>
    internal static void ServerTick(Keen.VRage.Core.Game.Systems.Session session)
    {
        if (_padsSeenCount == 0) return;
        lock (Host) Orphans(session);
    }

    /// <summary>Every 2 s: a pad no gear has claimed for OrphanSeconds (since it was first seen, or last claimed) is removed; gone ones
    /// forgotten.</summary>
    static void Orphans(Keen.VRage.Core.Game.Systems.Session session)
    {
        if (_padsSeenCount == 0 || session == null) return;
        double now = Now();
        if (now < _orphanAt) return;
        _orphanAt = now + 2.0;
        _drop.Clear(); _orphans.Clear();
        lock (_padsSeen)
        {
            foreach (var kv in _padsSeen)
            {
                if (!GridPhysics.Alive(kv.Key.Data)) { _drop.Add(kv.Key); continue; }
                if (Claimed(kv.Key, null)) _claimed.Add(kv.Key);   // (claimed: its clock starts again)
                else if (now - kv.Value > OrphanSeconds) _orphans.Add(kv.Key);
            }
            foreach (var e in _claimed) _padsSeen[e] = now;
            _claimed.Clear();
            foreach (var e in _drop) _padsSeen.Remove(e);
            foreach (var e in _orphans) _padsSeen.Remove(e);
            _padsSeenCount = _padsSeen.Count;
        }
        if (_orphans.Count == 0) return;
        var spawner = session.Get<IEntitySpawner>();
        foreach (var e in _orphans)
        {
            string n = "?";
            try { n = e.TryGet<CubeGridComponent>()?.DisplayName.EvaluateText(); } catch { }   // (a removal: rare)
            spawner?.DeleteEntity(e); Interlocked.Increment(ref OrphansRemoved);
            Log.Default?.Info($"[AERO-MAGGEAR] pad {n} removed: no gear claims it");
        }
        _orphans.Clear(); _drop.Clear();
    }

    // ── the harness ──

    /// <summary>Harness: every magnetic gear (aeroget AeroMagGearComponent.Summary; built on the server step - ServerDevReads).</summary>
    public static string Summary => ServerDevReads.Summary(ServerDevReads.MagGears);
    public static string Counters => $"spawns {Spawns} joins {Joins} losses {Losses} orphans {OrphansRemoved} selflock {SelfLockVetoes} locks {Locks} unlocks {Unlocks}";

    internal static string BuildSummary()
    {
        var sb = new System.Text.StringBuilder();
        lock (All)
        {
            sb.Append(All.Count).Append(" magnetic gears; ").Append(Counters);
            foreach (var g in All)
            {
                sb.Append(" | ").Append(g.Life.Phase).Append(' ').Append(MagPadNames.Name(g.Life.Uid));
                if (g._pad != null && GridPhysics.Alive(g._pad.Data))
                {
                    var pw = g._pad.Data.GetWorldTransform();
                    var bw = g.Entity.Data.GetWorldTransform();
                    var rel = WorldTransform.GetRelativeTransform(pw, bw);
                    var upPad = Vector3D.Transform(Vector3D.Up, pw.Orientation); var upBlock = Vector3D.Transform(Vector3D.Up, bw.Orientation);
                    double tilt = Math.Acos(Math.Clamp(Vector3D.Dot(upPad, upBlock), -1.0, 1.0)) * 57.2958;
                    sb.Append($" pad at ({rel.Position.X:F2}, {rel.Position.Y:F2}, {rel.Position.Z:F2}) in the block, tilt {tilt:F1} deg");
                }
                if (g._magnet != null) sb.Append(g._magnet.IsLocked ? " LOCKED" : g._magnet.ReadyToLockEntity != null ? " ready" : " free");
                sb.Append(" auto ").Append(g.AutoLock ? 1 : 0).Append(" park ").Append(g._gear?.CornerModule != null && g._gear.CornerModule.IsParked ? 1 : 0)
                  .Append(" travel ").Append((g._gear?.Travel ?? 0f).ToString("F2"));
                if (g.Gates.Status.Length > 0) sb.Append(" (").Append(g.Gates.Status).Append(')');
                if (g.Life.Why.Length > 0) sb.Append(" [").Append(g.Life.Why).Append(']');
                if (g.Last.Length > 0) sb.Append(" {").Append(g.Last).Append('}');
                if (g._gear != null && g._gear.Refused.Length > 0) sb.Append(" refused: ").Append(g._gear.Refused);
            }
        }
        return sb.ToString();
    }
}
