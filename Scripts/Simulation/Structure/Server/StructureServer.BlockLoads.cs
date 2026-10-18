#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

// LOADS AT THEIR OWN BLOCKS (2026-10-17, StructureEval.ComponentLoadsAtBlocks): the snapshot's share of each block
// component's air and each drag patch (Loads.CompF...), and the extra block loads other code publishes (PublishBlockLoad
// -> Loads.LoadBlock...).
public partial class AeroGridComponent
{
    /// <summary>The block components' air (AeroComponentRegistry.ResultForce...) and the drag patches' (PatchLoadF / P), as
    /// the ship took them this tick, into the snapshot - the world's multipliers applied as AeroSimJob applied them to the
    /// total (AeroTuning.ScalePart: so the components' scaled forces and moments add up to exactly what the scaled total
    /// holds of them). Simulation thread (the snapshot).</summary>
    void SnapComponentLoads(StructureEval.Loads L, Vector3 airVelLocal)
    {
        if (!StructureEval.ComponentLoadsAtBlocks || !AeroForcesApplied) return;
        bool tune = AeroTuning.DragMultiplier != 1f || AeroTuning.LiftMultiplier != 1f;
        float tk = tune ? AeroTuning.TorqueScale(LastResult.Force, airVelLocal) : 1f;
        var com = LastCoM;
        var reg = _components;
        int nc = reg != null && reg.ResultCount == reg.Count ? reg.ResultCount : 0;
        for (int i = 0; i < nc; i++) AddCompLoad(L, reg.ResultForce[i], reg.ResultPoint[i], reg.ResultMoment[i], reg.ResultAt[i], tune, tk, airVelLocal, com);
        // (a drag patch: at its point, on the block there - AeroApi.SetDrag gives no block; off the hull, the spread takes it)
        for (int i = 0; i < PatchLoadCount; i++) AddCompLoad(L, PatchLoadF[i], PatchLoadP[i], Vector3.Zero, PatchLoadP[i], tune, tk, airVelLocal, com);
    }

    static void AddCompLoad(StructureEval.Loads L, Vector3 f, Vector3 p, Vector3 m, Vector3 at, bool tune, float tk, Vector3 airVelLocal, Vector3 com)
    {
        if (!PhysicsHack.IsFinite(f) || !PhysicsHack.IsFinite(m)) return;
        if (tune) AeroTuning.ScalePart(ref f, ref m, p, com, tk, airVelLocal);
        L.CompF.Add(f); L.CompP.Add(p); L.CompT.Add(m); L.CompAt.Add(at);
    }

    /// <summary>One block's published load (PublishBlockLoad): the latest of the sim tick it was published in.</summary>
    struct BlockLoad { public Entity Block; public Vector3 F, P, T; public bool Engine; public int Frame; }
    readonly List<BlockLoad> _blockLoads = new();
    /// <summary>A published load is in the structure's snapshot while it is at most this many sim ticks old (published
    /// after this tick's snapshot - the attitude job - it is a tick old at the next); older, it is dropped.</summary>
    const int BlockLoadTicks = 2;
    /// <summary>Blocks published as engines (PublishBlockLoad isEngine): rigid in the structure like the game's thrusters
    /// (IsThrusterBlock - ShipStructure.Thruster, RigidThrusters). Held weakly: a removed block is not kept alive.</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Entity, object> _loadEngines = new();
    static readonly object _engineMark = new();
    internal static bool IsLoadEngine(Entity e) => e != null && _loadEngines.TryGetValue(e, out _);

    /// <summary>
    /// EXTRA BLOCK LOAD (2026-10-17): a force and torque on <paramref name="block"/> (one of this grid's blocks) THIS SIM
    /// TICK that the structure would otherwise not see - one this mod's air model and the game's thrusters do not carry
    /// (a rotor's cyclic force, a solid motor's thrust kept out of the thruster cache): the code that applies it to the
    /// body (AeroPhysics.ApplyForceAndTorque) publishes it here too. Grid-local: force (N), the point it acts at (m), a
    /// torque of its own (N m, besides the force's moment about the point). Never air this mod already counts (that would
    /// count twice). isEngine: the block is an engine - rigid inside like the game's thrusters (never worn or failed itself;
    /// its mounts carry the load and break, ShipStructure.RigidThrusters; the first such call rebuilds the structure) and
    /// its load off with Structure.ThrustLoads.
    /// Sampling: like the thrusters' loads - the latest value when an evaluation's snapshot is taken (StructureLoads.Rate a
    /// second), not an average; one entry per block, a second call in a tick replaces the first (publish the block's total).
    /// Call every tick it acts; one not renewed for BlockLoadTicks ticks is gone. Any thread (a lock; no allocation once
    /// the block has an entry). Server: only the server's structure reads it.
    /// </summary>
    internal void PublishBlockLoad(Entity block, Vector3 forceLocal, Vector3 pointLocal, Vector3 torqueLocal, bool isEngine)
    {
        if (block == null || !PhysicsHack.IsFinite(forceLocal) || !PhysicsHack.IsFinite(pointLocal) || !PhysicsHack.IsFinite(torqueLocal)) return;
        int now = _simFrameCount;
        lock (_blockLoads)
        {
            int at = -1;
            for (int i = _blockLoads.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_blockLoads[i].Block, block)) { at = i; continue; }
                if (now - _blockLoads[i].Frame > BlockLoadTicks) { _blockLoads.RemoveAt(i); if (at > i) at--; }
            }
            var e = new BlockLoad { Block = block, F = forceLocal, P = pointLocal, T = torqueLocal, Engine = isEngine, Frame = now };
            if (at >= 0) _blockLoads[at] = e; else _blockLoads.Add(e);
        }
        if (isEngine && !_loadEngines.TryGetValue(block, out _))
        {
            bool added = false;
            lock (_loadEngines) if (!_loadEngines.TryGetValue(block, out _)) { _loadEngines.Add(block, _engineMark); added = true; }
            if (added) System.Threading.Interlocked.Increment(ref StructBlockVersion);   // (rebuilt: the engine rigid - IsThrusterBlock)
        }
    }

    /// <summary>The published block loads still fresh, into the snapshot: each on its block (index - the structure's; one
    /// not in it yet: -1, found by its point in the evaluation), stale ones dropped. Simulation thread.</summary>
    void SnapBlockLoads(StructureEval.Loads L, Dictionary<Entity, int> index)
    {
        if (_blockLoads.Count == 0) return;
        int now = _simFrameCount;
        lock (_blockLoads)
            for (int i = _blockLoads.Count - 1; i >= 0; i--)
            {
                var e = _blockLoads[i];
                if (now - e.Frame > BlockLoadTicks) { _blockLoads.RemoveAt(i); continue; }
                L.LoadBlock.Add(index != null && index.TryGetValue(e.Block, out int b) ? b : -1);
                L.LoadF.Add(e.F); L.LoadP.Add(e.P); L.LoadT.Add(e.T); L.LoadEngine.Add(e.Engine);
            }
    }
}
