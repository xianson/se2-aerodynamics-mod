#pragma warning disable
using System;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
namespace AeroMod;

// DENTS AND PERMANENT BENDS (StructureServer): worn armour moved through the game's deformation bones.
public partial class AeroGridComponent
{
    /// <summary>Overloaded blocks dent as they wear (DentPerUtil of a full dent per unit of utilisation over 1, at most DentMax).</summary>
    public static bool Dents = true;
    /// <summary>A wear step's dent (m) per unit of utilisation over 1 - added each step while it stays overloaded - and the most
    /// a bone is dented in all (m; the game clamps a 0.25 m armour block's bones to 0.25 m besides).</summary>
    public static float DentPerUtil = 0.02f, DentMax = 0.12f;
    /// <summary>Diagnostics: bones dented.</summary>
    public static int DentWrites;
    /// <summary>Each bone's total dent (m, grid axes) and the part under one bone step not yet written (bone units).</summary>
    readonly Dictionary<Vector3I, (Vector3 total, Vector3 frac)> _dentLedger = new();

    /// <summary>A worn armour block's dent: its bones (the 2x2x2 or so round its cells) pushed along the load's direction by
    /// (util - 1) x DentPerUtil, each bone's total capped at DentMax, the part under one bone step carried. Armour only - the
    /// bones move only the armour mesh.</summary>
    void Dent(CubeBlockComponent b, Vector3 dir, float util)
    {
        try
        {
            if (b.Entity.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Armors.ArmorBlockComponent>() == null) return;
            var bones = Entity?.TryGet<Keen.Game2.Simulation.WorldObjects.BoneStorage.BoneStorageComponent>();
            if (bones == null) return;
            float step = Math.Clamp((util - 1f) * DentPerUtil, 0f, DentMax);
            if (step <= 0f) return;
            Vector3 d = Vector3.Normalize(dir) * step; float unit = BoneUnit;
            var bb = b.AABB;
            var lo = bones.BlockToBone(bb.Min); var hi = bones.BlockToBone(bb.Max) + Vector3I.One;
            for (int x = lo.X; x <= hi.X; x++) for (int y = lo.Y; y <= hi.Y; y++) for (int z = lo.Z; z <= hi.Z; z++)
            {
                var bone = new Vector3I(x, y, z);
                _dentLedger.TryGetValue(bone, out var led);
                var total = led.total + d;
                float len = total.Length();
                if (len > DentMax) total *= DentMax / len;   // (capped: a dent, not a crater)
                if (StepBone(bones, _dentLedger, bone, led, total, unit)) DentWrites++;
            }
        }
        catch (Exception e) { Log.Default?.Info($"[AERO-STRUCT] dent failed: {e.Message}"); }
    }

    /// <summary>The bends the evaluation planned (StructureBend.Plan, background) - moved here, the simulation thread's.</summary>
    readonly List<StructureBend.Bend> _stBends = new();
    /// <summary>Each joint's peak utilisation it was bent at (StructureBend.Key: by place, kept across rebuilds; background).</summary>
    readonly Dictionary<long, float> _bendPeaks = new();
    /// <summary>Each bone's total bend (m, grid axes) and the part under one bone step not yet written (bone units).</summary>
    readonly Dictionary<Vector3I, (Vector3 total, Vector3 frac)> _bendLedger = new();
    readonly HashSet<Vector3I> _bendBones = new();
    /// <summary>The game's bone step: a bone's full value (127) moves its vertices this far (m; BoneDeformationConfiguration
    /// .MaxBoneOffset, 1 by default - no override in GameData).</summary>
    public static float BoneOffsetM = 1f;
    /// <summary>A far side with more than this share of non-armour blocks is not bent (the bones move only the armour: a thruster
    /// or a wing block would stay put while the armour round it moved).</summary>
    public static float BendNonArmourShare = 0.1f;
    /// <summary>Most bones written per evaluation (every bend together).</summary>
    public static int BendMaxBones = 1500;
    /// <summary>Diagnostics: bends applied, skipped (mostly not armour, no bone storage), bones written; the last.</summary>
    public static int BendsApplied, BendsSkipped, BendBoneWrites; public static string BendLast = "-";

    /// <summary>Bone units per metre (a bone's Vector3B: 1/127 of BoneOffsetM).</summary>
    static float BoneUnit => 127f / MathF.Max(BoneOffsetM, 1e-3f);

    /// <summary>A bone's ledger (dents', bends') moved to its new total (m, grid axes): the whole bone steps since the last
    /// write written (WriteBoneDelta), the part under one step carried - so a slow dent or bend is not lost to rounding.
    /// led: its entry before (default if none). True when a step was written.</summary>
    static bool StepBone(Keen.Game2.Simulation.WorldObjects.BoneStorage.BoneStorageComponent bones, Dictionary<Vector3I, (Vector3 total, Vector3 frac)> ledger,
        Vector3I bone, (Vector3 total, Vector3 frac) led, Vector3 total, float unit)
    {
        var units = (total - led.total) * unit + led.frac;
        var q = new Vector3((int)units.X, (int)units.Y, (int)units.Z);   // (whole steps; the rest carried)
        ledger[bone] = (total, units - q);
        if (q == Vector3.Zero) return false;
        bones.WriteBoneDelta(bone, new Keen.VRage.Library.Mathematics.Vector3B(q));
        return true;
    }

