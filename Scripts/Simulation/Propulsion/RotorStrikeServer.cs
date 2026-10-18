#pragma warning disable
using System;
using System.Collections.Generic;
using System.Threading;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.GameSystems.ProjectileSystems;
using Keen.Game2.Simulation.WorldObjects.Characters;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.BlockOctrees;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Damage;
using Keen.VRage.Core;
using Keen.VRage.DCS.Annotations;
using Keen.VRage.Physics.Data;

namespace AeroMod;

/// <summary>
/// ROTOR STRIKES' CONSEQUENCES (2026-10-17, server; the laws: RotorStrike.cs). The probes (AeroRotorStrikeComponent, each rotor's own
/// job, OnStrikeProbe) only QUEUE what they hit; every strike is handled here, ONCE A TICK in one job (OnRotorStrike: the first server
/// grid's job takes it - after every grid's attitude job and every probe, before the physics step, as the tear's constraints: StructureTear), because a strike
/// touches other entities - another grid's blocks, a character, two bodies' velocities - that no rotor's own job may write.
/// Each strike:
///  1. WHAT WAS HIT, from the hit body: a character (it has CharacterLifeComponent), a grid (CubeGridComponent: the block at the hit -
///     the rotor's own block is never struck), anything else (terrain, rock, flora) is rock.
///  2. THE BLADES wear (RotorStrike.BladeWear) and their share of the rotor block's health is dealt to it through the game's damage
///     (GridDamageReceiverComponent.DealDamage - never below its destruction threshold), unless the world's rules refuse damage to it
///     (IGameplayRules.CanDamage - then the blades take nothing either). No health left: BROKEN - the propulsor's rpm 0 (it writes no
///     thrust from then on: AeroPropulsorComponent) and the rotor's angular momentum (RotorStrike.BreakAngularMomentum) dumped into the
///     ship about its spin axis.
///  3. THE STRUCK: a block loses RotorStrike.BlockDamageShare of its full health (the game's DealDamage: its damage stages, sounds and
///     destruction as any damage); a character takes RotorStrike.CharacterDamage through its own CharacterLifeComponent.DoDamage with the
///     game's Accident damage source (a normal death: the respawn screen says why) - only if the rules allow damaging it - and a knock-
///     back (RotorStrike.CharacterKnock) through RigidBodyDataFunctions.TryApplyImpulse (the game's explosion knock-back path); rock: no
///     scrape (the voxel editing API is not a mod reference - skipped).
///  4. THE SHIP gets the real impulse at the strike point against the blade's motion (RotorStrike.StrikeImpulse; a character's: what the
///     character took; its own blocks: none - an internal force), through the engine's own RigidBodyDataFunctions (ApplyImpulseAt), the
///     same path as AeroPhysics. Another dynamic grid struck takes the opposite impulse. THE STRUCTURE (2026-10-17) feels them
///     (AeroGridComponent.PublishBlockImpulse - one tick's impulse gathered and taken once by the next snapshot, the larger of the
///     window's mean and its peak tick over the collisions' crush time: Structure/Model/ImpulseWindow.cs): the ship's reaction
///     and the break's angular momentum at the ROTOR block (the blade carries the hit to its hub: the strike point's moment
///     loads the rotor's mount); the struck grid's push at the struck block; a strike on the ship's own blocks - no impulse on
///     the body, but a real pair of forces inside it - the push at the struck block and its reaction at the rotor block. Each
///     body's impulse also noted for its contact balance (NoteKnownImpulse: not a contact - else the wheels or locked plates
///     would carry it a second time).
///  5. EFFECTS for the client: sparks and a crunch at the strike point (TearSmoke.PublishStrike), at most one each FxGapSeconds per
///     rotor (and always on a break).
/// No allocation unless a strike happens; a strike's fault is caught (rare work, never a hot path) and counted.
/// </summary>
public static class RotorStrikeServer
{
    public struct Strike
    {
        public AeroRotorStrikeComponent Rotor; public AeroPropulsorComponent Prop; public Entity Grid;
        public DEntity Body; public Vector3D Pos; public Vector3 Normal, Motion, Spin;
        public float Radius, TipRadius, Rpm, Speed;
    }

    static readonly object _lock = new();
    static readonly List<Strike> _queue = new(), _work = new();
    /// <summary>Non-zero: strikes wait for this tick's job.</summary>
    internal static int Armed;
    /// <summary>Most strikes waiting at once (each rotor queues one at a time).</summary>
    public const int MaxQueued = 64;
    /// <summary>Sparks and a crunch at most this often per rotor (s).</summary>
    public static float FxGapSeconds = 0.15f;
    /// <summary>The game's Accident damage source ("KillMessageAccident"), the Impact one as the fallback.</summary>
    public static Guid AccidentSource = new("71f8b73e-6081-4bd4-95ed-4437802150e3"), ImpactSource = new("c28c6d5f-44a0-4543-ba28-a7f1b2233e10");

