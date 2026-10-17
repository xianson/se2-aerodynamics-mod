#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace AeroMod;

/// <summary>
/// A FAILED CUT'S WATCH - the game-free part of its separation check (Structure/Server/StructureServer.Failure.cs,
/// CheckCutSeparated; offline-tested in Tests/StructureTests 'CrackSeparates'): the grid's split count it compares against
/// (the baseline), its ticks left, its stage (0: the jagged surface - its stubs spared; 1: the plane's rule, stubs and all).
/// THE BASELINE IS THE CRACK'S START (2026-10-15, the integration run - the SSTO pulled at Strength 1.5: CutsForced +2): the
/// game splits the piece off as soon as the members let go so far part it - mid-crack, before its last ones (a hinge whose
/// path the others already cut) - and the baseline was read at the crack's END, after that split: the watch never saw it,
/// ran out both stages, and its plane's rule destroyed the jagged line's stubs and their mates across the plane ("28
/// block(s) still bridging it" = 14 stubs x 2) on a cut that had parted by itself, ~1 s after - the crumpled fold the
/// stubs show gone. A split in the crack still has to prove itself: no bridge left (Bridges, the stage's rule) or the
/// watch goes on - a crumb split off mid-crack does not end it.
/// </summary>
public class CutWatch
{
    /// <summary>The grid's split count last seen; ticks left in this stage; the stage; the cut left stubs (stage 0 spares them).</summary>
    public int Stamp, Ticks, Stage; public bool HasStubs;
    /// <summary>The crack's end found the grid split since its start (the cut likely parted mid-crack; diagnostics).</summary>
    public bool SplitDuring;
    /// <summary>The grid split at least once since the crack started (SplitSince).</summary>
    public bool SeenSplit;
    /// <summary>This stage spares the jagged line's stubs (stage 0, the cut left some) - for FORCING its bridges.</summary>
    public bool Spare => Stage == 0 && HasStubs;
    /// <summary>A SPLIT is judged on the jagged surface in EITHER stage (2026-10-15, the third integration run: stage 0
    /// forced the cut, the piece split off 23 ms later, and stage 1 judged that split on the plane's rule - the stubs and
    /// their mates across the plane, on the ship by design - and destroyed "4 block(s) still bridging it" of a cut that had
    /// parted). The plane's rule is stage 1's last resort at its time out only; what still holds a stub from beyond the
    /// plane counts on the jagged surface (StructureServer.Failure: BeyondStub, its faces all round).</summary>
    public bool SpareOnSplit => HasStubs;
    /// <summary>A failed cut's member split at its failing face but still mounted across it is destroyed whole at once
    /// (StructureServer.Failure: FailBlock, sever - MountedAcross; 2026-10-15). Off: the watch forces it, ~0.5 s later.</summary>
    public static bool Sever = true;
    /// <summary>The cut fails - its crack starts, before its first member lets go (or every member at once): the baseline;
    /// any split from here may be its separation.</summary>
    public void Begin(int stamp) { Stamp = stamp; Stage = 0; SplitDuring = SeenSplit = false; }
    /// <summary>The crack's last member gone: watched from now for ticks - the baseline KEPT (stampNow only notes a split
    /// during the crack: the next tick's SplitSince reports it).</summary>
    public void Finish(int stampNow, int ticks) { SplitDuring = stampNow != Stamp; Ticks = ticks; }
    /// <summary>Each tick, first: a split since the baseline (true once per split - the caller counts the bridges by this
    /// stage's rule: none - parted, done).</summary>
    public bool SplitSince(int stamp) { if (stamp == Stamp) return false; Stamp = stamp; SeenSplit = true; return true; }
    /// <summary>Each tick, then: this stage's time out.</summary>
    public bool Expired() => --Ticks <= 0;
    /// <summary>Out of time, its bridges forced: the stubs' stage done - watched once more on the plane's rule (true); else
    /// the watch is over.</summary>
    public bool NextStage(int stamp, int ticks) { if (!Spare) return false; Stage = 1; Ticks = ticks; Stamp = stamp; return true; }
    /// <summary>Out of time: its bridges forced (true) - stage 0's always; stage 1's, the plane's rule, only on a cut the grid
    /// never split along or that still bridges on the jagged surface (jagged: Bridges by the split's rule now). A piece that
    /// parted mid-crack with a member still hanging across leaves no split for stage 1 to see once stage 0 took that member
    /// - and its plane's rule took the stubs on a parted cut (2026-10-15, StructureTests 'CutLetsGo').</summary>
    public bool Force(int jagged) => Stage == 0 || !SeenSplit || jagged > 0;

