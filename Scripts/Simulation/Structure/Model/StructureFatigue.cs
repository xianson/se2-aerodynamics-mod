#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// FATIGUE AND PROGRESSIVE FAILURE (2026-10-14, the user: "i'd definitely like to see things fatigue more, failure is very
/// sudden rn") - game-free (the evaluation: StructureEval.Damage; the game side: Structure/Server/StructureServer.cs
/// ApplyStructureDamage; offline-tested in Tests/StructureTests 'Fatigue' and AeroBench 'fail').
///
/// A joint loses HEALTH (the block's integrity, which is its strength: ShipStructure.BlockStrength) at a rate set by its
/// utilisation u (load over its strength now; 1 is yield), integrated over time (dt-scaled: the evaluation's rate does not
/// change the outcome):
///  - FATIGUE below yield (Miner's rule in rate form): nothing under the endurance limit EnduranceUtil (0.5 - cruising never
///    tires a ship); above it Rate x ((u - E) / (1 - E))^Exponent per second - steep (cubic): continuous load at 0.6 of the
///    yield takes a tenth of a block's health in ~40 min, at 0.7 in ~5 min, at 0.9 in ~40 s, at the yield in 20 s. The
///    AeroBench 'fail' repeated pulls (3 s held at 0.8 of the yield every 10 s): the Tu-134's worst joint at 0.93 after 20
///    pulls, 0.85 after 40, yielding at ~80 (13 min) and breaking 30 s later;
///  - YIELD WEAR past 1: WearRate x (u - 1) / (BreakUtil - 1) per second on top (StructureEval.WearPerSecond 0.45 at
///    BreakUtil 1.5) - a joint at 1.2 goes in 2-3.5 s, a 0.5 s spike at 1.5 costs a quarter of its health (it used to
///    snap); it lets go when its health is EXHAUSTED (BreakBelow: the game's destruction threshold, 0.3). Calibrated so the AeroBench pull ramps' first break lands where
///    the old instant one did (Tu-134 3.8-3.9 g, Plane2 13.2-13.7 g, 2.3-3 s after the first wear);
///  - a SNAP only at a genuine overload (SnapUtil, 2.75 of the joint's INTACT strength): at once.
/// BY SECTION (2026-10-15, StructureEval.DamageBySection): the joints are the members of a CUT - each tires at its own
/// utilisation, so a weak neck tires across its whole section, and the cut fails whole when one of them is exhausted.
/// The loop closes by itself: health lost is strength lost (down to ShipStructure.MinIntegrity), so a tired joint's
/// utilisation rises under the same load - it fails at a lower load, and a joint past its yield runs away over a second or
/// several (bends and dents meanwhile, creaks rising) instead of snapping in a moment. A brief spike costs a little health;
/// a sustained or repeated pull costs more each time.
///
/// THE LEDGER (one per grid): the health lives in the GAME's block (its integrity: saved, replicated - a reload keeps the
/// fatigue, and the evaluation reads it back), but losing it a sliver per evaluation through the game's damage played the
/// block-damage sound every time (489 wears in a session: constant clanking) and sent it over the network. So each block's
/// loss is held here (Pending: the part not yet written; Local: the mod's own running integrity, the game's less the
/// pending, what the solve uses) and written as one step when it reaches WriteStep of the block's health (at most every
/// WriteEvery seconds a block), when it settles (SettleSeconds without more, at least SettleMin), when it crosses one of the
/// game's damage stages (Thresholds: each stage's smoke or sparks plays once on the way down), and AT ONCE when the block
/// fails. Only the sub-step remainder is lost on a reload or a rebuild that cannot place it.
/// </summary>
public static class StructureFatigue
{
    /// <summary>Fatigue below yield on (Structure.Fatigue). Off: only past the yield.</summary>
    public static bool Enabled = true;
    /// <summary>Health lost per second (fraction of full) at the yield from fatigue (Structure.FatigueRate).</summary>
    public static float Rate = 0.005f;
    /// <summary>The endurance limit: no fatigue at or under this utilisation (Structure.EnduranceUtil).</summary>
    public static float EnduranceUtil = 0.5f;
    /// <summary>How steeply fatigue grows from the endurance limit to the yield.</summary>
    public static float Exponent = 3f;
    /// <summary>How the yield wear grows past 1 (1: in proportion to the overshoot).</summary>
    public static float WearExponent = 1f;
    /// <summary>How a block's utilisation follows its own health inside an evaluation's step (Loss): 1 - inversely (tension,
    /// shear), 0.5 - as its root (a buckling panel's compression). Between: the solve's own mix.</summary>
    public static float FeedbackExponent = 0.5f;
    /// <summary>FATIGUE IS DRIVEN BY TENSION (2026-10-15, the user: "add compression vs tension to the break work"). A fatigue
    /// crack starts at a flaw and grows only while its faces are pulled apart; a compressive cycle presses them shut (why a
    /// compressive residual stress - shot peening, cold rolling - lengthens fatigue life, and why fatigue codes count the
    /// tensile part of the range and credit a compressive mean). So a member's fatigue runs at the full rate for its TENSILE
    /// part (its pull with its shear - TensilePart) and at this share for what its compression adds on top (on the slices
    /// only - the members' signs: ShipStructure.PieceTension / PieceCompression). The yield wear past 1 - crushing, buckling -
    /// is not scaled. 0.2: a pure compression member lasts about five times as long at the same utilisation; a member pulled
    /// as hard as it is pushed, as before.</summary>
    public static float CompressionFatigueShare = 0.2f;
    /// <summary>The tensile part of a member's utilisation u (a share of it, 0..1): 1 where tension governs (ut at least uc);
    /// else its pull and its shear without the compression (von Mises: sqrt(u^2 - uc^2 + ut^2) / u). NaN-safe (1).</summary>
    public static float TensilePart(float ut, float uc, float u)
    {
        if (!(uc > ut) || !(u > 0f)) return 1f;
        float t = MathF.Sqrt(MathF.Max(u * u - uc * uc, 0f) + MathF.Max(ut, 0f) * MathF.Max(ut, 0f));
        return Math.Clamp(t / u, 0f, 1f);
    }
    /// <summary>The fatigue rate (per second, before the class's factor) at utilisation u of which tensile is the tensile part:
    /// the full rate at the tensile utilisation, CompressionFatigueShare of the rest.</summary>
    static float FatigueRate(float u, float tensile)
    {
        float e = EnduranceUtil;
        if (!Enabled || !(u > e) || !(e < 1f)) return 0f;
        float full = Rate * MathF.Pow(MathF.Min((u - e) / (1f - e), 1f), Exponent);
        if (!(tensile < 1f)) return full;   // (tension governs; NaN: as tension)
        float ut = u * MathF.Max(tensile, 0f);
        float pulled = ut > e ? Rate * MathF.Pow(MathF.Min((ut - e) / (1f - e), 1f), Exponent) : 0f;
        return pulled + Math.Clamp(CompressionFatigueShare, 0f, 1f) * (full - pulled);
    }
    /// <summary>An instant break at or past this utilisation (Structure.SnapUtil): a genuine overload, no wearing through.</summary>
    public static float SnapUtil = 2.75f;
    /// <summary>A block fails when its health would fall to this (fraction of full): the game's own DestructionThreshold
    /// (CubeBlockDefinition, 0.3 - under it the game removes the block itself, with its death effects).</summary>
    public static float BreakBelow = 0.3f;