    // diagnostics (harness aeroget RotorStrikeServer.X)
    public static int Queued, Handled, Dropped, Breaks, OnRock, OnMetal, OnCharacters, Kills, Faults, Refused;
    public static string LastFault = "-";
    public static string Status => $"queued {Queued} handled {Handled} dropped {Dropped} rock {OnRock} metal {OnMetal} characters {OnCharacters} (killed {Kills}) breaks {Breaks} refused (rules) {Refused} faults {Faults} {LastFault}";

    /// <summary>(a rotor's job) a strike to handle this tick; false: the queue is full (dropped).</summary>
    internal static bool Queue(in Strike s)
    {
        lock (_lock)
        {
            if (_queue.Count >= MaxQueued) { Dropped++; return false; }
            _queue.Add(s); Queued++;
        }
        Volatile.Write(ref Armed, 1);
        return true;
    }

    /// <summary>(the one job a tick) every strike queued.</summary>
    internal static void Step()
    {
        lock (_lock) { _work.AddRange(_queue); _queue.Clear(); }
        for (int i = 0; i < _work.Count; i++)
        {
            var s = _work[i];
            try { AeroGridComponent.HandleRotorStrike(in s); Handled++; }
            catch (Exception e) { Faults++; LastFault = e.GetType().Name + ": " + e.Message; Log.Default?.Info($"[AERO-STRIKE] strike failed: {LastFault}"); }
            finally { if (s.Rotor != null) s.Rotor.Pending = false; }
        }
        _work.Clear();
    }

    /// <summary>The character damage's source, looked up on each character strike (rare; never a static kept across world loads).</summary>
    internal static DamageSourceDefinition Source()
    {
        if (DefinitionManager.Instance.TryGetDefinition(AccidentSource, out Definition d) && d is DamageSourceDefinition a) return a;
        if (DefinitionManager.Instance.TryGetDefinition(ImpactSource, out Definition d2) && d2 is DamageSourceDefinition b) return b;
        LastFault = "no Accident / Impact damage source: characters knocked back, no damage";
        return null;
    }

    static long _logAt;
    /// <summary>A strike's log line: at most 5 a second (a break always).</summary>
    internal static void Note(string line, bool always)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!always && now - _logAt < System.Diagnostics.Stopwatch.Frequency / 5) return;
        _logAt = now;
        Log.Default?.Info("[AERO-STRIKE] " + line);
    }
}

public partial class AeroGridComponent
{
    /// <summary>The strike job's place: after every grid's attitude job, before the physics step (as OnTear).</summary>
    [After(typeof(OnAttitude))]
    [After(typeof(AeroRotorStrikeComponent.OnStrikeProbe))]
    [Before(typeof(Keen.Game2.Simulation.Physics))]
    private class OnRotorStrike : JobGroup;

    [OnRotorStrike]
    [MustHave(typeof(AeroGridComponent))]
    private static void RotorStrikeJob(AeroGridComponent aero)
    {
        if (Volatile.Read(ref RotorStrikeServer.Armed) == 0 || !aero.IsServerScene) return;
        if (Interlocked.Exchange(ref RotorStrikeServer.Armed, 0) == 0) return;   // (another grid's job took this tick)
        RotorStrikeServer.Step();
    }

