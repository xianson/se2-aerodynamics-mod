#pragma warning disable
using System;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Damage;
namespace AeroMod;

// THE STRUCTURE'S DAMAGE (StructureServer): the fatigue ledger's health (background) and its writes to the game's blocks
// (simulation thread), with the guards every damage call goes through (Gone).
public partial class AeroGridComponent
{
    /// <summary>THE FATIGUE LEDGER (Structure/Model/StructureFatigue, 2026-10-14): this grid's health not yet written to its blocks
    /// (background: SyncHealth, the evaluation; simulation thread: ApplyStructureDamage - never at once).</summary>
    readonly StructureFatigue.Ledger _stHealth = new();
    readonly List<(int block, float loss)> _stDue = new();
    /// <summary>Each block's last failing joint (structure coordinates) and the way into it, for its health writes' fracture
    /// line (the pieces on the loaded side crack first); for the block list it was taken on.</summary>
    readonly Dictionary<int, (Vector3 at, Vector3 dir)> _stLoaded = new();
    List<CubeBlockComponent> _stLoadedFor;

    /// <summary>(background, the evaluation) The blocks' health into the structure: the game's (snap.Integrity, re-read every
    /// StructureLoads.IntegrityEvery s) less what the ledger holds unwritten - after a rebuild the ledger's blocks carried to
    /// their new places by entity (StructureFatigue.Ledger.Sync, Resize).</summary>
    void SyncHealth(ShipStructure st, StructBuildInput build, StructSnap snap)
    {
        int n = st.Count;
        if (build != null || _stHealth.Count != n)
        {
            int[] oldToNew = null;
            var oldIdx = _stIndexOf;
            if (build != null && oldIdx != null && _stHealth.Count > 0)
            {
                oldToNew = new int[_stHealth.Count]; Array.Fill(oldToNew, -1);
                for (int j = 0; j < build.Blocks.Count && j < n; j++)
                {
                    var e = build.Blocks[j]?.Entity;
                    if (e != null && oldIdx.TryGetValue(e, out int i) && i >= 0 && i < oldToNew.Length) oldToNew[i] = j;
                }
            }
            _stHealth.Resize(n, oldToNew);
        }
        _stHealth.Sync(snap.Integrity, st.Integrity, n, snap.Dt);
    }

    /// <summary>The last evaluation's damage (simulation thread). Rare work: guarded.
    /// PROGRESSIVE (2026-10-14, Structure/Model/StructureFatigue): each entry's health loss goes into the ledger (_stHealth), and
    /// the game's blocks take it in STEPS (the ledger's TakeDue: 5% of a block's health, at most once a second a block, a
    /// settled remainder, a damage stage crossed) - each through the game's damage WITH a fracture line from the block's
    /// loaded joint into it, so its pieces on the loaded side crack first and its damage stages play on the way down; the
    /// game's damage sound once a step, not every evaluation (489 wears in a session were constant clanking). A block that
    /// FAILS (health out, or a snap) takes its pending at once and goes (FailBlock).
    /// BY SECTION (2026-10-15, StructureEval.DamageBySection): a whole CUT fails together - every member in this pass (split
    /// along the cut where it has pieces, destroyed otherwise, each through FailBlock and its Gone guards), the torn-edge smoke
    /// and the tear's joint once at the cut's centre (CutDone), then the structure's damage held until it is rebuilt without
    /// what left (_stBreakHold: no load run on through the next cut on the old blocks).</summary>
    void ApplyStructureDamage()
    {
        if (_stBlocks == null) { _stDamage.Clear(); return; }
        if (_stDamage.Count == 0 && _stHealth.Dirty.Count == 0) return;
        try
        {
            var recv = Entity?.TryGet<GridDamageReceiverComponent>();
            if (recv == null) return;
            var gwt = Entity.Data.GetWorldTransform();
            if (!ReferenceEquals(_stLoadedFor, _stBlocks)) { _stLoaded.Clear(); _stLoadedFor = _stBlocks; }   // (rebuilt: other indices)
            // (TEARING, 2026-10-14: a large piece this breaks off stays tied at the torn joint for StructureTear.TearSeconds -
            //  the split signal watched before any block goes, the failed joints kept for the split that follows)
            List<Vector3> torn = null;
            if (StructureTear.Enabled)
                for (int di = 0; di < _stDamage.Count; di++) if (_stDamage[di].destroy) { StructureTear.Watch(this); torn = new List<Vector3>(); break; }
            // (BY SECTION, 2026-10-15: a failed CUT's members are consecutive entries - all fail here, now; its torn-edge smoke
            //  once, at the cut's centre, out of it, and the tear's joint that centre - not one per member)
            int ci = 0, cutFailed = 0, version0 = StructBlockVersion;   // (the blocks' version before any of this pass's failures)
            for (int di = 0; di < _stDamage.Count; di++)
            {
                var (bi, fraction, destroy) = _stDamage[di];
                (Vector3 at, Vector3 dir, float util) where = di < _stWhere.Count ? _stWhere[di] : (Vector3.Zero, Vector3.Zero, 0f);
                while (ci < _stCuts.Count && di >= _stCuts[ci].First + _stCuts[ci].Count) CutDone(_stCuts[ci++], ref cutFailed, torn, version0);
                bool inCut = ci < _stCuts.Count && di >= _stCuts[ci].First;
                if (bi < 0 || bi >= _stBlocks.Count) continue;
                var b = _stBlocks[bi];
                // (a block gone since the evaluation - broken off by an earlier one in this list, split away: its data is
                //  gone and the receiver threw "Data not found" for it, losing the rest of the list, 2026-10-14)
                if (Gone(b)) { _stHealth.TakeAll(bi); continue; }
                if (where.dir.LengthSquared() > 0.5f) _stLoaded[bi] = (where.at, where.dir);
                _stHealth.Accrue(bi, fraction);
                if (destroy)
                {
                    // (one entry's fault - the game removing it under us - never loses the rest of the list)
                    try { FailBlock(recv, gwt, bi, b, where, torn, !inCut); } catch (Exception fe) { _goneBlocks.Add(b); Log.Default?.Info($"[AERO-STRUCT] fail skipped: {fe.Message}"); }
                    if (inCut) { cutFailed++; if (!Gone(b)) _cutSurvivors.Add(b); }
                    continue;
                }
                // (WORN visibly: a joint past its yield dents its block the way its load pushes, more the further over, every
                //  evaluation it stays over - the game's own deformation bones, written straight: Dent (DealDamage's
                //  deformation is scaled by the damage dealt: a wear step's rounded to nothing). Fatigue under the yield and a
                //  heat burn only lose health)
                if (Dents && where.util > 1f && where.dir.LengthSquared() > 0.5f) Dent(b, where.dir, where.util);
            }
            while (ci < _stCuts.Count) CutDone(_stCuts[ci++], ref cutFailed, torn, version0);
            if (torn != null && torn.Count > 0) StructureTear.Record(Entity, torn);
            // the health writes due (the ledger's steps)
            _stHealth.TakeDue(_stDue);
            foreach (var (bi, loss) in _stDue)
            {
                if (bi < 0 || bi >= _stBlocks.Count || !(loss > 0f)) continue;
                var b = _stBlocks[bi];
                if (Gone(b)) continue;
                float full = FullHealth(b);
                float hp = loss * full;
                if (!(hp > 0f)) continue;
                // (never under the game's destruction threshold here: a write that would remove the block is a failure's -
                //  FailBlock - not a wear step's)
                hp = MathF.Min(hp, MathF.Max(b.AbsoluteHealth - (StructureFatigue.BreakBelow + 0.01f) * full, 0f));
                if (!(hp > 0f)) continue;
                try { recv.DealDamage(b, hp, null, _stLoaded.TryGetValue(bi, out var ld) ? Fracture(b, gwt, ld.at, ld.dir) : null); }
                catch (Exception we) { _goneBlocks.Add(b); Log.Default?.Info($"[AERO-STRUCT] wear skipped: {we.Message}"); continue; }
                StructureLoads.Wears++;
            }
        }
        catch (Exception e) { Log.Default?.Info($"[AERO-STRUCT] damage failed: {e.Message}"); }
        _stDamage.Clear(); _stCuts.Clear();
    }

