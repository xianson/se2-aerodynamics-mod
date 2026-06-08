using System;
using System.Diagnostics;
using Keen.VRage.Core;

#pragma warning disable
namespace AeroMod;

/// <summary>
/// Profiling stats registered into the SE2 stat overlay.
/// Two sections: "Grid" (focused grid) and "All" (sum across all grids per frame).
/// Budget % based on a 2ms frame budget. Main Thread mirrored from engine.
///
/// All stat registration is deferred to first use to avoid crashing during
/// early mod DLL loading when StatStorage isn't ready yet.
/// </summary>
public static class AeroStats
{
    private const float BudgetUs = 2000f; // 2ms budget
    private const int PeakWindow = 60;

    // ── Stat keys (populated on first BeginGrid call) ──
    public static StatKey Aero;

    // Grid
    public static StatKey GridTotalUs, GridDragUs, GridCompUs, GridCtrlUs;
    public static StatKey GridFaceOvrUs, GridSurfUs, GridWingUs, GridThrustUs, GridPhysUs, GridDrawUs, GridReadUs;
    // Drag breakdown
    public static StatKey DragShadowUs, DragForceUs, DragLiftUs;
    // Drag breakdown peaks
    public static StatKey DragShadowPeak, DragForcePeak, DragLiftPeak;
    public static StatKey GridFaces, GridWings, GridComps;
    public static StatKey GridSpeed, GridMach;

    // All
    public static StatKey AllTotalUs, AllGrids, AllFaces, AllWings;

    // Budget
    public static StatKey BudgetPct, BudgetPeak, BudgetAvg;

    // ── Peak trackers for drag breakdown ──
    private static PeakTracker _shadowPeak, _forcePeak, _liftPeak;
    // Latest values (written by Set helpers, read by CommitGrid for peak tracking)
    private static float _lastShadowUs, _lastForceUs, _lastLiftUs;

    // ── Init guard ──
    private static bool _initialized;

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;

        Aero = Stats.RegisterStat("Aero", StatKind.None);

        // Grid
        var grid = Aero.RegisterChild("Grid", StatKind.None);
        GridTotalUs   = grid.RegisterChild("Total",      StatKind.Raw, "N0", "µs");
        GridDragUs    = grid.RegisterChild("Drag",       StatKind.Raw, "N0", "µs");
        GridCompUs    = grid.RegisterChild("Components", StatKind.Raw, "N0", "µs");
        GridCtrlUs    = grid.RegisterChild("CtrlSurf",   StatKind.Raw, "N0", "µs");
        GridFaceOvrUs = grid.RegisterChild("FaceOvr",    StatKind.Raw, "N0", "µs");
        GridSurfUs    = grid.RegisterChild("SurfRebuild",StatKind.Raw, "N0", "µs");
        GridWingUs    = grid.RegisterChild("WingDetect", StatKind.Raw, "N0", "µs");
        GridThrustUs  = grid.RegisterChild("Thrust",     StatKind.Raw, "N0", "µs");
        GridPhysUs    = grid.RegisterChild("Physics",    StatKind.Raw, "N0", "µs");
        GridDrawUs    = grid.RegisterChild("DebugDraw",  StatKind.Raw, "N0", "µs");
        GridReadUs    = grid.RegisterChild("PhysRead",   StatKind.Raw, "N0", "µs");

        // Drag sub-breakdown with peaks
        var drag = grid.RegisterChild("Drag Detail", StatKind.None);
        DragShadowUs   = drag.RegisterChild("Shadow",      StatKind.Raw, "N0", "µs");
        DragShadowPeak = drag.RegisterChild("Shadow peak",  StatKind.Raw, "N0", "µs");
        DragForceUs    = drag.RegisterChild("Force",       StatKind.Raw, "N0", "µs");
        DragForcePeak  = drag.RegisterChild("Force peak",   StatKind.Raw, "N0", "µs");
        DragLiftUs     = drag.RegisterChild("WingLift",    StatKind.Raw, "N0", "µs");
        DragLiftPeak   = drag.RegisterChild("WingLift peak",StatKind.Raw, "N0", "µs");

