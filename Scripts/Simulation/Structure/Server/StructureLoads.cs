#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace AeroMod;

/// <summary>The world's structure and hull-heat settings (AeroSettings: Structure.*, Heat.*) and diagnostics.</summary>
public static class StructureLoads
{
    /// <summary>Compute the loads and the hull's heat (gauges, API). Damage is separate.</summary>
    public static bool Enabled = true;
    /// <summary>Loaded joints tire, overloaded ones wear, and when its health is out (or past StructureFatigue.SnapUtil) the
    /// outboard block fails - its pieces on the failing side broken, or the whole block (the game splits the rest off;
    /// Structure/Model/StructureFatigue, 2026-10-14) - a whole CUT at once, every member across it (2026-10-15:
    /// StructureEval.DamageBySection). ON by default (2026-10-14, the user: "turn damage on by default - I want to test it from time to time").</summary>
    public static bool Damage = true;
    /// <summary>(harness) dump the next rebuilt player grid's structure input to %TEMP%/AeroMod (DumpStructure).</summary>
    public static bool DumpNext; public static string LastDump = "-";
    /// <summary>The flight controller limits its own steering to what the structure carries (AeroGridComponent.UpdateSteerCap):
    /// the worst utilisation it steers to, the cap's floor, the share of the target under which it recovers, how fast (/s).</summary>
    public static bool SteerRespectsStructure = true;
    public static float SteerCapTarget = 0.9f, SteerCapMin = 0.05f, SteerCapRelax = 0.8f, SteerCapRecover = 0.5f;
    public static string SteerCapLast = "-";
    public static float DumpLoadsAbove = -1f;
    /// <summary>Hull blocks past their burn temperature lose health. ON by default (2026-10-14, the user).</summary>
    public static bool HeatDamage = true;
    /// <summary>Evaluations per second of a player's grid (the only ones evaluated).</summary>
    public static float Rate = 4f;
    /// <summary>Seconds a grid stays a player's grid after its pilot's seat last reported it (AeroApi.SetFocusGrid).</summary>
    public static float FocusLinger = 10f;
    /// <summary>Seconds between re-reads of the blocks' health (each a pass over every block on the simulation thread).</summary>
    public static float IntegrityEvery = 2f;
    /// <summary>Evaluations in the background at once, every grid together (aero's own builds are never queued behind
    /// more than this).</summary>
    public static int MaxInFlight = 1;
    internal static int InFlight;
    /// <summary>Utilisation (1: yield) at which a joint breaks; between 1 and it, it wears (StructureEval's: the offline
    /// failure simulation reads the same).</summary>
    public static float BreakUtil { get => StructureEval.BreakUtil; set => StructureEval.BreakUtil = value; }
    /// <summary>Health lost per second (fraction of full) per unit of utilisation over 1.</summary>
    public static float WearPerSecond { get => StructureEval.WearPerSecond; set => StructureEval.WearPerSecond = value; }
    /// <summary>Most CUTS broken per evaluation, each whole (2026-10-15: StructureEval.DamageBySection; the rest deferred whole
    /// and re-solved on what is left).</summary>
    public static int MaxBreaksPerEval { get => StructureEval.MaxBreaks; set => StructureEval.MaxBreaks = value; }
    /// <summary>NO ZIPPER (2026-10-15): after a cut fails, the structure's damage waits for the structure rebuilt without what
    /// left (the evaluations in between solve the old blocks: the departed piece still on, its load still through the next
    /// cut) - at most this many seconds (s) if no block change comes.</summary>
    public static float BreakHoldMax = 3f;
    /// <summary>Diagnostics: evaluations whose structure damage was held after a break; the last failed cut.</summary>
    public static int BreakHeld; public static string LastCut = "-";
    /// <summary>Seconds between structure rebuilds after blocks change.</summary>
    public static float RebuildCooldown = 1f;

    /// <summary>DEV: log every evaluation of piloted grids ([AERO-STRUCT-T]: utilisation, worst place, hottest skin,
    /// the loads' totals, the solve's time).</summary>
    public static bool Trace;
    // diagnostics (harness aeroget)
    public static int Evals, Builds, Breaks, Wears;
    /// <summary>COST, over the last second: the simulation thread's time in TickStructure (us per second, every grid), the
    /// background's time evaluating and building (ms per second), evaluations, and grids ticked / evaluated.</summary>
    public static float SimUsPerSec, BgMsPerSec, EvalsPerSec, SimMaxUs;
    public static int GridsTicked, GridsEvaluated;
    static long _simTicks, _bgTicks, _windowAt, _simMax; static int _evalsWin, _ticked, _evaluated;
    static int _idle;
    internal static void CountIdle() => _idle++;   // (racy on purpose: a diagnostic count)
    internal static void AddSim(long ticks)
    {
        System.Threading.Interlocked.Add(ref _simTicks, ticks); System.Threading.Interlocked.Increment(ref _ticked);
        long m; while (ticks > (m = System.Threading.Interlocked.Read(ref _simMax)) && System.Threading.Interlocked.CompareExchange(ref _simMax, ticks, m) != m) { }
    }
    internal static void AddBg(long ticks) { System.Threading.Interlocked.Add(ref _bgTicks, ticks); System.Threading.Interlocked.Increment(ref _evalsWin); }
    internal static void Evaluated() => System.Threading.Interlocked.Increment(ref _evaluated);
    internal static void Publish()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_windowAt == 0) { _windowAt = now; return; }
        double sec = (now - _windowAt) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (sec < 1.0) return;
        lock (typeof(StructureLoads))
        {
            if ((now - _windowAt) / (double)System.Diagnostics.Stopwatch.Frequency < 1.0) return;
            double f = System.Diagnostics.Stopwatch.Frequency;
            SimUsPerSec = (float)(System.Threading.Interlocked.Exchange(ref _simTicks, 0) / f * 1e6 / sec);
            SimMaxUs = (float)(System.Threading.Interlocked.Exchange(ref _simMax, 0) / f * 1e6);
            BgMsPerSec = (float)(System.Threading.Interlocked.Exchange(ref _bgTicks, 0) / f * 1e3 / sec);
            EvalsPerSec = (float)(System.Threading.Interlocked.Exchange(ref _evalsWin, 0) / sec);
            GridsTicked = (int)((System.Threading.Interlocked.Exchange(ref _ticked, 0) + System.Threading.Interlocked.Exchange(ref _idle, 0)) / (sec * 60.0) + 0.5);
            GridsEvaluated = System.Threading.Interlocked.Exchange(ref _evaluated, 0);
            _windowAt = now;
        }
    }
    public static float LastEvalMs, LastBuildMs, SeenMaxUtil, SeenMaxSkin;
    public static string LastBuild = "-", LastWorst = "-";
}