    /// <summary>A fracture through a block from a point on it (grid-local) along a direction (grid-local), in the block's own
    /// frame - the game's FracturingParameters.LineRelativeToBlock, as its collisions build it. Null: none known.</summary>
    static Keen.Game2.Simulation.WorldObjects.CubeGrids.Damage.FracturingParameters? Fracture(CubeBlockComponent b, Keen.VRage.Core.WorldTransform gwt, Vector3 at, Vector3 dir)
    {
        if (!Fractures || dir.LengthSquared() < 0.5f) return null;
        try
        {
            var bwt = b.Entity.Data.GetWorldTransform();
            Vector3D world = Keen.VRage.Core.WorldTransform.Transform((Vector3D)at, in gwt);
            Vector3 local = (Vector3)Keen.VRage.Core.WorldTransform.TransformInv(world, bwt);
            Vector3 d = Keen.VRage.Core.WorldTransform.TransformDirectionInv(Keen.VRage.Core.WorldTransform.TransformDirection(dir, gwt), bwt);
            return new Keen.Game2.Simulation.WorldObjects.CubeGrids.Damage.FracturingParameters { LineRelativeToBlock = new Line(local, Vector3.Normalize(d), 1f) };
        }
        catch { return null; }
    }
    public static bool Fractures = true;

    /// <summary>A block the game has removed or is removing: no entity, not alive, its health under its definition's
    /// destruction threshold (the game's own trigger for removing it - CubeBlockComponent ~4441; its flag is private), or one
    /// this grid already saw go. Every damage, dent and fracture call checks it first (a call on a dying block crashed).</summary>
    bool Gone(CubeBlockComponent b)
    {
        if (b == null || b.Entity == null || !PhysicsHack.Alive(b.Entity.Data) || _goneBlocks.Contains(b)) return true;
        // (split away to another grid since the evaluation - alive, but this grid's damage receiver no longer has its data:
        //  "Data not found", 2026-10-15)
        if (!ReferenceEquals(b.Grid?.Entity, Entity)) return true;
        return b.HealthIntegrity <= DestructionThreshold(b) + 1e-4f;
    }
    /// <summary>A block's full health (its definition's MaxHealth is not reachable from mod scripts: its health over its
    /// integrity; 0 for a block at none).</summary>
    static float FullHealth(CubeBlockComponent b) => b.HealthIntegrity > 1e-3f ? b.AbsoluteHealth / b.HealthIntegrity : 0f;
    readonly HashSet<CubeBlockComponent> _goneBlocks = new HashSet<CubeBlockComponent>(ReferenceEqualityComparer.Instance);
}