    /// <summary>Health lost per second (fraction of full) by a block of this class at utilisation u. breakUtil: where yield
    /// wear reaches wearRate per second (StructureEval.BreakUtil, WearPerSecond).</summary>
    public static float HealthRate(int mat, float u, float breakUtil, float wearRate) => HealthRate(mat, u, breakUtil, wearRate, 1f);
    /// <summary>As HealthRate, for a member of which tensile is the tensile part (TensilePart): its fatigue by its pull at the
    /// full rate, by its push at CompressionFatigueShare; the yield wear past 1 the same.</summary>
    public static float HealthRate(int mat, float u, float breakUtil, float wearRate, float tensile)
    {
        if (!(u > 0f)) return 0f;
        float r = FatigueRate(u, tensile);
        if (u > 1f)
        {
            float y = (u - 1f) / MathF.Max(breakUtil - 1f, 1e-3f);
            r += wearRate * (WearExponent == 1f ? y : MathF.Pow(y, WearExponent));
        }
        float f = mat >= 0 && mat < StructMaterial.FatigueFactor.Length ? StructMaterial.FatigueFactor[mat] : 1f;
        return r * f;
    }

    /// <summary>The health (fraction of full) a block of this class loses over dt at utilisation u0 now, its health have now,
    /// with the loop closed INSIDE the step (rate-independent: an evaluation every 0.5 s ends where eight of 0.0625 s do):
    /// its strength goes with its health (down to ShipStructure.MinIntegrity), the joint's with the weaker of its two blocks
    /// (cap: the other's strength over this one's now), the utilisation inversely. Sub-stepped where it wears fast. uIntact:
    /// the utilisation against this block's intact strength (the snap's).</summary>
    public static float Loss(int mat, float u0, float have, float cap, float dt, float breakUtil, float wearRate, out float uIntact)
        => Loss(mat, u0, have, cap, dt, breakUtil, wearRate, out uIntact, 1f);
    /// <summary>As Loss, for a member of which tensile is the tensile part (TensilePart: its fatigue by its pull at the full
    /// rate, by its push at CompressionFatigueShare).</summary>
    public static float Loss(int mat, float u0, float have, float cap, float dt, float breakUtil, float wearRate, out float uIntact, float tensile)
    {
        float minI = ShipStructure.MinIntegrity;
        float m0 = MathF.Max(MathF.Min(have, 1f), minI);
        float s0 = MathF.Min(1f, cap);
        uIntact = u0 * Follow(s0 / MathF.Min(1f / m0, cap));
        if (!(dt > 0f)) return 0f;
        float r0 = HealthRate(mat, u0, breakUtil, wearRate, tensile);
        if (!(r0 > 0f)) return 0f;
        if (r0 * dt < 0.005f) return r0 * dt;   // (fatigue: slow - one step)
        int n = Math.Clamp((int)MathF.Ceiling(dt / MaxSubStep), 1, 64);
        float h = dt / n, I = have;
        for (int i = 0; i < n && I > BreakBelow; i++)
        {
            float u = u0 * Follow(s0 / MathF.Min(MathF.Max(I, minI) / m0, cap));
            I -= HealthRate(mat, u, breakUtil, wearRate, tensile) * h;
        }
        return have - I;
    }
    /// <summary>A utilisation's factor for a strength's factor 1 / x (FeedbackExponent).</summary>
    static float Follow(float x) => FeedbackExponent == 1f ? x : MathF.Pow(x, FeedbackExponent);
    /// <summary>Longest sub-step of a fast-wearing block's loss (s).</summary>
    public static float MaxSubStep = 1f / 32f;

