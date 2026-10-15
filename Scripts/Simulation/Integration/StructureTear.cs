#pragma warning disable
using System;
using System.Reflection;
using System.Threading;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.BlockOctrees;
using Keen.VRage.Core;
using Keen.VRage.DCS.Annotations;
using Keen.VRage.Physics;
using Keen.VRage.Physics.Constraints;
using Keen.VRage.Physics.Constraints.Arguments;
using Keen.VRage.Physics.Data;
namespace AeroMod;

/// <summary>
/// TEARING (2026-10-14, the user's idea): when the structure breaks blocks (StructureServer.ApplyStructureDamage) and the game
/// splits a LARGE chunk off (CubeGridComponent.SplitGridSpawnedSignal, the next tick), the two grids stay tied for a moment
/// by soft physics constraints at the torn joint - one shared position motor on the linear axes of three 6-DoF
/// constraints (the joint and two points spread across it: their lever arms hold the turning; angular axes free) - and
/// the motor's force is ramped to nothing over TearSeconds (TearMath.Ramp: holds, sags, flutters, lets go), then the
/// constraints are destroyed and the motor disposed. Small bits (under MinMass) pop off at once, as before.
///  - the pattern is the game's own (MechanicalBlockCounterpartAlignmentComponent.MakeLinearMotorConstraint: two 6-DoF
///    constraints between two grids, one position motor on their linear axes, MaxForce / MinForce set each tick, the
///    constraints destroyed and then the motor disposed); 6-DoF, not fixed (fixed counts as "connected to static");
///    the args by new() (default leaves their arrays null: HavokConstraintManager throws);
///  - SERVER scene only (grid splits are the server's; constraints are not replicated - clients see the transforms);
///  - every create / tune / destroy in ONE job a tick (TearJob: the first server grid's claims it, after every grid's
///    attitude job and before the physics step), never in the split signal (it runs inside the game's block-change job:
///    it only queues);
///  - GUARDED: a body deleted (the piece destroyed, the grid's body rebuilt) destroys its constraints in the engine and
///    frees their data, and the motor's setters then touch it (HavokConstraintManager.ActivateMotorBodies keeps the dead
///    constraint) - so each tick, before any call: both entities alive, both bodies there, and no constraint flagged
///    Destroyed (HavokConstraint.Destroyed, by reflection: docs/REFLECTION_AND_WHITELIST.md); otherwise the handles are
///    dropped with no call into them (a lost motor is a few bytes). A throw drops that tear (Faults);
///  - while tied, the pair are one connected body to the engine (PhysicsHack.IsSubPart: the piece a sub-part; the Orbital
///    mod's GridMembers.IsConstrained: neither moved on rails) - for TearSeconds only.
///  - every split that follows our break - a light piece too - publishes its torn edge for the client's smoke (TearSmoke,
///    2026-10-14: the joint in the piece's frame, out of it), before the checks that let a light one pop off.
/// Idle cost: one volatile read per grid per tick (AeroSimJob arms the job only while something is queued or tied).
/// </summary>
public static class StructureTear
{
    /// <summary>Tear large chunks slowly (off: every split is instant, as the game does it).</summary>
    public static bool Enabled = true;
    /// <summary>Pieces at least this heavy (kg) tear slowly; lighter ones pop off at once.</summary>
    public static float MinMass = 2000f;
    /// <summary>Seconds from the split until the piece is free (the hold ramps to nothing over them).</summary>
    public static float TearSeconds = 1.2f;
    /// <summary>The hold at the start, in g of the piece's own weight (the three anchors together; 6: holds ~6 g).</summary>
    public static float ForceFactor = 6f;
    /// <summary>The motor's stiffness (0..1: the share of the error it corrects a step) and damping (the game's alignment
    /// uses 0.8 / 1).</summary>
    public static float Tau = 0.5f, Damping = 1f;
    /// <summary>How far (m) the two extra anchors sit out across the joint (the joint's own half-extent, if wider).</summary>
    public static float AnchorSpread = 1.5f;
    /// <summary>A broken block counts as the piece's joint when its centre is this close (m) to one of the piece's blocks.</summary>
    public static float PairRange = 3f;
    /// <summary>Most tears at once (more: instant).</summary>
    public static int MaxActive = 8;
    /// <summary>Ticks a break waits for its split (no split: forgotten), and a split waits for both bodies.</summary>
    public static int PendingTicks = 10, BodyWaitTicks = 30;