        _shadowPeak = new PeakTracker(PeakWindow);
        _forcePeak  = new PeakTracker(PeakWindow);
        _liftPeak   = new PeakTracker(PeakWindow);

        GridFaces     = grid.RegisterChild("Faces",      StatKind.Raw, "N0");
        GridWings     = grid.RegisterChild("Wings",      StatKind.Raw, "N0");
        GridComps     = grid.RegisterChild("Comps",      StatKind.Raw, "N0");
        GridSpeed     = grid.RegisterChild("Speed",      StatKind.Raw, "N1", "m/s");
        GridMach      = grid.RegisterChild("Mach",       StatKind.Raw, "N2");

        // All
        var all = Aero.RegisterChild("All", StatKind.None);
        AllTotalUs = all.RegisterChild("Total",  StatKind.Raw, "N0", "µs");
        AllGrids   = all.RegisterChild("Grids",  StatKind.Raw, "N0");
        AllFaces   = all.RegisterChild("Faces",  StatKind.Raw, "N0");
        AllWings   = all.RegisterChild("Wings",  StatKind.Raw, "N0");

        // Main thread (mirror engine stats)
        try { Stats.RegisterMirror(VRageStats.MainThread, Aero); }
        catch { /* not available yet */ }

        // Budget
        var budget = Aero.RegisterChild("Budget", StatKind.None);
        BudgetPct  = budget.RegisterChild("Frame %",      StatKind.Raw, "N1", "%");
        BudgetPeak = budget.RegisterChild("Peak % (60f)", StatKind.Raw, "N1", "%");
        BudgetAvg  = budget.RegisterChild("Avg % (60f)",  StatKind.Raw, "N1", "%");
    }

    // ── Frame accumulator state ──
    private static int _lastTickMs = -1;
    private static float _frameTotalUs;
    private static int _frameGridCount;
    private static int _frameFaceCount;
    private static int _frameWingCount;

    // ── Rolling history (budget %) ──
    private const int HistorySize = 60;
    private static readonly float[] _history = new float[HistorySize];
    private static int _historyIndex;

    // ── Periodic logging ──
    private static long _lastLogTimestamp;

    /// <summary>
    /// Call at the start of each grid's draw job. Resets accumulators on new frame.
    /// </summary>
    public static void BeginGrid()
    {
        EnsureInitialized();

        int now = Environment.TickCount;
        if (now != _lastTickMs)
        {
            if (_lastTickMs >= 0)
                FlushFrame();

            _lastTickMs = now;
            _frameTotalUs = 0f;
            _frameGridCount = 0;
            _frameFaceCount = 0;
            _frameWingCount = 0;
        }
    }

    /// <summary>
    /// Call after each grid finishes. Writes grid-level stats if focused,
    /// and accumulates into frame totals.
    /// </summary>
    public static void CommitGrid(float gridTotalUs, bool isFocused,
        int faces, int wings, int comps, float speed, float mach)
    {
        _frameTotalUs += gridTotalUs;
        _frameGridCount++;
        _frameFaceCount += faces;
        _frameWingCount += wings;

        if (isFocused)
        {
            GridTotalUs.Set(gridTotalUs);
            GridFaces.Set(faces);
            GridWings.Set(wings);
            GridComps.Set(comps);
            GridSpeed.Set(speed);
            GridMach.Set(mach);

            // Update drag breakdown peaks
            DragShadowPeak.Set(_shadowPeak.Push(_lastShadowUs));
            DragForcePeak.Set(_forcePeak.Push(_lastForceUs));
            DragLiftPeak.Set(_liftPeak.Push(_lastLiftUs));
        }
    }

    private static void FlushFrame()
    {
        AllTotalUs.Set(_frameTotalUs);
        AllGrids.Set(_frameGridCount);
        AllFaces.Set(_frameFaceCount);
        AllWings.Set(_frameWingCount);

        float pct = _frameTotalUs / BudgetUs * 100f;
        BudgetPct.Set(pct);

        _history[_historyIndex] = pct;
        _historyIndex = (_historyIndex + 1) % HistorySize;

        float sum = 0f, peak = 0f;
        for (int i = 0; i < HistorySize; i++)
        {
            float v = _history[i];
            sum += v;
            if (v > peak) peak = v;
        }
        BudgetPeak.Set(peak);
        BudgetAvg.Set(sum / HistorySize);

        long now = Stopwatch.GetTimestamp();
        if (_lastLogTimestamp == 0) _lastLogTimestamp = now;
        float elapsedSec = (float)((now - _lastLogTimestamp) * TicksToUs / 1_000_000.0);
        if (elapsedSec >= 10f)
        {
            _lastLogTimestamp = now;
            Log.Default?.Info(
                $"[AERO-PROF] {_frameGridCount} grids {_frameTotalUs:F0}µs ({pct:F1}%) " +
                $"drag={_lastShadowUs + _lastForceUs + _lastLiftUs:F0} " +
                $"[shad={_lastShadowUs:F0} force={_lastForceUs:F0} lift={_lastLiftUs:F0}] " +
                $"phys={_lastPhysUs:F0} thrust={_lastThrustUs:F0} " +
                $"cs={_lastCtrlUs:F0} comp={_lastCompUs:F0} faceOvr={_lastFaceOvrUs:F0} " +
                $"surf={_lastSurfUs:F0} wing={_lastWingUs:F0} draw={_lastDrawUs:F0} read={_lastReadUs:F0}");
        }
    }

    // ── Drag breakdown setters (set stat + stash for peak tracking) ──
    public static void SetShadow(float us) { DragShadowUs.Set(us); _lastShadowUs = us; }
    public static void SetForce(float us)  { DragForceUs.Set(us);  _lastForceUs = us; }
    public static void SetLift(float us)   { DragLiftUs.Set(us);   _lastLiftUs = us; }

    // ── Grid subsystem stashes (for logging) ──
    private static float _lastDragUs, _lastPhysUs, _lastThrustUs, _lastCtrlUs, _lastCompUs;
    private static float _lastFaceOvrUs, _lastSurfUs, _lastWingUs, _lastDrawUs, _lastReadUs;

    public static void SetDrag(float us)    { GridDragUs.Set(us);    _lastDragUs = us; }
    public static void SetPhys(float us)    { GridPhysUs.Set(us);    _lastPhysUs = us; }
    public static void SetThrust(float us)  { GridThrustUs.Set(us);  _lastThrustUs = us; }
    public static void SetCtrl(float us)    { GridCtrlUs.Set(us);    _lastCtrlUs = us; }
    public static void SetComp(float us)    { GridCompUs.Set(us);    _lastCompUs = us; }
    public static void SetFaceOvr(float us) { GridFaceOvrUs.Set(us); _lastFaceOvrUs = us; }
    public static void SetSurf(float us)    { GridSurfUs.Set(us);    _lastSurfUs = us; }
    public static void SetWing(float us)    { GridWingUs.Set(us);    _lastWingUs = us; }
    public static void SetDraw(float us)    { GridDrawUs.Set(us);    _lastDrawUs = us; }
    public static void SetRead(float us)    { GridReadUs.Set(us);    _lastReadUs = us; }

    // ── Stopwatch helpers ──
    private static readonly double TicksToUs = 1_000_000.0 / Stopwatch.Frequency;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Timestamp() => Stopwatch.GetTimestamp();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ElapsedUs(long start) => (float)((Stopwatch.GetTimestamp() - start) * TicksToUs);

    // ── Rolling peak tracker ──
    private class PeakTracker
    {
        private readonly float[] _buf;
        private int _idx;

        public PeakTracker(int size) => _buf = new float[size];

        public float Peak { get; private set; }

        public float Push(float val)
        {
            _buf[_idx] = val;
            _idx = (_idx + 1) % _buf.Length;

            float max = 0f;
            for (int i = 0; i < _buf.Length; i++)
                if (_buf[i] > max) max = _buf[i];
            Peak = max;
            return max;
        }
    }
}