    /// <summary>Write a block's pending loss once it reaches this much of its health ...</summary>
    public static float WriteStep = 0.05f;
    /// <summary>... at most this often a block (s) ...</summary>
    public static float WriteEvery = 1f;
    /// <summary>... or once it has settled (no more for SettleSeconds), if at least SettleMin.</summary>
    public static float SettleSeconds = 3f, SettleMin = 0.01f;
    /// <summary>The game's damage stages (health): crossing one writes at once, so each plays on the way down, one by one
    /// (the game plays one stage per health change - a jump past two merges them). BlockEffects: DamageIntegrityThresholds
    /// 0.75 / 0.5 (the incremental-damage sound; 0.25 is under the destruction threshold); OnDamagedBlockParticleEffect's
    /// bands - thrusters smoke and burn under 0.8, gyros and cockpits smoke and spark under 0.7, cockpits burn under 0.5.</summary>
    public static float[] Thresholds = { 0.8f, 0.75f, 0.7f, 0.5f };
    /// <summary>A fresh reading of the game's integrity this far over the mod's (game less pending) is a REPAIR (welded):
    /// the ledger takes the game's and forgets the pending.</summary>
    public static float RepairTolerance = 0.02f;

    // diagnostics (harness aeroget)
    public static int Writes, Repairs, Accrued;

    /// <summary>One grid's health ledger. Background (Sync, Accrue: the evaluation) and simulation thread (Take*: the damage
    /// applied) never at once - a grid's evaluation is done before its damage is applied, and the next one starts after.</summary>
    public sealed class Ledger
    {
        public float[] Local = Array.Empty<float>(), Pending = Array.Empty<float>(), LastWrite = Array.Empty<float>(), LastAccrue = Array.Empty<float>();
        /// <summary>Seconds of evaluation so far (the ledger's own clock: the evaluations' dt summed).</summary>
        public float Clock;
        /// <summary>Blocks with a pending loss.</summary>
        public readonly HashSet<int> Dirty = new();
        object _seen;
        public int Count => Local.Length;