    // diagnostics (harness aeroget)
    public static string Status = "-";
    /// <summary>Tears holding now; done (ramped out and destroyed); skipped (light, static, no joint near, too many);
    /// lost (a body went mid-tear - the handles dropped, nothing called); faults (a throw - that tear dropped).</summary>
    public static int Active, Torn, Skipped, Lost, Faults, Splits;

    sealed class Pending { public Entity Grid; public readonly List<Vector3> Local = new(); public int Age; }
    struct Spawned { public Entity Original, Piece; public int Age; }
    sealed class Tear
    {
        public Entity Piece, Original; public IPhysics Phys;
        public IPositionConstraintMotor Motor; public readonly IPhysicsConstraint[] C = new IPhysicsConstraint[3];
        public float F0; public int Age, Steps;
    }

    static readonly object _lock = new();
    static readonly List<Pending> _pending = new();       // (_lock: the sim jobs add)
    static readonly List<Spawned> _spawned = new();       // (_lock: the split signal adds)
    static readonly List<Spawned> _waiting = new();       // (the tear job's only)
    static readonly List<Tear> _active = new();           // (the tear job's only)
    /// <summary>Anything queued or tied (the sim job arms the tear job only then).</summary>
    internal static volatile int Work;
    /// <summary>Set by any server grid's sim job this tick while Work; the first tear job takes it.</summary>
    internal static int Armed;
    static CubeGridComponent.SplitGridSpawnedSignal.ExternalApi _onSplit;
    static readonly Vector3D[] _joint = new Vector3D[32];
    static readonly Vector3D[] _anchors = new Vector3D[3];

    /// <summary>(StructureServer.ApplyStructureDamage, before blocks are destroyed) the grid's split signal, once per grid.</summary>
    internal static void Watch(AeroGridComponent aero)
    {
        if (!Enabled || aero._tearWatched || aero.Entity == null) return;
        aero._tearWatched = true;
        try { _onSplit ??= OnSplit; CubeGridComponent.SplitGridSpawnedSignal.Subscribe(aero.Entity, _onSplit); }
        catch (Exception e) { Status = "watch failed: " + e.Message; }
    }

    /// <summary>(StructureServer.ApplyStructureDamage) blocks of this grid broke: where (grid-local centres), for the split
    /// that may follow.</summary>
    internal static void Record(Entity grid, List<Vector3> local)
    {
        if (!Enabled || grid == null || local == null || local.Count == 0) return;
        var p = new Pending { Grid = grid };
        p.Local.AddRange(local);
        lock (_lock) { if (_pending.Count < 32) _pending.Add(p); Work = 1; }
    }

    /// <summary>The game split a grid (inside its block-change job): queued only.</summary>
    static void OnSplit(Entity original, Entity piece)
    {
        if (!Enabled || original == null || piece == null) return;
        lock (_lock) { if (_spawned.Count < 32) { _spawned.Add(new Spawned { Original = original, Piece = piece }); Work = 1; } }
    }

