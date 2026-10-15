#pragma warning disable
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
    /// <summary>This stage spares the jagged line's stubs (stage 0, the cut left some).</summary>
    public bool Spare => Stage == 0 && HasStubs;
    /// <summary>The cut fails - its crack starts, before its first member lets go (or every member at once): the baseline;
    /// any split from here may be its separation.</summary>
    public void Begin(int stamp) { Stamp = stamp; Stage = 0; SplitDuring = false; }
    /// <summary>The crack's last member gone: watched from now for ticks - the baseline KEPT (stampNow only notes a split
    /// during the crack: the next tick's SplitSince reports it).</summary>
    public void Finish(int stampNow, int ticks) { SplitDuring = stampNow != Stamp; Ticks = ticks; }
    /// <summary>Each tick, first: a split since the baseline (true once per split - the caller counts the bridges by this
    /// stage's rule: none - parted, done).</summary>
    public bool SplitSince(int stamp) { if (stamp == Stamp) return false; Stamp = stamp; return true; }
    /// <summary>Each tick, then: this stage's time out.</summary>
    public bool Expired() => --Ticks <= 0;
    /// <summary>Out of time, its bridges forced: the stubs' stage done - watched once more on the plane's rule (true); else
    /// the watch is over.</summary>
    public bool NextStage(int stamp, int ticks) { if (!Spare) return false; Stage = 1; Ticks = ticks; Stamp = stamp; return true; }
}