    /// <summary>One strike: what was hit, the blades, the struck, the ship, the effects (RotorStrikeServer's summary).</summary>
    internal static void HandleRotorStrike(in RotorStrikeServer.Strike s)
    {
        var rot = s.Rotor; var prop = s.Prop; var ship = s.Grid;
        if (rot == null || prop == null || rot.Entity == null || !PhysicsHack.Alive(rot.Data) || rot.Broken) return;
        rot.Gap = RotorStrike.GapTicks(s.Rpm, s.TipRadius, AeroPhysics.Dt);
        if (ship == null || !PhysicsHack.Alive(ship.Data)) return;
        var hit = Entity.TryGetFromDataEntity(new DEntityContext(rot.Data.Scene, s.Body));
        if (hit == null || !PhysicsHack.Alive(hit.Data)) return;
        var rules = rot.Entity.GetSession()?.TryGet<IGameplayRules>();

        // 1. what was hit
        var life = hit.TryGet<CharacterLifeComponent>();
        CubeBlockComponent block = null;
        RotorStrike.Material mat;
        if (life != null) mat = RotorStrike.Material.Character;
        else if (hit.TryGet<CubeGridComponent>() != null)
        {
            mat = RotorStrike.Material.Metal;
            var hwt = hit.Data.GetWorldTransform();
            var local = (Vector3)WorldTransform.TransformInv(s.Pos, hwt) - WorldTransform.TransformDirectionInv(s.Normal, hwt) * 0.06f;
            block = hit.TryGet<BlockOctreeComponent>()?.TryGetCubeBlock(CellOf(local));
            if (block != null && ReferenceEquals(block.Entity, rot.Entity)) return;   // (its own block: never a strike)
        }
        else mat = RotorStrike.Material.Rock;
        float v = s.Speed;

        // 2. the blades, and their share of the rotor block
        float wear = RotorStrike.BladeWear(mat, v);
        var own = rot.Entity.TryGet<CubeBlockComponent>();
        bool wearOk = own != null && Allowed(rules, rot.Entity);
        if (!wearOk) RotorStrikeServer.Refused++;
        float before = rot.Health;
        if (wearOk && wear > 0f)
        {
            DamageRotorBlock(own, ship, RotorStrike.RotorBlockWear(wear));
            rot.Health = RotorStrike.AfterWear(rot.Health, wear);
            rot.Changed();
        }
        bool broke = !RotorStrike.IsBroken(before) && rot.Broken;

        // 3. the struck, and the impulse the ship takes (world, at the strike point)
        Vector3 onShip = Vector3.Zero;
        string what;
        switch (mat)
        {
            case RotorStrike.Material.Character:
            {
                RotorStrikeServer.OnCharacters++;
                float dmg = RotorStrike.CharacterDamage(v, life.MaxHealth);
                if (life.IsAlive && Allowed(rules, hit))
                {
                    var src = RotorStrikeServer.Source();
                    if (src != null && dmg > 0f) { life.DoDamage(dmg, src); if (!life.IsAlive) RotorStrikeServer.Kills++; }
                }
                float mc = hit.Data.TryGet<RigidBodyMassProperties>(out var mp) && mp.Mass > 1f ? mp.Mass : RotorStrike.CharacterMass;
                float jc = RotorStrike.CharacterKnock(s.Radius, s.TipRadius, v, mc);
                RigidBodyDataFunctions.TryApplyImpulse(s.Motion * jc, s.Pos, hit);
                onShip = -s.Motion * jc;
                what = $"character ({dmg:F0} of {life.MaxHealth:F0} health, knocked {jc / mc:F1} m/s)";
                break;
            }
            case RotorStrike.Material.Metal:
            {
                RotorStrikeServer.OnMetal++;
                float share = RotorStrike.BlockDamageShare(s.Radius, s.TipRadius, v);
                if (block != null && block.Entity != null && PhysicsHack.Alive(block.Entity.Data) && block.HealthIntegrity > DestructionThreshold(block) + 1e-4f && share > 0f)
                    hit.TryGet<GridDamageReceiverComponent>()?.DealDamage(block, share * FullHealth(block));
                float jm = RotorStrike.StrikeImpulse(mat, s.Radius, s.TipRadius, v);
                // (its own grid: an internal force - no impulse; another grid takes the blade's push, the ship the reaction)
                if (!ReferenceEquals(hit, ship)) { onShip = -s.Motion * jm; RigidBodyDataFunctions.TryApplyImpulse(s.Motion * jm, s.Pos, hit); }
                // (the structures: the struck block takes the blade's push - another grid's, or the ship's own with its reaction at
                //  the rotor, an internal pair; the ship's reaction on another grid is onShip, below)
                if (block?.Entity != null && jm > 0f)
                {
                    var hitAero = hit.TryGet<AeroGridComponent>();
                    if (hitAero != null)
                    {
                        var hwt2 = hit.Data.GetWorldTransform();
                        var push = s.Motion * jm;
                        Vector3 pl = (Vector3)WorldTransform.TransformInv(s.Pos, hwt2);
                        hitAero.PublishBlockImpulse(block.Entity, WorldTransform.TransformDirectionInv(push, hwt2), pl, Vector3.Zero);
                        if (ReferenceEquals(hit, ship)) hitAero.PublishBlockImpulse(rot.Entity, WorldTransform.TransformDirectionInv(-push, hwt2), pl, Vector3.Zero);
                        else if (AeroPhysics.IsDynamic(hit)) hitAero.NoteKnownImpulse(push);
                    }
                }
                what = $"{(ReferenceEquals(hit, ship) ? "own" : "other")} grid {hit.DebugName}{(block != null ? $" block -{share * 100f:F1} %" : "")}";
                break;
            }
            default:
            {
                RotorStrikeServer.OnRock++;
                float jr = RotorStrike.StrikeImpulse(mat, s.Radius, s.TipRadius, v);
                onShip = -s.Motion * jr;
                RigidBodyDataFunctions.TryApplyImpulse(s.Motion * jr, s.Pos, hit);   // (a static body - terrain - takes nothing: the game's check)
                what = "rock";
                break;
            }
        }
        if (onShip.LengthSquared() > 0f) RigidBodyDataFunctions.TryApplyImpulse(onShip, s.Pos, ship);

        // the break: the rotor stops at once - its angular momentum into the ship, about its spin axis
        float L = 0f;
        if (broke)
        {
            L = RotorStrike.BreakAngularMomentum(s.Rpm, s.TipRadius);
            AngularImpulse(ship, s.Spin * L);
            prop.Rpm = 0f;
            RotorStrikeServer.Breaks++;
        }
        // 4b. the ship's structure: the reaction and the break's angular momentum at the rotor block (PublishBlockImpulse)
        StructureImpulse(ship, rot.Entity, s.Pos, onShip, broke ? s.Spin * L : Vector3.Zero);
        rot.Strikes++;
        rot.Last = $"{what} at r {s.Radius:F2} m, {v:F0} m/s: blades {before:F2} -> {rot.Health:F2}{(broke ? " BROKEN" : "")}";
        RotorStrikeServer.Note($"{prop.Kind} {prop.Size}: {rot.Last}, ship impulse {onShip.Length():F0} N s{(broke ? $", rotor stopped: {L:F0} N m s into the ship" : "")}", broke);

        // 5. the effects (client): sparks and a crunch at the strike point, in the ship's frame
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (broke || now - rot.FxAt > (long)(RotorStrikeServer.FxGapSeconds * System.Diagnostics.Stopwatch.Frequency))
        {
            rot.FxAt = now;
            var swt = ship.Data.GetWorldTransform();
            TearSmoke.PublishStrike(ship, (Vector3)WorldTransform.TransformInv(s.Pos, swt), WorldTransform.TransformDirectionInv(s.Normal, swt), (int)mat, RotorStrike.Severity(v), broke);
        }
    }