    /// <summary>The tear work, once a tick (AeroGridComponent.TearJob: after every grid's attitude job, before the physics
    /// step; the first server grid's job to come takes it).</summary>
    internal static void Step()
    {
        var phys = PhysicsHack.Physics;
        lock (_lock)
        {
            for (int i = 0; i < _spawned.Count; i++) _waiting.Add(_spawned[i]);
            _spawned.Clear();
        }
        // the splits: paired with a break of their grid, tied once both bodies are in
        for (int i = _waiting.Count - 1; i >= 0; i--)
        {
            var w = _waiting[i]; w.Age++; _waiting[i] = w;
            bool done = true;
            try { done = TryStart(phys, w); }
            catch (Exception e) { Faults++; Status = $"start failed: {e.GetType().Name}: {e.Message}"; Log.Default?.Info($"[AERO-TEAR] {Status}"); }
            if (done) _waiting.RemoveAt(i);
        }
        // the tears: the hold ramped down, then let go
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var t = _active[i];
            bool done;
            try { done = TickTear(t); }
            catch (Exception e)
            {
                Faults++; Status = $"tear of {Name(t.Piece)} failed: {e.GetType().Name}: {e.Message}"; Log.Default?.Info($"[AERO-TEAR] {Status}");
                try { if (Usable(t)) Release(t); } catch { }
                done = true;
            }
            if (done) _active.RemoveAt(i);
        }
        Active = _active.Count;
        lock (_lock)
        {
            for (int i = _pending.Count - 1; i >= 0; i--) if (++_pending[i].Age > PendingTicks) _pending.RemoveAt(i);
            Work = _pending.Count + _spawned.Count + _waiting.Count + _active.Count;
        }
    }

    static string Name(Entity e) { try { return e?.DebugName ?? "?"; } catch { return "?"; } }

    /// <summary>A split: true when dealt with (tied, skipped, not ours), false to wait (bodies not in yet).</summary>
    static bool TryStart(IPhysics phys, Spawned w)
    {
        if (!Enabled || phys == null) return true;
        var od = w.Original.Data; var pd = w.Piece.Data;
        if (!PhysicsHack.Alive(od) || !PhysicsHack.Alive(pd)) return true;
        // (only a split that follows our break: one of this grid's breaks, still fresh - each kept while its splits wait)
        bool ours = false;
        lock (_lock) foreach (var p in _pending) if (p.Grid == w.Original) { ours = true; p.Age = 0; }
        if (!ours) return true;
        if (!phys.HasBody(w.Piece.DEntity) || !phys.HasBody(w.Original.DEntity))
        {
            if (w.Age > BodyWaitTicks) { Skipped++; Status = $"{Name(w.Piece)}: no body after {w.Age} ticks"; return true; }
            return false;
        }
        Splits++;
        var oct = w.Piece.TryGet<BlockOctreeComponent>();
        if (oct == null) { Skipped++; Status = $"{Name(w.Piece)}: no blocks"; return true; }
        PhysicsHack.TryGetMassProperties(pd, out float mass, out Vector3 comLocal);
        var wtO = od.GetWorldTransform(); var wtP = pd.GetWorldTransform();
        // the joint: the broken blocks (this grid's breaks) within PairRange of one of the piece's blocks
        int n = 0;
        var blocks = oct.GetAllCubeBlocks();
        lock (_lock)
            foreach (var p in _pending)
            {
                if (p.Grid != w.Original) continue;
                foreach (var a in p.Local)
                {
                    if (n >= _joint.Length) break;
                    Vector3D world = WorldTransform.Transform((Vector3D)a, in wtO);
                    Vector3 lp = (Vector3)WorldTransform.TransformInv(world, wtP);
                    float best = float.MaxValue;
                    foreach (var b in blocks)
                    {
                        if (b == null) continue;
                        var bb = b.AABB;
                        TearMath.CellBox(bb.Min, bb.Max, out var mn, out var mx);
                        float d = TearMath.BoxDistance(mn, mx, lp);
                        if (d < best) { best = d; if (d <= 0f) break; }
                    }
                    if (best <= PairRange) _joint[n++] = world;
                }
            }
        // (TORN-EDGE SMOKE, 2026-10-14: every piece that follows our break - light or heavy - smokes at its torn edge, out of
        //  it: the joint in the piece's own frame, away from its centre of mass; the ship's side was published at the break)
        if (n > 0)
        {
            var jl = (Vector3)WorldTransform.TransformInv(_joint[0], wtP);
            TearSmoke.Publish(w.Piece, jl, jl - comLocal);
        }
        if (_active.Count >= MaxActive) { Skipped++; Status = $"{Name(w.Piece)}: {MaxActive} tears already"; return true; }
        if (!pd.Has<DynamicTag>()) { Skipped++; Status = $"{Name(w.Piece)}: not dynamic"; return true; }
        if (!(mass >= MinMass)) { Skipped++; Status = $"{Name(w.Piece)}: {mass:F0} kg, pops off"; return true; }
        if (n == 0) { Skipped++; Status = $"{Name(w.Piece)}: no broken block near it"; return true; }
        Vector3D com = WorldTransform.Transform((Vector3D)comLocal, in wtP);
        int na = TearMath.Anchors(new ReadOnlySpan<Vector3D>(_joint, 0, n), com, AnchorSpread, _anchors);
        if (na == 0) { Skipped++; return true; }

        var t = new Tear { Piece = w.Piece, Original = w.Original, Phys = phys, F0 = TearMath.StartForce(mass, ForceFactor, na) };
        t.Steps = Math.Max(1, (int)(TearSeconds * 60f + 0.5f));
        var margs = new PositionConstraintMotorArgs { Tau = Tau, Damping = Damping, MinForce = -t.F0, MaxForce = t.F0 };
        t.Motor = (IPositionConstraintMotor)phys.AllocateMotor(in margs);
        try
        {
            for (int k = 0; k < na; k++)
            {
                // (pivots: the anchor in each body's own frame - the game's helper; the piece is body A, the "child")
                ConstraintHelper.CreatePivots(pd, od, _anchors[k], out Matrix pa, out Matrix pb);
                var args = new SixDegreesOfFreedomConstraintArgs { PivotA = pa, PivotB = pb };
                args.SetAxisMotor(ConstraintAxisIdentifier.LinearX, t.Motor);
                args.SetAxisMotor(ConstraintAxisIdentifier.LinearY, t.Motor);
                args.SetAxisMotor(ConstraintAxisIdentifier.LinearZ, t.Motor);
                t.C[k] = phys.AllocateConstraint(w.Piece.DEntity, w.Original.DEntity, args);
            }
        }
        catch
        {
            // (half made: what was made goes - both bodies are there, checked above this tick)
            foreach (var c in t.C) if (c != null && !IsDestroyed(c)) try { phys.DestroyConstraint(c); } catch { }
            try { t.Motor.Dispose(); } catch { }
            throw;
        }
        _active.Add(t);
        Active = _active.Count;
        Status = $"{Name(w.Piece)} ({mass / 1000f:F1} t) tearing from {Name(w.Original)}: {n} broken block(s), hold {t.F0 * na / 1000f:F0} kN over {TearSeconds:F1} s";
        Log.Default?.Info($"[AERO-TEAR] {Status}");
        return true;
    }

    /// <summary>One tick of a tear: true when it is over (let go, or lost).</summary>
    static bool TickTear(Tear t)
    {
        if (!Usable(t))
        {
            // (a body went: the engine destroyed the constraints with it - nothing of ours is called again)
            Lost++; Status = $"{Name(t.Piece)}: a body went mid-tear (dropped)"; Log.Default?.Info($"[AERO-TEAR] {Status}");
            return true;
        }
        t.Age++;
        if (t.Age >= t.Steps || !Enabled)
        {
            Release(t);
            Torn++; Status = $"{Name(t.Piece)} torn loose after {t.Age / 60f:F2} s";
            Log.Default?.Info($"[AERO-TEAR] {Status}");
            return true;
        }
        float f = t.F0 * TearMath.Ramp(t.Age / (float)t.Steps);
        t.Motor.MaxForce = f; t.Motor.MinForce = -f;
        return false;
    }

    /// <summary>Safe to call into the tear's handles: the same physics, both entities alive with their bodies, and no
    /// constraint destroyed by the engine (a body deleted or rebuilt).</summary>
    static bool Usable(Tear t)
    {
        var phys = PhysicsHack.Physics;
        if (phys == null || !ReferenceEquals(phys, t.Phys)) return false;
        if (!PhysicsHack.Alive(t.Piece.Data) || !PhysicsHack.Alive(t.Original.Data)) return false;
        if (!phys.HasBody(t.Piece.DEntity) || !phys.HasBody(t.Original.DEntity)) return false;
        foreach (var c in t.C) if (c != null && IsDestroyed(c)) return false;
        return true;
    }

    /// <summary>Let go (Usable checked): the constraints destroyed, then the motor disposed (the game's order).</summary>
    static void Release(Tear t)
    {
        for (int k = 0; k < t.C.Length; k++) { var c = t.C[k]; t.C[k] = null; if (c != null && !IsDestroyed(c)) t.Phys.DestroyConstraint(c); }
        var m = t.Motor; t.Motor = null; m?.Dispose();
    }

    static PropertyInfo _destroyed; static bool _destroyedTried;
    /// <summary>HavokConstraint.Destroyed (internal class, public getter; set when the engine destroyed it with a body).
    /// Unreadable: false (the alive / body checks still guard).</summary>
    static bool IsDestroyed(IPhysicsConstraint c)
    {
        if (!_destroyedTried) { _destroyedTried = true; _destroyed = c.GetType().GetProperty("Destroyed", BindingFlags.Public | BindingFlags.Instance); if (_destroyed == null) Log.Default?.Info("[AERO-TEAR] HavokConstraint.Destroyed unreadable: alive / body checks only"); }
        return _destroyed != null && _destroyed.GetValue(c) is bool b && b;
    }
}

public partial class AeroGridComponent
{
    /// <summary>(StructureTear.Watch) this grid's split signal is watched.</summary>
    internal bool _tearWatched;

    /// <summary>The tear job's place: after every grid's attitude job (so after the game's grid systems), before the physics
    /// step - the constraints made, tuned and destroyed there, once a tick.</summary>
    [After(typeof(OnAttitude))]
    [Before(typeof(Keen.Game2.Simulation.Physics))]
    private class OnTear : JobGroup;

    [OnTear]
    [MustHave(typeof(AeroGridComponent))]
    private static void TearJob(AeroGridComponent aero)
    {
        if (StructureTear.Armed == 0 || !aero.IsServerScene) return;
        if (Interlocked.Exchange(ref StructureTear.Armed, 0) == 0) return;   // (another grid's job took this tick)
        StructureTear.Step();
    }
}