        /// <summary>Set to n blocks: old block i becomes block remap[i] (-1: gone); null remap: a fresh start (nothing
        /// pending). Local re-read from the game at the next Sync.</summary>
        public void Resize(int n, int[] oldToNew)
        {
            var l = new float[n]; var p = new float[n]; var w = new float[n]; var a = new float[n];
            for (int i = 0; i < n; i++) { l[i] = -1f; w[i] = -1e9f; a[i] = -1e9f; }
            var d = new List<int>();
            if (oldToNew != null)
                for (int i = 0; i < oldToNew.Length && i < Local.Length; i++)
                {
                    int j = oldToNew[i]; if (j < 0 || j >= n) continue;
                    l[j] = Local[i]; p[j] = Pending[i]; w[j] = LastWrite[i]; a[j] = LastAccrue[i];
                    if (p[j] > 0f) d.Add(j);
                }
            Local = l; Pending = p; LastWrite = w; LastAccrue = a;
            Dirty.Clear(); foreach (int j in d) Dirty.Add(j);
            _seen = null;
        }

        /// <summary>The game's integrity read (game[b], 0..1) into the ledger, and the ledger's into the structure (st:
        /// integrity[b] = Local[b]). A reading not seen before (a new array: the game's health read again, after every write
        /// so far) re-anchors: repaired (over the expected by RepairTolerance) - take it, forget the pending; damaged by
        /// something else (under) - take it less the pending; else keep the mod's own (finer than the game's steps).</summary>
        public void Sync(float[] game, float[] integrity, int n, float dt)
        {
            if (Local.Length != n) Resize(n, null);
            Clock += MathF.Max(dt, 0f);
            bool fresh = game != null && !ReferenceEquals(game, _seen);
            _seen = game;
            for (int b = 0; b < n; b++)
            {
                float g = game != null && b < game.Length ? game[b] : 1f;
                if (Local[b] < 0f) { Local[b] = MathF.Max(g - Pending[b], 0f); }
                else if (fresh)
                {
                    float expect = Local[b] + Pending[b];
                    if (g > expect + RepairTolerance) { Local[b] = g; Pending[b] = 0f; Dirty.Remove(b); Repairs++; }
                    else if (g < expect - RepairTolerance) Local[b] = MathF.Max(g - Pending[b], 0f);
                }
                if (integrity != null && b < integrity.Length) integrity[b] = Local[b];
            }
        }

        /// <summary>A loss (fraction of full health) this evaluation: off the mod's integrity, onto the pending.</summary>
        public void Accrue(int b, float loss)
        {
            if (b < 0 || b >= Local.Length || !(loss > 0f)) return;
            loss = MathF.Min(loss, MathF.Max(Local[b], 0f));
            if (loss <= 0f) return;
            float before = Local[b];
            Local[b] -= loss; Pending[b] += loss; LastAccrue[b] = Clock;
            Dirty.Add(b); Accrued++;
            // (a damage stage crossed: written at once - its effect plays now, not a step later)
            foreach (float t in Thresholds) if (before > t && Local[b] <= t) { _stage.Add(b); break; }
        }
        readonly HashSet<int> _stage = new();

        /// <summary>This block's pending loss, all of it, now (a failing block): returned and cleared.</summary>
        public void Set(int b, float health) { if (b >= 0 && b < Local.Length) { Local[b] = MathF.Max(health, 0f); Pending[b] = 0f; Dirty.Remove(b); _stage.Remove(b); } }

        public float TakeAll(int b)
        {
            if (b < 0 || b >= Pending.Length) return 0f;
            float p = Pending[b]; Pending[b] = 0f; Dirty.Remove(b); _stage.Remove(b); LastWrite[b] = Clock;
            if (p > 0f) Writes++;
            return p;
        }

        /// <summary>The writes due now (block, loss): a step's worth not written for WriteEvery, a settled remainder, a stage
        /// crossed. Cleared from the pending.</summary>
        public void TakeDue(List<(int block, float loss)> outDue)
        {
            outDue.Clear();
            if (Dirty.Count == 0) return;
            foreach (int b in Dirty)
            {
                float p = Pending[b];
                bool step = p >= WriteStep && Clock - LastWrite[b] >= WriteEvery;
                bool settled = p >= SettleMin && Clock - LastAccrue[b] >= SettleSeconds;
                if (step || settled || _stage.Contains(b)) outDue.Add((b, p));
            }
            foreach (var (b, p) in outDue) { Pending[b] = 0f; Dirty.Remove(b); _stage.Remove(b); LastWrite[b] = Clock; Writes++; }
        }
    }
}