    /// <summary>A strike's impulse on the ship (world N s at the strike point) and angular impulse (world N m s) into its structure at
    /// the rotor block (PublishBlockImpulse; grid-local) - a static ship's structure bears it too - and the linear one noted for
    /// its contact balance (NoteKnownImpulse) when its body took it (a dynamic one: TryApplyImpulse's check).</summary>
    static void StructureImpulse(Entity ship, Entity rotor, Vector3D pos, Vector3 j, Vector3 l)
    {
        if (rotor == null || (j.LengthSquared() <= 0f && l.LengthSquared() <= 0f)) return;
        var aero = ship.TryGet<AeroGridComponent>();
        if (aero == null) return;
        var swt = ship.Data.GetWorldTransform();
        aero.PublishBlockImpulse(rotor, WorldTransform.TransformDirectionInv(j, swt), (Vector3)WorldTransform.TransformInv(pos, swt), WorldTransform.TransformDirectionInv(l, swt));
        if (AeroPhysics.IsDynamic(ship)) aero.NoteKnownImpulse(j);
    }

    static bool Allowed(IGameplayRules rules, Entity e) => rules == null || rules.CanDamage(new IGameplayRules.DamageContext(e)).Allowed;

    /// <summary>The blades' wear on their block: share of its full health through the game's damage, never down to its destruction.</summary>
    static void DamageRotorBlock(CubeBlockComponent b, Entity grid, float share)
    {
        if (b?.Entity == null || !(share > 0f)) return;
        float full = FullHealth(b);
        float hp = MathF.Min(share * full, MathF.Max(b.AbsoluteHealth - (DestructionThreshold(b) + 0.05f) * full, 0f));
        if (!(hp > 0f)) return;
        grid.TryGet<GridDamageReceiverComponent>()?.DealDamage(b, hp);
    }

    /// <summary>A world angular impulse (N m s) on a dynamic body (the engine's ApplyAngularImpulse).</summary>
    static void AngularImpulse(Entity e, Vector3 worldImpulse)
    {
        if (e == null || !PhysicsHack.IsFinite(worldImpulse) || !AeroPhysics.IsDynamic(e)) return;
        var data = e.Data;
        ref RigidBodyData rb = ref data.TryGetWritePtr<RigidBodyData>();
        if (Unsafe.IsNullRef(in rb) || !data.TryGet<RigidBodyMassProperties>(out var mass)) return;
        var wt = data.GetWorldTransform();
        rb.ApplyAngularImpulse(in mass, in wt, worldImpulse);
    }
}

public partial class AeroRotorStrikeComponent
{
    /// <summary>When its last sparks were published (Stopwatch ticks; RotorStrikeServer).</summary>
    internal long FxAt;
}