    /// <summary>
    /// PERMANENT BENDS, the game side (2026-10-14; the plan: Structure/StructureBend). Rare (a joint passing its last peak):
    /// guarded. SE2 cannot turn blocks, but its deformation BONES (BoneStorageComponent: one every 4 cells, 1 m; a bone's
    /// Vector3B is its offset in 1/127 of MaxBoneOffset - 1 m - and the armour mesh is skinned to the eight round each
    /// point) can be moved: every bone of the far side's blocks moves by the bend's displacement there - zero at the joint,
    /// growing outward - so the armour sags or twists as one piece. Written straight (WriteBoneDelta(Vector3I, Vector3B)),
    /// not through DealDamage: there the deformation is scaled by the damage dealt (a wear step's sliver of health rounds to
    /// nothing in a bone's 8 mm step) and is a fixed 3x3x3 bones from one corner (a dent, not a bend). The game replicates
    /// the bones to clients (BoneChangedSignal) and clamps each to its chunk's limit (a 0.25 m armour block's chunk: 0.25 m).
    ///  - each bone's total from bends is capped at StructureBend.Max (the ledger); the part under one step is carried, so a
    ///    slow bend is not lost to rounding;
    ///  - ARMOUR ONLY moves: a far side of more than BendNonArmourShare other blocks is not bent;
    ///  - at most BendMaxBones writes an evaluation.
    /// </summary>
    void ApplyBends()
    {
        if (_stBends.Count == 0) return;
        try
        {
            if (_stBlocks == null || !StructureBend.Enabled || !StructureLoads.Damage) return;
            var bones = Entity?.TryGet<Keen.Game2.Simulation.WorldObjects.BoneStorage.BoneStorageComponent>();
            if (bones == null) { BendsSkipped += _stBends.Count; BendLast = "no bone storage"; return; }
            int perBone = Math.Max(1, bones.BlocksPerBone);
            float boneM = perBone * TearMath.Cell, unit = BoneUnit;
            int writes = 0;
            foreach (var bend in _stBends)
            {
                // the far side's blocks still here; armour mostly
                _bendBones.Clear();
                int n = 0, other = 0;
                foreach (int bi in bend.Blocks)
                {
                    if (bi < 0 || bi >= _stBlocks.Count) continue;
                    var b = _stBlocks[bi];
                    if (Gone(b)) continue;
                    n++;
                    if (b.Entity.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Armors.ArmorBlockComponent>() == null) { other++; continue; }
                    // (its bones: each cell lies between the bones floor(c / 4) and the next)
                    var bb = b.AABB;
                    var lo = bones.BlockToBone(bb.Min); var hi = bones.BlockToBone(bb.Max) + Vector3I.One;
                    for (int x = lo.X; x <= hi.X; x++) for (int y = lo.Y; y <= hi.Y; y++) for (int z = lo.Z; z <= hi.Z; z++) _bendBones.Add(new Vector3I(x, y, z));
                }
                if (n == 0 || other > BendNonArmourShare * n) { BendsSkipped++; BendLast = $"{Entity?.DebugName}: joint at {bend.Joint} not bent - {other} of {n} far blocks not armour"; continue; }
                int wrote = 0; float tip = 0f;
                foreach (var bone in _bendBones)
                {
                    if (writes >= BendMaxBones) break;
                    // (a bone's place: the lower corner of its first cell - bone b at b x 1 m - 0.125, the grid's cells centred at i x 0.25)
                    var p = new Vector3(bone.X, bone.Y, bone.Z) * boneM - new Vector3(TearMath.Cell * 0.5f);
                    var d = StructureBend.Displacement(bend, p);
                    if (d.LengthSquared() < 1e-8f) continue;
                    _bendLedger.TryGetValue(bone, out var led);
                    var total = StructureBend.Capped(led.total, d);
                    if (!StepBone(bones, _bendLedger, bone, led, total, unit)) continue;
                    writes++; wrote++;
                    float l = total.Length(); if (l > tip) tip = l;
                }
                BendsApplied++; BendBoneWrites += wrote;
                BendLast = $"{Entity?.DebugName}: joint at {bend.Joint} (utilisation {bend.Util:F2}) set {bend.Rot.Length() * 57.2958f:F2} deg about {Vector3.Normalize(bend.Rot)}: {n} blocks, {wrote} bones, most {tip * 100f:F0} cm";
                Log.Default?.Info($"[AERO-STRUCT] bend {BendLast}");
            }
        }
        catch (Exception e) { BendsSkipped++; BendLast = "bend failed: " + e.Message; Log.Default?.Info($"[AERO-STRUCT] bend failed: {e.Message}"); }
        finally { _stBends.Clear(); }
    }
}