    // ── STILL ONE GRID? ASK THE GAME (2026-10-17, the user in play: "completely cut but did not separate" - a 33-member cut
    //    cracked across, its stage-0 force hit only a member that had left with an earlier piece ("bridge skipped: Data not
    //    found", nothing destroyed, no log), and every rule after it counted bridges in the members' box only - none there,
    //    so a split elsewhere and stage 1's time out both called it parted while the far side hung on, by something outside
    //    the box. The watch now asks the game: blocks just beyond the broken line (far seeds) and just behind it (near
    //    seeds) both still in this grid's octree - the game keeps only joined blocks in one grid - is JOINED, whatever the
    //    bridge count says; it is forced again (the holders anywhere on the far side: CutHolders) until it parts, a few
    //    rounds at most, and a cut it cannot part is logged, never dropped quietly. ──
    /// <summary>Forcing rounds done; after MaxRounds a cut still joined is given up (logged).</summary>
    public int Rounds;
    public static int MaxRounds = 4;
    /// <summary>The seeds' verdict: -1 unknown (no seeds - the bridge rules alone, as before), 0 apart (one side's seeds all
    /// gone from this grid), 1 joined (both sides' still in it).</summary>
    public static int Joined(bool known, bool farHere, bool nearHere) => !known ? -1 : farHere && nearHere ? 1 : 0;
    /// <summary>A split is this cut's separation: the seeds say apart, or (unknown) no bridge on the jagged surface.</summary>
    public static bool Credit(int bridges, int joined) => joined == 0 || joined < 0 && bridges == 0;
    /// <summary>Out of time: forced - known seeds: while joined (any stage), or stage 0's members that held on where no split
    /// was seen; unknown: Force(jagged).</summary>
    public bool Force(int jagged, int joined) => joined < 0 ? Force(jagged) : joined > 0 || Stage == 0 && !SeenSplit;
    /// <summary>The last round's rules: the plane's (stubs and all) and the holders by touch, not only by mounts.</summary>
    public bool Escalate => Rounds >= MaxRounds - 1;
    /// <summary>Still joined at a time out after MaxRounds forcings: given up (the caller logs it).</summary>
    public bool GiveUp(int joined) => joined > 0 && Rounds >= MaxRounds;
    /// <summary>A forcing done: watched again (true) - unknown seeds: only the stubs' stage into the plane's (NextStage);
    /// known: always (the next time out asks the seeds again - a forced cut is never dropped on the force alone).</summary>
    public bool Again(int stamp, int ticks, int joined)
    {
        Rounds++;
        if (joined < 0) return NextStage(stamp, ticks);
        Stage = 1; Ticks = ticks; Stamp = stamp; return true;
    }
}

/// <summary>
/// WHAT STILL HOLDS A CUT'S FAR SIDE (2026-10-17, game-free; the server's octree walk: StructureServer.Failure,
/// OctreeHolders): from blocks on the far side, every block reachable through joined far-side blocks; a far block joined to
/// a block that does not leave (the ship's side of the cut, or a stub of its jagged line) HOLDS it - its far end, so what
/// is destroyed is on the side that leaves. Anywhere on the far side, not only in the cut's cross-section.
/// </summary>
public abstract class CutHolders<T> where T : class
{
    /// <summary>The block leaves with the cut's far side (its centre beyond the plane, not a stub).</summary>
    protected abstract bool Leaves(T b);
    /// <summary>The block is never the one broken (RIGID THRUSTERS, 2026-10-17: an engine) - holding, its joined blocks that
    /// stay (its mounts on the ship's side) are, those not kept too; none such: itself after all.</summary>
    protected virtual bool Kept(T b) => false;
    /// <summary>The live blocks b is joined to (into: added; b itself never).</summary>
    protected abstract void Joined(T b, List<T> into);
    /// <summary>A walk starts (the subclass's per-walk state cleared).</summary>
    protected virtual void Started() { }
    readonly HashSet<T> _seen = new(ReferenceEqualityComparer.Instance);
    readonly Queue<T> _q = new();
    readonly List<T> _nb = new();
    /// <summary>The far-side blocks the last Find reached (holders included); it stopped at its cap.</summary>
    public IEnumerable<T> Reached => _seen;
    public bool Capped;
    /// <summary>The holders reachable from seeds (into: added) - at most maxBlocks far blocks walked. Returns how many.</summary>
    public int Find(IEnumerable<T> seeds, HashSet<T> into, int maxBlocks)
    {
        _seen.Clear(); _q.Clear(); Capped = false; Started();
        int found = 0;
        foreach (var s in seeds) if (s != null && Leaves(s) && _seen.Add(s)) _q.Enqueue(s);
        while (_q.Count > 0)
        {
            if (_seen.Count > maxBlocks) { Capped = true; break; }
            var b = _q.Dequeue();
            _nb.Clear(); Joined(b, _nb);
            bool holds = false;
            foreach (var o in _nb)
            {
                if (ReferenceEquals(o, b)) continue;
                if (!Leaves(o)) { holds = true; continue; }
                if (_seen.Add(o)) _q.Enqueue(o);
            }
            if (!holds) continue;
            bool viaMounts = false;
            if (Kept(b)) foreach (var o in _nb) if (!ReferenceEquals(o, b) && !Leaves(o) && !Kept(o)) { viaMounts = true; if (into.Add(o)) found++; }
            if (!viaMounts && into.Add(b)) found++;
        }
        return found;
    }
}
